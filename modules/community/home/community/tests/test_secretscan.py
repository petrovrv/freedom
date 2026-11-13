# SPDX-License-Identifier: AGPL-3.0-or-later
"""Final review of the strong-key rule: the structural scan (community/secretscan.py) as a table.

Every BLOCK text must be held by the outbound filter; every CLEAN text must pass. Known false positives (held on
purpose, the owner sees why) are listed in FALSE_POSITIVES and asserted as held, so a change of behaviour shows up.
"""
import unittest

from community import outfilter, secretscan

BLOCK = [
    # regression cases from the review
    '{"password":\n"x y z"}',
    '  {"password":\n"hunter two"}',
    '{"password"\n : "hunter two"}',
    "**password**: hunter two",
    "`password`: hunter two",
    "| password | hunter two |",
    "password (prod): hunter two",
    "MY_COMPANY_EU_WEST_PRODUCTION_DATABASE_PRIMARY_PASSWORD=hunter two",
    "api_keys:\n- hunter2two",
    "Authorization: Bearer abc1234",
    'Authorization: Bearer "quoted"',
    "Authorization: SSWS 00abcDEF",
    "Authorization: ApiKey k3y",
    "password: yourMum1985",
    "password: example2Hunter",
    "password: $Hunter2",
    "password: <hunter two>",
    "password: xxx # real is hunter two",
    "<password>hunter two</password>",
    "Пароль: x",
    "Wachtwoord: x",
    "pаssword: x",                                # Cyrillic а
    "passcode: 482193",
    # more of the same forms
    "password: my long passphrase",
    "db:\n  password: |\n    violet meadow silver river\n",
    "password: >-\n  violet meadow\n",
    "auth:\n  password:\n    violet meadow silver river\n",
    '"password" => "violet meadow"',
    "password -> hunter two",
    "pin: 1234",
    "x-api-key: abc",
    "apiKey: abc",
    "clientSecret = s3",
    "Passwort: geheim",
    "Kennwort = abc",
    "mot de passe : abc",
    "токен: abc",
    "ключ: abc",
    "nsec: abc",
    "Cookie: sid=abc",
    "**token** abc123",
    "sshpass -p hunter2 ssh host",
    "mysql -u root -phunter2 db",
    "tool --password hunter2",
    "tool --token=abc123",
    "setenv DB_PASSWORD hunter2",
    "https://x.example.org/login?user=a&password%3Dhunter2",
    "password: Hunter 2 Go",
    "secret: keep it simple",
]
CLEAN = [
    "the token budget is 3 % of the week",
    "tokens: 200000",
    "tokenizer: wordpiece",
    "use a password manager",
    "Password manager",
    "passwords live only in the OS keyring",
    "keyring service: aiwb/nostr",
    "password: <your password>",
    "password: ${DB_PASSWORD}",
    "password: %DB_PASSWORD%",
    "token: {{ token }}",
    'token: "<token>"',
    "api_key: none",
    "secret: ***",
    "password: xxx",
    "password: changeme",
    "password: redacted",
    "token: n/a",
    "password: your_password",
    "token: your-token",
    "api_key: your_api_key",
    "Authorization: Bearer <token>",
    "password:",
    "password:\nuser: anna-free",
    "bypass: the cache",
    "compass: north",
    "session: daily",
    "auth: OAuth device flow",
    "password-protected zip files are safer",
    "hold the pin; then tap",
    "all tests pass.",
    "the secret sharing scheme",
    "MRR 0.66 -> 0.87 on 22 questions",
]
FALSE_POSITIVES = [
    "secrets: [aiwb/co-push]",                 # keyring service names after a strong key: held
    "Password: required for login",            # prose after a strong key and a colon: held
    "| password | description |",              # a markdown table header with a strong key: held
]


class SecretScanTable(unittest.TestCase):
    def setUp(self):
        self.f = outfilter.generic_filter()

    def test_block(self):
        for t in BLOCK:
            with self.subTest(t=t):
                self.assertIn("secret", self.f.check(t).rules(), repr(t))

    def test_clean(self):
        for t in CLEAN:
            with self.subTest(t=t):
                r = self.f.check(t)
                self.assertTrue(r.ok, f"{t!r}: {r.explain()}")

    def test_documented_false_positives(self):
        for t in FALSE_POSITIVES:
            with self.subTest(t=t):
                self.assertIn("secret", self.f.check(t).rules(), repr(t))

    def test_placeholders_are_anchored(self):
        for v in ("<your password>", "${VAR}", "%VAR%", "{{ v }}", "***", "xxx", "changeme", "n/a", "your_password"):
            self.assertTrue(secretscan.is_placeholder(v), v)
        for v in ("<hunter two>", "$Hunter2", "xxx # real", "yourMum1985", "changeme2", "your_password1", "x"):
            self.assertFalse(secretscan.is_placeholder(v), v)

    def test_strong_identifiers(self):
        for k in ("MY_DB_PASSWORD", "apiKey", "x-api-key", "client_secret", "privkey", "access_keys", "github_token",
                  "token_url", "db.pass", "pw", "pin", "Wachtwoord", "Пароль"):
            self.assertTrue(secretscan.strong_key_name(k), k)
        for k in ("tokens", "tokenizer", "bypass", "compass", "passage", "session", "pinned", "keyring"):
            self.assertFalse(secretscan.strong_key_name(k), k)


if __name__ == "__main__":
    unittest.main()
