# SPDX-License-Identifier: AGPL-3.0-or-later
"""Test helpers: in-memory keys, temporary archives, a budget stub, a fake transport (an in-process relay)."""
from __future__ import annotations

import json
import os
import tempfile

from community import agent, archive, cards, gate, keys, nostr, outfilter

INSTALL = {"members": [{"env": "anna", "email": "anna@family-mail.be", "name": "Anna Kowalski", "role": "owner"}],
           "site": {"url": "https://co.mydomain.be"}}


def budget_ok(used=10.0, headroom=20.0, week_start="2026-11-09 08:00"):
    return lambda: {"ok": True, "usedPercent": used, "headroomPercent": headroom, "weekStartLocal": week_start}


def tmpdir():
    return tempfile.mkdtemp(prefix="aiwb-community-test-")


class MemTransport:
    """A relay in a list: publish appends, query filters; enough for unit tests of the Workbench."""

    def __init__(self, allowed=None):
        self.events, self.allowed = [], allowed

    def publish(self, urls, ev, k):
        res = {}
        for u in urls:
            if self.allowed is not None and ev["pubkey"] not in self.allowed:
                res[u] = [False, "restricted: not allowed"]
            else:
                if ev["id"] not in {e["id"] for e in self.events}:
                    self.events.append(ev)
                res[u] = [True, ""]
        return res

    def query(self, urls, filters, k):
        return {u: [e for e in self.events if nostr.match_any(e, filters)] for u in urls}


def workbench(name, relays=("mem://r1",), transport=None, settings=None, ddir=None, install=None, k=None):
    ddir = ddir or tmpdir()
    k = k or keys.generate()
    arch = archive.Archive(os.path.join(ddir, f"{name}.sqlite"))
    filt = outfilter.OutboundFilter.from_sources(install if install is not None else INSTALL)
    s = {"socialSharePercent": 3.0, "tickCostPercent": 0.3, "autoPublish": []}
    s.update(settings or {})
    return agent.Workbench(arch, k, f"wb-{name}", list(relays), filt, s, budget_provider=budget_ok(),
                           transport=transport or MemTransport(), owner_gate=gate.allow_all)


def sample_card(i=1, **kw):
    c = cards.new_card(
        title=kw.get("title", f"Idea {i}: associative chat search"),
        problem=kw.get("problem", "the agent does not find earlier chats by paraphrase"),
        approach=kw.get("approach", "reciprocal rank fusion of full-text search over messages and protocol and "
                                    "vectors of a static embedding model; aliases from Wikidata"),
        interface=kw.get("interface", 'chat-find "<question>"'),
        pitfalls=kw.get("pitfalls", "frequent names in aliases add noise; drop terms hitting > 5 % of chats"),
        result=kw.get("result", "MRR 0.66 -> 0.87 on 22 questions"),
        cost=kw.get("cost", "~1 agent day, 0 EUR infrastructure"),
        code=kw.get("code"))
    return c


def content(ev):
    return json.loads(ev["content"])
