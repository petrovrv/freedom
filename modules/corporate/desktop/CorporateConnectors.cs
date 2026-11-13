// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Corporate.Connectors;

// Connectors of the corporate module (Freedom F1, moved from CO.Core BuiltInConnectors): same ids, names and boxes as
// before, registered by CorporateModule only when the user turned corporate connectors on.

// Classic Outlook through COM. Boxes map onto outlook.json (Read = mail and calendar, Create = drafts, saved attachments and
// calendar events, Edit = move, flag, categories, own calendar events and answers to invitations, Delete = own calendar events); the Outlook tools enforce them themselves (outlook_disabled), so they are registered without a gate.
public sealed class OutlookConnector : Connector
{
    private readonly IOutlook _o;
    private readonly OutlookSettings _s;
    private readonly string[] _tools;

    public OutlookConnector(IOutlook o, OutlookSettings s, IEnumerable<string> tools) { _o = o; _s = s; _tools = tools.ToArray(); }

    public override string Id => "outlook";
    public override string Name => "Outlook";
    public override string Description =>
        "Classic Outlook on this computer: search and read mail and the calendar; with changes allowed — drafts (never sent), " +
        "attachments saved into writable folders, move, flag, categories; calendar — create and change your own events, " +
        "delete them (Delete box), answer invitations (the organizer is not mailed unless asked).";
    public override Rights Supported => Rights.All;   // Delete: own calendar events only; the agent never deletes mail
    public override IReadOnlyCollection<string> Tools => _tools;

    public override Rights Rights
    {
        get => !_s.Read ? Rights.None : Rights.Read | (_s.CanCreate ? Rights.Create : 0) | (_s.CanEdit ? Rights.Edit : 0)
            | (_s.CanDelete ? Rights.Delete : 0);
        set
        {
            _s.Read = value.HasFlag(Rights.Read);
            _s.Create = _s.Read && value.HasFlag(Rights.Create);
            _s.Edit = _s.Read && value.HasFlag(Rights.Edit);
            _s.Delete = _s.Read && value.HasFlag(Rights.Delete);
            _s.Write = _s.CanCreate || _s.CanEdit;
            _s.Save();
            Registry?.RaiseChanged(this);
        }
    }

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        var folders = await Task.Run(() => _o.Folders(), ct);
        return Ready($"{folders.Count} mail folders");
    }
}

// SharePoint: sharepoint_* open any SharePoint link with the CO browser session; folders connected by link are used
// through fs_* (their own rw/ro applies).
public sealed class SharePointConnector : Connector
{
    private readonly FolderRegistry _folders;
    public SharePointConnector(FolderRegistry folders) => _folders = folders;

    public override string Id => "sharepoint";
    public override string Name => "SharePoint";
    public override string Description =>
        "SharePoint sites with your CO browser session: read any link (sharepoint_list, sharepoint_read, sharepoint_download) " +
        "and folders connected by link or synced by OneDrive, which the agent uses like local folders.";
    public override Rights DefaultRights => Rights.Read;
    public override IReadOnlyCollection<string> Tools => new[] { "sharepoint_list", "sharepoint_read", "sharepoint_download" };

    public override string RowTitle => "SharePoint links";
    public override string RowNote => "any link given in a chat, read only";
    public override IEnumerable<ConnectedFolder> LinkedFolders => _folders.All.Where(f => f.IsRemote && !OneDriveConnector.IsPersonal(f.RemoteUrl));
    public override string FolderTitle(ConnectedFolder f) => SharePointNames.Title(f.Path);

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct) =>
        await ProbeFoldersAsync(_folders, LinkedFolders.ToList(), "folders by link", ct)
        ?? new ConnectorStatus(ConnectorHealth.Unchecked, "no folder connected by link; sharepoint_* sign in when first used", null);

    internal static async Task<ConnectorStatus?> ProbeFoldersAsync(FolderRegistry reg, List<ConnectedFolder> linked, string what, CancellationToken ct)
    {
        var sp = (reg.RemoteFolders as SharePointFs)?.Service;
        var first = linked.FirstOrDefault();
        if (sp == null || first == null) return null;
        if (reg.Resolve(first.Id).Remote is not SharePointPath path) return null;
        if (await sp.StatAsync(path, ct) is null)
            return new ConnectorStatus(ConnectorHealth.Unavailable, $"{first.Id}: folder not found", null);
        string via = sp.LastSignIn.Length > 0 ? $"; session via {sp.LastSignIn}" : "";
        return new ConnectorStatus(ConnectorHealth.Ready, $"{linked.Count} {what}, {first.Id} opens{via}", null);
    }
}

// OneDrive for Business: the synced folder of the OneDrive client, or folders of the personal site
// (<tenant>-my.sharepoint.com) connected by link. No tools of its own: the agent works through fs_*.
public sealed class OneDriveConnector : Connector
{
    private readonly FolderRegistry _folders;
    public OneDriveConnector(FolderRegistry folders) => _folders = folders;

    public override string Id => "onedrive";
    public override string Name => "OneDrive";
    public override string Description =>
        "Your OneDrive for Business: the folder synced by the OneDrive client, or OneDrive folders connected by link " +
        "(no sync needed). The agent uses them like local folders.";
    public override Rights Supported => Rights.None;   // rights are set per folder on the Folders tab

    public static bool IsPersonal(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase);

