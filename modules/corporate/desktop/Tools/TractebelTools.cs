// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Corporate.Tools;

// Tractebel document management (TE portal, HyperInfo) through the CO browser's session. The protocol follows
// C:\Boring\Bor_Connect.py (class Portal): sign-in /okta/authteportal.php → Okta → SAML → HyperInfoIIS.dll;
// search object=165833; document card object=128500;:A=<id>; files action=tblsql;object=208951;:ACTION=GETFILES;
// contributors object=137700 :Z=GENBLOCS; lists of values action=tblsql&type=report&object=191213&:A=GETSQL.
// Every request runs as fetch() inside a teportal page, so the user's cookies apply; nothing is stored by CO.
public static class TractebelTools
{
    public const string Host = "teportal.tractebel.myengie.com";
    public const string Base = "https://" + Host + "/HyperInfoIIS.dll";
    public const string Entry = "https://" + Host + "/okta/authteportal.php?host=" + Host + "&protocol=https";

    // A managed device releases their results without a prompt: the connector ticked is the consent (core ToolHost).
    static TractebelTools() => CO.Core.Host.ToolHost.ReleaseByConsent("te_");

    public static IEnumerable<IDeviceTool> All(IBrowser browser) => new IDeviceTool[]
    {
        new TeSearch(browser), new TeDoc(browser), new TeProjects(browser), new TeFetch(browser), new TeDownload(browser),
    };

    internal static bool AllowedHost(string host) =>
        host.Equals(Host, StringComparison.OrdinalIgnoreCase) || host.Equals("teportal1.tractebel.myengie.com", StringComparison.OrdinalIgnoreCase);

    // The portal home page names the user: var gUsername = "…". Without the Okta sign-in it serves a guest
    // (GUEST_SUEZ) who sees no documents (Bor_Connect._te_is_guest_html).
    public const string UserJs =
        "(async () => { try { const r = await fetch(" + "'" + Base + "'" + ", { credentials: 'include' }); const t = await r.text();" +
        " const m = t.match(/gUsername\\s*=\\s*['\"]([^'\"]*)/); return { user: m ? m[1] : '' }; } catch (e) { return { user: '' }; } })()";

    internal static bool IsUser(JsonElement r)
    {
        string u = Str(r, "user");
        return u.Length > 0 && !u.StartsWith("GUEST", StringComparison.OrdinalIgnoreCase);
    }

    public static TimeSpan SignInWait { get; set; } = TimeSpan.FromSeconds(90);

