// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if BRIDGE_HEADLESS
using AiWorkbenchBridge.HeadlessUi;
#else
using System.Windows.Forms;
#endif

namespace AiWorkbenchBridge
{
    /*
     * FAST TRANSPORT (v0.9)
     * =====================
     * Three tiers, each a fallback for the one above:
     *   1. Pub/Sub   - signals: job.ready / job.cancel in, job.claimed / job.events / job.result out.
     *   2. Drive API - Bridge polls the inbox folder through the API and downloads the package.
     *   3. G:        - the original Drive-for-Desktop watcher (unchanged, always on).
     *
     * Trust model is unchanged: a Pub/Sub message is only a hint that carries jobId and a
     * folder id. The job itself is always read from transport/hosts/<hostId>/inbox/<jobId>
     * in Drive and passes the same commit-marker, attachment and request validation as the
     * G: path. Pub/Sub output is a copy of what is written to Drive; Drive stays canonical.
     *
     * The OAuth refresh token lives in Windows Credential Manager, never in a file.
     * Every Google call runs on background threads; failures only degrade to the next tier.
     */
    internal sealed partial class BridgeApplicationContext
    {
        private const string GoogleCredentialTarget = "AIWorkbench.Bridge.Google";
        private const string GoogleScopes =
            "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/pubsub";

        private GoogleAuth _gAuth;
        private volatile string _psState = "off";
        private volatile string _apiState = "off";
        private volatile string _gInboxFolderId;
        private volatile bool _psResourcesReady;
        private DateTime _psResourcesCheckedUtc = DateTime.MinValue;
        private Thread[] _psPullThreads;
        private Thread _psPublishThread;
        private Thread _apiPollThread;
        private readonly BlockingCollection<PubSubOutMessage> _psOutbox =
            new BlockingCollection<PubSubOutMessage>(new ConcurrentQueue<PubSubOutMessage>(), 2000);
        private readonly ConcurrentDictionary<string, byte> _fastInFlight =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _fastClaimed =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private long _psDropped;

        private GoogleSettings G
        {
            get
            {
                GoogleSettings s = _config.google ?? new GoogleSettings();
                if (s.apiPollSeconds <= 0) s.apiPollSeconds = 5;
                if (s.pullThreads <= 0) s.pullThreads = 2;
                return s;
            }
        }

        private string JobsTopic { get { return "co-" + _config.hostId + "-jobs"; } }
        private string EventsTopic { get { return "co-" + _config.hostId + "-events"; } }

        // Live output is partitioned by environment (one topic + one CO pull subscription each).
        // Access to a partition is granted by the owner in Google Cloud IAM, not by Bridge.
        private string EnvEventsTopic(string env) { return EventsTopic + "-" + env; }
        private string EnvWebSubscription(string env) { return EventsTopic + "-" + env + "-web"; }

        private readonly ConcurrentDictionary<string, string> _jobEnv =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private void RememberJobEnvironment(string jobId, string env)
        {
            if (!String.IsNullOrEmpty(jobId) && !String.IsNullOrEmpty(env))
                _jobEnv[jobId] = env;
        }
        private string JobsSubscription { get { return "co-" + _config.hostId + "-jobs-bridge"; } }

        // ---------------------------------------------------------------- lifecycle

        private void StartFastTransport()
        {
            GoogleSettings g = G;
            if (!g.enabled)
            {
                Log("Fast transport disabled in config; G: only.");
                return;
            }

            _gAuth = new GoogleAuth(GoogleCredentialTarget);
            _psState = _gAuth.HasCredentials ? "starting" : "no credentials";
            _apiState = _psState;

            if (g.pubsub && !String.IsNullOrWhiteSpace(g.projectId))
            {
                _psPullThreads = new Thread[g.pullThreads];
                for (int i = 0; i < _psPullThreads.Length; i++)
                {
                    _psPullThreads[i] = new Thread(PubSubPullLoop);
                    _psPullThreads[i].IsBackground = true;
                    _psPullThreads[i].Name = "pubsub-pull-" + i;
                    _psPullThreads[i].Start();
                }

                _psPublishThread = new Thread(PubSubPublishLoop);
                _psPublishThread.IsBackground = true;
                _psPublishThread.Name = "pubsub-publish";
                _psPublishThread.Start();
            }
            else
            {
                _psState = "off";
            }

            _apiPollThread = new Thread(ApiPollLoop);
            _apiPollThread.IsBackground = true;
            _apiPollThread.Name = "drive-api-poll";
            _apiPollThread.Start();

            Log("Fast transport started: pubsub=" + g.pubsub + " project=" + g.projectId +
                " apiPoll=" + g.apiPollSeconds + "s credentials=" + _gAuth.HasCredentials);
        }

        private void StopFastTransport()
        {
            try { _psOutbox.CompleteAdding(); } catch { }
        }

        private string FastTransportStatus()
        {
            if (_gAuth == null)
                return "G: only";
            return "PubSub " + ShortState(_psState) + " | API " + ShortState(_apiState) + " | DB " + DbQueueStatus() + " | G: on";
        }

        private static string ShortState(string s)
        {
            if (s == null) return "?";
            if (s.Length > 40) return s.Substring(0, 40);
            return s;
        }

        private OwnerFastTransport BuildOwnerFastTransport()
        {
            OwnerFastTransport t = new OwnerFastTransport();
            GoogleSettings g = G;
            t.projectId = g.projectId;
            t.jobsTopic = JobsTopic;
            t.eventsTopic = EventsTopic;
            t.webSubscriptionPattern = EventsTopic + "-{env}-web";
            t.pubsub = _psState;
            t.api = _apiState;
            return t;
        }

        // Rest periods between failures grow to 60 s; a healthy call resets them.
        private void Backoff(ref int delayMs)
        {
            for (int waited = 0; waited < delayMs && !_exiting; waited += 250)
                Thread.Sleep(250);
            delayMs = Math.Min(60000, Math.Max(1000, delayMs * 2));
        }

