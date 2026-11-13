# SPDX-License-Identifier: AGPL-3.0-or-later
"""Outbound filter (SPEC §1.3, §6.4): a mechanical check of every text before it leaves the workbench.

A hit blocks publication; the owner sees each hit with the rule and the reason. Two groups of rules:

* generic (anyone can re-run them on a published post, so a flag by another workbench is verifiable, §8):
  email, phone, iban, address, user-path, secret, ip, tailnet;
* private (need this workbench's own lists): name (members of install.json + dossier people), known-email,
  owner-domain, owner-path.

Secret patterns come from the tool library (toolbox/security.py, the `toolbox scan` rules) with a built-in copy as
fallback. Owner exceptions (`allow`) drop hits whose matched text equals an allowed term.
"""
from __future__ import annotations

import functools
import ipaddress
import json
import re
import subprocess
import sys
import unicodedata
from dataclasses import dataclass, field

from . import config

GENERIC_RULES = {"email", "phone", "iban", "address", "user-path", "secret", "ip", "tailnet"}
PRIVATE_RULES = {"name", "known-email", "owner-domain", "owner-path"}

# ---------------------------------------------------------------- secrets: toolbox scan patterns

_FALLBACK_SECRET = [
    r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVAT[E] KEY-----",
    r"\bAKI[A]\w{16}\b", r"\bgh[pousr]_[A-Za-z0-9]{36,}\b", r"\bgithub_pa[t]_[A-Za-z0-9_]{60,}\b",
    r"\bAIz[a]Sy[A-Za-z0-9_\-]{33}\b", r"\bya2[9]\.[A-Za-z0-9_\-]{30,}", r"\b1//0[A-Za-z0-9_\-]{40,}",
    r"\bGOCSP[X]-[A-Za-z0-9_\-]{20,}", r"\bsk-an[t]-[A-Za-z0-9_\-]{30,}", r"\bsk-(?:pro[j]-)?[A-Za-z0-9_\-]{40,}",
    r"\bxox[abprs]-[A-Za-z0-9\-]{20,}", r"\beyJ[A-Za-z0-9_\-]{20,}\.eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{20,}",
]


def _toolbox_security():
    try:
        sys.path.insert(0, str(config.tools_dir() / "toolbox"))
        import security  # type: ignore
        return security
    except Exception:  # noqa: BLE001 - any import problem: use the built-in copy
        return None
    finally:
        if sys.path and sys.path[0].endswith("toolbox"):
            sys.path.pop(0)


_SEC = _toolbox_security()
SECRET_PATTERNS = list(_SEC.SECRET_PATTERNS) if _SEC else [re.compile(p) for p in _FALLBACK_SECRET]
SECRET_SOURCE = "toolbox/security.py" if _SEC else "built-in copy"
SECRET_PATTERNS += [re.compile(r"\bnse[c]1[02-9ac-hj-np-z]{58}\b")]            # Nostr secret key (NIP-19)
SECRET_NAME = _SEC.SECRET_NAME if _SEC else re.compile(
    r"(?i)(passw(or)?d|passwd|secret|api[_-]?key|apikey|access[_-]?key|auth[_-]?token|token|private[_-]?key|pin)")
# key/value credentials in any common form (round-3 review P1): JSON "password": "...", YAML password: ...,
# env PASSWORD=..., query ?token=..., with or without quotes around key and value
SECRET_KEYS = (r"passw(?:or)?d|passwd|pwd|pass|secret|client[_-]?secret|token|access[_-]?token|refresh[_-]?token|"
               r"auth[_-]?token|api[_-]?key|apikey|access[_-]?key|secret[_-]?key|private[_-]?key|privkey|nsec|"
               r"authorization|cookie|session(?:[_-]?id)?|credentials?|bearer")
ASSIGN_RE = re.compile(r"(?i)(?<![\w])[\"']?([\w.-]{0,30}?(?:" + SECRET_KEYS + r")[\w.-]{0,20})[\"']?[ \t]*[:=][ \t]*"
                       r"(?:\"([^\"\n]{1,500})\"|'([^'\n]{1,500})'|(<[^<>\n]{1,100}>|\{\{[^{}\n]{1,100}\}\})"
                       r"|([^\s\"'`,;&}{\]\[)(]{1,500}))")
AUTH_HEADER_RE = re.compile(r"(?i)\b(?:proxy-)?authorization\s*:\s*(?:bearer|basic|token|digest|negotiate)\s+"
                            r"([A-Za-z0-9._~+/=-]{8,})")
