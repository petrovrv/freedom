// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Corporate.SharePoint;

// TZ §7.4: SharePoint tools of the CO browser (moved from CO.Core BrowserTools, Freedom F1). They call the SharePoint REST
// API from inside a page of the SharePoint site, so they act with the user's own session and permissions; no tenant app
// registration is needed.
public static class SharePointTools
{
    // A managed device releases their results without a prompt: the connector ticked is the consent (core ToolHost).
    static SharePointTools() => CO.Core.Host.ToolHost.ReleaseByConsent("sharepoint_");

    public static IEnumerable<IDeviceTool> All(IBrowser browser) => All(_ => new BrowserUse(browser, null));

    public static IEnumerable<IDeviceTool> All(BrowserSource source) => new IDeviceTool[]
    {
        new SharePointList(source), new SharePointRead(source), new SharePointDownload(source),
    };
}

// A SharePoint location given as any URL the user can paste: browser address, sharing link with /r/, AllItems.aspx?id=...
public sealed record SharePointPath(Uri Origin, string Web, string ServerRelative) : RemoteItem
{
    public string WebUrl => Origin.GetLeftPart(UriPartial.Authority) + Web;
    public string WebUrlOrOrigin => Origin.GetLeftPart(UriPartial.Authority) + Web;
    public override string Url => Origin.GetLeftPart(UriPartial.Authority) + ServerRelative;
    public override string Name => ServerRelative.TrimEnd('/').Split('/').Last();

    public static SharePointPath? TryParse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https") return null;
        if (!u.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)) return null;

        // AllItems.aspx?id=%2Fsites%2FX%2FShared%20Documents%2FFolder (library views keep the folder in "id")
        string? id = System.Web.HttpUtility.ParseQueryString(u.Query)["id"];
        string path = !string.IsNullOrEmpty(id) ? id : Uri.UnescapeDataString(u.AbsolutePath);

        // Sharing links with a readable path: /:f:/r/sites/X/... , /:w:/r/..., /:x:/r/..., /:b:/r/...
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^/:[a-z]:/r(/.*)$");
        if (m.Success) path = m.Groups[1].Value;
        else if (path.StartsWith("/:", StringComparison.Ordinal)) return null;   // opaque sharing token: resolve in the browser first

        path = System.Text.RegularExpressions.Regex.Replace(path, @"/Forms/[^/]+\.aspx$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(id)) return null;
        path = path.TrimEnd('/');
        if (path.Length == 0) return null;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string web = parts.Length >= 2 && (parts[0].Equals("sites", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("teams", StringComparison.OrdinalIgnoreCase))
            ? "/" + parts[0] + "/" + parts[1] : "";
        return new SharePointPath(new Uri(u.GetLeftPart(UriPartial.Authority)), web, path);
    }

    // Opaque sharing links (/:f:/s/Site/EAbc…) resolve to a normal address after opening them in the browser.
    public static async Task<SharePointPath> ResolveAsync(IBrowser browser, string url, CancellationToken ct)
    {
        var p = TryParse(url);
        if (p != null) return p;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("invalid_args", "Not a SharePoint Online URL (https://<tenant>.sharepoint.com/...).");
        string landed = await browser.NavigateAsync(url, ct);
        if (TryParse(landed) == null && !SharePointApi.OnHost(landed, u.Host)
            && await SharePointApi.SignInAsync(browser, u.GetLeftPart(UriPartial.Authority), u.Host, ct))
            landed = await browser.NavigateAsync(url, ct);
        return TryParse(landed) ?? throw new ToolException("sign_in_needed",
            "The link did not open a SharePoint folder or file. Sign in to SharePoint in the CO browser (CO Desktop → Browser) and retry.");
    }

    public static string JsString(string s) => JsonSerializer.Serialize(s);
}

public static class SharePointApi
{
    public const long MaxFileBytes = 25L * 1024 * 1024;

    // Runs fetch() in a page of the SharePoint origin so the user's cookies apply.
    public static TimeSpan SignInWait { get; set; } = TimeSpan.FromSeconds(90);
    public static TimeSpan SignInPoll { get; set; } = TimeSpan.FromSeconds(1.5);

