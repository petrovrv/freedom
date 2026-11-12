"""snapcompact.py - compact views of a Playwright aria snapshot (mode 'ai') for browser-agent (0.9.0).

The full snapshot is saved to a file; the agent gets a bounded summary (interactive elements with
refs first, then headings, links and a little text) or only the nodes matching --find/--grep.
Stdlib only, no daemon access: also used by `browser-agent.py search <file> <query>`.
"""
import os
import re
import tempfile
import time
from pathlib import Path

DEFAULT_BUDGET = 4000
KEEP_HOURS = 24
LINE_RE = re.compile(r"^(\s*)- (.*)$")
REF_RE = re.compile(r"\[ref=((?:f\d+)?e\d+)\]")  # frame refs: f1e5
LEVEL_RE = re.compile(r"\[level=(\d)\]")
CONTROLS = ("textbox", "searchbox", "combobox", "checkbox", "radio", "switch", "slider", "spinbutton",
            "button", "menuitem", "menuitemcheckbox", "menuitemradio", "tab", "option", "listbox", "treeitem")
TEXTY = ("paragraph", "text", "blockquote", "cell", "listitem", "generic", "article", "strong", "emphasis")
HEADER_PREFIX = "#! "  # header lines at the top of a saved file


def snap_dir(owner):
    """Per-job folder in the user's temp: %TEMP%/aiwb-browser-agent/<owner>."""
    return Path(tempfile.gettempdir()) / "aiwb-browser-agent" / re.sub(r"[^A-Za-z0-9_.:-]", "-", owner or "cli")


def cleanup(root):
    """Delete saved snapshots older than KEEP_HOURS (all owners)."""
    limit = time.time() - KEEP_HOURS * 3600
    try:
        for p in root.parent.glob("*/*.txt"):
            try:
                if p.stat().st_mtime < limit:
                    p.unlink()
            except OSError:
                pass
    except OSError:
        pass


def save(owner, session, reply):
    """Write the full snapshot with a small header; return the path."""
    d = snap_dir(owner)
    d.mkdir(parents=True, exist_ok=True)
    cleanup(d)
    stamp = time.strftime("%Y%m%d-%H%M%S") + f"-{int(time.time() * 1000) % 1000:03d}"
    path = d / f"{re.sub(r'[^A-Za-z0-9_-]', '-', session)}-{stamp}-tab{reply.get('tab', 0)}.txt"
    head = (f"{HEADER_PREFIX}title: {reply.get('title', '')}\n{HEADER_PREFIX}url: {reply.get('url', '')}\n"
            f"{HEADER_PREFIX}saved: {time.strftime('%Y-%m-%d %H:%M:%S')} | untrusted page content\n")
    path.write_text(head + (reply.get("snapshot") or ""), encoding="utf-8")
    return path


def load(path):
    """Return (meta dict, snapshot text) of a saved file (plain snapshots without header work too)."""
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    meta, lines = {}, text.split("\n")
    i = 0
    while i < len(lines) and lines[i].startswith(HEADER_PREFIX):
        k, _, v = lines[i][len(HEADER_PREFIX):].partition(": ")
        meta[k] = v
        i += 1
    return meta, "\n".join(lines[i:])


def parse(text):
    """Flat list of nodes: dict(i, depth, role, name, ref, level, text, raw)."""
    nodes = []
    for i, line in enumerate(text.split("\n")):
        m = LINE_RE.match(line)
        if not m:
            continue
        body = m.group(2)
        node = {"i": i, "depth": len(m.group(1)) // 2, "raw": body, "role": "", "name": "", "ref": "",
                "level": "", "text": "", "url": ""}
        if body.startswith("/url:"):
            node["role"] = "/url"
            node["text"] = body[5:].strip().strip('"')
            nodes.append(node)
            continue
        role = re.match(r"[A-Za-z/][\w-]*", body)
        node["role"] = role.group(0) if role else ""
        rest = body[len(node["role"]):]
        nm = re.match(r'\s*"((?:[^"\\]|\\.)*)"', rest)
        if nm:
            node["name"] = nm.group(1).replace('\\"', '"')
            rest = rest[nm.end():]
        r = REF_RE.search(rest)
        node["ref"] = r.group(1) if r else ""
        lv = LEVEL_RE.search(rest)
        node["level"] = lv.group(1) if lv else ""
        tail = re.sub(r"\s*\[[^\]]*\]", "", rest)
        if tail.startswith(":"):
            node["text"] = tail[1:].strip().strip('"').replace('\\"', '"')
        nodes.append(node)
    # link urls and nameless links/buttons get their label from the subtree
    for k, n in enumerate(nodes):
        if n["role"] in ("link", "button") or n["role"] in CONTROLS:
            for c in nodes[k + 1:k + 8]:
                if c["depth"] <= n["depth"]:
                    break
                if c["role"] == "/url" and not n["url"]:
                    n["url"] = c["text"]
                elif not n["name"] and not n["text"] and (c["name"] or c["text"]):
                    n["text"] = c["name"] or c["text"]
    return nodes