COOKIE_HEADER_RE = re.compile(r"(?im)^\s*(?:set-)?cookie\s*:\s*([^\n]{8,})")
URL_USERINFO_RE = re.compile(r"(?i)\b[a-z][a-z0-9+.-]{1,15}://([^\s/:@]{1,100}):([^\s/@]{1,200})@[^\s/]")
TOKEN_RE = re.compile(r"(?<![A-Za-z0-9+/_=-])([A-Za-z0-9+/_-]{32,}={0,2})(?![A-Za-z0-9+/_=-])")
PLACEHOLDER = re.compile(r"(?i)^(?:<[^>]*>|\$\{?[\w.]+\}?|%[\w]+%|\{\{.*\}\}|x{3,}|\*{3,}|\.{3}|…|changeme|"
                         r"example\w*|dummy|placeholder|redacted|your[_-]?\w+|secret|password|token|none|null|true|"
                         r"false|\*+|-+|n/?a|tbd|todo|\?+)$")


def _entropy(v: str) -> float:
    import math
    freq = {c: v.count(c) for c in set(v)}
    return -sum(n / len(v) * math.log2(n / len(v)) for n in freq.values())


def strong_key_name(key: str) -> bool:
    """One definition of a strong key (community/secretscan.py)."""
    from . import secretscan
    return secretscan.strong_key_name(key)


def credential_value(v: str, strong_key: bool) -> bool:
    """A value given to a secret-named key is a credential unless it is an explicit placeholder. Strong keys
    (password, secret, private key, api key, token, credential ...) block ANY other value of >= 4 characters,
    whatever its shape - passphrases with spaces and path-like values included (round-4 review). Weaker keys
    (session, cookie, auth ...) need a token-like value."""
    v = v.strip()
    if not v or PLACEHOLDER.match(v):
        return False
    if strong_key:
        return len(v) >= 4
    if len(v) < 6 or " " in v or re.fullmatch(r"[\d.,_%]+", v):   # numbers are not credentials
        return False
    if v.isalpha() and v.islower():
        return False                      # "required", "optional": prose
    return bool(re.search(r"\d", v)) or (any(c.isupper() for c in v) and any(c.islower() for c in v)) or _entropy(v) >= 3.0


def token_like(v: str) -> bool:
    """A long free-standing token: >= 32 chars of base64/base64url, upper + lower + digit, high entropy. 64-hex ids
    (events, keys) and uuids are public and do not match (no upper case)."""
    return (len(v) >= 32 and any(c.isupper() for c in v) and any(c.islower() for c in v)
            and any(c.isdigit() for c in v) and _entropy(v) >= 4.0)


def _looks_secret(v: str) -> bool:
    if _SEC:
        try:
            return bool(_SEC.looks_secret(v))
        except Exception:  # noqa: BLE001
            pass
    import math
    if re.search(r"(?i)xxx|<|>|\$\{|example|your[_-]|changeme|dummy|placeholder|redacted|\*\*\*", v):
        return False
    if len(v) < 12 or not re.search(r"\d", v) or "/" in v or "\\" in v:
        return False
    freq = {c: v.count(c) for c in set(v)}
    return -sum(n / len(v) * math.log2(n / len(v)) for n in freq.values()) >= 3.3


# ---------------------------------------------------------------- generic patterns

EMAIL_RE = re.compile(r"(?<![\w.+-])([A-Za-z0-9][A-Za-z0-9._%+-]{0,63})@((?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,24})(?![\w-])")
EXAMPLE_DOMAINS = re.compile(r"(?i)(?:^|\.)(?:example\.(?:com|org|net)|example|test|invalid|localhost|"
                             r"users\.noreply\.github\.com)$")
NON_PERSONAL_LOCAL = {"git", "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster"}
PUBLIC_MAIL = {"gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com",
               "icloud.com", "me.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "gmx.de", "mail.ru",
               "yandex.ru", "ya.ru", "skynet.be", "telenet.be", "aol.com", "web.de", "zoho.com"}

PHONE_CAND = re.compile(r"(?<![\w.,/:#-])(?:\+|00)?\(?\d[\d \t().\-/]{6,22}\d(?![\w,/:-]|\.\d)")
DATE_LIKE = re.compile(r"^(?:19|20)\d{2}[-./]\d{1,2}[-./]\d{1,2}|^\d{1,2}[-./]\d{1,2}[-./]\d{2,4}$|^\d{1,2}:\d{2}")
IBAN_CAND = re.compile(r"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,32}")

