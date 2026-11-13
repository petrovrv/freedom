// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiWorkbenchBridge
{
    /*
     * DATABASE JOB QUEUE (v1.3.0)
     * ===========================
     * CO Web 0.19.0 queues text-only jobs in the AI Workbench database (<site>/agent/db.php, schema 4)
     * instead of transport/hosts/<hostId>/inbox on Drive. Bridge takes them with db.php job_claim (atomic
     * state inbox -> claimed), writes the request into claimed/<jobId> on G: and runs it like any other job.
     * Event batches and the committed result of every job (any origin) are copied to the database
     * (job_post_events, job_result), so that CO Web follows jobs without Drive calls. Drive still receives
     * everything it received before; the Drive inbox, the Drive API poll and Pub/Sub stay as they are.
     *
     * Trust: the database is written by db.php after a CO Web sign-in (environment checked) or with the
     * home PC key, the same key that already authorizes push. A request from the database passes the same
     * ReadAndValidateJob checks as one from the Drive inbox.
     *
     * Wake-up: a Pub/Sub job.ready hint with {"db": true}, else a poll every pollSeconds.
     */
    internal sealed class DbQueueConfig
    {
        public bool enabled { get; set; }
        public string url { get; set; }
        public int pollSeconds { get; set; }
        // v1.7.0: messages sent while a Claude job runs reach it through stdin (job_claim followups); false = off
        public bool followups { get; set; }

        public DbQueueConfig()
        {
            enabled = false;
            followups = true;
            url = null; // Freedom F1: <siteUrl>/agent/db.php (BridgeApplicationContext.NormalizeSite)
            pollSeconds = 10;
        }
    }

    // v1.19.0 (Freedom F12, SPEC §3.1a): this Bridge as one of many executors. hostId stays in config.json (any id,
    // "home-main" for the first PC); title and ownerEnv are what CO Web shows, serves the environments it takes jobs of.
    internal sealed class ExecutorConfig
    {
        public bool enabled { get; set; }      // false = no executor_put (the server then knows only home-main/site)
        public string title { get; set; }      // shown in CO Web; null = Home for home-main, else the machine name
        public string ownerEnv { get; set; }   // whose machine; null/"" = the owner of the workbench
        public string[] serves { get; set; }   // environments besides ownerEnv; ["*"] = all; null = the server default
        // the server may put a copy of an unclaimed job into its Drive inbox (Google Drive for desktop); null = Windows/macOS
        public bool? driveInbox { get; set; }
        public int beatSeconds { get; set; }

        public ExecutorConfig()
        {
            enabled = true;
            beatSeconds = 30;
        }
    }

    internal sealed class DbqPost
    {
        public string kind;     // events | result | reject
        public string jobId;
        public object payload;  // EventBatch | Dictionary (result op body) | reason string
    }

    internal sealed partial class BridgeApplicationContext
    {
        private Thread _dbqClaimThread;
        private Thread _dbqPostThread;
        private readonly AutoResetEvent _dbqWake = new AutoResetEvent(false);
        private readonly BlockingCollection<DbqPost> _dbqOut =
            new BlockingCollection<DbqPost>(new ConcurrentQueue<DbqPost>(), 5000);
        private volatile string _dbqState = "off";
        private long _dbqDropped;

        private bool DbqEnabled
        {
            get { return _config != null && _config.dbQueue != null && _config.dbQueue.enabled; }
        }

        private void StartDbQueue()
        {
            if (!DbqEnabled)
            {
                Log("Database queue disabled in config.");
                return;
            }

            _dbqState = "starting";
            _dbqClaimThread = new Thread(DbqClaimLoop);
            _dbqClaimThread.IsBackground = true;
            _dbqClaimThread.Name = "db-queue-claim";
            _dbqClaimThread.Start();

            _dbqPostThread = new Thread(DbqPostLoop);
            _dbqPostThread.IsBackground = true;
            _dbqPostThread.Name = "db-queue-post";
            _dbqPostThread.Start();

            ExecutorConfig ex = _config.executor ?? new ExecutorConfig();
            if (ex.enabled)
            {
                Thread beat = new Thread(ExecutorBeatLoop);
                beat.IsBackground = true;
                beat.Name = "executor-beat";
                beat.Start();
            }

            Log("Database queue started: " + _config.dbQueue.url + " poll=" + DbqPollSeconds() + "s");

            // v1.10.3: results RecoverClaimedJobs wrote before the queue was up.
            string[] rec;
            while (_recoveredResults.TryDequeue(out rec))
                DbqPostResult(rec[0], rec[1]);
        }

        // v1.10.3: [jobId, result.json path] of jobs recovered as failed at start; posted once the database queue runs.
        private readonly ConcurrentQueue<string[]> _recoveredResults = new ConcurrentQueue<string[]>();
        private readonly ConcurrentDictionary<string, DateTime> _dbqClosedKnown =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        // v1.10.3: a job the database still has as queued/claimed though this host already finished it in an earlier run
        // (10.11: three jobs recovered as failed after the 14:20 hang stayed "running" in CO Web, Stop did nothing). Posts
        // the committed result from completed/ or failed/; without one the job is rejected. Not for a job that runs here.
        private void DbqCloseKnown(string jobId, string why)
        {
            if (_runningJobs.ContainsKey(jobId) || _runningJobObjects.ContainsKey(jobId) ||
                Directory.Exists(Path.Combine(_claimedDir, jobId)))
                return;
            DateTime last;
            if (_dbqClosedKnown.TryGetValue(jobId, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10))
                return;
            _dbqClosedKnown[jobId] = DateTime.UtcNow;

            string result = FinishedResultPath(jobId);
            if (result != null)
            {
                Log("Database job " + jobId + " (" + why + ") finished here earlier; posting its result " + result + ".");
                DbqPostResult(jobId, result);
            }
            else
            {
                Log("Database job " + jobId + " (" + why + ") is known here but has no result; rejecting it.");
                DbqReject(jobId, "Already handled on this host (" + why + "); no result found.");
            }
        }

        // completed/<jobId> or failed/<jobId> (also failed/<jobId>.recovered-*) with a committed result.json, or null.
        private string FinishedResultPath(string jobId)
        {
            foreach (string root in new string[] { _completedDir, _failedDir })
            {
                try
                {
                    string p = Path.Combine(root, jobId, "result.json");
                    if (File.Exists(p) && File.Exists(Path.Combine(root, jobId, "request.json")))
                        return p;
                    foreach (string d in Directory.GetDirectories(root, jobId + ".recovered-*"))
                    {
                        p = Path.Combine(d, "result.json");
                        if (File.Exists(p) && File.Exists(Path.Combine(d, "request.json")))
                            return p;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private int DbqPollSeconds()
        {
            int s = _config.dbQueue.pollSeconds;
            return s <= 0 ? 10 : Math.Max(2, s);
        }

        // Pub/Sub job.ready for a database job: claim now instead of at the next poll.
        private void DbqWake()
        {
            if (_dbqClaimThread != null)
                _dbqWake.Set();
        }

        // ---------------------------------------------------------------- executor (v1.19.0, schema 28)

        private volatile HostOwnerRecord _lastOwnerRecord;
        private volatile string _executorState = "off";
        // the server said another machine owns this hostId (409 host_taken): take no jobs until it is ours again
        private volatile bool _executorTaken;

        // windows | macos | linux (the server's executors.os)
        internal static string ExecutorOs()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return "windows";
            if (Environment.OSVersion.Platform == PlatformID.MacOSX
                || File.Exists("/System/Library/CoreServices/SystemVersion.plist"))
                return "macos";
            return Environment.OSVersion.Platform == PlatformID.Unix ? "linux" : "";
        }

        // db.php executor_put body: the row of executors plus a copy of owner.json for kv host:<hostId>/owner
        internal Dictionary<string, object> ExecutorBody(HostOwnerRecord owner)
        {
            ExecutorConfig ex = _config.executor ?? new ExecutorConfig();
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["host"] = _config.hostId;
            body["kind"] = "bridge";
            body["title"] = String.IsNullOrWhiteSpace(ex.title) ? "" : ex.title.Trim(); // "": the server names it
            body["os"] = ExecutorOs();
            body["machine"] = Environment.MachineName;
            body["version"] = BridgeVersion;
            body["ownerEnv"] = ex.ownerEnv ?? "";
            body["state"] = _draining ? "updating" : (_paused ? "paused" : "running");

            List<string> agents = new List<string>();
            if (owner != null && owner.agents != null)
                agents.AddRange(owner.agents.Keys);
            List<string> caps = new List<string>();
            if (owner != null && owner.capabilities != null)
                caps.AddRange(owner.capabilities);
            // the server makes a Drive inbox copy of an unclaimed job only for an executor that reads one (Google Drive for
            // desktop exists on Windows and macOS; a Linux server keeps its transport folder local)
            bool driveInbox = ex.driveInbox ?? (ExecutorOs() == "windows" || ExecutorOs() == "macos");
            if (driveInbox && !String.IsNullOrEmpty(_hostTransportRoot) && Directory.Exists(_hostTransportRoot))
                caps.Add("drive-inbox");
            Dictionary<string, object> labels = new Dictionary<string, object>();
            labels["agents"] = agents.ToArray();
            labels["capabilities"] = caps.ToArray();
            labels["devices"] = owner != null && owner.devices != null ? owner.devices.Count : 0;
            if (ex.serves != null)
                labels["serves"] = ex.serves;
            body["labels"] = labels;
            if (owner != null)
            {
                owner.state = (string)body["state"];
                owner.runningJobs = _runningJobs.Count;
                body["owner"] = owner;
            }
            return body;
        }

        private void ExecutorBeatLoop()
        {
            ExecutorConfig ex = _config.executor ?? new ExecutorConfig();
            int seconds = Math.Max(10, Math.Min(60, ex.beatSeconds <= 0 ? 30 : ex.beatSeconds));
            bool oldServer = false;
            while (!_exiting)
            {
                if (!_hostConflict && _lastOwnerRecord != null)
                {
                    try
                    {
                        Dictionary<string, object> resp = DbqCall("executor_put", ExecutorBody(_lastOwnerRecord), true, 15000);
                        object okv;
                        if (resp.TryGetValue("ok", out okv) && okv is bool && (bool)okv)
                        {
                            if (_executorState != "ok")
                                Log("Executor " + _config.hostId + " registered (" + ExecutorOs() + ", every " + seconds + " s).");
                            _executorState = "ok";
                            _executorTaken = false;
                        }
                    }
                    catch (Exception e)
                    {
                        string m = e.Message ?? "";
                        if (m.Contains("host_taken"))
                        {
                            _executorState = "host_taken";
                            _executorTaken = true;
                            LogThrottled("executor-taken", "Executor id '" + _config.hostId + "' is used by another machine: give this Bridge its own hostId in config.json. " + PushCut(m, 200));
                        }
                        else if (m.Contains("unknown_op") || m.Contains("HTTP 400"))
                        {
                            // a server before schema 28: the first two ids live by owner.json on Drive as before
                            if (!oldServer)
                                Log("Executor heartbeat: the server does not know executor_put yet (" + PushCut(m, 120) + "); retrying every 10 min.");
                            oldServer = true;
                            _executorState = "server before schema 28";
                        }
                        else
                        {
                            _executorState = "error: " + m;
                            LogThrottled("executor-beat", "Executor heartbeat failed: " + m);
                        }
                    }
                }
                for (int i = 0; i < (oldServer ? 600 : seconds) && !_exiting; i++)
                    Thread.Sleep(1000);
            }
        }

        // ---------------------------------------------------------------- claim

        private void DbqClaimLoop()
        {
            int failures = 0;
            while (!_exiting)
            {
                int waitMs = failures == 0 ? DbqPollSeconds() * 1000 : Math.Min(60000, 2000 << Math.Min(failures, 5));
                _dbqWake.WaitOne(waitMs);
                if (_exiting)
                    break;
                if (_hostConflict || _executorTaken)
                    continue;

                try
                {
                    // v1.16.0: continuations of jobs paused for the update (Drive inbox) are taken first
                    int free = _paused || UpdateContinuationsPending() ? 0 : Math.Max(0, Math.Min(10, _config.maxParallelJobs - _runningJobs.Count));

                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["host"] = _config.hostId;
                    body["max"] = free;
                    body["machine"] = Environment.MachineName; // v1.19.0: a second machine with this id gets no jobs
                    if (FollowupsEnabled)
                        body["followups"] = true;
                    Dictionary<string, object> resp = DbqCall("job_claim", body, false);
                    if (failures > 0)
                        Log("Database queue: reachable again.");
                    failures = 0;
                    _dbqState = "ok";
                    SupClaimOk(); // v1.9.0

                    object[] cancel = DbqArray(resp, "cancel");
                    foreach (object c in cancel)
                        DbqCancel(c as string);

                    DeliverFollowups(DbqArray(resp, "followups"));

                    object[] jobs = DbqArray(resp, "jobs");
                    foreach (object o in jobs)
                    {
                        Dictionary<string, object> j = o as Dictionary<string, object>;
                        if (j != null)
                            DbqStart(DictString(j, "jobId"), DictString(j, "request"));
                    }

                    // A full batch may leave more jobs waiting.
                    if (free > 0 && jobs.Length >= free)
                        _dbqWake.Set();
                }
                catch (Exception ex)
                {
                    failures++;
                    _dbqState = "error: " + ex.Message;
                    SupClaimFailed(ex); // v1.9.0
                    LogThrottled("dbq-claim", "Database queue: claim failed: " + ex.Message);
                }
            }
        }

        private void DbqCancel(string jobId)
        {
            if (String.IsNullOrEmpty(jobId))
                return;
            WindowsJobObject jobObject;
            if (!_runningJobObjects.TryGetValue(jobId, out jobObject))
            {
                // v1.10.3: Stop on a job that no longer runs here (lost with an earlier Bridge process).
                if (JobIdAlreadyExists(jobId))
                    DbqCloseKnown(jobId, "cancel requested");
                return;
            }
            if (_cancelRequested.ContainsKey(jobId))
                return;
            _cancelRequested[jobId] = 0;
            Log("Cancellation requested for running job " + jobId + " (database).");
            try { jobObject.Terminate(4); } catch { }
        }

        // Materializes a claimed database job as claimed/<jobId> on G: and starts it. A job this host
        // already knows (Drive copy of the same jobId, lease re-offer) is skipped; v1.10.3: if it no longer runs here,
        // its earlier result goes to the database (DbqCloseKnown).
        private void DbqStart(string jobId, string requestJson)
        {
            try
            {
                ValidateSafeId(jobId, "database jobId");
            }
            catch (Exception ex)
            {
                Log("Database queue: ignoring job with invalid id: " + ex.Message);
                return;
            }

            if (String.IsNullOrEmpty(requestJson))
            {
                DbqReject(jobId, "Empty request in the database.");
                return;
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(requestJson);
            string claimedPath = Path.Combine(_claimedDir, jobId);

            Monitor.Enter(_scanLock);
            try
            {
                if (_runningJobs.ContainsKey(jobId) || _fastClaimed.ContainsKey(jobId) ||
                    JobIdAlreadyExists(jobId) || Directory.Exists(claimedPath))
                {
                    Log("Database job " + jobId + " is already known on this host; skipped.");
                    DbqCloseKnown(jobId, "offered again");
                    return;
                }
                // Not taken now: the database offers it again after the claim lease.
                if (_exiting || _paused || _hostConflict || _runningJobs.Count >= _config.maxParallelJobs)
                    return;

                string reqPath = Path.Combine(claimedPath, "request.json");
                Directory.CreateDirectory(claimedPath);
                File.WriteAllBytes(reqPath, bytes);
                CommitMarkerUtil.WriteForBytes(bytes, "request.json", Path.Combine(claimedPath, "request.ready"));

                try
                {
                    RecordUsedJobId(jobId);
                }
                catch (Exception ex)
                {
                    RejectJobFolder(claimedPath, jobId, "Could not persist jobId reservation: " + ex.Message);
                    return;
                }

                // A Drive inbox copy of the same job (CO Web fallback after a lost answer) is then dropped silently.
                _fastClaimed[jobId] = DateTime.UtcNow;

                if (!_runningJobs.TryAdd(jobId, 0))
                {
                    RejectJobFolder(claimedPath, jobId, "Internal duplicate running jobId.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("Database queue: could not claim " + jobId + ": " + ex.Message);
                return;
            }
            finally
            {
                Monitor.Exit(_scanLock);
            }

            RememberJobEnvironment(jobId, PreviewEnvironment(bytes));
            Log("Claimed job " + jobId + " via database.");
            PubSubPublish("job.claimed", jobId, new Dictionary<string, object> { { "via", "db" } });

            string claimedCopy = claimedPath;
            Task.Factory.StartNew(
                delegate { ProcessJob(jobId, claimedCopy); },
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
        }

        // v1.8.1: asked before a job from the Drive inbox runs (database fallback copy, attachment job). The database
        // marks a queued job claimed (a later job_cancel then reaches it through job_claim) and answers "skip" for a job
        // it knows as cancelled or finished (01.11: a cancelled job ran from its fallback copy and became completed).
        // Database unreachable: the copy waits and is asked about again on each scan. A server fallback copy (file
        // db-fallback) never runs without an answer; another copy runs after DriveClaimDeferMinutes of failures (CO Web
        // writes jobs to the Drive inbox exactly when the database fails). Database queue off: run.
        private const int DriveClaimRun = 0, DriveClaimSkip = 1, DriveClaimDefer = 2;
        private const int DriveClaimDeferMinutes = 5;
        private readonly ConcurrentDictionary<string, DateTime> _driveClaimDeferred =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private int DbqDriveClaim(string jobId, bool fallbackCopy)
        {
            if (!DbqEnabled)
                return DriveClaimRun;
            DateTime firstFail;
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["host"] = _config.hostId;
                body["jobId"] = jobId;
                Dictionary<string, object> resp = DbqCall("job_drive_claim", body, false, 8000);
                _driveClaimDeferred.TryRemove(jobId, out firstFail);
                if (DictString(resp, "action") == "skip")
                {
                    Log("Drive copy of job " + jobId + " not run: the database has it as " +
                        (DictString(resp, "state") ?? "?") + " (cancelled or finished).");
                    return DriveClaimSkip;
                }
                return DriveClaimRun;
            }
            catch (Exception ex)
            {
                firstFail = _driveClaimDeferred.GetOrAdd(jobId, DateTime.UtcNow);
                if (fallbackCopy || DateTime.UtcNow - firstFail < TimeSpan.FromMinutes(DriveClaimDeferMinutes))
                {
                    LogThrottled("dbq-drive-claim", "Database queue: job_drive_claim for " + jobId + " failed (" +
                        ex.Message + "); the Drive copy waits.");
                    return DriveClaimDefer;
                }
                _driveClaimDeferred.TryRemove(jobId, out firstFail);
                Log("Database queue: no answer about job " + jobId + " for " + DriveClaimDeferMinutes +
                    " min; running its Drive copy.");
                return DriveClaimRun;
            }
        }

        // ---------------------------------------------------------------- copies to the database

        private void DbqEnqueue(DbqPost p)
        {
            if (_dbqPostThread == null || _dbqOut.IsAddingCompleted)
                return;
            if (!_dbqOut.TryAdd(p))
                Interlocked.Increment(ref _dbqDropped);
        }

        private void DbqPostEvents(string jobId, EventBatch batch)
        {
            DbqPost p = new DbqPost();
            p.kind = "events";
            p.jobId = jobId;
            p.payload = batch;
            DbqEnqueue(p);
        }

        // resultPath is claimed/<jobId>/result.json; request.json lies next to it.
        private void DbqPostResult(string jobId, string resultPath)
        {
            if (_dbqPostThread == null)
                return;
            try
            {
                string dir = Path.GetDirectoryName(resultPath);
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["host"] = _config.hostId;
                body["request"] = JsonUtil.DeserializeObject(ReadJsonText(Path.Combine(dir, "request.json")));
                body["result"] = JsonUtil.DeserializeObject(ReadJsonText(resultPath));

                DbqPost p = new DbqPost();
                p.kind = "result";
                p.jobId = jobId;
                p.payload = body;
                DbqEnqueue(p);
            }
            catch (Exception ex)
            {
                LogThrottled("dbq-result", "Database queue: result of " + jobId + " not copied: " + ex.Message);
            }
        }

        private void DbqReject(string jobId, string reason)
        {
            DbqPost p = new DbqPost();
            p.kind = "reject";
            p.jobId = jobId;
            p.payload = reason;
            DbqEnqueue(p);
        }

        private static string ReadJsonText(string path)
        {
            string s = File.ReadAllText(path, Encoding.UTF8);
            return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
        }

        private void DbqPostLoop()
        {
            while (!_exiting)
            {
                DbqPost first;
                try
                {
                    if (!_dbqOut.TryTake(out first, 1000))
                        continue;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                string op;
                bool gz = true;
                Dictionary<string, object> body;
                if (first.kind == "events")
                {
                    // Coalesce queued batches into one call (order kept; the server dedupes by jobId + firstSequence).
                    List<object> batches = new List<object>();
                    batches.Add(DbqBatch(first));
                    DbqPost next;
                    while (batches.Count < 100 && _dbqOut.TryTake(out next))
                    {
                        if (next.kind == "events")
                        {
                            batches.Add(DbqBatch(next));
                            continue;
                        }
                        DbqSend("job_post_events", DbqEventsBody(batches), true, "events");
                        batches.Clear();
                        DbqSendOne(next);
                    }
                    if (batches.Count == 0)
                        continue;
                    op = "job_post_events";
                    body = DbqEventsBody(batches);
                }
                else
                {
                    DbqSendOne(first);
                    continue;
                }
                DbqSend(op, body, gz, "events");
            }
        }

        private static Dictionary<string, object> DbqBatch(DbqPost p)
        {
            Dictionary<string, object> b = new Dictionary<string, object>();
            b["jobId"] = p.jobId;
            b["batch"] = p.payload;
            return b;
        }

        private static Dictionary<string, object> DbqEventsBody(List<object> batches)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["batches"] = batches.ToArray();
            return body;
        }

        private void DbqSendOne(DbqPost p)
        {
            if (p.kind == "result")
            {
                DbqSend("job_result", (Dictionary<string, object>)p.payload, true, "result " + p.jobId);
            }
            else if (p.kind == "reject")
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["jobId"] = p.jobId;
                body["reason"] = p.payload as string ?? "Rejected by Bridge.";
                DbqSend("job_reject", body, false, "reject " + p.jobId);
            }
        }

        // Up to 4 attempts (2, 4, 8 s apart); then the copy is dropped: drive-sync brings result.json from Drive
        // within a minute, lost event batches only thin the live view.
        private void DbqSend(string op, Dictionary<string, object> body, bool gz, string what)
        {
            for (int attempt = 0; attempt < 4 && !_exiting; attempt++)
            {
                try
                {
                    DbqCall(op, body, gz);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 3)
                    {
                        Interlocked.Increment(ref _dbqDropped);
                        LogThrottled("dbq-post-" + op, "Database queue: " + op + " (" + what + ") dropped: " + ex.Message);
                        return;
                    }
                    for (int waited = 0; waited < (2000 << attempt) && !_exiting; waited += 250)
                        Thread.Sleep(250);
                }
            }
        }

        // ---------------------------------------------------------------- HTTP

        private Dictionary<string, object> DbqCall(string op, Dictionary<string, object> body, bool gz)
        {
            return DbqCall(op, body, gz, 20000);
        }

        private Dictionary<string, object> DbqCall(string op, Dictionary<string, object> body, bool gz, int timeoutMs)
        {
            PushEnsureTls12();

            string target = _config.push != null && !String.IsNullOrEmpty(_config.push.credentialTarget)
                ? _config.push.credentialTarget
                : PushDefaultTarget;
            string key = CredentialStore.Read(target);
            key = key == null ? "" : key.Replace("\0", "").Trim();
            if (key.Length == 0)
                throw new InvalidOperationException("no key in Credential Manager (" + target + ")");

            Dictionary<string, object> payload = new Dictionary<string, object>(body);
            payload["op"] = op;
            string json = JsonUtil.Serialize(payload);
            if (gz)
            {
                // The hosting firewall rejects plain bodies that contain code or SQL (chat text, tool output).
                Dictionary<string, object> wrap = new Dictionary<string, object>();
                wrap["gz"] = Convert.ToBase64String(Gzip(new UTF8Encoding(false).GetBytes(json)));
                json = JsonUtil.Serialize(wrap);
            }
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_config.dbQueue.url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Headers["X-CO-Push-Key"] = key;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.ContentLength = bytes.Length;
            using (Stream rs = req.GetRequestStream())
                rs.Write(bytes, 0, bytes.Length);

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    Dictionary<string, object> d = JsonUtil.DeserializeObject(PushReadBody(resp)) as Dictionary<string, object>;
                    if (d == null)
                        throw new InvalidOperationException("unexpected answer");
                    return d;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp == null)
                    throw;
                // the status before Close: .NET 8 throws ObjectDisposedException on StatusCode of a closed response
                int code = (int)errResp.StatusCode;
                string text = "";
                try { text = PushReadBody(errResp); } catch { }
                finally { errResp.Close(); }
                throw new InvalidOperationException("HTTP " + code + " " + PushCut(text, 200));
            }
        }

        private static byte[] Gzip(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static object[] DbqArray(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return new object[0];
            object[] a = v as object[];
            if (a != null)
                return a;
            System.Collections.ArrayList l = v as System.Collections.ArrayList;
            return l != null ? l.ToArray() : new object[0];
        }

        private string DbQueueStatus()
        {
            if (!DbqEnabled)
                return "off";
            long dropped = Interlocked.Read(ref _dbqDropped);
            return ShortState(_dbqState) + (dropped > 0 ? " (" + dropped + " dropped)" : "");
        }
    }
}
