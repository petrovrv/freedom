// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Core.Tools;

// TZ §7.1. Paths are "<folderId>/<relative path>"; an empty path lists the connected folders.
public static class FsTools
{
    public static IEnumerable<IDeviceTool> All() => new IDeviceTool[]
    {
        new FsList(), new FsStat(), new FsRead(), new FsSearch(),
        new FsWrite(), new FsEdit(), new FsMkdir(), new FsMove(), new FsCopy(), new FsDelete(),
    };

    internal static ActionPlan ReadPlan(string tool, params ResolvedPath[] paths)
    {
        foreach (var r in paths) Require(r, Rights.Read);
        var p = new ActionPlan { Tool = tool, Class = RiskClass.R, Summary = string.Join(", ", paths.Select(x => x.Virtual)) };
        foreach (var r in paths) p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    // The boxes ticked for the folder (or the subfolder rule covering the path) decide; a ticked box is the permission.
    internal static void Require(ResolvedPath r, Rights need)
    {
        Rights have = r.Rights;
        if ((have & need) == need) return;
        string where = r.Folder.RuleFor(r.Relative) is { } rule ? r.Folder.Id + "/" + rule.Path : r.Folder.Id;
        string missing = RightsText.List(need & ~have);
        throw new ToolException(have == Rights.Read ? "read_only" : "not_permitted",
            $"'{where}' does not allow: {missing} (allowed: {RightsText.List(have)}). The user can tick it in CO Desktop → Folders.");
    }

    // Overwriting needs Edit, a new file Create.
    internal static Rights WriteNeed(ResolvedPath r, bool overwrite) =>
        r.Remote != null ? (overwrite ? Rights.Edit : Rights.Create) : File.Exists(r.FullPath) ? Rights.Edit : Rights.Create;

    internal static bool IsUntrusted(UntrustedMode mode, ResolvedPath r) =>
        mode == UntrustedMode.Always || (mode == UntrustedMode.OutsideProject && !r.Folder.Project);

    internal static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
}

internal sealed class FsList : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_list", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "List a directory. Path '<folderId>/<relative>'; empty path lists connected folders.",
        InputSchema = Schema.Obj(("path", "string", "Directory path; empty for the list of connected folders", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        string p = Args.OptStr(a, "path") ?? "";
        if (p.Trim('/').Length == 0) return new ActionPlan { Tool = "fs_list", Class = RiskClass.R, Summary = "connected folders" };
        return FsTools.ReadPlan("fs_list", env.Folders.Resolve(p));
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string p = Args.OptStr(a, "path") ?? "";
        if (p.Trim('/').Length == 0)
        {
            var list = env.Folders.All.Select(f => new
            {
                id = f.Id, rights = RightsText.List(f.Granted), synced = f.Synced, project = f.Project,
                rules = f.Rules.Select(r => new { path = f.Id + "/" + r.Path, rights = RightsText.List(r.Rights) }),
            });
            return Task.FromResult(new ToolOutput { Content = FsTools.Json(list) });
        }
        var r = env.Folders.Resolve(p);
        if (r.Remote != null) return RemoteFolders.Of(env).ListAsync(r, env, ct);
        if (!Directory.Exists(r.FullPath)) throw new ToolException("not_found", "Directory not found.");
        const int max = 2000;
        var entries = new DirectoryInfo(r.FullPath).EnumerateFileSystemInfos()
            .Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max + 1)
            .Select(e => new
            {
                name = e.Name,
                type = e is DirectoryInfo ? "dir" : "file",
                size = (e as FileInfo)?.Length,
                modifiedUtc = e.LastWriteTimeUtc.ToString("u"),
            }).ToList();
        bool truncated = entries.Count > max;
        return Task.FromResult(new ToolOutput { Content = FsTools.Json(new { path = r.Virtual, entries = entries.Take(max), truncated }) });
    }
}

