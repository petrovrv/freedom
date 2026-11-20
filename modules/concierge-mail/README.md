# Mail concierge

Local, opt-in foundation for the agent email client described in docs/SPEC-concierge-mail.md. The module is disabled by default and does not send messages.

Run `python home/mailcli.py --root <private-archive-directory> --help`. Import an RFC822 message with `import-eml <file> --account <address>`, or fetch Gmail through read-only IMAP with `sync --account <address>`. Gmail credentials are retrieved from keyring service `aiwb/gmail`, account equal to the email address. No credentials are accepted in files or arguments.

The archive stores MIME originals and attachments by SHA256 and indexes plain text in SQLite FTS5. SQLite is the initial local implementation, not the PostgreSQL backend specified for the eventual homehub service. Keep the archive outside the source repository and outside shared chat directories. Disk encryption and access control remain deployment prerequisites.

The initial workflow supports cases, promises and versioned local reply drafts. Local drafts are not Gmail drafts. Publishing a draft uses the existing gmail-draft tool; sending is always performed by the owner. Security-code detection is a conservative heuristic, not the task-bound OTP vault from the specification.

Not yet implemented: PostgreSQL/pgvector, Gmail history/push and Graph adapters, homehub service deployment, full mailbox reconciliation, calendar integration, channel adapters, task-bound OTP, browser UI, automatic rules and family sharing. No production mailbox has been changed by this implementation.

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
[--account <address> ...]` keeps the choice in `delivery.json` next to the archive (unknown channels are refused,
channels that are not ready give a warning). The module's schedule runs `deliver tick` every 10 minutes: once a day
from the chosen time (a PC that wakes up more than 3 h later skips the day), it syncs the chosen accounts, builds the
briefing and pushes it only to the chosen channels (title + at most 400 characters, the rest as "… +N").
`deliver send [--dry-run]` does it now, `deliver set --off` stops it and keeps the choice, `deliver show` prints it.
A separate settings page is planned.
