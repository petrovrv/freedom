"""aiwb-e2e: end-to-end test of CO Web — Playwright signs in as the owner, sends "reply pong" in a test chat, waits for the reply in the DOM, deletes the chat."""
import argparse, json, os, secrets, shutil, string, subprocess, sys, threading
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

import requests

CRED_TARGET = "AIWorkbench.Bridge.Google"
SCRIPT_DIR = Path(__file__).resolve().parent
# Playwright of the local site workspace (~/aiwb-site); PW_MODULE or --pw-module override it
PW_DEFAULT = (Path.home() / "aiwb-site" / "node_modules" / "playwright" / "index.mjs").as_posix()


class Parser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        self.exit(2, f"{self.prog}: error: {message}\n")


def read_credential(target: str) -> str:
    """Read a generic credential blob from Windows Credential Manager."""
    import ctypes
    from ctypes import wintypes as w

    class CREDENTIAL(ctypes.Structure):
        _fields_ = [("Flags", w.DWORD), ("Type", w.DWORD), ("TargetName", w.LPWSTR),
                    ("Comment", w.LPWSTR), ("LastWritten", w.FILETIME),
                    ("CredentialBlobSize", w.DWORD), ("CredentialBlob", ctypes.POINTER(ctypes.c_ubyte)),
                    ("Persist", w.DWORD), ("AttributeCount", w.DWORD), ("Attributes", ctypes.c_void_p),
                    ("TargetAlias", w.LPWSTR), ("UserName", w.LPWSTR)]

    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    adv.CredReadW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
    adv.CredReadW.restype = w.BOOL
    adv.CredFree.argtypes = [ctypes.c_void_p]
    adv.CredFree.restype = None
    pcred = ctypes.POINTER(CREDENTIAL)()
    if not adv.CredReadW(target, 1, 0, ctypes.byref(pcred)):  # 1 = CRED_TYPE_GENERIC
        raise RuntimeError(f"credential '{target}' not readable (winerror {ctypes.get_last_error()})")
    try:
        c = pcred.contents
        blob = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize) if c.CredentialBlobSize else b""
    finally:
        adv.CredFree(pcred)
    enc = "utf-16-le" if len(blob) >= 2 and blob[1] == 0 else "utf-8"
    return blob.decode(enc, errors="replace").replace("\x00", "")


def google_token_and_email() -> tuple[str, str]:
    """Exchange the Bridge refresh token for an access token and resolve the account email."""
    data = json.loads(read_credential(CRED_TARGET))
    missing = [k for k in ("client_id", "client_secret", "refresh_token") if not data.get(k)]
    if missing:
        raise RuntimeError(f"credential JSON missing: {', '.join(missing)}")
    r = requests.post("https://oauth2.googleapis.com/token", timeout=60, data={
        "grant_type": "refresh_token", "client_id": data["client_id"],
        "client_secret": data["client_secret"], "refresh_token": data["refresh_token"]})
    if not r.ok:
        try:
            err = r.json().get("error_description") or r.json().get("error")
        except ValueError:
            err = r.reason
        raise RuntimeError(f"token endpoint HTTP {r.status_code}: {err}")
    token = r.json().get("access_token")
    if not token:
        raise RuntimeError("token endpoint returned no access_token")
    r = requests.get("https://www.googleapis.com/drive/v3/about", params={"fields": "user(emailAddress)"},
                     headers={"Authorization": f"Bearer {token}"}, timeout=60)
    if not r.ok:
        raise RuntimeError(f"drive about HTTP {r.status_code}")
    email = ((r.json().get("user") or {}).get("emailAddress") or "").lower()
    if not email:
        raise RuntimeError("no emailAddress in drive about response")
    return token, email