internal sealed class FsStat : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_stat", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Size, timestamps and type of a file or directory.",
        InputSchema = Schema.Obj(("path", "string", "Path '<folderId>/<relative>'", true)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env) => FsTools.ReadPlan("fs_stat", env.Folders.Resolve(Args.Str(a, "path")));

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        if (r.Remote != null) return RemoteFolders.Of(env).StatAsync(r, env, ct);
        FileSystemInfo fi = Directory.Exists(r.FullPath) ? new DirectoryInfo(r.FullPath) : new FileInfo(r.FullPath);
        if (!fi.Exists) throw new ToolException("not_found", "Not found.");
        return Task.FromResult(new ToolOutput
        {
            Content = FsTools.Json(new
            {
                path = r.Virtual,
                type = fi is DirectoryInfo ? "dir" : "file",
                size = (fi as FileInfo)?.Length,
                createdUtc = fi.CreationTimeUtc.ToString("u"),
                modifiedUtc = fi.LastWriteTimeUtc.ToString("u"),
                readOnly = fi.Attributes.HasFlag(FileAttributes.ReadOnly),
                synced = r.Folder.Synced,
            }),
        });
    }
}

internal sealed class FsRead : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_read", Class = RiskClass.R, Untrusted = UntrustedMode.OutsideProject,
        Description = "Read text of a file (plain text, PDF, DOCX, XLSX, PPTX). offset/length are in characters of the extracted text.",
        InputSchema = Schema.Obj(
            ("path", "string", "Path '<folderId>/<relative>'", true),
            ("offset", "integer", "Start character, default 0", false),
            ("length", "integer", "Characters to return, default 60000", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env) => FsTools.ReadPlan("fs_read", env.Folders.Resolve(Args.Str(a, "path")));

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        string text = r.Remote != null ? await RemoteFolders.Of(env).ReadTextAsync(r, env, ct) : TextExtractor.Extract(r.FullPath);
        long offset = Math.Clamp(Args.OptLong(a, "offset", 0), 0, text.Length);
        long length = Math.Clamp(Args.OptLong(a, "length", 60_000), 0, text.Length - offset);
        string slice = text.Substring((int)offset, (int)length);
        string header = $"[{r.Virtual}: characters {offset}-{offset + length} of {text.Length}]\n";
        return new ToolOutput { Content = header + slice, Untrusted = FsTools.IsUntrusted(Descriptor.Untrusted, r) };
    }
}

