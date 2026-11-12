"""Live checks of the AI Workbench database job queue (Bridge 1.3+, db.php of the site, schema 4+).

  queue   puts one real job into the database queue of a host and waits for Bridge to run it
  status  queue summary of the last 24 h (states, waiting and stuck jobs), failure causes 24 h / 7 d and Bridge
          supervisor events (schema 17)

SQL runs on the hosting over SSH (the owner's hz.py in AIWB_HZ_DIR or ~/.ssh where it exists, else install.json
site.ssh; paramiko) with the server's my.cnf; no secret here. Database: AIWB_DB_NAME, else install.json site.db.name.
"""
import argparse
import json
import os
import random
import string
import sys
import time
from datetime import datetime, timezone

def _install():
    """install.json of this install (Freedom F2) through install/aiwb_install.py of the source tree, or None."""
    for d in (os.environ.get("AIWB_SOURCE"), r"C:\AIWorkbench\source", os.path.expanduser("~/AIWorkbench/source")):
        if d and os.path.isfile(os.path.join(d, "install", "aiwb_install.py")):
            if os.path.join(d, "install") not in sys.path:
                sys.path.insert(0, os.path.join(d, "install"))
            import aiwb_install
            return aiwb_install
    return None


def _ssh():
    """SSH to the site: the owner's hz.py (~/.ssh) where it exists, else install.json site.ssh (aiwb_install)."""
    sys.path.insert(0, os.environ.get("AIWB_HZ_DIR") or os.path.join(os.path.expanduser("~"), ".ssh"))
    try:
        import hz
        return hz.connect()
    except ImportError:
        ai = _install()
        if ai is None:
            raise SystemExit("no hz.py and no install/aiwb_install.py (set AIWB_SOURCE to the source tree)")
        return ai.ssh_connect()


