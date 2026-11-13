// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.8.2: automatic engie.ai sign-in through the Okta Identity Engine API (the flow the owner's Bor_Connect.py uses),
// so an expired engie.ai session renews without the user, who only approves the Okta Verify push on the phone.
//
// How it fits together: this runs entirely in an HttpClient inside CO Desktop. The password is read from Windows
// Credential Manager and sent only to the configured Okta org - it never reaches the browser or any agent. The flow
// establishes an Okta org session (its cookies land in the local jar); the caller then copies ONLY those Okta session
// cookies into the engie.ai browser tab and reloads engie.ai, which completes its own OIDC code+PKCE sign-in with the
// session now present. During the push, Okta returns a number the user must pick in Okta Verify; it is surfaced to the
// user (tray + phone push) through the onNumber callback.
//
// The exact bootstrap (authorize URL) and cookie hand-over can only be confirmed against a live expired engie.ai
// session; until then the previous behaviour (interactive FastPass in the tab) remains the ultimate fallback.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CO.Corporate.SharePoint;

// A number challenge to show the user ("<id>:<number>"); id changes per attempt so each number is surfaced once.
public sealed record OktaNumberChallenge(string Id, string Number);

public static class EngieOktaIdx
{
    // engie.ai's public OIDC client (from the CO browser history); only used to obtain a login page + state token.
    private const string EngieClientId = "0oaja24g7nekUXflo0i7";
    private const string EngieRedirect = "https://engie.ai";
    private const string EngieScope = "openid profile email org";

    private static readonly TimeSpan PushWait = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly MediaTypeHeaderValue IdxContentType = new("application/json");

