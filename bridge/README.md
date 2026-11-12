# BridgeAgent v0.8.0

Windows tray bridge between Google Drive transport and local Claude Code / Codex CLI.

## Proven baseline

Before v0.5 both providers completed live end-to-end tests on the deployment host:

```text
ChatGPT -> Drive -> Bridge -> provider CLI -> workspace -> artifact -> Drive -> ChatGPT
```

Cyrillic filenames/content, UTF-8 stdin/stdout, structured completion, provider session IDs and SHA-256 artifact export were verified.

## v0.5 / v0.5.1 additions

- Protocol v3 while retaining v2 request compatibility.
- `conversation.operation = new | resume | fork`.
- Codex native resume and fork.
- Claude native resume.
- verified input attachments staged under `.co-attachments/<jobId>`.
- `cancel/<jobId>.cancel` remote cancellation.
- `owner.json` advertises bridge version and capabilities for the web UI.
- example Codex profile corrected to the deployment-tested `codex exec --json --approve-for-me -`.
- native `codex.exe` / `claude.exe` invocation; no `cmd.exe /c` wrapper in the example config.

## v0.6.0 additions

- Protocol v3 minor 1 (`protocolMinor: 1`); v3 and v2 requests remain valid.
- `mode = chat | cowork`: chat runs in a per-chat scratch folder under Drive, cowork in the project workspace.
- `model` and `effort` selected from a local catalog; Codex models are read from `models_cache.json`.
- Auto-export of new and modified workspace files as artifacts (limits, `.coignore`).
- `result.json` reports `mode`, `model`, `effort`, `modelReported`, `usage`, `exportSkipped`.
- `owner.json` publishes the agent catalog and project list (no local paths).
- Workspaces may live on Google Drive; `workspaceDriveRelativePath` lets the web UI browse them.

## Build

```bat
build.cmd
```

The release still targets the inbox .NET Framework compiler and needs no IDE/SDK when the framework `csc.exe` is present.

## Upgrade

A v0.5 `config.json` runs unchanged: projects without `allowedModes` accept only `cowork`, agents without `modes` use their base `arguments`, requests without `model`/`effort` use the CLI defaults, and auto-export is off unless `project.autoExport` or `request.output.autoExport` enables it.

To use the v0.6 features, compare `config.json` with `config.json.example`: add `modes`, `modelArguments`, `effortArguments`, `models` or `modelCatalogFile`, `efforts` per agent and `allowedModes`, `autoExport` per project.

Codex profiles in the example use `--skip-git-repo-check` and `requiresGitRepository: false`, so that document workspaces on Google Drive work without Git. Keep `requiresGitRepository: true` in a mode profile when code projects must be Git work trees.

Restart BridgeAgent after config changes. Rollback: stop BridgeAgent, start the v0.5.1 executable with its own config.

## Protocol

See `PROTOCOL.md` and `TZ-CO-WEB.md`.

## Trust model

See `AUDIT-CONTEXT.md`. The Drive inbox is intentionally an owner-controlled command channel; this is a design requirement, not an accidental remote-execution exposure.
