// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CO.Core.Policy;

// TZ §9.1
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskClass { R, W, X, N, E, D }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GrantDuration { Call, Task, Session, Permanent }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeKind { Folder, App, MailFolder, Domain }

public sealed record Scope(ScopeKind Kind, string Value);

public sealed class Grant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RiskClass Class { get; set; }
    public ScopeKind Kind { get; set; }
    public string Value { get; set; } = "";
    public GrantDuration Duration { get; set; }
    public string? TaskId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static bool ClassAllowsDuration(RiskClass c, GrantDuration d) => c switch
    {
        RiskClass.R or RiskClass.W => true,
        RiskClass.X => d is GrantDuration.Call or GrantDuration.Task,
        RiskClass.N => d is GrantDuration.Call or GrantDuration.Task or GrantDuration.Session,
        _ => false, // E and D never take a Grant (TZ §9.1)
    };
}

// What a tool is about to do, computed by the tool before execution.
public sealed class ActionPlan
{
    public required string Tool { get; init; }
    public required RiskClass Class { get; init; }
    public List<Scope> Scopes { get; } = new();
    public bool WritesToSynced { get; init; }
    public bool PermittedByUser { get; init; }       // the box for this action is ticked (Delete): no confirmation, also on managed
    public string? NavigationDomain { get; init; }
    public string Summary { get; init; } = "";      // one line for the confirmation window
    public string? Diff { get; init; }              // unified diff for file changes
    // Mac resident shell_run: the person at the computer confirms every call — no grants, no "for this task",
    // not skipped for a trusted (auto-approve) environment. Deny of the class still wins.
    public bool ConfirmEveryCall { get; init; }
}

public sealed class CallContext
{
    public required string RunId { get; init; }
    public required string TaskId { get; init; }
    public bool Tainted { get; init; }
    public bool WebTainted { get; init; }   // taint from a web page, a document site or the Host, not from the user's own folders
    public bool Managed { get; init; }
    public bool Trusted { get; init; }       // owner opted this environment into auto-approval: no confirmation prompts
}

public enum Verdict { Allow, Confirm, Deny }

public sealed record Decision(Verdict Verdict, string Reason, bool Grantable)
{
    public static Decision Allow(string reason) => new(Verdict.Allow, reason, false);
    public static Decision Confirm(string reason, bool grantable) => new(Verdict.Confirm, reason, grantable);
    public static Decision Deny(string reason) => new(Verdict.Deny, reason, false);
}

public sealed class GrantStore
{
    private readonly object _sync = new();
    private readonly List<Grant> _grants = new();
    private readonly string? _file;

    public GrantStore(string? file = null)
    {
        _file = file;
        if (_file != null && File.Exists(_file))
        {
            var loaded = JsonSerializer.Deserialize<List<Grant>>(File.ReadAllText(_file)) ?? new();
            _grants.AddRange(loaded.Where(g => g.Duration == GrantDuration.Permanent && Grant.ClassAllowsDuration(g.Class, g.Duration)));
        }
    }

    public IReadOnlyList<Grant> All { get { lock (_sync) return _grants.ToList(); } }

    public void Add(Grant g)
    {
        if (!Grant.ClassAllowsDuration(g.Class, g.Duration))
            throw new InvalidOperationException($"Class {g.Class} cannot take a {g.Duration} grant.");
        if (g.Duration == GrantDuration.Call) return; // a one-call grant is the approval itself
        lock (_sync) { _grants.Add(g); Save(); }
    }

    public bool Revoke(string id)
    {
        lock (_sync) { int n = _grants.RemoveAll(g => g.Id == id); Save(); return n > 0; }
    }

    public void EndTask(string taskId)
    {
        lock (_sync) { _grants.RemoveAll(g => g.Duration == GrantDuration.Task && g.TaskId == taskId); Save(); }
    }

    public bool Covers(RiskClass c, Scope scope, string taskId)
    {
        lock (_sync)
            return _grants.Any(g => g.Class == c && g.Kind == scope.Kind
                && (g.Duration != GrantDuration.Task || g.TaskId == taskId)
                && ScopeContains(g, scope));
    }

