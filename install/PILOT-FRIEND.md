# Freedom F2 pilot: a friend installs alone in ≤ 2 h (SPEC Freedom §9 F2)

Who: a new user (the first pilot is a friend of a maintainer), Windows 10/11 PC, own Claude Pro/Max or ChatGPT
Plus/Pro, own Google account, and a place for the server: shared hosting with PHP, MariaDB and SSH (variant A), a
VPS/NAS with Docker (B) or only this PC with Docker in WSL (C). Nobody helps by hand; when stuck, the help is
`freedom_setup.py report` → the user's own claude.ai / chatgpt.com chat (SPEC §4.3).

Pass: `verify` green and `aiwb-smoke queue` OK in ≤ 2 h, with the time of every step written down.

## What the user gets

One link: the latest release page of the Freedom repository on GitHub (`<repository>/releases/latest`). From
it, into one folder:

| File | What it is |
|---|---|
| `freedom-<version>-src.tar.gz` | the setup wizard, the server (CO Web, sign-in, database API), Bridge sources, modules, core tools |
| `CO-Desktop-Setup-<app version>.exe` | CO Desktop for Windows, **neutral**: no site, people or Google project inside |
| `freedom-manifest.json` | sizes and sha256 of the files, signed by the Freedom maintainers (`install/maintainers.json`) |

- **CO Desktop** reads the `install.json` the wizard writes (`%LOCALAPPDATA%\AIWorkbench\install.json`) and the
  desktop OAuth client next to it (`oauth-client-desktop.json`). Installed before the wizard ran, it asks for the site,
  the owner's Google account and the client file at first start. Its updates come from the user's own site through
  the user's own Bridge (`freedom_setup.py desktop-update`), never from the maintainers' sites.
- **Bridge** has no binary in the release: `freedom_setup.py bridge` builds it on the PC from the sources (the C#
  compiler of .NET Framework, part of Windows), signs it with a release key created on this PC, writes `config.json`
  from `install.json` and registers the logon task.
- **Not Authenticode-signed.** SmartScreen shows "Windows protected your PC" → More info → Run anyway. Check the file
  first: `freedom_setup.py desktop --installer <exe>` verifies the manifest signature and the hash, then installs.
- The trust list of the maintainers' keys comes with the archive: download the three files from the release page of
  the repository itself, not from an unknown mirror; later releases are checked against the list you already have.
- `git clone <repository URL>` instead of the archive is for updates and development (same steps).

## Steps and time budget