internal sealed class FsSearch : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_search", Class = RiskClass.R, Untrusted = UntrustedMode.OutsideProject,
        Description = "Search file names and text content under a directory. Returns matching lines.",
        InputSchema = Schema.Obj(
            ("path", "string", "Directory '<folderId>/<relative>'", true),
            ("query", "string", "Case-insensitive text; prefix 're:' for a regular expression", true),
            ("glob", "string", "File name filter, e.g. *.docx", false),
            ("maxResults", "integer", "Default 200", false)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env) => FsTools.ReadPlan("fs_search", env.Folders.Resolve(Args.Str(a, "path")));

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        string q = Args.Str(a, "query");
        string glob = Args.OptStr(a, "glob") ?? "*";
        int max = (int)Math.Clamp(Args.OptLong(a, "maxResults", 200), 1, 2000);
        if (r.Remote != null) return RemoteFolders.Of(env).SearchAsync(r, env, q, glob, max, FsTools.IsUntrusted(Descriptor.Untrusted, r), ct);
        Regex rx = q.StartsWith("re:")
            ? new Regex(q[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
            : new Regex(Regex.Escape(q), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var hits = new List<string>();
        int filesScanned = 0;
        var excludes = new HashSet<string>(FolderRegistry.DefaultExcludes.Concat(r.Folder.ExcludeNames), StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (string file in Directory.EnumerateFiles(r.FullPath, glob, opts))
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(r.Folder.Path, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(excludes.Contains)) continue;
            if (!r.Folder.RightsAt(rel.Replace(Path.DirectorySeparatorChar, '/')).HasFlag(Rights.Read)) continue;
            string v = env.Folders.ToVirtual(r.Folder, file);
            if (rx.IsMatch(Path.GetFileName(file))) hits.Add($"{v}: (file name)");
            if (++filesScanned > 20_000) { hits.Add("(stopped after 20000 files)"); break; }
            string text;
            try { text = TextExtractor.Extract(file); }
            catch (ToolException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (Exception) { continue; } // malformed documents are skipped, never fatal
            int lineNo = 0;
            foreach (string line in text.Split('\n'))
            {
                lineNo++;
                if (!rx.IsMatch(line)) continue;
                string t = line.Trim();
                hits.Add($"{v}:{lineNo}: {(t.Length > 300 ? t[..300] + "…" : t)}");
                if (hits.Count >= max) break;
            }
            if (hits.Count >= max) break;
        }
        return Task.FromResult(new ToolOutput
        {
            Content = hits.Count == 0 ? "No matches." : string.Join('\n', hits),
            Untrusted = FsTools.IsUntrusted(Descriptor.Untrusted, r),
        });
    }
}

internal sealed class FsWrite : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_write", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Create or overwrite a file: UTF-8 text in content, or any file (PDF, DOCX, images) copied from the " +
            "AI Workbench host with sourcePath. A snapshot is taken first.",
        InputSchema = Schema.Obj(
            ("path", "string", "Path '<folderId>/<relative>'", true),
            ("content", "string", "Full text content (UTF-8). Give content or sourcePath.", false),
            ("sourcePath", "string", "Absolute path of a file in this project's folder on the AI Workbench host (e.g. the chat's " +
                "scratch); Bridge sends its bytes, up to 7 MB. Use it for PDF and other binary files.", false),
            ("contentBase64", "string", "Binary content as base64 (filled by Bridge from sourcePath; only for tiny files otherwise).", false),
            ("overwrite", "boolean", "Allow replacing an existing file, default false", false)),
    };

    private const int MaxBinaryBytes = 20 * 1024 * 1024;

    // 0.9.8: binary content (contentBase64, from Bridge's sourcePath) or null for a text write.
    internal static byte[]? Binary(JsonElement a)
    {
        string? b64 = Args.OptStr(a, "contentBase64");
        if (b64 == null)
        {
            if (Args.OptStr(a, "sourcePath") != null)
                throw new ToolException("source_not_sent", "sourcePath is read by Bridge 1.9.2+; this Bridge did not send the file.");
            return null;
        }
        if (b64.Length > MaxBinaryBytes / 3 * 4 + 4)
            throw new ToolException("too_large", $"Binary content over {MaxBinaryBytes} bytes.");
        try { return Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ToolException("invalid_args", "contentBase64 is not valid base64."); }
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(r, FsTools.WriteNeed(r, Args.OptBool(a, "overwrite", false)));
        byte[]? bin = Binary(a);
        if (bin != null)
        {
            if (r.Remote != null) throw new ToolException("not_supported", "Binary files go only to local connected folders.");
            bool existsBin = File.Exists(r.FullPath);
            if (existsBin && !Args.OptBool(a, "overwrite", false))
                throw new ToolException("exists", "File exists; pass overwrite=true to replace it.");
            var pb = new ActionPlan
            {
                Tool = "fs_write", Class = RiskClass.W, WritesToSynced = r.Folder.Synced,
                Summary = $"{(existsBin ? "overwrite" : "create")} {r.Virtual} ({bin.Length} bytes, binary)",
            };
            pb.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
            return pb;
        }
        string content = Args.Str(a, "content");
        if (r.Remote != null) return RemoteFolders.WritePlan(env, "fs_write", $"write {r.Virtual} ({Encoding.UTF8.GetByteCount(content)} bytes)", RiskClass.W, r);
        bool exists = File.Exists(r.FullPath);
        if (exists && !Args.OptBool(a, "overwrite", false))
            throw new ToolException("exists", "File exists; pass overwrite=true to replace it.");
        string before = exists ? SafeText(r.FullPath) : "";
        var p = new ActionPlan
        {
            Tool = "fs_write", Class = RiskClass.W, WritesToSynced = r.Folder.Synced,
            Summary = $"{(exists ? "overwrite" : "create")} {r.Virtual} ({Encoding.UTF8.GetByteCount(content)} bytes)",
            Diff = LineDiff.Unified(before, content, r.Virtual),
        };
        p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(r, FsTools.WriteNeed(r, Args.OptBool(a, "overwrite", false)));
        byte[]? bin = Binary(a);
        if (r.Remote != null && bin == null) return RemoteFolders.Of(env).WriteAsync(r, env, Args.Str(a, "content"), Args.OptBool(a, "overwrite", false), ct);
        if (r.Remote != null) throw new ToolException("not_supported", "Binary files go only to local connected folders.");
        string? text = bin == null ? Args.Str(a, "content") : null;
        int step = env.Snapshots.BeginStep(env.RunId);
        env.Snapshots.CaptureFile(env.RunId, step, r.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(r.FullPath)!);
        if (bin != null) File.WriteAllBytes(r.FullPath, bin);
        else File.WriteAllText(r.FullPath, text, new UTF8Encoding(false));
        return Task.FromResult(new ToolOutput { Content = $"Wrote {r.Virtual} ({(bin != null ? bin.Length + " bytes" : "text")}, snapshot step {step})." });
    }

    internal static string SafeText(string path)
    {
        try { return TextExtractor.Extract(path); } catch (ToolException) { return "(binary or unreadable)"; }
    }
}

