// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using CO.Core.Doctor;
using Xunit;

namespace CO.Core.Tests;

// 21.11: the CO Desktop reserve executor with a fake db.php and a fake Claude CLI (no network, no processes).
public class ReserveExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-reserve-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
    private bool _takesJobs = true;
    private string _bridgeState = "running";
    private readonly List<JsonObject> _calls = new();
    private readonly Queue<JsonObject> _jobs = new(); // offered to job_claim of the reserve id
    private readonly Queue<JsonObject> _bridgeJobs = new(); // offered to job_claim of home-main
    private readonly string _exe;

    public ReserveExecutorTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "drive", "projects", "P", "chats", "CHAT-1", "scratch"));
        _exe = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(_exe, "");
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"agents\":{\"claude\":{\"executable\":" + JsonValue.Create(_exe).ToJsonString()
            + ",\"modes\":{\"chat\":{\"arguments\":[\"-p\",\"--output-format\",\"stream-json\",\"--verbose\"]}},\"modelArguments\":[\"--model\",\"{value}\"]}}}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private BridgeLayout Layout() => new()
    {
        ProgramsDir = _dir, StateRoot = _dir, HostId = "home-main", DriveRoot = Path.Combine(_dir, "drive"),
        DbUrl = "https://example.invalid/agent/db.php", ClaudeExe = _exe,
    };

    private int _failEvents, _failResults; // the next N calls of these ops fail (hosting refuses a connect)

    private Task<JsonObject> Db(JsonObject body, bool gz, CancellationToken ct)
    {
        lock (_calls) _calls.Add((JsonObject)body.DeepClone());
        string op = (string)body["op"]!;
        if (op == "job_post_events" && _failEvents > 0) { _failEvents--; throw new HttpRequestException("HTTP 503"); }
        if (op == "job_result" && _failResults > 0) { _failResults--; throw new HttpRequestException("HTTP 503"); }
        JsonObject r = op switch
        {
            "executors_host" => new JsonObject
            {
                ["executors"] = new JsonArray(new JsonObject { ["id"] = "home-main", ["takesJobs"] = _takesJobs, ["state"] = _bridgeState }),
            },
            "job_claim" => Claim(body),
            _ => new JsonObject { ["ok"] = true },
        };
        return Task.FromResult(r);
    }

    private JsonObject Claim(JsonObject body)
    {
        var q = (string)body["host"]! == "home-main" ? _bridgeJobs : _jobs;
        var jobs = new JsonArray();
        if ((int)body["max"]! > 0 && q.Count > 0) jobs.Add(q.Dequeue());
        return new JsonObject { ["jobs"] = jobs, ["cancel"] = new JsonArray() };
    }

    private List<string> _lastArgs = new();
    private string _lastCwd = "", _lastStdin = "";

    private Task<int> Cli(string exe, IReadOnlyList<string> args, string cwd, string stdin, Action<string> onLine, CancellationToken ct)
    {
        _lastArgs = args.ToList();
        _lastCwd = cwd;
        _lastStdin = stdin;
        onLine("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"S-NEW\",\"model\":\"claude-sonnet-5-5\"}");
        onLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"pong\",\"session_id\":\"S-NEW\",\"total_cost_usd\":0.01,\"num_turns\":1,\"duration_ms\":900,\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}");
        return Task.FromResult(0);
    }

    private ReserveExecutor Make() =>
        new(Layout(), Db, Cli, () => ("d-pc", "PC", "owner1"), _dir, now: () => _now);

    private static JsonObject Job(string id, string host, string agent = "claude", string op = "new", string? session = null) => new()
    {
        ["jobId"] = id,
        ["request"] = new JsonObject
        {
            ["schemaVersion"] = 3, ["hostId"] = host, ["jobId"] = id, ["environmentId"] = "owner1", ["projectId"] = "P",
            ["chatId"] = "CHAT-1", ["branchId"] = "BR-root", ["agent"] = agent, ["mode"] = "chat", ["model"] = "sonnet",
            ["instruction"] = "reply pong",
            ["conversation"] = new JsonObject { ["operation"] = op, ["sessionId"] = session },
        },
    };

    private async Task Step(ReserveExecutor r, TimeSpan advance)
    {
        _now += advance;
        await r.TickAsync(CancellationToken.None);
        for (int i = 0; i < 100 && r.Running > 0; i++) await Task.Delay(20);
    }

    private List<JsonObject> Ops(string op) { lock (_calls) return _calls.Where(c => (string)c["op"]! == op).ToList(); }

    [Fact]
    public void Id_is_a_valid_executor_id()
    {
        Assert.Equal("desk-home-pc", ReserveExecutor.IdFor("HOME-PC"));
        Assert.Equal("desk-a-b", ReserveExecutor.IdFor("A.B"));
        Assert.True(ReserveExecutor.IdFor(new string('x', 60)).Length <= 32);
    }

    [Fact]
    public async Task Paused_while_the_bridge_takes_jobs()
    {
        var r = Make();
        await Step(r, TimeSpan.Zero);
        await Step(r, TimeSpan.FromMinutes(5));
        Assert.False(r.Active);
        Assert.DoesNotContain(Ops("job_claim"), c => (string)c["host"]! == "home-main"); // only its own queue
        var beat = Ops("executor_put").Last();
        Assert.Equal("paused", (string)beat["state"]!);
        Assert.Equal("home-main", (string)beat["owner"]!["reserveFor"]!);
        Assert.Equal("owner1", (string)beat["labels"]!["serves"]![0]!);
    }

    [Fact]
    public async Task Takes_over_after_three_minutes_and_runs_claude()
    {
        _takesJobs = false;
        _bridgeState = "paused";
        var r = Make();
        await Step(r, TimeSpan.Zero);
        await Step(r, TimeSpan.FromMinutes(2));
        Assert.False(r.Active);
        _jobs.Enqueue(Job("JOB-1", r.Id));
        await Step(r, TimeSpan.FromMinutes(1.5));
        Assert.True(r.Active);
        Assert.Equal("running", (string)Ops("executor_put").Last()["state"]!);
        var res = Ops("job_result").Single();
        Assert.Equal(r.Id, (string)res["host"]!);
        var result = res["result"]!;
        Assert.Equal("completed", (string)result["status"]!);
        Assert.Equal("pong", (string)result["agentFinalResponse"]!);
        Assert.Equal("S-NEW", (string)result["sessionId"]!);
        Assert.Contains("--model", _lastArgs);
        Assert.EndsWith("scratch", _lastCwd);
        Assert.Contains("reply pong", _lastStdin);
        var types = Ops("job_post_events").SelectMany(e => e["batches"]![0]!["batch"]!["events"]!.AsArray()).Select(e => (string)e!["type"]!).ToList();
        Assert.Equal("claimed", types[0]);
        Assert.Contains("started", types);
        Assert.Equal("completed", types[^1]);
    }

    [Fact]
    public async Task Claims_the_waiting_jobs_of_the_bridge_and_resumes_on_drive()
    {
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _bridgeJobs.Enqueue(Job("JOB-2", "home-main", op: "resume", session: "S-OLD"));
        await Step(r, TimeSpan.FromMinutes(4));
        var res = Ops("job_result").Single();
        Assert.Equal("home-main", (string)res["host"]!);
        Assert.Equal("home-main", (string)res["result"]!["hostId"]!);
        int i = _lastArgs.IndexOf("--resume");
        Assert.True(i >= 0 && _lastArgs[i + 1] == "S-OLD");
    }

    [Fact]
    public async Task Codex_job_of_the_bridge_is_left_alone_and_own_one_rejected()
    {
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _bridgeJobs.Enqueue(Job("JOB-3", "home-main", agent: "codex"));
        _jobs.Enqueue(Job("JOB-4", r.Id, agent: "codex"));
        await Step(r, TimeSpan.FromMinutes(4));
        Assert.Empty(Ops("job_result"));
        var rej = Ops("job_reject").Single();
        Assert.Equal("JOB-4", (string)rej["jobId"]!);
    }

    [Fact]
    public async Task Stops_claiming_when_the_bridge_is_back()
    {
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        await Step(r, TimeSpan.FromMinutes(4));
        Assert.True(r.Active);
        _takesJobs = true;
        await Step(r, TimeSpan.FromMinutes(1));
        Assert.False(r.Active);
        int claims = Ops("job_claim").Count;
        await Step(r, TimeSpan.FromSeconds(10));
        Assert.Equal(claims, Ops("job_claim").Count);
        Assert.Equal("paused", (string)Ops("executor_put").Last()["state"]!);
    }

    [Fact]
    public async Task Updating_bridge_gets_ten_minutes()
    {
        _takesJobs = false;
        _bridgeState = "updating";
        var r = Make();
        await Step(r, TimeSpan.Zero);
        await Step(r, TimeSpan.FromMinutes(5));
        Assert.False(r.Active);
        await Step(r, TimeSpan.FromMinutes(6));
        Assert.True(r.Active);
    }

    [Fact]
    public async Task Transport_failures_are_retried()
    {
        _takesJobs = false;
        _failEvents = 2;
        _failResults = 1;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-5", r.Id));
        _now += TimeSpan.FromMinutes(4);
        await r.TickAsync(CancellationToken.None);
        for (int i = 0; i < 400 && r.Running > 0; i++) await Task.Delay(25);
        var ok = Ops("job_result");
        Assert.Equal(2, ok.Count); // one failure, then sent
        Assert.Equal("completed", (string)ok[^1]["result"]!["status"]!);
        var seqs = Ops("job_post_events").SelectMany(e => e["batches"]![0]!["batch"]!["events"]!.AsArray()).Select(e => (int)e!["sequence"]!).Distinct().ToList();
        Assert.Contains(1, seqs); // the failed first batch was sent again
    }

    [Fact]
    public async Task Result_kept_in_the_outbox_until_it_is_sent()
    {
        _takesJobs = false;
        _failResults = 4;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-6", r.Id));
        _now += TimeSpan.FromMinutes(4);
        await r.TickAsync(CancellationToken.None);
        for (int i = 0; i < 800 && r.Running > 0; i++) await Task.Delay(25);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "reserve", "outbox")));
        await Step(r, TimeSpan.FromSeconds(10));
        for (int i = 0; i < 200 && Directory.GetFiles(Path.Combine(_dir, "reserve", "outbox")).Length > 0; i++) await Task.Delay(25);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "reserve", "outbox")));
        Assert.Equal(5, Ops("job_result").Count);
        // never run again, also when the server offers it again
        _jobs.Enqueue(Job("JOB-6", r.Id));
        await Step(r, TimeSpan.FromSeconds(10));
        Assert.Equal(5, Ops("job_result").Count);
    }

    [Fact]
    public void Jobs_of_another_or_an_unknown_environment_are_refused()
    {
        var req = (JsonObject)Job("JOB-7", "home-main")["request"]!;
        Assert.Null(ReserveExecutor.Refusal(req, "owner1"));
        Assert.NotNull(ReserveExecutor.Refusal(req, ""));
        req["environmentId"] = "member1";
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
        req["environmentId"] = "";
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
    }

    [Fact]
    public async Task Unreadable_bridge_config_runs_nothing()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ broken");
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-8", r.Id));
        await Step(r, TimeSpan.FromMinutes(4));
        Assert.Empty(Ops("job_result"));
        Assert.Single(Ops("job_reject"));
    }

    [Fact]
    public void Only_final_refusals_drop_a_result()
    {
        Assert.True(ReserveExecutor.Permanent("HTTP 409 {}"));
        Assert.False(ReserveExecutor.Permanent("HTTP 429 slow down"));
        Assert.False(ReserveExecutor.Permanent("HTTP 503"));
    }

    [Fact]
    public async Task Without_drive_the_local_session_is_not_handed_back()
    {
        Directory.Delete(Path.Combine(_dir, "drive"), true);
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-9", r.Id, op: "resume", session: "S-OLD"));
        await Step(r, TimeSpan.FromMinutes(4));
        var result = Ops("job_result").Single()["result"]!;
        Assert.Equal("completed", (string)result["status"]!);
        Assert.Null(result["sessionId"]);
        Assert.DoesNotContain("--resume", _lastArgs);
        Assert.Contains(Path.Combine(_dir, "reserve"), _lastCwd);
    }

    [Fact]
    public void Path_like_ids_are_refused()
    {
        var req = (JsonObject)Job("JOB-10", "home-main")["request"]!;
        req["chatId"] = "..\\..\\Windows";
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
        req["chatId"] = "CHAT-1";
        req["projectId"] = "C:\\x";
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
    }

    [Fact]
    public void Restricted_chats_are_left_to_bridge()
    {
        var req = (JsonObject)Job("JOB-11", "home-main")["request"]!;
        req["permissions"] = new JsonObject { ["files"] = "write", ["shell"] = true, ["web"] = true };
        Assert.Null(ReserveExecutor.Refusal(req, "owner1"));
        req["permissions"] = new JsonObject { ["files"] = "read", ["shell"] = true, ["web"] = true };
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
        req["permissions"] = new JsonObject { ["files"] = "write", ["shell"] = false, ["web"] = true };
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
        req["permissions"] = new JsonObject { ["files"] = "write", ["shell"] = true, ["web"] = false };
        Assert.NotNull(ReserveExecutor.Refusal(req, "owner1"));
    }

    [Fact]
    public async Task Job_with_a_result_in_the_outbox_is_not_run_after_a_restart()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "reserve", "outbox"));
        File.WriteAllText(Path.Combine(_dir, "reserve", "outbox", "JOB-12.json"), "{\"jobId\":\"JOB-12\",\"seq\":3,\"body\":{\"op\":\"job_result\"}}");
        _failResults = 100;
        _takesJobs = false;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-12", r.Id));
        await Step(r, TimeSpan.FromMinutes(4));
        Assert.Equal("", _lastStdin);
    }

    [Fact]
    public void Interrupted_delivery_goes_to_the_outbox_on_restart()
    {
        string box = Path.Combine(_dir, "reserve", "outbox");
        Directory.CreateDirectory(box);
        File.WriteAllText(Path.Combine(box, "JOB-13.sending"), "{}");
        Make();
        Assert.True(File.Exists(Path.Combine(box, "JOB-13.json")));
        Assert.False(File.Exists(Path.Combine(box, "JOB-13.sending")));
    }

    [Fact]
    public async Task Agent_starts_only_after_the_server_has_the_claimed_event()
    {
        _takesJobs = false;
        _failEvents = 100;
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-14", r.Id));
        _now += TimeSpan.FromMinutes(4);
        await r.TickAsync(CancellationToken.None);
        for (int i = 0; i < 600 && r.Running > 0; i++) await Task.Delay(25);
        Assert.Equal("", _lastStdin);
        Assert.Empty(Ops("job_result"));
    }

    [Fact]
    public void A_job_left_running_by_an_exit_gets_a_failed_result()
    {
        string run = Path.Combine(_dir, "reserve", "running");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "JOB-15.json"), new JsonObject { ["host"] = "desk-x", ["request"] = Job("JOB-15", "desk-x")["request"]!.DeepClone(), ["seq"] = 4 }.ToJsonString());
        Make();
        Assert.False(File.Exists(Path.Combine(run, "JOB-15.json")));
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "reserve", "outbox", "JOB-15.json")))!;
        Assert.Equal("failed", (string)entry["body"]!["result"]!["status"]!);
    }

    [Fact]
    public async Task Paused_reserve_runs_out_its_own_queue()
    {
        var r = Make();
        await Step(r, TimeSpan.Zero);
        _jobs.Enqueue(Job("JOB-16", r.Id));
        await Step(r, TimeSpan.FromSeconds(61));
        Assert.False(r.Active);
        Assert.Single(Ops("job_result"));
        Assert.DoesNotContain(Ops("job_claim"), c => (string)c["host"]! == "home-main");
    }

    [Fact]
    public void Configured_permissions_are_kept()
    {
        var a = ReserveExecutor.StreamJson(new() { "-p", "--output-format", "json", "--permission-mode", "default", "--allowedTools", "Read" });
        Assert.Equal(new[] { "-p", "--output-format", "stream-json", "--permission-mode", "default", "--allowedTools", "Read", "--verbose" }, a);
        Assert.DoesNotContain("bypassPermissions", a);
    }

    [Fact]
    public void Failed_result_line_is_an_error()
    {
        string final = "", s = "", m = "";
        bool ok = false, seen = false;
        JsonObject? u = null;
        var errs = new JsonArray();
        ReserveExecutor.ParseLine("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"limit\"}", ref final, ref s, ref m, ref ok, ref seen, ref u, errs);
        Assert.True(seen);
        Assert.False(ok);
        Assert.Single(errs);
    }
}