def _owner_env() -> str:
    """Environment of the owner: AIWB_OWNER_ENV, else install.json (role owner), else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    ai = _install()
    return ((ai.owner() or {}).get("env") if ai else None) or "owner"


def _db_name() -> str:
    """Database of the site: AIWB_DB_NAME, else install.json site.db.name."""
    if os.environ.get("AIWB_DB_NAME"):
        return os.environ["AIWB_DB_NAME"]
    ai = _install()
    return (ai.get("site.db.name") if ai else None) or "co_aiwb"


MYSQL = "mysql --defaults-extra-file=$HOME/co-agent/secrets/my.cnf " + _db_name() + " -N -B"


def sql(conn, query: str) -> list[list[str]]:
    """Runs SQL through stdin (no shell quoting); returns rows of tab-separated columns."""
    stdin, stdout, stderr = conn.exec_command(MYSQL, timeout=60)
    stdin.write(query)
    stdin.channel.shutdown_write()
    out = stdout.read().decode("utf-8", "replace")
    err = stderr.read().decode("utf-8", "replace").strip()
    if err:
        raise RuntimeError(err)
    return [line.split("\t") for line in out.splitlines() if line]


def q(s: str) -> str:
    return "'" + s.replace("\\", "\\\\").replace("'", "''") + "'"


def cmd_queue(args) -> int:
    conn = _ssh()
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    job_id = f"JOB-SMOKE-{stamp}-" + "".join(random.choices(string.ascii_uppercase + string.digits, k=4))
    req = {
        "schemaVersion": 3, "protocolMinor": 1, "hostId": args.host, "jobId": job_id, "environmentId": args.env,
        "projectId": args.project, "chatId": args.chat, "branchId": "BR-root", "agent": "claude",
        "instruction": args.prompt or "Reply with exactly the word pong and nothing else.",
        "conversation": {"operation": "new", "sessionId": None}, "attachments": [], "artifactPaths": [],
        "timeoutMinutes": 0, "mode": args.mode, "model": args.model, "effort": "low", "output": {"autoExport": False},
    }
    body = json.dumps(req, ensure_ascii=False)
    sql(conn, f"""INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin,
                  queued_utc, request, sha, synced_utc)
                  VALUES ({q(job_id)}, {q(args.host)}, {q(args.env)}, {q(args.project)}, {q(args.chat)}, 'claude',
                  {q(args.mode)}, {q(args.model)}, 'inbox', 'db', UTC_TIMESTAMP(3), {q(body)}, SHA2({q(body)}, 256),
                  UTC_TIMESTAMP(3));""")
    print(f"queued {job_id} for {args.host} ({args.project}, {args.mode}, {args.model})")
    t0 = time.time()
    last = None
    while time.time() - t0 < args.timeout:
        time.sleep(3)
        rows = sql(conn, f"""SELECT state, IFNULL(status, ''), IFNULL(LEFT(JSON_VALUE(result, '$.agentFinalResponse'), 300), ''),
                             (SELECT COUNT(*) FROM job_events WHERE job_id = {q(job_id)}), IFNULL(fallback_utc, '')
                             FROM jobs WHERE job_id = {q(job_id)};""")
        if not rows:
            print("job row disappeared")
            return 1
        state, status, answer, batches, fallback = rows[0]
        if state != last:
            print(f"{time.time() - t0:5.1f} s  {state}" + (f"  (Drive fallback {fallback})" if fallback else ""))
            last = state
        if status:
            print(f"{time.time() - t0:5.1f} s  result {status}, event batches {batches}, answer: {answer!r}")
            if args.tool_like:
                # 0.3.0: tool calls seen in the job events (e.g. mcp__dev_ for device calls of a site job)
                hits = sql(conn, f"""SELECT COUNT(*) FROM job_events WHERE job_id = {q(job_id)} AND data LIKE {q('%' + args.tool_like + '%')};""")
                print(f"event batches mentioning {args.tool_like!r}: {hits[0][0]}")
            expect = (args.expect if args.expect is not None else ("" if args.prompt else "pong")).lower()
            ok = status == "completed" and expect in answer.lower() and (not args.tool_like or int(hits[0][0]) > 0)
            print("OK" if ok else "FAILED")
            return 0 if ok else 1
    sql(conn, f"UPDATE jobs SET cancel_requested = 1, state = IF(state = 'inbox', 'cancelled', state) WHERE job_id = {q(job_id)};")
    print(f"TIMEOUT after {args.timeout} s in state {last}; job cancelled")
    return 1


def cmd_status(args) -> int:
    conn = _ssh()
    print("database jobs of the last 24 h (origin, state, count):")
    for r in sql(conn, """SELECT IFNULL(origin, 'drive'), state, COUNT(*) FROM jobs
                          WHERE COALESCE(queued_utc, completed_utc, synced_utc) > UTC_TIMESTAMP() - INTERVAL 1 DAY
                          GROUP BY 1, 2 ORDER BY 1, 2;"""):
        print("  " + "  ".join(r))
    rows = sql(conn, """SELECT job_id, state, project_id, TIMESTAMPDIFF(MINUTE, COALESCE(claimed_utc, queued_utc), UTC_TIMESTAMP())
                        FROM jobs WHERE origin = 'db' AND result IS NULL AND state IN ('inbox', 'claimed')
                        ORDER BY queued_utc;""")
    print("waiting or running without result:" + ("" if rows else " none"))
    for r in rows:
        print(f"  {r[0]}  {r[1]}  {r[2]}  {r[3]} min")
    rows = sql(conn, "SELECT MAX(claimed_utc) FROM jobs WHERE origin = 'db';")
    print("last database claim (UTC):", rows[0][0] if rows else "-")
    health(conn)
    return 0


def health(conn) -> None:
    """Causes of failed jobs (job_failures) and Bridge supervisor events (host_events), schema 17 / db.php op health."""
    try:
        rows = sql(conn, """SELECT kind, SUM(failed_utc >= UTC_TIMESTAMP() - INTERVAL 1 DAY), COUNT(*), MAX(failed_utc),
                                   SUBSTRING_INDEX(GROUP_CONCAT(CONCAT(job_id, ' ', IFNULL(LEFT(REPLACE(detail, '\\n', ' '), 90), ''))
                                                   ORDER BY failed_utc DESC SEPARATOR '\\t'), '\\t', 1)
                            FROM job_failures WHERE failed_utc >= UTC_TIMESTAMP() - INTERVAL 7 DAY
                            GROUP BY kind ORDER BY 2 DESC, 3 DESC;""")
        events = sql(conn, """SELECT utc, host_id, kind, IFNULL(LEFT(REPLACE(detail, '\\n', ' '), 120), '') FROM host_events
                              WHERE utc >= UTC_TIMESTAMP() - INTERVAL 1 DAY ORDER BY utc DESC LIMIT 30;""")
    except RuntimeError as e:
        if "doesn't exist" in str(e):
            print("health: not deployed (schema 17)")
            return
        raise
    print("failures 24 h / 7 d by kind (last job, detail):" + ("" if rows else " none"))
    for r in rows:
        print(f"  {r[0]:<20} {r[1]:>3} {r[2]:>4}  last {r[3][:16]}  {r[4] if len(r) > 4 else ''}")
    print("supervisor events 24 h:" + ("" if events else " none"))
    for e in events:
        print(f"  {e[0][:19]}  {e[1]}  {e[2]}  {e[3] if len(e) > 3 else ''}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("queue", help="run one real job through the database queue")
    p.add_argument("--host", default="home-main")
    p.add_argument("--env", default=_owner_env())
    p.add_argument("--project", default="bridge-test")
    p.add_argument("--chat", default="CHAT-SMOKE")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--timeout", type=int, default=240, help="seconds to wait for the result")
    p.add_argument("--mode", default="chat", choices=["chat", "cowork"])
    p.add_argument("--prompt", help="own instruction instead of 'reply pong' (0.3.0)")
    p.add_argument("--expect", help="substring the answer must contain (default: pong, or nothing with --prompt)")
    p.add_argument("--tool-like", help="require job events containing this text, e.g. mcp__dev_ (device call of a site job)")
    p.set_defaults(fn=cmd_queue)
    sub.add_parser("status", help="queue summary of the last 24 h").set_defaults(fn=cmd_status)
    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
