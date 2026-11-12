// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.7.13: engie.ai state in the device heartbeat (Bridge publishes it in owner.json for CO Web without CO Desktop,
// e.g. a phone) and a secret-free automatic sign-in for the keep-alive loop (Okta FastPass / push, as for SharePoint).
// 0.8.1: when that is not enough, the Okta password fallback (CO Desktop -> Folders) signs in; the user approves the push.
using System.Text.Json;
using CO.Core.Browser;

namespace CO.Corporate.Tools;

public static partial class EngieTools
{
    // 0.7.13: keep-alive "enabled" predicate, captured by KeepAliveLoopAsync; null until the loop starts.
    private static Func<bool>? _enabled;

    // 0.7.13: an "Online" result older than this is reported as "unknown" (keep-alive runs every 10 min).
    private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromMinutes(25);

    // 0.7.13: automatic sign-in throttling / limits.
    private static readonly object _autoSignInLock = new();
    private static DateTime _lastAutoSignInUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoSignInEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutoSignInTimeout = TimeSpan.FromSeconds(90);
    // 0.7.13: ~30 s with nothing secret-free to press (20 x 1.5 s) => password/code needed, give up.
    private const int AutoSignInQuietLimit = 20;

    // 0.7.13: engie.ai session state for the heartbeat: online|signin|offline|unknown|off.
    public static string HeartbeatState()
    {
        try
        {
            var enabled = Volatile.Read(ref _enabled);
            if (enabled is not null && !enabled()) return "off";

            var (state, _, checkedUtc) = Session;
            return state switch
            {
                EngieSessionState.Online =>
                    DateTime.UtcNow - checkedUtc > HeartbeatStaleAfter ? "unknown" : "online",
                EngieSessionState.SignInNeeded => "signin",
                EngieSessionState.Offline => "offline",
                _ => "unknown",
            };
        }
        catch
        {
            // 0.7.13: never let the heartbeat fail because of the engie probe.
            return "unknown";
        }
    }

    // 0.7.13: secret-free automatic sign-in (Okta FastPass / push / Verify, Microsoft account tile, Stay signed in).
    // Never types passwords or codes. At most one attempt per 30 minutes; skipped while engie calls are running.
    internal static async Task<bool> TryAutoSignInAsync(IBrowser b, CancellationToken ct)
    {
        if (Volatile.Read(ref _inFlight) > 0) return false;

        lock (_autoSignInLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoSignInUtc < AutoSignInEvery) return false;
            _lastAutoSignInUtc = now;
        }

        Trace?.Invoke("engie keep-alive: automatic sign-in");
        await b.NavigateAsync(Origin + "/chat", ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + AutoSignInTimeout;
        var quiet = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SharePointApi.SignInPoll, ct).ConfigureAwait(false);

            // 0.7.13: an engie_chat call started meanwhile - leave the shared tab to it (its own sign-in wait covers it).
            if (Volatile.Read(ref _inFlight) > 0) return false;

            if (await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false) is int left && left >= MinFreshSeconds)
            {
                SetSession(EngieSessionState.Online, "");
                Trace?.Invoke("engie: automatic sign-in completed");
                return true;
            }

            var step = "";
            try
            {
                var r = await b.EvaluateAsync(SharePointApi.AutoStepJs, ct).ConfigureAwait(false);
                if (r.ValueKind == JsonValueKind.String) step = r.GetString() ?? "";
            }
            catch (InvalidOperationException)
            {
                // 0.7.13: the page navigated during the script - the flow is moving, not quiet.
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Trace?.Invoke("engie: sign-in step failed: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(step))
            {
                Trace?.Invoke("engie: sign-in step " + step);
                quiet = 0;
            }
            else if (++quiet >= AutoSignInQuietLimit)
            {
                // 0.7.13: nothing secret-free left to press (password/code needed); 0.8.1: try the Okta password.
                return await TryPasswordSignInAsync(b, ct).ConfigureAwait(false);
            }
        }

        return await TryPasswordSignInAsync(b, ct).ConfigureAwait(false); // 0.8.1: fall back to Okta password sign-in
    }

    // 0.8.2: set by the app; runs the Okta Identity Engine sign-in (password + Okta Verify push with a number) and
    // returns the Okta session cookies to carry into the engie.ai tab. The password never reaches the browser.
    public static Func<Action<CO.Corporate.SharePoint.OktaNumberChallenge>, Action<string>, CancellationToken,
        Task<CO.Corporate.SharePoint.EngieOktaIdx.Result>>? EngieOktaSignIn { get; set; }

    // 0.8.2: the app shows this number to the user (tray balloon); the heartbeat also carries it to the phone push.
    public static Action<string>? OktaNumberNotice { get; set; }

    // 0.8.2: how long to wait for the engie.ai OIDC sign-in to complete after the Okta cookies are placed in the tab.
    private static readonly TimeSpan EngieSignInWait = TimeSpan.FromSeconds(180);

    // 0.8.2: last-resort sign-in once nothing secret-free is left: Okta IDX (password + push with number), then the
    // Okta session cookies are placed in the engie.ai tab so its own OIDC sign-in completes.
    private static async Task<bool> TryPasswordSignInAsync(IBrowser b, CancellationToken ct)
    {
        var signIn = EngieOktaSignIn;
        if (signIn is null || Volatile.Read(ref _inFlight) > 0) return false;

        Trace?.Invoke("engie: Okta sign-in (approve the Okta Verify push)");

        void OnNumber(CO.Corporate.SharePoint.OktaNumberChallenge c)
        {
            SetOktaNumber(c.Id, c.Number);                 // heartbeat -> phone push
            try { OktaNumberNotice?.Invoke(c.Number); } catch { /* tray notice is best-effort */ }
        }

        CO.Corporate.SharePoint.EngieOktaIdx.Result res;
        try
        {
            res = await signIn(OnNumber, s => Trace?.Invoke("engie: " + s), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetOktaNumber("", "");
            Trace?.Invoke("engie: Okta sign-in failed: " + ex.Message);
            return false;
        }

        SetOktaNumber("", "");   // the number is no longer needed
        if (!res.Ok)
        {
            Trace?.Invoke("engie: Okta sign-in did not complete: " + res.Error);
            return false;
        }

        // Wait for any running engie calls that share the tab before touching it (up to 75 s).
        for (var i = 0; i < 75 && Volatile.Read(ref _inFlight) > 0; i++)
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (Volatile.Read(ref _inFlight) > 0)
        {
            Trace?.Invoke("engie: sign-in has the Okta session but an engie call is running; the tab reload is deferred");
            return false;
        }

        // Place the Okta session cookies in the browser, then let engie.ai complete its own OIDC sign-in.
        try
        {
            await b.SetCookiesAsync(res.OktaCookies, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace?.Invoke("engie: could not set Okta cookies in the browser: " + ex.Message);
            return false;
        }
        await b.NavigateAsync(Origin + "/chat", ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + EngieSignInWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SharePointApi.SignInPoll, ct).ConfigureAwait(false);
            if (Volatile.Read(ref _inFlight) > 0) return false;   // an engie call took the tab

            int? left = await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false);
            if (left >= MinFreshSeconds)
            {
                SetSession(EngieSessionState.Online, "");
                Trace?.Invoke("engie: Okta sign-in completed");
                return true;
            }

            try
            {
                await b.EvaluateAsync(SharePointApi.AutoStepJs, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // page navigating / no document yet - retry on next poll.
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Trace?.Invoke("engie: auto step failed: " + ex.Message);
            }
        }
        Trace?.Invoke("engie: engie.ai did not finish signing in within 180 s");
        return false;
    }
}
