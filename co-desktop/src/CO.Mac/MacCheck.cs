// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CO.Core;
using CO.Core.Crypto;
using CO.Core.Policy;
using CO.Core.Protocol;
using CO.Core.Tools;
using CO.Core.Transport;

namespace CO.Mac;

// Mac check (SPEC R2): CO.Core.Tests of this very release on this Mac. publish_mac.py builds the test bundle
// (co-mac-check-<version>-osx-arm64.tar.gz, self-contained) before the resident and compiles its sha256 into the resident
// (assembly metadata MacCheckSha256), so the resident runs only the bundle of its own build: the trust comes from the
// Bridge-signed update or the install link, not from the site. The run takes minutes (a device call has 120 s), so
// action=start returns at once and action=status reads the result. The tests run detached with HOME and TMPDIR in a
// throwaway folder; they never see the user's files or the resident's data.
internal sealed class MacCheckTool : IDeviceTool
{
    public static string DownloadUrl => InstallDefaults.SiteUrl + "/agent/mac.php?op=check&v="; // Freedom F1
    private const long MaxSize = 200L * 1024 * 1024;
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(20);

    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly object _sync = new();
    private Task? _job;

    public MacCheckTool(HttpClient http, IAccessTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public static string Root => Path.Combine(Paths.Home, "Library", "Caches", "CO", "mac-check");
    private static string StateFile => Path.Combine(Root, "state.json");
    private static string ResultFile => Path.Combine(Root, "result.json");

    public static string ExpectedSha256 =>
        typeof(MacCheckTool).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MacCheckSha256")?.Value?.Trim().ToLowerInvariant() ?? "";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "mac_check", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Run the CO.Core test suite of this CO Resident release on this Mac (SPEC R2 'Mac check'). action=start downloads the " +
                      "test bundle of this exact build (~40 MB, checked against the sha256 compiled into the resident) and starts it in the " +
                      "background, isolated from the user's files; returns at once. action=status (default): state (idle, downloading, " +
                      "running, done, failed, interrupted), version, start/finish times and, when done, the JSON summary (total, passed, " +
                      "failed, skipped, seconds, failures with messages). A run takes 2-5 minutes; poll status about once a minute. No confirmation. " +
                      "live=true (with start) also runs the live browser tests in a headless Chromium of this Mac (the agent browser's " +
                      "Chrome for Testing, else Chrome/Edge); the owner's rule: live browser tests run on the device that needs them, " +
                      "not on the owner's home PC.",
        InputSchema = Schema.Obj(("action", "string", "start | status", false),
            ("live", "boolean", "With action=start: also run the live browser tests (headless, own temp profile), default false", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) =>
        new() { Tool = "mac_check", Class = RiskClass.R, Summary = (Args.OptStr(args, "action") ?? "status") + " Mac check" };

    public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string action = Args.OptStr(args, "action") ?? "status";
        if (action == "status") return Task.FromResult(new ToolOutput { Content = Status() });
        if (action != "start") throw new ToolException("invalid_args", "action: start or status.");
        string sha = ExpectedSha256;
        if (sha.Length != 64) throw new ToolException("not_available", "This CO Resident build has no Mac check bundle (built without publish_mac.py).");
        // 0.3.7 (owner 10.11): live browser tests run here, on the Mac, not on the owner's PC; same Chromium as the agent browser.
        string? liveExe = null;
        if (Args.OptBool(args, "live", false))
            liveExe = MacBrowser.FindOrInstall(_http) ?? throw new ToolException("not_ready",
                "The agent browser (Chrome for Testing) is being installed on this Mac; start the live check again in a few minutes.");
        lock (_sync)
        {
            var cur = ReadState();
            bool busy = _job is { IsCompleted: false } || (cur is { State: "downloading" or "running" } && RunnerAlive(cur));
            if (busy)
                return Task.FromResult(new ToolOutput { Content = "already running\n" + Status() });
            Directory.CreateDirectory(Root);
            try { File.Delete(ResultFile); } catch (IOException) { }
            WriteState(new CheckState { State = "downloading", Version = Program.Version, StartedUtc = DateTime.UtcNow, Live = liveExe != null });
            _job = Task.Run(() => RunAsync(sha, liveExe));
        }
        return Task.FromResult(new ToolOutput { Content = "started\n" + Status() });
    }

    private async Task RunAsync(string sha, string? liveExe)
    {
        string ver = Program.Version;
        try
        {
            string dir = Path.Combine(Root, ver);
            string marker = Path.Combine(dir, ".sha256");
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != sha)
                await InstallAsync(ver, sha, dir);
            foreach (string old in Directory.GetDirectories(Root))
                if (Path.GetFileName(old) != ver && !Path.GetFileName(old).StartsWith("run-"))
                    try { Directory.Delete(old, true); } catch (Exception ex) { Log.Write("mac check cleanup: " + ex.Message); }
            Start(dir, ver, liveExe);
        }
        catch (Exception ex)
        {
            Log.Write("mac check failed: " + ex.Message);
            Update(s => { s.State = "failed"; s.Error = ex.Message; s.FinishedUtc = DateTime.UtcNow; });
        }
    }

