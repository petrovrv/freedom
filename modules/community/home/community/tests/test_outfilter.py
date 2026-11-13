# SPDX-License-Identifier: AGPL-3.0-or-later
import unittest

from community import outfilter
from community.outfilter import OutboundFilter

INSTALL = {
    "members": [
        {"env": "anna", "email": "anna@family-mail.be", "name": "Anna Kowalski", "role": "owner"},
        {"env": "boris", "email": "boris.k@gmail.com", "name": "Борис"},
    ],
    "site": {"url": "https://co.mydomain.be", "home": "/home/u000111222",
             "webRoot": "/home/u000111222/domains/co.mydomain.be/public_html"},
    "drive": {"root": "G:\\My Drive", "workbench": "AI-Workbench"},
}


def F(**kw):
    return OutboundFilter.from_sources(INSTALL, dossier_names=["Иван Сидоров", "Rose", "Mark Janssens"], **kw)


class HitsTest(unittest.TestCase):
    def setUp(self):
        self.f = F()

    def rules(self, text):
        return self.f.check(text).rules()

    def assertBlocked(self, text, rule):
        r = self.f.check(text)
        self.assertFalse(r.ok, f"not blocked: {text!r}")
        self.assertIn(rule, r.rules(), f"{text!r}: {r.explain()}")

    def test_member_and_dossier_names(self):
        self.assertBlocked("Anna said the search was slow", "name")
        self.assertBlocked("Kowalski reported it", "name")
        self.assertBlocked("мы обсуждали это с Борисом", "name")
        self.assertBlocked("Ивану Сидорову понравилось", "name")
        self.assertBlocked("Rose wrote the first version", "name")
        self.assertBlocked("ask Mark Janssens", "name")
        self.assertBlocked("ANNA in capitals", "name")
        self.assertBlocked("Anna's laptop", "name")

    def test_env_id_lowercase(self):
        self.assertBlocked("the job ran in env anna", "name")
        self.assertBlocked("environment boris was idle", "name")

    def test_emails(self):
        self.assertBlocked("mail anna@family-mail.be please", "known-email")
        self.assertBlocked("write to someone@corp-unknown.com", "email")
        self.assertBlocked("boris.k@gmail.com", "known-email")

    def test_owner_domains(self):
        self.assertBlocked("see https://co.mydomain.be/e/x", "owner-domain")
        self.assertBlocked("tools.mydomain.be has it", "owner-domain")
        self.assertBlocked("family-mail.be hosting", "owner-domain")

    def test_owner_paths(self):
        self.assertBlocked(r"open G:\My Drive\AI-Workbench\projects", "owner-path")
        self.assertBlocked("G:/My Drive/x", "owner-path")
        self.assertBlocked("require '/home/u000111222/co-agent/db.php'", "user-path")

    def test_user_paths(self):
        self.assertBlocked(r"log at C:\Users\jdoe\AppData\Local\x.log", "user-path")
        self.assertBlocked("C:/Users/jdoe/x", "user-path")
        self.assertBlocked("/Users/jdoe/Library/Logs", "user-path")
        self.assertBlocked("/home/jdoe/.config", "user-path")

    def test_phones(self):
        for t in ["call +32 470 12 34 56", "tel. 0470 12 34 56", "phone 0470/12.34.56", "+7 (912) 345-67-89",
                  "0032 9 123 45 67", "GSM 0470123456", "+31612345678"]:
            self.assertBlocked(t, "phone")

    def test_iban(self):
        self.assertBlocked("IBAN BE68 5390 0754 7034 thanks", "iban")
        self.assertBlocked("pay to BE68539007547034", "iban")
        self.assertBlocked("DE89 3704 0044 0532 0130 00", "iban")

    def test_addresses(self):
        for t in ["Kerkstraat 12, 9000 Gent", "lives at Frankrijklei 5A", "Hauptstraße 3, Berlin",
                  "221B Baker Street, London", "12 rue de la Loi, Bruxelles", "Rue de la Loi 16",
                  "ул. Ленина, д. 5", "B-9000 Gent", "1012 AB Amsterdam"]:
            self.assertBlocked(t, "address")

    def test_secrets_from_toolbox_patterns(self):
        self.assertBlocked("token ghp_" + "a1B2" * 9, "secret")
        self.assertBlocked("key sk-ant-" + "x9Y8" * 10, "secret")
        self.assertBlocked("-----BEGIN OPENSSH " + "PRIVATE KEY-----", "secret")  # split: export scanners
        self.assertBlocked("api_key = 'Zk3pQ9rT7vW2xY5a'", "secret")
        self.assertBlocked("nsec1" + "q" * 58, "secret")

    def test_ip_and_tailnet(self):
        self.assertBlocked("ssh to 8.8.4.4 port 22", "ip")
        self.assertBlocked("canvas at my-pc." + "tail" + "4f9a42.ts.net", "tailnet")

    def test_secret_is_masked_in_report(self):
        r = self.f.check("token ghp_" + "a1B2" * 9)
        d = r.to_dict()["hits"][0]
        self.assertIn("…", d["match"])

    def test_check_obj_reports_location(self):
        r = self.f.check_obj({"title": "ok", "pitfalls": "Anna found it"})
        self.assertEqual(r.hits[0].where, "pitfalls")

    def test_event_parts_skip_hex_tags(self):
        r = self.f.check_event_parts({"text": "clean"}, [["p", "ab" * 32], ["e", "cd" * 32], ["title", "Anna"]])
        self.assertEqual([h.rule for h in r.hits], ["name"])


