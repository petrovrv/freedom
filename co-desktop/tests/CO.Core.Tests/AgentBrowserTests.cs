// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for the agent browser (CO Desktop 0.8.0): URL guard, settings store, connector, ref parsing,
// plus opt-in live tests in a headless Chromium (LiveBrowser.cs: first on the target device, on the home PC only with the owner's yes).
// IP addresses are always built at runtime and never written as literals.

using System.Text.RegularExpressions;
using CO.Core.Browser;
using CO.Core.Connectors;
using CO.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CO.Core.Tests;

public class AgentBrowserTests
{
    private readonly ITestOutputHelper output;

    public AgentBrowserTests(ITestOutputHelper output) => this.output = output;

    // Builds a dotted IPv4 address from its octets.
    static string Ip(params int[] o) => string.Join(".", o);

    // Builds an IPv6 address from its groups (empty groups produce "::").
    static string Ip6(params string[] groups) => string.Join(":", groups);

    // Builds a root URL for a scheme and host.
    static string U(string scheme, string host) => scheme + "://" + host + "/";

    [Fact]
    public void IsAlwaysBlockedHost_BlocksLocalAndPrivateHosts()
    {
        var loopback6 = Ip6("", "", "1");
        var blocked = new[]
        {
            "localhost",
            "a.localhost",
            "printer.local",
            Ip(127, 0, 0, 1),
            Ip(10, 1, 2, 3),
            Ip(172, 16, 0, 1),
            Ip(172, 31, 255, 255),
            Ip(192, 168, 1, 1),
            Ip(169, 254, 0, 1),
            Ip(0, 0, 0, 0),
            "[" + loopback6 + "]",
            loopback6,
            Ip6("fd00", "", "1"),
            Ip6("fe80", "", "1"),
            Ip6("", "", "ffff", Ip(127, 0, 0, 1)),
        };
        foreach (var host in blocked)
            Assert.True(AgentWebGuard.IsAlwaysBlockedHost(host), $"expected blocked: {host}");

        var open = new[] { "example.com", Ip(8, 8, 8, 8), Ip(172, 32, 0, 1), Ip(11, 0, 0, 1) };
        foreach (var host in open)
            Assert.False(AgentWebGuard.IsAlwaysBlockedHost(host), $"expected not blocked: {host}");
    }

    [Fact]
    public void HostBlocked_MatchesDomainsAndSubdomains()
    {
        Assert.True(AgentWebGuard.HostBlocked("example.com", new[] { "example.com" }));
        Assert.True(AgentWebGuard.HostBlocked("sub.example.com", new[] { "example.com" }));
        Assert.True(AgentWebGuard.HostBlocked("a.b.EXAMPLE.com.", new[] { "example.com" }));
        Assert.True(AgentWebGuard.HostBlocked("x.foo.org", new[] { "*.foo.org" }));
        Assert.False(AgentWebGuard.HostBlocked("badexample.com", new[] { "example.com" }));
        Assert.False(AgentWebGuard.HostBlocked("example.com", new[] { "sub.example.com" }));
        Assert.False(AgentWebGuard.HostBlocked("example.com", Array.Empty<string>()));
        Assert.False(AgentWebGuard.HostBlocked("example.com", null));
    }

    [Fact]
    public void CheckDocumentUrl_AllowsPublicSitesExceptBlocked()
    {
        var block = new[] { "blocked.example" };
        // 0.9.0: empty blocklist = every public site.
        Assert.Null(AgentWebGuard.CheckDocumentUrl(U("https", "example.com"), Array.Empty<string>()));
        Assert.Null(AgentWebGuard.CheckDocumentUrl(U("https", "other.org"), block));
        Assert.Null(AgentWebGuard.CheckDocumentUrl(U("http", Ip(8, 8, 8, 8)), block));
        Assert.Equal(AgentWebGuard.BlockedListReason, AgentWebGuard.CheckDocumentUrl(U("https", "blocked.example"), block));
        Assert.Equal(AgentWebGuard.BlockedListReason, AgentWebGuard.CheckDocumentUrl(U("https", "www.blocked.example") + "path?q=1", block));
        Assert.Contains("scheme not allowed", AgentWebGuard.CheckDocumentUrl(U("ftp", "example.com"), block));
        Assert.Contains("scheme not allowed", AgentWebGuard.CheckDocumentUrl("file:///C:/x.txt", block));
        Assert.Null(AgentWebGuard.CheckDocumentUrl("about:blank", block));
        // Local and private addresses stay blocked with an empty list.
        foreach (var host in new[] { "localhost", "a.localhost", "nas.local", Ip(127, 0, 0, 1), Ip(192, 168, 0, 1), Ip(10, 1, 1, 1), Ip(172, 20, 0, 1), Ip(169, 254, 1, 1), "[" + Ip6("", "", "1") + "]" })
            Assert.Equal(AgentWebGuard.PrivateReason, AgentWebGuard.CheckDocumentUrl(U("https", host), Array.Empty<string>()));
    }

