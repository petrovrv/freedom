# Mail concierge

Local, opt-in foundation for the agent email client described in docs/SPEC-concierge-mail.md. The module is disabled by default and does not send messages.

Run `python home/mailcli.py --root <private-archive-directory> --help`. Import an RFC822 message with `import-eml <file> --account <address>`, or fetch Gmail through read-only IMAP with `sync --account <address>`. Gmail credentials are retrieved from keyring service `aiwb/gmail`, account equal to the email address. No credentials are accepted in files or arguments.

The archive stores MIME originals and attachments by SHA256 and indexes plain text in SQLite FTS5. SQLite is the initial local implementation, not the PostgreSQL backend specified for the eventual homehub service. Keep the archive outside the source repository and outside shared chat directories. Disk encryption and access control remain deployment prerequisites.

The initial workflow supports cases, promises and versioned local reply drafts. Local drafts are not Gmail drafts. Publishing a draft uses the existing gmail-draft tool; sending is always performed by the owner. Security-code detection is a conservative heuristic, not the task-bound OTP vault from the specification.

Not yet implemented: PostgreSQL/pgvector, Gmail push (Pub/Sub) and Graph adapters, calendar integration, channel adapters, task-bound OTP, browser UI, automatic rules and family sharing. No production mailbox has been changed by this implementation.

Validation: `python -m unittest discover -s home -p "test_*.py"`.

## Safety and synchronization boundaries

Decoded MIME headers and text parts, nested forwarded mail included, pass read-guard and the authentication-code heuristic before any archive write. Nothing is dropped silently: a suspicious message is stored with `tainted=1` and its read-guard rules, and drafting on its thread needs the owner's explicit yes (`draft-save --confirm-untrusted`). A detected code, sign-in link or password reset is stored only as a visible stub (sender and date only: no subject, raw message, body or attachments), so the code never reaches disk; checkpoints and Maildir export skip such stubs, the provider keeps the original. The heuristic is not the task-bound OTP vault of section 11. Binary document contents are not extracted or analyzed. CLI output also passes read-guard before exposure to an agent.

Default `sync` reads Sent first and then INBOX using read-only IMAP. Special folders keep their English Gmail name in the archive (`[Gmail]/Sent Mail`, `[Gmail]/Spam`, aliases `@sent`, `@spam`, `@drafts`, `@trash`, `@all`); on a Gmail in another language they are found by their RFC 6154 special-use flag. A missing Sent folder aborts the sync. Sync is incremental: UIDs already archived for the folder's UIDVALIDITY are skipped and the rest is fetched in batches of 50; `--refetch` downloads them again, which also restores stubs that an improved code heuristic no longer treats as codes. `--folder` is a single-folder diagnostic snapshot. All snapshots are bounded: the result explicitly reports `complete_mailbox: false`. Absence of a locally indexed reply is not proof that no reply exists. Existing and newly saved drafts are checked against indexed sent replies. Latest incoming currently means import order; backfills require further reconciliation of reply chronology.

Forwarded message/rfc822 parts remain attachments, not the outer searchable body. In-Reply-To chains reconcile across arbitrary arrival order. A merge involving multiple existing draft histories refuses the import and preserves both histories for review. Archives created before reply-edge tracking need re-import to reconstruct their edges. Draft updates support optimistic version checks; undo retains immutable revision history. The critic blocks unfinished markers including the question-mark emoji and flags financial/legal content for review. It never authorizes sending.

## Local recovery and export

- `checkpoint <new-directory>` creates a consistent SQLite snapshot, all content-addressed blobs and a SHA256 manifest.
- `verify <checkpoint-directory>` checks manifest paths, hashes, database integrity and message/attachment references.
- `--root <new-archive-directory> restore <checkpoint-directory>` verifies and restores atomically to a new destination.
- `export-maildir <new-directory> --account <address>` exports exact archived RFC822 originals for one account.

