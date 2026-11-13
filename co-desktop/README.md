# CO Desktop 0.4.5

Windows app that gives the agents on the home Bridge controlled access to this computer.
Specification: `TZ-CO-DESKTOP-v1.2.md`. Needs BridgeAgent 1.0.3.

## Contents

| Part | State |
|---|---|
| WebView2 shell with CO Web (`window.coDesktop` exposes device id and short name) | done |
| Device keys in CNG (TPM when present), Credential Manager for the Drive token | done |
| Device folder in Drive (`drive.file`), pairing request, 6-digit code, Bridge key pinning | done |
| Owner screen: pending devices, code check, service account + key upload, device record, revoke | done |
| Tool Host: signature, 5-minute window, replay guard, risk classes, grants, taint, managed profile, audit hash chain | done |
| `fs_list`, `fs_stat`, `fs_read` (text, PDF, DOCX, XLSX, PPTX), `fs_search`, `fs_write`, `fs_edit`, `fs_mkdir`, `fs_move`, `fs_delete` with snapshots and undo | done |
| `approve` (Claude Code permission prompt shown on the device) | done |
| CO browser (Edge, own profile, DevTools): `browser_open`, `browser_read` | 0.3.0 |
| SharePoint Online without sync: `sharepoint_list`, `sharepoint_read`, `sharepoint_download` | 0.3.0 |
| Agent browser in its own CO browser tab `agent-web`: `browser_navigate`, `browser_back`, `browser_snapshot`, `browser_scroll`, `browser_status`, `browser_click`, `browser_type`, `browser_select`, `browser_press`, `browser_upload`; site list, confirmations, takeover, journal; off on managed devices | 0.8.0 |
| One agent browser: tab per job (`agent-web:<RunId>`, `agent:<RunId>` for `browser_open`/`browser_read`/SharePoint), closed after 30 min idle (not while taken over) and at start when older than 24 h; operations of different tabs run in parallel; blocked sites instead of an allowlist (`Block`, domain covers subdomains, also for fetch/XHR; local and private addresses always blocked; old `Allow` moved to `AllowLegacy`); `browser\co-browser.json` (port, pid, Edge command line) for browser-agent, its tabs in `browser\ba-tabs.json` are never used or closed | 0.9.0 |
| Module page window (ext/1 `ui.window`, 0.13.0; before: the canvas window of 0.9.2): a module `ui.page` declared with `"window": true` that CO Web opens in a new tab gets its own window; one window per value of `windowKey` (e.g. per file), position and size per page in `desktop.json` (`PageWindows`); the page may move the window to the https origins of the module's `navigate` list and to Google sign-in, other links and new tabs open in the system browser; × unloads the page first, a second × closes at once | 0.13.0 |
| Freedom F10 module loader (`CO.Core/Modules`, ext/1 `device.tools`, `docs/ext1-device-tools.md`): workbench modules from `db.php modules_list`, their device tools in the catalog with the declared risk class (script tools from sha256-checked `device/` files; a module not signed by the maintainers counts R/W/N as X), per-device switch (`modules-device.json`), `module_off` for removed tools, device ops of `ui.settings` pages (`window.coDesktop.moduleOp`), Connectors → Modules; CO Resident on the Mac loads the same | F10 |
| Self-update from `AI-Workbench\desktop-updates` through Bridge, Bridge-signed (TZ §6.5) | 0.4.0 |
| Pub/Sub channel (gRPC streaming pull, service account) and Drive slot fallback | done |
| Bridge: MCP proxy, call signing, device registry, IAM, Drive fallback, `runAs` | done (BridgeAgent 1.0.0) |
| CO Resident for macOS 0.1.0 (`src/CO.Mac`): Keychain, osascript confirmations, `shell_run` (every command confirmed), `sys_info`, `log_read`; install offer in CO Web 0.84.0 + `agent/mac.php`; `tools/publish_mac.py` | 0.1.0 |
| CO Web: device badge in the composer, `devices` field in job requests | done (CO Web 0.8.1) |
| Bridge doctor (S3, tab **Doctor**, only where the Bridge launcher is installed): every 5 min checks launcher/Bridge processes, `owner.json` heartbeat, the day log, launcher `state.json` and the exe of the current version, `drain.request`, Defender detections, `claimed/` and `inbox/`, the site, local `claude`/`codex`; works without sign-in, the site and Bridge. Automatic: starts the launcher (task `AIWB-BridgeAutostart`) when nothing runs on two rounds (≤ 3/day), graceful restart (`drain.request` "doctor …", Bridge waits for its jobs) when the heartbeat is older than 15 min (≤ 2/day). Buttons: rollback to the previous version (launcher stopped, `state.json` current=previous, the current one in `bad`; the idle Bridge is ended and the launcher started), cancel markers for jobs stuck > timeout+30 min, kill Bridge (ends running jobs, owner only). "Ask Claude/Codex" runs the local CLI read-only with the report. Events go to Health (`db.php host_event`, kinds `doctor_*`, push key from Credential Manager), queued while the site is down; state in `doctor.json` | 0.10.0 |

## Layout

