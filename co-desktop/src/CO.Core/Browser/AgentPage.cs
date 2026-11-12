// SPDX-License-Identifier: AGPL-3.0-or-later
// PART 1: session lifecycle, CDP event handling, request guard, pause control,
// navigation and journal. AgentPage.Actions.cs holds the snapshot and the interactions.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CO.Core.Tools;

namespace CO.Core.Browser;

/// <summary>Why and by whom the agent tab is currently paused.</summary>
public sealed record PauseInfo(string Reason, string By, DateTime SinceUtc);

/// <summary>Minimal description of the page currently shown in the agent tab.</summary>
public sealed record PageInfo(string Url, string Title);

/// <summary>
/// Drives one background tab of the user's Edge ("CO browser") over CDP on behalf of the AI agent (CO Desktop 0.8.0).
/// 0.9.0: one instance per job tab (see RunTabs); operations of different tabs run in parallel.
/// All CDP access goes through <see cref="RunAsync{T}"/>, which serializes with other users of the same tab
/// and (re)initializes the session whenever the underlying tab was recreated.
/// </summary>
public sealed partial class AgentPage
{
    private const string UiWorld = "co-agent-ui";
    private const string OpsWorld = "co-agent-ops";
    private const string BindingName = "coAgentCtl";

    private readonly EdgeBrowser.EdgeTab _tab;
    private readonly AgentWebSettingsStore _settings;
    private readonly string _journalDir;
    private readonly Action<string>? _log;

    // The session that has been initialized (domains enabled, overlay injected).
    private CdpSession? _session;
    private volatile PauseInfo? _paused;
    // Last top-level document blocked by the request guard (set on the receive thread).
    private (string Url, string Reason)? _lastBlock;

    // Incremented whenever a top-level navigation is requested/started or a Document request is paused.
    // Used to detect navigations that start some time after an input event.
    private int _navSeq;
    private readonly ConcurrentQueue<string> _dialogs = new();
    // Frame ids of the agent tab: popups opened from its iframes are closed too.
    private readonly ConcurrentDictionary<string, byte> _frames = new();
    // Hosts seen connecting to a private address after passing the DNS check (DNS rebinding); blocked from then on.
    private readonly ConcurrentDictionary<string, byte> _rebound = new(StringComparer.OrdinalIgnoreCase);
    // 0.9.0: static, several job tabs write the same journal.
    private static int _shotSeq;
    private static readonly object _journalLock = new();