Existing destinations are never overwritten. Checkpoints are **plaintext local recovery copies**, not the encrypted remote backup specified for production. Hashes detect accidental corruption, not malicious replacement of both data and manifest. Use a private protected destination. No automatic backup schedule or cloud upload is included.

## Reverse spam filter (section 6)

`sync --folder "[Gmail]/Spam"` takes a read-only snapshot of the spam folder; `spam-rescue --account <address>` lists the messages there that the owner's context says are wanted: the owner wrote to the sender, the sender is a known correspondent, or the sender's domain belongs to an open case (also `awaited_reply` when the case has an open promise of theirs). Shared mailbox domains (gmail.com etc.) never match by domain. Only a sender authenticated by the topmost Authentication-Results header of a trusted receiving provider (authserv-id `mx.google.com`; each result parsed separately, comments ignored: DMARC, or DKIM/SPF pass aligned with the From domain) is rescued; context matches without it are listed as `unverified`. Tainted and redacted messages are never rescued. Nothing is moved at the provider; the result has a one-line `report` for the briefing. The dossier and learning from the owner's corrections are not connected yet.

## Morning briefing (sections 3, 6, 11, 13)

Messages from mailing lists (List-Id, List-Unsubscribe), bulk or auto-submitted mail and no-reply, order, billing or notification robots are marked `automated` at import (older archives are classified once on open); they never count as "your turn" (the briefing only reports how many). The 24 h windows for codes and flagged mail use the message date, so a backfill of old mail is not news. Meeting passcodes next to a Teams/Zoom/Webex meeting id (en/de/nl/fr/ru), pickup codes and "you can also reset your password" footers are not sign-in codes.

`briefing [--account A ...] [--lang en|ru] [--days 14] [--text]` gives, most urgent first: overdue promises (yours, then theirs with a reminder hint), threads where the other side wrote last and no reply is sent (last `--days` days, closed cases and spam excluded), the spam rescue, mail flagged by read-guard and sign-in codes/links of the last 24 h (sender domain only), threads where the owner wrote last and nobody answered for 7+ days, and waiting drafts. `--text` prints only short lines for a push, WhatsApp or the voice hub; a quiet morning is one line. Read only: no drafts are created and nothing is sent or moved.

## Delivery of the briefing

The module names no channel. `deliver channels` asks the own site (push.php `op=channels`, push key `aiwb/co-push`)
where a push to the person can go: `devices` (CO push to the subscribed devices) and every notify hook of an enabled
module (`<module>/<id>`, e.g. a messenger), with `ready` when the module says so; `suggested` lists the ready ones.
Nothing is delivered until the person chose: `deliver set --channels devices,<module>/<id> --at 07:30 --lang ru
[--account <address> ...]` keeps the choice and the archive it reads (`--root`, else the default) in `concierge-mail/delivery.json` next to install.json (unknown channels are refused,
channels that are not ready give a warning). The module's schedule runs `deliver tick` every 10 minutes: once a day
from the chosen time (a PC that wakes up more than 3 h later skips the day), it syncs the chosen accounts, builds the
briefing and pushes it only to the chosen channels (title + at most 400 characters, the rest as "… +N").
`deliver send [--dry-run]` does it now, `deliver set --off` stops it and keeps the choice, `deliver show` prints it.
A separate settings page is planned.

## Mail store on the home server (0.7.0)

The whole mailbox lives on the home server (Linux, systemd), one store per person: own system user `aiwb-<slug>` (since 0.9.0 the one user of the person for all modules,
module `provider-accounts`; `hubctl.py deploy` renames the older `cmail-<slug>` to it, same uid),
own directory `/srv/data/mail/<slug>` (0700). Since 0.8.0 the Google sign-in is the person's shared account of the
module `provider-accounts` (`/etc/credstore.encrypted/account.<slug>.google.cred`, systemd-creds encrypted with the host key; one
consent for all modules of the person): sync mints tokens with `mail.read` only, the backup with `drive.appfiles` only. One person's service cannot read another person's mail; the owner's
keyring holds no family member's token.

