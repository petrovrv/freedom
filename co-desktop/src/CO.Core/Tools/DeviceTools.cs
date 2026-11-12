// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using CO.Core.Policy;
using CO.Core.Runtime;

namespace CO.Core.Tools;

// Device self-management tools that need the DeviceRuntime (registered in App.cs with the live runtime).
public static class DeviceTools
{
    public static IEnumerable<IDeviceTool> All(DeviceRuntime runtime) => new IDeviceTool[] { new DeviceUpdate(runtime) };
}

// Lets the agent force the self-update now instead of waiting for the ~30-minute timer (or a manual
// "Check for updates now" click). It only forces the CHECK — the device still installs just the Bridge-signed
// release offered in its folder (DeviceUpdater verifies the signature), and still waits for no agent call to be
// running before the silent reinstall. If nothing newer is offered yet, it reports that and changes nothing.
internal sealed class DeviceUpdate : IDeviceTool
{
    private readonly DeviceRuntime _rt;
    public DeviceUpdate(DeviceRuntime rt) => _rt = rt;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "device_update", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description =
            "Force CO Desktop to check for a new Bridge-signed release now (instead of waiting for the ~30-minute " +
            "timer) and install it when no agent call is running. Only installs the signature-verified release Bridge " +
            "has offered; if none is newer, nothing changes. The device restarts into the new version shortly after " +
            "this call returns, so expect a brief disconnect. Returns the current version and the updater status.",
        InputSchema = Schema.Obj(),
    };

    public ActionPlan Plan(JsonElement args, ToolEnv env) =>
        new() { Tool = "device_update", Class = RiskClass.R, Summary = "force update check now" };

    public async Task<ToolOutput> ExecuteAsync(JsonElement args, ToolEnv env, CancellationToken ct)
    {
        await _rt.CheckForUpdateAsync(ct);
        return new ToolOutput { Content = $"Current version {DeviceRuntime.Version}.\nUpdate check: {_rt.Updater.Status}" };
    }
}