    [Fact]
    public void CheckSubresourceUrl_BlocksPrivateAndBlockedDomains()
    {
        var block = new[] { "blocked.example" };
        Assert.NotNull(AgentWebGuard.CheckSubresourceUrl(U("https", Ip(10, 0, 0, 5))));
        Assert.NotNull(AgentWebGuard.CheckSubresourceUrl(U("ws", "localhost")));
        Assert.Null(AgentWebGuard.CheckSubresourceUrl(U("https", "example.com")));
        Assert.Null(AgentWebGuard.CheckSubresourceUrl(U("https", "example.com"), block));
        Assert.Null(AgentWebGuard.CheckSubresourceUrl("data:text/plain,hi", block));
        // 0.9.0: fetch/XHR/WebSocket to a blocked domain is refused too.
        Assert.Equal(AgentWebGuard.BlockedListReason, AgentWebGuard.CheckSubresourceUrl(U("https", "api.blocked.example") + "v1", block));
        Assert.Equal(AgentWebGuard.BlockedListReason, AgentWebGuard.CheckSubresourceUrl(U("wss", "blocked.example"), block));
    }

    [Fact]
    public async Task CheckResolvedAsync_BlocksNamesResolvingToLoopback()
    {
        // "localtest.me" is a public DNS name that resolves to the IPv4 loopback address.
        try
        {
            await System.Net.Dns.GetHostAddressesAsync("localtest.me");
        }
        catch
        {
            // DNS is unavailable in this environment, so there is nothing to verify.
            return;
        }

        // A wildcard allow list must not permit a name that resolves to loopback.
        Assert.NotNull(await AgentWebGuard.CheckResolvedAsync(U("https", "localtest.me"), new[] { "*" }, CancellationToken.None));

        // Listing the host explicitly by name does not open private or local targets.
        Assert.NotNull(await AgentWebGuard.CheckResolvedAsync(U("https", "localtest.me"), new[] { "localtest.me" }, CancellationToken.None));

        // Public names stay allowed under the wildcard.
        Assert.Null(await AgentWebGuard.CheckResolvedAsync(U("https", "example.com"), new[] { "*" }, CancellationToken.None));
    }

    [Fact]
    public void Normalize_CleansAndDeduplicates()
    {
        var input = new string?[] { "  HTTPS://Www.Example.com/path?q=1", "*.foo.org", "foo.org", "", null };
        var result = AgentWebSettingsStore.Normalize(input);
        Assert.Equal(new[] { "www.example.com", "foo.org" }, result);
    }

    [Fact]
    public void Store_RoundTripsAndFallsBackToDefaults()
    {
        var file = Path.GetTempFileName();
        try
        {
            var store = new AgentWebSettingsStore(file);
            store.Save(new AgentWebSettings { Block = ["example.com", "foo.org"], Journal = false });

            var reloaded = new AgentWebSettingsStore(file);
            Assert.Equal(new[] { "example.com", "foo.org" }, reloaded.Current.Block);
            Assert.False(reloaded.Current.Journal);

            // Corrupt content must fall back to defaults.
            File.WriteAllText(file, "{not json");
            var corrupt = new AgentWebSettingsStore(file);
            Assert.Empty(corrupt.Current.Block);
            Assert.True(corrupt.Current.Journal);
        }
        finally
        {
            File.Delete(file);
        }

        // Missing file must yield defaults.
        var missing = new AgentWebSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        Assert.Empty(missing.Current.Block);
        Assert.Null(missing.Current.AllowLegacy);
        Assert.True(missing.Current.Journal);
    }

