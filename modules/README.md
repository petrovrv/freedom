# Modules (Freedom F10, ext/1)

A module is a folder with `module.json` (SPEC §3.6), its parts and `freedom-manifest.json` (signature). The core knows no
module by name (`release/check_core_names.py` keeps it so: module ids, ops, device tools, library tools and gateway
models from every `module.json` may not appear in core files beyond `release/core-names-baseline.json`).

Lifecycle: `install/freedom_modules.py` (check, sign, verify, plan, install, enable, disable, person, remove). Install
uploads the server part (standard modules only), registers the module on the server (`db.php` op `modules_host`) and
copies the folder to the home PC's local modules folder for Bridge (`AIWB_MODULES_DIR`, else
`%LOCALAPPDATA%\AIWorkbench\modules\<id>`; `~/.local/share/AIWorkbench/modules` on macOS/Linux).

## Bridge's module loader (bridge/BridgeCorporate.cs, to be renamed BridgeModules.cs)

Every minute the host owner's Bridge rescans the local folders and asks the server for the switches
(`modules_host` `list`: `enabled`, `people` = each member's personal switch). A module the server does not list is off;
offline, the last answer (`<modules>/.server-state.json`) stays. A module works for an environment when it is on and,
for a `personal` module, the person's switch is `auto`; a module of `scope: "environment"` without a personal switch
works for the owner's environment only. Jobs keep the modules they started with (event `modules` lists id and version).

| ext/1 point | manifest | what Bridge does |
|---|---|---|
| `service` | `provides.services[] {id, tool, run, windowsTask?}` | runs `python <tools>/<tool>/<tool>.py <run>` under its supervisor while the module is on: restart with backoff (5 s … 5 min), log `<modules>/.logs/<id>/<service>.log` (2 MB, one old copy), stop when the module goes off and when Bridge exits; a copy left by a crashed Bridge is ended by its pid file. `windowsTask` = the scheduled task that ran it before: Bridge ends and disables it when it takes over, and gives the job back to it (enable + run) after 3 quick failures in a row (exits or failed launches). A module that is off: its services are ended once, also a copy left by an earlier Bridge and its legacy task (end + disable), when the server's answer is known. The installer leaves services to Bridge while `<modules>/.bridge-supervisor.json` is younger than 5 min, otherwise it switches the task as before. |
| `service` schedule | `provides.services[] {id, kind: "schedule", cron \| every, tool \| cwd, run}` without `windowsTask` | Bridge runs it at its time (cron in local time, made up if up to 2 h late; `every` ISO-8601, at least a minute), one run at a time, never for the past on first sight; `tool` = a library tool, `cwd` = the module's own `home/` code with `run` = `python …`. Output in `<modules>/.logs/<id>/<service>.log`. A schedule that names a `windowsTask` stays with the Windows task scheduler (the installer switches it). |
| `device.tools` | `provides.deviceTools[] {name, risk: R/W/D, kind?}` | a call of a tool of a module that is off for the job's environment fails with `module_disabled` ("… belongs to the module '…', which is disabled …"), the tool is not listed; a `D` tool in a tainted chat fails with `module_risk`. Read-guard hooks and the device's approvals apply as to every device tool. |
| `models` | `provides.models[] {id, agent, title, models, defaultModel, card, slaveAllowed}` (permission `models.register`) | registered as `model_resources` `<owner env>/<module>-<id>` (`model_register`), unregistered when the module goes off. Gateway models (`kind: llm \| dispatcher`) are read by the server from the manifest instead. |
| `models` gateway | `provides.gateway {title, employerTitle, portalTitle, signinTitle, domains, errorPattern, usageSource, resumeText, blockedMarkers}` + a device tool of kind `llm` | the generic `Gateway`: runtime kind `gateway` (no CLI), blocked reason of a job (`gatewayBlocked`), sign-in pushes, dispatcher savings row, employer Outlook check under runAs. Installed but off = known, tools refused (the old "paused"). |
| `context` | `provides.context[] {id, file (.md in the module) \| text}` (permission `context`) | added to the prompt of a new session ("Rules of the workbench modules enabled for you"), at most 8000 characters per fragment. |
| `events` | `provides.hooks[] {event: job.before \| job.after, tool, run}` | the library tool gets `{event, jobId, env, chatId, projectId, agent, status}` on stdin; `job.before` (10 s) may answer `{"context": "…"}` (permission `context`), any hook `{"push": {title, body, url}}` (permission `push.send`, sent by the core to the job's environment); `job.after` runs in the background; a failing hook never fails the job. |
| `tools` | `provides.tools[] {name, minVersion}` | toolbox's module state (`toolbox.py module <id> --state …`) follows the switch, so the MCP server refuses the tools of a module that is off. |

New in ext/1 with this loader (SPEC §3.6 table to be updated): `provides.hooks`, `provides.gateway`, `risk` on device
tools, `run`/`tool` on services, `confidential` + `warning`, permission `models.register`.
