# Freedom: installation led by the user's own AI (SPEC §4.1 step 2, F2b)

You are the user's own agent (Claude Code or Codex, on the user's own paid subscription). `install/bootstrap.ps1`
installed you, downloaded this release, checked the sha256 of its files against the manifest and unpacked it here; the
user is signed in. From now on **you** install Freedom; the human is called only for:

- sign-ins (Google, hosting panel, the subscription), payments, and secrets (always through the `secret-prompt`
  window, never in the chat);
- choices only the human can make (where the server lives, which Google account, household members);
- a "yes" before anything irreversible or outward-facing (deploy to the hosting, cron lines, sending a report).

Everything else you do yourself, quietly: install programs, run the wizard, read its output, fix and retry.

## Rules

1. **State.** `%LOCALAPPDATA%\AIWorkbench\freedom-bootstrap.json` (cli, invite, lang, version, sources, downloads) and
   the wizard state next to `install.json`: `python install/freedom_setup.py status` shows the steps done and the
   next one. Always start with `status`: you may be a new session continuing an old installation.
2. **Journal.** Append one line per action to `install/agent-install.log` in this folder: UTC time, step, command,
   result (ok / FAIL + one line why), minutes spent. No passwords, tokens, e-mail addresses or hosts in it. The
   journal is the pilot's notes (time per step, where it got stuck) — keep it honest.
3. **Allowed actions** (pre-approved in `install/agent/claude-settings.json`): `winget install` of the packages
   below, `python -m pip install` of the packages below, `python install/freedom_setup.py …`,
   `python install/freedom_release.py verify …`, `python install/freedom_docker.py …`, `python install/freedom_modules.py …`,
   reading files. Anything else: ask the user once, say why.
4. **Language.** Talk to the user in the language from the state file (`lang`), short and concrete. Say what you do
   now and what you need from them; never ask them to type commands you can run.
5. **Never** put secrets in files, the chat or the journal; never disable antivirus, SmartScreen or UAC for good;
   never edit Freedom's code to get past a check — a failing check is a report (step 9).
6. **Windows quirks.** After `winget install` the PATH of your shell is old: call programs by full path
   (`%LOCALAPPDATA%\Programs\Python\Python312\python.exe`) or refresh PATH from the registry. `python` may be the
   Microsoft Store alias that opens the Store — check `python --version` really prints a version. Use the same Python
   for pip and the wizard.

## Steps

| # | Step | What you do | Human |
|---|---|---|---|
| 1 | Programs | `winget install -e --id Python.Python.3.12`, `OpenJS.NodeJS.LTS`, `Git.Git` (skip what is there); `python -m pip install paramiko keyring cryptography`; then `python install/freedom_release.py verify <downloads> --partial` (signatures of the maintainers; `--partial`: the Linux Bridge builds are not downloaded) | Google Drive for desktop: ask them to install and sign in (`winget install -e --id Google.GoogleDrive`, the sign-in is theirs) |
| 2 | Invite | `python install/freedom_setup.py invite <code from the state file>` or `invite --none` when it is empty | — |
| 3 | Preflight | `python install/freedom_setup.py preflight`; fix what it reports (install, pip) and run it again | — |
| 4 | Server choice | Explain the three variants in two lines each: A shared hosting with PHP 8.1+, MariaDB 10.5+, SSH; B a VPS/NAS with Docker; C only this PC (Docker in WSL, Tailscale for the phone). Recommend from what they already have | choose; for A: create the database + user and add the SSH key in the panel (you guide click by click) |
| 5 | init | Collect the answers in the chat, then `freedom_setup.py init --no-input --variant X --site-url … --owner-email … --owner-name … [--ssh-host … --ssh-user … --ssh-key … --site-home … --web-root … --db-name … --db-user …]`. Database password (A): start `python tools/secret-prompt/secret-prompt.py co-mysql --account <db user>` | types the password into the window |
| 6 | Subscriptions | `freedom_setup.py subs --install all` (the second CLI is optional; skip it if they have no second subscription), `subs` to confirm the sign-in | — |
| 7 | Site probe (A) | `freedom_setup.py site-probe`; show the SSH host key fingerprint and ask them to compare it with the panel, then `site-probe --host-key SHA256:…`. B/C: `install/docker/README.md`, `python install/freedom_docker.py up` | compares the fingerprint; B/C: installs Docker if missing (you guide) |
| 8 | Google Cloud | Follow `install/GOOGLE-CLOUD.md` (project, APIs, consent screen, two OAuth clients); `freedom_setup.py google --import-web-client <web json>`; save the desktop client as `oauth-client-desktop.json` next to install.json | signs in to Google Cloud console, clicks where their account is needed |
| 9 | Deploy | `freedom_setup.py site-deploy --build` (plan), show the plan, after their yes `site-deploy --build --apply`; A: give them the two cron lines for the panel. B/C: `freedom_docker.py deploy --build --oauth-client <web json>` | yes; pastes the cron lines |
| 10 | Bridge | `freedom_setup.py bridge` | Bridge tray → Connect Google |
| 11 | CO Desktop | `freedom_setup.py desktop --installer <downloads>\CO-Desktop-Setup-<v>.exe` | SmartScreen: More info → Run anyway; start CO Desktop, sign in with Google |
| 12 | Site | open the site URL for them | signs in with Google; the light turns green after Bridge |
| 13 | PC access | `freedom_setup.py pc-access --apply`, then `--check` | — |
| 14 | Verify | `freedom_setup.py verify` | — |

Step 11 of `PILOT-FRIEND.md` (site agent with `claude setup-token`) is optional: offer it at the end.

## When something fails

- First read the wizard's message and fix what is yours to fix (a missing program, a wrong answer in init, PATH).
  Retry at most twice per cause.
- A failure of Freedom itself (a wizard check that is wrong, a crash, a missing file in the release): make a report.
  With an invite: `python install/freedom_setup.py report --send` prints the exact text and a six-character CHECK;
  **show the text to the user**, and only after their yes run `report --send --yes CHECK`. Without an invite:
  `report` → offer the text for the Freedom forum. Then go on with the steps that do not depend on it.
- `report --answers` shows what the inviter answered (often: "fixed in the next release" → download it and rerun
  `bootstrap.ps1`, it keeps the state).

## Done

`verify` green, `aiwb-smoke` OK. Tell the user what was installed, what is left optional, the total time and the
journal path; ask them to send the journal to the person who invited them if they want to (it has no personal data).
