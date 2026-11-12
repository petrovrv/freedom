// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Core.Files;

// An item of a folder connected by link (a document library on a web site), as its provider understands it.
public abstract record RemoteItem
{
    public abstract string Url { get; }
    public abstract string Name { get; }
}

// Serves folders connected by link (FolderRegistry.RemoteFolders, set by a connector module): fs_* call these with the
// same arguments and results as on a local folder. Undo is the site's own: version history and its Recycle Bin.
public interface IRemoteFolderProvider
{
    string Title { get; }                                     // service name for messages ("on <Title>")

    // The folder's root item from its link (null: not a link this provider serves) and an item below it.
    RemoteItem? Root(string url);
    RemoteItem Child(RemoteItem root, string relative);

    Task<ToolOutput> ListAsync(ResolvedPath r, ToolEnv env, CancellationToken ct);
    Task<ToolOutput> StatAsync(ResolvedPath r, ToolEnv env, CancellationToken ct);
    Task<string> ReadTextAsync(ResolvedPath r, ToolEnv env, CancellationToken ct);
    Task<ToolOutput> SearchAsync(ResolvedPath r, ToolEnv env, string q, string glob, int max, bool untrusted, CancellationToken ct);
    Task<ToolOutput> WriteAsync(ResolvedPath r, ToolEnv env, string content, bool overwrite, CancellationToken ct);
    Task<ToolOutput> EditAsync(ResolvedPath r, ToolEnv env, Func<string, string> apply, CancellationToken ct);
    Task<ToolOutput> MkdirAsync(ResolvedPath r, ToolEnv env, CancellationToken ct);
    Task<ToolOutput> MoveAsync(ResolvedPath s, ResolvedPath t, ToolEnv env, CancellationToken ct);
    Task RecycleAsync(ResolvedPath r, ToolEnv env, CancellationToken ct);

    // Whole files, for fs_copy and tools that save into a connected folder.
    Task<bool?> IsDirAsync(RemoteItem item, CancellationToken ct);   // null: nothing has this name
    Task<byte[]> DownloadAsync(RemoteItem item, CancellationToken ct);
    Task UploadAsync(RemoteItem item, byte[] content, CancellationToken ct);
}

public static class RemoteFolders
{
    public static IRemoteFolderProvider Of(ToolEnv env) =>
        env.Folders.RemoteFolders ?? throw new ToolException("unsupported", "Folders connected by link are not available in this CO Desktop.");

    // Plan of a change in a folder connected by link: it is synced content (tainted runs confirm it).
    public static ActionPlan WritePlan(ToolEnv env, string tool, string summary, RiskClass cls, params ResolvedPath[] paths)
    {
        string where = env.Folders.RemoteFolders?.Title ?? "remote folder";
        var p = new ActionPlan { Tool = tool, Class = cls, WritesToSynced = true, Summary = summary + " (" + where + ")" };
        foreach (var r in paths) p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }
}
