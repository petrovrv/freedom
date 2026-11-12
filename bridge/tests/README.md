# Bridge agent runtime tests

`tests\run-tests.cmd` builds `AgentRuntimeTests.exe` from the Bridge sources and the test sources and compares the
frozen 1.3.5 code (`tests\Legacy135.cs`) with the 1.4.0 runtimes. No agent CLI is started.

## What it checks

- Stream snapshots: each sample file is fed to the Raw, Claude and Codex parsers; Codex also runs with a usage baseline.
  A sidecar `<name>.expected.json` (sessionId, finalResponse from `result.json`) is compared informationally.
- Command lines for every agent of the given `config.json` (and `config.json.example`) over a matrix: modes,
  new/resume/fork/branch, permissions, context folders, model, effort, cloud-denied tools.
- Device MCP arguments and the MCP config file.

`tests\samples` holds 4 synthetic streams. The harness writes content-addressed subagent hook files into
`%LOCALAPPDATA%\AIWorkbenchBridge\subagent-hooks`.

## How to run

Synthetic samples only:

    tests\run-tests.cmd

Real streams:

    python tests\export-db-events.py <out.jsonl>
    python tests\collect-samples.py --out <dir> --job-dir <transport\hosts\home-main\completed> --job-dir <...\failed> --job-dir <%LOCALAPPDATA%\AIWorkbenchBridge\jobs> --db-export <out.jsonl> --config <config.json>
    tests\run-tests.cmd --samples <dir> --config <config.json>

`export-db-events.py` exports database `job_events` of the last 30 days via `hz.py`. `collect-samples.py` gathers
`stdout.log` of completed and failed transport jobs, local jobs and the database export. Place `<dir>` outside the
repository: it contains chat content. A run on 524 real streams takes about 150 s.

## Exit codes

- `0`: all comparisons equal
- `1`: at least one difference
- `2`: usage error

## Adding a new CLI agent

1. Add an `AgentRuntime` subclass in `BridgeAgentRuntimes.cs`; set `Protocol` to the new `outputProtocol` value.
2. Override `BuildArguments` hooks and other members as required by the CLI.
3. Add an `AgentStreamParser` subclass in `BridgeAgentParsers.cs` and return it from `CreateParser`.
4. Add an agent entry with `"outputProtocol"` to `config.json`. Registration is automatic through reflection.
5. Add sample streams of the new runtime to `tests\samples` and run `tests\run-tests.cmd`. `Legacy135.cs` knows only
   the 1.3.5 protocols, so for a new protocol write direct assertions instead of legacy comparisons.
