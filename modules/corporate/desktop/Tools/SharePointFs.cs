// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CO.Corporate.Tools;

// fs_* tools on a SharePoint folder connected by link: same arguments and results as on a local folder (the core's
// IRemoteFolderProvider, FolderRegistry.RemoteFolders). Undo: SharePoint keeps the previous version of an overwritten
// file and puts deleted files into the site Recycle Bin.
public sealed class SharePointFs : IRemoteFolderProvider
{
    public SharePointFs(SharePointService service) => Service = service;

    public SharePointService Service { get; }
    public string Title => "SharePoint";

    public RemoteItem? Root(string url) => SharePointPath.TryParse(url);

    public RemoteItem Child(RemoteItem root, string relative)
    {
        var p = (SharePointPath)root;
        return p with { ServerRelative = p.ServerRelative + "/" + relative };
    }

    private SharePointService Sp(ToolEnv _) => Service;
    private static SharePointPath P(RemoteItem item) => (SharePointPath)item;
    private static SharePointPath P(ResolvedPath r) => (SharePointPath)r.Remote!;

    public async Task<bool?> IsDirAsync(RemoteItem item, CancellationToken ct) => (await Service.StatAsync(P(item), ct))?.IsDir;
    public Task<byte[]> DownloadAsync(RemoteItem item, CancellationToken ct) => Service.DownloadAsync(P(item), ct);
    public Task UploadAsync(RemoteItem item, byte[] content, CancellationToken ct) => Service.UploadAsync(P(item), content, ct);

    private static string Virtual(ResolvedPath root, string serverRelative)
    {
        string baseRel = P(root).ServerRelative.TrimEnd('/');
        string rel = serverRelative.Length > baseRel.Length ? serverRelative[(baseRel.Length + 1)..] : "";
        return rel.Length == 0 ? root.Virtual : root.Virtual.TrimEnd('/') + "/" + rel;
    }

    public async Task<ToolOutput> ListAsync(ResolvedPath r, ToolEnv env, CancellationToken ct)
    {
        var items = await Sp(env).ListAsync(P(r), ct);
        var entries = items.OrderBy(e => !e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => new
        {
            name = e.Name,
            type = e.IsDir ? "dir" : "file",
            size = e.Size,
            modifiedUtc = e.Modified?.ToString("u"),
        });
        return new ToolOutput { Content = FsTools.Json(new { path = r.Virtual, entries, truncated = false }) };
    }

    public async Task<ToolOutput> StatAsync(ResolvedPath r, ToolEnv env, CancellationToken ct)
    {
        var e = await Sp(env).StatAsync(P(r), ct) ?? throw new ToolException("not_found", "Not found.");
        return new ToolOutput
        {
            Content = FsTools.Json(new
            {
                path = r.Virtual, type = e.IsDir ? "dir" : "file", size = e.Size, items = e.Items,
                modifiedUtc = e.Modified?.ToString("u"), synced = true, sharePoint = r.Remote!.Url,
            }),
        };
    }

    public async Task<string> ReadTextAsync(ResolvedPath r, ToolEnv env, CancellationToken ct)
    {
        byte[] bytes = await Sp(env).DownloadAsync(P(r), ct);
        string tmp = Path.Combine(Path.GetTempPath(), "co-sp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(r.Remote!.Name));
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            return TextExtractor.Extract(tmp);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    public async Task<ToolOutput> SearchAsync(ResolvedPath r, ToolEnv env, string q, string glob, int max, bool untrusted, CancellationToken ct)
    {
        if (q.StartsWith("re:"))
            throw new ToolException("unsupported", "Regular expressions are not supported in SharePoint folders; search plain words.");
        var hits = await Sp(env).SearchAsync(P(r), q, max, ct);
        var nameFilter = new Regex("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        var lines = hits
            .Where(h => nameFilter.IsMatch(h.ServerRelative[(h.ServerRelative.LastIndexOf('/') + 1)..]))
            .Select(h => $"{Virtual(r, h.ServerRelative)}: {(h.Summary.Length > 300 ? h.Summary[..300] + "…" : h.Summary)}")
            .Take(max).ToList();
        string note = "\n[SharePoint search: content index of the site; files changed in the last minutes may be missing]";
        return new ToolOutput { Content = (lines.Count == 0 ? "No matches." : string.Join('\n', lines)) + note, Untrusted = untrusted };
    }

    public async Task<ToolOutput> WriteAsync(ResolvedPath r, ToolEnv env, string content, bool overwrite, CancellationToken ct)
    {
        var sp = Sp(env);
        var existing = await sp.StatAsync(P(r), ct);
        if (existing is { IsDir: true }) throw new ToolException("exists", "A folder has this name.");
        if (existing != null && !overwrite) throw new ToolException("exists", "File exists; pass overwrite=true to replace it.");
        await sp.UploadAsync(P(r), new UTF8Encoding(false).GetBytes(content), ct);
        return new ToolOutput { Content = $"Wrote {r.Virtual} on SharePoint{(existing != null ? " (previous version kept in version history)" : "")}." };
    }

    public async Task<ToolOutput> EditAsync(ResolvedPath r, ToolEnv env, Func<string, string> apply, CancellationToken ct)
    {
        string ext = Path.GetExtension(r.Remote!.Name).ToLowerInvariant();
        if (ext is ".docx" or ".xlsx" or ".pptx" or ".pdf")
            throw new ToolException("unsupported", "fs_edit works on text files; Office documents use word_/excel_/powerpoint_ tools.");
        var sp = Sp(env);
        byte[] bytes = await sp.DownloadAsync(P(r), ct);
        bool bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        string after = apply(TextExtractor.Decode(bytes));
        await sp.UploadAsync(P(r), new UTF8Encoding(bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(after)).ToArray(), ct);
        return new ToolOutput { Content = $"Edited {r.Virtual} on SharePoint (previous version kept in version history)." };
    }

    public async Task<ToolOutput> MkdirAsync(ResolvedPath r, ToolEnv env, CancellationToken ct)
    {
        await Sp(env).EnsureFolderAsync(P(r), ct);
        return new ToolOutput { Content = $"Created {r.Virtual} on SharePoint." };
    }

    public async Task<ToolOutput> MoveAsync(ResolvedPath s, ResolvedPath t, ToolEnv env, CancellationToken ct)
    {
        if (s.Remote is not SharePointPath sr || t.Remote is not SharePointPath tr || !sr.Origin.Equals(tr.Origin))
            throw new ToolException("unsupported", "fs_move works inside one place; to move between SharePoint and another folder use fs_copy, then fs_delete on the source.");
        var sp = Sp(env);
        if (await sp.StatAsync(sr, ct) is not { IsDir: false }) throw new ToolException("not_found", "Source file not found (directories are not moved).");
        if (await sp.StatAsync(tr, ct) != null) throw new ToolException("exists", "Target exists.");
        await sp.MoveAsync(sr, tr, ct);
        return new ToolOutput { Content = $"Moved {s.Virtual} → {t.Virtual} on SharePoint." };
    }

    public async Task RecycleAsync(ResolvedPath r, ToolEnv env, CancellationToken ct) => await Sp(env).RecycleAsync(P(r), ct);
}
