// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Browser;

namespace CO.Core.Tests;

public class ChromeForTestingTests
{
    private const string Index = """
        {"timestamp":"2026-11-08T00:00:00Z","channels":{"Stable":{"channel":"Stable","version":"157.0.8103.38","revision":"1",
         "downloads":{"chrome":[
          {"platform":"mac-x64","url":"https://storage.googleapis.com/chrome-for-testing-public/157.0.8103.38/mac-x64/chrome-mac-x64.zip"},
          {"platform":"mac-arm64","url":"https://storage.googleapis.com/chrome-for-testing-public/157.0.8103.38/mac-arm64/chrome-mac-arm64.zip"}]}}}}
        """;

    [Fact]
    public void PicksTheStableDownloadForThePlatform()
    {
        var (version, url) = ChromeForTesting.PickDownload(Index, "mac-arm64");
        Assert.Equal("157.0.8103.38", version);
        Assert.EndsWith("/mac-arm64/chrome-mac-arm64.zip", url);
    }

    [Fact]
    public void RefusesForeignHostsAndMissingPlatforms()
    {
        string evil = Index.Replace("https://storage.googleapis.com", "https://example.com");
        Assert.Throws<InvalidDataException>(() => ChromeForTesting.PickDownload(evil, "mac-arm64"));
        Assert.Throws<InvalidDataException>(() => ChromeForTesting.PickDownload(Index.Replace("https://", "http://"), "mac-arm64"));
        Assert.Throws<InvalidDataException>(() => ChromeForTesting.PickDownload(Index, "linux64"));
        Assert.Throws<InvalidDataException>(() => ChromeForTesting.PickDownload("{}", "mac-arm64"));
    }

    [Fact]
    public void SingletonLockOfAMissingOrDeadProcessIsFree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "co-cft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(EdgeBrowser.SingletonLockHeld(dir));
            try { File.CreateSymbolicLink(Path.Combine(dir, "SingletonLock"), "host-" + int.MaxValue); }
            catch (Exception) { return; }   // Windows without the symlink privilege
            Assert.False(EdgeBrowser.SingletonLockHeld(dir));
            File.Delete(Path.Combine(dir, "SingletonLock"));
            File.CreateSymbolicLink(Path.Combine(dir, "SingletonLock"), "host-" + Environment.ProcessId);
            Assert.True(EdgeBrowser.SingletonLockHeld(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
