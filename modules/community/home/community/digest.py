# SPDX-License-Identifier: AGPL-3.0-or-later
"""Weekly digest for the owner (SPEC §5.2): up to 3 ideas of other workbenches, ranked by

  0.35 * author reputation (normalised over the candidates)
+ 0.25 * confirmed implementations ("worked" reports by distinct other workbenches, capped at 5)
+ 0.40 * need: 1 - similarity to what we already have (tool library INDEX.md + own cards); a card with a measured
         result that resembles one of ours also scores as "maybe better than ours" (0.5 * similarity).

Each idea ends with a CO quick-reply line `[[options: …]]`. CO shows at most 8 buttons per message, so every idea is
its own message (blocks separated by a line `---`), labelled with its number: "1 внедрить своё", ...
"""
from __future__ import annotations

import json
import math
import re
import time
from collections import Counter
from pathlib import Path

from . import cards, config, guard, nostr, projects, reputation, schnorr

CHOICES = ["внедрить своё", "взять код", "не надо", "обсудить"]
CHOICE_KEYS = {"внедрить своё": "implement", "взять код": "take-code", "не надо": "skip", "обсудить": "discuss"}
WORD = re.compile(r"[a-zа-яё0-9]{3,}", re.I)
STOP = set("""the and for with that this from are was were has have not but you your our its into over under than then
when what which while also only each per via using use used can will one two all any more most other some such
без для что как это при все или его она они так уже ещё еще где кто чем над под про""".split())
MEASURE = re.compile(r"\d.*(?:→|->|%|x\b|×|times|раз)|(?:→|->)\s*\d", re.I)


def tokens(text: str) -> list[str]:
    return [w for w in (m.group(0).lower() for m in WORD.finditer(text or "")) if w not in STOP and not w.isdigit()]


def library_entries(index_path: Path | None = None) -> list[dict]:
    p = index_path or (config.tools_dir() / "INDEX.md")
    try:
        text = Path(p).read_text(encoding="utf-8")
    except OSError:
        return []
    out = []
    for m in re.finditer(r"^- `([^`]+)`\s+\S+\s+—\s+(.+)$", text, re.M):
        out.append({"name": m.group(1), "text": m.group(1).replace("-", " ") + " " + m.group(2)})
    return out


class Tfidf:
    def __init__(self, docs: list[str]):
        self.df = Counter()
        toks = [tokens(d) for d in docs]
        for t in toks:
            self.df.update(set(t))
        self.n = max(1, len(docs))

    def vec(self, text: str) -> dict:
        tf = Counter(tokens(text))
        v = {w: c * math.log(1 + self.n / (1 + self.df.get(w, 0))) for w, c in tf.items()}
        norm = math.sqrt(sum(x * x for x in v.values())) or 1.0
        return {w: x / norm for w, x in v.items()}

    @staticmethod
    def cos(a: dict, b: dict) -> float:
        if len(a) > len(b):
            a, b = b, a
        return sum(x * b.get(w, 0.0) for w, x in a.items())


def card_text(c: dict) -> str:
    return " ".join(str(c.get(k, "")) for k in ("title", "problem", "approach", "interface", "result"))


def card_key(author: str, uuid: str) -> str:
    """Decisions and shown cards are keyed by author and uuid (a uuid alone can be reused by anyone)."""
    return f"{author}:{uuid}"


def confirmations(events: list[dict], card_uuid: str, author: str, identities: dict | None = None) -> int:
    rs = set()
    for e in events:
        if e["kind"] != nostr.KIND_REPORT or projects.same_identity(e["pubkey"], author, identities):
            continue
        try:
            c = json.loads(e["content"])
        except (ValueError, RecursionError):
            continue
        if isinstance(c, dict) and c.get("outcome") == "worked" and cards.report_target(e, c) == (author, card_uuid):
            rs.add((identities or {}).get(e["pubkey"]) or e["pubkey"])     # one per person
    return len(rs)