    public override IEnumerable<ConnectedFolder> LinkedFolders => _folders.All.Where(f => f.IsRemote && IsPersonal(f.RemoteUrl));
    public override string FolderTitle(ConnectedFolder f) => SharePointNames.Title(f.Path);

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        var linked = await SharePointConnector.ProbeFoldersAsync(_folders, LinkedFolders.ToList(), "OneDrive folders by link", ct);
        if (linked != null) return linked;
        var roots = SyncDetector.OneDriveRoots().ToList();
        return roots.Count > 0
            ? Ready("synced: " + string.Join("; ", roots))
            : Unavailable("the OneDrive client does not sync on this computer; connect a OneDrive folder by link");
    }
}

// ENGIE AI Studio (engie.ai) models through the CO browser's Okta session.
public sealed class EngieConnector : Connector
{
    private readonly IBrowser _b;
    public EngieConnector(IBrowser b) => _b = b;

    public override string Id => "engie";
    public override string Name => "ENGIE AI Studio";
    public override string Description =>
        "Models of engie.ai (GPT, Claude, Mistral on ENGIE gateways) with your CO browser session: engie_chat, engie_fetch.";
    public override Rights DefaultRights => Rights.Read;
    public override IReadOnlyCollection<string> Tools => new[] { "engie_chat", "engie_fetch" };

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        await EngieTools.EnsureOnEngieAsync(_b, ct);
        return Ready("signed in to engie.ai in the CO browser");
    }
}

// Tractebel document management (TE portal) with the CO browser's Okta session; read only (downloads need Create on
// the target folder, checked there).
public sealed class TractebelConnector : Connector
{
    private readonly IBrowser _b;
    public TractebelConnector(IBrowser b) => _b = b;

    public override string Id => "tractebel";
    public override string Name => "Tractebel Portal";
    public override string Description =>
        "Tractebel document management (teportal.tractebel.myengie.com): search documents, cards with revisions and " +
        "contributors, project and person lookups, file downloads into a connected folder.";
    public override Rights DefaultRights => Rights.Read;
    public override IReadOnlyCollection<string> Tools => new[] { "te_search", "te_doc", "te_projects", "te_fetch", "te_download" };

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        // 0.7.7: the portal is reachable only through the employer network (Zscaler); check before opening a page.
        if (!await Reachable("https://teportal.tractebel.myengie.com/", ct))
            return Unavailable("teportal.tractebel.myengie.com is not reachable from this network (Zscaler / employer network needed)");
        // A guest session (GUEST_SUEZ) is not enough: this signs in through Okta when needed.
        return Ready("signed in as " + await TractebelTools.EnsureSignedInAsync(_b, ct));
    }

    // 0.7.8: through the system proxy (corporate PAC/proxy); any HTTP answer means reachable.
    private static readonly HttpClient ReachabilityClient = new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = null, // null => system proxy
        DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
        AllowAutoRedirect = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    internal static async Task<bool> Reachable(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
            using (var response = await ReachabilityClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                if (response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed) return true;
            }
            using var get = new HttpRequestMessage(HttpMethod.Get, uri);
            using var getResponse = await ReachabilityClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

// Microsoft Teams through the Teams web client in the CO browser; read only (no sending).
public sealed class TeamsConnector : Connector
{
    private readonly IBrowser _b;
    public TeamsConnector(IBrowser b) => _b = b;

    public override string Id => "teams";
    public override string Name => "Teams";
    public override string Description =>
        "Microsoft Teams chats and channels through the Teams web client in the CO browser: recent chats, messages, search, " +
        "read-only Graph calls. Nothing is sent.";
    public override Rights DefaultRights => Rights.Read;
    public override IReadOnlyCollection<string> Tools => new[] { "teams_chats", "teams_messages", "teams_search", "teams_fetch", "teams_probe" };

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        var r = await TeamsChatService.RunAsync(_b, TeamsChatService.ChatsJs(5), ct);
        var chats = r.GetProperty("chats");
        return Ready($"chat service answers; latest conversation: {(chats.GetArrayLength() > 0 ? TeamsTools.P(chats[0], "topic") is { Length: > 0 } t ? t : TeamsTools.P(chats[0], "last", "from") : "none")}");
    }
}

// 0.8.8: SAP Fiori of Tractebel (timesheets, leave) through OData with the CO browser's Okta session. Read opens sap_get;
// Create opens sap_write, Edit sap_update, Delete sap_delete (each call confirmed); writes only to the timesheet and
// leave services. Ticking a box is the consent, as for the calendar tools.
public sealed class SapConnector : Connector
{
    private readonly IBrowser _b;
    public SapConnector(IBrowser b) => _b = b;

    public override string Id => "sap";
    public override string Name => "SAP Fiori (HR)";
    public override string Description =>
        "SAP Fiori of Tractebel (tl0tp4pc.tractebel.suez): timesheets and leave through OData with the CO browser's Okta " +
        "session; Create allows changes (timesheet entries, leave requests).";
    public override Rights Supported => Rights.Read | Rights.Create | Rights.Edit | Rights.Delete;
    public override Rights DefaultRights => Rights.Read;
    public override IReadOnlyCollection<string> Tools => new[] { "sap_get", "sap_write", "sap_update", "sap_delete" };

    protected override async Task<ConnectorStatus> ProbeAsync(CancellationToken ct)
    {
        if (!await TractebelConnector.Reachable(SapTools.Base + "/sap/public/ping", ct))
            return Unavailable(SapTools.Host + " is not reachable from this network (employer network needed)");
        await SapTools.EnsureOnSapAsync(_b, ct);
        return Ready("signed in to SAP Fiori");
    }
}
