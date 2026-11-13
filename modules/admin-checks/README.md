# Module admin-checks (administrator checks)

Checks the owner of a workbench runs: `aiwb-evals` (golden tasks through the real job queue, after every Bridge
release or big CO Web change) and `aiwb-backup-check` (weekly restore test of the nightly database dump; push on
failure). Both tools stay in the tool library; the module lists them in `provides.tools`, so a switched-off module
makes the tool server refuse them.

| Layer | Here |
|---|---|
| `tools` | `aiwb-evals` ≥ 0.1.1, `aiwb-backup-check` ≥ 0.1.1 |
| `service` | `backup-check`, kind `schedule` (Sunday 05:00, `aiwb-backup-check run --notify`); today the Windows task `\AIWorkbench\BackupCheck` (enable/disable switch it on and off; a schedule is never started at enable) — Bridge's module loader takes it over when it lands |
| secrets | `aiwb/co-push` (the host key of the core, shared) |

Not in this module, by decision (F10, 12.11.2026): `aiwb-chat` and `aiwb-dossier`. They are thin command lines over
the core's own data (chat messages, protocol search, the dossier) and the core itself relies on them (the community
outbound filter reads the dossier, agents quote earlier messages), so they ship with the core tools of the export.

    python install/freedom_modules.py install modules/admin-checks --enable
