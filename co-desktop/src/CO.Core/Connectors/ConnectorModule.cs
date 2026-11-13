// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Auth;
using CO.Core.Browser;
using CO.Core.Files;
using CO.Core.Host;
using CO.Core.Tools;

namespace CO.Core.Connectors;

// Freedom F1 (SPEC §3): connectors that are not part of the core (corporate ones live in connectors/corporate) come as
// a module. The app registers a module only when the user turned it on; Register adds its connectors, tools, remote
// folders and hooks, Start begins its background work (kept-alive sessions).
// Freedom F10 (SPEC §3.6, §11.18): a compiled connector module is also the native part of the registry module with the
// same id (Modules.IModuleNativePart: native device tools, device ops of its ui.settings page). New modules declare
// their device tools in module.json and their settings as a ui.settings page; SettingsPanels stays only for the
// connector modules compiled before that (shown under "Older connector settings").
public interface IConnectorModule : Modules.IModuleNativePart
{
    void Register(ConnectorModuleContext ctx);
    void Start(CancellationToken ct);

    // Extra settings panels for the Connectors tab (WPF elements on Windows); built anew on each call.
    // Transitional (F10): a module with a ui.settings page does not need it.
    IEnumerable<object> SettingsPanels() => Array.Empty<object>();
}

// What the app hands to a module.
public sealed class ConnectorModuleContext
{
    public required ToolCatalog Catalog { get; init; }
    public required ConnectorRegistry Connectors { get; init; }
    public required FolderRegistry Folders { get; init; }
    public required Func<string, IBrowser> Tab { get; init; }   // background tab of the CO browser by key (one per service)
    public required BrowserSource JobTab { get; init; }         // this job's own tab ("agent:<RunId>")
    public required IBrowser Browser { get; init; }             // the CO browser (current page, used to resolve links)
    public required ISecretStore Secrets { get; init; }
    public required string DataDir { get; init; }              // settings files of the module (one per service)
    public Action<string> Log { get; init; } = _ => { };
}

// Hooks the core reads and a module fills; empty when no module is on.
public static class ConnectorHooks
{
    // Session of a browser-based AI connector for the heartbeat (Bridge, CO Web): online|signin|offline|unknown|off;
    // "" = no such connector. Number: a sign-in number challenge "<id>:<NN>" waiting for the user's phone, else "".
    public static Func<string> HeartbeatSession { get; set; } = () => "";
    public static Func<string> HeartbeatNumber { get; set; } = () => "";
    // Freedom F1 5b: id of that connector (manifest id of the corporate connector) for heartbeat corp.id.
    public static Func<string> HeartbeatConnectorId { get; set; } = () => "";

    // Extra fields of the CO Web "status" answer and extra methods of window.coDesktop (name -> call).
    public static Dictionary<string, Func<object?>> WebStatus { get; } = new();
    public static Dictionary<string, Func<CancellationToken, Task<object?>>> WebMethods { get; } = new();

    // A notice for the user (tray balloon on Windows); null until the window is up.
    public static Action<UserNotice>? Notice { get; set; }
}

public sealed record UserNotice(string Title, string Text, bool Warning, int Seconds);