internal sealed class FsEdit : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_edit", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Replace an exact text fragment in a text file. oldText must occur exactly once unless replaceAll=true.",
        InputSchema = Schema.Obj(
            ("path", "string", "Path '<folderId>/<relative>'", true),
            ("oldText", "string", "Exact text to replace", true),
            ("newText", "string", "Replacement", true),
            ("replaceAll", "boolean", "Replace every occurrence", false)),
    };

    private static (ResolvedPath r, string before, string after) Compute(JsonElement a, ToolEnv env)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(r, Rights.Edit);
        if (!File.Exists(r.FullPath)) throw new ToolException("not_found", "File not found.");
        string ext = Path.GetExtension(r.FullPath).ToLowerInvariant();
        if (ext is ".docx" or ".xlsx" or ".pptx" or ".pdf")
            throw new ToolException("unsupported", "fs_edit works on text files; Office documents use word_/excel_/powerpoint_ tools.");
        string before = TextExtractor.Decode(File.ReadAllBytes(r.FullPath));
        return (r, before, Apply(before, a));
    }

    private static string Apply(string before, JsonElement a)
    {
        string oldText = Args.Str(a, "oldText"), newText = Args.Str(a, "newText");
        if (oldText.Length == 0) throw new ToolException("invalid_args", "oldText is empty.");
        int count = CountOf(before, oldText);
        bool all = Args.OptBool(a, "replaceAll", false);
        if (count == 0) throw new ToolException("no_match", "oldText not found.");
        if (count > 1 && !all) throw new ToolException("ambiguous", $"oldText occurs {count} times; widen it or pass replaceAll=true.");
        return all ? before.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(before, oldText, newText);
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var remote = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(remote, Rights.Edit);
        if (remote.Remote != null) return RemoteFolders.WritePlan(env, "fs_edit", $"edit {remote.Virtual}", RiskClass.W, remote);
        var (r, before, after) = Compute(a, env);
        var p = new ActionPlan
        {
            Tool = "fs_edit", Class = RiskClass.W, WritesToSynced = r.Folder.Synced,
            Summary = $"edit {r.Virtual}", Diff = LineDiff.Unified(before, after, r.Virtual),
        };
        p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var remote = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(remote, Rights.Edit);
        if (remote.Remote != null) return RemoteFolders.Of(env).EditAsync(remote, env, text => Apply(text, a), ct);
        var (r, _, after) = Compute(a, env);
        int step = env.Snapshots.BeginStep(env.RunId);
        env.Snapshots.CaptureFile(env.RunId, step, r.FullPath);
        bool bom = File.ReadAllBytes(r.FullPath) is [0xEF, 0xBB, 0xBF, ..];
        File.WriteAllText(r.FullPath, after, new UTF8Encoding(bom));
        return Task.FromResult(new ToolOutput { Content = $"Edited {r.Virtual} (snapshot step {step})." });
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0;
        for (int i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string ReplaceFirst(string s, string o, string n)
    {
        int i = s.IndexOf(o, StringComparison.Ordinal);
        return s[..i] + n + s[(i + o.Length)..];
    }
}

internal sealed class FsMkdir : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_mkdir", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Create a directory (with parents).",
        InputSchema = Schema.Obj(("path", "string", "Path '<folderId>/<relative>'", true)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(r, Rights.Create);
        var p = new ActionPlan { Tool = "fs_mkdir", Class = RiskClass.W, WritesToSynced = r.Folder.Synced, Summary = $"mkdir {r.Virtual}" };
        p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "path"));
        FsTools.Require(r, Rights.Create);
        if (r.Remote != null) return RemoteFolders.Of(env).MkdirAsync(r, env, ct);
        int step = env.Snapshots.BeginStep(env.RunId);
        // Record missing ancestors top-down; undo walks the journal backwards, removing the deepest first.
        var missing = new Stack<string>();
        for (string? d = r.FullPath; d != null && !Directory.Exists(d) && PathUtil.IsUnder(d, r.Folder.Path); d = Path.GetDirectoryName(d))
            missing.Push(d);
        foreach (string d in missing) env.Snapshots.CaptureDir(env.RunId, step, d);
        Directory.CreateDirectory(r.FullPath);
        return Task.FromResult(new ToolOutput { Content = $"Created {r.Virtual}." });
    }
}

