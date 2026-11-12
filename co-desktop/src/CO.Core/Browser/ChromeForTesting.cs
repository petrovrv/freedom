// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;

namespace CO.Core.Browser;

// Mac resident R3: Chrome for Testing (Google's portable Chromium) as the agent browser where no Chrome/Edge is installed.
public static class ChromeForTesting
{
    public const string Index = "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json";

    // Stable channel entry for the platform in last-known-good-versions-with-downloads.json; only https from Google.
    public static (string version, string url) PickDownload(string json, string platform)
    {
        var stable = JsonNode.Parse(json)?["channels"]?["Stable"] ?? throw new InvalidDataException("no Stable channel");
        string version = stable["version"]?.GetValue<string>() ?? throw new InvalidDataException("no version");
        foreach (var d in stable["downloads"]?["chrome"]?.AsArray() ?? new JsonArray())
            if (d?["platform"]?.GetValue<string>() == platform && d["url"]?.GetValue<string>() is string u
                && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && (uri.Host == "storage.googleapis.com" || uri.Host.EndsWith(".googleapis.com", StringComparison.Ordinal)))
                return (version, u);
        throw new InvalidDataException("no " + platform + " download");
    }
}
