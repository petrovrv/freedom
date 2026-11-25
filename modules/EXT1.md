# ext/1 — the extension API of the core for modules

Reference of what a module may declare in `module.json` and what its pages may say to CO Web (SPEC §3.6). The core
knows no module by name: everything below is generic and read from the manifests of enabled modules
(`db.php op modules_list` → CO Web).

## Manifest (`module.json`, schema 1)

Base fields (`id`, `version`, `ext`, `scope`, `permissions`, `personal`, `server`, `dataPolicy`, `defaults`) — see
`geo-presence/module.json` and SPEC §3.6. `envs` (optional, F14c): the environments the module is for — nobody else
gets its pages, ops, data or actions (the owner still switches and removes it); such a module brings no part that works
for the whole house (tools, services, device parts, hooks, context, modes, import, side jobs). `provides` may contain:

| Key | Shape | What the core does |
|---|---|---|
| `ui[]` | `{slot, id, entry, title, label?, path?, window?, windowKey?, app?, icon?}`; slot `ui.header` \| `ui.page` \| `ui.panel` \| `ui.settings` | shows `entry` (an html file of `ui/`) in a sandboxed iframe. `ui.page` opens full-window at `/x/<module>/<id>`; `path` (e.g. `/canvas`) adds an old top-level address of CO Web (never one of CO Web's own routes). The query of the address goes to the page as `ctx.params`. **ui.window**: `window: true` on a `ui.page` = a desktop app (CO Desktop; CO Resident opens every CO page a page opens in a new tab in its own window anyway) shows the page in its own window when CO Web opens it in a new tab (`fileActions` `target: blank`, `navigate(url, 'blank')`), see *Desktop windows*. `ui.panel` is a pane next to a chat (mode viewers). |
| `modes[]` | `{key, label, short, icon, rules, offNote?, hintOn?, hintOff?, projectLabel?, newProjectHint?, inherit?, viewer?}` | **chat.mode**: a switch of the chat (header button, chat and project menus, a line in the timeline at every switch). While on, every job of the chat gets the text of `rules` (a registered `context/*.md` file of the module) as instructions; after it was switched off once, the text of `offNote`. State lives in the chat document: `<key>` (bool), `<key>Log` (switches: `{on, by, utc}`), project default `<key>Default` (new chats only), `<key>Doc` (the document of a viewer chat). `inherit`: branches and side questions of a chat keep the mode. `viewer {panel, param, titlePrefix}`: `?<param>=<path>` on a project or chat address shows the module's `ui.panel` `panel` left of the chat (see *Viewer pages*). |
| `context[]` | `"context/x.md"` | **context**: the text goes into every job of the person while the module is enabled. |
| `fileActions[]` | `{id, label, title?, exts[], exclude?, open}` | items of the file menu of chat files and project files for files with these extensions (`exclude`: a regex of names left out). `open {page, param?, target?}` opens the module's `ui.page` `page` with `?<param>=<path relative to AI-Workbench>` (`target: blank` = new tab/window); `open {mode}` opens the viewer of mode `mode`. |
| `messageViews[]` | `{match, label, strip[]?, replace[[re, text]]?}` | how a person sees user messages a module page wrote: when the regex `match` matches the text, the bubble shows `label` (`$1`… = groups of `match`) as a tag and the text with `match` and every `strip` regex removed and `replace` pairs applied. The agent gets the full text. |
| `sideJobs[]` | `{key, label, list?, item?, count?, countNoun?}` | jobs the module's server code queues into a chat with `request.<key> = {path, file}`: the question and the answer collapse into one row `label` (they never hold back the chat's own messages and never wait in the inbox). `list/item/count/countNoun`: the answer is JSON (last fenced block) with an array `list` of objects; the row shows `item` of each and the size of its `count` array (`countNoun`). |
| `navigate[]` | `"https://origin"` \| `"/path"` \| `{config: key}` | where a page may send the whole window (`navigate` below): an origin, a path of this site, or the origin of a value of the module's install config. |
| `actions[]` | `{op, description, args?, outward}` | **app actions for the agent** (F14b-1, section "App actions" below): user ops of the module (`server.ops.user`) an agent may call through the core tools `app_list` / `app_action`. `args` is a flat JSON schema (`properties` of type string \| integer \| number \| boolean, `enum`, `required`); `outward` (required) — true when the op acts outside the workbench (mail, calendars of others, devices of the house): such an op runs only after the person's Run on a card in "Waiting for you" (F14b-2). |
| `notify[]` | `{id, hooks: ["push"]}` | **notify**: the server entry's `notify[<id>]['push']` gets `(ctx, {envs, title, body, url, tag})` for every push the server sends (job finished, confirmations, family chats, alerts; `[[silent]]` ones never), whether or not the person has a subscribed device — e.g. to write it to them in a messenger. Optional hook `status` gets `(ctx, {env})` and returns `{ready, note?}`: whether this channel reaches the person now. Each hook is a delivery channel `<module>/<id>` next to the core's `devices`: push.php `op=channels {env}` (push key) lists them, `op=notify` with `channels: [...]` sends only there (without it: everywhere) and answers `modules: {<channel>: true|false}` (the hook ran without an error). Only standard modules; a failing hook never stops the push. |

