import json
import os
import re
import subprocess
import threading
import time
from datetime import datetime, timedelta, timezone


class EngieClient:
    _WRAP_START = re.compile(r"^<untrusted_content[^>]*>")
    _WRAP_END = re.compile(r"^</untrusted_content[^>]*>$")
    _FRAC = re.compile(r"(\.\d{6})\d+")

    def __init__(self, mcp_config_path, ledger_path):
        with open(mcp_config_path, encoding="utf-8") as f:
            cfg = json.load(f)
        servers = cfg.get("mcpServers") or {}
        if not servers:
            raise ValueError(f"no mcpServers in {mcp_config_path}")
        self.server_name, srv = next(iter(servers.items()))
        self.command = srv["command"]
        self.args = [str(a) for a in srv.get("args", [])]
        self.env = srv.get("env")
        i = self.args.index("--job") if "--job" in self.args else -1
        self.job = self.args[i + 1] if 0 <= i < len(self.args) - 1 else None
        self.ledger_path = ledger_path
        self._lock = threading.Lock()  # one call at a time
        self._wlock = threading.Lock()  # stdin writes
        self._cond = threading.Condition()
        self._responses = {}
        self._next_id = 0
        self._consumed = set()  # (utc, model) ledger entries already used
        self._proc = None
        self._start()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    # --- process / transport ---

    def _start(self):
        env = {**os.environ, **{k: str(v) for k, v in self.env.items()}} if self.env else None
        self._proc = subprocess.Popen(
            [self.command, *self.args],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", errors="replace", bufsize=1, env=env,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        threading.Thread(target=self._read_loop, args=(self._proc,), daemon=True).start()
        try:
            resp = self._request("initialize", {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "aiwb-modelbench", "version": "1.0"},
            }, timeout=30)
            if resp is None:
                raise RuntimeError("initialize timed out")
            if resp.get("error"):
                raise RuntimeError(f"initialize failed: {resp['error']}")
            self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except Exception:
            self.close()
            raise

    def _alive(self):
        return self._proc is not None and self._proc.poll() is None

    def _restart(self):
        self.close()
        self._start()

    def _read_loop(self, proc):
        try:
            for line in proc.stdout:
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except ValueError:
                    continue  # ignore non-JSON output
                if not isinstance(msg, dict) or "id" not in msg:
                    continue
                if "method" in msg:
                    # server-initiated request: answer ping, reject others
                    reply = {"jsonrpc": "2.0", "id": msg["id"]}
                    if msg["method"] == "ping":
                        reply["result"] = {}
                    else:
                        reply["error"] = {"code": -32601, "message": "method not found"}
                    try:
                        self._send(reply, proc)
                    except (OSError, ValueError):
                        pass
                    continue
                with self._cond:
                    self._responses[msg["id"]] = msg
                    self._cond.notify_all()
        except (OSError, ValueError):
            pass
        finally:
            with self._cond:
                self._cond.notify_all()

    def _send(self, msg, proc=None):
        proc = proc or self._proc
        if proc is None:
            raise RuntimeError("server not running")
        data = json.dumps(msg, ensure_ascii=False) + "\n"
        with self._wlock:
            proc.stdin.write(data)
            proc.stdin.flush()

    def _request(self, method, params, timeout):
        # returns response dict, None on timeout; raises if the process dies
        self._next_id += 1
        rid = self._next_id
        proc = self._proc
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params}, proc)
        deadline = time.monotonic() + timeout
        with self._cond:
            while rid not in self._responses:
                left = deadline - time.monotonic()
                if left <= 0:
                    return None
                if proc.poll() is not None:
                    raise RuntimeError(f"server exited (code {proc.returncode})")
                self._cond.wait(min(left, 0.5))
            return self._responses.pop(rid)

    # --- public API ---

    def ask(self, model, prompt, web_search=False, timeout=150):
        with self._lock:
            t0 = time.monotonic()
            start_utc = datetime.now(timezone.utc)
            arguments = {"model": model, "prompt": prompt}
            if web_search:
                arguments["web_search"] = True
            params = {"name": "engie_chat", "arguments": arguments}
            resp, err = None, None
            for attempt in range(2):  # restart dead process once
                try:
                    if not self._alive():
                        self._restart()
                    left = max(1.0, timeout - (time.monotonic() - t0))
                    resp, err = self._request("tools/call", params, left), None
                    break
                except (OSError, ValueError, RuntimeError) as e:
                    err = f"{type(e).__name__}: {e}"
            secs = round(time.monotonic() - t0, 3)

            if resp is None:
                if err:
                    return self._res("error", "", secs, error=err)
                return self._res("timeout", "", secs, error=f"no response within {timeout}s")
            if resp.get("error"):
                e = resp["error"]
                msg = e.get("message", str(e)) if isinstance(e, dict) else str(e)
                return self._res("error", msg, secs, error=msg)

            result = resp.get("result") or {}
            text = "\n".join(
                c.get("text", "") for c in result.get("content", [])
                if isinstance(c, dict) and c.get("text") is not None
            )
            text = self._unwrap(text)
            if result.get("isError") or text.startswith("Error "):
                return self._res("error", text, secs, error=text or "tool error")
            tin, tout = self._tokens(model, start_utc)
            return self._res("ok", text, secs, tin, tout)

    def close(self):
        proc, self._proc = self._proc, None
        if proc is None:
            return
        try:
            proc.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                pass
        with self._cond:
            self._cond.notify_all()

    # --- helpers ---

    @staticmethod
    def _res(status, answer, seconds, tokens_in=None, tokens_out=None, error=None):
        return {"status": status, "answer": answer, "seconds": seconds,
                "tokens_in": tokens_in, "tokens_out": tokens_out,
                "cost_usd": None, "error": error}

    @classmethod
    def _unwrap(cls, text):
        lines = text.strip().splitlines()
        if lines and cls._WRAP_START.match(lines[0].strip()):
            lines = lines[1:]
            if lines and lines[0].lstrip().startswith("The text below"):
                lines = lines[1:]
            if lines and cls._WRAP_END.match(lines[-1].strip()):
                lines = lines[:-1]
        return "\n".join(lines).strip()

    @classmethod
    def _parse_utc(cls, s):
        s = cls._FRAC.sub(r"\1", s.strip()).replace("Z", "+00:00")
        dt = datetime.fromisoformat(s)
        return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)

    @staticmethod
    def _int(v):
        try:
            return int(v)
        except (TypeError, ValueError):
            return None

    def _tokens(self, model, start_utc):
        if not self.job or not self.ledger_path:
            return None, None
        cutoff = start_utc - timedelta(seconds=2)
        deadline = time.monotonic() + 5
        while True:
            hit = self._scan_ledger(model, cutoff)
            if hit:
                return hit
            if time.monotonic() >= deadline:
                return None, None
            time.sleep(0.25)

    def _scan_ledger(self, model, cutoff):
        try:
            with open(self.ledger_path, encoding="utf-8", errors="replace") as f:
                lines = f.readlines()
        except OSError:
            return None
        for line in lines:
            try:
                rec = json.loads(line)
            except ValueError:
                continue  # partial or malformed line
            if not isinstance(rec, dict):
                continue
            if rec.get("job") != self.job or rec.get("model") != model or rec.get("kind") != "engie":
                continue
            utc = rec.get("utc")
            try:
                if self._parse_utc(utc) < cutoff:
                    continue
            except (TypeError, ValueError, AttributeError):
                continue
            key = (utc, model)
            if key in self._consumed:
                continue
            self._consumed.add(key)
            return self._int(rec.get("input")), self._int(rec.get("output"))
        return None