internal sealed class FsMove : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_move", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Move or rename a file. Never overwrites an existing target.",
        InputSchema = Schema.Obj(
            ("from", "string", "Source file '<folderId>/<relative>'", true),
            ("to", "string", "Target file '<folderId>/<relative>'", true)),
    };

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var (rs, rt) = (env.Folders.Resolve(Args.Str(a, "from")), env.Folders.Resolve(Args.Str(a, "to")));
        if (rs.Remote != null || rt.Remote != null)
        {
            FsTools.Require(rs, Rights.Edit);
            FsTools.Require(rt, Rights.Create);
            return RemoteFolders.WritePlan(env, "fs_move", $"move {rs.Virtual} -> {rt.Virtual}", RiskClass.W, rs, rt);
        }
        var (s, t) = Resolve(a, env);
        var p = new ActionPlan
        {
            Tool = "fs_move", Class = RiskClass.W, WritesToSynced = s.Folder.Synced || t.Folder.Synced,
            Summary = $"move {s.Virtual} → {t.Virtual}",
        };
        p.Scopes.Add(new Scope(ScopeKind.Folder, s.FullPath));
        p.Scopes.Add(new Scope(ScopeKind.Folder, t.FullPath));
        return p;
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var (rs, rt) = (env.Folders.Resolve(Args.Str(a, "from")), env.Folders.Resolve(Args.Str(a, "to")));
        if (rs.Remote != null || rt.Remote != null)
        {
            FsTools.Require(rs, Rights.Edit);
            FsTools.Require(rt, Rights.Create);
            return RemoteFolders.Of(env).MoveAsync(rs, rt, env, ct);
        }
        var (s, t) = Resolve(a, env);
        int step = env.Snapshots.BeginStep(env.RunId);
        env.Snapshots.CaptureFile(env.RunId, step, s.FullPath);
        env.Snapshots.CaptureFile(env.RunId, step, t.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(t.FullPath)!);
        File.Move(s.FullPath, t.FullPath, false);
        return Task.FromResult(new ToolOutput { Content = $"Moved {s.Virtual} → {t.Virtual} (snapshot step {step})." });
    }

    private static (ResolvedPath, ResolvedPath) Resolve(JsonElement a, ToolEnv env)
    {
        var s = env.Folders.Resolve(Args.Str(a, "from"));
        var t = env.Folders.Resolve(Args.Str(a, "to"));
        FsTools.Require(s, Rights.Edit);
        FsTools.Require(t, Rights.Create);
        if (!File.Exists(s.FullPath)) throw new ToolException("not_found", "Source file not found (directories are not moved).");
        if (File.Exists(t.FullPath) || Directory.Exists(t.FullPath)) throw new ToolException("exists", "Target exists.");
        return (s, t);
    }
}