    // Puts the CO browser on the portal as the signed-in user: /okta/authteportal.php → Okta → SAML → portal.
    // Buttons without a secret (FastPass, push) are pressed as for SharePoint; otherwise the user finishes in the CO browser.
    public static async Task<string> EnsureSignedInAsync(IBrowser b, CancellationToken ct)
    {
        string here = await b.CurrentUrlAsync(ct);
        if (!SharePointApi.OnHost(here, Host)) here = await b.NavigateAsync(Base, ct);
        if (SharePointApi.OnHost(here, Host))
        {
            var who = await b.EvaluateAsync(UserJs, ct);
            if (IsUser(who)) return Str(who, "user");
        }
        await b.NavigateAsync(Entry, ct);
        DateTime until = DateTime.UtcNow + SignInWait;
        bool shown = false;
        int quiet = 0;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(SharePointApi.SignInPoll, ct);
            string url = await b.CurrentUrlAsync(ct);
            if (SharePointApi.OnHost(url, Host) && !url.Contains("/okta/", StringComparison.OrdinalIgnoreCase))
            {
                JsonElement who = default;
                try { who = await b.EvaluateAsync(UserJs, ct); } catch (InvalidOperationException) { continue; }
                if (IsUser(who)) return Str(who, "user");
            }
            JsonElement step = default;
            try { step = await b.EvaluateAsync(SharePointApi.AutoStepJs, ct); } catch (InvalidOperationException) { }
            quiet = step.ValueKind == JsonValueKind.String && step.GetString()!.Length > 0 ? 0 : quiet + 1;
            if (!shown && quiet >= 4) { await b.BringToFrontAsync(ct); shown = true; }
        }
        throw new ToolException("sign_in_needed",
            "Tractebel Portal answers as a guest and the Okta sign-in did not finish. The CO browser shows the sign-in page; " +
            "ask the user to complete it there, then retry.");
    }

    // Signed-in portal page first, then js runs there (fetch with the user's cookies).
    internal static async Task<JsonElement> RunAsync(IBrowser b, string js, CancellationToken ct)
    {
        await EnsureSignedInAsync(b, ct);
        for (int attempt = 0; ; attempt++)
        {
            JsonElement r = await b.EvaluateAsync(js, ct);
            bool login = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.True;
            if (!login) return r;
            if (attempt > 0) throw new ToolException("sign_in_needed", "The portal answered with its sign-in page; ask the user to sign in to the Tractebel Portal in the CO browser.");
            await EnsureSignedInAsync(b, ct);
        }
    }

    // fetch() of a portal URL; HTML answers come back as readable text plus the links on the page.
    internal static string FetchJs(string url, string? postForm = null, bool raw = false) =>
        "(async () => { const url = " + JsonSerializer.Serialize(url) + ", form = " + JsonSerializer.Serialize(postForm) + ";" +
        " const opt = form === null ? { credentials: 'include' } : { method: 'POST', credentials: 'include'," +
        "   headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: form };" +
        " let r; try { r = await fetch(url, opt); } catch (e) { return { error: 'fetch_failed ' + ((e && e.message) || e) }; }" +
        " const type = r.headers.get('content-type') || '';" +
        (raw
            ? " const b = new Uint8Array(await r.arrayBuffer()); if (b.length > 67108864) return { error: 'too_large ' + b.length };" +
              " let s = ''; for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000));" +
              " const cd = r.headers.get('content-disposition') || '';" +
              " return { status: r.status, url: r.url, type, disposition: cd, size: b.length, b64: btoa(s) }; })()"
            : " const text = await r.text();" +
              " if (/okta|login\\.microsoftonline/i.test(new URL(r.url).host) || (/<form[^>]+(login|okta|saml)/i.test(text) && !/gUsername/.test(text))) return { login: true };" +
              " if (!/html/i.test(type)) return { status: r.status, url: r.url, type, text: text.slice(0, 400000) };" +
              " const d = new DOMParser().parseFromString(text, 'text/html');" +
              " d.querySelectorAll('script,style').forEach(e => e.remove());" +
              " const links = [...d.querySelectorAll('a[href]')].map(a => ({ text: (a.innerText || a.textContent || '').trim().slice(0, 200), href: new URL(a.getAttribute('href'), url).href }))" +
              "   .filter(l => l.href.startsWith('http')).slice(0, 500);" +
              " const sel = (text.match(/var theSelectedDOC = new selDOC\\(([^;]+)\\);/) || [])[1] || null;" +
              " return { status: r.status, url: r.url, type, text: (d.body ? (d.body.innerText || d.body.textContent || '') : '').replace(/[ \\t]+\\n/g, '\\n').replace(/\\n{3,}/g, '\\n\\n').trim().slice(0, 200000), links, selDoc: sel }; })()");

    internal static string Q(string s) => Uri.EscapeDataString(s);

    internal static void CheckUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !AllowedHost(u.Host))
            throw new ToolException("invalid_args", "url must be an https address on " + Host + ".");
    }

    internal static ToolOutput Out(object o) => new() { Content = FsTools.Json(o), Untrusted = true };

    internal static string Str(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static void ThrowIfError(JsonElement r)
    {
        string e = Str(r, "error");
        if (e.Length > 0) throw new ToolException("portal_error", "Tractebel Portal: " + e);
    }

    // Document id from an id or a card link (…object=128500;:A=12345).
    public static string DocId(string idOrUrl)
    {
        var m = System.Text.RegularExpressions.Regex.Match(idOrUrl, @"(?:^|[;&?]:?A=)(\d+)");
        if (m.Success) return m.Groups[1].Value;
        if (idOrUrl.All(char.IsAsciiDigit) && idOrUrl.Length > 0) return idOrUrl;
        throw new ToolException("invalid_args", "Give a document id (digits) or its portal link (…object=128500;:A=<id>).");
    }

    // Search URL of object 165833 with the fields of the portal's search form (Bor_Connect.FindDocumentGen).
    public static string SearchUrl(string number, string project, string subject, string author, string keyword,
        string dateFrom, string dateUntil, bool allRevisions)
    {
        var f = new (string k, string v)[]
        {
            ("PROJ_INDICATOR_IDname", ""), ("PROJ_INDICATOR_ID", ""), ("PROJ_NUMBERname", project), ("PROJ_NUMBER", ""),
            ("DOC_NATUREPopUp", ""), ("DOC_NATURE", ""), ("DOC_GENREname", ""), ("DOC_GENRE", ""), ("DEC_TECHNIQUE", ""),
            ("NUMBER", number), ("ISSUER_IDcode", ""), ("ISSUER_IDname", ""), ("ISSUER_ID", ""), ("REF_DOC_EXT", ""),
            ("AUTHORname", author), ("AUTHOR", ""), ("DOCUMENT_DATEFROM", dateFrom), ("DOCUMENT_DATEUNTIL", dateUntil),
            ("DOCUMENT_DATE", ""), ("SUBJECT", subject), ("KEYWORDname", keyword), ("KEYWORD", ""),
            ("CHOICEBTN_ALL_REV", allRevisions ? "ALL_REV" : ""), ("ALL_REV", allRevisions ? "ALL_REV" : ""),
        };
        return Base + "?action=tblsql&object=165833&" + string.Join("&", f.Select(x => x.k + "=" + Q(x.v)));
    }
}

