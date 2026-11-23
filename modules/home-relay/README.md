# home-relay — the site as postman between a person's pages and their home server

Pages of modules that keep a person's data on the home server (mail now, calendar next) reach it through the site
without the site ever seeing that data. One worker per person on the home server serves all such modules.

```
page of module M (iframe, ext/1)            site (this module's server code)          home server
  ui/relay.js seals {action,args,ts}  ->  M's own user op (e.g. mail_call)  ->  q/<env>/<id>.req
                                          delegates to home-relay lib          worker home-relay@<slug> long-polls
                                                                               the route `worker`, opens the request,
                                                                               hands it to M's handler, seals the answer
```

- **Site** (`server/ops.php`): lib `relay.status` / `relay.call` for the user ops of other modules (a page calls only its
  own module's ops; the module's server code fetches this lib with `co_module_load` of `home-relay`, see
  `concierge-mail/server/ops.php` `co_mail_relay()`), push op `relay_worker_token` (the owner's PC pairs or revokes the
  worker of an environment; only the sha256 of the token is kept), route `worker`. Requests and answers stay in
  `q/<env>/` only until taken, at most 60 s; per module the worker reports a small flat status (numbers and labels for
  a tray, at most 20 keys), never content.
- **Envelope** (`ui/relay.js` <-> `home/relayd.py`): ECDH P-256 of the page's session key and the worker's key
  (`/var/lib/home-relay/<slug>/relay-key.pem`), HKDF-SHA256 with salt `<env>|<module>|<id>` and info
  `freedom-home-relay/1 req|res`, AES-256-GCM, AAD = id. The module name in the salt: the site cannot hand a request
  to another module's handler. The request carries its time inside the sealed part; the worker answers only fresh
  (120 s) and never the same id twice.
- **The home server decides the binding.** The worker serves one person (`home-relay@<slug>`, user `aiwb-<slug>` of
  provider-accounts) of one environment (`AIWB_ENV` in `/etc/home-relay/<slug>.env`, taken from the person's record
  `/etc/freedom-accounts/<slug>.json` by `relayctl pair`). Handlers are what root-owned
  `/etc/home-relay/modules.d/<module>.json` name (`exec`, optional `envFile`, optional `if` = a path that must exist for
  the person); `{slug}` is filled in by the worker. A request chooses only the module; slugs, paths and commands never
  come from the page or the site. The handler gets `HOME_RELAY_ENV`, `HOME_RELAY_SLUG`, `HOME_RELAY_MODULE` and checks
  its own binding too (mail: the mailbox's `AIWB_ENV` must equal `HOME_RELAY_ENV`).
- **Handler protocol**: one long-lived child process per module, JSON lines on stdin/stdout (logs to stderr):
  `{"op":"status"}` -> `{"status":{...}}`; `{"op":"call","action":..,"args":{..}}` -> `{"ok":true,"result":..}` or
  `{"ok":false,"error":"<code>"}`. A handler that dies or takes longer than 30 s is restarted (`handler_down`).
  Calls are answered one after another (a slow call delays the next ones of that worker).
- **Stated limit** (as before for mail): whoever *controls* the site's code can act as a page; closing that needs the
  signed shell with device keys of SPEC §3.4 (F9).
- **Limits for a shared host**: at most 6 waiting calls per environment over all modules (`busy`), one waiting worker
  poll per environment, the database connection is closed while the poll waits; one answer up to 8 MB of ciphertext.

## On the home server

`python home/hub/relayctl.py [--host H --user U --key K] ...`

- `deploy` — worker to `/opt/home-relay`, unit template `home-relay@.service`; running workers restart.
- `pair SLUG` — token from the site (push key of this PC) straight into `home-relay.SLUG.cred`, env file, worker started.
- `unpair SLUG` — stop, revoke on the site, remove the credential.
- `status [SLUG]` — worker state, registered modules, last log lines.
- A module registers itself from its own deploy: `relayctl.register(args, module, exec, env_file=, write=, only_if=)`
  (writes `modules.d/<module>.json` and a drop-in `home-relay@.service.d/<module>.conf` with the folders it may write).

Checks: `python -m pytest home` and `php tests/server.php` (from the module folder).
