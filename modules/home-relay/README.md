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

## Python envelope client (0.1.3)

Import RelayClient from home/relay_client.py. Construct RelayClient(env, module, worker_pub),
then seal(action, args) returns an envelope; open(envelope['id'], answer) returns the authenticated result.
The caller supplies an authorised transport and a trusted worker public key. The client neither sends
requests nor grants permission: outward actions still require the application's approval protocol.
Create a new client when the worker key changes. All client key material stays in memory.

Up to six requests may be pending for 120 seconds (monotonic clock). IDs and timestamps are generated
inside seal; no automatic retry occurs. A valid answer, including an authenticated worker error, consumes
the pending request. Malformed answers and plain transport errors leave it pending; discard(id) releases
it on transport failure. Expired, discarded and already consumed IDs cannot be opened. The worker remains
responsible for request replay protection. Plain errors raise transport_error, authenticated failures
raise worker_error; neither exposes untrusted error text. This is encryption only, not policy or transport.

F14b-4 phase 1 in 0.1.4 adds job authorization, status discovery and the encrypted host
operation. Outward approval grants remain a separate phase; the envelope client alone
never authorizes execution.


## Agent app actions (0.1.4, F14b-4 phase 1)

`provides.actions` can declare `via: "home-relay"`. The host operations
`app_relay_status` and `app_relay_call` derive the environment from the live job,
check chat restrictions, write permission, module enablement and personal mode,
and reject outward actions (`relay_approval_unavailable`). The Python client uses
`seal(action, args, app_version=version)`; only ciphertext crosses the site. Agent
jobs running on the site retain the documented F9 limit: encryption runs there.

Status returns the current schema/version and worker public key. Call rechecks
authorization/version and attaches server-owned `dispatch` with actor, job, env,
module, action, version, outward:false, schema and SHA256 of the newline-joined
envelope id/pk/iv/ct. Client dispatch and plaintext args are rejected. Page calls
cannot supply dispatch; the internal relay library `appCall` is not a user op.
The metadata hash is a binding, not a signature against a malicious site.

Workers advertise `status.modules[module].relayAppProtocol=1` themselves; server
status and call refuse older workers, which would not enforce the dispatch.
Each queue claim also checks the current poll capability, preventing an older
concurrent worker or rollback from claiming a queued dispatch.
Deployment order: update worker, then server/core/toolbox; an old client receives
`relay_envelope_required` instead of silently calling a plaintext handler.

Root-owned `/etc/home-relay/modules.d/<module>.json` explicitly opts in safe
non-outward operations. Default is no agent actions:

```json
{"agentActions":{"cal_list":{"action":"list","version":"0.2.0","args":{"type":"object","properties":{"from":{"type":"string"}},"required":["from"],"additionalProperties":false}}}}
```

The worker requires the encrypted `_app:{version}` marker and trusted dispatch
together. It checks env/module/action/version, envelope hash, freshness, replay,
and both site and local schemas; only the root-selected target action and
validated args reach the handler. Never put an outward handler in `agentActions`.
Local module code still enforces resource ownership and action semantics.
Legacy page calls remain unchanged; absence of dispatch does not prove a human
click. This is not a boundary against agents holding authenticated user cookies.

Transport success is logged without args/results and requests page refresh;
only the executor can decrypt and authenticate the actual outcome. No retries
of mutations are automatic. `app_test_fixture.py` is a local test helper only.

Phase 2 remains pending: private durable proposals, encrypted preview to the
human page, and worker-enforced one-use grants binding proposal/action/version/
environment. A Python envelope cannot be rendered by a browser holding a
different ephemeral key, and its 120-second freshness is not a 30-minute card.
No pending outward card is created until this protocol exists.
