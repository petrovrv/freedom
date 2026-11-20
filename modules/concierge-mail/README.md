# Mail concierge

Local, opt-in foundation for the agent email client described in docs/SPEC-concierge-mail.md. The module is disabled by default and does not send messages.

Run `python home/mailcli.py --root <private-archive-directory> --help`. Import an RFC822 message with `import-eml <file> --account <address>`, or fetch Gmail through read-only IMAP with `sync --account <address>`. Gmail credentials are retrieved from keyring service `aiwb/gmail`, account equal to the email address. No credentials are accepted in files or arguments.

The archive stores MIME originals and attachments by SHA256 and indexes plain text in SQLite FTS5. SQLite is the initial local implementation, not the PostgreSQL backend specified for the eventual homehub service. Keep the archive outside the source repository and outside shared chat directories. Disk encryption and access control remain deployment prerequisites.

The initial workflow supports cases, promises and versioned local reply drafts. Local drafts are not Gmail drafts. Publishing a draft uses the existing gmail-draft tool; sending is always performed by the owner. Security-code detection is a conservative heuristic, not the task-bound OTP vault from the specification.

Not yet implemented: PostgreSQL/pgvector, Gmail history/push and Graph adapters, homehub service deployment, full mailbox reconciliation, calendar integration, channel adapters, task-bound OTP, browser UI, automatic rules and family sharing. No production mailbox has been changed by this implementation.

Validation: `python -m unittest discover -s home -p "test_*.py"`.

## Safety and synchronization boundaries

Decoded MIME headers and text parts, nested forwarded mail included, pass read-guard and the authentication-code heuristic before any archive write. Nothing is dropped silently: a suspicious message is stored with `tainted=1` and its read-guard rules, and drafting on its thread needs the owner's explicit yes (`draft-save --confirm-untrusted`). A detected code, sign-in link or password reset is stored only as a visible stub (sender and date only: no subject, raw message, body or attachments), so the code never reaches disk; checkpoints and Maildir export skip such stubs, the provider keeps the original. The heuristic is not the task-bound OTP vault of section 11. Binary document contents are not extracted or analyzed. CLI output also passes read-guard before exposure to an agent.

Default `sync` reads Sent first and then INBOX using read-only IMAP. Use `--sent-folder` for localized folders; a missing Sent folder aborts the sync. `--folder` is a single-folder diagnostic snapshot. All snapshots are bounded: the result explicitly reports `complete_mailbox: false`. Absence of a locally indexed reply is not proof that no reply exists. Existing and newly saved drafts are checked against indexed sent replies. Latest incoming currently means import order; backfills require further reconciliation of reply chronology.

Forwarded message/rfc822 parts remain attachments, not the outer searchable body. In-Reply-To chains reconcile across arbitrary arrival order. A merge involving multiple existing draft histories refuses the import and preserves both histories for review. Archives created before reply-edge tracking need re-import to reconstruct their edges. Draft updates support optimistic version checks; undo retains immutable revision history. The critic blocks unfinished markers including the question-mark emoji and flags financial/legal content for review. It never authorizes sending.

## Local recovery and export

- `checkpoint <new-directory>` creates a consistent SQLite snapshot, all content-addressed blobs and a SHA256 manifest.
- `verify <checkpoint-directory>` checks manifest paths, hashes, database integrity and message/attachment references.
- `--root <new-archive-directory> restore <checkpoint-directory>` verifies and restores atomically to a new destination.
- `export-maildir <new-directory> --account <address>` exports exact archived RFC822 originals for one account.

Existing destinations are never overwritten. Checkpoints are **plaintext local recovery copies**, not the encrypted remote backup specified for production. Hashes detect accidental corruption, not malicious replacement of both data and manifest. Use a private protected destination. No automatic backup schedule or cloud upload is included.

## Reverse spam filter (section 6)

`sync --folder "[Gmail]/Spam"` takes a read-only snapshot of the spam folder; `spam-rescue --account <address>` lists the messages there that the owner's context says are wanted: the owner wrote to the sender, the sender is a known correspondent, or the sender's domain belongs to an open case (also `awaited_reply` when the case has an open promise of theirs). Shared mailbox domains (gmail.com etc.) never match by domain. Only a sender authenticated by the topmost Authentication-Results header of a trusted receiving provider (authserv-id `mx.google.com`; each result parsed separately, comments ignored: DMARC, or DKIM/SPF pass aligned with the From domain) is rescued; context matches without it are listed as `unverified`. Tainted and redacted messages are never rescued. Nothing is moved at the provider; the result has a one-line `report` for the briefing. The dossier and learning from the owner's corrections are not connected yet.