        private bool GoogleReady()
        {
            if (_gAuth == null || _exiting)
                return false;
            if (!_gAuth.HasCredentials)
            {
                _psState = G.pubsub ? "no credentials" : "off";
                _apiState = "no credentials";
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- Pub/Sub in

        private void EnsurePubSubResources(string token)
        {
            if (_psResourcesReady && (DateTime.UtcNow - _psResourcesCheckedUtc).TotalMinutes < 30)
                return;

            PubSubLite ps = new PubSubLite(G.projectId, token);
            ps.EnsureTopic(JobsTopic);
            // Short retention: an old signal is worthless, the API/G: tiers cover the gap.
            ps.EnsureSubscription(JobsSubscription, JobsTopic, 30, "600s", null);

            foreach (string env in KnownEnvironments())
            {
                ps.EnsureTopic(EnvEventsTopic(env));
                ps.EnsureSubscription(EnvWebSubscription(env), EnvEventsTopic(env), 10, "600s", null);
            }
            _psResourcesReady = true;
            _psResourcesCheckedUtc = DateTime.UtcNow;
        }

        private void PubSubPullLoop()
        {
            int delay = 1000;
            while (!_exiting)
            {
                try
                {
                    if (!GoogleReady())
                    {
                        Backoff(ref delay);
                        continue;
                    }

                    string token = _gAuth.GetAccessToken();
                    EnsurePubSubResources(token);
                    PubSubLite ps = new PubSubLite(G.projectId, token);
                    List<PubSubInMessage> msgs = ps.Pull(JobsSubscription, 10, 90000);
                    _psState = "ok";
                    delay = 1000;

                    if (msgs.Count == 0)
                        continue;

                    ps.Acknowledge(JobsSubscription, msgs.Select(m => m.ackId).ToList());

                    foreach (PubSubInMessage m in msgs)
                        HandleJobsMessage(m);
                }
                catch (GoogleAuthException ex)
                {
                    _psState = "auth: " + ex.Message;
                    Backoff(ref delay);
                }
                catch (WebException ex)
                {
                    // A held pull that times out on our side is an empty pull, not a failure.
                    if (ex.Status == WebExceptionStatus.Timeout)
                        continue;
                    _psState = "error: " + ex.Message;
                    _psResourcesReady = false;
                    LogThrottled("pubsub-pull", "Pub/Sub pull failed: " + ex.Message);
                    Backoff(ref delay);
                }
                catch (Exception ex)
                {
                    _psState = "error: " + ex.Message;
                    _psResourcesReady = false;
                    LogThrottled("pubsub-pull", "Pub/Sub pull failed: " + ex.Message);
                    Backoff(ref delay);
                }
            }
        }

        private void HandleJobsMessage(PubSubInMessage m)
        {
            string type;
            string jobId;
            m.attributes.TryGetValue("type", out type);
            m.attributes.TryGetValue("jobId", out jobId);

            try
            {
                ValidateSafeId(jobId, "pubsub jobId");
            }
            catch (Exception ex)
            {
                Log("Ignoring Pub/Sub message with invalid jobId: " + ex.Message);
                return;
            }

            if (type == "job.ready")
            {
                string folderId = null;
                bool db = false;
                try
                {
                    Dictionary<string, object> data =
                        JsonUtil.DeserializeObject(Encoding.UTF8.GetString(m.data)) as Dictionary<string, object>;
                    folderId = DictString(data, "folderId");
                    object dbFlag;
                    db = data != null && data.TryGetValue("db", out dbFlag) && dbFlag is bool && (bool)dbFlag;
                }
                catch
                {
                }

                // v1.3.0: the job is in the database queue, not in the Drive inbox.
                if (db)
                {
                    DbqWake();
                    return;
                }

                string idCopy = jobId;
                string folderCopy = folderId;
                Task.Factory.StartNew(delegate { FastIntake(idCopy, folderCopy, "pubsub"); });
            }
            else if (type == "job.cancel")
            {
                _cancelRequested[jobId] = 0;
                WindowsJobObject jobObject;
                if (_runningJobObjects.TryGetValue(jobId, out jobObject))
                {
                    Log("Cancellation requested for running job " + jobId + " (Pub/Sub).");
                    try { jobObject.Terminate(4); } catch { }
                }
            }
        }

        // ---------------------------------------------------------------- Pub/Sub out

        private void PubSubPublish(string type, string jobId, object payload)
        {
            if (_gAuth == null || _psPublishThread == null || _psOutbox.IsAddingCompleted)
                return;

            try
            {
                string env;
                if (!_jobEnv.TryGetValue(jobId, out env))
                    return; // environment unknown yet: Drive carries this part
                PubSubOutMessage m = new PubSubOutMessage();
                m.topic = EnvEventsTopic(env);
                m.type = type;
                m.jobId = jobId;
                m.data = Encoding.UTF8.GetBytes(JsonUtil.Serialize(payload));
                if (!_psOutbox.TryAdd(m))
                    Interlocked.Increment(ref _psDropped);
            }
            catch (Exception ex)
            {
                LogThrottled("pubsub-enqueue", "Pub/Sub enqueue failed: " + ex.Message);
            }
        }

        private void FastPublishEvents(string jobId, EventBatch batch)
        {
            PubSubPublish("job.events", jobId, batch);
        }

        private void FastPublishResult(string jobId, string resultPath, string readyPath)
        {
            DbqPostResult(jobId, resultPath); // v1.3.0
            try
            {
                FastResultMessage r = new FastResultMessage();
                r.result = Convert.ToBase64String(File.ReadAllBytes(resultPath));
                r.marker = File.ReadAllText(readyPath, Encoding.UTF8);
                PubSubPublish("job.result", jobId, r);
            }
            catch (Exception ex)
            {
                LogThrottled("pubsub-result", "Pub/Sub result publish skipped: " + ex.Message);
            }
        }

        private void PubSubPublishLoop()
        {
            int delay = 1000;
            while (!_exiting)
            {
                PubSubOutMessage first;
                try
                {
                    if (!_psOutbox.TryTake(out first, 1000))
                        continue;
                }
                catch (InvalidOperationException)
                {
                    return; // completed
                }

                List<PubSubOutMessage> batch = new List<PubSubOutMessage>();
                batch.Add(first);
                PubSubOutMessage more;
                while (batch.Count < 50 && _psOutbox.TryTake(out more))
                    batch.Add(more);

                if (!GoogleReady())
                {
                    Interlocked.Add(ref _psDropped, batch.Count);
                    continue;
                }

                try
                {
                    string token = _gAuth.GetAccessToken();
                    EnsurePubSubResources(token);
                    PubSubLite ps = new PubSubLite(G.projectId, token);
                    foreach (IGrouping<string, PubSubOutMessage> byTopic in batch.GroupBy(x => x.topic))
                        ps.Publish(byTopic.Key, byTopic.ToList(), _config.hostId);
                    delay = 1000;
                }
                catch (Exception ex)
                {
                    // Output is also on Drive; a lost live copy only delays the viewer.
                    Interlocked.Add(ref _psDropped, batch.Count);
                    _psState = "publish error: " + ex.Message;
                    _psResourcesReady = false;
                    LogThrottled("pubsub-publish", "Pub/Sub publish failed: " + ex.Message);
                    Backoff(ref delay);
                }
            }
        }

        // ---------------------------------------------------------------- Drive API tier

        private string ResolveInboxFolderId(DriveLite drive)
        {
            string cached = _gInboxFolderId;
            if (cached != null)
                return cached;

            string rootId = drive.FindWorkbenchRoot("AI-Workbench");
            if (rootId == null)
            {
                int candidates = drive.Query("name='AI-Workbench' and mimeType='" + DriveLite.FolderMime + "' and trashed=false").Count;
                throw new InvalidOperationException("AI-Workbench with transport/ not visible to " + drive.AccountEmail() +
                    " (" + candidates + " folder(s) named AI-Workbench).");
            }

            string id = rootId;
            foreach (string part in new string[] { "transport", "hosts", _config.hostId, "inbox" })
            {
                id = drive.FindChildId(id, part, true);
                if (id == null)
                    throw new InvalidOperationException("Drive API: missing transport folder '" + part + "'.");
            }

            _gInboxFolderId = id;
            Log("Drive API inbox resolved: " + id);
            return id;
        }

        private void ApiPollLoop()
        {
            int delay = 1000;
            while (!_exiting)
            {
                try
                {
                    int waitMs = G.apiPollSeconds * 1000;
                    for (int waited = 0; waited < waitMs && !_exiting; waited += 250)
                        Thread.Sleep(250);

                    if (_paused || _hostConflict || !GoogleReady())
                        continue;

                    DriveLite drive = new DriveLite(_gAuth.GetAccessToken());
                    string inboxId = ResolveInboxFolderId(drive);
                    List<DriveItem> jobs = drive.ListChildren(inboxId);
                    _apiState = "ok";
                    delay = 1000;

                    foreach (DriveItem job in jobs)
                    {
                        if (job.mimeType != DriveLite.FolderMime)
                            continue;
                        if (_runningJobs.ContainsKey(job.name) || _fastInFlight.ContainsKey(job.name))
                            continue;
                        FastIntake(job.name, job.id, "api");
                    }
                }
                catch (GoogleAuthException ex)
                {
                    _apiState = "auth: " + ex.Message;
                    Backoff(ref delay);
                }
                catch (Exception ex)
                {
                    _apiState = "error: " + ex.Message;
                    _gInboxFolderId = null;
                    LogThrottled("api-poll", "Drive API poll failed: " + ex.Message);
                    Backoff(ref delay);
                }
            }
        }

        // Downloads inbox/<jobId> through the API, verifies it exactly like the G: path,
        // and claims it into G:\...\claimed\<jobId>. Any doubt returns silently: the job
        // stays in the Drive inbox and a slower tier picks it up.
        private void FastIntake(string jobId, string folderIdHint, string via)
        {
            if (_exiting || _paused || _hostConflict || !GoogleReady())
                return;

            try
            {
                ValidateSafeId(jobId, "jobId");
            }
            catch
            {
                return; // the G: path rejects invalid folders with a record
            }

            if (_runningJobs.ContainsKey(jobId) || _fastClaimed.ContainsKey(jobId) || JobIdAlreadyExists(jobId))
                return;
            if (_runningJobs.Count >= _config.maxParallelJobs)
                return;
            if (!_fastInFlight.TryAdd(jobId, 0))
                return;

            Stopwatch sw = Stopwatch.StartNew();
            string staging = Path.Combine(_localStateRoot, "fastintake", jobId);

            try
            {
                DriveLite drive = new DriveLite(_gAuth.GetAccessToken());
                string inboxId = ResolveInboxFolderId(drive);

                string folderId = null;
                if (!String.IsNullOrEmpty(folderIdHint))
                {
                    DriveItem meta = drive.GetMeta(folderIdHint);
                    if (meta != null && !meta.trashed && meta.name == jobId &&
                        meta.mimeType == DriveLite.FolderMime && meta.parents.Contains(inboxId))
                    {
                        folderId = meta.id;
                    }
                }

                if (folderId == null)
                    folderId = drive.FindChildId(inboxId, jobId, true);
                if (folderId == null)
                    return;

                List<DriveItem> files = drive.ListChildren(folderId);
                DriveItem req = files.FirstOrDefault(f => f.name == "request.json" && f.mimeType != DriveLite.FolderMime);
                DriveItem ready = files.FirstOrDefault(f => f.name == "request.ready" && f.mimeType != DriveLite.FolderMime);
                if (req == null || ready == null)
                    return; // producer has not committed yet

                TryDeleteDirectory(staging);
                Directory.CreateDirectory(staging);

                Task<byte[]> reqTask = Task.Factory.StartNew(() => drive.Download(req.id));
                Task<byte[]> readyTask = Task.Factory.StartNew(() => drive.Download(ready.id));
                File.WriteAllBytes(Path.Combine(staging, "request.json"), reqTask.Result);
                File.WriteAllBytes(Path.Combine(staging, "request.ready"), readyTask.Result);

                byte[] requestBytes;
                string reason;
                if (!CommitMarkerUtil.TryReadVerifiedPayload(
                        Path.Combine(staging, "request.json"),
                        Path.Combine(staging, "request.ready"),
                        out requestBytes,
                        out reason))
                {
                    return;
                }

                RememberJobEnvironment(jobId, PreviewEnvironment(requestBytes));
                DownloadAttachments(drive, files, requestBytes, staging);

                bool incomplete;
                if (!TryVerifyAttachmentPackage(staging, requestBytes, out incomplete, out reason))
                    return; // incomplete or invalid: the G: path waits or writes the rejection

                // v1.8.1: a Drive copy of a job the database knows as cancelled or finished never runs.
                int driveClaim = DbqDriveClaim(jobId, files.Any(f => f.name == "db-fallback"));
                if (driveClaim == DriveClaimSkip)
                {
                    try { drive.Trash(folderId); } catch { }
                    TryDeleteDirectory(Path.Combine(_inboxDir, jobId));
                    return;
                }
                if (driveClaim == DriveClaimDefer)
                    return; // the G: scan asks again

                string claimedPath = Path.Combine(_claimedDir, jobId);

                Monitor.Enter(_scanLock);
                try
                {
                    if (_exiting || _paused || _hostConflict)
                        return;
                    if (_runningJobs.ContainsKey(jobId) || JobIdAlreadyExists(jobId) || Directory.Exists(claimedPath))
                        return;
                    if (_runningJobs.Count >= _config.maxParallelJobs)
                        return;

                    CopyDirectoryTree(staging, claimedPath);

                    try
                    {
                        RecordUsedJobId(jobId);
                    }
                    catch (Exception ex)
                    {
                        RejectJobFolder(claimedPath, jobId, "Could not persist jobId reservation: " + ex.Message);
                        return;
                    }

                    _fastClaimed[jobId] = DateTime.UtcNow;

                    if (!_runningJobs.TryAdd(jobId, 0))
                    {
                        RejectJobFolder(claimedPath, jobId, "Internal duplicate running jobId.");
                        return;
                    }

                    // If Drive for Desktop already delivered the inbox copy, remove it here.
                    TryDeleteDirectory(Path.Combine(_inboxDir, jobId));
                }
                finally
                {
                    Monitor.Exit(_scanLock);
                }

                Log("Claimed job " + jobId + " via " + via + " in " + sw.ElapsedMilliseconds + " ms.");
                PubSubPublish("job.claimed", jobId, new Dictionary<string, object> { { "via", via } });

                string claimedCopy = claimedPath;
                Task.Factory.StartNew(
                    delegate { ProcessJob(jobId, claimedCopy); },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);

                // The job now lives in claimed on G:; drop the inbox copy in Drive so the
                // G: path never sees it. Failure is harmless (the scan deletes a late copy).
                try
                {
                    drive.Trash(folderId);
                }
                catch (Exception ex)
                {
                    LogThrottled("api-trash", "Could not trash Drive inbox folder for " + jobId + ": " + ex.Message);
                }
            }
            catch (GoogleAuthException ex)
            {
                _apiState = "auth: " + ex.Message;
            }
            catch (Exception ex)
            {
                LogThrottled("fast-intake", "Fast intake of " + jobId + " via " + via + " skipped: " + ex.Message);
            }
            finally
            {
                byte ignored;
                _fastInFlight.TryRemove(jobId, out ignored);
                TryDeleteDirectory(staging);
            }
        }

        // Environments that get a live-output partition: the owner's plus chat-project environments.
        private List<string> KnownEnvironments()
        {
            HashSet<string> envs = new HashSet<string>(StringComparer.Ordinal);
            if (_config.projects != null)
                foreach (ProjectProfile p in _config.projects.Values)
                    if (p != null && !String.IsNullOrEmpty(p.environmentId)) envs.Add(p.environmentId);
            if (_config.chatProjectEnvironments != null)
                foreach (string e in _config.chatProjectEnvironments.Keys) envs.Add(e);
            List<string> ok = new List<string>();
            foreach (string e in envs)
            {
                try { ValidateSafeId(e, "environment"); ok.Add(e); } catch { }
            }
            return ok;
        }

        private static string PreviewEnvironment(byte[] requestBytes)
        {
            try
            {
                int offset = requestBytes.Length >= 3 && requestBytes[0] == 0xEF ? 3 : 0;
                JobRequest r = JsonUtil.Deserialize<JobRequest>(
                    new UTF8Encoding(false, true).GetString(requestBytes, offset, requestBytes.Length - offset));
                return r == null ? null : r.environmentId;
            }
            catch
            {
                return null;
            }
        }

        private void DownloadAttachments(DriveLite drive, List<DriveItem> jobFiles, byte[] requestBytes, string staging)
        {
            JobRequest preview;
            try
            {
                int offset = requestBytes.Length >= 3 && requestBytes[0] == 0xEF ? 3 : 0;
                preview = JsonUtil.Deserialize<JobRequest>(
                    new UTF8Encoding(false, true).GetString(requestBytes, offset, requestBytes.Length - offset));
            }
            catch
            {
                return; // validation reports it
            }

            if (preview == null || preview.attachments == null || preview.attachments.Count == 0)
                return;

            DriveItem attDir = jobFiles.FirstOrDefault(f => f.name == "attachments" && f.mimeType == DriveLite.FolderMime);
            if (attDir == null)
                return;

            foreach (AttachmentRequest a in preview.attachments)
            {
                string relative = NormalizeRelativePath(a.path);
                string[] parts = relative.Split('\\');
                string parent = attDir.id;
                for (int i = 0; i < parts.Length - 1 && parent != null; i++)
                    parent = drive.FindChildId(parent, parts[i], true);
                if (parent == null)
                    return;

                string fileId = drive.FindChildId(parent, parts[parts.Length - 1], false);
                if (fileId == null)
                    return;

                string target = SafeCombineUnderRoot(Path.Combine(staging, "attachments"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                drive.DownloadToFile(fileId, target);
            }
        }

        private static void CopyDirectoryTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, dir.Substring(source.Length).TrimStart('\\')));
            // request.ready last, like every producer.
            List<string> files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToList();
            files.Sort((a, b) => (a.EndsWith("request.ready") ? 1 : 0) - (b.EndsWith("request.ready") ? 1 : 0));
            foreach (string file in files)
                File.Copy(file, Path.Combine(target, file.Substring(source.Length).TrimStart('\\')), false);
        }

        // Called from ScanInbox: a late G: copy of a job that the fast path already claimed.
        private bool ConsumeLateInboxCopy(string jobId, string jobDir)
        {
            DateTime claimedUtc;
            if (!_fastClaimed.TryGetValue(jobId, out claimedUtc))
                return false;

            TryDeleteDirectory(jobDir);
            if ((DateTime.UtcNow - claimedUtc).TotalHours > 24)
                _fastClaimed.TryRemove(jobId, out claimedUtc);
            return true;
        }

        private readonly ConcurrentDictionary<string, DateTime> _throttledLog =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private void LogThrottled(string key, string message)
        {
            DateTime last;
            if (_throttledLog.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalMinutes < 5)
                return;
            _throttledLog[key] = DateTime.UtcNow;
            Log(message);
        }

        // ---------------------------------------------------------------- tray actions

        private void ConnectGoogleInteractive()
        {
            string clientFile;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Google OAuth client (Desktop app) JSON";
                dlg.Filter = "OAuth client JSON (*.json)|*.json";
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;
                clientFile = dlg.FileName;
            }

            Thread t = new Thread(delegate()
            {
                try
                {
                    GoogleAuth.RunInstalledAppFlow(clientFile, GoogleCredentialTarget, GoogleScopes);
                    if (_gAuth != null)
                        _gAuth.Reload();
                    _psResourcesReady = false;
                    _gInboxFolderId = null;
                    Log("Google credentials stored in Windows Credential Manager.");

                    DialogResult del = MessageBox.Show(
                        "Google connected. The refresh token is stored in Windows Credential Manager.\n\n" +
                        "Delete the client JSON file now? It is no longer needed:\n" + clientFile,
                        "AI Workbench Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (del == DialogResult.Yes)
                        TryDeleteFile(clientFile);
                }
                catch (Exception ex)
                {
                    Log("Google connect failed: " + ex.Message);
                    MessageBox.Show("Google connect failed:\n" + ex.Message, "AI Workbench Bridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void DisconnectGoogle()
        {
            if (MessageBox.Show("Remove the Google token from Credential Manager? Bridge falls back to G: only.",
                    "AI Workbench Bridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            CredentialStore.Delete(GoogleCredentialTarget);
            if (_gAuth != null)
                _gAuth.Reload();
            Log("Google credentials removed.");
        }
    }

    // ==================================================================== config / records

    internal sealed class GoogleSettings
    {
        public bool enabled { get; set; }
        public string projectId { get; set; }
        public bool pubsub { get; set; }
        public int apiPollSeconds { get; set; }
        public int pullThreads { get; set; }

        public GoogleSettings()
        {
            enabled = true;
            pubsub = true;
            apiPollSeconds = 5;
            pullThreads = 2;
        }
    }

    internal sealed class OwnerFastTransport
    {
        public string projectId { get; set; }
        public string jobsTopic { get; set; }
        public string eventsTopic { get; set; }
        public string webSubscriptionPattern { get; set; }
        public string pubsub { get; set; }
        public string api { get; set; }
    }

    internal sealed class FastResultMessage
    {
        public string result { get; set; }   // base64 of result.json bytes
        public string marker { get; set; }   // result.ready text
    }

    internal sealed class PubSubOutMessage
    {
        public string topic;
        public string type;
        public string jobId;
        public byte[] data;
    }

    internal sealed class PubSubInMessage
    {
        public string ackId;
        public byte[] data;
        public Dictionary<string, string> attributes = new Dictionary<string, string>();
    }

    internal sealed class DriveItem
    {
        public string id;
        public string name;
        public string mimeType;
        public bool trashed;
        public List<string> parents = new List<string>();
    }

    internal sealed class GoogleAuthException : Exception
    {
        public GoogleAuthException(string message) : base(message) { }
    }

    // ==================================================================== HTTP

    internal sealed class GResponse
    {
        public int status;
        public byte[] body;
        public string Text { get { return body == null ? "" : Encoding.UTF8.GetString(body); } }
    }

    internal static class GHttp
    {
        static GHttp()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
        }

        public static GResponse Send(string method, string url, string token, string contentType, byte[] body, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (token != null)
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;

            if (body != null)
            {
                req.ContentType = contentType;
                req.ContentLength = body.Length;
                using (Stream s = req.GetRequestStream())
                    s.Write(body, 0, body.Length);
            }
            else if (method != "GET" && method != "DELETE")
            {
                req.ContentLength = 0;
            }

            GResponse r = new GResponse();
            HttpWebResponse resp = null;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null)
                    throw;
            }

            using (resp)
            {
                r.status = (int)resp.StatusCode;
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    r.body = ms.ToArray();
                }
            }
            return r;
        }

        public static GResponse Json(string method, string url, string token, object payload, int timeoutMs)
        {
            byte[] body = payload == null ? null : Encoding.UTF8.GetBytes(JsonUtil.Serialize(payload));
            return Send(method, url, token, "application/json; charset=utf-8", body, timeoutMs);
        }

        public static Dictionary<string, object> Parse(GResponse r)
        {
            if (r.body == null || r.body.Length == 0)
                return new Dictionary<string, object>();
            return JsonUtil.DeserializeObject(r.Text) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static void Ensure(GResponse r, string what)
        {
            if (r.status == 401 || r.status == 403)
            {
                string t = r.Text;
                throw new InvalidOperationException(what + ": HTTP " + r.status + " " + (t.Length > 300 ? t.Substring(0, 300) : t));
            }
            if (r.status < 200 || r.status >= 300)
            {
                string t = r.Text;
                throw new InvalidOperationException(what + ": HTTP " + r.status + " " + (t.Length > 300 ? t.Substring(0, 300) : t));
            }
        }

        // JavaScriptSerializer.DeserializeObject yields object[] for JSON arrays (ArrayList elsewhere).
        public static IEnumerable<object> Items(object v)
        {
            System.Collections.IEnumerable e = v as System.Collections.IEnumerable;
            if (e == null || v is string)
                yield break;
            foreach (object o in e)
                yield return o;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }
    }

    // ==================================================================== OAuth + keyring

    internal sealed class GoogleAuth
    {
        private readonly string _target;
        private readonly object _sync = new object();
        private Dictionary<string, object> _cred;
        private bool _loaded;
        private DateTime _loadedUtc = DateTime.MinValue;
        private string _access;
        private DateTime _expiresUtc = DateTime.MinValue;
        private DateTime _authFailedUtc = DateTime.MinValue;
        private string _authFailure;

        // v1.2.7: raised when the auth state changes from OK (null) to failed; argument = failure text
        internal static event Action<string> AuthFailed;

        public GoogleAuth(string target)
        {
            _target = target;
        }

        public void Reload()
        {
            lock (_sync)
            {
                _loaded = false;
                _cred = null;
                _access = null;
                _authFailure = null;
            }
        }

        public bool HasCredentials
        {
            get
            {
                lock (_sync)
                {
                    LoadLocked();
                    return _cred != null;
                }
            }
        }

        private void LoadLocked()
        {
            // Missing credentials are re-checked every 30 s, so a new sign-in is picked up.
            if (_loaded && (_cred != null || (DateTime.UtcNow - _loadedUtc).TotalSeconds < 30))
                return;
            _loaded = true;
            _loadedUtc = DateTime.UtcNow;
            string json = CredentialStore.Read(_target);
            _cred = json == null ? null : JsonUtil.DeserializeObject(json) as Dictionary<string, object>;
        }

        public string GetAccessToken()
        {
            lock (_sync)
            {
                if (_access != null && DateTime.UtcNow < _expiresUtc.AddMinutes(-5))
                    return _access;

                LoadLocked();
                if (_cred == null)
                    throw new GoogleAuthException("no credentials");

                // A revoked token is retried at most every 5 minutes.
                if (_authFailure != null && (DateTime.UtcNow - _authFailedUtc).TotalMinutes < 5)
                    throw new GoogleAuthException(_authFailure);

                string form =
                    "client_id=" + Uri.EscapeDataString(GHttp.Str(_cred, "client_id")) +
                    "&client_secret=" + Uri.EscapeDataString(GHttp.Str(_cred, "client_secret")) +
                    "&refresh_token=" + Uri.EscapeDataString(GHttp.Str(_cred, "refresh_token")) +
                    "&grant_type=refresh_token";

                GResponse r = GHttp.Send("POST", "https://oauth2.googleapis.com/token", null,
                    "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes(form), 30000);
                Dictionary<string, object> j = GHttp.Parse(r);

                if (r.status == 400 || r.status == 401)
                {
                    // v1.2.7: AuthFailed fires only when the state changes from OK (null) to failed
                    bool wasOk = _authFailure == null;
                    string failure = "re-connect needed (" + (GHttp.Str(j, "error") ?? ("HTTP " + r.status)) + ")";
                    _authFailure = failure;
                    _authFailedUtc = DateTime.UtcNow;
                    if (wasOk)
                    {
                        Action<string> handler = AuthFailed;
                        if (handler != null)
                        {
                            try
                            {
                                handler(failure);
                            }
                            catch (Exception)
                            {
                                // Subscribers must never break the auth flow.
                            }
                        }
                    }
                    throw new GoogleAuthException(failure);
                }
                GHttp.Ensure(r, "token refresh");

                _authFailure = null;
                _access = GHttp.Str(j, "access_token");
                int expires;
                if (!Int32.TryParse(GHttp.Str(j, "expires_in"), out expires))
                    expires = 3599;
                _expiresUtc = DateTime.UtcNow.AddSeconds(expires);
                return _access;
            }
        }

        // Installed-app OAuth with PKCE and a loopback redirect on 127.0.0.1.
        public static void RunInstalledAppFlow(string clientJsonPath, string target, string scopes)
        {
            Dictionary<string, object> root =
                JsonUtil.DeserializeObject(File.ReadAllText(clientJsonPath, Encoding.UTF8)) as Dictionary<string, object>;
            object inner;
            if (root == null || !root.TryGetValue("installed", out inner))
                throw new InvalidOperationException("Not a Desktop-app OAuth client file (no \"installed\" section).");
            Dictionary<string, object> c = (Dictionary<string, object>)inner;
            string clientId = GHttp.Str(c, "client_id");
            string clientSecret = GHttp.Str(c, "client_secret");
            if (String.IsNullOrEmpty(clientId) || String.IsNullOrEmpty(clientSecret))
                throw new InvalidOperationException("client_id or client_secret missing in the file.");

            string verifier = Base64Url(RandomBytes(48));
            string challenge;
            using (SHA256 sha = SHA256.Create())
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomBytes(16));

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port + "/";
                string url =
                    "https://accounts.google.com/o/oauth2/v2/auth?client_id=" + Uri.EscapeDataString(clientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                    "&response_type=code&access_type=offline&prompt=consent" +
                    "&scope=" + Uri.EscapeDataString(scopes) +
                    "&code_challenge=" + challenge + "&code_challenge_method=S256" +
                    "&state=" + state;

#if NET8_0_OR_GREATER
                // .NET 8: Process.Start(url) no longer goes through the shell (UseShellExecute is false by default).
                BridgePlatform.Current.Shell.OpenUrl(url);
#else
                Process.Start(url);
#endif

                IAsyncResult ar = listener.BeginAcceptTcpClient(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMinutes(5)))
                    throw new TimeoutException("No answer from the browser within 5 minutes.");

                string code;
                using (TcpClient client = listener.EndAcceptTcpClient(ar))
                using (NetworkStream ns = client.GetStream())
                {
                    ns.ReadTimeout = 10000;
                    StreamReader reader = new StreamReader(ns, Encoding.ASCII);
                    string requestLine = reader.ReadLine() ?? "";
                    string path = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "";
                    Dictionary<string, string> q = ParseQuery(path);

                    string error;
                    q.TryGetValue("error", out error);
                    string gotState;
                    q.TryGetValue("state", out gotState);
                    q.TryGetValue("code", out code);

                    string page = error == null && gotState == state && code != null
                        ? "AI Workbench Bridge is connected to Google. You can close this tab."
                        : "Google sign-in did not complete: " + (error ?? "state mismatch");
                    byte[] html = Encoding.UTF8.GetBytes(
                        "<!doctype html><meta charset=utf-8><title>AI Workbench Bridge</title>" +
                        "<body style=\"font:16px system-ui;padding:40px\">" + WebUtilityEncode(page) + "</body>");
                    byte[] head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                        html.Length + "\r\nConnection: close\r\n\r\n");
                    ns.Write(head, 0, head.Length);
                    ns.Write(html, 0, html.Length);

                    if (error != null)
                        throw new InvalidOperationException("Google returned: " + error);
                    if (gotState != state || code == null)
                        throw new InvalidOperationException("OAuth state mismatch.");
                }

                string form =
                    "code=" + Uri.EscapeDataString(code) +
                    "&client_id=" + Uri.EscapeDataString(clientId) +
                    "&client_secret=" + Uri.EscapeDataString(clientSecret) +
                    "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                    "&grant_type=authorization_code&code_verifier=" + verifier;
                GResponse r = GHttp.Send("POST", "https://oauth2.googleapis.com/token", null,
                    "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes(form), 30000);
                Dictionary<string, object> j = GHttp.Parse(r);
                GHttp.Ensure(r, "token exchange");

                string refresh = GHttp.Str(j, "refresh_token");
                if (String.IsNullOrEmpty(refresh))
                    throw new InvalidOperationException("Google did not return a refresh token.");

                Dictionary<string, object> stored = new Dictionary<string, object>();
                stored["client_id"] = clientId;
                stored["client_secret"] = clientSecret;
                stored["refresh_token"] = refresh;
                stored["scopes"] = scopes;
                stored["createdUtc"] = DateTime.UtcNow.ToString("o");
                CredentialStore.Write(target, JsonUtil.Serialize(stored));
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string WebUtilityEncode(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static Dictionary<string, string> ParseQuery(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = path.IndexOf('?');
            if (i < 0)
                return d;
            foreach (string pair in path.Substring(i + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                d[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                    Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return d;
        }

        private static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);
            return b;
        }

        internal static string Base64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    // Windows Credential Manager (generic credential, per-user, persisted on this machine).
    internal static class CredentialStore
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public static string Read(string target)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                return BridgePlatform.Current.Secrets.Read(target);
#endif
            IntPtr p;
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out p))
                return null;
            try
            {
                CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                if (c.CredentialBlobSize <= 0)
                    return null;
                byte[] blob = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                CredFree(p);
            }
        }

        public static void Write(string target, string secret)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                BridgePlatform.Current.Secrets.Write(target, secret, "AI Workbench Bridge: Google OAuth refresh token (Drive + Pub/Sub)");
                return;
            }
#endif
            byte[] blob = Encoding.UTF8.GetBytes(secret);
            if (blob.Length > 2560)
                throw new InvalidOperationException("Credential too large for Credential Manager.");
            IntPtr mem = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, mem, blob.Length);
                CREDENTIAL c = new CREDENTIAL();
                c.Type = CRED_TYPE_GENERIC;
                c.TargetName = target;
                c.Comment = "AI Workbench Bridge: Google OAuth refresh token (Drive + Pub/Sub)";
                c.CredentialBlobSize = blob.Length;
                c.CredentialBlob = mem;
                c.Persist = CRED_PERSIST_LOCAL_MACHINE;
                c.UserName = Environment.UserName;
                if (!CredWrite(ref c, 0))
                    throw new InvalidOperationException("CredWrite failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        public static void Delete(string target)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                BridgePlatform.Current.Secrets.Delete(target);
                return;
            }
#endif
            CredDelete(target, CRED_TYPE_GENERIC, 0);
        }
    }

    // ==================================================================== Drive (minimal)

    internal sealed class DriveLite
    {
        public const string FolderMime = "application/vnd.google-apps.folder";
        private const string Api = "https://www.googleapis.com/drive/v3/files";
        private const string Fields = "id,name,mimeType,trashed,parents";
        private readonly string _token;

        public DriveLite(string token)
        {
            _token = token;
        }

        private static string Q(string s)
        {
            return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        public List<DriveItem> Query(string q)
        {
            List<DriveItem> items = new List<DriveItem>();
            string pageToken = null;
            do
            {
                string url = Api + "?q=" + Uri.EscapeDataString(q) +
                    "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + Fields + ")") +
                    "&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                    (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
                GResponse r = GHttp.Send("GET", url, _token, null, null, 30000);
                GHttp.Ensure(r, "Drive list");
                Dictionary<string, object> j = GHttp.Parse(r);
                object files;
                if (j.TryGetValue("files", out files))
                {
                    foreach (object o in GHttp.Items(files))
                        items.Add(ToItem(o as Dictionary<string, object>));
                }
                pageToken = GHttp.Str(j, "nextPageToken");
            }
            while (pageToken != null);
            return items;
        }

        private static DriveItem ToItem(Dictionary<string, object> d)
        {
            DriveItem it = new DriveItem();
            it.id = GHttp.Str(d, "id");
            it.name = GHttp.Str(d, "name");
            it.mimeType = GHttp.Str(d, "mimeType");
            it.trashed = String.Equals(GHttp.Str(d, "trashed"), "True", StringComparison.OrdinalIgnoreCase);
            object parents;
            if (d != null && d.TryGetValue("parents", out parents))
                foreach (object p in GHttp.Items(parents))
                    it.parents.Add(Convert.ToString(p));
            return it;
        }

        public List<DriveItem> ListChildren(string parentId)
        {
            return Query(Q(parentId) + " in parents and trashed=false");
        }

        public string FindChildId(string parentId, string name, bool folder)
        {
            string q = Q(parentId) + " in parents and name=" + Q(name) + " and trashed=false";
            q += folder ? " and mimeType=" + Q(FolderMime) : " and mimeType!=" + Q(FolderMime);
            List<DriveItem> found = Query(q);
            return found.Count == 0 ? null : found[0].id;
        }

        public string FindWorkbenchRoot(string name)
        {
            foreach (DriveItem f in Query("name=" + Q(name) + " and mimeType=" + Q(FolderMime) + " and trashed=false"))
            {
                if (FindChildId(f.id, "transport", true) != null)
                    return f.id;
            }
            return null;
        }

        public string AccountEmail()
        {
            try
            {
                GResponse r = GHttp.Send("GET", "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)",
                    _token, null, null, 30000);
                if (r.status != 200)
                    return "account? (HTTP " + r.status + ")";
                Dictionary<string, object> u = GHttp.Parse(r)["user"] as Dictionary<string, object>;
                return GHttp.Str(u, "emailAddress") ?? "account?";
            }
            catch (Exception ex)
            {
                return "account? (" + ex.Message + ")";
            }
        }

        public DriveItem GetMeta(string id)
        {
            GResponse r = GHttp.Send("GET", Api + "/" + Uri.EscapeDataString(id) +
                "?fields=" + Uri.EscapeDataString(Fields) + "&supportsAllDrives=true", _token, null, null, 30000);
            if (r.status == 404)
                return null;
            GHttp.Ensure(r, "Drive get");
            return ToItem(GHttp.Parse(r));
        }

        public byte[] Download(string id)
        {
            GResponse r = GHttp.Send("GET", Api + "/" + Uri.EscapeDataString(id) +
                "?alt=media&supportsAllDrives=true", _token, null, null, 120000);
            GHttp.Ensure(r, "Drive download");
            return r.body;
        }

        public void DownloadToFile(string id, string path)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Api + "/" + Uri.EscapeDataString(id) +
                "?alt=media&supportsAllDrives=true");
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + _token;
            req.Timeout = 600000;
            req.ReadWriteTimeout = 600000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                s.CopyTo(fs);
        }

