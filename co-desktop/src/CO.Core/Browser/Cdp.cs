// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CO.Core.Browser;

// What the browser tools need from the CO browser (TZ §7.4).
public interface IBrowser
{
    Task<string> NavigateAsync(string url, CancellationToken ct);
    Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct);
    Task<string> CurrentUrlAsync(CancellationToken ct);
    Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
    // 0.8.2: place cookies in the browser's store (used to carry an identity-provider session obtained out-of-band into
    // a connector's tab so its OIDC sign-in completes without a password ever reaching the page).
    Task SetCookiesAsync(IReadOnlyList<BrowserCookieSet> cookies, CancellationToken ct) => Task.CompletedTask;
}

public sealed record BrowserCookie(string Name, string Value, string Domain, string Path);

// 0.8.2: a cookie to write into the browser, with the flags CDP Network.setCookie needs.
public sealed record BrowserCookieSet(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly);

// Minimal Chrome DevTools Protocol session over one WebSocket.
public sealed class CdpSession : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    // ClientWebSocket forbids concurrent SendAsync; event handlers may send while another call is in flight.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _next;
    private Task? _loop;

    public event Action<string, JsonElement>? EventReceived;
    // co-resident 0.5.1: events of flattened child sessions with their sessionId (popup address capture).
    public event Action<string, JsonElement, string?>? SessionEventReceived;
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpSession> ConnectAsync(Uri url, CancellationToken ct)
    {
        var s = new CdpSession();
        s._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await s._ws.ConnectAsync(url, ct);
        s._loop = Task.Run(s.ReceiveLoop);
        return s;
    }

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct, int timeoutMs = 60_000) =>
        SendAsync(method, parameters, null, ct, timeoutMs);

    // sessionId addresses a flattened child session on a browser-level connection.
    public async Task<JsonElement> SendAsync(string method, object? parameters, string? sessionId, CancellationToken ct, int timeoutMs = 60_000)
    {
        int id = Interlocked.Increment(ref _next);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters != null) msg["params"] = JsonSerializer.SerializeToNode(parameters);
        if (sessionId != null) msg["sessionId"] = sessionId;
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using (timeout.Token.Register(() => tcs.TrySetCanceled()))
        {
            try { return await tcs.Task; }
            finally { _pending.TryRemove(id, out _); }
        }
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idEl) && _pending.TryGetValue(idEl.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException("CDP: " + err.GetProperty("message").GetString()));
                    else
                        tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res : default);
                }
                else if (root.TryGetProperty("method", out var m))
                {
                    // Handlers run synchronously on the receive loop: they must be quick and start their own tasks.
                    // A throwing handler must not kill the loop.
                    try
                    {
                        var pe = root.TryGetProperty("params", out var p) ? p : default;
                        EventReceived?.Invoke(m.GetString() ?? "", pe);
                        if (SessionEventReceived is { } se)
                            se(m.GetString() ?? "", pe, root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"CDP event handler failed: {ex}");
                    }
                }
            }
        }
        catch (Exception) { /* connection dropped; pending calls time out */ }
        finally
        {
            foreach (var t in _pending.Values) t.TrySetException(new IOException("CO browser connection closed."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (Exception) { }
        _ws.Dispose();
        if (_loop != null) { try { await _loop; } catch (Exception) { } }
    }
}

// Microsoft Edge with a separate CO profile, driven over the DevTools port (TZ §7.4).
// The user signs in to sites in this window; tools act with that session and see only what the user sees.
public sealed class EdgeBrowser : IBrowser, IAsyncDisposable
{
    private readonly string _profileDir;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private int _port;
    // 0.9.0: port last published in co-browser.json (0 = not yet in this session).
    private int _publishedPort;
    private CdpSession? _page;

    // Mac resident R3: where the Chromium executable is (null = not installed) and what to say when it is missing.
    private readonly Func<string?> _findExe;
    private readonly string _missing;

    public EdgeBrowser(string profileDir) : this(profileDir, FindEdge, "Microsoft Edge is not installed.") { }

    public EdgeBrowser(string profileDir, Func<string?> findExe, string missing)
    {
        _profileDir = profileDir;
        _findExe = findExe;
        _missing = missing;
    }

    // Flags added to LaunchArgs on start (live tests: --headless=new, so no window opens on the user's desktop).
    public string[] ExtraArgs { get; init; } = [];

    public static string? FindEdge()
    {
        foreach (string p in new[]
                 {
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
                     Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\Edge\Application\msedge.exe"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    // Command line that starts Edge with the CO profile (relaunch command of a pinned CO browser button).
    public static string EdgeCommandLine(string edgeExe, string profileDir)
    {
        // Trim a trailing backslash so it cannot escape the closing quote.
        var dir = profileDir.TrimEnd('\\');
        // 0.9.3: with the agent look (orange theme and page frame), so a relaunch from the taskbar keeps it.
        AgentLook.Ensure(dir);
        return $"\"{edgeExe}\" \"--user-data-dir={dir}\" \"--load-extension={AgentLook.LoadExtensionValue(dir)}\" " +
               "--remote-debugging-port=0 --no-first-run --no-default-browser-check " + NoWebdriverArg + " " + NoOcclusionArg;
    }

    // 0.9.4: Edge started with --remote-debugging-port reports navigator.webdriver = true in every tab (Edge 156,
    // checked 04.11); Cloudflare Turnstile then fails even the user's own click. This flag keeps it false.
    public const string NoWebdriverArg = "--disable-blink-features=AutomationControlled";

    // 0.9.8: Windows marks a CO browser window covered by other windows as hidden; then mouse and key events of the
    // agent are not delivered (07.11: about 150 actions on one form instead of 30-40). Without native occlusion
    // detection a covered window keeps rendering and takes input; only a minimized window is hidden.
    public const string NoOcclusionArg = "--disable-features=CalculateNativeWinOcclusion";

    // 0.9.0: the flags CO Desktop launches Edge with (no URL); also published in co-browser.json, so external clients
    // (browser-agent) start the same browser the same way when CO Desktop is not running.
    public static string[] LaunchArgs(string profileDir) => new[]
    {
        "--user-data-dir=" + profileDir, "--remote-debugging-port=0", "--no-first-run",
        "--no-default-browser-check", "--new-window", NoWebdriverArg, NoOcclusionArg,
        // 0.9.3: orange theme and page frame (AgentLook), so the agent browser is not mistaken for the user's Edge.
        "--load-extension=" + AgentLook.LoadExtensionValue(profileDir),
    };

    private int PortFromFile()
    {
        string f = Path.Combine(_profileDir, "DevToolsActivePort");
        try { return File.Exists(f) && int.TryParse(File.ReadLines(f).FirstOrDefault(), out int p) ? p : 0; }
        catch (IOException) { return 0; }
    }

    // 0.8.7: Chromium holds "lockfile" in the user-data-dir open exclusively while the browser runs.
    // On macOS and Linux it keeps the symlink "SingletonLock" -> "<host>-<pid>" instead.
    private bool ProfileInUse()
    {
        if (!OperatingSystem.IsWindows()) return SingletonLockHeld(_profileDir);
        string f = Path.Combine(_profileDir, "lockfile");
        try
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }               // 0.8.7: sharing violation => Edge holds the profile
        catch (UnauthorizedAccessException) { return true; }
    }

    public static bool SingletonLockHeld(string profileDir)
    {
        try
        {
            string? target = new FileInfo(Path.Combine(profileDir, "SingletonLock")).LinkTarget;
            if (target == null) return false;
            int dash = target.LastIndexOf('-');
            if (dash < 0 || !int.TryParse(target[(dash + 1)..], out int pid) || pid <= 0) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }          // no such process: a stale lock Chromium replaces itself
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // 0.8.7: launch cooldown, so a failed start is not repeated on every call.
    private DateTime _launchFailedUtc = DateTime.MinValue;
    private string? _launchError;

    // Starts the CO browser window (or reuses it) and opens url in it.
    public async Task OpenWindowAsync(string? url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureStartedLocked(url, ct); }
        finally { _gate.Release(); }
        if (url != null) await NavigateAsync(url, ct);
    }

    private async Task EnsureStartedLocked(string? firstUrl, CancellationToken ct)
    {
        if (_port > 0 && await AliveAsync(ct))
        {
            // 0.9.0: the port may have been taken up by the badge poller or the start-up tab cleanup.
            if (_publishedPort != _port) await WriteBrowserFileLocked(0, ct);
            return;
        }

        // A CO browser left open by an earlier CO Desktop session keeps its port file.
        // 0.8.7: retry a few times, a busy Edge may answer late.
        int filePort = PortFromFile();
        if (filePort > 0)
        {
            _port = filePort;
            if (await AliveRetryAsync(ct))
            {
                _page = null;
                ResetTabsLocked();
                _launchFailedUtc = DateTime.MinValue;
                _launchError = null;
                await WriteBrowserFileLocked(0, ct);
                return;
            }
        }
        _port = 0;

        // 0.8.7: never start a second msedge.exe on a profile that is still running; it would only
        // forward its command line and open another about:blank window without a new DevTools port.
        if (ProfileInUse())
            throw new InvalidOperationException(
                "The CO browser is open but its DevTools port does not answer. Close all CO browser windows " +
                "(orange icon on the taskbar); CO Desktop reopens it on the next call.");

        // 0.8.7: after a failed launch, wait 5 minutes before trying again.
        if (_launchFailedUtc != DateTime.MinValue && DateTime.UtcNow - _launchFailedUtc < TimeSpan.FromMinutes(5))
            throw new InvalidOperationException(
                (_launchError ?? "The CO browser could not be started.") + " (next attempt after a 5-minute pause)");

        string edge = _findExe() ?? throw new InvalidOperationException(_missing);
        Directory.CreateDirectory(_profileDir);
        AgentLook.Ensure(_profileDir);
        // 0.8.7: the port file is deleted only right before a real launch.
        File.Delete(Path.Combine(_profileDir, "DevToolsActivePort"));
        var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var arg in LaunchArgs(_profileDir)) psi.ArgumentList.Add(arg);
        foreach (var arg in ExtraArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(firstUrl ?? "about:blank");
        _proc = Process.Start(psi);
        for (int i = 0; i < 100 && _port == 0; i++)
        {
            await Task.Delay(150, ct);
            _port = PortFromFile();
        }
        if (_port == 0)
        {
            _launchFailedUtc = DateTime.UtcNow;
            _launchError = "The CO browser did not open its DevTools port.";
            throw new InvalidOperationException(_launchError);
        }
        _launchFailedUtc = DateTime.MinValue;
        _launchError = null;
        _page = null;
        ResetTabsLocked();
        int launchedPid = 0;
        try { launchedPid = _proc?.Id ?? 0; } catch (InvalidOperationException) { }
        await WriteBrowserFileLocked(launchedPid, ct);
    }

    // ---- 0.9.0: files shared with external clients (browser-agent) in the profile folder ----
    // co-browser.json: written by CO Desktop after it launched or attached to Edge; browser-agent connects to the port
    // and, when no Edge runs, starts Edge with exactly this command line.
    // ba-tabs.json: targetIds of browser-agent tabs; CO Desktop never takes them for the user's tab and never closes them.
    private string BrowserFile => Path.Combine(_profileDir, "co-browser.json");
    private string ExternalTabsFile => Path.Combine(_profileDir, "ba-tabs.json");

    private async Task WriteBrowserFileLocked(int pidHint, CancellationToken ct)
    {
        try
        {
            int pid = await BrowserPidLocked(ct);
            if (pid == 0) pid = pidHint;
            var cmdline = new JsonArray { _findExe() ?? "msedge.exe" };
            foreach (var a in LaunchArgs(_profileDir)) cmdline.Add(a);
            var json = new JsonObject
            {
                ["port"] = _port,
                ["pid"] = pid,
                ["writer"] = "co-desktop " + CO.Core.Runtime.DeviceRuntime.Version,
                ["version"] = CO.Core.Runtime.DeviceRuntime.Version,
                ["cmdline"] = cmdline,
            };
            Directory.CreateDirectory(_profileDir);
            var tmp = BrowserFile + ".tmp";
            File.WriteAllText(tmp, json.ToJsonString(TabsJsonOptions));
            File.Move(tmp, BrowserFile, overwrite: true);
            _publishedPort = _port;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* best effort: browser-agent falls back to DevToolsActivePort */ }
    }

    // PID of the Edge browser process over the browser connection (0 when unknown). Under _gate.
    private async Task<int> BrowserPidLocked(CancellationToken ct)
    {
        try
        {
            var session = await EnsureBrowserWsLocked(ct);
            var result = await session.SendAsync("SystemInfo.getProcessInfo", null, ct, timeoutMs: 3_000);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("processInfo", out var list)
                && list.ValueKind == JsonValueKind.Array)
                foreach (var p in list.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Object
                        && p.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "browser"
                        && p.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var pid))
                        return pid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return 0;
    }

    // Tolerates a missing or invalid file (empty set).
    private HashSet<string> ExternalTargetIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(ExternalTabsFile)) return ids;
            if (JsonNode.Parse(File.ReadAllText(ExternalTabsFile)) is JsonObject o && o["targets"] is JsonArray a)
                foreach (var n in a)
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) ids.Add(s);
        }
        catch (Exception) { }
        return ids;
    }

    private async Task<bool> AliveAsync(CancellationToken ct)
    {
        try
        {
            using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/json/version", ct);
            return r.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    // 0.8.7: alive check with a caller-defined (short) timeout.
    private async Task<bool> AliveAsync(CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var r = await _http.GetAsync($"http://127.0.0.1:{_port}/json/version", cts.Token).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    // 0.8.7: up to 3 alive checks, 1 s apart.
    private async Task<bool> AliveRetryAsync(CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(1000, ct);
            if (await AliveAsync(ct)) return true;
        }
        return false;
    }

    // Returns the PID of the running Edge browser process via CDP, or 0 if unavailable; never launches the browser.
    public async Task<int> BrowserProcessIdAsync(CancellationToken ct)
    {
        // 0.8.7: cosmetic poller - skip when the gate is busy instead of waiting.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0;
        try
        {
            var shortTimeout = TimeSpan.FromSeconds(2); // 0.8.7: short HTTP checks for the poller
            if (_port == 0)
                _port = PortFromFile();
            if (_port == 0)
                return 0;
            if (!await AliveAsync(ct, shortTimeout).ConfigureAwait(false))
            {
                // The CO browser was restarted outside CO Desktop (e.g. from a pinned button): take its new port.
                int filePort = PortFromFile();
                if (filePort == 0 || filePort == _port)
                    return 0;
                _port = filePort;
                _page = null;
                ResetTabsLocked();
                if (!await AliveAsync(ct, shortTimeout).ConfigureAwait(false))
                    return 0;
            }
            if (_publishedPort != _port) await WriteBrowserFileLocked(0, ct).ConfigureAwait(false); // 0.9.0
            var session = await EnsureBrowserWsLocked(ct).ConfigureAwait(false);
            var result = await session.SendAsync("SystemInfo.getProcessInfo", null, ct, timeoutMs: 3_000).ConfigureAwait(false);

            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("processInfo", out var processes) &&
                processes.ValueKind == JsonValueKind.Array)
            {
                foreach (var process in processes.EnumerateArray())
                {
                    if (process.ValueKind == JsonValueKind.Object &&
                        process.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        type.GetString() == "browser" &&
                        process.TryGetProperty("id", out var id) &&
                        id.ValueKind == JsonValueKind.Number &&
                        id.TryGetInt32(out var pid))
                    {
                        return pid;
                    }
                }
            }

            return 0;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return 0;
        }
        finally
        {
            // 0.8.7: reached only when the gate was acquired above.
            _gate.Release();
        }
    }

    // The user's tab: the first normal page of the CO window that is neither a CO service tab (co-tabs.json)
    // nor (0.9.0) a tab of browser-agent (ba-tabs.json).
    private async Task<CdpSession> PageLocked(CancellationToken ct)
    {
        await EnsureStartedLocked(null, ct);
        if (_page is { IsOpen: true }) return _page;
        var list = JsonNode.Parse(await _http.GetStringAsync($"http://127.0.0.1:{_port}/json/list", ct))!.AsArray();
        var keyed = AllServiceTargetIdsLocked();
        keyed.UnionWith(ExternalTargetIds());
        var target = list.FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page" && t["webSocketDebuggerUrl"] != null
                                              && !keyed.Contains(t["id"]?.GetValue<string>() ?? ""));
        if (target == null)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{_port}/json/new?about:blank");
            target = JsonNode.Parse(await (await _http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct));
        }
        _page = await CdpSession.ConnectAsync(new Uri(target!["webSocketDebuggerUrl"]!.GetValue<string>()), ct);
        await _page.SendAsync("Page.enable", null, ct);
        await _page.SendAsync("Runtime.enable", null, ct);
        return _page;
    }

    public async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = await PageLocked(ct);
            await page.SendAsync("Page.navigate", new { url }, ct);
            await WaitReadyLocked(page, ct);
            return await HrefLocked(page, ct);
        }
        finally { _gate.Release(); }
    }

    private static async Task WaitReadyLocked(CdpSession page, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        for (int i = 0; i < 120; i++)
        {
            var r = await page.SendAsync("Runtime.evaluate", new { expression = "document.readyState", returnByValue = true }, ct);
            if (r.GetProperty("result").TryGetProperty("value", out var v) && v.GetString() == "complete") return;
            await Task.Delay(250, ct);
        }
    }

    private static async Task<string> HrefLocked(CdpSession page, CancellationToken ct)
    {
        var r = await page.SendAsync("Runtime.evaluate", new { expression = "location.href", returnByValue = true }, ct);
        return r.GetProperty("result").GetProperty("value").GetString() ?? "";
    }

    public async Task<string> CurrentUrlAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await HrefLocked(await PageLocked(ct), ct); }
        finally { _gate.Release(); }
    }

    // The session cookies of a site (e.g. the document site's session cookies), so plain HTTP can act with the user's sign-in.
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var r = await (await PageLocked(ct)).SendAsync("Network.getCookies", new { urls = new[] { url } }, ct);
            var list = new List<BrowserCookie>();
            foreach (var c in r.GetProperty("cookies").EnumerateArray())
                list.Add(new BrowserCookie(c.GetProperty("name").GetString()!, c.GetProperty("value").GetString()!,
                    c.GetProperty("domain").GetString()!, c.GetProperty("path").GetString()!));
            return list;
        }
        finally { _gate.Release(); }
    }

    // 0.8.2: write cookies into the browser store (domain given explicitly, so the current page URL does not matter).
    public async Task SetCookiesAsync(IReadOnlyList<BrowserCookieSet> cookies, CancellationToken ct)
    {
        if (cookies.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            var page = await PageLocked(ct);
            var arr = cookies.Select(c => new
            {
                name = c.Name,
                value = c.Value,
                domain = c.Domain,
                path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                secure = c.Secure,
                httpOnly = c.HttpOnly,
            }).ToArray();
            await page.SendAsync("Network.setCookies", new { cookies = arr }, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task BringToFrontAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await (await PageLocked(ct)).SendAsync("Page.bringToFront", null, ct); }
        finally { _gate.Release(); }
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = await PageLocked(ct);
            var r = await page.SendAsync("Runtime.evaluate",
                new { expression, awaitPromise = true, returnByValue = true, userGesture = false }, ct, 180_000);
            if (r.TryGetProperty("exceptionDetails", out var ex))
                throw new InvalidOperationException("Page script failed: " + ex.GetRawText()[..Math.Min(300, ex.GetRawText().Length)]);
            return r.GetProperty("result").TryGetProperty("value", out var v) ? v.Clone() : default;
        }
        finally { _gate.Release(); }
    }

    // ---- 0.7.7: one background tab per service (each connector, agent) ----
    // Tools never navigate the user's tab; a keyed tab closed by the user is recreated on the next call.
    private string DevToolsBase => "http://127.0.0.1:" + _port;

    private readonly Dictionary<string, (string TargetId, CdpSession Session)> _tabs = new();
    private CdpSession? _browserWs;

    // CO Desktop 0.8.0: new tabs opened by a guarded tab (target=_blank, window.open) are closed before they load.
    // co-resident 0.5.1: OnOpened fires at once on the receive thread; OnBlocked gets the address the popup was going to load
    // (read from its first document request when the target starts blank), so the agent tab can open it itself.
    internal sealed record PopupGuard(Action<string> OnBlocked, Func<string, bool> OwnsFrame, Action? OnOpened = null);
    private readonly ConcurrentDictionary<string, PopupGuard> _popupGuards = new();   // tab key -> guard
    private readonly ConcurrentDictionary<string, string> _guardTargetIds = new();    // tab key -> targetId
    private readonly ConcurrentDictionary<string, string> _closedTargets = new();     // closed popup targetId -> tab key
    private bool _autoAttachOn; // per _browserWs connection; under _gate

    private static void DisposeQuietly(CdpSession? s)
    {
        if (s is null) return;
        _ = Task.Run(async () => { try { await s.DisposeAsync(); } catch { } });
    }

    private void ResetTabsLocked()
    {
        foreach (var (_, session) in _tabs.Values) DisposeQuietly(session);
        _tabs.Clear();
        _guardTargetIds.Clear();
        DisposeQuietly(_browserWs);
        _browserWs = null;
        _autoAttachOn = false;
    }

    private void SetTabLocked(string key, string targetId, CdpSession session)
    {
        _tabs[key] = (targetId, session);
        _guardTargetIds[key] = targetId;
    }

    private void RemoveTabLocked(string key)
    {
        _tabs.Remove(key);
        _guardTargetIds.TryRemove(key, out _);
    }

    private async Task<CdpSession> EnsureBrowserWsLocked(CancellationToken ct)
    {
        if (_browserWs is not { IsOpen: true })
        {
            DisposeQuietly(_browserWs);
            _browserWs = null;
            _autoAttachOn = false;
            var version = JsonNode.Parse(await _http.GetStringAsync(DevToolsBase + "/json/version", ct));
            var browserUrl = version?["webSocketDebuggerUrl"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Browser webSocketDebuggerUrl not available.");
            var ws = await CdpSession.ConnectAsync(new Uri(browserUrl), ct);
            ws.EventReceived += (m, p) => OnBrowserEvent(ws, m, p);
            _browserWs = ws;
        }

        if (!_autoAttachOn && !_popupGuards.IsEmpty)
        {
            // Every new page waits for the debugger, so a blocked popup sends no request.
            await _browserWs.SendAsync("Target.setAutoAttach", new
            {
                autoAttach = true,
                waitForDebuggerOnStart = true,
                flatten = true,
                filter = new object[] { new { type = "page", exclude = false } }
            }, ct);
            _autoAttachOn = true;
        }
        return _browserWs;
    }

    internal async Task GuardPopupsLocked(string key, Action<string> onBlocked, Func<string, bool> ownsFrame, CancellationToken ct, Action? onOpened = null)
    {
        _popupGuards[key] = new PopupGuard(onBlocked, ownsFrame, onOpened);
        await EnsureBrowserWsLocked(ct);
    }

    // Re-arms the guard after the browser connection was replaced.
    internal async Task EnsurePopupGuardLocked(string key, CancellationToken ct)
    {
        if (_popupGuards.ContainsKey(key)) await EnsureBrowserWsLocked(ct);
    }

    // Receive loop: no awaits, no _gate.
    private void OnBrowserEvent(CdpSession ws, string method, JsonElement p)
    {
        if (method != "Target.attachedToTarget") return;
        var sessionId = p.GetProperty("sessionId").GetString();
        var info = p.GetProperty("targetInfo");
        var targetId = info.GetProperty("targetId").GetString() ?? "";
        var openerId = info.TryGetProperty("openerId", out var o) ? o.GetString() : null;
        var openerFrameId = info.TryGetProperty("openerFrameId", out var f) ? f.GetString() : null;
        var url = info.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        string? owner = null;
        foreach (var id in new[] { openerId, openerFrameId })
            if (owner is null && id is not null && _closedTargets.TryGetValue(id, out var chained)) owner = chained;
        if (owner is null && (openerId is not null || openerFrameId is not null))
        {
            foreach (var (key, guard) in _popupGuards)
            {
                var tabExists = _guardTargetIds.TryGetValue(key, out var tid);
                if (tabExists && openerId == tid) { owner = key; break; }
                try
                {
                    if ((openerId is not null && guard.OwnsFrame(openerId))
                        || (openerFrameId is not null && guard.OwnsFrame(openerFrameId)))
                    { owner = key; break; }
                }
                catch
                {
                    // Cannot tell: fail closed while the guarded tab exists.
                    if (tabExists) { owner = key; break; }
                }
            }
        }

        if (owner is not null)
        {
            _closedTargets[targetId] = owner; // before closing, so popups of this popup are caught too
            try { if (_popupGuards.TryGetValue(owner, out var g0)) g0.OnOpened?.Invoke(); }
            catch { /* the callback must not affect blocking */ }
            _ = Task.Run(async () =>
            {
                var target = url;
                if ((target.Length == 0 || target == "about:blank") && sessionId is not null)
                    target = await PopupDocumentUrlAsync(ws, sessionId) ?? url;
                try { await ws.SendAsync("Target.closeTarget", new { targetId }, CancellationToken.None, 10_000); }
                catch { /* target may already be gone */ }
                try { if (_popupGuards.TryGetValue(owner, out var g)) g.OnBlocked(target); }
                catch { /* the callback must not affect blocking */ }
            });
        }
        else if (sessionId is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ws.SendAsync("Runtime.runIfWaitingForDebugger", null, sessionId, CancellationToken.None, 10_000);
                    await ws.SendAsync("Target.detachFromTarget", new { sessionId }, CancellationToken.None, 10_000);
                }
                catch { /* fail open for the user's own tabs */ }
            });
        }
    }

    // co-resident 0.5.1: a popup that starts blank (window.open() and then a location) gets its address only from its first
    // document request. Every request of the popup is failed before it is sent; the document address is returned (null in 5 s).
    private static async Task<string?> PopupDocumentUrlAsync(CdpSession ws, string sessionId)
    {
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(string method, JsonElement p, string? sid)
        {
            if (sid != sessionId || method != "Fetch.requestPaused") return;
            var requestId = p.TryGetProperty("requestId", out var r) ? r.GetString() : null;
            if (requestId is not null)
                _ = ws.SendAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, sessionId, CancellationToken.None, 5000)
                    .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            var type = p.TryGetProperty("resourceType", out var rt) ? rt.GetString() : null;
            var u = p.TryGetProperty("request", out var rq) && rq.TryGetProperty("url", out var uu) ? uu.GetString() : null;
            if (type == "Document" && !string.IsNullOrEmpty(u) && u != "about:blank") found.TrySetResult(u);
        }
        ws.SessionEventReceived += On;
        try
        {
            await ws.SendAsync("Fetch.enable", new { patterns = new[] { new { urlPattern = "*", requestStage = "Request" } } }, sessionId, CancellationToken.None, 5000);
            await ws.SendAsync("Runtime.runIfWaitingForDebugger", null, sessionId, CancellationToken.None, 5000);
            var done = await Task.WhenAny(found.Task, Task.Delay(5000));
            return done == found.Task ? found.Task.Result : null;
        }
        catch { return null; }
        finally { ws.SessionEventReceived -= On; }
    }

    private HashSet<string> KeyedTargetIdsLocked() => new(_tabs.Values.Select(t => t.TargetId));

    private async Task<JsonArray> GetTargetListAsync(CancellationToken ct) =>
        JsonNode.Parse(await _http.GetStringAsync(DevToolsBase + "/json/list", ct))!.AsArray();

    private static JsonNode? FindTarget(JsonArray list, string id) =>
        list.FirstOrDefault(n => n?["id"]?.GetValue<string>() == id);

    private async Task<CdpSession> TabLocked(string key, CancellationToken ct)
    {
        await EnsureStartedLocked(null, ct);
        var list = await GetTargetListAsync(ct);

        if (_tabs.TryGetValue(key, out var entry))
        {
            if (entry.Session.IsOpen && FindTarget(list, entry.TargetId) is not null) return entry.Session;
            RemoveTabLocked(key);
            DisposeQuietly(entry.Session);
        }

        var reattached = await ReattachLocked(key, list, ct);
        if (reattached is not null)
        {
            SaveTabIdsLocked();
            return reattached;
        }

        var browserWs = await EnsureBrowserWsLocked(ct);
        var r = await browserWs.SendAsync("Target.createTarget",
            new { url = "about:blank", background = true, newWindow = false }, ct);
        var id = r.GetProperty("targetId").GetString()
            ?? throw new InvalidOperationException("Target.createTarget returned no targetId.");

        string? ws = null;
        for (int i = 0; i < 20 && ws is null; i++)
        {
            if (i > 0) await Task.Delay(100, ct);
            ws = FindTarget(await GetTargetListAsync(ct), id)?["webSocketDebuggerUrl"]?.GetValue<string>();
        }
        if (ws is null) throw new InvalidOperationException($"Target {id} did not expose a webSocketDebuggerUrl.");

        var s = await CdpSession.ConnectAsync(new Uri(ws), ct);
        try
        {
            await s.SendAsync("Page.enable", null, ct);
            await s.SendAsync("Runtime.enable", null, ct);
        }
        catch
        {
            DisposeQuietly(s);
            throw;
        }
        SetTabLocked(key, id, s);
        _tabCreated[key] = DateTime.UtcNow; // 0.9.0: age of per-job tabs (closed at start after 24 h)
        SaveTabIdsLocked();
        return s;
    }

    // 0.7.8: service tab ids persist in <profile>/co-tabs.json: the browser window outlives CO Desktop, so after a
    // restart the same tabs are reattached (no duplicates) and never mistaken for the user's tab.
    // 0.9.0: the file stays a flat map of strings {"<key>": "<targetId>"} (older CO Desktop and browser-agent read
    // it so); the creation time of a tab is an extra entry "created:<key>": "<ISO 8601 UTC>". Object values
    // {"id": "...", "created": "..."} are read too.
    private static readonly JsonSerializerOptions TabsJsonOptions = new() { WriteIndented = true };
    private const string CreatedPrefix = "created:";

    private string TabsFile => Path.Combine(_profileDir, "co-tabs.json");

    // key -> creation time of tabs created in this session (under _gate).
    private readonly Dictionary<string, DateTime> _tabCreated = new(StringComparer.Ordinal);

    // Raw entries of co-tabs.json: tab ids and "created:" entries, all as strings.
    private Dictionary<string, string> LoadTabFileLocked()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(TabsFile)) return result;
            if (JsonNode.Parse(File.ReadAllText(TabsFile)) is not JsonObject root) return result;
            foreach (var (key, node) in root)
            {
                if (string.IsNullOrWhiteSpace(key) || node is null) continue;
                if (node is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    if (!string.IsNullOrWhiteSpace(s)) result[key] = s;
                }
                else if (node is JsonObject o && !key.StartsWith(CreatedPrefix, StringComparison.Ordinal))
                {
                    if (o["id"] is JsonValue idv && idv.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
                        result[key] = id;
                    if (o["created"] is JsonValue cv && cv.TryGetValue<string>(out var c) && !string.IsNullOrWhiteSpace(c))
                        result[CreatedPrefix + key] = c;
                }
            }
        }
        catch { /* unreadable: no known tabs */ }
        return result;
    }

    private void WriteTabFileLocked(Dictionary<string, string> entries)
    {
        try
        {
            Directory.CreateDirectory(_profileDir);
            var tmp = TabsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, TabsJsonOptions));
            File.Move(tmp, TabsFile, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // key -> targetId (without the "created:" entries).
    private Dictionary<string, string> LoadTabIdsLocked()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in LoadTabFileLocked())
            if (!key.StartsWith(CreatedPrefix, StringComparison.Ordinal)) result[key] = value;
        return result;
    }

    private void SaveTabIdsLocked()
    {
        var merged = LoadTabFileLocked();
        foreach (var (key, tab) in _tabs)
        {
            merged[key] = tab.TargetId;
            if (_tabCreated.TryGetValue(key, out var created))
                merged[CreatedPrefix + key] = created.ToString("o");
        }
        WriteTabFileLocked(merged);
    }

    // keepFileEntry: the tab could not be closed (browser unavailable) - keep its record in co-tabs.json so it is
    // never mistaken for the user's tab and is closed by a later sweep.
    private void ForgetTabLocked(string key, bool keepFileEntry = false)
    {
        if (_tabs.TryGetValue(key, out var entry))
        {
            DisposeQuietly(entry.Session);
            RemoveTabLocked(key);
        }
        _tabCreated.Remove(key);
        _popupGuards.TryRemove(key, out _);
        if (keepFileEntry) { lock (_tabViews) _tabViews.Remove(key); return; }
        var file = LoadTabFileLocked();
        if (file.Remove(key) | file.Remove(CreatedPrefix + key)) WriteTabFileLocked(file);
        lock (_tabViews) _tabViews.Remove(key);
    }

    private HashSet<string> AllServiceTargetIdsLocked()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in _tabs.Values) ids.Add(tab.TargetId);
        foreach (var id in LoadTabIdsLocked().Values) ids.Add(id);
        return ids;
    }

    // 0.9.9: keys of the CO tabs the user pinned in the browser (the lock of the tab chamberlain: never closed, never
    // sent to another site), read from the "CO agent browser tabs" extension (AgentLook) like browser-agent does: its
    // service worker answers coTabs() over a separate browser connection (the main one auto-attaches new targets).
    // Null when unknown (browser not running, extension not loaded). Never starts the browser.
    public async Task<IReadOnlySet<string>?> PinnedKeysAsync(CancellationToken ct)
    {
        Dictionary<string, string> keyOf;
        await _gate.WaitAsync(ct);
        try
        {
            if (_port == 0 || _tabs.Count == 0) return _tabs.Count == 0 ? new HashSet<string>() : null;
            keyOf = _tabs.ToDictionary(t => t.Value.TargetId, t => t.Key, StringComparer.Ordinal);
        }
        finally { _gate.Release(); }
        try
        {
            var version = JsonNode.Parse(await _http.GetStringAsync(DevToolsBase + "/json/version", ct));
            var url = version?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (url is null) return null;
            await using var ws = await CdpSession.ConnectAsync(new Uri(url), ct);
            var targets = await ws.SendAsync("Target.getTargets", null, ct, 5000);
            string? sw = null;
            foreach (var t in targets.GetProperty("targetInfos").EnumerateArray())
            {
                var u = t.TryGetProperty("url", out var uv) ? uv.GetString() ?? "" : "";
                if (t.GetProperty("type").GetString() == "service_worker" && u.StartsWith("chrome-extension://", StringComparison.Ordinal)
                    && u.EndsWith("/co-tabs-sw.js", StringComparison.Ordinal))
                { sw = t.GetProperty("targetId").GetString(); break; }
            }
            if (sw is null) return null;
            var att = await ws.SendAsync("Target.attachToTarget", new { targetId = sw, flatten = true }, ct, 5000);
            var sessionId = att.GetProperty("sessionId").GetString();
            var r = await ws.SendAsync("Runtime.evaluate", new
            {
                expression = "self.coTabsVersion >= 1 ? self.coTabs() : null", awaitPromise = true, returnByValue = true,
            }, sessionId, ct, 5000);
            try { await ws.SendAsync("Target.detachFromTarget", new { sessionId }, ct, 2000); } catch { }
            if (!r.TryGetProperty("result", out var res) || !res.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in list.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True
                    && t.TryGetProperty("targetId", out var id) && id.ValueKind == JsonValueKind.String
                    && keyOf.TryGetValue(id.GetString()!, out var key))
                    pinned.Add(key);
            return pinned;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    // 0.9.0: closes a CO tab (per-job agent tabs when idle). Only tabs CO Desktop owns (in co-tabs.json or opened in
    // this session) are closed; never starts the browser. The key is forgotten when the tab is closed or already gone;
    // if the browser is unavailable the co-tabs.json record stays until a later close succeeds.
    public async Task CloseTabAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string? targetId = _tabs.TryGetValue(key, out var entry) ? entry.TargetId
                : LoadTabIdsLocked().TryGetValue(key, out var fromFile) ? fromFile : null;
            bool closed = targetId is null || await CloseTargetLocked(targetId, ct);
            ForgetTabLocked(key, keepFileEntry: !closed);
        }
        finally { _gate.Release(); }
    }

    // 0.9.0: at CO Desktop start, closes the CO tabs whose key matches (per-job agent tabs) and that are older than
    // maxAge or have no creation time (0.8.x tabs). Never starts the browser: without a running CO browser only the
    // entries of co-tabs.json are removed (only when no Edge holds the profile); a tab that a running browser failed
    // to close keeps its record.
    // Returns the number of keys removed.
    public async Task<int> CloseStaleTabsAsync(Func<string, bool> isTransient, TimeSpan maxAge, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = LoadTabFileLocked();
            var now = DateTime.UtcNow;
            var stale = new List<(string Key, string TargetId)>();
            foreach (var (key, targetId) in file)
            {
                if (key.StartsWith(CreatedPrefix, StringComparison.Ordinal) || !isTransient(key)) continue;
                // A call of this session has reattached the tab (under _gate, like this sweep): it is in use.
                if (_tabs.ContainsKey(key)) continue;
                bool fresh = file.TryGetValue(CreatedPrefix + key, out var c)
                    && DateTime.TryParse(c, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                    && now - created.ToUniversalTime() < maxAge;
                if (!fresh) stale.Add((key, targetId));
            }
            if (stale.Count == 0) return 0;

            // Attach only to a running browser (port file answers); then the normal attach path runs.
            bool alive = false;
            if (_port == 0)
            {
                int filePort = PortFromFile();
                if (filePort > 0)
                {
                    _port = filePort;
                    alive = await AliveAsync(ct, TimeSpan.FromSeconds(3));
                    _port = 0;
                    if (alive) await EnsureStartedLocked(null, ct);
                }
            }
            else alive = await AliveAsync(ct, TimeSpan.FromSeconds(3));
            bool edgeRunning = !alive && ProfileInUse();
            int removed = 0;
            foreach (var (key, targetId) in stale)
            {
                // No Edge on the profile (lockfile free): its tab ids are dead (a new Edge gives new ids), the record
                // goes. Edge running but DevTools not answering: keep the record for a later sweep.
                bool closed = alive ? await CloseTargetLocked(targetId, ct) : !edgeRunning;
                ForgetTabLocked(key, keepFileEntry: !closed);
                if (closed) removed++;
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    // Under _gate. True when the tab is closed or no longer exists; false when the browser is unavailable or the
    // close failed while the tab is still listed.
    private async Task<bool> CloseTargetLocked(string targetId, CancellationToken ct)
    {
        try
        {
            if (_port == 0 || !await AliveAsync(ct, TimeSpan.FromSeconds(3))) return false;
            var ws = await EnsureBrowserWsLocked(ct);
            var r = await ws.SendAsync("Target.closeTarget", new { targetId }, ct, 10_000);
            // Older protocol versions answer {success:false} instead of an error: then the tab list decides.
            if (!(r.ValueKind == JsonValueKind.Object && r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False))
                return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
        try { return FindTarget(await GetTargetListAsync(ct), targetId) is null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private async Task<CdpSession?> ReattachLocked(string key, JsonArray list, CancellationToken ct)
    {
        if (!LoadTabIdsLocked().TryGetValue(key, out var targetId)) return null;
        var target = FindTarget(list, targetId);
        if (target is null) return null;
        string? wsUrl;
        try { wsUrl = target["webSocketDebuggerUrl"]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        if (string.IsNullOrWhiteSpace(wsUrl) || !Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri)) return null;
        CdpSession? session = null;
        try
        {
            session = await CdpSession.ConnectAsync(wsUri, ct);
            await session.SendAsync("Page.enable", null, ct);
            await session.SendAsync("Runtime.enable", null, ct);
            SetTabLocked(key, targetId, session);
            return session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DisposeQuietly(session);
            throw;
        }
        catch
        {
            DisposeQuietly(session);
            return null;
        }
    }

    private readonly Dictionary<string, EdgeTab> _tabViews = new();

    public EdgeTab Tab(string key)
    {
        lock (_tabViews)
        {
            if (!_tabViews.TryGetValue(key, out var tab))
            {
                tab = new EdgeTab(this, key);
                _tabViews[key] = tab;
            }
            return tab;
        }
    }

    private static async Task<string> NavigateLocked(CdpSession page, string url, CancellationToken ct)
    {
        await page.SendAsync("Page.navigate", new { url }, ct);
        await WaitReadyLocked(page, ct);
        return await HrefLocked(page, ct);
    }

    private static async Task<JsonElement> EvaluateLocked(CdpSession page, string expression, CancellationToken ct)
    {
        var res = await page.SendAsync("Runtime.evaluate",
            new { expression, awaitPromise = true, returnByValue = true, userGesture = false }, ct, 180_000);

        if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("exceptionDetails", out var ex))
        {
            var raw = ex.GetRawText();
            if (raw.Length > 300) raw = raw[..300];
            throw new InvalidOperationException("Page script failed: " + raw);
        }

        if (res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("result", out var r)
            && r.ValueKind == JsonValueKind.Object
            && r.TryGetProperty("value", out var v))
            return v.Clone();

        return default;
    }

    private static async Task<IReadOnlyList<BrowserCookie>> CookiesLocked(CdpSession page, string url, CancellationToken ct)
    {
        var res = await page.SendAsync("Network.getCookies", new { urls = new[] { url } }, ct);
        var list = new List<BrowserCookie>();
        if (res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("cookies", out var cookies)
            || cookies.ValueKind != JsonValueKind.Array)
            return list;

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

        foreach (var c in cookies.EnumerateArray())
            list.Add(new BrowserCookie(Str(c, "name"), Str(c, "value"), Str(c, "domain"), Str(c, "path")));
        return list;
    }

    // 0.8.2: write cookies into the browser store; domain is explicit so the tab's current URL does not matter.
    private static Task SetCookiesLocked(CdpSession page, IReadOnlyList<BrowserCookieSet> cookies, CancellationToken ct)
    {
        var arr = cookies.Select(c => new
        {
            name = c.Name,
            value = c.Value,
            domain = c.Domain,
            path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
            secure = c.Secure,
            httpOnly = c.HttpOnly,
        }).ToArray();
        return page.SendAsync("Network.setCookies", new { cookies = arr }, ct);
    }

    // 0.9.0: each tab has its own semaphore for its operations; the process-wide _gate is held only while the tab is
    // found, attached or created (and for the browser connection), not for the whole operation. So operations on
    // different tabs (two jobs, or two connectors) no longer wait for each other; one tab still runs one operation
    // at a time. Lock order is always tab -> _gate.
    public sealed class EdgeTab : IBrowser
    {
        private readonly EdgeBrowser _owner;
        private readonly string _key;
        private readonly SemaphoreSlim _tabGate = new(1, 1);

        internal EdgeTab(EdgeBrowser owner, string key) { _owner = owner; _key = key; }

        // Under _tabGate: the tab's session, (re)created or reattached under _gate.
        private async Task<CdpSession> SessionLocked(CancellationToken ct)
        {
            await _owner._gate.WaitAsync(ct);
            try
            {
                var p = await _owner.TabLocked(_key, ct);
                await _owner.EnsurePopupGuardLocked(_key, ct);
                return p;
            }
            finally { _owner._gate.Release(); }
        }

        private async Task<T> Run<T>(Func<CdpSession, Task<T>> action, CancellationToken ct)
        {
            await _tabGate.WaitAsync(ct);
            try
            {
                var p = await SessionLocked(ct);
                return await action(p);
            }
            finally { _tabGate.Release(); }
        }

        public string Key => _key;

        // Raw CDP access for modules that need more than navigate/evaluate (agent browser, CO Desktop 0.8.0).
        // The session object changes when the tab is recreated; callers compare it to re-initialize.
        public Task<T> WithSessionAsync<T>(Func<CdpSession, Task<T>> action, CancellationToken ct) => Run(action, ct);

        // Closes new tabs this tab opens before they load. Call inside WithSessionAsync (0.9.0: takes _gate itself,
        // the tab's own semaphore is held by the caller).
        public async Task GuardPopupsInSessionAsync(Action<string> onBlocked, Func<string, bool> ownsFrame, CancellationToken ct, Action? onOpened = null)
        {
            await _owner._gate.WaitAsync(ct);
            try { await _owner.GuardPopupsLocked(_key, onBlocked, ownsFrame, ct, onOpened); }
            finally { _owner._gate.Release(); }
        }

        public Task<string> NavigateAsync(string url, CancellationToken ct) =>
            Run(p => NavigateLocked(p, url, ct), ct);

        public Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct) =>
            Run(p => EvaluateLocked(p, expression, ct), ct);

        public Task<string> CurrentUrlAsync(CancellationToken ct) =>
            Run(p => HrefLocked(p, ct), ct);

        public Task BringToFrontAsync(CancellationToken ct) =>
            Run(p => p.SendAsync("Page.bringToFront", null, ct), ct);

        public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct) =>
            Run(p => CookiesLocked(p, url, ct), ct);

        public Task SetCookiesAsync(IReadOnlyList<BrowserCookieSet> cookies, CancellationToken ct) =>
            cookies.Count == 0 ? Task.CompletedTask : Run(async p => { await SetCookiesLocked(p, cookies, ct); return 0; }, ct);

        // The user signs in on the service's own tab: open (or keep) the site there and show the tab.
        public Task OpenAsync(string url, CancellationToken ct) =>
            Run(async p =>
            {
                var href = await HrefLocked(p, ct);
                var sameHost = Uri.TryCreate(href, UriKind.Absolute, out var a)
                    && Uri.TryCreate(url, UriKind.Absolute, out var b)
                    && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
                if (!sameHost) await NavigateLocked(p, url, ct);
                await p.SendAsync("Page.bringToFront", null, ct);
                return 0;
            }, ct);

        // 0.9.0: closes this tab if CO Desktop owns it (see EdgeBrowser.CloseTabAsync), after a running operation.
        public async Task CloseAsync(CancellationToken ct)
        {
            await _tabGate.WaitAsync(ct);
            try { await _owner.CloseTabAsync(_key, ct); }
            finally { _tabGate.Release(); }
        }
    }


    // Live tests: stops the browser this instance started itself (the whole process tree). The CO browser of the user
    // is never closed this way; DisposeAsync leaves it open. 10.11: live tests left their Edge windows running.
    public async Task ShutdownAsync()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await p.WaitForExitAsync(cts.Token);
            }
        }
        catch (Exception) { }
        finally { p.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_page != null) await _page.DisposeAsync();
        ResetTabsLocked();
        _http.Dispose();
        // The window belongs to the user; it stays open when CO Desktop closes.
    }
}