def short(s, n):
    s = " ".join(str(s or "").split())
    return s if len(s) <= n else s[:n - 1] + "…"


def label(n, width=80):
    """One compact line for a node: role "name" ref [: text] [-> url]."""
    parts = [n["role"]]
    if n["level"]:
        parts[0] = f"h{n['level']}"
    if n["name"]:
        parts.append(f'"{short(n["name"], width)}"')
    if n["ref"]:
        parts.append(n["ref"])
    s = " ".join(parts)
    if n["text"] and n["text"] != n["name"]:
        s += ": " + short(n["text"], width)
    if n["url"]:
        s += " -> " + short(n["url"], 70)
    return s


def counts(nodes):
    c = {}
    for n in nodes:
        if n["role"] and n["role"] != "/url":
            c[n["role"]] = c.get(n["role"], 0) + 1
    return c


def summary(text, title="", url="", path=None, budget=DEFAULT_BUDGET):
    """Bounded overview: controls with refs, headings, links, start of the text."""
    budget = max(500, int(budget or DEFAULT_BUDGET))
    nodes = parse(text)
    cnt = counts(nodes)
    refs = sum(1 for n in nodes if n["ref"])
    top = ", ".join(f"{k} {v}" for k, v in sorted(cnt.items(), key=lambda kv: -kv[1])[:12])
    out = [f"# {title} | {url} | untrusted page content"]
    if path:
        out.append(f"# full snapshot: {path} ({len(text)} chars, {text.count(chr(10)) + 1} lines, {refs} refs)")
        out.append("# more (no new snapshot): search last TEXT | --ref eN | --role textbox | --lines A:B | --grep RE")
    out.append(f"# counts: {top}")
    used = sum(len(s) + 1 for s in out)
    shown = {}

    def section(name, items, cap, total):
        nonlocal used
        if not items:
            return
        head = f"## {name} ({len(items)})"
        if used + len(head) + 1 > min(cap, budget):
            return
        out.append(head)
        used += len(head) + 1
        k = 0
        for s in items:
            if used + len(s) + 3 + 24 > min(cap, budget):  # keep room for the "... more" line
                break
            out.append("- " + s)
            used += len(s) + 3
            k += 1
        shown[name] = (k, len(items), total)
        if k < len(items):
            more = f"  ... {len(items) - k} more"
            out.append(more)
            used += len(more) + 1

    seen, ctrl, links, heads, texts = set(), [], [], [], []
    for n in nodes:
        if n["role"] in CONTROLS and n["ref"]:
            s = label(n, 60)
            if s not in seen:
                seen.add(s)
                ctrl.append(s)
        elif n["role"] == "link" and n["ref"]:
            key = (n["name"] or n["text"], n["url"])
            if key not in seen:
                seen.add(key)
                links.append(label(n, 60))
        elif n["role"] == "heading":
            heads.append(label(n, 90))
        elif n["role"] in TEXTY and len(n["text"]) >= 40:
            texts.append(short(n["text"], 200))
    section("controls", ctrl, used + int(budget * 0.35), cnt)
    section("headings", heads, used + int(budget * 0.20), cnt)
    section("links", links, int(budget * 0.90), cnt)
    section("text", texts, budget, cnt)
    return "\n".join(out)