internal abstract class TeTool : IDeviceTool
{
    protected readonly IBrowser B;
    protected TeTool(IBrowser b) => B = b;
    public abstract ToolDescriptor Descriptor { get; }
    public virtual ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var p = new ActionPlan { Tool = Descriptor.Name, Class = RiskClass.R, Summary = Descriptor.Name };
        p.Scopes.Add(new Scope(ScopeKind.Domain, TractebelTools.Host));
        return p;
    }
    public abstract Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct);
}

internal sealed class TeSearch : TeTool
{
    public TeSearch(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "te_search", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Search the Tractebel document management (TE portal) like its search form: document number, project " +
            "number, subject words, author, keyword, document date range. Returns matching documents with id, card link and the " +
            "row text (reference, revision, title, dates). Then te_doc gives the card, files and contributors.",
        InputSchema = Schema.Obj(
            ("number", "string", "Document number (e.g. 0768841) or part of it", false),
            ("project", "string", "Project number (e.g. P.012345)", false),
            ("subject", "string", "Words of the title/subject", false),
            ("author", "string", "Author name", false),
            ("keyword", "string", "Keyword", false),
            ("date_from", "string", "Document date from, dd/mm/yyyy", false),
            ("date_until", "string", "Document date until, dd/mm/yyyy", false),
            ("all_revisions", "boolean", "Include older revisions, default false", false)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string S(string n) => (Args.OptStr(a, n) ?? "").Trim();
        if (new[] { "number", "project", "subject", "author", "keyword", "date_from", "date_until" }.All(n => S(n).Length == 0))
            throw new ToolException("invalid_args", "Give at least one search field.");
        string url = TractebelTools.SearchUrl(S("number"), S("project"), S("subject"), S("author"), S("keyword"),
            S("date_from"), S("date_until"), Args.OptBool(a, "all_revisions", false));
        // Rows of the result table with a document card link; the table text is kept for the model to read.
        string js = TractebelTools.FetchJs(url).Replace("return { status: r.status, url: r.url, type, text: (d.body",
            "const rows = [...d.querySelectorAll('tr')].map(tr => { const l = [...tr.querySelectorAll('a[href]')].map(x => x.getAttribute('href')).find(h => /object=128500/.test(h || ''));" +
            " if (!l) return null; const m = l.match(/:?A=(\\d+)/); return { id: m ? m[1] : null, url: new URL(l, url).href, row: (tr.innerText || '').replace(/\\s+/g, ' ').trim().slice(0, 400) }; }).filter(x => x);" +
            " const seen = new Set(); const docs = rows.filter(x => !seen.has(x.id) && seen.add(x.id));" +
            " if (docs.length) return { status: r.status, docs: docs.slice(0, 200), total: docs.length };" +
            " return { status: r.status, url: r.url, type, text: (d.body");
        var r = await TractebelTools.RunAsync(B, js, ct);
        TractebelTools.ThrowIfError(r);
        return TractebelTools.Out(r);
    }
}

internal sealed class TeDoc : TeTool
{
    public TeDoc(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "te_doc", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Card of a Tractebel Portal document: metadata (reference, revision, status, project, authors, dates, " +
            "confidentiality), the list of its files (GETFILES; use te_download with a file link) and the contributors (authors, " +
            "checkers, approvers with dates). id = document id or card link from te_search.",
        InputSchema = Schema.Obj(("id", "string", "Document id or card link (…object=128500;:A=<id>)", true)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string id = TractebelTools.DocId(Args.Str(a, "id"));
        var card = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(TractebelTools.Base + "?object=128500;:A=" + id), ct);
        TractebelTools.ThrowIfError(card);

        // selDOC(...) arguments: the 5th is the internal id GETFILES expects (Bor_Connect.GetDocumentsInfoFromUrl).
        object? files = null;
        string sel = TractebelTools.Str(card, "selDoc");
        var parts = System.Text.RegularExpressions.Regex.Matches(sel, @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""|[^,]+").Select(m => m.Value.Trim()).ToList();
        if (parts.Count >= 5 && long.TryParse(parts[4].Trim('\'', '"'), out long fileKey))
        {
            var fr = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(TractebelTools.Base + "?action=tblsql;object=208951;:ACTION=GETFILES;:B=" + fileKey), ct);
            files = new { key = fileKey, answer = TractebelTools.Str(fr, "text"), links = fr.TryGetProperty("links", out var fl) ? fl.Clone() : default(JsonElement?) };
        }

        var contrib = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(TractebelTools.Base,
            "object=137700&" + TractebelTools.Q(":A") + "=" + id + "&" + TractebelTools.Q(":Z") + "=GENBLOCS"), ct);

        return TractebelTools.Out(new
        {
            id, url = TractebelTools.Base + "?object=128500;:A=" + id,
            card = TractebelTools.Str(card, "text"),
            files,
            contributors = TractebelTools.Str(contrib, "text"),
            links = card.TryGetProperty("links", out var l) ? l.Clone() : default(JsonElement?),
        });
    }
}

internal sealed class TeProjects : TeTool
{
    public TeProjects(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "te_projects", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Look up Tractebel Portal lists by text: projects by number (kind=project), authors/employees by name " +
            "(kind=author), project leaders (kind=leader), document types (kind=doctype), project indicators (kind=indicator). " +
            "Returns the portal's id and display value of each match.",
        InputSchema = Schema.Obj(
            ("text", "string", "Filter text, e.g. a project number or a surname", true),
            ("kind", "string", "project (default), author, leader, doctype or indicator", false)),
    };

