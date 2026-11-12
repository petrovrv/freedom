// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CO.Core.Doctor;

// S3 (11.11): the CO Desktop doctor on the owner's computer checks and repairs Bridge from local files only, so it
// works while both the Site and Bridge are down. Safe repairs: start the launcher, a graceful restart (drain, Bridge
// waits for its running jobs), a rollback to the previous version, cancel markers for stuck jobs. Killing Bridge
// (its Job Objects end the agents of running jobs) happens only on the owner's button, or for an idle Bridge during a
// switch. Reports go to the Site (db.php host_event, Health) when it is reachable.

public enum DoctorLevel { Ok, Info, Warn, Fail }

public sealed record DoctorCheck(string Id, DoctorLevel Level, string Title, string Detail, string? Fix = null);

public sealed record ProcInfo(int Pid, string Path, DateTime StartUtc = default);

public sealed record ThreatInfo(DateTime Utc, string Resources);

// Platform side (WindowsDoctorProbe in CO.Desktop); tests use a fake.
public interface IDoctorProbe
{
    IReadOnlyList<ProcInfo> Processes(string name);
    IReadOnlyList<ThreatInfo> Detections();           // Defender detections; throws when unavailable
    string RunTask(string name);                      // scheduled task; returns an outcome, throws on failure
    void Start(string exe);
    void Kill(int pid);                               // this process only, never its tree
    Task<bool> SiteUpAsync(string url, CancellationToken ct);
    string? FindOnPath(string exe);
}

public static class DoctorFix
{
    public const string StartLauncher = "start_launcher", Restart = "restart", Rollback = "rollback",
        CancelStuck = "cancel_stuck", HardRestart = "hard_restart";

    public static string Label(string id) => id switch
    {
        StartLauncher => "Start Bridge (launcher)",
        Restart => "Restart Bridge when its jobs finish",
        Rollback => "Roll back to the previous Bridge version",
        CancelStuck => "Cancel stuck jobs",
        HardRestart => "Kill Bridge now (ends running jobs)",
        _ => id,
    };
}

public sealed class DoctorReport
{
    public DateTime Utc { get; init; }
    public List<DoctorCheck> Checks { get; } = new();
    public OwnerInfo? Owner { get; set; }
    public List<string> StuckJobs { get; } = new();
    public DoctorLevel Level => Checks.Count == 0 ? DoctorLevel.Ok : Checks.Max(c => c.Level);
    public IEnumerable<string> Fixes => Checks.Where(c => c.Fix != null && c.Level >= DoctorLevel.Warn).Select(c => c.Fix!).Distinct();
    public DoctorCheck? Find(string id) => Checks.FirstOrDefault(c => c.Id == id);

