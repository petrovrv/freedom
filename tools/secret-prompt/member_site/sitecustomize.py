"""Bridge 1.18+ puts this folder on PYTHONPATH of a household member's job (AIWB_JOB_ROLE=member): every python of the
job selects the refusing keyring backend even if PYTHON_KEYRING_BACKEND was dropped on the way (Codex filters
environment names containing KEY/SECRET/TOKEN from its shell). No import, only an environment variable."""
import os

if (os.environ.get("AIWB_JOB_ROLE") or "").strip().lower() == "member":
    os.environ["PYTHON_KEYRING_BACKEND"] = "aiwb_secret.MemberRefusedKeyring"
