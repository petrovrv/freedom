# Provider accounts

One sign-in per person and provider for all modules of the person's environment (SPEC §3.6, decision 22.11.2026).

- **Capabilities, not scopes.** A module names what it needs: `mail.read`, `mail.modify`, `mail.compose`, `mail.send`,
  `calendar.read`, `calendar.write`, `drive.appfiles`, `contacts.read`. The provider layer (`home/accounts.py`) maps
  them to its own scopes: `google` (implemented) and `microsoft` (Graph names only; connecting is not implemented yet).
- **One consent, then incremental.** `accountctl.py connect SLUG --want <caps>` asks only for what is still missing;
  Google keeps the earlier grants (`include_granted_scopes`). A new grant that lost earlier scopes is not saved.
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

A running long-lived service keeps the token it started with; capabilities added later reach it after a restart.
Each module still runs as its own Unix user per person; this module only shares the sign-in.

```
python modules/provider-accounts/home/hub/accountctl.py --host H --user U --key K connect person1 --want mail.read,drive.appfiles --client-credman <target>
python modules/provider-accounts/home/hub/accountctl.py --host H --user U --key K status
```

Validation: `python -m unittest discover -s modules/provider-accounts/home -p "test_*.py"`.
