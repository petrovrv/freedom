// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CO.Core.Crypto;
using CO.Core.Protocol;
using CO.Core.Transport;

namespace CO.Core.Pairing;

// pairing-request.json in the device folder; Bridge copies it to transport/hosts/<host>/devices/pending/.
public sealed class PairingRequest
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "co.device.pairing.v1";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("short")] public string Short { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("environmentId")] public string EnvironmentId { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("deviceFolderId")] public string DeviceFolderId { get; set; } = "";
    [JsonPropertyName("slotIds")] public List<string> SlotIds { get; set; } = new();
    [JsonPropertyName("resultsFolderId")] public string ResultsFolderId { get; set; } = "";
    [JsonPropertyName("tools")] public JsonArray Tools { get; set; } = new();
    [JsonPropertyName("signingKey")] public string SigningKey { get; set; } = "";         // base64 X||Y
    [JsonPropertyName("serviceAccountCert")] public string ServiceAccountCert { get; set; } = ""; // PEM
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("managed")] public bool Managed { get; set; }
}

// devices/<deviceId>.json written by the owner's client after "Pair device".
public sealed class DeviceRecord
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "co.device.v1";
    [JsonPropertyName("request")] public PairingRequest Request { get; set; } = new();
    [JsonPropertyName("serviceAccount")] public string ServiceAccount { get; set; } = "";
    [JsonPropertyName("serviceAccountKeyId")] public string ServiceAccountKeyId { get; set; } = "";
    [JsonPropertyName("pairedUtc")] public string PairedUtc { get; set; } = "";
    [JsonPropertyName("pairedBy")] public string PairedBy { get; set; } = "";
    [JsonPropertyName("revoked")] public bool Revoked { get; set; }
}

public static class PairingMath
{
    public static string NewDeviceId() =>
        "d-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string ServiceAccountId(string deviceId)
    {
        // IAM account ids: 6-30 chars, [a-z][a-z0-9-]*[a-z0-9]
        string id = "co-dev-" + deviceId.Replace("d-", "");
        return id.Length > 30 ? id[..30] : id;
    }

    // Self-signed certificate carrying the RSA key; the public part is uploaded as a service account key.
    public static string SelfSignedCertPem(RSA rsa, string deviceId)
    {
        var req = new CertificateRequest($"CN=co-device-{deviceId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        return cert.ExportCertificatePem();
    }
}

// Owner-side operations that need the IAM API (TZ §6.2.6). The token is short-lived and not stored.
public sealed class PairingAdmin
{
    private readonly HttpClient _http;
    private readonly string _project;
    private readonly Func<CancellationToken, Task<string>> _token;

    public PairingAdmin(HttpClient http, string project, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _project = project;
        _token = token;
    }

    // The code binds the device key to the Bridge key the device pinned (TZ §6.2.6-7).
    public static bool CodesMatch(PairingRequest r, string bridgePublicKey, string codeShownOnDevice) =>
        codeShownOnDevice.Length == 6 && KeyUtil.PairingCode(r.SigningKey, bridgePublicKey) == codeShownOnDevice;

