// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Transport;

namespace CO.Core.Subscriptions;

// S4: registers the signed-in subscriptions of this computer on the CO server as model resources of the environment
// (db.php op subs_put, schema 23 model_resources; S5 lists them and their "slave allowed" flag). Signed with the
// device's own Google token: the server takes the environment from the token's account, never from the request.
public sealed class SubsRegistry
{
    private readonly IAccessTokenSource _tokens;
    private readonly Func<(string Id, string Short, string Name)> _device;
    private readonly string _dbUrl;
    private readonly string _hostId;
    // Own client without redirects: the Google token must reach only the configured db.php.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    // hostId: the Bridge host of this computer ("" when none): its subscriptions are the host's own resources.
    public SubsRegistry(IAccessTokenSource tokens, Func<(string Id, string Short, string Name)> device, string siteUrl, string hostId = "")
    {
        _tokens = tokens;
        _device = device;
        _dbUrl = siteUrl.TrimEnd('/') + "/agent/db.php";
        _hostId = hostId;
    }

    public static JsonObject Body(IReadOnlyList<CliStatus> subs, (string Id, string Short, string Name) dev, string hostId)
    {
        var arr = new JsonArray();
        foreach (var s in subs)
            arr.Add(new JsonObject
            {
                ["agent"] = s.Id, ["installed"] = s.Installed, ["loggedIn"] = s.LoggedIn, ["account"] = s.Account,
                ["plan"] = s.Plan, ["method"] = s.Method, ["version"] = s.Version,
            });
        return new JsonObject
        {
            ["op"] = "subs_put",
            ["device"] = new JsonObject { ["id"] = dev.Id, ["short"] = dev.Short, ["name"] = dev.Name, ["hostId"] = hostId },
            ["subs"] = arr,
        };
    }

    public async Task<string> PutAsync(IReadOnlyList<CliStatus> subs, CancellationToken ct)
    {
        var dev = _device();
        if (dev.Short.Length == 0) return "device not set up";
        if (!_dbUrl.StartsWith("https://", StringComparison.Ordinal)) return "no CO site configured";
        using var req = new HttpRequestMessage(HttpMethod.Post, _dbUrl)
        {
            Content = new StringContent(Body(subs, dev, _hostId).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-CO-Auth", "1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));
        using var resp = await Http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {CliCatalog.Cut(text, 200)}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string Ids(string name) => r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.EnumerateArray().Select(x => x.GetString())) : "";
            string reg = Ids("registered"), gone = Ids("removed");
            return $"{DateTime.Now:HH:mm} registered: {(reg.Length > 0 ? reg : "none")}" + (gone.Length > 0 ? "; removed: " + gone : "");
        }
        catch (JsonException) { return "registered (unreadable answer)"; }
    }
}
