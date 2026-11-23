// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CO.Desktop;

// Freedom F10 (ext/1 ui.window, modules/EXT1.md): a ui.page that a module declares with "window": true opens in its own
// window when CO Web opens it in a new tab (the generic form of the 0.9.2 window of one module's editor). The page is
// the same as in a browser; window.coDesktop answers only getAccessToken and status there. The page may move the window
// to the https origins of the module's navigate list (e.g. an editor on the home PC) and to Google sign-in; other links
// and new tabs open in the system browser. One window per value of windowKey (else one per app); position and size
// are remembered per page in desktop.json (PageWindows).
// F14e.2 (SPEC §3.6a п. 9): every app window is an app of its own for Windows - own taskbar button with the app's icon
// (AppUserModelID per app, WindowAppId), Alt+Tab, Snap, any monitor; a pinned button starts it again (--app).
public sealed class ModulePageWindow : Window
{
    private static readonly Dictionary<string, ModulePageWindow> Windows = new(StringComparer.Ordinal);

    private readonly MainWindow _main;
    private readonly CoreWebView2Environment _env;
    private readonly Uri _start;
    private readonly string _key;
    private readonly string _page;                // "<module>/<page>": key of the remembered bounds
    private readonly HashSet<string> _origins;    // https origins of the module's navigate list
    private readonly string _appId, _relaunch, _label, _iconPath;
    private string _query;                        // the query the window was opened with (without window=1)
    private readonly WebView2 _web = new();
    private bool _closing;   // close asked: the page is being unloaded first (it may still send what it holds)
    private bool _unloaded;  // the page is gone, the window may close

