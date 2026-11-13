# Module mail-sheets (mail and spreadsheets)

Gmail over IMAP and Google Sheets from the command line, for the cases the chat connectors cannot do (attachments,
drafts with links — the connector rewrites URLs —, labels, scripted sheet writes). Nothing here sends mail.

| Layer | Here |
|---|---|
| `tools` | `gmail-attachments`, `gmail-draft`, `gmail-label`, `gsheets-bridge` |
| secrets | keyring `aiwb/gmail` (account = the mail address, IMAP app password; ask with `secret-prompt gmail`); `gsheets-bridge` reuses the Google token Bridge keeps in the Windows Credential Manager (Windows only) |
| scope | environment: each member enables it for their own mail |

    python install/freedom_modules.py install modules/mail-sheets --enable
