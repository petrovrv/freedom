# Coding API helpers

Run `python register.py --tools <tool-library> --env <owner-env>` to inspect the live model
catalog and balance without changing the server. Add `--apply` after installing the API
resource server support to register both helpers and update the balance snapshot.
Only pass `--funded-usd` with a verified USD amount; a euro purchase is not a USD baseline.
The local helper refreshes account snapshots after each call (best effort). Repeat registration to refresh manually.
Keys stay in the owning device keyring, as configured by aiwb-modelbench.
Local API helpers are synchronous and can run in parallel processes. They do not create
CO child chats or Bridge jobs. `wait` and `status` read their local API result records.
The helper mode is owner-only; the existing Slave allowed switch means family sharing.

This module supplies context rules only. It does not own the shared aiwb-subtask or aiwb-modelbench tools. Install them separately. Model resources can be removed in Models independently of this context module.
