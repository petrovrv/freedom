# Module docs-search (document search)

Full-text search over the documents of all projects. Until CO Web 0.102 the table `docs` and the ops `docs_*` /
`search_docs` were part of the core's `db.php` (schema 7); now they belong here and reach the core only through ext/1
`search.provider` (see `modules/assoc-search/README.md` for the hooks).

| Layer | Here |
|---|---|
| `search.provider` `docs` | hook `items`: best documents for CO Web AI search (core op `search_items`); hook `text`: indexed text of a project file for the file notes of `cron/files-cron.php` |
| server (standard module) | `server/ops.php` — host ops `docs_state`, `docs_put`, `docs_delete` (indexer), `docs_search` (agents); user op `search_docs` (CO Web before F10); install hook |
| data | table `m_docs_search_docs`; the install hook renames the pre-F10 core table `docs` (no re-indexing); remove `--data erase` drops it |
| service | tool `aiwb-docs` (`index`), hourly: Windows task `\AIWorkbench\DocsIndex` |
| tools | `aiwb-docs` 0.2.0+ (unchanged wire, gzipped host calls are allowed for host ops of modules) |

Install (same deploy step as the new core):

    python install/freedom_modules.py install modules/docs-search --enable

Disable: AI search shows chats only, file notes use the Drive text of small files only, the indexer gets `unknown_op`,
the table stays.

## Service (until the Bridge module loader runs services)

`provides.services` declares `{"id": "indexer", "tool": "aiwb-docs", "run": "index", "kind": "schedule", "cron":
"0 * * * *", "windowsTask": "\\AIWorkbench\\DocsIndex"}`. The existing Windows task keeps running until the Bridge loader of
module services schedules `aiwb-docs index` from this declaration; then delete the task (`schtasks /Delete /TN
\AIWorkbench\DocsIndex`).
