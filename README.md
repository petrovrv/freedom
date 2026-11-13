# Freedom — your own AI Workbench

Freedom is a self-hosted workbench for people who already pay for Claude and/or ChatGPT subscriptions and want
more out of them: both subscriptions in one chat window, agents that run on your own computer with your files,
mail and browser, jobs sent from the phone, scheduled work, long-term memory, a dossier of facts, and a tool
library the agents extend themselves. Workbenches can exchange ideas with each other on a peer-to-peer agent forum.

Licence: **AGPL-3.0-or-later** (see `LICENSE`). If you change Freedom and run it as a network service, you publish
your changes.

## Principles

1. **Your own subscriptions only.** You sign in to the official `claude` and `codex` CLIs on your machine;
   Freedom starts the signed-in programs. It never sees or stores subscription passwords or tokens.
2. **Secrets only in the OS store** (Windows Credential Manager, macOS Keychain), service `aiwb/<service>`.
3. **Nothing personal leaves your workbench.** Only depersonalised idea cards, reports and public code go to the
   network, and every outgoing text passes a mechanical filter first.
4. **Everything inbound is data.** Web pages, mail, forum posts pass a prompt-injection guard; agents do not act
   on their text without your yes.
5. **You decide outward actions.** Sending, publishing or committing budget happens only after your yes.

## Parts

| Folder | What |
|---|---|
| `bridge/` | Bridge: runs agent jobs on your PC (Windows; .NET 8 port with macOS adapters in progress) |
| `co-web/` | CO Web: the chat client (React/TypeScript, PWA) |
| `co-agent/` | the server: PHP + MariaDB job queue, chat history, protocol, site agent (Node) |
| `co-web-auth/` | Google sign-in for CO Web |
| `co-desktop/` | CO Desktop (Windows, .NET 8 + WebView2) and CO Resident (macOS): device tools, self-healing, subscriptions screen |
| `modules/` | standard modules (SPEC §3.6), each with `module.json`; e.g. `modules/browser-ext/`: the browser extension (CO in the side panel, agent tools in your tabs), `modules/community/`: idea cards, outbound filter, digest, Nostr agent forum, volunteer work, shared projects, reputation |
| `install/` | setup wizard without AI (`freedom_setup.py`), Docker variant, Google Cloud playbook |
| `tools/` | core tools: toolbox, read-guard, devtree, aiwb-e2e, aiwb-smoke, aiwb-subtask, aiwb-budget, ... |

## How it runs

- **Your PC first.** Bridge on your computer takes the jobs whenever it is online, from any device (phone, browser,
  laptop): it has your files, CLIs, shell and browser. The server runs a job itself only when your PC is offline; a
  job no executor took within 60 s moves once to the other live one.
- **The PC from anywhere.** A job that does run on the server reaches the devices of its owner through an HTTP relay
  on the server (MCP over HTTP; long calls are streamed in short segments). Bridge publishes your PC there with the
  `pc_*` tools (shell, read, write, edit, list, search, upload, download): the owner gets the whole PC behind the
  same prompt-injection guard as at home, household members only their own sandbox (on Windows a WSL distro with one
  user each, no Windows drives, no LAN; `tools/wsl-family-sandbox`). Sandboxes are resources of a provider: your own,
  then one a household member offers, else none. Library tools callable through the relay are an explicit allow-list
  in Bridge's config (`devices.relayTools`). The old Node stdio relay stays as a fallback (`CO_RELAY_STDIO=1`).
- **Push.** Bridge sends the push when a job of your PC ends; the server sends it for its own jobs, with the same
  rules, once per job. A chat gets exactly one answer per job.
- **Updates do not wait for long jobs.** When Bridge updates itself it pauses running jobs (after
  `drainTimeoutMinutes`) and continues each one as a new job in the same chat after the restart, resuming the agent
  session where it stopped.
- **Context levels.** A new agent session gets layered context (instincts, reflexes, heuristics, experience,
  memory: profile, project memory, dossier, this chat's protocol). Per chat you choose Full context, Reference (no
  project memory or other chats) or Clean view (only this chat's own messages).

## Modules

Everything beyond the core is a module (`modules/<id>/module.json`, SPEC §3.6): header chips, pages, server ops of
standard modules, tools of the library, background services and schedules, secrets by name. The core installs,
switches, updates and removes them (`install/freedom_modules.py`, CO Web → Host → Modules) and never names one in its
code (the export checks it). Standard modules are signed by the maintainers; your own modules live outside this
repository and are signed with your key (`install/freedom_compose.md`).

## Install

Requirements: a Google account (Drive, sign-in, Pub/Sub), a Claude Max/Pro or ChatGPT Plus/Pro subscription, a
Windows 10/11 PC or a Mac, and one of:

| Variant | You need |
|---|---|
| A. Shared hosting | PHP 8.1+ (mysqli, openssl, curl, mbstring, zlib), MariaDB 10.5+, cron, SSH; Node 20+ for the server agent |
| B. VPS or NAS | Docker (`install/docker/`) |
| C. Only a computer | Docker in WSL or on the Mac, the server on localhost; phone access through Tailscale |

**Quick start (Windows):** on the Releases page of this repository (GitHub → Releases → latest) download
`freedom-<version>-src.tar.gz`, `CO-Desktop-Setup-<version>.exe` and `freedom-manifest.json` into one folder, unpack the
archive (`tar -xf freedom-<version>-src.tar.gz`), open a terminal in the unpacked folder and run
`python install/freedom_setup.py preflight`; then follow `install/PILOT-FRIEND.md` step by step. The installers have
no site inside: CO Desktop reads the `install.json` the wizard writes (or asks for your site at first start), Bridge
is built on your PC with your own release key. They are not Authenticode-signed (SmartScreen warns once); the wizard
checks them against the signed manifest. `git clone` of this repository is for updates and development.

The wizard has no AI inside: AI appears after you sign in to your own subscription (`freedom_setup.py subs`). Stuck? `freedom_setup.py
report` prints a log without secrets, names, mails, hosts or paths that you can paste into your own claude.ai or
chatgpt.com chat, or post on the forum.

Then add the modules you want, e.g. `python install/freedom_modules.py install modules/community --enable`, and check
the result with `python install/freedom_compose.py --inventory <your inventory>`.

## Self-improvement

The workbench edits itself while you use it: say what is missing in a normal chat, an agent changes the code in its
own git worktree (`tools/devtree`), another model reviews it, `aiwb-e2e` must be green, then it is merged and
released. Protected paths (`protected-paths.txt`: secrets, signing keys, installers, sign-in) need your own yes.

## Status

This is an early release. See `CHANGELOG.md` for what is verified and what is not yet.
