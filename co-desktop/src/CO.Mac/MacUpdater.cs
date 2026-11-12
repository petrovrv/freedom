// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CO.Core;
using CO.Core.Crypto;
using CO.Core.Protocol;
using CO.Core.Runtime;
using CO.Core.Transport;

// The resident runs on macOS only (Main refuses other systems).
[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]

namespace CO.Mac;

// Self-update (SPEC R2). The resident creates resident-update.json in its device folder; Bridge writes there a manifest of
// the newest Mac release signed with the key this Mac pinned at pairing. The archive comes from the CO site with this
// device's Google token (the only address the token goes to). A release passes signature, version, size and sha256
// checks, is unpacked next to the app ("<app>.new"), signed ad hoc and must answer "version" before the swap; the old
// files stay as "*.old" until the new copy serves calls, and come back if it does not get there in three starts.
public sealed class MacUpdater
{
    public const string ManifestName = "resident-update.json";
    public static string DownloadUrl => InstallDefaults.SiteUrl + "/agent/mac.php?op=update"; // Freedom F1
    public const long MaxSize = 200L * 1024 * 1024;
    private static readonly string[] ArchiveFiles = { "co-resident", "oauth-client.json" };

    private readonly IDriveFiles _drive;
    private readonly IAccessTokenSource _tokens;
    private readonly HttpClient _http;
    private string? _manifestId;

    public MacUpdater(IDriveFiles drive, IAccessTokenSource tokens, HttpClient http)
    {
        _drive = drive;
        _tokens = tokens;
        _http = http;
    }

    public string Status { get; private set; } = "not checked";

    public static byte[] Payload(string version, string sha256Hex, long size) =>
        Wire.Payload("co.resident.update.v1", version, sha256Hex, size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string StagedDir => Paths.AppDir + ".new";
    private static string PendingFile => Path.Combine(Paths.DataDir, "update-pending.json");
    // A version that was rolled back is never installed again; Bridge has to offer a newer one.
    private static string RejectedFile => Path.Combine(Paths.DataDir, "update-rejected.txt");

    // Finds or creates resident-update.json (drive.file: Bridge may write only into a file the device created).
    private async Task<string> ManifestIdAsync(DeviceState st, CancellationToken ct)
    {
        if (_manifestId != null) return _manifestId;
        var existing = await _drive.ListChildrenAsync(st.Folder!.FolderId, ct);
        _manifestId = existing.FirstOrDefault(f => f.Name == ManifestName)?.Id
            ?? await _drive.CreateFileAsync(ManifestName, st.Folder.FolderId, "{}"u8.ToArray(), "application/json", ct);
        return _manifestId;
    }

    // Returns the version staged in StagedDir, or null (Status says why).
    public async Task<string?> CheckAsync(DeviceState st, string currentVersion, CancellationToken ct)
    {
        if (st.Folder == null || st.Bridge == null) { Status = "not paired"; return null; }
        byte[] raw = await _drive.DownloadAsync(await ManifestIdAsync(st, ct), ct);
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(raw); } catch (JsonException) { m = null; }
        if (m == null || m.Version.Length == 0) { Status = $"{currentVersion}, no release offered"; return null; }
        if (!Accept(m, st.Bridge.BridgePublicKey, currentVersion, out string why)) { Status = why; return null; }
        if (File.Exists(RejectedFile) && File.ReadAllText(RejectedFile).Trim() == m.Version)
        {
            Status = $"release {m.Version} was rolled back on this Mac; waiting for a newer one";
            return null;
        }
        byte[] bin = await DownloadAsync(ct);
        if (bin.LongLength != m.Size || Wire.Sha256Hex(bin) != m.Sha256)
        {
            Status = $"release {m.Version}: the site archive does not match the manifest yet";
            return null;
        }
        Stage(bin, m.Version);
        Status = $"{m.Version} staged, installs when no agent call is running";
        return m.Version;
    }

    // All checks on the offered manifest before anything is downloaded. Public for tests.
    public static bool Accept(UpdateManifest m, string bridgeKey, string currentVersion, out string why)
    {
        if (m.Url != DownloadUrl) { why = $"release {m.Version} rejected: unknown download address"; return false; }
        if (!Version.TryParse(m.Version, out var offered) || !Version.TryParse(currentVersion, out var current))
        { why = "offered release has an invalid version"; return false; }
        if (!KeyUtil.Verify(bridgeKey, Payload(m.Version, m.Sha256, m.Size), m.Signature))
        { why = $"release {m.Version} rejected: signature does not match the paired Bridge"; return false; }
        if (offered <= current) { why = $"{currentVersion} is current"; return false; }
        if (m.Size <= 0 || m.Size > MaxSize) { why = $"release {m.Version} rejected: size {m.Size}"; return false; }
        why = "";
        return true;
    }

