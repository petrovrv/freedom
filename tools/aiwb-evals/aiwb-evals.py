#!/usr/bin/env python3
"""aiwb-evals.py - golden-task evaluation suite for the AI Workbench job queue.

Submits a suite of golden tasks (cases.json) to the AI Workbench job queue
(MariaDB on the hosting, reached over SSH via the local `hz` helper or install.json site.ssh), waits for
the job runner (Bridge) to finish them, checks each expectation and reports
PASS/FAIL. Intended as a release gate before shipping a new Bridge build.

Cases file: {"defaults": {...}, "cases": [{...}, ...]}. Each case merges the
built-in defaults, then "defaults", then its own keys. See `run --help`.

Exit codes: 0 all passed, 1 at least one failure, 2 usage/config error.
"""
import argparse
import hashlib
import json
import os
import random
import re
import string
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone

sys.stdout.reconfigure(encoding='utf-8')


def _install():
    """install.json of this install (Freedom) through install/aiwb_install.py of the source tree, or None."""
    for d in (os.environ.get("AIWB_SOURCE"), r"C:\AIWorkbench\source", os.path.expanduser("~/AIWorkbench/source")):
        if d and os.path.isfile(os.path.join(d, "install", "aiwb_install.py")):
            if os.path.join(d, "install") not in sys.path:
                sys.path.insert(0, os.path.join(d, "install"))
            import aiwb_install
            return aiwb_install
    return None


def _install_get(key):
    ai = _install()
    return ai.get(key) if ai else None


def _ssh():
    """SSH to the site: the owner's hz.py (AIWB_HZ_DIR, else ~/.ssh) where it exists, else install.json site.ssh."""
    d = os.environ.get("AIWB_HZ_DIR") or os.path.join(os.path.expanduser("~"), ".ssh")
    if d not in sys.path:
        sys.path.insert(0, d)
    try:
        import hz  # hz.connect() -> paramiko SSHClient
        return hz.connect()
    except ImportError:
        ai = _install()
        if ai is None:
            raise SystemExit("no hz.py and no install/aiwb_install.py (set AIWB_SOURCE to the source tree)")
        return ai.ssh_connect()