STREET_NL_DE = (r"straat|laan|steenweg|dreef|plein|kaai|lei|weg|baan|singel|gracht|markt|"
                r"stra(?:ss|ß)e|gasse|allee|platz|ring|damm|ufer")
ADDRESS_RES = [
    # Kerkstraat 12, Frankrijklei 5A, Hauptstraße 3
    re.compile(r"\b[A-ZÀ-Ý][\w'’.-]*(?:" + STREET_NL_DE + r")\s+\d{1,4}(?:\s?[a-zA-Z](?![\w]))?(?:\s?(?:bus|bte|/)\s?\d+)?\b"),
    # 221B Baker Street, 10 Downing St
    re.compile(r"\b\d{1,5}[A-Za-z]?\s+(?:[A-Z][\w'’.-]+\s+){1,3}(?:Street|St\.|Road|Rd\.|Avenue|Ave\.|Lane|Ln\.|"
               r"Boulevard|Blvd\.?|Drive|Court|Square|Terrace|Crescent|Close)(?![\w])"),
    # 12 rue de la Loi / Rue de la Loi 16 / avenue Louise 54
    re.compile(r"(?i)\b\d{1,4}(?:\s?(?:bis|ter))?,?\s+(?:rue|avenue|av\.|boulevard|bd|chemin|place|allée|impasse|quai|"
               r"chaussée)\s+(?:(?:de|du|des|d'|la|le|l')\s*){0,2}[A-ZÀ-Ý][\w'’-]+"),
    re.compile(r"\b(?:[Rr]ue|[Aa]venue|[Bb]oulevard|[Cc]hemin|[Cc]haussée|[Qq]uai)\s+(?:(?:de|du|des|d'|la|le|l')\s*){0,2}"
               r"[A-ZÀ-Ý][\w'’-]+(?:\s[A-ZÀ-Ý][\w'’-]+)?\s+\d{1,4}\b"),
    # ул. Ленина, д. 5 / улица Пушкина 10 / пр-т Мира 3
    re.compile(r"(?:\b|(?<=\s))(?:ул\.|улица|пр-т|просп\.|проспект|пер\.|переулок|бульвар|б-р|шоссе|наб\.|набережная)"
               r"\s*[А-ЯЁ][\w-]+(?:\s+[А-ЯЁ]?[\w-]+)?,?\s*(?:д\.|дом)?\s*\d{1,4}"),
    # postcode with country prefix + city: B-9000 Gent, D-10115 Berlin; Dutch 1234 AB Amsterdam
    re.compile(r"\b(?:B|BE|NL|D|DE|F|FR|CH|L|LU|A|AT)-\d{4,5}\s+[A-ZÀ-Ý][\w'’-]+"),
    re.compile(r"\b[1-9]\d{3}\s?[A-Z]{2}\s+[A-Z][a-z]+"),
]
DUTCH_PC_STOP = {"AM", "PM", "AD", "BC", "CE", "OK", "ID", "IT", "UI", "AI", "DB", "MB", "GB", "KB", "TB", "MW", "KW"}

WIN_USER = re.compile(r"(?i)\b[A-Z]:[\\/]+(?:Users|Documents and Settings)[\\/]+([^\\/:*?\"<>|\r\n\t ]+(?: [^\\/:*?\"<>|\r\n\t ]+)?)")
NIX_USER = re.compile(r"(?<![\w.~-])/(?:home|Users)/([^/\s'\"`<>]+)")
GENERIC_USERS = {"public", "default", "default user", "all users", "shared", "user", "username", "%username%",
                 "<user>", "<name>", "$user", "${user}", "me", "you", "someone", "name", "<username>", "...", "…",
                 "admin", "administrator", "runner", "ubuntu", "root", "<you>"}

IPV4_RE = re.compile(r"(?<![\w.])(\d{1,3}(?:\.\d{1,3}){3})(?![\w.]|\.\d)")
TAILNET_RE = re.compile(r"(?i)\b(?:[\w-]+\.)?tail[0-9a-f]{4,8}\.ts\.net\b|\btail[0-9a-f]{6}\b")

