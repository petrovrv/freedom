// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Host;
using CO.Core.Policy;

namespace CO.Core.Tools;

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public interface IShellRunner
{
    Task<ShellResult> RunAsync(string command, string workDir, TimeSpan timeout, CancellationToken ct);
}

// Runs one command line through a POSIX shell (macOS: /bin/zsh -c). Output is capped; on timeout the process tree is killed.
public sealed class ProcessShellRunner : IShellRunner
{
    private readonly string _shell;
    public ProcessShellRunner(string shell = "/bin/zsh") => _shell = shell;

    public async Task<ShellResult> RunAsync(string command, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_shell)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, WorkingDirectory = workDir,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var p = Process.Start(psi) ?? throw new ToolException("shell_failed", "Could not start " + _shell);
        p.StandardInput.Close();
        var so = new StringBuilder();
        var se = new StringBuilder();
        var outTask = ReadCappedAsync(p.StandardOutput, so);
        var errTask = ReadCappedAsync(p.StandardError, se);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        // A background child ("cmd &") may keep the pipes open after the shell exits: wait for the output a few
        // seconds only, then return what arrived (the child is not waited for).
        var drain = Task.WhenAll(outTask, errTask);
        bool drained = await Task.WhenAny(drain, Task.Delay(DrainWait, CancellationToken.None)) == drain;
        string o, e;
        lock (so) o = so.ToString();
        lock (se) e = se.ToString();
        if (!drained) e += (e.Length > 0 && !e.EndsWith('\n') ? "\n" : "") + "(a background process still holds the output; not waited for)\n";
        return new ShellResult(timedOut ? -1 : p.ExitCode, o, e, timedOut);
    }

    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(3);

    private static async Task ReadCappedAsync(StreamReader r, StringBuilder sb)
    {
        char[] buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
            lock (sb)
                if (sb.Length < ShellRunTool.MaxOutputChars) sb.Append(buf, 0, Math.Min(n, ShellRunTool.MaxOutputChars - sb.Length));
    }
}

// shell.json in the data directory: the computer's user can switch shell_run off ("co-resident shell off").
public sealed class ShellSettings
{
    private readonly string _file;
    public ShellSettings(string file) => _file = file;

    public bool Enabled
    {
        get
        {
            try { return !File.Exists(_file) || JsonNode.Parse(File.ReadAllText(_file))?["enabled"]?.GetValue<bool>() != false; }
            catch (Exception) { return false; }   // unreadable file: stay off rather than guess
        }
        set => File.WriteAllText(_file, new JsonObject { ["enabled"] = value }.ToJsonString());
    }
}

// Mac resident R1: one shell command on the device. Every call is confirmed by the person at the computer
// (no grants, no "allow for task", also when the owner trusts the device); sudo is refused before asking.
public sealed class ShellRunTool : IDeviceTool
{
    public const int MaxOutputChars = 60_000;
    public const int DefaultTimeoutSec = 60, MaxTimeoutSec = 600;
    // The confirmation window must show the whole command (a dialog cannot scroll far): longer ones are refused.
    public const int MaxCommandChars = 1200, MaxWhyChars = 300;
    private readonly IShellRunner _runner;
    private readonly ShellSettings _settings;
    private readonly string _home;