def rank(cands: list[dict], events: list[dict], ours: list[dict], rep: dict, identities: dict | None = None) -> list[dict]:
    """cands: [{"card", "author"}]; ours: [{"name", "text"}]. Returns cands with scores, best first."""
    if not cands:
        return []
    model = Tfidf([o["text"] for o in ours] + [card_text(c["card"]) for c in cands])
    our_vecs = [(o["name"], model.vec(o["text"])) for o in ours]
    reps = [reputation.score(rep, c["author"]) for c in cands]
    rmax = max([r for r in reps if r > 0], default=1.0)
    out = []
    for c, r in zip(cands, reps):
        v = model.vec(card_text(c["card"]))
        best_name, best = "", 0.0
        for name, ov in our_vecs:
            s = Tfidf.cos(v, ov)
            if s > best:
                best_name, best = name, s
        measured = bool(MEASURE.search(c["card"].get("result", "")))
        need = max(1.0 - best, 0.5 * best if measured else 0.0)
        conf = confirmations(events, c["card"]["id"], c["author"], identities)
        total = 0.35 * max(0.0, r) / rmax + 0.25 * min(conf, 5) / 5 + 0.40 * need
        why = (f"у нас похожее: {best_name} (сходство {best:.2f}), у них замер — может быть лучше"
               if best >= 0.5 and measured else
               f"у нас такого нет (ближайшее: {best_name or '—'}, сходство {best:.2f})" if best < 0.5 else
               f"у нас похожее: {best_name} (сходство {best:.2f})")
        out.append({**c, "reputation": round(r, 3), "confirmations": conf, "similar": best_name,
                    "similarity": round(best, 3), "need": round(need, 3), "score": round(total, 4), "why": why})
    out.sort(key=lambda x: (-x["score"], x["card"]["id"], x["author"]))
    return out


def build(archive, my_pubkey: str, ours_library: list[dict] | None = None, now: int | None = None,
          limit: int = 3, allow: dict | None = None, include_digested: bool = False) -> dict:
    """allow = the relay allowlist {pubkeys, identities}: authors off the list are skipped, confirmations and
    reputation count one per GitHub identity (Sybil limit)."""
    identities = (allow or {}).get("identities") or None
    now = int(time.time()) if now is None else now
    events = archive.all_events()
    rep = reputation.compute(events, now, identities)
    decided = archive.decisions()
    done = set() if include_digested else archive.digested()
    cands = []
    allowed = None if allow is None or allow.get("pubkeys") is None else set(allow["pubkeys"])   # [] = nobody
    for card in archive.cards(own=False):
        author = card["author"]["pubkey"]
        key = card_key(author, card["id"])
        ev = archive.card_event(card["id"], author)
        if not ev or author == my_pubkey or key in decided or key in done:
            continue
        if allowed is not None and author not in allowed:
            continue
        cands.append({"card": card, "author": ev["pubkey"], "event": ev["id"]})
    ours = list(ours_library if ours_library is not None else library_entries())
    ours += [{"name": f"card:{c['title']}", "text": card_text(c)} for c in archive.cards(own=True)]
    ranked = rank(cands, events, ours, rep, identities)[:limit]
    return {"items": ranked, "markdown": render(ranked), "generatedAt": now}


def _field(text) -> str:
    """Foreign text on one line, without CO markers, separators or headings."""
    return " ".join(guard.escape_markup(str(text or "")).split())


def render(items: list[dict]) -> str:
    """Foreign card text is escaped (no [[options]] / --- / headings of its own) and, when read-guard finds it
    suspicious, shown wrapped as untrusted data; the options line is always ours."""
    if not items:
        return "Дайджест Freedom: на этой неделе новых идей нет."
    blocks = []
    for i, it in enumerate(items, 1):
        c = it["card"]
        body = [f"**Проблема.** {_field(c['problem'])}", f"**Как.** {_field(c['approach'])}",
                f"**Интерфейс.** {_field(c['interface'])}", f"**Грабли.** {_field(c['pitfalls'])}",
                f"**Результат.** {_field(c['result'])}", f"**Цена.** {_field(c['cost'])}"]
        if c.get("code"):
            body.append(f"**Код.** {_field(c['code'])}")
        body_text = guard.present("\n".join(body), f"freedom card {c['id'][:8]}")
        lines = [f"### {i}. {_field(c['title'])}",
                 f"*Автор:* `{schnorr.npub(it['author'])[:20]}…` · репутация {it['reputation']} · "
                 f"подтверждённых внедрений {it['confirmations']} · {it['why']}",
                 "", body_text, f"`card {it['author'][:12]}…:{c['id']}`", "",
                 "[[options: " + " | ".join(f"{i} {ch}" for ch in CHOICES) + "]]"]
        blocks.append("\n".join(lines))
    return "\n\n---\n\n".join(blocks)


def parse_choice(reply: str, items: list[dict]) -> tuple[str, str] | None:
    """'2 взять код' -> ('<author pubkey>:<card uuid>', 'take-code')."""
    m = re.match(r"^\s*(\d+)\s+(.+?)\s*$", reply or "")
    if not m:
        return None
    n, label = int(m.group(1)), m.group(2).lower()
    if not 1 <= n <= len(items) or label not in CHOICE_KEYS:
        return None
    return card_key(items[n - 1]["author"], items[n - 1]["card"]["id"]), CHOICE_KEYS[label]
