# Module assoc-search (associative search)

Finds chats by meaning and by other names of things. Until CO Web 0.102 this was part of the core's
`web/agent/search-lib.php` (schema 20); now the core's chat search knows only its own full-text lists and merges the
lists of search providers (ext/1 `search.provider`, below).

| Layer | Here |
|---|---|
| `search.provider` `vector` | hook `expand`: aliases — other names of the things a query mentions, as extra full-text words; hook `chats`: protocol entries by cosine to the query vector, best entry per chat |
| server (standard module) | `server/ops.php` — host ops `vec_todo`, `vec_put`, `vec_words`, `vec_texts`, `protocol_texts`, `alias_put`, `alias_list`, `alias_delete` (wire format of schema 20, host key); install hook |
| data | tables `m_assoc_search_vec_words`, `m_assoc_search_vec_protocol`, `m_assoc_search_aliases`; the install hook renames the pre-F10 core tables `vec_words`, `vec_protocol`, `aliases` (nothing is recomputed); remove `--data erase` drops them |
| service | tool `aiwb-assoc` (`index`), hourly: Windows task `\AIWorkbench\AssocIndex` |
| tools | `aiwb-assoc` 0.2.0+ (unchanged), `aiwb-chat find` reads the merged search (rank `vector` as before) |

Users see the same results as before: CO Web AI search, `aiwb-chat find` and the secretary's hints get the provider's
list merged by reciprocal rank exactly like the third list of schema 20.

Install (a workbench that had the search in the core — same deploy step as the new core, so search never loses the
vectors; the hourly indexer gets `unknown_op` until then):

    python install/freedom_modules.py install modules/assoc-search --enable

Disable: the chat search runs on full-text alone, the indexer's host ops answer `unknown_op`, the tables stay.

## ext/1 `search.provider` (core: `web/agent/search-lib.php`, `modules-lib.php` `co_module_hooks`)

A standard module declares `provides.search: [{id, hooks: [...]}]` and returns `'search' => [id => [hook => fn]]` from
its server entry. The core calls every enabled provider; one that throws is logged and skipped.

| Hook | In | Out |
|---|---|---|
| `expand` | `{env, q}` | `[query words => [other names]]` — extra full-text terms with lower weight (at most 24 in all) |
| `chats` | `{env, q, text, where, params, depth}` — `where`/`params`: the SQL scope over `p` (projects) and `c` (chats) | `null` (nothing to rank with) or chats best first `[{chatId, match?, entryId?}]`; merged by RRF as list `<id>`; chats outside the scope are dropped by the core |
| `items` | `{env, q, limit, projectId?}` | results that are not chats `[{kind, title, projectId, projectTitle?, path?, modified?, snippets?}]` (db.php `search_items`, CO Web AI search); items of projects outside the env are dropped |
| `text` | `{path, max}` | indexed text of a project file (path relative to `projects/`), used for file notes |

## Service (until the Bridge module loader runs services)

`provides.services` declares `{"id": "indexer", "tool": "aiwb-assoc", "run": "index", "kind": "schedule", "cron":
"20 * * * *", "windowsTask": "\\AIWorkbench\\AssocIndex"}`. Today the existing Windows task keeps running the indexer, and
`freedom_modules.py enable|disable` (and `sync`) switch that task on and off (a schedule is only re-enabled, it runs at its own time). When the Bridge loader of module services lands, it
schedules `aiwb-assoc index` from this declaration and the Windows task is deleted (`schtasks /Delete /TN
\AIWorkbench\AssocIndex`).
