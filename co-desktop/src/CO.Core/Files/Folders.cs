// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Tools;

namespace CO.Core.Files;

public sealed class ConnectedFolder
{
    public string Id { get; set; } = "";          // short name the model uses as the first path segment
    public string Path { get; set; } = "";        // absolute local, synced or UNC path
    public bool Writable { get; set; }            // folders.json before 0.6.1; kept equal to "any change right" for older readers
    public Rights? Rights { get; set; }           // 0.6.1: ticked boxes; null in older files (derived from Writable)
    public List<FolderRule> Rules { get; set; } = new();   // subfolders with their own rights; the longest match wins
    public bool Synced { get; set; }              // TZ §7.1: OneDrive / Drive for Desktop / Dropbox
    public bool Project { get; set; }             // files of the current project are trusted (TZ §7.1, column U)
    public List<string> ExcludeNames { get; set; } = new();
    // Set for a folder connected by link (a document library on a site); Path is the same URL. The JSON name is the one
    // of folders.json since 0.6 (persisted; kept so existing files load).
    [System.Text.Json.Serialization.JsonPropertyName("SharePointUrl")] public string? RemoteUrl { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsRemote => !string.IsNullOrEmpty(RemoteUrl);

    [System.Text.Json.Serialization.JsonIgnore]
    public Rights Granted => Rights ?? (Writable ? Policy.Rights.Read | Policy.Rights.Create | Policy.Rights.Edit : Policy.Rights.Read);

    // Rights at a path relative to the folder ("" = the folder itself, '/' separated).
    public Rights RightsAt(string relative) => RuleFor(relative)?.Rights ?? Granted;

    // The rule covering a relative path, the deepest one when rules nest; null = the folder's own rights.
    public FolderRule? RuleFor(string relative)
    {
        string rel = relative.Trim('/');
        return Rules.Where(r => FolderRule.Covers(r.Path, rel)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
    }

    // Rights a rule would inherit if it did not exist.
    public Rights InheritedAt(string relative)
    {
        string rel = relative.Trim('/');
        return Rules.Where(r => r.Path.Length < rel.Length && FolderRule.Covers(r.Path, rel))
            .OrderByDescending(r => r.Path.Length).FirstOrDefault()?.Rights ?? Granted;
    }
}

public sealed class FolderRule
{
    public string Path { get; set; } = "";        // relative to the connected folder, '/' separated
    public Rights Rights { get; set; }

    public static bool Covers(string rule, string relative) =>
        relative.Equals(rule, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed class ResolvedPath
{
    public required ConnectedFolder Folder { get; init; }
    public required string FullPath { get; init; }
    public required string Virtual { get; init; }   // "<folderId>/<relative>" with forward slashes
    public RemoteItem? Remote { get; init; }        // folders connected by link: the item on the site
    public bool IsNetwork => FullPath.StartsWith(@"\\", StringComparison.Ordinal);
    public string Relative => Virtual.Contains('/') ? Virtual[(Virtual.IndexOf('/') + 1)..] : "";
    public Rights Rights => Folder.RightsAt(Relative);
}

public sealed class FolderRegistry
{
    // Same spirit as Bridge contextRoots: credentials and profiles are never reachable.
    public static readonly string[] DefaultExcludes =
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config", "AppData",
        "$Recycle.Bin", "System Volume Information", "Microsoft", "Credentials", "Protect",
        "User Data", "Profiles", // browser profiles (Chrome/Edge "User Data", Firefox "Profiles")
    };

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly List<ConnectedFolder> _folders = new();
    private readonly string? _file;
    private readonly object _sync = new();

    public FolderRegistry(string? file = null)
    {
        _file = file;
        if (file != null && File.Exists(file))
        {
            try { _folders.AddRange(JsonSerializer.Deserialize<List<ConnectedFolder>>(File.ReadAllText(file)) ?? new()); }
            catch (JsonException)
            {
                // An unreadable file is kept as folders.json.bad for the user; CO Desktop starts with no folders.
                File.Copy(file, file + ".bad", true);
            }
        }
        // A damaged entry (no id or path, e.g. a hand-edited file) is dropped instead of stopping CO Desktop at start.
        // Before 0.6.1 a folder had one Writable box: it becomes read + create + edit; deletion stays off.
        _folders.RemoveAll(f => f == null || string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Path));
        foreach (var f in _folders)
        {
            f.Rights ??= f.Granted;
            f.Rules ??= new();
            f.ExcludeNames ??= new();
            f.Rules.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.Path));
        }
    }