    public static void Open(MainWindow main, CoreWebView2Environment env, Uri url, CO.Core.Modules.ModuleEntry m, CO.Core.Modules.ModuleUiEntry ui)
    {
        // F14e.2: one window per app; a page with windowKey has one window per value of that parameter (a file)
        string value = ui.WindowKey.Length > 0 ? System.Web.HttpUtility.ParseQueryString(url.Query).Get(ui.WindowKey) ?? "" : "";
        string key = m.Id + "/" + ui.Id + " " + value;
        if (Windows.TryGetValue(key, out var open))
        {
            // the app asked again with other parameters (a link to one letter): the open window goes there
            string q = PageQuery(url);
            if (q.Length > 0 && q != open._query && open._web.CoreWebView2 != null && !open._closing)
            {
                open._query = q;
                open._web.CoreWebView2.Navigate(url.AbsoluteUri);
            }
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var w = new ModulePageWindow(main, env, url, key, m, ui, value);
        Windows[key] = w;
        w.Show();
    }

    private ModulePageWindow(MainWindow main, CoreWebView2Environment env, Uri url, string key, CO.Core.Modules.ModuleEntry m,
        CO.Core.Modules.ModuleUiEntry ui, string value)
    {
        _main = main;
        _env = env;
        _start = url;
        _key = key;
        _page = m.Id + "/" + ui.Id;
        _origins = m.Navigate.Where(n => n.StartsWith("https://", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // a file-like window key names the window by its last part until the page sets its own title
        string name = ui.WindowKey.Length > 0 ? value.Split('/').LastOrDefault(p => p.Length > 0) ?? "" : "";
        Title = (name.Length > 0 ? name + " · " : "") + (ui.Title.Length > 0 ? ui.Title : m.Title) + " · CO";
        _query = PageQuery(url);
        _label = ui.Label.Length > 0 ? ui.Label : ui.Title.Length > 0 ? ui.Title : m.Title;
        _appId = Platform.WindowAppId.For(m.Id, ui.Id);
        _relaunch = "\"" + (Environment.ProcessPath ?? "CO.Desktop.exe") + "\" --app " + m.Id + "/" + ui.Id;
        _iconPath = "";
        try
        {
            // the app's own icon (its glyph of the module list); the page icon of CO when that fails
            var app = CO.Core.Modules.ModuleList.Apps(new[] { m }).FirstOrDefault(a => a.Page.Id == ui.Id);
            var (path, image) = AppIcons.For(m.Id + "/" + ui.Id, app?.Icon ?? (ui.Icon.Length > 0 ? ui.Icon : (_label.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(_label).ToUpperInvariant() : "")));
            _iconPath = path;
            Icon = image;
        }
        catch (Exception ex) { Log.Write("page window icon: " + ex.Message); Icon = LoadIcon(); }
        SourceInitialized += (_, _) => Platform.WindowAppId.Set(new WindowInteropHelper(this).Handle, _appId, _relaunch, _label, _iconPath);
        Content = _web;
        PlaceFromSettings();
        Loaded += async (_, _) => await InitAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Platform.WindowAppId.Clear(new WindowInteropHelper(this).Handle);
            Windows.Remove(_key);
            _web.Dispose();
        };
    }

    // the parameters of the page address, without the window=1 of a desktop window
    private static string PageQuery(Uri url)
    {
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        q.Remove("window");
        return q.Count == 0 ? "" : q.ToString() ?? "";
    }

    private static BitmapFrame LoadIcon()
    {
        using var s = typeof(ModulePageWindow).Assembly.GetManifestResourceStream("CO.Desktop.co-page.ico")
                      ?? throw new InvalidOperationException("Embedded page window icon missing.");
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    // ------------------------------------------------------------------ size and position

    private void PlaceFromSettings()
    {
        var b = _main.Settings.PageWindows.GetValueOrDefault(_page);
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        bool fits = b != null && b.Width >= 400 && b.Height >= 300
                    && b.Left + 100 <= vl + vw && b.Left + b.Width - 100 >= vl   // at least 100 DIP on a screen
                    && b.Top >= vt - 10 && b.Top + 40 <= vt + vh;              // title bar reachable
        if (!fits)
        {
            Width = Math.Min(1280, SystemParameters.WorkArea.Width * 0.9);
            Height = Math.Min(900, SystemParameters.WorkArea.Height * 0.9);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        // a second window open at the same time is shifted, so it does not cover the first one exactly
        double shift = 28 * Windows.Count;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b!.Left + shift;
        Top = b.Top + shift;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || r.Width < 1) return;
        _main.Settings.PageWindows[_page] = new DesktopSettings.WindowBounds
        {
            Left = Math.Round(r.Left), Top = Math.Round(r.Top), Width = Math.Round(r.Width), Height = Math.Round(r.Height),
            Maximized = WindowState == WindowState.Maximized,
        };
        _main.SaveSettingsSafe();
    }

    // ------------------------------------------------------------------ web view

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(_env);
            var core = _web.CoreWebView2;
            string origin = _main.WebOrigin;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(_main.DesktopShimScript(origin));
            core.WebMessageReceived += async (_, e) => await _main.OnPageWindowMessageAsync(core, e);
            core.DocumentTitleChanged += (_, _) =>
            {
                string t = core.DocumentTitle;
                if (string.IsNullOrWhiteSpace(t) || t == "about:blank" || t.Contains("://", StringComparison.Ordinal)) return;
                Title = t.Contains("CO", StringComparison.Ordinal) ? t : t + " · CO";
            };
            core.NavigationStarting += (_, e) =>
            {
                if (_closing && e.Uri == "about:blank") return;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) { e.Cancel = true; return; }
                // the CO site, Google sign-in, the origins of the module's navigate list (where its page moves the
                // window, e.g. an editor on the home PC) and redirects stay here; a link the user clicks elsewhere opens
                // in the system browser
                bool allowed = u.GetLeftPart(UriPartial.Authority) == origin || MainWindow.GoogleSignInHost.IsMatch(u.Host)
                               || (u.Scheme == "https" && _origins.Contains(u.GetLeftPart(UriPartial.Authority)))
                               || (!e.IsUserInitiated && u.Scheme == "https");
                if (!allowed)
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };
            core.NavigationCompleted += (_, _) =>
            {
                if (!_closing) return;
                if (core.Source == "about:blank") { _unloaded = true; Close(); }
                else _closing = false;   // the page asked to stay (beforeunload): the window stays too
            };
            core.NewWindowRequested += OnNewWindowRequested;
            core.WindowCloseRequested += (_, _) => Close();   // a Close button of the page (window.close)
            _web.Source = _start;
        }
        catch (Exception ex)
        {
            Log.Write("page window " + _page + ": " + ex);
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "The page could not start: " + ex.Message, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    // A page may open an empty tab inside a click and point it at an address later (popup blockers allow only that);
    // here that tab is a hidden web view whose first real address goes to the system browser. Other new windows (links
    // in the page) open in the system browser at once.
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (e.Uri.Length > 0 && e.Uri != "about:blank")
        {
            e.Handled = true;
            OpenExternal(e.Uri);
            return;
        }
        var deferral = e.GetDeferral();
        CoreWebView2Controller? popup = null;
        try
        {
            popup = await _env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(this).Handle);
            popup.IsVisible = false;
            var pc = popup.CoreWebView2;
            bool done = false;
            void Finish()
            {
                if (done) return;
                done = true;
                Dispatcher.BeginInvoke(() => { try { popup.Close(); } catch (Exception) { /* already gone */ } });
            }
            pc.NavigationStarting += (_, n) =>
            {
                if (n.Uri == "about:blank") return;
                n.Cancel = true;
                OpenExternal(n.Uri);
                Finish();
            };
            pc.NewWindowRequested += (_, n) => { n.Handled = true; OpenExternal(n.Uri); };
            pc.WindowCloseRequested += (_, _) => Finish();   // the page gave up and closed its tab
            e.NewWindow = pc;
            e.Handled = true;
            _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => Dispatcher.BeginInvoke(Finish));   // never left behind
        }
        catch (Exception ex)
        {
            Log.Write("page window popup: " + ex.Message);
            e.Handled = true;   // no tab: the page then opens the address with window.open, which goes out above
            try { popup?.Close(); } catch (Exception) { /* ignore */ }
        }
        finally { deferral.Complete(); }
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http" || u.Scheme == "mailto"))
            CO.Core.Auth.InstalledAppAuth.OpenInBrowser(uri);
    }

    // ------------------------------------------------------------------ closing

    // The page is unloaded before the window goes, so it can finish its work (sendBeacon on pagehide, an editor that
    // saves on its own close); a second click of × closes at once.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var core = _web.CoreWebView2;
        if (_unloaded || _closing || core == null || core.Source == "about:blank"
            || Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted)
        {
            SaveBounds();
            return;
        }
        e.Cancel = true;
        _closing = true;
        core.Navigate("about:blank");
    }
}