    private static readonly Dictionary<string, string> ParamIds = new()
    {
        ["project"] = "154500", ["author"] = "166033", ["leader"] = "247233", ["doctype"] = "107970", ["indicator"] = "154900",
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string kind = (Args.OptStr(a, "kind") ?? "project").Trim().ToLowerInvariant();
        if (!ParamIds.TryGetValue(kind, out var pid)) throw new ToolException("invalid_args", "kind: project, author, leader, doctype or indicator.");
        string url = TractebelTools.Base + "?action=tblsql&type=report&charset=iso-8859-1&mime=text/html&object=191213&" +
            TractebelTools.Q(":A") + "=GETSQL&" + TractebelTools.Q(":paramId") + "=" + pid + "&" + TractebelTools.Q(":TE_LOV_FILTER") + "=" + TractebelTools.Q(Args.Str(a, "text"));
        var r = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(url), ct);
        TractebelTools.ThrowIfError(r);
        return TractebelTools.Out(new { kind, answer = TractebelTools.Str(r, "text") });
    }
}

internal sealed class TeFetch : TeTool
{
    public TeFetch(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "te_fetch", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Open any Tractebel Portal address (HyperInfoIIS.dll on teportal.tractebel.myengie.com) with the user's " +
            "session and get its readable text and links; form = POST body (x-www-form-urlencoded). For pages the other te_ " +
            "tools do not cover (project cards, tasks, person pages, review status).",
        InputSchema = Schema.Obj(
            ("url", "string", "https address on teportal.tractebel.myengie.com", true),
            ("form", "string", "Optional POST body, e.g. object=137700&%3AA=123&%3AZ=GENBLOCS", false)),
    };

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string url = Args.Str(a, "url");
        TractebelTools.CheckUrl(url);
        var r = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(url, Args.OptStr(a, "form")), ct);
        TractebelTools.ThrowIfError(r);
        return TractebelTools.Out(r);
    }
}

