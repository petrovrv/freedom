// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Browser;
using CO.Core.Policy;

namespace CO.Core.Connectors;

// Lets the agent drive its own tab of the CO browser (CO Desktop 0.8.0); 0.9.0: all public sites except a blocked list.
public sealed class AgentBrowserConnector : Connector
{
    static readonly string[] ToolNames =
    {
        "browser_navigate", "browser_back", "browser_snapshot", "browser_scroll", "browser_status",
        "browser_click", "browser_type", "browser_select", "browser_press", "browser_upload",
    };

    readonly AgentWebSettingsStore _store;
    readonly bool _managed;

    public AgentBrowserConnector(AgentWebSettingsStore store, bool managed)
    {
        _store = store;
        _managed = managed;
    }

    public override string Id => "agentweb";
    public override string Name => "Agent browser";

    // On managed devices the row is shown but every call is refused.
    public override bool Planned => _managed;
    public override Rights Supported => Rights.Read | Rights.Edit;
    public override Rights DefaultRights => Rights.None;
    public override IReadOnlyCollection<string> Tools => ToolNames;

    public override string Description
    {
        get
        {
            if (_managed)
                return "Disabled on managed devices by company policy: the agent does not drive a browser with corporate sessions.";
            // 0.9.0: blocklist instead of allowlist.
            var block = _store.Current.Block;
            return "The agent drives its own tab of the CO browser (with your sign-ins), one tab per job, on any public site "
                 + "except the blocked sites you list below the table; local and private network addresses are always blocked. "
                 + "Read: open pages and read them; Edit: click, type, choose, upload. "
                 + "Sending is never done; forms, purchases and deletions ask you first; other clicks ask only if you turned that on. Blocked sites: "
                 + (block.Count == 0 ? "none" : string.Join(", ", block));
        }
    }

    protected override Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        if (_managed) return Task.FromResult(Unavailable("disabled on managed devices"));
        var count = _store.Current.Block.Count;
        return Task.FromResult(Ready(count == 0 ? "all public sites" : "all public sites except " + count + " blocked"));
    }
}
