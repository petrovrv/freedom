// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CO.Core.Transport;

namespace CO.Core.Modules;

// Freedom F10: reads the module registry of the CO server for this device (db.php op modules_list, signed with the
// device's own Google token like subs_put; the server takes the environment from the token) and keeps the last answer
// in modules.json, so the device starts with the modules it had when the site is down. Device files of a module come
// from agent/module.php?f=device/... without a token: they are checked against the sha256 of the signed-in list.
public interface IModuleSource
{
    Task<string> ListAsync(CancellationToken ct);
    Task<byte[]> FileAsync(ModuleEntry m, string rel, CancellationToken ct);
}

public sealed class ModuleRegistryClient : IModuleSource
{
    private readonly IAccessTokenSource _tokens;
    private readonly string _site;
    // Own client without redirects: the Google token must reach only the configured db.php.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public ModuleRegistryClient(IAccessTokenSource tokens, string siteUrl)
    {
        _tokens = tokens;
        _site = siteUrl.TrimEnd('/');
    }

    public async Task<string> ListAsync(CancellationToken ct)
    {
        if (!_site.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("no CO site configured");
        using var req = new HttpRequestMessage(HttpMethod.Post, _site + "/agent/db.php")
        {
            Content = new StringContent("{\"op\":\"modules_list\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-CO-Auth", "1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));
        using var resp = await Http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"modules_list: HTTP {(int)resp.StatusCode}");
        return text;
    }

    public async Task<byte[]> FileAsync(ModuleEntry m, string rel, CancellationToken ct)
    {
        if (!ModuleList.ValidDeviceFile(rel)) throw new ArgumentException("not a device file: " + rel);
        string url = $"{_site}/agent/module.php?m={Uri.EscapeDataString(m.Id)}&f={Uri.EscapeDataString(rel)}&v={Uri.EscapeDataString(m.Version)}";
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{m.Id}/{rel}: HTTP {(int)resp.StatusCode}");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}

// Device files of enabled modules on this computer: <dataDir>/modules/<id>/<version>/device/...; a file is used only
// while its sha256 matches the registry, and every check reads it again (a changed file is never run).
public sealed class ModuleFileCache
{
    private readonly string _root;
    public ModuleFileCache(string dataDir) => _root = Path.Combine(dataDir, "modules");

    public string Dir(ModuleEntry m) => Path.Combine(_root, m.Id, SafeVersion(m.Version));

    // The module's own working folder (kept over versions and switches; data stays when the module is off).
    public string StateDir(ModuleEntry m)
    {
        string d = Path.Combine(_root, m.Id, "state");
        Directory.CreateDirectory(d);
        return d;
    }

    public string PathOf(ModuleEntry m, string rel) => Path.Combine(Dir(m), rel.Replace('/', Path.DirectorySeparatorChar));

    public bool Matches(ModuleEntry m, string rel)
    {
        if (!m.Files.TryGetValue(rel, out string? sha)) return false;
        string p = PathOf(m, rel);
        if (!File.Exists(p)) return false;
        try
        {
            using var f = File.OpenRead(p);
            return Convert.ToHexString(SHA256.HashData(f)).Equals(sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Downloads the device files that are missing or changed; throws when a download does not match.</summary>
    public async Task<int> EnsureAsync(ModuleEntry m, IModuleSource src, CancellationToken ct)
    {
        int n = 0;
        foreach (var (rel, sha) in m.Files)
        {
            if (Matches(m, rel)) continue;
            byte[] data = await src.FileAsync(m, rel, ct);
            if (!Convert.ToHexString(SHA256.HashData(data)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{m.Id}/{rel}: download does not match the registry");
            string p = PathOf(m, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            string tmp = p + ".part";
            await File.WriteAllBytesAsync(tmp, data, ct);
            File.Move(tmp, p, true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            n++;
        }
        // older versions of this module are not needed any more (state/ stays)
        string parent = Path.Combine(_root, m.Id);
        foreach (string d in Directory.Exists(parent) ? Directory.GetDirectories(parent) : Array.Empty<string>())
        {
            string name = Path.GetFileName(d);
            if (name != "state" && name != SafeVersion(m.Version))
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    static string SafeVersion(string v) => string.Concat((v.Length == 0 ? "0" : v).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' ? c : '_'));
}
