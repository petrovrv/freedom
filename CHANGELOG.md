# Changelog

Changes of all components (Bridge, CO Web, server, CO Desktop / CO Resident, installer, modules, core tools); each
line names the component and its version. How releases are made: `docs/DEVELOPMENT.md`.

## Unreleased

## 0.4.0 (2026-11-13)

- Release checks: export rewrites that find nothing count as done (the core tree is public since the reversed
  direction); core-name count back to the baseline.
- **Neutral installers** (release page = the only link a new user needs, `install/PILOT-FRIEND.md`):
  - CO Desktop 0.14.0: a build without install.json reads the install.json of the setup wizard on this computer
    (`AIWB_INSTALL`, else `%LOCALAPPDATA%\AIWorkbench\install.json`), then `install.json` next to the app; with none
    of them a first-start window asks for the user's site, the owner's Google account and the desktop OAuth client
    (copied to `oauth-client-desktop.json` next to install.json); values left empty by an earlier start are filled from
    install.json later; the update download address is the site of this computer's settings. Builds with install.json
    (a maintainer's own) behave as before. The installer takes the OAuth client only when built with `-DCLIENT`.
  - `release/build_installers.py`: neutral CO Desktop installer (own build root, nothing embedded, fails on private
    words in the own assemblies), source tarball, notes draft; no Authenticode signature (the manifest signature
    is the check).
  - Bridge 1.18.1: `bridge/install.ps1` on a new PC makes `config.json` from install.json (`aiwb_install.py bridge-config`),
    creates a release key of this PC, builds and signs Bridge from the sources into a local channel and registers the
    logon task; existing installs keep their channel and task. `release.ps1` works in a tree without git. Config
    `desktopUpdatesDir` (CO Desktop / CO Resident releases offered to devices; default
    `%LOCALAPPDATA%\AIWorkbenchBridge\desktop-updates`, the folder of earlier installs where it exists).
  - Setup wizard: steps `bridge`, `desktop [--installer]` (checks the installer against the signed manifest, installs
    it), `desktop-update --installer` (a release's CO Desktop to the user's site and Bridge for all computers);
    preflight checks `cryptography`; `verify` runs aiwb-smoke / aiwb-e2e of the release's `tools/`.
- Guard: UTF-16 text files (with a byte order mark) are scanned like other text; the exported task XML
  `co-desktop/tools/AIWB-BridgeAutostart.xml` (unused, it named a computer and an account) is removed.
- Bridge 1.18.0: each person has their own keyring. Jobs get `AIWB_JOB_ENV` and `AIWB_JOB_ROLE` (`owner`, or
  `member` when the job's environment is known and differs from the owner's). A member's job also gets
  `PYTHON_KEYRING_BACKEND=aiwb_secret.MemberRefusedKeyring` (tool `secret-prompt` on `PYTHONPATH`): every python
  keyring call of any tool in that job is refused with `own_device_first` and a pointer to the device tool
  `secret_prompt` of the person's own device; `member_site\sitecustomize.py` on `PYTHONPATH` selects it again where
  Codex drops `*KEY*` variables. A member's read-guard PreToolUse hook also covers Grep and Glob (the owner's: as in 1.17).
  The owner's jobs: unchanged. Limit: the job still runs under the owner's Windows account (a barrier, not
  isolation; next step in the SPEC: own Windows account per household member).
- secret-prompt 0.4.0 (core tool): `aiwb_secret.job_role()` / `guard()` / `MemberRefused` decide in one place;
  `get_secret`/`set_secret` and the prompt window (exit 5) refuse a member's job.
- read-guard 0.5.0 (core tool): a member's job is denied shell, file and MCP calls that reach the owner's credential
  store (keyring, cmdkey, Cred*, vaultcmd, PasswordVault, win32cred, secret-tool, security, Credentials/Vault
  folders, role variables); no grant opens it.
- wsl-family-sandbox 0.2.0 (core tool): `aiwb-secret` - each sandbox user's own secret store (0700/0600 in the
  user's real home, set/get/has/list/delete/exec, value never from argv), also as a python keyring backend; `check`
  verifies that a sister's secrets are unreadable.