TWO_LEVEL_SUFFIX = {"co.uk", "org.uk", "ac.uk", "com.au", "co.jp", "co.nz", "com.br", "co.za", "com.tr", "com.ua",
                    "org.ru", "com.ru", "co.il", "or.jp", "ne.jp"}


def registrable(host: str) -> str:
    parts = host.lower().strip(".").split(".")
    if len(parts) >= 3 and ".".join(parts[-2:]) in TWO_LEVEL_SUFFIX:
        return ".".join(parts[-3:])
    return ".".join(parts[-2:])


# ---------------------------------------------------------------- normalisation (review item 8)

_SPACES = {"\u2028": "\n", "\u2029": "\n"}
# common Cyrillic/Greek letters that look Latin -> Latin; 1:1, so offsets stay valid
_CONFUSABLE = str.maketrans(
    "АВЕКМНОРСТХУІЈЅаеорсухіјѕкԁԛԝ" "ΑΒΕΖΗΙΚΜΝΟΡΤΥΧαοιν",
    "ABEKMHOPCTXYIJSaeopcyxijskdqw" "ABEZHIKMNOPTYXaoiv")


def normalize(text: str) -> str:
    """NFKC, format characters (zero-width, bidi, soft hyphen) removed, Unicode spaces -> ' '."""
    t = unicodedata.normalize("NFKC", text)
    out = []
    for ch in t:
        cat = unicodedata.category(ch)
        if cat == "Cf":
            continue
        out.append(_SPACES.get(ch, " " if cat == "Zs" else ch))
    return "".join(out)


@functools.lru_cache(maxsize=4096)
def _fold(ch: str) -> str:
    """One character without its accents (é -> e, ł stays); kept as is unless it folds to exactly one character."""
    base = "".join(c for c in unicodedata.normalize("NFKD", ch) if unicodedata.category(c) != "Mn")
    return base if len(base) == 1 else ch


def skeleton(text: str) -> str:
    """Homoglyph skeleton: accents dropped and Cyrillic/Greek look-alikes mapped to Latin; one character per input
    character, so match offsets stay valid."""
    return "".join(_fold(c) if ord(c) > 127 else c for c in text).translate(_CONFUSABLE)


def iban_ok(s: str) -> bool:
    s = s.replace(" ", "")
    if not 15 <= len(s) <= 34:
        return False
    r = s[4:] + s[:4]
    try:
        return int("".join(str(int(c, 36)) for c in r)) % 97 == 1
    except ValueError:
        return False


@dataclass
class Hit:
    rule: str
    match: str
    reason: str
    start: int = 0
    where: str = ""

    def to_dict(self, mask: bool = True) -> dict:
        m = self.match
        if mask and self.rule == "secret" and len(m) > 10:
            m = m[:6] + "…" + m[-2:]
        return {"rule": self.rule, "match": m, "reason": self.reason, "where": self.where, "at": self.start}


@dataclass
class Result:
    hits: list = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.hits

    def rules(self) -> list[str]:
        return sorted({h.rule for h in self.hits})

    def to_dict(self) -> dict:
        return {"ok": self.ok, "hits": [h.to_dict() for h in self.hits]}

    def explain(self) -> str:
        if self.ok:
            return "filter: clean"
        lines = [f"filter: BLOCKED, {len(self.hits)} hit(s)"]
        for h in self.hits:
            d = h.to_dict()
            lines.append(f"- [{d['rule']}] {d['match']!r} {('in ' + d['where'] + ' ') if d['where'] else ''}— {d['reason']}")
        return "\n".join(lines)


NAME_SUFFIX_CYR = r"(?:а|я|у|ю|ом|ем|ой|ей|е|ы|и|ою|ею|ым|ым|ых|ов|ев|ину|ина|ине|ой)?"


