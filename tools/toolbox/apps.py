"""Actions of apps for the agent (toolbox 1.8.0, Freedom F14b-1, SPEC §3.6a item 5): the MCP tools app_list and
app_action of the aiwb-tools server. An app (a Freedom module) describes some of its server ops as actions; the agent
calls them through db.php host ops app_list / app_action with the id of its job (AIWB_JOB_ID). The server takes the
environment, the chat and the rights from the job's row, never from this caller: the action runs as the person of the
job's environment would run it from the app, and the open pages of the app refresh. An action that acts outside the
workbench (outward) is not run: toolbox 1.9.0 (F14b-2) gets back a pending request, a card in "Waiting for you" that the
person runs or declines, and app_action_status tells its outcome. stdlib only.
"""
import base64
from datetime import datetime, timezone
import gzip
import importlib.util
import json
import os
from pathlib import Path
import urllib.error
import urllib.request

UA = "aiwb-tools-apps/1.11"
TIMEOUT = 40


def _install_get(key):
    """One value of install.json (dotted key): AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json."""
    path = os.environ.get("AIWB_INSTALL") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "AIWorkbench", "install.json")
    try:
        with open(path, encoding="utf-8-sig") as f:
            v = json.load(f)
    except (OSError, ValueError):
        return None
    for part in key.split("."):
        v = v.get(part) if isinstance(v, dict) else None
    return v


def _endpoint():
    if os.environ.get("AIWB_DB_URL"):
        return os.environ["AIWB_DB_URL"]
    url = _install_get("site.url")
    if not url:
        raise RuntimeError("site unknown: set AIWB_DB_URL or site.url in install.json")
    return str(url).rstrip("/") + "/agent/db.php"


def _key():
    """Host key of db.php: a file on the site (AIWB_DB_KEY_FILE), keyring aiwb/co-push on the home PC."""
    path = os.environ.get("AIWB_DB_KEY_FILE")
    if path:
        with open(path, encoding="utf-8") as f:
            return f.read().strip()
    import keyring
    key = keyring.get_password("aiwb/co-push", "default")
    if not key:
        raise RuntimeError("host key aiwb/co-push not found in keyring")
    return key


