// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core;
using CO.Core.Runtime;
using CO.Core.Transport;
using Xunit;

namespace CO.Core.Tests;

// Freedom F1: the names of the install come from the install.json built in (or, in a neutral build, from the
// install.json of this computer), never from constants.
public class InstallDefaultsTests
{
    [Fact]
    public void ValuesAreConsistent()
    {
        bool built = typeof(InstallDefaults).Assembly.GetManifestResourceNames().Contains("CO.Core.install.json");
        Assert.Equal(built, InstallDefaults.Source == "built-in");
        if (InstallDefaults.Source.Length == 0)
        {
            // CI and other machines without install.json: empty values, nothing invented
            Assert.Equal("", InstallDefaults.SiteUrl);
            Assert.Empty(InstallDefaults.Members);
            return;
        }
        Assert.Matches("^https?://", InstallDefaults.SiteUrl);
        Assert.False(InstallDefaults.SiteUrl.EndsWith('/'));
        Assert.NotEqual("", InstallDefaults.OwnerEnv);
        Assert.Equal(InstallDefaults.OwnerEmail, InstallDefaults.Members[InstallDefaults.OwnerEnv]);
        Assert.DoesNotContain(InstallDefaults.OwnerEnv, InstallDefaults.MemberEnvs);
        Assert.Equal(InstallDefaults.Members.Count - 1, InstallDefaults.MemberEnvs.Count);
    }

    [Fact]
    public void CandidatesEndNextToTheApp()
    {
        var c = InstallDefaults.CandidateFiles().ToList();
        Assert.True(c.Count >= 2);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "install.json"), c[^1]);
    }

    [Fact]
    public void ReadFileNeverThrows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "co-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(JsonValueKind.Undefined, InstallDefaults.ReadFile(Path.Combine(dir, "none.json")).ValueKind);
            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ not json");
            Assert.Equal(JsonValueKind.Undefined, InstallDefaults.ReadFile(bad).ValueKind);
            File.WriteAllText(bad, "[1,2]");
            Assert.Equal(JsonValueKind.Undefined, InstallDefaults.ReadFile(bad).ValueKind);
            string good = Path.Combine(dir, "install.json");
            File.WriteAllText(good, "{\"site\":{\"url\":\"https://co.example.org/\"},\"members\":[]}");
            JsonElement e = InstallDefaults.ReadFile(good);
            Assert.Equal("https://co.example.org", InstallDefaults.SiteUrlOf(e));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("https://co.example.org", "https://co.example.org")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("http://co.example.org", "")]
    [InlineData("http://127.0.0.1:8080", "")]
    [InlineData("https://co.example.org/agent", "")]
    [InlineData("ftp://co.example.org", "")]
    [InlineData("co.example.org", "")]
    [InlineData("", "")]
    public void SiteUrlOnlyHttp(string url, string expected)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { site = new { url } }));
        Assert.Equal(expected, InstallDefaults.SiteUrlOf(doc.RootElement));
    }

    [Fact]
    public void UpdaterUsesTheSiteOfThisComputer()
    {
        var u = new DeviceUpdater(null!, Path.GetTempPath()) { SiteBase = "https://co.example.net/" };
        Assert.Equal("https://co.example.net/agent/desktop.php?op=download", u.SiteDownloadUrl);
    }

    [Fact]
    public async Task UpdaterWithoutSiteRefusesSiteDownloads()
    {
        var u = new DeviceUpdater(new NoDrive("{\"version\":\"9.9.9\",\"url\":\"/agent/desktop.php?op=download\"}"), Path.GetTempPath()) { SiteBase = "" };
        var st = new DeviceState { UpdateManifestFileId = "m", Bridge = new BridgeInfo() };
        Assert.Null(await u.CheckAsync(st, "0.1.0", CancellationToken.None));
        Assert.Contains("unknown download address", u.Status);
    }

    private sealed class NoDrive(string manifest) : IDriveFiles
    {
        public Task<byte[]> DownloadAsync(string id, CancellationToken ct) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(manifest));
        public Task<string> CreateFolderAsync(string name, string? parentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CreateFileAsync(string name, string parentId, byte[] content, string mime, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateFileAsync(string id, byte[] content, string mime, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<DriveFile>> ListChildrenAsync(string parentId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task ShareAsync(string id, string email, string role, CancellationToken ct) => throw new NotSupportedException();
    }
}