    public static async Task<JsonElement> RunAsync(IBrowser browser, SharePointPath p, string js, CancellationToken ct)
    {
        string site = p.WebUrl.Length > 0 ? p.WebUrl : p.Origin.ToString();
        string here = await browser.CurrentUrlAsync(ct);
        if (!OnHost(here, p.Origin.Host))
            here = await browser.NavigateAsync(site, ct);
        if (!OnHost(here, p.Origin.Host) && !await SignInAsync(browser, site, p.Origin.Host, ct))
            throw SignInNeeded();

        for (int attempt = 0; ; attempt++)
        {
            JsonElement r = await browser.EvaluateAsync(js, ct);
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("error", out var err))
                return r;
            string e = err.GetString() ?? "";
            if (!(e.StartsWith("401") || e.StartsWith("403")))
                throw new ToolException("sharepoint_error", "SharePoint: " + e);
            if (attempt > 0)
                throw new ToolException("no_access", "SharePoint refused access (" + e[..3] + ") after a fresh sign-in: the account has no rights to this item.");
            if (!await SignInAsync(browser, site, p.Origin.Host, ct))
                throw SignInNeeded();
        }
    }

    private static ToolException SignInNeeded() => new("sign_in_needed",
        "Sign-in did not finish within " + (int)SignInWait.TotalSeconds + " s. The CO browser window shows the sign-in page; " +
        "ask the user to complete it there (Okta password or code), then retry.");

    public static bool OnHost(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    // The session cookie expired: open the site and let Microsoft → Okta → SharePoint run in the CO browser. With a live
    // Okta session or Okta FastPass it completes by itself. Buttons that carry no secret ("Stay signed in", a single
    // account tile, Okta FastPass / Okta Verify push) are pressed automatically; passwords and codes never are — then
    // the CO browser comes to the front and the user finishes the sign-in there.
    public static async Task<bool> SignInAsync(IBrowser browser, string siteUrl, string host, CancellationToken ct)
    {
        string url = await browser.NavigateAsync(siteUrl, ct);
        DateTime until = DateTime.UtcNow + SignInWait;
        bool shown = false;
        int quiet = 0;
        while (!OnHost(url, host))
        {
            if (DateTime.UtcNow > until) return false;
            JsonElement step = default;
            try { step = await browser.EvaluateAsync(AutoStepJs, ct); }
            catch (InvalidOperationException) { }   // the page navigated while the script ran
            bool pressed = step.ValueKind == JsonValueKind.String && step.GetString()!.Length > 0;
            quiet = pressed ? 0 : quiet + 1;
            if (!shown && quiet >= 3)
            {
                await browser.BringToFrontAsync(ct);   // waits for the user (password, code)
                shown = true;
            }
            await Task.Delay(SignInPoll, ct);
            url = await browser.CurrentUrlAsync(ct);
        }
        return true;
    }

    public const string AutoStepJs =
        "(() => { if (window.__coStep && Date.now() - window.__coStep < 4000) return '';" +
        " const vis = e => e && !e.disabled && e.offsetParent !== null;" +
        " const press = (e, what) => { if (!vis(e)) return ''; window.__coStep = Date.now(); e.click(); return what; };" +
        " const h = location.host;" +
        " if (/^login\\.(microsoftonline|microsoft|windows)\\.(com|net)$/.test(h)) {" +
        "  if (document.querySelector('#KmsiCheckboxField, #KmsiDescription')) return press(document.querySelector('#idSIButton9'), 'kmsi');" +
        "  const tiles = [...document.querySelectorAll('#tilesHolder div[role=button], #tilesHolder .table')].filter(vis);" +
        "  if (tiles.length === 1 && !/another account|ander account|autre compte/i.test(tiles[0].innerText)) return press(tiles[0], 'account');" +
        "  return ''; }" +
        " if (!document.querySelector('#okta-sign-in, [data-se=o-form], .okta-sign-in-header')) return '';" +
        " if (document.querySelector('input[type=password], input[name$=passcode]')) return '';" +
        " const all = [...document.querySelectorAll('a, button, input[type=submit], input[type=button]')].filter(vis);" +
        " const label = e => ((e.innerText || e.value || '') + ' ' + (e.getAttribute('aria-label') || '') + ' ' + (e.getAttribute('data-se') || '')).toLowerCase();" +
        " const pick = re => all.find(e => re.test(label(e)));" +
        " return press(pick(/fastpass|signed_nonce/), 'fastpass') || press(pick(/send push|push notification|okta_verify-push/), 'push')" +
        "  || press(pick(/okta verify/), 'okta-verify') || ''; })()";

    public static string FolderJs(SharePointPath p) =>
        "(async () => { const web = " + SharePointPath.JsString(p.WebUrl) + ", path = " + SharePointPath.JsString(p.ServerRelative) + ";" +
        " const q = web + \"/_api/web/GetFolderByServerRelativePath(decodedurl=@p)?@p=\" + encodeURIComponent(\"'\" + path.replace(/'/g, \"''\") + \"'\")" +
        " + \"&$select=Name,ServerRelativeUrl,ItemCount&$expand=Folders,Files\";" +
        " const r = await fetch(q, { headers: { Accept: 'application/json;odata=nometadata' }, credentials: 'include' });" +
        " if (!r.ok) return { error: r.status + ' ' + (await r.text()).slice(0, 300) };" +
        " const j = await r.json();" +
        " return { name: j.Name, path: j.ServerRelativeUrl," +
        " folders: (j.Folders || []).filter(f => f.Name !== 'Forms').map(f => ({ name: f.Name, path: f.ServerRelativeUrl, items: f.ItemCount }))," +
        " files: (j.Files || []).map(f => ({ name: f.Name, path: f.ServerRelativeUrl, size: Number(f.Length), modified: f.TimeLastModified })) }; })()";

    public static string FileJs(SharePointPath p) =>
        "(async () => { const web = " + SharePointPath.JsString(p.WebUrl) + ", path = " + SharePointPath.JsString(p.ServerRelative) + ";" +
        " const q = web + \"/_api/web/GetFileByServerRelativePath(decodedurl=@p)/$value?@p=\" + encodeURIComponent(\"'\" + path.replace(/'/g, \"''\") + \"'\");" +
        " const r = await fetch(q, { credentials: 'include' });" +
        " if (!r.ok) return { error: r.status + ' ' + (await r.text()).slice(0, 300) };" +
        " const b = new Uint8Array(await r.arrayBuffer());" +
        " if (b.length > " + MaxFileBytes + ") return { error: 'too_large ' + b.length + ' bytes' };" +
        " let s = ''; for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));" +
        " return { size: b.length, b64: btoa(s) }; })()";

    public static async Task<byte[]> DownloadAsync(IBrowser browser, SharePointPath p, CancellationToken ct)
    {
        JsonElement r = await RunAsync(browser, p, FileJs(p), ct);
        return Convert.FromBase64String(r.GetProperty("b64").GetString() ?? "");
    }
}