import json
import os
import shutil
import subprocess
import time


def _cli_result(status="error", answer="", seconds=0.0, tokens_in=None,
                tokens_out=None, cost_usd=None, error=None):
    return {"status": status, "answer": answer, "seconds": round(seconds, 3),
            "tokens_in": tokens_in, "tokens_out": tokens_out,
            "cost_usd": cost_usd, "error": error}


def _cli_insert(args, extra):
    # Insert before a trailing stdin marker "-", else append
    if args and args[-1] == "-":
        return args[:-1] + list(extra) + ["-"]
    return args + list(extra)


def _cli_resolve_exe(exe):
    exe = os.path.expandvars(os.path.expanduser(str(exe)))
    if os.path.isabs(exe):
        return exe
    found = shutil.which(exe)
    if not found:
        raise FileNotFoundError(f"executable not found: {exe}")
    return found


def _cli_build_cmd(agent, model, mode, bridge_config_path, web):
    with open(bridge_config_path, encoding="utf-8-sig") as f:
        cfg = json.load(f)
    a = cfg["agents"][agent]
    m = (a.get("modes") or {}).get(mode) if mode else None
    if isinstance(m, dict) and "arguments" in m:
        args = [str(x) for x in m["arguments"]]
    else:
        args = [str(x) for x in (a.get("arguments") or [])]
    if model:
        margs = [str(x).replace("{value}", str(model)) for x in (a.get("modelArguments") or [])]
        args = _cli_insert(args, margs)
    if agent == "claude":
        out, i = [], 0
        while i < len(args):
            x = args[i]
            if x == "--verbose":
                i += 1
                continue
            if x == "--output-format" and i + 1 < len(args) and args[i + 1] == "stream-json":
                out += ["--output-format", "json"]
                i += 2
                continue
            out.append(x)
            i += 1
        args = out
        if not web:
            args += ["--disallowedTools", "WebSearch,WebFetch"]
    elif agent == "codex":
        if not web:
            args = _cli_insert(args, ["-c", 'web_search="disabled"'])
    else:
        raise ValueError(f"unsupported agent: {agent}")
    return [_cli_resolve_exe(a["executable"])] + args


