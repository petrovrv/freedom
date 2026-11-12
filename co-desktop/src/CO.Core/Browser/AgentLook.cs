// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Core.Browser;

// 0.9.3: the CO browser looks different from the user's own Edge, so its windows and tabs are not mistaken for it:
// an orange theme (window frame, tab strip, toolbar) and an orange frame around every page. Two unpacked extensions
// in the profile folder, loaded with --load-extension (a theme extension may not carry content scripts).
// 0.9.6: a third one, "tabs", tells the tab chamberlain of browser-agent 0.13.0+ which tabs the user pinned (the lock:
// never closed, never sent to another site) and when they were last used. Same files as browser-agent\ext-tabs.
public static class AgentLook
{
    public const string FolderName = "co-agent-look";

    private const string ThemeManifest = """
        {
          "manifest_version": 3,
          "name": "CO agent browser theme",
          "version": "1.0",
          "description": "Orange window of the AI Workbench agent browser (CO Desktop).",
          "theme": {
            "colors": {
              "frame": [122, 46, 0],
              "frame_inactive": [92, 38, 8],
              "frame_incognito": [122, 46, 0],
              "toolbar": [255, 210, 160],
              "tab_text": [40, 20, 0],
              "tab_background_text": [255, 236, 214],
              "tab_background_text_inactive": [230, 205, 180],
              "bookmark_text": [40, 20, 0],
              "button_background": [255, 210, 160],
              "omnibox_background": [255, 241, 226],
              "omnibox_text": [30, 15, 0],
              "ntp_background": [255, 226, 194],
              "ntp_text": [40, 20, 0]
            }
          }
        }
        """;

    private const string FrameManifest = """
        {
          "manifest_version": 3,
          "name": "CO agent browser frame",
          "version": "1.0",
          "description": "Orange frame around pages of the AI Workbench agent browser (CO Desktop).",
          "content_scripts": [
            { "matches": ["<all_urls>"], "css": ["frame.css"], "run_at": "document_start", "all_frames": false }
          ]
        }
        """;

    // browser-agent evaluates coTabs() in this service worker over CDP; "debugger" only for getTargets (no attach).
    private const string TabsManifest = """
        {
          "manifest_version": 3,
          "name": "CO agent browser tabs",
          "version": "1.0",
          "description": "Tells the tab chamberlain of the AI Workbench agent browser which tabs the user pinned (lock) and when they were last used.",
          "permissions": ["tabs", "debugger"],
          "background": { "service_worker": "co-tabs-sw.js" }
        }
        """;

    private const string TabsWorker = """
        // CO agent browser tabs (browser-agent 0.13.0): the tab chamberlain reads the tab list over CDP by evaluating
        // coTabs() in this service worker. A pinned tab is the user's lock: never closed, never sent to another site.
        // chrome.debugger.getTargets() only lists targets (no attach, no banner); its id is the CDP target id.
        self.coTabsVersion = 1;
        self.coTabs = async () => {
          const [tabs, targets] = await Promise.all([chrome.tabs.query({}), chrome.debugger.getTargets()]);
          const ids = new Map(targets.filter(t => t.tabId !== undefined).map(t => [t.tabId, t.id]));
          return tabs.map(t => ({
            targetId: ids.get(t.id) || null, windowId: t.windowId, pinned: !!t.pinned, active: !!t.active,
            discarded: !!t.discarded, frozen: !!t.frozen, lastAccessed: t.lastAccessed || 0,
          }));
        };
        // Keeps the worker running so the chamberlain finds it; tab events wake it after the browser stopped it.
        const keep = () => chrome.runtime.getPlatformInfo(() => {});
        setInterval(keep, 20000);
        chrome.runtime.onStartup.addListener(keep);
        chrome.runtime.onInstalled.addListener(keep);
        chrome.tabs.onActivated.addListener(keep);
        chrome.tabs.onUpdated.addListener(keep);
        """;

    // A fixed, click-through outline above the page; an empty pseudo-element stays out of the accessibility tree.
    private const string FrameCss = """
        @media screen {
          html::after {
            content: "" !important; position: fixed !important; inset: 0 !important;
            border: 4px solid #e8730c !important; box-sizing: border-box !important;
            pointer-events: none !important; z-index: 2147483647 !important;
            background: none !important; display: block !important;
          }
        }
        """;

    public static string ThemeDir(string profileDir) => Path.Combine(profileDir.TrimEnd('\\'), FolderName, "theme");
    public static string FrameDir(string profileDir) => Path.Combine(profileDir.TrimEnd('\\'), FolderName, "frame");
    public static string TabsDir(string profileDir) => Path.Combine(profileDir.TrimEnd('\\'), FolderName, "tabs");

    // Value of --load-extension (comma-separated folders).
    public static string LoadExtensionValue(string profileDir) =>
        ThemeDir(profileDir) + "," + FrameDir(profileDir) + "," + TabsDir(profileDir);

    // Writes the extension files when missing or different; best effort, Edge starts without the look on failure.
    public static void Ensure(string profileDir)
    {
        try
        {
            Write(Path.Combine(ThemeDir(profileDir), "manifest.json"), ThemeManifest);
            Write(Path.Combine(FrameDir(profileDir), "manifest.json"), FrameManifest);
            Write(Path.Combine(FrameDir(profileDir), "frame.css"), FrameCss);
            // LF line endings and a final newline: byte-equal to browser-agent's copy (both rewrite only on a difference).
            Write(Path.Combine(TabsDir(profileDir), "manifest.json"), TabsManifest.ReplaceLineEndings("\n") + "\n");
            Write(Path.Combine(TabsDir(profileDir), "co-tabs-sw.js"), TabsWorker.ReplaceLineEndings("\n") + "\n");
        }
        catch (Exception) { }
    }

    private static void Write(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
