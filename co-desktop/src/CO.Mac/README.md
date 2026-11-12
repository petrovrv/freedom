# CO Resident for macOS 0.3.0

CO Desktop without a window for Apple Silicon Macs (macOS 14+): `CO.Core` as a LaunchAgent. The Mac becomes a
device of the home Bridge like a Windows computer with CO Desktop (pairing, signed calls, policy, audit, `fs_*`).
Specification and stages R0–R4: chat CHAT-20261107162301-RBC3, `scratch/SPEC.md`.

## What it has (R0 + R1 + R2 with Mac check + R3 + R4 window)

| Part | macOS implementation |
|---|---|
| Google sign-in | system browser, loopback (`InstalledAppAuth`), refresh token in the login Keychain (`security`, service `org.freedom-workbench.co.resident`, account `google`) |
| Device keys | P-256 and RSA 2048 as PKCS#8 in the Keychain (accounts `device-signing`, `device-sa`); Secure Enclave needs a signed app (R4) |
| Confirmations | `osascript display dialog`, one at a time, no answer in 2 min = Decline |
| Folders | `~/CO` (id `co`, read/create/edit) — not under `~/Documents`, so no privacy prompt; results in `~/Library/Application Support/CO/results` |
| `shell_run` | `zsh -c`, **every** call confirmed at the Mac (Run / Decline, no "for this task"), sudo/su refused, capped by the call timeout, `co-resident shell off` switches it off |
| `sys_info`, `log_read` | read-only diagnostics (macOS, chip, disk, browsers, Drive; own log or `log show`) |
| Updates (R2, 0.1.2+) | own channel, not the Windows one (`DeviceRuntime.AutoUpdate = false`): the resident creates `resident-update.json` in its device folder, Bridge 1.9.1+ writes there a manifest of the newest archive signed with the pinned Bridge key (domain `co.resident.update.v1`); every 30 min the resident checks it, downloads the archive from `mac.php?op=update` with its Google token, checks size/sha256, unpacks into `<app>.new`, signs ad hoc, runs `version`, and when no call is running copies the current files to `*.old` and renames the new ones over them (atomic, the LaunchAgent path is never empty), then exits (launchd starts the new copy). The new copy deletes `*.old` once it serves calls; if it does not get there in 3 starts, it puts the old files back and never takes that version again (`update-pending.json`, `update-rejected.txt`) |
| Mac check (R2, 0.3.2+) | `mac_check` tool: CO.Core.Tests of this exact release on the Mac. `publish_mac.py` publishes `tests/CO.MacCheck` (the tests + an in-process xunit runner, self-contained osx-arm64) as `co-mac-check-<version>-osx-arm64.tar.gz` (reproducible bytes), uploads it next to the release and compiles its sha256 into the resident (`-p:MacCheckSha256`, assembly metadata); the resident runs only a bundle with that hash (trust = its own signed update, the site only stores it). `action=start` downloads it from `mac.php?op=check&v=<version>` with the Google token, unpacks into `~/Library/Caches/CO/mac-check/<version>` (regular files only, no paths outside), signs the runner and dylibs ad hoc and starts it detached with `HOME`/`TMPDIR` in a throwaway `run-*` folder (20 min limit); returns at once (a device call has 120 s). `action=status` reads `state.json` and the runner's `result.json` (total/passed/failed/skipped, failures). On Windows the same runner gave 221/222 (the old symlink test without the privilege) in ~140 s <br>`live=true` (0.3.7) also runs the live browser tests (`CO_LIVE_BROWSER=1`, `CO_LIVE_BROWSER_EXE` = the agent browser's Chromium) headless in temp profiles that are stopped after each test. Owner's rule 10.11: live browser tests run on the device that needs them, on the owner's home PC only after his yes (read-guard 0.4.0 holds the command) |
| Agent browser (R3, 0.2.0+) | the CO Desktop agent browser tools (`browser_navigate`, `browser_snapshot`, `browser_click`, `browser_type`, …; one tab per job, closed after 30 min idle, blocked sites, forms/purchases confirmed in an `osascript` dialog) on a Chromium with its own profile `~/Library/Application Support/CO/browser` (no sign-ins of the Mac's user), DevTools on 127.0.0.1 only. Chromium: Chrome for Testing in `…/CO/browser-app` (downloaded on first use from Google's last-known-good index, unpacked with `ditto`, version checked, ~170 MB, no admin password), else Google Chrome / Microsoft Edge from `/Applications` with that profile. `co-resident browser on\|read\|off` (default on; `read` = open and read pages only); `sys_info` shows the state and install progress |
| CO window (R4, 0.3.0+) | `MacApp.cs`, AppKit/WebKit through the Objective-C runtime (`ObjC.cs`, no UI framework, builds on Windows). Menu-bar icon (status, "Agents may run commands" = shell on/off, agent browser on/read/off, open ~/CO, log, quit the menu) and CO Web in a WKWebView window (Edit menu for copy/paste, file upload panel, downloads to ~/Downloads, alert/confirm/prompt, microphone for the own site only, `/canvas` and other CO pages opened by `window.open` in their own windows, other links in the default browser, Safari user agent for the Google sign-in). `window.coDesktop` (`app: "co-mac"`) answers only `status`, so CO Web attaches this Mac's device to messages sent from the window; sign-in is the browser session (`/auth/login.php`), no desktop token. Separate process `co-resident app` from `~/Applications/CO.app` (a copy of the binary with Info.plist, `LSUIElement`, icon, ad hoc signature), LaunchAgent `org.freedom-workbench.co.app` (at login, restarted after a crash, off for the session after the 5th start in 10 min). The resident writes/refreshes CO.app and that LaunchAgent on start when its version is new; `co-resident window on\|off`; uninstall removes both |
| Exit diagnostics (0.1.2+) | every start logs how launchd saw the previous process end (`previous run: … launchd: last exit code …`); SIGTERM/SIGHUP/SIGINT/SIGQUIT and unhandled exceptions are logged; `log_read source=out` reads `resident.out` (runtime crashes), `log_read source=system start=<UTC>` reads a past hour of the unified log (up to 7 days back) |