def _call(op, params):
    job = os.environ.get("AIWB_JOB_ID", "")
    if not job:
        return None, "no AIWB_JOB_ID: app actions are available only inside an AI Workbench agent job"
    raw = gzip.compress(json.dumps(dict(params, op=op, jobId=job), ensure_ascii=False).encode("utf-8"))
    body = json.dumps({"gz": base64.b64encode(raw).decode("ascii")}).encode("ascii")
    req = urllib.request.Request(_endpoint(), data=body, method="POST", headers={
        "X-CO-Push-Key": _key(), "Content-Type": "application/json", "User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
            text = r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:500]
        try:
            detail = json.loads(detail).get("error") or detail
        except (ValueError, AttributeError):
            pass
        return None, "%s refused (HTTP %d): %s" % (op, e.code, detail)
    except urllib.error.URLError as e:
        return None, "%s: the site is not reachable (%s)" % (op, e.reason)
    try:
        return json.loads(text), None
    except ValueError:
        return None, "%s: not JSON from the site: %s" % (op, text[:300])


def app_list(args):
    res, err = _call("app_list", {})
    if err:
        return err, True
    if res.get("restricted"):
        return res.get("note") or "app actions are not available to this job", False
    apps = res.get("apps") or []
    if not apps:
        return "no app with actions is on in this environment", False
    return json.dumps(apps, ensure_ascii=False, indent=1), False


def app_action(args):
    app, action, params = args.get("app"), args.get("action"), args.get("args", {})
    if not isinstance(app, str) or not app or not isinstance(action, str) or not action:
        return "'app' and 'action' (strings from app_list) are required", True
    if not isinstance(params, dict):
        return "'args' must be an object (see the action's args schema in app_list)", True
    # Discover the route before sending any arguments: relay plaintext must never
    # be sent to the legacy endpoint, including when discovery fails.
    listing, err = _call("app_list", {})
    if err:
        return err, True
    if not isinstance(listing, dict) or listing.get("restricted"):
        return "app actions are not available to this job", True
    descriptor = next((a for item in listing.get("apps", []) if item.get("app") == app
                       for a in item.get("actions", []) if a.get("op") == action), None)
    if descriptor is None:
        return "unknown app action (see app_list)", True
    via = descriptor.get("via")
    if via == "home-relay":
        if type(descriptor.get("outward")) is not bool:
            return "invalid relay action descriptor", True
        return _relay_action(app, action, params, outward=descriptor["outward"])
    if via is not None:
        return "unsupported app action transport", True
    res, err = _call("app_action", {"app": app, "action": action, "args": params})
    if err:
        return err, True
    if res.get("pending"):
        # outward: not an error and not done - the person decides on a card
        return json.dumps({k: res.get(k) for k in ("status", "requestId", "expiresUtc", "message")}, ensure_ascii=False, indent=1), False
    if not res.get("ok"):
        msg = res.get("message") or (res.get("result") or {}).get("error") or res.get("error") or "refused"
        return "%s/%s: %s" % (app, action, msg), True
    return json.dumps(res.get("result"), ensure_ascii=False, indent=1), False


def _relay_client():
    """Load the installed module only; never download code or use a server path."""
    root = os.environ.get("AIWB_MODULES_DIR")
    if not root:
        # Same default as install/freedom_modules.py local_root on each OS.
        local = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
        root = str(Path(local) / "AIWorkbench" / "modules")
    path = Path(root) / "home-relay" / "home" / "relay_client.py"
    if not path.is_file():
        raise RuntimeError("install the updated home-relay module on this executor")
    spec = importlib.util.spec_from_file_location("aiwb_apps_relay_client", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.RelayClient


def _relay_action(app, action, params, *, outward=False):
    status, err = _call("app_relay_status", {"app": app, "action": action})
    if err:
        return err, True
    if not isinstance(status, dict) or status.get("ok") is not True:
        return "relay status refused", True
    relay = status.get("relay")
    if (status.get("via") != "home-relay" or status.get("app") != app
            or status.get("action") != action or status.get("outward") is not outward
            or not isinstance(status.get("env"), str) or not status["env"]
            or not isinstance(status.get("version"), str) or not status["version"]
            or not isinstance(relay, dict) or relay.get("online") is not True
            or relay.get("served") is not True):
        return "relay status is unavailable or does not match this action", True
    if outward and (type(status.get("approvalProtocol")) is not int or status["approvalProtocol"] != 1):
        return "relay confirmation protocol unavailable; no action was submitted", True
    client = None
    envelope = None
    try:
        client = _relay_client()(status["env"], app, relay.get("pub"))
        options = {"app_version": status["version"]}
        if outward:
            options["approval"] = {"phase": "stage"}
        envelope = client.seal(action, params, **options)
        wire_envelope = {key: envelope[key] for key in ("id", "pk", "iv", "ct")}
        response, err = _call("app_relay_propose" if outward else "app_relay_call", {"app": app, "action": action,
                             "version": status["version"], "envelope": wire_envelope})
        if err:
            # No retries: an ambiguous timeout may follow a successful action.
            return "relay transport failed; outcome unknown; no automatic retry", True
        if not isinstance(response, dict) or response.get("ok") is not True:
            return "relay request refused", True
        result = client.open(envelope["id"], response.get("envelope"))
        if outward:
            if (not isinstance(result, dict) or not isinstance(result.get("requestId"), str)
                    or not result["requestId"] or result["requestId"] != response.get("requestId")
                    or not isinstance(result.get("revision"), str) or not result["revision"]
                    or result["revision"] != response.get("revision") or response.get("pending") is not True
                    or type(result.get("expires")) is not int or not 0 < result["expires"] < 253402300799):
                return "relay proposal acknowledgement invalid; no automatic retry", True
            return json.dumps({"status": "pending", "requestId": result["requestId"],
                               "expiresUtc": datetime.fromtimestamp(result["expires"], timezone.utc).isoformat().replace('+00:00', 'Z'),
                               "message": "Private proposal stored; waits for the person's preview and Run."},
                              ensure_ascii=False, indent=1), False
        return json.dumps(result, ensure_ascii=False, indent=1), False
    except (ImportError, OSError, RuntimeError, ValueError, TypeError):
        # Avoid exposing decrypted content, response bodies or private arguments
        # in exception messages (including dependency/import errors).
        return "relay client unavailable or encrypted response invalid; no automatic retry", True
    finally:
        if client is not None and envelope is not None:
            client.discard(envelope["id"])


def app_action_status(args):
    rid = args.get("requestId")
    if not isinstance(rid, str) or not rid:
        return "'requestId' (from app_action) is required", True
    res, err = _call("app_action_status", {"requestId": rid})
    if err:
        return err, True
    if not res.get("ok"):
        return res.get("message") or res.get("error") or "refused", True
    if res.get("via") is not None:
        if res.get("via") != "home-relay":
            return "unsupported app action transport", True
        return _relay_result(rid, res)
    res.pop("ok", None)
    return json.dumps(res, ensure_ascii=False, indent=1), False


def _relay_result(rid, status):
    """Read durable outcome using a fresh key, including from a later agent job."""
    if status.get("requestId") != rid:
        return "relay status does not match this request", True
    if status.get("status") in ("denied", "expired"):
        return json.dumps({"requestId": rid, "status": status["status"]}), False
    relay = status.get("relay")
    if (any(not isinstance(status.get(k), str) or not status[k]
            for k in ("env", "app", "action", "version", "revision"))
            or type(status.get("approvalProtocol")) is not int or status["approvalProtocol"] != 1
            or not isinstance(relay, dict) or relay.get("online") is not True or relay.get("served") is not True):
        return "relay result unavailable; no execution retry", True
    client = envelope = None
    try:
        client = _relay_client()(status["env"], status["app"], relay.get("pub"))
        envelope = client.seal(status["action"], {}, app_version=status["version"],
                               approval={"phase": "result", "requestId": rid, "revision": status["revision"]})
        response, err = _call("app_relay_result", {"requestId": rid,
                              "envelope": {k: envelope[k] for k in ("id", "pk", "iv", "ct")}})
        if err or not isinstance(response, dict) or response.get("ok") is not True:
            return "relay result transport failed; no execution retry", True
        result = client.open(envelope["id"], response.get("envelope"))
        if (not isinstance(result, dict) or result.get("requestId") != rid
                or result.get("revision") != status["revision"]
                or result.get("state") not in ("pending", "executing", "done", "failed", "expired")):
            return "relay result acknowledgement invalid; no execution retry", True
        output = {"requestId": rid, "status": result["state"]}
        if result["state"] in ("done", "failed"):
            output["result"] = result.get("result")
        if result["state"] == "executing":
            output["message"] = "Execution claimed; outcome may be unknown. Do not resubmit the action."
        return json.dumps(output, ensure_ascii=False, indent=1), False
    except (ImportError, OSError, RuntimeError, ValueError, TypeError):
        return "relay result unavailable or encrypted response invalid; no execution retry", True
    finally:
        if client is not None and envelope is not None:
            client.discard(envelope["id"])


TOOLS = [
    {"name": "app_list", "description": "List the apps of this AI Workbench environment that offer actions to the agent "
     "(Freedom modules: Home, Calendar, ...) with each action's description, argument schema and outward flag. "
     "Call it before app_action.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "app_action", "description": "Run one action of an app as the person of this job's environment would run it "
     "from the app (same rights). An action marked outward (it acts outside the workbench) does not run now: it becomes "
     "a card in the person's \"Waiting for you\" and the answer is status pending with a requestId; tell the person and "
     "check app_action_status later. The open pages of the app refresh after an action.",
     "inputSchema": {"type": "object", "properties": {
         "app": {"type": "string", "description": "app id from app_list, e.g. smart-home"},
         "action": {"type": "string", "description": "action op from app_list, e.g. home_state"},
         "args": {"type": "object", "description": "arguments by the action's schema"}},
         "required": ["app", "action"]}},
    {"name": "app_action_status", "description": "Outcome of an outward app action that waits for the person: "
     "pending, running, done (with the app's result), failed, denied (the person declined) or expired.",
     "inputSchema": {"type": "object", "properties": {
         "requestId": {"type": "string", "description": "requestId from app_action"}}, "required": ["requestId"]}},
]
HANDLERS = {"app_list": app_list, "app_action": app_action, "app_action_status": app_action_status}