    public string Summary => Level switch
    {
        DoctorLevel.Fail => "Problem: " + string.Join("; ", Checks.Where(c => c.Level == DoctorLevel.Fail).Select(c => c.Title)),
        DoctorLevel.Warn => "Attention: " + string.Join("; ", Checks.Where(c => c.Level == DoctorLevel.Warn).Select(c => c.Title)),
        _ => "Bridge is healthy",
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append("Doctor report ").Append(Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC: ").AppendLine(Summary);
        foreach (var c in Checks)
        {
            sb.Append('[').Append(c.Level.ToString().ToUpperInvariant()).Append("] ").Append(c.Title);
            if (c.Detail.Length > 0) sb.Append(" — ").Append(c.Detail);
            if (c.Fix != null) sb.Append(" (fix: ").Append(c.Fix).Append(')');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

// owner.json of the host: heartbeat of Bridge (written every scan, also while paused or draining).
// RelayActive (Bridge 1.14+): device relay calls of site jobs running or being claimed; null = an older Bridge that does
// not report them, so it is never taken for idle (R2, review S3-01).
public sealed record OwnerInfo(DateTime UpdatedUtc, int ProcessId, string Version, string State, int RunningJobs, string Machine, int? RelayActive = null);

public sealed class DoctorState
{
    public List<DoctorAction> Actions { get; set; } = new();
    public List<DoctorEvent> Outbox { get; set; } = new();
    public string LastLevel { get; set; } = "";
    public int BothMissing { get; set; }
    // A switch waits for the idle Bridge: "rollback" or "restart" (launcher was not running), since SwitchUtc.
    public string? Switch { get; set; }
    public DateTime? SwitchUtc { get; set; }
    public int? SwitchPid { get; set; }          // the tray Bridge when the switch began
    public int IdleRounds { get; set; }          // rounds in a row the switching Bridge was seen idle
}

public sealed record DoctorAction(DateTime Utc, string Id, bool Auto);
public sealed record DoctorEvent(DateTime Utc, string Kind, string Detail);

public sealed class BridgeDoctor
{
    private readonly BridgeLayout _l;
    private readonly IDoctorProbe _p;
    private readonly string _statePath;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // one round or repair at a time (loop and buttons)
    private DoctorState _st;

    public BridgeLayout Layout => _l;
    public DoctorReport? Last { get; private set; }
    // db.php host_event sender (kind, detail); null = no reporting.
    public Func<string, string, CancellationToken, Task>? Send { get; set; }

    // Limits of automatic repairs in 24 h.
    public int MaxAutoLauncherStarts { get; init; } = 3;
    public int MaxAutoRestarts { get; init; } = 2;
    public int StaleHeartbeatMinutes { get; init; } = 15;

    public BridgeDoctor(BridgeLayout layout, IDoctorProbe probe, string statePath, Func<DateTime>? now = null, Action<string>? log = null)
    {
        _l = layout;
        _p = probe;
        _statePath = statePath;
        _now = now ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
        _st = LoadState(statePath);
    }

    public DoctorState State { get { lock (_sync) return _st; } }

    // ------------------------------------------------------------------ checks

    public async Task<DoctorReport> CheckAsync(CancellationToken ct)
    {
        DateTime now = _now();
        var r = new DoctorReport { Utc = now };
        bool drive = Safe(() => _l.DriveRoot.Length > 0 && Directory.Exists(_l.DriveRoot));
        r.Checks.Add(drive
            ? new DoctorCheck("drive", DoctorLevel.Ok, "Google Drive is mounted", _l.DriveRoot)
            : new DoctorCheck("drive", DoctorLevel.Fail, "Google Drive is not mounted", (_l.DriveRoot.Length > 0 ? _l.DriveRoot : "driveRoot not set") + ": Bridge waits for it; start Google Drive for desktop"));
        if (!File.Exists(_l.ConfigPath))
            r.Checks.Add(new DoctorCheck("config", DoctorLevel.Fail, "Bridge config.json is missing", _l.ConfigPath));

        r.Owner = drive ? ReadOwner() : null;
        var launchers = Launchers();
        var tray = TrayBridge(r.Owner, strict: false);
        var bridges = tray != null ? new List<ProcInfo> { tray } : new List<ProcInfo>();
        string lastLauncher = LastLines(_l.LauncherLog, 1).FirstOrDefault() ?? "";
        if (launchers.Count == 0 && bridges.Count == 0)
            r.Checks.Add(lastLauncher.Contains("closed by the user", StringComparison.Ordinal)
                // Exit from the tray menu: the owner's choice, never undone automatically.
                ? new DoctorCheck("process", DoctorLevel.Warn, "Bridge was closed from its tray menu", Cut(lastLauncher, 160), DoctorFix.StartLauncher)
                : new DoctorCheck("process", DoctorLevel.Fail, "Bridge is not running", "no launcher, no BridgeAgent", DoctorFix.StartLauncher));
        else if (bridges.Count == 0)
            r.Checks.Add(new DoctorCheck("process", DoctorLevel.Warn, "The launcher runs, Bridge does not", Cut(lastLauncher, 200)));
        else if (launchers.Count == 0)
            r.Checks.Add(new DoctorCheck("process", DoctorLevel.Warn, "Bridge runs without its launcher", $"BridgeAgent pid {tray!.Pid}; the Bridge supervisor restarts the launcher", DoctorFix.StartLauncher));
        else
            r.Checks.Add(new DoctorCheck("process", DoctorLevel.Ok, "Bridge is running", $"BridgeAgent pid {tray!.Pid}, launcher pid {launchers[0].Pid}"));

        // Heartbeat
        if (drive)
        {
            if (r.Owner == null)
                r.Checks.Add(new DoctorCheck("heartbeat", DoctorLevel.Warn, "owner.json is unreadable", _l.OwnerJson));
            else
            {
                double age = (now - r.Owner.UpdatedUtc).TotalMinutes;
                string d = $"written {Ago(age)} ago by Bridge {r.Owner.Version} ({r.Owner.State}, {r.Owner.RunningJobs} job(s))"
                    + (r.Owner.Machine.Length > 0 && !r.Owner.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? ", machine " + r.Owner.Machine : "");
                if (bridges.Count == 0)
                    r.Checks.Add(new DoctorCheck("heartbeat", DoctorLevel.Info, "Last Bridge heartbeat", d));
                else if (age <= 3)
                    r.Checks.Add(new DoctorCheck("heartbeat", DoctorLevel.Ok, "Bridge heartbeat is fresh", d));
                else if (age <= StaleHeartbeatMinutes)
                    r.Checks.Add(new DoctorCheck("heartbeat", DoctorLevel.Warn, "Bridge heartbeat is late", d));
                else
                    r.Checks.Add(new DoctorCheck("heartbeat", DoctorLevel.Fail, "Bridge hangs (no heartbeat)", d,
                        RestartRequestedLongAgo(now) ? DoctorFix.HardRestart : DoctorFix.Restart));
            }
        }

        CheckLog(r, now, bridges.Count > 0);
        CheckVersion(r, now);
        CheckDrain(r, now);
        CheckDefender(r, now);
        if (drive) CheckQueue(r, now, bridges.Count > 0 && r.Owner?.State == "running");
        await CheckSiteAsync(r, ct).ConfigureAwait(false);
        CheckCli(r);
        lock (_sync) Last = r;
        return r;
    }

    private void CheckLog(DoctorReport r, DateTime now, bool bridgeRunning)
    {
        FileInfo? log = null;
        try
        {
            log = new DirectoryInfo(_l.LogsDir).EnumerateFiles("*.log")
                .Where(f => Regex.IsMatch(f.Name, @"^\d{4}-\d{2}-\d{2}\.log$")).OrderByDescending(f => f.Name).FirstOrDefault();
        }
        catch (Exception) { }
        if (log == null) { r.Checks.Add(new DoctorCheck("log", DoctorLevel.Warn, "No Bridge log", _l.LogsDir)); return; }
        double age = (now - log.LastWriteTimeUtc).TotalMinutes;
        int errors = 0;
        string lastError = "";
        foreach (string line in LastLines(log.FullName, 600))
        {
            if (!TryLineUtc(line, out DateTime t) || (now - t).TotalMinutes > 30) continue;
            string text = line.Replace("\"failed\":0", "", StringComparison.Ordinal);   // push results {"failed":0}
            if (Regex.IsMatch(text, @"\b(fail(ed|ure)?|error|exception|fatal)\b", RegexOptions.IgnoreCase)) { errors++; lastError = line; }
        }
        string detail = $"{log.Name}, last line {Ago(age)} ago" + (errors > 0 ? $"; {errors} error line(s) in 30 min, last: {Cut(lastError, 220)}" : "");
        DoctorLevel level = bridgeRunning && age > StaleHeartbeatMinutes ? DoctorLevel.Warn
            : errors >= 20 ? DoctorLevel.Warn : errors > 0 ? DoctorLevel.Info : DoctorLevel.Ok;
        string title = level == DoctorLevel.Warn && age > StaleHeartbeatMinutes ? "Bridge log is silent" : errors >= 20 ? "Many errors in the Bridge log" : "Bridge log";
        r.Checks.Add(new DoctorCheck("log", level, title, detail));
    }

    private void CheckVersion(DoctorReport r, DateTime now)
    {
        var st = ReadLauncherState();
        if (st == null) { r.Checks.Add(new DoctorCheck("version", DoctorLevel.Warn, "Launcher state.json is unreadable", _l.LauncherStatePath)); return; }
        string cur = Str(st, "current") ?? "", prev = Str(st, "previous") ?? "", pend = Str(st, "pending") ?? "";
        var bad = (st["bad"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        if (cur.Length == 0)
            r.Checks.Add(new DoctorCheck("version", DoctorLevel.Fail, "No Bridge version installed", "state.json has no current version"));
        else if (!File.Exists(_l.BridgeExe(cur)))
            r.Checks.Add(new DoctorCheck("version", DoctorLevel.Fail, $"BridgeAgent.exe {cur} is missing",
                "the file is gone (Defender quarantine?); the launcher rolls back when it restarts Bridge",
                prev.Length > 0 && File.Exists(_l.BridgeExe(prev)) ? DoctorFix.Rollback : null));
        else
            r.Checks.Add(new DoctorCheck("version", bad.Contains(cur) ? DoctorLevel.Warn : DoctorLevel.Ok, $"Bridge {cur}",
                (prev.Length > 0 ? "previous " + prev : "no previous version") + (pend.Length > 0 ? ", update " + pend + " waits" : "")
                + (bad.Count > 0 ? ", bad: " + string.Join(" ", bad) : "")));
        var marked = LastLines(_l.LauncherLog, 200).Where(x => TryLineUtc(x, out DateTime t) && (now - t).TotalHours <= 24
            && (x.Contains("marked bad", StringComparison.Ordinal) || x.Contains("Launcher fatal", StringComparison.Ordinal))).ToList();
        if (marked.Count > 0)
            r.Checks.Add(new DoctorCheck("launcher", DoctorLevel.Warn, "The launcher rolled back or failed in 24 h", Cut(marked[^1], 220)));
    }

    private void CheckDrain(DoctorReport r, DateTime now)
    {
        if (!File.Exists(_l.DrainPath)) return;
        double age = Safe(() => (now - File.GetLastWriteTimeUtc(_l.DrainPath)).TotalMinutes);
        string text = Safe(() => File.ReadAllText(_l.DrainPath).Trim());
        r.Checks.Add(new DoctorCheck("drain", age > 30 ? DoctorLevel.Warn : DoctorLevel.Info,
            "Bridge waits for its jobs to finish before a restart", $"drain.request \"{Cut(text, 80)}\" since {Ago(age)}"));
    }

    private void CheckDefender(DoctorReport r, DateTime now)
    {
        IReadOnlyList<ThreatInfo> list;
        try { list = _p.Detections(); }
        catch (Exception ex) { r.Checks.Add(new DoctorCheck("defender", DoctorLevel.Info, "Defender state unavailable", Cut(ex.Message, 120))); return; }
        var hits = list.Where(t => (now - t.Utc).TotalDays <= 14 && IsOurs(t.Resources)).OrderByDescending(t => t.Utc).ToList();
        if (hits.Count == 0) { r.Checks.Add(new DoctorCheck("defender", DoctorLevel.Ok, "Defender: no detections of Bridge or CO Desktop", "")); return; }
        // A detection older than 2 days is history (01.11: the exe was replaced by a new version); it stays visible as info.
        bool fresh = (now - hits[0].Utc).TotalDays <= 2;
        r.Checks.Add(new DoctorCheck("defender", fresh ? DoctorLevel.Fail : DoctorLevel.Info,
            fresh ? "Defender quarantined a Bridge/CO file" : "Defender detected a Bridge/CO file earlier",
            $"{hits[0].Utc:yyyy-MM-dd HH:mm} UTC {Cut(hits[0].Resources, 200)}; as administrator: Add-MpPreference -ExclusionPath '{_l.VersionsDir}'"));
    }

    private bool IsOurs(string resources) =>
        resources.Contains("AIWorkbenchBridge", StringComparison.OrdinalIgnoreCase)
        || resources.Contains(@"\CO Desktop\", StringComparison.OrdinalIgnoreCase)
        || resources.Contains("CO.Desktop.exe", StringComparison.OrdinalIgnoreCase);

    private void CheckQueue(DoctorReport r, DateTime now, bool taking)
    {
        int running = 0;
        try
        {
            string claimed = Path.Combine(_l.HostDir, "claimed");
            if (Directory.Exists(claimed))
                foreach (var dir in new DirectoryInfo(claimed).EnumerateDirectories())
                {
                    if (File.Exists(Path.Combine(dir.FullName, "result.ready"))) continue;
                    running++;
                    // request.json keeps the time the job was queued; the job's event batches show when it started
                    // and whether it still writes. Stuck: started over the timeout + 30 min ago and silent for 30 min.
                    var (start, last) = Activity(dir);
                    if ((now - start).TotalMinutes > _l.MaxProcessMinutes + 30 && (now - last).TotalMinutes > 30) r.StuckJobs.Add(dir.Name);
                }
        }
        catch (Exception ex) { r.Checks.Add(new DoctorCheck("queue", DoctorLevel.Info, "Queue unreadable", Cut(ex.Message, 120))); return; }
        if (r.StuckJobs.Count > 0)
            r.Checks.Add(new DoctorCheck("stuck", DoctorLevel.Warn, $"{r.StuckJobs.Count} job(s) stuck in claimed",
                $"started over {_l.MaxProcessMinutes + 30} min ago, no events for 30 min, no result: {string.Join(", ", r.StuckJobs.Take(5))}", DoctorFix.CancelStuck));
        int waiting = 0;
        try
        {
            string inbox = Path.Combine(_l.HostDir, "inbox");
            if (Directory.Exists(inbox))
                waiting = new DirectoryInfo(inbox).EnumerateDirectories()
                    .Count(d => File.Exists(Path.Combine(d.FullName, "request.ready"))
                        && (now - File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "request.ready"))).TotalMinutes > 10);
        }
        catch (Exception) { }
        r.Checks.Add(new DoctorCheck("queue", taking && waiting > 0 ? DoctorLevel.Warn : DoctorLevel.Ok,
            waiting > 0 ? $"{waiting} job(s) wait in the Drive inbox over 10 min" : "Queue",
            $"{running} job(s) in claimed without a result"));
    }

    private (DateTime Start, DateTime Last) Activity(DirectoryInfo claimed)
    {
        var times = new List<DateTime>();
        string events = Path.Combine(_l.HostDir, "events", claimed.Name);
        if (Directory.Exists(events))
            times.AddRange(new DirectoryInfo(events).EnumerateFiles("*.json").Select(f => f.LastWriteTimeUtc));
        times.AddRange(claimed.EnumerateFiles().Where(f => !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).Select(f => f.LastWriteTimeUtc));
        if (times.Count == 0) return (claimed.CreationTimeUtc, claimed.CreationTimeUtc);
        var batches = Directory.Exists(events) ? new DirectoryInfo(events).EnumerateFiles("*.json").Select(f => f.LastWriteTimeUtc).ToList() : new List<DateTime>();
        return (batches.Count > 0 ? batches.Min() : times.Max(), times.Max());
    }

    private async Task CheckSiteAsync(DoctorReport r, CancellationToken ct)
    {
        string url = SiteOrigin();
        if (url.Length == 0) return;
        bool up;
        try { up = await _p.SiteUpAsync(url, ct).ConfigureAwait(false); }
        catch (Exception) { up = false; }
        r.Checks.Add(up ? new DoctorCheck("site", DoctorLevel.Ok, "Site is reachable", url)
            : new DoctorCheck("site", DoctorLevel.Warn, "Site is unreachable", url + "; reports wait until it is back"));
    }

    private void CheckCli(DoctorReport r)
    {
        foreach (var (name, configured) in new[] { ("claude", _l.ClaudeExe), ("codex", _l.CodexExe) })
        {
            string? path = ResolveCli(name, configured);
            r.Checks.Add(path != null ? new DoctorCheck("cli_" + name, DoctorLevel.Ok, name + " CLI", path)
                : new DoctorCheck("cli_" + name, DoctorLevel.Warn, name + " CLI not found", configured.Length > 0 ? configured : "not in PATH"));
        }
    }

    public string? ResolveCli(string name, string configured)
    {
        if (configured.Length > 0 && Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        return _p.FindOnPath(configured.Length > 0 ? configured : name + ".exe");
    }

    public string? ResolveCli(string name) => ResolveCli(name, name == "claude" ? _l.ClaudeExe : name == "codex" ? _l.CodexExe : "");

    public string SiteOrigin()
    {
        if (!Uri.TryCreate(_l.DbUrl, UriKind.Absolute, out var u)) return "";
        return u.GetLeftPart(UriPartial.Authority) + "/";
    }

    // ------------------------------------------------------------------ repairs

    public async Task<string> RepairAsync(string id, bool auto, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (id == DoctorFix.CancelStuck) await CheckAsync(ct).ConfigureAwait(false);   // act on fresh data only
            string outcome = Repair(id, auto);
            await FlushAsync(ct).ConfigureAwait(false);
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private string Repair(string id, bool auto)
    {
        string outcome = id switch
        {
            DoctorFix.StartLauncher => StartLauncher(),
            DoctorFix.Restart => Restart(),
            DoctorFix.Rollback => Rollback(),
            DoctorFix.CancelStuck => CancelStuck(),
            DoctorFix.HardRestart => HardRestart(),
            _ => throw new ArgumentException("unknown repair " + id),
        };
        lock (_sync)
        {
            _st.Actions.Add(new DoctorAction(_now(), id, auto));
            _st.Actions.RemoveAll(a => (_now() - a.Utc).TotalDays > 7);
        }
        _log($"Doctor: {(auto ? "auto " : "")}{id}: {outcome}");
        Enqueue("doctor_repair", (auto ? "auto " : "") + id + ": " + outcome);
        SaveState();
        return outcome;
    }

    // Automatic launcher start within the daily limit; false when the limit is reached.
    private bool AutoStartLauncher(DateTime now)
    {
        if (Count(DoctorFix.StartLauncher, now) >= MaxAutoLauncherStarts)
        {
            _log("Doctor: launcher start limit reached");
            return false;
        }
        Repair(DoctorFix.StartLauncher, true);
        return true;
    }

    private string StartLauncher()
    {
        if (Launchers().Count > 0) return "the launcher is already running";
        try { return _p.RunTask(BridgeLayout.AutostartTask); }
        catch (Exception ex)
        {
            if (!File.Exists(_l.LauncherExe)) throw new InvalidOperationException("task failed (" + ex.Message + ") and " + _l.LauncherExe + " is missing");
            _p.Start(_l.LauncherExe);
            return "task failed (" + Cut(ex.Message, 100) + "); launcher started directly";
        }
    }

    // Graceful: drain.request makes Bridge stop taking jobs and exit with code 3 once none runs; the launcher starts
    // it again. Without a launcher Bridge never exits on drain: the doctor ends the idle Bridge itself (Tick).
    private string Restart()
    {
        var tray = TrayBridge(ReadOwner(), strict: false);
        if (tray == null) return StartLauncher();
        WriteDrain();
        if (Launchers().Count > 0) return "drain requested; Bridge restarts when its running jobs finish";
        SetSwitch("restart", tray.Pid);
        return "drain requested; the launcher is not running, the doctor restarts Bridge once it is idle";
    }

    private string Rollback()
    {
        var st = ReadLauncherState() ?? throw new InvalidOperationException("state.json is unreadable");
        string cur = Str(st, "current") ?? "", prev = Str(st, "previous") ?? "";
        if (prev.Length == 0) throw new InvalidOperationException("there is no previous Bridge version to roll back to");
        if (!File.Exists(_l.BridgeExe(prev))) throw new InvalidOperationException("BridgeAgent.exe " + prev + " is missing too");
        // The launcher keeps state.json in memory: stop it first, then the new state counts at its next start.
        foreach (var p in Launchers()) _p.Kill(p.Pid);
        var bad = st["bad"] as JsonArray ?? new JsonArray();
        if (cur.Length > 0 && !bad.Any(x => x?.ToString() == cur)) bad.Add(cur);
        st["bad"] = bad;
        st["current"] = prev;
        st["previous"] = null;
        st["pending"] = null;
        WriteAtomic(_l.LauncherStatePath, st.ToJsonString());
        var tray = TrayBridge(ReadOwner(), strict: false);
        if (tray == null)
            return $"{cur} marked bad, back to {prev}; " + StartLauncher();
        WriteDrain();
        SetSwitch("rollback", tray.Pid);
        return $"{cur} marked bad, back to {prev}; Bridge switches when its running jobs finish";
    }

    private string CancelStuck()
    {
        var stuck = Last?.StuckJobs ?? new List<string>();
        if (stuck.Count == 0) return "no stuck jobs";
        string dir = Path.Combine(_l.HostDir, "cancel");
        Directory.CreateDirectory(dir);
        foreach (string job in stuck)
        {
            if (!Regex.IsMatch(job, @"^[A-Za-z0-9_.-]{1,120}$")) continue;
            File.WriteAllText(Path.Combine(dir, job + ".cancel"), $"doctor {_now():o}");
        }
        return $"cancel markers for {string.Join(", ", stuck)}; jobs that Bridge no longer runs are closed at its next restart";
    }

    private string HardRestart()
    {
        var tray = TrayBridge(ReadOwner(), strict: false);
        if (tray == null) return StartLauncher();
        // The launcher treats an early death after a version switch as a crash and marks the new version bad.
        var switched = LastLines(_l.LauncherLog, 20).LastOrDefault(x => x.Contains("Switched to Bridge", StringComparison.Ordinal));
        if (switched != null && TryLineUtc(switched, out DateTime t) && (_now() - t).TotalSeconds < 150)
            throw new InvalidOperationException("Bridge switched versions less than 2.5 min ago (launcher probation); try again in a few minutes.");
        _p.Kill(tray.Pid);   // the tray Bridge only; per-job proxies end with its jobs
        if (Launchers().Count == 0) return "Bridge killed; " + StartLauncher();
        return "Bridge killed; the launcher starts it again in about 10 s";
    }

    // ------------------------------------------------------------------ periodic tick

    // One doctor round (every few minutes while CO Desktop runs): checks, automatic safe repairs, the pending switch,
    // events for level changes, and sending of queued events.
    public async Task<DoctorReport> TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await TickCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<DoctorReport> TickCoreAsync(CancellationToken ct)
    {
        var r = await CheckAsync(ct).ConfigureAwait(false);
        DateTime now = _now();
        try { AutoRepair(r, now); }
        catch (Exception ex) { _log("Doctor: auto repair failed: " + ex.Message); Enqueue("doctor_repair", "auto repair failed: " + Cut(ex.Message, 200)); }

        string level = r.Level.ToString();
        bool changed;
        lock (_sync) { changed = _st.LastLevel != level; _st.LastLevel = level; }
        if (changed && (r.Level >= DoctorLevel.Warn || level == "Ok" || level == "Info"))
            Enqueue(r.Level >= DoctorLevel.Fail ? "doctor_fail" : r.Level == DoctorLevel.Warn ? "doctor_warn" : "doctor_ok", r.Summary);
        SaveState();
        await FlushAsync(ct).ConfigureAwait(false);
        return r;
    }

    private void AutoRepair(DoctorReport r, DateTime now)
    {
        ContinueSwitch(r, now);
        var process = r.Find("process");
        int both;
        lock (_sync) both = _st.BothMissing = process?.Level == DoctorLevel.Fail ? _st.BothMissing + 1 : 0;
        // Both gone on two rounds in a row (the launcher restarts a crashed Bridge within seconds).
        if (both >= 2)
        {
            lock (_sync) _st.BothMissing = 0;
            AutoStartLauncher(now);
            return;
        }
        // Hung Bridge: a drain is the safe step; killing it stays with the owner.
        if (r.Find("heartbeat") is { Level: DoctorLevel.Fail, Fix: DoctorFix.Restart } && !File.Exists(_l.DrainPath)
            && Count(DoctorFix.Restart, now) < MaxAutoRestarts && !RecentAction(DoctorFix.Restart, now, 30))
            Repair(DoctorFix.Restart, true);
    }

    // A rollback or a restart without a launcher waits until Bridge has no running job (two rounds in a row by the
    // owner.json of this machine and its process), then ends the idle Bridge and starts the launcher. The Bridge
    // supervisor may start the launcher first: then Bridge hands itself over when idle.
    private void ContinueSwitch(DoctorReport r, DateTime now)
    {
        string? sw;
        DateTime since;
        int? pid;
        lock (_sync) { sw = _st.Switch; since = _st.SwitchUtc ?? now; pid = _st.SwitchPid; }
        if (sw == null) return;
        var o = r.Owner;
        var tray = TrayBridge(o, strict: false);
        bool launcher = Launchers().Count > 0;
        string cur = Str(ReadLauncherState() ?? new JsonObject(), "current") ?? "";
        bool done = launcher && tray != null && (sw == "rollback"
            ? tray.Path.Equals(_l.BridgeExe(cur), StringComparison.OrdinalIgnoreCase)
            : tray.Pid != pid);
        if (done)
        {
            ClearSwitch();
            Enqueue("doctor_repair", sw + " finished");
            return;
        }
        if ((now - since).TotalHours > 6)
        {
            // Never leave a launcher-less Bridge paused by the doctor's drain.
            if (Safe(() => File.ReadAllText(_l.DrainPath)) is string d && d.StartsWith("doctor", StringComparison.Ordinal))
                Safe(() => { File.Delete(_l.DrainPath); return true; });
            if (!launcher) AutoStartLauncher(now);
            ClearSwitch();
            Enqueue("doctor_repair", sw + " given up after 6 h; drain withdrawn");
            return;
        }
        if (launcher) return;
        if (tray == null)
        {
            AutoStartLauncher(now);
            return;
        }
        var own = TrayBridge(o, strict: true);
        // Idle = no job and no relay call of a site job (a call may wait for a person up to 40 min); a Bridge that does
        // not report its relay calls is never idle here - its own drain exit or the owner ends it.
        bool idle = own != null && o != null && (now - o.UpdatedUtc).TotalMinutes <= 2 && o.RunningJobs == 0 && o.RelayActive == 0
            && o.State != "running";
        int rounds;
        lock (_sync) rounds = _st.IdleRounds = idle ? _st.IdleRounds + 1 : 0;
        if (rounds < 2) return;
        lock (_sync) _st.IdleRounds = 0;
        _p.Kill(own!.Pid);
        _log($"Doctor: {sw}: idle Bridge pid {own.Pid} ended");
        Enqueue("doctor_repair", $"{sw}: idle Bridge pid {own.Pid} ended");
        AutoStartLauncher(now);
    }

    // ------------------------------------------------------------------ events

    public void Enqueue(string kind, string detail)
    {
        lock (_sync)
        {
            _st.Outbox.Add(new DoctorEvent(_now(), kind, Cut(detail, 450)));
            if (_st.Outbox.Count > 50) _st.Outbox.RemoveRange(0, _st.Outbox.Count - 50);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (Send == null) return;
        while (true)
        {
            DoctorEvent e;
            lock (_sync)
            {
                if (_st.Outbox.Count == 0) break;
                e = _st.Outbox[0];
            }
            string detail = e.Detail;
            if ((_now() - e.Utc).TotalMinutes > 2) detail = $"[{e.Utc:yyyy-MM-dd HH:mm} UTC] " + detail;
            try { await Send(e.Kind, detail, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log("Doctor: report not sent (" + Cut(ex.Message, 120) + "), kept"); break; }
            lock (_sync) _st.Outbox.Remove(e);
        }
        SaveState();
    }

    // db.php op host_event with the push key of Bridge (Credential Manager of this Windows user).
    // Own client without redirects: the push key header must reach only the configured db.php.
    private static readonly HttpClient EventHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public static Func<string, string, CancellationToken, Task> HostEventSender(BridgeLayout l, Func<string, string?> readSecret) =>
        async (kind, detail, ct) =>
        {
            if (l.DbUrl.Length == 0) throw new InvalidOperationException("no dbQueue.url in config.json");
            string key = (readSecret(l.PushTarget) ?? "").Replace("\0", "").Trim();
            if (key.Length == 0) throw new InvalidOperationException("no push key in Credential Manager (" + l.PushTarget + ")");
            var body = new JsonObject { ["op"] = "host_event", ["host"] = l.HostId, ["kind"] = kind, ["detail"] = detail };
            using var req = new HttpRequestMessage(HttpMethod.Post, l.DbUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-CO-Push-Key", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await EventHttp.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)resp.StatusCode);
        };

    // ------------------------------------------------------------------ helpers

    // Only processes whose path is known and ours (another user's process has an unreadable path).
    public IReadOnlyList<ProcInfo> Launchers() =>
        _p.Processes("AIWorkbenchBridge").Where(p => p.Path.Equals(_l.LauncherExe, StringComparison.OrdinalIgnoreCase)).ToList();

    // BridgeAgent.exe of an installed version: the tray Bridge, also its per-job device proxies (--mcp-device) and the
    // launcher's --validate-config runs.
    private IReadOnlyList<ProcInfo> BridgeExes() =>
        _p.Processes("BridgeAgent").Where(p => p.Path.StartsWith(_l.VersionsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();

    // The tray Bridge: the process owner.json of this machine names; otherwise (strict = false) the oldest BridgeAgent,
    // since proxies start later inside its jobs. Automatic kills use strict only.
    public ProcInfo? TrayBridge(OwnerInfo? o, bool strict)
    {
        var list = BridgeExes();
        if (o != null && o.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && list.FirstOrDefault(p => p.Pid == o.ProcessId) is { } named)
            return named;
        return strict ? null : list.OrderBy(p => p.StartUtc).FirstOrDefault();
    }

    private OwnerInfo? ReadOwner()
    {
        if (_l.DriveRoot.Length == 0) return null;
        try
        {
            using var d = BridgeLayout.Parse(_l.OwnerJson);
            var e = d.RootElement;
            DateTime upd = DateTime.Parse(BridgeLayout.Str(e, "updatedUtc") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            int pid = e.TryGetProperty("processId", out var p) && p.TryGetInt32(out int pp) ? pp : 0;
            int jobs = e.TryGetProperty("runningJobs", out var j) && j.TryGetInt32(out int jj) ? jj : 0;
            int? relay = e.TryGetProperty("relayActive", out var ra) && ra.TryGetInt32(out int rr) ? rr : null;
            return new OwnerInfo(upd, pid, BridgeLayout.Str(e, "bridgeVersion") ?? "", BridgeLayout.Str(e, "state") ?? "", jobs, BridgeLayout.Str(e, "machineName") ?? "", relay);
        }
        catch (Exception) { return null; }
    }

    private JsonObject? ReadLauncherState()
    {
        try { return JsonNode.Parse(File.ReadAllText(_l.LauncherStatePath)) as JsonObject; }
        catch (Exception) { return null; }
    }

    private void WriteDrain() => File.WriteAllText(_l.DrainPath, "doctor " + _now().ToString("o", CultureInfo.InvariantCulture));

    private void SetSwitch(string kind, int pid) { lock (_sync) { _st.Switch = kind; _st.SwitchUtc = _now(); _st.SwitchPid = pid; _st.IdleRounds = 0; } SaveState(); }
    private void ClearSwitch() { lock (_sync) { _st.Switch = null; _st.SwitchUtc = null; _st.SwitchPid = null; _st.IdleRounds = 0; } SaveState(); }

    private bool RestartRequestedLongAgo(DateTime now)
    {
        lock (_sync)
            return _st.Actions.Any(a => a.Id == DoctorFix.Restart && (now - a.Utc).TotalMinutes >= 20 && (now - a.Utc).TotalHours <= 3);
    }

    private int Count(string id, DateTime now) { lock (_sync) return _st.Actions.Count(a => a.Auto && a.Id == id && (now - a.Utc).TotalHours <= 24); }
    private bool RecentAction(string id, DateTime now, int minutes) { lock (_sync) return _st.Actions.Any(a => a.Id == id && (now - a.Utc).TotalMinutes < minutes); }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool TryLineUtc(string line, out DateTime t)
    {
        int bar = line.IndexOf(" | ", StringComparison.Ordinal);
        t = default;
        return bar > 0 && DateTime.TryParse(line[..bar], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);
    }

    // Last lines of a log file that Bridge keeps open (shared read).
    public static List<string> LastLines(string path, int n)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 256 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var lines = sr.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
            return lines.Count > n ? lines.GetRange(lines.Count - n, n) : lines;
        }
        catch (Exception) { return new List<string>(); }
    }

    private static string Ago(double minutes) =>
        minutes < 1 ? $"{(int)(minutes * 60)} s" : minutes < 120 ? $"{(int)minutes} min" : $"{minutes / 60:0.#} h";

    internal static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static T Safe<T>(Func<T> f) { try { return f(); } catch (Exception) { return default!; } }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".doctor.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static DoctorState LoadState(string path)
    {
        try { return JsonSerializer.Deserialize<DoctorState>(File.ReadAllText(path)) ?? new DoctorState(); }
        catch (Exception) { return new DoctorState(); }
    }

    private void SaveState()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_st, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            WriteAtomic(_statePath, json);
        }
        catch (Exception ex) { _log("Doctor: state not saved: " + ex.Message); }
    }
}