    public AgentPage(EdgeBrowser.EdgeTab tab, AgentWebSettingsStore settings, string journalDir, Action<string>? log = null)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _journalDir = journalDir ?? throw new ArgumentNullException(nameof(journalDir));
        _log = log;
    }

    public PauseInfo? Paused => _paused;

    // 0.9.0: one AgentPage per job tab ("agent-web:<RunId>"); pause, rebound-host ban, frames and refs are per tab.
    public string TabKey => _tab.Key;

    /// <summary>Closes the tab (idle job tab). The page object must not be used afterwards.</summary>
    public Task CloseAsync(CancellationToken ct) => _tab.CloseAsync(ct);

    private void Log(string message) => _log?.Invoke("[agent-page] " + message);

    /// <summary>Runs <paramref name="f"/> with an initialized session, serialized with other tab users.</summary>
    public Task<T> RunAsync<T>(Func<CdpSession, Task<T>> f, CancellationToken ct) =>
        _tab.WithSessionAsync(async s =>
        {
            if (!ReferenceEquals(s, _session))
            {
                await InitAsync(s, ct);
                _session = s;
            }
            return await f(s);
        }, ct);

    // ---------------------------------------------------------------- session setup

    private async Task InitAsync(CdpSession s, CancellationToken ct)
    {
        s.EventReceived += (m, p) => OnEvent(s, m, p);

        await s.SendAsync("Page.enable", null, ct);
        await s.SendAsync("Runtime.enable", null, ct);
        await s.SendAsync("DOM.enable", null, ct);
        await s.SendAsync("Accessibility.enable", null, ct);
        await s.SendAsync("Fetch.enable", new
        {
            patterns = new[] { new { urlPattern = "*", requestStage = "Request" } }
        }, ct);

        await s.SendAsync("Network.enable", null, ct);

        _frames.Clear();
        var tree = await s.SendAsync("Page.getFrameTree", null, ct);
        var ids = new List<string>();
        CollectFrameIds(tree.GetProperty("frameTree"), ids);
        foreach (var id in ids) _frames.TryAdd(id, 0);
        await _tab.GuardPopupsInSessionAsync(OnPopupBlocked, id => _frames.ContainsKey(id), ct);

        await s.SendAsync("Runtime.addBinding", new { name = BindingName, executionContextName = UiWorld }, ct);
        await s.SendAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = AgentPageScripts.Overlay,
            worldName = UiWorld,
            runImmediately = true
        }, ct);

        try { await ApplyOverlayAsync(s, ct); }
        catch (Exception ex) { Log("overlay init failed: " + ex.Message); }

        Log("session initialized");
    }

    // ---------------------------------------------------------------- CDP events (receive thread)

    private void OnEvent(CdpSession s, string method, JsonElement p)
    {
        try
        {
            switch (method)
            {
                case "Fetch.requestPaused":
                    OnRequestPaused(s, p);
                    break;

                case "Runtime.bindingCalled":
                    if (Str(p, "name") != BindingName) break;
                    var payload = Str(p, "payload");
                    if (payload == "pause")
                    {
                        _paused = new PauseInfo("you took over", "user", DateTime.UtcNow);
                        Log("paused by user");
                    }
                    else if (payload == "resume")
                    {
                        _paused = null;
                        Log("resumed by user");
                    }
                    break;

                case "Page.javascriptDialogOpening":
                    var type = Str(p, "type") ?? "dialog";
                    var message = Str(p, "message") ?? "";
                    if (message.Length > 300) message = message[..300];
                    _dialogs.Enqueue($"{type}: {message}");
                    Log($"dialog dismissed ({type})");
                    _ = Task.Run(async () =>
                    {
                        try { await s.SendAsync("Page.handleJavaScriptDialog", new { accept = false }, CancellationToken.None); }
                        catch { /* dialog may already be gone */ }
                    });
                    break;

                case "Page.frameRequestedNavigation":
                case "Page.frameStartedLoading":
                case "Page.frameScheduledNavigation":
                    // Count navigation starts so input handlers can wait for late navigations.
                    Interlocked.Increment(ref _navSeq);
                    break;

                case "Network.responseReceived":
                {
                    if (!p.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object) break;
                    var ipStr = Str(resp, "remoteIPAddress")?.Trim().Trim('[', ']');
                    var respUrl = Str(resp, "url");
                    if (string.IsNullOrEmpty(ipStr) || string.IsNullOrEmpty(respUrl)) break;
                    if (!IPAddress.TryParse(ipStr, out var ip) || !AgentWebGuard.IsBlockedIp(ip)) break;
                    var host = ReboundHostOf(respUrl);
                    if (host is null || IPAddress.TryParse(host, out _)) break;
                    if (!_rebound.TryAdd(host, 0)) break;

                    _lastBlock = (respUrl, "name switched to a private or local address");
                    Interlocked.Increment(ref _navSeq);
                    Log($"DNS rebinding: {host} connected to {ip}");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await s.SendAsync("Page.navigate", new { url = "about:blank" }, CancellationToken.None);
                            await PauseAsync(s, "a site switched to a private address - check the page, then press 'Return to agent'", CancellationToken.None);
                        }
                        catch { /* best effort */ }
                    });
                    break;
                }

                case "Page.frameAttached":
                    if (Str(p, "frameId") is { } attached) _frames.TryAdd(attached, 0);
                    break;

                case "Page.frameDetached":
                    var detachReason = Str(p, "reason");
                    if ((detachReason is null || detachReason == "remove") && Str(p, "frameId") is { } detached)
                        _frames.TryRemove(detached, out _);
                    break;

                case "Page.frameNavigated":
                    if (p.TryGetProperty("frame", out var frame) && Str(frame, "id") is { } navId)
                        _frames.TryAdd(navId, 0);
                    if (p.TryGetProperty("frame", out frame) && !frame.TryGetProperty("parentId", out _))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await ApplyOverlayAsync(s, CancellationToken.None); }
                            catch { /* page may navigate again; next event retries */ }
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"event {method} failed: {ex.Message}");
        }
    }

    private void OnRequestPaused(CdpSession s, JsonElement p)
    {
        var requestId = Str(p, "requestId");
        if (requestId is null) return;

        string? reason;
        // Hoisted out of the try so the async continuation below can use them
        string url = "";
        bool isDocument = false;
        try
        {
            url = p.GetProperty("request").GetProperty("url").GetString() ?? "";
            var resourceType = Str(p, "resourceType");
            isDocument = resourceType == "Document";
            // A paused Document request is a navigation attempt (allowed or blocked).
            if (isDocument) Interlocked.Increment(ref _navSeq);
            // 0.9.0: blocked domains are refused for subresources too (fetch/XHR would bypass the list otherwise).
            var block = _settings.Current.Block;
            reason = isDocument
                ? AgentWebGuard.CheckDocumentUrl(url, block)
                : AgentWebGuard.CheckSubresourceUrl(url, block);
            reason ??= ReboundReason(url);

            if (reason != null && isDocument)
            {
                _lastBlock = (url, reason);
                Log($"blocked document {url}: {reason}");
            }
        }
        catch (Exception ex)
        {
            // Fail closed: if we cannot decide, the request does not go out.
            reason = "guard error";
            Log("request guard error: " + ex.Message);
        }

        var blocked = reason != null;
        _ = Task.Run(async () =>
        {
            // Local copy so the captured outer variable is not mutated from another thread
            var fail = blocked;
            if (!fail)
            {
                // DNS-level check: reject hosts resolving to disallowed addresses
                string? resolved;
                try
                {
                    resolved = await AgentWebGuard.CheckResolvedAsync(url, null, CancellationToken.None);
                }
                catch
                {
                    // Fail closed on any guard failure
                    resolved = "guard error";
                }
                if (resolved != null)
                {
                    fail = true;
                    if (isDocument)
                    {
                        _lastBlock = (url, resolved);
                    }
                    Log($"blocked {url}: {resolved}");
                }
            }
            try
            {
                if (fail)
                    await s.SendAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, CancellationToken.None);
                else
                    await s.SendAsync("Fetch.continueRequest", new { requestId }, CancellationToken.None);
            }
            catch { /* request may have been cancelled by the page */ }
        });
    }

    // Browser receive thread: a new tab opened by this tab was closed before it loaded.
    private void OnPopupBlocked(string url)
    {
        var u = url.Length == 0 ? "about:blank" : url;
        _lastBlock = (u, "New tabs and pop-up windows are blocked for the agent (use browser_navigate)");
        Interlocked.Increment(ref _navSeq);
        Log("blocked new tab " + u);
    }

    private static string? ReboundHostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var h = u.Host.Trim('[', ']').TrimEnd('.');
        return h.Length == 0 ? null : h;
    }

    private string? ReboundReason(string url) =>
        ReboundHostOf(url) is { } host && _rebound.ContainsKey(host)
            ? "name switched to a private or local address"
            : null;

    private static void CollectFrameIds(JsonElement frameTree, ICollection<string> into)
    {
        if (frameTree.TryGetProperty("frame", out var frame) && Str(frame, "id") is { } id)
            into.Add(id);
        if (frameTree.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectFrameIds(child, into);
    }

    /// <summary>Returns and clears the JavaScript dialogs dismissed since the last call.</summary>
    public string[] TakeDialogs()
    {
        var list = new List<string>();
        while (_dialogs.TryDequeue(out var d)) list.Add(d);
        return list.ToArray();
    }

    // ---------------------------------------------------------------- isolated worlds

    private static async Task<int> WorldAsync(CdpSession s, string world, CancellationToken ct)
    {
        var tree = await s.SendAsync("Page.getFrameTree", null, ct);
        var frameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        var r = await s.SendAsync("Page.createIsolatedWorld", new { frameId, worldName = world, grantUniveralAccess = false }, ct);
        return r.GetProperty("executionContextId").GetInt32();
    }

    private static async Task<JsonElement> EvalInWorldAsync(CdpSession s, string world, string expression, CancellationToken ct)
    {
        var contextId = await WorldAsync(s, world, ct);
        var r = await s.SendAsync("Runtime.evaluate", new { expression, contextId, returnByValue = true, awaitPromise = true }, ct);
        ThrowOnException(r);
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : default;
    }

    private static void ThrowOnException(JsonElement r)
    {
        if (!r.TryGetProperty("exceptionDetails", out var ex)) return;
        var text = ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d)
            ? d.GetString()
            : Str(ex, "text");
        text ??= "script error";
        if (text.Length > 300) text = text[..300];
        throw new InvalidOperationException(text);
    }

    private async Task ApplyOverlayAsync(CdpSession s, CancellationToken ct)
    {
        var paused = _paused;
        var expr = AgentPageScripts.Overlay
            + ";window.__coAgentUi && window.__coAgentUi.set("
            + JsonSerializer.Serialize(paused != null) + ","
            + JsonSerializer.Serialize(paused?.Reason) + ")";
        await EvalInWorldAsync(s, UiWorld, expr, ct);
    }

    // ---------------------------------------------------------------- pause control

    public void RequireNotPaused()
    {
        var p = _paused;
        if (p is null) return;
        throw new ToolException("paused_by_user",
            $"The user controls the agent tab ({p.Reason}, since {p.SinceUtc:HH:mm} UTC). " +
            "Ask the user in chat and retry after they press 'Return to agent' in the CO browser.");
    }

    private async Task PauseAsync(CdpSession s, string reason, CancellationToken ct)
    {
        _paused = new PauseInfo(reason, "auto", DateTime.UtcNow);
        Log("auto-paused: " + reason);
        try { await ApplyOverlayAsync(s, ct); }
        catch (Exception ex) { Log("overlay update failed: " + ex.Message); }
        // Inside WithSessionAsync: use the session command, not EdgeTab.BringToFrontAsync.
        await s.SendAsync("Page.bringToFront", null, ct);
    }

    // ---------------------------------------------------------------- page state

    // 0.9.9: also remembers the address for the tab chamberlain (LastUrl).
    private async Task<PageInfo> InfoAsync(CdpSession s, CancellationToken ct)
    {
        var r = await s.SendAsync("Runtime.evaluate", new { expression = "({u: location.href, t: document.title})", returnByValue = true }, ct);
        ThrowOnException(r);
        var v = r.GetProperty("result").GetProperty("value");
        var info = new PageInfo(Str(v, "u") ?? "", Str(v, "t") ?? "");
        _lastUrl = info.Url;
        return info;
    }

    private volatile string? _lastUrl;
    /// <summary>0.9.9: address of the page at the end of the last call (null before the first one).</summary>
    public string? LastUrl => _lastUrl;

    private static async Task WaitSettledAsync(CdpSession s, CancellationToken ct)
    {
        await Task.Delay(250, ct);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            try
            {
                var r = await s.SendAsync("Runtime.evaluate", new { expression = AgentPageScripts.ReadyState, returnByValue = true }, ct, 5000);
                var state = r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
                if (state == "complete") return;
                if (state == "interactive" && sw.ElapsedMilliseconds >= 3000) return;
            }
            catch (InvalidOperationException) { /* context destroyed while navigating */ }
            catch (TimeoutException) { }
            await Task.Delay(200, ct);
        }
    }

    // 0.9.9: a captcha no longer pauses the tab: the tab is marked captcha-pending (CaptchaPending) and the call
    // goes on, so the agent fills the rest of the form and asks the user at the end. One-time code and payment pages
    // still pause the tab (user_action_needed).
    private long _captchaTicks;
    public bool CaptchaPending => Interlocked.Read(ref _captchaTicks) != 0;

    private async Task<string?> ChallengeAsync(CdpSession s, CancellationToken ct)
    {
        try
        {
            var v = await EvalInWorldAsync(s, OpsWorld, AgentPageScripts.Challenge, ct);
            var ch = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (ch == "captcha")
            {
                if (Interlocked.CompareExchange(ref _captchaTicks, DateTime.UtcNow.Ticks, 0) == 0) Log("captcha pending");
                return ch;
            }
            if (Interlocked.Exchange(ref _captchaTicks, 0) != 0) Log("captcha gone");
            if (string.IsNullOrWhiteSpace(ch)) return null;
            await PauseAsync(s, ch + " - complete it in the CO browser, then press 'Return to agent'", ct);
            return ch;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("challenge check failed: " + ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- navigation

    public Task<PageInfo> NavigateAsync(string url, CancellationToken ct)
    {
        RequireNotPaused();
        url = (url ?? "").Trim();
        if (!url.Contains("://")) url = "https://" + url;

        var reason = AgentWebGuard.CheckDocumentUrl(url, _settings.Current.Block);
        if (reason != null) throw Blocked(url, reason);

        return RunAsync(async s =>
        {
            RequireNotPaused();
            _lastBlock = null;
            var r = await s.SendAsync("Page.navigate", new { url }, ct);
            await WaitSettledAsync(s, ct);
            return await AfterNavigationAsync(s, Str(r, "errorText"), ct);
        }, ct);
    }

    public Task<PageInfo> BackAsync(CancellationToken ct)
    {
        RequireNotPaused();
        return RunAsync(async s =>
        {
            RequireNotPaused();
            var h = await s.SendAsync("Page.getNavigationHistory", null, ct);
            var index = h.GetProperty("currentIndex").GetInt32();
            if (index <= 0) throw new ToolException("no_history", "Nothing to go back to.");
            var entryId = h.GetProperty("entries")[index - 1].GetProperty("id").GetInt32();

            _lastBlock = null;
            await s.SendAsync("Page.navigateToHistoryEntry", new { entryId }, ct);
            await WaitSettledAsync(s, ct);
            return await AfterNavigationAsync(s, null, ct);
        }, ct);
    }

    public Task<PageInfo> InfoAsync(CancellationToken ct) => RunAsync(s => InfoAsync(s, ct), ct);

    /// <summary>
    /// 0.9.9: re-checks a pending captcha after calls that do not end with the navigation checks (select, scroll,
    /// upload, snapshot, status): clears the mark when the widget is gone. Never pauses; no-op without a mark or
    /// while the user controls the tab.
    /// </summary>
    public async Task RecheckCaptchaAsync(CancellationToken ct)
    {
        if (!CaptchaPending || _paused != null) return;
        try
        {
            await RunAsync(async s =>
            {
                var v = await EvalInWorldAsync(s, OpsWorld, AgentPageScripts.Challenge, ct);
                if (!(v.ValueKind == JsonValueKind.String && v.GetString() == "captcha")
                    && Interlocked.Exchange(ref _captchaTicks, 0) != 0)
                    Log("captcha gone");
                return true;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log("captcha re-check failed: " + ex.Message); }
    }

    // Shared post-navigation checks: guard block, navigation error, challenge page.
    private async Task<PageInfo> AfterNavigationAsync(CdpSession s, string? errorText, CancellationToken ct)
    {
        if (_lastBlock is { } block) throw Blocked(block.Url, block.Reason);
        if (!string.IsNullOrEmpty(errorText))
            throw new ToolException("navigation_failed", "Navigation failed: " + errorText);

        var ch = await ChallengeAsync(s, ct);
        if (ch != null && ch != "captcha")
            throw new ToolException("user_action_needed",
                $"The page asks for {ch}. The agent tab is paused and in front; ask the user to complete it and press 'Return to agent'.");

        return await InfoAsync(s, ct);
    }

    // 0.9.0: the hint depends on the reason (blocked list vs. addresses that are always blocked).
    private static ToolException Blocked(string url, string reason) =>
        new("domain_blocked",
            reason + ": " + HostOf(url) + (reason == AgentWebGuard.BlockedListReason
                ? ". The user can remove the site in CO Desktop -> Connectors -> Blocked sites."
                : reason.Contains("private", StringComparison.Ordinal)
                    ? ". Local and private network addresses are always blocked."
                    : "."));

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ---------------------------------------------------------------- journal

    // 0.9.8: time of the last typed text, choice or upload in this tab (0 = none since the last navigation by the
    // agent); a tab with form input is kept open longer by the idle sweep (0.9.9: RunTabs state callback).
    private long _inputTicks;
    public void MarkInput() => Interlocked.Exchange(ref _inputTicks, DateTime.UtcNow.Ticks);
    public void ClearInput() => Interlocked.Exchange(ref _inputTicks, 0);
    // 0.9.9: typed data held by the page (formIdleMin applies, the chamberlain prefers other tabs).
    public bool HasInput => Interlocked.Read(ref _inputTicks) != 0;

    public async Task JournalAsync(string runId, string tool, object details, CancellationToken ct)
    {
        if (!_settings.Current.Journal) return;
        try
        {
            await RunAsync(async s =>
            {
                // 0.9.0: a screenshot taken while the tab commits a navigation (e.g. to about:blank after a block)
                // fails with "Not attached to an active page": retry once, then write the line without a shot.
                byte[]? bytes = null;
                for (int attempt = 0; attempt < 2 && bytes == null; attempt++)
                {
                    try
                    {
                        var shotRes = await s.SendAsync("Page.captureScreenshot", new { format = "jpeg", quality = 60 }, ct);
                        bytes = Convert.FromBase64String(Str(shotRes, "data") ?? "");
                    }
                    catch (Exception) when (attempt == 0 && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(400, ct);
                    }
                    catch (Exception ex) { Log($"journal screenshot failed ({tool}): {ex.Message}"); }
                }
                string? url = null;
                try { url = (await InfoAsync(s, ct)).Url; } catch (Exception) { }

                var now = DateTime.UtcNow;
                var dir = Path.Combine(_journalDir, now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);
                var safeTool = string.Concat(tool.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
                string? shot = null;
                if (bytes != null)
                {
                    shot = Path.Combine(dir, $"{now:HHmmss}-{Interlocked.Increment(ref _shotSeq):D4}-{safeTool}.jpg");
                    await File.WriteAllBytesAsync(shot, bytes, ct);
                }

                var line = JsonSerializer.Serialize(new
                {
                    t = now.ToString("o"),
                    runId,
                    tool,
                    url,
                    details,
                    shot
                });
                lock (_journalLock)
                    File.AppendAllText(Path.Combine(_journalDir, "journal.jsonl"), line + Environment.NewLine);
                return true;
            }, ct);
        }
        catch (Exception ex)
        {
            // The journal must never fail a tool.
            Log($"journal failed ({tool}): {ex.Message}");
        }
    }
}
