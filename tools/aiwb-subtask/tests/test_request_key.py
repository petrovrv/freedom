# SPDX-License-Identifier: AGPL-3.0-or-later
"""0.6.1 (review S5-02): the same spawn in the same job has one requestKey; a lost answer is repeated with that key.
python aiwb-subtask/tests/test_request_key.py  (no network: db_api is replaced)"""
import importlib.util, os, pathlib, sys, types, urllib.error

spec = importlib.util.spec_from_file_location("st", pathlib.Path(__file__).resolve().parents[1] / "aiwb-subtask.py")
st = importlib.util.module_from_spec(spec)
spec.loader.exec_module(st)
st.time.sleep = lambda s: None
fail = 0


def ok(name, cond):
    global fail
    fail += 0 if cond else 1
    print(("ok   " if cond else "FAIL ") + name)


os.environ["AIWB_JOB_ID"] = "JOB-1"
pl = {"parentChatId": "CHAT-20261111000000-AAAA", "resourceId": "home-main/claude", "prompt": "x", "mode": "cowork", "timeoutMinutes": 120}
a = types.SimpleNamespace(again=False)
k1, k2 = st.request_key(pl, a), st.request_key(dict(pl), a)
ok("same spawn, same key", k1 == k2 and len(k1) <= 64 and k1.startswith("s-"))
ok("other prompt, other key", st.request_key(dict(pl, prompt="y"), a) != k1)
os.environ["AIWB_JOB_ID"] = "JOB-2"
ok("other calling job, other key", st.request_key(pl, a) != k1)
ok("--again: a new key each time", st.request_key(pl, types.SimpleNamespace(again=True)) != st.request_key(pl, types.SimpleNamespace(again=True)))

seen = []
answers = [urllib.error.URLError("timed out"), RuntimeError("db.php subtask_spawn: HTTP 504: gateway"),
           RuntimeError("db.php subtask_spawn: in_progress"), {"jobId": "JOB-X", "repeated": True}]


def fake(payload, timeout=60):
    seen.append(payload["requestKey"])
    x = answers.pop(0)
    if isinstance(x, Exception):
        raise x
    return x


st.db_api = fake
res = st.spawn_with_key(dict(pl, requestKey="s-k"))
ok("timeout, 5xx and in_progress repeated with the same key", res["jobId"] == "JOB-X" and seen == ["s-k"] * 4)
answers[:] = [RuntimeError("db.php subtask_spawn: slave_not_allowed")]
try:
    st.spawn_with_key(dict(pl, requestKey="s-k"))
    ok("a refusal is not repeated", False)
except RuntimeError:
    ok("a refusal is not repeated", len(seen) == 5)
sys.exit(1 if fail else 0)
