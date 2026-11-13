# Module geo-presence (family presence)

First probe of the Freedom module format (SPEC §3.6, stage F10). Until CO Web 0.101.0 this was part of the core
(`web/agent/geo-lib.php`, `geotile.php`, `co-web/src/GeoStatus.tsx`, `GeoMap.tsx`); now the core knows nothing about it.

| Layer | Here |
|---|---|
| `ui.header` | `ui/header.html` + `header.js` + `header.css` — chips, questions, privacy modes, map picker; ext/1 bridge (`/ext/ext1.js`) |
| server (standard module) | `server/ops.php` — user ops `geo_state`, `geo_answer`, `geo_privacy`; push op `geo_put`; route `tile`; install hook moves the pre-F10 data |
| data | files in `~/co-agent/state/modules/geo-presence/` on the server; the tool's SQLite on the home PC |
| service | tool `geo-presence` (`serve`) under the Bridge supervisor (Bridge with the module loader takes over the old Windows task `\AIWorkbench\GeoPresence`, disables it, and gives it back after 3 quick failures; see `modules/README.md`) |
| personal | every member has the own switch (auto / ask / off) in CO Web → Host → Modules; only "auto" members appear |

Install (a workbench that had presence in the core before F10 — same deploy step as the new core, so the header is
never empty; the family's existing chips stay visible):

    python install/freedom_modules.py install modules/geo-presence --enable --default-person auto

`--default-person auto` is only a default: every member sees the own switch in Host → Modules and may change it. A new
install without it asks each member in the header first. While the module is off, the home PC's `geo_put` gets 403.
A member whose switch is not "auto" is left out of the header and the PC sends no "heading home" push about them
(tool geo-presence 0.4.3+; "nobody home" still counts everybody's real data, as the heating rules do).
