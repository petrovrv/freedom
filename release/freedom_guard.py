# SPDX-License-Identifier: AGPL-3.0-or-later
"""Freedom guard: nothing personal reaches the public repository (docs/DEVELOPMENT.md).

Runs release/export_freedom.py guard on the tree being published and on every new commit (added lines, new file
names, message, author and committer). Called by the git pre-push hook (release/hooks/pre-push, enabled with
`git config core.hooksPath release/hooks`) and by devtree finish before a merge (.devtree.json "guard").

  python release/freedom_guard.py pre-push REMOTE URL < "<local ref> <local sha> <remote ref> <remote sha>" lines
  (a new branch on REMOTE: the commits its tracking refs do not have; an unnamed URL: the whole history)
  python release/freedom_guard.py range A..B [--rev REV]

A maintainer's clone names the private parts in its local git config (never in the tree):
  git config freedom.terms        FILE   private words; with it a missing file is an error, not a skip
  git config freedom.ownerConfig  FILE   the owner's part of the export config (private files of the owner's tree)
Exit codes: 0 clean, 2 findings (push or merge refused), 1 error.
"""
from __future__ import annotations

import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import export_freedom as ef  # noqa: E402

ZERO = "0" * 40


def git_config(repo: pathlib.Path, key: str) -> str:
    r = subprocess.run(["git", "config", "--get", key], cwd=repo, capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else ""


def private_args(repo: pathlib.Path) -> list[str]:
    args = []
    terms = git_config(repo, "freedom.terms")
    if terms:
        args += ["--terms", terms, "--require-terms"]
    owner = git_config(repo, "freedom.ownerConfig")
    if owner:
        args += ["--owner-config", owner]
    return args


def run_guard(repo: pathlib.Path, rev: str, rng: str | None) -> int:
    argv = ["guard", "--repo", str(repo), "--rev", rev] + (["--range", rng] if rng else []) + private_args(repo)
    return ef.main(argv)


def pre_push(repo: pathlib.Path, lines: list[str], remote: str = "") -> int:
    worst = 0
    remotes = subprocess.run(["git", "remote"], cwd=repo, capture_output=True, text=True).stdout.split()
    for line in lines:
        parts = line.split()
        if len(parts) != 4:
            continue
        local_ref, local_sha, remote_ref, remote_sha = parts
        if local_sha == ZERO:  # deleting a remote branch publishes nothing
            continue
        if remote_sha == ZERO:
            # a new branch on the remote: every commit that the tracking refs of THIS remote do not have yet (other
            # remotes may be private); pushing to a URL that is no configured remote checks the whole history
            rng = f"{local_sha} --not --remotes={remote}" if remote in remotes else local_sha
        else:
            rng = f"{remote_sha}..{local_sha}"
        print(f"freedom guard: {local_ref} -> {remote_ref}", file=sys.stderr)
        rc = run_guard(repo, local_sha, rng)
        if rc:
            print(f"freedom guard: push of {local_ref} refused (exit {rc}); see the findings above", file=sys.stderr)
        worst = max(worst, rc)
    return worst


def main(argv: list[str]) -> int:
    repo = pathlib.Path(subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                                       check=True).stdout.strip())
    if argv[:1] == ["pre-push"]:
        return pre_push(repo, sys.stdin.read().splitlines(), argv[1] if len(argv) > 1 else "")
    if argv[:1] == ["range"] and len(argv) >= 2:
        rev = argv[argv.index("--rev") + 1] if "--rev" in argv else "HEAD"
        return run_guard(repo, rev, argv[1])
    print(__doc__, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
