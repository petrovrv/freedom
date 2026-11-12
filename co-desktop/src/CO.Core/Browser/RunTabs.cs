// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace CO.Core.Browser;

// 0.9.0: one CO browser tab per job (Run). Keys are "<prefix>:<RunId>" (agent-web:<RunId> for the agent browser,
// agent:<RunId> for browser_open/browser_read and connector tools by link); the tab is created by the job's first call,
// lives in co-tabs.json like the service tabs, and is closed after IdleTimeout without calls. A tab whose holder says
// "keep" (the user took it over or must finish a verification step) is not closed automatically. Tabs older than
// 24 h left over from an earlier CO Desktop session are closed at start (EdgeBrowser.CloseStaleTabsAsync).
// 0.9.9: tab chamberlain for the agent browser (same rules as browser-agent, see TabChamberlain): an owner (chat or
// Run) may hold several tabs, keys "<prefix>:<owner>", "<prefix>:<owner>#2", ...; NavigateAsync picks the tab for an
// address, Current gives the owner's tab of its last navigate to the other calls. With a state callback each tab
// closes per the rules of its last call (idle / typed data / pending captcha); a tab pinned in the browser (pinned
// callback) is never closed.
public sealed class RunTabs<T> : IDisposable where T : class
{
    // 0.9.8: 2 h (was 30 min): a half-filled application form waits for the user or the next turn of the chat.
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromHours(2);

    private sealed class Entry(string key, string owner, T value)
    {
        public string Key { get; } = key;
        public T Value { get; } = value;
        public long LastUsedTicks = DateTime.UtcNow.Ticks;
        public int Busy;
        // 0.9.9: the owner holding the tab (changes under _lock when another owner adopts a free tab) and the settings
        // of the last call that used it (null: IdleTimeout applies).
        public string Owner = owner;
        public AgentTabRules? Rules;
        // Set under _lock when the sweep decides to close the tab; a call for the same key waits for Closed
        // and then gets a fresh holder, so the sweep can never close a tab a call is using (Codex review 0.9.0).
        public bool Closing;
        public readonly ManualResetEventSlim Closed = new(false);
    }

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PinnedWait = TimeSpan.FromSeconds(3);
    private readonly object _lock = new();

    private readonly string _prefix;
    private readonly Func<string, T> _create;
    private readonly Func<string, T, CancellationToken, Task> _close;
    private readonly Func<T, bool>? _keep;
    private readonly Func<T, TabState>? _state;
    private readonly Func<CancellationToken, Task<IReadOnlySet<string>?>>? _pinned;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    // 0.9.9: owner -> key of the tab its last navigate chose (browser_snapshot, click, ... work there). Under _lock.
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly Timer? _timer;
    private int _sweeping;

    public TimeSpan IdleTimeout { get; set; }

    /// <param name="prefix">Key prefix, e.g. "agent-web".</param>
    /// <param name="create">Creates the holder for a tab key.</param>
    /// <param name="close">Closes the tab of a key (only CO-owned tabs).</param>
    /// <param name="keep">True while the tab must not be closed automatically (e.g. paused by the user).</param>
    /// <param name="sweepEvery">Timer period; null = no timer (call SweepAsync yourself).</param>
    /// <param name="state">0.9.9: address, typed data, captcha and pause of a holder (chamberlain and lifetimes).</param>
    /// <param name="pinned">0.9.9: keys of the tabs the user pinned in the browser; a null result = unknown.</param>
    public RunTabs(string prefix, Func<string, T> create, Func<string, T, CancellationToken, Task> close,
        Func<T, bool>? keep = null, TimeSpan? idle = null, TimeSpan? sweepEvery = null, Action<string>? log = null,
        Func<T, TabState>? state = null, Func<CancellationToken, Task<IReadOnlySet<string>?>>? pinned = null)
    {
        _prefix = prefix;
        _create = create;
        _close = close;
        _keep = keep;
        _state = state;
        _pinned = pinned;
        _log = log;
        IdleTimeout = idle ?? DefaultIdle;
        if (sweepEvery is { } period)
            _timer = new Timer(_ => _ = SweepAsync(CancellationToken.None), null, period, period);
    }

