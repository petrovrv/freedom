// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Policy;

namespace CO.Corporate.Tools;

// ENGIE AI Studio (engie.ai) gateway access through the CO browser's live session.
//
// engie.ai is a React SPA whose primary sign-in is Okta (@okta/okta-auth-js). okta-auth-js keeps the tokens in
// localStorage under "okta-token-storage" in plain JSON, and okta auto-renews them while the page is open. The app's
// gateway calls set the request header "authorization" to the raw Okta access token (NOT a "Bearer " prefix) and
// "x-id-token" to the Okta id token. (A separate, encrypted MSAL cache holds only the secondary Microsoft-365 token
// used for the "x-m365-token" header on /responses; the model endpoints /chat/completions and /messages do not need it.)
//
// The app attaches those headers via a fetch wrapper held in React context, NOT a global window.fetch override, so a
// raw fetch() from the page is unauthenticated. engie_fetch therefore reads okta-token-storage itself (inside the
// engie.ai page, so the page/okta keeps it fresh) and sets authorization + x-id-token before calling the gateway —
// the same shape the app uses. We read the plaintext Okta tokens only; the encrypted MSAL cache is never touched.
public static partial class EngieTools
{
    public static IEnumerable<IDeviceTool> All(IBrowser browser) => new IDeviceTool[] { new EngieFetch(browser), new EngieChat(browser) };

    // Model family → gateway host + chat path (Responses API). Paths differ per gateway (mapped by probing):
    // azure = /responses, aws = /v1/responses. gcp (Gemini) not mapped yet.
    internal static (string url, string provider)? ChatEndpoint(string model, string? provider)
    {
        string p = (provider ?? "").Trim().ToLowerInvariant();
        if (p.Length == 0)
        {
            string m = model.Trim().ToLowerInvariant();
            if (m.StartsWith("gpt") || m.StartsWith("mistral") || m.StartsWith("o1") || m.StartsWith("o3")) p = "azure";
            else if (m.StartsWith("claude") || m.StartsWith("agent")) p = "aws";
            else if (m.StartsWith("gemini")) p = "gcp";
        }
        return p switch
        {
            "azure" => ("https://azure.aistudio.genai.digital.engie.com/responses", "azure"),
            "aws" => ("https://aws.aistudio.genai.digital.engie.com/v1/responses", "aws"),
            _ => null,
        };
    }

    internal const string Origin = "https://engie.ai";

    // engie.ai itself plus the three model gateways (Azure/OpenAI, AWS/Claude, GCP/Gemini) and the usage endpoint.
    internal static bool AllowedHost(string host) =>
        host.Equals("engie.ai", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".aistudio.genai.digital.engie.com", StringComparison.OrdinalIgnoreCase);

