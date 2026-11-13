"""aiwb-assoc: associative chat search of AI Workbench - vectors and aliases in the CO database (schema 20).

  index    vectors of protocol entries (minishlab/potion-multilingual-128M, static model, 256 dims) and the word table
           the server builds query vectors from (sum of raw token embeddings per word + token count); hourly task
  aliases  build: other names of the things the env talks about (model + Wikidata) -> table aliases;
           list / add / rm by hand
  vec      debug: nearest protocol entries for a text, computed locally with the full model

Talks to db.php of the site (AIWB_DB_URL, else install.json site.url + /agent/db.php) with the home PC key (keyring aiwb/co-push, account default).
The search itself runs on the server: CO Web AI search (op search_chats) and aiwb-chat find (op find_chats).
"""
import argparse
import base64
import collections
import concurrent.futures as cf
import datetime as dt
import gzip
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def install_get(key, default=None):
    """One value of install.json: AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json (Windows), else
    ~/.config/aiwb/install.json; the default when the file or the key is missing."""
    path = os.environ.get("AIWB_INSTALL") or (
        os.path.join(os.environ["LOCALAPPDATA"], "AIWorkbench", "install.json")
        if os.name == "nt" and os.environ.get("LOCALAPPDATA")
        else os.path.join(os.path.expanduser("~"), ".config", "aiwb", "install.json"))
    try:
        with open(path, encoding="utf-8-sig") as f:
            cur = json.load(f)
    except (OSError, ValueError):
        return default
    for part in key.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur



