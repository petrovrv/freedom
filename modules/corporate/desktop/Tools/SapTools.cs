// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Corporate.Tools;

// SAP Gateway OData V2 tools; requests run via fetch() in a browser tab on the SAP origin (user cookies apply).
// Host and Base are defined in another part of this partial class (SapSession.cs).
public static partial class SapTools
{
    const string PathPrefix = "/sap/";
    const string ODataPrefix = "/sap/opu/odata/";
    const long DefaultLength = 60000;
    const long MaxLength = 200000;
    const int ExcerptLength = 1500;
    // Writes reach only the timesheet and leave services (review 0.8.8).
    public static readonly string[] WritableServices = { "HCMFAB_TIMESHEET_MAINT_SRV", "HCMFAB_LEAVE_REQUEST_CR_SRV" };

    public static IEnumerable<IDeviceTool> All(IBrowser b) => new IDeviceTool[]
    {
        new SapGet(b),
        new SapChange(b, "sap_write", new[] { "POST" }, "Create data in SAP Fiori OData (timesheet entries with TimeEntryOperation C, leave requests with ActionCode 1) with the user's session; token header handled. Needs the Create box of the SAP connector. Changes go to sap_update, deletions to sap_delete. Real business data: only on the user's explicit request."),
        new SapChange(b, "sap_update", new[] { "PUT", "MERGE", "PATCH", "POST" }, "Change SAP Fiori data in the timesheet or leave services: PUT/MERGE/PATCH, or POST with TimeEntryOperation U / ActionCode 2. Needs the Edit box of the SAP connector."),
        new SapChange(b, "sap_delete", new[] { "DELETE", "POST" }, "Delete SAP Fiori data in the timesheet or leave services: DELETE, or POST with TimeEntryOperation D / ActionCode 3 (withdraw a leave request). Needs the Delete box of the SAP connector (ticking it permits deletions). Only on the user's explicit request."),
    };

    // Service = <SERVICE> segment of /sap/opu/odata/<ns>/<SERVICE>/
    static void CheckWritable(string root)
    {
        var svc = root.TrimEnd('/').Split('/')[^1];
        if (!WritableServices.Contains(svc, StringComparer.OrdinalIgnoreCase))
            throw new ToolException("not_allowed", "Writes are limited to the SAP services " + string.Join(", ", WritableServices) + ".");
    }