class OutboundFilter:
    def __init__(self, names=(), emails=(), domains=(), owner_paths=(), allow=(), generic_only: bool = False):
        self.allow = {a.strip().lower() for a in allow if a and a.strip()}
        self.generic_only = generic_only
        self.emails = sorted({e.strip().lower() for e in emails if e and "@" in e})
        doms = {d.strip().lower().lstrip("@.") for d in domains if d and d.strip()}
        for e in self.emails:
            d = e.split("@", 1)[1]
            if d not in PUBLIC_MAIL:
                doms.add(registrable(d))
        self.domains = sorted(d for d in doms if "." in d and d not in PUBLIC_MAIL)
        self.owner_paths = sorted({p.strip().rstrip("\\/") for p in owner_paths if p and len(p.strip()) >= 4})
        self._name_res = self._compile_names(names)
        self._dom_res = [(d, re.compile(r"(?i)(?<![\w.-])(?:[\w-]+\.)*" + re.escape(skeleton(d)) + r"(?![\w-])"))
                         for d in self.domains]

    # -------------------------------------------------- construction from install.json / dossier

    @classmethod
    def from_sources(cls, install: dict | None = None, dossier_names=(), extra_names=(), extra_domains=(),
                     extra_paths=(), allow=()):
        install = install or {}
        names, emails, domains, paths = list(extra_names) + list(dossier_names), [], list(extra_domains), list(extra_paths)
        for m in install.get("members") or []:
            names += [m.get("name") or "", m.get("env") or ""]
            if m.get("email"):
                emails.append(m["email"])
        site = install.get("site") or {}
        if site.get("url"):
            host = re.sub(r"^\w+://", "", site["url"]).split("/")[0].split(":")[0]
            if host and not re.match(r"^[\d.]+$", host) and not EXAMPLE_DOMAINS.search(host):
                domains.append(registrable(host))
        for k in ("home", "webRoot"):
            if site.get(k):
                paths.append(site[k])
        drv = install.get("drive") or {}
        if drv.get("root"):
            paths.append(drv["root"])
        return cls(names=names, emails=emails, domains=domains, owner_paths=paths, allow=allow)

    @staticmethod
    def _compile_names(names):
        """Each person name: the full string and its capitalised tokens of >= 3 letters, case-sensitive (so "Rose"
        the name blocks, "rose by 5 %" passes); Cyrillic tokens also with case endings (Роман -> Романа, Роману).
        Environment ids (lowercase, e.g. "anna") match case-insensitively as whole words."""
        out, seen = [], set()
        for raw in names:
            raw = (raw or "").strip()
            if len(raw) < 3:
                continue
            terms = [raw] + [t for t in re.split(r"[\s,;()\"'«»]+", raw) if len(t) >= 3]
            for t in terms:
                key = t
                if key in seen:
                    continue
                seen.add(key)
                cyr = bool(re.search(r"[А-Яа-яЁё]", t))
                # regexes run on the homoglyph skeleton of both name and text (review item 8)
                if t.islower() and " " not in t:
                    rx = re.compile(r"(?i)(?<![\w@./\\-])" + re.escape(skeleton(t)) + r"(?![\w@-]|\.\w)")
                elif t[:1].isupper():
                    variants = [re.escape(skeleton(t)), re.escape(skeleton(t.upper()))]
                    suf = skeleton(NAME_SUFFIX_CYR) if cyr else r"(?:'s|’s)?"
                    rx = re.compile(r"(?<![\w@./\\-])(?:" + "|".join(variants) + r")" + suf + r"(?![\w@-])")
                else:
                    continue
                out.append((raw, rx))
                # surnames (not the first token) of >= 5 letters also match in any case: "kowalski", "KoWaLsKi"
                if t is not raw and t != terms[1] and len(t) >= 5 and t[:1].isupper():
                    suf = skeleton(NAME_SUFFIX_CYR) if cyr else r"(?:'s|’s)?"
                    out.append((raw, re.compile(r"(?i)(?<![\w@./\\-])" + re.escape(skeleton(t)) + suf + r"(?![\w@-])")))
        return out

    # -------------------------------------------------- checking

    def _allowed(self, text: str) -> bool:
        return text.strip().lower() in self.allow

    def check(self, text: str, where: str = "") -> Result:
        res = Result()
        if not text:
            return res
        text = normalize(text)
        skel = skeleton(text)
        add = res.hits.append

        def hit(rule, m, reason, start):
            if not self._allowed(m):
                add(Hit(rule, m, reason, start, where))

        # secrets first (a token may also look like other things)
        for p in SECRET_PATTERNS:
            for m in p.finditer(text):
                hit("secret", m.group(0), "looks like a key or token (toolbox scan secret patterns)", m.start())
        # strong keys: the structural scan (community/secretscan.py) decides; key/value regexes below only for weak keys
        from . import secretscan
        for at, val, why in secretscan.find(text, skel):
            hit("secret", val, why, at)
        for m in ASSIGN_RE.finditer(text):
            key = m.group(1)
            if re.search(r"(?i)(?:authorization|cookie)$", key) and m.group(5) is not None:
                continue                  # header forms: AUTH_HEADER_RE / COOKIE_HEADER_RE below read the value
            if strong_key_name(key):
                continue                  # strong keys: decided by secretscan above
            gi = next(i for i in (2, 3, 4, 5) if m.group(i) is not None)
            val = m.group(gi)
            if credential_value(val, False) or _looks_secret(val):
                hit("secret", val, f"credential assigned to '{key}'", m.start(gi))
        for rx, why in ((AUTH_HEADER_RE, "Authorization header credential"), (COOKIE_HEADER_RE, "Cookie header")):
            for m in rx.finditer(text):
                if not PLACEHOLDER.match(m.group(1).strip()):
                    hit("secret", m.group(1), why, m.start(1))
        for m in URL_USERINFO_RE.finditer(text):
            if not PLACEHOLDER.match(m.group(2)):
                hit("secret", m.group(2), f"password in a URL (user {m.group(1)})", m.start(2))
        for m in TOKEN_RE.finditer(text):
            if token_like(m.group(1)):
                hit("secret", m.group(1), "long high-entropy token", m.start(1))

        known = {skeleton(e) for e in self.emails}
        for m in EMAIL_RE.finditer(skel):
            addr, shown = m.group(0).lower(), text[m.start():m.end()]
            local, dom = m.group(1).lower(), m.group(2).lower()
            if not self.generic_only and addr in known:
                hit("known-email", shown, "e-mail of a member or a dossier person", m.start())
            elif EXAMPLE_DOMAINS.search(dom) or local in NON_PERSONAL_LOCAL or local.startswith("noreply"):
                continue
            else:
                hit("email", shown, "e-mail address", m.start())

        for m in PHONE_CAND.finditer(text):
            s = m.group(0).strip()
            digits = re.sub(r"\D", "", s)
            if DATE_LIKE.match(s) or (re.fullmatch(r"\d+(?:\.\d+){2,}", s) and len(digits) < 9):
                continue
            intl = s.startswith(("+", "00", "(+"))
            if intl and 8 <= len(digits) - (2 if s.startswith("00") else 0) <= 15:
                hit("phone", s, "international phone number", m.start())
            elif not intl and digits.startswith("0") and 9 <= len(digits) <= 11 and not digits.startswith("00"):
                groups = re.split(r"[ \t().\-/]+", s)
                if len(groups) >= 2 or len(digits) in (9, 10):
                    hit("phone", s, "national phone number", m.start())

        for m in IBAN_CAND.finditer(text):
            s = m.group(0).rstrip()
            for end in range(len(s), 14, -1):
                cand = s[:end].rstrip()
                if iban_ok(cand):
                    hit("iban", cand, "bank account number (IBAN, checksum valid)", m.start())
                    break

        for rx in ADDRESS_RES:
            for m in rx.finditer(text):
                s = m.group(0)
                if rx is ADDRESS_RES[-1] and s.split()[1][:2] in DUTCH_PC_STOP:
                    continue
                hit("address", s, "postal address", m.start())

        for rx in (WIN_USER, NIX_USER):
            for m in rx.finditer(text):
                u = m.group(1).strip().lower()
                if u in GENERIC_USERS or u.startswith(("<", "$", "%", "{")):
                    continue
                hit("user-path", m.group(0), f"filesystem path with a user name ({m.group(1)})", m.start())

        for m in IPV4_RE.finditer(text):
            try:
                ip = ipaddress.ip_address(m.group(1))
            except ValueError:
                continue
            before = text[max(0, m.start() - 9):m.start()].lower()
            if ip.is_global and not re.search(r"(?:v|version\s?|ver\.?\s?)$", before):
                hit("ip", m.group(1), "public IP address", m.start())

        for m in TAILNET_RE.finditer(text):
            hit("tailnet", m.group(0), "Tailscale tailnet name (identifies the owner's network)", m.start())

        if self.generic_only:
            return self._dedupe(res)

        for d, rx in self._dom_res:
            for m in rx.finditer(skel):
                if "@" in text[max(0, m.start() - 1):m.start()] and any(h.rule == "known-email" and h.start <= m.start()
                                                                     < h.start + len(h.match) for h in res.hits):
                    continue
                hit("owner-domain", text[m.start():m.end()], f"domain of the owner or a member ({d})", m.start())

        low = text.lower().replace("/", "\\")
        for p in self.owner_paths:
            pl = p.lower().replace("/", "\\")
            i = low.find(pl)
            while i >= 0:
                hit("owner-path", text[i:i + len(p)], "path of this installation", i)
                i = low.find(pl, i + 1)

        for raw, rx in self._name_res:
            for m in rx.finditer(skel):
                hit("name", text[m.start():m.end()], f"name of a member or dossier person ({raw})", m.start())
        return self._dedupe(res)

    @staticmethod
    def _dedupe(res: Result) -> Result:
        seen, out = set(), []
        for h in sorted(res.hits, key=lambda h: (h.start, h.rule)):
            k = (h.rule, h.match, h.start)
            if k not in seen:
                seen.add(k)
                out.append(h)
        res.hits = out
        return res

    def check_obj(self, obj, where: str = "") -> Result:
        """Every string of a JSON value (content and tags of an event), with its location."""
        res = Result()

        def walk(o, path):
            if isinstance(o, str):
                res.hits.extend(self.check(o, path).hits)
            elif isinstance(o, dict):
                for k, v in o.items():
                    walk(v, f"{path}.{k}" if path else str(k))
            elif isinstance(o, list):
                for i, v in enumerate(o):
                    walk(v, f"{path}[{i}]")
        walk(obj, where)
        return res

    unavailable = ""     # set by from_settings when the dossier is enabled but could not be read

    def check_event_parts(self, content: dict | str, free_tag_values: list) -> Result:
        """Content plus the free-text tag values from schemas.check_tags ([(where, text)]): ids, keys, coordinates and
        the identity tag are validated by shape there, every other tag value is checked here. A filter without its
        dossier names blocks every publication."""
        res = self.check_obj(content, "content") if not isinstance(content, str) else self.check(content, "content")
        if self.unavailable:
            res.hits.append(Hit("dossier-unavailable", "", f"names of the dossier unknown ({self.unavailable}); "
                                "publication blocked until the dossier answers", 0, "filter"))
        for where, text in free_tag_values:
            res.hits.extend(self.check(text, where).hits)
        return res


