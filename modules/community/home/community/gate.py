# SPDX-License-Identifier: AGPL-3.0-or-later
"""Owner-only actions (SPEC §1.5).

Inside an AI Workbench agent job these need the owner's own yes through read-guard:
  * approving any draft whose kind is not in the owner's auto-publish list (proposals, pledges, claims always);
  * `policy`, `settings` changes (filterAllow, relays, shares, dossier ...), `decide`, `--no-dossier`.

"Inside a job" = Bridge's job variables AIWB_JOB_ID / AIWB_CHAT_ID (read-guard's signal) OR a claude/codex process
among the ancestors of this process (read_guard._ancestor_agent_pid, Windows). At a plain terminal the owner acts.

Flow: the first attempt is held, read-guard raises a Yes/No card for the owner ("<code> да"); after the yes the agent
runs `read-guard.py grant <code>` (read-guard checks the yes is the owner's own chat message) and repeats the command.

Fail closed: no read-guard in a job, an exception, or read-guard answering "no decision" without an active grant
(its guard_check_action returns None on internal errors) all refuse.

Inherent limits (stated plainly): a process that strips the job variables AND does not run under a claude/codex
ancestor (or on a system where the ancestor check is unavailable) is treated as the owner; redirecting
LOCALAPPDATA (read-guard's state) or AIWB_COMMUNITY_DIR changes where grants and settings are read; one owner grant
opens every gated action of that chat for 15 minutes (read-guard's grant is per chat, not per action); editing
community.json or the archive by hand is outside this check. read-guard's own hooks and the owner's review are the
second line.
"""
from __future__ import annotations

import os

from . import guard

JOB_VARS = ("AIWB_JOB_ID", "AIWB_CHAT_ID")


class OwnerConfirmRequired(PermissionError):
    def __init__(self, message: str, code: str | None = None):
        super().__init__(message)
        self.code = code


def _ancestor_agent(rg) -> bool:
    try:
        return bool(rg is not None and rg._ancestor_agent_pid())
    except Exception:  # noqa: BLE001 - the check is best effort; job variables remain the main signal
        return False


def in_agent_job(environ=None, rg=None) -> bool:
    env = os.environ if environ is None else environ
    if any(env.get(v) for v in JOB_VARS):
        return True
    return _ancestor_agent(guard._RG if rg is None else rg)


def require_owner(action: str, environ=None, rg=None) -> None:
    """Return when the owner may be assumed or has granted; raise OwnerConfirmRequired otherwise."""
    rg = guard._RG if rg is None else rg
    if not in_agent_job(environ, rg):
        return
    if rg is None:
        raise OwnerConfirmRequired(f"{action}: needs the owner's yes, and read-guard is not available in this job")
    try:
        if rg.grant_active(rg.scope_key()):
            return
        held = rg.guard_check_action(f"Freedom community: {action}", tool="community", kind="publish", always=True,
                                     any_kind=True, reason="needs the owner's own yes (Freedom: publishing, budget "
                                                           "commitment or filter/policy change)")
    except Exception as e:  # noqa: BLE001 - fail closed
        raise OwnerConfirmRequired(f"{action}: read-guard failed ({type(e).__name__}); refusing") from None
    if held:
        raise OwnerConfirmRequired(held.get("error") or f"{action}: owner confirmation required",
                                   held.get("confirmCode"))
    # no grant and no hold: read-guard swallowed an internal error (it fails open) - we fail closed
    raise OwnerConfirmRequired(f"{action}: read-guard gave no decision (internal error, e.g. state folder not "
                               "writable); refusing")


def allow_all(action: str) -> None:
    """Gate for tests and simulations where approve() *is* the owner's yes."""
    return None


def default(action: str) -> None:
    require_owner(action)