class FalsePositiveTest(unittest.TestCase):
    """Texts typical for idea cards and reports must pass."""

    def setUp(self):
        self.f = F()

    def assertClean(self, text):
        r = self.f.check(text)
        self.assertTrue(r.ok, r.explain())

    def test_numbers_dates_versions(self):
        self.assertClean("MRR 0,66 -> 0,87 on 22 questions; recall 0.91")
        self.assertClean("Bridge 1.10.2 and CO Web 0.90.3 released 2026-11-12 at 10:30, 12.11.2026")
        self.assertClean("430 commits since 15.10.2026, 119 in the last week; ~45 tools")
        self.assertClean("uuid 01234567-89ab-4def-8123-456789abcdef, commit 04f81bf, sha 8953fbe")
        self.assertClean("cost ~1 agent day, 0 EUR; 1000 EUR budget; port 8080; RFC 2606")
        self.assertClean("unix time 1731400000, 3 % of the week, 10 % cap, k/7 ceiling")
        self.assertClean("schema 20, kind 30470, 2 400 000 tokens, 1 500 000 rows")
        self.assertClean("version 1.2.3.4 and v10.0.0.1")
        self.assertClean("half-life 6 months = 182.6 days; 0.5^(t/T)")

    def test_common_words_that_equal_names(self):
        self.assertClean("prices rose by 20 % after the change")
        self.assertClean("the anna-karenina corpus")  # env id inside a hyphenated word
        self.assertClean("the rose garden")           # lowercase word equal to a configured name
        self.assertClean("mark the event as read")

    def test_placeholder_emails_and_paths(self):
        self.assertClean("set owner to user@example.org; git@github.com:org/repo.git")
        self.assertClean("Co-Authored-By: Claude <noreply@anthropic.com>")
        self.assertClean(r"%LOCALAPPDATA%\AIWorkbench\install.json, C:\Users\<user>\x, C:\Users\Public")
        self.assertClean("~/.config/aiwb/install.json and /home/<user>/co-agent and /home/$USER/x")
        self.assertClean("Windows path C:\\AIWorkbench\\tools\\toolbox")

    def test_ips_and_hosts(self):
        self.assertClean("listen on 127.0.0.1:7447, LAN 192.168.1.20, docs 203.0.113.10")
        self.assertClean("https://github.com/<author>/<repo> and wss://relay.example.org")

    def test_generic_tech_text(self):
        self.assertClean("RRF over FTS of messages, FTS of the protocol and vectors of a static model; "
                         "aliases from Wikidata; cut terms that hit > 5 % of chats")
        self.assertClean("Street-level geocoding is off; the PC applies privacy modes before anything leaves it")
        self.assertClean("weg 3 tests passed")  # lowercase street suffix without a name
        self.assertClean("The Ringweg is a road")  # no house number
        self.assertClean("OK 2026 Release")         # not a Dutch postcode

    def test_owner_allow_list(self):
        f = F(allow=["Rose"])
        self.assertTrue(f.check("Rose was here").ok)


class DossierTest(unittest.TestCase):
    def test_dossier_names_from_stub_tool(self):
        import tempfile
        from pathlib import Path
        tools = Path(tempfile.mkdtemp())
        (tools / "aiwb-dossier").mkdir()
        (tools / "aiwb-dossier" / "aiwb-dossier.py").write_text(
            "import json, sys\n"
            "assert sys.argv[1] == '--json' and sys.argv[-1] == 'list', sys.argv\n"
            "print(json.dumps({'rows': [{'section': 'people', 'subject': 'Zelda Q', 'status': 'active'},"
            " {'section': 'state', 'subject': 'job', 'status': 'active'},"
            " {'section': 'people', 'subject': 'Old Name', 'status': 'replaced'}]}))\n", encoding="utf-8")
        self.assertEqual(outfilter.dossier_names(tools), ["Zelda Q"])
        with self.assertRaises(outfilter.DossierUnavailable):          # missing tool: never fail open
            outfilter.dossier_names(Path(tempfile.mkdtemp()))


class GenericOnlyTest(unittest.TestCase):
    def test_generic_ignores_private_lists(self):
        g = outfilter.generic_filter()
        self.assertTrue(g.check("Anna said hi on co.mydomain.be").ok)
        self.assertEqual(g.check("mail x@corp-unknown.com").rules(), ["email"])

    def test_iban_checksum(self):
        self.assertTrue(outfilter.iban_ok("BE68539007547034"))
        self.assertFalse(outfilter.iban_ok("BE68539007547035"))

    def test_registrable(self):
        self.assertEqual(outfilter.registrable("co.mydomain.be"), "mydomain.be")
        self.assertEqual(outfilter.registrable("a.b.example.co.uk"), "example.co.uk")


if __name__ == "__main__":
    unittest.main()