def owner_env() -> str:
    """Environment of the owner: AIWB_OWNER_ENV, else the member with role owner in install.json, else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
    return owner.get("env") or "owner"


SITE_URL = (install_get("site.url") or "").rstrip("/")
API = os.environ.get("AIWB_DB_URL") or (SITE_URL + "/agent/db.php" if SITE_URL else "")
DEFAULT_ENV = os.environ.get("AIWB_ENV") or owner_env()
# Wikidata/Hugging Face etiquette: a contact in the User-Agent (AIWB_CONTACT_EMAIL, install.json contact.email /
# contact.url, else site.url)
CONTACT = (os.environ.get("AIWB_CONTACT_EMAIL") or install_get("contact.email") or install_get("contact.url")
           or SITE_URL)
LOCAL = Path(os.environ.get("LOCALAPPDATA", str(Path.home())))
MODEL_DIR = LOCAL / "AIWorkbench" / "models" / "potion-multilingual-128M"
HF = "https://huggingface.co/minishlab/potion-multilingual-128M/resolve/main/"
STATE = LOCAL / "AIWorkbench" / "aiwb-assoc"
WIKIDATA = "https://www.wikidata.org/w/api.php"
UA = "aiwb-assoc/0.1.1 (AI Workbench personal search" + (f"; {CONTACT})" if CONTACT else ")")
WORD = re.compile(r"[^\W]+", re.U)
DIM = 256
VOCAB_DAYS = 7          # full rescan of message texts for the vocabulary
MIN_FREQ = 2            # message words need 2+ occurrences, protocol words go in at once
WORD_BATCH = 8000
LANGS = ("ru", "en", "uk", "nl")


def log(msg: str) -> None:
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def call(op: str, **body) -> dict:
    import keyring

    secret = keyring.get_password("aiwb/co-push", "default")
    if not secret:
        raise SystemExit("error: no key in keyring aiwb/co-push (account default)")
    raw = json.dumps({"op": op, **body}, ensure_ascii=False).encode("utf-8")
    data = json.dumps({"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}).encode("ascii")
    req = urllib.request.Request(API, data=data, method="POST", headers={
        "Content-Type": "application/json", "X-CO-Push-Key": secret, "User-Agent": UA})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:300]
            if e.code < 500 or attempt == 2:
                raise SystemExit(f"error: {op}: HTTP {e.code} {detail}")
        except urllib.error.URLError as e:
            if attempt == 2:
                raise SystemExit(f"error: {op}: {e}")
        time.sleep(3 * (attempt + 1))
    return {}


# ---- model (model2vec mean pooling reproduced with numpy + tokenizers)

class Model:
    def __init__(self):
        import numpy as np
        from tokenizers import Tokenizer

        self.np = np
        MODEL_DIR.mkdir(parents=True, exist_ok=True)
        for f in ("model.safetensors", "tokenizer.json"):
            p = MODEL_DIR / f
            if not p.exists():
                log(f"downloading {f} from Hugging Face")
                tmp = p.with_suffix(".part")
                urllib.request.urlretrieve(HF + f, tmp)
                tmp.replace(p)
        with open(MODEL_DIR / "model.safetensors", "rb") as fh:
            n = struct.unpack("<Q", fh.read(8))[0]
            head = json.loads(fh.read(n))
            meta = next(v for k, v in head.items() if k != "__metadata__")
            a, b = meta["data_offsets"]
            fh.seek(8 + n + a)
            dtype = {"F32": np.float32, "F16": np.float16}[meta["dtype"]]
            self.E = np.frombuffer(fh.read(b - a), dtype=dtype).reshape(meta["shape"]).astype(np.float32)
        self.tok = Tokenizer.from_file(str(MODEL_DIR / "tokenizer.json"))
        self.unk = self.tok.token_to_id("<unk>")

    def ids(self, texts):
        return [[i for i in e.ids if i != self.unk] for e in self.tok.encode_batch(list(texts), add_special_tokens=False)]

    def encode(self, texts):
        np = self.np
        out = np.zeros((len(texts), DIM), np.float32)
        for k, ids in enumerate(self.ids(texts)):
            if ids:
                v = self.E[ids].mean(axis=0)
                out[k] = v / (np.linalg.norm(v) or 1.0)
        return out

    def word_sums(self, forms):
        np = self.np
        S = np.zeros((len(forms), DIM), np.float32)
        N = np.zeros(len(forms), np.int32)
        for k, ids in enumerate(self.ids(forms)):
            N[k] = len(ids)
            if ids:
                S[k] = self.E[ids].sum(axis=0)
        return S, N


def q8(np, v, unit: bool):
    """int8 bytes + scale. unit: v is a unit vector, scale = 1/|q| (cosine = dot * scale); else v ~= q * scale."""
    m = float(np.abs(v).max())
    if m == 0.0:
        return bytes(DIM), 0.0
    q = np.round(v / m * 127).astype(np.int8)
    scale = 1.0 / float(np.linalg.norm(q.astype(np.float32))) if unit else m / 127
    return q.tobytes(), scale


def b64(b: bytes) -> str:
    return base64.b64encode(b).decode("ascii")


# ---- index

def load_json(p: Path, default):
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def save_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(".tmp")
    tmp.write_text(json.dumps(obj, ensure_ascii=False), encoding="utf-8")
    tmp.replace(p)


def count_words(counts: dict, text: str, proto: set | None = None) -> None:
    for w in WORD.findall(text or ""):
        if not 2 <= len(w) <= 60 or w.isdigit():
            continue
        lw = w.lower()
        if lw != lw.lower():
            continue
        e = counts.setdefault(lw, [0, {}])
        e[0] += 1
        if len(e[1]) < 4 or w in e[1]:
            e[1][w] = e[1].get(w, 0) + 1
        if proto is not None:
            proto.add(lw)


def cmd_index(a) -> int:
    t0 = time.time()
    state = load_json(STATE / "state.json", {})
    counts = load_json(STATE / "counts.json", {})
    model = Model()
    np = model.np
    proto_words: set = set(state.get("proto_words_pending", []))

    scan = state.get("vocab_scan_utc")
    full = a.full_vocab or not scan or (dt.datetime.now(dt.UTC) - dt.datetime.fromisoformat(scan)).days >= VOCAB_DAYS
    if full:
        log("vocabulary: full scan of message texts")
        counts, after, n = {}, "", 0
        while True:
            rows = call("vec_texts", after=after, limit=2000)["rows"]
            if not rows:
                break
            for eid, text in rows:
                count_words(counts, text)
            after, n = rows[-1][0], n + len(rows)
        log(f"vocabulary: {n} messages, {len(counts)} distinct words")
        state["vocab_scan_utc"] = dt.datetime.now(dt.UTC).isoformat()

    # protocol vectors
    done = 0
    while True:
        todo = call("vec_todo", limit=2000)
        rows = todo["rows"]
        if todo.get("removed"):
            log(f"protocol: {todo['removed']} stale vectors removed")
        if not rows:
            break
        V = model.encode([r["text"] for r in rows])
        put = []
        for r, v in zip(rows, V):
            qb, sc = q8(np, v, True)
            put.append({"id": r["id"], "chatId": r["chatId"], "sha": hashlib.sha1(r["text"].encode("utf-8")).hexdigest(), "scale": sc, "v": b64(qb)})
            count_words(counts, r["text"], proto_words)
        call("vec_put", protocol=put)
        done += len(put)
        log(f"protocol: {done} vectors, {todo['left'] - len(rows)} left")
        if a.limit and done >= a.limit:
            break

    # words the server does not have yet
    have, after = set(), ""
    while True:
        ws = call("vec_words", after=after, limit=50000)["words"]
        if not ws:
            break
        have.update(w for w, _ in ws)
        after = ws[-1][0]
    new = [w for w, (f, _) in counts.items() if w not in have and (f >= MIN_FREQ or w in proto_words)]
    log(f"words: {len(have)} on the server, {len(new)} to add")
    for i in range(0, len(new), WORD_BATCH):
        part = new[i:i + WORD_BATCH]
        forms = [max(counts[w][1].items(), key=lambda kv: kv[1])[0] if counts[w][1] else w for w in part]
        S, N = model.word_sums(forms)
        rows = []
        for w, s, n in zip(part, S, N):
            if n == 0:
                continue
            qb, sc = q8(np, s, False)
            rows.append({"w": w, "n": int(n), "scale": sc, "v": b64(qb), "freq": counts[w][0]})
        r = call("vec_put", words=rows)
        log(f"words: {min(i + WORD_BATCH, len(new))}/{len(new)} (skipped {r.get('skipped', 0)})")
    state["proto_words_pending"] = []
    state["last_run_utc"] = dt.datetime.now(dt.UTC).isoformat()
    save_json(STATE / "counts.json", counts)
    save_json(STATE / "state.json", state)
    log(f"done in {time.time() - t0:.0f}s")
    return 0


def cmd_vec(a) -> int:
    model = Model()
    np = model.np
    rows, after = [], 0
    while True:
        part = call("protocol_texts", env=a.env, after=after, limit=5000)["rows"]
        if not part:
            break
        rows += part
        after = part[-1][0]
    V = model.encode([r[2] for r in rows])
    q = model.encode([a.text])[0]
    sim = V @ q
    for j in np.argsort(-sim)[: a.limit]:
        print(f"{sim[j]:.3f}  {rows[j][1]}  {rows[j][2][:150]}")
    return 0


# ---- aliases

PROMPT = """Ты строишь словарь псевдонимов для поиска по истории чатов пользователя (личная система AI Workbench;
языки чатов русский и английский, иногда украинский и нидерландский). Ниже термины из протоколов его чатов, у каждого
частота и пример строки.

