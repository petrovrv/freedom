// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Browser;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Mac;

// R3: the agent browser on the Mac. Same tools as CO Desktop (browser_navigate, browser_snapshot, browser_click, …),
// one tab per job, in a Chromium of its own profile (no sign-ins of the Mac's user), DevTools only on 127.0.0.1.
// Chromium: Chrome for Testing in CO's data folder (installed on first use, no admin password), else Google Chrome
// or Microsoft Edge from /Applications started with that separate profile.
public static class MacBrowser
{
    public static string ProfileDir => Path.Combine(Paths.DataDir, "browser");
    public static string AppRoot => Path.Combine(Paths.DataDir, "browser-app");
    private static string JournalDir => Path.Combine(Paths.DataDir, "agent-browser");
    private const string CftApp = "chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing";

    private static EdgeBrowser? _browser;

    // One agent browser per process: the tools and the subscription sign-in (S4) share its profile.
    public static EdgeBrowser Browser(HttpClient http)
    {
        lock (Sync)
            return _browser ??= new EdgeBrowser(ProfileDir, () => FindOrInstall(http),
                "The agent browser is being installed on this Mac (Chrome for Testing, about 170 MB, a few minutes). Try again later; " +
                "sys_info shows the progress.");
    }

    public static string SettingsFile => Path.Combine(Paths.DataDir, "agent-browser.json");

    // 0.5.1: whether form submits, choices and uploads ask at the Mac first (default yes); purchase and delete always ask.
    public static bool ConfirmForms
    {
        get { try { return new AgentWebSettingsStore(SettingsFile).Current.ConfirmForms; } catch (Exception) { return true; } }
        set
        {
            var st = new AgentWebSettingsStore(SettingsFile);
            var c = st.Current;
            c.ConfirmForms = value;
            st.Save(c);
        }
    }

    public static IEnumerable<IDeviceTool> All(BrowserSettings settings, HttpClient http)
    {
        var browser = Browser(http);
        var store = new AgentWebSettingsStore(SettingsFile);
        var pages = new RunTabs<AgentPage>("agent-web",
            key => new AgentPage(browser.Tab(key), store, JournalDir, Log.Write),
            (_, page, ct) => page.CloseAsync(ct), keep: page => page.Paused != null,
            sweepEvery: TimeSpan.FromMinutes(1), log: Log.Write,
            // CO Desktop 0.9.9: tab chamberlain and per-chat lifetimes (pinned: only when the tabs extension runs).
            state: page => new TabState(page.LastUrl, page.HasInput, page.CaptchaPending, page.Paused != null),
            pinned: browser.PinnedKeysAsync);
        // 0.5.1: form confirmations are switched by the menu or 'co-resident browser forms' in other processes: read the file each time.
        foreach (var t in AgentBrowserTools.All(pages, () => store.Current.Block, () => store.Current.ConfirmClicks, () => ConfirmForms))
            yield return new Gated(t, settings);
    }

    // Executable to start, or null while Chrome for Testing is downloading (the call fails with the "being installed" text).
    public static string? FindOrInstall(HttpClient http)
    {
        string cft = Path.Combine(AppRoot, CftApp);
        if (File.Exists(cft)) return cft;
        foreach (var p in new[]
                 {
                     "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                     "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                     Path.Combine(Paths.Home, "Applications/Google Chrome.app/Contents/MacOS/Google Chrome"),
                 })
            if (File.Exists(p)) return p;
        StartInstall(http);
        return null;
    }

    private static readonly object Sync = new();
    private static Task? _install;
    public static string InstallStatus { get; private set; } = "";

    public static string Describe()
    {
        string cft = Path.Combine(AppRoot, CftApp);
        string? exe = File.Exists(cft) ? cft : null;
        string ver = File.Exists(Path.Combine(AppRoot, "version.txt")) ? File.ReadAllText(Path.Combine(AppRoot, "version.txt")).Trim() : "";
        return exe != null ? "Chrome for Testing " + ver : InstallStatus.Length > 0 ? InstallStatus : "Chrome for Testing not installed (installed on first use)";
    }