- `gmail-sync` (timer every 5 min): the first run fetches every message of the last `--years` (2) as raw RFC822 through
  the Gmail API (spam and trash included, drafts left for stage 4) and keeps the mailbox `historyId` read before the
  listing; later runs apply only the changes since it (new mail, labels, deletions marked `gone`, nothing removed from
  the archive). An expired `historyId` falls back to a full listing that skips archived messages. A message that fails
  to import is kept aside in `gmail_failed` and retried on every run. Same section 11 and read-guard rules as `sync`.
- `gmail-backfill --before YYYY-MM-DD | --query Q`: older mail on request. `gmail-status`: counters, no content.
- `backup-drive` (nightly): encrypted incremental backup into the person's own Google Drive (scope `drive.file`, folder
  "Freedom mail backup"): a SQLite snapshot plus packs of new blobs, sealed with the person's X25519 public key
  (`seal.py`, AES-256-GCM in 1 MiB chunks). The server holds only the public key and cannot read its own backups.
  `backup-keygen` makes the key pair on the person's device (private key in that device's keyring
  `aiwb/concierge-mail-backup`); `restore-drive <new-dir>` downloads, decrypts and verifies the newest backup there.
- `hub/hubctl.py` (on a PC, over SSH): `deploy` (code + unit templates), `add <slug> --account A --env E
  [--backup-public KEY]`, `connect <slug> --client-credman|--client-json` (installed-app OAuth with PKCE in the person's
  browser through `provider-accounts` with the capabilities `mail.read` + `drive.appfiles`, only the missing ones are asked; the
  refresh token goes straight into the shared host credential and is checked to belong to the named mailbox),
  `migrate <slug>` (re-encrypts a 0.7 `concierge-mail.<slug>.cred` as the shared one), `start|stop <slug>`, `status`,
  `run <slug> -- <mailcli args>`. `deploy` also copies `modules/provider-accounts/home`.

Read only: the Gmail scope is `gmail.readonly`, so nothing can be marked, moved, deleted or sent from here.

## Mail app and the site as postman (0.8.0, stage 2; through home-relay since 0.10.0)

The app ✉ (`ui/mail.html`, a `ui.page` with `app: true`, its own window in CO Desktop) and the tray indicator
(`ui/tray.html`, unread count, empty at 0) read the person's store on the home server through the module **home-relay**
(the site carries only sealed requests and answers; envelope, limits and the stated limit: `modules/home-relay/README.md`):

- **Handler** (`home/relay.py`, `mailcli.py relay-handler`): started by the person's worker `home-relay@<slug>` with
  the mailbox's env file; serves only when the mailbox's `AIWB_ENV` equals the worker's environment (otherwise
  `not_bound`). Read only: `overview`, `list`, `search`, `thread`, `attachment` (up to 4 MB). HTML loses scripts,
  frames, forms, event handlers and `javascript:` links; `cid:` images go inline; remote images stay blocked by the page
  CSP. Sign-in-code stubs show only sender and date. Status for the tray: unread count, last sync, account.
- **Site** (`server/ops.php`): `mail_status` (flat: paired, online, worker key, unread count) and `mail_call` (one
  sealed request) delegate to the home-relay lib with the module name `concierge-mail`.
- `hubctl.py deploy` registers the handler (`/etc/home-relay/modules.d/concierge-mail.json`, write access to
  `/srv/data/mail/<slug>` only) and removes the worker of 0.8-0.9 (`concierge-mail-relay@`); pairing a person is
  `modules/home-relay/home/hub/relayctl.py pair <slug>`.
- A thread gives its newest 100 messages, a message over 1.5 MB of HTML comes as text (`truncated`).

Server checks: `php tests/server.php` (from the module folder; no database or web server needed).