```text
CO-Desktop/
  CO.Desktop.sln
  src/CO.Core/          protocol, crypto, policy, tools, transport, pairing (net8.0, testable on any OS)
  src/CO.Desktop/       WPF + WebView2 shell (net8.0-windows)
  src/CO.Mac/           CO Resident for macOS: CO.Core as a LaunchAgent (osx-arm64), see its README
  tests/CO.Core.Tests/  53 tests, including Bridge wire compatibility (bridge/DeviceWire.cs compiled in)
  publish.cmd
```

## Install (any Windows 10/11 computer)

1. Run `setup\CO-Desktop-Setup-<version>.exe`. No administrator rights: it installs to
   `%LOCALAPPDATA%\Programs\CO Desktop`, adds Start menu and desktop shortcuts and starts the app.
2. First start: choose who uses the computer and sign in with Google once. The sign-in is kept in
   Credential Manager (`CO.Desktop.Google`).
   - Owner: one consent for Drive, Cloud IAM and Pub/Sub. The computer pairs itself with Bridge
     and the CO window inside the app is signed in with the same token.
   - Other members: Drive for app files only. The app shows a 6-digit code; the owner opens **Owner** in their
     CO Desktop, selects the request, types the code and presses *Pair*. The CO window asks for one browser
     sign-in, which the server keeps for 400 days.
3. **Folders**: connect the folders the agent may use. The short name is optional and is adjusted to a-z, 0-9, '-', ≤ 12.
4. **Browser** (SharePoint without sync): *Open CO browser*, sign in to SharePoint once in that window. Then ask the
   agent, for example: "list https://<tenant>.sharepoint.com/sites/X/Shared Documents/Specs and summarize Plan.docx".

Updates: install once; later versions arrive by themselves. To release one, copy
`CO-Desktop-Setup-<x.y.z>.exe` into `<Drive>\AI-Workbench\desktop-updates`. Within about 10 minutes Bridge
uploads it into every paired device's folder; each CO Desktop checks every 30 minutes (or *Device → Check for updates
now*) and reinstalls itself when no agent call runs. Sign-in and pairing stay.

Uninstall: *Apps → CO Desktop → Uninstall*. `%LOCALAPPDATA%\CO` (keys, audit log, snapshots) stays; the Google
sign-in is removed with *Device → Sign out of Google*.

## One-time Google Cloud setup (per installation, see install/GOOGLE-CLOUD.md)

- OAuth consent screen: **In production** (Testing revokes refresh tokens after 7 days).
- Web client `…419vq…`: redirect URI `https://co.example.org/auth/callback.php`; its secret lives only in
  `~/co-auth/config.php` on the server.
- Desktop client `…emviqk…`: shipped as `oauth-client.json` inside the installer.
- IAM API enabled (CO Desktop tries to enable it through Service Usage when it is off).

## Build

```bat
dotnet test tests\CO.Core.Tests
dotnet publish src\CO.Desktop -c Release -o %LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish -p:DebugType=none
makensis -DVERSION=x.y.z -DAPPDIR=%LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish -DCLIENT=<desktop client json> installer\co-desktop.nsi
```

Build outputs stay outside Drive: `Directory.Build.props` sends `bin`/`obj` to `%LOCALAPPDATA%\AIWorkbenchBuild\co-desktop`.
The installer is written to `installer\` (ignored by git); move it out after the build.

## Bridge 1.0.3 setup

1. `build.cmd` (adds `BridgeDevices.cs`, `DeviceWire.cs`).
2. `config.json`: sections `devices` and `runAs` as in `config.json.example`.
3. Tray log shows `Device-call key fingerprint: …`; the same value is in `owner.json` → `deviceCallKey`.
4. `runAs` (TZ §9.7): create a local Windows account without Outlook, sign in once, log in Claude Code and
   Codex there, give it access to the project workspaces, then store its password:
   `cmdkey /generic:AIWorkbench.Bridge.RunAs /user:co-agent /pass:<password>` and set `runAs.user`.

## Test job

`sample-job-devices/request.json` (Bridge folder) is a Claude chat job for the device `d-…`:
replace `deviceId`, `jobId`, then commit it with `New-CommitMarker.ps1` as usual. Expected: the agent calls
`mcp__dev_<short>__fs_list`, the call shows up in the device **Activity** tab and in the job events.

## Local data

`%LOCALAPPDATA%\CO\`: `device.json`, `folders.json`, `grants.json` (permanent grants only), `replay.tsv`,
`audit\audit.jsonl` (hash chain), `snapshots\<runId>\`, `results\` (oversized results), `webview\`,
`co-desktop.log`, `browser\` (CO browser profile), `updates\` (downloaded installer), `agent-browser.json` (blocked sites `Block`, journal, confirm-clicks and confirm-forms switches; also read by browser-agent), `browser\co-tabs.json` (CO tab keys -> targetId, `created:<key>` times), `browser\co-browser.json` (DevTools port, Edge pid and command line for external clients), `agent-browser\` (`journal.jsonl` and screenshots per day), `doctor.json` (Bridge doctor: repairs, unsent events, a pending switch). Keys: CNG containers `CO.Desktop.Device.Signing`, `CO.Desktop.Device.ServiceAccount`.
