// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using CO.Core.Doctor;
using Xunit;

namespace CO.Core.Tests;

// S3: Bridge doctor on a scratch layout with a fake probe (no processes are touched).
public class DoctorTests : IDisposable
{
    private sealed class FakeProbe : IDoctorProbe
    {
        public List<ProcInfo> LauncherProcs = new(), BridgeProcs = new();
        public List<ThreatInfo> Threats = new();
        public List<string> Calls = new();
        public bool SiteUp = true;
        public IReadOnlyList<ProcInfo> Processes(string name) => name == "AIWorkbenchBridge" ? LauncherProcs : name == "BridgeAgent" ? BridgeProcs : new();
        public IReadOnlyList<ThreatInfo> Detections() => Threats;
        public string RunTask(string name) { Calls.Add("task " + name); return "task " + name + " started"; }
        public void Start(string exe) => Calls.Add("start " + exe);
        public void Kill(int pid)
        {
            Calls.Add("kill " + pid);
            LauncherProcs.RemoveAll(p => p.Pid == pid);
            BridgeProcs.RemoveAll(p => p.Pid == pid);
        }
        public Task<bool> SiteUpAsync(string url, CancellationToken ct) => Task.FromResult(SiteUp);
        public string? FindOnPath(string exe) => null;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "co-doctor-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _programs, _state, _drive;
    private DateTime _now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeProbe _probe = new();
    private readonly List<(string Kind, string Detail)> _sent = new();