    public ShellRunTool(IShellRunner runner, ShellSettings settings, string home)
    {
        _runner = runner;
        _settings = settings;
        _home = home;
    }

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "shell_run", Class = RiskClass.X, Untrusted = UntrustedMode.Always,
        Description =
            "Run one shell command (zsh) on this computer, for diagnostics and checks. EVERY call shows the full command " +
            "to the person at the computer, who presses Run or Decline (no answer = declined); keep commands short, " +
            "readable and non-destructive (max 1200 characters), and say in 'why' what the command is for (max 300). " +
            "sudo is refused. Returns exit code, stdout and stderr (each up to 60 000 characters). " +
            "timeout_sec: default 60, limited by the device call timeout (usually 115 s); background processes are not waited for.",
        InputSchema = Schema.Obj(
            ("command", "string", "Command line for zsh -c.", true),
            ("why", "string", "One sentence for the person at the computer: what the command checks or does.", true),
            ("cwd", "string", "Working directory (default: home).", false),
            ("timeout_sec", "integer", "Time limit in seconds (default 60).", false)),
    };

    private sealed record Checked(string Command, string Why, string Cwd, int TimeoutSec);

    // Everything the person must see is checked here, before the confirmation (which ToolHost asks, outside the
    // execution timer, through ConfirmEveryCall).
    private Checked Check(JsonElement args, ToolEnv env)
    {
        string command = Args.Str(args, "command").Trim();
        string why = (Args.OptStr(args, "why") ?? "").Trim();
        if (command.Length == 0) throw new ToolException("invalid_args", "Empty command.");
        if (command.Length > MaxCommandChars)
            throw new ToolException("invalid_args", $"Command longer than {MaxCommandChars} characters: the person at the computer must be able to read all of it.");
        if (why.Length > MaxWhyChars) why = why[..MaxWhyChars] + "…";
        if (!_settings.Enabled) throw new ToolException("disabled", "shell_run is switched off on this computer by its user.");
        if (UsesSudo(command)) throw new ToolException("denied", "sudo/su is not allowed through shell_run.");
        string cwd = Args.OptStr(args, "cwd") is { Length: > 0 } c ? ExpandHome(c) : _home;
        if (!Directory.Exists(cwd)) throw new ToolException("invalid_args", "Working directory does not exist: " + cwd);
        int timeout = (int)Math.Clamp(Args.OptLong(args, "timeout_sec", DefaultTimeoutSec), 1, MaxTimeoutSec);
        // ToolHost stops the execution after timeoutMs; end a little earlier so the output still comes back.
        int callMs = env.Call?.TimeoutMs ?? 0;
        if (callMs > 10_000) timeout = Math.Min(timeout, callMs / 1000 - 5);
        return new Checked(command, why, cwd, timeout);
    }

    public ActionPlan Plan(JsonElement args, ToolEnv env)
    {
        var c = Check(args, env);
        return new ActionPlan
        {
            Tool = "shell_run", Class = RiskClass.X, ConfirmEveryCall = true,
            Summary = (c.Why.Length > 0 ? c.Why + "\n\n" : "") + "$ " + c.Command + "\n\n(in " + c.Cwd + ", up to " + c.TimeoutSec + " s)",
        };
    }

    // A guard against the obvious forms (also /usr/bin/sudo, "sudo", \sudo); the real control is the person who
    // reads the full command before pressing Run.
    public static bool UsesSudo(string command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command, @"(^|[\s;&|(){}`$'""/\\])(sudo|su|doas|pkexec)(['""]?)(\s|$|[;&|)])");

    public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        // Again after the confirmation: the user may have switched shell_run off while the dialog was open.
        var c = Check(args, env);
        var r = await _runner.RunAsync(c.Command, c.Cwd, TimeSpan.FromSeconds(c.TimeoutSec), ct);
        var sb = new StringBuilder();
        sb.Append(r.TimedOut ? $"exit: timeout after {c.TimeoutSec} s (killed)\n" : $"exit: {r.ExitCode}\n");
        if (r.Stdout.Length > 0) sb.Append("--- stdout\n").Append(r.Stdout).Append(r.Stdout.EndsWith('\n') ? "" : "\n");
        if (r.Stderr.Length > 0) sb.Append("--- stderr\n").Append(r.Stderr).Append(r.Stderr.EndsWith('\n') ? "" : "\n");
        if (r.Stdout.Length >= MaxOutputChars || r.Stderr.Length >= MaxOutputChars) sb.Append("(output truncated)\n");
        return new ToolOutput { Content = sb.ToString(), Untrusted = true };
    }

    private string ExpandHome(string p) => p == "~" ? _home : p.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, p[2..]) : p;
}
