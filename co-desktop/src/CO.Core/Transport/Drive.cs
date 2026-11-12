// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CO.Core.Transport;

public sealed class DriveFile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string MimeType { get; init; } = "";
    public Dictionary<string, string> AppProperties { get; init; } = new();
    public DateTime ModifiedUtc { get; init; }
}

// Only the calls Runtime needs under drive.file (TZ §6.1).
public interface IDriveFiles
{
    Task<string> CreateFolderAsync(string name, string? parentId, CancellationToken ct);
    Task<string> CreateFileAsync(string name, string parentId, byte[] content, string mime, CancellationToken ct);
    Task UpdateFileAsync(string id, byte[] content, string mime, CancellationToken ct);
    Task<byte[]> DownloadAsync(string id, CancellationToken ct);
    Task<List<DriveFile>> ListChildrenAsync(string parentId, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task ShareAsync(string id, string email, string role, CancellationToken ct);
}

public interface IAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

public sealed class DriveRest : IDriveFiles
{
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3/files";
    public const string FolderMime = "application/vnd.google-apps.folder";
    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    // Downloads include the installer offered for self-update (tens of MB on slow lines).
    private static readonly HttpClient LongHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    public DriveRest(HttpClient http, IAccessTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    private async Task<HttpRequestMessage> Req(HttpMethod m, string url, CancellationToken ct)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));
        return r;
    }

    private async Task<JsonNode> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Drive {req.Method} {req.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode} {Trim(body)}", null, resp.StatusCode);
        return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public async Task<string> CreateFolderAsync(string name, string? parentId, CancellationToken ct)
    {
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = FolderMime };
        if (parentId != null) meta["parents"] = new JsonArray(parentId);
        var req = await Req(HttpMethod.Post, Api + "?fields=id", ct);
        req.Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json");
        return (await Send(req, ct))["id"]!.GetValue<string>();
    }

    public async Task<string> CreateFileAsync(string name, string parentId, byte[] content, string mime, CancellationToken ct)
    {
        var meta = new JsonObject { ["name"] = name, ["parents"] = new JsonArray(parentId) };
        var multipart = new MultipartContent("related");
        multipart.Add(new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json"));
        var bytes = new ByteArrayContent(content);
        bytes.Headers.ContentType = new MediaTypeHeaderValue(mime);
        multipart.Add(bytes);
        var req = await Req(HttpMethod.Post, Upload + "?uploadType=multipart&fields=id", ct);
        req.Content = multipart;
        return (await Send(req, ct))["id"]!.GetValue<string>();
    }

    public async Task UpdateFileAsync(string id, byte[] content, string mime, CancellationToken ct)
    {
        var req = await Req(HttpMethod.Patch, $"{Upload}/{Uri.EscapeDataString(id)}?uploadType=media&fields=id", ct);
        req.Content = new ByteArrayContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        await Send(req, ct);
    }

    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct)
    {
        var req = await Req(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(id)}?alt=media", ct);
        using var resp = await LongHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Drive download {id}: HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<List<DriveFile>> ListChildrenAsync(string parentId, CancellationToken ct)
    {
        var list = new List<DriveFile>();
        string? page = null;
        do
        {
            string q = Uri.EscapeDataString($"'{parentId.Replace("'", "\\'")}' in parents and trashed=false");
            string url = $"{Api}?q={q}&pageSize=1000&fields=nextPageToken,files(id,name,mimeType,appProperties,modifiedTime)" +
                         (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            JsonNode n = await Send(await Req(HttpMethod.Get, url, ct), ct);
            foreach (JsonNode? f in n["files"]!.AsArray())
            {
                var props = new Dictionary<string, string>();
                if (f!["appProperties"] is JsonObject ap)
                    foreach (var kv in ap) props[kv.Key] = kv.Value?.GetValue<string>() ?? "";
                list.Add(new DriveFile
                {
                    Id = f["id"]!.GetValue<string>(), Name = f["name"]!.GetValue<string>(),
                    MimeType = f["mimeType"]!.GetValue<string>(), AppProperties = props,
                    ModifiedUtc = DateTime.Parse(f["modifiedTime"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal),
                });
            }
            page = n["nextPageToken"]?.GetValue<string>();
        } while (page != null);
        return list;
    }

    public async Task DeleteAsync(string id, CancellationToken ct) =>
        await Send(await Req(HttpMethod.Delete, $"{Api}/{Uri.EscapeDataString(id)}", ct), ct);

    public async Task ShareAsync(string id, string email, string role, CancellationToken ct)
    {
        var req = await Req(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(id)}/permissions?sendNotificationEmail=false", ct);
        req.Content = new StringContent(new JsonObject { ["type"] = "user", ["role"] = role, ["emailAddress"] = email }.ToJsonString(), Encoding.UTF8, "application/json");
        await Send(req, ct);
    }
}

// In-memory Drive for tests and the lab page.
public sealed class MemoryDrive : IDriveFiles
{
    private sealed class Node
    {
        public string Id = "", Name = "", Mime = "", Parent = "";
        public byte[] Content = Array.Empty<byte>();
        public Dictionary<string, string> Props = new();
        public DateTime Modified = DateTime.UtcNow;
    }

    private readonly Dictionary<string, Node> _nodes = new();
    private int _next;
    public List<(string id, string email, string role)> Shares { get; } = new();

    private string NewId() => "f" + Interlocked.Increment(ref _next);

    public Task<string> CreateFolderAsync(string name, string? parentId, CancellationToken ct)
    {
        lock (_nodes) { var n = new Node { Id = NewId(), Name = name, Mime = DriveRest.FolderMime, Parent = parentId ?? "root" }; _nodes[n.Id] = n; return Task.FromResult(n.Id); }
    }

    public Task<string> CreateFileAsync(string name, string parentId, byte[] content, string mime, CancellationToken ct)
    {
        lock (_nodes) { var n = new Node { Id = NewId(), Name = name, Mime = mime, Parent = parentId, Content = content }; _nodes[n.Id] = n; return Task.FromResult(n.Id); }
    }

    public Task UpdateFileAsync(string id, byte[] content, string mime, CancellationToken ct)
    {
        lock (_nodes) { _nodes[id].Content = content; _nodes[id].Modified = DateTime.UtcNow; }
        return Task.CompletedTask;
    }

    public void SetProperty(string id, string key, string value) { lock (_nodes) _nodes[id].Props[key] = value; }

    public Task<byte[]> DownloadAsync(string id, CancellationToken ct) { lock (_nodes) return Task.FromResult(_nodes[id].Content); }

    public Task<List<DriveFile>> ListChildrenAsync(string parentId, CancellationToken ct)
    {
        lock (_nodes)
            return Task.FromResult(_nodes.Values.Where(n => n.Parent == parentId).Select(n => new DriveFile
            { Id = n.Id, Name = n.Name, MimeType = n.Mime, AppProperties = new(n.Props), ModifiedUtc = n.Modified }).ToList());
    }

    public Task DeleteAsync(string id, CancellationToken ct) { lock (_nodes) _nodes.Remove(id); return Task.CompletedTask; }

    public Task ShareAsync(string id, string email, string role, CancellationToken ct) { lock (_nodes) Shares.Add((id, email, role)); return Task.CompletedTask; }
}
