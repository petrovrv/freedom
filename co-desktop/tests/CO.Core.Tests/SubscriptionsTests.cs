// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Subscriptions;
using Xunit;

namespace CO.Core.Tests;

public class SubscriptionsTests
{
    [Fact]
    public void ParsesClaudeAuthStatus()
    {
        var st = new CliStatus { Id = "claude" };
        CliCatalog.ParseClaudeStatus("{\n \"loggedIn\": true, \"authMethod\": \"claude.ai\", \"email\": \"a@b.c\", \"subscriptionType\": \"max\"\n}", st);
        Assert.True(st.LoggedIn);
        Assert.Equal("a@b.c", st.Account);
        Assert.Equal("max", st.Plan);
        var no = new CliStatus { Id = "claude" };
        CliCatalog.ParseClaudeStatus("{\"loggedIn\": false, \"authMethod\": \"none\"}", no);
        Assert.False(no.LoggedIn);
        var bad = new CliStatus { Id = "claude" };
        CliCatalog.ParseClaudeStatus("error: unknown command", bad);
        Assert.Null(bad.LoggedIn);
    }

    [Fact]
    public void ParsesCodexStatusAndAuthFile()
    {
        var st = new CliStatus { Id = "codex" };
        CliCatalog.ParseCodexStatus("Logged in using ChatGPT\n", 0, st);
        Assert.True(st.LoggedIn);
        Assert.Equal("ChatGPT", st.Method);
        var no = new CliStatus { Id = "codex" };
        CliCatalog.ParseCodexStatus("Not logged in\n", 1, no);
        Assert.False(no.LoggedIn);

        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string jwt = B64("{\"alg\":\"none\"}") + "." + B64("{\"email\":\"x@y.z\",\"https://api.openai.com/auth\":{\"chatgpt_plan_type\":\"plus\"}}") + ".sig";
        CliCatalog.ParseCodexAuthFile("{\"tokens\":{\"id_token\":\"" + jwt + "\",\"access_token\":\"secret\"}}", st);
        Assert.Equal("x@y.z", st.Account);
        Assert.Equal("plus", st.Plan);
        Assert.DoesNotContain("secret", st.Line());
        CliCatalog.ParseCodexAuthFile("not json", st);   // never throws
    }

    [Fact]
    public void FindsLinkAndDeviceCodeInCliOutput()
    {
        string claude = "Opening browser to sign in…\nIf the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&client_id=9d&state=ST1\nPaste code here if prompted >";
        Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&client_id=9d&state=ST1", CliCatalog.FindUrl(claude, "oauth/authorize"));
        string codex = "1. Open this link\n   \u001b[94mhttps://auth.openai.com/codex/device\u001b[0m\n\n2. Enter this one-time code \u001b[90m(expires in 15 minutes)\u001b[0m\n   \u001b[94mC69F-0EB6Q\u001b[0m\n";
        Assert.Equal("https://auth.openai.com/codex/device", CliCatalog.FindUrl(codex, "auth.openai.com"));
        Assert.Equal("C69F-0EB6Q", CliCatalog.FindDeviceCode(codex));
        Assert.Null(CliCatalog.FindDeviceCode("no code here"));
        Assert.Equal("2.1.310", CliCatalog.ParseVersion("2.1.310 (Claude Code)"));
        Assert.Equal("0.157.0", CliCatalog.ParseVersion("codex-cli 0.157.0"));
    }

    [Fact]
    public void BuildsClaudeCodeFromLinkOrCode()
    {
        string auth = "https://claude.com/cai/oauth/authorize?code=true&state=ST1";
        Assert.Equal("abc#ST2", CliCatalog.ClaudeCode("https://platform.claude.com/oauth/code/callback?code=abc&state=ST2", auth));
        Assert.Equal("abc#ST1", CliCatalog.ClaudeCode("https://platform.claude.com/oauth/code/callback?code=abc", auth));
        Assert.Equal("abc#ST1", CliCatalog.ClaudeCode("  abc ", auth));
        Assert.Equal("abc#XY", CliCatalog.ClaudeCode("abc#XY", auth));
        Assert.Throws<ArgumentException>(() => CliCatalog.ClaudeCode("two words", auth));
        Assert.Throws<ArgumentException>(() => CliCatalog.ClaudeCode("https://platform.claude.com/oauth/code/callback?code=a%0Ab&state=s", auth));
        Assert.Equal("/d /s /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" install -g @openai/codex\"",
            CliShell.CmdLine(@"C:\Program Files\nodejs\npm.cmd", new[] { "install", "-g", "@openai/codex" }));
        Assert.Throws<ArgumentException>(() => CliShell.CmdLine(@"C:\a&b\x.cmd", new[] { "x" }));
        Assert.Throws<ArgumentException>(() => CliCatalog.ClaudeCode("https://platform.claude.com/oauth/code/callback?x=1", auth));
    }

