// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Host;
using CO.Core.Tools;

namespace CO.Core.Tests;

public sealed class FakeShell : IShellRunner
{
    public List<(string cmd, string cwd, TimeSpan timeout)> Runs { get; } = new();
    public ShellResult Result { get; set; } = new(0, "Darwin\n", "", false);

    public Task<ShellResult> RunAsync(string command, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add((command, workDir, timeout));
        return Task.FromResult(Result);
    }
}

public class ShellRunnerTests
{
    // A real POSIX shell: zsh on macOS (the resident), Git's bash on a Windows build computer; skipped when absent.
    private static string? Shell() =>
        new[] { "/bin/zsh", "/bin/bash", @"C:\Program Files\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);

    [Fact]
    public async Task Background_child_does_not_hold_the_call()
    {
        if (Shell() is not string sh) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ProcessShellRunner(sh).RunAsync("echo started; sleep 20 &", Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        Assert.Contains("started", r.Stdout);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Timeout_kills_the_command()
    {
        if (Shell() is not string sh) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await new ProcessShellRunner(sh).RunAsync("sleep 30", Path.GetTempPath(), TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Exit_code_and_streams()
    {
        if (Shell() is not string sh) return;
        var r = await new ProcessShellRunner(sh).RunAsync("echo out; echo err >&2; exit 3", Path.GetTempPath(), TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.Stdout);
        Assert.Contains("err", r.Stderr);
    }
}

public class ShellToolTests
{
    private static (Fixture fx, FakeShell sh, ShellSettings st) Make()
    {
        var sh = new FakeShell();
        string root = Path.Combine(Path.GetTempPath(), "co-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var st = new ShellSettings(Path.Combine(root, "shell.json"));
        var fx = new Fixture(extraTools: new IDeviceTool[] { new ShellRunTool(sh, st, root) });
        return (fx, sh, st);
    }

    [Fact]
    public async Task Runs_only_after_the_user_allows_it()
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Allow));
        var r = await fx.Run("shell_run", new { command = "uname -s", why = "check the OS" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Single(sh.Runs);
        Assert.Contains("exit: 0", r.Content);
        Assert.Contains("Darwin", r.Content);
        var seen = Assert.Single(fx.Approvals.Seen);
        Assert.Contains("$ uname -s", seen.Plan.Summary);
        Assert.Contains("check the OS", seen.Plan.Summary);
    }

    [Fact]
    public async Task Declined_command_does_not_run()
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Deny));
        var r = await fx.Run("shell_run", new { command = "ls", why = "list" });
        Assert.False(r.Ok);
        Assert.Equal("denied_by_user", r.Error?.Code);
        Assert.Empty(sh.Runs);
    }

    [Fact]
    public void Policy_asks_every_call_even_when_trusted_and_denies_on_managed()
    {
        var policy = new CO.Core.Policy.PolicyEngine(new CO.Core.Policy.GrantStore());
        var plan = new CO.Core.Policy.ActionPlan { Tool = "shell_run", Class = CO.Core.Policy.RiskClass.X, ConfirmEveryCall = true };
        var trusted = policy.Decide(plan, new CO.Core.Policy.CallContext { RunId = "r", TaskId = "r", Trusted = true });
        Assert.Equal(CO.Core.Policy.Verdict.Confirm, trusted.Verdict);
        Assert.False(trusted.Grantable);
        var managed = policy.Decide(plan, new CO.Core.Policy.CallContext { RunId = "r", TaskId = "r", Managed = true });
        Assert.Equal(CO.Core.Policy.Verdict.Deny, managed.Verdict);
    }

    [Fact]
    public async Task Every_call_asks_again_even_after_allow_for_task()
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.AllowForTask));
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Deny));
        Assert.True((await fx.Run("shell_run", new { command = "ls", why = "a" })).Ok);
        Assert.False((await fx.Run("shell_run", new { command = "ls", why = "b" })).Ok);
        Assert.Equal(2, fx.Approvals.Seen.Count);
        Assert.Single(sh.Runs);
    }

    [Theory]
    [InlineData("sudo rm -rf /tmp/x")]
    [InlineData("ls; sudo whoami")]
    [InlineData("echo hi && su root")]
    [InlineData("$(sudo id)")]
    [InlineData("/usr/bin/sudo -n true")]
    [InlineData("\"sudo\" ls")]
    [InlineData(@"\sudo ls")]
    [InlineData("(sudo ls)")]
    [InlineData("su")]
    public async Task Sudo_is_refused_without_asking(string cmd)
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        var r = await fx.Run("shell_run", new { command = cmd, why = "x" });
        Assert.False(r.Ok);
        Assert.Equal("denied", r.Error?.Code);
        Assert.Empty(fx.Approvals.Seen);
        Assert.Empty(sh.Runs);
    }

    [Theory]
    [InlineData("pseudo-tool --help")]
    [InlineData("grep sudoers /etc/hosts")]
    [InlineData("echo issue")]
    public void Words_containing_sudo_are_not_sudo(string cmd) => Assert.False(ShellRunTool.UsesSudo(cmd));

    [Fact]
    public async Task Long_commands_are_refused_and_why_is_cut()
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        var r = await fx.Run("shell_run", new { command = "echo " + new string('a', 1300), why = "x" });
        Assert.False(r.Ok);
        Assert.Empty(fx.Approvals.Seen);
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Allow));
        Assert.True((await fx.Run("shell_run", new { command = "ls", why = new string('w', 2000) })).Ok);
        Assert.True(fx.Approvals.Seen[0].Plan.Summary.Length < 600);
        Assert.Contains("$ ls", fx.Approvals.Seen[0].Plan.Summary);
    }

    [Fact]
    public async Task Switched_off_after_the_plan_does_not_run()
    {
        // ToolHost asks between Plan and ExecuteAsync; switching off in that window must stop the command.
        var sh = new FakeShell();
        string root = Path.Combine(Path.GetTempPath(), "co-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var st = new ShellSettings(Path.Combine(root, "shell.json"));
        var tool = new ShellRunTool(sh, st, root);
        using var fx = new Fixture();
        var call = fx.Call("shell_run", new { command = "ls", why = "x" });
        var args = System.Text.Json.JsonDocument.Parse(call.ArgsJson).RootElement;
        var env = new ToolEnv { Folders = fx.Folders, Snapshots = fx.Snapshots, RunId = "run1", Call = call };
        Assert.True(tool.Plan(args, env).ConfirmEveryCall);
        st.Enabled = false;
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(args, env, CancellationToken.None));
        Assert.Equal("disabled", ex.Code);
        Assert.Empty(sh.Runs);
    }

    [Fact]
    public async Task Switched_off_by_the_user()
    {
        var (fx, sh, st) = Make();
        using var _fx = fx;
        st.Enabled = false;
        var r = await fx.Run("shell_run", new { command = "ls", why = "x" });
        Assert.False(r.Ok);
        Assert.Equal("disabled", r.Error?.Code);
        Assert.Empty(fx.Approvals.Seen);
        st.Enabled = true;
        Assert.True(st.Enabled);
    }

    [Fact]
    public async Task Timeout_is_capped_and_reported()
    {
        var (fx, sh, _) = Make();
        using var _fx = fx;
        sh.Result = new ShellResult(-1, "", "", true);
        fx.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Allow));
        var r = await fx.Run("shell_run", new { command = "sleep 999", why = "x", timeout_sec = 5000 });
        Assert.True(r.Ok);
        Assert.Contains("timeout", r.Content);
        // the fixture's call carries the default timeoutMs of 120 s: execution must end before Bridge stops waiting
        Assert.True(sh.Runs[0].timeout <= TimeSpan.FromSeconds(115));
    }
}