`installConfig {key: "install.json path"}` (top level): values the installer copies from the owner's `install.json`
into the module's server folder (`config.json`, never secrets); server code reads them as `$ctx['config']`.

Files the core reads itself (`context/*.md|txt`) must be registered files of the module (sha256 checked).

### Permissions the core enforces for pages

| Permission | Allows |
|---|---|
| `files.read` | `file.get`, `file.pdf`, `file.sibling` |
| `files.write` | `file.putSibling` |
| `chat.read` | a viewer panel (or a composer page) gets the events of its chat (`chat` messages) |
| `composer.send` | `coExt.send` (composer slot) |
| `media.play` | `coExt.play` (composer slot) |
| `chat.post` | `chat.ask` |
| `ui.navigate` | `navigate` (only to targets of `provides.navigate`) |
| `notify.push` | `coExt.notify` from any page and `$ctx['notify']` of server code: pushes of the app to its person (see *Notifications*) |
| `data.env` | `coExt.data.*` from any page: the app's data kept by the core for the signed-in person, and the agent's core actions `data_*` (see *Apps made on the fly*) |

### Desktop windows (`ui.window`)

A `ui.page` with `"window": true` opens in CO Desktop in its own window instead of a tab, the same page as in a browser
(no module code in the app). `windowKey` names the query parameter that identifies one window (`"path"`: one window per
file; without it, one per address); opening the same value again brings that window to the front. Position and size
are remembered per page (`desktop.json` `PageWindows["<module>/<page>"]`). In that window:

- `window.coDesktop` answers only `getAccessToken` and `status` (no device ops, no module switches);
- the page may move the window to this site, Google sign-in and the https origins of the module's `navigate` list (e.g.
  an editor on the home PC at an install-config address); a link the user clicks elsewhere opens in the system browser;
- a new tab the page opens goes to the system browser (an empty tab opened inside a click and pointed somewhere later
  works too: its first real address goes out);
- × unloads the page first (its `pagehide`/`beforeunload` run, e.g. to send what it holds), a second × closes at once.

