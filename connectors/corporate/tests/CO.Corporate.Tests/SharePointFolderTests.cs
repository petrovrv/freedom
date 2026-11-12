// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using CO.Core.Auth;
using CO.Core.Tools;
using Xunit;

namespace CO.Corporate.Tests;

// A small SharePoint Online: the REST calls SharePointService makes, over an in-memory library.
public sealed class FakeSharePoint : HttpMessageHandler
{
    public const string Host = "https://contoso.sharepoint.com";
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase) { "/sites/X/Shared Documents", "/sites/X/Shared Documents/Team" };
    public readonly List<string> Calls = new();
    public readonly List<string> Recycled = new();
    public SharePointService? Service;      // to issue FedAuth at the end of the Okta flow
    public bool RequireCookie;
    public bool Office;          // the Okta app link signs in by the Windows logon (Desktop SSO)
    public int PushPolls;

    private static string Arg(Uri u, string name)
    {
        string v = HttpUtility.ParseQueryString(u.Query)[name] ?? "";
        return v.Length >= 2 && v[0] == '\'' ? v[1..^1].Replace("''", "'") : v;
    }

    private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var u = req.RequestUri!;
        string path = Uri.UnescapeDataString(u.AbsolutePath);
        Calls.Add(req.Method + " " + path);

        // --- Okta classic API and the hand-over to SharePoint
        if (u.Host == "okta.example.com")
        {
            if (path.StartsWith("/home/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = req,
                    Content = new StringContent(Office
                        ? "<form method='post' action='" + Host + "/_forms/default.aspx?wa=wsignin1.0'><input type='hidden' name='wa' value='wsignin1.0'/><input type='hidden' name='wresult' value='x'/></form>"
                        : "<html><div id='okta-sign-in'><input name='identifier'/></div></html>"),
                };
            if (path == "/api/v1/authn")
                return Json(new
                {
                    status = "MFA_REQUIRED", stateToken = "st1",
                    _embedded = new { factors = new[] { new { factorType = "push", _links = new { verify = new { href = "https://okta.example.com/api/v1/authn/factors/f1/verify" } } } } },
                });
            if (path.EndsWith("/verify"))
                return ++PushPolls < 3
                    ? Json(new { status = "MFA_CHALLENGE", factorResult = "WAITING", _links = new { next = new { href = "https://okta.example.com/api/v1/authn/factors/f1/verify" } } })
                    : Json(new { status = "SUCCESS", sessionToken = "tok" });
            if (path == "/login/sessionCookieRedirect")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = req,
                    Content = new StringContent("<html><body onload='document.forms[0].submit()'><form method='post' action='" + Host +
                        "/_forms/default.aspx?wa=wsignin1.0'><input type='hidden' name='wa' value='wsignin1.0'/><input type='hidden' name='wresult' value='&lt;t/&gt;'/></form></body></html>"),
                };
        }
        if (path == "/_forms/default.aspx" && req.Method == HttpMethod.Post)
        {
            string form = await req.Content!.ReadAsStringAsync(ct);
            if (form.Contains("wresult")) Service!.Cookies.Add(new Cookie("FedAuth", "ok", "/", "contoso.sharepoint.com"));
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent("<html></html>") };
        }

        if (RequireCookie && !Service!.Cookies.GetCookies(new Uri(Host)).Any(c => c.Name == "FedAuth"))
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://login.microsoftonline.com/x") } };

        if (path == "/sites/X/_api/contextinfo") return Json(new { FormDigestValue = "d1", FormDigestTimeoutSeconds = 1800 });

        if (path.Contains("GetFolderByServerRelativePath(decodedurl=@p)") && req.Method == HttpMethod.Get)
        {
            string p = Arg(u, "@p");
            if (!Folders.Contains(p)) return Json(new { error = "nf" }, HttpStatusCode.NotFound);
            if (u.Query.Contains("Exists"))
                return Json(new { Name = p[(p.LastIndexOf('/') + 1)..], Exists = true, ItemCount = 1, TimeLastModified = "2026-10-01T10:00:00Z", ServerRelativeUrl = p });
            return Json(new
            {
                Name = p[(p.LastIndexOf('/') + 1)..], ServerRelativeUrl = p,
                Folders = Folders.Where(f => f.StartsWith(p + "/") && !f[(p.Length + 1)..].Contains('/'))
                    .Select(f => new { Name = f[(f.LastIndexOf('/') + 1)..], ServerRelativeUrl = f, ItemCount = 0, TimeLastModified = "2026-10-01T10:00:00Z" }),
                Files = Files.Keys.Where(f => f.StartsWith(p + "/") && !f[(p.Length + 1)..].Contains('/'))
                    .Select(f => new { Name = f[(f.LastIndexOf('/') + 1)..], ServerRelativeUrl = f, Length = Files[f].Length.ToString(), TimeLastModified = "2026-10-02T10:00:00Z" }),
            });
        }
        if (path.Contains("GetFileByServerRelativePath(decodedurl=@p)/$value"))
        {
            string p = Arg(u, "@p");
            return Files.TryGetValue(p, out var b) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) } : Json(new { }, HttpStatusCode.NotFound);
        }
        if (path.Contains("GetFileByServerRelativePath(decodedurl=@p)/recycle()"))
        {
            string p = Arg(u, "@p");
            if (!Files.Remove(p)) return Json(new { }, HttpStatusCode.NotFound);
            Recycled.Add(p);
            return Json(new { });
        }
        if (path.Contains("GetFileByServerRelativePath(decodedurl=@p)"))
        {
            string p = Arg(u, "@p");
            return Files.TryGetValue(p, out var b)
                ? Json(new { Name = p[(p.LastIndexOf('/') + 1)..], Length = b.Length.ToString(), TimeLastModified = "2026-10-02T10:00:00Z", ServerRelativeUrl = p })
                : Json(new { }, HttpStatusCode.NotFound);
        }
        if (path.Contains("/Files/AddUsingPath"))
        {
            Assert.Equal("d1", req.Headers.GetValues("X-RequestDigest").Single());
            string folder = Arg(u, "@f"), name = Arg(u, "@n");
            if (!Folders.Contains(folder)) return Json(new { }, HttpStatusCode.NotFound);
            Files[folder + "/" + name] = await req.Content!.ReadAsByteArrayAsync(ct);
            return Json(new { });
        }
        if (path.Contains("/Folders/AddUsingPath"))
        {
            Folders.Add(Arg(u, "@p"));
            return Json(new { });
        }
        if (path == "/sites/X/_api/search/query")
        {
            string q = HttpUtility.ParseQueryString(u.Query)["querytext"]!;
            var rows = Files.Where(f => q.Contains(Host + "/sites/X/Shared Documents/Team") && f.Key.StartsWith("/sites/X/Shared Documents/Team/")
                                        && Encoding.UTF8.GetString(f.Value).Contains("budget"))
                .Select(f => new { Cells = new object[]
                {
                    new { Key = "Path", Value = Host + Uri.EscapeUriString(f.Key) },
                    new { Key = "HitHighlightedSummary", Value = "the <c0>budget</c0> is fixed<ddd/>" },
                } });
            return Json(new { PrimaryQueryResult = new { RelevantResults = new { Table = new { Rows = rows } } } });
        }
        return Json(new { error = "unexpected " + path }, HttpStatusCode.BadRequest);
    }
}

