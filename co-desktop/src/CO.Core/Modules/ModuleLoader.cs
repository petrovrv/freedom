// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Core.Modules;

// Freedom F10 (SPEC §3.6, ext/1 device.tools): the module loader of a device (CO Desktop, CO Resident). It keeps the
// module list of the CO server (the same db.php modules_list CO Web reads), a per-device switch of each module with a
// device part (on by default unless the manifest says device.default "off"), and registers the tools of the active
// modules in the device catalog with their declared risk class, so every call goes through the policy of the core
// (confirmations, grants, taint, managed profile). A tool of a module that is switched off leaves the catalog and
// refuses with module_off. Two kinds of tools:
//   - script: device/<file> of the module, downloaded from module.php and checked against the sha256 of the list
//     before every run; it reads {"ext":1,"kind":"tool","tool","args","stateDir"} on stdin and answers on stdout;
//   - native: a part of the module compiled into the app (IModuleNativePart with the module id), e.g. a connector
//     that drives the CO browser. Its descriptor must carry the class the manifest declares.
// Device ops: calls of the module's settings page (ui.settings, shown by CO Web in this app) that act on this device;
// only ops the manifest declares (provides.deviceOps), answered by the native part or by the device.ops script.
// The core knows no module by name: ids, tools and ops all come from the list.

/// <summary>Part of a module compiled into the app; found by the module id of the server list.</summary>
public interface IModuleNativePart
{
    string Id { get; }
    /// <summary>The native tool for a deviceTools entry without "run"; null when this part has no such tool.</summary>
    IDeviceTool? NativeTool(string name) => null;
    /// <summary>A device op of the settings page; null = not handled here (the device.ops script answers).</summary>
    Task<JsonNode?>? DeviceOpAsync(string op, JsonElement args, CancellationToken ct) => null;
    /// <summary>The module became active (true) or inactive (false) on this device.</summary>
    void Activate(bool on) { }
}

/// <summary>Runs a device/ file of a module with a JSON request on stdin.</summary>
public interface IModuleRunner
{
    Task<ModuleRunResult> RunAsync(string file, string workDir, string stdin, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken ct);
}

public sealed record ModuleRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>State of a module with a device part on this device (Modules screen, "On this computer").</summary>
public sealed record ModuleDeviceState(string Id, string Title, string Version, bool Enabled, bool On, bool DefaultOn, bool Active,
    string Warning, IReadOnlyList<string> Tools, IReadOnlyList<string> Ops, string Problem);

public sealed class ModuleLoader
{
    public const int MaxOutput = 1 << 20;

