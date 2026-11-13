# ext/1 — the extension API of the core for modules

Reference of what a module may declare in `module.json` and what its pages may say to CO Web (SPEC §3.6). The core
knows no module by name: everything below is generic and read from the manifests of enabled modules
(`db.php op modules_list` → CO Web).

## Manifest (`module.json`, schema 1)

Base fields (`id`, `version`, `ext`, `scope`, `permissions`, `personal`, `server`, `dataPolicy`, `defaults`) — see
`geo-presence/module.json` and SPEC §3.6. `provides` may contain:

| Key | Shape | What the core does |
|---|---|---|
| `ui[]` | `{slot, id, entry, title, path?}`; slot `ui.header` \| `ui.page` \| `ui.panel` \| `ui.settings` | shows `entry` (an html file of `ui/`) in a sandboxed iframe. `ui.page` opens full-window at `/x/<module>/<id>`; `path` (e.g. `/canvas`) adds an old top-level address of CO Web (never one of CO Web's own routes). The query of the address goes to the page as `ctx.params`. `ui.panel` is a pane next to a chat (mode viewers). |
| `modes[]` | `{key, label, short, icon, rules, offNote?, hintOn?, hintOff?, projectLabel?, newProjectHint?, inherit?, viewer?}` | **chat.mode**: a switch of the chat (header button, chat and project menus, a line in the timeline at every switch). While on, every job of the chat gets the text of `rules` (a registered `context/*.md` file of the module) as instructions; after it was switched off once, the text of `offNote`. State lives in the chat document: `<key>` (bool), `<key>Log` (switches: `{on, by, utc}`), project default `<key>Default` (new chats only), `<key>Doc` (the document of a viewer chat). `inherit`: branches and side questions of a chat keep the mode. `viewer {panel, param, titlePrefix}`: `?<param>=<path>` on a project or chat address shows the module's `ui.panel` `panel` left of the chat (see *Viewer pages*). |
| `context[]` | `"context/x.md"` | **context**: the text goes into every job of the person while the module is enabled. |
| `fileActions[]` | `{id, label, title?, exts[], exclude?, open}` | items of the file menu of chat files and project files for files with these extensions (`exclude`: a regex of names left out). `open {page, param?, target?}` opens the module's `ui.page` `page` with `?<param>=<path relative to AI-Workbench>` (`target: blank` = new tab/window); `open {mode}` opens the viewer of mode `mode`. |
| `messageViews[]` | `{match, label, strip[]?, replace[[re, text]]?}` | how a person sees user messages a module page wrote: when the regex `match` matches the text, the bubble shows `label` (`$1`… = groups of `match`) as a tag and the text with `match` and every `strip` regex removed and `replace` pairs applied. The agent gets the full text. |
| `sideJobs[]` | `{key, label, list?, item?, count?, countNoun?}` | jobs the module's server code queues into a chat with `request.<key> = {path, file}`: the question and the answer collapse into one row `label` (they never hold back the chat's own messages and never wait in the inbox). `list/item/count/countNoun`: the answer is JSON (last fenced block) with an array `list` of objects; the row shows `item` of each and the size of its `count` array (`countNoun`). |
| `navigate[]` | `"https://origin"` \| `"/path"` \| `{config: key}` | where a page may send the whole window (`navigate` below): an origin, a path of this site, or the origin of a value of the module's install config. |

`installConfig {key: "install.json path"}` (top level): values the installer copies from the owner's `install.json`
into the module's server folder (`config.json`, never secrets); server code reads them as `$ctx['config']`.

Files the core reads itself (`context/*.md|txt`) must be registered files of the module (sha256 checked).

### Permissions the core enforces for pages

| Permission | Allows |
|---|---|
| `files.read` | `file.get`, `file.pdf`, `file.sibling` |
| `files.write` | `file.putSibling` |
| `chat.read` | a viewer panel gets the events of its chat (`chat` messages) |
| `chat.post` | `chat.ask` |
| `ui.navigate` | `navigate` (only to targets of `provides.navigate`) |

## Page protocol (`/ext/ext1.js`)

A page includes `/ext/ext1.js` and talks through `window.coExt`:

- `coExt.ready` → context `{ext, module, version, slot, uiId, me, owner, vw, theme, person, params, chat?}`.
- `coExt.call(op, args, {timeoutMs})` → a user op of the module (`server.ops.user`); default 30 s.
- `coExt.req(name, args, transfer?)` → a service of the core (below); binary data travels as `ArrayBuffer`.
- `coExt.size(w, h, overlay)` — header slots only.
- `coExt.navigate(url, target)` — `self` (whole window) or `blank` (new tab/window).
- `coExt.title(text)` — window title of a `ui.page`.
- `coExt.on('context'|'blur'|'chat', fn)`.

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