        public void Trash(string id)
        {
            GResponse r = GHttp.Json("PATCH", Api + "/" + Uri.EscapeDataString(id) + "?supportsAllDrives=true",
                _token, new Dictionary<string, object> { { "trashed", true } }, 30000);
            GHttp.Ensure(r, "Drive trash");
        }
    }

    // ==================================================================== Pub/Sub (minimal REST)

    internal sealed class PubSubLite
    {
        private const string Api = "https://pubsub.googleapis.com/v1/";
        private readonly string _project;
        private readonly string _token;

        public PubSubLite(string project, string token)
        {
            _project = project;
            _token = token;
        }

        private string P { get { return "projects/" + _project; } }

        public void EnsureTopic(string topic)
        {
            GResponse r = GHttp.Json("PUT", Api + P + "/topics/" + topic, _token, new Dictionary<string, object>(), 30000);
            if (r.status == 409)
                return;
            GHttp.Ensure(r, "create topic " + topic);
        }

        public void EnsureSubscription(string sub, string topic, int ackDeadline, string retention, string ttl)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["topic"] = P + "/topics/" + topic;
            body["ackDeadlineSeconds"] = ackDeadline;
            body["messageRetentionDuration"] = retention;
            // No TTL: the Bridge subscription must never expire while Bridge is offline.
            body["expirationPolicy"] = ttl == null
                ? (object)new Dictionary<string, object>()
                : new Dictionary<string, object> { { "ttl", ttl } };
            GResponse r = GHttp.Json("PUT", Api + P + "/subscriptions/" + sub, _token, body, 30000);
            if (r.status == 409)
                return;
            GHttp.Ensure(r, "create subscription " + sub);
        }

