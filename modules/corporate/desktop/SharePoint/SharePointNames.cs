// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Corporate.SharePoint;

// Human names of SharePoint / OneDrive rows (moved from CO.Core PlaceNames, Freedom F1), so the user sees which site or
// library a row is.
public static class SharePointNames
{
    // Default document libraries add nothing to a name (English, Dutch, French, German).
    private static readonly HashSet<string> DefaultLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shared Documents", "Documents", "Gedeelde documenten", "Documenten", "Documents partages", "Freigegebene Dokumente", "Dokumente",
    };

    // "https://engie.sharepoint.com/sites/BelgianNewBuild/Shared Documents/Specs" → "BelgianNewBuild › Specs";
    // a personal site (…-my.sharepoint.com/personal/…) → "OneDrive › Notes".
    public static string Title(string url)
    {
        var (site, rest) = Split(url);
        if (rest.Count > 2) rest = new List<string> { rest[0], "…", rest[^1] };
        return string.Join(" › ", new[] { site }.Concat(rest));
    }

    // Source for the agent name of a new SharePoint row: the folder, or the site when the link is a whole library.
    public static string IdSource(string url)
    {
        var (site, rest) = Split(url);
        return rest.Count > 0 ? rest[^1] : site;
    }

    private static (string site, List<string> rest) Split(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return (url, new());
        var parts = Uri.UnescapeDataString(u.AbsolutePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        string site = u.Host.Split('.')[0];
        if (parts.Count >= 2 && parts[0] is "sites" or "teams") { site = parts[1]; parts = parts.Skip(2).ToList(); }
        else if (parts.Count >= 2 && parts[0] == "personal") { site = "OneDrive"; parts = parts.Skip(2).ToList(); }
        if (parts.Count > 0 && DefaultLibraries.Contains(parts[0])) parts.RemoveAt(0);
        return (site, parts);
    }
}
