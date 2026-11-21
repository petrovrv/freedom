# Changelog

Changes of all components (Bridge, CO Web, server, CO Desktop / CO Resident, installer, modules, core tools); each
line names the component and its version. How releases are made: `docs/DEVELOPMENT.md`.

## Unreleased

- CO Desktop 0.15.0 / Core 0.14.0: reserve executor. On the computer of a Bridge, CO Desktop registers the executor desk-<machine> (executor_put with the host key of Bridge from Credential Manager, owner copy with reserveFor, serves only the environment of the person at the computer), paused while the Bridge is fine. When the server has seen the Bridge not taking jobs (executors_host takesJobs = false) for 3 min (10 min while updating), it becomes running, claims its own queue and the jobs waiting for that Bridge, and runs Claude Code itself with the arguments of the Bridge config (chat scratch on Drive with --resume when Drive is there, else a local folder and the latest chat messages from chat_read); events every 60 s at least, job_result through the server as Bridge does. Claude only; codex, isolated and file jobs of the Bridge queue are left to Bridge, own ones rejected. When the Bridge takes jobs again it claims nothing new. desktop.json "ReserveExecutorOff": true switches it off. Checked live: a db job for desk-<machine> answered in 10 s.
- CO Web 0.111.0: auto never waits for a reserve executor (owner.reserveFor) and takes it on its own computer only while it is online.
- CO Web 0.110.0: CO Desktop on the computer of a Bridge waits for that Bridge while it is alive but takes no jobs (updating, paused - e.g. Google Drive dropped out -, heartbeat late up to 15 min): auto picks it (routing.ownBridgeWaiting), the job is sent without hostAuto (no server handover) and routeHost does not fail over (holdOwn), the chat says "Waiting for ..."; other devices still go to the Site. Before, such jobs went to the Site agent, which cannot reach the PC. FamilyPanel retries read marks only while the family tab is open.
- CO Web 0.109.0: no more reload doublets while typing (Android). Each poll (useInterval) answers window focus and becoming visible at most once per 5 s of its own last call, after a random delay up to 1.5 s, instead of every poll of the page firing at once on each focus (about 15 database calls in one burst against the hosting limit of 20 new MySQL connections a second); the chat has one poll instead of two or three on the same reload, loads one at a time and keeps the list on screen when the events did not change; FamilyPanel lost its own extra visibility listener.
- CO Resident 0.6.0 (Mac): Gmail of the person at the Mac, read on the Mac itself — device tools `mail_status`, `mail_search` (Gmail search via X-GM-RAW), `mail_read` (text, HTML turned into text, attachment list; untrusted output), `mail_attachment` (into a connected folder), `mail_draft` (IMAP APPEND to Drafts, threaded replies, attachments; never sent, no SMTP) and `mail_label` (labels, read marks, archive; never Trash/Spam). The Google app password stays in the Mac's login Keychain (`secret_prompt name=gmail-imap account=<address>`), read per call with `security -w`, never in argv, logs, audit or output. Connector "Gmail (this Mac)" with modes off / read / drafts (default) / labels in the menu (CO → Gmail on this Mac) and `co-resident mail …`; `connectors_status` on the Mac. Own small IMAP and MIME code (`MacMailRules.cs`, no new package), unit-tested against scripted IMAP transcripts.
- Server: delivery channels of a push — push.php `op=channels {env}` lists `devices` (subscribed CO devices) and the notify hooks of enabled modules (`<module>/<id>`, ready from the optional ext/1 notify hook `status`); `op=notify` takes `channels: [...]` to send only there (without it, as before, everywhere).
- Module whatsapp 0.5.3: notify hook `status` — ready when the WhatsApp service writes the person's pushes.
- Module concierge-mail 0.6.0: delivery of the morning briefing to the channels the person chose (`deliver channels | set | show | send | tick`), schedule every 10 min sends once a day after the chosen time; the module names no channel and sends nothing before a choice.
- Module concierge-mail 0.5.0: checked on a real Gmail mailbox (read only) — localized Gmail folders (Sent/Spam found by their special-use flags, `--folder @spam`), incremental sync with batched fetch (`--refetch` re-reads), meeting passcodes of Teams/Zoom/Webex invitations, pickup codes and "you can reset your password" footers are no longer redacted as sign-in codes and former stubs are restored on refetch; mailing lists, auto-replies and no-reply/shop robots are marked automated and left out of "your turn"; the briefing counts codes and flagged mail of the last 24 h by the message date.
- Module concierge-mail 0.4.1: `briefing --account` also limits promises, drafts and cases to those accounts; `--text` prints plain lines.
- Module concierge-mail 0.4.0: morning briefing (`briefing --text`, en/ru) — overdue promises, your turn, long silences, spam rescue, flagged mail, sign-in codes of the last day, drafts; read only.
- Module concierge-mail 0.3.0: reverse spam filter (`spam-rescue`) — spam-folder messages from people the owner wrote to, known correspondents or domains of open cases, rescued only when the sender is authenticated (DMARC/aligned DKIM/SPF), read only.
- Module concierge-mail 0.2.1: mail flagged by read-guard is kept with a taint mark instead of being dropped, and drafting on its thread needs the owner's explicit yes; authentication-code mail is kept as a visible stub (sender and date) without subject, code, raw message or attachments; fewer false code detections (postcodes, vacancy numbers), binary attachments are no longer scanned as text.
- Module concierge-mail 0.2.0: opt-in local mail archive, guarded MIME ingest, read-only Inbox/Sent snapshots, versioned drafts and obligations, verified local checkpoints/restore and Maildir export. Production service and UI remain pending.

