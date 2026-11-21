// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CO.Core.Doctor;

// 21.11 (owner): CO Desktop on the computer of a Bridge runs Claude Code itself while that Bridge takes no jobs.
// It is an executor of its own, desk-<machine> (Freedom F12, db.php executor_put with the host key of Bridge from
// Credential Manager), registered all the time but 'paused' while the Bridge is fine, so CO Web knows it from the start.
// It becomes 'running' after the server has seen the Bridge not taking jobs (executors_host takesJobs = false) for
// ActivateAfter, and then also claims the jobs already queued for the Bridge on this machine (they wait there since
// CO Web 0.110.0). Same protocol as Bridge: job_claim, job_post_events, job_result / job_reject; the server writes the
// answer into the chat. Claude only, text jobs of the database queue, no restricted or isolated chats, and only the jobs of
// the owner: the device function gives the environment only when the person signed in to CO Desktop is the owner (App.cs),
// since the agent runs under this Windows account with the owner's keyring and without Bridge's member isolation.
// When the Bridge takes jobs again the reserve claims nothing new and finishes what it runs.
public sealed class ReserveExecutor
{
    public static readonly TimeSpan ActivateAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ActivateAfterUpdating = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private const int MaxRunning = 2;
    public const string Version = "1.0.0";

    // db.php call: body, whether it goes gzipped ({"gz": base64(gzip(json))}), answer (throws on HTTP errors)
    public delegate Task<JsonObject> DbCall(JsonObject body, bool gz, CancellationToken ct);
    // runs the CLI: exe, args, cwd, stdin text, each stdout line, cancel -> exit code
    public delegate Task<int> CliRun(string exe, IReadOnlyList<string> args, string cwd, string stdin, Action<string> onLine, CancellationToken ct);

    private readonly BridgeLayout _l;
    private readonly DbCall _db;
    private readonly CliRun _cli;
    private readonly Func<(string DeviceId, string Name, string Env)> _device;
    private readonly string _dataDir;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    // jobs of the Bridge queue the reserve does not run (codex, files, ...): never started; the server offers them again
    // after 120 s without events, so Bridge gets them back when it returns
    private readonly HashSet<string> _skipped = new();
    private DateTime _downSince = DateTime.MinValue, _lastCheck = DateTime.MinValue, _lastBeat = DateTime.MinValue, _lastDrain = DateTime.MinValue;
    private string _bridgeState = "";
    private bool _active, _beatRefused;

    public string Id { get; }
    public bool Active { get { lock (_sync) return _active; } }
    public int Running { get { lock (_sync) return _running.Count; } }
    public Func<bool> Enabled { get; set; } = () => true;

    public ReserveExecutor(BridgeLayout layout, DbCall db, CliRun cli, Func<(string, string, string)> device, string dataDir,
        Action<string>? log = null, Func<DateTime>? now = null)
    {
        _l = layout;
        _db = db;
        _cli = cli;
        _device = device;
        _dataDir = dataDir;
        _log = log ?? (_ => { });
        _now = now ?? (() => DateTime.UtcNow);
        Id = IdFor(Environment.MachineName);
        // results whose delivery a crash or exit interrupted go to the outbox
        try
        {
            if (Directory.Exists(OutboxDir))
                foreach (string f in Directory.GetFiles(OutboxDir, "*.sending"))
                    File.Move(f, Path.ChangeExtension(f, ".json"), true);
        }
        catch (Exception) { }
        CloseInterrupted();
    }