    public DoctorTests()
    {
        _programs = Path.Combine(_root, "Programs", "AIWorkbenchBridge");
        _state = Path.Combine(_root, "state");
        _drive = Path.Combine(_root, "drive");
        Directory.CreateDirectory(Path.Combine(_programs, "versions", "1.12.0"));
        Directory.CreateDirectory(Path.Combine(_programs, "versions", "1.10.3"));
        File.WriteAllText(Path.Combine(_programs, "versions", "1.12.0", "BridgeAgent.exe"), "x");
        File.WriteAllText(Path.Combine(_programs, "versions", "1.10.3", "BridgeAgent.exe"), "x");
        File.WriteAllText(Path.Combine(_programs, "AIWorkbenchBridge.exe"), "x");
        File.WriteAllText(Path.Combine(_programs, "launcher.json"), "{\"stateRoot\": " + Q(_state) + "}");
        File.WriteAllText(Path.Combine(_programs, "state.json"), "{\"current\":\"1.12.0\",\"previous\":\"1.10.3\",\"pending\":null,\"bad\":[\"1.7.1\"]}");
        Directory.CreateDirectory(Path.Combine(_state, "logs"));
        File.WriteAllText(Path.Combine(_state, "config.json"), "\uFEFF{\"hostId\":\"home-main\",\"driveRoot\":" + Q(_drive) +
            ",\"dbQueue\":{\"url\":\"https://co.example.org/agent/db.php\"},\"maxProcessTimeoutMinutes\":240,\"agents\":{\"claude\":{\"executable\":\"claude.exe\"}}}");
        File.WriteAllText(Path.Combine(_state, "logs", "2026-11-11.log"), "2026-11-11T11:59:00.0000000Z | Claimed job JOB-1 via database.\n");
        File.SetLastWriteTimeUtc(Path.Combine(_state, "logs", "2026-11-11.log"), _now.AddMinutes(-1));
        Directory.CreateDirectory(Path.Combine(_drive, "transport", "hosts", "home-main", "claimed"));
        Directory.CreateDirectory(Path.Combine(_drive, "transport", "hosts", "home-main", "inbox"));
        Owner(_now.AddMinutes(-1), "running", 1);
        Running(launcher: true, bridge: "1.12.0");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private void Owner(DateTime utc, string state, int jobs, int? relay = 0) =>
        File.WriteAllText(Path.Combine(_drive, "transport", "hosts", "home-main", "owner.json"),
            $"{{\"hostId\":\"home-main\",\"machineName\":\"{Environment.MachineName}\",\"processId\":200,\"bridgeVersion\":\"1.12.0\",\"updatedUtc\":\"{utc:o}\",\"state\":\"{state}\",\"runningJobs\":{jobs}"
            + (relay is int r ? $",\"relayActive\":{r}" : "") + "}");

    private void Running(bool launcher, string? bridge)
    {
        _probe.LauncherProcs = launcher ? new() { new ProcInfo(100, Path.Combine(_programs, "AIWorkbenchBridge.exe")) } : new();
        _probe.BridgeProcs = bridge != null ? new() { new ProcInfo(200, Path.Combine(_programs, "versions", bridge, "BridgeAgent.exe"), _now.AddHours(-1)) } : new();
    }

    private BridgeDoctor Doctor()
    {
        var l = BridgeLayout.Load(_programs)!;
        return new BridgeDoctor(l, _probe, Path.Combine(_root, "doctor.json"), () => _now)
        {
            Send = (k, d, ct) => { _sent.Add((k, d)); return Task.CompletedTask; },
        };
    }

    [Fact]
    public void LayoutComesFromLauncherAndConfig()
    {
        var l = BridgeLayout.Load(_programs)!;
        Assert.Equal(_state, l.StateRoot);
        Assert.Equal("home-main", l.HostId);
        Assert.Equal(_drive, l.DriveRoot);
        Assert.Equal("aiwb/co-push", l.PushTarget);
        Assert.Equal("claude.exe", l.ClaudeExe);
        Assert.Null(BridgeLayout.Load(Path.Combine(_root, "nothing")));
    }

    [Fact]
    public async Task HealthyBridgeIsOk()
    {
        var r = await Doctor().CheckAsync(CancellationToken.None);
        Assert.Equal(DoctorLevel.Ok, r.Find("process")!.Level);
        Assert.Equal(DoctorLevel.Ok, r.Find("heartbeat")!.Level);
        Assert.Equal(DoctorLevel.Ok, r.Find("version")!.Level);
        Assert.Equal(DoctorLevel.Ok, r.Find("defender")!.Level);
        Assert.Empty(r.Fixes);
    }

    [Fact]
    public async Task StaleHeartbeatAsksForGracefulRestartOnce()
    {
        Owner(_now.AddMinutes(-40), "running", 2);
        var d = Doctor();
        var r = await d.TickAsync(CancellationToken.None);
        Assert.Equal(DoctorLevel.Fail, r.Find("heartbeat")!.Level);
        Assert.True(File.Exists(Path.Combine(_state, "drain.request")));        // auto restart = drain, no kill
        Assert.DoesNotContain(_probe.Calls, c => c.StartsWith("kill"));
        Assert.Contains(_sent, e => e.Kind == "doctor_repair" && e.Detail.Contains("restart"));
        Assert.Contains(_sent, e => e.Kind == "doctor_fail");
        File.Delete(Path.Combine(_state, "drain.request"));
        _now = _now.AddMinutes(10);
        await d.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_state, "drain.request")));       // not again within 30 min
        _now = _now.AddMinutes(15);
        var r2 = await d.CheckAsync(CancellationToken.None);
        Assert.Equal(DoctorFix.HardRestart, r2.Find("heartbeat")!.Fix);        // the next step is the owner's
    }

    [Fact]
    public async Task NothingRunningStartsTheLauncherOnSecondRound()
    {
        Running(launcher: false, bridge: null);
        var d = Doctor();
        await d.TickAsync(CancellationToken.None);
        Assert.Empty(_probe.Calls);
        await d.TickAsync(CancellationToken.None);
        Assert.Contains("task AIWB-BridgeAutostart", _probe.Calls);
    }

    [Fact]
    public async Task MissingExeAndDefenderAreReported()
    {
        File.Delete(Path.Combine(_programs, "versions", "1.12.0", "BridgeAgent.exe"));
        _probe.Threats.Add(new ThreatInfo(_now.AddHours(-3), @"file:_C:\Users\x\AppData\Local\Programs\AIWorkbenchBridge\versions\1.12.0\BridgeAgent.exe"));
        _probe.Threats.Add(new ThreatInfo(_now.AddHours(-3), @"file:_C:\other\tool.exe"));
        var r = await Doctor().CheckAsync(CancellationToken.None);
        Assert.Equal(DoctorLevel.Fail, r.Find("version")!.Level);
        Assert.Equal(DoctorFix.Rollback, r.Find("version")!.Fix);
        Assert.Equal(DoctorLevel.Fail, r.Find("defender")!.Level);
        _probe.Threats[0] = _probe.Threats[0] with { Utc = _now.AddDays(-10) };   // an old incident is history
        Assert.Equal(DoctorLevel.Info, (await Doctor().CheckAsync(CancellationToken.None)).Find("defender")!.Level);
    }

    [Fact]
    public async Task StuckClaimedJobsGetCancelMarkers()
    {
        string job = Path.Combine(_drive, "transport", "hosts", "home-main", "claimed", "JOB-OLD");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "request.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(job, "request.json"), _now.AddHours(-6));
        string ev = Path.Combine(_drive, "transport", "hosts", "home-main", "events", "JOB-OLD");
        Directory.CreateDirectory(ev);
        File.WriteAllText(Path.Combine(ev, "000001-000001.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(ev, "000001-000001.json"), _now.AddHours(-5));
        // Queued long ago, started 20 min ago and writing events: not stuck.
        string late = Path.Combine(_drive, "transport", "hosts", "home-main", "claimed", "JOB-LATE");
        Directory.CreateDirectory(late);
        File.WriteAllText(Path.Combine(late, "request.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(late, "request.json"), _now.AddHours(-8));
        string lev = Path.Combine(_drive, "transport", "hosts", "home-main", "events", "JOB-LATE");
        Directory.CreateDirectory(lev);
        File.WriteAllText(Path.Combine(lev, "000001-000003.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(lev, "000001-000003.json"), _now.AddMinutes(-20));
        string fresh = Path.Combine(_drive, "transport", "hosts", "home-main", "claimed", "JOB-NEW");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "request.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(fresh, "request.json"), _now.AddMinutes(-5));
        var d = Doctor();
        var r = await d.CheckAsync(CancellationToken.None);
        Assert.Equal(new[] { "JOB-OLD" }, r.StuckJobs);
        await d.RepairAsync(DoctorFix.CancelStuck, false, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_drive, "transport", "hosts", "home-main", "cancel", "JOB-OLD.cancel")));
        Assert.False(File.Exists(Path.Combine(_drive, "transport", "hosts", "home-main", "cancel", "JOB-NEW.cancel")));
        Assert.False(File.Exists(Path.Combine(_drive, "transport", "hosts", "home-main", "cancel", "JOB-LATE.cancel")));
    }

    [Fact]
    public async Task RollbackWaitsForTheIdleBridge()
    {
        // A per-job device proxy of the same exe (started later) is never touched.
        _probe.BridgeProcs.Add(new ProcInfo(300, Path.Combine(_programs, "versions", "1.12.0", "BridgeAgent.exe"), _now.AddMinutes(-5)));
        var d = Doctor();
        string outcome = await d.RepairAsync(DoctorFix.Rollback, false, CancellationToken.None);
        Assert.Contains("1.12.0 marked bad", outcome);
        Assert.Contains("kill 100", _probe.Calls);                 // the launcher only
        Assert.DoesNotContain("kill 200", _probe.Calls);
        var st = JsonNode.Parse(File.ReadAllText(Path.Combine(_programs, "state.json")))!.AsObject();
        Assert.Equal("1.10.3", (string?)st["current"]);
        Assert.Null(st["previous"]);
        Assert.Contains("1.12.0", st["bad"]!.AsArray().Select(x => (string?)x));
        Assert.True(File.Exists(Path.Combine(_state, "drain.request")));
        Assert.Equal("rollback", d.State.Switch);

        // A job still runs: nothing is killed.
        Owner(_now, "updating", 1);
        await d.TickAsync(CancellationToken.None);
        Assert.DoesNotContain("kill 200", _probe.Calls);

        // Idle on two rounds in a row: the doctor ends the idle Bridge and starts the launcher.
        _now = _now.AddMinutes(5);
        Owner(_now, "updating", 0);
        await d.TickAsync(CancellationToken.None);
        Assert.DoesNotContain("kill 200", _probe.Calls);
        _now = _now.AddMinutes(5);
        Owner(_now, "updating", 0);
        await d.TickAsync(CancellationToken.None);
        Assert.Contains("kill 200", _probe.Calls);
        Assert.DoesNotContain("kill 300", _probe.Calls);
        Assert.Contains("task AIWB-BridgeAutostart", _probe.Calls);

        // The launcher starts the previous version: the switch is done.
        Running(launcher: true, bridge: "1.10.3");
        _now = _now.AddMinutes(5);
        Owner(_now, "running", 0);
        await d.TickAsync(CancellationToken.None);
        Assert.Null(d.State.Switch);
    }

    [Fact]
    public async Task RelayCallsOrAnOldBridgeKeepTheBridgeAlive()
    {
        // R2 (review S3-01): no job, but a site job's relay call runs (or the Bridge does not report relay calls)
        var d = Doctor();
        await d.RepairAsync(DoctorFix.Rollback, false, CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            _now = _now.AddMinutes(5);
            Owner(_now, "updating", 0, relay: 1);
            await d.TickAsync(CancellationToken.None);
        }
        Assert.DoesNotContain("kill 200", _probe.Calls);
        for (int i = 0; i < 3; i++)
        {
            _now = _now.AddMinutes(5);
            Owner(_now, "updating", 0, relay: null);
            await d.TickAsync(CancellationToken.None);
        }
        Assert.DoesNotContain("kill 200", _probe.Calls);
        // the relay call ended: two idle rounds, then the kill
        for (int i = 0; i < 2; i++)
        {
            _now = _now.AddMinutes(5);
            Owner(_now, "updating", 0, relay: 0);
            await d.TickAsync(CancellationToken.None);
        }
        Assert.Contains("kill 200", _probe.Calls);
    }

    [Fact]
    public async Task OwnerJsonOfAnotherMachineNeverLeadsToAKill()
    {
        var d = Doctor();
        await d.RepairAsync(DoctorFix.Rollback, false, CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            _now = _now.AddMinutes(5);
            File.WriteAllText(Path.Combine(_drive, "transport", "hosts", "home-main", "owner.json"),
                $"{{\"machineName\":\"OTHER-PC\",\"processId\":200,\"updatedUtc\":\"{_now:o}\",\"state\":\"updating\",\"runningJobs\":0}}");
            await d.TickAsync(CancellationToken.None);
        }
        Assert.DoesNotContain("kill 200", _probe.Calls);
    }

    [Fact]
    public async Task SwitchGivenUpWithdrawsTheDrain()
    {
        var d = Doctor();
        await d.RepairAsync(DoctorFix.Rollback, false, CancellationToken.None);
        _now = _now.AddHours(7);
        Owner(_now, "updating", 3);         // still busy after 6 h
        await d.TickAsync(CancellationToken.None);
        Assert.Null(d.State.Switch);
        Assert.False(File.Exists(Path.Combine(_state, "drain.request")));
        Assert.Contains("task AIWB-BridgeAutostart", _probe.Calls);
    }

    [Fact]
    public async Task TrayExitIsNotUndone()
    {
        Running(launcher: false, bridge: null);
        File.WriteAllText(Path.Combine(_state, "logs", "launcher.log"), "2026-11-11T11:00:00.0000000Z | Bridge was closed by the user; launcher stops.\n");
        var d = Doctor();
        await d.TickAsync(CancellationToken.None);
        var r = await d.TickAsync(CancellationToken.None);
        Assert.Equal(DoctorLevel.Warn, r.Find("process")!.Level);
        Assert.Empty(_probe.Calls);
    }

    [Fact]
    public async Task EventsWaitForTheSite()
    {
        var d = Doctor();
        d.Send = (k, det, ct) => throw new HttpRequestException("offline");
        d.Enqueue("doctor_fail", "x");
        await d.FlushAsync(CancellationToken.None);
        Assert.Single(d.State.Outbox);
        d.Send = (k, det, ct) => { _sent.Add((k, det)); return Task.CompletedTask; };
        _now = _now.AddMinutes(30);
        await d.FlushAsync(CancellationToken.None);
        Assert.Empty(d.State.Outbox);
        Assert.Contains(_sent, e => e.Kind == "doctor_fail" && e.Detail.StartsWith("[2026-11-11 12:00 UTC]"));
    }

    [Fact]
    public void RepairLinesOfTheAgentAreParsed()
    {
        Assert.Equal(new[] { "rollback" }, DoctorAi.Repairs("Причина: …\nREPAIR: rollback\nREPAIR: format_disk"));
    }
}

// Live, read-only: CO_LIVE_DOCTOR=1 prints the doctor report of this computer (checks only, no repairs).
public class DoctorLiveTests
{
    private sealed class ReadOnlyProbe : IDoctorProbe
    {
        public IReadOnlyList<ProcInfo> Processes(string name) => System.Diagnostics.Process.GetProcessesByName(name)
            .Select(p => { string path = ""; try { path = p.MainModule?.FileName ?? ""; } catch (Exception) { } return new ProcInfo(p.Id, path); }).ToList();
        public IReadOnlyList<ThreatInfo> Detections() => new List<ThreatInfo>();
        public string RunTask(string name) => throw new InvalidOperationException("read-only");
        public void Start(string exe) => throw new InvalidOperationException("read-only");
        public void Kill(int pid) => throw new InvalidOperationException("read-only");
        public Task<bool> SiteUpAsync(string url, CancellationToken ct) => Task.FromResult(true);
        public string? FindOnPath(string exe) => null;
    }

    [Fact]
    public async Task ReportOfThisComputer()
    {
        if (Environment.GetEnvironmentVariable("CO_LIVE_DOCTOR") != "1") return;
        var l = BridgeLayout.Load();
        Assert.NotNull(l);
        var d = new BridgeDoctor(l!, new ReadOnlyProbe(), Path.Combine(Path.GetTempPath(), "co-doctor-live.json"));
        var r = await d.CheckAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "co-doctor-live.txt"), r.ToText());
        // CO_LIVE_DOCTOR_AI=claude|codex (CO_LIVE_DOCTOR_EXE = its path): one real read-only question to the local CLI.
        if (Environment.GetEnvironmentVariable("CO_LIVE_DOCTOR_AI") is { Length: > 0 } agent)
        {
            string exe = Environment.GetEnvironmentVariable("CO_LIVE_DOCTOR_EXE") ?? agent;
            string answer = await DoctorAi.AskAsync(agent, exe, DoctorAi.Prompt(r, l!, "Коротко: здоров ли Bridge?"), l!, TimeSpan.FromMinutes(5), CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "co-doctor-live-ai.txt"), answer);
            Assert.NotEmpty(answer);
        }
    }
}
