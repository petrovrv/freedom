// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CO.Core.Auth;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Tools;

namespace CO.Corporate.SharePoint;

// Password sign-in used only when the CO browser cannot renew the session. The password is kept in Credential
// Manager (CredentialTarget) on this computer and goes nowhere but the Okta org below.
public sealed class OktaFallback
{
    public const string CredentialTarget = "CO.Desktop.Okta";
    public string OktaUrl { get; set; } = "";   // https://<org>.okta.com or .okta-emea.com
    public string AppLink { get; set; } = "";   // optional: Okta dashboard link of the Microsoft 365 / SharePoint app
    public string UserName { get; set; } = "";
    public bool Configured => OktaUrl.Length > 0 && UserName.Length > 0;
    // Enough to try the office path: Okta Desktop SSO / Kerberos needs no password, only where to go.
    public bool CanTryIntegrated => AppLink.Length > 0 || UserName.Contains('@');
}

public sealed record SpEntry(string Name, bool IsDir, long? Size, DateTime? Modified, string ServerRelative, int? Items = null);
public sealed record SpHit(string ServerRelative, string Summary);

// SharePoint Online over plain HTTP with the user's session cookies (FedAuth/rtFa). The cookies come from the CO
// browser; when they expire the browser sign-in runs (Microsoft → Okta, see SharePointApi.SignInAsync), and when
// that cannot finish, the Okta password fallback (same flow as the user's Bor_Connect.py: /api/v1/authn, push,
// sessionCookieRedirect, WS-Fed/SAML auto-forms).
public sealed class SharePointService
{
    public const long MaxDownloadBytes = TextExtractor.MaxSourceBytes;

    private readonly IBrowser? _browser;
    private readonly ISecretStore? _secrets;
    private readonly string? _settingsFile;
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _api;       // REST calls: redirects to a login page are auth failures
    private readonly HttpClient _flow;      // sign-in flow: follows redirects
    private readonly SemaphoreSlim _signIn = new(1, 1);
    private readonly bool _external;        // tests: the handler stands in for a signed-in SharePoint
    private readonly Dictionary<string, (string value, DateTime until)> _digests = new(StringComparer.OrdinalIgnoreCase);

