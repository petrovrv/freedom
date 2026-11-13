#!/usr/bin/env python3
"""aiwb-modelbench: quality matrix of agents/models per task type (see SKILL.md)."""
import json
import os
import re
import sys
import glob
import shutil
import argparse
import tempfile
import threading
import time
from pathlib import Path
from datetime import datetime, timezone
from concurrent.futures import ThreadPoolExecutor

from runners import EngieClient, run_cli

sys.stdout.reconfigure(encoding="utf-8")

SCRIPT_DIR = Path(__file__).resolve().parent
LOCAL = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData" / "Local")))
BRIDGE_DIR = LOCAL / "AIWorkbenchBridge"
DEFAULT_TASKS = str(SCRIPT_DIR / "tasks.json")
TASKS_FILE = DEFAULT_TASKS
DEFAULT_OUT = r"G:\My Drive\AI-Workbench\lab\model-bench"
DEFAULT_BRIDGE_CONFIG = str(BRIDGE_DIR / "config.json")
DEFAULT_LEDGER = str(BRIDGE_DIR / "usage-ledger.jsonl")
DEFAULT_LIMITS = r"G:\My Drive\AI-Workbench\transport\hosts\home-main\limits.json"
JOBS_DIR = BRIDGE_DIR / "jobs"
RESULTS_FILE = "results.jsonl"
RECO_FILE = "recommendations.json"
REPORT_FILE = "report.md"
CLI_AGENTS = ("claude", "codex")
LIMIT_AGENTS = ("claude", "codex", "dispatcher")
CLI_TIMEOUT = 900
ENGIE_TIMEOUT = 150


def utc_iso():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def split_ids(v):
    # Accept None, "a,b" or ["a", "b,c"]
    if not v:
        return None
    items = [v] if isinstance(v, str) else v
    return {x.strip() for s in items for x in str(s).split(",") if x.strip()}