    private static void StartInstall(HttpClient http)
    {
        lock (Sync)
        {
            if (_install is { IsCompleted: false }) return;
            _install = Task.Run(async () =>
            {
                try { await InstallAsync(http, CancellationToken.None); }
                catch (Exception ex)
                {
                    InstallStatus = "Chrome for Testing install failed: " + ex.Message;
                    Log.Write("agent browser: " + InstallStatus);
                }
            });
        }
    }

    // Download (curl-free: HttpClient sets no quarantine flag), unpack with ditto (keeps the bundle's symlinks and
    // modes, which ZipFile would break), check the version, then move into place in one rename.
    private static async Task InstallAsync(HttpClient http, CancellationToken ct)
    {
        InstallStatus = "Chrome for Testing: looking up the current version";
        Log.Write("agent browser: installing Chrome for Testing");
        string json;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            json = await http.GetStringAsync(ChromeForTesting.Index, cts.Token);
        }
        var (version, url) = ChromeForTesting.PickDownload(json, "mac-arm64");
        Directory.CreateDirectory(Paths.DataDir);
        string zip = Path.Combine(Paths.DataDir, "browser-app.zip"), fresh = AppRoot + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        try
        {
            InstallStatus = $"Chrome for Testing {version}: downloading";
            // Timeout covers only the headers with ResponseHeadersRead: the body copy has its own deadline,
            // so a stalled download ends and the next browser call starts a new attempt.
            using (var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(30));
                using var r = await dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                r.EnsureSuccessStatusCode();
                await using var src = await r.Content.ReadAsStreamAsync(deadline.Token);
                await using var dst = File.Create(zip);
                await src.CopyToAsync(dst, deadline.Token);
            }
            InstallStatus = $"Chrome for Testing {version}: unpacking";
            Directory.CreateDirectory(fresh);
            var (code, _, se) = Proc.Run("/usr/bin/ditto", new[] { "-x", "-k", zip, fresh }, timeoutMs: 600_000);
            if (code != 0) throw new InvalidOperationException("ditto: " + se.Trim());
            string exe = Path.Combine(fresh, CftApp);
            var (vc, vo, ve) = Proc.Run(exe, new[] { "--version" }, timeoutMs: 60_000);
            if (vc != 0 || !vo.Contains(version.Split('.')[0])) throw new InvalidOperationException($"version check: {vo.Trim()} {ve.Trim()}");
            File.WriteAllText(Path.Combine(fresh, "version.txt"), version + "\n");
            if (Directory.Exists(AppRoot)) Directory.Delete(AppRoot, true);
            Directory.Move(fresh, AppRoot);
            InstallStatus = "Chrome for Testing " + version;
            Log.Write($"agent browser: Chrome for Testing {version} installed");
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
            try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch (IOException) { }
        }
    }

    // The Mac's user switches the browser tools: on (read and act), read (open and read pages only), off.
    private sealed class Gated : IDeviceTool
    {
        private readonly IDeviceTool _inner;
        private readonly BrowserSettings _settings;
        public Gated(IDeviceTool inner, BrowserSettings settings) { _inner = inner; _settings = settings; }
        public ToolDescriptor Descriptor => _inner.Descriptor;

        private void Check()
        {
            string mode = _settings.Mode;
            if (mode == "off") throw new ToolException("disabled", "The agent browser is switched off on this Mac by its user.");
            if (mode == "read" && _inner.Descriptor.Class != RiskClass.R)
                throw new ToolException("disabled", "The agent browser on this Mac is set to read only (open and read pages); its user did not allow clicks and typing.");
        }

        public ActionPlan Plan(JsonElement args, ToolEnv env) { Check(); return _inner.Plan(args, env); }
        public Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct) { Check(); return _inner.ExecuteAsync(args, env, ct); }
    }
}

// browser.json {"mode": "on" | "read" | "off"}; no file = on (the offer tells the user so). Unreadable = off.
public sealed class BrowserSettings
{
    private readonly string _file;
    public BrowserSettings(string file) => _file = file;

    public string Mode
    {
        get
        {
            try
            {
                if (!File.Exists(_file)) return "on";
                string? m = JsonNode.Parse(File.ReadAllText(_file))?["mode"]?.GetValue<string>();
                return m is "on" or "read" or "off" ? m : "off";
            }
            catch (Exception) { return "off"; }
        }
        set => File.WriteAllText(_file, new JsonObject { ["mode"] = value }.ToJsonString());
    }
}
