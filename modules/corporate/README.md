# Module corporate (employer assistants)

Second probe of the Freedom module format (SPEC §3.6, §3.7, stage F10). Before F10 the core knew a "corporate
connector" from `connectors/corporate/manifest.json` (now `connector-manifest.json` here; server `corporate-lib.php`, CO Web `corporate.ts` embedded at
build, Bridge `BridgeCorporate.cs`). Now the description comes from this module and the core only knows a generic
**model gateway** (ext/1 `models` + `device.tools`): a confidential model reached through a device tool of kind `llm`,
optionally with a dispatcher agent that delegates writing to it.

**Off by default.** Enabling it shows the warning of `module.json` ("Employer data: you decide ..."). Installed but
disabled = the old `paused: true` of the connector manifest: its agents are known but not offered, chats addressed to them
go to another agent, its device tools fail with "module disabled". This is the owner's state since 27.10.2026.

| Layer | Here | Core extension point |
|---|---|---|
| models | `provides.models` `engie` (kind `llm`) and `dispatcher` (kind `dispatcher`), confidential | server builds the gateway description from the `modules` table (`modules_list.gateways`); Bridge `Gateway` from the local module folder |
| device.tools | `provides.deviceTools` with risk class R/W/D (Outlook, calendar, Teams, SharePoint, portal, SAP, the gateway `engie_chat`/`engie_fetch`) | Bridge refuses them while the module is off for the job's environment (`module_disabled`); a `D` tool in a chat that read untrusted text waits for a clean chat (`module_risk`); read-guard hooks and CO Desktop confirmations as for every device tool |
| gateway | `provides.gateway`: titles, employer mail domains, error pattern, savings source, resume text, blocked markers | push texts (sign-in, number), Outlook profile check under runAs, blocked reason of a job, dispatcher savings row |
| context | `context/rules.md` (confidential data stays in the environment) | Bridge adds it to a new session of the environments the module works for (scope `environment`: the owner) |
| events | resume of chats the gateway blocked (server part, `server/`) | module user ops, loaded only while enabled and standard-signed |
| tools | `sap-hr` | toolbox module state (refused while the module is off) |

The rule "confidential data never goes into shared chats or to other members" stays in the core as a generic rule
(confidential models, devices and context folders are refused in shared chats).

## Install (the owner's workbench, keeps today's state)

    python install/freedom_modules.py install modules/corporate

No `--enable`: the module stays off (paused). The installer uploads the server part, registers the module, copies it to
`%LOCALAPPDATA%\AIWorkbench\modules\corporate` for Bridge and switches `sap-hr` off in toolbox. Turning it on later:
CO Web → Host → Modules, or `freedom_modules.py enable corporate` — never call the employer's services while it is off.

## Desktop part (CO Desktop)

`desktop/` (project `CO.Corporate`, tests in `tests/CO.Corporate.Tests`) is compiled into CO Desktop by the generic
`co-desktop/ModuleParts.props` (modules/EXT1.md, *Device parts compiled into CO Desktop*); the core names neither the
project nor the module.

- `IConnectorModule` `CO.Corporate.CorporateModule` (id `corporate`) is registered when the module is installed, signed
  by the maintainers, enabled and on for the device (CO Desktop → Connectors → Modules); turning it on or off applies
  after a restart of CO Desktop. Per device it is off by default (`device.default: "off"`, with the warning of
  `device.warning`); the explicit choice of the older desktop setting `CorporateConnectors` (CO Desktop before 0.13.0)
  becomes the device switch once (`LegacySetting`).
- Its tools are the `provides.deviceTools` of `module.json` (names and risk class; the device's own approval rules apply).
- Heartbeat: keeps sending `corp {id, state, num}` (wire name; Bridge reads the sign-in state and number from it).
- Settings panels (`SettingsPanels`, WPF) become `ui.settings` pages of this module later (SPEC §3.6, decision 0.9.1).
- `connector-manifest.json` is the Freedom F1 connector manifest (`install/aiwb_install.py corporate-manifest`), read by
  Bridge only while the module is not installed on that PC.