def generic_filter() -> OutboundFilter:
    """Rules every workbench can apply to anybody's post (used to verify flags, §8)."""
    return OutboundFilter(generic_only=True)


class DossierUnavailable(RuntimeError):
    pass


def dossier_names(tools=None, env: str | None = None, timeout: int = 60) -> list[str]:
    """People names from aiwb-dossier (subjects of active rows). A valid answer with no rows is an empty list; a missing
    tool, a failed call or an answer without "rows" raises DossierUnavailable (never fail open, re-review 2)."""
    tools = tools or config.tools_dir()
    script = tools / "aiwb-dossier" / "aiwb-dossier.py"
    if not script.exists():
        raise DossierUnavailable(f"aiwb-dossier not found under {tools}")
    cmd = [sys.executable, str(script), "--json"] + (["--env", env] if env else []) + ["list"]   # --json is global
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=timeout)
        data = json.loads(out.stdout)
    except (OSError, ValueError, subprocess.SubprocessError) as e:
        raise DossierUnavailable(f"aiwb-dossier failed: {type(e).__name__}") from None
    if out.returncode != 0 or not isinstance(data, dict) or not isinstance(data.get("rows"), list):
        raise DossierUnavailable(f"aiwb-dossier gave no rows (exit {out.returncode})")
    return sorted({r.get("subject", "").strip() for r in data["rows"] if isinstance(r, dict)
                   and r.get("subject") and r.get("section") in ("people", "me") and r.get("status", "active") == "active"})


def from_settings(settings: dict, install: dict | None = None, use_dossier: bool | None = None,
                  names_source=None) -> OutboundFilter:
    """With the dossier enabled but unreadable the filter still works, and blocks every publication
    (rule dossier-unavailable) until the dossier answers or the owner turns it off (gated setting)."""
    install = config.load_install() if install is None else install
    use = settings.get("dossier", True) if use_dossier is None else use_dossier
    dn, problem = [], ""
    if use:
        try:
            dn = (names_source or dossier_names)()
        except DossierUnavailable as e:
            problem = str(e)
    f = OutboundFilter.from_sources(install, dossier_names=dn, extra_names=settings.get("extraNames", []),
                                    extra_domains=settings.get("extraDomains", []),
                                    extra_paths=settings.get("extraPaths", []), allow=settings.get("filterAllow", []))
    f.unavailable = problem
    return f