- CO Mac 0.5.4 / Desktop 0.14.1 / Core 0.13.1: allow Enter in Workday Skills and School search inputs before results open; recheck the site and focused input before dispatch and retain final submission confirmation.

- CO Web 0.108.0 + server: the agent's name in the family chat comes from the site config (`assistant_name`, default "AI Workbench"), and `assistant_calls` adds words that call it besides @co (e.g. @джарвис / @jarvis); db.php op members returns both in `install`.
- Module whatsapp 0.5.1: pushes of the family chat (tag `family-…`) are not copied to WhatsApp; the family chat stays in CO.
- Module whatsapp 0.5.2 (tool whatsapp-agent 0.6.2): in groups the agent also hears a mention by the bot's own @lid (WhatsApp marks @mentions with it, not the phone number), which the service now learns at start; shared contacts (vCards) in read and inbox, message ids in read, media download, send a file as a document, owner's notes for every conversation (`note --to '*'`).
- Module invite-help 0.1.1: the message of invite-help new links the real release page (--repo, FREEDOM_REPO or origin of the own core tree) instead of the <repository> placeholder.

## 0.5.1 (2026-11-16)

- Installer (F2b, SPEC §4.1 step 2): one command for Windows — `install/bootstrap.ps1`, published as the release asset `freedom-bootstrap.ps1` (`release/build_installers.py`) — installs one agent CLI (Claude Code, or Codex for ChatGPT) with Git/Node through winget, downloads the release into `%USERPROFILE%\Freedom`, checks every file's sha256 against the manifest, unpacks it, lets the human sign in, then starts the user's own agent with the new playbook `install/AGENT-INSTALL.md` (Python, packages, all wizard steps, a journal with time per step; the human only for sign-ins, secrets, choices and "yes") and a pre-approved allow list `install/agent/claude-settings.json` (deploy and `report --send` still ask). Rerunning it resumes. `freedom_release.py verify --partial`: files of the manifest that were not downloaded (Linux Bridge builds on Windows) are no problem, a changed one still is. The wizard without AI stays the fallback.

