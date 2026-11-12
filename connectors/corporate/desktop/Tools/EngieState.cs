// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.7.11: cached engie.ai session state for the CO Web "ENGIE" toggle.
using CO.Core.Browser;

namespace CO.Corporate.Tools;

public static partial class EngieTools
{
    // 0.7.11: session state reported to CO Web in the "status" web message.
    public enum EngieSessionState { Unknown, Online, SignInNeeded, Offline }

    // 0.7.11: cached state, guarded by _sessionLock.
    private static readonly object _sessionLock = new();
    private static EngieSessionState _sessionState = EngieSessionState.Unknown;
    private static string _sessionDetail = "";
    private static DateTime _sessionCheckedUtc = DateTime.MinValue;

    // 0.7.11: 1 while a RefreshSessionAsync probe is running (Interlocked).
    private static int _refreshing;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OpenPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BringToFrontTimeout = TimeSpan.FromSeconds(10);

    // 0.7.11: last known engie.ai session state.
    public static (EngieSessionState State, string Detail, DateTime CheckedUtc) Session
    {
        get
        {
            lock (_sessionLock)
                return (_sessionState, _sessionDetail, _sessionCheckedUtc);
        }
    }

    // 0.8.2: current Okta Verify number challenge for the heartbeat (Bridge pushes it to the owner's phone).
    private static readonly object _oktaNumLock = new();
    private static string _oktaNumId = "";      // changes per sign-in attempt so each number pushes once
    private static string _oktaNumValue = "";   // the number the user types in Okta Verify
    private static DateTime _oktaNumUtc = DateTime.MinValue;
    private static readonly TimeSpan OktaNumberValid = TimeSpan.FromMinutes(2);

    // 0.8.2: set ("<id>:<NN>") while a push with a number is waiting, "" to clear.
    internal static void SetOktaNumber(string id, string number)
    {
        lock (_oktaNumLock)
        {
            _oktaNumId = id ?? "";
            _oktaNumValue = number ?? "";
            _oktaNumUtc = string.IsNullOrEmpty(number) ? DateTime.MinValue : DateTime.UtcNow;
        }
    }

    // 0.8.2: heartbeat value "<id>:<NN>" while fresh, else "".
    public static string EngieOktaNumber()
    {
        lock (_oktaNumLock)
        {
            if (_oktaNumValue.Length == 0 || DateTime.UtcNow - _oktaNumUtc > OktaNumberValid) return "";
            return _oktaNumId + ":" + _oktaNumValue;
        }
    }

    // 0.7.11: update the cached state (also called from the token / keep-alive code).
    internal static void SetSession(EngieSessionState s, string detail)
    {
        bool changed;
        lock (_sessionLock)
        {
            changed = _sessionState != s;
            _sessionState = s;
            _sessionDetail = detail ?? "";
            _sessionCheckedUtc = DateTime.UtcNow;
        }
        if (changed)
            Trace?.Invoke($"engie: session state {s}{(string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")}");
    }

    // 0.7.11: light probe without navigating. Skipped while a tool call is in flight
    // or another probe is running (the cached state is returned then).
    public static async Task<EngieSessionState> RefreshSessionAsync(IBrowser b, CancellationToken ct)
    {
        if (Volatile.Read(ref _inFlight) > 0)
            return Session.State;
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return Session.State;

        try
        {
            string here = await b.CurrentUrlAsync(ct).ConfigureAwait(false);
            if (!OnEngie(here))
            {
                SetSession(EngieSessionState.Offline, "engie.ai is not open in the CO browser");
                return EngieSessionState.Offline;
            }

            if (await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false) is int left && left >= MinFreshSeconds)
            {
                SetSession(EngieSessionState.Online, "");
                return EngieSessionState.Online;
            }

            SetSession(EngieSessionState.SignInNeeded, "engie.ai needs sign-in");
            return EngieSessionState.SignInNeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetSession(EngieSessionState.Offline, ex.Message);
            return EngieSessionState.Offline;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    // 0.7.11: the user pressed the ENGIE toggle in CO Web. Opens/reloads engie.ai in the
    // CO browser tab, waits briefly for a fresh token, brings the tab to the front if a
    // sign-in is needed.
    public static async Task<EngieSessionState> OpenAsync(IBrowser b, CancellationToken ct)
    {
        if (Volatile.Read(ref _inFlight) > 0)
            return Session.State;

        try
        {
            Trace?.Invoke("engie: opening engie.ai (ENGIE toggle)");
            await b.NavigateAsync(Origin + "/chat", ct).ConfigureAwait(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < OpenTimeout)
            {
                await Task.Delay(OpenPollInterval, ct).ConfigureAwait(false);
                if (await TryGetSecondsLeftAsync(b, ct).ConfigureAwait(false) is int left && left >= MinFreshSeconds)
                {
                    SetSession(EngieSessionState.Online, "");
                    return EngieSessionState.Online;
                }
            }

            // 0.7.11: no fresh token - show the CO browser window so the user can sign in.
            try
            {
                using var frontCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                frontCts.CancelAfter(BringToFrontTimeout);
                await b.BringToFrontAsync(frontCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace?.Invoke($"engie: bring to front failed: {ex.GetType().Name}: {ex.Message}");
            }

            SetSession(EngieSessionState.SignInNeeded, "sign in to engie.ai in the CO browser window");
            return EngieSessionState.SignInNeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetSession(EngieSessionState.Offline, ex.Message);
            return EngieSessionState.Offline;
        }
    }
}