    public async Task<DeviceRecord> ApproveAsync(PairingRequest r, string ownerEmail, CancellationToken ct)
    {
        if (!Names.IsValidDeviceId(r.DeviceId)) throw new InvalidOperationException("Invalid deviceId.");
        string accountId = PairingMath.ServiceAccountId(r.DeviceId);
        await EnsureServiceEnabledAsync("iam.googleapis.com", ct);

        // The project may be configured by number (Bridge config); the account e-mail comes from IAM.
        JsonNode sa = await Call(HttpMethod.Post, $"https://iam.googleapis.com/v1/projects/{_project}/serviceAccounts",
            new JsonObject
            {
                ["accountId"] = accountId,
                ["serviceAccount"] = new JsonObject { ["displayName"] = $"CO device {r.Name}", ["description"] = r.DeviceId },
            }, ct, allowConflict: true);
        // Address the account by its unique id: valid whether the project was given by number or id.
        JsonNode? acct = sa["uniqueId"] != null ? sa : null;
        if (acct == null)
        {
            JsonNode list = await Call(HttpMethod.Get, $"https://iam.googleapis.com/v1/projects/{_project}/serviceAccounts?pageSize=100", null, ct);
            acct = list["accounts"]?.AsArray()
                .FirstOrDefault(a => a?["email"]?.GetValue<string>()?.StartsWith(accountId + "@", StringComparison.Ordinal) == true)
                ?? throw new InvalidOperationException("Service account " + accountId + " exists but was not found in the list.");
        }
        string email = acct["email"]!.GetValue<string>();
        string uniqueId = acct["uniqueId"]!.GetValue<string>();

        string pem64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(r.ServiceAccountCert));
        // A new service account is not visible to keys:upload for some seconds; IAM answers 403 meanwhile.
        JsonNode key = new JsonObject();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                key = await Call(HttpMethod.Post,
                    $"https://iam.googleapis.com/v1/projects/-/serviceAccounts/{uniqueId}/keys:upload",
                    new JsonObject { ["publicKeyData"] = pem64 }, ct);
                break;
            }
            catch (HttpRequestException ex) when (attempt < 12 && ex.Message.Contains("HTTP 403") && !ex.Message.Contains("constraints/"))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        string keyName = key["name"]?.GetValue<string>() ?? "";
        return new DeviceRecord
        {
            Request = r, ServiceAccount = email,
            ServiceAccountKeyId = keyName.Contains("/keys/") ? keyName[(keyName.LastIndexOf('/') + 1)..] : keyName,
            PairedUtc = Wire.Now(DateTime.UtcNow), PairedBy = ownerEmail,
        };
    }

    // The IAM API is off in a fresh project; the owner's cloud-platform token may switch it on (Service Usage API).
    private async Task EnsureServiceEnabledAsync(string service, CancellationToken ct)
    {
        string url = $"https://serviceusage.googleapis.com/v1/projects/{_project}/services/{service}";
        JsonNode state;
        try { state = await Call(HttpMethod.Get, url, null, ct); }
        catch (HttpRequestException) { return; }   // Service Usage API itself off: let the IAM call report the state
        if (state["state"]?.GetValue<string>() == "ENABLED") return;
        await Call(HttpMethod.Post, url + ":enable", new JsonObject(), ct);
        for (int i = 0; i < 24; i++)   // propagation usually takes under a minute
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            state = await Call(HttpMethod.Get, url, null, ct);
            if (state["state"]?.GetValue<string>() == "ENABLED") { await Task.Delay(TimeSpan.FromSeconds(10), ct); return; }
        }
        throw new InvalidOperationException(service + " was enabled but is not active yet; retry in a few minutes.");
    }

    // TZ §6.4.2: keys and the account itself are deleted; Bridge removes topics and stops using the folder.
    public async Task RevokeAsync(DeviceRecord d, CancellationToken ct)
    {
        await Call(HttpMethod.Delete, $"https://iam.googleapis.com/v1/projects/{_project}/serviceAccounts/{d.ServiceAccount}", null, ct, allowNotFound: true);
        d.Revoked = true;
    }

    private async Task<JsonNode> Call(HttpMethod m, string url, JsonNode? body, CancellationToken ct, bool allowConflict = false, bool allowNotFound = false)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if ((allowConflict && (int)resp.StatusCode == 409) || (allowNotFound && (int)resp.StatusCode == 404))
            return new JsonObject();
        if (!resp.IsSuccessStatusCode)
        {
            if (text.Contains("disableServiceAccountKeyUpload", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Organization policy iam.disableServiceAccountKeyUpload blocks key upload for this project (TZ §6.2.8). " + text);
            throw new HttpRequestException($"IAM {m} {new Uri(url).AbsolutePath}: HTTP {(int)resp.StatusCode} {text}");
        }
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }
}