    // Write tool -> user right it represents.
    static readonly Dictionary<string, string> ToolRights = new(StringComparer.Ordinal)
        { ["sap_write"] = "Create", ["sap_update"] = "Edit", ["sap_delete"] = "Delete" };
    const System.Text.RegularExpressions.RegexOptions RxOpts = System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
    static readonly System.Text.RegularExpressions.Regex TeoRx = new(@"""TimeEntryOperation""\s*:\s*""([A-Za-z]?)""", RxOpts);
    static readonly System.Text.RegularExpressions.Regex AcRx = new(@"""ActionCode""\s*:\s*""?(\d+)""?", RxOpts);

    // Review 0.8.8: the timesheet service changes and deletes entries with POST (TimeEntryOperation U/D) and the leave
    // service uses ActionCode 1/2/3, so the right is taken from the body, not only from the HTTP method.
    public static void CheckOperation(string tool, string service, string method, string path, string? body)
    {
        if (tool is null || !ToolRights.TryGetValue(tool, out var toolRight))
            throw new ToolException("invalid_args", $"Unknown write tool '{tool}'.");
        if (service is null || !WritableServices.Contains(service, StringComparer.OrdinalIgnoreCase))
            throw new ToolException("not_allowed", "Writes are limited to the SAP services " + string.Join(", ", WritableServices) + ".");

        string m = (method ?? "").Trim().ToUpperInvariant();
        (string Right, string What)? byMethod = m switch
        {
            "POST" => ("Create", "creates data (HTTP POST)"),
            "PUT" or "MERGE" or "PATCH" => ("Edit", $"changes data (HTTP {m})"),
            "DELETE" => ("Delete", "deletes data (HTTP DELETE)"),
            _ => null
        };
        if (byMethod is null)
            throw new ToolException("invalid_args", $"HTTP method '{method}' is not allowed for write tools.");

        string rawPath = (path ?? "").Split('?')[0];
        // Review 0.8.8: $batch is not supported for writes (inner requests and inner failures cannot be checked here).
        if (rawPath.EndsWith("/$batch", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("not_allowed", "$batch is not supported for writes; send one request per entity.");

        var needs = new List<(string Right, string What)>();
        string text = body ?? "";
        // Review 0.8.8: markers are read from the raw text, so escapes that could hide them are refused.
        if (text.Contains("\\u", StringComparison.OrdinalIgnoreCase) || text.Contains("\\x", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("not_allowed", "Write bodies with escaped characters (\\u, \\x) are not supported; send plain UTF-8 JSON.");
        foreach (System.Text.RegularExpressions.Match x in TeoRx.Matches(text))
        {
            string v = x.Groups[1].Value.ToUpperInvariant();
            needs.Add(v switch
            {
                "" => ("Create", "creates timesheet entries (TimeEntryOperation empty)"),
                "C" => ("Create", "creates timesheet entries (TimeEntryOperation C)"),
                "U" => ("Edit", "changes timesheet entries (TimeEntryOperation U)"),
                "D" => ("Delete", "deletes timesheet entries (TimeEntryOperation D)"),
                _ => throw new ToolException("not_allowed", $"Unsupported TimeEntryOperation '{v}'.")
            });
        }
        foreach (System.Text.RegularExpressions.Match x in AcRx.Matches(text))
        {
            string v = x.Groups[1].Value;
            needs.Add(v.TrimStart('0') switch
            {
                "1" => ("Create", "creates a leave request (ActionCode 1)"),
                "2" => ("Edit", "changes a leave request (ActionCode 2)"),
                "3" => ("Delete", "withdraws a leave request (ActionCode 3)"),
                _ => throw new ToolException("not_allowed", $"Unsupported ActionCode '{v}'.")
            });
        }
        // Inner requests of a $batch body (DELETE/MERGE lines, method override headers) count too (review 0.8.8).
        foreach (var n in BatchNeeds(text))
        {
            if (n.Right == "Unknown") throw new ToolException("not_allowed", "The request " + n.What + "; not supported.");
            needs.Add(n);
        }
        // The method decides when the body has no markers; non-POST verbs always count.
        if (needs.Count == 0 || m != "POST") needs.Add(byMethod.Value);

        if (needs.Select(n => n.Right).Distinct().Count() > 1)
            throw new ToolException("not_allowed", "The request mixes operations of different rights; send them separately.");

        var (right, what) = needs[0];
        if (right != toolRight)
        {
            string correctTool = ToolRights.First(kv => kv.Value == right).Key;
            throw new ToolException("not_allowed", "This request " + what + "; use " + correctTool + " (needs the " + right + " box).");
        }
    }

    // Scans an OData V2 $batch body (including nested changesets): one (Right, What) per inner request or method
    // override header that changes data. Inner POST and GET yield nothing (POST bodies are classified by their markers).
    public static IEnumerable<(string Right, string What)> BatchNeeds(string? body)
    {
        if (string.IsNullOrEmpty(body)) return Array.Empty<(string, string)>();
        const System.Text.RegularExpressions.RegexOptions opts = System.Text.RegularExpressions.RegexOptions.Multiline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var lines = System.Text.RegularExpressions.Regex.Matches(body, @"^\s*(GET|POST|PUT|MERGE|PATCH|DELETE)\s+\S+\s+HTTP/\d\.\d\s*$", opts)
            .Select(m => (m.Index, Value: m.Groups[1].Value, IsOverride: false));
        var headers = System.Text.RegularExpressions.Regex.Matches(body, @"(?<![\w-])X-HTTP-Method(?:-Override)?[ \t]*:[ \t]*([^\r\n]*)", opts)
            .Select(m => (m.Index, Value: m.Groups[1].Value.Trim(), IsOverride: true));

        static (string Right, string What)? Classify(string value, bool isOverride) =>
            value.ToUpperInvariant() switch
            {
                "" or "GET" or "POST" => null,
                "DELETE" => ("Delete", "deletes data (DELETE in a batch)"),
                var u when u is "PUT" or "MERGE" or "PATCH" => ("Edit", $"changes data ({u} in a batch)"),
                _ when isOverride => ("Unknown", $"uses method override {value}"),
                _ => null
            };

        return lines.Concat(headers).OrderBy(x => x.Index)
            .Select(x => Classify(x.Value, x.IsOverride)).Where(t => t.HasValue).Select(t => t!.Value).ToList();
    }

    // Non-2xx (incl. a missing status = 0) is a tool error with SAP's message.
    static void CheckHttp(JsonElement r)
    {
        var s = Status(r);
        if (s < 200 || s >= 300) throw new ToolException("sap_http_" + s, "SAP answered HTTP " + s + ": " + Excerpt(Str(r, "text")));
    }

    // SAP error text from {"error":{"message":{"value":...}}}, else the first 1500 chars.
    static string Excerpt(string? text)
    {
        var t = text ?? "";
        try
        {
            using var d = JsonDocument.Parse(t);
            var root = d.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch (JsonException) { }
        return t.Length <= ExcerptLength ? t : t[..ExcerptLength];
    }

    // Validates an OData path and returns the absolute URL on Base. The path part is checked raw and percent-decoded
    // (review 0.8.8: an encoded ".." or "/" would let the browser normalise the URL into another service).
    public static string CheckPath(string path)
    {
        if (path is null || !path.StartsWith(PathPrefix, StringComparison.Ordinal)) throw InvalidPath();
        if (path.Contains("://") || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw InvalidPath();
        int q = path.IndexOf('?');
        string raw = q >= 0 ? path[..q] : path;
        static bool HasTraversal(string x) => x.Contains("..") || x.Contains('\\') || x.Contains("://");
        if (HasTraversal(raw)) throw InvalidPath();
        string decoded = Uri.UnescapeDataString(raw);
        if (HasTraversal(decoded) || decoded.Contains('%') || decoded.Any(char.IsControl)
            || decoded.Count(c => c == '/') != raw.Count(c => c == '/'))
            throw InvalidPath();
        return Base + path;
    }

    // "/sap/opu/odata/<ns>/<SERVICE>/..." -> "/sap/opu/odata/<ns>/<SERVICE>/"
    public static string ServiceRoot(string path)
    {
        var p = path ?? "";
        var cut = p.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) p = p[..cut];
        if (!p.StartsWith(ODataPrefix, StringComparison.Ordinal)) throw InvalidPath();
        var seg = p[ODataPrefix.Length..].Split('/');
        if (seg.Length < 2 || seg[0].Length == 0 || seg[1].Length == 0) throw InvalidPath();
        return ODataPrefix + seg[0] + "/" + seg[1] + "/";
    }

    static ToolException InvalidPath() =>
        new("invalid_args", "path must be an OData path starting with /sap/ on " + Host + ".");

    // Runs JS on the SAP tab; retries once after re-preparing the tab if the session expired.
    static async Task<JsonElement> RunAsync(IBrowser b, string js, CancellationToken ct)
    {
        await EnsureOnSapAsync(b, ct);
        var r = await b.EvaluateAsync(js, ct);
        if (NeedsSession(r))
        {
            await EnsureOnSapAsync(b, ct, force: true);
            r = await b.EvaluateAsync(js, ct);
            if (NeedsSession(r))
                throw new ToolException("sign_in_needed",
                    "SAP Fiori needs the user's sign-in in the CO browser (or " + Host + " is unreachable: employer network needed); ask the user to sign in there, then retry.");
        }
        ThrowIfError(r);
        CheckHttp(r);
        return r;
    }

    static void ThrowIfError(JsonElement r)
    {
        if (r.ValueKind == JsonValueKind.Object
            && r.TryGetProperty("error", out var e)
            && e.ValueKind == JsonValueKind.String)
            throw new ToolException("sap_error", "SAP: " + e.GetString());
    }

    static string? Str(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int Status(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty("status", out var v) && v.TryGetInt32(out var s) ? s : 0;

    static ToolOutput Output(object obj) => new() { Content = FsTools.Json(obj), Untrusted = true };

    static string J(object? v) => JsonSerializer.Serialize(v);

    const string CatchJs = "catch (e) { return { error: 'fetch_failed ' + (e && e.message ? e.message : String(e)) }; }";

    // ---- sap_get ----
    sealed class SapGet : IDeviceTool
    {
        readonly IBrowser _b;
        public SapGet(IBrowser b) => _b = b;

        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = "sap_get",
            Class = RiskClass.R,
            Untrusted = UntrustedMode.Always,
            Description = "Read SAP Fiori OData (HR: timesheets, leave) with the user's session: GET a /sap/opu/odata/... path (add $format=json); page long answers with offset/length. Library tool sap-hr documents the services.",
            InputSchema = Schema.Obj(
                ("path", "string", "OData path starting with /sap/, query allowed", true),
                ("offset", "integer", "Start character of the answer text (default 0)", false),
                ("length", "integer", "Characters to return (default 60000, max 200000)", false))
        };

        static (string Path, string Url, long Offset, long Length) Read(JsonElement a)
        {
            var path = Args.Str(a, "path");
            var url = CheckPath(path);
            var offset = Args.OptLong(a, "offset", 0);
            var length = Args.OptLong(a, "length", DefaultLength);
            if (offset < 0) throw new ToolException("invalid_args", "offset must be >= 0.");
            if (length < 1 || length > MaxLength) throw new ToolException("invalid_args", "length must be 1.." + MaxLength + ".");
            return (path, url, offset, length);
        }

        public ActionPlan Plan(JsonElement a, ToolEnv env)
        {
            var x = Read(a);
            var p = new ActionPlan { Tool = "sap_get", Class = RiskClass.R, Summary = "GET " + x.Path };
            p.Scopes.Add(new Scope(ScopeKind.Domain, Host));
            return p;
        }

        public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
        {
            var x = Read(a);
            var js =
                "(async () => { try { " +
                "const r = await fetch(" + J(x.Url) + ", { credentials: 'include', headers: { 'Accept': 'application/json' } }); " +
                "const text = await r.text(); " +
                "return { status: r.status, url: r.url, type: r.headers.get('content-type'), text }; } " +
                CatchJs + " })()";
            var r = await RunAsync(_b, js, ct);
            var text = Str(r, "text") ?? "";
            var start = (int)Math.Min(x.Offset, text.Length);
            var len = (int)Math.Min(x.Length, text.Length - start);
            return Output(new
            {
                status = Status(r),
                type = Str(r, "type"),
                total = text.Length,
                offset = start,
                text = text.Substring(start, len)
            });
        }
    }

    // ---- sap_write (POST), sap_update (PUT/MERGE/PATCH), sap_delete (DELETE, no body) ----
    sealed class SapChange : IDeviceTool
    {
        readonly IBrowser _b;
        readonly string[] _methods;
        readonly bool _isDelete;
        public ToolDescriptor Descriptor { get; }

        public SapChange(IBrowser b, string name, string[] methods, string description)
        {
            _b = b; _methods = methods; _isDelete = name == "sap_delete";
            var path = ("path", "string", "OData path /sap/opu/odata/<ns>/<SERVICE>/... in " + string.Join(" or ", WritableServices), true);
            Descriptor = new()
            {
                Name = name,
                Class = _isDelete ? RiskClass.D : RiskClass.W,
                Untrusted = UntrustedMode.Never,
                Description = description,
                InputSchema = Schema.Obj(
                    ("method", "string", string.Join(", ", methods) + (name == "sap_update" ? "" : " (default " + methods[0] + "; POST when a body is given for sap_delete)"), name == "sap_update"),
                    path,
                    ("body", "string", "Request body (usually JSON)", false),
                    ("content_type", "string", "Body content type (default application/json)", false)),
            };
        }

        record Req(string Method, string Path, string Url, string Root, string? Body, string ContentType);

        Req Read(JsonElement a)
        {
            var m = Args.OptStr(a, "method")?.Trim().ToUpperInvariant();
            var body = Args.OptStr(a, "body");
            if (string.IsNullOrEmpty(m))
            {
                if (Descriptor.Name == "sap_update") throw new ToolException("invalid_args", "method is required: one of " + string.Join(", ", _methods) + ".");
                m = _isDelete && body is not null ? "POST" : _methods[0];
            }
            if (!_methods.Contains(m)) throw new ToolException("invalid_args", Descriptor.Name + " supports method " + string.Join(", ", _methods) + " only.");
            var path = Args.Str(a, "path");
            var url = CheckPath(path);
            var root = ServiceRoot(path);
            CheckWritable(root);
            if (m == "DELETE" && body is not null) throw new ToolException("invalid_args", "DELETE takes no body.");
            CheckOperation(Descriptor.Name, root.TrimEnd('/').Split('/')[^1], m, path, body);
            var ctype = Args.OptStr(a, "content_type");
            if (string.IsNullOrWhiteSpace(ctype)) ctype = "application/json";
            if (ctype.Length > 200 || ctype.Any(char.IsControl)) throw new ToolException("invalid_args", "content_type is invalid.");
            // Only JSON: the bodies CheckOperation can read.
            if (body is not null && !ctype.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("invalid_args", "content_type must be application/json.");
            return new(m, path, url, Base + root, body, ctype);
        }

        public ActionPlan Plan(JsonElement a, ToolEnv env)
        {
            var q = Read(a);
            // D: the Delete box of the SAP connector (checked by its gate) is the permission, as for folders; this also
            // lets it run under the managed-device profile.
            var p = new ActionPlan { Tool = Descriptor.Name, Class = _isDelete ? RiskClass.D : RiskClass.W, Summary = q.Method + " " + q.Path, PermittedByUser = _isDelete };
            p.Scopes.Add(new Scope(ScopeKind.Domain, Host));
            return p;
        }

        public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
        {
            var q = Read(a);
            var js =
                "(async () => { try { " +
                "const t = await fetch(" + J(q.Root) + ", { credentials: 'include', headers: { 'X-CSRF-Token': 'Fetch', 'Accept': 'application/json' } }); " +
                "const token = t.headers.get('x-csrf-token'); " +
                // Without a token keep the answer: a redirect to the sign-in page must start the sign-in flow (NeedsSession).
                "if (!token || token.toLowerCase() === 'required') { const tt = await t.text(); " +
                "return { error: 'no_csrf_token', status: t.status, url: t.url, type: t.headers.get('content-type'), text: tt.slice(0, 4000) }; } " +
                "const body = " + J(q.Body) + "; " +
                "const headers = { 'X-CSRF-Token': token, 'Accept': 'application/json' }; " +
                "if (body !== null) headers['Content-Type'] = " + J(q.ContentType) + "; " +
                // Review 0.8.8: a failure after the request left must not be retried (it may have been committed).
                "let r; try { r = await fetch(" + J(q.Url) + ", { method: " + J(q.Method) + ", credentials: 'include', headers, body: body ?? undefined }); } " +
                "catch (e) { return { error: 'outcome_unknown: the request was sent but no answer came back (' + (e && e.message ? e.message : String(e)) + '); check in SAP before repeating it' }; } " +
                "let text = ''; try { text = await r.text(); } catch (e) { text = ''; } " +
                "return { status: r.status, url: r.url, type: r.headers.get('content-type'), text: text.slice(0, " + MaxLength + "), message: r.headers.get('sap-message') }; } " +
                CatchJs + " })()";
            var r = await RunAsync(_b, js, ct);
            return Output(new
            {
                status = Status(r),
                url = Str(r, "url"),
                type = Str(r, "type"),
                message = Str(r, "message"),
                text = Str(r, "text") ?? ""
            });
        }
    }
}