    [Fact]
    public void PilotActsOnlyOnProviderPages()
    {
        Assert.True(LoginPilot.IsClaudeCallback("https://platform.claude.com/oauth/code/callback?code=abc&state=s"));
        Assert.False(LoginPilot.IsClaudeCallback("https://evil.example/oauth/code/callback?code=abc"));
        Assert.False(LoginPilot.IsClaudeCallback("http://platform.claude.com/oauth/code/callback?code=abc"));
        Assert.False(LoginPilot.IsClaudeCallback("https://evilclaude.ai/oauth/code/callback?code=abc&state=s"));
        Assert.False(LoginPilot.IsClaudeCallback("https://platform.claude.com/oauth/code/callback?code=abc&state=other", "s"));
        Assert.True(LoginPilot.IsClaudeCallback("https://platform.claude.com/oauth/code/callback?code=abc&state=s", "s"));
        Assert.False(LoginPilot.OwnPage("codex", "https://community.openai.com/"));
        Assert.Contains("location.hostname", LoginPilot.MailCodeScript("claude", "123456"));
        Assert.True(LoginPilot.OwnPage("claude", "https://claude.ai/oauth/authorize?x"));
        Assert.False(LoginPilot.OwnPage("claude", "https://auth.openai.com/codex/device"));
        Assert.True(LoginPilot.OwnPage("codex", "https://auth.openai.com/codex/device"));
        Assert.False(LoginPilot.OwnPage("codex", "https://notopenai.com/"));
        Assert.False(LoginPilot.OwnPage("codex", "https://accounts.google.com/"));
        Assert.True(LoginPilot.IsMagicLink("claude", "https://claude.ai/magic-link#abc:def"));
        Assert.False(LoginPilot.IsMagicLink("claude", "https://claude.ai.evil.example/magic-link#x"));
        Assert.False(LoginPilot.IsMagicLink("codex", "https://claude.ai/magic-link#x"));
        Assert.Equal("https://claude.ai/magic-link#k:v", GmailWebMail.Parse("claude",
            "{\"links\":[\"https://claude.ai/images/x.png\",\"https://claude.ai/magic-link#k:v\"],\"text\":\"Sign in 000000\"}")!.Link);
        Assert.Equal("123456", GmailWebMail.Parse("codex", "{\"links\":[],\"text\":\"Your ChatGPT code is 123456\"}")!.Code);
        Assert.Null(GmailWebMail.Parse("codex", "{\"links\":[],\"text\":\"Your bank code is 123456\"}"));
        Assert.Contains("to:a@b.c after:", GmailWebMail.Query("claude", "a@b.c", DateTime.UtcNow));
        Assert.Contains("\"C69F-0EB6Q\"", LoginPilot.StepScript("codex", "C69F-0EB6Q", "", true));
        Assert.Contains("authorize", LoginPilot.StepScript("claude", "", "a@b.c", true));
        // S7: the code boxes of auth.openai.com are filled by a paste event, and the code is shown on the page
        string codex = LoginPilot.StepScript("codex", "C69F-0EB6Q", "", true, false, true);
        Assert.Contains("ClipboardEvent('paste'", codex);
        Assert.Contains("co-code-hint", codex);
        string? dump = Environment.GetEnvironmentVariable("CO_DUMP_STEPSCRIPT");
        if (!string.IsNullOrEmpty(dump)) File.WriteAllText(dump, LoginPilot.StepScript("codex", Environment.GetEnvironmentVariable("CO_DUMP_CODE") ?? "C69F-0EB6Q", "", true, false, true));
    }