    public OktaFallback Okta { get; private set; } = new();
    public TimeSpan PushWait { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan PushPoll { get; set; } = TimeSpan.FromSeconds(2);
    public CookieContainer Cookies => _jar;
    public string LastSignIn { get; private set; } = "";   // which way the session was last renewed

    public SharePointService(IBrowser? browser, ISecretStore? secrets, string? settingsFile, HttpMessageHandler? handler = null, bool signedIn = true)
    {
        _browser = browser;
        _secrets = secrets;
        _settingsFile = settingsFile;
        _external = handler != null && signedIn;
        if (settingsFile != null && File.Exists(settingsFile))
            Okta = JsonSerializer.Deserialize<OktaFallback>(File.ReadAllText(settingsFile)) ?? new();
        _api = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = _jar, AllowAutoRedirect = false }, handler == null)
            { Timeout = TimeSpan.FromSeconds(100) };
        // UseDefaultCredentials: in the office network Okta Desktop SSO (and ADFS-style Windows sign-in) accept the
        // Windows logon by Kerberos/NTLM, so no password and no push are needed there.
        _flow = handler != null ? new HttpClient(handler, false)
            : new HttpClient(new HttpClientHandler { CookieContainer = _jar, AllowAutoRedirect = true, UseDefaultCredentials = true });
        _flow.Timeout = TimeSpan.FromSeconds(60);
        _flow.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CO-Desktop");
    }

    public bool HasPassword => _secrets?.Read(OktaFallback.CredentialTarget) is { Length: > 0 };

    public void SaveOkta(OktaFallback o, string? password)
    {
        Okta = o;
        if (_settingsFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (_secrets != null && password != null)
        {
            if (password.Length == 0) _secrets.Delete(OktaFallback.CredentialTarget);
            else _secrets.Write(OktaFallback.CredentialTarget, password);
        }
    }

    // ------------------------------------------------------------------ files

    public async Task<List<SpEntry>> ListAsync(SharePointPath p, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(p, "/_api/web/GetFolderByServerRelativePath(decodedurl=@p)?@p=" + Arg(p.ServerRelative)
            + "&$select=Name,ServerRelativeUrl&$expand=Folders,Files", ct, notFound: "Directory not found.");
        var list = new List<SpEntry>();
        foreach (var f in doc.RootElement.GetProperty("Folders").EnumerateArray())
        {
            string name = f.GetProperty("Name").GetString()!;
            if (name == "Forms" && p.ServerRelative.Count(c => c == '/') <= 4) continue;   // library system folder
            list.Add(new SpEntry(name, true, null, Date(f, "TimeLastModified"), f.GetProperty("ServerRelativeUrl").GetString()!, Int(f, "ItemCount")));
        }
        foreach (var f in doc.RootElement.GetProperty("Files").EnumerateArray())
            list.Add(new SpEntry(f.GetProperty("Name").GetString()!, false, long.TryParse(Str(f, "Length"), out long n) ? n : null,
                Date(f, "TimeLastModified"), f.GetProperty("ServerRelativeUrl").GetString()!));
        return list;
    }

    // null when nothing exists at the path.
    public async Task<SpEntry?> StatAsync(SharePointPath p, CancellationToken ct)
    {
        using (var r = await SendAsync(p, () => Get(p, "/_api/web/GetFileByServerRelativePath(decodedurl=@p)?@p=" + Arg(p.ServerRelative)
                   + "&$select=Name,Length,TimeCreated,TimeLastModified,ServerRelativeUrl"), ct))
            if (r.IsSuccessStatusCode)
            {
                using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                var e = d.RootElement;
                return new SpEntry(Str(e, "Name")!, false, long.TryParse(Str(e, "Length"), out long n) ? n : null, Date(e, "TimeLastModified"), Str(e, "ServerRelativeUrl")!);
            }
        using (var r = await SendAsync(p, () => Get(p, "/_api/web/GetFolderByServerRelativePath(decodedurl=@p)?@p=" + Arg(p.ServerRelative)
                   + "&$select=Name,Exists,ItemCount,TimeLastModified,ServerRelativeUrl"), ct))
            if (r.IsSuccessStatusCode)
            {
                using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                var e = d.RootElement;
                if (e.TryGetProperty("Exists", out var ex) && ex.ValueKind == JsonValueKind.False) return null;
                return new SpEntry(Str(e, "Name")!, true, null, Date(e, "TimeLastModified"), Str(e, "ServerRelativeUrl")!, Int(e, "ItemCount"));
            }
        return null;
    }

    public async Task<byte[]> DownloadAsync(SharePointPath p, CancellationToken ct)
    {
        using var r = await SendAsync(p, () => Get(p, "/_api/web/GetFileByServerRelativePath(decodedurl=@p)/$value?@p=" + Arg(p.ServerRelative)), ct);
        await Ensure(r, "File not found.", ct);
        if (r.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new ToolException("too_large", $"File is {r.Content.Headers.ContentLength} bytes; limit {MaxDownloadBytes}.");
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task UploadAsync(SharePointPath file, byte[] content, CancellationToken ct)
    {
        string parent = file.ServerRelative[..file.ServerRelative.LastIndexOf('/')];
        await EnsureFolderAsync(file with { ServerRelative = parent }, ct);
        using var r = await PostAsync(file, "/_api/web/GetFolderByServerRelativePath(decodedurl=@f)/Files/AddUsingPath(decodedurl=@n,overwrite=true)?@f="
            + Arg(parent) + "&@n=" + Arg(file.Name), new ByteArrayContent(content), ct);
        await Ensure(r, "Target folder not found.", ct);
    }

    public async Task EnsureFolderAsync(SharePointPath folder, CancellationToken ct)
    {
        if (await StatAsync(folder, ct) is { IsDir: true }) return;
        string parent = folder.ServerRelative[..folder.ServerRelative.LastIndexOf('/')];
        if (parent.Length > folder.Web.Length) await EnsureFolderAsync(folder with { ServerRelative = parent }, ct);
        using var r = await PostAsync(folder, "/_api/web/Folders/AddUsingPath(decodedurl=@p)?@p=" + Arg(folder.ServerRelative), null, ct);
        await Ensure(r, "Parent folder not found.", ct);
    }

    public async Task MoveAsync(SharePointPath from, SharePointPath to, CancellationToken ct)
    {
        await EnsureFolderAsync(to with { ServerRelative = to.ServerRelative[..to.ServerRelative.LastIndexOf('/')] }, ct);
        using var r = await PostAsync(from, "/_api/web/GetFileByServerRelativePath(decodedurl=@s)/MoveToUsingPath(DestPath=@d,moveOperations=0)?@s="
            + Arg(from.ServerRelative) + "&@d=" + Uri.EscapeDataString("{'DecodedUrl':'" + to.ServerRelative.Replace("'", "\\'") + "'}"), null, ct);
        if (r.StatusCode == HttpStatusCode.BadRequest)
        {
            // Older farms: classic moveto with a server-relative URL.
            using var r2 = await PostAsync(from, "/_api/web/GetFileByServerRelativeUrl(" + Quote(from.ServerRelative) + ")/moveto(newurl="
                + Quote(to.ServerRelative) + ",flags=0)", null, ct);
            await Ensure(r2, "Source file not found.", ct);
            return;
        }
        await Ensure(r, "Source file not found.", ct);
    }

    // To the site Recycle Bin, so the user can restore it.
    public async Task RecycleAsync(SharePointPath p, CancellationToken ct)
    {
        using var r = await PostAsync(p, "/_api/web/GetFileByServerRelativePath(decodedurl=@p)/recycle()?@p=" + Arg(p.ServerRelative), null, ct);
        await Ensure(r, "File not found.", ct);
    }

    // SharePoint search (full text, documents' content included) limited to the folder.
    public async Task<List<SpHit>> SearchAsync(SharePointPath folder, string query, int max, CancellationToken ct)
    {
        string scope = folder.Origin.GetLeftPart(UriPartial.Authority) + folder.ServerRelative;
        string kql = "\"" + query.Replace("\"", "") + "\" path:\"" + scope + "\"";
        using var doc = await GetJsonAsync(folder, "/_api/search/query?querytext=" + Uri.EscapeDataString("'" + kql.Replace("'", "''") + "'")
            + "&selectproperties=" + Uri.EscapeDataString("'Path,HitHighlightedSummary,IsDocument'") + "&rowlimit=" + Math.Clamp(max, 1, 500)
            + "&trimduplicates=false", ct, notFound: "Search is not available on this site.");
        var hits = new List<SpHit>();
        if (!doc.RootElement.TryGetProperty("PrimaryQueryResult", out var pqr) || pqr.ValueKind != JsonValueKind.Object) return hits;
        foreach (var row in pqr.GetProperty("RelevantResults").GetProperty("Table").GetProperty("Rows").EnumerateArray())
        {
            string? path = null, summary = "";
            foreach (var c in row.GetProperty("Cells").EnumerateArray())
            {
                string key = c.GetProperty("Key").GetString() ?? "";
                if (key == "Path") path = c.GetProperty("Value").GetString();
                else if (key == "HitHighlightedSummary") summary = c.GetProperty("Value").GetString() ?? "";
            }
            if (path == null || !Uri.TryCreate(path, UriKind.Absolute, out var u)) continue;
            string rel = Uri.UnescapeDataString(u.AbsolutePath);
            summary = Regex.Replace(summary, "</?c0>|<ddd/>", m => m.Value == "<ddd/>" ? "…" : "");
            hits.Add(new SpHit(rel, WebUtility.HtmlDecode(summary).Trim()));
        }
        return hits;
    }

    // ------------------------------------------------------------------ HTTP

    private static string Arg(string serverRelative) => Uri.EscapeDataString("'" + serverRelative.Replace("'", "''") + "'");
    private static string Quote(string s) => "'" + Uri.EscapeDataString(s.Replace("'", "''")) + "'";
    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
    private static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out int i) ? i : null;
    private static DateTime? Date(JsonElement e, string n) => DateTime.TryParse(Str(e, n), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static HttpRequestMessage Get(SharePointPath p, string api)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, p.WebUrlOrOrigin + api);
        m.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        return m;
    }

    private async Task<JsonDocument> GetJsonAsync(SharePointPath p, string api, CancellationToken ct, string notFound)
    {
        using var r = await SendAsync(p, () => Get(p, api), ct);
        await Ensure(r, notFound, ct);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> PostAsync(SharePointPath p, string api, HttpContent? body, CancellationToken ct)
    {
        byte[]? bytes = body == null ? null : await body.ReadAsByteArrayAsync(ct);
        string digest = await DigestAsync(p, ct);
        return await SendAsync(p, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, p.WebUrlOrOrigin + api) { Content = bytes == null ? new ByteArrayContent(Array.Empty<byte>()) : new ByteArrayContent(bytes) };
            m.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            m.Headers.Add("X-RequestDigest", digest);
            return m;
        }, ct);
    }

    private async Task<string> DigestAsync(SharePointPath p, CancellationToken ct)
    {
        lock (_digests)
            if (_digests.TryGetValue(p.WebUrlOrOrigin, out var d) && d.until > DateTime.UtcNow) return d.value;
        using var r = await SendAsync(p, () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, p.WebUrlOrOrigin + "/_api/contextinfo") { Content = new ByteArrayContent(Array.Empty<byte>()) };
            m.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            return m;
        }, ct);
        await Ensure(r, "Site not found.", ct);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        string value = doc.RootElement.GetProperty("FormDigestValue").GetString()!;
        int seconds = Int(doc.RootElement, "FormDigestTimeoutSeconds") ?? 1800;
        lock (_digests) _digests[p.WebUrlOrOrigin] = (value, DateTime.UtcNow.AddSeconds(seconds - 120));
        return value;
    }

    private static async Task Ensure(HttpResponseMessage r, string notFound, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        string body = "";
        try { body = await r.Content.ReadAsStringAsync(ct); } catch (Exception) { }
        string msg = Regex.Match(body, "\"message\"\\s*:\\s*(\\{[^}]*\"value\"\\s*:\\s*)?\"(?<m>[^\"]+)\"").Groups["m"].Value;
        throw r.StatusCode switch
        {
            HttpStatusCode.NotFound => new ToolException("not_found", notFound),
            HttpStatusCode.Forbidden => new ToolException("no_access", "SharePoint refused access: the account has no rights to this item."),
            HttpStatusCode.Conflict => new ToolException("exists", msg.Length > 0 ? msg : "Target exists."),
            _ => new ToolException("sharepoint_error", $"SharePoint {(int)r.StatusCode}: {(msg.Length > 0 ? msg : body[..Math.Min(200, body.Length)])}"),
        };
    }

    private static bool IsLoginRedirect(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.Unauthorized
        || ((int)r.StatusCode is >= 300 and < 400 && r.Headers.Location is { } loc
            && (loc.IsAbsoluteUri ? !loc.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || loc.AbsolutePath.Contains("/_forms/", StringComparison.OrdinalIgnoreCase)
                                  : loc.OriginalString.Contains("/_forms/", StringComparison.OrdinalIgnoreCase)))
        || (r.StatusCode == HttpStatusCode.Forbidden && r.Headers.Contains("X-Forms_Based_Auth_Required"));

    // One request with the stored cookies; a login redirect renews the session once and repeats the request.
    private async Task<HttpResponseMessage> SendAsync(SharePointPath p, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        if (!HasSession(p.Origin)) await RenewAsync(p, afterFailure: false, ct);
        var r = await _api.SendAsync(make(), ct);
        if (!IsLoginRedirect(r)) return r;
        r.Dispose();
        await RenewAsync(p, afterFailure: true, ct);
        r = await _api.SendAsync(make(), ct);
        if (IsLoginRedirect(r)) { r.Dispose(); throw SignInNeeded(); }
        return r;
    }

    private bool HasSession(Uri origin) => _external ||
        _jar.GetCookies(origin).Any(c => !c.Expired && (c.Name == "FedAuth" || c.Name == "rtFa"));

    private static ToolException SignInNeeded() => new("sign_in_needed",
        "SharePoint sign-in did not finish. The CO browser shows the sign-in page; ask the user to complete it there " +
        "(CO Desktop → Browser), or to set up the Okta password fallback (CO Desktop → Folders), then retry.");

    private async Task RenewAsync(SharePointPath p, bool afterFailure, CancellationToken ct)
    {
        await _signIn.WaitAsync(ct);
        try
        {
            if (!afterFailure && HasSession(p.Origin)) return;   // another call renewed meanwhile
            // 1. Cookies the CO browser already holds (skipped after a failure: they are the stale ones).
            if (_browser != null && !afterFailure && await TakeBrowserCookiesAsync(p.Origin, ct)) { LastSignIn = "browser session"; return; }
            // 2. Office: the Windows logon signs in without a password (Okta Desktop SSO / Kerberos). Elsewhere this
            //    ends on a sign-in page within a few requests and the next way is tried.
            if (Okta.CanTryIntegrated && await IntegratedSignInAsync(p, ct)) { LastSignIn = "Windows sign-in (office network)"; return; }
            // 3. Browser sign-in: Microsoft → Okta with the live Okta session, FastPass or push.
            if (_browser != null && await SharePointApi.SignInAsync(_browser, p.WebUrlOrOrigin, p.Origin.Host, ct) && await TakeBrowserCookiesAsync(p.Origin, ct))
            { LastSignIn = "browser sign-in"; return; }
            // 4. Okta password (+ push outside the office; Okta often skips MFA inside it).
            if (Okta.Configured && HasPassword && await OktaPasswordSignInAsync(p, ct)) { LastSignIn = "Okta password"; return; }
            throw SignInNeeded();
        }
        finally { _signIn.Release(); }
    }

    private async Task<bool> TakeBrowserCookiesAsync(Uri origin, CancellationToken ct)
    {
        var cookies = await _browser!.GetCookiesAsync(origin.GetLeftPart(UriPartial.Authority) + "/", ct);
        int taken = 0;
        foreach (var c in cookies)
        {
            try { _jar.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain)); taken++; }
            catch (CookieException) { }
        }
        return taken > 0 && HasSession(origin);
    }

    // ------------------------------------------------------------------ Okta password fallback (Bor_Connect.py)

    private async Task<string> OktaTargetAsync(SharePointPath p, CancellationToken ct) =>
        Okta.AppLink.Length > 0 ? Okta.AppLink : await FederatedLoginUrlAsync(p, ct);

    private async Task<bool> IntegratedSignInAsync(SharePointPath p, CancellationToken ct)
    {
        try
        {
            using var r = await _flow.GetAsync(await OktaTargetAsync(p, ct), ct);
            await FollowAutoFormsAsync(r, ct);
            if (!HasSession(p.Origin))
            {
                using var touch = await _flow.GetAsync(p.WebUrlOrOrigin + "/", ct);
                await FollowAutoFormsAsync(touch, ct);
            }
            return HasSession(p.Origin);
        }
        catch (HttpRequestException) { return false; }   // e.g. Okta not reachable this way
    }

    // 0.8.1: Okta primary authentication (password + Okta Verify push), extracted from OktaPasswordSignInAsync
    // 0.8.1: so the engie.ai sign-in can reuse it. The password stays in this process; only the one-time token leaves.
    public async Task<(string Origin, string Token)?> OktaSessionTokenAsync(CancellationToken ct)
    {
        if (!Okta.Configured || !HasPassword) return null;

        var uri = new Uri(Okta.OktaUrl.Contains("://") ? Okta.OktaUrl : "https://" + Okta.OktaUrl);
        // 0.8.1: new - never send the password over plain http.
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ToolException("sign_in_failed", "Okta address must be https");
        string okta = uri.GetLeftPart(UriPartial.Authority);

        string? password = _secrets?.Read(OktaFallback.CredentialTarget);
        if (string.IsNullOrEmpty(password)) return null;

        // 1. Primary authentication.
        using var authn = await _flow.PostAsync(okta + "/api/v1/authn",
            Json(new { username = Okta.UserName, password }), ct).ConfigureAwait(false);
        using var a = JsonDocument.Parse(await authn.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        string status = Str(a.RootElement, "status") ?? "";
        string? token = Str(a.RootElement, "sessionToken");
        if (status == "MFA_REQUIRED")
            token = await OktaPushAsync(a.RootElement, ct).ConfigureAwait(false);
        else if (status != "SUCCESS")
            throw new ToolException("sign_in_failed", "Okta /api/v1/authn: " + (Str(a.RootElement, "errorSummary") ?? status)
                + (authn.StatusCode == HttpStatusCode.NotFound || authn.StatusCode == HttpStatusCode.Forbidden ? " (the classic Okta API may be off in this org)" : ""));
        if (string.IsNullOrEmpty(token)) return null;

        return (okta, token);
    }

    // 0.8.2: automatic engie.ai sign-in via the Okta Identity Engine (password + Okta Verify push with a number).
    // The password stays in this process; the caller carries the returned Okta session cookies into the engie.ai tab.
    public async Task<EngieOktaIdx.Result> EngieOktaSignInAsync(
        Action<OktaNumberChallenge> onNumber, Action<string> onTrace, CancellationToken ct)
    {
        if (!Okta.Configured)
            return new EngieOktaIdx.Result { Ok = false, Error = "Okta address or user name is not set (CO Desktop → Folders)." };
        var uri = new Uri(Okta.OktaUrl.Contains("://") ? Okta.OktaUrl : "https://" + Okta.OktaUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return new EngieOktaIdx.Result { Ok = false, Error = "Okta address must be https." };
        string? password = _secrets?.Read(OktaFallback.CredentialTarget);
        if (string.IsNullOrEmpty(password))
            return new EngieOktaIdx.Result { Ok = false, Error = "No Okta password stored (CO Desktop → Folders)." };

        return await EngieOktaIdx.SignInAsync(uri.GetLeftPart(UriPartial.Authority), Okta.UserName, password, onNumber, onTrace, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> OktaPasswordSignInAsync(SharePointPath p, CancellationToken ct)
    {
        // 0.8.1: step 1 moved to OktaSessionTokenAsync (which now also enforces https).
        var t = await OktaSessionTokenAsync(ct).ConfigureAwait(false);
        if (t is null) return false;
        var (okta, token) = t.Value;

        // 2. Okta session cookie, then on to Microsoft / SharePoint.
        string target = await OktaTargetAsync(p, ct);
        using var redirect = await _flow.GetAsync(okta + "/login/sessionCookieRedirect?checkAccountSetupComplete=true&token="
            + Uri.EscapeDataString(token) + "&redirectUrl=" + Uri.EscapeDataString(target), ct);
        await FollowAutoFormsAsync(redirect, ct);

        // 3. An app link may land on the Microsoft 365 portal; touching the site issues FedAuth there.
        if (!HasSession(p.Origin))
        {
            using var touch = await _flow.GetAsync(p.WebUrlOrOrigin + "/", ct);
            await FollowAutoFormsAsync(touch, ct);
        }
        return HasSession(p.Origin);
    }

    // The site sends us to login.microsoftonline.com; whr/domain_hint make Microsoft hand over to Okta at once.
    private async Task<string> FederatedLoginUrlAsync(SharePointPath p, CancellationToken ct)
    {
        var noRedirect = _api;
        using var r = await noRedirect.GetAsync(p.WebUrlOrOrigin + "/_layouts/15/Authenticate.aspx?Source=%2F", ct);
        string url = r.Headers.Location?.ToString() ?? p.WebUrlOrOrigin;
        for (int i = 0; i < 5 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase); i++)
        {
            using var next = await noRedirect.GetAsync(url, ct);
            if (next.Headers.Location == null) break;
            url = new Uri(u, next.Headers.Location).ToString();
        }
        string domain = Okta.UserName.Contains('@') ? Okta.UserName[(Okta.UserName.IndexOf('@') + 1)..] : "";
        if (domain.Length > 0 && url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            url += (url.Contains('?') ? "&" : "?") + "whr=" + Uri.EscapeDataString(domain) + "&domain_hint=" + Uri.EscapeDataString(domain)
                + "&login_hint=" + Uri.EscapeDataString(Okta.UserName);
        return url;
    }

    private async Task<string?> OktaPushAsync(JsonElement authn, CancellationToken ct)
    {
        JsonElement? factor = null;
        foreach (var f in authn.GetProperty("_embedded").GetProperty("factors").EnumerateArray())
            if (Str(f, "factorType") == "push") { factor = f; break; }
        if (factor == null)
            throw new ToolException("sign_in_failed", "Okta asks for a factor other than Okta Verify push; sign in in the CO browser instead.");
        string verify = factor.Value.GetProperty("_links").GetProperty("verify").GetProperty("href").GetString()!;
        string stateToken = Str(authn, "stateToken") ?? "";
        using var start = await _flow.PostAsync(verify, Json(new { stateToken }), ct);
        string body = await start.Content.ReadAsStringAsync(ct);
        DateTime until = DateTime.UtcNow + PushWait;
        while (true)
        {
            using var v = JsonDocument.Parse(body);
            string st = Str(v.RootElement, "status") ?? "";
            if (st == "SUCCESS") return Str(v.RootElement, "sessionToken");
            string result = Str(v.RootElement, "factorResult") ?? "";
            if (result is "REJECTED" or "TIMEOUT" or "CANCELLED")
                throw new ToolException("sign_in_failed", "Okta Verify push " + result.ToLowerInvariant() + ".");
            if (DateTime.UtcNow > until) throw new ToolException("sign_in_failed", "Okta Verify push was not approved in time.");
            await Task.Delay(PushPoll, ct);
            var links = v.RootElement.GetProperty("_links");
            string poll = (links.TryGetProperty("poll", out var pl) ? pl : links.GetProperty("next")).GetProperty("href").GetString()!;
            using var next = await _flow.PostAsync(poll, Json(new { stateToken }), ct);
            body = await next.Content.ReadAsStringAsync(ct);
        }
    }

    // SAML / WS-Fed hand-overs are HTML pages with a form that a browser submits by script.
    private async Task FollowAutoFormsAsync(HttpResponseMessage r, CancellationToken ct)
    {
        string html = await r.Content.ReadAsStringAsync(ct);
        Uri at = r.RequestMessage?.RequestUri ?? new Uri("https://localhost/");
        for (int i = 0; i < 6; i++)
        {
            var form = Regex.Match(html, "<form[^>]*\\saction=[\"']([^\"']+)[\"'][^>]*>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!form.Success) return;
            var fields = new List<KeyValuePair<string, string>>();
            foreach (Match m in Regex.Matches(form.Groups[2].Value, "<input[^>]*>", RegexOptions.IgnoreCase))
            {
                string name = Regex.Match(m.Value, "\\sname=[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase).Groups[1].Value;
                if (name.Length == 0) continue;
                string value = Regex.Match(m.Value, "\\svalue=[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase).Groups[1].Value;
                fields.Add(new(WebUtility.HtmlDecode(name), WebUtility.HtmlDecode(value)));
            }
            if (!fields.Any(f => f.Key is "SAMLResponse" or "wresult" or "wa" or "code" or "id_token" or "RelayState")) return;
            var action = new Uri(at, WebUtility.HtmlDecode(form.Groups[1].Value));
            using var next = await _flow.PostAsync(action, new FormUrlEncodedContent(fields), ct);
            html = await next.Content.ReadAsStringAsync(ct);
            at = next.RequestMessage?.RequestUri ?? action;
        }
    }

    private static StringContent Json(object o)
    {
        var c = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }
}
