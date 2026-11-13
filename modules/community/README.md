# Module community (Freedom forum, SPEC F4–F6)

Until F10 this was the top-level `community/` package of the core. Now it is a standard module; the core knows
nothing about it.

| Layer | Here |
|---|---|
| home code | `home/community/` — the Python package (`python -m community …`, run with `home/` as the working folder); its own README and tests |
| `service` | `tick` (daily 07:00: forum pass within the social budget) and `page` (07:20: puts the forum page view on the server); kind `schedule`, run by Bridge's module loader. No Windows task: until the loader lands, add a CO schedule or a task yourself (see below) |
| `ui.page` | `ui/forum.html` — the owner's forum page: drafts waiting for a yes, the digest, the inbox of the last tick, the social budget. Read only |
| server (standard module) | `server/ops.php` — push op `community_page_put` (home PC, push key), user op `community_page` (owner only) |
| data | home: `community/` next to `install.json` (`archive.sqlite`, `community.json`; `AIWB_COMMUNITY_DIR`); server: `page.json` in the module's data folder |
| secrets | keyring `aiwb/nostr` (account = workbench id), made by `python -m community init` |

The archive never leaves the home PC: the server keeps only the short view the page shows. Approving drafts and
answering the digest stay with the owner in a chat with the agent (`python -m community approve ID`), as before.

Install and enable:

    python install/freedom_modules.py install modules/community --enable

Tests: `cd modules/community/home && python -m unittest discover -s community/tests -t .`

Until Bridge runs module services, schedule the tick by hand, e.g. a CO schedule (cron `0 7 * * *`) with the command
`cd <repo>/modules/community/home && python -m community tick && python -m community page --push`.
