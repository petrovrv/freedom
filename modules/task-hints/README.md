# Module task-hints (which agent for this task)

Standard module of Freedom (SPEC §3.6, §3.7 row "Подсказки"). Until CO Web 0.102.0 this was part of the core
(`co-web/src/taskHint.ts`, `taskHintData.ts`); now the core has only the generic `ui.hint` slot.

| Layer | Here |
|---|---|
| `ui.hint` (permission `composer.read`) | `ui/hint.html` + `hint.js`: a hidden page; CO Web sends it the composer text (debounced) and it answers `coExt.hint({text, title, picks})`. CO Web shows the first pick its host offers next to the model picker ("Recommended for excel: GPT-6-Sol (subscription)") and switches only on a click |
| data | `ui/hint-data.js` (`window.TASK_HINT_DATA`): category → primary/fallback pick, the bench date |
| `tools` | `aiwb-modelbench` (tool library): `report --module <repo>/modules/task-hints` rewrites `ui/hint-data.js` |

## Refresh the recommendations

    python "<tools>/aiwb-modelbench/aiwb-modelbench.py" report --module modules/task-hints
    node modules/task-hints/tests/hint.selftest.mjs
    node modules/task-hints/tests/page-e2e.mjs               # the hidden page in a sandboxed frame (PW_CHROME=<chrome.exe>)
    python install/freedom_modules.py sign modules/task-hints      # bump "version" in module.json first
    python install/freedom_modules.py install modules/task-hints --enable

The page sees only the text being typed (nothing is sent anywhere: the frame has no network, `connect-src 'none'`).
Turning the module off removes the hint.
