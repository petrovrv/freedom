"""aiwb-backup-check: restore the newest nightly CO database dump into a private throwaway MariaDB on 127.0.0.1
and check it (dump age, restore rc, CHECK TABLE, row counts vs production stats). Production DB is never touched:
the only production access is the read-only `stats` op of db.php over HTTPS."""
import argparse
import base64
import datetime
import gzip
import hashlib
import json
import math
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

UA = "aiwb-backup-check/0.1"
TEST_DB = "aiwb_restore_test"
assert TEST_DB.startswith("aiwb_restore_")
KEY_TABLES = ["projects", "chats", "events", "jobs", "kv"]
LOCAL = os.environ["LOCALAPPDATA"]
DEFAULT_MARIADB_DIR = Path(LOCAL) / "AIWorkbench" / "mariadb"
NOWIN = subprocess.CREATE_NO_WINDOW


def install_get(key: str, default=None):
    """One value of install.json: AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json; the default when the file
    or the key is missing."""
    path = os.environ.get("AIWB_INSTALL") or os.path.join(LOCAL, "AIWorkbench", "install.json")
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


def site_api(name: str) -> str:
    """agent/<name> of the site: AIWB_SITE_URL, else install.json site.url."""
    url = os.environ.get("AIWB_SITE_URL") or install_get("site.url")
    if not url:
        raise RuntimeError("site unknown: set site.url in install.json or AIWB_SITE_URL")
    return url.rstrip("/") + "/agent/" + name