        public List<PubSubInMessage> Pull(string sub, int max, int timeoutMs)
        {
            GResponse r = GHttp.Json("POST", Api + P + "/subscriptions/" + sub + ":pull", _token,
                new Dictionary<string, object> { { "maxMessages", max } }, timeoutMs);
            GHttp.Ensure(r, "pull " + sub);
            List<PubSubInMessage> list = new List<PubSubInMessage>();
            Dictionary<string, object> j = GHttp.Parse(r);
            object rec;
            if (!j.TryGetValue("receivedMessages", out rec))
                return list;
            foreach (object o in GHttp.Items(rec))
            {
                Dictionary<string, object> rm = o as Dictionary<string, object>;
                if (rm == null)
                    continue;
                PubSubInMessage m = new PubSubInMessage();
                m.ackId = GHttp.Str(rm, "ackId");
                object msg;
                if (rm.TryGetValue("message", out msg))
                {
                    Dictionary<string, object> md = msg as Dictionary<string, object>;
                    string data = GHttp.Str(md, "data");
                    m.data = String.IsNullOrEmpty(data) ? new byte[0] : Convert.FromBase64String(data);
                    object attrs;
                    if (md != null && md.TryGetValue("attributes", out attrs) && attrs is Dictionary<string, object>)
                        foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)attrs)
                            m.attributes[kv.Key] = Convert.ToString(kv.Value);
                }
                list.Add(m);
            }
            return list;
        }

        public void Acknowledge(string sub, List<string> ackIds)
        {
            if (ackIds.Count == 0)
                return;
            GResponse r = GHttp.Json("POST", Api + P + "/subscriptions/" + sub + ":acknowledge", _token,
                new Dictionary<string, object> { { "ackIds", ackIds } }, 30000);
            GHttp.Ensure(r, "ack " + sub);
        }

        public void Publish(string topic, List<PubSubOutMessage> batch, string hostId)
        {
            List<object> msgs = new List<object>();
            foreach (PubSubOutMessage m in batch)
            {
                msgs.Add(new Dictionary<string, object>
                {
                    { "data", Convert.ToBase64String(m.data) },
                    { "attributes", new Dictionary<string, object>
                        {
                            { "type", m.type }, { "jobId", m.jobId }, { "hostId", hostId },
                            { "sentMs", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString() }
                        }
                    }
                });
            }
            GResponse r = GHttp.Json("POST", Api + P + "/topics/" + topic + ":publish", _token,
                new Dictionary<string, object> { { "messages", msgs } }, 30000);
            GHttp.Ensure(r, "publish " + topic);
        }
    }
}
