// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Protocol;

namespace CO.Core.Pairing;

// Owner-side pairing screen (TZ §6.2.6). Runs with a short-lived token (drive + cloud-platform)
// obtained on "Pair device" and dropped afterwards.
public sealed class OwnerConsole
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _rootName, _host;

    public OwnerConsole(HttpClient http, string accessToken, string workbenchRootName, string hostId)
    {
        _http = http;
        _token = accessToken;
        _rootName = workbenchRootName;
        _host = hostId;
    }

    public sealed record Pending(PairingRequest Request, string FileId);

    public async Task<string> BridgePublicKeyAsync(CancellationToken ct)
    {
        string hostDir = await PathAsync(ct, "transport", "hosts", _host);
        string? ownerId = await FindAsync(hostDir, "owner.json", false, ct) ?? throw new InvalidOperationException("owner.json not found.");
        JsonNode owner = JsonNode.Parse(await DownloadAsync(ownerId, ct))!;
        return owner["deviceCallKey"]?["publicKey"]?.GetValue<string>()
               ?? throw new InvalidOperationException("owner.json has no deviceCallKey; Bridge must be v1.0 or later.");
    }

    public async Task<List<Pending>> ListPendingAsync(CancellationToken ct)
    {
        string pending = await PathAsync(ct, "transport", "hosts", _host, "devices", "pending");
        var list = new List<Pending>();
        foreach (var (id, name) in await ChildrenAsync(pending, ct))
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
            try { list.Add(new Pending(JsonSerializer.Deserialize<PairingRequest>(await DownloadAsync(id, ct))!, id)); }
            catch (JsonException) { }
        }
        return list;
    }

    // Name and owner e-mails of a Drive file as Google reports them (auto-pairing proof, see AutoPair).
    public async Task<(string name, List<string> owners)> FileOwnersAsync(string fileId, CancellationToken ct)
    {
        JsonNode n = await Send(Req(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields=name,owners(emailAddress)"), ct);
        var owners = n["owners"]?.AsArray().Select(o => o?["emailAddress"]?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList() ?? new();
        return (n["name"]?.GetValue<string>() ?? "", owners);
    }

    public async Task WriteDeviceRecordAsync(DeviceRecord rec, CancellationToken ct)
    {
        string devices = await PathAsync(ct, "transport", "hosts", _host, "devices");
        string name = rec.Request.DeviceId + ".json";
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
        string? existing = await FindAsync(devices, name, false, ct);
        if (existing != null)
        {
            var req = Req(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{existing}?uploadType=media");
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            await Send(req, ct);
            return;
        }
        var meta = new JsonObject { ["name"] = name, ["parents"] = new JsonArray(devices) };
        var mp = new MultipartContent("related")
        {
            new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json"),
            new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        var create = Req(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id");
        create.Content = mp;
        await Send(create, ct);
    }

    public async Task<List<DeviceRecord>> ListDevicesAsync(CancellationToken ct)
    {
        string devices = await PathAsync(ct, "transport", "hosts", _host, "devices");
        var list = new List<DeviceRecord>();
        foreach (var (id, name) in await ChildrenAsync(devices, ct))
            if (name.EndsWith(".json", StringComparison.Ordinal))
                try { list.Add(JsonSerializer.Deserialize<DeviceRecord>(await DownloadAsync(id, ct))!); } catch (JsonException) { }
        return list;
    }

    private async Task<string> PathAsync(CancellationToken ct, params string[] parts)
    {
        string? cur = await FindRootAsync(ct) ?? throw new InvalidOperationException($"Folder '{_rootName}' not found in My Drive.");
        foreach (string p in parts)
            cur = await FindAsync(cur, p, true, ct) ?? throw new InvalidOperationException($"Folder '{p}' not found under {_rootName}.");
        return cur;
    }

    private async Task<string?> FindRootAsync(CancellationToken ct) => await FindAsync("root", _rootName, true, ct);

    private async Task<string?> FindAsync(string parent, string name, bool folder, CancellationToken ct)
    {
        string q = $"'{parent}' in parents and name = '{name.Replace("'", "\\'")}' and trashed = false" +
                   (folder ? " and mimeType = 'application/vnd.google-apps.folder'" : "");
        JsonNode n = await Send(Req(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?fields=files(id)&q=" + Uri.EscapeDataString(q)), ct);
        return n["files"]!.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
    }

    private async Task<List<(string id, string name)>> ChildrenAsync(string parent, CancellationToken ct)
    {
        string q = $"'{parent}' in parents and trashed = false";
        JsonNode n = await Send(Req(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?pageSize=1000&fields=files(id,name)&q=" + Uri.EscapeDataString(q)), ct);
        return n["files"]!.AsArray().Select(f => (f!["id"]!.GetValue<string>(), f["name"]!.GetValue<string>())).ToList();
    }

    private async Task<byte[]> DownloadAsync(string id, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{id}?alt=media"), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    private async Task<JsonNode> Send(HttpRequestMessage r, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(r, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Drive: HTTP {(int)resp.StatusCode} {body}");
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
    }
}
