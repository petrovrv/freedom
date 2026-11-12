# Freedom variants B and C: the server in Docker

SPEC Freedom §4, stage F3b. Variant A (shared hosting) is `install/freedom_setup.py site-deploy`. Variants B and C run
the same server in Docker:

| Variant | Where | Web | HTTPS |
|---|---|---|---|
| **B** | VPS or NAS with Docker | `https://<your domain>` | Caddy gets the certificate itself (Let's Encrypt / ZeroSSL) |
| **C** | only your computer (Windows with WSL, Mac, Linux) | `http://localhost:18080` on this computer | from outside through Tailscale (`tailscale serve` / `funnel`) |

The stack (`compose.yaml` + `compose.variant-b.yaml` / `compose.variant-c.yaml`) is the hosting of variant A in
containers:

| Service | Image | What |
|---|---|---|
| `app` | built from `Dockerfile` (php:8.3-apache + Node 24 + MariaDB client) | Apache + PHP (mysqli, openssl, curl, mbstring, zlib) as user `co`, `HOME=/home/co`, cPanel layout: `~/public_html` (CO Web, `agent/`, `auth/`), `~/co-agent` (cron, db, executor, secrets), `~/co-auth`. Apache answers TLS on 443 inside the stack (the `.htaccess` of CO Web sends plain HTTP to https). The site agent (`co-agent/executor`, Node) is started by PHP like on the hosting. |
| `cron` | the same image | the two cron lines of the hosting panel (`freedom_setup.CRON_LINES`: `drive-sync.php` every minute, `watch-cron.php` every 5 min) run by `freedom-cron.php`; shares the process list of `app` |
| `db` | `mariadb:11` | the database; passwords only through Docker secrets |
| `caddy` | `caddy:2` | reverse proxy: B on 80/443 with automatic HTTPS, C on `127.0.0.1:18080` plain HTTP |

The image holds no AI Workbench code. `freedom_docker.py deploy` writes the code into the volume `home` with the
same repo → server map as variant A (`freedom_setup.layout()`), and uses the same secret writers (`db.json`, `my.cnf`,
`push-notify-key`, `~/co-auth/config.php`, all 0600 in 0700 folders), the same schema step (`db/schema.sql`, then the
members of install.json through `db/members.php`). An update of the code is `deploy` again, not an image rebuild.

## Requirements

- Docker Engine 24+ with the compose v2 plugin (`docker compose version`).
  - VPS: Debian/Ubuntu, `curl -fsSL https://get.docker.com | sh`; 2 vCPU, 2 GB RAM, 10 GB disk are enough.
  - NAS: Synology Container Manager / QNAP Container Station / Unraid with compose; x86-64 or arm64.
  - Windows (variant C): Docker Engine inside WSL 2 (or Docker Desktop with the WSL backend). `freedom_docker.py`
    runs on Windows and calls `wsl.exe -e docker ...` (`docker.cli: "wsl"`, the default on Windows).
  - Mac (variant C): Docker Desktop, OrbStack or Colima (`docker.cli: "docker"`).
- Python 3.10+ on the computer that runs `freedom_docker.py` (the Docker host itself, or the Windows side of WSL).
- Node 20+ and npm on that computer to build CO Web (`deploy --build`).
- Variant B: a domain whose A/AAAA record points at the server, ports 80 and 443 open (Caddy needs both for the
  certificate).
- Variant C from outside: Tailscale on the computer (and on the phone).
- A Google Cloud project with a **web** OAuth client (`install/GOOGLE-CLOUD.md`); without it everything runs except
  the sign-in.

## install.json

The same file as variant A (`install/install.example.json`), with `"variant": "B"` or `"C"`. SSH, `site.home` and
`site.webRoot` are not needed (the stack sets `/home/co` and `/home/co/public_html`; `site.db.host` becomes `db`).
Optional `docker` object, defaults in brackets:

| Key | Meaning |
|---|---|
| `project` (`freedom`) | compose project name; containers, volumes and the image `<project>-app:local` carry it |
| `cli` (`auto`) | `docker` (Docker on this computer; Linux and Mac only) or `wsl` (Windows: Docker of the default WSL distribution); `auto` = `wsl` on Windows |
| `stackDir` (`~/.local/share/aiwb-freedom/<project>` on the Docker host) | compose files, `.env`, `Caddyfile`, `secrets/` |
| `bind` (B `0.0.0.0`, C `127.0.0.1`) | address of the published ports; C accepts only `127.0.0.1`, `localhost`, `::1` (other devices: Tailscale or variant B); `verify` connects to it when it is a specific address |
| `httpPort` / `httpsPort` (B 80/443, C 18080/-) | published ports |
| `tls` (`auto`) | B: `auto` = ACME, `internal` = Caddy's own CA (tests, a LAN name), or an e-mail for ACME |
| `acmeEmail` | contact e-mail for Let's Encrypt (optional) |
| `keyring` (`true`) | also keep the database password, push key and OAuth client in the OS keyring of this computer (`aiwb/co-mysql`, `aiwb/co-push`, `aiwb/google-oauth-web`), so Bridge and the tools find them; on a host without a keyring backend (headless VPS) it is switched off with a warning and a keyring error never stops a step |
| `cpus` (2) | CPU limit of `app` (db and cron get half, Caddy 0.5); every limit is capped at the CPUs of the Docker host (`nproc`) |

`site.url`: B — `https://co.example.org`; C — the Tailscale name `https://<pc>.<tailnet>.ts.net`, or
`http://localhost:18080` when the site is only used on this computer (variant C only; `localhost`, not `127.0.0.1`:
Google accepts plain-HTTP redirects and browsers keep the `Secure` / `__Host-` sign-in cookies only for that name).

## Steps

```
python install/freedom_docker.py plan        # what goes where; no changes
python install/freedom_docker.py up          # stack folder + secrets (once, 0600) + docker compose up -d --build
python install/freedom_docker.py deploy --build --oauth-client client_secret_XXXX.json
                                             # CO Web build, site files, config.php, auth config, secrets, schema,
                                             # members, cron lines; delete the downloaded client file afterwards
python install/freedom_docker.py verify      # see below; --php-tests --agent for the full check
python install/freedom_docker.py backup      # database dump
python install/freedom_docker.py down        # stop (data stays); --volumes deletes the data, --rmi the image
```

The wizard hands variants B/C to `freedom_docker.py`, so the usual wizard steps work for B/C too: `freedom_setup.py init --variant C`,
`site-probe` (Docker), `site-deploy --diff/--apply` (up + deploy), `verify` (the checks below, then aiwb-smoke/e2e).

`deploy` options: `--diff` (read only), `--parts agent,auth,web,home,corporate,config,secrets,schema,cron[,cli]`,
`--rewrite-config` (replace a changed `config.php`), `--no-bak`. `--parts ...,cli` installs `claude` and `codex` for
the site agent into the volume (`~/co-agent/cli`); then put a `claude setup-token` into keyring
`aiwb/site-claude-token` (or the stack secret `site_claude_token`) and run `deploy --parts secrets`.

`verify` checks: containers running (db healthy); CO Web index through Caddy (B: TLS chain, with `tls internal`
against Caddy's root CA); `/auth/login.php` redirects to Google with `redirect_uri = <site.url>/auth/callback.php`;
`/agent/ping.php` answers (403 `owner_only` before anyone signed in); `db.php` `stats` with the host key (200) and
without it (403); the members of install.json in the database; the cron sidecar ran a script with rc 0
(`--wait-cron 330`; `drive-sync.php` exits 1 with "session file missing" until the owner signed in to
Google, `watch-cron.php` runs every 5 minutes). `--agent` starts the site agent the way PHP does (`co_site_agent_kick`) and shows its log — inside
the stack nobody is signed in, so it waits for the owner's Google session and the CLIs report no login.
`--php-tests` runs `co-agent/tests` in the app container against a throwaway database (own user, dropped afterwards).

## Google sign-in (what you must do)

Google only accepts sign-in redirects it knows. In Google Cloud → APIs & Services → Credentials → your **web** client:

- Authorized JavaScript origins: your `site.url` (e.g. `https://co.example.org`, `https://pc.tailnet.ts.net`, or
  `http://localhost:18080`).
- Authorized redirect URIs: `<site.url>/auth/callback.php`.

`http://localhost` is allowed by Google for web clients; any other plain-HTTP address is not, so a site reached from
other devices needs HTTPS (variant B, or Tailscale for C). The address in the browser must be exactly `site.url`:
the sign-in cookie is bound to it and `/auth/*` refuses requests from another origin.

## Variant C with Tailscale

1. Install Tailscale on the computer, sign in, enable MagicDNS and HTTPS certificates in the admin console
   (DNS → HTTPS Certificates).
2. `tailscale serve --bg --https=443 http://127.0.0.1:18080` — the site is `https://<pc>.<tailnet>.ts.net` for your
   own devices in the tailnet (phone with the Tailscale app). `tailscale serve status` shows it.
3. Only if people outside your tailnet must reach it (not needed for the family on their own Tailscale accounts
   shared to the node): `tailscale funnel --bg 443 http://127.0.0.1:18080`, which makes it public on the Internet.
4. Put that `https://...ts.net` address into install.json `site.url`, the Google client (origins and redirect URI),
   then `freedom_docker.py deploy --build --parts web,config --rewrite-config` (CO Web embeds the address at build
   time) and, if the auth config was written with the old address, delete `~/co-auth/config.php` in the volume
   (`docker compose -p <project> exec -u co app rm /home/co/co-auth/config.php`) and `deploy --parts config`.

The site agent does not go through Tailscale: it calls `db.php` inside the stack (`https://app`, `config.php`
`site_url`), so it works whatever the public name is.

## Backups

`freedom_docker.py backup` writes `<stackDir>/backups/<db>-<time>.sql.gz` on the Docker host (mysqldump of the db
container with `--single-transaction`). By hand:

```
docker compose -p freedom exec -T db sh -c 'MYSQL_PWD="$(cat /run/secrets/db_root_password)" mariadb-dump -uroot \
  --single-transaction --routines --triggers "$MARIADB_DATABASE"' | gzip > co-$(date +%F).sql.gz
```

Restore: `gunzip -c FILE | docker compose -p freedom exec -T db sh -c 'MYSQL_PWD="$(cat /run/secrets/db_root_password)" mariadb -uroot "$MARIADB_DATABASE"'`.
Also keep `<stackDir>/secrets/` (the push key and the database passwords) and, if you want the site agent's state, the
volume `<project>_home` (`docker run --rm -v <project>_home:/v -v $PWD:/b alpine tar czf /b/home.tgz -C /v .`).
For a nightly dump put `python install/freedom_docker.py backup` into cron / Task Scheduler and copy the folder away.

## Updates

- Code of the site (most updates): `git pull`, then `freedom_docker.py deploy --build`. Changed files keep a
  `.bak-<time>` copy in the volume; `db/schema.sql` is idempotent and runs every deploy.
- Image (PHP, Node, Apache security fixes): `freedom_docker.py up` (rebuilds with `--build`; add `docker compose -p
  <project> build --pull` first for new base images). The volume and the database stay.
- MariaDB: the `mariadb:11` tag follows 11.x; `MARIADB_AUTO_UPGRADE=1` upgrades the system tables on start. Make a
  `backup` first.

## Troubleshooting

| Symptom | Check / fix |
|---|---|
| `up` fails at the build | `docker compose -p <project> build app` shows the step; no Internet for apt/npm on a NAS → set a proxy for Docker |
| `db` never healthy | `docker compose -p <project> logs db`; a volume made with other passwords: the secrets folder and the volume must belong together (`down --volumes` starts over, deletes the data) |
| HTTP 502 from Caddy | `app` not running: `docker compose -p <project> logs app` |
| index 200 but the page is blank | `deploy --build` was not run (no `co-web/dist`) |
| sign-in: "redirect_uri_mismatch" | the Google client lacks `<site.url>/auth/callback.php` (section Google sign-in) |
| sign-in loops back / `bad_origin` | the address in the browser is not `site.url` (e.g. `127.0.0.1` vs `localhost`) |
| B: no certificate | DNS of the domain, ports 80/443 reachable from the Internet, `docker compose -p <project> logs caddy` |
| db.php 403 for Bridge | Bridge uses keyring `aiwb/co-push`; it must equal `<stackDir>/secrets/push_key` |
| cron | `docker compose -p <project> logs cron` shows every run with its rc and output |
| site agent | `docker compose -p <project> exec -u co app tail ~/co-agent/executor/logs/daemon.out` and the dated log next to it |
| Windows: `wsl.exe` path errors | run `freedom_docker.py` from a normal folder (it calls WSL with the working directory `C:\`) |
| ports busy | change `docker.httpPort` / `httpsPort` in install.json, then `up` |

`freedom_setup.py report` makes a help request without secrets, names or hosts (SPEC §4.3), as in variant A.
