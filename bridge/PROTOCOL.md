# Bridge transport protocol v3 (minor 1)

## Scope

Protocol v3 extends the proven v2 transport with the four primitives required by the CO site:

1. provider session `resume`;
2. Codex native `fork`;
3. input attachments;
4. remote cancellation.

v0.5 still accepts v2 jobs. New web clients must emit `schemaVersion: 3`.

## Host transport

```text
AI-Workbench/
  transport/
    hosts/<hostId>/
      owner.json
      inbox/
      claimed/
      events/
      completed/
      failed/
      rejected/
      cancel/
```

`owner.json` includes `bridgeVersion` and a capability list so the web UI can disable unsupported controls.

## Request package

```text
inbox/<jobId>/
  request.json
  attachments/              optional
    <relative paths>
  request.ready
```

Producer order is strictly:

```text
attachments -> request.json -> request.ready
```

Drive may expose entries out of order. Bridge therefore verifies every declared attachment before claiming the directory. A missing or temporarily mismatching attachment keeps the job in `inbox` for a later scan.

## request.json v3

```json
{
  "schemaVersion": 3,
  "hostId": "home-main",
  "jobId": "JOB-01...",
  "environmentId": "anna",
  "projectId": "PRJ-...",
  "chatId": "CHAT-...",
  "branchId": "BR-root",
  "agent": "codex",
  "instruction": "Continue the implementation.",
  "conversation": {
    "operation": "resume",
    "sessionId": "01a0d891-..."
  },
  "attachments": [
    {
      "path": "requirements/spec.pdf",
      "sizeBytes": 123456,
      "sha256": "...64 hex..."
    }
  ],
  "artifactPaths": ["dist/report.pdf"],
  "timeoutMinutes": 30
}
```

`conversation.operation`:

```text
new       create provider session
resume    continue provider sessionId
fork      fork provider sessionId when provider supports native fork
```

Provider mapping in v0.5:

```text
Codex new:    codex exec --json --approve-for-me -
Codex resume: codex exec resume --json <sessionId> -
Codex fork:   codex exec fork --json <sessionId> -
Claude new:   claude -p ... --output-format stream-json ...
Claude resume: base arguments + --resume <sessionId>
Claude fork:  no native v0.5 implementation; web layer starts a new session from branch context
```

The Codex resume/fork syntax above was obtained from `codex exec resume --help` and `codex exec fork --help` on the actual deployment host on 2026-09-25.

## Attachments

Attachments are data, never executable parameters. Their paths must be relative and remain under `attachments/` in the request package.

Before the agent starts, Bridge copies them into:

```text
<workspace>/.co-attachments/<jobId>/...
```

Bridge verifies size and SHA-256 both before claim and after staging. The generated prompt tells the agent where files are staged. The staging directory is deleted after the job.

