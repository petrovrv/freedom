# SPDX-License-Identifier: AGPL-3.0-or-later
"""Structural credential scan for the outbound filter (final review of the strong-key rule).

Not a list of regex cases but one conservative procedure; false positives are fine (the owner sees why a draft is
held):

1. Find every strong-key WORD in the homoglyph skeleton of the text: inside an identifier (MY_COMPANY_DB_PASSWORD,
   apiKey, x-api-key), in markdown (**password**, `password`, | password |), as an XML tag (<password>), with a
   parenthetical (password (prod)), in several languages (пароль, токен, ключ, wachtwoord, Passwort, Kennwort,
   mot de passe).
2. After it (and after closing quotes / markdown / a parenthetical, possibly on the next line) expect a separator
   (: = => -> := | - >) - or, for CLI and label forms, a space.
3. Take the value: the rest of the line, or for an empty rest the next non-empty line when it continues the value
   (indented, a list item, a quoted string, a JSON/YAML continuation).
4. Block unless the value is EXACTLY one anchored placeholder (<your password>, ${VAR}, %VAR%, {{ var }}, ***, xxx,
   changeme, redacted, none, n/a, your_password ...). A trailing "# comment" never makes a value clean.

Also: CLI forms (--password X, -p X after sshpass/mysql, setenv/export KEY VALUE) and URL-encoded password%3D... .
"""
from __future__ import annotations

import re

# ---------------------------------------------------------------- what is a strong key

STRONG_PARTS = {
    "password", "passwords", "passphrase", "passwd", "pwd", "pw", "pass", "passcode", "pin", "secret", "secrets",
    "token", "nsec", "credential", "credentials", "bearer", "authorization", "cookie", "apikey", "privatekey",
    "privkey", "accesskey", "clientsecret", "wachtwoord", "passwort", "kennwort",
}
PAIR_FIRST = {"api", "private", "access", "client", "secret", "x"}
PAIR_SECOND = {"key", "keys", "secret"}
# substrings that make any identifier strong (ASCII ones and the skeleton of non-Latin ones, see _skel_words)
SUBSTRINGS = ["password", "passwd", "passphrase", "passcode", "wachtwoord", "passwort", "kennwort", "apikey",
              "privatekey", "accesskey", "clientsecret"]
NON_LATIN = ["пароль", "токен", "ключ"]