    [Fact]
    public async Task PilotPassesClaudeCallbackCode()
    {
        var b = new FakeBrowser();
        int step = 0;
        b.Answer = js => { if (js == "document.visibilityState") return "\"visible\""; step++; if (step == 1) { b.Url = "https://platform.claude.com/oauth/code/callback?code=C1&state=S1"; return "\"clicked:authorize\""; } return "\"\""; };
        var s = new LoginSession { Cli = "claude" };
        typeof(LoginSession).GetProperty("Url")!.SetValue(s, "https://claude.com/cai/oauth/authorize?state=S1");
        var notes = new List<string>();
        string? got = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await LoginPilot.RunAsync(b, s, code => { got = code; return "code passed"; }, notes.Add, cts.Token);
        Assert.Equal("https://platform.claude.com/oauth/code/callback?code=C1&state=S1", got);
        Assert.Contains("pressed 'authorize'", notes);
    }

    [Fact]
    public async Task PilotStopsPressingAfterThreeTimesAndWaitsForLogin()
    {
        LoginPilot.ActionPause = TimeSpan.Zero;
        var b = new FakeBrowser();
        int scripts = 0;
        b.Answer = js => { if (js.Contains("elementFromPoint")) return "\"\""; if (js == "document.visibilityState") return "\"visible\""; scripts++; return "\"clicked:continue\""; };
        var s = new LoginSession { Cli = "codex" };
        typeof(LoginSession).GetProperty("Url")!.SetValue(s, "https://auth.openai.com/codex/device");
        var notes = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoginPilot.RunAsync(b, s, _ => "", notes.Add, cts.Token));
        Assert.Equal(4, scripts);
    }

    [Fact]
    public void RegistrationBodyCarriesNoSecrets()
    {
        var st = new CliStatus { Id = "codex", Installed = true, LoggedIn = true, Account = "x@y.z", Plan = "plus", Version = "0.1", Path = @"C:\secret\path" };
        var body = SubsRegistry.Body(new[] { st }, ("d-1", "pc1", "PC 1"), "home-main").ToJsonString();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("subs_put", doc.RootElement.GetProperty("op").GetString());
        Assert.Equal("pc1", doc.RootElement.GetProperty("device").GetProperty("short").GetString());
        Assert.Equal("home-main", doc.RootElement.GetProperty("device").GetProperty("hostId").GetString());
        Assert.Equal("x@y.z", doc.RootElement.GetProperty("subs")[0].GetProperty("account").GetString());
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task SignInBelongsToTheRunThatStartedIt()
    {
        var m = new SubscriptionManager(new HttpClient());
        var t = new CliSubsTool(m);
        var s = new LoginSession { Cli = "claude", Owner = "run:R1" };
        typeof(LoginSession).GetProperty("Url")!.SetValue(s, "https://claude.com/cai/oauth/authorize?state=S1");
        typeof(LoginSession).GetProperty("State")!.SetValue(s, "waiting");
        var logins = (Dictionary<string, LoginSession>)typeof(SubscriptionManager).GetField("_logins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(m)!;
        logins["claude"] = s;
        CO.Core.Tools.ToolEnv Env(string run) => new() { Folders = null!, Snapshots = null!, RunId = run };
        JsonElement A(string json) => JsonDocument.Parse(json).RootElement;
        var other = await Assert.ThrowsAsync<CO.Core.Tools.ToolException>(() => t.ExecuteAsync(A("{\"action\":\"login_code\",\"cli\":\"claude\",\"code\":\"x\"}"), Env("R2"), CancellationToken.None));
        Assert.Equal("not_yours", other.Code);
        typeof(LoginSession).GetProperty("Message")!.SetValue(s, "m");
        var view = await t.ExecuteAsync(A("{\"action\":\"login_status\",\"cli\":\"claude\"}"), Env("R2"), CancellationToken.None);
        Assert.DoesNotContain("state=S1", view.Content);
        Assert.Equal(CO.Core.Policy.RiskClass.X, t.Plan(A("{\"action\":\"login_cancel\",\"cli\":\"claude\"}"), null!).Class);
    }

    [Fact]
    public async Task DeviceCodeStaysWithTheRunThatStartedIt()
    {
        // R2 (review S4-01): after the pilot filled the codex code, another run, the log and Message never carry it
        LoginPilot.ActionPause = TimeSpan.Zero;
        var m = new SubscriptionManager(new HttpClient());
        var log = new List<string>();
        m.Log = log.Add;
        var t = new CliSubsTool(m);
        var s = new LoginSession { Cli = "codex", Owner = "run:R1" };
        typeof(LoginSession).GetProperty("Url")!.SetValue(s, "https://auth.openai.com/codex/device");
        typeof(LoginSession).GetProperty("UserCode")!.SetValue(s, "C69F-0EB6Q");
        typeof(LoginSession).GetProperty("State")!.SetValue(s, "waiting");
        var logins = (Dictionary<string, LoginSession>)typeof(SubscriptionManager).GetField("_logins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(m)!;
        logins["codex"] = s;
        var b = new FakeBrowser { Url = "https://auth.openai.com/codex/device" };
        b.Answer = js => js == "document.visibilityState" ? "\"visible\"" : js.Contains("elementFromPoint") ? "\"\"" : "\"filled\"";
        var pilot = typeof(SubscriptionManager).GetMethod("PilotAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var run = (Task)pilot.Invoke(m, new object[] { s, ((IBrowser)b, (Func<Task>)(() => Task.CompletedTask)) })!;
        await Task.Delay(TimeSpan.FromSeconds(5));
        ((CancellationTokenSource)typeof(LoginSession).GetField("Cts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!).Cancel();
        await run;
        Assert.Contains(log, l => l.Contains("device code typed on the page"));
        Assert.DoesNotContain(log, l => l.Contains("C69F") || l.Contains("0EB6Q"));
        CO.Core.Tools.ToolEnv Env(string r) => new() { Folders = null!, Snapshots = null!, RunId = r };
        JsonElement A(string json) => JsonDocument.Parse(json).RootElement;
        // a Message with the code or a link in it (CLI output tail, an error) is scrubbed as it is set
        typeof(LoginSession).GetProperty("Message")!.SetValue(s, "exit 1: open https://auth.openai.com/codex/device?x=1 and enter C69F-0EB6Q (or C69F0EB6Q, ABCD-12345)");
        var other = await t.ExecuteAsync(A("{\"action\":\"login_status\",\"cli\":\"codex\"}"), Env("R2"), CancellationToken.None);
        Assert.DoesNotContain("C69F", other.Content);
        Assert.DoesNotContain("ABCD-12345", other.Content);
        Assert.DoesNotContain("auth.openai.com", other.Content);
        Assert.Contains("another agent run", other.Content);
        var mine = await t.ExecuteAsync(A("{\"action\":\"login_status\",\"cli\":\"codex\"}"), Env("R1"), CancellationToken.None);
        Assert.Contains("C69F-0EB6Q", mine.Content);       // the run that started it still gets the code
    }

    [Fact]
    public void ToolPlansInstallAndSignInAsExecution()
    {
        var m = new SubscriptionManager(new HttpClient());
        var t = new CliSubsTool(m);
        JsonElement A(string json) => JsonDocument.Parse(json).RootElement;
        var p = t.Plan(A("{\"action\":\"login_start\",\"cli\":\"codex\"}"), null!);
        Assert.Equal(CO.Core.Policy.RiskClass.X, p.Class);
        Assert.Equal("cli:codex", p.Scopes.Single().Value);
        Assert.Equal(CO.Core.Policy.RiskClass.R, t.Plan(A("{\"action\":\"login_code\",\"cli\":\"claude\",\"code\":\"x\"}"), null!).Class);
        Assert.Equal(CO.Core.Policy.RiskClass.R, t.Plan(A("{\"action\":\"status\"}"), null!).Class);
        Assert.Throws<CO.Core.Tools.ToolException>(() => t.Plan(A("{\"action\":\"rm\"}"), null!));
        Assert.Throws<CO.Core.Tools.ToolException>(() => t.Plan(A("{\"action\":\"install\",\"cli\":\"bash\"}"), null!));
    }
}

// CO_LIVE_SUBS=1: the real CLIs of this computer. Sign-ins go to a throwaway CLAUDE_CONFIG_DIR / CODEX_HOME, so the
// computer's own sign-ins are never touched; the sign-in is only started (link and code), then cancelled.
public class SubscriptionsLiveTests
{
    [Fact]
    public async Task StatusAndSignInStartOfThisComputer()
    {
        if (Environment.GetEnvironmentVariable("CO_LIVE_SUBS") != "1") return;
        var real = new SubscriptionManager(new HttpClient());
        var all = await real.StatusAllAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "co-subs-live.txt"), real.Describe());

        string tmp = Path.Combine(Path.GetTempPath(), "co-subs-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tmp, "claude"));
        Directory.CreateDirectory(Path.Combine(tmp, "codex"));
        var sh = new CliShell();
        sh.ExtraEnv["CLAUDE_CONFIG_DIR"] = Path.Combine(tmp, "claude");
        sh.ExtraEnv["CODEX_HOME"] = Path.Combine(tmp, "codex");
        var m = new SubscriptionManager(new HttpClient(), sh);
        var sb = new StringBuilder();
        foreach (string id in new[] { "claude", "codex" })
        {
            if (all.Single(x => x.Id == id) is not { Installed: true }) continue;
            var st = await m.StatusAsync(id, CancellationToken.None);
            sb.AppendLine("fresh " + st.Line());
            Assert.NotEqual(true, st.LoggedIn);
            var s = await m.StartLoginAsync(id, CancellationToken.None, pilot: false);
            sb.AppendLine(s.Describe());
            Assert.Equal("waiting", s.State);
            Assert.StartsWith("https://", s.Url);
            if (id == "codex") Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4,6}$", s.UserCode);
            Assert.True(m.Cancel(id));
        }
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "co-subs-live.txt"), "\n" + sb);
        try { Directory.Delete(tmp, true); } catch (Exception) { }
    }
}

// CO_LIVE_SUBS_LOGIN=claude|codex: a whole sign-in through the pilot in the CO browser of this computer (profile
// CO_LIVE_SUBS_PROFILE, default %LOCALAPPDATA%\CO\browser, where the user is signed in to the providers), into a
// throwaway CLAUDE_CONFIG_DIR / CODEX_HOME; the computer's own sign-ins are not touched. Leaves one extra
// sign-in session at the provider.
public class SubscriptionsLiveLoginTests
{
    [Fact]
    public async Task WholeSignInThroughTheCoBrowser()
    {
        string? id = Environment.GetEnvironmentVariable("CO_LIVE_SUBS_LOGIN");
        if (string.IsNullOrEmpty(id)) return;
        string profile = Environment.GetEnvironmentVariable("CO_LIVE_SUBS_PROFILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CO", "browser");
        string tmp = Path.Combine(Path.GetTempPath(), "co-subs-login-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var sh = new CliShell();
        sh.ExtraEnv[id == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME"] = tmp;
        var browser = new CO.Core.Browser.EdgeBrowser(profile);
        var m = new SubscriptionManager(new HttpClient(), sh);
        var log = new StringBuilder();
        m.Log = l => { lock (log) log.AppendLine($"{DateTime.Now:HH:mm:ss} {l}"); };
        m.LoginTab = _ => (browser.Tab("subs-login-test"), () => browser.CloseTabAsync("subs-login-test", CancellationToken.None));
        m.Mail = new GmailWebMail(() => browser.Tab("subs-mail-test"));
        var s = await m.StartLoginAsync(id, CancellationToken.None, email: Environment.GetEnvironmentVariable("CO_LIVE_SUBS_EMAIL"));
        int wait = int.TryParse(Environment.GetEnvironmentVariable("CO_LIVE_SUBS_WAIT"), out int w) ? w : 300;
        for (int i = 0; i < wait && !s.Ended; i++) await Task.Delay(1000);
        if (!s.Ended)
        {
            try
            {
                var page = await browser.Tab("subs-login-test").EvaluateAsync(@"location.href.slice(0, 120) + ' | ' + (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 600) + ' | ' + JSON.stringify([...document.querySelectorAll('input')].map(i => i.value))", CancellationToken.None);
                log.AppendLine("page at the end: " + page);
            }
            catch (Exception ex) { log.AppendLine("page: " + ex.Message); }
            m.Cancel(id);
        }
        var st = await m.StatusAsync(id, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "co-subs-login-" + id + ".txt"), log + "\n" + s.Describe() + "\n" + st.Line());
        await Task.Delay(1500);
        try { Directory.Delete(tmp, true); } catch (Exception) { }
        Assert.Equal("done", s.State);
        Assert.True(st.LoggedIn);
    }
}
