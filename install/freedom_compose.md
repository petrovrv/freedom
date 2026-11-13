# A workbench = Freedom core + modules + data (F10, SPEC §3.6)

The reverse task of the module stage: an existing AI Workbench installation must be exactly the Freedom core, a set of
modules (standard ones from `modules/`, the owner's private ones from elsewhere) and its data. Nothing of a feature
may stay hidden in the core, and no tool or service may run outside a module or the core list.

## Check

    python install/freedom_compose.py [--offline] [--inventory FILE] [--json]

It lists

- **the core** — this source tree outside `modules/` and the core tools (`release/freedom-export.json` → `tools`,
  plus `coreTools` of the inventory);
- **the modules at home** — folders with `module.json` in `modules/` of the tree, in `install.json` → `modules.dirs`
  and in `AIWB_MODULE_DIRS`; for each: version, origin (tree / private), trust (standard / own / unsigned), the tools
  it lists, whether the server has it installed and enabled (`modules_host list`, skipped with `--offline`) and
  whether the tool library records its tools (`toolbox modules`);
- **the private layer** — `install.json`, the workbench folder (context, projects), the host key in the keyring,
  the owner's release scripts excluded from the export (presence only, never values).

and checks the inventory (default `release/freedom-compose.json`; it is not exported because it names the owner's
private modules):

| Check | A gap is |
|---|---|
| features | a non-core feature of SPEC §3.7 without a module folder, or a tool the feature needs that its module does not list |
| tools | a library tool that is neither a core tool nor listed by any module (orphan) |
| principle 1 | a file or code that SPEC §3.7 moves out of the core still in the core (`coreMustNot`) |
| private layer | a missing part of it |

Modules that exist as folders but are not installed or not enabled are printed as **to do** (with the install
command), not as gaps. Exit code 0 = no gaps, 2 = gaps.

## Modules of tools and services (manifest fields, ext/1)

Used by `admin-checks`, `mail-sheets`, `community` and the private modules; `install/freedom_modules.py` checks them.

| Field | Meaning |
|---|---|
| `provides.tools` | `[{name, minVersion}]` (or plain names): tools of the library the module owns. Install takes a tool from the module's `tools/<name>/` or from `tools/` of the distribution (`toolbox.py install --from`) when it is missing or older, fails before anything else changes when a tool is missing or below `minVersion`, and records the tools as owned by the module (`toolbox.py module <id> --state enabled\|disabled\|removed`). A tool whose only owners are switched off is refused by the tool server. Remove leaves the tool files in the library. |
| `provides.services` | `[{id, kind: daemon\|schedule, cron \| every, tool \| cwd, run, windowsTask}]`. `kind schedule` needs `cron` (5 fields, the same syntax as Bridge schedules) or `every` (ISO-8601 duration: `PT15M`, `PT1H`, `P1D`); `tool` runs `<tool> <run>` from the library, `cwd` (inside `home/`) runs `run` there. Bridge's module loader will supervise them; until it does, `windowsTask` (under `\AIWorkbench\`) is the existing Windows task that enable/disable switch on and off — a schedule is only re-enabled, never started at enable. |
| `provides.secrets` | `[{name, account, why}]`: keyring service names (`aiwb/<name>[/<part>]`) the module needs, shown in the install plan. Names only — values stay in the keyring (`secret-prompt`). |
| `home/`, `tools/` | code of the home PC: signed with the module, never uploaded to the server. |

`python install/freedom_modules.py sync` makes the home PC follow the switches on the server (the owner may switch a
module in CO Web → Host → Modules): tool states and services of every module whose state changed. Bridge's module
loader can call it on its tick; until then run it after switching a module in CO Web.

## The owner's private modules

Modules for particular sites, countries and personal affairs are not part of Freedom. Keep them **outside the source
repository** (e.g. `C:\AIWorkbench\private-modules\`, a local git repository), add the folder to `install.json` →
`modules.dirs`, and sign them with an own key:

    python install/freedom_release.py keygen --name owner        # key in keyring aiwb/freedom-release, prints the public key
    # install.json: "modules": {"dirs": ["C:\\AIWorkbench\\private-modules"], "trustedKeys": [{"name": "owner", "key": "<hex>"}]}
    python install/freedom_modules.py sign <private module> --name owner

Outside the repository is the safest place: the export takes `git archive` of the source tree, so a private module can
never reach it, and the export check never sees private ids. An own key gives the trust level "own": the module
installs without `--allow-untrusted` but never counts as standard, so it gets no server code.

## Moving an existing installation

1. Merge the core with the modules moved out (`modules/<id>`), deploy the core.
2. For each module the check lists under "to do": `python install/freedom_modules.py install <folder> --enable`
   (modules that had data in the core move it with their install hook, e.g. `geo-presence`).
3. `python install/freedom_compose.py` until it prints no gaps.
