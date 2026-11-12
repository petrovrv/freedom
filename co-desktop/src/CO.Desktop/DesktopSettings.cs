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
    // 0.9.2 (canvas M6): last position and size of a canvas window (DIP); null until one is closed.
    public WindowBounds? CanvasWindow { get; set; }
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
    // Freedom F1: corporate connectors (connectors/corporate: mail and calendar, document sites by link, team chats, portals,
    // external AI through the CO browser). Off unless install.json turns them on; read at start (Connectors tab ->
    // Corporate connectors -> Apply and restart). A desktop.json without the key gets the install default.
    public bool CorporateConnectors { get; set; } = InstallDefaults.CorporateConnectors;
    // Environment -> Google account of its user (TZ-CO-WEB §5).
    public Dictionary<string, string> Members { get; set; } = InstallDefaults.Members;

    // 0.9.7: the owner's computer pairs these members' computers without the code when the device folder in
    // Drive is owned by the member's account above (CO.Core AutoPair). Empty list: every computer needs the code.
    public List<string> AutoPairEnvironments { get; set; } = InstallDefaults.MemberEnvs;

    public string ResolveOAuthClientFile() =>
        string.IsNullOrWhiteSpace(OAuthClientFile) ? Path.Combine(AppContext.BaseDirectory, "oauth-client.json") : OAuthClientFile;

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CO");
    public static string FilePath => Path.Combine(DataDir, "desktop.json");

    public static DesktopSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        DesktopSettings s = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new DesktopSettings()
            : new DesktopSettings();
        s.Save();   // writes new defaults back, so the file always shows every option
        return s;
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
