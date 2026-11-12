// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using CO.Core.Browser;
using CO.Core.Policy;

namespace CO.Corporate.Tools;

// Microsoft Teams through the Teams web client in the CO browser (same idea as engie_fetch): the page signs in
// (Microsoft → Okta) and keeps its MSAL access tokens in web storage; calls to Microsoft Graph run as fetch() inside
// the Teams page with the token the client itself obtained. Token values never leave the page; tool output carries
// only data. No app registration in the tenant is needed. Sending messages is not offered.
public static class TeamsTools
{
    public const string AppUrl = "https://teams.microsoft.com/v2/";

    // A managed device releases their results without a prompt: the connector ticked is the consent (core ToolHost).
    static TeamsTools() => CO.Core.Host.ToolHost.ReleaseByConsent("teams_");

    public static IEnumerable<IDeviceTool> All(IBrowser browser) => new IDeviceTool[]
    {
        new TeamsProbe(browser), new TeamsChats(browser), new TeamsMessages(browser), new TeamsSearch(browser), new TeamsFetch(browser),
    };

    internal static bool OnTeams(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.Equals("teams.microsoft.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("teams.cloud.microsoft", StringComparison.OrdinalIgnoreCase));

    internal static bool AllowedApiHost(string host) =>
        host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".teams.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("substrate.office.com", StringComparison.OrdinalIgnoreCase);

    internal static async Task EnsureOnTeamsAsync(IBrowser b, CancellationToken ct)
    {
        string here = await b.CurrentUrlAsync(ct);
        if (OnTeams(here)) return;
        here = await b.NavigateAsync(AppUrl, ct);
        if (OnTeams(here)) return;
        // Microsoft → Okta sign-in, same automation as SharePoint; the user finishes it in the CO browser if asked.
        if (!await SharePointApi.SignInAsync(b, AppUrl, new Uri(AppUrl).Host, ct) && !OnTeams(await b.CurrentUrlAsync(ct)))
            throw new ToolException("sign_in_needed",
                "Teams sign-in did not finish. The CO browser shows the sign-in page; ask the user to complete it there, then retry.");
    }

    // Access tokens of the MSAL cache in localStorage/sessionStorage (credentialType AccessToken). pick(resource) returns
    // the freshest one whose scopes mention the resource host; the secret stays inside the page.
    internal const string TokenJs =
        " const toks = []; for (const st of [localStorage, sessionStorage]) { for (let i = 0; i < st.length; i++) { const k = st.key(i);" +
        "   let v = null; try { v = JSON.parse(st.getItem(k)); } catch (e) { continue; }" +
        "   if (v && v.credentialType === 'AccessToken' && v.secret) toks.push({ k, target: String(v.target || ''), exp: Number(v.expiresOn || v.extendedExpiresOn || 0), client: v.clientId, secret: v.secret }); } }" +
        " const now = Date.now() / 1000;" +
        " const pick = res => toks.filter(t => t.exp > now + 60 && t.target.toLowerCase().includes(res)).sort((a, b) => b.target.length - a.target.length || b.exp - a.exp)[0];";

    internal static string CallJs(string url, string method, string? jsonBody) =>
        "(async () => {" + TokenJs +
        " const url = " + JsonSerializer.Serialize(url) + ", host = new URL(url).host;" +
        " const t = pick(host === 'graph.microsoft.com' ? 'graph.microsoft.com' : host) || (host.endsWith('teams.microsoft.com') ? pick('teams') : null);" +
        " if (!t) return { error: 'no_token', have: toks.map(x => x.target).slice(0, 30) };" +
        " const opt = { method: " + JsonSerializer.Serialize(method) + ", headers: { authorization: 'Bearer ' + t.secret, accept: 'application/json' } };" +
        (jsonBody != null ? " opt.headers['content-type'] = 'application/json'; opt.body = " + JsonSerializer.Serialize(jsonBody) + ";" : "") +
        " let r; try { r = await fetch(url, opt); } catch (e) { return { error: 'fetch_failed ' + ((e && e.message) || e) }; }" +
        " const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch (e) {}" +
        " return { status: r.status, scopes: t.target, json, text: json ? null : text.slice(0, 20000) }; })()";

    internal static async Task<JsonElement> CallAsync(IBrowser b, string url, string method, string? body, CancellationToken ct)
    {
        await EnsureOnTeamsAsync(b, ct);
        JsonElement r = default;
        // Right after the page opens the client may still be fetching its tokens.
        for (int i = 0; i < 8; i++)
        {
            r = await b.EvaluateAsync(CallJs(url, method, body), ct);
            if (!(r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e) && e.GetString() == "no_token")) break;
            await Task.Delay(2500, ct);
        }
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var err))
        {
            string code = err.GetString() ?? "";
            if (code == "no_token")
                throw new ToolException("no_token", "The Teams web client in the CO browser holds no token for " + new Uri(url).Host +
                    ". Run teams_probe to see which tokens it has. Tokens seen: " + (r.TryGetProperty("have", out var h) ? h.ToString() : ""));
            throw new ToolException("teams_error", "Teams: " + code);
        }
        int status = r.GetProperty("status").GetInt32();
        if (status is 401 or 403)
            throw new ToolException("no_access", $"Graph refused ({status}) with the Teams client's token (scopes: {Str(r, "scopes")}). " +
                (r.TryGetProperty("json", out var j) ? j.ToString() : ""));
        if (status >= 400)
            throw new ToolException("teams_error", $"HTTP {status}: " + (r.TryGetProperty("json", out var j2) && j2.ValueKind != JsonValueKind.Null ? j2.ToString() : Str(r, "text")));
        return r;
    }

    internal static string Str(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Plain(string? html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html ?? "", @"<(br|/p|/div)\s*/?>", "\n", RegexOptions.IgnoreCase), "<[^>]+>", "")).Trim();

    internal static string P(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind == JsonValueKind.Null ? "" : e.ToString();
    }

    internal static object Message(JsonElement m) => new
    {
        id = P(m, "id"),
        time = P(m, "createdDateTime"),
        from = P(m, "from", "user", "displayName") is { Length: > 0 } u ? u : P(m, "from", "application", "displayName"),
        text = Plain(P(m, "body", "content")) is var t && t.Length > 4000 ? t[..4000] + "…" : Plain(P(m, "body", "content")),
        subject = P(m, "subject"),
        attachments = m.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => new { name = P(x, "name"), url = P(x, "contentUrl") }).ToArray() : null,
        mentions = m.TryGetProperty("mentions", out var mm) && mm.ValueKind == JsonValueKind.Array
            ? mm.EnumerateArray().Select(x => P(x, "mentioned", "user", "displayName")).Where(x => x.Length > 0).ToArray() : null,
    };

    internal static ToolOutput Out(object o) => new() { Content = FsTools.Json(o), Untrusted = true };
}

