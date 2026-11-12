// SPDX-License-Identifier: AGPL-3.0-or-later
using AiWorkbenchBridge;
using CO.Core.Crypto;
using CO.Core.Protocol;
using CO.Core.Runtime;
using Xunit;

namespace CO.Core.Tests;

public class UpdaterTests
{
    private static (UpdateManifest m, byte[] bin) Offer(EcdsaSigner bridge, string version, byte[]? bin = null)
    {
        bin ??= "installer bytes"u8.ToArray();
        string sha = Wire.Sha256Hex(bin);
        var m = new UpdateManifest { Version = version, Sha256 = sha, Size = bin.Length, FileName = $"CO-Desktop-Setup-{version}.exe" };
        // Bridge builds the signed bytes with its own (C# 5) code; the device must accept them.
        m.Signature = Convert.ToBase64String(bridge.Sign(DeviceWire.UpdatePayload(version, sha, bin.Length)));
        return (m, bin);
    }

    private static string Dir() => Path.Combine(Path.GetTempPath(), "co-upd-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NewerSignedReleaseIsDownloadedAndVerified()
    {
        var bridge = EcdsaSigner.CreateEphemeral();
        var (m, bin) = Offer(bridge, "0.5.0");
        var u = new DeviceUpdater(new UnusedDrive(), Dir());
        var r = await u.EvaluateAsync(m, bridge.PublicKey, "0.4.0", () => Task.FromResult(bin), CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal("0.5.0", r!.Version);
        Assert.Equal(bin, File.ReadAllBytes(r.SetupPath));
    }

    [Fact]
    public async Task SameOrOlderVersionIsIgnored()
    {
        var bridge = EcdsaSigner.CreateEphemeral();
        var u = new DeviceUpdater(new UnusedDrive(), Dir());
        foreach (string v in new[] { "0.4.0", "0.3.9" })
        {
            var (m, bin) = Offer(bridge, v);
            Assert.Null(await u.EvaluateAsync(m, bridge.PublicKey, "0.4.0", () => throw new InvalidOperationException("no download"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task ForeignSignatureAndTamperedInstallerAreRejected()
    {
        var bridge = EcdsaSigner.CreateEphemeral();
        var other = EcdsaSigner.CreateEphemeral();
        var u = new DeviceUpdater(new UnusedDrive(), Dir());

        var (m1, bin1) = Offer(other, "0.5.0");
        Assert.Null(await u.EvaluateAsync(m1, bridge.PublicKey, "0.4.0", () => Task.FromResult(bin1), CancellationToken.None));
        Assert.Contains("signature", u.Status);

        var (m2, _) = Offer(bridge, "0.5.0");
        byte[] evil = "installer bytez"u8.ToArray();
        Assert.Null(await u.EvaluateAsync(m2, bridge.PublicKey, "0.4.0", () => Task.FromResult(evil), CancellationToken.None));

        var (m3, bin3) = Offer(bridge, "0.5.0");
        m3.Version = "9.9.9";   // version is covered by the signature
        Assert.Null(await u.EvaluateAsync(m3, bridge.PublicKey, "0.4.0", () => Task.FromResult(bin3), CancellationToken.None));
    }

    [Fact]
    public async Task SiteUrlOtherThanTheOwnSiteIsRefusedBeforeAnyDownload()
    {
        var bridge = EcdsaSigner.CreateEphemeral();
        var (m, _) = Offer(bridge, "0.5.0");
        m.Url = "https://evil.example/agent/desktop.php?op=download";
        var tokens = new CountingTokens();
        var u = new DeviceUpdater(new ManifestDrive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m)), Dir()) { SiteTokens = tokens };
        var state = new DeviceState { UpdateManifestFileId = "man", Bridge = new BridgeInfo { BridgePublicKey = bridge.PublicKey } };
        Assert.Null(await u.CheckAsync(state, "0.4.0", CancellationToken.None));
        Assert.Contains("unknown download address", u.Status);
        Assert.Equal(0, tokens.Calls);
    }

    private sealed class CountingTokens : CO.Core.Transport.IAccessTokenSource
    {
        public int Calls;
        public Task<string> GetAccessTokenAsync(CancellationToken ct) { Calls++; return Task.FromResult("t"); }
    }

    private sealed class ManifestDrive(byte[] manifest) : CO.Core.Transport.IDriveFiles
    {
        private static Exception E() => new InvalidOperationException("not used");
        public Task<string> CreateFolderAsync(string n, string? p, CancellationToken ct) => throw E();
        public Task<string> CreateFileAsync(string n, string p, byte[] c, string m, CancellationToken ct) => throw E();
        public Task UpdateFileAsync(string id, byte[] c, string m, CancellationToken ct) => throw E();
        public Task<byte[]> DownloadAsync(string id, CancellationToken ct) => id == "man" ? Task.FromResult(manifest) : throw E();
        public Task<List<CO.Core.Transport.DriveFile>> ListChildrenAsync(string p, CancellationToken ct) => throw E();
        public Task DeleteAsync(string id, CancellationToken ct) => throw E();
        public Task ShareAsync(string id, string e, string r, CancellationToken ct) => throw E();
    }

    private sealed class UnusedDrive : CO.Core.Transport.IDriveFiles
    {
        private static Exception E() => new InvalidOperationException("not used");
        public Task<string> CreateFolderAsync(string n, string? p, CancellationToken ct) => throw E();
        public Task<string> CreateFileAsync(string n, string p, byte[] c, string m, CancellationToken ct) => throw E();
        public Task UpdateFileAsync(string id, byte[] c, string m, CancellationToken ct) => throw E();
        public Task<byte[]> DownloadAsync(string id, CancellationToken ct) => throw E();
        public Task<List<CO.Core.Transport.DriveFile>> ListChildrenAsync(string p, CancellationToken ct) => throw E();
        public Task DeleteAsync(string id, CancellationToken ct) => throw E();
        public Task ShareAsync(string id, string e, string r, CancellationToken ct) => throw E();
    }
}
