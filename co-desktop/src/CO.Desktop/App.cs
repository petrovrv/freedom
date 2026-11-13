// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using CO.Core.Auth;
using CO.Core.Connectors;
using CO.Core.Policy;
using CO.Core.Files;
using CO.Core.Host;
using CO.Core.Runtime;
using CO.Core.Transport;
using CO.Desktop.Platform;
using CO.Desktop.Ui;

namespace CO.Desktop;

public sealed class App : Application
{
    private static Mutex? _single;

    [STAThread]
    public static void Main()
    {
        _single = new Mutex(true, "CO.Desktop.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("CO Desktop is already running.", "CO Desktop");
            return;
        }
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UI exception: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "CO Desktop — error");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // The Pub/Sub client recreates its streams itself (also after "Unavailable"); only other errors are logged.
            if (!e.Exception.ToString().Contains("SubscriberClientImpl"))
                Log.Write("Unobserved: " + e.Exception);
            e.SetObserved();
        };
        app.Run(new MainWindow(Services.Create()));
    }
}

public sealed class Services
{
    public required DesktopSettings Settings { get; init; }
    public required HttpClient Http { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required WindowsKeyStore Keys { get; init; }
    public required InstalledAppAuth? Auth { get; init; }
    public required DeviceRuntime Runtime { get; init; }
    public required WpfApprovalService Approvals { get; init; }
    public required CO.Core.Browser.EdgeBrowser Browser { get; init; }
    public required ConnectorRegistry Connectors { get; init; }
    public required CO.Core.Browser.AgentWebSettingsStore AgentWeb { get; init; }
    // Freedom F10: compiled module parts registered at start (their module enabled and on for this device).
    public required IReadOnlyList<IConnectorModule> Modules { get; init; }
    // Freedom F10 (SPEC §3.6): modules of the workbench (db.php modules_list) with a device part: their device tools in
    // the catalog through the risk classes, their device ops for ui.settings pages, the per-device switch.
    public required CO.Core.Modules.ModuleLoader ModuleLoader { get; init; }
    // S3: Bridge doctor, only on the computer where the Bridge launcher is installed.
    public required CO.Core.Doctor.BridgeDoctor? Doctor { get; init; }
    // S4: agent CLI subscriptions of this computer (Subscriptions tab, device tool cli_subs).
    public required CO.Core.Subscriptions.SubscriptionManager Subs { get; init; }

    // One Google token per computer, kept in Credential Manager (sign in once).
    public const string CredentialTarget = "CO.Desktop.Google";
    public const string OwnerScopes = "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/pubsub";
    public const string MemberScopes = "https://www.googleapis.com/auth/drive.file";

    public bool SignedIn => Auth?.HasRefreshToken == true && Runtime.State.Account.Length > 0;
    public bool IsOwner => Runtime.State.Role == "owner";

    // 0.8.3: the owner's Google account is signed in (role and address both match); gates the managed-profile switch.
    public static bool IsOwnerAccount(DeviceState st, DesktopSettings settings) =>
        st.Role == "owner" && st.Account.Length > 0 && st.Account.Equals(settings.OwnerEmail, StringComparison.OrdinalIgnoreCase);

    public static Services Create()
    {
        var settings = DesktopSettings.Load();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var secrets = new CredentialManagerStore();
        var keys = new WindowsKeyStore();
        InstalledAppAuth? auth = null;
        string clientFile = settings.ResolveOAuthClientFile();
        if (File.Exists(clientFile))
            auth = new InstalledAppAuth(http, OAuthClient.FromJson(File.ReadAllText(clientFile)), secrets, CredentialTarget);
        IDriveFiles drive = auth != null ? new DriveRest(http, auth) : new UnconfiguredDrive();
        var approvals = new WpfApprovalService();
        var runtime = new DeviceRuntime(DesktopSettings.DataDir, keys, drive, approvals);
        runtime.Updater.SiteTokens = auth;   // 0.9.1: installer from the CO site
        runtime.State.OwnerEmail = settings.OwnerEmail;
        // The owner account always signs in with the owner scopes; restores a role lost from device.json.
        if (runtime.State.Role.Length == 0 && runtime.State.Account.Length > 0)
            runtime.State.Role = runtime.State.Account.Equals(settings.OwnerEmail, StringComparison.OrdinalIgnoreCase) ? "owner" : "member";
        // 0.8.3: the owner can switch the managed profile off on this computer (Connectors -> Managed-device profile);
        // the switch counts only while the owner account is signed in.
        runtime.State.Managed = ManagedDetector.IsDomainOrEntraJoined() && !(settings.ManagedProfileOff && IsOwnerAccount(runtime.State, settings));
        runtime.Save();

        // CO browser: a separate Edge profile the user signs in to; document sites without sync (TZ §7.4).
        string browserDir = Path.Combine(DesktopSettings.DataDir, "browser");
        var browser = new CO.Core.Browser.EdgeBrowser(browserDir);
        // 0.8.4: own taskbar icon for the CO browser windows (orange globe with a robot), unlike the user's Edge.
        string browserIcon = Path.Combine(DesktopSettings.DataDir, "co-browser.ico");
        try
        {
            using var src = typeof(App).Assembly.GetManifestResourceStream("CO.Desktop.co-browser.ico")!;
            using var dst = File.Create(browserIcon);
            src.CopyTo(dst);
        }
        catch (Exception ex) { Log.Write("co-browser.ico: " + ex.Message); /* an older copy or the Edge icon stays */ }
        if (CO.Core.Browser.EdgeBrowser.FindEdge() is string edgeExe)
            _ = CO.Desktop.Platform.BrowserWindowBadge.RunAsync(browser.BrowserProcessIdAsync, browserIcon,
                CO.Core.Browser.EdgeBrowser.EdgeCommandLine(edgeExe, browserDir), CancellationToken.None);
        // Connectors (CO Desktop → Connectors): external services with their own sign-in and access level, in tab order.
        var connectors = new ConnectorRegistry(Path.Combine(DesktopSettings.DataDir, "connectors.json"));
        // Gmail and Google Calendar of the Claude account: only on the computer that runs Bridge (it publishes the file).
        foreach (var c in CloudConnectors.Load(CloudConnectors.DefaultPath)) connectors.Add(c);

        // 0.7.7: every service has its own background tab; the user's tab is never navigated by tools.
        // 0.9.0: browser_open/browser_read and connector tools by link: one tab "agent:<RunId>" per job, closed after 30 min idle.
        var agentTabs = new CO.Core.Browser.RunTabs<CO.Core.Browser.EdgeBrowser.EdgeTab>("agent", browser.Tab,
            (_, tab, ct) => tab.CloseAsync(ct), sweepEvery: TimeSpan.FromMinutes(1), log: Log.Write);
        var jobTab = CO.Core.Tools.BrowserTools.JobTabs(agentTabs);
        foreach (var t in CO.Core.Tools.BrowserTools.All(jobTab)) runtime.Catalog.Add(t);

        // Freedom F10 (SPEC §3.6): desktop parts of modules compiled into the app (co-desktop/ModuleParts.props: connectors
        // to outside services, tools that drive the CO browser). Registered below, after the module list.
        var parts = ModuleParts.Create(Log.Write);
        var moduleCtx = new ConnectorModuleContext
        {
            Catalog = runtime.Catalog, Connectors = connectors, Folders = runtime.Folders, Tab = browser.Tab, JobTab = jobTab,
            Browser = browser, Secrets = secrets, DataDir = DesktopSettings.DataDir, Log = Log.Write,
        };
        // 0.8.0 agent browser: own tab "agent-web" (the "agent" tab keeps browser_open/read and connector tools by link),
        // confirmations, journal with screenshots. 0.9.0: blocked sites instead of an allowlist; one tab
        // "agent-web:<RunId>" per job, closed after 30 min idle unless the user took it over (or must finish a check).
        var agentWeb = new CO.Core.Browser.AgentWebSettingsStore(Path.Combine(DesktopSettings.DataDir, "agent-browser.json"));
        var agentBrowser = connectors.Add(new CO.Core.Connectors.AgentBrowserConnector(agentWeb, runtime.State.Managed));
        if (!runtime.State.Managed)
        {
            // Company policy: no agent driving of a browser with company sign-ins on managed devices.
            string journalDir = Path.Combine(DesktopSettings.DataDir, "agent-browser");
            var agentPages = new CO.Core.Browser.RunTabs<CO.Core.Browser.AgentPage>("agent-web",
                key => new CO.Core.Browser.AgentPage(browser.Tab(key), agentWeb, journalDir, Log.Write),
                (_, page, ct) => page.CloseAsync(ct),
                // 0.9.8: a tab with typed form data stays up to 6 h (idle limit 2 h otherwise).
                // 0.9.9: lifetimes per chat (idleMin / formIdleMin / captchaKeep of the call, see TabChamberlain.IdleLimit),
                // several tabs per chat; a tab the user pinned in the CO browser is never closed or sent to another site.
                keep: page => page.Paused != null,
                sweepEvery: TimeSpan.FromMinutes(1), log: Log.Write,
                state: page => new CO.Core.Browser.TabState(page.LastUrl, page.HasInput, page.CaptchaPending, page.Paused != null),
                pinned: browser.PinnedKeysAsync);
            foreach (var t in CO.Core.Tools.AgentBrowserTools.All(agentPages, () => agentWeb.Current.Block, () => agentWeb.Current.ConfirmClicks, () => agentWeb.Current.ConfirmForms))
                runtime.Catalog.Add(connectors.Gate(agentBrowser, t, t.Descriptor.Class == CO.Core.Policy.RiskClass.R ? Rights.Read : Rights.Edit));
        }
        // 0.9.0: per-job tabs left by an earlier session: close those older than 24 h (and the 0.8.x "agent"/"agent-web"
        // tabs). Only tabs listed in co-tabs.json; never starts the CO browser.
        _ = Task.Run(async () =>
        {
            try
            {
                int n = await browser.CloseStaleTabsAsync(k => k == "agent" || k == "agent-web"
                    || k.StartsWith("agent:", StringComparison.Ordinal) || k.StartsWith("agent-web:", StringComparison.Ordinal),
                    TimeSpan.FromHours(24), CancellationToken.None);
                if (n > 0) Log.Write($"CO browser: closed {n} stale job tab(s)");
            }
            catch (Exception ex) { Log.Write("CO browser: stale tab cleanup failed: " + ex.Message); }
        });
        runtime.Catalog.Add(new ConnectorsStatusTool(connectors));
        // Agent-forced self-update (no manual button): device_update forces CheckForUpdateAsync now.
        foreach (var t in CO.Core.Tools.DeviceTools.All(runtime)) runtime.Catalog.Add(t);
        // Oversized results go to a read-only connected folder the model can page through (TZ §8.4).
        string results = Path.Combine(DesktopSettings.DataDir, "results");
        Directory.CreateDirectory(results);
        if (runtime.Folders.Find(settings.ResultsFolderId) == null)
            runtime.Folders.Add(results, settings.ResultsFolderId, writable: false, project: false, synced: false);

        CO.Core.Doctor.BridgeDoctor? doctor = null;
        var bridgeLayout = CO.Core.Doctor.BridgeLayout.Load();
        if (bridgeLayout is { } layout)
        {
            doctor = new CO.Core.Doctor.BridgeDoctor(layout, new WindowsDoctorProbe(http), Path.Combine(DesktopSettings.DataDir, "doctor.json"), log: Log.Write);
            doctor.Send = CO.Core.Doctor.BridgeDoctor.HostEventSender(layout, secrets.Read);
        }

        // S4: subscriptions. The sign-in page opens in the CO browser (the profile where the user is signed in to the
        // sites); registration on the CO server with this computer's Google token, once signed in.
        var subs = new CO.Core.Subscriptions.SubscriptionManager(http) { Log = Log.Write };
        // Managed devices: no agent driving of the CO browser (company policy), the person signs in from the link.
        if (!runtime.State.Managed)
        {
            subs.LoginTab = cli => (browser.Tab("subs-login-" + cli), async () =>
            {
                await browser.CloseTabAsync("subs-login-" + cli, CancellationToken.None);
                await browser.CloseTabAsync("subs-mail", CancellationToken.None);
            });
            subs.Mail = new CO.Core.Subscriptions.GmailWebMail(() => browser.Tab("subs-mail"));
        }
        if (auth != null && settings.WebUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            var registry = new CO.Core.Subscriptions.SubsRegistry(auth,
                () => (runtime.State.DeviceId, runtime.State.Short, runtime.State.Name), settings.WebUrl, bridgeLayout?.HostId ?? "");
            subs.Report = (all, ct) => auth.HasRefreshToken && runtime.State.Account.Length > 0
                ? registry.PutAsync(all, ct) : Task.FromResult("not signed in to CO");
        }
        runtime.Catalog.Add(new CO.Core.Subscriptions.CliSubsTool(subs));

        // Freedom F10: module loader (ext/1 device.tools). The list comes from the CO site with this computer's Google
        // token; modules.json keeps the last one, so module tools are in the catalog before the first publish.
        // Compiled connector modules are native parts of the registry modules with their id. Last: every core tool is
        // in the catalog already, so a module can never take a core name.
        var moduleSource = auth != null && settings.WebUrl.StartsWith("https://", StringComparison.Ordinal)
            ? new CO.Core.Modules.ModuleRegistryClient(auth, settings.WebUrl) : null;
        var moduleLoader = new CO.Core.Modules.ModuleLoader(runtime.Catalog, DesktopSettings.DataDir, moduleSource,
            natives: parts, log: Log.Write);
        moduleLoader.Managed = () => runtime.State.Managed;
        // a module not signed by the maintainers runs its own script for a settings page only after a yes here
        moduleLoader.ConfirmScriptOp = (m, op, ct) => System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            System.Windows.MessageBox.Show(
                $"The module {m.Title} {m.Version} (not signed by the Freedom maintainers) wants to run its own code on this computer for '{op}'.\n\nAllow it until CO Desktop restarts?",
                "CO Desktop", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No)
            == System.Windows.MessageBoxResult.Yes).Task;
        // an explicit choice of an older version (a desktop.json switch of a part) becomes the part's device switch once
        foreach (var p in parts)
            if (p.LegacySetting.Length > 0 && settings.Legacy != null && settings.Legacy.TryGetValue(p.LegacySetting, out var old)
                && old.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                && moduleLoader.SeedSwitch(p.Id, old.ValueKind == System.Text.Json.JsonValueKind.True))
                Log.Write($"module part '{p.Id}': device switch {(old.ValueKind == System.Text.Json.JsonValueKind.True ? "on" : "off")} from desktop.json {p.LegacySetting}");
        moduleLoader.LoadCached();
        // A compiled part is registered (its connectors, tools, hooks, background work) only while its module is enabled in
        // the workbench and on for this device, as the last module list says; a later change applies after a restart.
        var modules = ModuleParts.RegisterActive(parts, moduleLoader, moduleCtx, Log.Write);
        if (modules.Count > 0) moduleLoader.Apply(moduleLoader.Modules);

        return new Services
        {
            Settings = settings, Http = http, Secrets = secrets, Keys = keys, Auth = auth,
            Runtime = runtime, Approvals = approvals, Browser = browser, Connectors = connectors, AgentWeb = agentWeb, Modules = modules, ModuleLoader = moduleLoader, Doctor = doctor, Subs = subs,
        };
    }
}

public static class Log
{
    private static readonly object Sync = new();
    public static string FilePath => Path.Combine(DesktopSettings.DataDir, "co-desktop.log");

    public static void Write(string line)
    {
        lock (Sync)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.UtcNow:O} {line}\n"); } catch (IOException) { }
        }
    }
}

// Placeholder until DeviceOAuthClientFile is set in desktop.json.
internal sealed class UnconfiguredDrive : IDriveFiles
{
    private static Exception E() => new InvalidOperationException(
        "oauth-client.json is missing next to CO.Desktop.exe (reinstall CO Desktop).");
    public Task<string> CreateFolderAsync(string n, string? p, CancellationToken ct) => throw E();
    public Task<string> CreateFileAsync(string n, string p, byte[] c, string m, CancellationToken ct) => throw E();
    public Task UpdateFileAsync(string id, byte[] c, string m, CancellationToken ct) => throw E();
    public Task<byte[]> DownloadAsync(string id, CancellationToken ct) => throw E();
    public Task<List<DriveFile>> ListChildrenAsync(string p, CancellationToken ct) => throw E();
    public Task DeleteAsync(string id, CancellationToken ct) => throw E();
    public Task ShareAsync(string id, string e, string r, CancellationToken ct) => throw E();
}