internal abstract class TeamsTool : IDeviceTool
{
    protected readonly IBrowser B;
    protected TeamsTool(IBrowser b) => B = b;
    public abstract ToolDescriptor Descriptor { get; }
    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var p = new ActionPlan { Tool = Descriptor.Name, Class = RiskClass.R, Summary = Descriptor.Name };
        p.Scopes.Add(new Scope(ScopeKind.Domain, "graph.microsoft.com"));
        return p;
    }
    public abstract Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct);
}

internal sealed class TeamsProbe : TeamsTool
{
    public TeamsProbe(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "teams_probe", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Diagnostics of the Teams connection: opens Teams in the CO browser if needed and lists the access tokens " +
            "the Teams web client holds (resource and scopes, expiry; never the token values) and whether Graph answers /me.",
        InputSchema = Schema.Obj(),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        await TeamsTools.EnsureOnTeamsAsync(B, ct);
        await Task.Delay(3000, ct);
        var r = await B.EvaluateAsync("(async () => {" + TeamsTools.TokenJs +
            " const keys = []; for (const st of [localStorage, sessionStorage]) for (let i = 0; i < st.length; i++) keys.push(st.key(i));" +
            " let me = null; const g = pick('graph.microsoft.com');" +
            " if (g) { try { const r = await fetch('https://graph.microsoft.com/v1.0/me', { headers: { authorization: 'Bearer ' + g.secret } }); me = r.status + ' ' + (await r.text()).slice(0, 300); } catch (e) { me = 'error ' + e; } }" +
            " return { url: location.href, tokens: toks.map(t => ({ target: t.target, expires: new Date(t.exp * 1000).toISOString(), client: t.client })), graphMe: me," +
            " storageKeys: keys.length, encryptedCache: keys.some(k => /encrypt/i.test(k)) }; })()", ct);
        return new ToolOutput { Content = FsTools.Json(r) };
    }
}

