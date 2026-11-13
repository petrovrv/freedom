// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Core.Connectors;

// External services the agent reaches from this device (CO Desktop → Connectors): the agent browser, the Claude
// account's cloud connectors, and those of the desktop parts of modules (e.g. mail, document sites, portals).
// Each one has its own sign-in and session; the user ticks the same boxes as for folders (Read, Create, Edit, Delete)
// and a ticked box is the permission.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorHealth { Unchecked, Ready, SignInNeeded, Unavailable, Off, Planned }

public sealed record ConnectorStatus(ConnectorHealth Health, string Detail, DateTime? CheckedUtc)
{
    public static readonly ConnectorStatus Unchecked = new(ConnectorHealth.Unchecked, "", null);
}

public abstract class Connector
{
    internal ConnectorRegistry? Registry { get; set; }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual bool Planned => false;                    // shown, not usable yet
    public virtual Rights Supported => Rights.Read;          // boxes that mean something for this service
    public virtual Rights DefaultRights => Rights.None;
    public virtual IReadOnlyCollection<string> Tools => Array.Empty<string>();

    // Rows of the Connectors tab: the connector's own row (title and grey note) and, below it, the folders connected by
    // link that belong to it (one row each, with the folder's own boxes).
    public virtual string RowTitle => Name;
    public virtual string RowNote => "";
    public virtual IEnumerable<Files.ConnectedFolder> LinkedFolders => Array.Empty<Files.ConnectedFolder>();
    public virtual string FolderTitle(Files.ConnectedFolder f) => f.Path;

    public virtual Rights Rights
    {
        get => Planned ? Rights.None : (Registry?.StoredRights(Id) ?? DefaultRights) & Supported;
        set { if (!Planned) Registry?.StoreRights(Id, value & Supported); }
    }

    public ConnectorStatus Status { get; private set; } = ConnectorStatus.Unchecked;

    // Live probe of the session; slow (COM, HTTP, CO browser), so it runs on demand only.
    public async Task<ConnectorStatus> CheckAsync(CancellationToken ct)
    {
        if (Planned) Status = new(ConnectorHealth.Planned, "planned for a next release", DateTime.UtcNow);
        else if (Supported != Rights.None && !Rights.HasFlag(Rights.Read)) Status = new(ConnectorHealth.Off, "Read is not ticked on this computer", DateTime.UtcNow);
        else
        {
            try { Status = await ProbeAsync(ct) with { CheckedUtc = DateTime.UtcNow }; }
            catch (OperationCanceledException) { throw; }
            catch (ToolException ex) when (ex.Code is "sign_in_needed" or "auth_required")
            { Status = new(ConnectorHealth.SignInNeeded, ex.Message, DateTime.UtcNow); }
            catch (Exception ex) { Status = new(ConnectorHealth.Unavailable, ex.Message, DateTime.UtcNow); }
        }
        Registry?.RaiseChanged(this);
        return Status;
    }

    protected abstract Task<ConnectorStatus> ProbeAsync(CancellationToken ct);

    protected static ConnectorStatus Ready(string detail) => new(ConnectorHealth.Ready, detail, null);
    protected static ConnectorStatus Unavailable(string detail) => new(ConnectorHealth.Unavailable, detail, null);

    internal bool Allows(Rights need) => !Planned && Rights.HasFlag(Rights.Read) && (Rights & need) == need;

    // Error for a tool call the ticked boxes do not allow.
    internal ToolException Refusal(Rights need)
    {
        if (Planned) return new ToolException("connector_unavailable", $"{Name} is not available on this computer yet.");
        if (!Rights.HasFlag(Rights.Read))
            return new ToolException("connector_off", $"{Name} is off on this computer. The user can tick Read in CO Desktop → Connectors → {Name}.");
        return new ToolException("connector_read_only",
            $"{Name}: {RightsText.List(need & ~Rights)} is not ticked on this computer. The user can tick it in CO Desktop → Connectors.");
    }
}

public sealed class ConnectorRegistry
{
    private readonly string? _file;
    private readonly object _sync = new();
    private readonly Dictionary<string, Rights> _rights = new();
    private readonly List<Connector> _connectors = new();

    public event Action<Connector>? Changed;

    public ConnectorRegistry(string? file)
    {
        _file = file;
        if (file == null || !File.Exists(file)) return;
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
        foreach (var (id, v) in raw) _rights[id] = Parse(v);
    }

