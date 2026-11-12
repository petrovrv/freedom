// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.7.9: AI Workbench keeps engie.ai signed in by itself. A background loop (CO Desktop start + every 10 minutes)
// opens engie.ai in its own CO-browser tab (starting the browser if needed) and renews the Okta token by a reload;
// only a real interactive sign-in involves the user: the tab comes to the front and a tray notice appears.
// Tool calls that hit an expired session wait up to 75 s for that sign-in instead of failing at once (0.7.10).
using CO.Core.Browser;

namespace CO.Corporate.Tools;

public static partial class EngieTools
{
    private const string SignInNeededCode = "sign_in_needed";

    private static readonly object _notifyLock = new();
    private static DateTime _lastNotifyUtc = DateTime.MinValue;
    private static DateTime _lastKeepAliveNotifyUtc = DateTime.MinValue;

    public static Action<string>? SignInNeeded { get; set; }

    // engie_chat / engie_fetch calls in progress; a keep-alive reload would abort their gateway request.
    private static int _inFlight;

    // 0.8.7: UTC time a sign-in page was first seen in the engie tab (null = not on sign-in)
    private static DateTime? _signInSeenUtc;

    // 0.8.7: max time to defer keep-alive while a sign-in page is showing
    private static readonly TimeSpan SignInMaxDefer = TimeSpan.FromMinutes(20);

    // 0.8.7: true if the URL's host is an interactive sign-in host (Okta / Microsoft)
    internal static bool IsSignInHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        static bool IsOrEndsWith(string h, string domain) =>
            h.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

        return IsOrEndsWith(host, "okta-emea.com")
            || IsOrEndsWith(host, "okta.com")
            || IsOrEndsWith(host, "oktapreview.com")
            || host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task KeepAliveLoopAsync(IBrowser b, Func<bool> enabled, CancellationToken ct)
    {
        // 0.7.13: lets HeartbeatState() report "off" when the ENGIE connector is disabled.
        _enabled = enabled;
        var delay = TimeSpan.FromSeconds(20);

        while (true)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromMinutes(10);

            try
            {
                if (!enabled() || Volatile.Read(ref _inFlight) > 0)
                    continue;

                // 0.8.7: don't interrupt an interactive sign-in in the engie tab
                string? currentUrl = null;
                try
                {
                    currentUrl = await b.CurrentUrlAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    currentUrl = null; // 0.8.7: URL unreadable, proceed as before
                }

                if (currentUrl is not null)
                {
                    if (IsSignInHost(currentUrl))
                    {
                        var now = DateTime.UtcNow;
                        _signInSeenUtc ??= now;

                        if (now - _signInSeenUtc.Value < SignInMaxDefer)
                        {
                            Trace?.Invoke("engie keep-alive: sign-in page open in the engie tab, skipped");
                            continue;
                        }

                        _signInSeenUtc = null; // 0.8.7: sign-in page stuck >= 20 min, proceed
                    }
                    else
                    {
                        _signInSeenUtc = null; // 0.8.7: not on sign-in page, reset
                    }
                }

                await EnsureOnEngieAsync(b, ct).ConfigureAwait(false);

                int? secondsLeft = await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false);
                if ((secondsLeft is null || secondsLeft < 900) && Volatile.Read(ref _inFlight) == 0)
                {
                    Trace?.Invoke("engie keep-alive: renewing");
                    await EnsureFreshTokenAsync(b, ct, force: true).ConfigureAwait(false);
                }

                SetSession(EngieSessionState.Online, ""); // 0.7.11: state for the CO Web ENGIE toggle
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ToolException ex) when (ex.Code == SignInNeededCode)
            {
                // 0.7.13: try the secret-free automatic sign-in (FastPass / push) before bothering the user.
                try
                {
                    if (await TryAutoSignInAsync(b, ct).ConfigureAwait(false))
                        continue;
                }
                catch (Exception autoEx) when (autoEx is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Trace?.Invoke("engie keep-alive: automatic sign-in failed: " + autoEx.Message);
                }

                SetSession(EngieSessionState.SignInNeeded, "engie.ai needs sign-in");
                bool notify;
                lock (_notifyLock)
                {
                    var now = DateTime.UtcNow;
                    notify = now - _lastKeepAliveNotifyUtc >= TimeSpan.FromHours(1);
                    if (notify)
                        _lastKeepAliveNotifyUtc = now;
                }

                if (notify)
                {
                    try
                    {
                        await NotifySignIn(b).ConfigureAwait(false);
                    }
                    catch (Exception nex)
                    {
                        Trace?.Invoke($"engie keep-alive: notify failed: {nex.Message}");
                    }
                }
                else
                {
                    Trace?.Invoke("engie keep-alive: sign-in needed (notification suppressed)");
                }
            }
            catch (Exception ex)
            {
                Trace?.Invoke($"engie keep-alive: {ex.GetType().Name}: {ex.Message}");
                SetSession(EngieSessionState.Offline, ex.Message);
            }
        }
    }

    internal static async Task NotifySignIn(IBrowser b)
    {
        Trace?.Invoke("engie: interactive sign-in needed");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await b.BringToFrontAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"engie: bring to front failed: {ex.Message}");
        }

        bool notify;
        lock (_notifyLock)
        {
            var now = DateTime.UtcNow;
            notify = now - _lastNotifyUtc >= TimeSpan.FromMinutes(30);
            if (notify)
                _lastNotifyUtc = now;
        }

        if (!notify)
            return;

        try
        {
            SignInNeeded?.Invoke("engie.ai: sign-in needed — the engie.ai tab is open in the CO browser");
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"engie: SignInNeeded handler failed: {ex.Message}");
        }
    }

    internal static async Task WaitForInteractiveSignInAsync(IBrowser b, TimeSpan wait, CancellationToken ct)
    {
        await NotifySignIn(b).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + wait;
        var pollInterval = TimeSpan.FromSeconds(3);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                int? secondsLeft = await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false);
                if (secondsLeft is >= 120)
                {
                    Trace?.Invoke("engie: interactive sign-in completed");
                    SetSession(EngieSessionState.Online, "");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace?.Invoke($"engie: sign-in poll: {ex.Message}");
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, ct).ConfigureAwait(false);
        }

        throw new ToolException(
            SignInNeededCode,
            "engie.ai is waiting for sign-in: complete it in the engie.ai tab of the CO browser (it was brought to the front), then retry.");
    }
}