def find(text, query, regex=False, context=1, budget=DEFAULT_BUDGET, title="", url="", path=None):
    """Lines matching the query (substring, case-insensitive; or regex) with their ref-bearing ancestors."""
    budget = max(500, int(budget or DEFAULT_BUDGET))
    try:
        rx = re.compile(query if regex else re.escape(query), re.I)
    except re.error as e:
        raise ValueError(f"bad regex: {e}")
    lines = text.split("\n")
    nodes = {n["i"]: n for n in parse(text)}
    hits = [i for i, l in enumerate(lines) if rx.search(l)]
    out = [f"# {title} | {url} | untrusted page content"] if (title or url) else []
    if path:
        out.append(f"# full snapshot: {path}")
    out.append(f"# {len(hits)} match(es) for {'/' + query + '/' if regex else repr(query)}")
    used = sum(len(s) + 1 for s in out)
    last_end, shown = -1, 0
    for i in hits:
        if i <= last_end:
            continue  # already printed as context of the previous hit
        # ancestors with refs (nearest first, up to 2)
        anc, depth = [], (nodes[i]["depth"] if i in nodes else 99)
        for j in range(i - 1, -1, -1):
            n = nodes.get(j)
            if n and n["depth"] < depth:
                depth = n["depth"]
                if n["ref"] and n["role"] not in ("generic",):
                    anc.append(f"{n['role']}{' ' + chr(34) + short(n['name'], 40) + chr(34) if n['name'] else ''} {n['ref']}")
                    if len(anc) == 2:
                        break
            if depth == 0:
                break
        a, b = max(0, i - context, last_end + 1), min(len(lines) - 1, i + context)
        block = [f"@L{i + 1}" + (f" in {' < '.join(anc)}" if anc else "")]
        block += [("> " if k == i else "  ") + short(lines[k].strip(), 220) for k in range(a, b + 1)]
        size = sum(len(s) + 1 for s in block)
        if used + size > budget:
            out.append(f"... {len([h for h in hits if h > last_end]) } more match(es) not shown; narrow the query"
                       " or raise --budget")
            break
        out += block
        used += size
        last_end, shown = b, shown + 1
    return "\n".join(out)


def _budgeted(out, lines, budget, more_hint):
    used = sum(len(s) + 1 for s in out)
    for k, s in enumerate(lines):
        if used + len(s) + 1 > budget:
            out.append(f"... {len(lines) - k} more line(s) not shown; {more_hint}")
            break
        out.append(s)
        used += len(s) + 1
    return "\n".join(out)


def _head(title, url, path):
    out = [f"# {title} | {url} | untrusted page content"] if (title or url) else []
    if path:
        out.append(f"# full snapshot: {path}")
    return out


def subtree(text, ref, budget=DEFAULT_BUDGET, title="", url="", path=None):
    """The node with [ref=REF] and everything nested under it (e.g. a form, dialog, list row)."""
    budget = max(500, int(budget or DEFAULT_BUDGET))
    lines = text.split("\n")
    tag = f"[ref={ref}]"
    start = next((i for i, l in enumerate(lines) if tag in l), None)
    out = _head(title, url, path)
    if start is None:
        out.append(f"# ref {ref} not found; refs change after page updates - take a new snapshot")
        return "\n".join(out)
    m = LINE_RE.match(lines[start])
    depth = len(m.group(1)) if m else 0
    end = start + 1
    while end < len(lines):
        m2 = LINE_RE.match(lines[end])
        if m2 and len(m2.group(1)) <= depth:
            break
        end += 1
    out.append(f"# ref {ref}: lines {start + 1}-{end}")
    body = [lines[k][depth:] for k in range(start, end)]
    return _budgeted(out, body, budget, f"use --lines {start + 1}:{end} with a larger --budget")


def by_role(text, role, budget=DEFAULT_BUDGET, title="", url="", path=None):
    """All nodes of a role (textbox, button, link, heading, row...) as compact lines with line numbers."""
    budget = max(500, int(budget or DEFAULT_BUDGET))
    roles = {r.strip().lower() for r in role.split(",") if r.strip()}
    hits = [n for n in parse(text) if n["role"].lower() in roles]
    out = _head(title, url, path)
    out.append(f"# {len(hits)} node(s) with role {','.join(sorted(roles))}")
    return _budgeted(out, [f"@L{n['i'] + 1} {label(n, 80)}" for n in hits], budget, "narrow with --find or raise --budget")


def fragment(text, spec, budget=DEFAULT_BUDGET, title="", url="", path=None):
    """Raw lines A:B (1-based, inclusive; 'A:' to the end, 'A' = 40 lines from A)."""
    budget = max(500, int(budget or DEFAULT_BUDGET))
    lines = text.split("\n")
    a, sep, b = str(spec).partition(":")
    try:
        a = max(1, int(a or 1))
        b = int(b) if b else (len(lines) if sep else a + 39)
    except ValueError:
        raise ValueError(f"bad --lines {spec!r}; use A:B, A: or A")
    b = min(b, len(lines))
    out = _head(title, url, path)
    out.append(f"# lines {a}-{b} of {len(lines)}")
    return _budgeted(out, [f"{k + 1}: {lines[k]}" for k in range(a - 1, b)], budget,
                     "continue with the next --lines range")


def latest(owner, session):
    """Newest saved snapshot of this owner and session, or None."""
    d = snap_dir(owner)
    pref = re.sub(r"[^A-Za-z0-9_-]", "-", session) + "-"
    files = sorted((p for p in d.glob(pref + "*.txt")), key=lambda p: p.stat().st_mtime) if d.is_dir() else []
    return str(files[-1]) if files else None
