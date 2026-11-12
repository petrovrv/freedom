// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CO.Desktop;

// 0.9.2 (canvas M6, SPEC.md of chat CHAT-20261028152233-8P34 §3.6): "Open in canvas" in CO Web opens
// /canvas?path=… in its own window instead of a tab. The page is the same as in a browser: it gets a ticket from the
// site (window.coDesktop.getAccessToken, or the cookie session) and moves on to the editor of canvas-bridge on the
// home PC (<ts.net>/cb/edit). One window per file; position and size are remembered in desktop.json (CanvasWindow).
// Links and Share ▾ (a Gmail draft needs the site's cookie session with gmail.compose) open in the system browser.
public sealed class CanvasWindow : Window
{
    private static readonly Dictionary<string, CanvasWindow> Windows = new(StringComparer.Ordinal);

    private readonly MainWindow _main;
    private readonly CoreWebView2Environment _env;
    private readonly Uri _start;
    private readonly string _key;
    private readonly WebView2 _web = new();
    private bool _closing;   // close asked: the page is being unloaded first (the plugin sends unsent threads)
    private bool _unloaded;  // the page is gone, the window may close

    public static void Open(MainWindow main, CoreWebView2Environment env, Uri url)
    {
        string path = System.Web.HttpUtility.ParseQueryString(url.Query).Get("path") ?? "";
        if (Windows.TryGetValue(path, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var w = new CanvasWindow(main, env, url, path);
        Windows[path] = w;
        w.Show();
    }

    private CanvasWindow(MainWindow main, CoreWebView2Environment env, Uri url, string path)
    {
        _main = main;
        _env = env;
        _start = url;
        _key = path;
        string name = path.Split('/').LastOrDefault(p => p.Length > 0) ?? "Canvas";
        Title = name + " · CO canvas";
        Icon = LoadIcon();
        Content = _web;
        PlaceFromSettings();
        Loaded += async (_, _) => await InitAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Windows.Remove(_key);
            _web.Dispose();
        };
    }

    private static BitmapFrame LoadIcon()
    {
        using var s = typeof(CanvasWindow).Assembly.GetManifestResourceStream("CO.Desktop.co-canvas.ico")
                      ?? throw new InvalidOperationException("Embedded canvas icon missing.");
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    // ------------------------------------------------------------------ size and position

    private void PlaceFromSettings()
    {
        var b = _main.Settings.CanvasWindow;
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
        _main.Settings.CanvasWindow = new DesktopSettings.WindowBounds
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
            core.WebMessageReceived += async (_, e) => await _main.OnCanvasMessageAsync(core, e);
            core.DocumentTitleChanged += (_, _) =>
            {
                string t = core.DocumentTitle;
                if (string.IsNullOrWhiteSpace(t) || t == "about:blank" || t.Contains("://", StringComparison.Ordinal)) return;
                Title = t.Contains("CO", StringComparison.Ordinal) ? t : t + " · CO canvas";
            };
            core.NavigationStarting += (_, e) =>
            {
                if (_closing && e.Uri == "about:blank") return;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) { e.Cancel = true; return; }
                // the CO site, Google sign-in, the editor page of canvas-bridge (<ts.net>/cb/edit: CO Web moves there
                // itself, "Try anyway" by a click) and redirects stay here; a link the user clicks elsewhere opens in
                // the system browser
                bool allowed = u.GetLeftPart(UriPartial.Authority) == origin || MainWindow.GoogleSignInHost.IsMatch(u.Host)
                               || (u.Scheme == "https" && u.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                                   && u.AbsolutePath == "/cb/edit")
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
            core.WindowCloseRequested += (_, _) => Close();   // Close button of the editor page (window.close)
            _web.Source = _start;
        }
        catch (Exception ex)
        {
            Log.Write("canvas window: " + ex);
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "The canvas could not start: " + ex.Message, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    // Share ▾ opens an empty tab inside the click and points it at the draft page later (edit.js); here that tab is a
    // hidden web view whose first real address goes to the system browser. Other new windows (links in the document)
    // open in the system browser at once.
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
            pc.WindowCloseRequested += (_, _) => Finish();   // the share failed: edit.js closes its tab
            e.NewWindow = pc;
            e.Handled = true;
            _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => Dispatcher.BeginInvoke(Finish));   // never left behind
        }
        catch (Exception ex)
        {
            Log.Write("canvas popup: " + ex.Message);
            e.Handled = true;   // no tab: edit.js then opens the draft address with window.open, which goes out above
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

    // The page is unloaded before the window goes, so the CO Agent plugin sends unsent threads (sendBeacon on pagehide)
    // and ONLYOFFICE saves on its own close; a second click of × closes at once.
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