def load_tasks(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    data.setdefault("targets", [])
    data.setdefault("tasks", [])
    return data


def latest_results(results_path):
    latest = {}
    p = Path(results_path)
    if not p.exists():
        return latest
    with open(p, encoding="utf-8") as f:
        for line in f:
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("type") == "result":
                latest[(rec.get("target"), rec.get("task"))] = rec
    return latest


def read_limits(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError):
        return {}


def limits_snapshot(limits):
    agents = limits.get("agents", {}) or {}
    return {a: {w.get("id"): w.get("usedPercent") for w in (agents.get(a, {}) or {}).get("windows", []) or []}
            for a in LIMIT_AGENTS}


def limit_block(limits, agent, min_left):
    for w in ((limits.get("agents", {}) or {}).get(agent, {}) or {}).get("windows", []) or []:
        used = w.get("usedPercent")
        if used is not None and float(used) > 100 - float(min_left):
            return f"limit: {w.get('id')} used {used}%"
    return None


def resolve_mcp_config(args):
    if getattr(args, "mcp_config", None):
        return args.mcp_config
    if getattr(args, "job", None):
        hits = sorted(glob.glob(str(JOBS_DIR / f"{args.job}-*" / "mcp-devices.json")))
        return hits[0] if hits else None
    return None


def apply_check(task, answer):
    chk = task.get("check") or {}
    fails = [f"missing: {p}" for p in chk.get("regex", []) if not re.search(p, answer or "", re.I | re.S)]
    fails += [f"forbidden: {p}" for p in chk.get("not_regex", []) if re.search(p, answer or "", re.I | re.S)]
    return not fails, fails


def build_pairs(cfg, args):
    t_ids, k_ids, cats = split_ids(args.targets), split_ids(args.tasks), split_ids(args.categories)
    corp = corporate_on(getattr(args, "corporate", "auto"))
    pairs = []
    for t in cfg["targets"]:
        if t_ids and t["id"] not in t_ids:
            continue
        if t.get("disabled"):  # target known to be unavailable (reason in tasks.json)
            continue
        if not corp and is_corporate(t) and not (t_ids and t["id"] in t_ids):  # 0.2.2: module corporate off
            continue
        for k in cfg["tasks"]:
            cat = k.get("category")
            if k_ids and k["id"] not in k_ids or cats and cat not in cats:
                continue
            if k.get("for") == "agents" and t["agent"] not in CLI_AGENTS:
                continue
            if t.get("categories") and cat not in t["categories"]:
                continue
            if t["agent"] == "engie" and not t.get("web", False) and cat == "web":
                continue
            pairs.append((t, k))
    return pairs


def cmd_list(args):
    cfg = load_tasks(args.tasks_file)
    print(f"Targets ({len(cfg['targets'])}):")
    for t in cfg["targets"]:
        cats = ",".join(t.get("categories", [])) or "*"
        print(f"  {t['id']:30} {t['agent']:7} {t.get('tier', ''):6} web={t.get('web', False)!s:5} "
              f"cats={cats}  {t.get('label', '')}")
    print(f"Tasks ({len(cfg['tasks'])}):")
    for k in cfg["tasks"]:
        print(f"  {k['id']:30} {k.get('category', ''):10} {k.get('for', 'all'):6} "
              f"{k.get('mode', 'chat'):6} {k.get('title', '')}")
    return 0


def cmd_run(args):
    cfg = load_tasks(args.tasks_file)
    out = Path(args.out)
    results_path = out / RESULTS_FILE
    pairs = build_pairs(cfg, args)
    if not args.rerun:
        done = latest_results(results_path)
        pairs = [(t, k) for t, k in pairs if done.get((t["id"], k["id"]), {}).get("status") != "ok"]
    if args.dry_run:
        for t, k in pairs:
            print(f"{t['id']:30} {k['id']:30} {k.get('category', '')}")
        print(f"{len(pairs)} pair(s)")
        return 0
    if not pairs:
        print("Nothing to run.")
        return 0

    out.mkdir(parents=True, exist_ok=True)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S")
    lock = threading.Lock()

    def write(rec):
        with lock, open(results_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()

    def log(msg):
        with lock:
            print(msg, flush=True)

    def fail(status, err):
        return {"status": status, "answer": "", "seconds": 0, "tokens_in": None,
                "tokens_out": None, "cost_usd": None, "error": err}

    def record(t, k, res):
        ok = res.get("status") == "ok"
        check_ok, check_fail = apply_check(k, res.get("answer")) if ok else (None, [])
        write({"type": "result", "run": run_id, "utc": utc_iso(), "target": t["id"], "agent": t["agent"],
               "model": t.get("model"), "tier": t.get("tier"), "task": k["id"], "category": k.get("category"),
               "status": res.get("status"), "error": res.get("error"), "seconds": res.get("seconds"),
               "tokens_in": res.get("tokens_in"), "tokens_out": res.get("tokens_out"),
               "cost_usd": res.get("cost_usd"), "answer": res.get("answer"), "check_ok": check_ok,
               "check_fail": check_fail, "score": None, "judge_reason": None})
        chk = "-" if check_ok is None else ("pass" if check_ok else "FAIL " + "; ".join(check_fail))
        err = f" ({res.get('error')})" if res.get("error") and not ok else ""
        log(f"{t['id']:30} {k['id']:28} {res.get('status', '?'):8} "
            f"{float(res.get('seconds') or 0):7.1f}s {chk}{err}")

    write({"type": "run", "run": run_id, "utc": utc_iso(), "limits_start": limits_snapshot(read_limits(args.limits))})

    engie = [p for p in pairs if p[0]["agent"] == "engie"]
    groups = {a: [p for p in pairs if p[0]["agent"] == a] for a in CLI_AGENTS}
    mcp = resolve_mcp_config(args) if engie else None

    def run_engie():
        if not mcp:
            for t, k in engie:
                log(f"{t['id']:30} {k['id']:28} skipped  (no device MCP config)")
            return
        client = EngieClient(mcp, args.ledger)
        try:
            for t, k in engie:
                try:
                    res = client.ask(t["model"], k["prompt"], web_search=k.get("category") == "web",
                                     timeout=ENGIE_TIMEOUT)
                except Exception as e:
                    res = fail("error", f"{type(e).__name__}: {e}")
                record(t, k, res)
        finally:
            client.close()

    def run_cli_pair(t, k):
        block = limit_block(read_limits(args.limits), t["agent"], args.min_left)
        if block:
            return record(t, k, fail("skipped", block))
        wd = tempfile.mkdtemp(prefix="mb-")
        try:
            res = run_cli(t["agent"], t["model"], k["prompt"], k.get("mode", "chat"), args.bridge_config, wd,
                          timeout=CLI_TIMEOUT, web=k.get("category") == "web")
        except Exception as e:
            res = fail("error", f"{type(e).__name__}: {e}")
        finally:
            shutil.rmtree(wd, ignore_errors=True)
        record(t, k, res)

    def run_pool(group):
        if group:
            with ThreadPoolExecutor(max_workers=max(1, args.parallel_cli)) as ex:
                list(ex.map(lambda p: run_cli_pair(*p), group))

    threads = [threading.Thread(target=run_engie)] if engie else []
    threads += [threading.Thread(target=run_pool, args=(g,)) for g in groups.values() if g]
    for th in threads:
        th.start()
    for th in threads:
        th.join()

    write({"type": "run_end", "run": run_id, "utc": utc_iso(), "limits_end": limits_snapshot(read_limits(args.limits))})
    log(f"Run {run_id} done: {len(pairs)} pair(s) -> {results_path}")
    return 0

def _utc_now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def _append_line(path, rec):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(rec, ensure_ascii=False) + "\n")


def _run_check(task, answer):
    # Deterministic regex check; returns (ok, message)
    chk = task.get("check") or {}
    flags = re.I | re.M | re.S
    for pat in chk.get("regex", []) or []:
        try:
            if not re.search(pat, answer, flags):
                return False, f"missing /{pat}/"
        except re.error as e:
            return False, f"bad regex /{pat}/: {e}"
    for pat in chk.get("not_regex", []) or []:
        try:
            if re.search(pat, answer, flags):
                return False, f"forbidden /{pat}/"
        except re.error as e:
            return False, f"bad regex /{pat}/: {e}"
    return True, ""


def _first_json_block(text):
    # Return first balanced {...} block, string-aware
    if not text:
        return None
    start = text.find("{")
    if start < 0:
        return None
    depth, in_str, esc = 0, False, False
    for i in range(start, len(text)):
        c = text[i]
        if in_str:
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                in_str = False
            continue
        if c == '"':
            in_str = True
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    return None


def _parse_scores(text, n):
    blk = _first_json_block(text)
    if not blk:
        return None
    try:
        obj = json.loads(blk)
    except json.JSONDecodeError:
        return None
    if not isinstance(obj, dict):
        return None
    out = {}
    for i in range(1, n + 1):
        v = obj.get(f"A{i}")
        if not isinstance(v, dict) or "score" not in v:
            return None
        try:
            s = int(round(float(v["score"])))
        except (TypeError, ValueError):
            return None
        out[i] = (max(0, min(10, s)), str(v.get("reason", "")).strip()[:300])
    return out


def _judge_prompt(task, batch):
    j = task.get("judge") or {}
    parts = [
        "You are a strict, impartial evaluator. Judge each answer independently against the rubric; "
        "do not compare answers with each other.",
        "",
        "## Task prompt",
        task.get("prompt", ""),
        "",
        "## Rubric",
        j.get("rubric") or "(none)",
        "",
        "## Reference answer",
        j.get("reference") or "(none)",
        "",
        "## Answers",
    ]
    for i, r in enumerate(batch, 1):
        ans = r.get("answer") or ""
        if len(ans) > 3500:
            ans = ans[:3500] + "\n[truncated]"
        parts += [f"### A{i}", ans, ""]
    keys = ",".join(f'"A{i}":{{"score":<0-10 integer>,"reason":"<max 20 words>"}}' for i in range(1, len(batch) + 1))
    parts += [
        "Return ONLY a JSON object, no prose, no code fences, exactly in this shape:",
        "{" + keys + "}",
    ]
    return "\n".join(parts)


def cmd_judge(args):
    data = load_tasks(args.tasks_file)
    tasks = {t["id"]: t for t in data.get("tasks", [])}
    rpath = os.path.join(args.out, "results.jsonl")
    if not os.path.exists(rpath):
        print(f"No results file: {rpath}", file=sys.stderr)
        return 2
    latest = latest_results(rpath)
    todo = [r for r in latest.values() if args.rejudge or r.get("score") is None]
    if not todo:
        print("Nothing to judge.")
        return 0

    pending = {}
    n_rule = 0
    for r in todo:
        task = tasks.get(r.get("task"))
        score = reason = None
        if r.get("status") == "skipped":
            continue  # not run (limit guard): no score
        if r.get("status") != "ok":
            score, reason = 0, f"status {r.get('status')}"
        elif task is None:
            print(f"skip {r.get('target')}/{r.get('task')}: unknown task", file=sys.stderr)
            continue
        else:
            ok, msg = _run_check(task, r.get("answer") or "")
            if not ok:
                score, reason = 0, f"check: {msg}"
            elif not task.get("judge"):
                score, reason = 10, "check passed"
            else:
                pending.setdefault(task["id"], []).append(r)
                continue
        rec = dict(r)
        rec.update(score=score, judge_reason=reason, judge_model="rule", judgedUtc=_utc_now())
        _append_line(rpath, rec)
        n_rule += 1
    print(f"rule-scored: {n_rule}")

    if not pending:
        return 0
    cfg = resolve_mcp_config(args)
    if not cfg:
        print("No MCP config for judge (use --mcp-config or --job).", file=sys.stderr)
        return 2

    from runners import EngieClient
    client = EngieClient(cfg, args.ledger)
    n_llm = n_fail = 0
    try:
        for tid, rs in pending.items():
            task = tasks[tid]
            for b in range(0, len(rs), 5):
                batch = rs[b:b + 5]
                prompt = _judge_prompt(task, batch)
                parsed = None
                for attempt in range(2):
                    res = client.ask(args.judge_model, prompt, timeout=150)
                    if res.get("status") == "ok":
                        parsed = _parse_scores(res.get("answer") or "", len(batch))
                    if parsed:
                        break
                    print(f"judge {tid} batch {b // 5 + 1}: attempt {attempt + 1} failed "
                          f"({res.get('status')}: {res.get('error') or 'unparseable'})", file=sys.stderr)
                if not parsed:
                    n_fail += len(batch)
                    continue
                for i, r in enumerate(batch, 1):
                    s, why = parsed[i]
                    rec = dict(r)
                    rec.update(score=s, judge_reason=why, judge_model=args.judge_model, judgedUtc=_utc_now())
                    _append_line(rpath, rec)
                    n_llm += 1
                    print(f"  {r.get('target')} / {tid}: {s} - {why}")
    finally:
        client.close()
    print(f"llm-judged: {n_llm}, failed: {n_fail}")
    return 0


TIER_COST = {"engie": 0, "light": 1, "heavy": 2}
FILE_CATEGORIES = ("code", "docx", "xlsx")


def _mean(xs):
    return sum(xs) / len(xs) if xs else None


def _md(s):
    return str(s).replace("|", "\\|")


def corporate_on(mode="auto"):
    """0.2.2: are corporate models (agent engie, Dispatcher) available here? on/off, or auto: the Freedom module
    corporate in tool-modules.json (toolbox/modstate.py), else install.json connectors.corporate, else off."""
    if mode in ("on", "off"):
        return mode == "on"
    try:
        sys.path.insert(0, str(SCRIPT_DIR.parent / "toolbox"))
        import modstate
        m = modstate.load()["modules"].get("corporate")
        if isinstance(m, dict):
            return bool(m.get("enabled"))
    except Exception:  # noqa: BLE001 - no toolbox next to this tool: fall back to install.json
        pass
    path = os.environ.get("AIWB_INSTALL") or str(LOCAL / "AIWorkbench" / "install.json")
    try:
        with open(path, encoding="utf-8-sig") as f:
            return bool(((json.load(f) or {}).get("connectors") or {}).get("corporate"))
    except (OSError, ValueError, AttributeError):
        return False


def is_corporate(meta):
    return meta.get("tier") == "engie" or meta.get("agent") in ("engie", "dispatcher")


def neutral_label(label, corporate=False):
    """A corporate target's label names no employer: its '(...)' suffix becomes '(corporate)'."""
    label = str(label or "")
    return re.sub(r"\s*\([^)]*\)\s*$", "", label) + " (corporate)" if corporate else label


def cmd_report(args):
    data = load_tasks(args.tasks_file)
    tasks = {t["id"]: t for t in data.get("tasks", [])}
    tdefs = {t["id"]: t for t in data.get("targets", [])}
    rpath = os.path.join(args.out, "results.jsonl")
    if not os.path.exists(rpath):
        print(f"No results file: {rpath}", file=sys.stderr)
        return 2
    rows = []
    for r in latest_results(rpath).values():
        if tdefs.get(r.get("target"), {}).get("disabled") or r.get("status") == "skipped":
            continue
        if r.get("score") is None and getattr(args, "provisional", False) and r.get("task") in tasks:
            # Not judged yet: score by the regex check alone (10 pass, 0 fail or error)
            ok = r.get("status") == "ok" and _run_check(tasks[r["task"]], r.get("answer") or "")[0]
            r = dict(r, score=10 if ok else 0, judge_model="check-only")
        if r.get("score") is not None:
            rows.append(r)
    if not rows:
        print("No scored results; run 'judge' first.", file=sys.stderr)
        return 2

    # Category order: tasks file first, then extras
    cats = []
    for t in data.get("tasks", []):
        c = t.get("category") or "uncategorized"
        if c not in cats:
            cats.append(c)

    acc, meta, judges = {}, {}, set()
    for r in rows:
        tg = r.get("target")
        cat = (tasks.get(r.get("task")) or {}).get("category") or r.get("category") or "uncategorized"
        if cat not in cats:
            cats.append(cat)
        if r.get("judge_model") and r.get("judge_model") != "rule":
            judges.add(r["judge_model"])
        d = tdefs.get(tg, {})
        meta.setdefault(tg, {
            "agent": d.get("agent") or r.get("agent"),
            "model": d.get("model") or r.get("model"),
            "label": neutral_label(d.get("label") or tg, is_corporate({"tier": d.get("tier") or r.get("tier"),
                                                                      "agent": d.get("agent") or r.get("agent")})),
            "tier": d.get("tier") or "heavy",
        })
        for key in (cat, "_overall"):
            a = acc.setdefault(tg, {}).setdefault(key, {"s": [], "t": [], "tok": 0})
            a["s"].append(float(r["score"]))
            if isinstance(r.get("seconds"), (int, float)):
                a["t"].append(float(r["seconds"]))
            a["tok"] += int(r.get("tokens_out") or 0)

    matrix = {}
    for tg, per in acc.items():
        matrix[tg] = {}
        for cat, a in per.items():
            sec = _mean(a["t"])
            matrix[tg][cat] = {
                "score": round(_mean(a["s"]), 2),
                "seconds": round(sec, 1) if sec is not None else None,
                "tokens_out": a["tok"],
                "n": len(a["s"]),
            }

    # Target order: tasks file first, then extras
    order = [t for t in tdefs if t in matrix] + sorted(t for t in matrix if t not in tdefs)

    def pick(cands, cat):
        if not cands:
            return None, None, 0
        best = max(matrix[t][cat]["score"] for t in cands)
        pool = [t for t in cands if matrix[t][cat]["score"] >= best - 0.5]
        pool.sort(key=lambda t: (
            TIER_COST.get(meta[t]["tier"], 3),
            matrix[t][cat]["seconds"] if matrix[t][cat]["seconds"] is not None else 1e9,
            -matrix[t][cat]["score"],
        ))
        return pool[0], best, len(pool)

    def entry(tg, cat):
        m = matrix[tg][cat]
        return {"target": tg, "agent": meta[tg]["agent"], "model": meta[tg]["model"],
                "label": meta[tg]["label"], "score": m["score"], "seconds": m["seconds"]}

    def why(tg, cat, best, k):
        m = matrix[tg][cat]
        sec = f", {m['seconds']:.0f}s" if m["seconds"] is not None else ""
        base = f"{m['score']:.1f}{sec}, tier {meta[tg]['tier']}, n={m['n']}"
        return base + (f"; cheapest/fastest of {k} within 0.5 of best {best:.1f}" if k > 1 else "; top score")

    # 0.2.2: corporate targets are recommended (and reach the hint data) only while the module corporate is on
    corp = corporate_on(getattr(args, "corporate", "auto"))
    rec_order = [t for t in order if corp or not is_corporate(meta[t])]
    recs, rec_rows = {}, []
    for cat in cats:
        # Only targets that ran every task of the category (cowork file tasks exclude chat-only corporate models)
        full = max((matrix[t].get(cat, {}).get("n", 0) for t in rec_order), default=0)
        cands = [t for t in rec_order if full and matrix[t].get(cat, {}).get("n", 0) >= full]
        prim, best, k = pick(cands, cat)
        if not prim:
            continue
        fb, _, _ = pick([t for t in cands if meta[t]["agent"] != meta[prim]["agent"]], cat)
        recs[cat] = {"primary": entry(prim, cat), "fallback": entry(fb, cat) if fb else None}
        fb_txt = f"{meta[fb]['label']} ({matrix[fb][cat]['score']:.1f})" if fb else "-"
        rec_rows.append(f"| {_md(cat)} | {_md(meta[prim]['label'])} | {_md(fb_txt)} | {_md(why(prim, cat, best, k))} |")

    def cell(m):
        if not m:
            return "-"
        return f"{m['score']:.1f} · {m['seconds']:.0f}s" if m["seconds"] is not None else f"{m['score']:.1f}"

    now = _utc_now()
    judge_txt = ", ".join(sorted(judges)) or None
    md = [
        "# Model bench report",
        "",
        f"Generated: {now}  ",
        f"Judge: {judge_txt or 'rule-only'}  ",
        f"Scored results: {len(rows)}",
        f"Corporate models: {'recommended' if corp else 'not recommended (module corporate is off)'}",
        "",
        "## Score matrix (mean score · mean seconds)",
        "",
        "| Target | " + " | ".join(_md(c) for c in cats) + " | Overall |",
        "|---|" + "---|" * (len(cats) + 1),
    ]
    for tg in order:
        cells = [cell(matrix[tg].get(c)) for c in cats] + [cell(matrix[tg].get("_overall"))]
        md.append(f"| {_md(meta[tg]['label'])} | " + " | ".join(cells) + " |")
    md += ["", "## Recommendations", "", "| Category | Recommended | Fallback | Why |", "|---|---|---|---|"]
    md += rec_rows
    md.append("")
    text = "\n".join(md)

    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "report.md"), "w", encoding="utf-8") as f:
        f.write(text)
    out = {
        "generatedUtc": now,
        "judgeModel": judge_txt,
        "categories": recs,
        "matrix": {tg: {c: v for c, v in matrix[tg].items() if c != "_overall"} for tg in order},
    }
    with open(os.path.join(args.out, "recommendations.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    # CO Web hint data (taskHintData.ts for CO Web <= 0.102; hint-data.js for the module task-hints): category -> primary/fallback agent and model
    def _pick(e, cat):
        if not e:
            return None
        if e["agent"] == "engie" and cat in FILE_CATEGORIES:
            # Work on files needs tools: Dispatcher (Claude orchestration) writes with the same corporate model
            return {"agent": "dispatcher", "model": e["model"], "label": "Dispatcher + " + e["label"]}
        return {"agent": e["agent"], "model": e["model"], "label": e["label"]}
    hint = {"benchDate": now[:10], "categories": {c: {"primary": _pick(v["primary"], c), "fallback": _pick(v["fallback"], c)}
                                                   for c, v in recs.items()}}
    with open(os.path.join(args.out, "taskHintData.ts"), "w", encoding="utf-8") as f:
        f.write("// Generated by aiwb-modelbench report; do not edit by hand.\n"
                "export default " + json.dumps(hint, ensure_ascii=False, indent=2) + ";\n")
    # 0.2.0: the data of the Freedom module task-hints (CO Web 0.103+ has no hint of its own): ui/hint-data.js
    js = ("// SPDX-License-Identifier: AGPL-3.0-or-later\n// Generated by aiwb-modelbench report (hint-data.js); do not edit by hand.\n"
          "window.TASK_HINT_DATA = " + json.dumps(hint, ensure_ascii=False, indent=2) + ";\n")
    targets = [os.path.join(args.out, "hint-data.js")]
    if getattr(args, "module", None):
        targets.append(os.path.join(args.module, "ui", "hint-data.js"))
    for t in targets:
        with open(t, "w", encoding="utf-8", newline="\n") as f:
            f.write(js)
    print(text)
    return 0


def main(argv=None):
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--tasks-file", default=TASKS_FILE)
    common.add_argument("--out", default=DEFAULT_OUT)
    common.add_argument("--bridge-config", default=DEFAULT_BRIDGE_CONFIG)
    common.add_argument("--ledger", default=DEFAULT_LEDGER)
    common.add_argument("--limits", default=DEFAULT_LIMITS)

    p = argparse.ArgumentParser(prog="modelbench", description="Benchmark models/agents on task categories.")
    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("list", parents=[common], help="List targets and tasks")
    sp.set_defaults(func=cmd_list)

    sp = sub.add_parser("run", parents=[common], help="Run tasks against targets")
    sp.add_argument("--targets", help="Comma-separated target ids")
    sp.add_argument("--tasks", help="Comma-separated task ids")
    sp.add_argument("--categories", help="Comma-separated categories")
    sp.add_argument("--mcp-config")
    sp.add_argument("--job")
    sp.add_argument("--min-left", type=int, default=30)
    sp.add_argument("--parallel-cli", type=int, default=2)
    sp.add_argument("--rerun", action="store_true")
    sp.add_argument("--dry-run", action="store_true")
    sp.add_argument("--corporate", choices=("auto", "on", "off"), default="auto",
                    help="include corporate targets (auto: only while the Freedom module corporate is on)")
    sp.set_defaults(func=cmd_run)

    sp = sub.add_parser("judge", parents=[common], help="Score results")
    sp.add_argument("--judge-model", default="claude-opus-5.5")
    sp.add_argument("--mcp-config")
    sp.add_argument("--job")
    sp.add_argument("--rejudge", action="store_true")
    sp.set_defaults(func=cmd_judge)

    sp = sub.add_parser("report", parents=[common], help="Build report.md and recommendations.json")
    sp.add_argument("--provisional", action="store_true", help="score unjudged results by the regex check alone")
    sp.add_argument("--module", help="folder of the Freedom module task-hints: also write its ui/hint-data.js")
    sp.add_argument("--corporate", choices=("auto", "on", "off"), default="auto",
                    help="recommend corporate models (auto: the Freedom module corporate is on)")
    sp.set_defaults(func=cmd_report)

    args = p.parse_args(argv)
    try:
        rc = args.func(args)
    except KeyboardInterrupt:
        print("Interrupted.", file=sys.stderr)
        return 130
    return 0 if rc is None else int(rc)


if __name__ == "__main__":
    sys.exit(main())