    // 0.6.0 stored "Off" / "Read" / "Write"; 0.6.1 stores the ticked boxes ("Read, Create").
    public static Rights Parse(string v) => v switch
    {
        "Off" => Rights.None,
        "Write" => Rights.Read | Rights.Create | Rights.Edit,
        _ => Enum.TryParse<Rights>(v, out var r) ? r : Rights.None,
    };

    public IReadOnlyList<Connector> All { get { lock (_sync) return _connectors.ToList(); } }

    public Connector? Find(string id) { lock (_sync) return _connectors.FirstOrDefault(c => c.Id == id); }

    public T Add<T>(T c) where T : Connector
    {
        c.Registry = this;
        lock (_sync) _connectors.Add(c);
        return c;
    }

    // Wraps a tool so the connector's boxes are enforced before the tool plans or runs. need defaults to
    // Read for read tools and Create for the rest.
    public IDeviceTool Gate(Connector c, IDeviceTool tool, Rights? need = null) =>
        new GatedTool(c, tool, need ?? (tool.Descriptor.Class == RiskClass.R ? Rights.Read : Rights.Create));

    internal Rights? StoredRights(string id) { lock (_sync) return _rights.TryGetValue(id, out var r) ? r : null; }

    internal void StoreRights(string id, Rights r)
    {
        lock (_sync)
        {
            _rights[id] = r;
            if (_file != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var raw = _rights.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
                File.WriteAllText(_file, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        var c = Find(id);
        if (c != null) RaiseChanged(c);
    }

    internal void RaiseChanged(Connector c) => Changed?.Invoke(c);

    private sealed class GatedTool : IDeviceTool
    {
        private readonly Connector _c;
        private readonly IDeviceTool _inner;
        private readonly Rights _need;
        public GatedTool(Connector c, IDeviceTool inner, Rights need) { _c = c; _inner = inner; _need = need; }

        public ToolDescriptor Descriptor => _inner.Descriptor;

        public ActionPlan Plan(JsonElement args, ToolEnv env)
        {
            if (!_c.Allows(_need)) throw _c.Refusal(_need);
            return _inner.Plan(args, env);
        }

        public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
        {
            if (!_c.Allows(_need)) throw _c.Refusal(_need);
            return _inner.ExecuteAsync(args, env, ct);
        }
    }
}

// Connector known by name only: shown on the tab so the user sees what is coming, refuses every call.
public sealed class PlannedConnector : Connector
{
    private readonly string _id, _name, _desc;
    private readonly Rights _supported;
    public PlannedConnector(string id, string name, string description, Rights supported = Rights.Read)
    { _id = id; _name = name; _desc = description; _supported = supported; }
    public override string Id => _id;
    public override string Name => _name;
    public override string Description => _desc;
    public override bool Planned => true;
    public override Rights Supported => _supported;
    protected override Task<ConnectorStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new ConnectorStatus(ConnectorHealth.Planned, "planned for a next release", null));
}

// connectors_status: what the agent may use on this device right now. Scheduled runs call it first so a
// missing sign-in is reported instead of a half-empty result.
public sealed class ConnectorsStatusTool : IDeviceTool
{
    private readonly ConnectorRegistry _r;
    public ConnectorsStatusTool(ConnectorRegistry r) => _r = r;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "connectors_status", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description =
            "Connectors of this computer (mail, document sites, portals, the agent browser): boxes the user " +
            "ticked (read, create, edit, delete), session health and the tools each one serves. check=true probes every enabled " +
            "connector live (its sign-in or session; may take up to a minute) — do this before " +
            "unattended work. Tools of a connector without Read fail with connector_off; tell the user where to tick it.",
        InputSchema = Schema.Obj(("check", "boolean", "Probe sessions live (default false: last known status)", false)),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) =>
        new() { Tool = "connectors_status", Class = RiskClass.R, Summary = "connector status" };

    public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        bool check = Args.OptBool(args, "check", false);
        var list = new List<object>();
        foreach (var c in _r.All)
        {
            var st = check ? await c.CheckAsync(ct) : c.Status;
            list.Add(new
            {
                id = c.Id, name = c.Name, planned = c.Planned,
                rights = RightsText.List(c.Rights), supported = RightsText.List(c.Supported),
                health = st.Health.ToString(), detail = st.Detail,
                checkedUtc = st.CheckedUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'"),
                tools = c.Tools,
            });
        }
        return new ToolOutput { Content = FsTools.Json(list) };
    }
}
