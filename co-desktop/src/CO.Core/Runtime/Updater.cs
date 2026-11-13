// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CO.Core.Crypto;
using CO.Core.Protocol;
using CO.Core.Transport;

namespace CO.Core.Runtime;

// update.json in the device folder, written by Bridge (TZ §6.5).
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("publishedUtc")] public string PublishedUtc { get; set; } = "";
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";
    // 0.9.1: the installer comes from the site (Drive blocks the exe as malware, update.bin answers 403).
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    public static byte[] Payload(string version, string sha256Hex, long size) =>
        Wire.Payload("co.desktop.update.v1", version, sha256Hex, size.ToString(CultureInfo.InvariantCulture));
}

public sealed record UpdateReady(string Version, string SetupPath);

// Checks the Bridge-signed release offered in the device folder and downloads a newer installer.
public sealed class DeviceUpdater
{
    public const long MaxSize = 400L * 1024 * 1024;
    // The only address the Google token is sent to; any other url in the manifest is refused. Freedom 0.4.0: the site
    // of this computer's settings (a neutral build has no site built in); the built-in site until the app sets it.
    public string SiteBase { get; set; } = InstallDefaults.SiteUrl;
    public string SiteDownloadUrl => SiteBase.TrimEnd('/') + "/agent/desktop.php?op=download";
    private static readonly HttpClient SiteHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly IDriveFiles _drive;
    private readonly string _dir;

    // Google sign-in of this device; desktop.php serves the installer only to CO users.
    public IAccessTokenSource? SiteTokens { get; set; }

    public DeviceUpdater(IDriveFiles drive, string dataDir)
    {
        _drive = drive;
        _dir = Path.Combine(dataDir, "updates");
    }

    public string Status { get; private set; } = "not checked";

    // Returns null when there is nothing newer or the offer fails a check; Status says why.
    public async Task<UpdateReady?> CheckAsync(DeviceState state, string currentVersion, CancellationToken ct)
    {
        if (state.UpdateManifestFileId.Length == 0 || state.Bridge == null)
        {
            Status = "update files not created yet";
            return null;
        }
        byte[] raw = await _drive.DownloadAsync(state.UpdateManifestFileId, ct);
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(raw); } catch (JsonException) { m = null; }
        if (m == null || m.Version.Length == 0)
        {
            Status = $"{currentVersion} (no release offered) · checked {DateTime.Now:HH:mm}";
            return null;
        }
        if (m.Url.Length > 0)
        {
            if (SiteBase.Length == 0 || m.Url != SiteDownloadUrl)
            {
                Status = $"release {m.Version} rejected: unknown download address";
                return null;
            }
            return await EvaluateAsync(m, state.Bridge.BridgePublicKey, currentVersion, () => DownloadFromSiteAsync(ct), ct);
        }
        if (state.UpdateBinFileId.Length == 0)
        {
            Status = "update files not created yet";
            return null;
        }
        return await EvaluateAsync(m, state.Bridge.BridgePublicKey, currentVersion,
            () => _drive.DownloadAsync(state.UpdateBinFileId, ct), ct);
    }

    // Runs only after the manifest signature and version checks passed (EvaluateAsync).
    private async Task<byte[]> DownloadFromSiteAsync(CancellationToken ct)
    {
        if (SiteTokens == null) throw new InvalidOperationException("not signed in to Google");
        using var req = new HttpRequestMessage(HttpMethod.Get, SiteDownloadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await SiteTokens.GetAccessTokenAsync(ct));
        using var resp = await SiteHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"site download: HTTP {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentLength is long len && len > MaxSize) throw new InvalidOperationException("site download: too large");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // Split out for tests: all checks that decide whether an offered installer may run.
    public async Task<UpdateReady?> EvaluateAsync(UpdateManifest m, string bridgeKey, string currentVersion,
        Func<Task<byte[]>> download, CancellationToken ct)
    {
        if (!Version.TryParse(m.Version, out var offered) || !Version.TryParse(currentVersion, out var current))
        {
            Status = "offered release has an invalid version";
            return null;
        }
        if (!KeyUtil.Verify(bridgeKey, UpdateManifest.Payload(m.Version, m.Sha256, m.Size), m.Signature))
        {
            Status = $"release {m.Version} rejected: signature does not match the paired Bridge";
            return null;
        }
        if (offered <= current)
        {
            Status = $"{currentVersion} is current · checked {DateTime.Now:HH:mm}";
            return null;
        }
        if (m.Size <= 0 || m.Size > MaxSize)
        {
            Status = $"release {m.Version} rejected: size {m.Size}";
            return null;
        }

        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"CO-Desktop-Setup-{offered.ToString(3)}.exe");
        if (!(File.Exists(path) && new FileInfo(path).Length == m.Size && HashFile(path) == m.Sha256))
        {
            Status = $"downloading {m.Version}…";
            byte[] bin = await download();
            if (bin.LongLength != m.Size || Wire.Sha256Hex(bin) != m.Sha256)
            {
                Status = $"release {m.Version}: downloaded installer does not match the manifest yet (site not published?)";
                return null;
            }
            string tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bin, ct);
            File.Move(tmp, path, true);
        }
        foreach (string old in Directory.GetFiles(_dir, "CO-Desktop-Setup-*.exe"))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch (IOException) { }
        Status = $"{m.Version} downloaded, installs when no agent call is running";
        return new UpdateReady(offered.ToString(3), path);
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
