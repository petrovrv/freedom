# SPDX-License-Identifier: AGPL-3.0-or-later
"""Social and volunteer budget shares of the week (SPEC §6.2 default 3 %, §7.1 default 10 %).

The weekly window and the overall pace come from aiwb-budget (limits.json of Bridge). The share spent on the forum is
kept in the archive ledger per budget week: a tick may run when aiwb-budget allows a job of that cost now AND the
category's spend this week plus the tick cost stays within its share.
"""
from __future__ import annotations

import datetime as dt
import json
import subprocess
import sys

from . import config


def aiwb_budget_provider(agent: str = "claude", job_cost: float = 0.3, timeout: int = 60):
    """Callable returning the aiwb-budget `check` JSON."""
    def call():
        script = config.tools_dir() / "aiwb-budget" / "aiwb-budget.py"
        cmd = [sys.executable, str(script), "check", "--agent", agent, "--job-cost", str(job_cost), "--reserve", "0"]
        try:
            out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=timeout)
            return json.loads(out.stdout)
        except (OSError, ValueError, subprocess.SubprocessError) as e:
            return {"ok": False, "allowSpawn": 0, "reason": f"aiwb-budget not available: {e}"}
    return call


def _week_start(info: dict) -> int:
    ws = info.get("weekStartLocal")
    if ws:
        try:
            return int(dt.datetime.strptime(ws, "%Y-%m-%d %H:%M").timestamp())
        except ValueError:
            pass
    today = dt.date.today()
    monday = today - dt.timedelta(days=today.weekday())
    return int(dt.datetime.combine(monday, dt.time()).timestamp())


class ShareBudget:
    def __init__(self, archive, category: str, share_percent: float, cost_percent: float, provider):
        self.archive, self.category = archive, category
        self.share, self.cost, self.provider = float(share_percent), float(cost_percent), provider
        self.info: dict = {}
        self.week = 0

    def check(self) -> dict:
        self.info = self.provider() or {}
        self.week = _week_start(self.info)
        spent = self.archive.spent(self.category, self.week)
        out = {"category": self.category, "sharePercent": self.share, "spentPercent": round(spent, 3),
               "costPercent": self.cost, "weekStart": self.week, "allowed": False}
        if not self.info.get("ok"):
            out["reason"] = "no weekly window: " + str(self.info.get("reason", "aiwb-budget gave no answer"))
            return out
        headroom = self.info.get("headroomPercent")
        if headroom is not None and headroom < self.cost:
            out["reason"] = f"week pace: headroom {headroom}% < cost {self.cost}%"
            return out
        if spent + self.cost > self.share + 1e-9:
            out["reason"] = f"{self.category} share used: {spent:.2f}% + {self.cost}% > {self.share}%"
            return out
        out["allowed"] = True
        out["reason"] = "ok"
        return out

    def charge(self, note: str = "", percent: float | None = None):
        self.archive.spend(self.category, self.week or _week_start(self.info), self.cost if percent is None else percent, note)
