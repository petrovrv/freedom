// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AiWorkbenchBridge
{
    // v1.2.7: Web Push through <site>/agent/push.php. The site encrypts and delivers to the devices
    // subscribed in CO Web (Host tab, Notifications); Bridge only posts {env, title, body, url, tag} with the
    // shared key from Credential Manager (written by Python keyring, service aiwb/co-push).
    internal sealed class PushConfig
    {
        public string url { get; set; }

        // Windows Credential Manager target holding the X-CO-Push-Key secret.
        public string credentialTarget { get; set; }

        // Jobs shorter than this send nothing, unless the chat is a schedule chat (title starting with U+23F0).
        public int minJobSeconds { get; set; }

        public bool enabled { get; set; }

        // Environment that receives Bridge-level notifications (Google sign-in needed).
        public string ownerEnv { get; set; }

        public PushConfig()
        {
            url = null; // Freedom F1: <siteUrl>/agent/push.php (NormalizeSite)
            credentialTarget = "aiwb/co-push";
            minJobSeconds = 45;
            enabled = false;
            ownerEnv = null; // NormalizeSite: the first environment of chatProjectEnvironments
        }
    }

    internal sealed partial class BridgeApplicationContext
    {
        private const string PushDefaultTarget = "aiwb/co-push";

        private static bool _pushNoKeyLogged;
        private static bool _pushTlsSet;
        private static readonly object _pushTlsLock = new object();
        private static readonly object _pushAuthLock = new object();
        private static DateTime _pushLastAuthNotifyUtc = DateTime.MinValue;

        // Fire-and-forget: returns immediately, the POST runs on the thread pool.
        private void Notify(string env, string title, string body, string url, string tag)
        {
            PushConfig push = _config != null ? _config.push : null;
            if (push == null || !push.enabled || String.IsNullOrEmpty(env))
                return;

            string endpoint = push.url;
            if (String.IsNullOrEmpty(endpoint))
                return;
            string target = push.credentialTarget ?? PushDefaultTarget;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                PushSend(endpoint, target, env, title, body, url, tag);
            });
        }

        private void PushSend(string endpoint, string target, string env, string title, string body, string url, string tag)
        {
            try
            {
                PushEnsureTls12();

                string key = CredentialStore.Read(target);
                // Python keyring stores the secret as UTF-16LE; Read decodes UTF-8, so an ASCII key has NULs between chars.
                key = key == null ? "" : key.Replace("\0", "").Trim();
                if (key.Length == 0)
                {
                    if (!_pushNoKeyLogged)
                    {
                        _pushNoKeyLogged = true;
                        Log("Push: no key in Credential Manager (" + target + ")");
                    }
                    return;
                }

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["op"] = "notify";
                payload["env"] = env;
                payload["title"] = title ?? "";
                payload["body"] = body ?? "";
                payload["url"] = url ?? "/";
                payload["tag"] = tag ?? "";
                byte[] bytes = new UTF8Encoding(false).GetBytes(JsonUtil.Serialize(payload));

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Headers["X-CO-Push-Key"] = key;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.ContentLength = bytes.Length;

                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(bytes, 0, bytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string text = PushReadBody(resp);
                    Log("Push: " + (int)resp.StatusCode + " " + PushCut(text, 200) + " (" + tag + ")");
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    string errText = "";
                    try
                    {
                        errText = PushReadBody(errResp);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        errResp.Close();
                    }
                    Log("Push: " + wex.Status + " HTTP " + (int)errResp.StatusCode + " " + PushCut(errText, 200) + " (" + tag + ")");
                }
                else
                {
                    Log("Push: " + wex.Status + " " + wex.Message + " (" + tag + ")");
                }
            }
            catch (Exception ex)
            {
                Log("Push: " + ex.Message + " (" + tag + ")");
            }
        }

        // 3072 = Tls12; the enum member is missing from the .NET 4.0 reference assemblies.
        private static void PushEnsureTls12()
        {
            lock (_pushTlsLock)
            {
                if (_pushTlsSet)
                    return;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                }
                catch (NotSupportedException)
                {
                }
                _pushTlsSet = true;
            }
        }

        private static string PushReadBody(HttpWebResponse resp)
        {
            Stream s = resp.GetResponseStream();
            if (s == null)
                return "";
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string PushCut(string text, int max)
        {
            if (String.IsNullOrEmpty(text))
                return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // Push for a finished job: skips cancelled jobs and short jobs outside schedule chats.
        internal void NotifyJobFinished(JobRequest request, string finalStatus, AgentRunResult runResult, string failureText)
        {
            try
            {
                if (request == null)
                    return;
                PushConfig push = _config != null ? _config.push : null;
                if (push == null || !push.enabled)
                    return;
                if (String.Equals(finalStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
                    return;

                string env = request.environmentId;
                if (String.IsNullOrEmpty(env))
                    return;

                string projectId = request.projectId ?? "";
                string chatId = request.chatId ?? "";
                string chatTitle = PushReadChatTitle(projectId, chatId);
                bool schedule = chatTitle.StartsWith("⏰", StringComparison.Ordinal);

                int minSeconds = push.minJobSeconds > 0 ? push.minJobSeconds : 45;
                if (runResult != null && !String.IsNullOrEmpty(runResult.startedUtc))
                {
                    DateTime started;
                    if (DateTime.TryParse(runResult.startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started))
                    {
                        if (started.Kind == DateTimeKind.Unspecified)
                            started = DateTime.SpecifyKind(started, DateTimeKind.Utc);
                        double seconds = (DateTime.UtcNow - started.ToUniversalTime()).TotalSeconds;
                        if (seconds < minSeconds && !schedule)
                            return;
                    }
                }

                bool completed = String.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase);
                string title = (completed ? "✓ " : "✗ ") + chatTitle;
                string body = PushPlain(completed ? (runResult != null ? runResult.finalResponse : null) : failureText);
                if (String.IsNullOrEmpty(body))
                    body = completed ? "Done" : "Failed";

                string url = "/e/" + Uri.EscapeDataString(env)
                           + "/p/" + Uri.EscapeDataString(projectId)
                           + "/c/" + Uri.EscapeDataString(chatId);

                Notify(env, title, body, url, chatId);
            }
            catch (Exception ex)
            {
                Log("Push: " + ex.Message);
            }
        }

        // Chat title from <driveRoot>\projects\<projectId>\chats\<chatId>\chat.json; fallback chatId.
        private string PushReadChatTitle(string projectId, string chatId)
        {
            try
            {
                if (_config != null && !String.IsNullOrEmpty(_config.driveRoot) && projectId.Length > 0 && chatId.Length > 0)
                {
                    string path = Path.Combine(_config.driveRoot, "projects", projectId, "chats", chatId, "chat.json");
                    if (File.Exists(path))
                    {
                        Dictionary<string, object> chat = JsonUtil.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                        object value;
                        if (chat != null && chat.TryGetValue("title", out value) && value != null)
                        {
                            string t = Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
                            if (t.Length > 0)
                                return t;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Push: chat title: " + ex.Message);
            }
            return chatId;
        }

        // Rough markdown -> plain text, single line, max 180 chars.
        private static string PushPlain(string text)
        {
            if (String.IsNullOrEmpty(text))
                return "";

            string s = Regex.Replace(text, "```[\\s\\S]*?(```|$)", " ");
            s = Regex.Replace(s, "!?\\[([^\\]]*)\\]\\([^)]*\\)", "$1");
            s = Regex.Replace(s, "^[ \\t]*[#>]+[ \\t]*", "", RegexOptions.Multiline);
            s = s.Replace("*", "").Replace("`", "");
            s = Regex.Replace(s, "(?<!\\w)_+|_+(?!\\w)", "");
            s = s.Replace("#", "").Replace(">", "");
            s = Regex.Replace(s, "\\s+", " ").Trim();

            if (s.Length > 180)
            {
                int cut = 179;
                if (Char.IsHighSurrogate(s[cut - 1]))
                    cut--;
                s = s.Substring(0, cut).TrimEnd() + "…";
            }
            return s;
        }

        // v1.2.8: heartbeat to push.php every 5 min; the site alerts "Bridge offline" after 15 min without one.
        private System.Threading.Timer _pushHeartbeatTimer;
        private static int _pushHeartbeatBusy;                 // 0 = idle, 1 = running (Interlocked)
        private static bool _pushHeartbeatFailing;             // accessed only while the busy flag is held
        private static DateTime _pushHeartbeatLastFailLogUtc = DateTime.MinValue;

        private void StartPushHeartbeat()
        {
            if (_pushHeartbeatTimer != null)
                return;
            _pushHeartbeatTimer = new System.Threading.Timer(
                delegate(object state) { PushHeartbeat(); },
                null,
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMinutes(5));
        }

        private void PushHeartbeat()
        {
            // Skip if a previous call is still running.
            if (Interlocked.CompareExchange(ref _pushHeartbeatBusy, 1, 0) != 0)
                return;
            try
            {
                PushConfig push = _config == null ? null : _config.push;
                if (push == null || !push.enabled)
                    return;
                string endpoint = push.url;
                if (String.IsNullOrEmpty(endpoint))
                    return;

                string key = CredentialStore.Read(push.credentialTarget ?? PushDefaultTarget);
                key = key == null ? "" : key.Replace("\0", "").Trim();
                if (key.Length == 0)
                    throw new InvalidOperationException("push key missing in Credential Manager");

                PushEnsureTls12();

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["op"] = "heartbeat";
                payload["host"] = _config.hostId ?? "";
                payload["version"] = BridgeVersion;
                byte[] body = new UTF8Encoding(false).GetBytes(JsonUtil.Serialize(payload));

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(endpoint);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Headers["X-CO-Push-Key"] = key;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.KeepAlive = false;
                req.ContentLength = body.Length;

                using (Stream s = req.GetRequestStream())
                {
                    s.Write(body, 0, body.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    PushReadBody(resp);
                }

                SupHeartbeatOk(); // v1.9.0
                if (_pushHeartbeatFailing)
                {
                    _pushHeartbeatFailing = false;
                    Log("Push heartbeat: OK again");
                }
            }
            catch (WebException ex)
            {
                string msg = ex.Message;
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    try
                    {
                        msg = "HTTP " + (int)resp.StatusCode + " " + PushCut(PushReadBody(resp), 200);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        resp.Close();
                    }
                }
                PushHeartbeatFail(msg);
            }
            catch (Exception ex)
            {
                PushHeartbeatFail(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _pushHeartbeatBusy, 0);
            }
        }

        // Logs failures at most once per hour.
        private void PushHeartbeatFail(string message)
        {
            try
            {
                _pushHeartbeatFailing = true;
                DateTime now = DateTime.UtcNow;
                if ((now - _pushHeartbeatLastFailLogUtc) >= TimeSpan.FromHours(1))
                {
                    _pushHeartbeatLastFailLogUtc = now;
                    Log("Push heartbeat: " + PushCut(message ?? "", 300));
                }
            }
            catch (Exception)
            {
            }
        }

        // GoogleAuth.AuthFailed handler: one push to ownerEnv per 6 hours at most.
        internal void NotifyGoogleAuthFailed(string reason)
        {
            try
            {
                PushConfig push = _config != null ? _config.push : null;
                if (push == null || !push.enabled)
                    return;

                lock (_pushAuthLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if (_pushLastAuthNotifyUtc != DateTime.MinValue && (now - _pushLastAuthNotifyUtc).TotalHours < 6)
                        return;
                    _pushLastAuthNotifyUtc = now;
                }

                string ownerEnv = OwnerEnvOf(_config);
                string r = String.IsNullOrEmpty(reason) ? "Google sign-in failed" : reason.Trim().TrimEnd('.');
                Notify(ownerEnv, "Bridge: Google sign-in needed", r + ". Tray → Connect Google on the home PC.", "/", "bridge-auth");
            }
            catch (Exception ex)
            {
                Log("Push: " + ex.Message);
            }
        }
    }

    // v1.9.0: config.json "supervisor" section. Absent or 0 values -> defaults; restartMode "dry" only logs.
    internal sealed class SupervisorConfig
    {
        public bool? enabled { get; set; }
        public string restartMode { get; set; }          // "dry" (default: log and push "would restart") | "on"
        public int claimFailures { get; set; }           // 5
        public int claimWindowMinutes { get; set; }      // 10
        public int stuckJobMinutes { get; set; }         // 30
        public int ownershipFailMinutes { get; set; }    // 10
        public int drivePushMinutes { get; set; }        // 20
        public int softResetWaitMinutes { get; set; }    // 5
        public int drainTimeoutMinutes { get; set; }     // 30
        public int updateDrainMinutes { get; set; }      // 30; v1.16.0: update drain -> running jobs paused; < 0 waits as before
        public int startupGraceMinutes { get; set; }     // 5
        public int maxRestartsPerHour { get; set; }      // 2
        public int maxRestartsPerDay { get; set; }       // 4
        public int launcherCheckSeconds { get; set; }    // 120
        public int maxLauncherStartsPerDay { get; set; } // 3
    }

    // v1.9.0: supervisor.json in the state folder: restarts and launcher starts survive a Bridge restart.
    internal sealed class SupervisorState
    {
        public List<string> restartsUtc { get; set; }
        public List<string> launcherStartsUtc { get; set; }
    }

    // v1.9.0: Bridge supervision (design: chat CHAT-20261103142900-BZML, scratch/design.md).
    //  1. Launcher watchdog: the launcher (AIWorkbenchBridge.exe) missing on 2 checks in a row -> the logon task
    //     AIWB-BridgeAutostart is run (Bridge never starts the exe itself), at most maxLauncherStartsPerDay; once the
    //     new launcher has run 2 min and no job is running, Bridge drains (exit 3) so that the launcher owns it again.
    //  2. "Alive but not working": repeated claim failures (not a network or server outage), owner.json not written
    //     while G: is there. Ladder: soft reset -> drain and exit 3 through the launcher -> push to the owner. Never
    //     restarts for no network, a server error, a missing G:, while paused or updating, or without a launcher.
    //     Limits maxRestartsPerHour / maxRestartsPerDay are kept on disk.
    //  3. A running job without any process for stuckJobMinutes (not waiting for its chat's previous message) is
    //     reported (log, host event, push); the job timeout still ends it.
    // Events go to the database (db.php op host_event -> host_events) and the log.
    internal sealed partial class BridgeApplicationContext
    {
        private const string SupDrainMarker = "supervisor";
        private const string SupAutostartTask = "AIWB-BridgeAutostart";

        private Thread _supThread;
        private readonly DateTime _supStartUtc = DateTime.UtcNow;
        private readonly object _supLock = new object();
        private readonly List<DateTime> _supClaimFails = new List<DateTime>();
        private int _supClaimLastClass;                  // 0 other, 1 network, 2 server (HTTP 5xx)
        private string _supClaimLastError = "";
        private DateTime _supClaimOkUtc = DateTime.UtcNow;
        private bool _supClaimFailing;                   // threshold reached; only a successful claim clears it
        private DateTime _supOwnerFailSinceUtc = DateTime.MinValue;
        private string _supOwnerLastError = "";
        private DateTime _supHeartbeatOkUtc = DateTime.MinValue;
        private DateTime _supLastLauncherCheckUtc = DateTime.MinValue;
        private string _supLauncherExe;                  // null -> not the installed layout, no watchdog
        private int _supLauncherMissing;
        private bool _supHandover;
        private DateTime _supLauncherAliveSinceUtc = DateTime.MinValue;
        private string _supProblem;
        private string _supProblemDetail = "";
        private int _supLevel;                           // 0 none, 1 soft reset done, 2 drain asked, 3 owner told
        private DateTime _supLevelUtc = DateTime.MinValue;
        private string _supOutage;                       // net_down | server_down episode being logged
        private DateTime _supDrainAskedUtc = DateTime.MinValue;
        private DateTime _supDriveMissingSinceUtc = DateTime.MinValue;
        private bool _supDrivePushed;
        private readonly Dictionary<string, DateTime> _supNoProcessSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> _supStuckReported = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _supPushedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _supNoProcessOk = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Dictionary<string, object>> _supEvents = new ConcurrentQueue<Dictionary<string, object>>();

        private SupervisorConfig Sup
        {
            get { return _config != null && _config.supervisor != null ? _config.supervisor : new SupervisorConfig(); }
        }

        private static int SupDef(int v, int d) { return v > 0 ? v : d; }

        private string SupMode
        {
            get
            {
                string m = (Sup.restartMode ?? "").Trim().ToLowerInvariant();
                return m == "on" ? "on" : "dry";
            }
        }

        private void StartSupervisor()
        {
            if (Sup.enabled == false || _supThread != null)
                return;
            _supLauncherExe = SupFindLauncherExe();
            Log("Supervisor: on, restartMode=" + SupMode + (_supLauncherExe == null ? ", launcher watchdog off (not the installed layout)" : ""));
            _supThread = new Thread(SupLoop);
            _supThread.IsBackground = true;
            _supThread.Name = "supervisor";
            _supThread.Start();
        }

        // Installed layout: <root>\versions\<v>\BridgeAgent.exe next to <root>\AIWorkbenchBridge.exe (a test instance
        // under a test launcher too; it never runs the logon task, SupRunAutostartTask).
        private static string SupFindLauncherExe()
        {
            try
            {
                DirectoryInfo verDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
                if (verDir.Parent == null || !String.Equals(verDir.Parent.Name, "versions", StringComparison.OrdinalIgnoreCase) || verDir.Parent.Parent == null)
                    return null;
                string exe = Path.Combine(verDir.Parent.Parent.FullName, "AIWorkbenchBridge.exe");
                return File.Exists(exe) ? exe : null;
            }
            catch
            {
                return null;
            }
        }

        private void SupLoop()
        {
            while (!_exiting)
            {
                try { Thread.Sleep(60000); } catch { }
                if (_exiting)
                    break;
                try
                {
                    SupTick(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    LogThrottled("supervisor", "Supervisor: " + ex.Message);
                }
            }
        }

        private void SupTick(DateTime now)
        {
            if (_supLauncherExe != null && (now - _supLastLauncherCheckUtc).TotalSeconds >= SupDef(Sup.launcherCheckSeconds, 120) - 5)
            {
                _supLastLauncherCheckUtc = now;
                SupCheckLauncher(now);
            }
            SupCheckStuckJobs(now);
            SupCheckLadder(now);
            SupCheckOwnDrain(now);
            SupCheckDrive(now); // Directory.Exists on a hung G: may block this thread
            SupFlushEvents();   // last: HTTP to the site, up to 15 s per event while it is slow
        }

        // ---------------------------------------------------------------- signals from the rest of Bridge

        private void SupClaimOk()
        {
            lock (_supLock)
            {
                _supClaimFails.Clear();
                _supClaimOkUtc = DateTime.UtcNow;
                _supClaimFailing = false;
            }
        }

        private void SupClaimFailed(Exception ex)
        {
            lock (_supLock)
            {
                _supClaimFails.Add(DateTime.UtcNow);
                if (_supClaimFails.Count > 100)
                    _supClaimFails.RemoveAt(0);
                _supClaimLastClass = SupErrorClass(ex);
                _supClaimLastError = ex == null ? "" : ex.Message;
            }
        }

        private void SupOwnerOk()
        {
            _supOwnerFailSinceUtc = DateTime.MinValue;
        }

        private void SupOwnerFailed(Exception ex)
        {
            if (_supOwnerFailSinceUtc == DateTime.MinValue)
                _supOwnerFailSinceUtc = DateTime.UtcNow;
            _supOwnerLastError = ex == null ? "" : ex.Message;
        }

        private void SupHeartbeatOk()
        {
            _supHeartbeatOkUtc = DateTime.UtcNow;
        }

        // 1 = network (DNS, connect, timeout), 2 = server (HTTP 5xx), 0 = anything else (key, answer, bug).
        internal static int SupErrorClass(Exception ex)
        {
            WebException wex = ex as WebException;
            if (wex != null)
            {
                switch (wex.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.Timeout:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.SendFailure:
                    case WebExceptionStatus.KeepAliveFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                        return 1;
                }
            }
            string m = ex == null ? "" : (ex.Message ?? "");
            if (Regex.IsMatch(m, @"\AHTTP 5\d\d\b"))
                return 2;
            if (ex is IOException || m.IndexOf("could not be resolved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("Unable to connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            return 0;
        }

        // CheckDrainRequest asks before exiting with code 3: without a launcher nobody would start Bridge again.
        private bool SupBlockDrainExit()
        {
            if (_supLauncherExe == null || SupLauncherAlive())
                return false;
            LogThrottled("sup-drain-block", "Supervisor: drain finished but the launcher is not running; waiting for it before exiting.");
            return true;
        }

        // A job waiting for the previous message of its chat, or a gateway job, has no process by design.
        private void SupNoProcessOk(string jobId, bool on)
        {
            if (String.IsNullOrEmpty(jobId))
                return;
            byte ignored;
            if (on)
                _supNoProcessOk[jobId] = 0;
            else
                _supNoProcessOk.TryRemove(jobId, out ignored);
        }

        // ---------------------------------------------------------------- 1. launcher watchdog

        private bool SupLauncherAlive()
        {
            if (_supLauncherExe == null)
                return true;
            Process[] list;
            try { list = Process.GetProcessesByName("AIWorkbenchBridge"); }
            catch { return true; }
            bool alive = false;
            foreach (Process p in list)
            {
                try
                {
                    if (!alive)
                    {
                        string path = null;
                        try { path = p.MainModule.FileName; }
                        catch { alive = true; } // path unreadable: assume it is ours, never restart on doubt
                        if (path != null && String.Equals(Path.GetFullPath(path), Path.GetFullPath(_supLauncherExe), StringComparison.OrdinalIgnoreCase))
                            alive = true;
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
            return alive;
        }

        private void SupCheckLauncher(DateTime now)
        {
            if (SupLauncherAlive())
            {
                if (_supLauncherMissing > 0)
                    Log("Supervisor: launcher is running again.");
                _supLauncherMissing = 0;
                if (_supLauncherAliveSinceUtc == DateTime.MinValue)
                    _supLauncherAliveSinceUtc = now;
                // The launcher started by the watchdog waits for this orphan Bridge (its own start exits with code 4).
                // Hand over when idle: drain (no job runs) -> exit 3 -> the launcher starts Bridge as its child.
                if (_supHandover && (now - _supLauncherAliveSinceUtc).TotalMinutes >= 2 && !_draining && !_paused &&
                    _runningJobs.Count == 0)
                {
                    _supHandover = false;
                    SupEvent("handover", "launcher is back; Bridge restarts under it");
                    SupRequestDrain("handover");
                }
                return;
            }

            _supLauncherAliveSinceUtc = DateTime.MinValue;
            _supLauncherMissing++;
            if (_supLauncherMissing < 2)
            {
                Log("Supervisor: launcher not running (check 1 of 2).");
                return;
            }

            SupervisorState st = SupLoadState();
            int today = SupCount(st.launcherStartsUtc, now.AddDays(-1));
            if (today >= SupDef(Sup.maxLauncherStartsPerDay, 3))
            {
                if (SupPushOnce("launcher_limit", 6))
                {
                    SupEvent("launcher_limit", today + " launcher starts in 24 h; not starting it again");
                    SupPush("Bridge: launcher keeps stopping",
                        "The Bridge launcher stopped again after " + today + " restarts in 24 h. Bridge runs without it: no updates, no restart after a crash.");
                }
                return;
            }

            string outcome;
            bool started = SupRunAutostartTask(out outcome);
            _supLauncherMissing = 0;
            if (!started)
            {
                // Not counted against the daily limit: the next check (2 more misses) tries again.
                if (SupPushOnce("launcher_start_failed", 6))
                {
                    SupEvent("launcher_start_failed", outcome);
                    SupPush("Bridge: cannot start the launcher", "The Bridge launcher is not running and " + outcome + ".");
                }
                else
                    Log("Supervisor: launcher start failed: " + outcome);
                return;
            }
            st.launcherStartsUtc = SupAppend(st.launcherStartsUtc, now);
            SupSaveState(st);
            _supHandover = true;
            SupEvent("launcher_restarted", "launcher was not running; " + outcome);
        }

        private static bool SupRunAutostartTask(out string outcome)
        {
            outcome = SupRunAutostartTaskText();
            return outcome.StartsWith("task ", StringComparison.Ordinal) || outcome.StartsWith("test instance", StringComparison.Ordinal);
        }

        private static string SupRunAutostartTaskText()
        {
            if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("AIWB_BRIDGE_INSTANCE")))
                return "test instance: logon task not run";
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                try { return BridgePlatform.Current.Autostart.Kick(); }
                catch (Exception ex) { return "autostart failed: " + ex.Message; }
            }
#endif
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    "/Run /TN " + SupAutostartTask);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000))
                        return "schtasks did not finish in 30 s";
                    return p.ExitCode == 0 ? "task " + SupAutostartTask + " started" : "schtasks exit " + p.ExitCode + " " + PushCut(err.Trim(), 150);
                }
            }
            catch (Exception ex)
            {
                return "schtasks failed: " + ex.Message;
            }
        }

        // ---------------------------------------------------------------- 2. alive but not working

        // Returns the problem that the ladder handles, or null. Outages that a restart cannot fix are logged once.
        private string SupDetect(DateTime now, out string detail)
        {
            detail = "";
            SupervisorConfig c = Sup;
            TimeSpan window = TimeSpan.FromMinutes(SupDef(c.claimWindowMinutes, 10));
            string outage = null;
            string problem = null;

            if (DbqEnabled)
            {
                int n;
                int cls;
                string err;
                bool failing;
                lock (_supLock)
                {
                    _supClaimFails.RemoveAll(delegate(DateTime t) { return now - t > window; });
                    n = _supClaimFails.Count;
                    cls = _supClaimLastClass;
                    err = _supClaimLastError;
                    // Retries back off to 60 s, so the count in the window may drop while nothing works yet.
                    if (n >= SupDef(c.claimFailures, 5) && now - _supClaimOkUtc >= window)
                        _supClaimFailing = true;
                    failing = _supClaimFailing;
                }
                if (failing)
                {
                    bool heartbeatOk = now - _supHeartbeatOkUtc < window;
                    if (cls == 2)
                        outage = "server_down";
                    else if (cls == 1 && !heartbeatOk)
                        outage = "net_down";
                    else
                    {
                        problem = "claim_failing";
                        detail = n + " claim failures in " + (int)window.TotalMinutes + " min: " + PushCut(err, 200);
                    }
                    if (outage != null)
                        detail = PushCut(err, 200);
                }
            }

            DateTime ownerSince = _supOwnerFailSinceUtc;
            if (problem == null && ownerSince != DateTime.MinValue &&
                (now - ownerSince).TotalMinutes >= SupDef(c.ownershipFailMinutes, 10) &&
                _supDriveMissingSinceUtc == DateTime.MinValue)
            {
                problem = "owner_write_failed";
                detail = "owner.json not written for " + (int)(now - ownerSince).TotalMinutes + " min: " + PushCut(_supOwnerLastError, 200);
            }

            if (outage != _supOutage)
            {
                if (outage != null)
                    SupEvent(outage, (outage == "net_down" ? "no network: " : "server error: ") + detail + "; no restart");
                else if (_supOutage != null)
                    SupEvent("recovered", _supOutage + " is over");
                _supOutage = outage;
            }
            return problem;
        }

        private void SupCheckLadder(DateTime now)
        {
            string detail;
            string problem = SupDetect(now, out detail);
            SupervisorConfig c = Sup;

            if (problem == null)
            {
                if (_supProblem != null)
                {
                    SupEvent("recovered", _supProblem + " is over");
                    _supProblem = null;
                }
                _supLevel = 0;
                return;
            }
            if ((now - _supStartUtc).TotalMinutes < SupDef(c.startupGraceMinutes, 5))
                return;
            if (problem != _supProblem)
            {
                _supProblem = problem;
                _supLevel = 0;
            }
            _supProblemDetail = detail;

            if (_supLevel == 0)
            {
                SupSoftReset();
                _supLevel = 1;
                _supLevelUtc = now;
                SupEvent("soft_reset", problem + ": " + detail);
                return;
            }
            if (_supLevel == 1 && (now - _supLevelUtc).TotalMinutes >= SupDef(c.softResetWaitMinutes, 5))
            {
                _supLevelUtc = now;
                string blocked = SupRestartBlocked(now);
                if (SupMode != "on")
                {
                    _supLevel = 3;
                    string note = blocked == null ? "" : "; would be blocked: " + blocked;
                    SupEvent("would_restart", problem + ": " + detail + " (restartMode=" + SupMode + note + ")");
                    SupTellOwner(problem, detail, "Dry run: Bridge would restart now (supervisor.restartMode=" + SupMode + note + ").");
                    return;
                }
                if (blocked != null)
                {
                    _supLevel = 3;
                    SupEvent("restart_limit", problem + ": no restart (" + blocked + ")");
                    SupTellOwner(problem, detail, "Bridge was not restarted: " + blocked + ".");
                    return;
                }
                SupervisorState st = SupLoadState();
                st.restartsUtc = SupAppend(st.restartsUtc, now);
                SupSaveState(st);
                _supLevel = 2;
                SupEvent("restart", problem + ": " + detail);
                SupRequestDrain("restart " + problem);
                return;
            }
            // Level 2: Bridge is draining (SupCheckOwnDrain watches the time). Level 3: repeat the push hourly.
            if (_supLevel == 3)
                SupTellOwner(problem, detail, "Still not working.");
        }

        private string SupRestartBlocked(DateTime now)
        {
            if (_draining)
                return "an update drain is running";
            if (_paused)
                return "Bridge is paused";
            if (_supLauncherExe == null)
                return "no launcher (not the installed layout)";
            if (!SupLauncherAlive())
                return "the launcher is not running";
            SupervisorState st = SupLoadState();
            int hour = SupCount(st.restartsUtc, now.AddHours(-1));
            int day = SupCount(st.restartsUtc, now.AddDays(-1));
            if (hour >= SupDef(Sup.maxRestartsPerHour, 2))
                return hour + " restarts in the last hour";
            if (day >= SupDef(Sup.maxRestartsPerDay, 4))
                return day + " restarts in 24 h";
            return null;
        }

        private void SupTellOwner(string problem, string detail, string what)
        {
            if (!SupPushOnce("problem-" + problem, 1))
                return;
            SupEvent("supervisor_push", problem);
            SupPush("Bridge is alive but not working",
                (problem == "claim_failing" ? "Jobs cannot be taken from the database. " : "owner.json on Google Drive is not written. ") +
                what + " " + PushCut(detail, 200));
        }

        // Step 1: drop pooled connections to the site, force the owner.json write and a scan, wake the claim loop.
        private void SupSoftReset()
        {
            List<string> urls = new List<string>();
            if (_config.dbQueue != null && !String.IsNullOrEmpty(_config.dbQueue.url))
                urls.Add(_config.dbQueue.url);
            if (_config.push != null && !String.IsNullOrEmpty(_config.push.url))
                urls.Add(_config.push.url);
            foreach (string u in urls)
            {
                try { ServicePointManager.FindServicePoint(new Uri(u)).CloseConnectionGroup(""); }
                catch { }
            }
            _lastHostHeartbeatUtc = DateTime.MinValue;
            if (DbqEnabled)
                DbqWake();
            QueueScan();
        }

        // drain.request "<version> <utc> supervisor": CheckDrainRequest pauses intake and exits with code 3 when no job
        // runs; the launcher deletes the file and starts the same version again (no pending update).
        private void SupRequestDrain(string why)
        {
            try
            {
                File.WriteAllText(Path.Combine(_localStateRoot, "drain.request"),
                    BridgeVersion + " " + DateTime.UtcNow.ToString("o") + " " + SupDrainMarker + " " + why, Encoding.UTF8);
                _supDrainAskedUtc = DateTime.UtcNow;
                Log("Supervisor: drain requested (" + why + ").");
            }
            catch (Exception ex)
            {
                Log("Supervisor: cannot write drain.request: " + ex.Message);
            }
        }

        // A supervisor drain that waits longer than drainTimeoutMinutes (a long job) is withdrawn: intake resumes.
        private void SupCheckOwnDrain(DateTime now)
        {
            if (_supDrainAskedUtc == DateTime.MinValue)
                return;
            if ((now - _supDrainAskedUtc).TotalMinutes < SupDef(Sup.drainTimeoutMinutes, 30))
                return;
            _supDrainAskedUtc = DateTime.MinValue;
            string path = Path.Combine(_localStateRoot, "drain.request");
            try
            {
                if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8).IndexOf(" " + SupDrainMarker + " ", StringComparison.Ordinal) < 0)
                    return; // the launcher's own drain (update) is not ours to cancel
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log("Supervisor: cannot withdraw drain.request: " + ex.Message);
                return;
            }
            SupEvent("drain_timeout", _runningJobs.Count + " job(s) still running; restart withdrawn");
            if (_supProblem != null)
            {
                _supLevel = 3;
                _supLevelUtc = now;
                SupTellOwner(_supProblem, _supProblemDetail, "Restart withdrawn: jobs were still running after " + SupDef(Sup.drainTimeoutMinutes, 30) + " min.");
            }
        }

        // ---------------------------------------------------------------- 3. stuck jobs, Drive

        private void SupCheckStuckJobs(DateTime now)
        {
            int minutes = SupDef(Sup.stuckJobMinutes, 30);
            HashSet<string> running = new HashSet<string>(_runningJobs.Keys, StringComparer.Ordinal);
            foreach (string jobId in running)
            {
                int active = 0;
                WindowsJobObject jo;
                if (_runningJobObjects.TryGetValue(jobId, out jo))
                    active = jo.ActiveProcesses();
                if (active != 0 || _supNoProcessOk.ContainsKey(jobId))
                {
                    _supNoProcessSince.Remove(jobId);
                    continue;
                }
                DateTime since;
                if (!_supNoProcessSince.TryGetValue(jobId, out since))
                {
                    _supNoProcessSince[jobId] = now;
                    continue;
                }
                if ((now - since).TotalMinutes >= minutes && _supStuckReported.Add(jobId))
                {
                    SupEvent("stuck_job", jobId + ": running for Bridge, no agent process for " + (int)(now - since).TotalMinutes + " min");
                    SupPush("Bridge: job without a process",
                        "Job " + jobId + " has had no agent process for " + (int)(now - since).TotalMinutes +
                        " min. Stop it in CO Web; the job timeout ends it otherwise.");
                }
            }
            foreach (string k in new List<string>(_supNoProcessSince.Keys))
                if (!running.Contains(k))
                    _supNoProcessSince.Remove(k);
            _supStuckReported.RemoveWhere(delegate(string k) { return !running.Contains(k); });
        }

        private void SupCheckDrive(DateTime now)
        {
            bool present;
            try { present = Directory.Exists(_driveRoot); }
            catch { present = false; }
            if (present)
            {
                if (_supDriveMissingSinceUtc != DateTime.MinValue)
                {
                    int min = (int)(now - _supDriveMissingSinceUtc).TotalMinutes;
                    if (_supDrivePushed || min >= 2)
                        SupEvent("drive_back", "Google Drive is back after " + min + " min");
                }
                _supDriveMissingSinceUtc = DateTime.MinValue;
                _supDrivePushed = false;
                return;
            }
            if (_supDriveMissingSinceUtc == DateTime.MinValue)
            {
                _supDriveMissingSinceUtc = now;
                return;
            }
            int missing = (int)(now - _supDriveMissingSinceUtc).TotalMinutes;
            if (!_supDrivePushed && missing >= SupDef(Sup.drivePushMinutes, 20))
            {
                _supDrivePushed = true;
                SupEvent("drive_missing", _driveRoot + " missing for " + missing + " min; no restart");
                SupPush("Bridge: Google Drive is missing",
                    _driveRoot + " has been missing for " + missing + " min. Jobs with files fail; check Google Drive for desktop on the home PC.");
            }
        }

        // ---------------------------------------------------------------- events, push, state

        private void SupEvent(string kind, string detail)
        {
            Log("Supervisor: " + kind + " - " + detail);
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["host"] = _config.hostId ?? "";
            e["kind"] = kind;
            e["detail"] = PushCut(detail ?? "", 480);
            e["utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            _supEvents.Enqueue(e);
            while (_supEvents.Count > 50)
            {
                Dictionary<string, object> drop;
                _supEvents.TryDequeue(out drop);
            }
            ThreadPool.QueueUserWorkItem(delegate { SupFlushEvents(); });
        }

        private int _supFlushBusy;

        // Sent in order; on failure the rest waits for the next tick (network or site down).
        private void SupFlushEvents()
        {
            if (!DbqEnabled || Interlocked.CompareExchange(ref _supFlushBusy, 1, 0) != 0)
                return;
            try
            {
                Dictionary<string, object> e;
                while (_supEvents.TryPeek(out e))
                {
                    try
                    {
                        DbqCall("host_event", e, false, 15000);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.StartsWith("HTTP 4", StringComparison.Ordinal))
                        {
                            LogThrottled("sup-event-drop", "Supervisor: host_event rejected (" + PushCut(ex.Message, 120) + "); dropped.");
                            _supEvents.TryDequeue(out e);
                            continue;
                        }
                        return;
                    }
                    _supEvents.TryDequeue(out e);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _supFlushBusy, 0);
            }
        }

        private void SupPush(string title, string body)
        {
            string env = OwnerEnvOf(_config);
            Notify(env, title, PushCut(body, 380), "/", "bridge-supervisor");
        }

        // True at most once per `hours` for a key.
        private bool SupPushOnce(string key, int hours)
        {
            DateTime now = DateTime.UtcNow;
            lock (_supPushedUtc)
            {
                DateTime last;
                if (_supPushedUtc.TryGetValue(key, out last) && (now - last).TotalHours < hours)
                    return false;
                _supPushedUtc[key] = now;
                return true;
            }
        }

        private string SupStatePath
        {
            get { return Path.Combine(_localStateRoot, "supervisor.json"); }
        }

        private SupervisorState SupLoadState()
        {
            SupervisorState st = null;
            try
            {
                if (File.Exists(SupStatePath))
                    st = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<SupervisorState>(
                        File.ReadAllText(SupStatePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                LogThrottled("sup-state", "Supervisor: supervisor.json unreadable (" + ex.Message + "); counting from zero.");
            }
            if (st == null)
                st = new SupervisorState();
            if (st.restartsUtc == null) st.restartsUtc = new List<string>();
            if (st.launcherStartsUtc == null) st.launcherStartsUtc = new List<string>();
            return st;
        }

        private void SupSaveState(SupervisorState st)
        {
            try
            {
                AtomicFile.WriteJson(SupStatePath, st);
            }
            catch (Exception ex)
            {
                Log("Supervisor: cannot write supervisor.json: " + ex.Message);
            }
        }

        internal static int SupCount(List<string> utcs, DateTime sinceUtc)
        {
            int n = 0;
            if (utcs == null)
                return 0;
            foreach (string s in utcs)
            {
                DateTime t;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) && t >= sinceUtc)
                    n++;
            }
            return n;
        }

        // Keeps the last 2 days.
        internal static List<string> SupAppend(List<string> utcs, DateTime now)
        {
            List<string> keep = new List<string>();
            if (utcs != null)
                foreach (string s in utcs)
                {
                    DateTime t;
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) && t >= now.AddDays(-2))
                        keep.Add(s);
                }
            keep.Add(now.ToString("o", CultureInfo.InvariantCulture));
            return keep;
        }
    }

    // v1.16.0: an update must not hold every new job for hours, nor lose the running ones (12.11: the drain for 1.15.0
    // waited 2 h 08 min for seven long jobs and Home took nothing meanwhile). The launcher's drain.request is honoured
    // as before (no new claims, exit 3 once no job runs); when jobs still run after supervisor.updateDrainMinutes
    // (default 30, like drainTimeoutMinutes; < 0 = wait without a limit as before), every agent run is stopped like Stop
    // and its job ends "cancelled" with "Paused for the Bridge update ..." (CO Web diagnosis bridge_update_pause, not a
    // failure). Before that result, a continuation job is queued in the same chat: the same request with a new jobId,
    // resuming the agent's own session (claude --resume / codex exec resume) with a "continue where you stopped" prompt;
    // a run that never started (waiting for its chat) is queued again unchanged. The package goes into this host's
    // inbox (the old Bridge is paused and does not take it), its job row and chat message into the database, so CO Web
    // shows it queued and Stop reaches it. The next Bridge process (new version, or the old one after a rollback) takes
    // these continuations before any job of the database queue (update-continuations.txt, at most 5 min).
    // Gateway runs have no process and finish over HTTP; the drain still waits for them and for relay calls.
    // A supervisor drain (marker "supervisor") is never turned into a pause: it is withdrawn after drainTimeoutMinutes.
    internal sealed class UpdatePausedException : Exception
    {
        public UpdatePausedException() : base("Paused for the Bridge update before the agent started.") { }
    }

    internal sealed partial class BridgeApplicationContext
    {
        internal const string UpdatePauseText = "Paused for the Bridge update";
        private const string ContinuationsFile = "update-continuations.txt";

        private readonly ConcurrentDictionary<string, string> _updatePaused =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // jobId -> target version ("" unknown)
        private DateTime _drainSinceUtc = DateTime.MinValue;
        private string _drainText;                       // drain.request as read when the wait (re)started
        private bool _updatePauseStarted;
        private readonly HashSet<string> _resumeFirst = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _resumeFirstUntilUtc = DateTime.MinValue;

        // drain.request of the launcher: "<version> <utc>"; of the supervisor: "<version> <utc> supervisor <why>".
        internal static bool IsUpdateDrain(string drainText)
        {
            return drainText != null && drainText.IndexOf(" " + SupDrainMarker + " ", StringComparison.Ordinal) < 0;
        }

        internal static bool UpdatePauseDue(DateTime drainSinceUtc, DateTime nowUtc, int minutes)
        {
            if (minutes < 0 || drainSinceUtc == DateTime.MinValue)
                return false;
            return (nowUtc - drainSinceUtc).TotalMinutes >= SupDef(minutes, 30);
        }

        internal static string UpdatePauseMessage(string updateTo, string continuationJobId)
        {
            return UpdatePauseText + (String.IsNullOrEmpty(updateTo) ? "" : " to " + updateTo) + ": " +
                (String.IsNullOrEmpty(continuationJobId)
                    ? "the job was stopped and could not be queued again; resend the message."
                    : "the job was stopped and continues as job " + continuationJobId + " in this chat after the restart.");
        }

        internal static string ContinuationPrompt(string updateTo)
        {
            return "[Bridge update]\n" +
                "The AI Workbench Bridge on the home computer was updated" + (String.IsNullOrEmpty(updateTo) ? "" : " to " + updateTo) +
                " while you were working on the previous request, and your run was stopped for the restart. This is the same " +
                "session: everything you did so far is above, and the files you wrote are on disk.\n" +
                "Continue the previous request from where you stopped. Check what is already done (files, drafts, commands, " +
                "subtasks) before repeating a step; never send, submit or publish anything a second time. Then answer with the " +
                "complete final answer to the previous request.\n" +
                "[End of Bridge update]";
        }

        // The continuation request: the original one (unknown fields kept) with a new jobId and no attachments (they are
        // already in the workspace). With the paused run's session it resumes that session with ContinuationPrompt;
        // without one (the run never started) the original conversation and instruction run again.
        internal static Dictionary<string, object> BuildUpdateContinuation(
            Dictionary<string, object> original, string newJobId, string sessionId, bool tainted, string updateTo)
        {
            Dictionary<string, object> req = new Dictionary<string, object>(original, StringComparer.Ordinal);
            string oldJobId = DictString(original, "jobId");
            req["jobId"] = newJobId;
            req["attachments"] = new object[0];
            if (!String.IsNullOrEmpty(sessionId))
            {
                Dictionary<string, object> conv = new Dictionary<string, object>();
                conv["operation"] = "resume";
                conv["sessionId"] = sessionId;
                req["conversation"] = conv;
                req["instruction"] = ContinuationPrompt(updateTo);
            }
            if (tainted)
                req["tainted"] = true;
            req["updateContinuationOf"] = oldJobId;
            return req;
        }

        // CheckDrainRequest: drain.request appeared (since) or went away (reset).
        private void UpdateDrainStarted()
        {
            _drainSinceUtc = DateTime.UtcNow;
            _drainText = ReadDrainText();
            _updatePauseStarted = false;
        }

        private void UpdateDrainCancelled()
        {
            _drainSinceUtc = DateTime.MinValue;
            _drainText = null;
            _updatePauseStarted = false;
        }

        private string ReadDrainText()
        {
            try { return File.ReadAllText(Path.Combine(_localStateRoot, "drain.request"), Encoding.UTF8); }
            catch { return null; }
        }

        // CheckDrainRequest, under _scanLock, while jobs still run: pause them once the update has waited long enough.
        // Each RunAgent loop sees the flag within 0.5 s, stops its process tree like Stop and ProcessJob queues the
        // continuation; a job still waiting for its chat stops before its agent starts (UpdatePausedException).
        private void MaybePauseForUpdate()
        {
            if (_updatePauseStarted)
                return;
            string text = ReadDrainText();
            if (text == null)
                return;
            if (text != _drainText)
            {
                // rewritten without being deleted (a supervisor drain became the launcher's update): wait from now
                _drainText = text;
                _drainSinceUtc = DateTime.UtcNow;
                _updateTo = ReadDrainVersion();
                return;
            }
            if (!IsUpdateDrain(text) || !UpdatePauseDue(_drainSinceUtc, DateTime.UtcNow, Sup.updateDrainMinutes))
                return;
            if (_supLauncherExe != null && !SupLauncherAlive())
                return; // nobody would start Bridge again: keep the jobs running (SupBlockDrainExit)
            _updatePauseStarted = true;
            int n = 0;
            foreach (string jobId in _runningJobs.Keys)
            {
                _updatePaused[jobId] = _updateTo ?? "";
                n++;
            }
            if (n == 0)
                return;
            int minutes = (int)(DateTime.UtcNow - _drainSinceUtc).TotalMinutes;
            Log("Update " + (_updateTo ?? "?") + " has waited " + minutes + " min; pausing " + n + " running job(s); they continue after the restart.");
            SupEvent("update_pause", n + " job(s) paused for the update to " + (_updateTo ?? "?") + " after " + minutes + " min");
        }

        private bool UpdatePauseRequested(string jobId)
        {
            return _updatePaused.ContainsKey(jobId);
        }

        // Queues the continuation of a paused job; its jobId, or null when it could not be queued. Nothing that can fail
        // comes after the job row: the chat message first (the answer needs it), then the package (commit marker last),
        // then the job row (CO Web shows it queued, Stop reaches it; without it the job still runs from the inbox).
        private string QueueUpdateContinuation(byte[] requestBytes, JobRequest request, string sessionId, EventBatchWriter events)
        {
            if (request == null || requestBytes == null)
                return null;
            string pkg = null;
            try
            {
                string updateTo;
                _updatePaused.TryGetValue(request.jobId, out updateTo);
                string json = new UTF8Encoding(false).GetString(requestBytes).TrimStart('﻿');
                Dictionary<string, object> original = JsonUtil.Deserialize<Dictionary<string, object>>(json);
                string jobId = NextContinuationJobId();
                Dictionary<string, object> req = BuildUpdateContinuation(original, jobId, sessionId, IsJobTainted(request.jobId), updateTo);
                if (String.IsNullOrEmpty(sessionId))
                {
                    // The same request again: its staged files stay under .co-attachments/<old jobId>/ (kept by ProcessJob).
                    StringBuilder notes = new StringBuilder();
                    AppendAttachmentNotes(notes, request);
                    if (notes.Length > 0)
                        req["instruction"] = (DictString(original, "instruction") ?? "") + Environment.NewLine + notes.ToString();
                }
                object conversation;
                req.TryGetValue("conversation", out conversation);

                Dictionary<string, object> ev = new Dictionary<string, object>();
                ev["schemaVersion"] = 1;
                ev["eventId"] = "EVT-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + RandomPart(6);
                ev["sequence"] = 0;
                ev["type"] = "user_message";
                ev["createdUtc"] = DateTime.UtcNow.ToString("o");
                ev["chatId"] = request.chatId;
                ev["branchId"] = request.branchId;
                ev["jobId"] = jobId;
                ev["hostId"] = _config.hostId;
                ev["agent"] = request.agent;
                ev["text"] = "↻ " + (String.IsNullOrEmpty(sessionId)
                    ? "Bridge update" + (String.IsNullOrEmpty(updateTo) ? "" : " to " + updateTo) + ": the previous message had not started yet and runs again here."
                    : "Bridge update" + (String.IsNullOrEmpty(updateTo) ? "" : " to " + updateTo) + ": the previous job was paused for the restart and continues here in the same session.");
                ev["author"] = "bridge";
                ev["attachments"] = new object[0];
                ev["returnFiles"] = new object[0];
                ev["conversation"] = conversation;
                ev["mode"] = request.mode;
                ev["model"] = request.model;
                ev["effort"] = request.effort;
                string chatDir = SafeCombineUnderRoot(_projectsDir, Path.Combine(request.projectId, "chats", request.chatId));
                AppendUserEvent(chatDir, ev, "Update continuation " + jobId);

                pkg = Path.Combine(_inboxDir, jobId);
                Directory.CreateDirectory(pkg);
                byte[] reqBytes = new UTF8Encoding(false).GetBytes(JsonPretty(JsonUtil.Serialize(req)));
                File.WriteAllBytes(Path.Combine(pkg, "request.json"), reqBytes);
                CommitMarkerUtil.WriteForBytes(reqBytes, "request.json", Path.Combine(pkg, "request.ready"));
                pkg = null; // committed: it runs

                try
                {
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["hostId"] = _config.hostId;
                    row["state"] = "inbox";
                    row["request"] = req;
                    Dictionary<string, object> put = new Dictionary<string, object>();
                    put["jobs"] = new object[] { row };
                    if (DbqEnabled)
                        DbqCall("put", put, true);
                }
                catch (Exception ex)
                {
                    Log("Update continuation " + jobId + ": job row not written to the database (" + ex.Message + "); it runs from the inbox.");
                }
                try
                {
                    File.AppendAllText(Path.Combine(_localStateRoot, ContinuationsFile), jobId + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Log("Update continuation " + jobId + ": not listed for the next start (" + ex.Message + ").");
                }
                Log("Job " + request.jobId + " paused for the update; continuation " + jobId +
                    (String.IsNullOrEmpty(sessionId) ? " runs the request again." : " resumes session " + sessionId + "."));
                if (events != null)
                    events.Write("bridge", "update_continuation", "Continues as job " + jobId + " after the Bridge restart.");
                return jobId;
            }
            catch (Exception ex)
            {
                Log("Job " + request.jobId + ": update continuation could not be queued: " + ex.Message);
                if (pkg != null)
                    TryDeleteDirectory(pkg);
                return null;
            }
        }

        private readonly object _continuationIdLock = new object();
        private string _lastContinuationId = "";

        // Increasing ids: the next process claims its inbox in name order, so continuations start in the queued order.
        private string NextContinuationJobId()
        {
            lock (_continuationIdLock)
            {
                string id;
                int tries = 0;
                do
                {
                    if (++tries % 40 == 0)
                        Thread.Sleep(1000);
                    id = "JOB-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + RandomPart(6);
                }
                while (String.CompareOrdinal(id, _lastContinuationId) <= 0);
                _lastContinuationId = id;
                return id;
            }
        }

        // Session of a request as ProcessJob locks it (agent:sessionId for resume/fork), or null.
        internal static string ContinuationSessionKey(Dictionary<string, object> req)
        {
            if (req == null)
                return null;
            object convObj;
            Dictionary<string, object> conv = req.TryGetValue("conversation", out convObj) ? convObj as Dictionary<string, object> : null;
            if (conv == null)
                return null;
            string op = DictString(conv, "operation");
            string session = DictString(conv, "sessionId");
            if (String.IsNullOrEmpty(op) || op == "new" || String.IsNullOrEmpty(session))
                return null;
            return DictString(req, "agent") + ":" + session;
        }

        // Continuations of the previous process per session, in the order it queued them (update-continuations.txt).
        private readonly Dictionary<string, List<string>> _continuationOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // At start: continuations the previous process queued run before any job of the database queue, and those of
        // one session in their order (the paused job before a message of its chat that was waiting for it).
        private void LoadUpdateContinuations()
        {
            string path = Path.Combine(_localStateRoot, ContinuationsFile);
            try
            {
                if (!File.Exists(path))
                    return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                lock (_resumeFirst)
                {
                    foreach (string line in lines)
                    {
                        string id = line.Trim();
                        if (id.Length == 0 || _resumeFirst.Contains(id))
                            continue;
                        _resumeFirst.Add(id);
                        string key = null;
                        try
                        {
                            string rp = Path.Combine(Path.Combine(_inboxDir, id), "request.json");
                            if (File.Exists(rp))
                                key = ContinuationSessionKey(JsonUtil.Deserialize<Dictionary<string, object>>(
                                    File.ReadAllText(rp, Encoding.UTF8).TrimStart('﻿')));
                        }
                        catch
                        {
                        }
                        if (key == null)
                            continue;
                        List<string> order;
                        if (!_continuationOrder.TryGetValue(key, out order))
                            _continuationOrder[key] = order = new List<string>();
                        order.Add(id);
                    }
                    _resumeFirstUntilUtc = DateTime.UtcNow.AddMinutes(5);
                }
                File.Delete(path);
                Log("Update continuations to take first: " + _resumeFirst.Count + ".");
            }
            catch (Exception ex)
            {
                Log("Update continuations not read: " + ex.Message);
            }
        }

        // True while a continuation of the previous process still waits in the inbox (DbqClaimLoop claims nothing then).
        private bool UpdateContinuationsPending()
        {
            lock (_resumeFirst)
            {
                if (_resumeFirst.Count == 0)
                    return false;
                if (DateTime.UtcNow > _resumeFirstUntilUtc)
                {
                    _resumeFirst.Clear();
                    return false;
                }
                _resumeFirst.RemoveWhere(delegate(string id) { return !Directory.Exists(Path.Combine(_inboxDir, id)); });
                return _resumeFirst.Count > 0;
            }
        }

        // ProcessJob, before the session lock: a continuation waits (at most 10 min) until the continuations queued
        // before it for the same session have finished; one that is no longer in the inbox, claimed or running is done.
        private void WaitForEarlierContinuations(string jobId, EventBatchWriter events)
        {
            DateTime until = DateTime.UtcNow.AddMinutes(10);
            bool told = false;
            try
            {
                while (!_exiting && DateTime.UtcNow < until)
                {
                    string earlier = null;
                    lock (_resumeFirst)
                    {
                        foreach (List<string> order in _continuationOrder.Values)
                        {
                            int at = order.IndexOf(jobId);
                            for (int k = 0; k < at && earlier == null; k++)
                            {
                                string id = order[k];
                                if (_runningJobs.ContainsKey(id) || Directory.Exists(Path.Combine(_inboxDir, id)) ||
                                    Directory.Exists(Path.Combine(_claimedDir, id)))
                                    earlier = id;
                            }
                        }
                    }
                    if (earlier == null)
                        return;
                    if (!told)
                    {
                        told = true;
                        SupNoProcessOk(jobId, true);
                        if (events != null)
                            events.Write("bridge", "queued", "Waiting for the earlier continuation " + earlier + " of this chat.");
                    }
                    Thread.Sleep(1000);
                }
            }
            finally
            {
                if (told)
                    SupNoProcessOk(jobId, false);
            }
        }

        private void ContinuationDone(string jobId)
        {
            lock (_resumeFirst)
            {
                foreach (List<string> order in _continuationOrder.Values)
                    order.Remove(jobId);
            }
        }
    }
}
