// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Core.Files;

// Human names for connected places, so the user sees which site or library a row is.
public static class PlaceNames
{
    // What a synced folder belongs to, by its sync root: "OneDrive" (root "OneDrive - <org>"), "Google Drive",
    // "Dropbox", or "Library" (document libraries synced by the OneDrive client under the organisation folder). null = not synced.
    public static string? SyncedKind(string path, IEnumerable<string> roots)
    {
        string? root = roots.Where(r => Policy.PathUtil.IsUnder(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root == null) return null;
        string name = Path.GetFileName(root.TrimEnd('\\', '/'));
        if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        if (name.Length == 0 || name.Equals("My Drive", StringComparison.OrdinalIgnoreCase) || name.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
            return "Google Drive";
        if (name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
        return "Library";
    }

    // Last two folders of a path: "Amentum (CMS) › EEX - Technical Support …".
    public static string Tail(string path)
    {
        var parts = path.TrimEnd('\\', '/').Split('\\', '/').Where(p => p.Length > 0).ToArray();
        return parts.Length <= 2 ? string.Join(" › ", parts) : string.Join(" › ", parts[^2..]);
    }
}