    // desk-<machine>: lower case, [a-z0-9-], at most 32 characters (db.php EXEC_ID_RE)
    public static string IdFor(string machine)
    {
        string m = Regex.Replace((machine ?? "").ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        if (m.Length == 0) m = "pc";
        string id = "desk-" + m;
        return id.Length > 32 ? id[..32].TrimEnd('-') : id;
    }

    public Task RunAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log("Reserve: " + Cut(ex.Message, 200)); }
            try { await Task.Delay(TimeSpan.FromSeconds(_active ? 5 : 10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }, ct);

    // One step: check the Bridge (every 30 s), beat (every 30 s or on a change), claim while active.
    public async Task TickAsync(CancellationToken ct)
    {
        DateTime now = _now();
        // the outbox goes on in the background: its network waits never delay cancellations or claims
        if (Interlocked.CompareExchange(ref _outboxBusy, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                try { await SendOutboxAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _log("Reserve: outbox: " + Cut(ex.Message, 120)); }
                finally { Interlocked.Exchange(ref _outboxBusy, 0); }
            }, CancellationToken.None);
        bool changed = false;
        if (now - _lastCheck >= CheckEvery)
        {
            _lastCheck = now;
            changed = await CheckBridgeAsync(now, ct).ConfigureAwait(false);
        }
        if (changed || now - _lastBeat >= BeatEvery)
        {
            _lastBeat = now;
            await BeatAsync(ct).ConfigureAwait(false);
        }
        // nothing is claimed before CO Desktop knows whose computer it is (Refusal needs the environment)
        bool can = Running < MaxRunning && !_beatRefused && _device().Env.Length > 0;
        if (can && Active)
            await ClaimAsync(true, ct).ConfigureAwait(false);
        else if (can && now - _lastDrain >= TimeSpan.FromSeconds(60))
        {
            // paused: jobs still queued for the reserve itself are run out (once a minute: no load on the database)
            _lastDrain = now;
            await ClaimAsync(false, ct).ConfigureAwait(false);
        }
        else if (Running > 0)
            await PollCancelsAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> CheckBridgeAsync(DateTime now, CancellationToken ct)
    {
        bool takes;
        string state;
        var list = await _db(new JsonObject { ["op"] = "executors_host" }, true, ct).ConfigureAwait(false);
        var row = (list["executors"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => Str(e, "id") == _l.HostId);
        takes = row != null && row["takesJobs"] is JsonValue tv && tv.TryGetValue(out bool b) && b;
        state = row == null ? "unknown" : Str(row, "state");
        bool wasActive;
        lock (_sync)
        {
            wasActive = _active;
            _bridgeState = state;
            if (takes) _downSince = DateTime.MinValue;
            else if (_downSince == DateTime.MinValue) _downSince = now;
            TimeSpan need = state == "updating" ? ActivateAfterUpdating : ActivateAfter;
            _active = Enabled() && !takes && now - _downSince >= need;
        }
        if (_active != wasActive)
            _log(_active
                ? $"Reserve: Bridge {_l.HostId} takes no jobs since {_downSince:HH:mm} UTC ({state}); {Id} runs Claude Code here"
                : $"Reserve: Bridge {_l.HostId} takes jobs again; {Id} claims nothing new ({Running} running)");
        return _active != wasActive;
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        var (deviceId, name, env) = _device();
        string machine = Environment.MachineName;
        string claude = ClaudeExe();
        var agents = new JsonObject();
        if (claude.Length > 0)
            agents["claude"] = new JsonObject
            {
                ["displayName"] = "Claude", ["modes"] = new JsonArray("chat", "cowork"), ["models"] = new JsonArray(),
                ["efforts"] = new JsonArray("low", "medium", "high", "xhigh", "max"),
            };
        string[] caps = { "protocol-v3", "conversation-resume", "modes", "model-select", "effort-select", "cancel", "usage", "db-queue" };
        string state = Active && claude.Length > 0 ? "running" : "paused";
        var devices = new JsonArray();
        if (deviceId.Length > 0)
            devices.Add(new JsonObject { ["deviceId"] = deviceId, ["name"] = name.Length > 0 ? name : machine, ["environmentId"] = env, ["online"] = true });
        var owner = new JsonObject
        {
            ["schemaVersion"] = 3, ["hostId"] = Id, ["updatedUtc"] = _now().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["state"] = state, ["machineName"] = machine, ["bridgeVersion"] = "co-desktop-reserve " + Version,
            ["reserveFor"] = _l.HostId, ["protocolMinor"] = 2, ["capabilities"] = new JsonArray(caps.Select(c => (JsonNode)c).ToArray()),
            ["agents"] = agents, ["devices"] = devices, ["runningJobs"] = Running,
        };
        var labels = new JsonObject
        {
            ["agents"] = new JsonArray(agents.Select(a => (JsonNode)a.Key).ToArray()),
            ["capabilities"] = new JsonArray(caps.Select(c => (JsonNode)c).ToArray()),
            ["devices"] = devices.Count,
        };
        // only the environment of the person at this computer (the owner of the Bridge key)
        if (env.Length > 0) labels["serves"] = new JsonArray(env);
        var body = new JsonObject
        {
            ["op"] = "executor_put", ["host"] = Id, ["kind"] = "bridge", ["title"] = "CO Desktop reserve (" + machine + ")",
            ["os"] = "windows", ["machine"] = machine, ["version"] = "co-desktop-reserve " + Version, ["ownerEnv"] = env,
            ["state"] = state, ["labels"] = labels, ["owner"] = owner,
        };
        try
        {
            var r = await _db(body, true, ct).ConfigureAwait(false);
            if (r["ok"] is JsonValue ok && ok.TryGetValue(out bool okb) && okb) _beatRefused = false;
            else { _beatRefused = true; _log("Reserve: executor_put refused: " + Cut(r.ToJsonString(), 200)); }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("409") || ex.Message.Contains("400"))
        {
            _beatRefused = true;
            _log("Reserve: executor " + Id + " refused by the server: " + Cut(ex.Message, 200));
        }
    }

    private async Task ClaimAsync(bool bridgeQueue, CancellationToken ct)
    {
        // own queue first, then (while active) the jobs that wait for the Bridge of this machine
        foreach (string host in bridgeQueue ? new[] { Id, _l.HostId } : new[] { Id })
        {
            if (Running >= MaxRunning) break;
            var r = await _db(new JsonObject
            {
                ["op"] = "job_claim", ["host"] = host, ["max"] = host == Id ? 1 : 3, ["machine"] = Environment.MachineName,
            }, false, ct).ConfigureAwait(false);
            Cancel(r);
            foreach (var j in (r["jobs"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                string jobId = Str(j, "jobId");
                JsonObject? req = j["request"] as JsonObject;
                if (req == null && j["request"] is JsonValue s && s.TryGetValue(out string? text)) req = JsonNode.Parse(text) as JsonObject;
                if (jobId.Length == 0 || req == null) continue;
                lock (_sync)
                    if (_skipped.Contains(jobId) || _running.ContainsKey(jobId)) continue;
                // its result waits in the outbox (also from before a restart of CO Desktop): never run again
                if (IdOk.IsMatch(jobId) && (File.Exists(Path.Combine(OutboxDir, jobId + ".json")) || File.Exists(Path.Combine(OutboxDir, jobId + ".sending")))) { lock (_sync) _skipped.Add(jobId); continue; }
                if (Running >= MaxRunning) continue; // offered again after 120 s
                Start(host, jobId, req, ct);
            }
        }
    }

    private async Task PollCancelsAsync(CancellationToken ct)
    {
        foreach (string host in new[] { Id, _l.HostId })
        {
            // max 0: only the cancel list of the jobs this host runs
            var r = await _db(new JsonObject { ["op"] = "job_claim", ["host"] = host, ["max"] = 0, ["machine"] = Environment.MachineName }, false, ct).ConfigureAwait(false);
            Cancel(r);
        }
    }

    private void Cancel(JsonObject r)
    {
        foreach (var c in (r["cancel"] as JsonArray) ?? new JsonArray())
        {
            string id = c is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
            lock (_sync)
                if (_running.TryGetValue(id, out var cts)) { _log("Reserve: cancel " + id); cts.Cancel(); }
        }
    }

    private void Start(string host, string jobId, JsonObject req, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync) _running[jobId] = cts;
        _ = Task.Run(async () =>
        {
            try { await RunJobAsync(host, jobId, req, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log($"Reserve: job {jobId} failed: {Cut(ex.Message, 200)}"); }
            finally
            {
                lock (_sync) _running.Remove(jobId);
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    // ids of AI Workbench (PROJECT, CHAT-..., JOB-...): letters, digits, '_' and '-', never a path
    private static readonly Regex IdOk = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant);

    // Why the reserve does not run a job, or null.
    public static string? Refusal(JsonObject req, string ownEnv)
    {
        string agent = Str(req, "agent");
        if (agent != "claude") return $"The CO Desktop reserve runs Claude only (not '{agent}'); the job waits for Bridge.";
        if (req["permissions"] is JsonObject p)
        {
            // the reserve has no permission mapping of its own: a chat with any restriction (read-only files, no shell, no
            // web, isolated) waits for Bridge, which applies it
            string files = Str(p, "files");
            if (files.Length > 0 && files != "write") return "Chats with restricted file access run only on Bridge.";
            if (p["shell"] is JsonValue sh && sh.TryGetValue(out bool shb) && !shb) return "Chats without shell run only on Bridge.";
            if (p["web"] is JsonValue wb && wb.TryGetValue(out bool wbb) && !wbb) return "Chats without web run only on Bridge.";
        }
        if (req["attachments"] is JsonArray a && a.Count > 0) return "Jobs with files run only on Bridge.";
        if (!IdOk.IsMatch(Str(req, "projectId")) || !IdOk.IsMatch(Str(req, "chatId")) || !IdOk.IsMatch(Str(req, "jobId")))
            return "Project, chat or job id is not a plain id.";
        string env = Str(req, "environmentId");
        if (ownEnv.Length == 0) return "The CO Desktop reserve does not know the environment of this computer yet.";
        if (env != ownEnv) return "The CO Desktop reserve runs only the jobs of " + ownEnv + ".";
        return null;
    }

    internal async Task RunJobAsync(string host, string jobId, JsonObject req, CancellationToken ct)
    {
        var ev = new Events(this, jobId);
        string? refusal = Refusal(req, _device().Env);
        string[]? baseArgs = refusal == null ? ClaudeArgs(Str(req, "mode") is { Length: > 0 } rm ? rm : "chat") : null;
        if (refusal == null && baseArgs == null) refusal = "The Bridge config (agents.claude arguments) cannot be read; the job waits for Bridge.";
        if (refusal != null)
        {
            // a job of the Bridge queue stays there (no events: offered again after 120 s); one addressed to the reserve is
            // rejected (job_reject is not a gzip op of db.php)
            _log($"Reserve: {jobId} not run: {refusal}");
            if (host == Id) await _db(new JsonObject { ["op"] = "job_reject", ["jobId"] = jobId, ["reason"] = refusal }, false, ct).ConfigureAwait(false);
            else lock (_sync) _skipped.Add(jobId);
            return;
        }
        string exe = ClaudeExe();
        DateTime started = _now();
        using var alive = new CancellationTokenSource();
        var beat = Task.Run(async () =>
        {
            // a claimed job without event batches for 120 s is offered again: a sign of life every 60 s from the claim on,
            // until the result is delivered (or kept in the outbox, which goes on with it)
            while (!alive.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), alive.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await ev.FlushOrBeatAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log($"Reserve: events of {jobId}: {Cut(ex.Message, 120)}"); }
            }
        });
        // a marker while it runs, written before the first event: if CO Desktop exits or crashes, the next start closes the
        // job with a failed result; without a marker the job is not taken (no event: the server offers it again)
        string marker = Path.Combine(RunningDir, jobId + ".json");
        try
        {
            Directory.CreateDirectory(RunningDir);
            File.WriteAllText(marker + ".tmp", new JsonObject { ["host"] = host, ["request"] = req.DeepClone(), ["seq"] = 1000000 }.ToJsonString()); // past any sequence of the run
            File.Move(marker + ".tmp", marker, true);
        }
        catch (Exception ex)
        {
            alive.Cancel();
            await beat.ConfigureAwait(false);
            _log($"Reserve: {jobId} not started: running marker not written ({Cut(ex.Message, 120)})");
            return;
        }
        // db.php offers a claimed job again only while it has no event at all (DB_CLAIM_LEASE_SECONDS): the agent starts
        // only after the server accepted the 'claimed' event, so no crash, exit or lost heartbeat can make it run twice
        ev.Add("bridge", "claimed", $"Job claimed by the CO Desktop reserve on {Environment.MachineName} (Bridge {_l.HostId} is {_bridgeState}).");
        for (int i = 0; i < 3 && !ev.Accepted; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3 * i), CancellationToken.None).ConfigureAwait(false);
            await ev.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        if (!ev.Accepted)
        {
            // not ours yet: the server offers it again after the lease, to whoever claims first
            alive.Cancel();
            await beat.ConfigureAwait(false);
            _log($"Reserve: {jobId} not started: the 'claimed' event did not reach the server");
            try { File.Delete(marker); } catch (Exception) { }
            return;
        }
        // from here on every way out ends with a result (setup uses no cancellable call and throws nothing)

        string mode = Str(req, "mode") is { Length: > 0 } m ? m : "chat";
        string model = Str(req, "model"), effort = Str(req, "effort");
        var conv = req["conversation"] as JsonObject;
        string op = conv != null ? Str(conv, "operation") : "new";
        string session = conv != null ? Str(conv, "sessionId") : "";
        string projectId = Str(req, "projectId"), chatId = Str(req, "chatId");

        // the chat scratch on Drive, as Bridge uses it (sessions can be resumed there); else a local folder
        string drive = _l.DriveRoot.Length > 0 && projectId.Length > 0 && chatId.Length > 0
            ? Path.Combine(_l.DriveRoot, "projects", projectId, "chats", chatId, "scratch") : "";
        bool onDrive = drive.Length > 0 && Directory.Exists(drive);
        string cwd = onDrive ? drive : Path.Combine(_dataDir, "reserve", chatId);
        string setupError;
        try
        {
            string root = Path.GetFullPath(onDrive ? _l.DriveRoot : Path.Combine(_dataDir, "reserve")) + Path.DirectorySeparatorChar;
            setupError = Path.GetFullPath(cwd).StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "" : "working folder outside " + root;
        }
        catch (Exception ex) { setupError = "working folder: " + ex.Message; }

        var args = new List<string>(baseArgs!);
        var cfg = Config();
        if (model.Length > 0) args.AddRange(Expand(cfg, "modelArguments", model, "--model"));
        if (effort.Length > 0) args.AddRange(Expand(cfg, "effortArguments", effort, "--effort"));
        string prompt = Str(req, "instruction");
        string note = $"[CO Desktop reserve: Bridge '{_l.HostId}' of this computer takes no jobs ({_bridgeState}), so CO Desktop runs this job with Claude Code directly. "
            + (onDrive ? "" : "Google Drive is not available: the working directory is a local folder, files you write stay on this computer. ")
            + "No device tools of Bridge in this job.]\n";
        bool resumed = false;
        if (op == "resume" && session.Length > 0 && onDrive)
        {
            args.Add("--resume");
            args.Add(session);
            resumed = true;
        }
        else if (op == "resume")
            prompt = await HistoryAsync(chatId, CancellationToken.None).ConfigureAwait(false) + prompt;

        await ev.AddAsync("bridge", "started", $"Starting agent 'claude' (mode {mode}{(model.Length > 0 ? ", model " + model : "")}) in {cwd}{(resumed ? ", resuming the session" : "")}.", CancellationToken.None).ConfigureAwait(false);

        string final = "", sessionId = "", modelReported = "", error = "";
        bool success = false, sawResult = false;
        JsonObject? usage = null;
        var errors = new JsonArray();
        int exit = -1;
        bool timedOut = false, cancelled = false;
        int minutes = req["timeoutMinutes"] is JsonValue tmv && tmv.TryGetValue(out int tm) && tm > 0 ? tm : _l.MaxProcessMinutes;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        minutes = Math.Clamp(minutes, 1, 24 * 60);
        limit.CancelAfter(TimeSpan.FromMinutes(minutes));
        try
        {
            if (setupError.Length > 0) throw new InvalidOperationException(setupError);
            ct.ThrowIfCancellationRequested();
            if (exe.Length == 0) throw new InvalidOperationException("claude CLI not found (Bridge config agents.claude.executable, PATH, ~\\.local\\bin)");
            Directory.CreateDirectory(cwd);
            exit = await _cli(exe, args, cwd, note + prompt, line =>
            {
                ev.Add("agent", "stdout", line);
                ParseLine(line, ref final, ref sessionId, ref modelReported, ref success, ref sawResult, ref usage, errors);
            }, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut = !cancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            limit.Cancel(); // the agent; the heartbeat goes on until the result is delivered
        }
        if (cancelled && !ct.IsCancellationRequested) cancelled = false;
        string status = cancelled ? "cancelled" : timedOut ? "timeout" : sawResult && success && exit == 0 ? "completed" : "failed";
        if (status == "failed" && error.Length == 0)
            error = errors.Count > 0 ? errors[0]!.ToString() : sawResult ? "Claude Code reported an error" : $"Claude Code exited with {exit} without a result";
        if (status == "timeout") error = $"Agent exceeded timeout of {minutes} minutes.";
        if (status == "cancelled") error = "Cancellation requested by client.";
        await ev.AddAsync("bridge", status, status == "completed" ? "Job completed successfully." : error, CancellationToken.None).ConfigureAwait(false);
        await ev.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["schemaVersion"] = 3, ["hostId"] = Str(req, "hostId") is { Length: > 0 } h ? h : host, ["jobId"] = jobId,
            ["environmentId"] = Str(req, "environmentId"), ["projectId"] = projectId, ["chatId"] = chatId,
            ["branchId"] = Str(req, "branchId"), ["agent"] = "claude", ["conversationOperation"] = resumed ? "resume" : "new",
            ["status"] = status, ["startedUtc"] = started.ToString("o"), ["completedUtc"] = _now().ToString("o"),
            ["exitCode"] = exit, ["timedOut"] = timedOut, ["structuredFinalSeen"] = sawResult, ["structuredSuccess"] = success,
            // a session of a local folder cannot be resumed in the Drive scratch later (Bridge, or the reserve once Drive is
            // back): not handed back, so the next turn resumes the chat's own session there
            ["sessionId"] = sessionId.Length > 0 && onDrive ? sessionId : null, ["agentFinalResponse"] = final,
            ["agentErrorEvents"] = errors.Count, ["agentErrorMessages"] = errors.DeepClone(),
            ["error"] = status == "completed" ? null : error,
            ["artifacts"] = new JsonArray(), ["missingArtifacts"] = new JsonArray(), ["failedArtifacts"] = new JsonArray(),
            ["mode"] = mode, ["model"] = model.Length > 0 ? model : null, ["effort"] = effort.Length > 0 ? effort : null,
            ["modelReported"] = modelReported.Length > 0 ? modelReported : null, ["usage"] = usage,
            ["autoExport"] = false, ["exportSkipped"] = new JsonArray(), ["tainted"] = false,
            ["reserve"] = Id,
        };
        // the result must arrive: a few tries (the hosting refuses a connect now and then), then the outbox on disk that
        // every tick sends again; the job is never run here again
        lock (_sync) _skipped.Add(jobId);
        var payload = new JsonObject { ["op"] = "job_result", ["host"] = host, ["request"] = req.DeepClone(), ["result"] = result };
        // on disk before the first try (<jobId>.sending, the outbox pass leaves it alone): a crash during delivery keeps it;
        // a restart turns .sending into .json
        string file = Path.Combine(OutboxDir, jobId + ".json");
        bool onDisk = false;
        try
        {
            Directory.CreateDirectory(OutboxDir);
            File.WriteAllText(file + ".tmp", new JsonObject { ["jobId"] = jobId, ["seq"] = ev.Sequence, ["body"] = payload.DeepClone() }.ToJsonString());
            File.Move(file + ".tmp", Path.ChangeExtension(file, ".sending"), true);
            onDisk = true;
        }
        catch (Exception ex) { _log($"Reserve: result of {jobId} not written to the outbox: {Cut(ex.Message, 120)}"); }
        bool sent = false;
        for (int i = 0; i < 4 && !sent; i++)
        {
            try
            {
                await _db((JsonObject)payload.DeepClone(), true, CancellationToken.None).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception ex)
            {
                _log($"Reserve: result of {jobId} not sent ({Cut(ex.Message, 120)})");
                if (i < 3) await Task.Delay(TimeSpan.FromSeconds(3 * (i + 1)), CancellationToken.None).ConfigureAwait(false);
            }
        }
        alive.Cancel();
        await beat.ConfigureAwait(false);
        string sending = Path.ChangeExtension(file, ".sending");
        bool durable = sent;
        if (sent)
        {
            try { if (onDisk) File.Delete(sending); } catch (Exception) { }
        }
        else
        {
            var entry = new JsonObject { ["jobId"] = jobId, ["seq"] = ev.Sequence, ["body"] = payload };
            try
            {
                // the outbox pass sends it from now on (renamed whole: a tick never reads half a file)
                if (!onDisk) throw new IOException("not on disk");
                File.WriteAllText(file + ".tmp", entry.ToJsonString());
                File.Move(file + ".tmp", file, true);
                File.Delete(sending);
                durable = true;
                _log($"Reserve: result of {jobId} kept in the outbox");
            }
            catch (Exception ex)
            {
                lock (_sync) _memOutbox.Add(entry);
                _log($"Reserve: result of {jobId} kept in memory (outbox: {Cut(ex.Message, 120)})");
            }
        }
        // the marker goes only once the result is delivered or on disk (else a restart closes the job from it)
        if (durable) try { File.Delete(marker); } catch (Exception) { }
        _log($"Reserve: {jobId} {status} ({(_now() - started).TotalSeconds:0} s)");
    }

    private string RunningDir => Path.Combine(_dataDir, "reserve", "running");

    // Jobs a previous CO Desktop left running (exit, crash): a failed result for each into the outbox.
    private void CloseInterrupted()
    {
        string[] files;
        try { files = Directory.Exists(RunningDir) ? Directory.GetFiles(RunningDir, "*.json") : Array.Empty<string>(); }
        catch (Exception ex) { _log("Reserve: running markers not read: " + Cut(ex.Message, 120)); return; }
        foreach (string f in files)
        {
            try
            {
                string jobId = Path.GetFileNameWithoutExtension(f);
                var m = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
                if (m?["request"] is JsonObject req && IdOk.IsMatch(jobId) && !File.Exists(Path.Combine(OutboxDir, jobId + ".json")))
                {
                    var result = new JsonObject
                    {
                        ["schemaVersion"] = 3, ["hostId"] = Str(req, "hostId"), ["jobId"] = jobId, ["environmentId"] = Str(req, "environmentId"),
                        ["projectId"] = Str(req, "projectId"), ["chatId"] = Str(req, "chatId"), ["branchId"] = Str(req, "branchId"),
                        ["agent"] = "claude", ["status"] = "failed", ["completedUtc"] = _now().ToString("o"), ["exitCode"] = -1,
                        ["agentFinalResponse"] = "", ["sessionId"] = null, ["agentErrorMessages"] = new JsonArray(),
                        ["error"] = "CO Desktop exited while the reserve ran this job; send the message again.",
                        ["artifacts"] = new JsonArray(), ["missingArtifacts"] = new JsonArray(), ["failedArtifacts"] = new JsonArray(),
                        ["reserve"] = Id,
                    };
                    var entry = new JsonObject
                    {
                        ["jobId"] = jobId, ["seq"] = (m["seq"] is JsonValue sv && sv.TryGetValue(out int si) ? si : 1000000) + 1,
                        ["body"] = new JsonObject { ["op"] = "job_result", ["host"] = Str(m, "host"), ["request"] = req.DeepClone(), ["result"] = result },
                    };
                    Directory.CreateDirectory(OutboxDir);
                    string file = Path.Combine(OutboxDir, jobId + ".json");
                    File.WriteAllText(file + ".tmp", entry.ToJsonString());
                    File.Move(file + ".tmp", file, true);
                    _log($"Reserve: {jobId} was interrupted by an exit of CO Desktop; a failed result goes out");
                }
                File.Delete(f);
            }
            catch (Exception ex) { _log("Reserve: interrupted job not closed: " + Cut(ex.Message, 120)); }
        }
    }

    private string OutboxDir => Path.Combine(_dataDir, "reserve", "outbox");

    // a refusal for good (bad request, forbidden, unknown or finished job); 429 and 5xx are tried again
    public static bool Permanent(string message) =>
        Regex.IsMatch(message, @"^HTTP (400|403|404|409|410|422)\b");

    // results that could not even be written to disk: sent from memory like the outbox (lost only if CO Desktop exits)
    private readonly List<JsonObject> _memOutbox = new();
    private int _outboxBusy;

    private async Task SendOutboxAsync(CancellationToken ct)
    {
        // (entry, file or null for one kept in memory)
        var all = new List<(JsonObject Entry, string? File)>();
        lock (_sync) all.AddRange(_memOutbox.Select(e => (e, (string?)null)));
        if (Directory.Exists(OutboxDir))
            foreach (string f in Directory.GetFiles(OutboxDir, "*.json"))
            {
                JsonObject? entry;
                try { entry = JsonNode.Parse(File.ReadAllText(f)) as JsonObject; }
                catch (Exception) { entry = null; }
                if (entry?["body"] is not JsonObject) { File.Move(f, f + ".bad", true); continue; }
                all.Add((entry, f));
            }
        if (all.Count == 0) return;

        // 1. one call with a sign of life for every waiting result, again every 50 s while they go out one by one, so no
        // claim expires meanwhile
        if (await BeatAllAsync(all, ct).ConfigureAwait(false) == false) { Save(all); return; } // still no connection
        DateTime lastBeat = _now();

        // 2. the results, each on its own
        for (int k = 0; k < all.Count; k++)
        {
            var (entry, file) = all[k];
            if (_now() - lastBeat > TimeSpan.FromSeconds(50))
            {
                await BeatAllAsync(all.Skip(k).ToList(), ct).ConfigureAwait(false);
                lastBeat = _now();
            }
            bool? done = await SendResultAsync(entry, ct).ConfigureAwait(false);
            if (done == null) { Save(new() { (entry, file) }); continue; }
            if (file == null) { lock (_sync) _memOutbox.Remove(entry); }
            else if (done == true) File.Delete(file);
            else File.Move(file, file + ".refused", true);
        }
    }

    // false: no connection
    private async Task<bool> BeatAllAsync(List<(JsonObject Entry, string? File)> all, CancellationToken ct)
    {
        var batches = new JsonArray();
        foreach (var (entry, _) in all)
        {
            int seq = (entry["seq"] is JsonValue sv && sv.TryGetValue(out int si) ? si : 0) + 1;
            entry["seq"] = seq;
            batches.Add(new JsonObject
            {
                ["jobId"] = Str(entry, "jobId"),
                ["batch"] = new JsonObject
                {
                    ["schemaVersion"] = 2, ["firstSequence"] = seq, ["lastSequence"] = seq,
                    ["events"] = new JsonArray(new JsonObject
                    {
                        ["schemaVersion"] = 2, ["sequence"] = seq, ["createdUtc"] = _now().ToString("o"), ["source"] = "bridge",
                        ["type"] = "heartbeat", ["content"] = "The CO Desktop reserve has the result and sends it again.",
                    }),
                },
            });
        }
        try { await _db(new JsonObject { ["op"] = "job_post_events", ["batches"] = batches }, true, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (Permanent(ex.Message)) { } // the results decide
        catch (Exception) { return false; }
        return true;
    }

    // the sequence numbers used so far stay with the entries
    private static void Save(List<(JsonObject Entry, string? File)> list)
    {
        foreach (var (entry, file) in list)
            if (file != null)
                try { File.WriteAllText(file + ".tmp", entry.ToJsonString()); File.Move(file + ".tmp", file, true); } catch (Exception) { }
    }

    // true: delivered; false: refused for good; null: no connection (try again next tick)
    private async Task<bool?> SendResultAsync(JsonObject entry, CancellationToken ct)
    {
        string jobId = Str(entry, "jobId");
        try { await _db((JsonObject)entry["body"]!.DeepClone(), true, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (Permanent(ex.Message))
        {
            // refused for good (the job was cancelled or finished elsewhere): kept aside, not retried
            _log($"Reserve: result of {jobId} refused: {Cut(ex.Message, 120)}");
            return false;
        }
        catch (Exception) { return null; }
        _log($"Reserve: result of {jobId} sent from the outbox");
        return true;
    }

    // Claude stream-json: init (session, model), result (text, success, usage), errors.
    public static void ParseLine(string line, ref string final, ref string session, ref string model, ref bool success,
        ref bool sawResult, ref JsonObject? usage, JsonArray errors)
    {
        if (line.Length == 0 || line[0] != '{') return;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { return; }
        if (o == null) return;
        string type = Str(o, "type");
        if (Str(o, "session_id") is { Length: > 0 } sid) session = sid;
        if (type == "system" && Str(o, "subtype") == "init" && Str(o, "model") is { Length: > 0 } mm) model = mm;
        if (type != "result") return;
        sawResult = true;
        bool isError = o["is_error"] is JsonValue ie && ie.TryGetValue(out bool b) && b;
        success = !isError && Str(o, "subtype") is "success" or "";
        final = Str(o, "result");
        if (isError && final.Length > 0) errors.Add(final);
        var u = o["usage"] as JsonObject;
        usage = new JsonObject
        {
            ["inputTokens"] = Num(u, "input_tokens"), ["cachedInputTokens"] = Num(u, "cache_read_input_tokens"),
            ["cacheCreationInputTokens"] = Num(u, "cache_creation_input_tokens"), ["outputTokens"] = Num(u, "output_tokens"),
            ["reasoningOutputTokens"] = 0,
            ["costUsd"] = o["total_cost_usd"] is JsonValue c && c.TryGetValue(out double cd) ? cd : null,
            ["durationMs"] = Num(o, "duration_ms"), ["numTurns"] = Num(o, "num_turns"),
        };
    }

    private static long Num(JsonObject? o, string k)
    {
        if (o == null || o[k] is not JsonValue v) return 0;
        if (v.TryGetValue(out long l)) return l;
        return v.TryGetValue(out double d) ? (long)d : 0;
    }

    // The latest messages of the chat (db.php chat_read with the host key) for a session that cannot be resumed here.
    private async Task<string> HistoryAsync(string chatId, CancellationToken ct)
    {
        if (chatId.Length == 0) return "";
        try
        {
            var r = await _db(new JsonObject { ["op"] = "chat_read", ["chatId"] = chatId }, true, ct).ConfigureAwait(false);
            var msgs = (r["messages"] as JsonArray)?.OfType<JsonObject>().TakeLast(12).ToList() ?? new();
            if (msgs.Count == 0) return "";
            var sb = new StringBuilder("[The session of this chat cannot be resumed here. Its latest messages, for context:]\n");
            foreach (var m in msgs)
                sb.Append('#').Append(m["seq"]).Append(' ').Append(Str(m, "type") == "user_message" ? "User" : "Agent").Append(": ")
                  .Append(Cut(Str(m, "text"), 2000)).Append("\n\n");
            return sb.Append("[End of the latest messages. Current request:]\n").ToString();
        }
        catch (Exception ex)
        {
            _log("Reserve: chat history not read: " + Cut(ex.Message, 120));
            return "";
        }
    }

    // ------------------------------------------------------------------ Bridge config

    private JsonObject? Config()
    {
        try { return JsonNode.Parse(File.ReadAllText(_l.ConfigPath), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject; }
        catch (Exception) { return null; }
    }

    // The arguments Bridge uses for this mode (config.json agents.claude.modes.<mode>.arguments, else agents.claude.arguments),
    // with the output switched to stream-json (the reserve reads it); the permissions configured there are kept as they
    // are. null (the job is not run) when the config cannot be read or has no Claude arguments: never wider defaults.
    internal string[]? ClaudeArgs(string mode)
    {
        var c = (Config()?["agents"] as JsonObject)?["claude"] as JsonObject;
        JsonArray? a = ((c?["modes"] as JsonObject)?[mode] as JsonObject)?["arguments"] as JsonArray ?? c?["arguments"] as JsonArray;
        var list = a?.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "").Where(s => s.Length > 0).ToList();
        if (list == null || list.Count == 0) return null;
        return StreamJson(list);
    }

    public static string[] StreamJson(List<string> list)
    {
        var o = new List<string>(list);
        int i = o.IndexOf("--output-format");
        if (i >= 0 && i + 1 < o.Count) o[i + 1] = "stream-json";
        else if (i >= 0) o.Add("stream-json");
        else o.AddRange(new[] { "--output-format", "stream-json" });
        if (!o.Contains("--verbose")) o.Add("--verbose");
        if (!o.Contains("-p") && !o.Contains("--print")) o.Insert(0, "-p");
        return o.ToArray();
    }

    private static IEnumerable<string> Expand(JsonObject? cfg, string key, string value, string flag)
    {
        var a = ((cfg?["agents"] as JsonObject)?["claude"] as JsonObject)?[key] as JsonArray;
        var list = a?.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? (s ?? "").Replace("{value}", value) : "").Where(s => s.Length > 0).ToList();
        return list is { Count: > 0 } ? list : new[] { flag, value };
    }

    private string ClaudeExe()
    {
        if (_l.ClaudeExe.Length > 0 && File.Exists(_l.ClaudeExe)) return _l.ClaudeExe;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "claude.exe");
                if (dir.Trim().Length > 0 && File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return "";
    }

    // ------------------------------------------------------------------ events

    // Event batches of one job (db.php job_post_events; the format of Bridge, schemaVersion 2).
    private sealed class Events
    {
        private readonly ReserveExecutor _x;
        private readonly string _jobId;
        private readonly List<JsonObject> _pending = new();
        private readonly object _sync = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private int _seq;
        private DateTime _lastSent = DateTime.MinValue;
        private volatile bool _accepted;

        // the server has at least one batch of this job (it is not offered to anyone again)
        public bool Accepted => _accepted;

        public int Sequence { get { lock (_sync) return _seq; } }

        public Events(ReserveExecutor x, string jobId) { _x = x; _jobId = jobId; }

        public void Add(string source, string type, string content)
        {
            lock (_sync)
                _pending.Add(new JsonObject
                {
                    ["schemaVersion"] = 2, ["sequence"] = ++_seq, ["createdUtc"] = _x._now().ToString("o"),
                    ["source"] = source, ["type"] = type, ["content"] = content.Length > 60000 ? content[..60000] : content,
                });
            if (_x._now() - _lastSent > TimeSpan.FromSeconds(2)) _ = FlushAsync(CancellationToken.None);
        }

        public async Task AddAsync(string source, string type, string content, CancellationToken ct)
        {
            Add(source, type, content);
            await FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task FlushOrBeatAsync(CancellationToken ct)
        {
            bool empty;
            lock (_sync) empty = _pending.Count == 0;
            if (empty) Add("bridge", "heartbeat", "The CO Desktop reserve is still running the agent.");
            await FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task FlushAsync(CancellationToken ct)
        {
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                List<JsonObject> batch;
                lock (_sync)
                {
                    if (_pending.Count == 0) return;
                    batch = new List<JsonObject>(_pending);
                    _pending.Clear();
                }
                _lastSent = _x._now();
                int first = (int)batch[0]["sequence"]!, last = (int)batch[^1]["sequence"]!;
                var body = new JsonObject
                {
                    ["op"] = "job_post_events",
                    ["batches"] = new JsonArray(new JsonObject
                    {
                        ["jobId"] = _jobId,
                        ["batch"] = new JsonObject
                        {
                            ["schemaVersion"] = 2, ["firstSequence"] = first, ["lastSequence"] = last,
                            ["events"] = new JsonArray(batch.Select(e => e.DeepClone()).ToArray()), // the batch may be sent again
                        },
                    }),
                };
                try
                {
                    await _x._db(body, true, ct).ConfigureAwait(false);
                    _accepted = true;
                }
                catch (Exception ex)
                {
                    // put back in front: the next flush sends them again (the server dedupes on firstSequence)
                    lock (_sync) _pending.InsertRange(0, batch);
                    _x._log($"Reserve: events of {_jobId} not sent: {Cut(ex.Message, 120)}");
                }
            }
            finally { _send.Release(); }
        }
    }

    // ------------------------------------------------------------------ transport and CLI

    // db.php with the push key of Bridge (Credential Manager), no redirects (the key goes only to the configured URL).
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };

    public static DbCall HostCall(BridgeLayout l, Func<string, string?> readSecret) => async (body, gz, ct) =>
    {
        if (l.DbUrl.Length == 0) throw new InvalidOperationException("no dbQueue.url in the Bridge config");
        string key = (readSecret(l.PushTarget) ?? "").Replace("\0", "").Trim();
        if (key.Length == 0) throw new InvalidOperationException("no push key in Credential Manager (" + l.PushTarget + ")");
        string json = body.ToJsonString();
        if (gz)
        {
            using var ms = new MemoryStream();
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(Encoding.UTF8.GetBytes(json));
            json = new JsonObject { ["gz"] = Convert.ToBase64String(ms.ToArray()) }.ToJsonString();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, l.DbUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-CO-Push-Key", key);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)resp.StatusCode + " " + Cut(text, 200));
        return JsonNode.Parse(text.Length > 0 ? text : "{}") as JsonObject ?? new JsonObject();
    };

    // Agents started by the reserve; killed when CO Desktop exits (App.OnExit, process exit), so a job never goes on
    // without its heartbeat and is never run twice.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> Live = new();

    static ReserveExecutor() => AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();

    public static void KillAll()
    {
        foreach (var p in Live.Values)
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    // claude.exe with the prompt on stdin, stdout line by line; a cancel kills the process tree.
    public static Task<int> ProcessRun(string exe, IReadOnlyList<string> args, string cwd, string stdin, Action<string> onLine, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["AIWB_RESERVE"] = "1";
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
            Live[p.Id] = p;
            // ends with CO Desktop, also on a crash or a forced exit (job object, kill on close); refused: not run at all
            if (OperatingSystem.IsWindows() && !KillOnCloseJob.Add(p))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                Live.TryRemove(p.Id, out _);
                throw new InvalidOperationException("the agent could not be tied to the life of CO Desktop (job object)");
            }
            using var gone = new Gone(() => Live.TryRemove(p.Id, out _));
            void KillTree() { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { } }
            using var reg = ct.Register(KillTree);
            var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                p.StandardInput.Close();
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
                    onLine(line);
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the agent never keeps running after the reserve gave the job up
                KillTree();
                try { await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                throw;
            }
            await err.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return p.ExitCode;
        }, CancellationToken.None);

    // ------------------------------------------------------------------ helpers

    private sealed class Gone(Action a) : IDisposable
    {
        public void Dispose() => a();
    }

    private static string Str(JsonObject? o, string k) =>
        o != null && o[k] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
