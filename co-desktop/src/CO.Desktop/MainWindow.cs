// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CO.Core.Auth;
using CO.Core.Connectors;
using CO.Core.Host;
using CO.Core.Pairing;
using CO.Core.Policy;
using CO.Core.Protocol;
using CO.Core.Transport;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CO.Desktop;

public sealed class MainWindow : Window
{
    private readonly Services _s;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _code = new() { FontSize = 34, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _grants = new() { Height = 260 };
    private readonly ObservableCollection<string> _activity = new();
    private readonly ListBox _activityList = new() { Height = 320, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly ListBox _pending = new() { Height = 140 };
    private readonly ListBox _devices = new() { Height = 140 };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private readonly CancellationTokenSource _cts = new();
    private int _waiting;
    // 0.7.5: draggable splitter between CO Web and the device panel; width remembered in desktop.json.
    private const double DefaultPanelWidth = 380;
    private const double PanelMinWidth = 260;
    private const double PanelMaxRatio = 0.70;
    private const double WebMinWidth = 400;
    private readonly ColumnDefinition _webColumn = new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = WebMinWidth };
    private readonly ColumnDefinition _panelColumn = new() { Width = new GridLength(DefaultPanelWidth) };
    private readonly GridSplitter _panelSplitter = new()
    {
        Width = 5,
        Background = Brushes.Gainsboro,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.CurrentAndNext,
        ShowsPreview = false,
        Cursor = System.Windows.Input.Cursors.SizeWE,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Focusable = false,
        ToolTip = "Drag to resize the device panel (double-click to reset)",
    };

    private bool IsPanelOpen => _panelColumn.Width.Value > 0;

    // 70 % of the window width (no limit until the window has been measured); never below the open minimum.
    private double PanelMaxWidth => ActualWidth > 0 ? Math.Max(PanelMinWidth, ActualWidth * PanelMaxRatio) : double.PositiveInfinity;

    private double ClampPanelWidth(double width) =>
        Math.Clamp(double.IsFinite(width) && width > 0 ? width : DefaultPanelWidth, PanelMinWidth, PanelMaxWidth);
    private readonly Button _panelToggle = new()
    {
        Width = 30, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Top,
        BorderThickness = new Thickness(1, 0, 1, 0), Background = Brushes.WhiteSmoke,
    };

    private void SetPanel(bool open)
    {
        if (open)
        {
            _panelColumn.MinWidth = PanelMinWidth;
            _panelColumn.MaxWidth = PanelMaxWidth;
            _panelColumn.Width = new GridLength(ClampPanelWidth(_s.Settings.PanelWidth));
            _panelSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _panelColumn.MinWidth = 0;
            _panelColumn.Width = new GridLength(0);
            _panelSplitter.Visibility = Visibility.Collapsed;
        }
        _panelToggle.Content = new TextBlock
        {
            Text = open ? "▶  Device" : "◀  Device", Margin = new Thickness(0, 12, 0, 0),
            LayoutTransform = new RotateTransform(90),
        };
        _panelToggle.ToolTip = open ? "Hide the device panel" : "Show the device panel: device, folders, connectors, CO browser, permissions, activity, owner";
    }

    private void OnPanelSplitterDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!IsPanelOpen) return;
        if (_panelSplitter.Parent is Grid g)
            g.ColumnDefinitions[2].Width = GridLength.Auto;
        // The splitter moves with the panel, so HorizontalChange is the movement since the previous event.
        double requested = _panelColumn.ActualWidth - e.HorizontalChange; // drag left => wider panel
        double webRoom = _webColumn.ActualWidth + _panelColumn.ActualWidth - WebMinWidth;
        double width = Math.Clamp(requested, PanelMinWidth, Math.Max(PanelMinWidth, Math.Min(PanelMaxWidth, webRoom)));
        _panelColumn.Width = new GridLength(width);
        e.Handled = true;
    }

    private void OnPanelSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!IsPanelOpen || e.Canceled || e.HorizontalChange == 0) return; // zero move = click / double-click
        _panelColumn.Width = new GridLength(Math.Round(_panelColumn.Width.Value));
        _s.Settings.PanelWidth = Math.Round(_panelColumn.ActualWidth);
        SaveSettingsSafe();
    }

    private void OnPanelSplitterDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsPanelOpen) return;
        _s.Settings.PanelWidth = DefaultPanelWidth;
        _panelColumn.Width = new GridLength(ClampPanelWidth(DefaultPanelWidth));
        SaveSettingsSafe();
    }

    internal void SaveSettingsSafe()
    {
        try { _s.Settings.Save(); }
        catch { /* ignore persistence errors */ }
    }

    public MainWindow(Services s)
    {
        _s = s;
        Title = $"CO Desktop {Core.Runtime.DeviceRuntime.Version} — {s.Runtime.State.Name}";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1400, area.Width * 0.95);
        Height = Math.Min(900, area.Height * 0.95);

        // CO Web needs more than 1100 px for its project panel (context folders) and usage bars;
        // the device panel folds into a 30 px strip.
        // Columns: 0 web (*), 1 toggle (Auto), 2 splitter (Auto), 3 panel (px). The splitter's drag is converted into
        // a panel width (GridSplitter would otherwise size its own Auto column).
        var grid = new Grid();
        grid.ColumnDefinitions.Add(_webColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(_panelColumn);
        grid.Children.Add(_web);
        _panelToggle.Click += (_, _) => SetPanel(!IsPanelOpen);
        Grid.SetColumn(_panelToggle, 1);
        grid.Children.Add(_panelToggle);
        Grid.SetColumn(_panelSplitter, 2);
        _panelSplitter.AddHandler(System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
            new System.Windows.Controls.Primitives.DragDeltaEventHandler(OnPanelSplitterDragDelta), handledEventsToo: true);
        _panelSplitter.DragCompleted += OnPanelSplitterDragCompleted;
        _panelSplitter.MouseDoubleClick += OnPanelSplitterDoubleClick;
        grid.Children.Add(_panelSplitter);
        var tabs = new TabControl();
        Grid.SetColumn(tabs, 3);
        grid.Children.Add(tabs);
        SizeChanged += (_, e) =>
        {
            // MaxWidth constrains layout only; the stored width is kept so the panel regrows when the window widens.
            if (!e.WidthChanged) return;
            _panelColumn.MaxWidth = PanelMaxWidth;
            // a width clamped in a narrow window returns to the saved one when the window widens
            if (IsPanelOpen) _panelColumn.Width = new GridLength(ClampPanelWidth(_s.Settings.PanelWidth));
        };
        SetPanel(area.Width >= 1600);
        tabs.Items.Add(new TabItem { Header = "Device", Content = Scroll(DeviceTab()) });
        tabs.Items.Add(new TabItem { Header = "Folders", Content = Scroll(FoldersTab()) });
        tabs.Items.Add(new TabItem { Header = "Connectors", Content = Scroll(ConnectorsTab()) });
        tabs.Items.Add(new TabItem { Header = "Browser", Content = Scroll(BrowserTab()) });
        tabs.Items.Add(new TabItem { Header = "Permissions", Content = Scroll(GrantsTab()) });
        tabs.Items.Add(new TabItem { Header = "Activity", Content = Scroll(ActivityTab()) });
        tabs.Items.Add(new TabItem { Header = "Owner", Content = Scroll(OwnerTab()) });
        // S4: own CLI subscriptions (claude, codex): install, sign-in, check, registration on the server.
        var subsPanel = new Ui.SubscriptionsPanel(s.Subs);
        tabs.Items.Add(new TabItem { Header = "Subscriptions", Content = Scroll(subsPanel) });
        _ = subsPanel.RunLoopAsync(_cts.Token);
        if (s.Doctor != null)
        {
            // S3: the doctor works without sign-in, pairing, the site or Bridge.
            var doctor = new Ui.DoctorPanel(s.Doctor, (title, text, warn) => _tray.ShowBalloonTip(10000, title, text,
                warn ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info));
            tabs.Items.Add(new TabItem { Header = "Doctor", Content = Scroll(doctor) });
            _ = doctor.RunLoopAsync(_cts.Token);
        }
        Content = grid;

        if (s.Runtime.State.Managed)
            Title += "  —  MANAGED DEVICE: company data rules apply (TZ §9.6)";

        _tray.Icon = AppIcon.Tray();
        Icon = AppIcon.Window();
        _tray.Text = "CO Desktop";
        _tray.Visible = true;
        // 0.7.9 / 0.8.2: notices of connector modules (an interactive sign-in is needed, a number to pick on the phone).
        ConnectorHooks.Notice = n => Dispatcher.BeginInvoke(() =>
            _tray.ShowBalloonTip(n.Seconds * 1000, n.Title, n.Text,
                n.Warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info));
        _tray.DoubleClick += (_, _) => ShowMain();
        // F14e.2: the apps of the launcher in the tray menu and in the jump list of the CO button; a later start of
        // CO Desktop (a jump list task, a pinned app window) hands its request over the instance pipe
        RefreshApps();
        s.ModuleLoader.Applied += () => Dispatcher.BeginInvoke(RefreshApps);
        _ = Platform.InstancePipe.ListenAsync(line => Dispatcher.BeginInvoke(() => OnInstanceRequest(line)), _cts.Token);

        s.Approvals.Pending += _ => Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Increment(ref _waiting);
            _tray.ShowBalloonTip(5000, "CO Desktop", "An agent asks for confirmation.", System.Windows.Forms.ToolTipIcon.Info);
        });
        s.Runtime.StatusChanged += m => Dispatcher.BeginInvoke(() => { Log.Write(m); RefreshStatus(m); });
        s.Runtime.UpdateReady += r => Dispatcher.BeginInvoke(async () => await InstallWhenIdleAsync(r));
        if (s.Auth != null) s.Auth.ReauthRequired += kind => Dispatcher.BeginInvoke(() => OnReauthRequired(kind));
        _tray.BalloonTipClicked += async (_, _) => { if (_reauthPending) await RunGuardedAsync(ReSignInAsync); };

        Loaded += async (_, _) => await OnLoadedAsync();
        Closed += async (_, _) =>
        {
            _cts.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            await s.Runtime.DisposeAsync();
        };
    }

    private async Task OnLoadedAsync()
    {
        RefreshFolders();
        RefreshGrants();
        RefreshStatus("");
        await InitWebAsync();
        if (_s.Auth == null) { RefreshStatus("oauth-client.json is missing next to CO.Desktop.exe; reinstall CO Desktop."); return; }
        if (!_s.SignedIn)
        {
            if (_s.Runtime.State.Account.Length > 0 && _s.Runtime.State.EnvironmentId.Length > 0)
            {
                OnReauthRequired("revoked");
                await RunGuardedAsync(ReSignInAsync);
            }
            else await FirstRunAsync();
            return;
        }
        await RunGuardedAsync(ConnectAsync);
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(ex.ToString()); RefreshStatus(ex.Message); MessageBox.Show(this, ex.Message, "CO Desktop"); }
    }

    // First start: one Google sign-in covers the device, pairing and the embedded CO Web (TZ §6.1).
    private async Task FirstRunAsync()
    {
        var w = new FirstRunWindow(Settings) { Owner = this };
        if (w.ShowDialog() != true || w.Environment == null) { RefreshStatus("Not signed in. Use Device → Sign in."); return; }
        await RunGuardedAsync(() => SignInAndConnectAsync(w.Environment));
    }

    private async Task SignInAndConnectAsync(string env)
    {
        string expected = Settings.Members.TryGetValue(env, out var m) ? m : "";
        bool owner = expected.Equals(Settings.OwnerEmail, StringComparison.OrdinalIgnoreCase);
        RefreshStatus("Waiting for Google sign-in in the browser…");
        string? email = await _s.Auth!.SignInAsync(owner ? Services.OwnerScopes : Services.MemberScopes,
            InstalledAppAuth.OpenInBrowser, _cts.Token, expected.Length > 0 ? expected : null);
        if (expected.Length > 0 && !string.Equals(email, expected, StringComparison.OrdinalIgnoreCase))
        {
            _s.Auth.SignOut();
            throw new InvalidOperationException($"Signed in as {email}, but {env} uses {expected}. Sign in again with {expected}.");
        }
        _s.Runtime.State.Account = email ?? "";
        _s.Runtime.State.Role = owner ? "owner" : "member";
        _s.Runtime.State.EnvironmentId = env;
        _s.Runtime.Save();
        // 0.8.3: the managed-profile switch counts only for the owner; another account restarts with the profile on.
        if (!_s.Runtime.State.Managed && Settings.ManagedProfileOff && CO.Desktop.Platform.ManagedDetector.IsDomainOrEntraJoined()
            && !Services.IsOwnerAccount(_s.Runtime.State, Settings))
        {
            MessageBox.Show(this, "The managed-device profile was switched off by the owner. CO Desktop restarts with the profile on for this account.", "CO Desktop");
            Restart("managed profile on for a non-owner account");
            return;
        }
        if (_web.CoreWebView2 != null) _web.CoreWebView2.Reload();   // CO Web picks up the desktop token
        await ConnectAsync();
    }

    private bool _reauthPending;

    // Google refused the stored token (for example the Workspace "Google Cloud session control" asks to
    // re-authenticate every 16 hours). Pairing, keys and folders stay; only the Google sign-in is repeated,
    // in the system browser where passkeys work.
    private void OnReauthRequired(string kind)
    {
        if (_reauthPending) return;
        _reauthPending = true;
        Log.Write("Google token refused: " + kind);
        SetPanel(true);
        RefreshStatus(kind == "reauth"
            ? "Google asks to sign in again (Workspace reauthentication policy). Press \"Sign in with Google\"."
            : "The Google sign-in was revoked. Press \"Sign in with Google\".");
        _tray.ShowBalloonTip(10000, "CO Desktop", "Google sign-in expired. Click here to sign in again.", System.Windows.Forms.ToolTipIcon.Warning);
    }

    private async Task ReSignInAsync()
    {
        string env = _s.Runtime.State.EnvironmentId;
        string account = _s.Runtime.State.Account;
        RefreshStatus("Waiting for Google sign-in in the browser…");
        string? email = await _s.Auth!.SignInAsync(_s.IsOwner ? Services.OwnerScopes : Services.MemberScopes,
            InstalledAppAuth.OpenInBrowser, _cts.Token, account);
        if (!string.Equals(email, account, StringComparison.OrdinalIgnoreCase))
        {
            _s.Auth.SignOut();
            throw new InvalidOperationException($"Signed in as {email}, but this computer belongs to {account} ({env}). Sign in again with {account}.");
        }
        _reauthPending = false;
        Log.Write("Google sign-in renewed for " + email);
        RefreshStatus("Signed in again.");
        if (_web.CoreWebView2 != null) _web.CoreWebView2.Reload();
        if (_s.Runtime.Host == null) await ConnectAsync();
    }

    // One entry for every "sign in" button: a known account signs in again without the first-run questions.
    private async Task SignInAsync()
    {
        if (_s.SignedIn) { if (_s.Runtime.Host == null) await ConnectAsync(); return; }
        if (_s.Runtime.State.Account.Length > 0 && _s.Runtime.State.EnvironmentId.Length > 0) await ReSignInAsync();
        else await FirstRunAsync();
    }

    private async Task ConnectAsync()
    {
        if (_s.Runtime.State.Paired) { await StartRuntimeAsync(); return; }
        if (_s.Runtime.State.Folder == null || _s.Runtime.State.PairingRequestFileId.Length == 0)
            await _s.Runtime.CreatePairingRequestAsync(_s.Runtime.State.Account, _s.Runtime.State.EnvironmentId, _cts.Token);
        RefreshStatus(_s.IsOwner ? "Connecting to Bridge…" : "Waiting for the owner to approve this computer…");
        await PollPairingAsync();
    }

    // ------------------------------------------------------------------ web UI

    private async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DesktopSettings.DataDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
            var origin = new Uri(_s.Settings.WebUrl).GetLeftPart(UriPartial.Authority);
            _webOrigin = origin;
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DesktopShimScript(origin));
            _web.CoreWebView2.WebMessageReceived += async (_, e) => await OnWebMessageAsync(_web.CoreWebView2, e, origin, pageWindow: false);
            // Freedom F10 (ext/1 ui.window): a module page CO Web opens in a new tab gets its own window when the module
            // declares it so (ui.page "window": true); other new tabs keep the default.
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.GetLeftPart(UriPartial.Authority) != origin) return;
                foreach (var m in _s.ModuleLoader.Modules)
                {
                    if (m.WindowPage(u.AbsolutePath) is not { } page) continue;
                    e.Handled = true;
                    ModulePageWindow.Open(this, env, u, m, page);
                    return;
                }
            };
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                var u = new Uri(e.Uri);
                // Google sign-in passes through country domains (accounts.google.be) and youtube.com to set
                // its cookies; those redirects stay here, links the user clicks open in the system browser.
                bool allowed = u.GetLeftPart(UriPartial.Authority) == origin || GoogleSignInHost.IsMatch(u.Host)
                               || (!e.IsUserInitiated && u.Scheme == "https");
                if (!allowed)
                {
                    e.Cancel = true;
                    CO.Core.Auth.InstalledAppAuth.OpenInBrowser(e.Uri);  // external links open in the system browser
                }
            };
            _web.Source = new Uri(_s.Settings.WebUrl);
            _webEnv = env;   // ready for apps only now: an app opened earlier would be overwritten by the start page
            OpenPendingApps();   // started with --app, or asked for while the web view was starting
        }
        catch (Exception ex)
        {
            Log.Write("WebView2: " + ex);
            _web.Visibility = Visibility.Collapsed;
            RefreshStatus("WebView2 Runtime is missing or failed: " + ex.Message);
        }
    }

    private string _webOrigin = "";
    private CoreWebView2Environment? _webEnv;

    // ------------------------------------------------------------------ apps (F14e.2)

    // apps asked for before the web view or the module list was ready, in order, each once
    private readonly List<string> _pendingApps = App.StartupApp is { } first ? new() { first } : new();
    private string _appsShown = "\0";               // what the tray menu and the jump list show now

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnInstanceRequest(string line)
    {
        if (line == "show") ShowMain();
        else if (line.StartsWith("app ", StringComparison.Ordinal)) OpenApp(line[4..].Trim());
    }

    /// <summary>Opens an app of the launcher ("module/page"): a ui.window page in its own window, another app in the
    /// shell of the main window (CO Web takes the bare /x/ address into the shell).</summary>
    internal void OpenApp(string key)
    {
        if (CO.Core.Modules.ModuleList.ParseAppKey(key) is not { } k) { Log.Write("open app: bad key"); return; }
        var app = CO.Core.Modules.ModuleList.Apps(_s.ModuleLoader.Modules).FirstOrDefault(a => a.Key == key);
        if (_webEnv == null || _webOrigin.Length == 0 || (app == null && !AppsKnown))
        {
            // the web view or the module list is not there yet: opened when it comes
            if (!_pendingApps.Contains(key)) _pendingApps.Add(key);
            return;
        }
        if (app == null) { Log.Write("open app: " + key + " is not an app of this workbench"); ShowMain(); return; }
        string path = app.Page.Path.Length > 0 ? app.Page.Path : "/x/" + k.Module + "/" + k.Page;
        if (app.Page.Window)
        {
            ModulePageWindow.Open(this, _webEnv, new Uri(_webOrigin + path + "?window=1"), app.Module, app.Page);
            return;
        }
        _web.CoreWebView2?.Navigate(_webOrigin + "/x/" + k.Module + "/" + k.Page);
        ShowMain();
    }

    // the module list was applied once (maybe empty, also the cached one); before that an unknown app may still come
    private bool AppsKnown => _s.ModuleLoader.Listed;

    private void OpenPendingApps()
    {
        if (_webEnv == null || !AppsKnown || _pendingApps.Count == 0) return;
        var keys = _pendingApps.ToList();
        _pendingApps.Clear();
        foreach (var key in keys) OpenApp(key);
    }

    private void RefreshApps()
    {
        var apps = CO.Core.Modules.ModuleList.Apps(_s.ModuleLoader.Modules);
        OpenPendingApps();   // the list came after --app
        string shown = string.Join("\n", apps.Select(a => a.Key + "\t" + a.Label + "\t" + a.Icon + "\t" + a.Page.Window));
        if (shown == _appsShown) return;
        _appsShown = shown;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("Open CO Desktop", null, (_, _) => ShowMain());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        if (apps.Count > 0) menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var jump = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        string exe = Environment.ProcessPath ?? "";
        foreach (var a in apps)
        {
            string key = a.Key;
            System.Drawing.Image? image = null;
            string icon = "";
            try { image = AppIcons.MenuImage(key, a.Icon); icon = AppIcons.For(key, a.Icon).Path; }
            catch (Exception ex) { Log.Write("app icon " + key + ": " + ex.Message); }
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(a.Label, image, (_, _) => OpenApp(key)));
            if (exe.Length > 0)
                jump.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = a.Label, Description = a.Page.Title.Length > 0 ? a.Page.Title : a.Label, CustomCategory = "Apps",
                    ApplicationPath = exe, Arguments = "--app " + key, IconResourcePath = icon.Length > 0 ? icon : exe, IconResourceIndex = 0,
                });
        }
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
        try
        {
            System.Windows.Shell.JumpList.SetJumpList(Application.Current, jump);
            jump.Apply();
        }
        catch (Exception ex) { Log.Write("jump list: " + ex.Message); }
    }

    // window.coDesktop: device identity (TZ §8.3) and the desktop Google token for CO Web (TZ-CO-WEB §5.1).
    // 0.9.2: also injected into module page windows (ModulePageWindow), which answer only getAccessToken and status.
    internal string DesktopShimScript(string origin)
    {
        var info = new
        {
            app = "co-desktop", version = Core.Runtime.DeviceRuntime.Version,
            deviceId = _s.Runtime.State.DeviceId, deviceShort = _s.Runtime.State.Short, managed = _s.Runtime.State.Managed,
        };
        // Connector modules add their own methods (e.g. the toggle of an external AI in CO Web).
        string extra = string.Concat(ConnectorHooks.WebMethods.Keys.Select(k => ", " + k + ": () => call(" + JsonSerializer.Serialize(k) + ")"));
        return "if (location.origin === " + JsonSerializer.Serialize(origin) + ") { (() => {" +
            " const pending = new Map(); let n = 0;" +
            " window.chrome.webview.addEventListener('message', (e) => { const m = e.data || {}; const p = pending.get(m.id); if (!p) return; pending.delete(m.id); m.error ? p.reject(new Error(m.error)) : p.resolve(m.result); });" +
            " const call = (type, args) => new Promise((resolve, reject) => { const id = ++n; pending.set(id, { resolve, reject }); window.chrome.webview.postMessage(args === undefined ? { type, id } : { type, id, args }); });" +
            " window.coDesktop = Object.freeze(Object.assign(" + JsonSerializer.Serialize(info) +
            ", { getAccessToken: () => call('getAccessToken'), signIn: () => call('signIn'), status: () => call('status'), voiceTyping: () => call('voiceTyping')" +
            // Freedom F10 (ext/1 device.tools): device ops of module settings pages and the per-device module switch
            ", moduleOp: (module, op, args) => call('moduleOp', { module, op, args })" +
            ", moduleDevice: () => call('moduleDevice'), moduleDeviceSet: (module, on) => call('moduleDeviceSet', { module, on: on === true })" +
            extra + " }));" +
            " })(); }";
    }

    internal string WebOrigin => _webOrigin;

    internal Task OnPageWindowMessageAsync(CoreWebView2 target, CoreWebView2WebMessageReceivedEventArgs e) =>
        OnWebMessageAsync(target, e, _webOrigin, pageWindow: true);

    private async Task OnWebMessageAsync(CoreWebView2 target, CoreWebView2WebMessageReceivedEventArgs e, string origin, bool pageWindow)
    {
        if (!e.Source.StartsWith(origin + "/", StringComparison.Ordinal) && e.Source != origin) return;
        int id;
        string type;
        JsonElement margs = default;
        try
        {
            if (e.WebMessageAsJson.Length > 64 * 1024) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            id = doc.RootElement.GetProperty("id").GetInt32();
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a)) margs = a.Clone();
        }
        catch (Exception) { return; }

        object reply;
        try
        {
            if (pageWindow && type != "getAccessToken" && type != "status") throw new InvalidOperationException("unknown_request");
            switch (type)
            {
                case "getAccessToken":
                    // Only the owner's token carries the scopes CO Web needs (drive + pubsub).
                    if (!_s.SignedIn || !_s.IsOwner) throw new InvalidOperationException("no_desktop_token");
                    var (token, expiresIn) = await _s.Auth!.GetAccessTokenWithLifetimeAsync(_cts.Token);
                    reply = new { id, result = new { access_token = token, expires_in = expiresIn, email = _s.Runtime.State.Account } };
                    break;
                case "status":
                    // The composer attaches this device to a Run only when it is paired (TZ §8.3).
                    var status = new Dictionary<string, object?>
                    {
                        ["deviceId"] = _s.Runtime.State.DeviceId, ["name"] = _s.Runtime.State.Name, ["short"] = _s.Runtime.State.Short,
                        ["paired"] = _s.Runtime.State.Paired && _s.Runtime.Host != null,
                    };
                    // 0.7.11: fields of connector modules (the session of an external AI for its CO Web toggle).
                    foreach (var (key, field) in ConnectorHooks.WebStatus) status[key] = field();
                    reply = new { id, result = status };
                    break;
                case "voiceTyping":
                    // 0.7.3: WebView2 has no Web Speech service; Windows voice typing types into the focused textarea.
                    _web.Focus();
                    await Task.Delay(150);
                    NativeKeys.WinH();
                    reply = new { id, result = new { started = true } };
                    break;
                case "signIn":
                    await SignInAsync();
                    reply = new { id, result = new { } };
                    break;
                // Freedom F10: modules with a device part (ext/1 device.tools); not for module page windows
                case "moduleDevice" when !pageWindow:
                    reply = new { id, result = _s.ModuleLoader.States().Select(m => new { id = m.Id, on = m.On, active = m.Active, tools = m.Tools, problem = m.Problem, warning = m.Warning }) };
                    break;
                case "moduleDeviceSet" when !pageWindow:
                    await _s.ModuleLoader.SetOnAsync(ArgStr(margs, "module"), margs.ValueKind == JsonValueKind.Object && margs.TryGetProperty("on", out var onv) && onv.ValueKind == JsonValueKind.True, _cts.Token);
                    reply = new { id, result = new { } };
                    break;
                case "moduleOp" when !pageWindow:
                {
                    var opArgs = margs.ValueKind == JsonValueKind.Object && margs.TryGetProperty("args", out var oa) ? oa : default;
                    if (opArgs.ValueKind == JsonValueKind.Undefined) opArgs = JsonDocument.Parse("{}").RootElement;
                    var res = await _s.ModuleLoader.DeviceOpAsync(ArgStr(margs, "module"), ArgStr(margs, "op"), opArgs, _cts.Token);
                    // the answer of the module goes back as plain JSON
                    reply = new { id, result = res == null ? (JsonElement?)null : JsonDocument.Parse(res.ToJsonString()).RootElement };
                    break;
                }
                default:
                    // 0.7.11: methods of connector modules (e.g. open/reload an external AI in its CO-browser tab).
                    if (pageWindow || !ConnectorHooks.WebMethods.TryGetValue(type, out var method)) throw new InvalidOperationException("unknown_request");
                    reply = new { id, result = await method(_cts.Token) };
                    break;
            }
        }
        catch (Exception ex) { reply = new { id, error = ex.Message }; }
        try { target.PostWebMessageAsJson(JsonSerializer.Serialize(reply)); }
        catch (Exception ex) when (pageWindow) { Log.Write("page window reply: " + ex.Message); /* the window closed meanwhile */ }
    }

    private static string ArgStr(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static readonly System.Text.RegularExpressions.Regex GoogleSignInHost = new(
        @"^([a-z0-9-]+\.)*(google\.[a-z]{2,3}(\.[a-z]{2})?|gstatic\.com|googleusercontent\.com|youtube\.com)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal DesktopSettings Settings => _s.Settings;

    // ------------------------------------------------------------------ device tab

    private UIElement DeviceTab()
    {
        var p = Panel();
        p.Children.Add(H("Device"));
        p.Children.Add(_status);
        p.Children.Add(Btn("Sign in with Google and connect", SignInAsync));
        p.Children.Add(_updateStatus);
        p.Children.Add(Btn("Check for updates now", async () =>
        {
            if (_s.Runtime.Host == null) throw new InvalidOperationException("The device is not connected to Bridge yet.");
            await _s.Runtime.CheckForUpdateAsync(_cts.Token);
            _updateStatus.Text = "Updates: " + _s.Runtime.Updater.Status;
        }));
        if (_s.IsOwner)
        {
            // Owner opt-in: skip every confirmation prompt on this computer. Takes effect immediately (read live).
            var trust = new CheckBox
            {
                Content = "I trust this environment — auto-approve all agent actions (no prompts; includes writes, deletions and external actions)",
                IsChecked = _s.Runtime.State.Trusted,
                Margin = new Thickness(0, 12, 0, 0),
            };
            trust.Checked += (_, __) => { _s.Runtime.State.Trusted = true; _s.Runtime.Save(); RefreshStatus("Auto-approval ON: this computer will not ask to confirm agent actions."); };
            trust.Unchecked += (_, __) => { _s.Runtime.State.Trusted = false; _s.Runtime.Save(); RefreshStatus("Auto-approval OFF: agent actions are confirmed as usual."); };
            p.Children.Add(trust);
        }
        else if (_s.Runtime.OwnerTrusted)
            p.Children.Add(Label("Auto-approval is on (granted by the owner): agent actions run without prompts; " +
                "form submits, purchases and deletions in the agent browser still ask."));
        p.Children.Add(Label("Pairing code (members' computers: the owner compares it):"));
        p.Children.Add(_code);
        p.Children.Add(Btn("Undo all file changes of a Run…", () =>
        {
            string? run = Prompt("Run id to undo:");
            if (!string.IsNullOrWhiteSpace(run))
                MessageBox.Show($"Restored {_s.Runtime.Snapshots.RevertRun(run.Trim())} step(s).", "CO Desktop");
            return Task.CompletedTask;
        }));
        p.Children.Add(Btn("Sign out of Google on this computer", () =>
        {
            if (MessageBox.Show(this, "Remove the stored Google sign-in? The device stops serving agents until you sign in again.",
                    "CO Desktop", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.CompletedTask;
            _s.Auth?.SignOut();
            _s.Runtime.State.Account = "";
            _s.Runtime.Save();
            // 0.8.3: the owner's switch of the managed profile ends with the owner's sign-in.
            if (!_s.Runtime.State.Managed && CO.Desktop.Platform.ManagedDetector.IsDomainOrEntraJoined())
            {
                Restart("signed out with the managed profile off");
                return Task.CompletedTask;
            }
            RefreshStatus("Signed out.");
            return Task.CompletedTask;
        }));
        return p;
    }

    private readonly TextBlock _updateStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _installing;

    // Silent reinstall over the running copy (TZ §6.5). Sign-in (Credential Manager), keys and %LOCALAPPDATA%\CO
    // stay; the installer stops this process, and cmd starts the new version after it.
    private async Task InstallWhenIdleAsync(Core.Runtime.UpdateReady r)
    {
        if (_installing) return;
        _installing = true;
        Log.Write($"update {r.Version} ready: {r.SetupPath}");
        while (_s.Runtime.Host is { ActiveCalls: > 0 })
        {
            _updateStatus.Text = $"Updates: {r.Version} waits for the running agent call to finish";
            try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); } catch (OperationCanceledException) { return; }
        }
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CO.Desktop.exe");
        _tray.ShowBalloonTip(3000, "CO Desktop", $"Updating to {r.Version}…", System.Windows.Forms.ToolTipIcon.Info);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c start \"\" /wait \"{r.SetupPath}\" /S & start \"\" \"{exe}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(r.SetupPath)!,
        };
        System.Diagnostics.Process.Start(psi);
        Log.Write($"update {r.Version}: installer started, exiting");
        await Task.Delay(1500);
        Application.Current.Shutdown();
    }

    private async Task PollPairingAsync()
    {
        SetPanel(true);   // the pairing code and status are on the Device tab
        while (!_cts.IsCancellationRequested && !_s.Runtime.State.Paired)
        {
            try
            {
                await _s.Runtime.RefreshBridgeInfoAsync(_cts.Token);
                _code.Text = _s.Runtime.PairingCode() ?? "— — —";
                if (_s.IsOwner && _s.Runtime.State.Bridge?.State == "pending" && !_selfApproved)
                    await SelfApproveAsync();
                RefreshStatus(_s.Runtime.State.Bridge?.State is { Length: > 0 } st ? "Bridge: " + st : "Waiting for Bridge to pick up the request…");
            }
            catch (Exception ex) { RefreshStatus("Pairing check failed: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token); } catch (OperationCanceledException) { return; }
        }
        if (_s.Runtime.State.Paired) await StartRuntimeAsync();
    }

    private bool _selfApproved;

    // The owner's own computer: the pinned Bridge key is checked against owner.json with the owner's token,
    // which replaces the manual code comparison (TZ §6.2.7).
    private async Task SelfApproveAsync()
    {
        var (console, admin) = await OwnerOpsAsync();
        string bridgeKey = await console.BridgePublicKeyAsync(_cts.Token);
        if (bridgeKey != _s.Runtime.State.Bridge!.BridgePublicKey)
            throw new InvalidOperationException("The Bridge key in owner.json differs from the key this computer received. Pairing stopped.");
        var pend = (await console.ListPendingAsync(_cts.Token)).FirstOrDefault(x => x.Request.DeviceId == _s.Runtime.State.DeviceId);
        if (pend == null) return;   // Bridge has not copied the request yet
        DeviceRecord rec = await admin.ApproveAsync(pend.Request, _s.Runtime.State.Account, _cts.Token);
        await console.WriteDeviceRecordAsync(rec, _cts.Token);
        _selfApproved = true;
        RefreshStatus("Approved; Bridge creates the channels (up to a minute).");
    }

    // 0.9.7: pending requests of members in Settings.AutoPairEnvironments are paired without the code once Google
    // confirms that the member's account owns the device folder (AutoPair). Others stay in Owner for the code.
    private readonly HashSet<string> _autoPairRefused = new(StringComparer.Ordinal);
    private bool _autoPairStarted;

    private async Task AutoPairLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await AutoPairOnceAsync(); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write("auto-pair: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task AutoPairOnceAsync()
    {
        if (Settings.AutoPairEnvironments.Count == 0 || !_s.SignedIn || !_s.IsOwner || _reauthPending) return;
        var (console, admin) = await OwnerOpsAsync();
        var pending = await console.ListPendingAsync(_cts.Token);
        if (pending.Count == 0) return;
        // A paired request stays in pending/ until Bridge has made the channels; a device record means done (or revoked).
        var recorded = (await console.ListDevicesAsync(_cts.Token)).Select(d => d.Request.DeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (var pend in pending)
        {
            PairingRequest r = pend.Request;
            if (r.DeviceId == _s.Runtime.State.DeviceId || r.DeviceFolderId.Length == 0 || recorded.Contains(r.DeviceId)) continue;
            if (!Settings.AutoPairEnvironments.Contains(r.EnvironmentId, StringComparer.Ordinal)) continue;   // waits for the code
            try
            {
                var (folderName, owners) = await console.FileOwnersAsync(r.DeviceFolderId, _cts.Token);
                string? refusal = AutoPair.Refusal(r, Settings.Members, Settings.AutoPairEnvironments, Settings.OwnerEmail, folderName, owners);
                if (refusal != null)
                {
                    if (_autoPairRefused.Add(r.DeviceId + "|" + refusal))
                        Log.Write($"auto-pair: {r.DeviceId} ({r.Name}, {r.Account}) waits for the code: {refusal}");
                    continue;
                }
                DeviceRecord rec = await admin.ApproveAsync(r, _s.Runtime.State.Account, _cts.Token);
                rec.PairedBy = _s.Runtime.State.Account + " (auto: folder owned by " + r.Account + ")";
                await console.WriteDeviceRecordAsync(rec, _cts.Token);
                Log.Write($"auto-pair: paired {r.DeviceId} ({r.Name}, {r.Os}, {r.Account}, {r.EnvironmentId})");
                await Dispatcher.InvokeAsync(() => _tray.ShowBalloonTip(8000, "CO Desktop",
                    $"Paired {r.Name} ({r.Account}) automatically.", System.Windows.Forms.ToolTipIcon.Info));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A stale or unreadable request (folder deleted, access removed) must not block the others.
                if (_autoPairRefused.Add(r.DeviceId + "|error|" + ex.Message))
                    Log.Write($"auto-pair: {r.DeviceId} ({r.Name}) failed: {ex.Message}");
            }
        }
    }

    private async Task<(OwnerConsole console, PairingAdmin admin)> OwnerOpsAsync()
    {
        if (!_s.SignedIn || !_s.IsOwner) throw new InvalidOperationException("Only the owner's computer, signed in, can pair devices.");
        string token = await _s.Auth!.GetAccessTokenAsync(_cts.Token);
        return (new OwnerConsole(_s.Http, token, Settings.WorkbenchRootName, Settings.HostId),
                new PairingAdmin(_s.Http, Settings.PubSubProjectId, ct => _s.Auth!.GetAccessTokenAsync(ct)));
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            var sink = new LocalResultSink(_s.Runtime.Folders, Settings.ResultsFolderId);
            await _s.Runtime.StartAsync(sink, _cts.Token);
            _s.Runtime.Host!.Completed += (call, r) => Dispatcher.BeginInvoke(() =>
            {
                _activity.Insert(0, $"{DateTime.Now:HH:mm:ss} {(r.Ok ? "ok " : "ERR")} {call.Tool,-12} run={call.RunId} {(r.Ok ? "" : r.Error?.Code)}");
                if (_activity.Count > 500) _activity.RemoveAt(_activity.Count - 1);
                RefreshGrants();
            });
            _code.Text = "paired";
            StartModulesLoop();
            if (_s.IsOwner && !_autoPairStarted) { _autoPairStarted = true; _ = Task.Run(AutoPairLoopAsync); }
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() => { RefreshStatus(""); if (!_installing) _updateStatus.Text = "Updates: " + _s.Runtime.Updater.Status; });
                    try { await Task.Delay(5000, _cts.Token); } catch (OperationCanceledException) { return; }
                }
            });
        }
        catch (Exception ex) { RefreshStatus("Start failed: " + ex.Message); Log.Write("start: " + ex); }
    }

    private void RefreshStatus(string message)
    {
        var st = _s.Runtime.State;
        string transport = _s.Runtime.PubSub == null ? "not started"
            : $"Pub/Sub {_s.Runtime.PubSub.State}, Drive {_s.Runtime.DriveChannel?.State}";
        _status.Text =
            $"Device id: {st.DeviceId}\nTool server: dev_{st.Short}\nAccount: {st.Account}\nEnvironment: {st.EnvironmentId}\n" +
            $"Keys: {(_s.Keys.HardwareBacked ? "TPM" : "software (DPAPI)")}\nManaged profile: {(st.Managed ? "ON" : "off")}\n" +
            $"Paired: {st.Paired}\nTransport: {transport}\n" + (message.Length > 0 ? "\n" + message : "");
        _tray.Text = $"CO Desktop — {(st.Paired ? transport : "not paired")}".Length > 63
            ? "CO Desktop" : $"CO Desktop — {(st.Paired ? transport : "not paired")}";
    }

    // ------------------------------------------------------------------ folders

    private readonly Grid _folderTable = new() { Margin = new Thickness(0, 4, 0, 0) };

    // Folders on this computer: local, network and synced by OneDrive / Drive for Desktop. Folders connected by link
    // are rows of the Connectors tab.
    private UIElement FoldersTab()
    {
        var p = Panel();
        p.Children.Add(H("Folders on this computer"));
        p.Children.Add(Label("The agent sees only these folders; paths are '<name>/<relative path>'. A ticked box is the permission: " +
            "the agent acts without asking. Edits keep a snapshot (Undo on the Device tab), deletions go to the Recycle Bin. " +
            "A subfolder added inside a connected folder becomes its row with own boxes; the deepest row wins."));
        p.Children.Add(_folderTable);

        p.Children.Add(H("Add"));
        var id = new TextBox();
        p.Children.Add(Label("Name for the agent (optional; made from the folder name, a-z, 0-9, '-', ≤ 12). New rows start with Read."));
        p.Children.Add(id);
        p.Children.Add(Btn("Add folder or subfolder…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to connect" };
            if (dlg.ShowDialog(this) == true) ConnectLocal(dlg.FolderName, id.Text);
            id.Text = "";
            return Task.CompletedTask;
        }));

        p.Children.Add(Label("Synced by OneDrive on this computer (OneDrive and document libraries of your organisation; in the browser: " +
            "library → Sync or \"Add shortcut to My files\", then Refresh):"));
        var synced = new ComboBox();
        void RefreshSynced()
        {
            synced.Items.Clear();
            foreach (string e in CO.Core.Files.SyncDetector.OneDriveRoots().Concat(CO.Core.Files.SyncDetector.LibraryEntries()).Distinct(StringComparer.OrdinalIgnoreCase))
                synced.Items.Add(e);
            synced.SelectedIndex = synced.Items.Count > 0 ? 0 : -1;
        }
        RefreshSynced();
        p.Children.Add(synced);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Btn("Add selected", () =>
        {
            if (synced.SelectedItem is string path) ConnectLocal(path, id.Text);
            id.Text = "";
            return Task.CompletedTask;
        }));
        row.Children.Add(new TextBlock { Width = 8 });
        row.Children.Add(Btn("Refresh", () => { RefreshSynced(); return Task.CompletedTask; }));
        p.Children.Add(row);

        _s.Runtime.Folders.Changed += () => Dispatcher.BeginInvoke(RefreshFolders);
        return p;
    }

    private void ConnectLocal(string path, string typedId)
    {
        _s.Runtime.Folders.Add(path, _s.Runtime.Folders.MakeId(typedId, path), Rights.Read, project: false);
    }

    private void RefreshFolders()
    {
        var reg = _s.Runtime.Folders;
        var t = NewTable(_folderTable, ("Folder", 1), ("Read", 0), ("Create", 0), ("Edit", 0), ("Delete", 0), ("Project", 0), ("", 0));
        var local = reg.All.Where(f => !f.IsRemote).ToList();
        var roots = CO.Core.Files.SyncDetector.KnownSyncRoots().ToList();
        foreach (var f in local)
        {
            bool system = f.Id == Settings.ResultsFolderId;   // CO's own folder for large results
            string? kind = f.Synced ? CO.Core.Files.PlaceNames.SyncedKind(f.Path, roots) : null;
            string sub = kind != null ? $"{kind}, synced · {CO.Core.Files.PlaceNames.Tail(f.Path)}" : ShortPath(f.Path);
            var cells = new List<UIElement> { NameCell(f.Id, sub, f.Path, rename: system ? null : () => RenameFolder(f.Id)) };
            foreach (var r in RightsText.Columns)
                cells.Add(Box(f.Granted.HasFlag(r), !system, on => reg.SetRights(f.Id, null, on ? f.Granted | r : f.Granted & ~r)));
            cells.Add(Box(f.Project, !system, on => reg.SetProject(f.Id, on), "Project folder: its content is trusted"));
            cells.Add(system ? Cell("") : RemoveButton(() => reg.Remove(f.Id)));
            AddRow(t, cells);
            AddRuleRows(t, f, 2, rule => RemoveButton(() => reg.RemoveRule(f.Id, rule.Path)));
        }
        if (local.Count == 0) AddRow(t, new UIElement[] { Cell("(no folders)", grey: true) });
    }

    // Subfolder rules of a connected folder: indented rows with their own boxes. tail = cells after the boxes;
    // the last of them is the remove button.
    private void AddRuleRows(Grid t, CO.Core.Files.ConnectedFolder f, int tail, Func<CO.Core.Files.FolderRule, UIElement> remove)
    {
        var reg = _s.Runtime.Folders;
        foreach (var rule in f.Rules.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            bool same = f.InheritedAt(rule.Path) == rule.Rights;
            var rc = new List<UIElement>
            {
                NameCell("└ " + rule.Path, same ? "same as above" : "", same ? "Same boxes as the row above: this row changes nothing yet" : rule.Path,
                    indent: true, grey: same),
            };
            foreach (var r in RightsText.Columns)
                rc.Add(Box(rule.Rights.HasFlag(r), true, on => reg.SetRights(f.Id, rule.Path, on ? rule.Rights | r : rule.Rights & ~r)));
            for (int i = 1; i < tail; i++) rc.Add(Cell(""));
            rc.Add(remove(rule));
            AddRow(t, rc);
        }
    }

    private void RenameFolder(string id)
    {
        string? name = Prompt($"New agent name for '{id}' (a-z, 0-9, '-', ≤ 12). Chats that used '{id}/…' must use the new name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { _s.Runtime.Folders.Rename(id, name.Trim().ToLowerInvariant()); }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, "CO Desktop"); }
    }

    // The panel is narrow: long paths show their last two folders, the full path is the tooltip.
    private static string ShortPath(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        return path.Length <= 28 || parts.Length <= 3 ? path : "…\\" + string.Join("\\", parts[^2..]);
    }

    // ------------------------------------------------------------------ connectors

    private readonly Grid _connectorTable = new() { Margin = new Thickness(0, 4, 0, 0) };

    private UIElement ConnectorsTab()
    {
        var p = Panel();
        p.Children.Add(H("Connectors"));
        p.Children.Add(Label("Everything outside this computer: mail, calendars, folders connected by link, portals. The boxes mean " +
            "the same as on the Folders tab, and a ticked box is the permission. Nothing is sent on your behalf: mail stays a draft. " +
            "Grey boxes do not apply. Agents see this table with the tool connectors_status."));
        p.Children.Add(_connectorTable);
        p.Children.Add(Label("Point at a name or status for details."));
        p.Children.Add(Btn("Check all", async () =>
        {
            foreach (var c in _s.Connectors.All) await c.CheckAsync(_cts.Token);
        }));

        // Freedom F10: modules of the workbench with a device part or a settings page (ui.settings, shown by CO Web).
        p.Children.Add(_modulesBox);
        RefreshModulesBox();
        _s.ModuleLoader.Applied += () => Dispatcher.BeginInvoke(RefreshModulesBox);
        // Connector modules compiled into the app keep their WPF panels until they have a ui.settings page.
        foreach (var m in _s.Modules)
            foreach (var panel in m.SettingsPanels().OfType<UIElement>()) p.Children.Add(panel);

        p.Children.Add(AgentSitesBox());
        p.Children.Add(ManagedProfileBox());
        _s.Connectors.Changed += _ => Dispatcher.BeginInvoke(RefreshConnectors);
        _s.Runtime.Folders.Changed += () => Dispatcher.BeginInvoke(RefreshConnectors);
        RefreshConnectors();
        return p;
    }

    // ------------------------------------------------------------------ modules (Freedom F10, SPEC §3.6)

    private readonly StackPanel _modulesBox = new();
    private bool _modulesLoop;

    private void RefreshModulesBox()
    {
        _modulesBox.Children.Clear();
        var list = _s.ModuleLoader.Modules.Where(m => m.Enabled && (m.HasDevicePart || m.Ui.Any(u => u.Slot == "ui.settings"))).ToList();
        if (list.Count == 0) return;
        var panel = new StackPanel();
        _modulesBox.Children.Add(new GroupBox
        {
            Header = new TextBlock { Text = "Modules", FontWeight = FontWeights.SemiBold, FontSize = 14 },
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(6, 2, 6, 6), Content = panel,
        });
        panel.Children.Add(Label("Modules the owner turned on in the workbench (Host → Modules). Tools of a module work on this " +
            "computer only while it is on here; they ask like the built-in tools of the same class."));
        var states = _s.ModuleLoader.States().ToDictionary(x => x.Id);
        foreach (var m in list)
        {
            panel.Children.Add(new TextBlock { Text = m.Title + "  " + m.Version, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            if (states.TryGetValue(m.Id, out var st))
            {
                var check = new CheckBox { Content = "On this computer", IsChecked = st.On, Margin = new Thickness(0, 4, 0, 0) };
                string id = m.Id, warning = st.Warning, title = m.Title;
                check.Click += async (_, _) =>
                {
                    bool on = check.IsChecked == true;
                    if (on && warning.Length > 0 && MessageBox.Show(this, title + "\n\n" + warning, "CO Desktop",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        check.IsChecked = false;
                        return;
                    }
                    try { await _s.ModuleLoader.SetOnAsync(id, on, _cts.Token); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "CO Desktop"); }
                    RefreshModulesBox();
                };
                panel.Children.Add(check);
                string tools = string.Join(", ", m.Tools.Select(t => $"{t.Name} [{CO.Core.Modules.ModuleLoader.EffectiveClass(m, t)}]"));
                if (tools.Length > 0) panel.Children.Add(Label("Tools: " + tools + (st.Active ? "" : " (off here)")));
                if (st.Problem.Length > 0) panel.Children.Add(new TextBlock { Text = st.Problem, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange });
            }
            if (m.Ui.Any(u => u.Slot == "ui.settings"))
            {
                string id = m.Id;
                panel.Children.Add(Btn("Settings of " + m.Title + "…", () => OpenModuleSettingsAsync(id)));
            }
        }
    }

    // The settings page of a module is its ui.settings page in CO Web (Host → Modules), the same on every platform.
    private async Task OpenModuleSettingsAsync(string id)
    {
        if (_web.CoreWebView2 == null) throw new InvalidOperationException("The CO window is not ready.");
        await _web.CoreWebView2.ExecuteScriptAsync("window.coOpenModules && window.coOpenModules(" + JsonSerializer.Serialize(id) + ")");
        _web.Focus();
    }

    // Module list from the CO site: now and every 5 minutes; a changed tool set is published to Bridge.
    private void StartModulesLoop()
    {
        if (_modulesLoop) return;
        _modulesLoop = true;
        _s.ModuleLoader.CatalogChanged += () => _ = Task.Run(async () =>
        {
            if (!_s.Runtime.State.Paired) return;
            try { await _s.Runtime.PublishCatalogAsync(_cts.Token); }
            catch (Exception ex) { Log.Write("module tools: catalog not published: " + ex.Message); }
        });
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_s.SignedIn)
                    try { await _s.ModuleLoader.RefreshAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Log.Write("modules: " + ex.Message); }
                try { await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    private void RefreshConnectors()
    {
        var reg = _s.Runtime.Folders;
        var t = NewTable(_connectorTable, ("Name", 1), ("Read", 0), ("Create", 0), ("Edit", 0), ("Delete", 0), ("Status", 0));
        foreach (var c in _s.Connectors.All)
        {
            // Folders connected by link: one row each, under the service they belong to.
            var linked = c.LinkedFolders.ToList();
            if (c.Supported != Rights.None || c.Planned)
            {
                var cells = new List<UIElement>
                {
                    NameCell(c.RowTitle, c.RowNote,
                        c.Description + (c.Tools.Count > 0 ? "\nTools: " + string.Join(", ", c.Tools) : ""), grey: c.Planned),
                };
                foreach (var r in RightsText.Columns)
                {
                    bool applies = c.Supported.HasFlag(r);
                    cells.Add(Box(applies && c.Rights.HasFlag(r), applies && !c.Planned, on => c.Rights = on ? c.Rights | r : c.Rights & ~r,
                        applies ? null : $"{RightsText.Label(r)} does not apply here"));
                }
                cells.Add(StatusCell(c));
                AddRow(t, cells);
            }
            foreach (var f in linked)
            {
                var cells = new List<UIElement>
                {
                    NameCell(c.FolderTitle(f), $"{c.Name} · agent name {f.Id}", f.Path, rename: () => RenameFolder(f.Id)),
                };
                foreach (var r in RightsText.Columns)
                    cells.Add(Box(f.Granted.HasFlag(r), true, on => reg.SetRights(f.Id, null, on ? f.Granted | r : f.Granted & ~r)));
                cells.Add(RemoveButton(() => reg.Remove(f.Id)));
                AddRow(t, cells);
                AddRuleRows(t, f, 1, rule => RemoveButton(() => reg.RemoveRule(f.Id, rule.Path)));
            }
        }
    }

    private static UIElement StatusCell(Connector c)
    {
        var (text, detail, brush) = StatusText(c);
        return new TextBlock
        {
            Text = text, ToolTip = detail.Length > 0 ? detail : null, Foreground = brush,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 2, 4, 2),
        };
    }

    // Short word for the narrow panel; the detail (account, folder count, error) is the tooltip.
    private static (string text, string detail, Brush brush) StatusText(Connector c)
    {
        var st = c.Status;
        string detail = st.Detail + (st.CheckedUtc is { } u ? $" (checked {u.ToLocalTime():HH:mm})" : "");
        if (c.Planned) return ("planned", "comes with a next release", Brushes.Gray);
        if (c.Supported != Rights.None && !c.Rights.HasFlag(Rights.Read)) return ("○ off", "", Brushes.Gray);
        return st.Health switch
        {
            ConnectorHealth.Ready => ("● ready", detail, Brushes.DarkGreen),
            ConnectorHealth.SignInNeeded => ("● sign in", detail, Brushes.DarkOrange),
            ConnectorHealth.Unavailable => ("● error", detail, Brushes.Firebrick),
            _ => ("–", detail.Length > 0 ? detail : "not checked yet: press Check all", Brushes.Gray),
        };
    }

    // Name in bold over a grey one-line location, cut with "…" in the narrow panel; the full text is the tooltip.
    // rename: double-click asks for a new agent name.
    private UIElement NameCell(string title, string sub, string? tip, bool indent = false, bool grey = false, Action? rename = null)
    {
        var sp = new StackPanel
        {
            Margin = new Thickness(indent ? 14 : 4, 3, 4, 3), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            ToolTip = rename == null ? tip : tip + "\nDouble-click to rename",
        };
        if (rename != null)
            sp.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) rename(); };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontWeight = indent ? FontWeights.Normal : FontWeights.SemiBold, Foreground = grey ? Brushes.Gray : Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (sub.Length > 0)
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
        return sp;
    }

    // ------------------------------------------------------------------ table helpers

    private static Grid NewTable(Grid g, params (string header, double star)[] cols)
    {
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        foreach (var (_, star) in cols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = star > 0 ? new GridLength(star, GridUnitType.Star) : GridLength.Auto });
        // Star columns (location, status) wrap; the others size to content, the first one (name) never wraps.
        AddRow(g, cols.Select((c, i) => (UIElement)new TextBlock
        {
            Text = c.header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 4),
            HorizontalAlignment = c.star > 0 || i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
        }).ToList());
        return g;
    }

    private static void AddRow(Grid g, IReadOnlyList<UIElement> cells)
    {
        int row = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (row > 0)
        {
            var line = new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, Math.Max(1, g.ColumnDefinitions.Count));
            g.Children.Add(line);
        }
        for (int i = 0; i < cells.Count && i < g.ColumnDefinitions.Count; i++)
        {
            Grid.SetRow(cells[i], row);
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
    }

    private static TextBlock Cell(string text, bool bold = false, bool grey = false, string? tip = null, bool wrap = true) => new()
    {
        Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 3),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = grey ? Brushes.Gray : Brushes.Black,
        ToolTip = tip,
    };

    private CheckBox Box(bool on, bool enabled, Action<bool> changed, string? tip = null)
    {
        var b = new CheckBox
        {
            IsChecked = on, IsEnabled = enabled, ToolTip = tip,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 6, 2),
        };
        b.Click += (_, _) =>
        {
            try { changed(b.IsChecked == true); }
            catch (Exception ex) { Log.Write("rights: " + ex); MessageBox.Show(ex.Message, "CO Desktop"); }
        };
        return b;
    }

    private Button RemoveButton(Action remove) => SmallButton("✕", () =>
    {
        remove();
        return Task.CompletedTask;
    }, "Remove this row");

    private Button SmallButton(string text, Func<Task> action, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Log.Write(text + ": " + ex); MessageBox.Show(ex.Message, "CO Desktop"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    // 0.8.3: owner's switch for the managed-device profile (TZ §9.6) on a domain/Entra-joined computer.
    // Read at start, so a change applies after a restart; sending stays forbidden either way.
    private UIElement ManagedProfileBox()
    {
        var panel = new StackPanel();
        var box = new GroupBox
        {
            Header = new TextBlock { Text = "Managed-device profile", FontWeight = FontWeights.SemiBold, FontSize = 14 },
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(6, 2, 6, 6),
            Content = panel,
        };
        if (!CO.Desktop.Platform.ManagedDetector.IsDomainOrEntraJoined())
        {
            panel.Children.Add(Label("This computer is not joined to a company domain or Entra: the profile does not apply."));
            return box;
        }

        bool on = _s.Runtime.State.Managed;
        panel.Children.Add(Label("This computer is joined to a company domain or Entra. The profile is now " + (on ? "ON" : "OFF") + ". " +
            "ON: only read and write actions, every result is confirmed before it reaches the model (except connectors with Read ticked), no agent browser. " +
            "OFF: the agent browser works on the listed sites with your sign-ins, all action classes are allowed under the usual confirmations, " +
            "and results reach the model without confirmation. Sending is never done in either mode."));
        panel.Children.Add(Label("Only the owner's account, signed in on this computer, can change it."));
        var check = new CheckBox { Content = "Managed profile off on this device", IsChecked = Settings.ManagedProfileOff, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(check);
        panel.Children.Add(Btn("Apply and restart", () =>
        {
            bool off = check.IsChecked == true;
            if (off == Settings.ManagedProfileOff && off != on) return Task.CompletedTask;
            if (off && MessageBox.Show(this,
                    "Switch the managed-device profile off on this computer?\n\n" +
                    "Agents will drive the CO browser with your company sign-ins on the listed sites, and company data " +
                    "will reach the models without your confirmation. This may break your company's IT rules for managed devices.",
                    "CO Desktop", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                check.IsChecked = Settings.ManagedProfileOff;
                return Task.CompletedTask;
            }
            if (!Services.IsOwnerAccount(_s.Runtime.State, Settings))
                throw new InvalidOperationException("Only the owner's account can change the managed-device profile.");
            if (_s.Runtime.Host is { ActiveCalls: > 0 })
                throw new InvalidOperationException("An agent call is running; try again when it finishes.");
            Settings.ManagedProfileOff = off;
            Settings.Save();
            Restart($"managed profile {(off ? "off" : "on")} by the owner");
            return Task.CompletedTask;
        }));
        return box;
    }

    // Starts a new CO Desktop after this one exits (the settings are read at start).
    private void Restart(string why)
    {
        Log.Write(why + "; restarting");
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CO.Desktop.exe");
        // ping waits ~3 s so the single-instance mutex of this process is gone when the new one starts.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c ping -n 4 127.0.0.1 >nul & start \"\" \"{exe}\"") { UseShellExecute = false, CreateNoWindow = true });
        Application.Current.Shutdown();
    }

    // Subscribed once, so rebuilding the connectors tab does not stack handlers.
    private bool _agentWebHooked;

    // Site and journal settings for the 0.8.0 agent browser (own tab of the CO browser).
    // 0.9.0: "Blocked sites" (blocklist) instead of the allowlist "Agent browser sites".
    private UIElement AgentSitesBox()
    {
        if (!_agentWebHooked)
        {
            _agentWebHooked = true;
            // Refresh the connector description whenever the settings change.
            _s.AgentWeb.Changed += () => Dispatcher.BeginInvoke(() => RefreshConnectors());
        }

        var panel = new StackPanel();
        var box = new GroupBox
        {
            Header = new TextBlock { Text = "Blocked sites", FontWeight = FontWeights.SemiBold, FontSize = 14 },
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(6, 2, 6, 6),
            Content = panel,
        };

        if (_s.Runtime.State.Managed)
        {
            panel.Children.Add(Label("Disabled on managed devices by company policy."));
            return box;
        }

        panel.Children.Add(Label("Sites the agent must not open in its tabs of the CO browser (browser_navigate and the rest; pages, frames and requests to them). One domain per line, e.g. example.com; subdomains are included. All other public sites are allowed. Sending is never done; purchases and deletions always ask you first; form submissions, choices in lists and uploads ask unless you untick the second box below; other clicks run without asking unless you tick the first box below. Disabled on managed devices."));
        panel.Children.Add(Label("Local and private network addresses are always blocked."));

        var text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join("\n", _s.AgentWeb.Current.Block),
        };
        panel.Children.Add(text);

        var check = new CheckBox
        {
            Content = "Keep a journal with a screenshot of every action (%LOCALAPPDATA%\\CO\\agent-browser)",
            IsChecked = _s.AgentWeb.Current.Journal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(check);
        var confirmClicksCheck = new CheckBox
        {
            Content = "Ask before clicks with an unclear effect (off: the agent clicks ordinary buttons and links without asking)",
            IsChecked = _s.AgentWeb.Current.ConfirmClicks,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(confirmClicksCheck);
        var confirmFormsCheck = new CheckBox
        {
            Content = "Ask before form submits, choices in lists and file uploads (purchase and delete always ask; sending is never allowed)",
            IsChecked = _s.AgentWeb.Current.ConfirmForms,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(confirmFormsCheck);
        // co-resident 0.5.3 (CO.Core): off by default on Windows.
        var otpCheck = new CheckBox
        {
            Content = "Agent may type one-time codes from chat (a login or verification code you send in chat; passwords, card and bank fields never)",
            IsChecked = _s.AgentWeb.Current.AllowOtpFromChat == true,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(otpCheck);

        var status = Label("");
        panel.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Btn("Save sites", () =>
        {
            var list = CO.Core.Browser.AgentWebSettingsStore.Normalize(text.Text.Split(new[] { '\n', '\r', ',' }));
            _s.AgentWeb.Save(new CO.Core.Browser.AgentWebSettings { Block = list, Journal = check.IsChecked == true, ConfirmClicks = confirmClicksCheck.IsChecked == true, ConfirmForms = confirmFormsCheck.IsChecked == true, AllowOtpFromChat = otpCheck.IsChecked == true });
            text.Text = string.Join("\n", list);
            status.Text = list.Count == 0 ? "Saved: no blocked sites." : $"Saved: {list.Count} blocked sites.";
            return Task.CompletedTask;
        }));
        buttons.Children.Add(Btn("Open journal folder", () =>
        {
            var dir = System.IO.Path.Combine(DesktopSettings.DataDir, "agent-browser");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            return Task.CompletedTask;
        }));
        panel.Children.Add(buttons);

        return box;
    }

    // ------------------------------------------------------------------ CO browser

    private readonly TextBlock _browserStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private UIElement BrowserTab()
    {
        var p = Panel();
        p.Children.Add(H("CO browser"));
        p.Children.Add(Label(
            "A separate Microsoft Edge window with its own profile. Sign in there to your sites once; " +
            "the agent then reads them with your session (tools browser_open, browser_read and the tools of connectors). " +
            "Your usual Edge and Chrome stay untouched."));
        var url = new TextBox { Text = "https://" };
        p.Children.Add(Label("Address (for example a folder link)"));
        p.Children.Add(url);
        p.Children.Add(Btn("Open CO browser", async () =>
        {
            string u = url.Text.Trim();
            await _s.Browser.OpenWindowAsync(u.Length > "https://".Length ? u : null, _cts.Token);
            _browserStatus.Text = "Open: " + await _s.Browser.CurrentUrlAsync(_cts.Token);
        }));
        p.Children.Add(Btn("Show current page address", async () =>
        {
            _browserStatus.Text = "Open: " + await _s.Browser.CurrentUrlAsync(_cts.Token);
        }));
        p.Children.Add(_browserStatus);
        p.Children.Add(Label(
            "Agent tools start this window themselves when it is closed. Pages and files read from sites are " +
            "untrusted content: the Run is marked Tainted and later writes ask for confirmation."));
        if (CO.Core.Browser.EdgeBrowser.FindEdge() == null)
            _browserStatus.Text = "Microsoft Edge is not installed; the CO browser needs it.";
        return p;
    }

    // ------------------------------------------------------------------ grants

    private UIElement GrantsTab()
    {
        var p = Panel();
        p.Children.Add(H("Permissions (Grant)"));
        p.Children.Add(_grants);
        p.Children.Add(Btn("Revoke selected", () =>
        {
            if (_grants.SelectedItem is ListBoxItem { Tag: string gid }) { _s.Runtime.Grants.Revoke(gid); RefreshGrants(); }
            return Task.CompletedTask;
        }));
        return p;
    }

    private void RefreshGrants()
    {
        _grants.Items.Clear();
        foreach (Grant g in _s.Runtime.Grants.All)
            _grants.Items.Add(new ListBoxItem
            {
                Tag = g.Id,
                Content = $"{g.Class} {g.Kind} {g.Duration}{(g.TaskId != null ? " task " + g.TaskId : "")}\n    {g.Value}",
            });
    }

    // ------------------------------------------------------------------ activity

    private UIElement ActivityTab()
    {
        var p = Panel();
        p.Children.Add(H("Tool calls"));
        _activityList.ItemsSource = _activity;
        p.Children.Add(_activityList);
        p.Children.Add(Btn("Open audit log folder", () =>
        {
            InstalledAppAuth.OpenInBrowser(Path.Combine(DesktopSettings.DataDir, "audit"));
            return Task.CompletedTask;
        }));
        return p;
    }

    // ------------------------------------------------------------------ owner

    private UIElement OwnerTab()
    {
        var p = Panel();
        p.Children.Add(H("Owner: pair and revoke devices"));
        p.Children.Add(Label("Uses this computer's owner sign-in. Members' computers appear here after they sign in."));
        p.Children.Add(Btn("Refresh", RefreshOwnerAsync));
        p.Children.Add(Label("Pending requests"));
        p.Children.Add(_pending);
        var code = new TextBox();
        p.Children.Add(Label("Code shown on the device"));
        p.Children.Add(code);
        p.Children.Add(Btn("Pair selected device", async () =>
        {
            if (_pending.SelectedItem is not ListBoxItem { Tag: OwnerConsole.Pending pend }) return;
            var (console, admin) = await OwnerOpsAsync();
            string bridgeKey = await console.BridgePublicKeyAsync(_cts.Token);
            if (!PairingAdmin.CodesMatch(pend.Request, bridgeKey, code.Text.Trim()))
                throw new InvalidOperationException("The code does not match. Do not pair this device.");
            DeviceRecord rec = await admin.ApproveAsync(pend.Request, _s.Runtime.State.Account, _cts.Token);
            await console.WriteDeviceRecordAsync(rec, _cts.Token);
            MessageBox.Show($"Paired {pend.Request.Name}. Bridge creates its channels on the next scan.", "CO Desktop");
            await RefreshOwnerAsync();
        }));
        p.Children.Add(Label("Paired devices"));
        p.Children.Add(_devices);
        p.Children.Add(Btn("Revoke selected device", async () =>
        {
            if (_devices.SelectedItem is not ListBoxItem { Tag: DeviceRecord d }) return;
            if (MessageBox.Show($"Revoke {d.Request.Name}? Its service account is deleted.", "CO Desktop", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var (console, admin) = await OwnerOpsAsync();
            await admin.RevokeAsync(d, _cts.Token);
            await console.WriteDeviceRecordAsync(d, _cts.Token);
            await RefreshOwnerAsync();
        }));
        return p;
    }

    private async Task RefreshOwnerAsync()
    {
        var (owner, _) = await OwnerOpsAsync();
        _pending.Items.Clear();
        foreach (var pend in await owner.ListPendingAsync(_cts.Token))
            _pending.Items.Add(new ListBoxItem { Tag = pend, Content = $"{pend.Request.Name} ({pend.Request.Account}, {pend.Request.EnvironmentId})\n    {pend.Request.DeviceId}" });
        _devices.Items.Clear();
        foreach (var d in await owner.ListDevicesAsync(_cts.Token))
            _devices.Items.Add(new ListBoxItem { Tag = d, Content = $"{d.Request.Name} {(d.Revoked ? "REVOKED" : "")}\n    {d.Request.DeviceId} · {d.Request.Account}" });
    }

    // ------------------------------------------------------------------ helpers

    private static StackPanel Panel() => new() { Margin = new Thickness(10) };
    private static ScrollViewer Scroll(UIElement e) => new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static TextBlock H(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Label(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };

    private Button Btn(string text, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3, 6, 3), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Log.Write(text + ": " + ex); MessageBox.Show(ex.Message, "CO Desktop"); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    private string? Prompt(string question)
    {
        var box = new TextBox { Margin = new Thickness(0, 6, 0, 6) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var w = new Window { Title = "CO Desktop", Width = 360, Height = 150, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock { Text = question });
        sp.Children.Add(box);
        sp.Children.Add(ok);
        w.Content = sp;
        ok.Click += (_, _) => w.DialogResult = true;
        return w.ShowDialog() == true ? box.Text : null;
    }
}
