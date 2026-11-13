// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CO.Core;
using CO.Core.Runtime;
using CO.Core.Tools;
using static CO.Mac.ObjC;

namespace CO.Mac;

// R4: the CO window for the Mac — a menu-bar icon (status, agent commands on/off, agent browser mode, ~/CO, log)
// and CO Web in a WKWebView window, like the CO Desktop window on Windows. A separate process ("co-resident app",
// started at login by its own LaunchAgent from ~/Applications/CO.app), so a problem in the window never stops the
// device. window.coDesktop gives CO Web this Mac's device (status) so messages sent here use its tools; sign-in to
// CO Web is the browser session (/auth/login.php), as for every non-owner CO Desktop.
public static unsafe class MacApp
{
    private static string Origin => InstallDefaults.SiteUrl; // Freedom F1: the CO site of this install
    private static readonly Regex GoogleSignInHost = new(
        @"^([a-z0-9-]+\.)*(google\.[a-z]{2,3}(\.[a-z]{2})?|gstatic\.com|googleusercontent\.com|youtube\.com)$", RegexOptions.IgnoreCase);

    private const nuint CommandKey = 1 << 20, ShiftKey = 1 << 17;
    private const nint PolicyRegular = 0, PolicyAccessory = 1;

    private static IntPtr _app, _del, _statusItem, _statusLine, _shellItem, _formsItem, _mainWin;
    private static readonly IntPtr[] _browserItems = new IntPtr[3];
    private static readonly List<IntPtr> _popups = new();
    private static readonly Dictionary<IntPtr, string> _downloads = new();
    private static volatile Snapshot _snap = new(false, "", "", "", "", false, false, false, true, "on");
    private static int _refreshing;

    private sealed record Snapshot(bool SetUp, string Env, string DeviceId, string Name, string Short, bool Paired,
        bool Revoked, bool Running, bool Shell, string Browser, bool ConfirmForms = true);

    private static string ShowFile => Path.Combine(Paths.DataDir, "app.show");
    private static string StartsFile => Path.Combine(Paths.DataDir, "app.starts");

    // fromLaunchd: started at login; stops itself at the 5th start in 10 minutes (a crash loop). show: opened from Finder.
    public static int Run(bool fromLaunchd, bool show)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The CO window runs on macOS only.");
        Directory.CreateDirectory(Paths.DataDir);
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(Paths.DataDir, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            // Already running: the running copy opens its window when it sees app.show (checked every 2 s).
            if (show) File.WriteAllText(ShowFile, DateTime.UtcNow.ToString("O"));
            return 0;
        }
        if (fromLaunchd && TooManyStarts()) { Log.Write("window app: 5 starts in 10 minutes, staying off until the next login"); return 0; }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("window app crash: " + e.ExceptionObject);
        Log.Write($"window app {Program.Version} started, pid {Environment.ProcessId}");

