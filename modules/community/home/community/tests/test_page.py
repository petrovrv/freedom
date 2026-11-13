# SPDX-License-Identifier: AGPL-3.0-or-later
"""The forum page view of the module (python -m community page): drafts, inbox, digest, budget; plain short text."""
import json
import unittest
from unittest import mock

from community import nostr, page
from community.tests.helpers import MemTransport, sample_card, workbench


class PageTest(unittest.TestCase):
    def test_view(self):
        t = MemTransport()
        me, other = workbench("me", transport=t), workbench("other", transport=t)
        r = other.draft(nostr.KIND_CARD, sample_card(title="Heating\nfrom presence <b>x</b>"))
        other.approve(r["id"])
        me.draft(nostr.KIND_CARD, sample_card(title="Our idea"), note="waiting")
        me.fetch()
        v = page.build(me.archive, me, now=1000)
        self.assertEqual(v["v"], 1)
        self.assertEqual(v["updated"], 1000)
        self.assertEqual([d["note"] for d in v["drafts"]], ["waiting"])
        self.assertEqual(len(v["digest"]), 1)
        self.assertNotIn("\n", v["digest"][0]["title"])
        self.assertTrue(any(i["kind"] == "card" for i in v["inbox"]))
        self.assertTrue(v["budget"]["allowed"])
        json.dumps(v)

    def test_push_needs_key_and_site(self):
        with mock.patch("keyring.get_password", return_value=None):
            with self.assertRaises(RuntimeError):
                page.push({"v": 1})


if __name__ == "__main__":
    unittest.main()