// Copies a file of any type (docx, xlsx, pdf, images) between connected places: local or synced folders and
// folders connected by link (document libraries of a site), in any direction. Source needs Read, target Create (Edit to replace).
internal sealed class FsCopy : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_copy", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Copy a file of any type (Word, Excel, PDF, images) between connected folders, including to and from " +
            "folders connected by link (document libraries of a site): upload a local file there or download one. overwrite=true " +
            "replaces an existing target (the site keeps the previous version in its version history; local: snapshot).",
        InputSchema = Schema.Obj(
            ("from", "string", "Source file '<folderId>/<relative>'", true),
            ("to", "string", "Target file '<folderId>/<relative>' (a name ending with '/' keeps the source name)", true),
            ("overwrite", "boolean", "Replace an existing target, default false", false)),
    };

    private static (ResolvedPath s, ResolvedPath t, bool overwrite) Resolve(JsonElement a, ToolEnv env)
    {
        var s = env.Folders.Resolve(Args.Str(a, "from"));
        string to = Args.Str(a, "to");
        if (to.EndsWith('/')) to += s.Virtual.Split('/').Last();
        var t = env.Folders.Resolve(to);
        bool overwrite = Args.OptBool(a, "overwrite", false);
        FsTools.Require(s, Rights.Read);
        FsTools.Require(t, FsTools.WriteNeed(t, overwrite));
        // Same rule as downloads by link: content from outside the project does not enter a trusted project folder.
        if (t.Folder.Project && !s.Folder.Project && s.Folder.Id != t.Folder.Id)
            throw new ToolException("not_allowed", $"'{t.Folder.Id}' is a project folder (trusted content); copy files from outside the project to a non-project folder.");
        return (s, t, overwrite);
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var (s, t, overwrite) = Resolve(a, env);
        var p = new ActionPlan
        {
            Tool = "fs_copy", Class = RiskClass.W, WritesToSynced = t.Folder.Synced,
            Summary = $"copy {s.Virtual} → {t.Virtual}{(overwrite ? " (replace)" : "")}",
        };
        p.Scopes.Add(new Scope(ScopeKind.Folder, s.FullPath));
        p.Scopes.Add(new Scope(ScopeKind.Folder, t.FullPath));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var (s, t, overwrite) = Resolve(a, env);
        var sp = s.Remote != null || t.Remote != null ? RemoteFolders.Of(env) : null;

        byte[] bytes;
        if (s.Remote != null) bytes = await sp!.DownloadAsync(s.Remote, ct);
        else
        {
            var fi = new FileInfo(s.FullPath);
            if (!fi.Exists) throw new ToolException("not_found", "Source file not found (directories are not copied).");
            if (fi.Length > TextExtractor.MaxSourceBytes) throw new ToolException("too_large", $"File is {fi.Length} bytes; limit {TextExtractor.MaxSourceBytes}.");
            bytes = await File.ReadAllBytesAsync(s.FullPath, ct);
        }

        if (t.Remote != null)
        {
            bool? dir = await sp!.IsDirAsync(t.Remote, ct);
            if (dir == true) throw new ToolException("exists", "A folder has this name.");
            if (dir != null && !overwrite) throw new ToolException("exists", "Target exists; pass overwrite=true to replace it.");
            await sp.UploadAsync(t.Remote, bytes, ct);
            return new ToolOutput { Content = $"Copied {s.Virtual} → {t.Virtual} on {sp.Title} ({bytes.Length} bytes){(dir != null ? "; previous version kept in version history" : "")}." };
        }

        bool exists = File.Exists(t.FullPath);
        if (Directory.Exists(t.FullPath)) throw new ToolException("exists", "A folder has this name.");
        if (exists && !overwrite) throw new ToolException("exists", "Target exists; pass overwrite=true to replace it.");
        int step = env.Snapshots.BeginStep(env.RunId);
        env.Snapshots.CaptureFile(env.RunId, step, t.FullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(t.FullPath)!);
        await File.WriteAllBytesAsync(t.FullPath, bytes, ct);
        return new ToolOutput { Content = $"Copied {s.Virtual} → {t.Virtual} ({bytes.Length} bytes, snapshot step {step})." };
    }
}

