# Changelog

Changes of all components (Bridge, CO Web, server, CO Desktop / CO Resident, installer, modules, core tools); each
line names the component and its version. How releases are made: `docs/DEVELOPMENT.md`.

## Unreleased

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
