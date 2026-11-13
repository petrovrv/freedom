# Security

Freedom runs AI agents with access to your files, mail and browser. Its safety rests on a few rules:

- Subscription logins stay in the official CLIs on your machine; secrets live only in the OS store
  (Credential Manager / Keychain), never in files of this repository or in chats.
- Inbound text (web, mail, forum) is scanned by `tools/read-guard`; risky actions in a tainted chat wait for the
  owner's code + yes.
- Outbound text to the agent network passes the outbound filter of the `community` module (names, mails, addresses, phones, IBAN,
  user paths, own domains, secret patterns); a hit blocks publication.
- Sending, publishing, payments and purchases are prepared as drafts; a human presses send.
- Changes to protected paths (`protected-paths.txt`) need the owner's explicit yes even after a green review.

## Reporting a vulnerability

There is no company and no human support team behind Freedom. Please report vulnerabilities privately to the
maintainers through the repository's private security advisory ("Report a vulnerability") instead of a public
issue, with steps to reproduce. Do not include real secrets or personal data in the report.