    public sealed class Result
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public IReadOnlyList<BrowserCookieSet> OktaCookies { get; init; } = Array.Empty<BrowserCookieSet>();
    }

    // Runs the IDX flow against the Okta org. onNumber is called once when the push number is known; onTrace logs
    // steps (never secrets). Returns the Okta session cookies to copy into the browser, or an error.
    public static async Task<Result> SignInAsync(
        string oktaBase, string username, string password,
        Action<OktaNumberChallenge> onNumber, Action<string> onTrace, CancellationToken ct,
        HttpMessageHandler? handler = null, CookieContainer? cookieJar = null)
    {
        var host = new Uri(oktaBase).Host;
        var jar = cookieJar ?? new CookieContainer();
        bool ownHandler = handler == null;
        handler ??= new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true, UseCookies = true };
        using var http = new HttpClient(handler, ownHandler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CO-Desktop");

        try
        {
            // 1. A throwaway OIDC authorize to get the Okta login page + state token (and the transaction cookies).
            string authorize = oktaBase + "/oauth2/default/v1/authorize"
                + "?client_id=" + Uri.EscapeDataString(EngieClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(EngieRedirect)
                + "&response_type=code&scope=" + Uri.EscapeDataString(EngieScope)
                + "&state=" + Guid.NewGuid().ToString("N") + "&nonce=" + Guid.NewGuid().ToString("N");
            using var page = await http.GetAsync(authorize, ct).ConfigureAwait(false);
            string html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (page.RequestMessage?.RequestUri?.Host.Contains("login/cert") == true)
                return Fail("engie.ai Okta requires a client certificate here; sign in in the CO browser.");
            string? stateToken = ExtractStateToken(html);
            if (stateToken == null)
                return Fail("Could not read the Okta state token (the sign-in page looked unexpected).");
            onTrace("Okta IDX: login page loaded");

            // 2. introspect
            var t = await PostIdxAsync(http, oktaBase + "/idp/idx/introspect", new { stateToken }, ct).ConfigureAwait(false);

            // 3. decline the Okta Verify loopback/FastPass probe so Okta offers the push factor instead
            t = await CancelAuthenticatorChallengeAsync(http, t, ct).ConfigureAwait(false);

            if (!HasRemediation(t))
                return Fail("Okta returned no sign-in step (remediation).");

            // 4. identify (username), then password
            t = await SubmitAsync(http, t, "identify", new Dictionary<string, object> { ["identifier"] = username }, ct).ConfigureAwait(false);
            if (Messages(t) is { Length: > 0 } m1) return Fail("Okta rejected the user name: " + m1);
            onTrace("Okta IDX: user name accepted");

            t = await SubmitPasswordAsync(http, t, password, ct).ConfigureAwait(false);
            if (Messages(t) is { Length: > 0 } m2) return Fail("Okta rejected the password: " + m2);
            onTrace("Okta IDX: password accepted");

            // 5. choose Okta Verify push (if a factor choice is offered)
            t = await SelectPushAsync(http, t, ct).ConfigureAwait(false);
            if (Messages(t) is { Length: > 0 } m3) return Fail("Okta: " + m3);

            // 6. surface the number (Okta may return it here or only in a poll response), then poll until approved
            string? lastNumber = null;
            void Surface(JsonElement el)
            {
                string? n = NumberChallenge(el);
                if (n == null || n == lastNumber) return;
                lastNumber = n;
                onNumber(new OktaNumberChallenge(Guid.NewGuid().ToString("N"), n));
                onTrace("Okta IDX: push sent (number " + n + ")");
            }
            Surface(t);
            if (lastNumber == null) onTrace("Okta IDX: push sent");

            t = await PollAsync(http, t, Surface, ct).ConfigureAwait(false);
            if (Messages(t) is { Length: > 0 } m4) return Fail("Okta Verify: " + m4);
            if (!TryGetProp(t, "success", out var success))
                return Fail("The Okta sign-in did not complete (no success).");
            onTrace("Okta IDX: push approved");

            // 7. follow the success redirect so Okta sets the org session cookie in the jar
            string href = Str(success, "href") ?? "";
            if (href.Length > 0)
            {
                using var done = await http.GetAsync(href, ct).ConfigureAwait(false);
                _ = await done.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            // 8. hand the Okta session cookies back for the browser (session cookies only, this org host)
            var cookies = new List<BrowserCookieSet>();
            foreach (Cookie c in jar.GetCookies(new Uri(oktaBase)))
            {
                if (c.Expired) continue;
                cookies.Add(new BrowserCookieSet(c.Name, c.Value, string.IsNullOrEmpty(c.Domain) ? host : c.Domain,
                    string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure, c.HttpOnly));
            }
            if (cookies.Count == 0)
                return Fail("Signed in to Okta but found no session cookie to carry to engie.ai.");

            return new Result { Ok = true, OktaCookies = cookies };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }

        static Result Fail(string msg) => new() { Ok = false, Error = msg };
    }

    // ---- IDX helpers ----

    private static async Task<JsonElement> PostIdxAsync(HttpClient http, string url, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = IdxContentType;
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Accept", "application/json; okta-version=1.0.0");
        using var r = await http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // The initial Okta login HTML carries: var stateToken = 'xxx';  (sometimes JS-escaped).
    private static string? ExtractStateToken(string html)
    {
        var m = Regex.Match(html, "var stateToken\\s*=\\s*'([^']*)'");
        if (!m.Success) m = Regex.Match(html, "var stateToken\\s*=\\s*\"([^\"]*)\"");
        if (!m.Success) return null;
        return Regex.Unescape(m.Groups[1].Value);
    }

    private static async Task<JsonElement> CancelAuthenticatorChallengeAsync(HttpClient http, JsonElement t, CancellationToken ct)
    {
        if (!TryGetProp(t, "authenticatorChallenge", out var ac)) return t;
        if (!TryGetProp(ac, "value", out var v)) return t;
        if (!TryGetProp(v, "cancel", out var cancel)) return t;
        string href = Str(cancel, "href") ?? "";
        if (href.Length == 0) return t;
        // The cancel form has a field (usually stateHandle); echo its first value and give Okta the reason.
        var body = new Dictionary<string, object> { ["reason"] = "OV_UNREACHABLE_BY_LOOPBACK" };
        if (TryGetProp(cancel, "value", out var form) && form.ValueKind == JsonValueKind.Array && form.GetArrayLength() > 0)
        {
            var first = form[0];
            string name = Str(first, "name") ?? "";
            string value = Str(first, "value") ?? "";
            if (name.Length > 0) body[name] = value;
        }
        return await PostIdxAsync(http, href, body, ct).ConfigureAwait(false);
    }

    private static bool HasRemediation(JsonElement t) =>
        TryGetProp(t, "remediation", out var rem) && TryGetProp(rem, "value", out var v)
        && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;

    private static bool TryFindRemediation(JsonElement t, string name, out JsonElement found)
    {
        found = default;
        if (!TryGetProp(t, "remediation", out var rem) || !TryGetProp(rem, "value", out var v) || v.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var e in v.EnumerateArray())
            if (Str(e, "name") == name) { found = e; return true; }
        return false;
    }

    private static JsonElement FirstRemediation(JsonElement t)
    {
        TryGetProp(t, "remediation", out var rem);
        TryGetProp(rem, "value", out var v);
        return v[0];
    }

    private static string StateHandle(JsonElement remediationEntry, JsonElement whole)
    {
        if (TryGetProp(remediationEntry, "value", out var form) && form.ValueKind == JsonValueKind.Array)
            foreach (var f in form.EnumerateArray())
                if (Str(f, "name") == "stateHandle") return Str(f, "value") ?? "";
        return Str(whole, "stateHandle") ?? "";
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient http, JsonElement t, string remediationName,
        Dictionary<string, object> fields, CancellationToken ct)
    {
        JsonElement entry = TryFindRemediation(t, remediationName, out var e) ? e : FirstRemediation(t);
        string href = Str(entry, "href") ?? "";
        fields["stateHandle"] = StateHandle(entry, t);
        return await PostIdxAsync(http, href, fields, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> SubmitPasswordAsync(HttpClient http, JsonElement t, string password, CancellationToken ct)
    {
        JsonElement entry = TryFindRemediation(t, "challenge-authenticator", out var e) ? e : FirstRemediation(t);
        string href = Str(entry, "href") ?? "";
        var body = new Dictionary<string, object>
        {
            ["credentials"] = new Dictionary<string, object> { ["passcode"] = password },
            ["stateHandle"] = StateHandle(entry, t),
        };
        return await PostIdxAsync(http, href, body, ct).ConfigureAwait(false);
    }

    // If Okta offers a factor choice, pick Okta Verify push; otherwise the flow is already on the push challenge.
    private static async Task<JsonElement> SelectPushAsync(HttpClient http, JsonElement t, CancellationToken ct)
    {
        if (TryGetProp(t, "success", out _)) return t;
        if (!TryFindRemediation(t, "select-authenticator-authenticate", out var entry)) return t;

        string? authenticatorId = FindOktaVerifyPushId(entry);
        if (authenticatorId == null)
            return t;   // leave it; the caller reports if no push appears

        string href = Str(entry, "href") ?? "";
        var body = new Dictionary<string, object>
        {
            ["authenticator"] = new Dictionary<string, object> { ["id"] = authenticatorId, ["methodType"] = "push" },
            ["stateHandle"] = StateHandle(entry, t),
        };
        return await PostIdxAsync(http, href, body, ct).ConfigureAwait(false);
    }

    private static string? FindOktaVerifyPushId(JsonElement entry)
    {
        // remediation "value" -> field "authenticator" -> options[] -> {label, value:{form:{value:[{name:id,value},{name:methodType,options:[push,...]}]}}}
        if (!TryGetProp(entry, "value", out var fields) || fields.ValueKind != JsonValueKind.Array) return null;
        foreach (var f in fields.EnumerateArray())
        {
            if (Str(f, "name") != "authenticator") continue;
            if (!TryGetProp(f, "options", out var options) || options.ValueKind != JsonValueKind.Array) continue;
            foreach (var opt in options.EnumerateArray())
            {
                string label = Str(opt, "label") ?? "";
                if (label.IndexOf("Okta Verify", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // the id lives in the option's nested form value named "id"
                if (TryGetProp(opt, "value", out var ov) && TryGetProp(ov, "form", out var form)
                    && TryGetProp(form, "value", out var fv) && fv.ValueKind == JsonValueKind.Array)
                    foreach (var x in fv.EnumerateArray())
                        if (Str(x, "name") == "id") return Str(x, "value");
            }
        }
        return null;
    }

    private static string? NumberChallenge(JsonElement t)
    {
        if (!TryGetProp(t, "currentAuthenticator", out var ca)) return null;
        if (!TryGetProp(ca, "value", out var v)) return null;
        if (!TryGetProp(v, "contextualData", out var cd)) return null;
        return Str(cd, "correctAnswer");
    }

    private static async Task<JsonElement> PollAsync(HttpClient http, JsonElement t, Action<JsonElement> onResponse, CancellationToken ct)
    {
        if (TryGetProp(t, "success", out _)) return t;
        if (!TryFindRemediation(t, "challenge-poll", out var poll)) return t;
        string href = Str(poll, "href") ?? "";
        var body = new Dictionary<string, object> { ["stateHandle"] = StateHandle(poll, t) };

        var deadline = DateTime.UtcNow + PushWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            t = await PostIdxAsync(http, href, body, ct).ConfigureAwait(false);
            onResponse(t);   // the number challenge may first appear in a poll response
            if (TryGetProp(t, "success", out _)) return t;
            if (Messages(t) is { Length: > 0 }) return t;
            if (!TryFindRemediation(t, "challenge-poll", out poll)) return t;
            href = Str(poll, "href") ?? "";
            body["stateHandle"] = StateHandle(poll, t);
        }
        return t;
    }

    // ---- small JSON utilities ----

    private static bool TryGetProp(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }

    private static string? Str(JsonElement e, string name) =>
        TryGetProp(e, name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // First Okta message text, or "" when none.
    private static string Messages(JsonElement t)
    {
        if (!TryGetProp(t, "messages", out var msgs) || !TryGetProp(msgs, "value", out var v) || v.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var m in v.EnumerateArray())
        {
            string s = Str(m, "message") ?? "";
            if (s.Length > 0) return s;
        }
        return "";
    }
}
