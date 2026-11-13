"""aiwb-e2e: job queue checks in the database over SSH (hz.py) — free Bridge slot, job report, leftover cancel."""
import json
import os
import sys
import time
from typing import Callable

def _core_dirs():
    """Core tree with install/aiwb_install.py: AIWB_FREEDOM / AIWB_SOURCE, devtree.json "repo", C:/AIWorkbench/freedom."""
    dirs = [os.environ.get("AIWB_FREEDOM"), os.environ.get("AIWB_SOURCE")]
    local = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    try:
        with open(os.environ.get("AIWB_DEVTREE_CONFIG") or os.path.join(local, "AIWorkbench", "devtree.json"),
                  encoding="utf-8-sig") as f:
            dirs.append((json.load(f) or {}).get("repo"))
    except (OSError, ValueError, AttributeError):
        pass
    return dirs + [r"C:\AIWorkbench\freedom", os.path.expanduser("~/AIWorkbench/freedom")]


def _install():
    """install.json of this install (Freedom F2) through install/aiwb_install.py of the source tree, or None."""
    for d in _core_dirs():
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
            raise SystemExit("no hz.py and no install/aiwb_install.py (set AIWB_FREEDOM to the Freedom tree)")
        return ai.ssh_connect()


def _db_name() -> str:
    """Database of the site: AIWB_DB_NAME, else install.json site.db.name."""
    if os.environ.get("AIWB_DB_NAME"):
        return os.environ["AIWB_DB_NAME"]
    ai = _install()
    return (ai.get("site.db.name") if ai else None) or "co_aiwb"


MYSQL = "mysql --defaults-extra-file=$HOME/co-agent/secrets/my.cnf " + _db_name() + " -N -B"


def _connect():
    return _ssh()


def sql(conn, query: str) -> list[list[str]]:
    # Run a query through the remote mysql client; raise on any stderr output
    stdin, stdout, stderr = conn.exec_command(MYSQL, timeout=60)
    stdin.write(query)
    stdin.flush()
    stdin.channel.shutdown_write()
    out = stdout.read().decode("utf-8", "replace")
    err = stderr.read().decode("utf-8", "replace")
    if err.strip():
        raise RuntimeError(f"MySQL error: {err.strip()}")
    return [line.split("\t") for line in out.splitlines() if line]


def q(s) -> str:
    # SQL string literal; None -> NULL
    if s is None:
        return "NULL"
    return "'" + str(s).replace("\\", "\\\\").replace("'", "''") + "'"


def max_parallel(host: str) -> int:
    # Parallel job limit for known hosts; 0 = unknown (no check)
    if host != "home-main":
        return 0
    path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "AIWorkbenchBridge", "config.json")
    try:
        with open(path, encoding="utf-8-sig") as f:
            v = json.load(f).get("maxParallelJobs")
        if isinstance(v, int) and not isinstance(v, bool) and v > 0:
            return v
    except (OSError, ValueError, AttributeError):
        pass
    return 6


def _drive_claimed(host: str) -> set[str]:
    # Jobs Bridge holds right now; jobs claimed through Drive stay 'inbox' in the database
    path = rf"G:\My Drive\AI-Workbench\transport\hosts\{host}\claimed"
    try:
        with os.scandir(path) as it:
            return {e.name for e in it if e.name.startswith("JOB-")}
    except OSError:
        return set()


def wait_for_slot(host: str, wait_sec: float, log: Callable[[str], None]) -> tuple[bool, int, int]:
    # Wait until running + queued jobs of the host are below its limit, or until wait_sec elapses
    limit = max_parallel(host)
    if limit == 0:
        return True, 0, 0
    conn = _connect()
    try:
        deadline = time.monotonic() + wait_sec
        last_log: float | None = None
        while True:
            rows = sql(
                conn,
                f"SELECT job_id, state FROM jobs WHERE host_id={q(host)} "
                f"AND state IN ('claimed','inbox') "
                f"AND queued_utc > UTC_TIMESTAMP() - INTERVAL 3 HOUR;",
            )
            running = _drive_claimed(host) | {jid for jid, st in rows if st == "claimed"}
            waiting = {jid for jid, st in rows if st == "inbox"} - running
            busy = len(running) + len(waiting)
            if busy < limit:
                return True, busy, limit
            now = time.monotonic()
            remaining = deadline - now
            if remaining <= 0:
                return False, busy, limit
            if last_log is None or now - last_log >= 60:
                log(f"{host} busy: {len(running)} running + {len(waiting)} queued, "
                    f"limit {limit}; waiting for a free slot")
                last_log = now
            time.sleep(min(15, remaining))
    finally:
        conn.close()


def _num(v: str) -> float | None:
    # mysql -B prints NULL literally
    return None if v == "NULL" else float(v)


def job_report(chat_id, cancel: bool) -> dict | None:
    # Report all jobs of a chat; optionally cancel pending/running ones
    if not chat_id:
        return None
    conn = _connect()
    try:
        rows = sql(conn, (
            "SELECT job_id, state, origin, host_id, fallback_utc IS NOT NULL, "
            "TIMESTAMPDIFF(MICROSECOND, queued_utc, claimed_utc)/1e6, "
            "TIMESTAMPDIFF(MICROSECOND, queued_utc, completed_utc)/1e6, "
            f"cancel_requested FROM jobs WHERE chat_id={q(chat_id)} ORDER BY queued_utc;"
        ))
        jobs = []
        for job_id, state, origin, host_id, fallback, claimed, completed, cancel_req in rows:
            job = {
                "jobId": job_id,
                "state": state,
                "origin": None if origin == "NULL" else origin,
                "hostId": None if host_id == "NULL" else host_id,
                "driveFallback": fallback == "1",
                "claimedAfterSec": _num(claimed),
                "completedAfterSec": _num(completed),
                "cancelRequested": cancel_req not in ("0", "NULL", ""),
            }
            if cancel and state in ("inbox", "claimed"):
                sql(conn, (
                    "UPDATE jobs SET cancel_requested = 1, "
                    f"state = IF(state = 'inbox', 'cancelled', state) WHERE job_id = {q(job_id)};"
                ))
                job["cancelledByE2E"] = True
                job["cancelRequested"] = True
                if state == "inbox":
                    job["state"] = "cancelled"
            jobs.append(job)
        return {"jobs": jobs}
    finally:
        conn.close()