    private static bool ScopeContains(Grant g, Scope s) => g.Kind switch
    {
        ScopeKind.Folder => PathUtil.IsUnder(s.Value, g.Value),
        ScopeKind.Domain => s.Value.Equals(g.Value, StringComparison.OrdinalIgnoreCase)
            || s.Value.EndsWith("." + g.Value, StringComparison.OrdinalIgnoreCase),
        _ => s.Value.Equals(g.Value, StringComparison.OrdinalIgnoreCase),
    };

    private void Save()
    {
        if (_file == null) return;
        var perm = _grants.Where(g => g.Duration == GrantDuration.Permanent).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(perm));
        File.Move(tmp, _file, true);
    }
}

public sealed class PolicyEngine
{
    private readonly GrantStore _grants;

    public PolicyEngine(GrantStore grants) => _grants = grants;

    public Decision Decide(ActionPlan plan, CallContext ctx)
    {
        Decision d = DecideCore(plan, ctx);
        if (plan.ConfirmEveryCall)
            return d.Verdict == Verdict.Deny ? d : Decision.Confirm("Confirmed at the computer for every call.", false);
        // Owner opted this environment into auto-approval: never prompt, auto-allow what would be confirmed.
        // Deny stays Deny (e.g. a managed-device profile restriction is an org policy trust does not override).
        if (ctx.Trusted && d.Verdict == Verdict.Confirm)
            return Decision.Allow("trusted environment (owner opted into auto-approval)");
        return d;
    }

    private Decision DecideCore(ActionPlan plan, CallContext ctx)
    {
        // TZ §9.6: a managed profile allows only R and W.
        if (ctx.Managed && plan.Class is not (RiskClass.R or RiskClass.W) && !(plan.Class == RiskClass.D && plan.PermittedByUser))
            return Decision.Deny($"Class {plan.Class} is disabled by the managed-device profile.");

        bool covered = plan.Scopes.Count > 0 && plan.Scopes.All(s => _grants.Covers(plan.Class, s, ctx.TaskId));

        switch (plan.Class)
        {
            case RiskClass.R:
                return Decision.Allow("read inside connected scope");

            case RiskClass.W:
                // Writes reach only folders the user connected as writable; connecting one is the consent.
                // Snapshots keep every change revertible.
                if (ctx.WebTainted && plan.WritesToSynced)
                    return Decision.Confirm("Write to a synced folder after reading web content (TZ §9.4.3).", false);
                return Decision.Allow("writable connected folder");

            case RiskClass.X:
                if (ctx.Tainted)
                    return Decision.Confirm("Code execution in a tainted Run (TZ §9.4.3).", false);
                return covered ? Decision.Allow("covered by grant") : Decision.Confirm("Code execution.", true);

            case RiskClass.N:
                if (!ctx.Tainted) return Decision.Allow("navigation in a clean Run");
                return covered ? Decision.Allow("domain in grant")
                    : Decision.Confirm($"Navigation to {plan.NavigationDomain} from a tainted Run (TZ §9.4.3).", false);

            case RiskClass.E:
                return Decision.Confirm("External action.", false);

            case RiskClass.D:
                // Deletion to a Recycle Bin, allowed by the Delete box of the folder (CO Desktop 0.6.1).
                return plan.PermittedByUser ? Decision.Allow("Delete is ticked for this folder") : Decision.Confirm("Deletion.", false);
        }
        return Decision.Deny("Unknown class.");
    }
}

public static class PathUtil
{
    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string p)
    {
        string full = Path.GetFullPath(p);
        return full.Length > Path.GetPathRoot(full)!.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool IsUnder(string path, string root)
    {
        if (path.Contains("://") || root.Contains("://"))   // folders connected by link are URLs
        {
            string pr = root.TrimEnd('/');
            return path.Equals(pr, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pr + "/", StringComparison.OrdinalIgnoreCase);
        }
        string p = Normalize(path), r = Normalize(root);
        if (p.Equals(r, Cmp)) return true;
        string rs = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(rs, Cmp);
    }
}