    public string Prefix => _prefix;

    public string KeyOf(string? runId) => _prefix + ":" + OwnerOf(runId);

    private static string OwnerOf(string? owner) => string.IsNullOrWhiteSpace(owner) ? "default" : owner.Trim();

    // True for keys of this registry, and for the single 0.8.x key "<prefix>" (closed at start, no longer used).
    public bool OwnsKey(string key) =>
        key == _prefix || key.StartsWith(_prefix + ":", StringComparison.Ordinal);

    /// <summary>The job's holder (created on first use) for one call; dispose the lease when the call ends.</summary>
    public Lease Use(string? runId)
    {
        var key = KeyOf(runId);
        while (true)
        {
            Entry e;
            lock (_lock)
            {
                e = _entries.GetOrAdd(key, k => new Entry(k, OwnerOf(runId), _create(k)));
                if (!e.Closing) return LeaseLocked(e, null);
            }
            // The idle tab of this job is being closed right now: wait until the close has finished (bounded by
            // the CDP timeouts of the close; the sweep removes the entry before Closed is set), then start with a
            // fresh holder.
            while (!e.Closed.Wait(CloseWait)) _log?.Invoke($"[run-tabs] still waiting for {key} to close");
        }
    }

    /// <summary>
    /// 0.9.9: the tab for browser_navigate to <paramref name="url"/> chosen by the chamberlain (TabChamberlain.Choose):
    /// this owner's tab of the site, a free tab of the site, a new tab while under the limit, else the owner's least
    /// recently used tab. It becomes the owner's current tab.
    /// </summary>
    public async Task<Lease> NavigateAsync(string? owner, string? url, AgentTabRules rules, CancellationToken ct)
    {
        var o = OwnerOf(owner);
        var pinned = await PinnedAsync(ct);
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var tabs = _entries.Values.Where(e => !e.Closing).Select(e => CandidateLocked(e, pinned)).ToList();
            var key = TabChamberlain.Choose(tabs, o, TabChamberlain.SiteOf(url), rules, now);
            Entry e;
            if (key != null && _entries.TryGetValue(key, out var found) && !found.Closing)
            {
                e = found;
                if (e.Owner != o)
                {
                    _log?.Invoke($"[run-tabs] {o} takes the free tab {key} of {e.Owner}");
                    e.Owner = o;
                }
            }
            else e = NewLocked(o);
            _current[o] = e.Key;
            return LeaseLocked(e, rules);
        }
    }

    /// <summary>0.9.9: the owner's current tab for one call (a new tab when it holds none).</summary>
    public Lease Current(string? owner, AgentTabRules? rules = null)
    {
        var o = OwnerOf(owner);
        lock (_lock)
        {
            var e = CurrentLocked(o) ?? NewLocked(o);
            _current[o] = e.Key;
            return LeaseLocked(e, rules);
        }
    }

    /// <summary>0.9.9: the owner's current tab if it holds one; never creates one.</summary>
    public T? FindCurrent(string? owner)
    {
        lock (_lock) return CurrentLocked(OwnerOf(owner))?.Value;
    }

    /// <summary>0.9.9: tabs held by the owner, the current one first.</summary>
    public async Task<IReadOnlyList<OwnedTab>> TabsOfAsync(string? owner, CancellationToken ct)
    {
        var o = OwnerOf(owner);
        var pinned = await PinnedAsync(ct);
        lock (_lock)
        {
            var current = CurrentLocked(o);
            return _entries.Values.Where(e => !e.Closing && e.Owner == o)
                .OrderByDescending(e => ReferenceEquals(e, current)).ThenByDescending(LastUsed)
                .Select(e => new OwnedTab(e.Key, e.Value, ReferenceEquals(e, current), LastUsed(e),
                    pinned?.Contains(e.Key) == true, e.Rules ?? AgentTabRules.Default))
                .ToList();
        }
    }

    /// <summary>The job's holder if its tab was opened in this session; never creates one.</summary>
    public T? Find(string? runId) => _entries.TryGetValue(KeyOf(runId), out var e) ? e.Value : null;

    public int Count => _entries.Count;

    private static DateTime LastUsed(Entry e) => new(Interlocked.Read(ref e.LastUsedTicks), DateTimeKind.Utc);

    private Lease LeaseLocked(Entry e, AgentTabRules? rules)
    {
        e.Busy++;
        if (rules != null) e.Rules = rules;
        Interlocked.Exchange(ref e.LastUsedTicks, DateTime.UtcNow.Ticks);
        return new Lease(e.Value, () =>
        {
            Interlocked.Exchange(ref e.LastUsedTicks, DateTime.UtcNow.Ticks);
            lock (_lock) e.Busy--;
        });
    }

    private Entry? CurrentLocked(string owner)
    {
        if (_current.TryGetValue(owner, out var k) && _entries.TryGetValue(k, out var e) && !e.Closing && e.Owner == owner)
            return e;
        return _entries.Values.Where(x => !x.Closing && x.Owner == owner).OrderByDescending(LastUsed).FirstOrDefault();
    }

    // First free key of the owner: "<prefix>:<owner>" (the 0.9.8 key, so a tab of an earlier session is reattached),
    // then "#2", "#3", ...
    private Entry NewLocked(string owner)
    {
        var first = KeyOf(owner);
        var key = first;
        for (int n = 2; _entries.ContainsKey(key); n++) key = first + "#" + n;
        var e = new Entry(key, owner, _create(key));
        _entries[key] = e;
        return e;
    }

    private TabCandidate CandidateLocked(Entry e, IReadOnlySet<string>? pinned)
    {
        TabState st = default;
        if (_state != null)
        {
            try { st = _state(e.Value); }
            catch { st = new TabState(null, true, false, true); }   // unknown: never adopted, reused last
        }
        return new TabCandidate(e.Key, e.Owner, TabChamberlain.SiteOf(st.Url), st.Typed, st.Captcha, st.Paused,
            e.Busy > 0, pinned?.Contains(e.Key) == true, LastUsed(e));
    }

    private async Task<IReadOnlySet<string>?> PinnedAsync(CancellationToken ct)
    {
        if (_pinned is null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PinnedWait);
            return await _pinned(cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke("[run-tabs] pinned tabs unknown: " + ex.Message);
            return null;
        }
    }

    // Under _lock: idle for longer than its limit (rules and state, or IdleTimeout), not busy, not kept.
    private bool ExpiredLocked(Entry e, DateTime now)
    {
        if (e.Closing || e.Busy > 0) return false;
        TimeSpan? limit = IdleTimeout;
        if (_state != null)
        {
            TabState st;
            try { st = _state(e.Value); }
            catch { return false; }
            limit = TabChamberlain.IdleLimit(st, e.Rules ?? AgentTabRules.Default);
        }
        if (limit is null || now - LastUsed(e) < limit) return false;
        bool keep;
        try { keep = _keep?.Invoke(e.Value) == true; }
        catch { keep = true; }
        return !keep;
    }

    /// <summary>Closes tabs idle for their limit (not busy, not kept, not pinned). Returns the number closed.</summary>
    public async Task<int> SweepAsync(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return 0;
        int closed = 0;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            IReadOnlySet<string>? pinned = null;
            bool pinnedRead = false;
            foreach (var (key, e) in _entries)
            {
                lock (_lock)
                    if (!ExpiredLocked(e, now)) continue;
                // 0.9.9: the pinned list only when something is due (one browser query per sweep at most).
                if (!pinnedRead)
                {
                    pinned = await PinnedAsync(ct);
                    pinnedRead = true;
                }
                if (pinned?.Contains(key) == true) continue;
                // Decided under _lock: no call holds the tab, and new calls for the key wait until it is closed.
                lock (_lock)
                {
                    if (!ExpiredLocked(e, now)) continue;
                    e.Closing = true;
                }
                try
                {
                    await _close(key, e.Value, ct);
                    closed++;
                    _log?.Invoke($"[run-tabs] closed idle tab {key}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Invoke($"[run-tabs] closing {key} failed: {ex.Message}");
                }
                finally
                {
                    lock (_lock)
                    {
                        ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new KeyValuePair<string, Entry>(key, e));
                        if (_current.TryGetValue(e.Owner, out var cur) && cur == key) _current.Remove(e.Owner);
                    }
                    e.Closed.Set();
                }
            }
        }
        finally { Volatile.Write(ref _sweeping, 0); }
        return closed;
    }

    public void Dispose() => _timer?.Dispose();

    public sealed record OwnedTab(string Key, T Value, bool Current, DateTime LastUsedUtc, bool Pinned, AgentTabRules Rules);

    public sealed class Lease : IDisposable
    {
        private Action? _release;
        internal Lease(T value, Action release) { Value = value; _release = release; }
        public T Value { get; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>0.9.9: what the chamberlain knows about a holder: address, typed data, unsolved captcha, user pause.</summary>
public readonly record struct TabState(string? Url, bool Typed, bool Captcha, bool Paused);

/// <summary>0.9.9: one tab as the chamberlain sees it (Site = host without "www.", null for a blank page).</summary>
public sealed record TabCandidate(string Key, string Owner, string? Site, bool Typed, bool Captcha, bool Paused,
    bool Busy, bool Pinned, DateTime LastUsedUtc);

// 0.9.9: tab rules shared with browser-agent (contract 09.11.2026). Pure functions, unit-tested.
public static class TabChamberlain
{
    // A tab of another owner counts as free (its job finished) after this long without calls.
    public static readonly TimeSpan FreeAfter = TimeSpan.FromMinutes(5);

    public static string? SiteOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0)
            return null;
        var h = u.Host.ToLowerInvariant().TrimEnd('.');
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    // Not held by a call, nothing typed, no captcha, not paused or pinned by the user, unused for FreeAfter.
    public static bool IsFree(TabCandidate t, DateTime nowUtc) =>
        !t.Busy && !t.Typed && !t.Captcha && !t.Paused && !t.Pinned && nowUtc - t.LastUsedUtc >= FreeAfter;

    /// <summary>
    /// Key of the tab a navigate of <paramref name="owner"/> to <paramref name="site"/> uses, or null for a new tab:
    /// 1. the owner's tab on the site (the most recent; a pinned one too, it stays on its site);
    /// 2. while the owner holds fewer than rules.Tabs tabs (0 = unlimited): a free tab of another owner on the site,
    ///    then the owner's own blank tab, else a new tab (adoption counts against the limit too);
    /// 3. the owner's least recently used tab, preferring one without typed data and without a pending captcha;
    ///    pinned and paused tabs are never sent to another site (a new tab then, over the limit).
    /// Tabs pinned in the browser do not count against the limit.
    /// </summary>
    public static string? Choose(IReadOnlyList<TabCandidate> tabs, string owner, string? site, AgentTabRules rules, DateTime nowUtc)
    {
        var own = tabs.Where(t => t.Owner == owner).ToList();
        if (site != null && own.Where(t => t.Site == site).MaxBy(t => t.LastUsedUtc) is { } same)
            return same.Key;
        bool room = rules.Tabs == 0 || own.Count(t => !t.Pinned) < rules.Tabs;
        if (room)
        {
            if (site != null && tabs.Where(t => t.Owner != owner && t.Site == site && IsFree(t, nowUtc)).MaxBy(t => t.LastUsedUtc) is { } free)
                return free.Key;
            return own.Where(t => t.Site == null && !t.Typed && !t.Captcha && !t.Paused && !t.Pinned && !t.Busy)
                .MaxBy(t => t.LastUsedUtc)?.Key;
        }
        return own.Where(t => !t.Pinned && !t.Paused)
            .OrderBy(t => t.Typed || t.Captcha).ThenBy(t => t.Busy).ThenBy(t => t.LastUsedUtc)
            .FirstOrDefault()?.Key;
    }

    /// <summary>How long a tab may stay without actions: null = never (pending captcha with CaptchaKeep, or paused).</summary>
    public static TimeSpan? IdleLimit(TabState st, AgentTabRules rules) =>
        st.Paused || (st.Captcha && rules.CaptchaKeep) ? null : st.Typed ? rules.FormIdle : rules.Idle;
}
