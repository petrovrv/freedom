// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Corporate;

// Freedom F1 (SPEC §3): corporate connectors of CO Desktop as one module — Outlook (COM), Teams, OneDrive and SharePoint
// (folders by link, Okta fallback), Tractebel Portal, SAP Fiori and ENGIE AI Studio (the example of an external AI
// reached through the CO browser: sign-in, keep-alive, prompt sanitizing). Off by default: CO Desktop registers it only
// while the module corporate is enabled in the workbench and on for the device (co-desktop/ModuleParts.props, App.cs).
// Ids, tool names and settings files (outlook.json, sharepoint.json, connectors.json) are the ones of CO Desktop 0.9.x.
public sealed class CorporateModule : IConnectorModule
{
    private ConnectorModuleContext? _ctx;
    private EngieConnector? _engie;
    private SharePointService? _sharePoint;
    private CancellationToken _ct;

    public string Id => "corporate";

    // CO Desktop before 0.13.0 switched this part with desktop.json CorporateConnectors.
    public string LegacySetting => "CorporateConnectors";

    public void Register(ConnectorModuleContext ctx)
    {
        _ctx = ctx;
        var connectors = ctx.Connectors;
        EngieTools.Trace = ctx.Log; // 0.7.4: token renewals in co-desktop.log

        // Outlook (classic, COM): off until the user allows it; the Outlook tools check outlook.json themselves.
        var outlook = OutlookSettings.Load(Path.Combine(ctx.DataDir, "outlook.json"));
        var outlookCom = new OutlookCom();
        var outlookTools = OutlookTools.All(outlookCom, outlook).ToList();
        connectors.Add(new OutlookConnector(outlookCom, outlook, outlookTools.Select(t => t.Descriptor.Name)));
        var teams = connectors.Add(new TeamsConnector(ctx.Tab("teams")));
        connectors.Add(new OneDriveConnector(ctx.Folders));
        var sharePoint = connectors.Add(new SharePointConnector(ctx.Folders));
        var tractebel = connectors.Add(new TractebelConnector(ctx.Tab("tractebel")));
        var sap = connectors.Add(new SapConnector(ctx.Tab("sap")));
        _engie = connectors.Add(new EngieConnector(ctx.Tab("engie")));

        // sharepoint_* by link: the job's own tab, like browser_open/browser_read.
        foreach (var t in SharePointTools.All(ctx.JobTab)) ctx.Catalog.Add(connectors.Gate(sharePoint, t));
        // ENGIE AI Studio gateway via the CO browser's engie.ai session (the page attaches/refreshes the token).
        foreach (var t in EngieTools.All(ctx.Tab("engie"))) ctx.Catalog.Add(connectors.Gate(_engie, t));
        // Teams and Tractebel Portal: their Read box opens the tools; te_download also needs Create on the target folder.
        foreach (var t in TeamsTools.All(ctx.Tab("teams"))) ctx.Catalog.Add(connectors.Gate(teams, t, Rights.Read));
        foreach (var t in TractebelTools.All(ctx.Tab("tractebel"))) ctx.Catalog.Add(connectors.Gate(tractebel, t, Rights.Read));
        // 0.8.8 SAP Fiori: sap_get needs Read, sap_write Create, sap_update Edit, sap_delete Delete.
        foreach (var t in SapTools.All(ctx.Tab("sap")))
            ctx.Catalog.Add(connectors.Gate(sap, t, t.Descriptor.Name switch
            {
                "sap_write" => Rights.Create, "sap_update" => Rights.Edit, "sap_delete" => Rights.Delete, _ => Rights.Read,
            }));
        foreach (var t in outlookTools) ctx.Catalog.Add(t);

        // SharePoint folders connected by link: HTTP with the CO browser's session, Okta password as fallback.
        _sharePoint = new SharePointService(ctx.Tab("sharepoint"), ctx.Secrets, Path.Combine(ctx.DataDir, "sharepoint.json"));
        ctx.Folders.RemoteFolders = new SharePointFs(_sharePoint);
        // 0.8.2: engie.ai sign-in falls back to the Okta Identity Engine flow (password + Okta Verify push with a
        // number) via the SharePoint service; the password stays in-process, only the Okta session cookies are carried
        // into the engie.ai tab.
        EngieTools.EngieOktaSignIn = _sharePoint.EngieOktaSignInAsync;

        // 0.7.13 / 0.8.2: engie.ai session and the Okta Verify number in the heartbeat (Bridge pushes it to the phone).
        ConnectorHooks.HeartbeatSession = EngieTools.HeartbeatState;
        ConnectorHooks.HeartbeatNumber = EngieTools.EngieOktaNumber;
        ConnectorHooks.HeartbeatConnectorId = () => "engie"; // Freedom F1 5b: id of the gateway (connector-manifest.json)
        // 0.7.9: engie.ai needs an interactive sign-in (its tab was brought to the front); 0.8.2: the Okta Verify number.
        EngieTools.SignInNeeded = msg => ConnectorHooks.Notice?.Invoke(new UserNotice("CO Desktop", msg, true, 8));
        EngieTools.OktaNumberNotice = number => ConnectorHooks.Notice?.Invoke(
            new UserNotice("engie.ai sign-in", "Open Okta Verify and choose the number " + number + ".", false, 30));
        // 0.7.11: the ENGIE toggle of CO Web (window.coDesktop: "status" carries engie, openEngie opens/reloads the tab).
        ConnectorHooks.WebStatus["engie"] = EngieStatus;
        ConnectorHooks.WebStatus["corp"] = EngieStatus; // Freedom F1 5b: CO Web 0.99.1 reads corp, older ones engie
        ConnectorHooks.WebMethods["openEngie"] = async ct =>
        {
            if (!EngieOn()) throw new InvalidOperationException("engie_connector_off");
            await EngieTools.OpenAsync(ctx.Tab("engie"), ct);
            return EngieStatus();
        };
    }