        LoadFrameworks();
        Send(New("NSAutoreleasePool"), "self");
        _app = Send(Cls("NSApplication"), "sharedApplication");
        SendN(_app, "setActivationPolicy:", PolicyAccessory);
        _del = Send(Send(DefineDelegate(), "alloc"), "init");
        Send(_app, "setDelegate:", _del);
        BuildMainMenu();
        BuildStatusItem();
        _snap = Compute();
        RefreshMenu();
        Timer(2.0, _del, "tick:");
        if (show) ShowMain();
        Send(_app, "run");
        GC.KeepAlive(lockFile);
        return 0;
    }

    private static bool TooManyStarts()
    {
        var now = DateTime.UtcNow;
        var starts = (File.Exists(StartsFile) ? File.ReadAllLines(StartsFile) : Array.Empty<string>())
            .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
            .Where(t => now - t < TimeSpan.FromMinutes(10)).Append(now).ToList();
        File.WriteAllLines(StartsFile, starts.Select(t => t.ToString("O")));
        return starts.Count > 4;
    }

    // ------------------------------------------------------------------ status

    private static Snapshot Compute()
    {
        DeviceState? st = null;
        try
        {
            string f = Path.Combine(Paths.DataDir, "device.json");
            if (File.Exists(f)) st = JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(f));
        }
        catch (Exception ex) { Log.Write("window app: device.json: " + ex.Message); }
        var (code, so, _) = Proc.Run("/bin/launchctl", new[] { "print", $"gui/{Program.Uid()}/{Paths.Label}" }, timeoutMs: 10_000);
        return new Snapshot(st != null && st.Account.Length > 0, st?.EnvironmentId ?? "", st?.DeviceId ?? "", st?.Name ?? "", st?.Short ?? "",
            st?.Paired == true, st?.Bridge?.State == "revoked", code == 0 && so.Contains("state = running"),
            new ShellSettings(Path.Combine(Paths.DataDir, "shell.json")).Enabled,
            new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")).Mode, MacBrowser.ConfirmForms);
    }

    private static string StatusText(Snapshot s) =>
        !s.SetUp ? "Not set up: run the install command from CO"
        : s.Revoked ? "Disconnected by the owner"
        : !s.Running ? "The CO helper is not running"
        : !s.Paired ? "Waiting to be connected to AI Workbench"
        : $"Connected · {s.Env} · dev_{s.Short}";

    private static bool Connected(Snapshot s) => s.SetUp && s.Paired && s.Running && !s.Revoked;

    private static void RefreshMenu()
    {
        var s = _snap;
        Send(_statusLine, "setTitle:", Str(StatusText(s)));
        SendN(_shellItem, "setState:", s.Shell ? 1 : 0);
        SendN(_formsItem, "setState:", s.ConfirmForms ? 1 : 0);
        string[] modes = { "on", "read", "off" };
        for (int i = 0; i < 3; i++) SendN(_browserItems[i], "setState:", s.Browser == modes[i] ? 1 : 0);
        IntPtr button = Send(_statusItem, "button");
        IntPtr img = Send(Cls("NSImage"), "imageWithSystemSymbolName:accessibilityDescription:",
            Str(Connected(s) ? "bubble.left.and.bubble.right" : "exclamationmark.bubble"), Str("CO"));
        if (img != IntPtr.Zero)
        {
            SendBool(img, "setTemplate:", true);
            Send(button, "setImage:", img);
            Send(button, "setTitle:", Str(""));
        }
        else Send(button, "setTitle:", Str(Connected(s) ? "CO" : "CO !"));
        Send(button, "setToolTip:", Str("CO: " + StatusText(s)));
    }

    // ------------------------------------------------------------------ menus

    private static IntPtr Item(string title, string? action, string key = "", IntPtr target = default, nuint mods = 0)
    {
        IntPtr item = Send(Send(Cls("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:",
            Str(title), action == null ? IntPtr.Zero : Sel(action), Str(key));
        if (target != IntPtr.Zero) Send(item, "setTarget:", target);
        if (mods != 0) SendN(item, "setKeyEquivalentModifierMask:", (nint)mods);
        return Send(item, "autorelease");
    }

    private static IntPtr Menu(string title, params IntPtr[] items)
    {
        IntPtr menu = Send(Send(Send(Cls("NSMenu"), "alloc"), "initWithTitle:", Str(title)), "autorelease");
        foreach (var i in items) Send(menu, "addItem:", i);
        return menu;
    }

    private static IntPtr Separator() => Send(Cls("NSMenuItem"), "separatorItem");

    private static IntPtr Sub(string title, IntPtr menu)
    {
        IntPtr top = Item(title, null);
        Send(top, "setSubmenu:", menu);
        return top;
    }

    // Shortcuts of the window (copy/paste reach the web page only through an Edit menu); shown while the window is open.
    private static void BuildMainMenu()
    {
        IntPtr main = Menu("",
            Sub("CO", Menu("CO",
                Item("Hide CO", "hide:", "h"),
                Separator(),
                Item("Close CO window", "performClose:", "q"))),
            Sub("Edit", Menu("Edit",
                Item("Undo", "undo:", "z"),
                Item("Redo", "redo:", "z", default, CommandKey | ShiftKey),
                Separator(),
                Item("Cut", "cut:", "x"),
                Item("Copy", "copy:", "c"),
                Item("Paste", "paste:", "v"),
                Item("Select All", "selectAll:", "a"))),
            Sub("View", Menu("View",
                Item("Reload", "reload:", "r"),
                Item("Back", "goBack:", "["),
                Item("Forward", "goForward:", "]"))),
            Sub("Window", Menu("Window",
                Item("Minimize", "performMiniaturize:", "m"),
                Item("Close", "performClose:", "w"))));
        Send(_app, "setMainMenu:", main);
    }

    private static void BuildStatusItem()
    {
        _statusItem = SendD(Send(Cls("NSStatusBar"), "systemStatusBar"), "statusItemWithLength:", -1.0);   // NSVariableStatusItemLength
        Send(_statusItem, "retain");
        _statusLine = Item("…", null);
        _shellItem = Item("Agents may run commands (asks every time)", "toggleShell:", "", _del);
        _formsItem = Item("Ask before sending forms", "toggleForms:", "", _del);
        string[] titles = { "On", "Read pages only", "Off" };
        for (int i = 0; i < 3; i++)
        {
            _browserItems[i] = Item(titles[i], "browserMode:", "", _del);
            SendN(_browserItems[i], "setTag:", i);
        }
        IntPtr menu = Menu("CO",
            Item("Open CO", "openWindow:", "o", _del),
            Separator(),
            _statusLine,
            _shellItem,
            Sub("Agent browser", Menu("Agent browser", _browserItems.Append(Separator()).Append(_formsItem).ToArray())),
            Item("Open the CO folder", "openFolder:", "", _del),
            Item("Show the log", "openLog:", "", _del),
            Separator(),
            Item($"CO for Mac {Program.Version}", null),
            Item("Quit the menu (the helper keeps running)", "quitMenu:", "", _del));
        Send(_statusItem, "setMenu:", menu);
    }

    // ------------------------------------------------------------------ windows

    private static string ShimScript()
    {
        var info = new { app = "co-mac", version = Program.Version, deviceId = _snap.DeviceId, deviceShort = _snap.Short, managed = false };
        return "if (location.origin === " + JsonSerializer.Serialize(Origin) + ") { (() => {" +
            " const pending = new Map(); let n = 0;" +
            " Object.defineProperty(window, '__coReply', { value: (m) => { const p = pending.get(m.id); if (!p) return; pending.delete(m.id); m.error ? p.reject(new Error(m.error)) : p.resolve(m.result); } });" +
            " const call = (type, args) => new Promise((resolve, reject) => { const id = ++n; pending.set(id, { resolve, reject }); window.webkit.messageHandlers.co.postMessage(JSON.stringify(args === undefined ? { type, id } : { type, id, args })); });" +
            " window.coDesktop = Object.freeze(Object.assign(" + JsonSerializer.Serialize(info) + ", { status: () => call('status')" +
            // Freedom F10 (ext/1 device.tools): device ops of module settings pages and the per-device switch, like CO Desktop
            ", moduleOp: (module, op, args) => call('moduleOp', { module, op, args })" +
            ", moduleDevice: () => call('moduleDevice'), moduleDeviceSet: (module, on) => call('moduleDeviceSet', { module, on: on === true }) }));" +
            " })(); }";
    }

    private static IntPtr MakeConfig()
    {
        IntPtr cfg = Send(New("WKWebViewConfiguration"), "autorelease");
        IntPtr ucc = Send(cfg, "userContentController");
        IntPtr script = InitUserScript(Send(Cls("WKUserScript"), "alloc"), Str(ShimScript()), 0, true);   // at document start
        Send(ucc, "addUserScript:", script);
        Send(script, "release");
        Send(ucc, "addScriptMessageHandler:name:", _del, Str("co"));
        // Google refuses sign-in in web views that do not look like Safari.
        Send(cfg, "setApplicationNameForUserAgent:", Str("Version/26.0 Safari/605.1.15"));
        SendBool(Send(cfg, "preferences"), "setJavaScriptCanOpenWindowsAutomatically:", true);
        return cfg;
    }

    private static (IntPtr win, IntPtr web) MakeWindow(IntPtr cfg, double w, double h)
    {
        var rect = new CGRect(0, 0, w, h);
        IntPtr win = InitWindow(Send(Cls("NSWindow"), "alloc"), rect, 1 | 2 | 4 | 8);   // titled, closable, miniaturizable, resizable
        SendBool(win, "setReleasedWhenClosed:", false);
        IntPtr web = SendRect(Send(Cls("WKWebView"), "alloc"), "initWithFrame:configuration:", rect, cfg);
        Send(web, "setNavigationDelegate:", _del);
        Send(web, "setUIDelegate:", _del);
        SendBool(web, "setAllowsBackForwardNavigationGestures:", true);
        if (RespondsTo(web, "setInspectable:")) SendBool(web, "setInspectable:", true);
        Send(win, "setContentView:", web);
        Send(web, "release");   // the window keeps it
        Send(win, "setTitle:", Str("CO"));
        Send(win, "setDelegate:", _del);
        Send(win, "center");
        return (win, web);
    }

    private static void Front(IntPtr win)
    {
        SendN(_app, "setActivationPolicy:", PolicyRegular);   // Dock icon and Cmd-Tab while a window is open
        Send(win, "makeKeyAndOrderFront:", IntPtr.Zero);
        Send(win, "orderFrontRegardless");   // also when opened from the timer (app.show), without a user event
        if (RespondsTo(_app, "activate")) Send(_app, "activate");   // macOS 14+
        else SendBool(_app, "activateIgnoringOtherApps:", true);
    }

    private static void ShowMain()
    {
        if (_mainWin == IntPtr.Zero)
        {
            IntPtr web;
            (_mainWin, web) = MakeWindow(MakeConfig(), 1200, 820);
            Send(_mainWin, "setFrameAutosaveName:", Str("CO main window"));
            Send(web, "loadRequest:", Send(Cls("NSURLRequest"), "requestWithURL:", Url(Origin + "/")));
        }
        Front(_mainWin);
    }

    private static bool IsPopup(IntPtr web) => web != IntPtr.Zero && Send(web, "window") != _mainWin;

    private static void ClosePopupLater(IntPtr web)
    {
        IntPtr win = Send(web, "window");
        if (win != IntPtr.Zero && win != _mainWin) Later(win, "close");
    }

    private static string? UrlOf(IntPtr nsurl) => nsurl == IntPtr.Zero ? null : FromStr(Send(nsurl, "absoluteString"));

    private static bool SameOrigin(Uri u) => u.GetLeftPart(UriPartial.Authority) == Origin;

    private static void OpenOutside(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" or "mailto") Dialog.Open(url);
    }

    // ------------------------------------------------------------------ delegate (app, menus, window, WebKit)

    private static IntPtr DefineDelegate() => DefineClass("COWindowAppDelegate",
        new[] { "NSApplicationDelegate", "NSWindowDelegate", "WKNavigationDelegate", "WKUIDelegate", "WKScriptMessageHandler", "WKDownloadDelegate" },
        ("tick:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnTick, "v@:@"),
        ("openWindow:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenWindow, "v@:@"),
        ("toggleShell:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnToggleShell, "v@:@"),
        ("browserMode:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnBrowserMode, "v@:@"),
        ("toggleForms:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnToggleForms, "v@:@"),
        ("openFolder:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenFolder, "v@:@"),
        ("openLog:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenLog, "v@:@"),
        ("quitMenu:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnQuit, "v@:@"),
        ("applicationShouldHandleReopen:hasVisibleWindows:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, byte>)&OnReopen, "c@:@c"),
        ("windowWillClose:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnWindowWillClose, "v@:@"),
        ("webView:decidePolicyForNavigationAction:decisionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnNavigationAction, "v@:@@@?"),
        ("webView:decidePolicyForNavigationResponse:decisionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnNavigationResponse, "v@:@@@?"),
        ("webView:navigationAction:didBecomeDownload:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnBecameDownload, "v@:@@@"),
        ("webView:navigationResponse:didBecomeDownload:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnBecameDownload, "v@:@@@"),
        ("webView:didFinishNavigation:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnFinished, "v@:@@"),
        ("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDownloadDestination, "v@:@@@@?"),
        ("downloadDidFinish:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnDownloadFinished, "v@:@"),
        ("download:didFailWithError:resumeData:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDownloadFailed, "v@:@@@"),
        ("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&OnCreateWebView, "@@:@@@@"),
        ("webViewDidClose:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnWebViewClose, "v@:@"),
        ("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnOpenPanel, "v@:@@@@?"),
        ("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnAlert, "v@:@@@@?"),
        ("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnConfirm, "v@:@@@@?"),
        ("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnPrompt, "v@:@@@@@?"),
        ("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, nint, IntPtr, void>)&OnMediaPermission, "v@:@@@q@?"),
        ("userContentController:didReceiveScriptMessage:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage, "v@:@@"));

    // Every callback runs on the main thread; an exception must not cross into Objective-C (it would abort the app).
    private static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Write($"window app: {what}: {ex}"); }
    }

    // Work for the main thread from a worker (module call answers); run by the next tick.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainQueue = new();
    private static void MainThread(Action a) => _mainQueue.Enqueue(a);

    [UnmanagedCallersOnly]
    private static void OnTick(IntPtr self, IntPtr sel, IntPtr timer) => Guard("tick", () =>
    {
        if (File.Exists(ShowFile)) { File.Delete(ShowFile); ShowMain(); }
        while (_mainQueue.TryDequeue(out var job)) job();   // F10: answers of module calls made on a worker
        // Status every 2 s from a worker (launchctl, file reads), applied to the menu on the next tick.
        if (Interlocked.Exchange(ref _refreshing, 1) == 0)
            _ = Task.Run(() => { try { _snap = Compute(); } catch (Exception ex) { Log.Write("window app: status: " + ex.Message); } finally { _refreshing = 0; } });
        RefreshMenu();
    });

    [UnmanagedCallersOnly]
    private static void OnOpenWindow(IntPtr self, IntPtr sel, IntPtr sender) => Guard("open", ShowMain);

    [UnmanagedCallersOnly]
    private static void OnToggleShell(IntPtr self, IntPtr sel, IntPtr sender) => Guard("shell", () =>
    {
        var s = new ShellSettings(Path.Combine(Paths.DataDir, "shell.json"));
        s.Enabled = !s.Enabled;
        Log.Write("window app: shell_run " + (s.Enabled ? "on" : "off"));
        _snap = _snap with { Shell = s.Enabled };
        RefreshMenu();
    });

    [UnmanagedCallersOnly]
    private static void OnBrowserMode(IntPtr self, IntPtr sel, IntPtr sender) => Guard("browser", () =>
    {
        string mode = SendRetN(sender, "tag") switch { 0 => "on", 1 => "read", _ => "off" };
        new BrowserSettings(Path.Combine(Paths.DataDir, "browser.json")).Mode = mode;
        Log.Write("window app: agent browser " + mode);
        _snap = _snap with { Browser = mode };
        RefreshMenu();
    });

    // 0.5.1: form submits, choices and uploads of the agent browser ask first (checked) or run without asking.
    [UnmanagedCallersOnly]
    private static void OnToggleForms(IntPtr self, IntPtr sel, IntPtr sender) => Guard("forms", () =>
    {
        bool on = !_snap.ConfirmForms;
        MacBrowser.ConfirmForms = on;
        Log.Write("window app: agent browser form confirmations " + (on ? "on" : "off"));
        _snap = _snap with { ConfirmForms = on };
        RefreshMenu();
    });

    [UnmanagedCallersOnly]
    private static void OnOpenFolder(IntPtr self, IntPtr sel, IntPtr sender) => Guard("folder", () =>
    {
        Directory.CreateDirectory(Paths.WorkFolder);
        Proc.Run("/usr/bin/open", new[] { Paths.WorkFolder });
    });

    [UnmanagedCallersOnly]
    private static void OnOpenLog(IntPtr self, IntPtr sel, IntPtr sender) =>
        Guard("log", () => Proc.Run("/usr/bin/open", new[] { "-a", "Console", Paths.LogFile }));

    [UnmanagedCallersOnly]
    private static void OnQuit(IntPtr self, IntPtr sel, IntPtr sender) => Guard("quit", () =>
    {
        Log.Write("window app: quit from the menu");
        Send(_app, "terminate:", IntPtr.Zero);   // exit 0: launchd starts it again only at the next login
    });

    [UnmanagedCallersOnly]
    private static byte OnReopen(IntPtr self, IntPtr sel, IntPtr app, byte visible)
    {
        Guard("reopen", ShowMain);   // CO.app opened again from Finder, Launchpad or Spotlight
        return 0;
    }

    [UnmanagedCallersOnly]
    private static void OnWindowWillClose(IntPtr self, IntPtr sel, IntPtr note) => Guard("close", () =>
    {
        IntPtr win = Send(note, "object");
        if (_popups.Remove(win))
        {
            IntPtr web = Send(win, "contentView");
            Send(web, "setNavigationDelegate:", IntPtr.Zero);
            Send(web, "setUIDelegate:", IntPtr.Zero);
            Send(win, "setDelegate:", IntPtr.Zero);
            Send(win, "autorelease");
        }
        // The main window is only hidden (the page and its state stay for the next "Open CO").
        bool mainOpen = _mainWin != IntPtr.Zero && win != _mainWin && SendRetBool(_mainWin, "isVisible");
        if (_popups.Count == 0 && !mainOpen) SendN(_app, "setActivationPolicy:", PolicyAccessory);
    });

    // Pages of CO and the Google sign-in stay in the window; links elsewhere open in the default browser.
    [UnmanagedCallersOnly]
    private static void OnNavigationAction(IntPtr self, IntPtr sel, IntPtr web, IntPtr action, IntPtr handler)
    {
        nint policy = 1;   // allow
        Guard("navigation", () =>
        {
            if (RespondsTo(action, "shouldPerformDownload") && SendRetBool(action, "shouldPerformDownload")) { policy = 2; return; }
            IntPtr frame = Send(action, "targetFrame");
            if (frame != IntPtr.Zero && !SendRetBool(frame, "isMainFrame")) return;   // frames inside a CO page (module pages, previews)
            string? url = UrlOf(Send(Send(action, "request"), "URL"));
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
            if (u.Scheme is "about" or "blob" or "data") return;
            bool popup = IsPopup(web);
            // Google hosts stay in the window only during a sign-in: it starts at accounts.google.com or from /auth/ of CO
            // and goes on across Google's own hosts (country domains, youtube.com cookies). A link to Docs or YouTube
            // clicked in a CO page opens in the default browser.
            bool signIn = false;
            if (!popup && u.Scheme == "https" && GoogleSignInHost.IsMatch(u.Host))
            {
                Uri.TryCreate(UrlOf(Send(web, "URL")) ?? "", UriKind.Absolute, out var cur);
                signIn = u.Host == "accounts.google.com"
                    || (cur != null && (GoogleSignInHost.IsMatch(cur.Host) || (SameOrigin(cur) && cur.AbsolutePath.StartsWith("/auth/", StringComparison.Ordinal))));
            }
            bool inside = SameOrigin(u) || signIn;
            if (inside) return;
            policy = 0;
            OpenOutside(url);
            if (popup && UrlOf(Send(web, "URL")) is null or "about:blank") ClosePopupLater(web);
        });
        CallBlockN(handler, policy);
    }

    [UnmanagedCallersOnly]
    private static void OnNavigationResponse(IntPtr self, IntPtr sel, IntPtr web, IntPtr response, IntPtr handler)
    {
        nint policy = 1;
        Guard("response", () =>
        {
            // Download: "Content-Disposition: attachment", or a page of a type WebKit cannot show (not inside frames).
            IntPtr r = Send(response, "response");
            string? cd = IsKind(r, Cls("NSHTTPURLResponse")) ? FromStr(Send(r, "valueForHTTPHeaderField:", Str("Content-Disposition"))) : null;
            if (cd != null && cd.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase)) policy = 2;
            else if (SendRetBool(response, "isForMainFrame") && !SendRetBool(response, "canShowMIMEType")) policy = 2;
        });
        CallBlockN(handler, policy);
    }

    [UnmanagedCallersOnly]
    private static void OnBecameDownload(IntPtr self, IntPtr sel, IntPtr web, IntPtr actionOrResponse, IntPtr download) =>
        Guard("download", () => Send(download, "setDelegate:", _del));

    [UnmanagedCallersOnly]
    private static void OnFinished(IntPtr self, IntPtr sel, IntPtr web, IntPtr nav) => Guard("title", () =>
    {
        string? title = FromStr(Send(web, "title"));
        IntPtr win = Send(web, "window");
        if (win != IntPtr.Zero) Send(win, "setTitle:", Str(string.IsNullOrWhiteSpace(title) ? "CO" : title));
    });

    // Downloads go to ~/Downloads under a free name; a notification says where.
    [UnmanagedCallersOnly]
    private static void OnDownloadDestination(IntPtr self, IntPtr sel, IntPtr download, IntPtr response, IntPtr filename, IntPtr handler)
    {
        IntPtr dest = IntPtr.Zero;
        Guard("download destination", () =>
        {
            string name = Path.GetFileName(FromStr(filename) ?? "");
            if (name.Length == 0 || name.StartsWith('.')) name = "download" + name;
            string dir = Path.Combine(Paths.Home, "Downloads");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
                path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            _downloads[download] = path;
            dest = FileUrl(path);
        });
        CallBlock(handler, dest);   // nil cancels the download
    }

    [UnmanagedCallersOnly]
    private static void OnDownloadFinished(IntPtr self, IntPtr sel, IntPtr download) => Guard("download done", () =>
    {
        if (_downloads.Remove(download, out var path)) Dialog.Notify("Saved in Downloads: " + Path.GetFileName(path), "CO");
    });

    [UnmanagedCallersOnly]
    private static void OnDownloadFailed(IntPtr self, IntPtr sel, IntPtr download, IntPtr error, IntPtr resume) => Guard("download failed", () =>
    {
        _downloads.Remove(download, out var path);
        Log.Write($"window app: download {path} failed: {FromStr(Send(error, "localizedDescription"))}");
    });

    // window.open / target=_blank: CO pages (module pages too) get their own window, anything else opens in the browser.
    [UnmanagedCallersOnly]
    private static IntPtr OnCreateWebView(IntPtr self, IntPtr sel, IntPtr web, IntPtr cfg, IntPtr action, IntPtr features)
    {
        IntPtr created = IntPtr.Zero;
        Guard("new window", () =>
        {
            string? url = UrlOf(Send(Send(action, "request"), "URL"));
            bool ownBlob = url != null && url.StartsWith("blob:" + Origin + "/", StringComparison.Ordinal);   // a file made by a CO page
            if (url is { Length: > 0 } && url != "about:blank" && !ownBlob && Uri.TryCreate(url, UriKind.Absolute, out var u) && !SameOrigin(u))
            {
                OpenOutside(url);
                return;
            }
            var (win, nweb) = MakeWindow(cfg, 1100, 800);
            _popups.Add(win);
            Front(win);
            created = nweb;
        });
        return created;
    }

    [UnmanagedCallersOnly]
    private static void OnWebViewClose(IntPtr self, IntPtr sel, IntPtr web) => Guard("window.close", () =>
    {
        if (IsPopup(web)) ClosePopupLater(web);
    });

    [UnmanagedCallersOnly]
    private static void OnOpenPanel(IntPtr self, IntPtr sel, IntPtr web, IntPtr parameters, IntPtr frame, IntPtr handler)
    {
        IntPtr urls = IntPtr.Zero;
        Guard("open panel", () =>
        {
            IntPtr panel = Send(Cls("NSOpenPanel"), "openPanel");
            SendBool(panel, "setCanChooseFiles:", true);
            SendBool(panel, "setAllowsMultipleSelection:", SendRetBool(parameters, "allowsMultipleSelection"));
            SendBool(panel, "setCanChooseDirectories:", RespondsTo(parameters, "allowsDirectories") && SendRetBool(parameters, "allowsDirectories"));
            if (SendRetN(panel, "runModal") == 1) urls = Send(panel, "URLs");   // NSModalResponseOK
        });
        CallBlock(handler, urls);   // nil = cancelled
    }

    private static IntPtr Alert(IntPtr message, params string[] buttons)
    {
        IntPtr alert = Send(New("NSAlert"), "autorelease");
        Send(alert, "setMessageText:", Str("CO"));
        Send(alert, "setInformativeText:", message == IntPtr.Zero ? Str("") : message);
        foreach (var b in buttons) Send(alert, "addButtonWithTitle:", Str(b));
        return alert;
    }

    [UnmanagedCallersOnly]
    private static void OnAlert(IntPtr self, IntPtr sel, IntPtr web, IntPtr message, IntPtr frame, IntPtr handler)
    {
        Guard("alert", () => SendRetN(Alert(message, "OK"), "runModal"));
        CallBlock(handler);
    }

    [UnmanagedCallersOnly]
    private static void OnConfirm(IntPtr self, IntPtr sel, IntPtr web, IntPtr message, IntPtr frame, IntPtr handler)
    {
        bool ok = false;
        Guard("confirm", () => ok = SendRetN(Alert(message, "OK", "Cancel"), "runModal") == 1000);   // first button
        CallBlockBool(handler, ok);
    }

    [UnmanagedCallersOnly]
    private static void OnPrompt(IntPtr self, IntPtr sel, IntPtr web, IntPtr prompt, IntPtr defaultText, IntPtr frame, IntPtr handler)
    {
        IntPtr answer = IntPtr.Zero;
        Guard("prompt", () =>
        {
            IntPtr alert = Alert(prompt, "OK", "Cancel");
            IntPtr field = Send(SendRect(Send(Cls("NSTextField"), "alloc"), "initWithFrame:", new CGRect(0, 0, 320, 24)), "autorelease");
            Send(field, "setStringValue:", defaultText == IntPtr.Zero ? Str("") : defaultText);
            Send(alert, "setAccessoryView:", field);
            Send(Send(alert, "window"), "setInitialFirstResponder:", field);
            if (SendRetN(alert, "runModal") == 1000) answer = Send(field, "stringValue");
        });
        CallBlock(handler, answer);   // nil = cancelled
    }

    // Microphone for voice typing: CO pages only (macOS asks the user once more for the app itself).
    [UnmanagedCallersOnly]
    private static void OnMediaPermission(IntPtr self, IntPtr sel, IntPtr web, IntPtr origin, IntPtr frame, nint type, IntPtr handler)
    {
        nint decision = 2;   // deny
        Guard("media", () =>
        {
            string? host = FromStr(Send(origin, "host"));
            string? protocol = FromStr(Send(origin, "protocol"));
            if (protocol == "https" && "https://" + host == Origin && type == 1) decision = 1;   // WKMediaCaptureTypeMicrophone -> grant
        });
        CallBlockN(handler, decision);
    }

    // window.coDesktop.status() from CO Web: this Mac's device, attached to messages only while it is connected.
    [UnmanagedCallersOnly]
    private static void OnScriptMessage(IntPtr self, IntPtr sel, IntPtr ucc, IntPtr message) => Guard("message", () =>
    {
        IntPtr web = Send(message, "webView");
        IntPtr body = Send(message, "body");
        if (web == IntPtr.Zero || !IsKind(body, Cls("NSString"))) return;
        IntPtr info = Send(message, "frameInfo");
        if (!SendRetBool(info, "isMainFrame")) return;
        if (UrlOf(Send(web, "URL")) is not string page || !Uri.TryCreate(page, UriKind.Absolute, out var pu) || !SameOrigin(pu)) return;
        int id;
        string type;
        JsonElement args = default;
        string text = FromStr(body) ?? "";
        if (text.Length > 64 * 1024) return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            id = doc.RootElement.GetProperty("id").GetInt32();
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a)) args = a.Clone();
        }
        catch (Exception) { return; }
        var s = _snap;
        if (type is "moduleOp" or "moduleDevice" or "moduleDeviceSet")
        {
            // module calls may run a module's device file: off the main thread, the answer comes back on it
            MacModules.Start(type, args, id, reply => MainThread(() =>
                Send(web, "evaluateJavaScript:completionHandler:", Str("window.__coReply(" + reply + ")"), IntPtr.Zero)));
            return;
        }
        object reply = type == "status"
            ? new { id, result = new { deviceId = s.DeviceId, name = s.Name, @short = s.Short, paired = Connected(s) } }
            : new { id, error = "unknown_request" };
        Send(web, "evaluateJavaScript:completionHandler:", Str("window.__coReply(" + JsonSerializer.Serialize(reply) + ")"), IntPtr.Zero);
    });
}

// ~/Applications/CO.app (a copy of the resident binary, so Finder, Spotlight and the Dock show "CO" with its icon)
// and the LaunchAgent that starts its menu at login. The resident refreshes both on every start of a new version.
public static class MacAppInstall
{
    public const string Label = "org.freedom-workbench.co.app";
    public static string Bundle => Path.Combine(Paths.Home, "Applications", "CO.app");
    private static string Exe => Path.Combine(Bundle, "Contents", "MacOS", "CO");
    private static string AgentFile => Path.Combine(Paths.Home, "Library", "LaunchAgents", Label + ".plist");
    private static string SettingsFile => Path.Combine(Paths.DataDir, "window.json");
    private static string BuiltFile => Path.Combine(Paths.DataDir, "window.version");   // outside the bundle: the signature seals it

    public static bool Enabled
    {
        get
        {
            try { return !File.Exists(SettingsFile) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile))?["enabled"]?.GetValue<bool>() != false; }
            catch (Exception) { return true; }
        }
        set => File.WriteAllText(SettingsFile, new System.Text.Json.Nodes.JsonObject { ["enabled"] = value }.ToJsonString());
    }

    public static string Describe()
    {
        if (!Enabled) return "off";
        var (code, so, _) = Proc.Run("/bin/launchctl", new[] { "print", $"gui/{Program.Uid()}/{Label}" });
        return code != 0 ? "on (not loaded)" : so.Contains("state = running") ? "on (menu running)" : "on (menu not running)";
    }

    public static void Ensure(string version, string sourceBinary)
    {
        if (!Enabled) { Stop(); return; }
        bool changed = false;
        if (!File.Exists(Exe) || !File.Exists(BuiltFile) || File.ReadAllText(BuiltFile).Trim() != version)
        {
            WriteBundle(version, sourceBinary);
            changed = true;
        }
        string plist = AgentPlist();
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile)!);
        if (!File.Exists(AgentFile) || File.ReadAllText(AgentFile) != plist) { File.WriteAllText(AgentFile, plist); changed = true; }
        string domain = "gui/" + Program.Uid();
        bool loaded = Proc.Run("/bin/launchctl", new[] { "print", domain + "/" + Label }).code == 0;
        if (!changed && loaded) return;
        Proc.Run("/bin/launchctl", new[] { "bootout", domain + "/" + Label });
        var (code, _, se) = Proc.Run("/bin/launchctl", new[] { "bootstrap", domain, AgentFile });
        if (code != 0) throw new InvalidOperationException("launchctl bootstrap (window) failed: " + se.Trim());
        Log.Write($"window app {version}: LaunchAgent {(changed ? "installed" : "loaded")}");
    }

    public static void Stop()
    {
        Proc.Run("/bin/launchctl", new[] { "bootout", $"gui/{Program.Uid()}/{Label}" });
        if (File.Exists(AgentFile)) File.Delete(AgentFile);
    }

    public static void Remove()
    {
        Stop();
        if (Directory.Exists(Bundle)) Directory.Delete(Bundle, true);
        if (File.Exists(BuiltFile)) File.Delete(BuiltFile);
    }

    private static void WriteBundle(string version, string sourceBinary)
    {
        string contents = Path.Combine(Bundle, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(version));
        using (var icon = typeof(MacAppInstall).Assembly.GetManifestResourceStream("CO.icns"))
        using (var f = File.Create(Path.Combine(contents, "Resources", "CO.icns")))
            icon?.CopyTo(f);
        // A new file renamed over the old one: a running menu keeps its copy until it restarts.
        File.Copy(sourceBinary, Exe + ".new", overwrite: true);
        File.SetUnixFileMode(Exe + ".new", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(Exe + ".new", Exe, overwrite: true);
        Proc.Run("/usr/bin/xattr", new[] { "-cr", Bundle });   // codesign refuses Finder info / quarantine attributes
        var (code, _, se) = Proc.Run("/usr/bin/codesign", new[] { "--force", "-s", "-", Bundle }, timeoutMs: 120_000);
        if (code != 0) throw new InvalidOperationException("codesign CO.app failed: " + se.Trim());
        try { Proc.Run("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister", new[] { "-f", Bundle }); }
        catch (Exception ex) { Log.Write("window app: lsregister: " + ex.Message); }   // only for Spotlight; Finder finds it anyway
        File.WriteAllText(BuiltFile, version);
        Log.Write($"window app {version}: {Bundle} written");
    }

    private static string InfoPlist(string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleIdentifier</key><string>org.freedom-workbench.co.app</string>
          <key>CFBundleName</key><string>CO</string>
          <key>CFBundleDisplayName</key><string>CO</string>
          <key>CFBundleExecutable</key><string>CO</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleShortVersionString</key><string>{version}</string>
          <key>CFBundleVersion</key><string>{version}</string>
          <key>CFBundleIconFile</key><string>CO</string>
          <key>LSMinimumSystemVersion</key><string>14.0</string>
          <key>LSUIElement</key><true/>
          <key>NSHighResolutionCapable</key><true/>
          <key>NSMicrophoneUsageDescription</key><string>CO uses the microphone only for voice typing in the chat.</string>
        </dict>
        </plist>
        """;

    private static string AgentPlist()
    {
        string esc(string s) => System.Security.SecurityElement.Escape(s)!;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array><string>{esc(Exe)}</string><string>app</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
              <key>ThrottleInterval</key><integer>30</integer>
              <key>ProcessType</key><string>Interactive</string>
              <key>LimitLoadToSessionType</key><string>Aqua</string>
              <key>StandardOutPath</key><string>{esc(Path.Combine(Paths.LogDir, "app.out"))}</string>
              <key>StandardErrorPath</key><string>{esc(Path.Combine(Paths.LogDir, "app.out"))}</string>
            </dict>
            </plist>
            """;
    }
}
