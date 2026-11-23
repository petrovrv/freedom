# Provider accounts

One sign-in per person and provider for all modules of the person's environment (SPEC §3.6, decision 22.11.2026).

- **Capabilities, not scopes.** A module names what it needs: `mail.read`, `mail.modify`, `mail.compose`, `mail.send`,
  `calendar.read`, `calendar.write`, `drive.appfiles`, `contacts.read`. The provider layer (`home/accounts.py`) maps
  them to its own scopes: `google` (implemented) and `microsoft` (Graph names only; connecting is not implemented yet).
- **One consent, then more rights later.** `accountctl.py connect SLUG --want <caps>` does nothing when everything is
  granted; otherwise the consent asks for the whole set (granted + new), because an installed-app client does not reliably
  return the union of grants. The account is checked, a grant without a refresh token or that lost earlier scopes is not
  saved, and credential + record are replaced together under a lock of the person; if another connect or a disconnect
  changed the grants while the consent was open, nothing is saved. Modules keep working on the old token meanwhile.
- **Where the token lives.** The refresh token goes from the person's browser straight into one encrypted systemd
  credential on the home server, `/etc/credstore.encrypted/account.<slug>.<provider>.cred` (credential name `account`),
  never into a file or keyring of the PC. `/etc/freedom-accounts/<slug>.json` (root, 0600) keeps the account, the
  environment and what was granted, without secrets.
- **Narrowed tokens per module.** A module's unit loads the credential
  (`LoadCredentialEncrypted=account:/etc/credstore.encrypted/account.%i.google.cred`) and mints access tokens with
  `accounts.TokenSource(path, capabilities=[...])`: the refresh asks only for that module's scopes, so a mail process
  never holds a calendar token. A capability that was not granted fails before any request, with a hint to connect.
- **Older credentials.** `accountctl.py migrate SLUG --from /etc/credstore.encrypted/<old>.cred --from-name <name> --env E`
  re-encrypts a module-specific credential as the shared one on the host (the old file stays until it is removed by hand).

- **One switch off.** `accountctl.py disconnect SLUG [--revoke]` removes the credential for all modules of the person
  (running services that load it are stopped, later runs report that the sign-in is gone); `--revoke` also withdraws the grant at Google, from the host, so the
  token never travels to the PC. Without `--revoke` the grant stays at Google (visible in the person's Google account).

A running long-lived service keeps the token it started with; capabilities added later reach it after a restart.
Every module runs the services of a person as the one Unix user of that person, `aiwb-<slug>` (`accountctl.py person SLUG
[--adopt OLD]` creates it, or renames an older module-specific user to it keeping the uid); each module keeps its own data
directory and loads only its own narrowed token.

```
python modules/provider-accounts/home/hub/accountctl.py --host H --user U --key K connect person1 --want mail.read,drive.appfiles --client-credman <target>
python modules/provider-accounts/home/hub/accountctl.py --host H --user U --key K status
```

Validation: `python -m unittest discover -s modules/provider-accounts/home -p "test_*.py"`.

## OAuth client of an install (distribution)

Every install uses its own OAuth client in its own Google Cloud project (the agent sets it up after the base install,
`install/GOOGLE-CLOUD.md`); there is no shared Freedom client.

- Publishing status **In production**, not Testing: in Testing Google expires refresh tokens after 7 days.
- The app stays **unverified**. `gmail.readonly` is a restricted scope and `calendar.*` sensitive: an unverified app
  shows a warning on the consent screen ("Google hasn't verified this app" → Advanced → continue) and is limited to
  100 users for its lifetime. For a family install that is enough; verification (and for restricted scopes a security
  assessment) is only needed beyond that and is not planned.
- Client type: installed app (Desktop) with a loopback redirect for consent from a PC. Consent from a phone needs a web
  client with an HTTPS redirect through the install's own site (planned, stage 2c of the mail plan).