    private readonly object _sync = new();
    private readonly ToolCatalog _catalog;
    private readonly ModuleFileCache _cache;
    private readonly IModuleSource? _source;
    private readonly IModuleRunner _runner;
    private readonly string _listFile, _switchFile, _platform;
    private readonly Dictionary<string, IModuleNativePart> _natives;
    private readonly Action<string> _log;
    private IReadOnlyList<ModuleEntry> _list = Array.Empty<ModuleEntry>();
    private readonly Dictionary<string, bool> _switches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Module, string Key)> _registered = new(StringComparer.Ordinal);   // tool -> module, spec key
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fileProblems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    /// <summary>The set of tools changed (publish the catalog again).</summary>
    public event Action? CatalogChanged;

    /// <summary>Every apply of a module list (new modules, settings pages, switches), also without a tool change.</summary>
    public event Action? Applied;

    /// <summary>Managed device (company profile): device ops that run a module's own script are refused, as class X is.</summary>
    public Func<bool> Managed { get; set; } = () => false;

    /// <summary>Asks the person at this device before a module not signed by the maintainers runs its own script for a
    /// device op (module, op) -> allowed; null = such ops are refused. A yes holds for that module version until the app
    /// restarts.</summary>
    public Func<ModuleEntry, string, CancellationToken, Task<bool>>? ConfirmScriptOp { get; set; }
    private readonly HashSet<string> _opGrants = new(StringComparer.Ordinal);

    public ModuleLoader(ToolCatalog catalog, string dataDir, IModuleSource? source, IEnumerable<IModuleNativePart>? natives = null,
        IModuleRunner? runner = null, string? platform = null, Action<string>? log = null)
    {
        _catalog = catalog;
        _cache = new ModuleFileCache(dataDir);
        _source = source;
        _runner = runner ?? new ProcessModuleRunner();
        _platform = platform ?? CurrentPlatform;
        _natives = (natives ?? Array.Empty<IModuleNativePart>()).Where(n => ModuleList.ValidId(n.Id)).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        _log = log ?? (_ => { });
        _listFile = Path.Combine(dataDir, "modules.json");
        _switchFile = Path.Combine(dataDir, "modules-device.json");
        LoadSwitches();
    }

    // modules-device.json may also be written by another process of the app (the Mac window next to CO Resident)
    private void LoadSwitches()
    {
        try
        {
            if (!File.Exists(_switchFile) || JsonNode.Parse(File.ReadAllText(_switchFile)) is not JsonObject o) return;
            lock (_sync)
            {
                _switches.Clear();
                foreach (var (k, v) in o)
                    if (ModuleList.ValidId(k) && v is JsonValue jv && jv.TryGetValue(out bool b)) _switches[k] = b;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { _log("modules-device.json: " + ex.Message); }
    }

    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public ModuleFileCache Cache => _cache;
    public IReadOnlyList<ModuleEntry> Modules { get { lock (_sync) return _list; } }

    /// <summary>Per-device switch: the person's choice on this device, else the manifest default.</summary>
    public bool On(ModuleEntry m) { lock (_sync) return _switches.TryGetValue(m.Id, out bool b) ? b : m.DefaultOn; }

    public bool IsActive(string id) { lock (_sync) return _active.Contains(id); }

    /// <summary>The module list from the server (kept in modules.json); on failure the last kept one. Then Apply.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        LoadSwitches();
        string? json = null;
        if (_source != null)
        {
            try
            {
                json = await _source.ListAsync(ct);
                if (ModuleList.Parse(json) == null) { _log("modules_list: not an ext/1 list; kept the last one"); json = null; }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_listFile)!);
                    File.WriteAllText(_listFile + ".tmp", json);
                    File.Move(_listFile + ".tmp", _listFile, true);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log("modules_list: " + ex.Message); json = null; }
        }
        if (json == null && File.Exists(_listFile))
            try { json = File.ReadAllText(_listFile); } catch (IOException) { }
        var list = json == null ? null : ModuleList.Parse(json);
        if (list == null) return false;
        foreach (var m in list.Where(m => m.Enabled && m.HasDevicePart && On(m))) await EnsureFilesAsync(m, ct);
        return Apply(list);
    }

    // device files of a module that is (going to be) active: download what is missing or changed
    private readonly Dictionary<string, SemaphoreSlim> _downloads = new(StringComparer.Ordinal);

    private async Task EnsureFilesAsync(ModuleEntry m, CancellationToken ct)
    {
        if (_source == null || m.Files.Count == 0) return;
        SemaphoreSlim gate;
        lock (_sync) gate = _downloads.TryGetValue(m.Id, out var g) ? g : _downloads[m.Id] = new SemaphoreSlim(1, 1);
        await gate.WaitAsync(ct);   // a refresh and a switch never download the same module at once
        try
        {
            await _cache.EnsureAsync(m, _source, ct);
            lock (_sync) _fileProblems.Remove(m.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { lock (_sync) _fileProblems[m.Id] = "device files: " + ex.Message; _log($"module {m.Id}: {ex.Message}"); }
        finally { gate.Release(); }
    }

    /// <summary>The list kept in modules.json (the last answer of the server), applied without network: the device
    /// starts with the module tools it had, before it can reach the site.</summary>
    public bool LoadCached()
    {
        LoadSwitches();
        try { return File.Exists(_listFile) && ModuleList.Parse(File.ReadAllText(_listFile)) is { } list && Apply(list); }
        catch (IOException ex) { _log("modules.json: " + ex.Message); return false; }
    }

    /// <summary>Registers the tools of active modules and takes out the rest; true when the catalog changed.</summary>
    public bool Apply(IReadOnlyList<ModuleEntry> list)
    {
        bool changed = false;
        var activations = new List<(IModuleNativePart, bool)>();
        lock (_sync)
        {
            _list = list;
            var want = new Dictionary<string, (ModuleEntry M, IDeviceTool Tool, string Key)>(StringComparer.Ordinal);
            var nowActive = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in list)
            {
                if (!m.Enabled || !m.HasDevicePart || !On(m)) continue;
                nowActive.Add(m.Id);
                // a compiled part is bound only to a standard module (another author cannot borrow its id)
                IModuleNativePart? native = m.Standard && _natives.TryGetValue(m.Id, out var nat) ? nat : null;
                var skipped = new List<string>();
                foreach (var spec in m.Tools)
                {
                    if (!spec.RunsOn(_platform)) continue;
                    string? why = null;
                    IDeviceTool? inner = null;
                    if (spec.Native)
                    {
                        inner = native?.NativeTool(spec.Name);
                        if (inner == null) why = "no native part in this app";
                        else if (inner.Descriptor.Name != spec.Name || inner.Descriptor.Class != spec.Class) why = "native tool differs from the manifest";
                    }
                    else if (!_cache.Matches(m, spec.Run)) why = "device file missing or changed";
                    if (why == null && want.ContainsKey(spec.Name)) why = "name taken by another module";
                    if (why == null && _catalog.Contains(spec.Name) && !_registered.ContainsKey(spec.Name)) why = "name taken by the core";
                    if (why != null) { skipped.Add($"{spec.Name}: {why}"); continue; }
                    want[spec.Name] = (m, new ModuleTool(this, m, spec with { Class = EffectiveClass(m, spec), Untrusted = spec.Untrusted || (!spec.Native && !m.Standard) }, inner), $"{m.Id}|{m.Version}|{m.Standard}|{EffectiveClass(m, spec)}|{SpecKey(spec)}|{(spec.Run.Length > 0 && m.Files.TryGetValue(spec.Run, out var sha) ? sha : "")}");
                }
                // a download problem (set by RefreshAsync) stays until the next download succeeds
                string files = _fileProblems.TryGetValue(m.Id, out var fp) ? fp : "";
                string all = string.Join("; ", (files.Length > 0 ? new[] { files } : Array.Empty<string>()).Concat(skipped));
                if (all.Length > 0) _problems[m.Id] = all; else _problems.Remove(m.Id);
            }
            foreach (var (name, (mod, key)) in _registered.ToList())
            {
                if (want.TryGetValue(name, out var w) && w.Key == key) continue;
                var entry = list.FirstOrDefault(x => x.Id == mod);
                _catalog.Remove(name, "module_off", entry == null
                    ? $"The module that provided '{name}' is not installed any more."
                    : $"'{name}' belongs to the module {entry.Title}, which is off on this device or in the workbench. The user can turn it on in Host → Modules.");
                _registered.Remove(name);
                changed = true;
            }
            foreach (var (name, w) in want)
            {
                if (_registered.ContainsKey(name)) continue;
                _catalog.Add(w.Tool);
                _registered[name] = (w.M.Id, w.Key);
                changed = true;
            }
            foreach (var id in nowActive.Except(_active))
                if (_natives.TryGetValue(id, out var n) && list.Any(x => x.Id == id && x.Standard)) activations.Add((n, true));
            foreach (var id in _active.Except(nowActive)) if (_natives.TryGetValue(id, out var n)) activations.Add((n, false));
            _active.Clear();
            _active.UnionWith(nowActive);
        }
        foreach (var (n, on) in activations)
            try { n.Activate(on); } catch (Exception ex) { _log($"module {n.Id} activate({on}): {ex.Message}"); }
        try { Applied?.Invoke(); } catch (Exception ex) { _log("modules applied: " + ex.Message); }
        if (changed)
        {
            _log("module tools: " + string.Join(", ", RegisteredTools()));
            CatalogChanged?.Invoke();
        }
        return changed;
    }

    public IReadOnlyList<string> RegisteredTools() { lock (_sync) return _registered.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(); }

    /// <summary>Turns a module on or off on this device, downloads its device files when it goes on, applies the list.</summary>
    public async Task<bool> SetOnAsync(string id, bool on, CancellationToken ct)
    {
        Store(id, on);
        var m = Modules.FirstOrDefault(x => x.Id == id);
        if (on && m is { Enabled: true }) await EnsureFilesAsync(m, ct);
        return Apply(Modules);
    }

    /// <summary>Sets the per-device switch only when this device has none for the module yet (an older setting taken
    /// over); true when it was set.</summary>
    public bool SeedSwitch(string id, bool on)
    {
        LoadSwitches();
        lock (_sync) if (_switches.ContainsKey(id)) return false;
        Store(id, on);
        return true;
    }

    private void Store(string id, bool on)
    {
        lock (_sync)
        {
            if (!ModuleList.ValidId(id)) throw new ArgumentException("bad module id");
            _switches[id] = on;
            var o = new JsonObject();
            foreach (var (k, v) in _switches.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[k] = v;
            Directory.CreateDirectory(Path.GetDirectoryName(_switchFile)!);
            File.WriteAllText(_switchFile + ".tmp", o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_switchFile + ".tmp", _switchFile, true);
        }
        _log($"module {id}: {(on ? "on" : "off")} on this device");
    }

    public IReadOnlyList<ModuleDeviceState> States()
    {
        lock (_sync)
            return _list.Where(m => m.HasDevicePart).Select(m => new ModuleDeviceState(m.Id, m.Title, m.Version, m.Enabled, On(m), m.DefaultOn,
                _active.Contains(m.Id), m.Warning, _registered.Where(kv => kv.Value.Module == m.Id).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                m.DeviceOps, _problems.TryGetValue(m.Id, out var p) ? p : "")).ToList();
    }

    /// <summary>A device op of a module's settings page (ext/1 call of an op in provides.deviceOps).</summary>
    public async Task<JsonNode?> DeviceOpAsync(string id, string op, JsonElement args, CancellationToken ct)
    {
        ModuleEntry? m;
        IModuleNativePart? native;
        lock (_sync)
        {
            m = _list.FirstOrDefault(x => x.Id == id);
            native = m != null && m.Standard && _natives.TryGetValue(id, out var nat) ? nat : null;
        }
        if (m == null || !m.Enabled) throw new ToolException("module_disabled", "The module is not on in this workbench.");
        if (!ModuleList.ValidOp(op) || !m.DeviceOps.Contains(op)) throw new ToolException("op_not_declared", $"The module does not declare the device op '{op}'.");
        if (!IsActive(id)) throw new ToolException("module_off", $"{m.Title} is off on this device.");
        if (args.ValueKind != JsonValueKind.Object) throw new ToolException("invalid_args", "args must be an object.");
        if (native?.DeviceOpAsync(op, args, ct) is { } t) return await t;
        if (m.OpsRun.Length == 0) throw new ToolException("op_unavailable", $"{m.Title} has no handler for '{op}' on this device.");
        // the op runs the module's own code on this device: like class X (managed profile refuses it), and a module not
        // signed by the maintainers asks the person first
        if (Managed()) throw new ToolException("denied_by_policy", "Module scripts do not run on a managed device.");
        if (!m.Standard)
        {
            string grant = m.Id + "@" + m.Version;
            bool granted;
            lock (_sync) granted = _opGrants.Contains(grant);
            if (!granted)
            {
                if (ConfirmScriptOp == null || !await ConfirmScriptOp(m, op, ct))
                    throw new ToolException("denied_by_user", $"{m.Title} may not run its own code on this device.");
                lock (_sync) _opGrants.Add(grant);
            }
        }
        string stdout = await RunScriptAsync(m, m.OpsRun, new JsonObject { ["kind"] = "op", ["op"] = op, ["args"] = JsonNode.Parse(args.GetRawText()) }, TimeSpan.FromMinutes(2), ct);
        try { return JsonNode.Parse(stdout); } catch (JsonException) { return JsonValue.Create(stdout); }
    }

    internal async Task<string> RunScriptAsync(ModuleEntry m, string rel, JsonObject request, TimeSpan timeout, CancellationToken ct)
    {
        // the file is checked right before the run: a file changed since the download is never run
        if (!_cache.Matches(m, rel)) throw new ToolException("module_file_changed", $"{m.Id}/{rel} does not match the module registry; it is not run.");
        string state = _cache.StateDir(m);
        request["ext"] = ModuleList.ExtVersion;
        request["module"] = m.Id;
        request["version"] = m.Version;
        request["stateDir"] = state;
        request["platform"] = _platform;
        var env = new Dictionary<string, string> { ["AIWB_MODULE_ID"] = m.Id, ["AIWB_MODULE_STATE"] = state, ["AIWB_EXT"] = "1" };
        var r = await _runner.RunAsync(_cache.PathOf(m, rel), state, request.ToJsonString(), env, timeout, ct);
        if (r.TimedOut) throw new ToolException("timeout", $"{m.Id}/{rel} did not answer in {timeout.TotalSeconds:0} s.");
        if (r.ExitCode != 0)
            throw new ToolException("module_tool_failed", $"{m.Id}/{rel} exited with {r.ExitCode}: " + Tail(r.Stderr.Length > 0 ? r.Stderr : r.Stdout, 2000));
        return r.Stdout;
    }

    /// <summary>A script tool of a module not signed by the Freedom maintainers runs its own code on this device, so a
    /// declared R, W or N counts as X there (confirmed, grantable for the task); standard modules keep their class.</summary>
    public static RiskClass EffectiveClass(ModuleEntry m, ModuleToolSpec s) =>
        !s.Native && !m.Standard && s.Class is RiskClass.R or RiskClass.W or RiskClass.N ? RiskClass.X : s.Class;

    private static string Tail(string s, int n) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();

    private static string SpecKey(ModuleToolSpec s) =>
        $"{s.Name}|{s.Class}|{s.Run}|{s.Untrusted}|{s.Description}|{s.Input?.ToJsonString()}";

    // A tool of a module in the device catalog. Plan and Execute check that the module is still active, so a call that
    // raced with the switch refuses too.
    private sealed class ModuleTool : IDeviceTool
    {
        private readonly ModuleLoader _l;
        private readonly ModuleEntry _m;
        private readonly ModuleToolSpec _s;
        private readonly IDeviceTool? _inner;

        public ModuleTool(ModuleLoader l, ModuleEntry m, ModuleToolSpec s, IDeviceTool? inner)
        {
            _l = l; _m = m; _s = s; _inner = inner;
            Descriptor = new ToolDescriptor
            {
                Name = s.Name, Class = s.Class,
                Untrusted = inner?.Descriptor.Untrusted ?? (s.Untrusted ? UntrustedMode.Always : UntrustedMode.Never),
                Description = $"[module {m.Title}] " + (s.Description.Length > 0 ? s.Description : inner?.Descriptor.Description ?? s.Name),
                InputSchema = inner?.Descriptor.InputSchema.DeepClone().AsObject()
                    ?? (s.Input?.DeepClone() as JsonObject) ?? new JsonObject { ["type"] = "object" },
            };
        }

        public ToolDescriptor Descriptor { get; }

        private void Check()
        {
            if (!_l.IsActive(_m.Id))
                throw new ToolException("module_off", $"'{_s.Name}' belongs to the module {_m.Title}, which is off on this device or in the workbench.");
        }

        public ActionPlan Plan(JsonElement args, ToolEnv env)
        {
            Check();
            if (_inner != null)
            {
                var p = _inner.Plan(args, env);
                if (p.Class != _s.Class && Rank(p.Class) < Rank(_s.Class))
                    throw new ToolException("module_class_mismatch", $"'{_s.Name}' planned class {p.Class} below the declared {_s.Class}.");
                return p;
            }
            string a = args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText();
            var plan = new ActionPlan
            {
                Tool = _s.Name, Class = _s.Class,
                Summary = $"{_m.Title}: {_s.Name} " + (a.Length > 300 ? a[..300] + "…" : a),
            };
            // "allow for this task" covers this tool of this module version only
            plan.Scopes.Add(new Scope(ScopeKind.App, $"module:{_m.Id}@{_m.Version}/{_s.Name}"));
            return plan;
        }

        public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
        {
            Check();
            if (_inner != null) return await _inner.ExecuteAsync(args, env, ct);
            var req = new JsonObject
            {
                ["kind"] = "tool", ["tool"] = _s.Name,
                ["args"] = args.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(args.GetRawText()),
                ["runId"] = env.RunId,
            };
            string outText = await _l.RunScriptAsync(_m, _s.Run, req, TimeSpan.FromMinutes(30), ct);
            return new ToolOutput { Content = outText, Untrusted = _s.Untrusted };
        }

        // R < W < N < D < X < E: how much a class can change; a native plan may raise the class, never lower it
        private static int Rank(RiskClass c) => c switch
        {
            RiskClass.R => 0, RiskClass.W => 1, RiskClass.N => 2, RiskClass.D => 3, RiskClass.X => 4, RiskClass.E => 5, _ => 6,
        };
    }
}

/// <summary>Starts a device/ file by its extension (.py python, .js node, .ps1 PowerShell, .sh sh, .cmd/.bat cmd,
/// anything else as an executable); stdin carries the request, stdout the answer (capped at 1 MB).</summary>
public sealed class ProcessModuleRunner : IModuleRunner
{
    public static (string Exe, string[] Args) Command(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool win = OperatingSystem.IsWindows();
        return ext switch
        {
            ".py" => (win ? "python" : "python3", new[] { "-I", file }),
            ".js" or ".mjs" => ("node", new[] { file }),
            ".ps1" => (win ? "powershell.exe" : "pwsh", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }),
            ".sh" => ("/bin/sh", new[] { file }),
            ".cmd" or ".bat" => ("cmd.exe", new[] { "/d", "/c", file }),
            _ => (file, Array.Empty<string>()),
        };
    }

    public async Task<ModuleRunResult> RunAsync(string file, string workDir, string stdin, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken ct)
    {
        var (exe, args) = Command(file);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = workDir, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new ToolException("module_tool_failed", "could not start " + exe);
        var sbo = new StringBuilder();
        var sbe = new StringBuilder();
        var so = ReadCappedAsync(p.StandardOutput, sbo);
        var se = ReadCappedAsync(p.StandardError, sbe);
        // the timeout covers the input too: a program that never reads a large request must not hang the call
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            try
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
                p.StandardInput.Close();
            }
            catch (IOException) { /* the program does not read its input */ }
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        // a child the program left behind may keep the pipes open: wait for the output a few seconds only
        var drain = Task.WhenAll(so, se);
        await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));
        string o, e;
        lock (sbo) o = sbo.ToString();
        lock (sbe) e = sbe.ToString();
        return new ModuleRunResult(timedOut ? -1 : p.ExitCode, o, e, timedOut);
    }

    private static async Task ReadCappedAsync(StreamReader r, StringBuilder sb)
    {
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
            lock (sb)
                if (sb.Length < ModuleLoader.MaxOutput) sb.Append(buf, 0, Math.Min(n, ModuleLoader.MaxOutput - sb.Length));
    }
}
