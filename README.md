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
| `browser-ext/` | browser extension: CO in the side panel, agent tools in your tabs |
| `connectors/corporate/` | optional corporate connector (off by default; your employer's data is your responsibility) |
| `community/` | idea cards, outbound filter, digest, Nostr agent forum, volunteer work, shared projects, reputation |
| `install/` | setup wizard without AI (`freedom_setup.py`), Docker variant, Google Cloud playbook |
| `tools/` | core tools: toolbox, read-guard, devtree, aiwb-e2e, aiwb-smoke, aiwb-subtask, aiwb-budget, ... |

## Install

Requirements: a Google account (Drive, sign-in, Pub/Sub), a Claude Max/Pro or ChatGPT Plus/Pro subscription, a
Windows 10/11 PC or a Mac, and one of:

| Variant | You need |
|---|---|
| A. Shared hosting | PHP 8.1+ (mysqli, openssl, curl, mbstring, zlib), MariaDB 10.5+, cron, SSH; Node 20+ for the server agent |
| B. VPS or NAS | Docker (`install/docker/`) |
| C. Only a computer | Docker in WSL or on the Mac, the server on localhost; phone access through Tailscale |

Start with `python install/freedom_setup.py preflight` and follow `install/PILOT-FRIEND.md`. The wizard has no AI
inside: AI appears after you sign in to your own subscription (`freedom_setup.py subs`). Stuck? `freedom_setup.py
report` prints a log without secrets, names, mails, hosts or paths that you can paste into your own claude.ai or
chatgpt.com chat, or post on the forum.

## Self-improvement

The workbench edits itself while you use it: say what is missing in a normal chat, an agent changes the code in its
own git worktree (`tools/devtree`), another model reviews it, `aiwb-e2e` must be green, then it is merged and
released. Protected paths (`protected-paths.txt`: secrets, signing keys, installers, sign-in) need your own yes.

## Status

This is an early release. See `CHANGELOG.md` for what is verified and what is not yet.
