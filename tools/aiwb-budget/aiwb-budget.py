"""Token budget governor for scheduled AI Workbench campaigns.

Reads the subscription usage that Bridge writes to transport/hosts/<host>/limits.json
and decides whether a scheduled dispatcher may start more agent jobs, so that the
weekly limit is spent evenly: by the end of budget day k of the week the used share
must not exceed k/7 (unused budget of earlier days carries over).
"""
import argparse
import datetime as dt
import json
import math
import os
import sys
from zoneinfo import ZoneInfo

DEFAULT_ROOT = r"G:\My Drive\AI-Workbench\transport\hosts"
WEEK = dt.timedelta(days=7)
DAY = dt.timedelta(days=1)


def parse_utc(s):
    s = s.rstrip("Z")
    if "." in s:
        head, frac = s.split(".", 1)
        s = head + "." + frac[:6]
    return dt.datetime.fromisoformat(s).replace(tzinfo=dt.timezone.utc)


def read_week_window(root, agent, now):
    """Freshest still-valid weekly window of the agent over all hosts."""
    best = None
    for host in sorted(os.listdir(root)):
        path = os.path.join(root, host, "limits.json")
        if not os.path.isfile(path):
            continue
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            continue
        a = (data.get("agents") or {}).get(agent) or {}
        observed = a.get("observedUtc")
        for w in a.get("windows") or []:
            if w.get("windowMinutes") != 10080 or not w.get("resetsAtUtc") or observed is None:
                continue
            reset = parse_utc(w["resetsAtUtc"])
            if reset <= now:
                continue
            cand = {"host": host, "observed": parse_utc(observed), "reset": reset,
                    "used": float(w.get("usedPercent") or 0), "status": a.get("status")}
            if best is None or cand["observed"] > best["observed"]:
                best = cand
    return best


def in_night(local_hour, start, end):
    return (local_hour >= start or local_hour < end) if start > end else (start <= local_hour < end)


def check(args):
    now = dt.datetime.now(dt.timezone.utc)
    w = read_week_window(args.root, args.agent, now)
    if w is None:
        out = {"ok": False, "allowSpawn": 0, "reason": f"no valid weekly window for {args.agent} in limits.json"}
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 2
    start = w["reset"] - WEEK
    day_k = min(7, int((now - start) / DAY) + 1)
    day_end = start + day_k * DAY
    ceiling = 100.0 * day_k / 7
    tz = ZoneInfo(args.timezone)
    local = now.astimezone(tz)
    night = in_night(local.hour, args.night_start, args.night_end)
    if not night and args.day_share < 1:
        # daytime may use only this share of today's 1/7, the rest is left for the night
        ceiling -= (1 - args.day_share) * 100.0 / 7
    headroom = ceiling - args.reserve - w["used"]
    max_par = args.night_parallel if night else args.day_parallel
    age_min = (now - w["observed"]).total_seconds() / 60
    by_budget = max(0, math.floor(headroom / args.job_cost)) if args.job_cost > 0 else max_par
    allow = max(0, min(max_par - args.running, by_budget))
    reason = []
    if w["status"] not in (None, "allowed"):
        allow, reason = 0, reason + [f"status {w['status']}"]
    if age_min > args.stale_minutes:
        reason.append(f"limits {age_min:.0f} min old")
        if args.strict_stale:
            allow = 0
    if headroom < args.job_cost:
        reason.append(f"headroom {headroom:.1f}% < job cost {args.job_cost}%")
    if args.running >= max_par:
        reason.append(f"{args.running} running >= max {max_par} ({'night' if night else 'day'})")
    out = {
        "ok": True, "agent": args.agent, "host": w["host"],
        "observedLocal": w["observed"].astimezone(tz).strftime("%Y-%m-%d %H:%M"),
        "limitsAgeMin": round(age_min),
        "usedPercent": w["used"],
        "weekStartLocal": start.astimezone(tz).strftime("%Y-%m-%d %H:%M"),
        "budgetDay": day_k,
        "budgetDayEndsLocal": day_end.astimezone(tz).strftime("%Y-%m-%d %H:%M"),
        "ceilingPercent": round(ceiling, 1),
        "reservePercent": args.reserve,
        "headroomPercent": round(headroom, 1),
        "paceNowPercent": round(100.0 * (now - start) / WEEK, 1),
        "mode": "night" if night else "day",
        "maxParallel": max_par, "running": args.running, "jobCostPercent": args.job_cost,
        "allowSpawn": allow,
        "reason": "; ".join(reason) or "ok",
    }
    if args.log:
        os.makedirs(os.path.dirname(os.path.abspath(args.log)), exist_ok=True)
        with open(args.log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"utc": now.strftime("%Y-%m-%dT%H:%M:%SZ"), "used": w["used"],
                                "day": day_k, "weekStart": start.strftime("%Y-%m-%dT%H:%MZ"),
                                "running": args.running, "allow": allow, "note": args.note}) + "\n")
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 0


def history(args):
    rows = []
    with open(args.log, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    days = {}
    for r in rows:
        key = (r["weekStart"], r["day"])
        d = days.setdefault(key, {"first": r["used"], "last": r["used"], "ticks": 0})
        d["last"] = r["used"]
        d["ticks"] += 1
    prev_last = {}
    print("week start        day  used at start  used at end  spent  ticks")
    for (ws, k), d in sorted(days.items()):
        base = prev_last.get(ws, d["first"])
        print(f"{ws}  {k:>3}  {base:>13.1f}  {d['last']:>11.1f}  {d['last'] - base:>5.1f}  {d['ticks']:>5}")
        prev_last[ws] = d["last"]
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check", help="may the dispatcher start jobs now? prints JSON")
    c.add_argument("--agent", default="claude", help="key in limits.json (claude, codex)")
    c.add_argument("--root", default=DEFAULT_ROOT, help="transport/hosts folder")
    c.add_argument("--running", type=int, default=0, help="campaign jobs running now")
    c.add_argument("--reserve", type=float, default=3.0, help="percent kept free for the owner's own chats")
    c.add_argument("--job-cost", type=float, default=2.0, help="estimated weekly percent per job")
    c.add_argument("--day-parallel", type=int, default=1)
    c.add_argument("--night-parallel", type=int, default=3)
    c.add_argument("--day-share", type=float, default=1.0,
                   help="share of today's 1/7 usable in day mode (e.g. 0.5 keeps half for the night)")
    c.add_argument("--night-start", type=int, default=23, help="local hour")
    c.add_argument("--night-end", type=int, default=8, help="local hour")
    c.add_argument("--timezone", default="Europe/Brussels")
    c.add_argument("--stale-minutes", type=int, default=180)
    c.add_argument("--strict-stale", action="store_true", help="no spawns when limits.json is stale")
    c.add_argument("--log", help="append a snapshot line to this JSONL file")
    c.add_argument("--note", default="", help="free text stored in the log line")
    h = sub.add_parser("history", help="spend per budget day from a --log file")
    h.add_argument("--log", required=True)
    args = p.parse_args()
    return check(args) if args.cmd == "check" else history(args)


if __name__ == "__main__":
    sys.exit(main())