// Teams chat service (what the Teams web client itself uses): the client's api.spaces.skype.com token is exchanged at
// authsvc for a Skype token, which reads conversations and messages at the regional chat service. Microsoft Graph
// is not used here: the Teams web client of this tenant holds no Graph token (teams_probe, 2026-10-15).
public static class TeamsChatService
{
    // Common part: token exchange (cached in the page for its lifetime), GET helper, HTML → text without executing it.
    private const string Prelude =
        TeamsTools.TokenJs +
        " const spaces = pick('api.spaces.skype.com');" +
        " if (!spaces) return { error: 'no_token', have: toks.map(x => x.target).slice(0, 30) };" +
        " let s = window.__coSkype;" +
        " if (!s || s.exp < Date.now() + 60000) {" +
        "   const a = await fetch('https://teams.microsoft.com/api/authsvc/v1.0/authz', { method: 'POST', headers: { authorization: 'Bearer ' + spaces.secret } });" +
        "   if (!a.ok) return { error: 'authz ' + a.status + ' ' + (await a.text()).slice(0, 300) };" +
        "   const j = await a.json();" +
        "   s = { token: j.tokens.skypeToken, exp: Date.now() + (Number(j.tokens.expiresIn) || 3600) * 1000," +
        "         chat: (j.regionGtms && j.regionGtms.chatService) || 'https://emea.ng.msg.teams.microsoft.com' };" +
        "   window.__coSkype = s; }" +
        " const h = { authentication: 'skypetoken=' + s.token, accept: 'application/json' };" +
        " const get = async u => { const r = await fetch(u, { headers: h }); if (!r.ok) throw new Error(r.status + ' ' + (await r.text()).slice(0, 300)); return r.json(); };" +
        // The Teams page enforces Trusted Types (no DOMParser/innerHTML): tags are stripped and entities decoded by hand.
        " const ent = { amp: '&', lt: '<', gt: '>', quot: '\"', apos: \"'\", nbsp: ' ' };" +
        " const text = x => String(x || '').replace(/<(br|\\/p|\\/div|\\/li)\\s*\\/?>/gi, '\\n').replace(/<[^>]*>/g, '')" +
        "   .replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);/gi, (m, e) => e[0] === '#' ? String.fromCodePoint(e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10)) : (ent[e.toLowerCase()] || m))" +
        "   .replace(/\\n{3,}/g, '\\n\\n').trim();" +
        " const conv = c => ({ id: c.id, topic: (c.threadProperties && c.threadProperties.topic) || '', type: (c.threadProperties && (c.threadProperties.productThreadType || c.threadProperties.threadType)) || c.id.split(':')[0]," +
        "   last: c.lastMessage ? { from: c.lastMessage.imdisplayname || '', time: c.lastMessage.composetime || c.lastMessage.originalarrivaltime || '', text: text(c.lastMessage.content).slice(0, 500) } : null });" +
        " const msg = m => ({ id: m.id, from: m.imdisplayname || '', time: m.composetime || m.originalarrivaltime || '', type: m.messagetype, text: text(m.content).slice(0, 4000)," +
        "   files: (() => { try { return JSON.parse((m.properties || {}).files || '[]').map(f => ({ name: f.fileName || f.title, url: f.objectUrl || (f.fileInfo && f.fileInfo.fileUrl) })); } catch (e) { return []; } })() });" +
        " const isText = m => /^(RichText|Text)/i.test(m.messagetype || '');" +
        " const convs = async n => (await get(s.chat + '/v1/users/ME/conversations?view=msnp24Equivalent&pageSize=' + n)).conversations || [];" +
        " const msgs = async (id, n) => ((await get(s.chat + '/v1/users/ME/conversations/' + encodeURIComponent(id) + '/messages?view=msnp24Equivalent|supportsMessageProperties&pageSize=' + n + '&startTime=1')).messages || []).filter(isText);";

    public static string ChatsJs(int top) =>
        "(async () => {" + Prelude + " try { return { chats: (await convs(" + top + ")).filter(c => !/^48:/.test(c.id)).map(conv) }; } catch (e) { return { error: String(e.message || e) }; } })()";

    public static string MessagesJs(string id, int top) =>
        "(async () => {" + Prelude + " try { return { messages: (await msgs(" + JsonSerializer.Serialize(id) + ", " + top + ")).map(msg) }; } catch (e) { return { error: String(e.message || e) }; } })()";

