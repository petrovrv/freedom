// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Browser;

namespace CO.Core.Tests;

// Live browser tests (CO_LIVE_BROWSER=1). Owner's rule 10.11: they run first on the device that needs them (a family
// Mac: device tool mac_check action=start live=true); on the owner's home PC only after his yes (read-guard holds the
// command). Each test starts its own Chromium in a temp profile, headless unless CO_LIVE_BROWSER_VISIBLE=1, and stops
// it at the end, so no windows are left on anyone's desktop.
internal static class LiveBrowser
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CO_LIVE_BROWSER") == "1";

    public static LiveEdge Start(string profileDir) => new(new EdgeBrowser(profileDir, FindExe,
        "No Chromium for live tests: install Microsoft Edge or set CO_LIVE_BROWSER_EXE.")
    {
        ExtraArgs = Environment.GetEnvironmentVariable("CO_LIVE_BROWSER_VISIBLE") == "1" ? [] : ["--headless=new"],
    });

    // CO_LIVE_BROWSER_EXE (mac_check passes the agent browser of the Mac), else Edge on Windows, else Chrome/Edge on macOS.
    private static string? FindExe()
    {
        string? env = Environment.GetEnvironmentVariable("CO_LIVE_BROWSER_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        if (OperatingSystem.IsWindows()) return EdgeBrowser.FindEdge();
        foreach (var p in new[]
                 {
                     "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                     "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                 })
            if (File.Exists(p)) return p;
        return null;
    }
}

internal sealed class LiveEdge(EdgeBrowser browser) : IAsyncDisposable
{
    public EdgeBrowser Browser { get; } = browser;

    public async ValueTask DisposeAsync()
    {
        await Browser.ShutdownAsync();
        await Browser.DisposeAsync();
    }
}