    [Fact]
    public void ConfirmForms_DefaultsToTrue_MissingInOldFile_AndRoundTrips()
    {
        Assert.True(new AgentWebSettings().ConfirmForms);

        var dir = Path.Combine(Path.GetTempPath(), "co-agentweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-web.json");
        try
        {
            // A file written by 0.8.8 has no ConfirmForms: keep asking.
            File.WriteAllText(file, "{\"Allow\":[\"example.com\"],\"Journal\":true,\"ConfirmClicks\":false}");
            Assert.True(new AgentWebSettingsStore(file).Current.ConfirmForms);

            // 0.9.0: the Allow migration and a later Save keep ConfirmForms = false.
            File.WriteAllText(file, "{\"Allow\":[\"example.com\"],\"ConfirmForms\":false}");
            var migrated = new AgentWebSettingsStore(file).Current;
            Assert.False(migrated.ConfirmForms);
            Assert.Equal(new[] { "example.com" }, migrated.AllowLegacy);

            new AgentWebSettingsStore(file).Save(new AgentWebSettings { ConfirmForms = false, Block = ["example.com"] });
            Assert.False(new AgentWebSettingsStore(file).Current.ConfirmForms);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfirmClicks_DefaultsToFalse_AndRoundTripsThroughSaveAndNewStore()
    {
        Assert.False(new AgentWebSettings().ConfirmClicks);

        var dir = Path.Combine(Path.GetTempPath(), "co-agentweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-web.json");
        try
        {
            var store = new AgentWebSettingsStore(file);
            Assert.False(store.Current.ConfirmClicks);

            store.Save(new AgentWebSettings { ConfirmClicks = true, Journal = false, Block = ["example.com"] });

            var reloaded = new AgentWebSettingsStore(file);
            Assert.True(reloaded.Current.ConfirmClicks);
            Assert.False(reloaded.Current.Journal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_MovesLegacyAllowToAllowLegacy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "co-agentweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-browser.json");
        try
        {
            // A 0.8.x file: allowlist, journal off, confirm clicks on.
            File.WriteAllText(file, """{"Allow":["Example.com","foo.org"],"Journal":false,"ConfirmClicks":true}""");
            var store = new AgentWebSettingsStore(file);
            Assert.Empty(store.Current.Block);                       // the allowlist is not applied in any way
            Assert.Equal(new[] { "example.com", "foo.org" }, store.Current.AllowLegacy);
            Assert.False(store.Current.Journal);
            Assert.True(store.Current.ConfirmClicks);

            // The file is rewritten at once: no "Allow" any more, "AllowLegacy" and "Block" present.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.False(json.ContainsKey("Allow"));
            Assert.True(json.ContainsKey("AllowLegacy"));
            Assert.True(json.ContainsKey("Block"));

            // Saving from the UI (no AllowLegacy given) keeps the legacy list.
            store.Save(new AgentWebSettings { Block = ["bad.example"], Journal = true });
            var reloaded = new AgentWebSettingsStore(file);
            Assert.Equal(new[] { "bad.example" }, reloaded.Current.Block);
            Assert.Equal(new[] { "example.com", "foo.org" }, reloaded.Current.AllowLegacy);
            Assert.True(reloaded.Current.Journal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Store_NewFileHasNoLegacyAndBlockedChecksUseIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "co-agentweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-browser.json");
        try
        {
            var store = new AgentWebSettingsStore(file);
            store.Save(new AgentWebSettings { Block = ["https://Blocked.example/path", "*.ads.example"] });
            Assert.Equal(new[] { "blocked.example", "ads.example" }, store.Current.Block);
            Assert.DoesNotContain("AllowLegacy", File.ReadAllText(file));
            Assert.NotNull(AgentWebGuard.CheckDocumentUrl(U("https", "x.ads.example"), store.Current.Block));
            Assert.Null(AgentWebGuard.CheckDocumentUrl(U("https", "example.com"), store.Current.Block));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunTabs_OneHolderPerRun_IdleSweepSkipsBusyAndKept()
    {
        var closed = new List<string>();
        var keep = new HashSet<string>();
        using var tabs = new RunTabs<string>("agent-web", key => key,
            (key, _, _) => { lock (closed) closed.Add(key); return Task.CompletedTask; },
            keep: key => keep.Contains(key), idle: TimeSpan.FromMinutes(30));

        Assert.Equal("agent-web:run-a", tabs.KeyOf("run-a"));
        Assert.Equal("agent-web:default", tabs.KeyOf(""));
        Assert.True(tabs.OwnsKey("agent-web:x") && tabs.OwnsKey("agent-web") && !tabs.OwnsKey("agent-webx"));
        Assert.Null(tabs.Find("run-a"));

        using (var a = tabs.Use("run-a")) Assert.Equal("agent-web:run-a", a.Value);
        using (var b = tabs.Use("run-b")) Assert.Equal("agent-web:run-b", b.Value);
        var busy = tabs.Use("run-c");
        keep.Add("agent-web:run-b");
        Assert.Equal(3, tabs.Count);

        // Not idle yet.
        Assert.Equal(0, await tabs.SweepAsync(CancellationToken.None));
        // 31 minutes later: run-a closes, run-b is kept (taken over), run-c is busy.
        Assert.Equal(1, await tabs.SweepAsync(CancellationToken.None, DateTime.UtcNow.AddMinutes(31)));
        Assert.Equal(new[] { "agent-web:run-a" }, closed);
        Assert.Null(tabs.Find("run-a"));
        Assert.NotNull(tabs.Find("run-b"));

        busy.Dispose();
        keep.Clear();
        Assert.Equal(2, await tabs.SweepAsync(CancellationToken.None, DateTime.UtcNow.AddMinutes(31)));
        Assert.Equal(0, tabs.Count);
    }

    [Fact]
    public async Task RunTabs_CallDuringIdleClose_WaitsAndGetsFreshHolder()
    {
        // Codex review 0.9.0: a call for the same job while its idle tab is being closed must not get the
        // tab that is about to close, and the sweep must not close a holder a call is using.
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int created = 0, closes = 0;
        using var tabs = new RunTabs<object>("agent-web", _ => { Interlocked.Increment(ref created); return new object(); },
            async (_, _, _) => { Interlocked.Increment(ref closes); closing.TrySetResult(); await release.Task; },
            idle: TimeSpan.FromMinutes(30));

        object first;
        using (var a = tabs.Use("run-a")) first = a.Value;
        var sweep = tabs.SweepAsync(CancellationToken.None, DateTime.UtcNow.AddMinutes(31));
        await closing.Task;   // the sweep is inside close

        var call = Task.Run(() => { using var l = tabs.Use("run-a"); return l.Value; });
        await Task.Delay(200);
        Assert.False(call.IsCompleted);   // waits for the close to finish

        release.SetResult();
        Assert.Equal(1, await sweep);
        var second = await call;
        Assert.NotSame(first, second);
        Assert.Equal(2, created);
        Assert.Equal(1, closes);
        Assert.Same(second, tabs.Find("run-a"));
    }

    [Fact]
    public async Task EdgeBrowser_CloseStaleTabs_PrunesOldJobTabsWithoutBrowser()
    {
        var dir = Path.Combine(Path.GetTempPath(), "co-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fresh = DateTime.UtcNow.AddHours(-1).ToString("o");
            var old = DateTime.UtcNow.AddHours(-30).ToString("o");
            // Old 0.8.x string map plus 0.9.0 "created:" entries and one object entry.
            File.WriteAllText(Path.Combine(dir, "co-tabs.json"), "{"
                + "\"mail\": \"T-MAIL\", \"agent-web\": \"T-OLD\", \"agent\": \"T-OLD2\","
                + "\"agent-web:r1\": \"T1\", \"created:agent-web:r1\": \"" + fresh + "\","
                + "\"agent-web:r2\": \"T2\", \"created:agent-web:r2\": \"" + old + "\","
                + "\"agent:r3\": {\"id\": \"T3\", \"created\": \"" + old + "\"}"
                + "}");
            // No DevToolsActivePort: the browser is not running, nothing may be started.
            await using var edge = new EdgeBrowser(dir);
            int n = await edge.CloseStaleTabsAsync(k => k == "agent" || k == "agent-web"
                || k.StartsWith("agent:") || k.StartsWith("agent-web:"), TimeSpan.FromHours(24), CancellationToken.None);
            Assert.Equal(4, n); // agent-web, agent (no time), agent-web:r2, agent:r3 (old)

            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "co-tabs.json")))!.AsObject();
            Assert.Equal("T-MAIL", (string?)json["mail"]);
            Assert.Equal("T1", (string?)json["agent-web:r1"]);
            Assert.True(json.ContainsKey("created:agent-web:r1"));
            foreach (var gone in new[] { "agent-web", "agent", "agent-web:r2", "created:agent-web:r2", "agent:r3" })
                Assert.False(json.ContainsKey(gone), gone);
            // The file stays a flat map of strings (readable by 0.8.x and browser-agent).
            Assert.All(json, kv => Assert.Equal(System.Text.Json.JsonValueKind.String, kv.Value!.GetValueKind()));
            Assert.False(File.Exists(Path.Combine(dir, "lockfile")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Connector_ReflectsManagedFlagAndSiteList()
    {
        var file = Path.GetTempFileName();
        try
        {
            var store = new AgentWebSettingsStore(file);

            var managed = new AgentBrowserConnector(store, managed: true);
            Assert.True(managed.Planned);
            Assert.Contains("managed", managed.Description, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("Blocked sites: none", new AgentBrowserConnector(store, managed: false).Description);
            store.Save(new AgentWebSettings { Block = ["example.com"], Journal = true });
            var local = new AgentBrowserConnector(store, managed: false);
            Assert.False(local.Planned);
            Assert.Contains("example.com", local.Description);
            Assert.Equal(10, local.Tools.Count);
            Assert.All(local.Tools, t => Assert.StartsWith("browser_", t));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ParseRef_AcceptsNodeRefsAndRejectsOthers()
    {
        Assert.Equal(42, AgentPage.ParseRef("n42"));
        foreach (var bad in new[] { "e1", "", "n", "n-1" })
        {
            var ex = Assert.Throws<ToolException>(() => AgentPage.ParseRef(bad));
            Assert.Equal("bad_ref", ex.Code);
        }
    }

    // Finds the first [ref=nX] on a snapshot line containing both markers.
    static string FindRef(string snapshot, string role, string label)
    {
        var line = snapshot.Split('\n').FirstOrDefault(l =>
            l.Contains(role, StringComparison.OrdinalIgnoreCase) && l.Contains(label, StringComparison.Ordinal));
        Assert.NotNull(line);
        var m = Regex.Match(line!, @"\[ref=(n\d+)\]");
        Assert.True(m.Success, $"no ref on line: {line}");
        return m.Groups[1].Value;
    }

    [Theory]
    [InlineData("Password", true)]
    [InlineData("Enter your PIN", true)]
    [InlineData("CVC", true)]
    [InlineData("Security code", true)]
    [InlineData("Card number", true)]
    [InlineData("One-time code", true)]
    [InlineData("Wachtwoord", true)]
    [InlineData("Номер счёта", true)]
    [InlineData("Код подтверждения", true)]
    [InlineData("Customer name", false)]
    [InlineData("Postal code", false)]
    [InlineData("Telephone", false)]
    [InlineData("Zip code", false)]
    [InlineData("Spinner", false)]
    [InlineData("", false)]
    public void SensitiveName_MatchesSecretFieldNames(string name, bool expected) =>
        Assert.Equal(expected, AgentPage.SensitiveName(name));

    [Fact]
    public async Task Live_ClickInShadowRoot_And_LeverCardsFieldTyped()
    {
        if (!LiveBrowser.Enabled) return;

        var dir = Path.Combine(Path.GetTempPath(), "co-agent-click-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var live = LiveBrowser.Start(Path.Combine(dir, "profile"));
            var edge = live.Browser;
            var store = new AgentWebSettingsStore(Path.Combine(dir, "agent-web.json"));
            var page = new AgentPage(edge.Tab("agent-web"), store, Path.Combine(dir, "journal"), s => output.WriteLine(s));

            // LinkedIn-like button whose text sits in a shadow root (10.11: "Failed to execute 'contains' on 'Node'") and a
            // Lever custom question named cards[<uuid>][field0], which is not a payment card.
            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "document.body.innerHTML = '<div id=b role=button tabindex=0 onclick=\"window.__c = (window.__c || 0) + 1\"></div>" +
                             "<form><label for=q>Notice period</label><input id=q name=\"cards[5f1e][field0]\"></form>';" +
                             "document.getElementById('b').attachShadow({ mode: 'open' }).innerHTML = '<span style=\"padding:20px\">Connect</span>'; true",
            }, cts.Token), cts.Token);
            var snap = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(snap.Text);
            await page.ClickAsync(FindRef(snap.Text, "button", "Connect"), cts.Token);
            await page.TypeAsync(FindRef(snap.Text, "textbox", "Notice period"), "one month", false, cts.Token);

            var n = await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "[window.__c, document.getElementById('q').value].join()",
                returnByValue = true,
            }, cts.Token), cts.Token);
            Assert.Equal("1,one month", n.GetProperty("result").GetProperty("value").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Live_Upload_TwoInputsAndInputMadeOnClick()
    {
        if (!LiveBrowser.Enabled) return;

        var dir = Path.Combine(Path.GetTempPath(), "co-agent-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var live = LiveBrowser.Start(Path.Combine(dir, "profile"));
            var edge = live.Browser;
            var store = new AgentWebSettingsStore(Path.Combine(dir, "agent-web.json"));
            var page = new AgentPage(edge.Tab("agent-web"), store, Path.Combine(dir, "journal"), s => output.WriteLine(s));
            var file = Path.Combine(dir, "cv.txt");
            File.WriteAllText(file, "cv");

            // itds.nl-like form (09.11): two hidden inputs, the agent picks the heading of the second; a widget that makes
            // its input only on click (Dropzone-like) and keeps it out of the document.
            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "document.body.innerHTML = '<form><h3>CV</h3><input type=file id=a style=\"display:none\">" +
                             "<h3>Motivation letter</h3><input type=file id=b style=\"display:none\"></form>" +
                             "<button type=button onclick=\"const f = document.createElement(\\'input\\'); f.type = \\'file\\'; window.__f = f; f.click()\">Drop files here</button>'; true",
            }, cts.Token), cts.Token);
            var snap = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(snap.Text);
            await page.UploadAsync(FindRef(snap.Text, "heading", "Motivation letter"), file, cts.Token);
            await page.UploadAsync(FindRef(snap.Text, "button", "Drop files here"), file, cts.Token);

            var n = await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "[document.getElementById('a').files.length, document.getElementById('b').files.length, window.__f.files.length].join()",
                returnByValue = true,
            }, cts.Token), cts.Token);
            Assert.Equal("0,1,1", n.GetProperty("result").GetProperty("value").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Live_Upload_ButtonOverHiddenFileInput()
    {
        if (!LiveBrowser.Enabled) return;

        var dir = Path.Combine(Path.GetTempPath(), "co-agent-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var live = LiveBrowser.Start(Path.Combine(dir, "profile"));
            var edge = live.Browser;
            var store = new AgentWebSettingsStore(Path.Combine(dir, "agent-web.json"));
            var page = new AgentPage(edge.Tab("agent-web"), store, Path.Combine(dir, "journal"), s => output.WriteLine(s));
            var file = Path.Combine(dir, "cv.txt");
            File.WriteAllText(file, "cv");

            // A Homerun-like widget (09.11): the snapshot shows the button, the file input is hidden next to it.
            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "document.body.innerHTML = '<div class=up><input type=file id=f style=\"display:none\">" +
                             "<button type=button onclick=\"document.getElementById(\\'f\\').click()\">Select files</button></div>'; true",
            }, cts.Token), cts.Token);
            var snap = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(snap.Text);
            await page.UploadAsync(FindRef(snap.Text, "button", "Select files"), file, cts.Token);

            var n = await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = "document.getElementById('f').files.length", returnByValue = true,
            }, cts.Token), cts.Token);
            Assert.Equal(1, n.GetProperty("result").GetProperty("value").GetInt32());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Live_EdgeAgentPage_EndToEnd()
    {
        if (!LiveBrowser.Enabled) return;

        var dir = Path.Combine(Path.GetTempPath(), "co-agent-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var live = LiveBrowser.Start(Path.Combine(dir, "profile"));
            var edge = live.Browser;

            var store = new AgentWebSettingsStore(Path.Combine(dir, "agent-web.json"));
            store.Save(new AgentWebSettings { Block = ["iana.org"], Journal = true });

            var journalDir = Path.Combine(dir, "journal");
            var page = new AgentPage(edge.Tab("agent-web"), store, journalDir, s => output.WriteLine(s));

            // Allowed site: navigate and snapshot.
            var nav = await page.NavigateAsync(U("https", "example.com"), cts.Token);
            Assert.Contains("example.com", nav.Url);
            var snap = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(snap.Text);
            Assert.Contains("Example Domain", snap.Text);
            Assert.Matches("ref=n\\d+", snap.Text);

            // A blocked site must be refused.
            var blocked = await Assert.ThrowsAsync<ToolException>(
                () => page.NavigateAsync(U("https", "www.iana.org"), cts.Token));
            Assert.Equal("domain_blocked", blocked.Code);

            // Form interaction on httpbin.org/forms/post.
            await page.NavigateAsync(U("https", "httpbin.org") + "forms/post", cts.Token);
            var form = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(form.Text);
            var customerRef = FindRef(form.Text, "textbox", "Customer name");
            var buttonRef = FindRef(form.Text, "button", "Submit order");

            await page.TypeAsync(customerRef, "Test User", false, cts.Token);

            // Values that look like long digit sequences are masked in snapshots.
            var phoneRef = FindRef(form.Text, "textbox", "Telephone");
            var digits = string.Concat(Enumerable.Repeat("1234", 4));
            await page.TypeAsync(phoneRef, digits, false, cts.Token);
            var masked = await page.SnapshotAsync(20000, cts.Token);
            var phoneLine = masked.Text.Split('\n').First(l => l.Contains("Telephone") && l.Contains("textbox"));
            Assert.Contains("***", phoneLine);
            Assert.DoesNotContain(digits, phoneLine);
            Assert.Contains(masked.Text.Split('\n'), l => l.Contains("Test User"));
            var inspect = await page.InspectAsync(buttonRef, cts.Token);
            Assert.Equal("purchase", inspect.Category);
            Assert.Contains(inspect.Fields, f => f.Value == "Test User");

            // Block the site while its page is still open; further access must be blocked.
            store.Save(new AgentWebSettings { Block = ["iana.org", "httpbin.org"], Journal = true });
            var revoked = await Assert.ThrowsAsync<ToolException>(() => page.SnapshotAsync(20000, cts.Token));
            Assert.Equal("domain_blocked", revoked.Code);

            // Restore the original list for the remaining steps.
            store.Save(new AgentWebSettings { Block = ["iana.org"], Journal = true });

            // Journal entry is written to journal.jsonl.
            await page.JournalAsync("run-test", "test", new { step = 1 }, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(journalDir, "journal.jsonl")));

            // Link to a blocked site must be blocked
            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            var snap2 = await page.SnapshotAsync(20000, cts.Token);
            var learnRef = FindRef(snap2.Text, "link", "Learn more");
            var ex2 = await Assert.ThrowsAsync<ToolException>(() => page.ClickAsync(learnRef, cts.Token));
            Assert.Equal("domain_blocked", ex2.Code);
            try { await page.BackAsync(cts.Token); } catch (ToolException) { }

            // New tabs (target=_blank, window.open) must not escape the agent tab
            var popupUrl = U("https", "www.iana.org");
            var popupUrlJs = System.Text.Json.JsonSerializer.Serialize(popupUrl);
            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = $@"(() => {{
                    const a = document.createElement('a');
                    a.id = 'co-pop'; a.target = '_blank'; a.href = {popupUrlJs};
                    a.textContent = 'Open popup test';
                    document.body.insertBefore(a, document.body.firstChild);
                    return true;
                }})()",
                returnByValue = true
            }, cts.Token), cts.Token);
            var popSnap = await page.SnapshotAsync(20000, cts.Token);
            var popRef = FindRef(popSnap.Text, "link", "Open popup test");
            // The popup target is closed by auto-attach before it loads; the click itself may report ok
            // (same on 0.8.9). What matters is checked below: no tab with the blocked site remains.
            try { await page.ClickAsync(popRef, cts.Token); }
            catch (ToolException popEx) { Assert.Equal("domain_blocked", popEx.Code); }

            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                // Deferred: a synchronous window.open waits for the popup, which waits for the debugger (auto-attach).
                expression = $"setTimeout(() => window.open({popupUrlJs}, '_blank'), 0), true",
                returnByValue = true,
                userGesture = true
            }, cts.Token), cts.Token);
            await Task.Delay(1500, cts.Token);

            var targets = await page.RunAsync(s => s.SendAsync("Target.getTargets", new { }, cts.Token), cts.Token);
            var pages = targets.GetProperty("targetInfos").EnumerateArray()
                .Where(t => t.GetProperty("type").GetString() == "page")
                .ToList();
            Assert.DoesNotContain(pages, t => (t.GetProperty("url").GetString() ?? "").Contains("iana.org"));
            output.WriteLine($"Page targets after popup attempts: {pages.Count}");

            var navAfterPopup = await page.NavigateAsync(U("https", "example.com"), cts.Token);
            Assert.Contains("example.com", navAfterPopup.Url);

            // A real mouse click cannot reach a hidden window: only run the takeover check when the tab is visible
            var visibilityState = await page.RunAsync<string>(async s =>
            {
                await s.SendAsync("Page.bringToFront", new { }, cts.Token);
                await Task.Delay(500, cts.Token);
                var r = await s.SendAsync("Runtime.evaluate", new { expression = "document.visibilityState", returnByValue = true }, cts.Token);
                return r.GetProperty("result").GetProperty("value").GetString() ?? "";
            }, cts.Token);
            if (visibilityState != "visible")
            {
                output.WriteLine("takeover check skipped: the Edge window is hidden");
                return;
            }

            // User takeover via a real mouse click on the overlay's "Take over" button
            await page.RunAsync<bool>(async s =>
            {
                var r = await s.SendAsync("Runtime.evaluate", new { expression = "innerWidth", returnByValue = true }, cts.Token);
                var w = r.GetProperty("result").GetProperty("value").GetDouble();
                int x = (int)(w / 2 + 75), y = 12;
                foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                    await s.SendAsync("Input.dispatchMouseEvent", new { type, x, y, button = "left", clickCount = 1 }, cts.Token);
                return true;
            }, cts.Token);

            var pauseDeadline = DateTime.UtcNow.AddSeconds(3);
            while (page.Paused == null && DateTime.UtcNow < pauseDeadline)
                await Task.Delay(100, cts.Token);
            Assert.NotNull(page.Paused);
            Assert.Equal("user", page.Paused!.By);

            var ex3 = await Assert.ThrowsAsync<ToolException>(() => page.NavigateAsync(U("https", "example.com"), cts.Token));
            Assert.Equal("paused_by_user", ex3.Code);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Edge may still hold profile files; ignore.
            }
        }
    }

    // co-resident 0.5.1 (a member's Mac, 13.11): date inputs get their value in the input's format, a link with a real address
    // is not a submit, and a new window opened by the agent tab is opened in that tab (also when it starts blank).
    [Fact]
    public async Task Live_DatesLinksAndNewWindows()
    {
        if (!LiveBrowser.Enabled) return;

        var dir = Path.Combine(Path.GetTempPath(), "co-agent-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var live = LiveBrowser.Start(Path.Combine(dir, "profile"));
            var store = new AgentWebSettingsStore(Path.Combine(dir, "agent-web.json"));
            store.Save(new AgentWebSettings { Block = ["iana.org"], Journal = false });
            var page = new AgentPage(live.Browser.Tab("agent-web"), store, Path.Combine(dir, "journal"), s => output.WriteLine(s));

            await page.NavigateAsync(U("https", "example.com"), cts.Token);
            var target = U("https", "example.org");
            var targetJs = System.Text.Json.JsonSerializer.Serialize(target);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = $@"(() => {{
                    document.body.insertAdjacentHTML('afterbegin',
                      '<input type=date aria-label=""Start date"">' +
                      '<input type=month aria-label=""Since month"">' +
                      '<a href=""/jobs/1"">Apply now</a>' +
                      '<button type=button>Apply</button>' +
                      '<a target=""_blank"" href=' + JSON.stringify({targetJs}) + '>Company site</a>' +
                      '<button type=button id=blank>Open blank</button>');
                    document.getElementById('blank').onclick = () => {{ const w = window.open(); w.location = {targetJs} + '?b=1'; }};
                    return true;
                }})()",
                returnByValue = true,
            }, cts.Token), cts.Token);
            var snap = await page.SnapshotAsync(20000, cts.Token);
            output.WriteLine(snap.Text);

            // Dates: day-first and ISO; text a date field cannot take is refused with the format.
            var dateRef = FindRef(snap.Text, "DateTime", "Start date");
            await page.TypeAsync(dateRef, "01/12/2026", false, cts.Token);
            var value = await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new { expression = "document.querySelector('input[type=date]').value", returnByValue = true }, cts.Token), cts.Token);
            Assert.Equal("2026-12-01", value.GetProperty("result").GetProperty("value").GetString());
            var bad = await Assert.ThrowsAsync<ToolException>(() => page.TypeAsync(dateRef, "next week", false, cts.Token));
            Assert.Equal("bad_date", bad.Code);

            // Links with an address only open a page; an Apply button still asks.
            Assert.Null((await page.InspectAsync(FindRef(snap.Text, "link", "Apply now"), cts.Token)).Category);
            Assert.Equal("submit", (await page.InspectAsync(FindRef(snap.Text, "button", "Apply"), cts.Token)).Category);

            // target=_blank: the address opens in the agent tab.
            var opened = await page.ClickAsync(FindRef(snap.Text, "link", "Company site"), cts.Token);
            Assert.Contains("example.org", opened.Url);
            Assert.NotNull(page.TakeNote());

            // window.open() + location: the address is read from the blank window's first request.
            await page.BackAsync(cts.Token);
            await page.RunAsync(s => s.SendAsync("Runtime.evaluate", new
            {
                expression = $@"(() => {{
                    document.body.insertAdjacentHTML('afterbegin', '<button type=button id=blank>Open blank</button>');
                    document.getElementById('blank').onclick = () => {{ const w = window.open(); w.location = {targetJs} + '?b=1'; }};
                    return true;
                }})()",
                returnByValue = true,
            }, cts.Token), cts.Token);
            var snap2 = await page.SnapshotAsync(20000, cts.Token);
            var blank = await page.ClickAsync(FindRef(snap2.Text, "button", "Open blank"), cts.Token);
            Assert.Contains("example.org/?b=1", blank.Url);

            var pages = (await page.RunAsync(s => s.SendAsync("Target.getTargets", new { }, cts.Token), cts.Token))
                .GetProperty("targetInfos").EnumerateArray().Count(t => t.GetProperty("type").GetString() == "page");
            output.WriteLine($"page targets: {pages}");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // 0.9.6: the tabs extension (pinned tabs = the user's lock, for browser-agent's tab chamberlain) is written with LF
    // endings and loaded together with the look; a service worker with tabs + debugger permissions.
    [Fact]
    public void AgentLook_WritesAndLoadsTabsExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "co-look-" + Guid.NewGuid().ToString("N"));
        try
        {
            AgentLook.Ensure(dir);
            var tabs = AgentLook.TabsDir(dir);
            Assert.Contains(tabs, AgentLook.LoadExtensionValue(dir).Split(','));
            var manifest = File.ReadAllText(Path.Combine(tabs, "manifest.json"));
            var sw = File.ReadAllText(Path.Combine(tabs, "co-tabs-sw.js"));
            Assert.DoesNotContain("\r", manifest + sw);
            Assert.EndsWith("}\n", manifest);
            using var doc = System.Text.Json.JsonDocument.Parse(manifest);
            Assert.Equal("co-tabs-sw.js", doc.RootElement.GetProperty("background").GetProperty("service_worker").GetString());
            var perms = doc.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "tabs", "debugger" }, perms);
            Assert.Contains("self.coTabs = async", sw);
            Assert.StartsWith("// CO agent browser tabs", sw);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
