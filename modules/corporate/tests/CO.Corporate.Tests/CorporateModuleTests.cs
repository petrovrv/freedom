// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Corporate;

namespace CO.Corporate.Tests;

// Freedom F1 (SPEC §3): the core catalog has no corporate tools; the module adds them (same names and connector ids
// as CO Desktop 0.9.x) only when CO Desktop registers it.
public class CorporateModuleTests
{
    private static readonly string[] CorporateTools =
    {
        "engie_chat", "engie_fetch", "outlook_search", "outlook_draft", "calendar_list", "sharepoint_list", "sharepoint_read",
        "sharepoint_download", "te_search", "te_download", "teams_chats", "teams_messages", "sap_get", "sap_write",
    };

    private static readonly string[] CorporateConnectorIds = { "outlook", "teams", "onedrive", "sharepoint", "tractebel", "sap", "engie" };

    private static (ToolCatalog Catalog, ConnectorRegistry Connectors, FolderRegistry Folders) Build(bool corporate)
    {
        var browser = new FakeBrowser();
        var catalog = new ToolCatalog();
        foreach (var t in FsTools.All()) catalog.Add(t);
        foreach (var t in BrowserTools.All(browser)) catalog.Add(t);
        var connectors = new ConnectorRegistry(null);
        var folders = new FolderRegistry();
        if (corporate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "co-corp-" + Guid.NewGuid().ToString("N"));
            new CorporateModule().Register(new ConnectorModuleContext
            {
                Catalog = catalog, Connectors = connectors, Folders = folders, Tab = _ => browser,
                JobTab = _ => new BrowserUse(browser, null), Browser = browser, Secrets = new MemorySecrets(), DataDir = dir,
            });
        }
        catalog.Add(new ConnectorsStatusTool(connectors));
        return (catalog, connectors, folders);
    }

    private static bool IsCorporate(string tool) =>
        new[] { "engie_", "outlook_", "calendar_", "sharepoint_", "te_", "teams_", "sap_" }.Any(p => tool.StartsWith(p, StringComparison.Ordinal));

    [Fact]
    public void OffMeansNoCorporateToolsConnectorsOrRemoteFolders()
    {
        var (catalog, connectors, folders) = Build(corporate: false);
        var names = catalog.Descriptors.Select(d => d.Name).ToList();
        Assert.Contains("browser_open", names);
        Assert.Contains("fs_read", names);
        Assert.DoesNotContain(names, IsCorporate);
        Assert.Empty(connectors.All);
        Assert.Null(folders.RemoteFolders);
    }

    [Fact]
    public void OnAddsTheCorporateToolsAndConnectors()
    {
        var (catalog, connectors, folders) = Build(corporate: true);
        var names = catalog.Descriptors.Select(d => d.Name).ToHashSet();
        foreach (var t in CorporateTools) Assert.Contains(t, names);
        Assert.Equal(CorporateConnectorIds, connectors.All.Select(c => c.Id));
        Assert.IsType<SharePointFs>(folders.RemoteFolders);
        Assert.True(ConnectorHooks.WebMethods.ContainsKey("openEngie"));
        Assert.True(ConnectorHooks.WebStatus.ContainsKey("engie"));
        // Each corporate tool sits behind its connector's boxes or checks its own settings (Outlook): none is open
        // while nothing is ticked except the defaults (Read for ENGIE, SharePoint, Teams, Tractebel, SAP).
        Assert.Equal(Rights.None, connectors.Find("outlook")!.Rights);
        Assert.Equal(Rights.Read, connectors.Find("engie")!.Rights);
    }
}
