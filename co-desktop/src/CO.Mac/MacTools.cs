// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Mac;

public static class MacTools
{
    public static IEnumerable<IDeviceTool> All(ShellSettings shell, HttpClient http, CO.Core.Transport.IAccessTokenSource tokens) => new IDeviceTool[]
    {
        new ShellRunTool(new ProcessShellRunner("/bin/zsh"), shell, Paths.Home),
        new SysInfoTool(shell),
        new LogReadTool(),
        new MacCheckTool(http, tokens),
        new SecretPromptTool(),   // 0.5.2: secrets in this Mac's own Keychain (MacSecrets.cs)
        new SecretListTool(),
        new SecretDeleteTool(),
    };
}

// Facts about the Mac for diagnostics; read-only, no confirmation.
internal sealed class SysInfoTool : IDeviceTool
{
    private readonly ShellSettings _shell;
    public SysInfoTool(ShellSettings shell) => _shell = shell;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "sys_info", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Facts about this Mac: macOS version, chip, memory, free disk, uptime, installed browsers and Google Drive, agent browser state, " +
                      "CO Resident version, connected folders and whether shell_run is on. Read-only, no confirmation.",
        InputSchema = Schema.Obj(),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) => new() { Tool = "sys_info", Class = RiskClass.R, Summary = "system facts" };

    public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        var sb = new StringBuilder();
        void Line(string name, string file, params string[] a)
        {
            try
            {
                var (code, so, se) = Proc.Run(file, a, timeoutMs: 10_000);
                sb.Append(name).Append(": ").Append((code == 0 ? so : "error " + se).Trim().Replace("\n", "\n  ")).Append('\n');
            }
            catch (Exception ex) { sb.Append(name).Append(": error ").Append(ex.Message).Append('\n'); }
        }
        sb.Append("CO Resident: ").Append(Program.Version).Append(" (core ").Append(CO.Core.Runtime.DeviceRuntime.Version).Append(")\n");
        Line("macOS", "/usr/bin/sw_vers");
        Line("arch", "/usr/bin/uname", "-m");
        Line("chip", "/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string");
        Line("memory bytes", "/usr/sbin/sysctl", "-n", "hw.memsize");
        Line("uptime", "/usr/bin/uptime");
        Line("disk (home)", "/bin/df", "-h", Paths.Home);
        foreach (var app in new[] { "Google Chrome", "Microsoft Edge", "Safari", "Google Drive", "Firefox" })
        {
            string plist = $"/Applications/{app}.app/Contents/Info.plist";
            if (!File.Exists(plist)) { sb.Append(app).Append(": not installed\n"); continue; }
            var (code, so, _) = Proc.Run("/usr/bin/defaults", new[] { "read", plist.Replace("/Info.plist", "/Info"), "CFBundleShortVersionString" }, timeoutMs: 5000);
            sb.Append(app).Append(": ").Append(code == 0 ? so.Trim() : "installed").Append('\n');
        }
        sb.Append("agent browser: ").Append(new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")).Mode)
          .Append(", ").Append(MacBrowser.Describe()).Append('\n');
        sb.Append("folders: ").Append(string.Join(", ", env.Folders.All.Select(f => $"{f.Id}={f.Path} ({f.Granted})"))).Append('\n');
        sb.Append("shell_run: ").Append(_shell.Enabled ? "on (every command confirmed at the Mac)" : "off").Append('\n');
        sb.Append("log: ").Append(Paths.LogFile).Append('\n');
        return Task.FromResult(new ToolOutput { Content = sb.ToString() });
    }
}

