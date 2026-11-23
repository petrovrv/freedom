// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Policy;

namespace CO.Core.Modules;

// Freedom F10 (SPEC §3.6): what a device knows of the modules of its workbench. The server registry (db.php op
// modules_list, the same list CO Web reads) says which modules are installed and enabled; for an enabled module it adds
// the block "device": the tools it declares for devices with their risk class (ext/1 device.tools), the device ops its
// settings page may call on the device it is shown on, the sha256 of its device/ files and the per-device default.
// The core knows no module by name: everything here comes from that list.

public sealed record ModuleUiEntry(string Slot, string Id, string Entry, string Title)
{
    public string Path { get; init; } = "";        // ui.page: an extra top-level address of CO Web ("" = only /x/<module>/<id>)
    public bool Window { get; init; }              // ext/1 ui.window: a desktop app opens this page in its own window
    public string WindowKey { get; init; } = "";   // query parameter naming one window ("" = the whole address)
    public bool App { get; init; }                 // ext/1 shell: an app of the launcher (also the jump list and tray menu)
    public string Icon { get; init; } = "";        // its icon: a short emoji or text
    public string Label { get; init; } = "";       // its short name
}

/// <summary>ext/1 shell F14e.2: an app of the launcher (a ui.page with app: true of an enabled module), the way CO Web
/// shows it: the icon falls back to the first letter of the name.</summary>
public sealed record ModuleApp(ModuleEntry Module, ModuleUiEntry Page, string Label, string Icon)
{
    public string Key => Module.Id + "/" + Page.Id;
}

public sealed record ModuleToolSpec(string Name, RiskClass Class, string Description, JsonObject? Input, string Run,
    IReadOnlyList<string> Platforms, bool Untrusted)
{
    public bool Native => Run.Length == 0;   // served by the module's part compiled into the app
    public bool RunsOn(string platform) => Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
}

public sealed class ModuleEntry
{
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Standard { get; init; }
    public string Publisher { get; init; } = "";
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ModuleUiEntry> Ui { get; init; } = Array.Empty<ModuleUiEntry>();
    public IReadOnlyList<ModuleToolSpec> Tools { get; init; } = Array.Empty<ModuleToolSpec>();
    public IReadOnlyList<string> DeviceOps { get; init; } = Array.Empty<string>();
    public string OpsRun { get; init; } = "";                 // device/ file that answers device ops ("" = native part)
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();  // device/<rel> -> sha256
    public bool DefaultOn { get; init; } = true;              // per-device default (manifest device.default)
    public string Warning { get; init; } = "";                // shown before the module is switched on on a device
    public IReadOnlyList<string> Navigate { get; init; } = Array.Empty<string>();   // ext/1 navigate: https origins, site paths

    /// <summary>The ui.page of an enabled module that a desktop app opens in its own window for this address path
    /// (/x/&lt;module&gt;/&lt;page&gt; or the page's own path); null when none.</summary>
    public ModuleUiEntry? WindowPage(string path) => !Enabled ? null : Ui.FirstOrDefault(u => u.Slot == "ui.page" && u.Window
        && (path == $"/x/{Id}/{u.Id}" || (u.Path.Length > 0 && path == u.Path)));

    public bool HasDevicePart => Tools.Count > 0 || DeviceOps.Count > 0;
    public ModuleToolSpec? Tool(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

public static class ModuleList
{
    public const int ExtVersion = 1;
    static readonly System.Text.RegularExpressions.Regex IdRe = new("^[a-z0-9][a-z0-9-]{0,62}$");
    static readonly System.Text.RegularExpressions.Regex ToolRe = new("^[a-z][a-z0-9_]{2,63}$");
    static readonly System.Text.RegularExpressions.Regex OpRe = new("^[a-z][a-z0-9_]{2,40}$");
    static readonly System.Text.RegularExpressions.Regex DeviceFileRe = new("^device/[A-Za-z0-9._/-]+$");
    static readonly System.Text.RegularExpressions.Regex EntryRe = new(@"^ui/[A-Za-z0-9._/-]+\.html$");
    static readonly System.Text.RegularExpressions.Regex PagePathRe = new("^/[a-z0-9][a-z0-9-]{1,40}$");
    static readonly System.Text.RegularExpressions.Regex WindowKeyRe = new("^[A-Za-z][A-Za-z0-9_-]{0,30}$");
    static readonly System.Text.RegularExpressions.Regex NavRe = new(@"^(https://[a-z0-9.-]+(:\d{1,5})?|/[A-Za-z0-9._/-]{1,80})$");
    static readonly System.Text.RegularExpressions.Regex PageIdRe = new("^[A-Za-z0-9._-]{1,40}$");
    static readonly string[] Slots = { "ui.header", "ui.page", "ui.panel", "ui.settings" };

    public static bool ValidId(string id) => IdRe.IsMatch(id);
    public static bool ValidOp(string op) => OpRe.IsMatch(op);
    public static bool ValidDeviceFile(string rel) => DeviceFileRe.IsMatch(rel) && !rel.Contains("..");

    /// <summary>Apps of the launcher in the order of the list (CO Web shellApps): the jump list and the tray menu of a
    /// desktop app.</summary>
    public static IReadOnlyList<ModuleApp> Apps(IEnumerable<ModuleEntry> modules) => modules.Where(m => m.Enabled)
        .SelectMany(m => m.Ui.Where(u => u.Slot == "ui.page" && u.App).Select(u =>
        {
            string label = u.Label.Length > 0 ? u.Label : u.Title.Length > 0 ? u.Title : m.Title.Length > 0 ? m.Title : m.Id;
            string first = label.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(label).ToUpperInvariant() : "";
            return new ModuleApp(m, u, label, u.Icon.Length > 0 ? u.Icon : first.Length > 0 ? first : "▫");
        })).ToList();

    /// <summary>"&lt;module&gt;/&lt;page&gt;" of an app (the --app argument of a jump list task); null when malformed.</summary>
    public static (string Module, string Page)? ParseAppKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 104) return null;
        int i = key.IndexOf('/');
        if (i <= 0) return null;
        string m = key[..i], p = key[(i + 1)..];
        return ValidId(m) && PageIdRe.IsMatch(p) && !p.Contains("..") ? (m, p) : null;
    }