    public event Action? Changed;

    // Ticks or clears boxes of a folder (rule = null) or of one of its subfolder rules.
    public void SetRights(string id, string? rule, Rights rights)
    {
        lock (_sync)
        {
            var f = _folders.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException($"No folder '{id}'.");
            if (rule == null) f.Rights = rights;
            else (f.Rules.FirstOrDefault(r => r.Path.Equals(rule, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"No rule '{rule}' in '{id}'.")).Rights = rights;
            f.Writable = (f.Granted & (Rights.Create | Rights.Edit | Rights.Delete)) != 0;
            Save();
        }
        Changed?.Invoke();
    }

    // New name for the agent (first path segment). Chats that used the old name have to use the new one.
    public void Rename(string id, string newId)
    {
        newId = newId.Trim();
        if (!ToolNames.IsValidShort(newId.Replace("-", "")))
            throw new ArgumentException("Name: lower-case letters, digits and '-', up to 12 characters.");
        lock (_sync)
        {
            var f = _folders.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException($"No folder '{id}'.");
            if (newId == id) return;
            if (_folders.Any(x => x.Id == newId)) throw new ArgumentException($"Name '{newId}' is taken.");
            f.Id = newId;
            Save();
        }
        Changed?.Invoke();
    }

    public void SetProject(string id, bool project)
    {
        lock (_sync)
        {
            var f = _folders.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException($"No folder '{id}'.");
            f.Project = project;
            Save();
        }
        Changed?.Invoke();
    }

    public void RemoveRule(string id, string rule)
    {
        lock (_sync)
        {
            _folders.FirstOrDefault(x => x.Id == id)?.Rules.RemoveAll(r => r.Path.Equals(rule, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        Changed?.Invoke();
    }

    // A path inside a connected folder becomes a rule of that folder: one path per file for the agent,
    // its own boxes in the table. Starts with the rights it inherits; returns (folder id, rule path).
    private (string id, string rule)? AddAsRule(string path)
    {
        var parent = _folders.Where(f => PathUtil.IsUnder(path, f.Path) && !SamePath(path, f.Path))
            .OrderByDescending(f => f.Path.Length).FirstOrDefault();
        if (parent == null) return null;
        string rel = RelativeOf(parent, path);
        if (parent.Rules.Any(r => r.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"'{parent.Id}/{rel}' is already in the table.");
        parent.Rules.Add(new FolderRule { Path = rel, Rights = parent.InheritedAt(rel) });
        return (parent.Id, rel);
    }

    // Folders already connected under a new parent turn into its rules; those with the parent's rights are dropped.
    private void AbsorbChildren(ConnectedFolder parent)
    {
        foreach (var child in _folders.Where(f => f != parent && PathUtil.IsUnder(f.Path, parent.Path)).ToList())
        {
            string rel = RelativeOf(parent, child.Path);
            if (child.Granted != parent.Granted)
                parent.Rules.Add(new FolderRule { Path = rel, Rights = child.Granted });
            foreach (var r in child.Rules)
                parent.Rules.Add(new FolderRule { Path = rel + "/" + r.Path, Rights = r.Rights });
            _folders.Remove(child);
        }
    }

    private static bool SamePath(string a, string b) =>
        PathUtil.IsUnder(a, b) && PathUtil.IsUnder(b, a);

    private static string RelativeOf(ConnectedFolder parent, string path) =>
        parent.IsRemote
            ? Uri.UnescapeDataString(path[parent.Path.TrimEnd('/').Length..]).Trim('/')
            : System.IO.Path.GetRelativePath(parent.Path, path).Replace('\\', '/');

    public IReadOnlyList<ConnectedFolder> All { get { lock (_sync) return _folders.ToList(); } }

    // Serves the folders connected by link (set by a connector module; null: such folders are not available).
    public IRemoteFolderProvider? RemoteFolders { get; set; }

    // A folder connected by its link: the agent uses it like a local one (TZ §7.1, folders by link).
    public ConnectedFolder AddRemote(RemoteItem p, string id, bool writable) =>
        AddRemote(p, id, writable ? Rights.Read | Rights.Create | Rights.Edit : Rights.Read);

    public ConnectedFolder AddRemote(RemoteItem p, string id, Rights rights)
    {
        if (!ToolNames.IsValidShort(id.Replace("-", "")))
            throw new ArgumentException("Folder id: lower-case letters, digits and '-', up to 12 characters.");
        var f = new ConnectedFolder { Id = id, Path = p.Url, RemoteUrl = p.Url, Rights = rights, Project = false, Synced = true };
        return Insert(f);
    }

    public ConnectedFolder Add(string path, string id, bool writable, bool project, bool? synced = null) =>
        Add(path, id, writable ? Rights.Read | Rights.Create | Rights.Edit : Rights.Read, project, synced);

    public ConnectedFolder Add(string path, string id, Rights rights, bool project, bool? synced = null)
    {
        if (!ToolNames.IsValidShort(id.Replace("-", "")))
            throw new ArgumentException("Folder id: lower-case letters, digits and '-', up to 12 characters.");
        string full = PathUtil.Normalize(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var f = new ConnectedFolder
        {
            Id = id, Path = full, Rights = rights, Project = project,
            Synced = synced ?? SyncDetector.IsSynced(full),
        };
        return Insert(f);
    }

    // A new path inside a connected folder becomes a rule of that folder (the returned folder); a new parent
    // takes in the folders connected below it.
    private ConnectedFolder Insert(ConnectedFolder f)
    {
        ConnectedFolder result;
        lock (_sync)
        {
            if (_folders.Any(x => SamePath(x.Path, f.Path)))
                throw new ArgumentException($"'{f.Path}' is already connected.");
            if (AddAsRule(f.Path) is { } rule)
                result = _folders.First(x => x.Id == rule.id);
            else
            {
                if (_folders.Any(x => x.Id == f.Id)) throw new ArgumentException($"Folder id '{f.Id}' exists.");
                f.Writable = (f.Granted & (Rights.Create | Rights.Edit | Rights.Delete)) != 0;
                _folders.Add(f);
                AbsorbChildren(f);
                result = f;
            }
            Save();
        }
        Changed?.Invoke();
        return result;
    }

    private static readonly string[] Translit =
    {
        "a", "b", "v", "g", "d", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p",
        "r", "s", "t", "u", "f", "h", "c", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya",
    };

    // Turns anything the user typed (or the folder name) into a free id of the allowed form:
    // lower-case ASCII, Cyrillic transliterated, other characters become '-', at most 12, unique.
    public string MakeId(string? typed, string path)
    {
        string src = string.IsNullOrWhiteSpace(typed) ? Path.GetFileName(path.TrimEnd('\\', '/')) : typed;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in src.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch)) sb.Append(ch);
            else if (ch is >= 'а' and <= 'я') sb.Append(Translit[ch - 'а']);
            else if (ch == 'ё') sb.Append('e');
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string baseId = sb.ToString().Trim('-');
        if (baseId.Length > ToolNames.MaxShort) baseId = baseId[..ToolNames.MaxShort].Trim('-');
        if (baseId.Length == 0) baseId = "folder";
        string id = baseId;
        for (int n = 2; Find(id) != null; n++)
        {
            string suffix = n.ToString();
            string head = baseId.Length + suffix.Length > ToolNames.MaxShort ? baseId[..(ToolNames.MaxShort - suffix.Length)] : baseId;
            id = head + suffix;
        }
        return id;
    }

    public void Remove(string id)
    {
        lock (_sync) { _folders.RemoveAll(f => f.Id == id); Save(); }
        Changed?.Invoke();
    }

    public ConnectedFolder? Find(string id) { lock (_sync) return _folders.FirstOrDefault(f => f.Id == id); }

    public ResolvedPath Resolve(string virtualPath)
    {
        string[] parts = virtualPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ToolException("invalid_path", "Path must start with a connected folder id.");
        ConnectedFolder folder = Find(parts[0])
            ?? throw new ToolException("outside_scope", $"'{parts[0]}' is not a connected folder.");

        var excludes = new HashSet<string>(DefaultExcludes.Concat(folder.ExcludeNames), StringComparer.OrdinalIgnoreCase);
        RemoteItem? root = folder.IsRemote
            ? (RemoteFolders ?? throw new ToolException("unsupported", $"Folder '{folder.Id}' is connected by link; such folders are not available in this CO Desktop."))
                .Root(folder.RemoteUrl!) ?? throw new ToolException("invalid_path", $"Folder '{folder.Id}' has a bad link.")
            : null;
        string current = folder.Path;
        if (root == null) CheckNotReparse(current, virtualPath);
        for (int i = 1; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (seg is "." or "..") throw new ToolException("invalid_path", "'.' and '..' are not allowed.");
            if (seg.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0 || seg.Any(char.IsControl))
                throw new ToolException("invalid_path", $"Invalid characters in '{seg}'.");
            if (seg.EndsWith('.') || seg.EndsWith(' '))
                throw new ToolException("invalid_path", $"Name '{seg}' ends with a dot or space.");
            if (DeviceNames.Contains(System.IO.Path.GetFileNameWithoutExtension(seg)))
                throw new ToolException("invalid_path", $"'{seg}' is a reserved device name.");
            if (excludes.Contains(seg))
                throw new ToolException("excluded", $"'{seg}' is excluded from agent access.");
            if (root != null) { current = current.TrimEnd('/') + "/" + seg; continue; }
            current = System.IO.Path.Combine(current, seg);
            CheckNotReparse(current, virtualPath);
        }

        if (root != null)
        {
            string rel = string.Join('/', parts.Skip(1));
            return new ResolvedPath
            {
                Folder = folder, FullPath = current, Virtual = string.Join('/', parts),
                Remote = rel.Length == 0 ? root : RemoteFolders!.Child(root, rel),
            };
        }

        if (!PathUtil.IsUnder(current, folder.Path))
            throw new ToolException("outside_scope", "Path escapes the connected folder.");

        return new ResolvedPath { Folder = folder, FullPath = current, Virtual = string.Join('/', parts) };
    }

    public string ToVirtual(ConnectedFolder folder, string fullPath)
    {
        string rel = System.IO.Path.GetRelativePath(folder.Path, fullPath).Replace('\\', '/');
        return rel == "." ? folder.Id : folder.Id + "/" + rel;
    }

    // Symlinks, junctions and mount points are never traversed (same rule as Bridge v0.7).
    private static void CheckNotReparse(string path, string virtualPath)
    {
        FileSystemInfo? fi = Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path) : null;
        if (fi != null && (fi.Attributes & FileAttributes.ReparsePoint) != 0 && fi.LinkTarget != null)
            throw new ToolException("reparse_point", $"'{virtualPath}' goes through a link or junction.");
    }

    private void Save()
    {
        if (_file == null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_folders, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class SyncDetector
{
    // TZ §7.1: synced folders are marked so tainted writes into them need confirmation.
    public static bool IsSynced(string path)
    {
        foreach (string root in KnownSyncRoots())
            if (PathUtil.IsUnder(path, root)) return true;
        return false;
    }

    // Roots of the OneDrive client ("OneDrive", "OneDrive - <org>") that exist on this computer.
    public static IEnumerable<string> OneDriveRoots() =>
        KnownSyncRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => System.IO.Path.GetFileName(r.TrimEnd('\\', '/')).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));

    // Synced roots of other organisations' libraries (the OneDrive client puts them under "<org>\") and their folders.
    public static IEnumerable<string> LibraryEntries()
    {
        var oneDrive = OneDriveRoots().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string root in KnownSyncRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
            if (oneDrive.Contains(root) || name.Length == 0 || name is "Dropbox" or "Google Drive") continue;
            yield return root;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (string d in subs.Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))) yield return d;
        }
    }

    public static IEnumerable<string> KnownSyncRoots()
    {
        foreach (string var in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            string? v = Environment.GetEnvironmentVariable(var);
            if (!string.IsNullOrEmpty(v)) yield return v;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (string r in WindowsSyncRoots()) yield return r;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return System.IO.Path.Combine(home, "Dropbox");
            yield return System.IO.Path.Combine(home, "Google Drive");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsSyncRoots()
    {
        var list = new List<string>();
        try
        {
            // Cloud Files API sync roots (OneDrive, Drive for Desktop streaming, Dropbox) register here.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (key != null)
                foreach (string sub in key.GetSubKeyNames())
                {
                    using var users = key.OpenSubKey(sub + @"\UserSyncRoots");
                    if (users == null) continue;
                    foreach (string n in users.GetValueNames())
                        if (users.GetValue(n) is string p && p.Length > 0) list.Add(p);
                }
            // Drive for Desktop mounts a drive letter (default G:) without a sync root entry.
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady && d.VolumeLabel.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
                    list.Add(d.RootDirectory.FullName);
        }
        catch (Exception) { /* detection is best effort; the user can mark a folder synced manually */ }
        return list;
    }
}
