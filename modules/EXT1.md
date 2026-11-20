# ext/1 — the extension API of the core for modules

Reference of what a module may declare in `module.json` and what its pages may say to CO Web (SPEC §3.6). The core
knows no module by name: everything below is generic and read from the manifests of enabled modules
(`db.php op modules_list` → CO Web).

## Manifest (`module.json`, schema 1)

Base fields (`id`, `version`, `ext`, `scope`, `permissions`, `personal`, `server`, `dataPolicy`, `defaults`) — see
`geo-presence/module.json` and SPEC §3.6. `provides` may contain:

| Key | Shape | What the core does |
|---|---|---|
| `ui[]` | `{slot, id, entry, title, path?, window?, windowKey?}`; slot `ui.header` \| `ui.page` \| `ui.panel` \| `ui.settings` | shows `entry` (an html file of `ui/`) in a sandboxed iframe. `ui.page` opens full-window at `/x/<module>/<id>`; `path` (e.g. `/canvas`) adds an old top-level address of CO Web (never one of CO Web's own routes). The query of the address goes to the page as `ctx.params`. **ui.window**: `window: true` on a `ui.page` = a desktop app (CO Desktop; CO Resident opens every CO page a page opens in a new tab in its own window anyway) shows the page in its own window when CO Web opens it in a new tab (`fileActions` `target: blank`, `navigate(url, 'blank')`), see *Desktop windows*. `ui.panel` is a pane next to a chat (mode viewers). |
| `modes[]` | `{key, label, short, icon, rules, offNote?, hintOn?, hintOff?, projectLabel?, newProjectHint?, inherit?, viewer?}` | **chat.mode**: a switch of the chat (header button, chat and project menus, a line in the timeline at every switch). While on, every job of the chat gets the text of `rules` (a registered `context/*.md` file of the module) as instructions; after it was switched off once, the text of `offNote`. State lives in the chat document: `<key>` (bool), `<key>Log` (switches: `{on, by, utc}`), project default `<key>Default` (new chats only), `<key>Doc` (the document of a viewer chat). `inherit`: branches and side questions of a chat keep the mode. `viewer {panel, param, titlePrefix}`: `?<param>=<path>` on a project or chat address shows the module's `ui.panel` `panel` left of the chat (see *Viewer pages*). |
| `context[]` | `"context/x.md"` | **context**: the text goes into every job of the person while the module is enabled. |
| `fileActions[]` | `{id, label, title?, exts[], exclude?, open}` | items of the file menu of chat files and project files for files with these extensions (`exclude`: a regex of names left out). `open {page, param?, target?}` opens the module's `ui.page` `page` with `?<param>=<path relative to AI-Workbench>` (`target: blank` = new tab/window); `open {mode}` opens the viewer of mode `mode`. |
| `messageViews[]` | `{match, label, strip[]?, replace[[re, text]]?}` | how a person sees user messages a module page wrote: when the regex `match` matches the text, the bubble shows `label` (`$1`… = groups of `match`) as a tag and the text with `match` and every `strip` regex removed and `replace` pairs applied. The agent gets the full text. |
| `sideJobs[]` | `{key, label, list?, item?, count?, countNoun?}` | jobs the module's server code queues into a chat with `request.<key> = {path, file}`: the question and the answer collapse into one row `label` (they never hold back the chat's own messages and never wait in the inbox). `list/item/count/countNoun`: the answer is JSON (last fenced block) with an array `list` of objects; the row shows `item` of each and the size of its `count` array (`countNoun`). |
| `navigate[]` | `"https://origin"` \| `"/path"` \| `{config: key}` | where a page may send the whole window (`navigate` below): an origin, a path of this site, or the origin of a value of the module's install config. |
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
- `coExt.title(text)` — window title of a `ui.page`.
- `coExt.on('context'|'blur'|'chat', fn)`.

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

`path` is always relative to AI-Workbench and starts with `projects/`.

CO Web fills the manifest-declared names in, but never shows a module more than it declared: pages of a switched-off
module disappear, its modes, file actions and message views go with it; only `sideJobs` stay listed so that old rows of
a switched-off module remain collapsed and out of the inbox.

### Viewer pages (`ui.panel` of a mode)

`ctx.params[param]` is the document path, `ctx.chat = {id|null, title}`. With `chat.read` the page gets
`chat` messages `{events, canAsk}` whenever the chat changes (`events`: user and assistant messages of the chat with
`eventId, type, text, status, createdUtc, jobId`).