internal sealed class TeDownload : TeTool
{
    public TeDownload(IBrowser b) : base(b) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "te_download", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Download a file of a Tractebel Portal document (a file link from te_doc) into a connected folder or a " +
            "SharePoint folder; then read it with fs_read. Needs Create on the target (Edit to replace).",
        InputSchema = Schema.Obj(
            ("url", "string", "File link on teportal.tractebel.myengie.com (from te_doc)", true),
            ("to", "string", "Target '<folderId>/<relative>'; ending with '/' keeps the portal's file name", true),
            ("overwrite", "boolean", "Replace an existing file, default false", false)),
    };

    private static ResolvedPath Target(JsonElement a, ToolEnv env, string? fileName)
    {
        string to = Args.Str(a, "to");
        if (to.EndsWith('/')) to += fileName ?? "document";
        var t = env.Folders.Resolve(to);
        FsTools.Require(t, FsTools.WriteNeed(t, Args.OptBool(a, "overwrite", false)));
        if (t.Folder.Project)
            throw new ToolException("not_allowed", $"'{t.Folder.Id}' is a project folder (trusted content). Save portal files to a non-project folder.");
        return t;
    }

    public override ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        TractebelTools.CheckUrl(Args.Str(a, "url"));
        var t = Target(a, env, null);
        var p = new ActionPlan { Tool = "te_download", Class = RiskClass.W, WritesToSynced = t.Folder.Synced, Summary = "download from Tractebel Portal → " + t.Virtual };
        p.Scopes.Add(new Scope(ScopeKind.Folder, t.FullPath));
        p.Scopes.Add(new Scope(ScopeKind.Domain, TractebelTools.Host));
        return p;
    }

    public override async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string url = Args.Str(a, "url");
        TractebelTools.CheckUrl(url);
        var r = await TractebelTools.RunAsync(B, TractebelTools.FetchJs(url, raw: true), ct);
        TractebelTools.ThrowIfError(r);
        if (r.TryGetProperty("status", out var st) && st.GetInt32() >= 400) throw new ToolException("portal_error", $"Tractebel Portal answered HTTP {st.GetInt32()}.");
        string type = TractebelTools.Str(r, "type");
        byte[] bytes = Convert.FromBase64String(TractebelTools.Str(r, "b64"));
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase) && bytes.Length < 200_000)
            throw new ToolException("not_a_file", "The link returned a portal page, not a file. Open it with te_fetch to find the file link.");

        var name = System.Text.RegularExpressions.Regex.Match(TractebelTools.Str(r, "disposition"), "filename\\*?=(?:UTF-8'')?\"?([^\";]+)");
        string? fileName = name.Success ? Uri.UnescapeDataString(name.Groups[1].Value).Trim() : null;
        if (fileName != null) fileName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var t = Target(a, env, fileName);
        bool overwrite = Args.OptBool(a, "overwrite", false);

        if (t.Remote != null)
        {
            var sp = RemoteFolders.Of(env);
            var existing = await sp.IsDirAsync(t.Remote, ct);
            if (existing != null && !overwrite) throw new ToolException("exists", "Target exists; pass overwrite=true to replace it.");
            await sp.UploadAsync(t.Remote, bytes, ct);
        }
        else
        {
            if (File.Exists(t.FullPath) && !overwrite) throw new ToolException("exists", "Target exists; pass overwrite=true to replace it.");
            int step = env.Snapshots.BeginStep(env.RunId);
            env.Snapshots.CaptureFile(env.RunId, step, t.FullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(t.FullPath)!);
            await File.WriteAllBytesAsync(t.FullPath, bytes, ct);
        }
        return new ToolOutput { Content = $"Saved {t.Virtual} ({bytes.Length} bytes, {type}). Read it with fs_read." };
    }
}