    // Word search over the latest messages of the most recent conversations (the tenant gives the web client no search token).
    public static string SearchJs(string query, int chats, int perChat, int max) =>
        "(async () => {" + Prelude +
        " const words = " + JsonSerializer.Serialize(query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + ";" +
        " const hits = []; let scanned = 0;" +
        " try { for (const c of (await convs(" + chats + ")).filter(c => !/^48:/.test(c.id))) {" +
        "   let list = []; try { list = await msgs(c.id, " + perChat + "); } catch (e) { continue; } scanned += list.length;" +
        "   for (const m of list) { const t = text(m.content); const low = (t + ' ' + (m.imdisplayname || '')).toLowerCase();" +
        "     if (words.every(w => low.includes(w))) hits.push({ chat: c.id, topic: (c.threadProperties && c.threadProperties.topic) || '', ...msg(m) });" +
        "     if (hits.length >= " + max + ") break; }" +
        "   if (hits.length >= " + max + ") break; } } catch (e) { return { error: String(e.message || e) }; }" +
        " return { hits, scanned, note: 'searched the latest " + perChat + " messages of the " + chats + " most recent chats' }; })()";

    public static async Task<JsonElement> RunAsync(IBrowser b, string js, CancellationToken ct)
    {
        await TeamsTools.EnsureOnTeamsAsync(b, ct);
        JsonElement r = default;
        for (int i = 0; i < 8; i++)   // right after the page opens the client may still be fetching its tokens
        {
            r = await b.EvaluateAsync(js, ct);
            if (!(r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e) && e.GetString() == "no_token")) break;
            await Task.Delay(2500, ct);
        }
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var err))
        {
            string code = err.GetString() ?? "";
            if (code == "no_token")
                throw new ToolException("sign_in_needed", "Teams in the CO browser is not signed in (no chat token). Open Teams in the CO browser once and sign in.");
            if (code.StartsWith("401") || code.StartsWith("authz 401"))
                throw new ToolException("sign_in_needed", "Teams session expired; reload Teams in the CO browser. " + code);
            throw new ToolException("teams_error", "Teams: " + code);
        }
        return r;
    }
}

internal sealed class TeamsChats : TeamsTool
{
    public TeamsChats(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "teams_chats", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Recent Teams conversations (1:1, group, meeting and channel threads), newest first, with topic and the " +
            "last message (sender, time, text). Use the id with teams_messages.",
        InputSchema = Schema.Obj(("top", "integer", "How many conversations, default 30, at most 100", false)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        int top = (int)Math.Clamp(Args.OptLong(a, "top", 30), 1, 100);
        var r = await TeamsChatService.RunAsync(B, TeamsChatService.ChatsJs(top), ct);
        return TeamsTools.Out(r.GetProperty("chats").Clone());
    }
}

internal sealed class TeamsMessages : TeamsTool
{
    public TeamsMessages(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "teams_messages", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Messages of a Teams conversation (id from teams_chats: chat or channel thread), newest first: sender, " +
            "time, text, shared files (SharePoint/OneDrive links: open with sharepoint_read).",
        InputSchema = Schema.Obj(
            ("chat_id", "string", "Conversation id from teams_chats (e.g. 19:…@thread.v2)", true),
            ("top", "integer", "How many messages, default 50, at most 200", false)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        int top = (int)Math.Clamp(Args.OptLong(a, "top", 50), 1, 200);
        var r = await TeamsChatService.RunAsync(B, TeamsChatService.MessagesJs(Args.Str(a, "chat_id"), top), ct);
        return TeamsTools.Out(r.GetProperty("messages").Clone());
    }
}

internal sealed class TeamsSearch : TeamsTool
{
    public TeamsSearch(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "teams_search", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Find Teams messages containing all given words (text or sender name) in the latest messages of the most " +
            "recent conversations. Returns conversation id and topic with each message. Widen chats/per_chat for older messages.",
        InputSchema = Schema.Obj(
            ("query", "string", "Words that must all occur (case-insensitive)", true),
            ("chats", "integer", "How many recent conversations to scan, default 40, at most 100", false),
            ("per_chat", "integer", "Latest messages per conversation, default 50, at most 200", false),
            ("max", "integer", "Most hits to return, default 50", false)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string q = Args.Str(a, "query").Trim();
        if (q.Length == 0) throw new ToolException("invalid_args", "query is empty.");
        var r = await TeamsChatService.RunAsync(B, TeamsChatService.SearchJs(q,
            (int)Math.Clamp(Args.OptLong(a, "chats", 40), 1, 100), (int)Math.Clamp(Args.OptLong(a, "per_chat", 50), 1, 200),
            (int)Math.Clamp(Args.OptLong(a, "max", 50), 1, 200)), ct);
        return TeamsTools.Out(r.Clone());
    }
}

internal sealed class TeamsFetch : TeamsTool
{
    public TeamsFetch(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "teams_fetch", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Read-only Microsoft Graph call with the Teams web client's session, for what the other teams_ tools do " +
            "not cover: GET https://graph.microsoft.com/v1.0/... (joined teams, channels, chat members, hosted contents). " +
            "Only GET; nothing is sent.",
        InputSchema = Schema.Obj(("url", "string", "https://graph.microsoft.com/v1.0/... (GET)", true)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string url = Args.Str(a, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !TeamsTools.AllowedApiHost(u.Host))
            throw new ToolException("invalid_args", "url must be https on graph.microsoft.com (or a Teams API host).");
        var r = await TeamsTools.CallAsync(B, url, "GET", null, ct);
        return TeamsTools.Out(r.TryGetProperty("json", out var j) && j.ValueKind != JsonValueKind.Null ? j.Clone() : TeamsTools.Str(r, "text"));
    }
}
