# ext/1 `device.tools` and device ops (Freedom F10, SPEC §3.6)

How a module brings tools to the devices of a workbench (CO Desktop on Windows, CO Resident on the Mac) and how its
settings page acts on the device it is shown on. The core knows no module by name: everything below comes from the
module's `module.json` through `db.php modules_list`.

## Manifest

```jsonc
{
  "provides": {
    "ui": [{ "slot": "ui.settings", "id": "settings", "entry": "ui/settings.html" }],
    "deviceTools": [
      // script tool: device/<file> of the module, run on the device
      { "name": "mymod_scan", "class": "R", "description": "…", "input": { "type": "object", "properties": {} },
        "run": "device/scan.py", "platforms": ["windows", "macos"], "untrusted": true },
      // native tool: no "run"; served by the part of the module compiled into the app (IModuleNativePart, same id)
      { "name": "mymod_open", "class": "N" }
    ],
    "deviceOps": ["mymod_folder_add", "mymod_status"]
  },
  "device": { "default": "on", "warning": "Shown before the module is switched on on a device.", "ops": "device/ops.py" }
}
```

- All of it needs the permission `device.tools` (the owner agrees to it when enabling the module); without it the
  server lists no device part and the device loads none.
- `name` `[a-z][a-z0-9_]{2,63}`, unique; a name the core already uses is skipped on the device.
- `class` (also accepted as `risk`) one of the core risk classes `R W X N E D` (TZ-CO-DESKTOP §9). The call goes through the device policy like a
  built-in tool of that class (confirmations, grants, taint, managed profile). A **script** tool of a module that is not
  standard (not signed by the Freedom maintainers) counts a declared R, W or N as **X**: it runs the author's code.
- `untrusted` (default true): the output taints the run and is wrapped as data; script tools of non-standard modules
  are always untrusted. A compiled native part is bound only to a standard module with its id.
- `platforms`: `windows`, `macos`, `linux`; empty = all.
- `device.default`: `on` (default) or `off` — the per-device switch before the person chose. `warning` is shown when
  the person switches it on (Connectors → Modules on CO Desktop, Host → Modules in CO Web inside the app).
- `device.ops`: the device/ file that answers device ops not handled by a native part.

`install/freedom_modules.py check` validates all of it.

## Device side

- The device reads `modules_list` with its own Google token (every 5 minutes; the last answer is kept in
  `modules.json`, so tools are there at start without the site). A module is **active** on a device when it is enabled
  in the workbench and its device switch (`modules-device.json`) is on.
- `device/` files are downloaded from `agent/module.php?m=<id>&f=device/<file>` and checked against the sha256 of
  the list; the check repeats right before every run, a changed file is never run.
- Tools of active modules are added to the device catalog and published to Bridge (pairing-request.json, Bridge reads
  it every 2 minutes). A tool of a module switched off leaves the catalog; a call then fails with `module_off`.
- Script protocol: the file is started by extension (`.py` python -I, `.js` node, `.ps1` PowerShell, `.sh` sh,
  `.cmd/.bat` cmd, else as an executable) in the module's state folder (`<data>/modules/<id>/state`, kept over versions
  and switches), environment `AIWB_MODULE_ID`, `AIWB_MODULE_STATE`, `AIWB_EXT=1`. Stdin is one JSON object:
  `{"ext":1,"kind":"tool","tool","args","runId","module","version","stateDir","platform"}` (tools) or
  `{"ext":1,"kind":"op","op","args",…}` (device ops). Stdout is the answer (tools: text for the model; ops: JSON),
  capped at 1 MB; exit code ≠ 0 fails the call with the end of stderr.

## Device ops from a settings page

A module's `ui.settings` page or its app (`ui.page` with `app: true`, e.g. a device app that starts a program)
calls `coExt.call(op, args)` as for its server ops (other slots cannot call device ops). CO Web (`extBridge.callRoute`) sends an op from
`provides.deviceOps` to the app it runs in (`window.coDesktop.moduleOp(module, op, args)` on CO Desktop and in the CO
window of the Mac); in a plain browser the call fails with `device_op_unavailable`. The app checks again: module
enabled, op declared, module on on this device; then the native part answers or the `device.ops` file runs. A `device.ops` script runs the module's own code: it is refused on a managed device, and a module not
signed by the maintainers runs it only after a yes of the person at the device (held for that module version until
the app restarts).

`ui.settings` pages are shown in CO Web Host → Modules under their module (the same iframe sandbox and MessageChannel
as every slot). CO Desktop's Connectors tab lists the modules with their switch and opens their settings there.