def _parts(ident: str) -> list[str]:
    out = []
    for chunk in re.split(r"[_\-.]+", ident):
        out += [p.lower() for p in re.findall(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+", chunk)]
    return [p for p in out if p]


def strong_identifier(ident: str, skel_lower: str, non_latin: list[str]) -> bool:
    """ident: the identifier in skeleton form with its original case; skel_lower: lowered."""
    parts = _parts(ident)
    if any(p in STRONG_PARTS for p in parts):
        return True
    if any(a in PAIR_FIRST and b in PAIR_SECOND for a, b in zip(parts, parts[1:])):
        return True
    flat = skel_lower.replace("_", "").replace("-", "").replace(".", "")
    return any(s in flat for s in SUBSTRINGS) or any(w in skel_lower for w in non_latin)


def strong_key_name(key: str) -> bool:
    """Same test for a bare key string (compatibility with the older API)."""
    from .outfilter import skeleton
    sk = skeleton(key)
    return strong_identifier(sk, sk.lower(), [skeleton(w) for w in NON_LATIN])


# ---------------------------------------------------------------- placeholders (anchored, exact)

_KEYW = (r"(?:password|passphrase|passwd|pwd|pass(?:code)?|pin|secret|token|api[ _-]?key|key|access[ _-]?key|"
         r"private[ _-]?key|client[ _-]?secret|credentials?|value|redacted|bearer(?:[ _-]?token)?|cookie|session|"
         r"user(?:name)?|here)")
PLACEHOLDER_RES = [
    re.compile(r"(?i)^<\s*(?:your[ _-]?|my[ _-]?|the[ _-]?)?" + _KEYW + r"(?:[ _-]?here)?\s*>$"),
    re.compile(r"^\$\{?[A-Z][A-Z0-9_]*\}?$"),                 # ${VAR} / $VAR, upper case only ($Hunter2 is a value)
    re.compile(r"^%[A-Z][A-Z0-9_]*%$"),
    re.compile(r"^\{\{\s*[\w.]+\s*\}\}$"),
    re.compile(r"^\*{3,}$"),
    re.compile(r"(?i)^x{3,}$"),
    re.compile(r"^(?:\.{3}|…)$"),
    re.compile(r"(?i)^(?:changeme|redacted|none|null|n/a|tbd|todo)$"),
    re.compile(r"(?i)^your[_-]" + _KEYW + r"$"),
]
AUTH_SCHEME = re.compile(r"(?i)^(?:bearer|basic|token|ssws|apikey|api-key|digest|negotiate|hmac|key)\s+")


def is_placeholder(value: str) -> bool:
    v = value.strip()
    return any(p.match(v) for p in PLACEHOLDER_RES)


# ---------------------------------------------------------------- scanning

IDENT_RE = re.compile(r"[\w][\w\-.]*[\w]|[\w]")
SEP_RE = re.compile(r"\s*(=>|->|:=|:|=|\||–|—|-(?=\s))")
PHRASES = [re.compile(r"(?i)\bmot\s+de\s+passe\b")]
CLI_RES = [
    (re.compile(r"(?i)\b(?:sshpass|mysql|mysqldump|mysqladmin|mariadb)\b[^\n]*?\s-p\s*([^\s\-]\S*)"), "CLI -p password"),
    (re.compile(r"(?i)(?:^|\s)--?(?:password|passwd|pass|pwd|token|secret|api-?key|access-?key)(?:=|\s+)([^\s=-]\S*)"),
     "CLI option value"),
    (re.compile(r"(?im)^\s*(?:setenv|export|set)\s+([\w]+)\s+([^\s=]\S*(?:\s+\S+)?)\s*$"), "setenv value"),
    (re.compile(r"(?i)(?:password|passwd|pwd|pass|token|secret|api_?key|access_?key)%3d([^&\s#]+)"), "URL-encoded value"),
]


def _clean_value(v: str) -> str:
    v = v.strip()
    v = re.sub(r"[\s,;]+$", "", v)
    while v and v[-1] in "}])|" and v.count(v[-1]) > v.count({"}": "{", "]": "[", ")": "(", "|": "\0"}[v[-1]]):
        v = v[:-1].rstrip(" ,;")
    for w in ("**", "__", "`", '"', "'"):
        if len(v) >= 2 * len(w) and v.startswith(w) and v.endswith(w):
            v = v[len(w):-len(w)].strip()
    v = v.strip("`*").strip()
    if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
        v = v[1:-1].strip()
    return v


def _line_bounds(text: str, i: int) -> tuple[int, int]:
    s = text.rfind("\n", 0, i) + 1
    e = text.find("\n", i)
    return s, (len(text) if e < 0 else e)


def _continuation(text: str, line_end: int, key_indent: int) -> str:
    """The next non-empty line if it continues a value (indented, a list item, quoted, a JSON/YAML continuation)."""
    pos, steps = line_end + 1, 0
    while pos <= len(text) and steps < 3:
        e = text.find("\n", pos)
        e = len(text) if e < 0 else e
        line = text[pos:e]
        if line.strip():
            indent = len(line) - len(line.lstrip())
            st = line.lstrip()
            if indent > key_indent or st[:1] in "-*\"'`{[:|>":
                return line
            return ""
        pos, steps = e + 1, steps + 1
    return ""


def find(text: str, skel: str) -> list[tuple[int, str, str]]:
    """[(position, value, reason)] in `text` (already normalised); skel = skeleton(text), same length."""
    low = skel.lower()
    non_latin = [w for w in _skel_words()]
    hits = []
    spans = [(m.start(), m.end(), m.group(0)) for m in IDENT_RE.finditer(skel)]
    for ph in PHRASES:
        spans += [(m.start(), m.end(), "password") for m in ph.finditer(skel)]
    spans.sort()
    for start, end, ident in spans:
        if not strong_identifier(ident, low[start:end], non_latin):
            continue
        r = _value_after(text, start, end)
        if r is None:
            continue
        value, how = r
        key = text[start:end]
        if key.lower() in ("authorization", "bearer") or "author" in key.lower():
            value = AUTH_SCHEME.sub("", value, count=1).strip()
        if value and not is_placeholder(value):
            hits.append((start, value, f"value of the secret key '{key}' ({how}; only exact placeholders pass)"))
    for rx, why in CLI_RES:
        for m in rx.finditer(text):
            if why == "setenv value":
                sk = skeleton_of(m.group(1))
                if not strong_identifier(sk, sk.lower(), non_latin):
                    continue
                val = m.group(2)
            else:
                val = m.group(1)
            if not is_placeholder(_clean_value(val)):
                hits.append((m.start(), val, why))
    return hits


def skeleton_of(s: str) -> str:
    from .outfilter import skeleton
    return skeleton(s)


_SKEL_WORDS = None


def _skel_words():
    global _SKEL_WORDS
    if _SKEL_WORDS is None:
        from .outfilter import skeleton
        _SKEL_WORDS = [skeleton(w).lower() for w in NON_LATIN]
    return _SKEL_WORDS


def _value_after(text: str, start: int, end: int):
    """(value, how) after a strong key at text[start:end], or None when no value follows."""
    ls, le = _line_bounds(text, start)
    line = text[ls:le]
    indent = len(line) - len(line.lstrip())
    before = text[ls:start]
    # XML element <password ...>value</password>
    m = re.match(r"[^<>\n]*>", text[end:]) if before.rstrip().endswith("<") else None
    if m:
        tag = text[start:end]
        close = re.compile(r"</\s*" + re.escape(tag) + r"\s*>", re.I).search(text, end + m.end())
        if not close:
            return None                   # "<token>" alone is a placeholder, not an element
        return _clean_value(text[end + m.end():close.start()]), "XML element"
    j = end
    # closing quotes / markdown around the key, and an optional parenthetical
    mw = re.match(r"(?:[\"'`*_]|\*\*)*", text[j:])
    j += mw.end()
    mp = re.match(r"[ \t]*\([^()\n]{0,60}\)", text[j:])
    if mp:
        j += mp.end()
        j += re.match(r"(?:[\"'`*_])*", text[j:]).end()
    # separator, possibly after a line break ({"password"\n : "x"})
    ms = SEP_RE.match(text, j)
    if ms:
        sep_end = ms.end()
    else:
        mnl = re.match(r"[ \t]*\n[ \t]*(?:=>|:=|:|=)", text[j:])
        sep_end = j + mnl.end() if mnl else None
    if sep_end is not None:
        _, le2 = _line_bounds(text, sep_end) if sep_end < len(text) else (0, len(text))
        rest = text[sep_end:le2]
        if re.match(r"^[|>][+-]?\d?[+-]?\s*(?:#.*)?$", rest.strip()):          # YAML block scalar
            cont = _continuation(text, le2, indent)
            return (cont.strip() or rest.strip()), "block scalar"
        if not rest.strip():
            cont = _continuation(text, le2, indent)
            if not cont:
                return None
            rest = re.sub(r"^\s*[-*]\s+", "", cont)
            rest = re.sub(r"^\s*(=>|:=|:|=)\s*", "", rest)
            how = "value on the next line"
        else:
            how = "assignment"
        if before.lstrip().startswith("|") or rest.rstrip().endswith("|"):
            rest = rest.split("|")[0] if rest.strip().split("|")[0].strip() else rest
        return _clean_value(rest), how
    # space separator only for a markdown-wrapped label (**password** hunter2, `token` abc); plain prose such as
    # "password manager" or "the token budget" is not an assignment. CLI and setenv forms: CLI_RES.
    rest = text[j:le]
    wrapped = text[end:j].strip() != "" and re.search(r"[*`]$", before)
    if wrapped and rest.strip():
        return _clean_value(rest), "markdown label"
    return None