- Module whatsapp 0.5.0 (tool whatsapp-agent 0.5.1): concierge rules — without the owner in the conversation the agent serves only people the owner introduced (a note, or its own first message sent with `--purpose`): obvious things it decides itself within the notes, the rest it asks the owner about (🔔) and asks the person to wait; a person not introduced gets only "I'll tell the owner" and the owner a 🔔 asking who it is (before: anyone could talk about the owner's affairs). The owner's agent records an introduction, permission or scope with `note`.
- Module whatsapp 0.4.0 (tool whatsapp-agent 0.5.0): voice notes — a voice note from a household member or saved contact is transcribed through the home voice hub (module voice, `/v1/voice/transcribe`) and reaches the agent as text; the recording is deleted after; messages of one chat stay in order; `transcribe --file/--msg`. Also tool 0.4.1 (`config --push-mode important|all`, default without "✓" pushes of finished jobs) and 0.4.2 (`note`/`send --purpose` keyed by phone number, not the @lid WhatsApp now returns).
- Server (push-lib.php) + ext/1 `provides.notify`: every push the server sends (`co_push_broadcast`) is also handed to the `push` hook of enabled standard modules with the envs it reaches, whether or not they have a subscribed device. Module whatsapp 0.3.0 (tool whatsapp-agent 0.4.0): CO pushes to WhatsApp — the service writes the pushes of the envs in `config --pushes` to their family numbers (host op `wa_pushes`, copies kept on the server only for envs the service asked for in the last 30 min, at most 300 / 24 h; pushes about the WhatsApp chats themselves skipped).
- Module whatsapp 0.2.0 (tool whatsapp-agent 0.3.0): owner's rules — household members (numbers in the service config) write directly as to their own CO chat (a normal job in their environment's chat "💬 WhatsApp", made on first use, their dossier); other contacts and groups stay isolated; in groups the agent speaks only when called (mention, reply, its name) and sees the last 15 messages; without the owner in the conversation it talks only on the owner's notes (`note`, `send --purpose`) or about the owner's affairs, otherwise it asks for permission and the owner gets a 🔔 on WhatsApp; anti-injection rules on every isolated turn; 20 turns/hour, 80/day outside the family; `wa_ask` finds or makes the chat itself.
- Module whatsapp 0.1.0 (tool whatsapp-agent 0.2.0): the workbench's own WhatsApp account as a linked device on the home PC (whatsapp-web.js, Edge window off the screen; WhatsApp refused to link a headless one); messages from saved contacts of the account become isolated jobs in a chat the owner chooses (host ops `wa_ask`, `wa_answer`; one job per contact at a time, a contact's session resumed within 6 h) and the answer goes back to the contact; others are logged and ignored; agents send only when the owner asks. Works around whatsapp-web.js 1.34.8 never reaching "ready" with WhatsApp Web 2.3000.105x. Needs Bridge 1.21.0.
- Bridge 1.21.0: `permissions.files = "isolated"` — Claude starts with `--safe-mode` (no CLAUDE.md, skills, plugins, hooks, MCP) and `--tools WebSearch,WebFetch` (a deny list missed Workflow, cron and worktree tools), file tools denied, no shell; Codex refuses it. A job with `request.whatsapp` (module whatsapp) always gets it, with no devices, context folders or module context, and the lean launch of voice jobs. `"none"` keeps its old meaning (no restriction).