Since Bridge 1.6.0 (capability `chat-files`) an attachment may carry `target`, a relative path inside the chat scratch
(CO Web 0.55.0). When the job works in the chat scratch, Bridge writes the file to `<scratch>/<target>` (replacing an
existing file, which is then the user's new version of that chat file; the older one stays in the Drive revision history)
and does not delete it after the job. An identical existing file is left untouched. Attachments without `target`, or jobs
whose workspace is not the chat scratch, are staged under `.co-attachments/<jobId>/` as before.

A web client should preserve original display names in its chat event metadata. Protocol `path` is only the safe transport-relative path.

## Cancellation

Client creates:

```text
transport/hosts/<hostId>/cancel/<jobId>.cancel
```

The marker is idempotent. For a running job Bridge sets cancellation state and terminates its Windows Job Object. Terminal result uses:

```json
"status": "cancelled"
```

In v0.5 cancelled results physically finalize through the non-completed terminal bucket; UI must use `result.status`, not folder name, as semantic status.

## Events

Event batching is unchanged from v2. Additional event types:

```text
attachment_staged
cancel_requested
cancelled
```

UI polls `events/<jobId>/` and renders provider JSONL as typed tool/activity cards.

## Result v3

Adds:

```json
"conversationOperation": "resume"
```

and retains provider `sessionId`. A successful fork normally returns a new provider session/thread ID; the chat branch must bind to the returned ID rather than the parent ID.

## Backward compatibility

BridgeAgent v0.5 accepts v2 requests as `conversation.operation=new` with no attachments.

## Security/trust model

The owner-controlled Drive inbox remains an intentional remote command channel. A request cannot select an executable, arbitrary working directory or raw shell command. Agent and project remain local allowlists. Attachments are content and may contain prompt injection; this is inherent in an agent intentionally asked to inspect those files and is part of the documented trust model in `AUDIT-CONTEXT.md`.


## Minor 1 (BridgeAgent v0.6)

All fields are optional. A request without them behaves as v3 minor 0.

```json
{
  "schemaVersion": 3,
  "protocolMinor": 1,
  "mode": "chat",
  "model": "opus",
  "effort": "high",
  "output": { "autoExport": true, "maxFiles": 20, "maxBytes": 52428800 }
}
```

| Field | Rule |
|---|---|
| `mode` | `chat` or `cowork`; default `cowork`; must be in `project.allowedModes` and in the agent `modes` |
| `model` | id from `owner.json` `agents.<agent>.models[].id`; omitted = CLI default |
| `effort` | id from the model `efforts`, else from the agent `efforts`; omitted = CLI default |
| `output.autoExport` | overrides `project.autoExport` |
| `output.maxFiles`, `output.maxBytes` | lower the host limits; cannot raise them |

Working directory:

```text
cowork  project workspace from config.json
chat    driveRoot/projects/<projectId>/chats/<chatId>/scratch/   (created by Bridge, persists across turns)
```

Provider sessions are bound to their working directory. The web client resumes a session only with the same agent and mode; a mode switch starts a new session with a context package.

### result.json additions

```json
{
  "mode": "chat",
  "model": "sonnet",
  "effort": "low",
  "modelReported": "claude-sonnet-5",
  "autoExport": true,
  "usage": {
    "inputTokens": 4, "cachedInputTokens": 57187, "cacheCreationInputTokens": 11473,
    "outputTokens": 199, "reasoningOutputTokens": 0,
    "costUsd": 0.0603, "durationMs": 3383, "numTurns": 2
  },
  "artifacts": [ { "sourceRelativePath": "hello.md", "driveRelativePath": "...", "sizeBytes": 60, "sha256": "...", "origin": "auto" } ],
  "exportSkipped": [ { "path": "big.zip", "reason": "file larger than 104857600 bytes" } ]
}
```

`origin` is `declared` for `artifactPaths` and `auto` for auto-export. Missing declared artifacts still fail the job; auto-export problems go to `exportSkipped` only.

Since Bridge 1.6.0 every artifact also has `md5`. When the workspace lies under `driveRoot` (the chat scratch, a project
folder on Drive), Bridge copies nothing: the record has `"ref": true` and `driveRelativePath` is the live file itself.
Drive for desktop uploads each change as a new revision, so a message finds its version by `md5` among the Drive
revisions of that file (Drive keeps unpinned revisions 30 days). A workspace outside Drive is copied to
`artifacts/<jobId>/` as before (`"ref": false`).

`costUsd` is reported by Claude Code; Codex reports tokens only (`costUsd`, `durationMs`, `numTurns` are `null`).

### Events

```text
artifact              exported file (declared or auto)
artifact_skipped      auto-export skipped a file (path: reason)
auto_export_disabled  snapshot not possible (entry limit, unreadable .coignore)
```

### owner.json additions

```json
{
  "protocolMinor": 1,
  "capabilities": ["...", "modes", "model-select", "effort-select", "auto-export", "usage", "project-catalog"],
  "agents": {
    "claude": {
      "displayName": "Claude",
      "modes": ["chat", "cowork"],
      "models": [ { "id": "opus", "label": "Opus (latest)", "description": null, "efforts": null, "defaultEffort": null } ],
      "efforts": ["low", "medium", "high", "xhigh", "max"],
      "defaultModel": null,
      "defaultEffort": null
    }
  },
  "projects": {
    "bridge-test": {
      "environmentId": "anna",
      "allowedAgents": ["codex", "claude"],
      "allowedModes": ["chat", "cowork"],
      "autoExport": true,
      "workspaceDriveRelativePath": "projects/bridge-test/workspace"
    }
  }
}
```

### .coignore

Optional file in the workspace root. One pattern per line, `#` comments, `*` and `?` wildcards. A pattern without `/` matches a name at any depth; a pattern with `/` is anchored at the root; a trailing `/` marks a directory.

### Drive-only projects (BridgeAgent v0.6.1)

A `projectId` that is not configured in `projects` is accepted when `mode = chat` and `config.chatProjectEnvironments[environmentId]` exists; the agent must be in its `allowedAgents`. `owner.json` publishes the map as `chatProjectEnvironments`.

### Context folders (BridgeAgent v0.7)

```json
"contextFolders": [
  { "rootId": "home", "relativePath": "Documents/Work/LTO", "access": "read", "label": "LTO docs" }
]
```

`rootId` must be a key of `owner.json` `contextRoots`; `relativePath` uses `/`, empty means the whole root; at most `maxContextFolders` (default 10).

Folder browser data:

```text
transport/hosts/<hostId>/folders/<rootId>.json        index {tree:{n,c:[...],m}} m = more levels below
transport/hosts/<hostId>/folders/refresh.request      client asks for a rebuild
transport/hosts/<hostId>/folders/requests/<id>.json   {rootId, relativePath}
transport/hosts/<hostId>/folders/results/<id>.json    two levels below relativePath, or {error}
```

### Usage limits (BridgeAgent v0.8)

`transport/hosts/<hostId>/limits.json`:

```json
{ "schemaVersion": 1, "updatedUtc": "...", "agents": {
  "claude": { "source": "claude-code", "observedUtc": "...", "status": "allowed", "plan": null,
    "windows": [ { "id": "session", "key": "five_hour", "windowMinutes": 300, "usedPercent": 1, "resetsAtUtc": "..." },
                 { "id": "week", "key": "seven_day", "windowMinutes": 10080, "usedPercent": 4, "resetsAtUtc": "..." } ] },
  "codex": { "source": "codex-session-log", "plan": "plus", "windows": [ ... "primary" / "secondary" ... ] } } }
```

Values are the provider's own numbers at `observedUtc`; a window whose `resetsAtUtc` has passed is reset, its new value arrives with the next request.


## Minor 2 (BridgeAgent v1.0): device tools

Specification: TZ-CO-DESKTOP v1.2. All fields are optional; a request without them behaves as minor 1.

### request.json

```json
{
  "protocolMinor": 2,
  "devices": ["d-3f9a61c2b0e14d77"],
  "tainted": false,
  "task": { "taskId": "TASK-01", "parentRunId": "JOB-00", "budget": 0 }
}
```

- `devices`: at most 4 paired, non-revoked devices of the job's `environmentId`. Claude gets `--mcp-config` with one stdio server `dev_<short>` per device, `--allowedTools mcp__dev_<short>` and `--permission-prompt-tool mcp__dev_<short>__approve`; Codex gets `-c mcp_servers.dev_<short>.*`.
- `tainted`: the Run starts tainted (inherited from a parent Run, a handed-over Task or a ContextEpoch built from tainted events).
- `task.taskId`: device grants "for this Task" bind to it; default is the jobId.

### result.json

`tainted: true` when a device tool returned untrusted content during the Run. CO passes it on to child Runs.

### owner.json

```json
"capabilities": [..., "device-tools"],
"deviceCallKey": { "publicKey": "<base64 X||Y, P-256>", "fingerprint": "<sha256 hex of the 64 bytes>" },
"runAsWarning": null
```

### Device registry

```text
transport/hosts/<hostId>/devices/pending/<deviceId>.json   pairing request copied by Bridge from the device folder
transport/hosts/<hostId>/devices/<deviceId>.json           device record written by the owner's client
```

Device record: `{ schema: "co.device.v1", request: {deviceId, short, name, environmentId, account, deviceFolderId, slotIds, resultsFolderId, tools, signingKey, serviceAccountCert, ...}, serviceAccount, serviceAccountKeyId, pairedUtc, pairedBy, revoked }`.

### Device folder (created by CO Desktop in the user's Drive)

```text
CO-Device-<deviceId>/
  pairing-request.json    written by the device
  bridge.json             created empty by the device, filled by Bridge:
                          {state: pending|paired|revoked, hostId, projectId, ownerEmail,
                           bridgePublicKey, serviceAccount, serviceAccountKeyId}
  heartbeat.json          device, every 5 min
  calls/slot-00..07.json  created by the device; Bridge writes {"state":"call","call":{tool.call}}
  results/<callId>.json   device; Bridge sets appProperties.consumed = "true", device deletes
  audit/
```

### Pub/Sub

| Resource | Writer → reader |
|---|---|
| topic + subscription `co-<host>-dev-<deviceId>-calls` (retention 600 s) | Bridge → device service account |
| topic `co-<host>-dev-results`, subscription `co-<host>-dev-results-bridge` | device service accounts → Bridge |
| subscription `co-<host>-events-<env>-dev-<deviceId>` (TTL 7 days) | Bridge → device |

Attributes: `type` (`tool.call`, `tool.result`, `device.heartbeat`), `deviceId`, `callId`.

`device.heartbeat` data (every 30 s): `{type, deviceId, utc, version, channel, activeCalls, engie}`; `engie` (CO Desktop 0.7.13) is `online|signin|offline|unknown|off`. Bridge 1.4.1 publishes paired devices with their `online`/`engie` state in `owner.json` → `devices`.

`owner.json` (Bridge 1.4.2) also carries `state` (`running|paused|updating`), `updateTo` (target version while draining for an update), `runningJobs` and `stateSinceUtc`; it is refreshed every `hostLeaseSeconds/3` in every state, so a paused or draining Bridge is not reported as stale.

Bridge 1.16.0: an update drain that waits longer than `supervisor.updateDrainMinutes` (default 30) pauses the running jobs: their `result.json` has `status: cancelled`, `error` starting with `Paused for the Bridge update`, `pausedForUpdate` (target version) and `continuationJobId`, the id of the job Bridge queued in the same chat (request with `updateContinuationOf`, resuming the paused session) and runs first after the restart.

### tool.call / tool.result

```json
{ "v": 1, "callId": "call-<32 hex>", "runId": "<jobId>", "taskId": "...", "deviceId": "...", "tool": "fs_read",
  "argsJson": "{\"path\":\"work/a.txt\"}", "issuedUtc": "2026-09-26T12:00:00.000Z", "tainted": false,
  "timeoutMs": 120000, "sig": "<base64 P1363>" }

{ "v": 1, "callId": "...", "runId": "...", "deviceId": "...", "tool": "fs_read", "ok": true,
  "error": { "code": "...", "message": "..." }, "content": "...", "untrusted": true,
  "issuedUtc": "...", "sig": "<base64 P1363>" }
```

Signed bytes (UTF-8, fields joined by LF):

```text
call:   co.tool.call.v1 | callId | runId | taskId | deviceId | tool | sha256hex(argsJson) | issuedUtc | tainted 1/0
result: co.tool.result.v1 | callId | runId | deviceId | tool | ok 1/0 | sha256hex(code LF message, or "") |
        sha256hex(content) | untrusted 1/0 | issuedUtc
```

The device drops calls older than 5 minutes and any `callId` it has executed in the last 24 hours, so one call may be sent through Pub/Sub and a Drive slot.


## Database queue (BridgeAgent 1.3.0, CO Web 0.19.0)

Text-only jobs for a host that announces the `db-queue` capability go to the AI Workbench database instead of the Drive inbox. The request is the same `request.json` document.

| Step | Call (`<site>/agent/db.php`, JSON `{op, ...}`) | Auth |
|---|---|---|
| CO Web queues | `job_submit {request}` → row `origin=db`, `state=inbox`; 409 if the jobId exists | CO Web session / bearer, environment of the request |
| CO Web hints | Pub/Sub `job.ready` with data `{"jobId", "db": true}` | |
| Bridge claims | `job_claim {host, max}` → `{jobs:[{jobId, request}], cancel:[jobId]}`; `inbox → claimed` atomically; a claimed job without event batches for 120 s is offered again | `X-CO-Push-Key` |
| Bridge reports | `job_post_events {batches:[{jobId, batch}]}`, `job_result {host, request, result}`, `job_reject {jobId, reason}` | `X-CO-Push-Key` |
| CO Web follows | `job_get {jobId}` → `{state, status, origin, result, rejection, cancelRequested}`; `job_events {jobId, afterSeq}` → batches with `lastSequence > afterSeq` | session / bearer |
| CO Web cancels | `job_cancel {jobId}`: `inbox → cancelled` and the Drive inbox copy (fallback or attachment job) is trashed, otherwise `cancel_requested = 1` (Bridge stops the job at its next claim call) | session / bearer |
| Bridge runs a Drive inbox job (1.8.1) | `job_drive_claim {host, jobId}` → `{action: run\|skip, state}`: a queued row becomes `claimed`; `skip` for a cancelled or finished job (Bridge removes the copy); `run` for an unknown jobId; no answer: Bridge keeps the copy; a server fallback copy (file `db-fallback` next to `request.json`) waits for an answer, any other copy runs after 5 min of failures | `X-CO-Push-Key` |

Bodies that carry chat text are sent as `{"gz": base64(gzip(json))}` (hosting firewall). Bridge still writes `claimed/`, `events/`, `completed/` on G:, so Drive keeps a full copy. A jobId runs at most once: Bridge skips a database job whose id it already knows and drops a later Drive copy of a database job. Fallback: a database job no Bridge has claimed for 90 s is copied into the Drive inbox, unless the host's Bridge called `job_claim` within the last 60 s (meta `claim_poll_<host>`); a `cancelled` row keeps that state whatever result arrives later.

## Follow-ups (BridgeAgent 1.7.0, CO Web 0.60.0, schema 13)

A message sent while a job of the chat runs is a follow-up of that job, not a new job (`co-agent/web/agent/followup-lib.php`, table `job_followups`).

| Step | Call | Auth |
|---|---|---|
| CO Web adds | `followup_add {jobId, text, deliver}` → `{id, state}`; `deliver=true` (Claude on a host with capability `followups`) → `pending`, else `late`; 409 `job_done` if the job is over | session / bearer, write role on the job's chat |
| CO Web hints | Pub/Sub `job.ready {"jobId", "db": true}` (Bridge claims at once) | |
| Bridge takes | `job_claim {host, max, followups: true}` → also `followups:[{id, jobId, text}]` of its unfinished jobs; `pending → offered` (an unacknowledged one is offered again after 120 s) | `X-CO-Push-Key` |
| Bridge reports | `followup_ack {host, results:[{id, outcome}]}`, outcome `delivered` (written into Claude's stdin; the server writes a `user_message` `EVT-FU-<id>` with `followupOf: jobId` and no `jobId`, retried on the next `job_claim` if that write failed; Bridge remembers outcomes, so a re-offer after a lost ack is not written again), `late` (stdin closed), `retry` (back to `pending`) | `X-CO-Push-Key` |
| CO Web follows | `followup_list {chatId}` → open rows; `state` is `late` and `jobOver` true for rows of a finished job | session / bearer |
| CO Web sends late ones | after the job: `followup_take {chatId, ids}` → `taken` (`sending`, one device at a time), one normal message with their texts, then `followup_done {chatId, ids}` (`requeued`); unconfirmed `sending` rows are late again after 120 s | session / bearer |
| CO Web withdraws | `followup_cancel {chatId, id}` (pending or late only) | session / bearer |

Claude runs with `--input-format stream-json`: stdin line 1 is the prompt `{"type":"user","message":{"role":"user","content":...}}`, a follow-up is one more such line prefixed `[Follow-up from the user, sent while you were working on this request]`. Bridge closes stdin when the `result` event is seen.

## Web devices (BridgeAgent 1.5.0)

A CO Web tab (Chrome/Edge, File System Access API) acts as a file device. Full spec: `lab/web-device/SPEC.md`.
Transport is the AI Workbench database via `<site>/agent/db.php` (schema 11: tables `web_devices`,
`web_device_calls`). There is no Pub/Sub, no Drive and no P-256 signature (`sig` is absent). Trust is based on HTTPS
plus the browser session (the server checks ownership) and on the push key (Bridge). Requires `dbQueue.enabled` in `config.json`.

- Device id: `w-` + 16 lowercase hex. Short name: `web` + first 6 hex. MCP server: `dev_<short>`.

Bridge ops (header `X-CO-Push-Key`):

| Op | Request | Response |
|---|---|---|
| `webdev_list` | `{}` | `devices`: `{deviceId, short, name, userEmail, environmentId, folders, tools, lastSeenUtc, online}`. `online` = last seen < 60 s. |
| `webdev_call_put` | `{call}`, sent gzip-wrapped as `{"gz": ...}` | Inserts a queued row. |
| `webdev_call_get` | `{callIds}` | `{results:[{callId, state, result}]}` for done or expired rows. |

Registry:
- Bridge calls `webdev_list` every 30 s. It also calls it right before validating a job whose `devices` names a `w-` id.
- Entries are merged into the device registry as `kind=web`. A web device missing from the list counts as revoked.
- Entries with an invalid id, short name, environment or tool name are ignored.
- Tools `approve` and `ask_user` are dropped from a web catalog. Every other tool of `tools` is passed through as is,
  including `fs_delete` and `fs_move` (SPEC section 12), which the browser always confirms.

Validation:
- Same rules as desktop devices: max 4 devices per Run, environment must equal the job's `environmentId`, short names
  must be unique. No service account is used.
- `job_submit` on the server also checks that every `w-` device belongs to the submitter (403 `device_not_owned`).

Calls:
- The envelope is identical to `tool.call` without `sig`: `v`, `callId`, `runId`, `taskId`, `deviceId`, `tool`,
  `argsJson`, `issuedUtc`, `tainted`, `timeoutMs`.
- `timeoutMs` = `devices.callTimeoutSeconds` (default 120 s). `fs_write`, `fs_edit`, `fs_mkdir`, `fs_delete` and `fs_move`
  get +120 s for the in-page confirmation.
- Offline check: the device is offline if the server reports `online=false` or the list is older than 60 s. Bridge
  does one fresh `webdev_list` first. If still offline: error `device_unreachable`
  ("Open CO in a browser tab on that computer").
- Bridge polls `webdev_call_get` every 700 ms until the call is done. If the row is expired, or nothing arrives within
  `timeoutMs` + 15 s: `device_timeout`.
- The result envelope is `tool.result` without `sig`. Bridge checks `callId`, `runId`, `deviceId` and `tool`.
  Mismatch: `bad_result`.
- `argsJson` or content over 1 MB: `too_large`.
- `webdev_call_put` is sent gzip-wrapped; if the server answers `gz_ops_only`, Bridge resends it plain.
- Call limit (`maxCallsPerRun`), `deviceToolsDeny` and taint (`untrusted:true` taints the Run) apply as for desktop devices.

Permission prompt:
- `--permission-prompt-tool mcp__dev_<short>__approve` uses the first CO Desktop device of the request.
- A request with only web devices gets no permission prompt tool.

`owner.json` `devices`:
- Web devices are added as `{deviceId, short, shortName, name, kind:"web", userEmail, environmentId, online,
  lastHeartbeatUtc}`. `lastHeartbeatUtc` = `lastSeenUtc`.
- Desktop entries get `kind:"desktop"` and `short`.