def _owner_env():
    """Environment of the owner: AIWB_OWNER_ENV, else install.json (role owner), else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    ai = _install()
    return ((ai.owner() or {}).get("env") if ai else None) or "owner"


MYSQL = ("mysql --defaults-extra-file=$HOME/co-agent/secrets/my.cnf "
         + (os.environ.get("AIWB_DB_NAME") or _install_get("site.db.name") or "co_aiwb") + " -N -B")
POLL_SECONDS = 4
BASE = {"host": "home-main", "env": _owner_env(), "project": "bridge-test", "agent": "claude",
        "mode": "chat", "model": None, "effort": None, "devices": [], "permissions": {},
        "prompt": "", "title": "", "timeout": 300}
UNESC = {"n": "\n", "t": "\t", "\\": "\\", "0": "\0", "r": "\r"}


def sql(conn, query, _tries=3):
    # Parallel SSH channels sometimes drop ("Channel closed"): retry on the same connection, then give up.
    for attempt in range(_tries):
        try:
            return _sql(conn, query)
        except RuntimeError:
            raise
        except Exception:
            if attempt == _tries - 1:
                raise
            time.sleep(2 + attempt * 3)


def _sql(conn, query):
    stdin, stdout, stderr = conn.exec_command(MYSQL, timeout=60)
    stdin.write(query)
    stdin.channel.shutdown_write()
    out = stdout.read().decode('utf-8', 'replace')
    err = stderr.read().decode('utf-8', 'replace').strip()
    if err:
        raise RuntimeError(err)
    return [l.split('\t') for l in out.splitlines() if l]


def q(s):
    if s is None:
        return "NULL"
    return "'" + str(s).replace("\\", "\\\\").replace("'", "''") + "'"


def unesc(s):
    """Undo mysql -B escaping (\\n, \\t, \\\\, \\0, \\r)."""
    out, i = [], 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            out.append(UNESC.get(s[i + 1], "\\" + s[i + 1]))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def new_job_id():
    ts = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    rnd = "".join(random.choices(string.ascii_uppercase + string.digits, k=4))
    return f"JOB-EVAL-{ts}-{rnd}"


def load_cases(path):
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        sys.exit(f"error: cannot read cases file {path}: {e}")
    defaults = data.get("defaults", {}) or {}
    cases = []
    for raw in data.get("cases", []):
        if "id" not in raw:
            sys.exit(f"error: case without 'id' in {path}")
        c = {**BASE, **defaults, **raw}
        c["expect"] = {"status": "completed", **(defaults.get("expect") or {}),
                       **(raw.get("expect") or {})}
        cases.append(c)
    return cases


def check(exp, status, answer, secs, get_events):
    # A single string is accepted wherever a list is expected.
    exp = {k: ([v] if isinstance(v, str) and k != "status" else v) for k, v in exp.items()}
    r = []
    want = exp.get("status", "completed")
    if want and status != want:
        r.append(f"status={status or '?'} (expected {want})")
    low = answer.lower()
    for s in exp.get("contains") or []:
        if s.lower() not in low:
            r.append(f"missing {s!r}")
    for s in exp.get("not_contains") or []:
        if s.lower() in low:
            r.append(f"unexpected {s!r}")
    for p in exp.get("regex") or []:
        try:
            if not re.search(p, answer, re.IGNORECASE | re.DOTALL):
                r.append(f"no match /{p}/")
        except re.error as e:
            r.append(f"bad regex /{p}/: {e}")
    if exp.get("tools_used") or exp.get("tools_not_used") or exp.get("tool_input_not_used"):
        ev = get_events()
        # an item of tools_used may be a list: any one of its substrings is enough (Write or PowerShell)
        for t in exp.get("tools_used") or []:
            alts = t if isinstance(t, list) else [t]
            if not any(a in ev for a in alts):
                r.append(f"tool {' | '.join(alts)!r} not used")
        for t in exp.get("tools_not_used") or []:
            if t in ev:
                r.append(f"tool {t!r} used")
        # only inside tool calls, not in the answer text (an answer may show the user a command)
        calls = tool_inputs(ev)
        for t in exp.get("tool_input_not_used") or []:
            if t in calls:
                r.append(f"tool call with {t!r}")
    mx = exp.get("max_seconds")
    if mx is not None and secs > float(mx):
        r.append(f"took {secs}s > {mx}s")
    return r


def tool_inputs(ev):
    """The tool_use blocks of the (unescaped) event journal: name and input of every tool call."""
    out = []
    for m in re.finditer(r'"type":"tool_use"', ev):
        chunk = ev[m.end():m.end() + 4000]
        end = min([i for i in (chunk.find('"caller"'), chunk.find('"type":"tool_result"'), chunk.find('"type":"text"')) if i >= 0] or [len(chunk)])
        out.append(chunk[:end])
    return "\n".join(out)


def fetch_events(conn, jid):
    rows = sql(conn, "SET SESSION group_concat_max_len=16777216; "
                     "SELECT IFNULL(GROUP_CONCAT(data SEPARATOR '\\n'),'') "
                     f"FROM job_events WHERE job_id={q(jid)};")
    # Agent stdout is JSON inside JSON: drop the escaping so '"name":"Write"' matches.
    return unesc(rows[0][0]).replace("\\", "") if rows else ""


def run_case(c, device):
    res = {"id": c["id"], "ok": False, "seconds": 0.0, "status": "", "reasons": [],
           "answer": "", "job_id": None}
    devices = list(c.get("devices") or [])
    if any("$DEVICE" in str(d) for d in devices):
        if not device:
            res["reasons"].append("no --device")
            return res
        devices = [str(d).replace("$DEVICE", device) for d in devices]
    conn = None
    try:
        conn = _ssh()
        jid = new_job_id()
        res["job_id"] = jid
        req = {"schemaVersion": 3, "protocolMinor": 2, "hostId": c["host"], "jobId": jid,
               "environmentId": c["env"], "projectId": c["project"],
               "chatId": "CHAT-EVAL-" + hashlib.sha1(c["id"].encode()).hexdigest()[:10], "branchId": "BR-root", "agent": c["agent"],
               "instruction": c["prompt"], "conversation": {"operation": "new", "sessionId": None},
               "attachments": [], "artifactPaths": [], "timeoutMinutes": 0, "mode": c["mode"],
               "model": c.get("model"), "effort": c.get("effort"), "output": {"autoExport": False},
               "devices": devices, "permissions": c.get("permissions") or {}, "contextFolders": []}
        rj = q(json.dumps(req, ensure_ascii=False))
        sql(conn, "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, "
                  "state, origin, queued_utc, request, sha, synced_utc) VALUES ("
                  f"{q(jid)}, {q(c['host'])}, {q(c['env'])}, {q(c['project'])}, "
                  f"{q(req['chatId'])}, {q(c['agent'])}, {q(c['mode'])}, {q(c.get('model'))}, "
                  f"'inbox', 'db', UTC_TIMESTAMP(3), {rj}, SHA2({rj}, 256), UTC_TIMESTAMP(3));")
        t0 = time.monotonic()
        timeout = float(c.get("timeout") or 300)
        status = answer = ""
        while True:
            rows = sql(conn, "SELECT state, IFNULL(status,''), "
                             "IFNULL(JSON_VALUE(result,'$.agentFinalResponse'),'') "
                             f"FROM jobs WHERE job_id={q(jid)};")
            if not rows:
                raise RuntimeError("job row disappeared")
            row = rows[0] + ["", "", ""]
            if row[1]:
                status, answer = unesc(row[1]), unesc(row[2])
                break
            if time.monotonic() - t0 >= timeout:
                sql(conn, "UPDATE jobs SET cancel_requested=1, "
                          f"state=IF(state='inbox','cancelled',state) WHERE job_id={q(jid)};")
                res["reasons"].append(f"timeout after {timeout:g}s (state={unesc(row[0])}, cancel requested)")
                status = "timeout"
                break
            time.sleep(POLL_SECONDS)
        res["seconds"] = round(time.monotonic() - t0, 1)
        res["status"], res["answer"] = status, answer
        if status != "timeout":
            cache = {}

            def get_events():
                if "ev" not in cache:
                    cache["ev"] = fetch_events(conn, jid)
                return cache["ev"]
            res["reasons"] += check(c["expect"], status, answer, res["seconds"], get_events)
    except Exception as e:  # noqa: BLE001 - any failure is a FAIL reason
        res["reasons"].append(f"error: {e}")
    finally:
        if conn:
            conn.close()
    res["ok"] = not res["reasons"]
    res["xfail"] = c.get("xfail")  # known issue: reported, not counted as a failure
    return res


def cmd_list(args):
    for c in load_cases(args.cases):
        print(f"{c['id']:<28} {c['agent']}/{c['mode']}/{c['host']:<14} {c.get('title', '')}")
    return 0


def cmd_run(args):
    cases = load_cases(args.cases)
    only = {s.strip() for s in args.only.split(",") if s.strip()} if args.only else None
    skip = {s.strip() for s in args.skip.split(",") if s.strip()} if args.skip else set()
    if only:
        unknown = only - {c["id"] for c in cases}
        if unknown:
            print(f"warning: unknown case id(s): {', '.join(sorted(unknown))}", file=sys.stderr)
    cases = [c for c in cases if (not only or c["id"] in only) and c["id"] not in skip]
    if not cases:
        print("error: no cases selected", file=sys.stderr)
        return 2
    if args.host:
        for c in cases:
            c["host"] = args.host
    results = {}
    with ThreadPoolExecutor(max_workers=max(1, args.parallel)) as ex:
        futs = {ex.submit(run_case, c, args.device): c["id"] for c in cases}
        for f in as_completed(futs):
            r = f.result()
            results[r["id"]] = r
            tag = ('XPASS' if r['ok'] else 'XFAIL') if r.get('xfail') else ('PASS' if r['ok'] else 'FAIL')
            line = f"{tag}  {r['id']}  {r['seconds']}s  {'; '.join(r['reasons'])}"
            if r.get("xfail"):
                line += f"  (known: {r['xfail']})"
            elif not r["ok"] and r["answer"]:
                line += "  | " + r["answer"][:120].replace("\n", " ")
            print(line.rstrip(), flush=True)
    ordered = [results[c["id"]] for c in cases]
    passed = sum(r["ok"] or bool(r.get("xfail")) for r in ordered)
    xf = sum(bool(r.get("xfail")) and not r["ok"] for r in ordered)
    print(f"{passed}/{len(ordered)} passed" + (f" ({xf} known issue(s))" if xf else ""))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(ordered, f, ensure_ascii=False, indent=2)
    return 0 if passed == len(ordered) else 1


def main():
    default_cases = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cases.json")
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--cases", default=default_cases, metavar="PATH",
                        help="cases JSON file (default: cases.json next to this script)")
    p = argparse.ArgumentParser(description="Run golden tasks against the AI Workbench job "
                                            "queue and report PASS/FAIL before releasing Bridge.")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", parents=[common], help="list cases (id, agent/mode/host, title)")
    r = sub.add_parser("run", parents=[common], help="submit cases, wait and check expectations")
    r.add_argument("--only", metavar="IDS", help="comma-separated case ids to run")
    r.add_argument("--skip", metavar="IDS", help="comma-separated case ids to skip")
    r.add_argument("--device", metavar="ID", default=os.environ.get("AIWB_EVALS_DEVICE") or _install_get("evals.device"),
                   help="device id substituted for $DEVICE (default: $AIWB_EVALS_DEVICE, else evals.device in install.json - "
                        "the PC with the corporate model session)")
    r.add_argument("--host", metavar="H", help="override host for all cases")
    r.add_argument("--parallel", type=int, default=3, metavar="N",
                   help="max jobs in flight at once (default: 3)")
    r.add_argument("--json", metavar="OUT", help="write results as JSON list to OUT")
    args = p.parse_args()
    sys.exit(cmd_list(args) if args.cmd == "list" else cmd_run(args))


if __name__ == "__main__":
    main()
