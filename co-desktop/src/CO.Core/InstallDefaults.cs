// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json;

namespace CO.Core;

// Freedom F1: the names of this install (CO site, owner, members, Google Cloud project) are not in the code. The build
// embeds the client part of install.json (CO.Core.csproj target AiwbInstallJson); settings files of the apps start from
// these values. A neutral build (no install.json at build time, Freedom 0.4.0 release installers) reads the install.json
// the setup wizard wrote on this computer (install/aiwb_install.py path(): AIWB_INSTALL, else
// %LOCALAPPDATA%\AIWorkbench\install.json, else ~/.config/aiwb/install.json), then install.json next to the app;
// without any of them the values are empty and the app asks for them at first start.
public static class InstallDefaults
{
    private static readonly Lazy<(JsonElement Doc, string Source)> Loaded = new(Load);
    private static JsonElement Doc => Loaded.Value.Doc;

    /// <summary>Where the values come from: "built-in", a file path, or "" (none).</summary>
    public static string Source => Loaded.Value.Source;

    private static (JsonElement, string) Load()
    {
        try
        {
            using Stream? s = typeof(InstallDefaults).Assembly.GetManifestResourceStream("CO.Core.install.json");
            if (s != null) return (JsonDocument.Parse(s).RootElement.Clone(), "built-in");
        }
        catch (JsonException) { }
        foreach (string path in CandidateFiles())
        {
            JsonElement e = ReadFile(path);
            if (e.ValueKind == JsonValueKind.Object) return (e, path);
        }
        return (default, "");
    }

    /// <summary>install.json files a build without built-in values looks at, in order (same rule as aiwb_install.path()).</summary>
    public static IEnumerable<string> CandidateFiles()
    {
        string? env = Environment.GetEnvironmentVariable("AIWB_INSTALL");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        else if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) yield return Path.Combine(local, "AIWorkbench", "install.json");
        }
        else yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "aiwb", "install.json");
        yield return Path.Combine(AppContext.BaseDirectory, "install.json");
    }

    /// <summary>The object of an install.json file, default when it is missing or broken (never throws).</summary>
    public static JsonElement ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    /// <summary>https://host of an install.json object ("" when missing or not http(s)).</summary>
    public static string SiteUrlOf(JsonElement doc)
    {
        string u = Str(doc, "site", "url").TrimEnd('/');
        return Uri.TryCreate(u, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "https" || uri.Scheme == "http") ? u : "";
    }

    internal static string Str(JsonElement e, params string[] path)
    {
        foreach (string p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    private static IEnumerable<JsonElement> MemberRows() =>
        Doc.ValueKind == JsonValueKind.Object && Doc.TryGetProperty("members", out JsonElement m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>https://co.example.org (no trailing slash), "" when not built in.</summary>
    public static string SiteUrl => SiteUrlOf(Doc);

    public static string PubSubProjectId => Str(Doc, "google", "projectNumber");

    /// <summary>Environment -> Google account, in the order of install.json.</summary>
    public static Dictionary<string, string> Members =>
        MemberRows().Where(r => Str(r, "env").Length > 0).ToDictionary(r => Str(r, "env"), r => Str(r, "email").ToLowerInvariant());

    public static string OwnerEnv => MemberRows().Select(r => Str(r, "role") == "owner" ? Str(r, "env") : "").FirstOrDefault(e => e.Length > 0) ?? "";

    public static string OwnerEmail => OwnerEnv.Length > 0 && Members.TryGetValue(OwnerEnv, out string? e) ? e : "";

    /// <summary>Display name of the owner ("the owner" when not built in), for texts like "send this code to …".</summary>
    public static string OwnerName =>
        MemberRows().Where(r => Str(r, "role") == "owner").Select(r => Str(r, "name")).FirstOrDefault(n => n.Length > 0) ?? "the owner";

    /// <summary>Environments other than the owner's (their computers pair without the code, AutoPair).</summary>
    public static List<string> MemberEnvs => Members.Keys.Where(e => e != OwnerEnv).ToList();
}