Для каждого термина, у которого есть другие имена, верни группу:
- canonical: основное имя;
- aliases: как ещё этот предмет могут назвать в поисковом запросе: другое написание и транслитерация (капча / captcha /
  каптча), перевод на русский или английский (резервная копия / backup / бэкап), сокращение или полное название,
  разговорный вариант, конкретное описательное название из 2-3 слов (ONLYOFFICE -> "офисный редактор", "редактор
  документов"; Cloudflare Turnstile -> "проверка на робота"). Для людей: варианты имени на русском и латиницей,
  уменьшительные, имя без фамилии, если оно однозначно;
- wikidata: английское название для поиска в Wikidata, если это известная публичная вещь (продукт, компания,
  технология, общее понятие), иначе null. Для людей из личной жизни и внутренних названий системы - null.

Правила: одна группа = один предмет или один человек (не объединяй двух людей, людей и их дела, предмет и задачу
про него); не давай общих одиночных слов (система, файл, ошибка, модель), не связывай разные вещи, не выдумывай.
Внутренние названия системы (CO, CO Web, Bridge, резидент, секретарь...) объясняй по примеру строки. Пропусти термины
без полезных псевдонимов. Только JSON: {"groups": [{"term": "...", "canonical": "...", "aliases": ["..."], "wikidata": null}]}

Термины:
"""

CONFIRM = """Для каждого термина из личной системы пользователя выбери подходящий объект Wikidata из кандидатов
(тот же самый предмет, не однофамилец, не созвездие, не город) или null, если подходящего нет.
Только JSON: {"picks": {"<n>": "Q..." или null}}

"""


def ask_model(prompt: str, model: str) -> dict:
    exe = os.environ.get("AIWB_CLAUDE") or shutil.which("claude") or "claude"
    r = subprocess.run([exe, "-p", "--model", model, "--tools", "", "--no-session-persistence", "--strict-mcp-config",
                        "--setting-sources", "", "--output-format", "json"],
                       input=prompt, capture_output=True, text=True, encoding="utf-8", timeout=1200)
    try:
        t = json.loads(r.stdout).get("result") or ""
        return json.loads(t[t.find("{"):t.rfind("}") + 1])
    except ValueError:
        log(f"model answer not JSON: {(r.stdout or r.stderr)[:200]}")
        return {}


def norm(t: str) -> str:
    return " ".join(WORD.findall((t or "").lower()))[:120]


EN_STOP = set("""the and for not with from that this was were are is be been has have had will would can could should
may might must shall into onto over under after before about above below than then them they their there these those what
when where which while who whom why how all any both each few more most other some such only own same so too very just
also yes you your our his her its out off one two three new old now here via per yet nor but if or as at by in of on to up
it we he she do does did done get got set use used using make made need needs run runs check test tests fix fixed fixes
step note list file files data text line value values true false null none ok live plan next update ref unit status
answer decision fact question constraint artifact open closed todo done""".split())


RU_END = re.compile(r"(ами|ями|ого|его|ому|ему|ыми|ими|ов|ев|ей|ой|ий|ый|ая|яя|ое|ее|ые|ие|ам|ям|ах|ях|ом|ем|ую|юю|а|я|ы|и|у|ю|е|о|ь)$")


def candidates(env: str, top: int) -> list[tuple[str, int, str]]:
    """Terms worth aliasing: Latin or capitalised words and capitalised pairs used in 3+ chats (3/4 of the list),
    long Cyrillic words used in 5+ chats (1/4), people of the dossier."""
    rows, after = [], 0
    while True:
        part = call("protocol_texts", env=env, after=after, limit=5000)["rows"]
        if not part:
            break
        rows += part
        after = part[-1][0]
    log(f"aliases: {len(rows)} protocol entries of {env}")
    df, ex, form = collections.defaultdict(set), {}, collections.defaultdict(collections.Counter)
    cap = lambda w: w[0].isupper() or any(ch.isdigit() for ch in w)
    for _, chat, text in rows:
        toks = WORD.findall(text)
        for i, w in enumerate(toks):
            lw = w.lower()
            if len(w) < 3 or w.isdigit() or lw in EN_STOP:
                continue
            latin = bool(re.search(r"[A-Za-z]", w))
            keys = []
            if latin or (cap(w) and i > 0):
                keys.append(("s", lw, w))
            elif len(w) >= 6 and re.fullmatch(r"[а-яё]+", w):
                keys.append(("c", RU_END.sub("", lw), w))  # word forms of one stem count together
            if i + 1 < len(toks) and cap(w) and len(toks[i + 1]) >= 2 and cap(toks[i + 1]) and toks[i + 1].lower() not in EN_STOP:
                keys.append(("s", f"{lw} {toks[i + 1].lower()}", f"{w} {toks[i + 1]}"))
            for kind, k, f in keys:
                df[(kind, k)].add(chat)
                form[(kind, k)][f] += 1
                ex.setdefault((kind, k), text[:110])
    pick = lambda kind, mindf, n: [k for d, k in sorted(((len(c), k) for k, c in df.items() if k[0] == kind and len(c) >= mindf), reverse=True)[:n]]
    keys = pick("s", 3, top * 3 // 4) + pick("c", 5, top - top * 3 // 4)
    out = [(form[k].most_common(1)[0][0], len(df[k]), ex[k]) for k in keys]
    try:
        people = call("dossier_host", env=env, action="list", section="people").get("rows", [])
    except SystemExit:
        people = []
    seen = {norm(t) for t, _, _ in out}
    for p in people:
        s = (p.get("subject") or "").strip()
        if s and norm(s) not in seen:
            seen.add(norm(s))
            out.append((s, 0, "person: " + (p.get("text") or "")[:90]))
    return out


def wikidata(params: dict) -> dict:
    url = WIKIDATA + "?" + urllib.parse.urlencode({**params, "format": "json"})
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except (urllib.error.URLError, TimeoutError):
            time.sleep(2 * (attempt + 1))
    return {}


def chat_docs(envs) -> dict:
    """Normalised protocol text per (env, chat) of the given envs, padded with spaces for ' term ' lookups."""
    docs = collections.defaultdict(list)
    for env in sorted(set(envs)):
        after = 0
        while True:
            part = call("protocol_texts", env=env, after=after, limit=5000)["rows"]
            if not part:
                break
            for _, chat, text in part:
                docs[(env, chat)].append(norm(text))
            after = part[-1][0]
    return {k: " " + " ".join(v) + " " for k, v in docs.items()}


SMALL_ENV = 100  # chats with a protocol


def frequent_filter(groups: list, docs: dict, share: float, floor: int) -> tuple[list, dict]:
    """Drop from model/Wikidata groups the names found in the protocols of more than max(floor, share * chats) chats of
    the group's env, or of all envs together for a general group and an env under SMALL_ENV chats (13 chats alone say
    little: "check", "bridge" are common words), and
    names shorter than 3 characters: such a name (Google Drive, бэкап, json) added to a query pulls in chats that only
    mention it. Groups by hand (source user) stay. Returns (groups, {term: chats}) of dropped terms."""
    per_env = collections.Counter(e for e, _ in docs)
    lim_all = max(floor, share * len(docs))
    stop = set(load_json(STATE / "aliases-drop.json", []))
    df_cache, dropped, out = {}, {}, []

    def df(t, env):
        if (env, t) not in df_cache:
            pat = " " + t + " "
            df_cache[(env, t)] = sum(1 for (e, _), d in docs.items() if (not env or e == env) and pat in d)
        return df_cache[(env, t)]

    for g in groups:
        if g.get("source") == "user":
            out.append(g)
            continue
        keep = []
        for t in g["terms"]:
            n_all = df(t, "")
            mine = per_env.get(g["env"], 0)
            # an env with SMALL_ENV+ chats judges its own names alone (a family member's name is frequent overall, rare and useful in the owner's env)
            common = n_all > lim_all if (not g["env"] or mine < SMALL_ENV) else df(t, g["env"]) > max(floor, share * mine)
            if len(t) < 3 or common or t in stop:
                dropped[t] = n_all
            else:
                keep.append(t)
        out.append({**g, "terms": keep})
    return out, dropped


def alias_envs(extra: str = "") -> set:
    """Envs that have alias groups, plus extra."""
    envs = {r["env"] for r in call("alias_list")["aliases"] if r["env"]}
    return envs | ({extra} if extra else set())


def cmd_aliases_build(a) -> int:
    cache = load_json(STATE / f"aliases-{a.env}.json", {"model": {}, "wd_search": {}, "wd_pick": {}, "wd_terms": {}})
    terms = candidates(a.env, a.top)
    log(f"aliases: {len(terms)} candidate terms")
    todo = [t for t in terms if norm(t[0]) not in cache["model"]]
    batches = [todo[i:i + a.batch] for i in range(0, len(todo), a.batch)]

    def one(batch):
        lines = "\n".join(f"- {t} ({n}): {e}" for t, n, e in batch)
        return batch, ask_model(PROMPT + lines, a.model)

    with cf.ThreadPoolExecutor(a.parallel) as ex:
        for k, (batch, res) in enumerate(ex.map(one, batches)):
            got = {norm(g.get("term", "")): g for g in res.get("groups", []) if isinstance(g, dict)}
            for t, _, _ in batch:
                cache["model"][norm(t)] = got.get(norm(t))
            log(f"aliases: model batch {k + 1}/{len(batches)}, {len(got)} groups")
            save_json(STATE / f"aliases-{a.env}.json", cache)

    # one thing per group: "Анна и Мария", "помощь Марии" would tie different things together
    compound = re.compile(r"(\s(и|and|en|та|&)\s|/|\+)|^(помощь|поиск|проект|работа|дела)\s", re.I)
    groups = [g for g in cache["model"].values() if g and g.get("aliases") and not compound.search(g.get("canonical", ""))]
    # Wikidata: search, model picks the entity, labels and aliases in ru/en/uk/nl
    need = sorted({g["wikidata"] for g in groups if g.get("wikidata")} - set(cache["wd_search"]))
    for i, name in enumerate(need):
        r = wikidata({"action": "wbsearchentities", "search": name, "language": "en", "uselang": "en", "limit": 4, "type": "item"})
        cache["wd_search"][name] = [(s["id"], s.get("label", ""), s.get("description", "")) for s in r.get("search", [])]
        if i % 50 == 49:
            log(f"aliases: wikidata search {i + 1}/{len(need)}")
            save_json(STATE / f"aliases-{a.env}.json", cache)
    pick_todo = [g for g in groups if g.get("wikidata") and norm(g["canonical"]) not in cache["wd_pick"] and cache["wd_search"].get(g["wikidata"])]
    for i in range(0, len(pick_todo), 40):
        part = pick_todo[i:i + 40]
        text = CONFIRM + "\n".join(
            f"{n}. {g['canonical']} ({', '.join(g['aliases'][:3])}) - кандидаты: " +
            "; ".join(f"{q} {l}: {d}" for q, l, d in cache["wd_search"][g["wikidata"]]) for n, g in enumerate(part))
        picks = ask_model(text, a.model).get("picks", {})
        for n, g in enumerate(part):
            q = picks.get(str(n))
            cache["wd_pick"][norm(g["canonical"])] = q if isinstance(q, str) and re.fullmatch(r"Q\d+", q) else None
        log(f"aliases: wikidata picks {min(i + 40, len(pick_todo))}/{len(pick_todo)}")
        save_json(STATE / f"aliases-{a.env}.json", cache)
    qids = sorted({q for q in cache["wd_pick"].values() if q} - set(cache["wd_terms"]))
    for i in range(0, len(qids), 50):
        r = wikidata({"action": "wbgetentities", "ids": "|".join(qids[i:i + 50]), "props": "labels|aliases", "languages": "|".join(LANGS)})
        for q, e in r.get("entities", {}).items():
            ts = [x["value"] for x in e.get("labels", {}).values()]
            ts += [x["value"] for l in e.get("aliases", {}).values() for x in l]
            cache["wd_terms"][q] = sorted({t for t in ts if 2 <= len(t) <= 60 and len(t.split()) <= 4})
    save_json(STATE / f"aliases-{a.env}.json", cache)

    out = []
    for g in groups:
        names = [g.get("term", ""), g["canonical"], *g["aliases"]]
        own = sorted({norm(t) for t in names if len(norm(t)) >= 2})
        if len(own) >= 2:
            out.append({"env": a.env, "grp": ("own:" + norm(g["canonical"]))[:80], "source": "model", "terms": own})
        q = cache["wd_pick"].get(norm(g["canonical"]))
        if q and cache["wd_terms"].get(q):
            wd = sorted({norm(t) for t in cache["wd_terms"][q] + [g["canonical"]] if len(norm(t)) >= 2})
            out.append({"env": "", "grp": q, "source": "wikidata", "terms": wd})
    if a.max_share > 0:
        out, dropped = frequent_filter(out, chat_docs(alias_envs(a.env)), a.max_share, a.min_chats)
        small = [{"env": o["env"], "grp": o["grp"]} for o in out if len(o["terms"]) < 2]
        out = [o for o in out if len(o["terms"]) >= 2]
        if small and not a.dry_run:
            call("alias_delete", groups=small)  # alias_put leaves a stored group alone when fewer than 2 names come
        log(f"aliases: {len(dropped)} frequent terms dropped: {', '.join(sorted(dropped, key=lambda t: -dropped[t])[:15])}")
    log(f"aliases: {sum(1 for o in out if o['source'] == 'model')} own groups, {sum(1 for o in out if o['source'] == 'wikidata')} Wikidata groups")
    if a.dry_run:
        for o in out[: a.show]:
            print(json.dumps(o, ensure_ascii=False))
        return 0
    if a.replace:
        old = {(r["env"], r["grp"]) for r in call("alias_list", env=a.env)["aliases"] if r["source"] == "model" and r["env"] == a.env}
        gone = old - {(o["env"], o["grp"]) for o in out}
        if gone:
            call("alias_delete", groups=[{"env": e, "grp": g} for e, g in gone])
    n = 0
    for i in range(0, len(out), 1000):
        n += call("alias_put", groups=out[i:i + 1000])["inserted"]
    log(f"aliases: {n} terms written")
    return 0


def cmd_aliases_prune(a) -> int:
    """Apply the frequent-name rule to the stored groups of all envs (model and Wikidata; user groups stay)."""
    rows = call("alias_list")["aliases"]
    stored = collections.defaultdict(list)
    for r in rows:
        stored[(r["env"], r["grp"], r["source"])].append(r["term"])
    groups = [{"env": e, "grp": g, "source": s, "terms": ts} for (e, g, s), ts in stored.items()]
    pruned, dropped = frequent_filter(groups, chat_docs(alias_envs()), a.max_share, a.min_chats)
    changed = [p for p, g in zip(pruned, groups) if len(p["terms"]) != len(g["terms"])]
    gone = [{"env": p["env"], "grp": p["grp"]} for p in changed if len(p["terms"]) < 2]
    put = [p for p in changed if len(p["terms"]) >= 2]
    for t in sorted(dropped, key=lambda t: -dropped[t]):
        print(f"{dropped[t]:5} {t}")
    log(f"aliases prune: {len(dropped)} terms dropped, {len(put)} groups shortened, {len(gone)} groups removed")
    if a.dry_run:
        return 0
    if gone:
        call("alias_delete", groups=gone)
    for i in range(0, len(put), 1000):
        call("alias_put", groups=put[i:i + 1000])
    return 0


def cmd_aliases_drop(a) -> int:
    """Names never to use as aliases (kept in aliases-drop.json, every build and prune honours it), then prune."""
    stop = set(load_json(STATE / "aliases-drop.json", []))
    if a.undo:
        stop -= {norm(t) for t in a.terms}
    else:
        stop |= {norm(t) for t in a.terms if norm(t)}
    save_json(STATE / "aliases-drop.json", sorted(stop))
    log(f"aliases drop list: {len(stop)} names" + (" (rebuild the env to bring names back)" if a.undo else ""))
    if a.undo:
        return 0
    a.dry_run = False
    return cmd_aliases_prune(a)


def cmd_aliases_list(a) -> int:
    rows = call("alias_list", env=a.env)["aliases"]
    groups = collections.defaultdict(list)
    for r in rows:
        groups[(r["env"] or "*", r["grp"], r["source"])].append(r["term"])
    for (env, grp, src), ts in sorted(groups.items()):
        if a.grep and not any(a.grep.lower() in t for t in ts):
            continue
        print(f"{env:8} {src:8} {grp:40} {' | '.join(ts)}")
    print(f"{len(groups)} groups, {len(rows)} terms", file=sys.stderr)
    return 0


def cmd_aliases_add(a) -> int:
    grp = a.grp or ("user:" + norm(a.terms[0]))[:80]
    r = call("alias_put", groups=[{"env": a.env, "grp": grp, "source": "user", "terms": a.terms}])
    print(f"{grp}: {r['inserted']} terms")
    return 0


def cmd_aliases_rm(a) -> int:
    r = call("alias_delete", groups=[{"env": a.env if a.env != "*" else "", "grp": a.grp}])
    print(f"{r['deleted']} terms deleted")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("index", help="protocol vectors and the word table (hourly task)")
    p.add_argument("--full-vocab", action="store_true", help="rescan all message texts for the vocabulary now")
    p.add_argument("--limit", type=int, default=0, help="stop after N protocol vectors (test)")
    p.add_argument("--log", default="", help="append output to this file (scheduled task under pythonw)")
    p.set_defaults(fn=cmd_index)
    p = sub.add_parser("vec", help="nearest protocol entries of an env for a text (local, full model)")
    p.add_argument("text")
    p.add_argument("--env", default=DEFAULT_ENV)
    p.add_argument("--limit", type=int, default=10)
    p.set_defaults(fn=cmd_vec)
    p = sub.add_parser("aliases", help="alias dictionary")
    s2 = p.add_subparsers(dest="sub", required=True)
    b = s2.add_parser("build", help="candidates from the env's protocols -> model -> Wikidata -> table aliases")
    b.add_argument("--env", default=DEFAULT_ENV)
    b.add_argument("--top", type=int, default=900, help="candidate terms by number of chats")
    b.add_argument("--batch", type=int, default=90)
    b.add_argument("--parallel", type=int, default=3)
    b.add_argument("--model", default="sonnet")
    b.add_argument("--replace", action="store_true", help="delete own model groups of the env that this run did not produce")
    b.add_argument("--dry-run", action="store_true")
    b.add_argument("--show", type=int, default=40)
    b.set_defaults(fn=cmd_aliases_build)
    for b in (b, s2.add_parser("prune", help="drop frequent names from the stored groups (all envs)")):
        b.add_argument("--max-share", type=float, default=0.05, help="drop a name found in more than this share of the chats (0 = off for build)")
        b.add_argument("--min-chats", type=int, default=10, help="...but never below this number of chats")
        if b.prog.endswith("prune"):
            b.add_argument("--dry-run", action="store_true")
            b.set_defaults(fn=cmd_aliases_prune)
    b = s2.add_parser("drop", help="names never to use as aliases: aliases drop бэкап 'google drive' [--undo]")
    b.add_argument("terms", nargs="+")
    b.add_argument("--undo", action="store_true", help="take the names off the list (a rebuild brings them back)")
    b.add_argument("--max-share", type=float, default=0.05)
    b.add_argument("--min-chats", type=int, default=10)
    b.set_defaults(fn=cmd_aliases_drop)
    b = s2.add_parser("list")
    b.add_argument("--env", default=DEFAULT_ENV)
    b.add_argument("--grep", default="")
    b.set_defaults(fn=cmd_aliases_list)
    b = s2.add_parser("add", help="one group by hand: aliases add 'ONLYOFFICE' 'офисный редактор'")
    b.add_argument("terms", nargs="+")
    b.add_argument("--env", default=DEFAULT_ENV, help="env, or '' for everyone")
    b.add_argument("--grp", default="")
    b.set_defaults(fn=cmd_aliases_add)
    b = s2.add_parser("rm", help="delete a group")
    b.add_argument("grp")
    b.add_argument("--env", default=DEFAULT_ENV, help="env, or * for a general group")
    b.set_defaults(fn=cmd_aliases_rm)
    a = ap.parse_args(argv)
    if getattr(a, "log", ""):
        Path(a.log).parent.mkdir(parents=True, exist_ok=True)
        sys.stdout = sys.stderr = open(a.log, "a", encoding="utf-8", buffering=1)
    try:
        return a.fn(a)
    except SystemExit as e:
        if getattr(a, "log", "") and e.code not in (None, 0):
            print(e.code)
        raise


if __name__ == "__main__":
    sys.exit(main())