def run_script(node, script_name, cfg, timeout):
    """Run a Node script from SCRIPT_DIR with cfg on stdin; return the last stdout JSON line."""
    token = cfg.get('token') or ''
    try:
        proc = subprocess.run(
            [node, script_name], cwd=str(SCRIPT_DIR), input=json.dumps(cfg),
            capture_output=True, text=True, encoding='utf-8', errors='replace',
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return {'status': 'error', 'reason': f'timeout after {timeout}s running {script_name}'}
    except OSError as e:
        return {'status': 'error', 'reason': f'cannot start node: {e}'}

    # Forward logs, redacting the token just in case
    if proc.stderr:
        err = proc.stderr.replace(token, '***') if token else proc.stderr
        sys.stderr.write(err)
        if not err.endswith('\n'):
            sys.stderr.write('\n')

    lines = [ln for ln in (proc.stdout or '').splitlines() if ln.strip()]
    if not lines:
        return {'status': 'error', 'reason': f'no output from {script_name} (exit {proc.returncode})'}
    try:
        res = json.loads(lines[-1])
    except ValueError:
        tail = lines[-1].replace(token, '***') if token else lines[-1]
        return {'status': 'error', 'reason': f'unparseable output from {script_name}: {tail[:200]}'}
    if not isinstance(res, dict):
        return {'status': 'error', 'reason': f'unexpected output type from {script_name}'}
    return res


def _site_default() -> str:
    """Freedom: the site of this install - AIWB_SITE_URL, else install.json site.url."""
    if os.environ.get("AIWB_SITE_URL"):
        return os.environ["AIWB_SITE_URL"].rstrip("/")
    import e2e_db
    ai = e2e_db._install()
    return ((ai.get("site.url") if ai else None) or "https://co.example.org").rstrip("/")


def _env_default() -> str:
    """The owner's environment - AIWB_OWNER_ENV, else install.json (role owner)."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    import e2e_db
    ai = e2e_db._install()
    return ((ai.owner() or {}).get("env") if ai else None) or "owner"


def add_shot_parser(sub):
    """Register the 'shot' subcommand."""
    p = sub.add_parser('shot', help='Open a page with injected auth and save a PNG screenshot')
    p.add_argument('--base', default=_site_default(), help='Site base URL')
    p.add_argument('--env', default=_env_default(), help='Environment name')
    p.add_argument('--path', required=True, help='URL path, e.g. /e/anna/p/AI-Workbench/c/CHAT-x')
    p.add_argument('--out', required=True, help='Output PNG path')
    p.add_argument('--dist', metavar='PATH', help='Local build dir (must contain index.html)')
    p.add_argument('--wait-selector', default='.chat-list li', help='Selector to wait for before settle')
    p.add_argument('--settle-ms', type=int, default=2500, help='Extra wait after selector (ms)')
    p.add_argument('--width', type=int, default=1400)
    p.add_argument('--height', type=int, default=900)
    p.add_argument('--mobile', action='store_true', help='390x844, isMobile, hasTouch, DPR 2')
    p.add_argument('--browser', choices=['chromium', 'msedge', 'webkit', 'firefox'], default='chromium',
                   help='Engine; webkit = Safari (npx playwright install webkit)')
    p.add_argument('--clip-selector', help='Screenshot only this element')
    p.add_argument('--eval', help='JS expression evaluated after settle; result returned as "eval"')
    p.add_argument('--click', help='Selector clicked before waiting/settle')
    p.add_argument('--steps', metavar='FILE', help='JSON list of UI steps after settle (click/fill+text/press[+on]/wait/sleep/files+paths/eval/shot; optional:true)')
    p.add_argument('--storage', action='append', default=[], metavar='KEY=VALUE',
                   help='localStorage item set before the page loads (repeatable)')
    p.add_argument('--timeout', type=int, default=120, help='Overall timeout in seconds')
    p.add_argument('--json', action='store_true', help='Print JSON result')
    p.add_argument('--pw-module',
                   default=os.environ.get('PW_MODULE', PW_DEFAULT),
                   help='Path to playwright index.mjs')
    p.add_argument('--headed', action='store_true', help='Show the browser window')
    return p


def cmd_shot(args) -> int:
    """Execute 'shot'. Exit codes: 0 ok, 1 error, 2 cannot run."""
    def fail(msg):
        print(f'aiwb-e2e shot: cannot run: {msg}', file=sys.stderr)
        return 2

    # Validate inputs
    dist = None
    if args.dist:
        dist_path = Path(args.dist).expanduser().resolve()
        if not (dist_path / 'index.html').is_file():
            return fail(f'--dist has no index.html: {dist_path}')
        dist = str(dist_path)

    pw_module = Path(args.pw_module).expanduser()
    if not pw_module.is_file():
        return fail(f'playwright module not found: {pw_module} (use --pw-module or PW_MODULE)')

    node = os.environ.get('NODE') or shutil.which('node')
    if not node:
        return fail('node not found in PATH')

    if not (SCRIPT_DIR / 'shot.mjs').is_file():
        return fail(f'shot.mjs not found in {SCRIPT_DIR}')

    try:
        token, email = google_token_and_email()
    except Exception as e:
        return fail(f'cannot get token: {e}')
    if not token:
        return fail('empty token')

    out = Path(args.out).expanduser().resolve()

    cfg = {
        'base': args.base.rstrip('/'),
        'env': args.env,
        'path': args.path if args.path.startswith('/') else '/' + args.path,
        'out': str(out),
        'dist': dist,
        'waitSelector': args.wait_selector,
        'settleMs': args.settle_ms,
        'width': args.width,
        'height': args.height,
        'mobile': args.mobile,
        'browser': args.browser,
        'clipSelector': args.clip_selector,
        'eval': args.eval,
        'click': args.click,
        'steps': json.loads(Path(args.steps).read_text(encoding='utf-8')) if args.steps else None,
        'storage': dict(kv.split('=', 1) for kv in args.storage if '=' in kv),
        'headed': args.headed,
        'pwModule': pw_module.as_posix(),
        'token': token,
        'email': email,
        'host': 'home-main',
    }

    res = run_script(node, 'shot.mjs', cfg, args.timeout + 30)
    res.pop('token', None)  # defensive: never print the token
    ok = res.get('status') == 'ok'

    if args.json:
        print(json.dumps(res, indent=2, ensure_ascii=False))
    else:
        if ok:
            print(f"aiwb-e2e shot: OK {res.get('out') or out}")
        else:
            print(f"aiwb-e2e shot: ERROR {res.get('reason') or 'unknown'}")
        if args.eval:
            if res.get('evalError'):
                print(f"  eval error: {res['evalError']}")
            else:
                print(f"  eval: {json.dumps(res.get('eval'), ensure_ascii=False)}")
        for st in res.get('steps') or []:
            print(f"  step {st.get('step')}: {json.dumps({k: v for k, v in st.items() if k != 'step'}, ensure_ascii=False)[:300]}")
        errors = res.get('pageErrors') or []
        print(f"  page errors: {len(errors)}")
        for e in errors[:5]:
            print(f"    - {e}")
        if dist:
            print(f"  served files: {res.get('servedFiles', 0)}")
        if 'elapsedSec' in res:
            print(f"  elapsed: {res['elapsedSec']}s")

    return 0 if ok else 1


def build_parser() -> Parser:
    p = Parser(prog="aiwb-e2e.py", description="End-to-end test of CO Web (the site of install.json).")
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run", help="run the end-to-end test")
    r.add_argument("--base", default=_site_default())
    r.add_argument("--env", default=_env_default())
    r.add_argument("--project", default="bridge-test")
    r.add_argument("--host", default="home-main", choices=["auto", "home-main", "site"],
                   help="auto: home-main when a slot frees within --queue-wait, else site")
    r.add_argument("--agent", default="claude")
    r.add_argument("--model-hint", default="sonnet")
    r.add_argument("--prompt", default="[aiwb-e2e {run}] reply pong")
    r.add_argument("--expect", default="pong", help="regex expected in reply")
    r.add_argument("--timeout", type=int, default=300, help="seconds")
    r.add_argument("--dist", metavar="PATH", help="serve CO Web static files from this local build (API stays prod)")
    r.add_argument("--keep", action="store_true")
    r.add_argument("--headed", action="store_true")
    r.add_argument("--out", metavar="DIR", default=str(
        Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData/Local") / "AIWorkbenchWork" / "e2e"))
    r.add_argument("--queue-wait", type=int, default=600, help="seconds to wait for a free Bridge slot")
    r.add_argument("--no-db", action="store_true", help="skip the SSH/database queue checks")
    r.add_argument("--json", action="store_true")
    r.add_argument("--pw-module", default=os.environ.get("PW_MODULE") or PW_DEFAULT)
    add_shot_parser(sub)
    return p


def run_node(node: str, cfg: dict, timeout: int) -> dict:
    """Run e2e.mjs, stream stderr live, parse the last stdout line as result JSON."""
    proc = subprocess.Popen([node, "e2e.mjs"], cwd=SCRIPT_DIR, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, encoding="utf-8", errors="replace")
    tail: deque[str] = deque(maxlen=40)

    def pump():
        for line in proc.stderr:
            tail.append(line.rstrip("\r\n"))
            sys.stderr.write(line)
            sys.stderr.flush()

    t = threading.Thread(target=pump, daemon=True)
    t.start()
    killed = threading.Event()

    def kill():
        killed.set()
        proc.kill()

    timer = threading.Timer(timeout, kill)  # hard stop even while stdout read is blocking
    timer.start()
    try:
        try:
            proc.stdin.write(json.dumps(cfg, ensure_ascii=False))
            proc.stdin.close()
        except OSError:
            pass
        out = proc.stdout.read()
        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            kill()
            proc.wait()
    finally:
        timer.cancel()
    t.join(timeout=5)
    if killed.is_set():
        return {"status": "error", "reason": f"node timed out after {timeout}s and was killed"}
    lines = [l for l in out.splitlines() if l.strip()]
    try:
        result = json.loads(lines[-1])
        if not isinstance(result, dict):
            raise ValueError("result is not an object")
        return result
    except (IndexError, ValueError):
        return {"status": "error", "reason": "no result from node: " + "\n".join(tail)}


def print_human(result: dict, dist_given: bool) -> None:
    label = {"green": "GREEN", "red": "RED"}.get(result.get("status"), "ERROR")
    print(f"aiwb-e2e: {label} ({result.get('runId')}, host {result.get('host')})")
    if result.get("chatId") is not None:
        print(f"chat: {result['chatId']}")
    if isinstance(result.get("timings"), dict) and result["timings"]:
        print("timings: " + ", ".join(f"{k}={v}s" for k, v in result["timings"].items()))
    if result.get("reply") is not None:
        reply = str(result["reply"]).replace("\r\n", "\n").replace("\r", "\n").replace("\n", " ")
        print(f"reply: {reply[:200]}")
    if result.get("cleanup") is not None:
        print(f"cleanup: {result['cleanup']}")
    for key in ("reason", "screenshot"):
        if result.get(key) is not None:
            print(f"{key}: {result[key]}")
    pe = result.get("pageErrors")
    if pe:
        print(f"page errors: {len(pe)}")
    if dist_given and result.get("servedFiles") is not None:
        print(f"served local files: {result['servedFiles']}")


def job_line(j: dict) -> str:
    # One-line human summary of a job record; None parts are omitted
    line = f"job {j.get('jobId')}: {j.get('state')}"
    if j.get("origin") is not None:
        line += f" (origin {j['origin']})"
    for key, label in (("claimedAfterSec", "claimed"), ("completedAfterSec", "completed")):
        v = j.get(key)
        if v is not None:
            try:
                line += f", {label} +{float(v):.1f}s"
            except (TypeError, ValueError):
                line += f", {label} +{v}s"
    if j.get("cancelledByE2E"):
        line += ", cancelled by e2e"
    return line


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        s.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args()
    if args.cmd == "shot":
        return cmd_shot(args)

    run_id = ("E2E-" + datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S") + "-"
              + "".join(secrets.choice(string.ascii_uppercase + string.digits) for _ in range(4)))

    def fail(msg: str) -> int:
        print(f"aiwb-e2e: {msg}", file=sys.stderr)
        return 2

    def log(msg: str) -> None:
        print("[e2e] " + str(msg), file=sys.stderr)

    dist = None
    if args.dist:
        d = Path(args.dist).resolve()
        if not d.is_dir() or not (d / "index.html").is_file():
            return fail(f"--dist must be an existing directory containing index.html: {d}")
        dist = str(d)
    pw = Path(args.pw_module)
    if not pw.is_file():
        return fail(f"playwright module not found: {pw}")
    if not (SCRIPT_DIR / "e2e.mjs").is_file():
        return fail(f"e2e.mjs not found in {SCRIPT_DIR}")
    node = shutil.which("node")
    if not node:
        return fail("node not found on PATH")

    out_dir = Path(args.out).resolve()
    result = None

    # Lazy import of the DB helper module (skipped with --no-db)
    db = None
    if not args.no_db:
        try:
            import e2e_db
            db = e2e_db
        except Exception as e:
            print(f"aiwb-e2e: warning: cannot import e2e_db: {e}", file=sys.stderr)

    # Wait for a free job slot on the host before starting (other agents' jobs go first)
    host_note = None
    if args.host == "auto":
        chosen = "home-main"
        note = "auto -> home-main"
        if db is None:
            note = "auto -> home-main (queue check unavailable)"
        else:
            try:
                ok, busy, limit = db.wait_for_slot("home-main", args.queue_wait, log)
                if not ok:
                    chosen = "site"
                    note = "auto -> site"
                    log(f"home-main busy ({busy}/{limit}); using site")
            except Exception as e:
                print(f"aiwb-e2e: warning: queue check failed, using home-main: {e}", file=sys.stderr)
                note = "auto -> home-main (queue check failed)"
        args.host = chosen
        host_note = note
    elif db is not None:
        try:
            ok, busy, limit = db.wait_for_slot(args.host, args.queue_wait, log)
            if not ok:
                result = {"status": "error",
                          "reason": f"{args.host} busy: {busy}/{limit} jobs running for "
                                    f"{args.queue_wait} s; e2e not run",
                          "busy": True}
        except Exception as e:
            print(f"aiwb-e2e: warning: queue check failed, continuing: {e}", file=sys.stderr)

    if result is None:
        try:
            token, email = google_token_and_email()
        except Exception as e:  # never include the token in messages
            return fail(f"cannot get Google token: {e}")

        cfg = {"token": token, "email": email, "base": args.base, "env": args.env, "project": args.project,
               "host": args.host, "agent": args.agent, "modelHint": args.model_hint,
               "prompt": args.prompt.replace("{run}", run_id), "expect": args.expect,
               "timeoutSec": args.timeout, "dist": dist, "keep": args.keep, "headed": args.headed,
               "outDir": str(out_dir), "runId": run_id, "pwModule": pw.resolve().as_uri()}
        try:
            result = run_node(node, cfg, args.timeout + 300)
        except Exception as e:
            result = {"status": "error", "reason": f"failed to run node: {e}"}

        # Collect job records for the chat (and cancel leftovers)
        if db is not None and result.get("chatId"):
            try:
                rep = db.job_report(result["chatId"], cancel=result.get("status") != "green")
                result["jobs"] = rep["jobs"]
            except Exception as e:
                result["jobsError"] = str(e)

            jobs = result.get("jobs") or []
            # 0.2.1: the job must go to the host asked for (CO Web routing may move it)
            wrong = [j for j in jobs if j.get("hostId") and j["hostId"] != args.host]
            if wrong:
                result["status"] = "red"
                result["reason"] = (f"job went to {wrong[0]['hostId']}, not {args.host}; "
                                    + str(result.get("reason", "")))
            # A red run whose job was never claimed may be caused by host saturation
            if (result.get("status") == "red" and jobs
                    and all(j.get("claimedAfterSec") is None for j in jobs)):
                try:
                    job_host = jobs[0].get("hostId") or args.host
                    ok, busy, limit = db.wait_for_slot(job_host, 0, log)
                    if not ok and not wrong:
                        result["status"] = "error"
                        result["busy"] = True
                        result["reason"] = (f"{job_host} became busy ({busy}/{limit}); job not claimed: "
                                            + str(result.get("reason", "")))
                except Exception:
                    pass

    code = {"green": 0, "red": 1}.get(result.get("status"), 2)
    result["runId"] = run_id
    result["host"] = args.host
    if host_note:
        result["hostChoice"] = host_note
    result["exitCode"] = code
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
        with open(out_dir / "runs.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps(result, ensure_ascii=False) + "\n")
    except OSError as e:
        print(f"aiwb-e2e: warning: cannot write runs.jsonl: {e}", file=sys.stderr)

    if args.json:
        print(json.dumps(result, ensure_ascii=False))
    else:
        print_human(result, dist is not None)
        for j in result.get("jobs") or []:
            print(job_line(j))
    return code


if __name__ == "__main__":
    sys.exit(main())