def _cli_kill(proc):
    # Kill the whole process tree (npm .cmd shims spawn child processes)
    try:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                       capture_output=True, timeout=30,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except Exception:
        pass
    try:
        proc.kill()
    except Exception:
        pass


def _cli_int(v):
    try:
        return int(v) if v is not None else None
    except (TypeError, ValueError):
        return None


def _cli_parse_claude(stdout):
    # Returns (answer, tokens_in, tokens_out, cost, error)
    obj = None
    try:
        obj = json.loads(stdout.strip())
    except ValueError:
        pass
    if not isinstance(obj, dict):
        obj = None
        for line in reversed(stdout.splitlines()):
            try:
                o = json.loads(line)
            except ValueError:
                continue
            if isinstance(o, dict) and o.get("type") == "result":
                obj = o
                break
    if obj is None:
        return "", None, None, None, "no result JSON in output"
    answer = obj.get("result")
    answer = answer if isinstance(answer, str) else ("" if answer is None else str(answer))
    usage = obj.get("usage") if isinstance(obj.get("usage"), dict) else None
    tin = tout = None
    if usage is not None:
        tin = sum(_cli_int(usage.get(k)) or 0 for k in
                  ("input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"))
        tout = _cli_int(usage.get("output_tokens"))
    cost = obj.get("total_cost_usd")
    try:
        cost = float(cost) if cost is not None else None
    except (TypeError, ValueError):
        cost = None
    err = None
    if obj.get("is_error"):
        err = answer or str(obj.get("subtype") or "claude reported an error")
    return answer, tin, tout, cost, err


def _cli_parse_codex(stdout):
    # Returns (answer, tokens_in, tokens_out, cost, error)
    answer, usage, err = "", None, None
    for line in stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            ev = json.loads(line)
        except ValueError:
            continue
        if not isinstance(ev, dict):
            continue
        t = ev.get("type")
        if t == "item.completed":
            item = ev.get("item") or {}
            if isinstance(item, dict) and item.get("type") == "agent_message":
                answer = str(item.get("text") or "")
        elif t == "turn.completed":
            if isinstance(ev.get("usage"), dict):
                usage = ev["usage"]
        elif t == "error":
            err = str(ev.get("message") or "codex error")
        elif t == "turn.failed":
            e = ev.get("error")
            err = str((e.get("message") if isinstance(e, dict) else e) or "turn failed")
    tin = tout = None
    if usage is not None:
        tin = _cli_int(usage.get("input_tokens"))
        tout = _cli_int(usage.get("output_tokens"))
        r = _cli_int(usage.get("reasoning_output_tokens"))
        if r is not None:
            tout = (tout or 0) + r
    return answer, tin, tout, None, err


def run_cli(agent, model, prompt, mode, bridge_config_path, workdir, timeout=900, web=True):
    t0 = time.monotonic()
    try:
        cmd = _cli_build_cmd(agent, model, mode, bridge_config_path, web)
        proc = subprocess.Popen(cmd, cwd=workdir, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            out_b, err_b = proc.communicate((prompt or "").encode("utf-8"), timeout=timeout)
        except subprocess.TimeoutExpired:
            _cli_kill(proc)
            try:
                proc.communicate(timeout=10)
            except Exception:
                pass
            return _cli_result("timeout", seconds=time.monotonic() - t0,
                               error=f"timeout after {timeout}s")
        secs = time.monotonic() - t0
        stdout = (out_b or b"").decode("utf-8", errors="replace")
        stderr = (err_b or b"").decode("utf-8", errors="replace")
        parser = _cli_parse_claude if agent == "claude" else _cli_parse_codex
        answer, tin, tout, cost, perr = parser(stdout)
        status, error = ("error", perr) if perr else ("ok", None)
        if not answer and proc.returncode != 0:
            status = "error"
            tail = stderr.strip()[-500:]
            error = perr or f"exit code {proc.returncode}: {tail}"
        elif agent == "claude" and perr == "no result JSON in output":
            error = f"{perr}; stderr: {stderr.strip()[-500:]}"
        return _cli_result(status, answer, secs, tin, tout, cost, error)
    except Exception as e:
        return _cli_result("error", seconds=time.monotonic() - t0,
                           error=f"{type(e).__name__}: {e}")