    /// <summary>Modules of a modules_list answer; anything malformed is left out. Null when the answer is not a list
    /// of this ext version (an older server, an error).</summary>
    public static IReadOnlyList<ModuleEntry>? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { return null; }
        if (root is not JsonObject o || o["modules"] is not JsonArray arr) return null;
        if (o["ext"] is JsonValue v && v.TryGetValue(out int ext) && ext != ExtVersion) return null;
        var list = new List<ModuleEntry>();
        foreach (var n in arr)
        {
            if (n is not JsonObject m) continue;
            string id = Str(m, "id");
            if (!ValidId(id) || list.Any(x => x.Id == id)) continue;
            // a device part counts only with the permission device.tools the owner agreed to
            var dev = Strings(m["permissions"]).Contains("device.tools") ? m["device"] as JsonObject : null;
            list.Add(new ModuleEntry
            {
                Id = id, Version = Str(m, "version"), Title = Str(m, "title") is { Length: > 0 } t ? t : id,
                Description = Str(m, "description"), Enabled = Bool(m, "enabled"), Standard = Bool(m, "standard"),
                Publisher = Str(m, "publisher"), Permissions = Strings(m["permissions"]),
                Ui = UiEntries(m["ui"]),
                Tools = dev == null ? Array.Empty<ModuleToolSpec>() : ToolSpecs(dev["tools"]),
                DeviceOps = dev == null ? Array.Empty<string>() : Strings(dev["ops"]).Where(ValidOp).Distinct().ToList(),
                OpsRun = dev != null && Str(dev, "opsRun") is { } r && ValidDeviceFile(r) ? r : "",
                Files = dev?["files"] is JsonObject f
                    ? f.Where(kv => ValidDeviceFile(kv.Key) && kv.Value is JsonValue sv && sv.TryGetValue(out string? sha) && IsSha(sha))
                        .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>().ToLowerInvariant())
                    : new Dictionary<string, string>(),
                DefaultOn = dev == null || Str(dev, "default") != "off",
                Warning = dev == null ? "" : Str(dev, "warning"),
                Navigate = Strings(m["navigate"]).Where(n => NavRe.IsMatch(n) && !n.Contains("..")).Distinct().ToList(),
            });
        }
        return list;
    }

    static IReadOnlyList<ModuleUiEntry> UiEntries(JsonNode? n)
    {
        var list = new List<ModuleUiEntry>();
        if (n is not JsonArray a) return list;
        foreach (var u in a.OfType<JsonObject>())
        {
            string slot = Str(u, "slot"), entry = Str(u, "entry");
            if (!Slots.Contains(slot) || !EntryRe.IsMatch(entry) || entry.Contains("..")) continue;
            bool page = slot == "ui.page";
            list.Add(new ModuleUiEntry(slot, Str(u, "id") is { Length: > 0 } id ? id : slot, entry, Str(u, "title"))
            {
                Path = page && PagePathRe.IsMatch(Str(u, "path")) ? Str(u, "path") : "",
                Window = page && Bool(u, "window"),
                WindowKey = page && WindowKeyRe.IsMatch(Str(u, "windowKey")) ? Str(u, "windowKey") : "",
                App = page && Bool(u, "app"),
                Icon = ShortText(Str(u, "icon"), 8),
                Label = ShortText(Str(u, "label"), 60),
            });
        }
        return list;
    }

    static IReadOnlyList<ModuleToolSpec> ToolSpecs(JsonNode? n)
    {
        var list = new List<ModuleToolSpec>();
        if (n is not JsonArray a) return list;
        foreach (var t in a.OfType<JsonObject>())
        {
            string name = Str(t, "name"), run = Str(t, "run");
            if (!ToolRe.IsMatch(name) || list.Any(x => x.Name == name)) continue;
            string c = Str(t, "class") is { Length: > 0 } k ? k : Str(t, "risk");   // "risk" is another name of the class
            if (!Enum.TryParse<RiskClass>(c, ignoreCase: false, out var cls) || !Enum.IsDefined(cls) || c.Length != 1) continue;
            if (run.Length > 0 && !ValidDeviceFile(run)) continue;
            list.Add(new ModuleToolSpec(name, cls, Str(t, "description"), t["input"] as JsonObject, run, Strings(t["platforms"]),
                t["untrusted"] is not JsonValue u || !u.TryGetValue(out bool b) || b));
        }
        return list;
    }

    // a name or an icon shown by the desktop app: no control characters, not longer than max text elements
    static string ShortText(string s, int max)
    {
        s = s.Trim();
        if (s.Any(char.IsControl)) return "";
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var sb = new System.Text.StringBuilder();
        for (int n = 0; n < max && e.MoveNext(); n++) sb.Append(e.GetTextElement());
        return sb.ToString();
    }

    static bool IsSha(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue(out bool b) && b;

    static IReadOnlyList<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : Array.Empty<string>();
}