    private async Task<byte[]> DownloadAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"site download: HTTP {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentLength is long len && len > MaxSize) throw new InvalidOperationException("site download: too large");
        // Without Content-Length (chunked) the limit is enforced while reading.
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(chunk, ct)) > 0)
        {
            if (buf.Length + n > MaxSize) throw new InvalidOperationException("site download: too large");
            buf.Write(chunk, 0, n);
        }
        return buf.ToArray();
    }

    // Unpacks the two known files into StagedDir, signs the binary ad hoc and checks that it starts with the offered version.
    private static void Stage(byte[] archive, string version)
    {
        string dir = StagedDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using (var gz = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            TarEntry? e;
            while ((e = tar.GetNextEntry()) != null)
            {
                string name = e.Name.StartsWith("./") ? e.Name[2..] : e.Name;
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !ArchiveFiles.Contains(name))
                    throw new InvalidOperationException("unexpected entry in the release archive: " + e.Name);
                e.ExtractToFile(Path.Combine(dir, name), overwrite: true);
            }
        }
        string bin = Path.Combine(dir, "co-resident");
        foreach (string f in ArchiveFiles)
            if (!File.Exists(Path.Combine(dir, f))) throw new InvalidOperationException("release archive lacks " + f);
        File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(Path.Combine(dir, "oauth-client.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var (code, _, se) = Proc.Run("/usr/bin/codesign", new[] { "--force", "--sign", "-", bin }, timeoutMs: 120_000);
        if (code != 0) throw new InvalidOperationException("codesign failed: " + se.Trim());
        var (vc, vo, ve) = Proc.Run(bin, new[] { "version" }, timeoutMs: 60_000);
        if (vc != 0 || vo.Trim() != version)
            throw new InvalidOperationException($"the new program does not start ({vc}): {(vo + ve).Trim()}");
    }

    // Every step keeps an executable at the LaunchAgent path: the current files are backed up as *.old, then the staged ones
    // are renamed over them (rename(2) replaces the target atomically on the same volume). The rollback record is written
    // first, so a crash in between still leads to OnStart. The running process keeps its open image; launchd starts the
    // new copy after this one exits.
    public static void Swap(string from, string to)
    {
        // Complete backups (copied to *.old.tmp, renamed) exist before the record that may restore them.
        foreach (string f in ArchiveFiles)
        {
            string cur = Path.Combine(Paths.AppDir, f);
            File.Copy(cur, cur + ".old.tmp", overwrite: true);
            File.Move(cur + ".old.tmp", cur + ".old", overwrite: true);
        }
        WritePending(new Pending { From = from, To = to, Starts = 0 });
        try
        {
            File.Move(Path.Combine(StagedDir, "oauth-client.json"), Path.Combine(Paths.AppDir, "oauth-client.json"), overwrite: true);
            File.Move(Path.Combine(StagedDir, "co-resident"), Path.Combine(Paths.AppDir, "co-resident"), overwrite: true);
        }
        catch
        {
            PutOldBack();
            File.Delete(PendingFile);
            throw;
        }
    }

    // *.old -> *.rollback (copy) -> renamed over the current files; never leaves the path empty.
    private static void PutOldBack()
    {
        foreach (string f in ArchiveFiles)
        {
            string cur = Path.Combine(Paths.AppDir, f), old = cur + ".old", tmp = cur + ".rollback";
            if (!File.Exists(old)) continue;
            File.Copy(old, tmp, overwrite: true);
            File.Move(tmp, cur, overwrite: true);
        }
    }

    // Temporary file renamed over the record: a crash while writing never leaves a truncated one.
    private static void WritePending(Pending p)
    {
        string tmp = PendingFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p));
        File.Move(tmp, PendingFile, overwrite: true);
    }

    private sealed class Pending
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Starts { get; set; }
    }

    // Called at every start of "run", under the writer lock (a second copy waiting for the lock is not a start).
    // False = this copy is a failed update and the previous one was put back: exit.
    public static bool OnStart(string version)
    {
        if (!File.Exists(PendingFile)) return true;
        Pending? p;
        try { p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingFile)); } catch (JsonException) { p = null; }
        bool haveOld = File.Exists(Path.Combine(Paths.AppDir, "co-resident.old"));
        if (p == null || !haveOld) { File.Delete(PendingFile); return true; }
        // The swap did not finish (crash before the binary was replaced): the old binary runs; put its other files back.
        if (p.To != version) { PutOldBack(); File.Delete(PendingFile); return true; }
        p.Starts++;
        if (p.Starts <= 3) { WritePending(p); return true; }
        Log.Write($"update {p.From} -> {p.To} did not reach 'serving' in 3 starts; putting {p.From} back");
        File.WriteAllText(RejectedFile, p.To + "\n");
        PutOldBack();
        File.Delete(PendingFile);
        return false;
    }

    // The new copy serves calls: the update is final.
    public static void Confirm(string version)
    {
        if (!File.Exists(PendingFile)) return;
        try { File.Delete(PendingFile); } catch (IOException) { }
        foreach (string f in ArchiveFiles)
            try { File.Delete(Path.Combine(Paths.AppDir, f + ".old")); } catch (IOException) { }
        try { if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, true); } catch (Exception ex) { Log.Write("cleanup " + StagedDir + ": " + ex.Message); }
        Log.Write("update to " + version + " confirmed");
    }
}