Data: `~/Library/Application Support/CO` (device.json, folders, grants, audit), log `~/Library/Logs/CO/resident.log`,
LaunchAgent `~/Library/LaunchAgents/org.freedom-workbench.co.resident.plist`, program `~/Applications/CO Resident/`; window:
`~/Applications/CO.app`, `~/Library/LaunchAgents/org.freedom-workbench.co.app.plist`, `~/Library/Logs/CO/app.out`.

## Commands

```
co-resident setup --env boris   # from the installer: sign-in, pairing request, LaunchAgent, waits for the connection
co-resident status                  # account, paired, pairing code, folders, shell, agent state
co-resident folders add ~/Projects [id] [--read-only] | rm <id> | list
co-resident shell off | on
co-resident browser on | read | off   # agent browser: act / read pages only / off
co-resident window on | off         # the CO menu-bar icon and window (~/Applications/CO.app), at login
co-resident signin                  # Google sign-in again
co-resident uninstall [--purge]
```

## Build and publish (Windows)

`python co-desktop\tools\publish_mac.py` — `dotnet publish` for osx-arm64 (self-contained single file, built on
Windows, unsigned), archive with `oauth-client.json` from the installed CO Desktop, upload to `~/co-desktop/mac` on
the site. `--dry-run` only builds the archive. The installer signs the binary ad hoc on the Mac (`codesign -s -`),
which Apple Silicon requires; downloads by `curl` carry no quarantine flag, so Gatekeeper does not block it.

Install: CO Web shows household members on a Mac an offer (`MacOffer.tsx`); after the consent box it asks
`agent/mac.php` for a one-time command `curl -fsSL '…/agent/mac.php?op=install&t=…' | bash` (30 min, one download).
Pairing: no code for members in `AutoPairEnvironments` (the household members of `members`) — the owner's CO Desktop (0.9.7+) pairs the
Mac by itself when Google reports the device folder `CO-Device-<id>` as owned by the member's account. The resident
shows the 6-digit code only if it is not paired 15 minutes after Bridge saw the request (then CO Desktop → Owner, as before).

## Not checked yet

Built and unit-tested on Windows only; the first real run is the first install on a Mac. To watch: the
`osascript` dialog from a LaunchAgent (activation), Keychain access prompts, Pub/Sub over gRPC on macOS.
R4 window: not run on a Mac yet. Watch whether Google accepts the sign-in in WKWebView (Safari user agent), the
Objective-C calls (`window app:` lines in resident.log, crashes in app.out), downloads and the upload panel. The ad hoc
signature changes with every version, so macOS asks again for the microphone (and ~/Downloads) after each update.
Not done (needs an Apple Developer account, $99/year): Developer ID signature and notarization, so no .dmg (a
browser download would be quarantined), and no Secure Enclave key (needs a provisioning profile).