public sealed class MemorySecrets : ISecretStore
{
    public readonly Dictionary<string, string> Items = new();
    public string? Read(string target) => Items.TryGetValue(target, out var v) ? v : null;
    public void Write(string target, string secret) => Items[target] = secret;
    public void Delete(string target) => Items.Remove(target);
}

public class SharePointFolderTests
{
    private static (Fixture f, FakeSharePoint sp) Connected(bool writable = true)
    {
        var sp = new FakeSharePoint();
        var f = new Fixture();
        f.Folders.RemoteFolders = new SharePointFs(sp.Service = new SharePointService(null, null, null, sp));
        var path = SharePointPath.TryParse(FakeSharePoint.Host + "/sites/X/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FX%2FShared%20Documents%2FTeam")!;
        f.Folders.AddRemote(path, "team", writable);
        return (f, sp);
    }

    [Fact]
    public async Task BinaryFilesAreCopiedToAndFromSharePoint()
    {
        var (f, sp) = Connected();
        byte[] docx = { 0x50, 0x4B, 0x03, 0x04, 0, 1, 2, 255 };
        File.WriteAllBytes(Path.Combine(f.Work, "Layout.docx"), docx);

        var up = await f.Run("fs_copy", new { from = "work/Layout.docx", to = "team/notes/" });
        Assert.True(up.Ok, up.Error?.Message);
        Assert.Equal(docx, sp.Files["/sites/X/Shared Documents/Team/notes/Layout.docx"]);
        Assert.Equal("exists", (await f.Run("fs_copy", new { from = "work/Layout.docx", to = "team/notes/Layout.docx" })).Error!.Code);
        Assert.True((await f.Run("fs_copy", new { from = "work/Layout.docx", to = "team/notes/Layout.docx", overwrite = true })).Ok);

        var down = await f.Run("fs_copy", new { from = "team/notes/Layout.docx", to = "od/Layout.docx" });
        Assert.True(down.Ok, down.Error?.Message);
        Assert.Equal(docx, File.ReadAllBytes(Path.Combine(f.Synced, "Layout.docx")));
        // A site's file does not land in the trusted project folder.
        Assert.Equal("not_allowed", (await f.Run("fs_copy", new { from = "team/notes/Layout.docx", to = "work/x.docx" })).Error!.Code);
    }