internal sealed class SharePointList : IDeviceTool
{
    private readonly BrowserSource _src;
    public SharePointList(BrowserSource src) => _src = src;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "sharepoint_list", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "List a SharePoint Online folder (subfolders and files with size and date) using the user's CO browser session. Accepts the folder's browser address or sharing link.",
        InputSchema = BrowserTools.UrlSchema("SharePoint folder URL"),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string url = Args.Str(a, "url");
        var p = new ActionPlan { Tool = "sharepoint_list", Class = RiskClass.R, Summary = "list " + url };
        p.Scopes.Add(new Scope(ScopeKind.Domain, BrowserTools.Domain(url)));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        using var use = _src(env);
        var _b = use.Browser;
        var sp = await SharePointPath.ResolveAsync(_b, Args.Str(a, "url"), ct);
        var r = await SharePointApi.RunAsync(_b, sp, SharePointApi.FolderJs(sp), ct);
        var sb = new StringBuilder();
        string origin = sp.Origin.GetLeftPart(UriPartial.Authority);
        sb.AppendLine($"Folder {r.GetProperty("path").GetString()}");
        foreach (var f in r.GetProperty("folders").EnumerateArray())
            sb.AppendLine($"[dir]  {f.GetProperty("name").GetString()}  ({f.GetProperty("items").GetInt32()} items)  {origin}{f.GetProperty("path").GetString()}");
        foreach (var f in r.GetProperty("files").EnumerateArray())
            sb.AppendLine($"[file] {f.GetProperty("name").GetString()}  {f.GetProperty("size").GetInt64()} bytes  {f.GetProperty("modified").GetString()}  {origin}{f.GetProperty("path").GetString()}");
        return new ToolOutput { Content = sb.ToString(), Untrusted = true };
    }
}

