// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CO.Core.Transport;

namespace CO.Core.Auth;

public interface ISecretStore
{
    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target);
}

public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _d = new();
    public string? Read(string t) { lock (_d) return _d.TryGetValue(t, out var v) ? v : null; }
    public void Write(string t, string s) { lock (_d) _d[t] = s; }
    public void Delete(string t) { lock (_d) _d.Remove(t); }
}

public sealed class OAuthClient
{
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }   // Desktop-app clients have a non-confidential secret

    public static OAuthClient FromJson(string json)
    {
        JsonNode n = JsonNode.Parse(json)!;
        JsonNode c = n["installed"] ?? n["web"] ?? n;
        return new OAuthClient { ClientId = c["client_id"]!.GetValue<string>(), ClientSecret = c["client_secret"]?.GetValue<string>() };
    }
}

// Installed-app flow: PKCE + loopback redirect; refresh token only in the secret store (TZ §6.1).
public sealed class InstalledAppAuth : IAccessTokenSource
{
    private readonly HttpClient _http;
    private readonly OAuthClient _client;
    private readonly ISecretStore _store;
    private readonly string _target;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _access;
    private DateTime _expires;

    public InstalledAppAuth(HttpClient http, OAuthClient client, ISecretStore store, string target)
    {
        _http = http;
        _client = client;
        _store = store;
        _target = target;
    }

    public bool HasRefreshToken => _store.Read(_target) != null;

    // Raised when Google refuses the stored refresh token; the argument is "reauth" or "revoked".
    public event Action<string>? ReauthRequired;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_access != null && DateTime.UtcNow < _expires.AddMinutes(-2)) return _access;
            string refresh = _store.Read(_target) ?? throw new InvalidOperationException("Not signed in.");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _client.ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
            };
            if (_client.ClientSecret != null) form["client_secret"] = _client.ClientSecret;
            JsonNode t;
            try { t = await PostToken(form, ct); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("invalid_grant"))
            {
                // Revoked, expired, or a Workspace reauthentication policy (invalid_rapt): only a new
                // interactive sign-in helps. The dead token is dropped so the app shows "signed out".
                _store.Delete(_target);
                _access = null;
                ReauthRequired?.Invoke(ex.Message.Contains("invalid_rapt") ? "reauth" : "revoked");
                throw new ReauthRequiredException(ex.Message);
            }
            _access = t["access_token"]!.GetValue<string>();
            _expires = DateTime.UtcNow.AddSeconds(t["expires_in"]?.GetValue<int>() ?? 3600);
            return _access;
        }
        finally { _gate.Release(); }
    }

    // Opens the system browser. Returns the granted access token; stores the refresh token when asked.
    public static async Task<(string access, string? refresh, string? email)> AuthorizeAsync(
        HttpClient http, OAuthClient client, string scopes, bool offline, Action<string> openBrowser, CancellationToken ct, string? loginHint = null)
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        string redirect = $"http://127.0.0.1:{port}/";
        string verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = B64Url(RandomNumberGenerator.GetBytes(16));

        string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
                     "&client_id=" + Uri.EscapeDataString(client.ClientId) +
                     "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                     "&scope=" + Uri.EscapeDataString(scopes + " openid email") +
                     "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state +
                     (offline ? "&access_type=offline&prompt=consent" : "") +
                     (string.IsNullOrEmpty(loginHint) ? "" : "&login_hint=" + Uri.EscapeDataString(loginHint));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        openBrowser(url);

        var ctxTask = listener.GetContextAsync();
        var done = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (done != ctxTask) throw new TimeoutException("Sign-in was not completed within 5 minutes.");
        HttpListenerContext ctx = ctxTask.Result;
        string? code = ctx.Request.QueryString["code"];
        string? gotState = ctx.Request.QueryString["state"];
        byte[] page = Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif'>CO Desktop: sign-in finished, you can close this tab.</body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(page, ct);
        ctx.Response.Close();
        if (code == null || gotState != state) throw new InvalidOperationException("Sign-in was cancelled or the state did not match.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["code"] = code, ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = redirect,
        };
        if (client.ClientSecret != null) form["client_secret"] = client.ClientSecret;
        using var resp = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Token exchange failed: " + body);
        JsonNode t = JsonNode.Parse(body)!;
        string? email = null;
        if (t["id_token"]?.GetValue<string>() is string idt && idt.Split('.') is { Length: 3 } parts)
            email = JsonNode.Parse(Encoding.UTF8.GetString(FromB64Url(parts[1])))?["email"]?.GetValue<string>();
        return (t["access_token"]!.GetValue<string>(), t["refresh_token"]?.GetValue<string>(), email);
    }

    public async Task<string?> SignInAsync(string scopes, Action<string> openBrowser, CancellationToken ct, string? loginHint = null)
    {
        var (access, refresh, email) = await AuthorizeAsync(_http, _client, scopes, true, openBrowser, ct, loginHint);
        if (refresh == null) throw new InvalidOperationException("Google did not return a refresh token.");
        _store.Write(_target, refresh);
        _access = access;
        _expires = DateTime.UtcNow.AddMinutes(55);
        return email;
    }

    public void SignOut() { _store.Delete(_target); _access = null; }

    // Access token plus remaining lifetime, for handing to the embedded CO Web page.
    public async Task<(string token, int expiresIn)> GetAccessTokenWithLifetimeAsync(CancellationToken ct)
    {
        string t = await GetAccessTokenAsync(ct);
        return (t, Math.Max(60, (int)(_expires - DateTime.UtcNow).TotalSeconds));
    }

    public static void OpenInBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async Task<JsonNode> PostToken(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Token refresh failed: " + body);
        return JsonNode.Parse(body)!;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

public sealed class ReauthRequiredException : InvalidOperationException
{
    public ReauthRequiredException(string detail) : base("Google asks to sign in again (" + detail.Replace('\n', ' ') + ")") { }
}
