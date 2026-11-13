# Module chat-import (import of claude.ai and ChatGPT chats)

Standard module of Freedom (SPEC §3.6, §3.7 row "Импорт"). Until CO Web 0.102.0 this was part of the core
(`co-web/src/ImportView.tsx`, `co-web/src/importers/`, the "Import…" button); now the core only has the generic
`import` point and knows no service.

| Layer | Here |
|---|---|
| `ui.page` `import` | `ui/import.html` + `import.js` (built from `src/` by `build.mjs`) + `import.css`: reads an official export (zip or `conversations.json`/`projects.json`) in the browser, list, search, preview, target projects. CO Web shows a button "Import…" next to "+ New" and the page at `/e/<env>/m/chat-import/import` |
| `import` (ext/1, permission `import.write`) | the page hands the core one conversation at a time: `core.import.context`, `core.import.write`, `core.import.finish` (CO Web `importWriter.ts`: chat.json + one sealed event segment + attachment texts, index `imports/index.json`) |
| `tools` | `claude-ai-sync`, `chatgpt-sync` (tool library): write through the core's database API at home (`db.php` op `put` / `chat_read`, host key `aiwb/co-push`) |
| `agentSchedules` `nightly-sync` | 04:30: fetch → pending → assign → import of both tools as an agent job (a Bridge schedule in `schedules.json` of a project, cowork mode). Declared, not a `service`: Bridge runs module schedule services by itself, which would give a workbench that already has this agent schedule a second, assign-less run |
| `secrets` | `aiwb/claude-ai` (sessionKey), `aiwb/chatgpt` (session cookie) — keyring of the home PC, filled by the tools' `refresh-cookie` |

## Build, sign, install

    node modules/chat-import/build.mjs                       # after a change of src/ (needs co-web/node_modules)
    node modules/chat-import/tests/parse.selftest.ts
    node modules/chat-import/tests/page-e2e.mjs              # the page in a sandboxed frame over ext/1 (PW_CHROME=<chrome.exe>)
    node modules/chat-import/tests/coweb-e2e.mjs             # through CO Web (co-web/dist) with a Drive mock
    python install/freedom_modules.py sign modules/chat-import
    python install/freedom_modules.py install modules/chat-import --enable

`localOnly` files (`src/`, `tests/`, `build.mjs`, `tsconfig.json`) are signed with the module but not uploaded to the
server. Turning the module off removes the button and the page; imported chats stay (they are ordinary chats of the
core). The nightly schedule is a Bridge schedule of the owner's project and is switched in CO Web → Schedules.
