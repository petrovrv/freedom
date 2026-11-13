# Developing Freedom

This repository is the **upstream of the Freedom core**. Its history is public history: every commit, its message,
author and every line it ever added are published for good. Maintainers develop here directly; their own
workbenches are built from this tree plus a private layer that never enters it.

## Layers of a workbench

| Layer | Where | In this repository |
|---|---|---|
| Core | a clone of this repository (the maintainer's main tree, e.g. `<root>\freedom`) | yes |
| Standard modules | `modules/` | yes |
| Core tools | `tools/` (refreshed from the maintainer's tool library, see below) | yes |
| Private modules | own folder (`install.json` → `modules.dirs`, own signing key in `modules.trustedKeys`) | **never** |
| Owner overlay | private folder: own release/deploy scripts with personal hosts, own build identity (bundle id prefix, installer publisher), own inventory for `install/freedom_compose.py`, the guard's private parts | **never** |
| Settings | `install.json` (names, hosts, members of one installation) | **never** |
| Data and secrets | server database and state, home state, OS keyring (`aiwb/<service>`) | **never** |

A workbench = core + modules + private modules + overlay + settings + data. Updating a workbench is updating the
core clone; the private layers are untouched.

## How a change flows

1. `devtree start NAME` — a worktree with branch `agent/NAME` of the main tree (devtree finds the main tree in
   `devtree.json`, see `tools/devtree/SKILL.md`). One agent, one worktree; never edit the main tree directly.
2. Change and test in the worktree. Tests of the part you touch: `bridge\tests\run-tests.cmd`,
   `dotnet test co-desktop/tests/CO.Core.Tests`, `npm test` and `npm run build` in `co-web`, PHP tests in
   `co-agent/tests`, `python -m pytest install/tests release/tests`, `python install/freedom_compose.py --offline`.
3. `devtree finish NAME -m "<component> <version>: <what>"` — in this order: protected paths
   (`protected-paths.txt`, a maintainer's explicit yes), cross-review by another model, `aiwb-e2e` for changes of
   `bridge/`, `co-web/`, `co-web-auth/`, `co-agent/`, `co-desktop/`, rebase, **guard** (`.devtree.json`), fast-forward
   of `main`, push.
4. Every push passes the guard again (`release/hooks/pre-push`; `devtree` sets `core.hooksPath release/hooks`), and
   CI runs the build and the generic part of the guard on every push and pull request.
5. A maintainer deploys to their own installation from their main tree plus their overlay (an unpublished local
   branch `owner` = `main` + one overlay commit, rebuilt from the overlay on every deploy). Releases of Bridge and
   CO Desktop and the server deploy run from that branch, never from a dirty tree.

## The guard: nothing personal is published

`release/export_freedom.py guard` (called by `release/freedom_guard.py`) checks

- the tree being merged or pushed: generic rules of `release/freedom-export.json` (hosting account paths, user
  profile paths, tailnet names, IBANs, private keys, API keys and tokens, e-mail addresses outside example domains,
  employer brands outside the corporate module), module names in core code (SPEC §3.6 principle 1);
- **every new commit one by one**: added lines, new file names, the message, the author and the committer — a line
  added and removed again still lives in the history;
- the private parts of a maintainer, named in the clone's local git config, never in the tree:
  `git config freedom.terms <file>` (private words: names of people, own domains, account ids, machine names; with
  this key a missing file is an error) and `git config freedom.ownerConfig <file>` (files of the maintainer's own
  tree that must never appear here).

A refusal (exit 2; devtree exit 7) stops the merge or the push. The commits are not public yet: rewrite them
(`git commit --amend`, `git rebase`) — a new commit that removes the text is not enough. `--force` does not bypass
the guard.

Commit identity: a neutral author name and a GitHub noreply or `example.org` address (`git config user.name`,
`user.email` in the clone). Commit messages name components and versions, not people; approvals of protected paths
are logged locally by devtree, not in the message.

## Versions and changelogs

- Each component keeps its own version where it is today: Bridge `BridgeVersion` (`bridge/BridgeAgent.cs`),
  CO Desktop and CO Resident (`DeviceRuntime.Version` and the `.csproj`), CO Web (`co-web/package.json`), the
  server schema (`co-agent/db/schema.sql`, `meta.schema`), each module (`module.json` `version`, re-signed after
  every change). The version is bumped in the commit that changes the component.
- `CHANGELOG.md` at the root is the changelog of all components: the commit adds a line under **Unreleased**
  (`- Bridge 1.18.0: ...`).
- A Freedom release `x.y.z`: move **Unreleased** under `## x.y.z (date)`, tag `vx.y.z`, build the source archive
  and the signed manifest (`install/freedom_release.py build/sign/verify --strict`), GitHub release with both.
  Installations update their core clone to the tag.

## Core tools

`tools/` holds the core tools and the tools that standard modules list in `provides.tools`. They are developed in
the maintainer's tool library (`toolbox`, its own repository); `python release/export_freedom.py tools --into
<worktree>` refreshes `tools/` of a worktree from the library (with the same checks), and the change goes through
`devtree finish` like any other. Tools written for one person or one employer stay out (`toolsExclude`).

## Moving from a private tree

A maintainer who developed in a private tree first exports it once more
(`release/export_freedom.py sync --into <clone>`: one normal commit on the clone, checked by the guard, not pushed),
then switches devtree's main tree to the clone. The private tree stays read-only as an archive; its history is
never pushed here.

Limits of the guard (a reviewer still reads what is published): it reads text files line by line in UTF-8, so
UTF-16 text, metadata inside binary files (PDF, images, Office files) and patterns spanning lines are not seen; a
push to a URL that is no configured remote checks the whole history and can take long. The guard's own files are
protected paths: a change to them needs a maintainer's explicit yes.
