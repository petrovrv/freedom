// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json;

namespace CO.Core;

// Freedom F1: the names of this install (CO site, owner, members, Google Cloud project) are not in the code. The build
// embeds the client part of install.json (CO.Core.csproj target AiwbInstallJson); settings files of the apps start from
// these values. A build without install.json has empty values and the app asks for them in its settings.
public static class InstallDefaults
{
    private static readonly Lazy<JsonElement> Doc = new(() =>
    {
        try
        {
            using Stream? s = typeof(InstallDefaults).Assembly.GetManifestResourceStream("CO.Core.install.json");
            return s == null ? default : JsonDocument.Parse(s).RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    });

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (string p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    private static IEnumerable<JsonElement> MemberRows() =>
        Doc.Value.ValueKind == JsonValueKind.Object && Doc.Value.TryGetProperty("members", out JsonElement m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>https://co.example.org (no trailing slash), "" when not built in.</summary>
    public static string SiteUrl => Str(Doc.Value, "site", "url").TrimEnd('/');

    public static string PubSubProjectId => Str(Doc.Value, "google", "projectNumber");

    /// <summary>Environment -> Google account, in the order of install.json.</summary>
    public static Dictionary<string, string> Members =>
        MemberRows().Where(r => Str(r, "env").Length > 0).ToDictionary(r => Str(r, "env"), r => Str(r, "email").ToLowerInvariant());

    public static string OwnerEnv => MemberRows().Select(r => Str(r, "role") == "owner" ? Str(r, "env") : "").FirstOrDefault(e => e.Length > 0) ?? "";

    public static string OwnerEmail => OwnerEnv.Length > 0 && Members.TryGetValue(OwnerEnv, out string? e) ? e : "";

    /// <summary>Display name of the owner ("the owner" when not built in), for texts like "send this code to …".</summary>
    public static string OwnerName =>
        MemberRows().Where(r => Str(r, "role") == "owner").Select(r => Str(r, "name")).FirstOrDefault(n => n.Length > 0) ?? "the owner";

    /// <summary>Corporate connectors (connectors/corporate) on by default in this install: install.json
    /// "connectors": {"corporate": true}. Off when absent (SPEC §3: employer data, the user decides).</summary>
    public static bool CorporateConnectors =>
        Doc.Value.ValueKind == JsonValueKind.Object && Doc.Value.TryGetProperty("connectors", out JsonElement c)
        && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("corporate", out JsonElement v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Environments other than the owner's (their computers pair without the code, AutoPair).</summary>
    public static List<string> MemberEnvs => Members.Keys.Where(e => e != OwnerEnv).ToList();
}
