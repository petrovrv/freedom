# Module study (study mode and study viewer)

Until this module the study mode was part of the CO Web core (`studyMode.ts`, `studyDoc.ts`, `studyConvert.ts`,
`StudyViewer.tsx`, `study.css`). Now the core knows only the generic ext/1 points (`../EXT1.md`): `chat.mode`,
`ui.panel` viewers, `fileActions`, `messageViews` and the core file services.

| Layer | Here |
|---|---|
| `chat.mode` | mode `study`: rules `context/rules.md` while on, `context/off.md` once switched off; switch in the chat, the project default for new chats, a line in the chat at every switch; branches and side questions keep it |
| `ui.panel` | `ui/viewer.html` + `viewer.js` (one IIFE bundle: React + pdf.js, worker on the main thread — an opaque-origin frame cannot start workers) + `viewer.css`; opens at `?study=<path>` left of the document's chat |
| `fileActions` | "Open in study viewer" for pdf, Office, text and image files |
| `messageViews` | a viewer question shows as "📖 file · p. N" and the question; the agent gets the full text |
| data | none of its own: the chat document keeps `study`, `studyLog`, `studyDoc`, the project `studyDefault` (the same keys as before, so existing chats keep their state); the PDF copy of a converted file lives next to it as `<name>.study.pdf` (Drive app properties `studyOf`, `studySource`) |

Permissions: `files.read`, `files.write` (the PDF copy next to the original), `chat.read` (markers of questions),
`chat.post` (Ask / WTF send into the chat).

Build and test (uses `co-web/node_modules`):

    node modules/study/build.mjs                     # ui-src/ -> ui/viewer.js
    node modules/study/tests/viewer.e2e.mjs          # Playwright: the page in a sandboxed frame with the module CSP
    node modules/study/tests/studyDoc.selftest.ts    # tags, messages, PDF writer
    node modules/study/tests/views.selftest.ts       # messageViews == the old askDisplay

Install (a workbench that had study mode in the core; nothing to migrate):

    python install/freedom_modules.py install modules/study --enable
