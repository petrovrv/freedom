// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Host;
using CO.Core.Tools;
using Xunit;

namespace CO.Corporate.Tests;

public class SharePointPathTests
{
    [Theory]
    [InlineData("https://amentum.sharepoint.com/sites/Proj/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FProj%2FShared%20Documents%2FSpecs&viewid=1",
        "/sites/Proj", "/sites/Proj/Shared Documents/Specs")]
    [InlineData("https://amentum.sharepoint.com/sites/Proj/Shared%20Documents/Forms/AllItems.aspx",
        "/sites/Proj", "/sites/Proj/Shared Documents")]
    [InlineData("https://amentum.sharepoint.com/:x:/r/sites/Proj/Shared%20Documents/Plan.xlsx?d=w123&csf=1",
        "/sites/Proj", "/sites/Proj/Shared Documents/Plan.xlsx")]
    [InlineData("https://amentum.sharepoint.com/teams/T1/Docs/a.docx", "/teams/T1", "/teams/T1/Docs/a.docx")]
    public void ParsesReadableUrls(string url, string web, string path)
    {
        var p = SharePointPath.TryParse(url)!;
        Assert.Equal(web, p.Web);
        Assert.Equal(path, p.ServerRelative);
        Assert.Equal("https://amentum.sharepoint.com" + web, p.WebUrl);
    }

    [Theory]
    [InlineData("https://amentum.sharepoint.com/:f:/s/Proj/EAbcDEF")]       // opaque sharing link
    [InlineData("https://amentum.sharepoint.com/sites/Proj/SitePages/Home.aspx")]
    [InlineData("http://amentum.sharepoint.com/sites/Proj/Docs")]
    [InlineData("https://example.com/sites/Proj/Docs")]
    public void RejectsOtherUrls(string url) => Assert.Null(SharePointPath.TryParse(url));
}

public class SharePointToolTests
{
    private const string FolderJson =
        "{\"name\":\"Specs\",\"path\":\"/sites/Proj/Shared Documents/Specs\",\"folders\":[{\"name\":\"Old\",\"path\":\"/sites/Proj/Shared Documents/Specs/Old\",\"items\":3}]," +
        "\"files\":[{\"name\":\"Plan.txt\",\"path\":\"/sites/Proj/Shared Documents/Specs/Plan.txt\",\"size\":5,\"modified\":\"2026-09-01T10:00:00Z\"}]}";

    [Fact]
    public async Task ListIsUntrustedAndTaintsTheRun()
    {
        var b = new FakeBrowser { Answer = _ => FolderJson };
        using var f = new Fixture(browser: b, extraTools: SharePointTools.All(b));
        var r = await f.Run("sharepoint_list", new { url = "https://amentum.sharepoint.com/sites/Proj/Shared%20Documents/Specs" }, run: "sp");
        Assert.True(r.Ok, r.Error?.Message);
        Assert.True(r.Untrusted);
        Assert.Contains("Plan.txt", r.Content);
        Assert.True(f.Host.IsTainted("sp"));
        Assert.StartsWith("https://amentum.sharepoint.com/sites/Proj", b.Url);   // tab moved to the site origin
    }

    [Fact]
    public async Task RepeatedRefusalAfterFreshSignInIsNoAccess()
    {
        var b = new FakeBrowser { Answer = _ => "{\"error\":\"401 Unauthorized\"}" };
        using var f = new Fixture(browser: b, extraTools: SharePointTools.All(b));
        var r = await f.Run("sharepoint_list", new { url = "https://amentum.sharepoint.com/sites/Proj/Docs" });
        Assert.False(r.Ok);
        Assert.Equal("no_access", r.Error!.Code);
    }

    [Fact]
    public async Task DownloadWritesAfterApprovalAndRefusesProjectFolders()
    {
        var b = new FakeBrowser { Answer = _ => "{\"size\":5,\"b64\":\"" + Convert.ToBase64String("hello"u8.ToArray()) + "\"}" };
        using var f = new Fixture(browser: b, extraTools: SharePointTools.All(b));
        const string url = "https://amentum.sharepoint.com/sites/Proj/Shared%20Documents/Specs/Plan.txt";

        var denied = await f.Run("sharepoint_download", new { url, to = "work/" });
        Assert.False(denied.Ok);
        Assert.Equal("not_allowed", denied.Error!.Code);

        f.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Allow));
        var ok = await f.Run("sharepoint_download", new { url, to = "od/in/" });
        Assert.True(ok.Ok, ok.Error?.Message);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(f.Synced, "in", "Plan.txt")));
    }
}


// Session expiry: the site redirects to Okta, the sign-in finishes by itself, the call goes through.
public sealed class RedirectingBrowser : IBrowser
{
    public string Url { get; private set; } = "about:blank";
    public int Polls, Fronted, Lists;
    public bool UserNeeded;
    public Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        Url = url.Contains(".sharepoint.com") && Lists == 0 ? "https://login.example-okta.com/app/sso" : url;
        return Task.FromResult(Url);
    }
    public Task<string> CurrentUrlAsync(CancellationToken ct)
    {
        if (Url.Contains("okta") && ++Polls >= (UserNeeded ? 6 : 2)) Url = "https://contoso.sharepoint.com/sites/X";
        return Task.FromResult(Url);
    }
    public Task BringToFrontAsync(CancellationToken ct) { Fronted++; return Task.CompletedTask; }
    public Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        string json = expression.Contains("__coStep") ? "\"\"" :
            "{\"name\":\"X\",\"path\":\"/sites/X/Shared Documents\",\"folders\":[],\"files\":[]}";
        if (!expression.Contains("__coStep")) Lists++;
        return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }
}

public class SharePointSignInTests
{
    public SharePointSignInTests() { SharePointApi.SignInPoll = TimeSpan.FromMilliseconds(1); SharePointApi.SignInWait = TimeSpan.FromSeconds(5); }

    [Fact]
    public async Task ExpiredSessionRenewsSilently()
    {
        var b = new RedirectingBrowser();
        using var f = new Fixture(managed: true, browser: b, extraTools: SharePointTools.All(b));
        var r = await f.Run("sharepoint_list", new { url = "https://contoso.sharepoint.com/sites/X/Shared Documents" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(0, b.Fronted);
        Assert.Empty(f.Approvals.Seen);   // managed device: SharePoint results are released without a prompt
    }

    [Fact]
    public async Task SignInNeedingTheUserBringsBrowserToFront()
    {
        var b = new RedirectingBrowser { UserNeeded = true };
        using var f = new Fixture(browser: b, extraTools: SharePointTools.All(b));
        var r = await f.Run("sharepoint_list", new { url = "https://contoso.sharepoint.com/sites/X/Shared Documents" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(1, b.Fronted);
    }
}