- Module voice 0.3.10 (tool voice 0.3.16): a voice point on a Mac (and a Linux desktop) without Bridge — the hub owner makes a one-time pairing code (`hub-pair`), the person runs one Terminal command (`/setup/mac.sh` served by the hub: Python through uv without admin rights, the point venv, the code exchanged for the point key into the login keychain, voice enrollment, always-on listening asked of the person at the Mac, a LaunchAgent / systemd user unit via `service install`); the code is single-use and expires.
- Module voice 0.3.9 (hub tool voice 0.3.15): `music.byEnv` — default source, excluded sources and default speaker per person (the voice the hub recognised, else the point's environment; in CO Web the member the CO server sent the phrase for), e.g. one member's own Spotify and another's YouTube Music.
- Module voice 0.3.8 (hub tool voice 0.3.14): `music.defaultProvider` (a title without a named source is looked up there first) and `music.excludeProviders` (a source used only when named, e.g. another household member's Spotify); a named source with nothing found falls back to the default one and says so; spoken requests of several sentences are parsed whole (polite words and "search for a recording" stay out of the title, the speaker may be named mid-phrase).
- Module voice 0.3.7 (hub tool voice 0.3.13): a music source can be named by voice — Internet Archive ("из интернет-архива", "from the internet archive"/"web archive") and SoundCloud next to Spotify and YouTube Music; the provider match ignores the instance suffix of Music Assistant URIs (`spotify--abc://`), so "из Спотифая" never matched before; with a source named, tracks are tried first (archive searches return labels and uploaders as artists).
- Module voice 0.3.6 (point tool voice 0.3.12): a new voice session gets who and when — home voice assistant, the speaker's environment, UTC time — plus, in the speaker's own unshared chat, their dossier (6000 chars) and titles with summaries of their 8 latest chats; before, it had only the voice chat's protocol and answered "which speaker?" to a question about the user's current work. Point: a missing output device (Bluetooth speaker taken by a phone) falls back to the system default, the output keep-alive retries every 30 s and the PortAudio device list is refreshed at the input reopen so a returning speaker is found; music phrases survive wake-word tails and fillers.
- Module voice 0.3.5 (point tool voice 0.3.10): the wake check takes the best voiceprint score among the point's people against `wakeCheckThreshold`; before, it also needed the hub to name the person, which the hub does only from 0.65 (phrase threshold), so a lower point threshold never worked and a 1 s wake word in the owner's voice (0.60–0.65) was refused.
- Module voice 0.3.4: CO Web 🗣 Talk waits 2 s of quiet (was 1 s; a pause inside a sentence cut it off) and shows "Listening" only once the microphone records. Point tool voice 0.3.9: point log `voice-point.log` (start, wake words, near misses from `nearMiss` to the threshold, a heartbeat with level and best score), the input stream is reopened after 15 s of exact zeros and every 10 min while waiting; the tool copy also carries 0.3.1–0.3.7 (enroll pace and `--append`, `--output-device`, whole-word wake check, beeps for Bluetooth speakers, output keep-alive, "one moment" filler).
- Bridge 1.20.0: a voice job (`request.voice`, module voice) starts Claude with `--strict-mcp-config` and an empty server list — no account connectors, plugin or settings MCP servers (33 on the owner's PC); device servers named on the command line stay. Measured start of `claude -p` with haiku: 13 s → 4 s. Other agents start as before. `net8/tests/json-golden.txt` regenerated (`JobRequest.voice`, `BridgeConfig.executor`).
- Module voice 0.3.3 (server): spoken questions to claude run on a fast model — `haiku`, else `sonnet`, whichever the executor's catalog (owner.json) has; a question that starts with "сложный вопрос" (uk "складне питання", en "hard/complex question", nl "moeilijke vraag") gets the model of the chat's last typed turn, which voice turns no longer overwrite.

## 0.5.0 (2026-11-14)

- **Install by invitation** (SPEC §7.5): setup wizard step `invite` comes first (paste the invite code of the person who invited you, or none: help = forum); `report --send` shows the sanitized report and sends it to the inviter only after the friend's yes, `report --answers` prints the answers; the invite secret stays in the keyring (`aiwb/freedom-invite`).
- Module invite-help 0.1.0 (new, off by default): the inviter's side — owner ops `invite_*` (create/list/revoke invites, read/answer/close reports), public routes `intake` and `answers` (invite id + secret, stored as sha256 only; 64 KB per report, 30 per invite a day, 200 open, push to the owner), home tool `invite-help` (report text wrapped as untrusted data by read-guard, per-invite budget measured with aiwb-budget, `show`/`answer` stop at 80 % until the owner says yes; closed reports deleted after 30 days). The wizard sends a report only after the friend's y or `--yes CHECK` of the text shown. The inviter's AI never connects to the friend's computer.
- CO Web 0.106.0 (database hiccups): reads of the CO database retry twice (1 s, 3 s) after a timeout, a network error or
  a 5xx/429 answer, and the breaker after 3 failed reads pauses for 30 s instead of 5 min; a chat keeps the messages on
  screen or shows this browser's last copy (IndexedDB, 30 recent chats per account, wiped on sign-out) with a notice
  instead of an empty page with a red error, tries again every 10 s and when the network comes back; messages sent
  meanwhile wait in the outbox. Host tab → Jobs lists the database failures this browser saw (timeout / http code /
  network) to tell a slow network from a refusing host.

- Module voice 0.2.0: speaker recognition in two levels - `voice.py enroll` trains a wake-word verifier on the device and sends a voiceprint to the hub; the hub (`people`, `speakerId`, `/v1/voice/enroll|forget|whois`) routes a recognised voice to that person's chat, strangers get home commands only.

- Module voice 0.1.1 (review fixes): the hub checks the key before reading a body, limits length and parallel requests;
  every voice point has its own key with its person, chat and shell switch (`points`), the client key of the server
  never makes the hub ask the agent; voice jobs are side jobs (`request.voice`, one collapsed row; the chat's own
  session is never resumed by a voice answer), the job is stored before its message, one voice job per chat under a
  lock, the executor must still serve the environment, no shell unless the point allows it; CO Web's voice mode asks
  Home Assistant again (route `client`). CO Web 0.105.1: `composer.send` needs a click into the page (10 sends within
  10 minutes per click) and cannot pass twice in one tick; the silence meter resumes a suspended audio context.

- **Voice assistant** (Freedom F13, SPEC §3.8): module voice 0.1.0 - a voice hub for the family server (`tools/voice`
  `voice.py hub`, standard library only: speech to text and piper voices through speaches, Home Assistant conversation
  first, any HA error or "agent, ..." to the CO agent of a chat, answer as audio, Cast speakers through HA), a voice
  point with a wake word for Windows and Linux (`voice.py point`: openWakeWord + silero VAD, own venv, always-on
  listening only after the device owner turns it on; service `point` under Bridge), 🗣 voice mode in the composer, and
  server ops `voice_status`, `voice_speak`, `voice_ask_user`, `voice_answer_user`, upload `turn`, host ops `voice_ask` /
  `voice_answer` (a phrase of a voice point becomes a message and a job of a chat, own agent session `request.voice`).
- CO Web 0.105.0, ext/1 for composer pages: `media.audio` with `silenceMs` (the recording stops after a pause),
  `media.play` (CO Web plays an audio answer for the sandboxed frame), `composer.send` (a text goes out as the chat's
  next message), `chat.read` in the `ui.composer` slot.

- Setup wizard `subs`: finds claude/codex right after their official installers (`~/.local/bin`, the npm prefix) and adds those folders to the Windows user PATH; before, a fresh install said "claude: not installed" (rehearsal in a clean Windows 11 VM).
- Installer (Docker variants B/C): a new stack makes its own database password and push key and never copies them from the OS keyring, which can hold another installation's (a test stand on the owner's PC took the real push key); a keyring entry with another value is kept with a warning.
- Bridge 1.19.0 install.sh: `--deps` also installs OpenSSL 3 (`libssl3` / `libssl3t64`, Fedora `openssl-libs`) and zlib, and the install stops with a clear hint when OpenSSL is missing (minimal Debian images have none; .NET 8 needs it).
- Module dictation 0.2.0: provider `local` (own OpenAI-compatible Whisper server, e.g. speaches on a home server over HTTPS) and a provider chain in `transcribe.json` with failover (local first, Groq/OpenAI next); vocabulary `prompt`; the composer page falls back to browser recognition when every server service fails.
- **Many executors** (Freedom F12, SPEC §3.1a): any number of Bridges (Windows PCs, Macs, Linux servers) besides the
  site agent; `home-main` and `site` stay the ids of the first two and work without any setting change.
  - Server schema 28: table `executors`; db.php `executor_put` (host key, every 30 s: the row plus a copy of owner.json
    in `kv host:<id>/owner`, so a Bridge without Google Drive is seen by models, autoswitch and handover),
    `executors_host`, user ops `executors` / `executor_set` (the owner switches an executor off or forgets it).
    An id belongs to its machine: another machine gets `host_taken` (409) and no jobs from `job_claim`. `job_submit`
    and subtask templates take any known executor; an executor of a family member (`ownerEnv`) takes only the jobs of
    the environments it serves. Handover of an unclaimed `auto` job (60 s) to the best live executor that fits (the
    environment's own one, Home, Site, the others), once, without a double run; a device job only to the Site (Claude)
    or the Bridge of its devices; it runs before the Drive sync of `drive-sync.php` (no longer skipped while Drive
    fails). Drive inbox copies only for executors that read one (`drive-inbox`). Built-in model resources of a family
    member's executor belong to that member; autoswitch works on every Bridge executor.
  - Bridge 1.19.0: registers as an executor (`config.json` `executor`: `title`, `ownerEnv`, `serves`, `driveInbox`),
    sends its machine with `job_claim`, takes no jobs after `host_taken`; .NET 8: the HTTP status of a refused db.php or
    push call is read before the response is closed (was "Cannot access a disposed object").
  - CO Web 0.104.0: the executor list comes from the server (`executors.ts`); the traffic light has a row per
    executor (green: Site and any Bridge); Host and devices offers every executor and lists them with their health;
    auto = this computer's own Bridge, else the environment's order from the server (Home first, then Site, then the
    others); every Bridge executor uses the database queue; the environment note names the executor.
  - aiwb-subtask 0.7.0 (`--host` any executor id, `hosts`), aiwb-e2e 0.6.5 (`--host` any executor id).
- **Bridge for Linux** (F3a; Debian/Ubuntu, Fedora; x86_64, aarch64):
  - Bridge 1.19.0: `bridge/install.sh` for one Linux account (not root): packages (`--deps`), the session's Secret
    Service or, on a server without a desktop, gnome-keyring as a systemd user service unlocked at start (secrets stay
    in the keyring; Bridge, secret-tool and Python keyring share `aiwb/<name>`), `config.json` from install.json, a
    release build checked against the signed manifest or a build from the sources, the computer's own release key
    (`aiwb/bridge-release` in the keyring), systemd user service `aiworkbench-bridge.service` + linger.
    `net8/UnixLauncher.cs`: the update launcher of Linux/macOS (`--launcher`) on the Windows channel format
    (`BridgeAgent-<v>-<rid>` + `.sig.json`, same ECDSA key format, drain, probation, roll-back; SIGTERM stops without a
    restart), `--release-key`, `--release-publish`, `--version`. Config `chatFolderWaitSeconds` (default 90; 0 where
    the workbench root is a local folder). Tests: release channel (incl. a CNG key of `release.ps1` verified by the
    new code), json-golden refreshed (was stale since 1.18.1), the job-role test runs on Linux.
  - Setup wizard: `bridge` on Linux runs `install.sh` (`--deps`, `--binary`, `--host-id`), says how to put the host key
    into the keyring; `preflight` on Linux (systemd user manager, linger, secret-tool, gnome-keyring, a Bridge build or
    the .NET SDK); Bridge config path on Linux fixed (`~/.local/share`). `aiwb_install.py bridge-config --posix`.
  - `release/build_installers.py`: `BridgeAgent-<bridge>-linux-x64` and `-linux-arm64` into the release (own build
    root, private-word check of the own assembly; `--skip-bridge`). Guide: `install/PILOT-FRIEND.md`, section "Linux".
  - Bridge 1.19.0, devices on Linux: the device-call key outside Windows is kept in the keyring (was CNG only, so the
    whole device hub and the relay were off on Linux); config `devices.relayHomeShort` (short name of `h-<host>`,
    default `home`; a second computer of the owner needs its own). `install.sh --system-user NAME` (as root: system
    account, sources/build/install.json into its home, linger, run as the account; `--uninstall` also removes the
    account it made), `--put-secret NAME` (stdin into the account's keyring), `--relay-short`, `--relay-tools`, the
    tool library of the source tree for `h-<host>` (`AIWB_TOOLS`), apt with needrestart suspended.

## 0.4.0 (2026-11-13)

- Release checks: export rewrites that find nothing count as done (the core tree is public since the reversed
  direction); core-name count back to the baseline.
- **Neutral installers** (release page = the only link a new user needs, `install/PILOT-FRIEND.md`):
  - CO Desktop 0.14.0: a build without install.json reads the install.json of the setup wizard on this computer
    (`AIWB_INSTALL`, else `%LOCALAPPDATA%\AIWorkbench\install.json`), then `install.json` next to the app; with none
    of them a first-start window asks for the user's site, the owner's Google account and the desktop OAuth client
    (copied to `oauth-client-desktop.json` next to install.json); values left empty by an earlier start are filled from
    install.json later; the update download address is the site of this computer's settings. Builds with install.json
    (a maintainer's own) behave as before. The installer takes the OAuth client only when built with `-DCLIENT`.
  - `release/build_installers.py`: neutral CO Desktop installer (own build root, nothing embedded, fails on private
    words in the own assemblies), source tarball, notes draft; no Authenticode signature (the manifest signature
    is the check).
  - Bridge 1.18.1: `bridge/install.ps1` on a new PC makes `config.json` from install.json (`aiwb_install.py bridge-config`),
    creates a release key of this PC, builds and signs Bridge from the sources into a local channel and registers the
    logon task; existing installs keep their channel and task. `release.ps1` works in a tree without git. Config
    `desktopUpdatesDir` (CO Desktop / CO Resident releases offered to devices; default
    `%LOCALAPPDATA%\AIWorkbenchBridge\desktop-updates`, the folder of earlier installs where it exists).
  - Setup wizard: steps `bridge`, `desktop [--installer]` (checks the installer against the signed manifest, installs
    it), `desktop-update --installer` (a release's CO Desktop to the user's site and Bridge for all computers);
    preflight checks `cryptography`; `verify` runs aiwb-smoke / aiwb-e2e of the release's `tools/`.
- Guard: UTF-16 text files (with a byte order mark) are scanned like other text; the exported task XML
  `co-desktop/tools/AIWB-BridgeAutostart.xml` (unused, it named a computer and an account) is removed.
- Bridge 1.18.0: each person has their own keyring. Jobs get `AIWB_JOB_ENV` and `AIWB_JOB_ROLE` (`owner`, or
  `member` when the job's environment is known and differs from the owner's). A member's job also gets
  `PYTHON_KEYRING_BACKEND=aiwb_secret.MemberRefusedKeyring` (tool `secret-prompt` on `PYTHONPATH`): every python
  keyring call of any tool in that job is refused with `own_device_first` and a pointer to the device tool
  `secret_prompt` of the person's own device; `member_site\sitecustomize.py` on `PYTHONPATH` selects it again where
  Codex drops `*KEY*` variables. A member's read-guard PreToolUse hook also covers Grep and Glob (the owner's: as in 1.17).
  The owner's jobs: unchanged. Limit: the job still runs under the owner's Windows account (a barrier, not
  isolation; next step in the SPEC: own Windows account per household member).
- secret-prompt 0.4.0 (core tool): `aiwb_secret.job_role()` / `guard()` / `MemberRefused` decide in one place;
  `get_secret`/`set_secret` and the prompt window (exit 5) refuse a member's job.
- read-guard 0.5.0 (core tool): a member's job is denied shell, file and MCP calls that reach the owner's credential
  store (keyring, cmdkey, Cred*, vaultcmd, PasswordVault, win32cred, secret-tool, security, Credentials/Vault
  folders, role variables); no grant opens it.
- wsl-family-sandbox 0.2.0 (core tool): `aiwb-secret` - each sandbox user's own secret store (0700/0600 in the
  user's real home, set/get/has/list/delete/exec, value never from argv), also as a python keyring backend; `check`
  verifies that a sister's secrets are unreadable.
- CO Resident 0.5.3 (CO.Core, also in the next CO Desktop): one-time codes from chat in the agent browser - setting
  `AllowOtpFromChat` (`agent-browser.json`; Mac menu Agent browser -> "Agent may type one-time codes from chat",
  `co-resident browser otp on|off|default`, CO Desktop checkbox in Connectors -> agent browser). On: a one-time-code page
  does not pause the tab, it is marked otp-pending and the tool result tells the agent to ask the user in chat for the
  code and `browser_type` it into the code field's ref; `browser_type` types a code-like text (3-12 letters/digits)
  only into fields protected solely as one-time-code fields, never into password, card, CVC, IBAN, PIN, secret or token
  fields; payment pages still pause (also a code page within 30 min of a purchase click in the tab or with card/bank
  confirmation text, 3-D Secure), captcha unchanged; the code is masked in snapshots and confirmations and journal
  screenshots are skipped while it may be on the page; an unreadable setting file counts as off on the Mac.
  Default: on for a household member's Mac (device role `member`), off for the owner's Mac and on Windows. Off:
  behaviour as before (snapshots and confirmations now also mask values of one-time-code fields named in more ways).

## 0.3.0 (2026-11-13)

The development direction is reversed: this repository is the upstream of the core.

- **This repository is now the upstream of the core** (`docs/DEVELOPMENT.md`): maintainers develop in a clone of it;
  their own installations are this tree plus a private layer that never enters it.
- **Guard** (`release/export_freedom.py guard`, `release/freedom_guard.py`, `release/hooks/pre-push`, CI job
  `guard`, devtree `.devtree.json`): the tree and every new commit (added lines, file names, message, author) are
  checked for personal data, secrets and module names in the core before a merge or a push.
- devtree 0.8.0: main tree from `devtree.json`, the repository's guard before merge and push.
- task-hints 0.1.1: neutral labels of the corporate models; the module of 0.2.0 failed signature verification
  because the export changed its hint data after signing.
- Bridge: `BridgeCorporate.cs` renamed `BridgeModules.cs` (the generic module gateway).
- CO Resident 0.5.1 (CO.Core, also in the next CO Desktop): agent browser - date/time/month/week inputs get their value
  in the input's format (ISO or day-first), a new tab or window opened by the agent tab is opened in that tab (its
  address read from the blank window's first request when needed), links with a real address outside a form are not
  submits, an unanswered confirmation is `no_answer` (not a refusal; browser actions wait 10 min with a notification
  on the Mac), form confirmations switchable on the Mac (menu "Ask before sending forms", `co-resident browser forms ask|auto`).
- CO Resident 0.5.2: secrets of agent tools in the Mac's own login Keychain - `secret_prompt` (native dialog with a
  hidden field, value only to `security -i` stdin as service `aiwb/<name>` / account, never in argv, logs, audit or
  output; returns saved/cancelled/timeout/waiting), `secret_list` (names only) and `secret_delete` (confirmed every
  call); tools on the Mac read them with Python keyring `aiwb/<name>`. The resident's own items are refused.

## 0.2.0 (2026-11-13)

Everything that is not the core is now a module.

- **Module interface ext/1** (`modules/EXT1.md`, `docs/ext1-device-tools.md`): a module is a folder with a signed
  `module.json` that declares what it provides — tools, services and schedules, device tools, models, chat modes,
  pages, settings, search providers, hooks and events. The core may not add new mentions of module names
  (`release/check_core_names.py`, baseline only shrinks; the export check refuses module ids outside the allow-list).
- **Loaders.** Bridge loads modules (services with takeover of an old scheduled task, schedules, device tools,
  models, context, events, tools that follow the module switch); CO Desktop and CO Resident load module device tools
  and settings pages (Connectors → Modules); CO Web loads module pages and chat modes through `ext/ext1.js`; the
  server keeps the module registry and a generic model gateway. Install, update, disable, enable and remove with
  `install/freedom_modules.py` or CO Web → Host → Modules; remove asks whether to keep or erase the module's data.
- **13 standard modules** in `modules/`: admin-checks, assoc-search, browser-ext, canvas, chat-import, community,
  corporate (off by default), dictation, docs-search, geo-presence, mail-sheets, study, task-hints.
- **Compose check** (`install/freedom_compose.py`, `install/freedom_compose.md`): shows what a workbench consists of
  (core, modules, tools, own private modules) and the gaps between them.
- **How it runs** (README): where jobs run, the PC from anywhere through the relay, push, updates that do not wait
  for long jobs.

## 0.1.0 (2026-11-12)

First public export. Status of each stage: see the release notes.