    [Fact]
    public async Task CopyNeedsCreateAtTheTarget()
    {
        var (f, _) = Connected(writable: false);
        File.WriteAllBytes(Path.Combine(f.Work, "a.pdf"), new byte[] { 1, 2 });
        Assert.Equal("read_only", (await f.Run("fs_copy", new { from = "work/a.pdf", to = "team/a.pdf" })).Error!.Code);
    }

    [Fact]
    public async Task SharePointFolderWorksLikeALocalOne()
    {
        var (f, sp) = Connected();
        sp.Files["/sites/X/Shared Documents/Team/plan.txt"] = Encoding.UTF8.GetBytes("alpha\nbudget 2027\n");

        var list = await f.Run("fs_list", new { path = "team" });
        Assert.True(list.Ok, list.Error?.Message);
        Assert.Contains("plan.txt", list.Content);

        var read = await f.Run("fs_read", new { path = "team/plan.txt" });
        Assert.True(read.Ok, read.Error?.Message);
        Assert.Contains("budget 2027", read.Content);
        Assert.True(read.Untrusted);   // not a project folder

        var write = await f.Run("fs_write", new { path = "team/sub/new.txt", content = "hello" });
        Assert.True(write.Ok, write.Error?.Message);
        Assert.Equal("hello", Encoding.UTF8.GetString(sp.Files["/sites/X/Shared Documents/Team/sub/new.txt"]));
        Assert.Contains("/sites/X/Shared Documents/Team/sub", sp.Folders);

        var exists = await f.Run("fs_write", new { path = "team/plan.txt", content = "x" });
        Assert.Equal("exists", exists.Error!.Code);

        var edit = await f.Run("fs_edit", new { path = "team/plan.txt", oldText = "alpha", newText = "ALPHA" });
        Assert.True(edit.Ok, edit.Error?.Message);
        Assert.StartsWith("ALPHA", Encoding.UTF8.GetString(sp.Files["/sites/X/Shared Documents/Team/plan.txt"]));

        var stat = await f.Run("fs_stat", new { path = "team/plan.txt" });
        Assert.Contains("\"file\"", stat.Content);

        var search = await f.Run("fs_search", new { path = "team", query = "budget" });
        Assert.True(search.Ok, search.Error?.Message);
        Assert.Contains("team/plan.txt: the budget is fixed…", search.Content);

        var move = await f.Run("fs_move", new { from = "team/sub/new.txt", to = "team/renamed.txt" });
        Assert.False(move.Ok);   // the fake has no MoveTo endpoint: a clear SharePoint error, nothing lost
        Assert.True(sp.Files.ContainsKey("/sites/X/Shared Documents/Team/sub/new.txt"));

        f.Folders.SetRights("team", null, CO.Core.Policy.Rights.All);   // the Delete box is the permission
        var del = await f.Run("fs_delete", new { paths = new[] { "team/sub/new.txt" } });
        Assert.True(del.Ok, del.Error?.Message);
        Assert.Contains("/sites/X/Shared Documents/Team/sub/new.txt", sp.Recycled);
        Assert.Empty(f.Approvals.Seen);
    }