internal sealed class SharePointRead : IDeviceTool
{
    private readonly BrowserSource _src;
    public SharePointRead(BrowserSource src) => _src = src;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "sharepoint_read", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Read the text of a SharePoint file (PDF, DOCX, XLSX, PPTX, text; up to 25 MB) with the user's CO browser session. offset/length are characters of the extracted text.",
        InputSchema = BrowserTools.UrlSchema("SharePoint file URL (as listed by sharepoint_list)",
            ("offset", "integer", "Start character, default 0", false), ("length", "integer", "Characters, default 60000", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string url = Args.Str(a, "url");
        var p = new ActionPlan { Tool = "sharepoint_read", Class = RiskClass.R, Summary = "read " + url };
        p.Scopes.Add(new Scope(ScopeKind.Domain, BrowserTools.Domain(url)));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        using var use = _src(env);
        var _b = use.Browser;
        var sp = await SharePointPath.ResolveAsync(_b, Args.Str(a, "url"), ct);
        byte[] bytes = await SharePointApi.DownloadAsync(_b, sp, ct);
        string tmp = Path.Combine(Path.GetTempPath(), "co-sp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(sp.Name));
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            string text = TextExtractor.Extract(tmp);
            long offset = Math.Clamp(Args.OptLong(a, "offset", 0), 0, text.Length);
            long length = Math.Clamp(Args.OptLong(a, "length", 60_000), 0, text.Length - offset);
            return new ToolOutput
            {
                Content = $"[{sp.Name}: characters {offset}-{offset + length} of {text.Length}]\n" + text.Substring((int)offset, (int)length),
                Untrusted = true,
            };
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }
}

internal sealed class SharePointDownload : IDeviceTool
{
    private readonly BrowserSource _src;
    public SharePointDownload(BrowserSource src) => _src = src;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "sharepoint_download", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Save a SharePoint file (up to 25 MB) into a writable connected folder. 'to' is '<folderId>/<relative path>'; a path ending with '/' keeps the SharePoint file name.",
        InputSchema = BrowserTools.UrlSchema("SharePoint file URL", ("to", "string", "Target '<folderId>/<relative>'", true)),
    };

    private static (ResolvedPath target, string url) Target(JsonElement a, ToolEnv env)
    {
        string url = Args.Str(a, "url"), to = Args.Str(a, "to");
        if (to.EndsWith('/'))
        {
            var sp = SharePointPath.TryParse(url) ?? throw new ToolException("invalid_args", "With a folder target the URL must name the file; list the folder with sharepoint_list first.");
            to += sp.Name;
        }
        var r = env.Folders.Resolve(to);
        FsTools.Require(r, File.Exists(r.FullPath) ? Rights.Edit : Rights.Create);
        // Files of project folders are read as trusted; a site's file must not enter the Run that way.
        if (r.Folder.Project)
            throw new ToolException("not_allowed", $"'{r.Folder.Id}' is a project folder (trusted content). Save SharePoint files to a non-project folder.");
        return (r, url);
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var (r, url) = Target(a, env);
        var p = new ActionPlan
        {
            Tool = "sharepoint_download", Class = RiskClass.W, WritesToSynced = r.Folder.Synced,
            Summary = $"download {url}\n  → {r.Virtual}{(File.Exists(r.FullPath) ? " (replaces the existing file)" : "")}",
        };
        p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        using var use = _src(env);
        var _b = use.Browser;
        var (r, url) = Target(a, env);
        var sp = await SharePointPath.ResolveAsync(_b, url, ct);
        byte[] bytes = await SharePointApi.DownloadAsync(_b, sp, ct);
        int step = env.Snapshots.BeginStep(env.RunId);
        env.Snapshots.CaptureFile(env.RunId, step, r.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(r.FullPath)!);
        await File.WriteAllBytesAsync(r.FullPath, bytes, ct);
        return new ToolOutput { Content = $"Saved {sp.Name} ({bytes.Length} bytes) to {r.Virtual} (snapshot step {step})." };
    }
}