internal sealed class FsDelete : IDeviceTool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "fs_delete", Class = RiskClass.D, Untrusted = UntrustedMode.Never,
        Description = "Delete files (not directories). Local volumes: Recycle Bin; network paths: snapshot, then delete.",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["paths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Files '<folderId>/<relative>'" },
            },
            ["required"] = new JsonArray("paths"),
            ["additionalProperties"] = false,
        },
    };

    private static List<ResolvedPath> Resolve(JsonElement a, ToolEnv env)
    {
        if (!a.TryGetProperty("paths", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new ToolException("invalid_args", "paths: non-empty array required.");
        var list = new List<ResolvedPath>();
        foreach (var e in arr.EnumerateArray())
        {
            var r = env.Folders.Resolve(e.GetString() ?? "");
            FsTools.Require(r, Rights.Delete);
            if (r.Remote == null && !File.Exists(r.FullPath)) throw new ToolException("not_found", $"{r.Virtual} not found.");
            list.Add(r);
        }
        return list;
    }

    public ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        var list = Resolve(a, env);
        var p = new ActionPlan
        {
            Tool = "fs_delete", Class = RiskClass.D, WritesToSynced = list.Any(r => r.Folder.Synced), PermittedByUser = true,
            Summary = "delete:\n" + string.Join('\n', list.Select(r => "  " + r.Virtual)),
        };
        foreach (var r in list) p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    public async Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var list = Resolve(a, env);
        int step = env.Snapshots.BeginStep(env.RunId);
        foreach (var r in list.Where(r => r.Remote == null)) env.Snapshots.CaptureFile(env.RunId, step, r.FullPath); // throws → nothing deleted
        foreach (var r in list)
        {
            if (r.Remote != null)
                await RemoteFolders.Of(env).RecycleAsync(r, env, ct);   // site Recycle Bin
            else if (!r.IsNetwork && OperatingSystem.IsWindows())
                RecycleBin.Delete(r.FullPath);
            else
                File.Delete(r.FullPath);
        }
        return new ToolOutput { Content = $"Deleted {list.Count} file(s) (snapshot step {step}; files of folders connected by link go to the site Recycle Bin)." };
    }
}

internal static class RecycleBin
{
    // SHFileOperation with FOF_ALLOWUNDO moves the file to the Recycle Bin without UI.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Delete(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted != 0 || File.Exists(path))
            throw new IOException($"Recycle Bin delete failed (0x{rc:X}).");
    }
}
