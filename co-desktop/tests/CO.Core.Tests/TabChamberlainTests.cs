// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Desktop 0.9.9: agent browser settings of the chat (unsigned "browser" field of a device call) and the tab
// chamberlain (several tabs per chat, same-site reuse, limit with LRU, lifetimes per state, captcha keep, pinned lock).

using System.Text;
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Protocol;
using CO.Core.Tools;
using Xunit;

namespace CO.Core.Tests;

public class TabChamberlainTests
{
    private static AgentTabRules Rules(string json) => AgentTabRules.Parse(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Rules_MissingOrBrokenTakeDefaults_ValuesAreClamped()
    {
        Assert.Equal(AgentTabRules.Default, AgentTabRules.Parse(null));
        Assert.Equal(new AgentTabRules(0, 120, 360, true), AgentTabRules.Default);
        Assert.Equal(AgentTabRules.Default, Rules("{}"));
        Assert.Equal(AgentTabRules.Default, Rules("[1,2]"));
        Assert.Equal(AgentTabRules.Default, Rules("null"));
        Assert.Equal(AgentTabRules.Default, Rules("{\"tabs\":\"x\",\"idleMin\":true,\"formIdleMin\":{},\"captchaKeep\":\"maybe\"}"));
        Assert.Equal(new AgentTabRules(20, 5, 2880, false),
            Rules("{\"tabs\":50,\"idleMin\":1,\"formIdleMin\":99999,\"captchaKeep\":false}"));
        // formIdleMin never below idleMin (also when it is missing and its default is lower).
        Assert.Equal(new AgentTabRules(0, 600, 600, true), Rules("{\"tabs\":-3,\"idleMin\":600}"));
        Assert.Equal(new AgentTabRules(0, 120, 120, true), Rules("{\"formIdleMin\":30}"));
        Assert.Equal(new AgentTabRules(1, 1440, 1440, false), Rules("{\"tabs\":\"1\",\"idleMin\":1e9,\"formIdleMin\":-1e30,\"captchaKeep\":0}"));
        Assert.Equal(new AgentTabRules(3, 120, 360, true), Rules("{\"tabs\":2.6,\"captchaKeep\":\"1\"}"));
        Assert.Equal(TimeSpan.FromMinutes(120), AgentTabRules.Default.Idle);
        Assert.Equal(TimeSpan.FromMinutes(360), AgentTabRules.Default.FormIdle);
    }

    [Fact]
    public void ToolCall_BrowserFieldIsUnsignedAndReachesToolEnv()
    {
        using var f = new Fixture();
        string json = "{\"v\":1,\"callId\":\"c1\",\"runId\":\"JOB-1\",\"taskId\":\"JOB-1\",\"deviceId\":\"d\",\"tool\":\"browser_status\","
            + "\"argsJson\":\"{}\",\"issuedUtc\":\"2026-11-09T10:00:00Z\",\"tainted\":false,\"timeoutMs\":120000,\"chatId\":\"CHAT-1\","
            + "\"browser\":{\"tabs\":1,\"idleMin\":30,\"formIdleMin\":90,\"captchaKeep\":false}}";
        var call = Wire.Deserialize<ToolCall>(json);
        var plain = Wire.Deserialize<ToolCall>(json.Replace(",\"browser\":{\"tabs\":1,\"idleMin\":30,\"formIdleMin\":90,\"captchaKeep\":false}", ""));
        Assert.Equal(Encoding.UTF8.GetString(plain.SigningPayload()), Encoding.UTF8.GetString(call.SigningPayload()));
        var env = new ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-1", Call = call };
        Assert.Equal(new AgentTabRules(1, 30, 90, false), env.Browser);
        Assert.Equal(AgentTabRules.Default, new ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-1", Call = plain }.Browser);
        Assert.Equal(AgentTabRules.Default, new ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-1" }.Browser);
        // A broken value never fails the call.
        var broken = Wire.Deserialize<ToolCall>(json.Replace("{\"tabs\":1,\"idleMin\":30,\"formIdleMin\":90,\"captchaKeep\":false}", "\"lots\""));
        Assert.Equal(AgentTabRules.Default, new ToolEnv { Folders = f.Folders, Snapshots = f.Snapshots, RunId = "JOB-1", Call = broken }.Browser);
    }

    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static TabCandidate T(string key, string owner, string? site, int minutesAgo, bool typed = false, bool captcha = false,
        bool paused = false, bool busy = false, bool pinned = false) =>
        new(key, owner, site, typed, captcha, paused, busy, pinned, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void SiteOf_HostWithoutWww_BlankIsNull()
    {
        Assert.Equal("example.com", TabChamberlain.SiteOf("https://WWW.Example.com./jobs?id=1"));
        Assert.Equal("jobs.example.com", TabChamberlain.SiteOf("http://jobs.example.com"));
        Assert.Null(TabChamberlain.SiteOf("about:blank"));
        Assert.Null(TabChamberlain.SiteOf(null));
        Assert.Null(TabChamberlain.SiteOf("not a url"));
    }

    [Fact]
    public void Choose_SameSiteOwnTab_ThenFreeTabOfSite_ThenOwnBlank_ThenNew()
    {
        var unlimited = AgentTabRules.Default;
        var tabs = new List<TabCandidate>
        {
            T("a:1", "A", "jobs.example", 30),
            T("a:2", "A", "jobs.example", 3),            // most recent own tab of the site
            T("a:3", "A", "news.example", 1),
            T("b:1", "B", "shop.example", 60),           // free (idle, clean)
            T("c:1", "C", "mail.example", 60, typed: true),
        };
        Assert.Equal("a:2", TabChamberlain.Choose(tabs, "A", "jobs.example", unlimited, Now));
        Assert.Equal("b:1", TabChamberlain.Choose(tabs, "A", "shop.example", unlimited, Now));
        // Typed data: another chat's tab is not free.
        Assert.Null(TabChamberlain.Choose(tabs, "A", "mail.example", unlimited, Now));
        // New site, no blank own tab: a new tab.
        Assert.Null(TabChamberlain.Choose(tabs, "A", "other.example", unlimited, Now));
        // Own blank tab (e.g. opened by browser_snapshot before any navigate) is used before a new one.
        tabs.Add(T("a:4", "A", null, 2));
        Assert.Equal("a:4", TabChamberlain.Choose(tabs, "A", "other.example", unlimited, Now));
        // Blank address: never matches a site, takes the blank tab.
        Assert.Equal("a:4", TabChamberlain.Choose(tabs, "A", null, unlimited, Now));
    }

    [Fact]
    public void Choose_FreeTabNeedsIdleCleanNotBusyNotPinned()
    {
        var r = AgentTabRules.Default;
        string? Pick(TabCandidate other) => TabChamberlain.Choose(new[] { other }, "A", "site.example", r, Now);
        Assert.Equal("b", Pick(T("b", "B", "site.example", 6)));
        Assert.Null(Pick(T("b", "B", "site.example", 2)));                      // its job may still run
        Assert.Null(Pick(T("b", "B", "site.example", 60, busy: true)));
        Assert.Null(Pick(T("b", "B", "site.example", 60, captcha: true)));
        Assert.Null(Pick(T("b", "B", "site.example", 60, paused: true)));
        Assert.Null(Pick(T("b", "B", "site.example", 60, pinned: true)));
        Assert.Null(Pick(T("b", "B", "site.example", 60, typed: true)));
        Assert.True(TabChamberlain.IsFree(T("b", "B", "x", 5), Now));
    }

    [Fact]
    public void Choose_AtLimit_ReusesLeastRecentlyUsed_PreferringCleanTabs()
    {
        var two = AgentTabRules.Default with { Tabs = 2 };
        var tabs = new List<TabCandidate>
        {
            T("a:1", "A", "one.example", 50, typed: true),    // oldest, but holds typed data
            T("a:2", "A", "two.example", 10),
            T("b:1", "B", "three.example", 60),               // free, but A is at its limit
        };
        Assert.Equal("a:2", TabChamberlain.Choose(tabs, "A", "three.example", two, Now));
        // Same site still wins at the limit.
        Assert.Equal("a:1", TabChamberlain.Choose(tabs, "A", "one.example", two, Now));
        // Under the limit: a new tab.
        Assert.Null(TabChamberlain.Choose(tabs, "A", "four.example", AgentTabRules.Default with { Tabs = 3 }, Now));
        // All own tabs have typed data or a captcha: the least recently used of them.
        tabs[1] = T("a:2", "A", "two.example", 10, captcha: true);
        Assert.Equal("a:1", TabChamberlain.Choose(tabs, "A", "four.example", two, Now));
        // A tab busy with another call of the chat is taken last.
        var busy = new[] { T("a:1", "A", "one.example", 50, busy: true), T("a:2", "A", "two.example", 10) };
        Assert.Equal("a:2", TabChamberlain.Choose(busy, "A", "x.example", two, Now));
        // a member with tabs = 1, always the one tab.
        var one = AgentTabRules.Default with { Tabs = 1 };
        Assert.Equal("a:1", TabChamberlain.Choose(new[] { T("a:1", "A", "one.example", 1, typed: true) }, "A", "two.example", one, Now));
    }

    [Fact]
    public void Choose_PinnedOrPausedTabsAreNeverSentToAnotherSite()
    {
        var one = AgentTabRules.Default with { Tabs = 1 };
        var pinned = new[] { T("a:1", "A", "one.example", 50, pinned: true) };
        // Pinned tabs do not count against the limit: a new tab for another site, the pinned one for its own site.
        Assert.Null(TabChamberlain.Choose(pinned, "A", "two.example", one, Now));
        Assert.Equal("a:1", TabChamberlain.Choose(pinned, "A", "one.example", one, Now));
        var paused = new[] { T("a:1", "A", "one.example", 50, paused: true) };
        Assert.Null(TabChamberlain.Choose(paused, "A", "two.example", one, Now));
    }

    [Fact]
    public void IdleLimit_PerState()
    {
        var r = new AgentTabRules(0, 30, 90, true);
        Assert.Equal(TimeSpan.FromMinutes(30), TabChamberlain.IdleLimit(new TabState("https://a", false, false, false), r));
        Assert.Equal(TimeSpan.FromMinutes(90), TabChamberlain.IdleLimit(new TabState("https://a", true, false, false), r));
        Assert.Null(TabChamberlain.IdleLimit(new TabState("https://a", true, true, false), r));
        Assert.Equal(TimeSpan.FromMinutes(90), TabChamberlain.IdleLimit(new TabState("https://a", true, true, false), r with { CaptchaKeep = false }));
        Assert.Null(TabChamberlain.IdleLimit(new TabState("https://a", false, false, true), r));
    }

    private sealed class FakeTab(string key)
    {
        public string Key { get; } = key;
        public string? Url;
        public bool Typed, Captcha, Paused;
    }

    [Fact]
    public async Task RunTabs_SeveralTabsPerChat_CurrentTab_LifetimesAndPinned()
    {
        var closed = new List<string>();
        var pinnedKeys = new HashSet<string>();
        using var tabs = new RunTabs<FakeTab>("agent-web", k => new FakeTab(k),
            (key, _, _) => { lock (closed) closed.Add(key); return Task.CompletedTask; },
            keep: t => t.Paused,
            state: t => new TabState(t.Url, t.Typed, t.Captcha, t.Paused),
            pinned: _ => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>(pinnedKeys)));
        var rules = new AgentTabRules(0, 30, 90, true);

        FakeTab jobs, news;
        using (var l = await tabs.NavigateAsync("chat-A", "https://jobs.example/apply", rules, default)) { jobs = l.Value; jobs.Url = "https://jobs.example/apply"; }
        Assert.Equal("agent-web:chat-A", jobs.Key);   // the 0.9.8 key for the first tab
        using (var l = await tabs.NavigateAsync("chat-A", "https://news.example/", rules, default)) { news = l.Value; news.Url = "https://news.example/"; }
        Assert.Equal("agent-web:chat-A#2", news.Key);
        // The other tools work in the tab of the last navigate; status lists both, the current one first.
        using (var l = tabs.Current("chat-A")) Assert.Same(news, l.Value);
        using (var l = await tabs.NavigateAsync("chat-A", "https://www.jobs.example/next", rules, default)) Assert.Same(jobs, l.Value);
        Assert.Same(jobs, tabs.FindCurrent("chat-A"));
        var owned = await tabs.TabsOfAsync("chat-A", default);
        Assert.Equal(new[] { "agent-web:chat-A", "agent-web:chat-A#2" }, owned.Select(t => t.Key));
        Assert.True(owned[0].Current && !owned[1].Current);
        Assert.Null(tabs.FindCurrent("chat-B"));
        Assert.Empty(await tabs.TabsOfAsync("chat-B", default));

        // Limit 1 (a member): a third site reuses the least recently used tab of the chat.
        using (var l = await tabs.NavigateAsync("chat-A", "https://third.example/", rules with { Tabs = 1 }, default)) Assert.Same(news, l.Value);
        Assert.Equal(2, tabs.Count);

        // Lifetimes: 31 min idle closes a clean tab, typed data keeps it until 90 min, a captcha forever.
        jobs.Typed = true;
        var now = DateTime.UtcNow;
        Assert.Equal(1, await tabs.SweepAsync(default, now.AddMinutes(31)));
        Assert.Equal(new[] { "agent-web:chat-A#2" }, closed);
        jobs.Captcha = true;
        Assert.Equal(0, await tabs.SweepAsync(default, now.AddDays(3)));
        // Pinned by the user: never closed, even without the captcha.
        jobs.Captcha = false;
        pinnedKeys.Add(jobs.Key);
        Assert.Equal(0, await tabs.SweepAsync(default, now.AddDays(3)));
        pinnedKeys.Clear();
        Assert.Equal(1, await tabs.SweepAsync(default, now.AddMinutes(91)));
        Assert.Equal(0, tabs.Count);
        Assert.Null(tabs.FindCurrent("chat-A"));

        // captchaKeep = false: a captcha tab closes after idleMin like any other; the per-Run Use still works.
        using (var l = await tabs.NavigateAsync("chat-A", "https://c.example/", rules with { CaptchaKeep = false }, default)) { l.Value.Url = "https://c.example/"; l.Value.Captcha = true; }
        Assert.Equal(0, await tabs.SweepAsync(default, DateTime.UtcNow.AddMinutes(29)));
        Assert.Equal(1, await tabs.SweepAsync(default, DateTime.UtcNow.AddMinutes(31)));
        using (var l = tabs.Use("JOB-7")) Assert.Equal("agent-web:JOB-7", l.Value.Key);
        using (var l = tabs.Current("JOB-7")) Assert.Equal("agent-web:JOB-7", l.Value.Key);
    }
}