    private async Task InstallAsync(string ver, string sha, string dir)
    {
        byte[] bin;
        using (var req = new HttpRequestMessage(HttpMethod.Get, DownloadUrl + ver))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(CancellationToken.None));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"test bundle download: HTTP {(int)resp.StatusCode}");
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(chunk, cts.Token)) > 0)
            {
                if (buf.Length + n > MaxSize) throw new InvalidOperationException("test bundle download: too large");
                buf.Write(chunk, 0, n);
            }
            bin = buf.ToArray();
        }
        if (Wire.Sha256Hex(bin) != sha) throw new InvalidOperationException("test bundle does not match the sha256 of this build");
        string tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        string rootFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        using (var gz = new GZipStream(new MemoryStream(bin), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            TarEntry? e;
            while ((e = tar.GetNextEntry()) != null)
            {
                string name = e.Name.StartsWith("./") ? e.Name[2..] : e.Name;
                if (name.Length == 0) continue;
                string target = Path.GetFullPath(Path.Combine(tmp, name));
                if (!target.StartsWith(rootFull, StringComparison.Ordinal) || Path.IsPathRooted(name))
                    throw new InvalidOperationException("unexpected path in the test bundle: " + e.Name);
                if (e.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidOperationException("unexpected entry in the test bundle: " + e.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
            }
        }
        string exe = Path.Combine(tmp, "co-mac-check");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(tmp, "CO.Core.Tests.dll")))
            throw new InvalidOperationException("test bundle lacks co-mac-check or CO.Core.Tests.dll");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Apple Silicon runs only signed Mach-O code; an ad hoc signature is enough for a program started from here.
        foreach (string f in Directory.GetFiles(tmp, "*.dylib", SearchOption.AllDirectories).Prepend(exe))
        {
            var (code, _, se) = Proc.Run("/usr/bin/codesign", new[] { "--force", "--sign", "-", f }, timeoutMs: 120_000);
            if (code != 0) throw new InvalidOperationException("codesign " + Path.GetFileName(f) + ": " + se.Trim());
        }
        File.WriteAllText(Path.Combine(tmp, ".sha256"), sha + "\n");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(tmp, dir);
        Log.Write($"mac check bundle {ver} installed ({bin.Length} bytes)");
    }

    // Detached: survives a restart of the resident (an update swap); the runner writes result.json itself.
    private void Start(string dir, string ver, string? liveExe)
    {
        string run = Path.Combine(Root, "run-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(run, "tmp"));
        var psi = new ProcessStartInfo(Path.Combine(dir, "co-mac-check"))
        {
            UseShellExecute = false, WorkingDirectory = run,
            RedirectStandardInput = true, RedirectStandardOutput = false, RedirectStandardError = false,
        };
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(ResultFile);
        psi.Environment["HOME"] = run;
        psi.Environment["TMPDIR"] = Path.Combine(run, "tmp") + "/";
        psi.Environment["CO_MAC_CHECK"] = "1";
        if (liveExe != null)
        {
            psi.Environment["CO_LIVE_BROWSER"] = "1";
            psi.Environment["CO_LIVE_BROWSER_EXE"] = liveExe;
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start co-mac-check");
        p.StandardInput.Close();
        int pid = p.Id;
        Update(s => { s.State = "running"; s.Pid = pid; s.RunDir = run; });
        Log.Write($"mac check {ver} running (pid {pid})");
        if (!p.WaitForExit((int)RunLimit.TotalMilliseconds))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            Update(s => { s.State = "failed"; s.Error = $"no result within {RunLimit.TotalMinutes:0} minutes, stopped"; s.FinishedUtc = DateTime.UtcNow; });
        }
        else
        {
            int code = p.ExitCode;
            Update(s => { s.State = File.Exists(ResultFile) ? "done" : "failed"; s.ExitCode = code; s.FinishedUtc = DateTime.UtcNow;
                          if (!File.Exists(ResultFile)) s.Error = "the runner exited with code " + code + " without a result"; });
            Log.Write($"mac check {ver} finished, exit {code}");
        }
        try { Directory.Delete(run, true); } catch (Exception ex) { Log.Write("mac check cleanup: " + ex.Message); }
    }

    private static string Status()
    {
        var s = ReadState();
        if (s == null) return "state: idle (never run on this Mac)\nresident: " + Program.Version + (ExpectedSha256.Length == 64 ? "" : " (no Mac check bundle in this build)");
        if (s.State == "running" && !RunnerAlive(s) && !File.Exists(ResultFile)) s.State = "interrupted";
        if (s.State == "running" && File.Exists(ResultFile)) s.State = "done";
        var sb = new StringBuilder();
        sb.Append("state: ").Append(s.State).Append('\n');
        sb.Append("version: ").Append(s.Version).Append(" (resident now ").Append(Program.Version).Append(")\n");
        if (s.Live) sb.Append("live browser tests: yes (headless)\n");
        sb.Append("started: ").Append(s.StartedUtc.ToString("O")).Append('\n');
        if (s.FinishedUtc is DateTime f) sb.Append("finished: ").Append(f.ToString("O")).Append('\n');
        if (s.ExitCode is int c) sb.Append("exit code: ").Append(c).Append('\n');
        if (s.Error != null) sb.Append("error: ").Append(s.Error).Append('\n');
        if (File.Exists(ResultFile)) sb.Append("result: ").Append(File.ReadAllText(ResultFile).Trim()).Append('\n');
        return sb.ToString();
    }

    private static bool RunnerAlive(CheckState s)
    {
        if (s.State == "downloading") return DateTime.UtcNow - s.StartedUtc < TimeSpan.FromMinutes(15);
        if (s.Pid is not int pid) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && DateTime.UtcNow - s.StartedUtc < RunLimit + TimeSpan.FromMinutes(15); }
        catch (ArgumentException) { return false; }
    }

    private sealed class CheckState
    {
        public string State { get; set; } = "idle";
        public string Version { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public int? Pid { get; set; }
        public int? ExitCode { get; set; }
        public string? RunDir { get; set; }
        public string? Error { get; set; }
        public bool Live { get; set; }
    }

    private static CheckState? ReadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<CheckState>(File.ReadAllText(StateFile)) : null; }
        catch (Exception) { return null; }
    }

    private static void WriteState(CheckState s)
    {
        File.WriteAllText(StateFile + ".tmp", JsonSerializer.Serialize(s));
        File.Move(StateFile + ".tmp", StateFile, overwrite: true);
    }

    private void Update(Action<CheckState> change)
    {
        lock (_sync)
        {
            var s = ReadState() ?? new CheckState { Version = Program.Version, StartedUtc = DateTime.UtcNow };
            change(s);
            WriteState(s);
        }
    }
}