    internal static bool OnEngie(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("engie.ai", StringComparison.OrdinalIgnoreCase);

    // Make sure the CO browser sits on an authenticated engie.ai page (the app, hence the fetch patch, is mounted).
    internal static async Task EnsureOnEngieAsync(IBrowser b, CancellationToken ct)
    {
        string here = await b.CurrentUrlAsync(ct);
        if (!OnEngie(here)) here = await b.NavigateAsync(Origin + "/chat", ct);
        if (!OnEngie(here))
        {
            SetSession(EngieSessionState.SignInNeeded, "engie.ai needs sign-in");
            // 0.7.11: the ENGIE button in the CO chat composer replaces the Connectors button.
            throw new ToolException("sign_in_needed",
                "The CO browser is not signed in to engie.ai. Press the ENGIE button in the CO chat composer " +
                "(it opens engie.ai in the CO browser), complete the sign-in, then retry.");
        }
    }

    // 0.7.4: an idle engie.ai tab stops renewing its Okta token and the gateway answers 401 "Missing authorization".
    // Before each call the token's expiry is checked; an expired one is renewed by reloading the page (silent SSO
    // while the Okta session lives), and a 401 is retried once after a forced reload.
    // Optional diagnostic sink (may be null).
    public static Action<string>? Trace { get; set; }

    // Minimum remaining token lifetime (seconds) considered "fresh".
    private const int MinFreshSeconds = 120;

    // Polling parameters used after a reload.
    private static readonly TimeSpan RefreshPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(25);

    private const string SessionExpiredMessage =
        "engie.ai session expired: the page could not renew the sign-in silently. Press the ENGIE button in the CO chat composer (it opens engie.ai in the CO browser), complete the sign-in, then retry.";

    // Reads Okta token storage (sessionStorage first, then localStorage) and reports access-token state.
    internal const string TokenStateJs = """
        (async () => {
          const KEY = 'okta-token-storage';
          const read = (getStore) => {
            try {
              const s = getStore();
              const raw = s ? s.getItem(KEY) : null;
              return raw ? JSON.parse(raw) : null;
            } catch { return null; }
          };
          const hasAccess = (st) => !!(st && st.accessToken && typeof st.accessToken.accessToken === 'string' && st.accessToken.accessToken.length > 0);
          let st = read(() => window.sessionStorage);
          if (!hasAccess(st)) st = read(() => window.localStorage);
          if (!hasAccess(st)) return { has: false, secondsLeft: null };
          const exp = Number(st.accessToken.expiresAt);
          if (!Number.isFinite(exp)) return { has: true, secondsLeft: null };
          return { has: true, secondsLeft: Math.floor(exp - Date.now() / 1000) };
        })()
        """;

    // Returns seconds left on the access token, or null when no (usable) token is present.
    internal static async Task<int?> TokenSecondsLeftAsync(IBrowser b, CancellationToken ct)
    {
        JsonElement r = await b.EvaluateAsync(TokenStateJs, ct);
        if (r.ValueKind != JsonValueKind.Object) return null;

        if (!r.TryGetProperty("has", out JsonElement has) || has.ValueKind != JsonValueKind.True)
            return null;

        if (!r.TryGetProperty("secondsLeft", out JsonElement left) ||
            left.ValueKind != JsonValueKind.Number ||
            !left.TryGetDouble(out double secs))
            return null;

        // Clamp to int range to avoid overflow on odd values.
        return (int)Math.Clamp(Math.Floor(secs), int.MinValue, int.MaxValue);
    }

    // Ensures a non-expiring access token; reloads the app to trigger silent SSO renewal if needed.
    internal static async Task EnsureFreshTokenAsync(IBrowser b, CancellationToken ct, bool force = false)
    {
        await EnsureOnEngieAsync(b, ct);

        if (!force && await TryGetSecondsLeftAsync(b, ct) is int s && s >= MinFreshSeconds)
        {
            SetSession(EngieSessionState.Online, ""); // 0.7.11: state for the CO Web ENGIE toggle
            return;
        }

        Trace?.Invoke("engie: token expired/missing, reloading engie.ai");
        await b.NavigateAsync(Origin + "/chat", ct);

        // Poll until the app has re-acquired a fresh token or the timeout elapses.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < RefreshTimeout)
        {
            await Task.Delay(RefreshPollInterval, ct);
            if (await TryGetSecondsLeftAsync(b, ct) is int left && left >= MinFreshSeconds)
            {
                SetSession(EngieSessionState.Online, "");
                return;
            }
        }

        SetSession(EngieSessionState.SignInNeeded, "engie.ai needs sign-in");
        throw new ToolException("sign_in_needed", SessionExpiredMessage);
    }

    // Evaluates JS after ensuring a fresh token; retries once after a forced renewal on HTTP 401.
    internal static async Task<JsonElement> EvaluateAuthedAsync(IBrowser b, string js, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight); // keep-alive leaves the tab alone while a call runs
        try { return await EvaluateAuthedCoreAsync(b, js, ct); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private static async Task<JsonElement> EvaluateAuthedCoreAsync(IBrowser b, string js, CancellationToken ct)
    {
        try
        {
            await EnsureFreshTokenAsync(b, ct);
        }
        catch (ToolException ex) when (ex.Code == "sign_in_needed")
        {
            // 0.7.10: a real sign-in is needed: show the engie.ai tab and wait for the user. 75 s: with the 25 s
            // renewal attempt it stays inside Bridge's 120 s device-call timeout.
            await WaitForInteractiveSignInAsync(b, TimeSpan.FromSeconds(75), ct);
        }
        JsonElement r = await b.EvaluateAsync(js, ct);

        if (Is401(r))
        {
            Trace?.Invoke("engie: gateway returned 401, forcing token renewal and retrying");
            try
            {
                await EnsureFreshTokenAsync(b, ct, force: true);
            }
            catch (ToolException ex) when (ex.Code == "sign_in_needed")
            {
                // same wait as before the call: the session needs an interactive sign-in
                await WaitForInteractiveSignInAsync(b, TimeSpan.FromSeconds(75), ct);
            }
            r = await b.EvaluateAsync(js, ct);

            if (Is401(r))
                throw new ToolException("sign_in_needed", SessionExpiredMessage);
        }

        return r;
    }

    // Token probe that tolerates evaluation failures (e.g. page still loading); cancellation still propagates.
    private static async Task<int?> TryGetSecondsLeftAsync(IBrowser b, CancellationToken ct)
    {
        try
        {
            return await TokenSecondsLeftAsync(b, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // True when the result is an object carrying a numeric "status" equal to 401.
    private static bool Is401(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object &&
        r.TryGetProperty("status", out JsonElement status) &&
        status.ValueKind == JsonValueKind.Number &&
        status.TryGetInt32(out int code) &&
        code == 401;
}

internal sealed class EngieFetch : IDeviceTool
{
    private readonly IBrowser _b;
    public EngieFetch(IBrowser b) => _b = b;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "engie_fetch", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description =
            "Call the ENGIE AI Studio gateway with the CO browser's signed-in engie.ai session (the page attaches and " +
            "refreshes the token itself). url must be on engie.ai or *.aistudio.genai.digital.engie.com. Use GET " +
            "/v1/models to list models, POST a provider's /chat/completions (OpenAI/Azure, AWS/Claude) or /messages " +
            "with a JSON body. Returns the HTTP status and response body.",
        InputSchema = Schema.Obj(
            ("url", "string", "Full https URL on engie.ai or *.aistudio.genai.digital.engie.com", true),
            ("method", "string", "HTTP method, default POST", false),
            ("body", "object", "JSON request body (for POST)", false),
            ("headers", "object", "Extra request headers, e.g. provider beta headers", false)),
    };

    private static string Validate(JsonElement a)
    {
        string url = Args.Str(a, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new ToolException("invalid_args", "url must be an absolute https URL.");
        if (!EngieTools.AllowedHost(u.Host))
            throw new ToolException("invalid_args", "url host must be engie.ai or *.aistudio.genai.digital.engie.com.");
        return url;
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string url = Validate(a);
        string method = (Args.OptStr(a, "method") ?? "POST").ToUpperInvariant();
        var p = new ActionPlan { Tool = "engie_fetch", Class = RiskClass.R, NavigationDomain = BrowserTools.Domain(url), Summary = $"engie_fetch {method} {url}" };
        p.Scopes.Add(new Scope(ScopeKind.Domain, BrowserTools.Domain(url)));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string url = Validate(a);
        string method = (Args.OptStr(a, "method") ?? "POST").ToUpperInvariant();
        string bodyJson = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null
            ? b.GetRawText() : "null";
        string headersJson = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? h.GetRawText() : "{}";

        // Runs inside the engie.ai page. okta-auth-js (custom storage) keeps the tokens in sessionStorage under
        // "okta-token-storage" (falls back to localStorage), auto-renewed by okta. After a fresh page load the app
        // repopulates them via silent SSO, so poll briefly. Attach them the way the app does — authorization = raw
        // access token, x-id-token = id token. Use credentials:'omit': the gateway authorizes by bearer header, and
        // including cookies would break CORS (wildcard Access-Control-Allow-Origin is incompatible with credentials).
        string js =
            "(async () => {" +
            "  const url = " + JsonSerializer.Serialize(url) + ", method = " + JsonSerializer.Serialize(method) + ";" +
            "  const extra = " + headersJson + ", body = " + bodyJson + ";" +
            "  const headers = Object.assign({}, extra);" +
            "  const hasH = n => Object.keys(headers).some(k => k.toLowerCase() === n);" +
            "  let authed = false;" +
            "  const readOkta = () => {" +
            "    let raw = null;" +
            "    try { raw = sessionStorage.getItem('okta-token-storage'); } catch (e) {}" +
            "    if (!raw) { try { raw = localStorage.getItem('okta-token-storage'); } catch (e) {} }" +
            "    if (!raw) return null;" +
            "    try { return JSON.parse(raw); } catch (e) { return null; }" +
            "  };" +
            "  let tok = readOkta();" +
            "  for (let i = 0; i < 30 && !(tok && tok.accessToken && tok.accessToken.accessToken); i++) {" +
            "    await new Promise(res => setTimeout(res, 400)); tok = readOkta();" +
            "  }" +
            "  try {" +
            "    const at = tok && tok.accessToken && tok.accessToken.accessToken;" +
            "    const idt = tok && tok.idToken && tok.idToken.idToken;" +
            "    if (at && !hasH('authorization')) { headers['authorization'] = at; authed = true; }" +
            "    if (idt && !hasH('x-id-token')) headers['x-id-token'] = idt;" +
            "  } catch (e) {}" +
            "  const opt = { method, headers, credentials: 'omit' };" +
            "  if (body !== null && method !== 'GET' && method !== 'HEAD') {" +
            "    if (!hasH('content-type')) headers['content-type'] = 'application/json';" +
            "    opt.body = typeof body === 'string' ? body : JSON.stringify(body);" +
            "  }" +
            "  let r;" +
            "  try { r = await fetch(url, opt); } catch (e) { return { error: 'fetch_failed ' + ((e && e.message) || e), authed }; }" +
            "  const text = await r.text();" +
            "  return { status: r.status, ok: r.ok, body: text.slice(0, 180000), authed };" +
            "})()";

        JsonElement r = await EngieTools.EvaluateAuthedAsync(_b, js, ct);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var err))
        {
            bool au = r.TryGetProperty("authed", out var ae) && ae.ValueKind == JsonValueKind.True;
            throw new ToolException("engie_error", "engie_fetch: " + (err.GetString() ?? "unknown error") + (au ? " (token attached)" : " (no okta token found)"));
        }

        int status = r.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
        bool authed = r.TryGetProperty("authed", out var av) && av.ValueKind == JsonValueKind.True;
        string respBody = r.TryGetProperty("body", out var bd) ? bd.GetString() ?? "" : "";
        var sb = new StringBuilder();
        sb.AppendLine($"HTTP {status} {method} {url}  (auth: {(authed ? "okta token attached" : "none")})").AppendLine();
        sb.Append(respBody);
        return new ToolOutput { Content = sb.ToString(), Untrusted = true };
    }
}

// Convenience over engie_fetch: ask one ENGIE model a prompt and get just the reply text. Picks the gateway and path
// by model family, builds the Responses API body, attaches the Okta token, and parses the SSE stream into plain text.
// NOTE: ENGIE's gateway is a locked chat assistant — the model cannot use your files or custom tools and its system
// prompt is fixed by ENGIE. So read/write documents yourself (fs_* / sharepoint_*) and pass the text in via `prompt`.
internal sealed class EngieChat : IDeviceTool
{
    private readonly IBrowser _b;
    public EngieChat(IBrowser b) => _b = b;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "engie_chat", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description =
            "Ask one ENGIE model a prompt and get its reply text. model: e.g. gpt-6-luna / gpt-6-sol / mistral-large-3 " +
            "(azure) or claude-opus-5.5 / claude-sonnet-5 / claude-haiku-4.5 (aws); the gateway is chosen automatically. " +
            "The model has NO access to your files and a fixed ENGIE system prompt — read documents with fs_*/sharepoint_* " +
            "yourself and include the text in prompt. Optional web_search=true lets the model search the web.",
        InputSchema = Schema.Obj(
            ("model", "string", "ENGIE model id, e.g. gpt-6-luna or claude-opus-5.5", true),
            ("prompt", "string", "The full prompt / question (include any document text the model should work on)", true),
            ("provider", "string", "Override gateway: azure or aws (default: inferred from model)", false),
            ("web_search", "boolean", "Allow the model to use ENGIE's web search tool", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string model = Args.Str(a, "model");
        var p = new ActionPlan { Tool = "engie_chat", Class = RiskClass.R, Summary = $"engie_chat {model}" };
        p.Scopes.Add(new Scope(ScopeKind.Domain, "aistudio.genai.digital.engie.com"));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string model = Args.Str(a, "model");
        string prompt = Args.Str(a, "prompt");
        string? provider = Args.OptStr(a, "provider");
        bool web = Args.OptBool(a, "web_search", false);

        var ep = EngieTools.ChatEndpoint(model, provider);
        if (ep == null)
            throw new ToolException("invalid_args",
                $"No gateway for model '{model}'. Use a gpt-*/mistral-* (azure) or claude-*/agent-* (aws) model; gemini is not wired yet.");
        string url = ep.Value.url;

        // 0.7.12: the gateway firewall drops prompts with loopback URLs or odd symbols (fetch_failed): clean the
        // prompt first; after fetch_failed retry once with strict cleanup; tell the caller what was changed.
        static string? Err(JsonElement r) =>
            r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
        var retried = false;
        var s = EngiePromptSanitizer.Sanitize(prompt);
        JsonElement r = await EngieTools.EvaluateAuthedAsync(_b, BuildScript(url, model, s.Text, web), ct);
        if (Err(r)?.StartsWith("fetch_failed", StringComparison.Ordinal) == true)
        {
            retried = true;
            s = EngiePromptSanitizer.Sanitize(prompt, strict: true);
            r = await EngieTools.EvaluateAuthedAsync(_b, BuildScript(url, model, s.Text, web), ct);
        }
        if (Err(r) is { } error)
            throw new ToolException("engie_error",
                "engie_chat: " + error + (retried ? " (also after strict cleanup of the prompt)" : ""));
        string note = retried
            ? $"[co: prompt cleaned for the ENGIE gateway after fetch_failed (strict retry): {s.Urls} local URL(s) rewritten, {s.Chars} char(s) escaped]"
            : s.Urls + s.Chars > 0
                ? $"[co: prompt cleaned for the ENGIE gateway: {s.Urls} local URL(s) rewritten, {s.Chars} char(s) escaped]"
                : "";

        bool ok = r.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
        int status = r.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
        string text = r.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
        if (!ok)
            throw new ToolException("engie_error", $"engie_chat {model}: HTTP {status}: {text}");
        return new ToolOutput { Content = (note == "" ? "" : note + "\n\n") + text + EngieChatUsage.Trailer(model, r), Untrusted = true };
    }

    private static string BuildScript(string url, string model, string prompt, bool web)
    {
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = model,
            ["input"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject { ["type"] = "input_text", ["text"] = prompt },
                    },
                },
            },
        };
        if (web)
            body["tools"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["type"] = "web_search" } };

        string js =
            "(async () => {" +
            "  const url = " + JsonSerializer.Serialize(url) + ", bodyObj = " + body.ToJsonString() + ";" +
            "  const headers = { 'content-type': 'application/json', 'accept': 'text/event-stream' };" +
            "  let authed = false;" +
            "  const readOkta = () => { let r = null; try { r = sessionStorage.getItem('okta-token-storage'); } catch (e) {}" +
            "    if (!r) { try { r = localStorage.getItem('okta-token-storage'); } catch (e) {} }" +
            "    if (!r) return null; try { return JSON.parse(r); } catch (e) { return null; } };" +
            "  let tok = readOkta();" +
            "  for (let i = 0; i < 30 && !(tok && tok.accessToken && tok.accessToken.accessToken); i++) { await new Promise(x => setTimeout(x, 400)); tok = readOkta(); }" +
            "  const at = tok && tok.accessToken && tok.accessToken.accessToken, idt = tok && tok.idToken && tok.idToken.idToken;" +
            "  if (at) { headers['authorization'] = at; authed = true; } if (idt) headers['x-id-token'] = idt;" +
            "  let r; try { r = await fetch(url, { method: 'POST', headers, credentials: 'omit', body: JSON.stringify(bodyObj) }); }" +
            "  catch (e) { return { error: 'fetch_failed ' + ((e && e.message) || e), authed }; }" +
            "  const raw = await r.text();" +
            "  if (!r.ok) return { status: r.status, ok: false, text: raw.slice(0, 8000), authed };" +
            "  let out = ''; const done = []; let usage = null;" +
            "  for (const line of raw.split('\\n')) { const s = line.trim(); if (!s.startsWith('data:')) continue;" +
            "    const j = s.slice(5).trim(); if (j === '[DONE]') continue; let d; try { d = JSON.parse(j); } catch (e) { continue; }" +
            "    if (d.type === 'response.output_text.delta' && typeof d.delta === 'string') out += d.delta;" +
            "    else if (d.type === 'response.output_text.done' && typeof d.text === 'string') done.push(d.text);" +
            "    else if (d.type === 'response.completed' && d.response && d.response.usage) usage = d.response.usage; }" +
            "  return { status: r.status, ok: true, text: (out || done.join('\\n')), usage, authed };" +
            "})()";
        return js;
    }
}

// Token counts of the gateway (Responses API usage) as an HTML comment after the reply: Bridge strips it, adds it
// to its usage ledger (dispatcher savings) and passes only the text on. Older Bridges leave an invisible comment.
public static class EngieChatUsage
{
    public static string Trailer(string model, JsonElement r)
    {
        if (!r.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return "";
        long Get(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        var o = new { model, input = Get("input_tokens"), output = Get("output_tokens") };
        return "\n\n<!-- co:usage " + JsonSerializer.Serialize(o) + " -->";
    }
}
