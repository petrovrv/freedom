// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using CO.Core.Connectors;
using CO.Core.Modules;

namespace CO.Desktop;

// Freedom F10 (SPEC §3.6): desktop parts of modules compiled into this app. co-desktop/ModuleParts.props references every
// modules/<id>/desktop/<Name>.csproj of the tree and lists the assembly names in the assembly metadata "CO.ModulePart";
// here each public IConnectorModule type of those assemblies is created. The core names none of them: a tree without
// module desktop parts builds and runs the core alone. A part is bound to the registry module with its id (ModuleLoader
// binds a compiled part only to a module signed by the maintainers) and registered only while that module is enabled in
// the workbench and on for this device.
internal static class ModuleParts
{
    public const string MetadataKey = "CO.ModulePart";

    public static List<IConnectorModule> Create(Action<string> log)
    {
        var list = new List<IConnectorModule>();
        foreach (var a in typeof(ModuleParts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Where(a => a.Key == MetadataKey))
        {
            if (string.IsNullOrWhiteSpace(a.Value)) continue;
            try
            {
                var asm = Assembly.Load(new AssemblyName(a.Value));
                foreach (var t in asm.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }
                             && typeof(IConnectorModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    if (Activator.CreateInstance(t) is not IConnectorModule m) continue;
                    if (!ModuleList.ValidId(m.Id) || list.Any(x => x.Id == m.Id)) { log($"module part {t.FullName}: bad or repeated id '{m.Id}'"); continue; }
                    list.Add(m);
                }
            }
            catch (Exception ex) { log($"module part {a.Value}: {ex.Message}"); }
        }
        if (list.Count > 0) log("module parts compiled in: " + string.Join(", ", list.Select(m => m.Id)));
        return list;
    }

    /// <summary>Registers and starts the parts whose module is active on this device now (the module list kept in
    /// modules.json); returns them. A part whose module is switched on or off later asks for a restart.</summary>
    public static List<IConnectorModule> RegisterActive(IReadOnlyList<IConnectorModule> parts, ModuleLoader loader,
        ConnectorModuleContext ctx, Action<string> log)
    {
        var on = new List<IConnectorModule>();
        foreach (var m in parts.Where(p => Active(loader, p.Id)))
        {
            try
            {
                m.Register(ctx);
                m.Start(CancellationToken.None);
                on.Add(m);
                log($"module part '{m.Id}' on");
            }
            catch (Exception ex) { log($"module part '{m.Id}': {ex.Message}"); }
        }
        var noticed = new HashSet<string>(StringComparer.Ordinal);
        loader.Applied += () =>
        {
            lock (noticed)
            foreach (var m in parts)
            {
                bool want = Active(loader, m.Id), have = on.Contains(m);
                if (want == have) { noticed.Remove(m.Id); continue; }
                if (!noticed.Add(m.Id)) continue;
                string title = loader.Modules.FirstOrDefault(x => x.Id == m.Id)?.Title ?? m.Id;
                log($"module part '{m.Id}': {(want ? "on" : "off")} in the module list; applies after a restart");
                ConnectorHooks.Notice?.Invoke(new UserNotice("CO Desktop",
                    $"{title} was turned {(want ? "on" : "off")}. Restart CO Desktop to {(want ? "load" : "unload")} it on this computer.", false, 10));
            }
        };
        return on;
    }

    // a compiled part belongs only to a module signed by the maintainers (another author cannot borrow its id)
    private static bool Active(ModuleLoader loader, string id) => loader.IsActive(id) && loader.Modules.Any(x => x.Id == id && x.Standard);
}