| # | Step | Command / place | Who | Budget |
|---|---|---|---|---|
| 0 | Python 3.10+, Node 20+, git (`winget install Python.Python.3.12 OpenJS.NodeJS.LTS Git.Git`), Google Drive for desktop (signed in); download the three files, `tar -xf freedom-<v>-src.tar.gz`, terminal in the folder; `python -m pip install paramiko keyring cryptography` | terminal | user | 15 min |
| 1 | `python install/freedom_setup.py preflight` | | wizard | 1 min |
| 2 | Variant A: hosting panel → MariaDB database + user, SSH key (panel → SSH access → add public key). B/C: install Docker (`install/docker/README.md`) | panel | user | 10 min |
| 3 | `freedom_setup.py init` (asks: site, e-mail, name, SSH, folders, database; `--variant C` for Docker on this PC); password → keyring `aiwb/co-mysql` | | wizard + user | 5 min |
| 4 | `freedom_setup.py subs --install all`, `subs --login claude` and/or `--login codex` (the last click is the user's) | | wizard + user | 10 min |
| 5 | A: `freedom_setup.py site-probe` (first time: compare the SSH host key with the panel, answer yes) | | wizard + user | 2 min |
| 6 | Own agent: "follow install/GOOGLE-CLOUD.md" (project, APIs, consent, two clients); `freedom_setup.py google --import-web-client <web json>`; the desktop client saved as `oauth-client-desktop.json` next to install.json | browser | agent + user | 20 min |
| 7 | `freedom_setup.py site-deploy --build --apply`; A: the two cron lines into the panel | | wizard + user | 10 min |
| 8 | `freedom_setup.py bridge` (config, own key, build, logon task), then Bridge tray → Connect Google | | wizard + user | 10 min |
| 9 | `freedom_setup.py desktop --installer CO-Desktop-Setup-<v>.exe` (checks, installs), start CO Desktop, sign in with Google | | wizard + user | 10 min |
| 10 | Site: sign in with Google; light blue → green after Bridge | browser | user | 5 min |
| 11 | Site agent (optional): `claude setup-token` → secret-prompt `site-claude-token`; `site-deploy --apply --parts secrets,cli` | | user | 10 min |
| 11a | `freedom_setup.py pc-access --apply`, then `--check` (pc_* for the owner; WSL sandbox for the other household members, 15-30 min, skipped without WSL or members) | | wizard | 2-30 min |
| 12 | `freedom_setup.py verify` (site, sign-in, db.php, then `tools/aiwb-smoke`, `tools/aiwb-e2e` of the release) | | wizard | 5 min |
|   | **Total** | | | **≈ 1 h 55 min** |

`freedom_setup.py status` shows the steps done and the next one. Write down per step: start, end, what was unclear,
where the report was used. These notes become fixes of the wizard.

Later releases: download the new `CO-Desktop-Setup-<v>.exe` + manifest and run `freedom_setup.py desktop-update
--installer <exe>` once: every paired computer of the household gets it from the user's own site. Server and Bridge:
unpack (or `git pull`) the new sources, `site-deploy --apply`, `bridge` (builds and signs the new Bridge; the launcher
switches when no job runs).

## Rehearsal (before a pilot, or instead of a second computer)

1. Clean Windows: Windows Sandbox (Windows 11 Pro: `Enable-WindowsOptionalFeature -Online -FeatureName
   Containers-DisposableClientVM`, administrator + restart) or a Hyper-V VM ("Quick Create" or a 90-day evaluation
   image), nothing installed. A test Google account. Server: variant C (Docker in WSL on the host or in the VM) or a
   test hosting account / second database + subdomain with its **own hosting user** (never the home of a live install:
   `~/co-agent` is per user).
2. Go through the table with a stopwatch; every FAIL → `report` → check that the report is enough to find the cause.
3. Without a clean Windows the client part can be rehearsed in a separate profile on a used PC: `AIWB_INSTALL`
   pointing to a new folder, `AIWB_BRIDGE_RELEASE_KEY` / `AIWB_BRIDGE_TASK` to other names, `LOCALAPPDATA` to a temp
   folder for `bridge/install.ps1` (it then neither touches the live Bridge nor its key).

## Known limits (13.11.2026)

- Pub/Sub, Drive and sign-in need the Google project of step 6; there is no variant without Google in v1 (SPEC §4).
- `co_queue_handover` and CO Web know the two executors as `home-main` and `site`: role names of the protocol, the
  same in every install. A second Bridge PC per install is not in v1.
- macOS: CO Resident and Bridge .NET 8 (`bridge/net8`) have no neutral installer yet. Linux: Bridge only, see the
  section "Linux" below.
- Variant A on cPanel uses `~/public_html`; the server finds its home there (`co_agent_home`, `cron/boot.php`,
  `co-web-auth/auth/lib.php` look at several depths).

## Variants B and C (Docker, F3b)

A user without shared hosting uses a VPS/NAS (B) or only the own PC (C): the same server in Docker, see
`install/docker/README.md`. Steps 2, 5 and 7 of the table become: `"variant": "B"` or `"C"` in install.json (no SSH or
hosting folders), `python install/freedom_docker.py up`, `deploy --build --oauth-client <web client JSON>`, `verify`;
no cron lines for a panel (the cron sidecar runs them). C from the phone needs Tailscale (`tailscale serve`), and the
Google web client needs `<site.url>/auth/callback.php` as a redirect URI. Budget on top of the table: Docker install
15 min (VPS: `get.docker.com`; Windows: Docker Engine in WSL 2), first `up` 5–10 min (image build).

## Linux: Bridge on a computer or server without Windows (F3a)

Who: an install that already has its site (any variant) and adds a Linux machine as the computer that runs the jobs:
a home server, a VPS, a NAS with a Linux shell. Debian 12+, Ubuntu 22.04+, Fedora 40+; x86_64 or aarch64; systemd.
Bridge there is the .NET 8 build (`bridge/net8`, headless: no tray, no CO Desktop), installed by `bridge/install.sh`
for one Linux account (not root).

| # | Step | Command | Who | Budget |
|---|---|---|---|---|
| L0 | Into one folder of the server: `freedom-<v>-src.tar.gz`, `freedom-manifest.json`, `BridgeAgent-<bridge>-linux-x64` (aarch64: `-linux-arm64`) of the release page; `tar -xzf freedom-<v>-src.tar.gz` | ssh | user | 5 min |
| L1 | `install.json` of the install → `~/.config/aiwb/install.json` (copy from the Windows PC, or `python3 freedom-<v>/install/freedom_setup.py init`); a computer without Google Drive works in `~/AIWorkbench` | | user | 2 min |
| L2 | `python3 freedom-<v>/install/freedom_setup.py bridge --deps` (sudo asks for the password once: packages, linger) | | wizard | 5-10 min |
| L3 | The key of this host for the site's `db.php` into the keyring: `secret-tool store --label=aiwb/co-push service aiwb/co-push username default`, paste, Enter, Ctrl+D | ssh | user | 1 min |
| L4 | Agent CLIs: `freedom_setup.py subs --install all`, then `claude` / `codex login` in the ssh session (the sign-in is the user's) | ssh | user | 10 min |
| L5 | `systemctl --user status aiworkbench-bridge`, `tail ~/.local/share/AIWorkbenchBridge/logs/$(date -u +%F).log`: "Database queue started", no "claim failed" | ssh | user | 1 min |

What `bridge/install.sh` does (`bash bridge/install.sh --help`):

- **Secrets stay in the keyring.** A desktop session's Secret Service (GNOME Keyring, KWallet) is used as it is. A
  server without a desktop gets gnome-keyring as a systemd user service (`aiwb-keyring.service`; D-Bus activation of
  `org.freedesktop.secrets` points at it, the distribution's own desktop keyring units are masked for this account);
  its login keyring is unlocked at start with a random password in `~/.config/aiwb-keyring/unlock` (0600). That file is
  the one secret outside the keyring, as the logon password is on a desktop: whoever reads files of this account reads
  its secrets, so the account is the boundary (a dedicated account for Bridge, disk encryption on the server). Bridge,
  `secret-tool` and Python `keyring` (tools, the wizard) see the same entries `aiwb/<name>`, account `default`.
- **Build:** the release's `BridgeAgent-<bridge>-linux-<arch>` next to the unpacked folder, checked against
  `freedom-manifest.json` (as the CO Desktop installer); else `--build` from the sources with the .NET 8 SDK
  (`sudo apt install dotnet-sdk-8.0` / `sudo dnf install dotnet-sdk-8.0`). Self-contained: no .NET runtime needed.
- **Updates — the channel of Windows:** the build is signed with this computer's own release key (ECDSA P-256, private
  part in the keyring as `aiwb/bridge-release`) into `~/.local/share/AIWorkbenchBridge/updates`; the launcher
  (`~/.local/lib/aiworkbench-bridge/BridgeAgent --launcher`) starts the newest signed version, asks Bridge to drain when
  a newer one arrives, and goes back to the previous version if the new one dies within 2 min. A later release: unpack
  it and run `freedom_setup.py bridge` again. `--release-key <public key> --channel <dir>` trusts another computer's
  key and channel instead.
- **Service:** `aiworkbench-bridge.service` of the user's systemd (`Restart=always`) and `loginctl enable-linger`, so
  it starts at boot without a login and survives logouts. A user service, not a system one: Bridge runs the agent CLIs
  with their sign-in in this account's home, as the logon task on Windows does, and no part runs as root. Dedicated
  account: `sudo useradd -m aiwb && sudo -iu aiwb`, then the steps above as that user.
- `config.json` (`~/.local/share/AIWorkbenchBridge/config.json`) comes from `install.json`: agent CLIs without `.exe`,
  `chatFolderWaitSeconds: 0` (no chat folder comes through Drive for desktop), `--host-id ID` for the host name.
  `bash bridge/install.sh --uninstall` removes the services (config, logs, keyring and channel stay).

Limits (13.11.2026): the server's queue knows the executors `home-main` and `site` only (`db.php` `DB_QUEUE_HOSTS`), so
until executors can be registered a Linux Bridge takes the role `home-main` — instead of the Windows PC's Bridge, not
next to it (two Bridges with one host id share its jobs). Not on Linux: tray, CO Desktop / devices (device tools need
the Windows key store), `runAs`, Google sign-in from the tray (`BridgeAgent --google-connect <client.json>` needs a
browser on that computer; Bridge works through the database queue without it).