// The resident's own log, or the macOS unified log (log show) for a recent window; read-only.
internal sealed class LogReadTool : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "log_read", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Read logs on this Mac. source=resident (default): last 'lines' lines of the CO Resident log (max 1000). " +
                      "source=out: last 'lines' lines of resident.out (stdout/stderr of the LaunchAgent: runtime crashes). " +
                      "source=crash: summaries of the newest macOS crash reports of co-resident ('lines' = how many, default 3, max 10). " +
                      "source=system: macOS unified log for 'minutes' (max 60) filtered by 'predicate' (log show syntax, " +
                      "e.g. process == \"Google Drive\"), the last minutes or from 'start' (UTC, within the last 7 days), " +
                      "compact style, up to 60 000 characters.",
        InputSchema = Schema.Obj(
            ("source", "string", "resident | out | crash | system", false),
            ("lines", "integer", "resident/out: number of last lines (default 200, max 1000)", false),
            ("minutes", "integer", "system: time window in minutes (default 10, max 60)", false),
            ("start", "string", "system: window start in UTC, e.g. 2026-11-07T20:20:00Z (default: the last 'minutes')", false),
            ("predicate", "string", "system: log show --predicate expression", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) => new() { Tool = "log_read", Class = RiskClass.R, Summary = "read logs" };

    public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        string source = Args.OptStr(args, "source") ?? "resident";
        if (source is "resident" or "out")
        {
            string file = source == "out" ? Path.Combine(Paths.LogDir, "resident.out") : Paths.LogFile;
            int n = (int)Math.Clamp(Args.OptLong(args, "lines", 200), 1, 1000);
            string text = File.Exists(file) ? string.Join("\n", File.ReadLines(file).TakeLast(n)) : "(no log yet)";
            return Task.FromResult(new ToolOutput { Content = text, Untrusted = true });
        }
        if (source == "crash")
        {
            int k = (int)Math.Clamp(Args.OptLong(args, "lines", 3), 1, 10);
            var reports = MacCrash.Reports().Take(k).Select(f => MacCrash.Summarize(f)).ToList();
            return Task.FromResult(new ToolOutput { Content = reports.Count > 0 ? string.Join("\n\n", reports) : "(no crash reports of co-resident)", Untrusted = true });
        }
        if (source != "system") throw new ToolException("invalid_args", "source: resident, out, crash or system.");
        int min = (int)Math.Clamp(Args.OptLong(args, "minutes", 10), 1, 60);
        var a = new List<string> { "show", "--style", "compact" };
        if (Args.OptStr(args, "start") is { Length: > 0 } st)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!DateTime.TryParse(st, inv, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var from)
                || from < DateTime.UtcNow.AddDays(-7) || from > DateTime.UtcNow)
                throw new ToolException("invalid_args", "start: UTC time within the last 7 days, e.g. 2026-11-07T20:20:00Z.");
            // log show reads local time; the resident runs in the user's time zone.
            a.AddRange(new[] { "--start", from.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv), "--end", from.AddMinutes(min).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv) });
        }
        else a.AddRange(new[] { "--last", min + "m" });
        if (Args.OptStr(args, "predicate") is { Length: > 0 } pr) { a.Add("--predicate"); a.Add(pr); }
        return Task.FromResult(new ToolOutput { Content = TailOf("/usr/bin/log", a, ShellRunTool.MaxOutputChars, 60_000), Untrusted = true });
    }

    // Keeps only the last maxChars of the output while reading (a busy Mac logs hundreds of MB an hour).
    private static string TailOf(string file, IEnumerable<string> args, int maxChars, int timeoutMs)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var x in args) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new ToolException("log_failed", "Could not start " + file);
        var lines = new LinkedList<string>();
        int total = 0;
        bool cut = false;
        var err = p.StandardError.ReadToEndAsync();
        var reader = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                lines.AddLast(line);
                total += line.Length + 1;
                while (total > maxChars && lines.First != null) { total -= lines.First.Value.Length + 1; lines.RemoveFirst(); cut = true; }
            }
        });
        if (!reader.Wait(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } reader.Wait(5000); cut = true; }
        p.WaitForExit(5000);
        string head = cut ? $"(only the last {maxChars} characters)\n" : "";
        if (p.HasExited && p.ExitCode != 0 && lines.Count == 0) return "log show failed: " + (err.IsCompleted ? err.Result : "");
        return head + string.Join("\n", lines);
    }
}
