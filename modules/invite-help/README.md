# invite-help — install by invitation (SPEC §7.5)

You invite a friend to install Freedom without lending them your AI.

1. `python tools/invite-help/invite-help.py new --label "Ann"` — an invite code and a message you send yourself.
2. The friend's setup wizard asks for the code first (`freedom_setup.py invite`). Before their subscription works, a
   failed step can be sent to you: `freedom_setup.py report --send` shows the exact sanitized text and asks first.
3. Your agent gets a push, reads the report as untrusted data (`invite-help show`), fixes Freedom (wizard, docs) or
   writes an answer (`invite-help answer`), the cost is measured between `show` and `answer` (aiwb-budget); at 80 % of the invite's budget `show`/`answer` stop and ask the owner.
4. The wizard fetches answers: `freedom_setup.py report --answers`.

Your AI never connects to the friend's computer and never talks to the friend; after the friend signs in to their own
subscription, their own AI does the rest of the installation. Without an invite the help is the forum (SPEC §4.3).

Server: routes `intake` and `answers` (`agent/module.php?m=invite-help&r=…`, POST JSON, invite id + secret, the secret
is stored as sha256 only), owner ops `invite_*`. Limits: 64 KB per report, 30 per invite per day, 200 open, 50 live
invites, 1–90 days, budget 0–50 % of a week. Data stays when the module is switched off.
