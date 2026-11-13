// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Modules;
using CO.Core.Tools;

namespace CO.Mac;

// Freedom F10 (SPEC §3.6, ext/1 device.tools): module calls of CO Web in the CO window of the Mac. The window is a
// process of its own next to CO Resident: it reads the module list CO Resident keeps (modules.json) and the switches of
// this Mac (modules-device.json), writes a switch there (CO Resident applies it on its next round, within 5 minutes)
// and answers device ops of module settings pages with the module's device files CO Resident downloaded and checked.
public static class MacModules
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // a yes for a module's own code holds for that module version until the window app restarts
    private static readonly HashSet<string> Granted = new(StringComparer.Ordinal);

    /// <summary>Runs a module call on a worker; done gets the JSON reply {id, result} or {id, error}.</summary>
    public static void Start(string type, JsonElement args, int id, Action<string> done) => _ = Task.Run(async () =>
    {
        object r;
        try { r = new { id, result = await CallAsync(type, args) }; }
        catch (Exception ex) { r = new { id, error = ex.Message }; }
        done(JsonSerializer.Serialize(r));
    });

    public static async Task<object?> CallAsync(string type, JsonElement args)
    {
        await Gate.WaitAsync();
        try
        {
            var loader = new ModuleLoader(new ToolCatalog(), Paths.DataDir, null, log: Log.Write)
            {
                Managed = ManagedDevice,
                // a module not signed by the maintainers runs its own script only after a yes of the person at this Mac
                ConfirmScriptOp = (m, op, ct) => Task.Run(() =>
                {
                    string key = m.Id + "@" + m.Version;
                    lock (Granted) if (Granted.Contains(key)) return true;
                    bool yes = Dialog.Ask(
                        $"The module {m.Title} {m.Version} (not signed by the Freedom maintainers) wants to run its own code on this Mac for '{op}'.",
                        "CO", new[] { "Deny", "Allow" }, "Deny", 120, caution: true) == "Allow";
                    if (yes) lock (Granted) Granted.Add(key);
                    return yes;
                }, ct),
            };
            loader.LoadCached();
            switch (type)
            {
                case "moduleDevice":
                    return loader.States().Select(m => new { id = m.Id, on = m.On, active = m.Active, tools = m.Tools, problem = m.Problem, warning = m.Warning }).ToList();
                case "moduleDeviceSet":
                    await loader.SetOnAsync(Str(args, "module"), args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True, CancellationToken.None);
                    return new { };
                case "moduleOp":
                    var a = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var x) ? x : JsonDocument.Parse("{}").RootElement;
                    JsonNode? res = await loader.DeviceOpAsync(Str(args, "module"), Str(args, "op"), a, CancellationToken.None);
                    return res == null ? null : JsonDocument.Parse(res.ToJsonString()).RootElement;
                default:
                    throw new InvalidOperationException("unknown_request");
            }
        }
        finally { Gate.Release(); }
    }

    // device.json of CO Resident (read only here): a managed device refuses module scripts
    private static bool ManagedDevice()
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.DataDir, "device.json")))?["managed"]?.GetValue<bool>() == true; }
        catch (Exception) { return true; }   // unknown = the safe answer
    }

    private static string Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
