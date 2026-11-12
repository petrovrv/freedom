// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace CO.Core.Policy;

// What the user ticked for a folder, a subfolder rule or a connector (CO Desktop → Folders, Connectors).
// A ticked box is the permission itself: the agent acts without asking (deletions go to a Recycle Bin).
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Rights
{
    None = 0,
    Read = 1,     // list, read, search
    Create = 2,   // new files and folders, saved attachments, downloads, drafts
    Edit = 4,     // change existing files (snapshot first), move and rename; mail: move, flag, categories
    Delete = 8,   // files to the Recycle Bin (the site's Recycle Bin for folders connected by link)
    All = Read | Create | Edit | Delete,
}

public static class RightsText
{
    public static string Label(Rights r) => r switch
    {
        Rights.Read => "Read",
        Rights.Create => "Create",
        Rights.Edit => "Edit",
        Rights.Delete => "Delete",
        _ => r.ToString(),
    };

    public static readonly Rights[] Columns = { Rights.Read, Rights.Create, Rights.Edit, Rights.Delete };

    // Short form for tool output: "read, create".
    public static string List(Rights r) =>
        r == Rights.None ? "none" : string.Join(", ", Columns.Where(c => r.HasFlag(c)).Select(c => Label(c).ToLowerInvariant()));
}
