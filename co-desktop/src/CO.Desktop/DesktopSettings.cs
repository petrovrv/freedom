// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core;

namespace CO.Desktop;

public sealed class DesktopSettings
{
    // Freedom F1: first values come from the install.json built in (CO.Core InstallDefaults); desktop.json keeps them.
    public string WebUrl { get; set; } = InstallDefaults.SiteUrl.Length > 0 ? InstallDefaults.SiteUrl + "/" : "";
    // 0.7.5: width (DIP) of the device panel; changed by dragging the splitter, double-click resets to 380.
    public double PanelWidth { get; set; } = 380;
    // Freedom F10 (ext/1 ui.window): last position and size (DIP) of the window of a module page, by "<module>/<page>".
    public Dictionary<string, WindowBounds> PageWindows { get; set; } = new(StringComparer.Ordinal);
    public string HostId { get; set; } = "home-main";
    public string WorkbenchRootName { get; set; } = "AI-Workbench";
    public string OwnerEmail { get; set; } = InstallDefaults.OwnerEmail;
    public string EnvironmentId { get; set; } = InstallDefaults.OwnerEnv;
    // Google Cloud project of Bridge (Pub/Sub topics, IAM for device service accounts).
    public string PubSubProjectId { get; set; } = InstallDefaults.PubSubProjectId;
    // Desktop-app OAuth client installed next to CO.Desktop.exe; empty means "<install dir>\oauth-client.json".
    public string OAuthClientFile { get; set; } = "";
    // Folder id that receives oversized results (TZ §8.4); created on first start.
    public string ResultsFolderId { get; set; } = "co-results";
    // 0.8.3: owner's choice to switch the managed-device profile (TZ §9.6) off on a domain/Entra-joined computer;
    // read at start (Connectors -> Managed-device profile -> Apply and restart).
    public bool ManagedProfileOff { get; set; }
    // 0.15.0: the CO Desktop reserve executor (runs Claude Code while the Bridge of this computer takes no jobs) is off.
    public bool ReserveExecutorOff { get; set; }
    // Keys this version does not know (settings of older versions, e.g. the switch of a module part before Freedom F10),
    // kept on save so a module part can take its old choice over (App.cs, IConnectorModule.LegacySetting).
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Legacy { get; set; }
    // Environment -> Google account of its user (TZ-CO-WEB §5).
    public Dictionary<string, string> Members { get; set; } = InstallDefaults.Members;

    // 0.9.7: the owner's computer pairs these members' computers without the code when the device folder in
    // Drive is owned by the member's account above (CO.Core AutoPair). Empty list: every computer needs the code.
    public List<string> AutoPairEnvironments { get; set; } = InstallDefaults.MemberEnvs;

    // Freedom 0.4.0: the neutral installer has no client of its own; the setup wizard keeps the user's desktop client
    // next to install.json (freedom_setup.py google: oauth-client-desktop.json), the first-start window copies it there.
    public static string WizardOAuthClientFile =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(InstallDefaults.CandidateFiles().First())) ?? DataDir, "oauth-client-desktop.json");

    public string ResolveOAuthClientFile()
    {
        if (!string.IsNullOrWhiteSpace(OAuthClientFile)) return OAuthClientFile;
        string bundled = Path.Combine(AppContext.BaseDirectory, "oauth-client.json");
        return File.Exists(bundled) || !File.Exists(WizardOAuthClientFile) ? bundled : WizardOAuthClientFile;
    }

    /// <summary>A site address is set (the first-start window asks for it in a neutral build).</summary>
    public bool HasSite => InstallDefaults.IsSiteUrl(WebUrl);

    /// <summary>The site of this computer without the trailing slash ("" when not set).</summary>
    public string SiteBase => HasSite ? WebUrl.TrimEnd('/') : "";

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CO");
    public static string FilePath => Path.Combine(DataDir, "desktop.json");

    public static DesktopSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        DesktopSettings s = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new DesktopSettings()
            : new DesktopSettings();
        s.FillFromInstall();
        s.Save();   // writes new defaults back, so the file always shows every option
        return s;
    }

    // Freedom 0.4.0: CO Desktop installed before the setup wizard wrote install.json saved empty values; take the
    // install's values for what is still empty (values the user set are never replaced).
    public void FillFromInstall()
    {
        if (WebUrl.Length == 0 && InstallDefaults.SiteUrl.Length > 0) WebUrl = InstallDefaults.SiteUrl + "/";
        if (OwnerEmail.Length == 0) OwnerEmail = InstallDefaults.OwnerEmail;
        if (EnvironmentId.Length == 0) EnvironmentId = InstallDefaults.OwnerEnv;
        if (PubSubProjectId.Length == 0) PubSubProjectId = InstallDefaults.PubSubProjectId;
        if (Members.Count == 0 && InstallDefaults.Members.Count > 0)
        {
            Members = InstallDefaults.Members;
            if (AutoPairEnvironments.Count == 0) AutoPairEnvironments = InstallDefaults.MemberEnvs;
        }
    }

    public sealed class WindowBounds
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