Only pages of enabled modules (the device's `modules_list`) get a window; in a browser the page simply opens in a tab.

## Shell: apps, tray, windows (SPEC §3.6a)

CO Web is a small shell around module pages, the same on a phone and on a computer. What a module shows comes in
four kinds (one module may give several): an **app** (a tile of ▦), a **tray** indicator (`ui.header`), an
**extension** of the core's own screens (`ui.composer`, `fileActions`, `modes`, `ui.panel`, `ui.hint`, `search`) and
a **service** without a screen (`services`, `deviceTools`, `context`, `tools`).

- **App** — a `ui.page` with `"app": true`, `"icon"` (an emoji or up to 8 characters) and `"label"` is a tile of the
  launcher (▦ in the header; a popover on a computer, a full-screen grid on a phone). Pages without `app` keep their
  button in the sidebar. A desktop app (CO Desktop, CO Resident) opens an app with `window: true` in its own window;
  everywhere else the app fills the centre of CO Web.
- **Tray** — the `ui.header` slot is for small status indicators only (one per module, no long text, no action buttons;
  actions live in the app). A tray indicator belongs to a module whose service really runs and shows its state
  (running / not answering); without a running service it stays empty. A tray page may be empty while there is nothing to show: `coExt.size(0, 0, false)`. The
  core keeps the tray inside the width of the window (it scrolls sideways on a phone) and draws an open popover
  (`size(..., true)`) fixed over the page, kept inside the window.
- **Settings** — the last tile of the launcher is the core app Settings (the control panel): the core panels grouped
  as This chat (chat, session), Project and System (host and devices, modules), and one tile per enabled module with
  `ui.settings` pages or a person's switch (`personal`); the tile opens Host → Modules at that module and takes the
  icon of the module's app, else the `icon` of any other of its pages (e.g. the tray page), else the first letter.
  A module needs nothing extra.
- **Badge** — `coExt.badge(v)` from any loaded page of the module (usually the tray page, which is always loaded): a
  count or a mark of up to 3 characters on the module's app tile, and a red dot on ▦ while any app has one; `0`, blank
  or `null` clears it. Example: unread mail. The ☰ menu keeps navigation only (projects, family, files).
- **Navigation** (F14e.1) — an open app has a bar on top: "← Chats | ▦ | its icon and name" and the other recent
  apps. Back of the browser or of Android, Esc and "← Chats" leave it for the page it was opened from; app to app
  replaces the history entry. A left app is not destroyed: its iframe is hidden and keeps its state (the last 3 on a
  phone, 5 on a computer stay alive, the oldest is unloaded and loads again on the next open); `ctx.visible` is false
  while it is hidden (a page may pause its polling). Esc that no handler of the page prevented (`preventDefault`) and
  that was not typed in a text field leaves the app — a page that uses Esc itself calls `preventDefault`. Recent apps
  are icons in the header on a computer.
- **Device apps** — a program of a computer (not a web page) is a module with an app page, `deviceOps` and
  `deviceTools` run by a `device/` script (`docs/ext1-device-tools.md`): inside CO Desktop the app page calls a device
  op that starts the program; elsewhere the page says where it runs, and agents use the program's functions as device
  tools. Example: `photostudio`.
- **Open with** — `fileActions` (above): a file of a chat opens in the app that declared its extension.
- **App actions** (F14b-1) — see "App actions for the agent" below: the agent uses an app the way its person does, and
  the open pages of the app get the event `refresh` after an action.

Example (`smart-home`): `{ "slot": "ui.page", "id": "home", "entry": "ui/home.html", "title": "Дом", "app": true,
"icon": "🏠", "label": "Дом" }` plus a tray page that shows a chip only while Zigbee pairing runs.

## App actions for the agent (F14b-1, F14b-2, SPEC §3.6a item 5)

An app describes some of its user ops as actions (`provides.actions`); an agent job calls them with the rights of the
person of the job's environment, as if the person pressed the button in the app. No page is needed for an action.

- **Agent side**: the MCP server `aiwb-tools` (core tool `toolbox` 1.8.0+, on the home PC and on the site) has
  `app_list` (apps of the environment with their actions, schemas and `outward`) and `app_action {app, action, args}`
  (runs one action, returns the module's answer). It sends the job id (`AIWB_JOB_ID`) with the host key to `db.php`
  host ops `app_list` / `app_action` (`co-agent/web/agent/apps-lib.php`).
- **Who**: the server takes the environment, the chat and the person's rights from the job's row in `jobs`, never
  from the caller; the job must still run. The handler gets the usual context (`myEnv`, `owner`, `email` of the
  person) plus `actor: "agent"` and `jobId`, and checks access itself exactly as for a page call. A member's job acts
  only in the member's own environment.
- **Outward** (F14b-2, toolbox 1.9.0, CO Web 0.120.0): an action with `outward: true` does not run on the agent's
  call. The server checks the arguments and files a request (`kv env/<env> app-requests`): a card in "Waiting for
  you" of the job's environment with the app, the action's description, each argument with its schema `description`
  and the chat it came from; the person's devices get a push. The agent gets `{status: "pending", requestId,
  expiresUtc}` (the same call from the same chat while the card waits returns the same card; at most 20 cards wait
  per environment) and asks `app_action_status {requestId}` (also from a later job of the environment):
  `pending | running | done | failed | denied | expired`, with the module's answer after a run. Only the person of
  that environment sees and decides the card (user op `app_request_decide {id, decision: run|deny, etag}`): the
  card waits 30 min; the first decision takes it out of `pending` under a row lock, so a second click is refused;
  Run sends the card's etag (app, action, module version, arguments) and the server refuses a changed request or a
  module updated since. The handler then runs with the person's own rights, context `actor: "person"` and
  `confirmed: {request, jobId}`; the journal has `request` rows and `action` rows with `confirmed`.
- **Refused**: a job of a shared chat, of a chat below one or queued restricted (no list, no actions), a read-only
  chat (`permissions.files: read`: list only), a module switched off or switched off by the person (`personal` off).
  The core also refuses arguments outside the action's schema (`args.x ...`) before the handler sees them.
- **Refresh**: after a successful action the server records an app event of the environment (`kv env/<env>
  app-events`); CO Web asks for new ones (user op `app_events {since}`, at once and then every 5 s while an app is
  alive or a tray page of a module with actions is shown, and the tab is visible; `modules_list` names each module's
  `actions`) and sends every frame of that module (the app page and its tray page) the event
  `coExt.on('refresh', fn)` with `{by: "agent", actions: [...]}`. A page loads its data again. Windows of CO Desktop
  get it with F14e.2.
- Every action is written to the module journal (`module_log`, action `action`).

Example (`smart-home` 0.4.0): `home_state` (read the house, `outward: false`) and `home_control` (switch a device,
`outward: true` — the agent's call becomes a card; the house switches only after the person's Run).

## Notifications of apps (F14b-3, SPEC §3.6a item 8)

An app tells its person about something while it is closed (an important letter, a reminder) through the core's push.
Permission `notify.push` in `permissions` (an update that adds it switches the module off until the owner agrees, as any
grown permission: `freedom_modules.py enable <id>`).

- **From a page** (any slot, e.g. the tray page that stays loaded): `coExt.notify({title, body?, tag?, open?})` →
  `{ok: true, sent}` or `{ok: false, error, message, retryAfter?}`; it goes to the signed-in person only (user op
  `app_notify`, env of the session).
- **From server code** (standard modules): `$ctx['notify']([...])` in any handler. A user op notifies only its caller
  (`env` defaults to `myEnv`, another env is refused `not_your_env`); a push or host op (a service) names `env` (a
  member). A service on the home PC without PHP: push.php `op=app_notify {module, env, title, body?, tag?, open?}` with
  the push key (`X-CO-Push-Key`).
- **Fields**: `title` (required, 80 characters), `body` (300; whitespace folds into one line), `tag`
  (`[A-Za-z0-9][A-Za-z0-9._-]{0,40}`: a new push with the same tag replaces the shown one; the same tag with the same
  title, body and address within 10 minutes is not sent at all, answer `{duplicate: true}`), `open {page?, params?}`
  (a `ui.page` id of the module, default its app page; the click opens `/e/<env>/m/<module>/<page>` in the shell, with
  `params` (up to 8 short strings) the bare page `/x/<module>/<page>?…` that gets them as `ctx.params`; in CO Desktop
  the app's window).
- **What the person sees**: the push title is "<app icon> <app name> · <title>" (a module cannot pass its push off as
  one of the core). It goes through `co_push_notify` to the person's subscribed devices and to the notify hooks of the
  other enabled modules (e.g. WhatsApp), never to the sender's own hooks (no loop).
- **Limits and switches**: at most 10 an hour and 50 a day per app and person (`rate_limited` with `retryAfter` in
  seconds); no quiet hours (the core has none). The person switches an app's notifications off for themselves in
  Settings → the module ("Notifications", user op `app_notify_set {id, on}`; `modules_list` gives `notify: {on}` for
  modules with the permission); `personal` mode `off` stops them too (`person_off`).
- **Journal**: every push sent is a `notify` row of `module_log` (`by` page | server | service, `tag`, devices reached,
  hooks that took it); the switch is a `notify-switch` row.

Minimal example — a module that tells its person when a long import finished (server code of a user op):

```php
'import_done' => static function (array $ctx, array $in): array {
    // ... the work ...
    $ctx['notify'](['title' => 'Import finished', 'body' => count($in['chats'] ?? []) . ' chats imported', 'tag' => 'import',
        'open' => ['page' => 'import']]);
    return ['ok' => true];
},
```

and in `module.json`: `"permissions": ["notify.push"]`. A page does the same with
`coExt.notify({title: 'Import finished', tag: 'import'})`. Test: `co-agent/tests/notify-test.php`.

## Apps made on the fly (F14c, SPEC §3.6a item 6)

A person asks their agent for a small app ("a shopping list", "a timer", "notes for the trip"); the agent makes a
module from the template and installs it like any app. The app is the person's own: signed with the owner's key (trust
level *own*, SPEC §3.6), for one environment (`envs`), in a sandboxed iframe with `connect-src 'none'`, with the usual
switch, and checked before install.

**Data kept by the core.** Only standard modules run PHP on the server, so an own app keeps its data through the core
(permission `data.env`): `coExt.data.get(key)` → `{ok, value, utc}` (`value` null when missing), `set(key, value)` →
`{ok, utc}` (`null` deletes), `delete(key)` → `{ok, deleted}`, `list(prefix)` → `{ok, keys: [{key, size, utc}]}`;
refused: `{ok: false, error: too_large | too_many_keys | quota | not_your_app | person_off | module_disabled}`. Keys
`[A-Za-z0-9][A-Za-z0-9._-]{0,79}`, string values up to 64 KB, 200 keys and 1 MB per app and person (user op
`app_data`, kv `env/<env>` `app-data.<module>`). The agent of that person gets the core actions `data_list`,
`data_get`, `data_set`, `data_delete` in `app_list` (not outward; a write is journaled and the open app gets
`refresh`). Switching the app off keeps the data; `remove --data erase` deletes it. Any module may use it.

**Playbook for the agent** (on the home PC, owner jobs; tree `C:\AIWorkbench\freedom` or the Freedom distribution; `<dir>` = the app folder):

1. Asked for an app → `python install/freedom_modules.py new <id> --app --title "<name>" --icon "<emoji>" --lang ru
   [--env <env of the person>]` — the folder goes to `install.json modules.dirs[0]` (the owner's private modules) or
   `--dir`. The template is a list kept in `coExt.data` (key `items`), themed by the core, with a test.
2. Change `ui/main.html|js|css` to what the person asked (keep: no network, no `localStorage`, no remote scripts,
   no secrets — the CSP blocks them anyway and the review refuses them); raise `version` on every change.
3. `python <app>/tests/test_app.py` and `python install/freedom_modules.py check <dir>` (and `review <dir>`).
4. `python install/freedom_modules.py sign <dir> --own` (keyring `aiwb/freedom-release`, account `owner`; its public key
   must be in `install.json modules.trustedKeys`).
5. `python install/freedom_modules.py install <dir> --enable` — `plan` shows `level: own`, `envs`, the review;
   install stops on a review error. Update = the same with a higher version; off = `disable <id>`; away =
   `remove <id> --data keep|erase`.
6. Open `/e/<env>/m/<id>/main` (the tile is in ▦ of that person only) and look at it:
   `aiwb-e2e.py shot --path /e/<env>/m/<id>/main --out shot.png` (signed in as the owner: for another env only the owner's view). Tell the person what the app does and how to remove it.

A household member's agent cannot install (installing needs the owner's keys on the home PC): it makes the folder
with `--env <member>` and asks the owner; the owner's agent signs and installs it into that member's environment only.

## Device parts compiled into CO Desktop

Most device work of a module is `provides.deviceTools` with a `run` script (`device/`, sha256-checked before each run).
A module that needs the app itself (COM, WPF, the CO browser's sessions) may carry a .NET project
`modules/<id>/desktop/<Name>.csproj` with a public `CO.Core.Connectors.IConnectorModule` (its `Id` = the module id) and
tests in `modules/<id>/tests/<Name>.Tests/`. `co-desktop/ModuleParts.props` compiles every such project into CO Desktop
(the core names none of them; CI, `publish.cmd` and `release/ci_export.sh` run every `modules/*/tests/*.Tests`). The app
binds a part only to a module signed by the maintainers and registers it (connectors, tools, hooks, background work)
only while the module is enabled in the workbench and on for the device, as the last module list says; turning it on or
off later applies after a restart of CO Desktop (the app says so). Its tools must be the `deviceTools` of the manifest
without `run` (names and risk class).

## Page protocol (`/ext/ext1.js`)

A page includes `/ext/ext1.js` and talks through `window.coExt`:

- `coExt.ready` → context `{ext, module, version, slot, uiId, me, owner, vw, theme, person, params, chat?}`.
- `coExt.call(op, args, {timeoutMs})` → a user op of the module (`server.ops.user`); default 30 s.
- `coExt.req(name, args, transfer?)` → a service of the core (below); binary data travels as `ArrayBuffer`.
- `coExt.size(w, h, overlay)` — header slots only.
- `coExt.navigate(url, target)` — `self` (whole window) or `blank` (new tab/window).
- `coExt.title(text)` — window title of a `ui.page` (applied while the app is shown).
- `coExt.badge(v)` — the module's badge in the shell (count on its app tile, dot on ▦); `0`/blank/`null` clears it.
- `coExt.notify({title, body, tag, open})` — a push to the signed-in person (`notify.push`, *Notifications of apps*).
- `coExt.on('context'|'blur'|'chat'|'refresh', fn)` — `refresh`: an action an agent asked for ran in this app (F14b-1; also the person's Run on its card, F14b-2).

Composer slot (`ui.composer`, a page in the action row of the message box; CO Web captures for the frame, which has an
opaque origin): `coExt.input`/`focusInput` (`composer.input`), `coExt.audio.start(maxSeconds, {silenceMs})`/`stop`/`cancel`
(`media.audio`; with `silenceMs` the recording stops by itself after that much quiet following speech, event `audio`
`{silence: true}`, then `stop()` returns it), `coExt.speech.*` (`media.speech`), `coExt.send(text)` (`composer.send`: the
text goes out as the chat's next message with the composer's agent and model; refused while the chat is busy and
without the person: a click into the page allows up to 10 sends within 10 minutes, else `needs_user_touch`),
`coExt.play(bytes, type)` / `coExt.play(null)` (`media.play`: CO Web plays an `audio/*` ArrayBuffer up to 10 MB and
answers `{ended}` when it ends or is stopped). With `chat.read` a composer page gets `chat` `{events, canAsk}` like a
viewer panel. Examples: `dictation/README.md` (input), `voice/README.md` (voice mode).

Core services (`coExt.req`):

| Name | Args → result | Needs |
|---|---|---|
| `file.get` | `{path}` → `{name, mimeType, id, md5, modifiedTime, size, bytes}` | `files.read` |
| `file.pdf` | `{path}` → `{bytes, note}`: the file converted to PDF by Google Drive (Office, text; a temporary copy in My Drive is deleted; `note` says when it could not be) | `files.read` |
| `file.sibling` | `{path, name, withBytes?}` → `{id, name, appProperties, bytes}` or `null`: a file next to `path` whose name starts with the name of `path` (`bytes` null with `withBytes: false`) | `files.read` |
| `file.putSibling` | `{path, name, bytes, mimeType, appProperties}` → `{id}`: create such a file, or replace one whose first app property has the same value (a file the module made for this original); anything else is refused | `files.write` |
| `chat.ask` | `{text, files: [{name, type, bytes}]}` → `{ok}`: the viewer's chat sends this message (a new chat is made for a draft) | `chat.post`, viewer panel |
| `chat.reveal` | `{eventId}` → scrolls the chat to the message | viewer panel |
| `panel.close` | `{}` → closes the viewer | viewer panel |
| `chat.open` | `{projectId, chatId?, viewer?, mode?}` → `{ok}`: opens that chat of the shown environment in the shell (from a bare page or an app window: a new tab of CO); ids only, the server decides access as for any address. `viewer` (letters, digits, space, `_.-`, ≤ 64) is a document of the module's own chat-mode viewer (`mode` names the mode, else its first mode with a viewer): `?<param>=<viewer>` shows the viewer left of the chat; without `chatId` the project address opens the chat of that document or makes one at the first question | `ui.navigate` |

`path` is always relative to AI-Workbench and starts with `projects/`.

CO Web fills the manifest-declared names in, but never shows a module more than it declared: pages of a switched-off
module disappear, its modes, file actions and message views go with it; only `sideJobs` stay listed so that old rows of
a switched-off module remain collapsed and out of the inbox.

### Viewer pages (`ui.panel` of a mode)

`ctx.params[param]` is the document path, `ctx.chat = {id|null, title}`. With `chat.read` the page gets
`chat` messages `{events, canAsk}` whenever the chat changes (`events`: user and assistant messages of the chat with
`eventId, type, text, status, createdUtc, jobId`).
