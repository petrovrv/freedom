// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AiWorkbenchBridge
{
    // v1.2.0: scheduled runs. projects/<projectId>/schedules.json lists prompts with a cron time; when one is due Bridge
    // queues an ordinary job (inbox/<jobId>/request.json + request.ready) into the schedule's own chat and writes the
    // user_message event there, so CO Web shows the run and its answer like any other turn. The agent of the run has
    // the same tools as in a chat (devices, connectors, files), so new tools become available to schedules by themselves.
    internal sealed partial class BridgeApplicationContext
    {
        private DateTime _lastScheduleScanUtc = DateTime.MinValue;
        private readonly object _scheduleLock = new object();

        private string ScheduleStatePath()
        {
            return Path.Combine(_localStateRoot, "schedules-state.json");
        }

        internal void MaybeRunSchedules()
        {
            if ((DateTime.UtcNow - _lastScheduleScanUtc).TotalSeconds < 30)
                return;
            _lastScheduleScanUtc = DateTime.UtcNow;
            if (!Monitor_TryEnter())
                return;
            try
            {
                Dictionary<string, ScheduleState> state = LoadScheduleState();
                bool changed = false;
                if (!Directory.Exists(_projectsDir))
                    return;
                foreach (string projectDir in Directory.GetDirectories(_projectsDir))
                {
                    string file = Path.Combine(projectDir, "schedules.json");
                    if (!File.Exists(file))
                        continue;
                    ScheduleFile sf;
                    try { sf = JsonUtil.Deserialize<ScheduleFile>(File.ReadAllText(file, Encoding.UTF8)); }
                    catch (Exception ex) { Log("schedules.json unreadable in " + projectDir + ": " + ex.Message); continue; }
                    if (sf == null || sf.schedules == null)
                        continue;
                    string projectId = Path.GetFileName(projectDir);
                    foreach (ScheduleEntry s in sf.schedules)
                    {
                        try
                        {
                            if (CheckSchedule(projectDir, projectId, s, state))
                                changed = true;
                        }
                        catch (Exception ex)
                        {
                            Log("Schedule " + projectId + "/" + (s == null ? "?" : s.id) + " failed: " + ex.Message);
                        }
                    }
                }
                if (changed)
                    AtomicFile.WriteJson(ScheduleStatePath(), state);
            }
            finally
            {
                System.Threading.Monitor.Exit(_scheduleLock);
            }
        }

        private readonly HashSet<string> _scheduleBadPerms = new HashSet<string>();

        private bool Monitor_TryEnter()
        {
            return System.Threading.Monitor.TryEnter(_scheduleLock);
        }

        private Dictionary<string, ScheduleState> LoadScheduleState()
        {
            try
            {
                if (File.Exists(ScheduleStatePath()))
                {
                    Dictionary<string, ScheduleState> s = JsonUtil.Deserialize<Dictionary<string, ScheduleState>>(File.ReadAllText(ScheduleStatePath(), Encoding.UTF8));
                    if (s != null)
                        return s;
                }
            }
            catch (Exception ex)
            {
                Log("schedules-state.json unreadable, starting fresh: " + ex.Message);
            }
            return new Dictionary<string, ScheduleState>(StringComparer.Ordinal);
        }

        // Returns true when the state changed.
        private bool CheckSchedule(string projectDir, string projectId, ScheduleEntry s, Dictionary<string, ScheduleState> state)
        {
            if (s == null || String.IsNullOrWhiteSpace(s.id) || String.IsNullOrWhiteSpace(s.prompt))
                return false;
            string key = projectId + "/" + s.id;
            if (s.permissions != null)
            {
                // 1.9.3: "files": true/false written by hand is normalised; an unknown value skips the schedule
                // (logged once) instead of producing jobs and a chat that Bridge rejects.
                string files = NormalizeFilesPermission(s.permissions.files);
                if (files == "")
                {
                    if (_scheduleBadPerms.Add(key + "|" + s.permissions.files))
                        Log("Schedule " + key + " skipped: permissions.files must be read or write (got '" + s.permissions.files + "') in schedules.json");
                    return false;
                }
                s.permissions.files = files;
            }
            ScheduleState st;
            bool known = state.TryGetValue(key, out st);
            if (!known)
            {
                st = new ScheduleState();
                state[key] = st;
            }

            bool runNow = s.runNow;
            DateTime nowUtc = DateTime.UtcNow;
            DateTime? due = null;
            if (s.enabled && !String.IsNullOrWhiteSpace(s.cron))
            {
                TimeZoneInfo tz = ScheduleTimeZone(s.timezone);
                CronExpression cron = CronExpression.Parse(s.cron);
                int late = s.maxLateMinutes > 0 ? s.maxLateMinutes : 120;
                due = cron.LastMatchUtc(nowUtc, tz, late);
            }

            DateTime lastDue = ParseUtc(st.lastDueUtc);
            if (!known && !runNow)
            {
                // A schedule seen for the first time starts with its next time; it does not fire for the past.
                st.lastDueUtc = (due ?? nowUtc).ToString("o");
                return true;
            }
            bool fire = runNow || (due.HasValue && due.Value > lastDue);
            if (!fire)
                return !known;

            if (due.HasValue && due.Value > lastDue)
                st.lastDueUtc = due.Value.ToString("o");

            // One run at a time: the previous job of this schedule must have left the inbox and the claimed folder.
            if (!String.IsNullOrEmpty(st.lastJobId) &&
                (Directory.Exists(Path.Combine(_inboxDir, st.lastJobId)) || Directory.Exists(Path.Combine(Path.Combine(_hostTransportRoot, "claimed"), st.lastJobId))))
            {
                Log("Schedule " + key + " skipped: previous run " + st.lastJobId + " still running.");
                st.skipped++;
                NotifyStuckSchedule(key, s, st);
                if (runNow) ClearRunNow(projectDir, s.id);
                return true;
            }

            string jobId = QueueScheduledRun(projectDir, projectId, s, due ?? nowUtc);
            st.lastJobId = jobId;
            st.lastRunUtc = nowUtc.ToString("o");
            st.runs++;
            if (runNow) ClearRunNow(projectDir, s.id);
            Log("Schedule " + key + " queued " + jobId + (runNow ? " (run now)" : " (due " + (due ?? nowUtc).ToString("o") + ")") + ".");
            return true;
        }

        // v1.3.2: a previous run that never left the inbox for 30 min blocks the schedule (2026-10-15: 15 h unnoticed).
        // One push per stuck job to the owner environment.
        private void NotifyStuckSchedule(string key, ScheduleEntry s, ScheduleState st)
        {
            try
            {
                if (st.stuckNotified == st.lastJobId || !Directory.Exists(Path.Combine(_inboxDir, st.lastJobId)))
                    return;
                DateTime queued = ParseUtc(st.lastRunUtc);
                if (queued == DateTime.MinValue || (DateTime.UtcNow - queued).TotalMinutes < 30)
                    return;
                st.stuckNotified = st.lastJobId;
                Log("Schedule " + key + ": job " + st.lastJobId + " has not been taken since " + st.lastRunUtc + "; owner notified.");
                string ownerEnv = OwnerEnvOf(_config);
                Notify(ownerEnv, "Schedule stuck: " + (String.IsNullOrEmpty(s.title) ? key : s.title),
                    "Job " + st.lastJobId + " has waited in the inbox since " + queued.ToString("yyyy-MM-dd HH:mm") +
                    " UTC; later runs are skipped. See the Bridge log.", "/", "schedule-stuck-" + key);
            }
            catch (Exception ex)
            {
                Log("Schedule " + key + ": stuck check failed: " + ex.Message);
            }
        }

        private static DateTime ParseUtc(string s)
        {
            DateTime d;
            return !String.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)
                ? d : DateTime.MinValue;
        }

        // IANA names used in schedules → Windows zones (the .NET Framework knows only the latter).
        internal static TimeZoneInfo ScheduleTimeZone(string name)
        {
            string id = String.IsNullOrWhiteSpace(name) ? "Europe/Brussels" : name.Trim();
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map["Europe/Brussels"] = "Romance Standard Time";
            map["Europe/Paris"] = "Romance Standard Time";
            map["Europe/Amsterdam"] = "W. Europe Standard Time";
            map["Europe/Berlin"] = "W. Europe Standard Time";
            map["Europe/London"] = "GMT Standard Time";
            map["Europe/Moscow"] = "Russian Standard Time";
            map["UTC"] = "UTC";
            string win;
            if (map.TryGetValue(id, out win)) id = win;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { return TimeZoneInfo.Local; }
        }

        private void ClearRunNow(string projectDir, string scheduleId)
        {
            UpdateScheduleFile(projectDir, scheduleId, delegate (Dictionary<string, object> e) { e["runNow"] = false; });
        }

        // Read–modify–write of one entry of schedules.json, keeping every other field as the user wrote it.
        private void UpdateScheduleFile(string projectDir, string scheduleId, Action<Dictionary<string, object>> change)
        {
            string file = Path.Combine(projectDir, "schedules.json");
            Dictionary<string, object> root = JsonUtil.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            object arr;
            if (root == null || !root.TryGetValue("schedules", out arr) || !(arr is System.Collections.IEnumerable))
                return;
            foreach (object o in (System.Collections.IEnumerable)arr)
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                if (e != null && LedgerStr(e, "id") == scheduleId)
                    change(e);
            }
            File.WriteAllText(file, JsonPretty(JsonUtil.Serialize(root)), new UTF8Encoding(false));
        }

        // ---------- the run ----------

        private string QueueScheduledRun(string projectDir, string projectId, ScheduleEntry s, DateTime dueUtc)
        {
            string environmentId = ProjectEnvironment(projectDir);
            string mode = String.IsNullOrWhiteSpace(s.mode) ? "cowork" : s.mode;
            string agent = String.IsNullOrWhiteSpace(s.agent) ? "claude" : s.agent;
            string chatDir = EnsureScheduleChat(projectDir, projectId, s, mode);
            string chatId = Path.GetFileName(chatDir);
            string jobId = "JOB-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + RandomPart(6);

            TimeZoneInfo tz = ScheduleTimeZone(s.timezone);
            string dueLocal = TimeZoneInfo.ConvertTimeFromUtc(dueUtc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string chatPath = "AI-Workbench/projects/" + projectId + "/chats/" + chatId;
            // Freedom F1: the Drive mount is the parent of config.driveRoot (G:\My Drive\AI-Workbench -> G:\My Drive)
            string driveMount = Path.GetDirectoryName(_config.driveRoot.TrimEnd('\\')) ?? _config.driveRoot;
            string instruction =
                "[AI Workbench environment]\n" +
                "You work inside AI Workbench (CO). This is an unattended scheduled run: nobody is watching while it runs and nobody " +
                "can answer questions or confirmations.\n" +
                "- Executor: the home computer (Windows, BridgeAgent). Google Drive is mounted as " + driveMount + ", so " + chatPath + " is " + driveMount + "\\" + chatPath.Replace('/', '\\') + ".\n" +
                "- Project " + projectId + ", chat \"" + (String.IsNullOrEmpty(s.title) ? s.id : s.title) + "\" (" + chatId + "), mode " + mode + ". Your working directory is this chat's scratch folder.\n" +
                "- AI-Workbench/CONTEXT.md describes the system.\n" +
                "[End of AI Workbench environment]\n\n" +
                "[Scheduled run]\n" +
                "Schedule \"" + (String.IsNullOrEmpty(s.title) ? s.id : s.title) + "\" (" + s.id + ", cron " + s.cron + " " + (s.timezone ?? "Europe/Brussels") + "), due " + dueLocal + ".\n" +
                "- If the task uses a computer (CO Desktop device), first call its connectors_status with check=true; if a connector the task needs is not ready, say which one and what the user must do, and stop.\n" +
                "- Never send mail or messages and never accept or create meetings: prepare drafts; the user sends them.\n" +
                "- Keep state between runs in files of the project (for example a JSON ledger in the chat's scratch folder), not in memory.\n" +
                "- Answer with a short report: what you found, what you prepared (drafts, files), what needs the user.\n" +
                "[End of scheduled run]\n\n" + s.prompt;

            Dictionary<string, object> req = new Dictionary<string, object>();
            req["schemaVersion"] = 3;
            req["hostId"] = _config.hostId;
            req["jobId"] = jobId;
            req["environmentId"] = environmentId;
            req["projectId"] = projectId;
            req["chatId"] = chatId;
            req["branchId"] = "BR-root";
            req["agent"] = agent;
            req["instruction"] = instruction;
            Dictionary<string, object> conv = new Dictionary<string, object>();
            conv["operation"] = "new";
            conv["sessionId"] = null;
            req["conversation"] = conv;
            req["attachments"] = new object[0];
            req["artifactPaths"] = new object[0];
            req["timeoutMinutes"] = s.timeoutMinutes > 0 ? s.timeoutMinutes : 0;
            req["protocolMinor"] = s.devices != null && s.devices.Count > 0 ? 2 : 1;
            if (s.devices != null && s.devices.Count > 0) req["devices"] = s.devices;
            if (s.permissions != null) req["permissions"] = s.permissions;
            req["mode"] = mode;
            req["model"] = s.model;
            req["effort"] = s.effort;
            Dictionary<string, object> output = new Dictionary<string, object>();
            output["autoExport"] = true;
            req["output"] = output;

            // Chat first, so CO Web finds the turn as soon as the job runs; then the job package, commit marker last.
            AppendScheduleEvent(chatDir, chatId, jobId, agent, mode, s);
            string pkg = Path.Combine(_inboxDir, jobId);
            Directory.CreateDirectory(pkg);
            string reqPath = Path.Combine(pkg, "request.json");
            byte[] reqBytes = new UTF8Encoding(false).GetBytes(JsonPretty(JsonUtil.Serialize(req)));
            File.WriteAllBytes(reqPath, reqBytes);
            CommitMarkerUtil.WriteForBytes(reqBytes, "request.json", Path.Combine(pkg, "request.ready"));
            return jobId;
        }

        private string ProjectEnvironment(string projectDir)
        {
            try
            {
                string pj = Path.Combine(projectDir, "project.json");
                if (File.Exists(pj))
                {
                    Dictionary<string, object> p = JsonUtil.Deserialize<Dictionary<string, object>>(File.ReadAllText(pj, Encoding.UTF8));
                    string env = LedgerStr(p, "environmentId");
                    if (env.Length > 0) return env;
                }
            }
            catch (Exception) { }
            return OwnerEnvOf(_config);
        }

        // The schedule's own chat ("⏰ <title>"), created on the first run; its id is written back into schedules.json.
        private string EnsureScheduleChat(string projectDir, string projectId, ScheduleEntry s, string mode)
        {
            string chats = Path.Combine(projectDir, "chats");
            if (!String.IsNullOrWhiteSpace(s.chatId) && Directory.Exists(Path.Combine(chats, s.chatId)))
                return Path.Combine(chats, s.chatId);

            string chatId = "CHAT-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + RandomPart(4);
            string dir = Path.Combine(chats, chatId);
            Directory.CreateDirectory(Path.Combine(dir, "events"));
            Directory.CreateDirectory(Path.Combine(dir, "scratch")); // 1.6.1: else the first job waits for a cloud scratch
            string now = DateTime.UtcNow.ToString("o");
            Dictionary<string, object> chat = new Dictionary<string, object>();
            chat["chatId"] = chatId;
            chat["projectId"] = projectId;
            chat["title"] = "\u23F0 " + (String.IsNullOrEmpty(s.title) ? s.id : s.title);
            chat["rootBranchId"] = "BR-root";
            chat["createdUtc"] = now;
            chat["updatedUtc"] = now;
            chat["defaultMode"] = mode;
            chat["titleAuto"] = false;
            if (s.permissions != null) chat["permissions"] = s.permissions;
            File.WriteAllText(Path.Combine(dir, "chat.json"), JsonPretty(JsonUtil.Serialize(chat)), new UTF8Encoding(false));
            // 1.3.3: the chat goes to the database at once (its messages live only there); drive-sync adds the folder id.
            try
            {
                Dictionary<string, object> put = new Dictionary<string, object>();
                put["chats"] = new object[] { chat };
                DbqCall("put", put, true);
            }
            catch (Exception ex)
            {
                Log("Schedule " + s.id + ": chat not written to the database (" + ex.Message + "); drive-sync will copy it");
            }
            s.chatId = chatId;
            UpdateScheduleFile(projectDir, s.id, delegate (Dictionary<string, object> e) { e["chatId"] = chatId; });
            return dir;
        }

        private void AppendScheduleEvent(string chatDir, string chatId, string jobId, string agent, string mode, ScheduleEntry s)
        {
            string eventId = "EVT-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + RandomPart(6);
            Dictionary<string, object> ev = new Dictionary<string, object>();
            ev["schemaVersion"] = 1;
            ev["eventId"] = eventId;
            ev["sequence"] = 0; // 1.3.3: assigned by the database (append_event)
            ev["type"] = "user_message";
            ev["createdUtc"] = DateTime.UtcNow.ToString("o");
            ev["chatId"] = chatId;
            ev["branchId"] = "BR-root";
            ev["jobId"] = jobId;
            ev["hostId"] = _config.hostId;
            ev["agent"] = agent;
            ev["text"] = "\u23F0 " + (String.IsNullOrEmpty(s.title) ? s.id : s.title) + " (" + s.cron + ")\n\n" + s.prompt;
            ev["author"] = "scheduler";
            ev["attachments"] = new object[0];
            ev["returnFiles"] = new object[0];
            Dictionary<string, object> conv = new Dictionary<string, object>();
            conv["operation"] = "new";
            conv["sessionId"] = null;
            ev["conversation"] = conv;
            ev["mode"] = mode;
            ev["model"] = s.model;
            ev["effort"] = s.effort;
            ev["workbenchNote"] = true;
            AppendUserEvent(chatDir, ev, "Schedule " + s.id);
        }

        // A user_message of Bridge's own (schedules, v1.16.0 update continuations). 1.3.3: chat messages live only in
        // the database; append_event numbers the message and touches the chat.
        private void AppendUserEvent(string chatDir, Dictionary<string, object> ev, string what)
        {
            string eventId = Convert.ToString(ev["eventId"], CultureInfo.InvariantCulture);
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["event"] = ev;
                DbqCall("append_event", body, true);
                return;
            }
            catch (Exception ex)
            {
                Log(what + ": message not written to the database (" + ex.Message + "); writing the Drive fallback");
            }

            // Fallback: a Drive file that drive-sync imports. Old message files were removed, so the number is placed
            // after any database message (chats hold far fewer than 10000 messages).
            string events = Path.Combine(chatDir, "events");
            Directory.CreateDirectory(events);
            int seq = 0;
            foreach (string f in Directory.GetFiles(events, "*.json"))
            {
                int n;
                string name = Path.GetFileName(f);
                if (name.Length > 6 && Int32.TryParse(name.Substring(0, 6), out n) && n > seq) seq = n;
            }
            seq = Math.Max(seq, 10000) + 1;
            ev["sequence"] = seq;
            File.WriteAllText(Path.Combine(events, seq.ToString("000000") + "-user_message-" + eventId + ".json"),
                JsonPretty(JsonUtil.Serialize(ev)), new UTF8Encoding(false));

            // The chat moves to the top of the list in CO Web.
            string cj = Path.Combine(chatDir, "chat.json");
            try
            {
                Dictionary<string, object> chat = JsonUtil.Deserialize<Dictionary<string, object>>(File.ReadAllText(cj, Encoding.UTF8));
                chat["updatedUtc"] = DateTime.UtcNow.ToString("o");
                File.WriteAllText(cj, JsonPretty(JsonUtil.Serialize(chat)), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static string RandomPart(int n)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
            byte[] b = new byte[n];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            StringBuilder sb = new StringBuilder(n);
            foreach (byte x in b) sb.Append(chars[x % chars.Length]);
            return sb.ToString();
        }

        // Readable files for people who edit schedules.json by hand.
        private static string JsonPretty(string json)
        {
            StringBuilder sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\' || (i > 1 && json[i - 2] == '\\'))) inString = !inString;
                if (inString) { sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c); sb.Append('\n'); indent++; sb.Append(new string(' ', indent * 2)); }
                else if (c == '}' || c == ']') { sb.Append('\n'); indent--; sb.Append(new string(' ', indent * 2)); sb.Append(c); }
                else if (c == ',') { sb.Append(c); sb.Append('\n'); sb.Append(new string(' ', indent * 2)); }
                else if (c == ':') sb.Append(": ");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    internal sealed class ScheduleFile
    {
        public int schemaVersion { get; set; }
        public List<ScheduleEntry> schedules { get; set; }
    }

    internal sealed class ScheduleEntry
    {
        public string id { get; set; }
        public string title { get; set; }
        public bool enabled { get; set; }
        public string cron { get; set; }              // minute hour day-of-month month day-of-week
        public string timezone { get; set; }          // IANA, default Europe/Brussels
        public string agent { get; set; }             // claude (default), dispatcher, codex
        public string mode { get; set; }              // cowork (default) or chat
        public string model { get; set; }
        public string effort { get; set; }
        public List<string> devices { get; set; }     // CO Desktop device ids whose tools the run may use
        public PermissionsRequest permissions { get; set; }
        public string prompt { get; set; }
        public string chatId { get; set; }            // set by Bridge on the first run
        public int maxLateMinutes { get; set; }       // a run missed (computer asleep) is made up if late by at most this; default 120
        public int timeoutMinutes { get; set; }
        public bool runNow { get; set; }              // set by the user or CO Web; Bridge runs once and clears it
    }

    internal sealed class ScheduleState
    {
        public string lastDueUtc { get; set; }
        public string lastRunUtc { get; set; }
        public string lastJobId { get; set; }
        public int runs { get; set; }
        public int skipped { get; set; }
        // v1.3.2: lastJobId already reported as stuck in the inbox
        public string stuckNotified { get; set; }
    }

    // Five-field cron: minute hour day-of-month month day-of-week (0 or 7 = Sunday); "*", "a-b", "a,b", "*/n", "a-b/n".
    // As in Vixie cron, when both day fields are restricted a day matching either one counts.
    internal sealed class CronExpression
    {
        private bool[] _min, _hour, _dom, _mon, _dow;
        private bool _domAny, _dowAny;

        public static CronExpression Parse(string expr)
        {
            string[] f = (expr ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5)
                throw new FormatException("cron needs 5 fields (minute hour day month weekday): '" + expr + "'");
            CronExpression c = new CronExpression();
            c._min = Field(f[0], 0, 59);
            c._hour = Field(f[1], 0, 23);
            c._dom = Field(f[2], 1, 31);
            c._mon = Field(f[3], 1, 12);
            c._dow = Field(f[4], 0, 7);
            if (c._dow[7]) c._dow[0] = true;
            c._domAny = f[2] == "*";
            c._dowAny = f[4] == "*";
            return c;
        }

        private static bool[] Field(string s, int lo, int hi)
        {
            bool[] set = new bool[hi + 1];
            foreach (string part in s.Split(','))
            {
                string range = part;
                int step = 1;
                int slash = part.IndexOf('/');
                if (slash >= 0)
                {
                    step = Int32.Parse(part.Substring(slash + 1), CultureInfo.InvariantCulture);
                    range = part.Substring(0, slash);
                    if (step < 1) throw new FormatException("cron step must be ≥ 1");
                }
                int a, b;
                if (range == "*") { a = lo; b = hi; }
                else if (range.Contains("-"))
                {
                    string[] ab = range.Split('-');
                    a = Int32.Parse(ab[0], CultureInfo.InvariantCulture);
                    b = Int32.Parse(ab[1], CultureInfo.InvariantCulture);
                }
                else
                {
                    a = Int32.Parse(range, CultureInfo.InvariantCulture);
                    b = slash >= 0 ? hi : a;
                }
                if (a < lo || b > hi || a > b) throw new FormatException("cron value out of range: '" + part + "'");
                for (int v = a; v <= b; v += step) set[v] = true;
            }
            return set;
        }

        public bool Matches(DateTime local)
        {
            if (!_min[local.Minute] || !_hour[local.Hour] || !_mon[local.Month]) return false;
            bool dom = _dom[local.Day], dow = _dow[(int)local.DayOfWeek];
            if (_domAny && _dowAny) return true;
            if (_domAny) return dow;
            if (_dowAny) return dom;
            return dom || dow;
        }

        // Latest minute ≤ now (UTC) that matches in the zone, looking back at most maxLateMinutes; null if none.
        public DateTime? LastMatchUtc(DateTime nowUtc, TimeZoneInfo tz, int maxLateMinutes)
        {
            DateTime t = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
            for (int i = 0; i <= maxLateMinutes; i++, t = t.AddMinutes(-1))
                if (Matches(TimeZoneInfo.ConvertTimeFromUtc(t, tz)))
                    return t;
            return null;
        }
    }
}