- CO Resident 0.5.3 (CO.Core, also in the next CO Desktop): one-time codes from chat in the agent browser - setting
  `AllowOtpFromChat` (`agent-browser.json`; Mac menu Agent browser -> "Agent may type one-time codes from chat",
  `co-resident browser otp on|off|default`, CO Desktop checkbox in Connectors -> agent browser). On: a one-time-code page
  does not pause the tab, it is marked otp-pending and the tool result tells the agent to ask the user in chat for the
  code and `browser_type` it into the code field's ref; `browser_type` types a code-like text (3-12 letters/digits)
  only into fields protected solely as one-time-code fields, never into password, card, CVC, IBAN, PIN, secret or token
  fields; payment pages still pause (also a code page within 30 min of a purchase click in the tab or with card/bank
  confirmation text, 3-D Secure), captcha unchanged; the code is masked in snapshots and confirmations and journal
  screenshots are skipped while it may be on the page; an unreadable setting file counts as off on the Mac.
  Default: on for a household member's Mac (device role `member`), off for the owner's Mac and on Windows. Off:
  behaviour as before (snapshots and confirmations now also mask values of one-time-code fields named in more ways).

## 0.3.0 (2026-11-13)

The development direction is reversed: this repository is the upstream of the core.

- **This repository is now the upstream of the core** (`docs/DEVELOPMENT.md`): maintainers develop in a clone of it;
  their own installations are this tree plus a private layer that never enters it.
- **Guard** (`release/export_freedom.py guard`, `release/freedom_guard.py`, `release/hooks/pre-push`, CI job
  `guard`, devtree `.devtree.json`): the tree and every new commit (added lines, file names, message, author) are
  checked for personal data, secrets and module names in the core before a merge or a push.
- devtree 0.8.0: main tree from `devtree.json`, the repository's guard before merge and push.
- task-hints 0.1.1: neutral labels of the corporate models; the module of 0.2.0 failed signature verification
  because the export changed its hint data after signing.
- Bridge: `BridgeCorporate.cs` renamed `BridgeModules.cs` (the generic module gateway).
- CO Resident 0.5.1 (CO.Core, also in the next CO Desktop): agent browser - date/time/month/week inputs get their value
  in the input's format (ISO or day-first), a new tab or window opened by the agent tab is opened in that tab (its
  address read from the blank window's first request when needed), links with a real address outside a form are not
  submits, an unanswered confirmation is `no_answer` (not a refusal; browser actions wait 10 min with a notification
  on the Mac), form confirmations switchable on the Mac (menu "Ask before sending forms", `co-resident browser forms ask|auto`).
- CO Resident 0.5.2: secrets of agent tools in the Mac's own login Keychain - `secret_prompt` (native dialog with a
  hidden field, value only to `security -i` stdin as service `aiwb/<name>` / account, never in argv, logs, audit or
  output; returns saved/cancelled/timeout/waiting), `secret_list` (names only) and `secret_delete` (confirmed every
  call); tools on the Mac read them with Python keyring `aiwb/<name>`. The resident's own items are refused.

## 0.2.0 (2026-11-13)

Everything that is not the core is now a module.

- **Module interface ext/1** (`modules/EXT1.md`, `docs/ext1-device-tools.md`): a module is a folder with a signed
  `module.json` that declares what it provides — tools, services and schedules, device tools, models, chat modes,
  pages, settings, search providers, hooks and events. The core may not add new mentions of module names
  (`release/check_core_names.py`, baseline only shrinks; the export check refuses module ids outside the allow-list).
- **Loaders.** Bridge loads modules (services with takeover of an old scheduled task, schedules, device tools,
  models, context, events, tools that follow the module switch); CO Desktop and CO Resident load module device tools
  and settings pages (Connectors → Modules); CO Web loads module pages and chat modes through `ext/ext1.js`; the
  server keeps the module registry and a generic model gateway. Install, update, disable, enable and remove with
  `install/freedom_modules.py` or CO Web → Host → Modules; remove asks whether to keep or erase the module's data.
- **13 standard modules** in `modules/`: admin-checks, assoc-search, browser-ext, canvas, chat-import, community,
  corporate (off by default), dictation, docs-search, geo-presence, mail-sheets, study, task-hints.
- **Compose check** (`install/freedom_compose.py`, `install/freedom_compose.md`): shows what a workbench consists of
  (core, modules, tools, own private modules) and the gaps between them.
- **How it runs** (README): where jobs run, the PC from anywhere through the relay, push, updates that do not wait
  for long jobs.

## 0.1.0 (2026-11-12)

First public export. Status of each stage: see the release notes.