    [Fact]
    public async Task ReadOnlySharePointFolderRefusesWrites()
    {
        var (f, _) = Connected(writable: false);
        var r = await f.Run("fs_write", new { path = "team/a.txt", content = "x" });
        Assert.Equal("read_only", r.Error!.Code);
    }

    [Fact]
    public void PathsCannotEscapeTheSharePointFolder()
    {
        var (f, _) = Connected();
        Assert.Throws<ToolException>(() => f.Folders.Resolve("team/../Secret"));
        var r = f.Folders.Resolve("team/a/b.txt");
        Assert.Equal("/sites/X/Shared Documents/Team/a/b.txt", Assert.IsType<SharePointPath>(r.Remote).ServerRelative);
    }

    [Fact]
    public async Task OktaPasswordFallbackSignsInWithPush()
    {
        var sp = new FakeSharePoint { RequireCookie = true };
        var secrets = new MemorySecrets();
        var svc = new SharePointService(null, secrets, null, sp, signedIn: false) { PushPoll = TimeSpan.FromMilliseconds(1) };
        sp.Service = svc;
        svc.SaveOkta(new OktaFallback { OktaUrl = "https://okta.example.com", AppLink = "https://okta.example.com/home/o365/1/2", UserName = "r@example.com" }, "pw");
        Assert.Equal("pw", secrets.Items[OktaFallback.CredentialTarget]);

        var path = SharePointPath.TryParse(FakeSharePoint.Host + "/sites/X/Shared Documents/Team")!;
        var items = await svc.ListAsync(path, default);
        Assert.NotNull(items);
        Assert.Equal(3, sp.PushPolls);
        Assert.Equal("Okta password", svc.LastSignIn);   // away from the office: integrated sign-in ended on the Okta page
        Assert.Contains("POST /_forms/default.aspx", sp.Calls);
    }

    [Fact]
    public async Task InTheOfficeTheWindowsLogonSignsInWithoutPassword()
    {
        var sp = new FakeSharePoint { RequireCookie = true, Office = true };
        var svc = new SharePointService(null, new MemorySecrets(), null, sp, signedIn: false);
        sp.Service = svc;
        svc.SaveOkta(new OktaFallback { OktaUrl = "https://okta.example.com", AppLink = "https://okta.example.com/home/o365/1/2", UserName = "r@example.com" }, null);
        var path = SharePointPath.TryParse(FakeSharePoint.Host + "/sites/X/Shared Documents/Team")!;
        await svc.ListAsync(path, default);
        Assert.DoesNotContain("POST /api/v1/authn", sp.Calls);
        Assert.Equal(0, sp.PushPolls);
        Assert.Equal("Windows sign-in (office network)", svc.LastSignIn);
    }

    [Fact]
    public async Task WithoutBrowserOrPasswordAskForSignIn()
    {
        var sp = new FakeSharePoint { RequireCookie = true };
        var svc = new SharePointService(null, new MemorySecrets(), null, sp, signedIn: false);
        sp.Service = svc;
        var path = SharePointPath.TryParse(FakeSharePoint.Host + "/sites/X/Shared Documents/Team")!;
        var ex = await Assert.ThrowsAsync<ToolException>(() => svc.ListAsync(path, default));
        Assert.Equal("sign_in_needed", ex.Code);
    }
}
