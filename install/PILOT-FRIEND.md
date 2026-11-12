# Freedom F2 pilot: a friend installs alone in ≤ 2 h (SPEC Freedom §9 F2)

Who: the first pilot user (a friend of the owner), Windows PC, own Claude Pro/Max or ChatGPT Plus/Pro, own Google
account, own shared hosting with PHP and MariaDB (variant A). The owner does not help by hand; if the friend is stuck,
the help is `freedom_setup.py report` → the friend's own claude.ai / chatgpt.com chat (SPEC §4.3).

Pass: the friend reaches `verify` green and `aiwb-smoke queue` OK in ≤ 2 h, with the time of every step written down.

## Before the pilot (owner's side, once)

1. Access to the source for the friend: a read-only clone of `ai-workbench` (private until F7) or a zip of `main`.
2. Installers. Bridge: `bridge/install.ps1` and `config.json` from the template + `aiwb_install.py bridge-patch`.
   CO Desktop embeds the client part of install.json at build time (`InstallDefaults`): the site address, members,
   project number, and its updater downloads from that site. For the pilot either (a) the owner's signed build, with
   `WebUrl` changed to the friend's site in the CO Desktop settings (updates keep coming from the owner's site — fine
   for a pilot, not for F7), or (b) a build from source with the friend's install.json (`dotnet build`; signing and
   the update channel stay the owner's). A neutral installer that asks for the site at first start is F3/F7 work.
3. Hosting: the friend has one, or buys the cheapest plan with SSH (Hostinger Premium/Business, any cPanel host with SSH
   and MariaDB 10.5+). Without SSH the variant A does not work (FTP-only is not supported by the wizard).
4. Remaining gaps, see the end of this file: decide which are done before the pilot.

## Steps and time budget

| # | Step | Command / place | Who | Budget |
|---|---|---|---|---|
| 0 | Python 3.10+, git, Node 20+ (winget), `pip install paramiko keyring` | terminal | friend | 10 min |
| 1 | `python install/freedom_setup.py preflight` | | wizard | 1 min |
| 2 | Hosting panel: MariaDB database + user, SSH key (panel → SSH access → add public key) | panel | friend | 10 min |
| 3 | `freedom_setup.py init` (asks: site, e-mail, name, SSH, folders, database); password → keyring `aiwb/co-mysql` | | wizard + friend | 5 min |
| 4 | `freedom_setup.py subs --install all`, `subs --login claude` and/or `--login codex` (the last click is the friend's) | | wizard + friend | 10 min |
| 5 | `freedom_setup.py site-probe` (first time: compare the SSH host key with the panel, answer yes) | | wizard + friend | 2 min |
| 6 | Own agent: "follow install/GOOGLE-CLOUD.md" (project, APIs, consent, two clients) | browser | agent + friend | 20 min |
| 7 | `freedom_setup.py site-deploy --build --apply`; the two cron lines into the panel | | wizard + friend | 10 min |
| 8 | Install CO Desktop and Bridge; Bridge → Connect Google (desktop client); `aiwb_install.py bridge-patch` | | friend (+ agent) | 15 min |
| 9 | Site: sign in with Google; light blue → green after Bridge | browser | friend | 5 min |
| 10 | Site agent (optional): `claude setup-token` → secret-prompt `site-claude-token`; `site-deploy --apply --parts secrets,cli` | | friend | 10 min |
| 10a | `freedom_setup.py pc-access --apply`, then `--check` (pc_* for the owner; WSL sandbox for the other household members, 15-30 min, skipped without WSL or members) | | wizard | 2-30 min |
| 11 | `freedom_setup.py verify` (site, sign-in, db.php, then aiwb-smoke / aiwb-e2e) | | wizard | 5 min |
|   | **Total** | | | **≈ 1 h 45 min** |

Write down per step: start, end, what was unclear, where the report was used. These notes become fixes of the wizard.

## Rehearsal in a VM (before the friend, or instead of a second computer)

1. Windows 11 VM (Hyper-V "Quick Create" or a 90-day evaluation image), nothing installed; a test Google account; a
   test hosting account (or a second database + subdomain on the owner's hosting, e.g. `freedom-test.<domain>`, with its
   own folder `~/domains/freedom-test.<domain>`; **never** the owner's `co-agent/` home — the test must use another
   hosting user, because `~/co-agent` is per user).
2. Go through the table above with a stopwatch; every FAIL → `report` → check that the report is enough to find the
   cause without the owner.
3. Variant A on cPanel: the same, with a cPanel host (`~/public_html`): the server finds its home there since F2
   (`co_agent_home`, `cron/boot.php`); the sign-in endpoints (`co-web-auth/auth/lib.php`, protected) still assume the
   hPanel depth — see the protected list.

## Gaps known on 11.11.2026 (not blocking the rehearsal on hPanel)

- `co-agent/executor/run.sh` (protected) has the Node path of Hostinger/CloudLinux (`/opt/alt/alt-nodejs24`); on other
  hosts the site agent needs `~/co-agent/bin` in PATH (the wizard makes `~/co-agent/bin/node`). Protected branch
  `freedom-f2-protected`.
- `co-web-auth/auth/lib.php` (protected) reads `~/co-auth/config.php` at the hPanel depth only (cPanel layout breaks the
  sign-in). Same branch.
- `co-web/deploy_web.py`, `co-web-auth/deploy_auth.py` (protected) still connect through the owner's personal
  `hz.py`; the wizard does their job for new installs. Same branch: they use `aiwb_install.ssh_connect()`.
- Tool library: `aiwb-smoke`, `aiwb-e2e` connect through `hz.py` and default to the owner's site; they need the
  fallback to install.json before a friend can run them (tool library, not this repo).
- `co_queue_handover` and CO Web know the two executors as `home-main` and `site`: these are role names of the protocol,
  the same in every install, not personal bindings. A second Bridge PC per install is not in v1.
- CO Desktop / CO Resident: no neutral installer yet (site embedded at build time, updates from the build's site).
- Pub/Sub, Drive and sign-in need the Google project of step 6; there is no variant without Google in v1 (SPEC §4).

## Variants B and C (Docker, F3b)

A friend without shared hosting uses a VPS/NAS (B) or only the own PC (C): the same server in Docker, see
`install/docker/README.md`. Steps 2, 5 and 7 of the table become: `"variant": "B"` or `"C"` in install.json (no SSH or
hosting folders), `python install/freedom_docker.py up`, `deploy --build --oauth-client <web client JSON>`, `verify`;
no cron lines for a panel (the cron sidecar runs them). C from the phone needs Tailscale (`tailscale serve`), and the
Google web client needs `<site.url>/auth/callback.php` as a redirect URI. Budget on top of the table: Docker install
15 min (VPS: `get.docker.com`; Windows: Docker Engine in WSL 2), first `up` 5–10 min (image build).
