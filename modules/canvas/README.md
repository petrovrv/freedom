# canvas — ONLYOFFICE editor on the home PC (Freedom module)

Chat and project files open in ONLYOFFICE Docs on the home PC and save back to Drive in place (Drive keeps the file id
and makes a revision). Word, Excel and PowerPoint files open as they are; Markdown and text open as a Word notebook
(pandoc makes a docx next to them, *Save as Markdown* writes it back). Comment threads marked `@agent` go to the chat's
agent in its own session and come back as replies and tracked changes. *Share* puts the document (docx without the
agent's threads, or PDF) into a Gmail draft made with the user's own Google account; nothing is ever sent.

Stage F10 of Freedom moved the canvas out of the core into this module (ext/1, `modules/EXT1.md`). Behaviour is unchanged.

## What moved out of the core

| Before (core) | Now (module) |
|---|---|
| `canvas-bridge/` (repo root) | `canvas-bridge/` — the same service; it finds `install/` at `<repo>/install` (`agent_threads.py`), writes no `__pycache__` into the signed folder |
| `co-agent/web/agent/canvas-lib.php` | `server/canvas-lib.php`, called by `server/ops.php`; the canvas URL now comes from the install config (`publicUrl`), `config.php canvas_url` is the fallback |
| `db.php` ops `canvas_ticket`, `canvas_share_draft` (user), `canvas_job`, `canvas_job_get`, `canvas_share_put` (host key, gz list) | the same op names as module ops (`server.ops.user` / `server.ops.host`); errors are in the body (`{error: forbidden\|not_found\|need_gmail\|busy\|uncertain\|…}`) with HTTP 200 |
| `co-web/src/Canvas.tsx` (`/canvas?path=`) | `ui/editor.html` + `ui/editor.js` — `ui.page` `editor`, path `/canvas` |
| `co-web/src/CanvasShare.tsx` (`/canvas-share?id=`) | `ui/share.html` + `ui/share.js` — `ui.page` `share`, path `/canvas-share`; "Google was already asked for this share" is kept with `coExt.storage` (a module page has no sessionStorage; the sign-in page accepts only `back=/canvas-share?id=<id>`) |
| `FileMenu.tsx` `canvasKind` / `canvasItem` | `provides.fileActions` `office` (docx, xlsx, pptx) and `notebook` (md, markdown, txt; with its title); the legacy artifact path → scratch mapping (`canvasPath`) is done by `ui/editor.js` |
| `ChatView.tsx` `CanvasRow` / `canvasReplies`, `e.canvas` filters | `provides.sideJobs` `canvas` (list `threads`, item `reply`, count `ops` "tracked change"); event key `canvas` unchanged, so old rows keep working |
| `finalize-lib.php` / `inbox-lib.php` `canvas` key | generic `co_module_side_keys` |

## Layers

| Layer | Where | What |
|---|---|---|
| Pages | `ui/` (sandboxed iframe of CO Web, `/ext/ext1.js`) | `/canvas?path=` asks a ticket and sends the window to `<publicUrl>/cb/edit#<ticket>`; `/canvas-share?id=` makes the Gmail draft and sends the window to Gmail |
| Server | `server/ops.php` → `server/canvas-lib.php` (`~/co-agent/modules/canvas/`) | role check and HS256 ticket (120 s); side jobs for comment threads; shared files for an hour in `~/co-agent/state/canvas-share/` |
| Home PC | `canvas-bridge/` (task `\AIWorkbench\CanvasBridge`, 127.0.0.1:8089) | ONLYOFFICE Docs on 127.0.0.1:8088, both behind Tailscale Funnel (`/cb`, `/`); CO Agent plugin of the editor (`canvas-bridge/plugin/`) |

## Install (a workbench that had canvas in the core)

```
python install/freedom_modules.py install modules/canvas --enable
```

- uploads the module, registers it with `config.publicUrl` = install.json `canvas.publicUrl` and switches it on;
- re-points the Windows task `\AIWorkbench\CanvasBridge` from `<repo>/canvas-bridge/canvas_bridge.py` to
  `<repo>/modules/canvas/canvas-bridge/canvas_bridge.py` (from the task's own XML: Python, triggers and settings stay)
  and restarts it, so the moved code is what runs. A later `install` of the enabled module re-points only if needed.

Nothing else changes on the PC or the server:

- ticket key: `~/co-agent/secrets/canvas-ticket-key` on the server = keyring `aiwb/canvas` / `ticket` on the home PC;
- keyring `aiwb/onlyoffice` / `jwt` (ONLYOFFICE JWT) and `aiwb/co-push` (host key for db.php);
- state of the bridge: `%LOCALAPPDATA%\AIWorkbenchCanvas` (sessions, comment asks, log).

Disable (`freedom_modules.py disable canvas`) removes the menu items, the pages and the ops and stops the task; old canvas
rows of chats stay out of the inbox (their key stays known while the module is installed).

## CO Desktop

`CanvasWindow` of CO Desktop still opens addresses starting with `/canvas` in its own window; the `ui.page` path `/canvas`
keeps that working (the page then sends that window to the editor on the PC).