    public void Start(CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Register the module first.");
        _ct = ct;
        // 0.7.9: keep the engie.ai tab open and its sign-in fresh while the ENGIE connector is on (Read ticked);
        // starts the CO browser if needed. A real sign-in brings the tab to the front and shows a tray notice.
        _ = EngieTools.KeepAliveLoopAsync(ctx.Tab("engie"), EngieOn, ct);
    }

    public IEnumerable<object> SettingsPanels()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Register the module first.");
        yield return CorporatePanels.FolderByLink(ctx);
        yield return CorporatePanels.OktaBox(_sharePoint, ctx.Log);
    }

    // 0.7.11: the ENGIE connector has Read ticked on this computer.
    private bool EngieOn() => _engie != null && _engie.Rights.HasFlag(Rights.Read);

    // 0.7.11: engie.ai session state for the CO Web ENGIE toggle ("status" and "openEngie" replies). Answers from the
    // cache and starts a background probe when it is older than 60 s.
    private object EngieStatus()
    {
        if (!EngieOn()) return new { state = "off", detail = "", checkedUtc = (string?)null };
        var (st, detail, checkedUtc) = EngieTools.Session;
        if (st == EngieTools.EngieSessionState.Unknown || DateTime.UtcNow - checkedUtc > TimeSpan.FromSeconds(60))
            _ = EngieTools.RefreshSessionAsync(_ctx!.Tab("engie"), _ct)
                .ContinueWith(t => _ctx.Log("engie status refresh: " + t.Exception!.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
        string state = st switch
        {
            EngieTools.EngieSessionState.Online => "online",
            EngieTools.EngieSessionState.SignInNeeded => "signin",
            EngieTools.EngieSessionState.Offline => "offline",
            _ => "unknown",
        };
        return new { state, detail, checkedUtc = checkedUtc == DateTime.MinValue ? null : checkedUtc.ToString("o") };
    }
}