def owner_env() -> str:
    """Environment of the owner: AIWB_OWNER_ENV, else the member with role owner in install.json, else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
    return owner.get("env") or "owner"


def default_backup_dir() -> Path:
    """backups/db of the AI-Workbench folder in Drive: AIWB_ROOT, else install.json drive.root / drive.workbench."""
    root = os.environ.get("AIWB_ROOT") or os.path.join(install_get("drive.root") or r"G:\My Drive",
                                                       install_get("drive.workbench") or "AI-Workbench")
    return Path(root) / "backups" / "db"
_VERSION_RE = re.compile(r"^mariadb-(\d+(?:\.\d+)*)-winx64$", re.IGNORECASE)


# ---- portable MariaDB ----

def install(version: str, dest: Path) -> Path:
    base = f"https://archive.mariadb.org/mariadb-{version}/winx64-packages/"
    name = f"mariadb-{version}-winx64.zip"
    bin_dir = dest / f"mariadb-{version}-winx64" / "bin"
    if (bin_dir / "mariadbd.exe").is_file():
        print(f"MariaDB {version} already installed at {bin_dir}", file=sys.stderr)
        return bin_dir
    dest.mkdir(parents=True, exist_ok=True)
    hdr = {"User-Agent": UA}
    h = hashlib.sha256()
    tmp = tempfile.NamedTemporaryFile(dir=dest, delete=False, suffix=".zip")
    tmp_path = Path(tmp.name)
    try:
        with tmp, urllib.request.urlopen(urllib.request.Request(base + name, headers=hdr), timeout=60) as r:
            n, nxt = 0, 10 << 20
            while chunk := r.read(1 << 20):
                tmp.write(chunk); h.update(chunk); n += len(chunk)
                if n >= nxt:
                    print(f"  downloaded {n >> 20} MB", file=sys.stderr); nxt += 10 << 20
        expected = None
        try:
            with urllib.request.urlopen(urllib.request.Request(base + "sha256sums.txt", headers=hdr), timeout=60) as r:
                lines = r.read().decode("utf-8", "replace").splitlines()
            expected = next((t[0].lower() for l in lines if (t := l.split()) and t[-1].lstrip("*").removeprefix("./") == name), None)
            if expected is None:
                print(f"warning: {name} not listed in sha256sums.txt; checksum not verified", file=sys.stderr)
        except urllib.error.HTTPError as e:
            print(f"warning: could not fetch sha256sums.txt ({e.code}); checksum not verified", file=sys.stderr)
        if expected is not None and expected != h.hexdigest():
            raise SystemExit(f"SHA-256 mismatch for {name}: expected {expected}, got {h.hexdigest()}")
        with zipfile.ZipFile(tmp_path) as zf:
            zf.extractall(dest)
        return bin_dir
    finally:
        tmp_path.unlink(missing_ok=True)


def find_bin(dest: Path) -> Path:
    candidates: list[tuple[tuple[int, ...], Path]] = []
    for d in dest.glob("mariadb-*-winx64"):
        if not d.is_dir() or not (d / "bin" / "mariadbd.exe").is_file():
            continue
        m = _VERSION_RE.match(d.name)
        if not m:
            continue
        candidates.append((tuple(int(part) for part in m.group(1).split(".")), d))
    if not candidates:
        raise SystemExit(f"MariaDB not found in {dest}; run: aiwb-backup-check.py install")
    candidates.sort(key=lambda c: c[0])
    return candidates[-1][1] / "bin"


# ---- private throwaway server (127.0.0.1 only) ----

class LocalServer:
    def __init__(self, bin_dir: Path, work_dir: Path, port: int):
        self.bin_dir, self.work_dir, self.port = bin_dir, work_dir, port
        self.data = work_dir / "data"
        self.err = work_dir / "mariadbd.err"
        self.proc = None

    def exe(self, name) -> str:
        return str(self.bin_dir / f"{name}.exe")

    def client_args(self) -> list:
        # The only place that builds connection args: local throwaway server, never production.
        return ["--no-defaults", "-h", "127.0.0.1", "-P", str(self.port), "-u", "root", "--protocol=TCP"]

    def run(self, args: list, check=True) -> subprocess.CompletedProcess:
        r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                           creationflags=NOWIN, timeout=600)
        if check and r.returncode != 0:
            raise RuntimeError(f"{Path(args[0]).name} rc={r.returncode}: " + (r.stderr or "")[-1000:])
        return r

    def sql(self, statement: str) -> list[list[str]]:
        r = self.run([self.exe("mariadb"), *self.client_args(), "-N", "-B",
                      "--default-character-set=utf8mb4", "-e", statement])
        return [line.split("\t") for line in r.stdout.splitlines() if line]

    def ping(self) -> bool:
        return self.run([self.exe("mariadb-admin"), *self.client_args(), "ping"], check=False).returncode == 0

    def _shutdown(self):
        self.run([self.exe("mariadb-admin"), *self.client_args(), "shutdown"], check=False)

    @staticmethod
    def _rmtree(path):
        path = Path(path)
        if not path.exists():
            return

        def onerror(func, p, _exc):
            os.chmod(p, stat.S_IWRITE)
            func(p)
        shutil.rmtree(path, onerror=onerror)

    def _err_tail(self) -> str:
        return self.err.read_text(encoding="utf-8", errors="replace")[-1500:] if self.err.exists() else ""

    def start(self):
        if self.data.exists():
            self._shutdown()
            time.sleep(2)
            self._rmtree(self.data)
        self.work_dir.mkdir(parents=True, exist_ok=True)
        self.run([self.exe("mariadb-install-db"), f"--datadir={self.data}"])
        self.proc = subprocess.Popen(
            [self.exe("mariadbd"), "--no-defaults", f"--datadir={self.data}", f"--port={self.port}",
             "--bind-address=127.0.0.1", "--skip-name-resolve", "--max-allowed-packet=1G",
             "--innodb-buffer-pool-size=256M", "--character-set-server=utf8mb4", f"--log-error={self.err}"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=NOWIN)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise RuntimeError(f"mariadbd exited rc={self.proc.returncode}: " + self._err_tail())
            if self.ping():
                return
            time.sleep(0.5)
        self.proc.kill()
        self.proc.wait(10)
        raise RuntimeError("mariadbd did not become ready within 60 s: " + self._err_tail())

    def restore(self, gz_path, db: str) -> dict:
        self.sql(f"CREATE DATABASE `{db}` CHARACTER SET utf8mb4")
        total, tail = 0, b""
        with tempfile.TemporaryFile() as errf:
            p = subprocess.Popen(
                [self.exe("mariadb"), *self.client_args(), "--default-character-set=utf8mb4",
                 "--max-allowed-packet=1G", db],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=errf, creationflags=NOWIN)
            t0 = time.monotonic()
            try:
                with gzip.open(gz_path, "rb") as f:
                    while chunk := f.read(1 << 20):
                        total += len(chunk)
                        tail = (tail + chunk)[-400:]
                        try:
                            p.stdin.write(chunk)
                        except (BrokenPipeError, OSError):
                            break
            finally:
                try:
                    p.stdin.close()
                except OSError:
                    pass
                rc = p.wait()
            elapsed = time.monotonic() - t0
            errf.seek(0)
            tail_text = errf.read().decode("utf-8", "replace")[-1000:]
        m = re.search(rb"-- Dump completed on (\d{4}-\d\d-\d\d +\d{1,2}:\d\d:\d\d)", tail)
        return {"rc": rc, "stderr": tail_text, "sql_bytes": total, "seconds": round(elapsed, 1),
                "dump_completed": m.group(1).decode() if m else None}

    def stop(self, keep=False):
        if self.proc:
            try:
                self._shutdown()
            except Exception:
                pass
            try:
                self.proc.wait(30)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(10)
            self.proc = None
        if not keep:
            self._rmtree(self.data)


def check_db(srv, db: str, key_tables: list[str]) -> tuple[dict, dict, dict]:
    names = [r[0] for r in srv.sql(f"SELECT table_name FROM information_schema.tables WHERE table_schema='{db}' ORDER BY 1")]
    counts = {t: int(srv.sql(f"SELECT COUNT(*) FROM `{db}`.`{t}`")[0][0]) for t in names}
    ok, err = set(), set()
    if names:
        for r in srv.sql("CHECK TABLE " + ", ".join(f"`{db}`.`{t}`" for t in names)):
            t = r[0].removeprefix(f"{db}.")
            if r[2] == "status" and r[3] == "OK": ok.add(t)
            if r[2] == "error": err.add(t)
    bad = [t for t in names if t not in ok or t in err]
    check_table = {"ok": bool(names) and not bad,
                   "detail": f"{len(names)-len(bad)}/{len(names)} OK" + (" bad: " + ", ".join(bad) if bad else "")}
    missing = [t for t in key_tables if t not in counts]
    empty = [t for t in key_tables if t != "kv" and counts.get(t) == 0]
    parts = ([f"missing: {', '.join(missing)}"] if missing else []) + ([f"empty: {', '.join(empty)}"] if empty else [])
    tables = {"ok": not missing and not empty, "detail": "; ".join(parts) or f"{len(names)} tables"}
    return counts, check_table, tables


# ---- site API (home PC key, keyring aiwb/co-push) ----

def push_key() -> str:
    import keyring
    k = keyring.get_password("aiwb/co-push", "default")
    if not k:
        raise SystemExit("no key in keyring aiwb/co-push (account default)")
    return k


def post(url: str, payload: dict, gz: bool, timeout: int) -> str:
    body = json.dumps(payload).encode()
    if gz:
        body = json.dumps({"gz": base64.b64encode(gzip.compress(body)).decode()}).encode()
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "Content-Type": "application/json", "X-CO-Push-Key": push_key(), "User-Agent": UA})
    for attempt in range(1, 4):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read().decode()
        except urllib.error.HTTPError as e:
            msg = e.read().decode(errors="replace")
            if e.code < 500 or attempt == 3:
                raise RuntimeError(f"{url}: HTTP {e.code} {msg[:300]}") from e
        except urllib.error.URLError as e:
            if attempt == 3:
                raise RuntimeError(f"{url}: {e.reason}") from e
        time.sleep(3 * attempt)


def prod_stats():
    return json.loads(post(site_api("db.php"), {"op": "stats"}, True, 60))


def notify(env, title, body):
    return post(site_api("push.php"), {"op": "notify", "env": env, "title": title[:120], "body": body[:380],
                           "url": "/", "tag": "backup-check"}, False, 30)


def compare(restored: dict, prod: dict, tolerance: float, min_slack: int) -> list[dict]:
    out = []
    for table, v in prod.items():
        if not (isinstance(v, dict) and "count" in v):
            continue
        p = int(v["count"])
        r = restored.get(table)
        allowed = max(min_slack, math.ceil(tolerance * p))
        diff = None if r is None else r - p
        ok = diff is not None and abs(diff) <= allowed
        out.append({"table": table, "restored": r, "prod": p, "diff": diff, "allowed": allowed, "ok": ok})
    return out


# ---- run ----

def cmd_run(a) -> tuple[int, dict]:
    rep = {"ok": False, "checks": {}, "counts": {}, "prod": [], "timings": {}}
    checks = rep["checks"]
    t0 = time.monotonic()
    srv = None
    code = 2
    try:
        files = sorted(Path(a.backup_dir or default_backup_dir()).glob(a.pattern), key=lambda p: p.stat().st_mtime)
        if not files:
            checks["age"] = {"ok": False, "detail": "no backup files"}
            return 1, rep
        f = files[-1]
        st = f.stat()
        age = (time.time() - st.st_mtime) / 3600
        rep["backup"] = {
            "name": f.name,
            "size": st.st_size,
            "mtime": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(st.st_mtime)),
            "age_hours": round(age, 2),
        }
        checks["age"] = {"ok": age <= a.max_age_hours, "detail": f"{age:.1f} h (max {a.max_age_hours})"}

        bin_dir = Path(a.mariadb_bin) if a.mariadb_bin else find_bin(DEFAULT_MARIADB_DIR)
        srv = LocalServer(bin_dir, Path(a.work_dir), a.port)
        srv.start()
        r = srv.restore(f, TEST_DB)
        rc, dc = r["rc"], r["dump_completed"]
        rep["backup"]["dump_completed"] = dc
        rep["timings"]["restore_s"] = r["seconds"]
        checks["restore"] = {
            "ok": rc == 0,
            "detail": f"rc={rc}, {r['sql_bytes'] / 1e6:.1f} MB SQL" + ("; " + str(r["stderr"]) if rc else ""),
        }
        checks["dump_complete"] = {"ok": bool(dc), "detail": dc or "trailer missing"}

        counts, ct, tb = check_db(srv, TEST_DB, KEY_TABLES)
        rep["counts"] = counts
        checks["check_table"] = ct
        checks["tables"] = tb

        if not a.no_prod:
            rows = compare(counts, prod_stats(), a.tolerance, a.min_slack)
            rep["prod"] = rows
            bad = [x["table"] for x in rows if not x["ok"]]
            checks["prod_counts"] = {
                "ok": not bad,
                "detail": ("off: " + ", ".join(bad)) if bad
                else f"{len(rows)} tables within tolerance {a.tolerance:.0%}/{a.min_slack}",
            }

        rep["ok"] = all(c["ok"] for c in checks.values())
        code = 0 if rep["ok"] else 1
    except Exception as e:
        rep["error"] = f"{type(e).__name__}: {e}"
        rep["traceback"] = traceback.format_exc()
        code = 2
    finally:
        if srv:
            try:
                srv.stop(a.keep)
            except Exception as e:
                rep.setdefault("cleanup_error", str(e))
        rep["timings"]["total_s"] = round(time.monotonic() - t0, 1)
    return code, rep


def format_report(rep: dict) -> str:
    checks = rep.get("checks") or {}
    failed = [n for n, c in checks.items() if not c.get("ok")]
    if rep.get("error"):
        failed.append("error")
    ok = rep.get("ok") and not failed
    out = ["BACKUP CHECK OK" if ok else "BACKUP CHECK FAILED: " + ", ".join(failed or ["unknown"])]
    b = rep.get("backup")
    if b:
        try:
            out.append(f"backup: {b.get('name', '?')} {float(b.get('size') or 0) / 1e6:.1f} MB, mtime {b.get('mtime', '?')}, "
                       f"age {float(b.get('age_hours') or 0):.1f} h, dump completed {b.get('dump_completed', '?')} UTC")
        except (TypeError, ValueError):
            out.append(f"backup: {b}")
    out += [f"  [{'OK' if c.get('ok') else 'FAIL'}] {n}: {c.get('detail', '')}" for n, c in checks.items()]
    counts = rep.get("counts") or {}
    if counts:
        prod = {r.get("table"): r for r in rep.get("prod") or []}

        def sd(d):
            return "-" if d is None else (f"{d:+d}" if isinstance(d, int) and d else str(d))
        hdr = ("table", "restored", "prod", "diff", "allowed", "ok")
        rows = []
        for t in sorted(counts):
            r = prod.get(t)
            if r:
                rows.append((t, str(r.get("restored", counts[t])), str(r.get("prod", "-")), sd(r.get("diff")),
                             str(r.get("allowed", "-")), "OK" if r.get("ok") else "FAIL"))
            else:
                rows.append((t, str(counts[t]), "-", "-", "-", "-"))
        w = [max(len(x[i]) for x in [hdr, *rows]) for i in range(len(hdr))]
        fmt = lambda r: "  ".join(v.ljust(w[i]) if i == 0 else v.rjust(w[i]) for i, v in enumerate(r)).rstrip()
        out += [fmt(hdr)] + [fmt(r) for r in rows]
    tm = rep.get("timings") or {}
    f = lambda v: f"{v:.1f}" if isinstance(v, float) else ("-" if v is None else str(v))
    out.append(f"restore {f(tm.get('restore_s'))} s, total {f(tm.get('total_s'))} s")
    if rep.get("error"):
        out.append(f"error: {rep['error']}")
    return "\n".join(out)


def failure_summary(rep):
    parts = [f"{n}: {c.get('detail', '')}" for n, c in (rep.get("checks") or {}).items() if not c.get("ok")]
    if rep.get("error"):
        parts.append(str(rep["error"]))
    return ", ".join(parts)


def write_log(path, code, text, rep):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    ts = datetime.datetime.now().astimezone().isoformat(timespec="seconds")
    entry = f"===== {ts} exit={code}\n{text}\n"
    if rep.get("traceback"):
        entry += f"{rep['traceback']}\n"
    with path.open("a", encoding="utf-8") as f:
        f.write(entry)


def cmd_status(a):
    p = Path(a.log)
    if not p.is_file():
        print("no log")
        return
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
    starts = [i for i, l in enumerate(lines) if l.startswith("===== ")]
    print("\n".join(lines[starts[-1]:] if starts else lines))


def main(argv=None):
    default_log = Path(LOCAL) / "AIWorkbenchBridge" / "logs" / "backup-check.log"
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    pi = sub.add_parser("install", help="download portable MariaDB into %%LOCALAPPDATA%%")
    pi.add_argument("--version", default="11.8.9")
    pi.add_argument("--dir", type=Path, default=DEFAULT_MARIADB_DIR)

    pr = sub.add_parser("run", help="restore the newest backup into a throwaway local server and check it")
    pr.add_argument("--backup-dir", type=Path, default=None, help="folder of the nightly dumps (default: <AI-Workbench in Drive>/backups/db)")
    pr.add_argument("--pattern", default="db-*.sql.gz")
    pr.add_argument("--max-age-hours", type=float, default=26)
    pr.add_argument("--tolerance", type=float, default=0.10)
    pr.add_argument("--min-slack", type=int, default=10)
    pr.add_argument("--port", type=int, default=33307)
    pr.add_argument("--work-dir", type=Path, default=Path(LOCAL) / "AIWorkbench" / "backup-check")
    pr.add_argument("--mariadb-bin", type=Path, default=None)
    pr.add_argument("--keep", action="store_true")
    pr.add_argument("--no-prod", action="store_true")
    pr.add_argument("--notify", action="store_true")
    pr.add_argument("--notify-env", default=None, help="environment to push to (default: the owner's, install.json)")
    pr.add_argument("--json", action="store_true")
    pr.add_argument("--log", type=Path, default=default_log)

    ps = sub.add_parser("status", help="print the last report from the log")
    ps.add_argument("--log", type=Path, default=default_log)

    a = ap.parse_args(argv)

    if a.cmd == "install":
        print(install(a.version, a.dir))
        return 0
    if a.cmd == "status":
        cmd_status(a)
        return 0

    code, rep = cmd_run(a)
    text = format_report(rep)
    print(json.dumps(rep, ensure_ascii=False, indent=2) if a.json else text)
    write_log(a.log, code, text, rep)
    if a.notify and code != 0:
        try:
            notify(a.notify_env or owner_env(), "DB backup restore check failed", failure_summary(rep))
            print("notified")
        except Exception as e:
            print(f"notify failed: {e}", file=sys.stderr)
    return code


if __name__ == "__main__":
    sys.exit(main())
