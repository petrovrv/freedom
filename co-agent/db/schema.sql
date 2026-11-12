-- AI Workbench database (step 1 of the Drive → MariaDB migration, 2026-10-15).
-- MariaDB 11.8, utf8mb4 / InnoDB, idempotent. Apply: mysql --defaults-extra-file=<cnf> <database> < schema.sql
SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Schema metadata
CREATE TABLE IF NOT EXISTS meta (
  k VARCHAR(40)  NOT NULL,
  v VARCHAR(200) NULL,
  PRIMARY KEY (k)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO meta (k, v) VALUES ('schema', '1');

-- Projects (projects/<id>/project.json)
CREATE TABLE IF NOT EXISTS projects (
  project_id    VARCHAR(100) NOT NULL,
  env           VARCHAR(40)  NOT NULL,
  title         VARCHAR(300) NULL,
  description   TEXT         NULL,
  default_agent VARCHAR(40)  NULL,
  archived      TINYINT(1)   NOT NULL DEFAULT 0,
  created_utc   DATETIME(3)  NULL,
  updated_utc   DATETIME(3)  NULL,
  data          LONGTEXT     NOT NULL,           -- full JSON document
  sha           CHAR(64)     NOT NULL,           -- SHA-256 of data
  synced_utc    DATETIME(3)  NOT NULL,
  PRIMARY KEY (project_id),
  KEY ix_projects_env_updated (env, updated_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Chats (projects/<p>/chats/<id>/chat.json)
CREATE TABLE IF NOT EXISTS chats (
  chat_id        VARCHAR(60)  NOT NULL,
  project_id     VARCHAR(100) NOT NULL,
  title          VARCHAR(500) NULL,
  root_branch_id VARCHAR(60)  NULL,
  default_mode   VARCHAR(20)  NULL,
  created_utc    DATETIME(3)  NULL,
  updated_utc    DATETIME(3)  NULL,
  data           LONGTEXT     NOT NULL,
  sha            CHAR(64)     NOT NULL,
  synced_utc     DATETIME(3)  NOT NULL,
  PRIMARY KEY (chat_id),
  KEY ix_chats_project_updated (project_id, updated_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Chat events (events/NNNNNN-<type>-<eventId>.json)
CREATE TABLE IF NOT EXISTS events (
  event_id    VARCHAR(60)  NOT NULL,
  chat_id     VARCHAR(60)  NOT NULL,
  branch_id   VARCHAR(60)  NULL,
  seq         INT          NOT NULL,             -- ordering within chat
  type        VARCHAR(40)  NOT NULL,
  job_id      VARCHAR(80)  NULL,
  agent       VARCHAR(40)  NULL,
  author      VARCHAR(200) NULL,
  created_utc DATETIME(3)  NULL,
  text        MEDIUMTEXT   NULL,                 -- searchable plain text
  data        LONGTEXT     NOT NULL,
  sha         CHAR(64)     NOT NULL,
  synced_utc  DATETIME(3)  NOT NULL,
  PRIMARY KEY (event_id),
  KEY ix_events_chat_seq (chat_id, seq),
  KEY ix_events_job (job_id),
  FULLTEXT KEY ft_text (text)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Agent jobs (transport/hosts/<host>/<state>/<jobId>/request.json + result.json)
CREATE TABLE IF NOT EXISTS jobs (
  job_id        VARCHAR(80)  NOT NULL,
  host_id       VARCHAR(40)  NULL,
  env           VARCHAR(40)  NULL,
  project_id    VARCHAR(100) NULL,
  chat_id       VARCHAR(60)  NULL,
  agent         VARCHAR(40)  NULL,
  mode          VARCHAR(20)  NULL,
  model         VARCHAR(80)  NULL,
  state         VARCHAR(20)  NULL,
  status        VARCHAR(40)  NULL,
  started_utc   DATETIME(3)  NULL,
  completed_utc DATETIME(3)  NULL,
  request       LONGTEXT     NULL,
  result        LONGTEXT     NULL,
  sha           CHAR(64)     NOT NULL,
  synced_utc    DATETIME(3)  NOT NULL,
  PRIMARY KEY (job_id),
  KEY ix_jobs_chat (chat_id),
  KEY ix_jobs_state (state)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Scoped key/value documents (host:<id>/owner|limits|cloud-connectors, project:<id>/schedules)
CREATE TABLE IF NOT EXISTS kv (
  scope       VARCHAR(160) NOT NULL,
  name        VARCHAR(80)  NOT NULL,
  data        LONGTEXT     NOT NULL,
  sha         CHAR(64)     NOT NULL,
  updated_utc DATETIME(3)  NOT NULL,
  PRIMARY KEY (scope, name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Drive file id → record, so that a trashed/removed file deletes its row (cron/drive-sync.php, schema 2)
CREATE TABLE IF NOT EXISTS drive_map (
  file_id     VARCHAR(80)  NOT NULL,
  kind        VARCHAR(20)  NOT NULL,             -- project|chat|chatdir|event|job_request|job_result|kv
  rec_id      VARCHAR(200) NOT NULL,
  updated_utc DATETIME(3)  NOT NULL,
  PRIMARY KEY (file_id),
  KEY ix_drive_map_rec (kind, rec_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

UPDATE meta SET v = '2' WHERE k = 'schema' AND v = '1';

-- Schema 3 (CO Web 0.18.0, database-first reads): Drive folder ids, so that CO Web needs no Drive listing to open
-- a project or chat. Filled by cron/drive-sync.php (parents of project.json / chat.json), by CO Web writes and once
-- by db/backfill-folders.php. ADD COLUMN IF NOT EXISTS keeps this file idempotent (MariaDB).
ALTER TABLE projects ADD COLUMN IF NOT EXISTS folder_id VARCHAR(80) NULL;
ALTER TABLE chats ADD COLUMN IF NOT EXISTS folder_id VARCHAR(80) NULL;
UPDATE meta SET v = '3' WHERE k = 'schema' AND v IN ('1', '2');

-- Schema 4 (Bridge 1.3.0, CO Web 0.19.0): job queue in the database. CO Web inserts a job (origin 'db', state 'inbox'),
-- Bridge claims it atomically through db.php job_claim and reports event batches and the result; Drive inbox/claimed/
-- completed folders stay as the fallback path and as a copy. job_events keeps the Bridge event batches of every job
-- (30 days), so that CO Web follows a running job without Drive calls.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS origin VARCHAR(10) NULL;            -- 'db' = submitted through job_submit
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS queued_utc DATETIME(3) NULL;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS claimed_utc DATETIME(3) NULL;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS cancel_requested TINYINT(1) NOT NULL DEFAULT 0;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS rejection VARCHAR(1000) NULL;
ALTER TABLE jobs ADD INDEX IF NOT EXISTS ix_jobs_queue (host_id, state, queued_utc);

CREATE TABLE IF NOT EXISTS job_events (
  job_id      VARCHAR(80)  NOT NULL,
  first_seq   INT          NOT NULL,
  last_seq    INT          NOT NULL,
  data        MEDIUMTEXT   NOT NULL,             -- EventBatch JSON as written to transport/.../events/<jobId>/
  created_utc DATETIME(3)  NOT NULL,
  PRIMARY KEY (job_id, first_seq),
  KEY ix_job_events_created (created_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

UPDATE meta SET v = '4' WHERE k = 'schema' AND v IN ('1', '2', '3');

-- Schema 5 (2026-10-16): drive-sync copies a database job that no Bridge has claimed for 90 s into the Drive inbox
-- (same jobId; Bridge runs a jobId once). fallback_utc marks the copy.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS fallback_utc DATETIME(3) NULL;
-- cron/watch-cron.php: the push about a stuck queued or claimed job was sent
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS alerted_utc DATETIME(3) NULL;
UPDATE meta SET v = '5' WHERE k = 'schema' AND v IN ('1', '2', '3', '4');

-- Schema 6 (2026-10-16, project memory): automatic chat summaries (cron/summarize.php, Claude Haiku on the server),
-- shown in CO Web and given to new agent sessions together with the project's context/memory.md.
ALTER TABLE chats ADD COLUMN IF NOT EXISTS summary TEXT NULL;
ALTER TABLE chats ADD COLUMN IF NOT EXISTS summary_utc DATETIME(3) NULL;
UPDATE meta SET v = '6' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5');

-- Schema 7 (2026-10-16, document search): text of project documents (docx, pdf, xlsx, pptx, md, txt, ...) in chunks,
-- written by the aiwb-docs tool on the home PC (db.php docs_state / docs_put / docs_delete), searched by CO Web
-- (search_docs) and agents (docs_search). doc_key = sha1 of the path relative to AI-Workbench/projects.
CREATE TABLE IF NOT EXISTS docs (
  doc_key     CHAR(40)      NOT NULL,
  chunk       INT           NOT NULL,
  project_id  VARCHAR(100)  NOT NULL,
  path        VARCHAR(1000) NOT NULL,             -- relative to AI-Workbench/projects, forward slashes
  name        VARCHAR(255)  NOT NULL,
  mtime       BIGINT        NOT NULL,             -- seconds, of the indexed file
  size        BIGINT        NOT NULL,
  indexed_utc DATETIME(3)   NOT NULL,
  text        MEDIUMTEXT    NOT NULL,
  PRIMARY KEY (doc_key, chunk),
  KEY ix_docs_project (project_id),
  FULLTEXT KEY ft_docs (name, text)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '7' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6');

-- Schema 8 (2026-10-17, chat protocol): the secretary (cron/protocol.php, the same model as the chat's agent) keeps
-- a protocol of each chat — decisions, constraints, facts, open questions, artifacts, answer pointers — linked to the
-- source message (events.seq). CO Web shows it next to the messages (db.php op protocol); agents do not read it yet.
CREATE TABLE IF NOT EXISTS protocol (
  id          BIGINT        NOT NULL AUTO_INCREMENT,
  chat_id     VARCHAR(60)   NOT NULL,
  entry_no    INT           NOT NULL,             -- per chat: 1, 2, 3 ...
  section     VARCHAR(20)   NOT NULL,             -- decision|constraint|fact|question|artifact|answer
  text        TEXT          NOT NULL,
  quote       TEXT          NULL,                 -- verbatim fragment of the source message
  msg_seq     INT           NOT NULL,             -- events.seq of the source message
  status      VARCHAR(12)   NOT NULL DEFAULT 'active', -- active|superseded|closed
  replaced_by INT           NULL,                 -- entry_no of the replacement
  closed_seq  INT           NULL,                 -- events.seq that closed or superseded the entry
  author      VARCHAR(20)   NOT NULL DEFAULT 'secretary',
  model       VARCHAR(60)   NULL,
  created_utc DATETIME(3)   NOT NULL,
  updated_utc DATETIME(3)   NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_protocol_chat_no (chat_id, entry_no),
  KEY ix_protocol_chat_seq (chat_id, msg_seq)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
ALTER TABLE chats ADD COLUMN IF NOT EXISTS protocol_seq INT NULL;            -- last events.seq the secretary read
ALTER TABLE chats ADD COLUMN IF NOT EXISTS protocol_utc DATETIME(3) NULL;
ALTER TABLE chats ADD COLUMN IF NOT EXISTS protocol_error VARCHAR(500) NULL;
ALTER TABLE chats ADD COLUMN IF NOT EXISTS protocol_model VARCHAR(60) NULL;
UPDATE meta SET v = '8' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7');

-- Schema 9 (2026-10-18, project memory candidates): the secretary (cron/protocol.php) also proposes durable
-- project-level decisions and facts for context/memory.md; the owner accepts (CO Web appends the line to memory.md on
-- Drive) or rejects them in CO Web (db.php memory_candidates / memory_decide).
CREATE TABLE IF NOT EXISTS memory_candidates (
  id           BIGINT        NOT NULL AUTO_INCREMENT,
  project_id   VARCHAR(100)  NOT NULL,
  chat_id      VARCHAR(60)   NOT NULL,
  msg_seq      INT           NOT NULL,                -- events.seq of the source message
  tag          VARCHAR(12)   NOT NULL,                -- decision|fact
  text         VARCHAR(400)  NOT NULL,
  norm_sha     CHAR(40)      NOT NULL,                -- sha1 of the normalized text (duplicates)
  day          DATE          NOT NULL,                -- date of the source message
  status       VARCHAR(12)   NOT NULL DEFAULT 'pending', -- pending|accepted|rejected
  final_text   VARCHAR(400)  NULL,                  -- text as accepted (may be edited)
  model        VARCHAR(60)   NULL,
  created_utc  DATETIME(3)   NOT NULL,
  decided_utc  DATETIME(3)   NULL,
  decided_by   VARCHAR(200)  NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_memcand_project_norm (project_id, norm_sha),
  KEY ix_memcand_project_status (project_id, status),
  KEY ix_memcand_chat_seq (chat_id, msg_seq)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '9' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8');

-- Schema 10 (2026-10-19, sharing): projects and single chats shared with other environments (editor/viewer);
-- jobs.actor_env = who submitted.
CREATE TABLE IF NOT EXISTS shares (
  kind        VARCHAR(8)   NOT NULL, -- project|chat
  target_id   VARCHAR(100) NOT NULL,
  env         VARCHAR(40)  NOT NULL,
  role        VARCHAR(8)   NOT NULL, -- editor|viewer
  added_by    VARCHAR(200) NULL,
  created_utc DATETIME(3)  NOT NULL,
  PRIMARY KEY (kind, target_id, env),
  KEY ix_shares_env (env, kind)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS actor_env VARCHAR(40) NULL;
UPDATE meta SET v = '10' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9');

-- Schema 11 (2026-10-19, web device; lab/web-device/SPEC.md): a CO Web tab acts as a file device for agent runs.
-- web_devices: one row per browser and user (device_id "w-" + 16 hex); web_device_calls: tool.call envelopes queued by
-- Bridge (webdev_call_put), claimed by the tab (webdev_claim) and answered (webdev_result). watch-cron.php expires
-- calls past issuedUtc + timeoutMs and deletes finished calls older than 10 min (open ones after their deadline).
CREATE TABLE IF NOT EXISTS web_devices (
  device_id      VARCHAR(18)  NOT NULL,
  user_email     VARCHAR(190) NOT NULL,
  environment_id VARCHAR(64)  NOT NULL,
  short_name     VARCHAR(12)  NOT NULL, -- "web" + first 6 hex of the id; MCP server dev_<short>
  name           VARCHAR(120) NOT NULL,
  folders_json   TEXT         NOT NULL, -- [{id,name,access:"read"|"write"}]
  tools_json     MEDIUMTEXT   NOT NULL, -- MCP tools/list format
  created_utc    DATETIME(3)  NOT NULL,
  last_seen_utc  DATETIME(3)  NOT NULL,
  revoked        TINYINT(1)   NOT NULL DEFAULT 0,
  PRIMARY KEY (device_id),
  KEY ix_webdev_user (user_email),
  KEY ix_webdev_env (environment_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
CREATE TABLE IF NOT EXISTS web_device_calls (
  call_id     VARCHAR(64) NOT NULL,
  device_id   VARCHAR(18) NOT NULL,
  job_id      VARCHAR(64) NOT NULL,
  tool        VARCHAR(32) NOT NULL,
  args_json   MEDIUMTEXT  NOT NULL, -- full tool.call envelope
  state       ENUM('queued','claimed','done','expired') NOT NULL DEFAULT 'queued',
  result_json MEDIUMTEXT  NULL,     -- tool.result envelope
  created_utc DATETIME(3) NOT NULL,
  claimed_utc DATETIME(3) NULL,
  done_utc    DATETIME(3) NULL,
  PRIMARY KEY (call_id),
  KEY ix_dev_state (device_id, state)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '11' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10');

-- Schema 12 (2026-10-28, chat files as version streams; docs/SPEC-chat-files.md, web/agent/files-lib.php).
-- One row per live file of a chat (path relative to AI-Workbench). Versions are the Drive revisions of that file, linked to
-- messages by md5; extra holds what Drive cannot: members (files absorbed by a merge), parent (branch), restored, shas.
CREATE TABLE IF NOT EXISTS chat_files (
  id             INT          NOT NULL AUTO_INCREMENT,
  chat_id        VARCHAR(60)  NOT NULL,
  project_id     VARCHAR(100) NOT NULL,
  path           VARCHAR(700) NOT NULL,
  path_sha       CHAR(40)     NOT NULL,           -- sha1(path)
  name           VARCHAR(300) NOT NULL,           -- path inside the workspace, as shown
  kind           ENUM('doc','temp') NOT NULL DEFAULT 'doc',
  kind_user      TINYINT(1)   NOT NULL DEFAULT 0, -- kind set in CO Web (Keep / Temporary)
  state          ENUM('live','hidden','merged','gone') NOT NULL DEFAULT 'live',
  merged_into    INT          NULL,
  legacy         TINYINT(1)   NOT NULL DEFAULT 0, -- versions are copies in artifacts/<job>/ (before Bridge 1.6)
  extra          TEXT         NULL,
  versions       INT          NOT NULL DEFAULT 0,
  head_md5       CHAR(32)     NULL,
  head_sha256    CHAR(64)     NULL,
  head_size      BIGINT       NULL,
  head_utc       DATETIME(3)  NULL,
  head_origin    VARCHAR(10)  NULL,               -- agent | user
  head_seq       INT          NULL,               -- message of the newest version
  first_utc      DATETIME(3)  NULL,
  annotation     VARCHAR(400) NULL,
  annotation_sha CHAR(64)     NULL,               -- head_sha256 the annotation was written for
  annotation_utc DATETIME(3)  NULL,
  gone_utc       DATETIME(3)  NULL,
  updated_utc    DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_chat_files_path (chat_id, path_sha),
  KEY ix_chat_files_gc (state, kind, head_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
-- Files the cleanup (cron/files-gc.php) or the migration moved to the Drive trash.
CREATE TABLE IF NOT EXISTS files_trash (
  path_sha    CHAR(40)     NOT NULL,
  path        VARCHAR(700) NOT NULL,
  file_id     VARCHAR(100) NULL,
  reason      VARCHAR(20)  NOT NULL,              -- temp | merged | legacy | duplicate
  bytes       BIGINT       NOT NULL DEFAULT 0,
  trashed_utc DATETIME(3)  NOT NULL,
  PRIMARY KEY (path_sha)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '12' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11');

-- Schema 13 (2026-10-30, follow-ups; web/agent/followup-lib.php). A message the user sends while a job of the chat runs.
-- pending -> offered (job_claim gave it to Bridge) -> delivered (written into the running Claude's stdin; the server then
-- writes it into the chat as a user_message with followupOf) | late (the agent cannot take it: Codex, the turn already
-- ended) -> sending (one CO Web took it; back to late after 120 s without followup_done) -> requeued (sent as the next
-- message after the job) | cancelled (the user withdrew it). A delivered row's chat event is EVT-FU-<id> (written again
-- by job_claim while event_id is NULL).
CREATE TABLE IF NOT EXISTS job_followups (
  id            INT           NOT NULL AUTO_INCREMENT,
  job_id        VARCHAR(80)   NOT NULL,
  chat_id       VARCHAR(60)   NOT NULL,
  host_id       VARCHAR(40)   NULL,
  author        VARCHAR(200)  NULL,
  text          MEDIUMTEXT    NOT NULL,
  state         ENUM('pending','offered','delivered','late','sending','requeued','cancelled') NOT NULL DEFAULT 'pending',
  created_utc   DATETIME(3)   NOT NULL,
  offered_utc   DATETIME(3)   NULL,               -- offered to Bridge, or taken for sending (state sending)
  done_utc      DATETIME(3)   NULL,
  event_id      VARCHAR(80)   NULL,
  PRIMARY KEY (id),
  KEY ix_followups_chat (chat_id, state),
  KEY ix_followups_job (job_id, state)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '13' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12');

-- Schema 14 (2026-10-31, memory consolidation; cron/memory-consolidate.php). A nightly pass over context/memory.md
-- proposes revisions as candidates: kind add (the secretary, schema 9) | revise (the lines in replaces become one new
-- line, text) | drop (the lines in replaces are removed). Nothing is applied unattended: accepting in CO Web rewrites
-- memory.md and appends the removed lines to context/memory-archive.md (db.php memory_decide). replaces = JSON list of
-- the exact original lines; a revision whose lines left memory.md fails on accept and becomes obsolete on the next pass.
ALTER TABLE memory_candidates ADD COLUMN IF NOT EXISTS kind VARCHAR(8) NOT NULL DEFAULT 'add';
ALTER TABLE memory_candidates ADD COLUMN IF NOT EXISTS replaces MEDIUMTEXT NULL;
ALTER TABLE memory_candidates ADD COLUMN IF NOT EXISTS reason VARCHAR(400) NULL;
UPDATE meta SET v = '14' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13');

-- Schema 15 (2026-11-01, job timeline and cost; web/agent/jobstats-lib.php). One row per finished job, built by
-- cron/drive-sync.php from jobs.result and job_events while the events are still kept (14 days): tokens, the cost
-- Claude Code reported (cost_usd) and the API-equivalent (api_usd: cost_usd, else tokens x meta.api_prices), tool calls,
-- errors, per-tool counts and a compact timeline (steps, cleared after 180 days; the counters stay). parent_chat_id is
-- set for jobs of subtask chats (aiwb-subtask), so a job's subtask tree outlives the chats' events.
CREATE TABLE IF NOT EXISTS job_stats (
  job_id          VARCHAR(80)   NOT NULL,
  env             VARCHAR(40)   NULL,
  project_id      VARCHAR(100)  NULL,
  chat_id         VARCHAR(60)   NULL,
  parent_chat_id  VARCHAR(60)   NULL,
  host_id         VARCHAR(40)   NULL,
  agent           VARCHAR(40)   NULL,
  model           VARCHAR(80)   NULL,
  outcome         VARCHAR(12)   NOT NULL,         -- completed|failed|cancelled
  queued_utc      DATETIME(3)   NULL,
  started_utc     DATETIME(3)   NULL,
  completed_utc   DATETIME(3)   NULL,
  duration_ms     BIGINT        NULL,             -- started (or claimed) -> completed
  input_tokens    BIGINT        NULL,             -- as the agent reports (Codex: cached included)
  cached_tokens   BIGINT        NULL,
  cache_write_tokens BIGINT     NULL,
  output_tokens   BIGINT        NULL,
  reasoning_tokens BIGINT       NULL,
  cost_usd        DECIMAL(14,4) NULL,             -- reported by Claude Code
  api_usd         DECIMAL(14,4) NULL,             -- API-equivalent (reported, else from meta.api_prices)
  turns           INT           NULL,
  tool_calls      INT           NOT NULL DEFAULT 0,
  tool_errors     INT           NOT NULL DEFAULT 0,
  subagents       INT           NOT NULL DEFAULT 0,
  has_trace       TINYINT(1)    NOT NULL DEFAULT 0,  -- job_events were read
  tools           TEXT          NULL,             -- JSON {name: [calls, errors, ms]}, top 30
  steps           MEDIUMTEXT    NULL,             -- JSON [[offsetMs, durMs|null, kind, label, err, depth, outTokens|null], ...]
  job_sha         CHAR(64)      NOT NULL,         -- jobs.sha the row was built from
  built_utc       DATETIME(3)   NOT NULL,
  PRIMARY KEY (job_id),
  KEY ix_job_stats_completed (completed_utc),
  KEY ix_job_stats_chat (chat_id, completed_utc),
  KEY ix_job_stats_parent (parent_chat_id, queued_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '15' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14');

-- Schema 16 (2026-11-01, the tool library learns from jobs; cron/tool-learn.php). tool_usage: calls of library tools
-- found in job_events (mcp = aiwb-tools tool_run, cli = a shell command with ...\tools\<name>\...), per tool, day and
-- job; errors = tool results marked is_error. tool_candidates: nightly proposals - new (repeated one-off code -> a tool
-- or an extension), skill (agents erred on a tool -> SKILL.md fix), archive (unused for 60 days). The owner accepts
-- in CO Web (db.php tool_decide); an accepted proposal runs as an agent job in a new chat (status accepted only once that
-- chat exists, chat_id) that changes the library
-- through toolbox.py only. memory_candidates kind 'rule' (the protocol secretary): a person's correction of the agent
-- as a rule for agents; tag = target: env (Reflexes: the environment profile of target_env) | project (memory.md);
-- reason = the verbatim words of the correction, msg_seq = its message.
CREATE TABLE IF NOT EXISTS tool_usage (
  tool        VARCHAR(60)  NOT NULL,
  day         DATE         NOT NULL,
  job_id      VARCHAR(80)  NOT NULL,
  via         VARCHAR(4)   NOT NULL,                -- mcp|cli
  calls       INT          NOT NULL DEFAULT 0,
  errors      INT          NOT NULL DEFAULT 0,
  PRIMARY KEY (tool, day, job_id, via),
  KEY ix_tool_usage_day (day)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
CREATE TABLE IF NOT EXISTS tool_candidates (
  id           BIGINT        NOT NULL AUTO_INCREMENT,
  kind         VARCHAR(8)    NOT NULL,                -- new|skill|archive
  tool         VARCHAR(60)   NOT NULL,                -- existing tool, or the proposed name of a new one
  title        VARCHAR(300)  NOT NULL,
  text         TEXT          NOT NULL,                -- what to change and why
  evidence     MEDIUMTEXT    NULL,                    -- JSON list of {jobId, chatId, projectId, note}
  norm_sha     CHAR(40)      NOT NULL,
  status       VARCHAR(12)   NOT NULL DEFAULT 'pending', -- pending|accepted|rejected|obsolete
  model        VARCHAR(60)   NULL,
  created_utc  DATETIME(3)   NOT NULL,
  decided_utc  DATETIME(3)   NULL,
  decided_by   VARCHAR(200)  NULL,
  chat_id      VARCHAR(80)   NULL,                    -- accepted: the chat whose agent job carries it out
  offered_utc  DATETIME(3)   NULL,                    -- last Accept & run (task handed to CO Web, chat not reported yet)
  PRIMARY KEY (id),
  UNIQUE KEY ux_toolcand_norm (norm_sha),
  KEY ix_toolcand_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
ALTER TABLE memory_candidates ADD COLUMN IF NOT EXISTS target_env VARCHAR(40) NULL; -- kind rule: env of the person who corrected
UPDATE meta SET v = '16' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15');

-- Schema 17 (2026-11-03, Bridge supervision; web/agent/failures-lib.php). job_failures: one row per failed/timed_out
-- job with its cause (kind from the diagnosis rules shared with co-web/src/diagnose.ts, cases in diagnose.cases.json),
-- built by the same cron pass as job_stats; the daily check (aiwb-smoke status) and the CO Web Host tab read the
-- summary through db.php op health. host_events: events of the Bridge supervisor (launcher watchdog, the restart
-- ladder) posted by Bridge (db.php op host_event), kept 90 days.
CREATE TABLE IF NOT EXISTS job_failures (
  job_id       VARCHAR(80)   NOT NULL,
  env          VARCHAR(40)   NULL,
  host_id      VARCHAR(40)   NULL,
  agent        VARCHAR(40)   NULL,
  model        VARCHAR(80)   NULL,
  project_id   VARCHAR(100)  NULL,
  chat_id      VARCHAR(80)   NULL,
  failed_utc   DATETIME(3)   NOT NULL,
  status       VARCHAR(16)   NOT NULL,              -- failed|timed_out
  exit_code    INT           NULL,
  kind         VARCHAR(40)   NOT NULL,
  detail       VARCHAR(500)  NULL,
  stderr_tail  VARCHAR(2048) NULL,
  last_event   VARCHAR(500)  NULL,
  diag_ver     INT           NOT NULL DEFAULT 1,
  job_sha      CHAR(64)      NULL,
  PRIMARY KEY (job_id),
  KEY ix_job_failures_utc (failed_utc),
  KEY ix_job_failures_kind (kind, failed_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
CREATE TABLE IF NOT EXISTS host_events (
  id       BIGINT        NOT NULL AUTO_INCREMENT,
  host_id  VARCHAR(40)   NOT NULL,
  utc      DATETIME(3)   NOT NULL,
  kind     VARCHAR(40)   NOT NULL,
  detail   VARCHAR(500)  NULL,
  PRIMARY KEY (id),
  KEY ix_host_events_utc (utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '17' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16');

-- Schema 18 (2026-11-03, confirmation cards; web/agent/actions-lib.php). action_requests: a risky action of an agent
-- (send, purchase, delete, publish, git push, ...) that read-guard held in any chat; Bridge forwards the hook's event
-- (job_events, source guard, content type risky_action_held) and db.php op job_post_events files it here. One row per
-- chat and code (read-guard keeps one open code per chat; later actions of the same request are appended to actions).
-- "Waiting for you" shows pending rows as cards (Yes / No send "<code> да" / "<code> нет" into the chat), a push goes to
-- the chat owner's env when the row is created.
CREATE TABLE IF NOT EXISTS action_requests (
  id           BIGINT        NOT NULL AUTO_INCREMENT,
  chat_id      VARCHAR(60)   NOT NULL,
  job_id       VARCHAR(80)   NULL,
  env          VARCHAR(40)   NULL,                  -- env of the chat's project (the one who may confirm)
  code         CHAR(4)       NOT NULL,
  kind         VARCHAR(12)   NOT NULL,              -- send|purchase|delete|publish|push|secret|submit|other
  summary      VARCHAR(500)  NOT NULL,
  command      VARCHAR(500)  NULL,
  actions      TEXT          NULL,                  -- JSON list of {kind, summary} held under the same code
  tainted      TINYINT       NOT NULL DEFAULT 0,    -- the chat read text with signs of prompt injection
  state        VARCHAR(10)   NOT NULL DEFAULT 'pending', -- pending|approved|denied|expired
  created_utc  DATETIME(3)   NOT NULL,
  expires_utc  DATETIME(3)   NOT NULL,
  decided_utc  DATETIME(3)   NULL,
  decided_by   VARCHAR(200)  NULL,
  pushed_utc   DATETIME(3)   NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_action_chat_code (chat_id, code),
  KEY ix_action_state (state, expires_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
-- events.writer: the signed-in account (session, not the client's author field) whose put_user created or last changed
-- the event; chat_read byOwner and the cards use it, so an editor of a shared chat cannot pass as its owner (review).
ALTER TABLE events ADD COLUMN IF NOT EXISTS writer VARCHAR(200) NULL;
UPDATE meta SET v = '18' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17');

-- Schema 19 (2026-11-07, dossier and secretary hints; web/agent/dossier-lib.php). dossier: what agents should know about
-- a person of an environment — section me (facts about the person), people (people around them, one row per fact,
-- subject = the person's name), state (current affairs, subject = the topic). The protocol secretary adds and supersedes
-- rows on its own (auto-accepted, decision of the owner 07.11), the person edits them in CO Web (Inspector → Dossier),
-- agents through aiwb-dossier. New agent sessions get a compact block (db.php op dossier_context); a shared project or
-- chat gets only the people section. Superseded rows stay (status replaced, replaced_by), deleted ones get status deleted.
CREATE TABLE IF NOT EXISTS dossier (
  id           BIGINT        NOT NULL AUTO_INCREMENT,
  env          VARCHAR(40)   NOT NULL,
  section      VARCHAR(10)   NOT NULL,              -- me|people|state
  subject      VARCHAR(120)  NOT NULL DEFAULT '',   -- person (people), topic (state), short key (me)
  text         TEXT          NOT NULL,
  status       VARCHAR(10)   NOT NULL DEFAULT 'active', -- active|replaced|closed|deleted
  replaced_by  BIGINT        NULL,
  source_chat  VARCHAR(80)   NULL,
  source_msg   INT           NULL,
  source_date  DATE          NULL,                  -- date of the source message (when the fact was said)
  author       VARCHAR(40)   NOT NULL DEFAULT 'secretary', -- secretary|import|user|agent
  model        VARCHAR(60)   NULL,
  created_utc  DATETIME(3)   NOT NULL,
  updated_utc  DATETIME(3)   NOT NULL,
  updated_by   VARCHAR(200)  NULL,
  PRIMARY KEY (id),
  KEY ix_dossier_env (env, status, section, subject),
  FULLTEXT KEY ft_dossier (subject, text)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
-- Secretary hints (db.php op related): full-text search over the protocol entries of other chats for every message.
ALTER TABLE protocol ADD FULLTEXT INDEX IF NOT EXISTS ft_protocol_text (text);
UPDATE meta SET v = '19' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18');

-- Schema 20 (2026-11-09, associative search; web/agent/search-lib.php). Chat search merges three ranked lists by
-- reciprocal rank (RRF): full-text over messages, full-text over protocol entries, and vectors of protocol entries.
-- The vectors come from the static model minishlab/potion-multilingual-128M (256 dims) computed on the home PC by the
-- library tool aiwb-assoc (hourly task). PHP cannot run the model, so the query vector is built from vec_words: for
-- each word the sum of its raw token embeddings and the token count; query = sum / count, normalised (model2vec mean
-- pooling per word). Vectors are int8 with a float scale.
CREATE TABLE IF NOT EXISTS vec_words (
  word        VARCHAR(60)   CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, -- lower case
  n           SMALLINT      NOT NULL,             -- tokens of the word
  scale       FLOAT         NOT NULL,             -- sum of token vectors = v * scale
  v           VARBINARY(256) NOT NULL,            -- int8
  freq        INT           NOT NULL DEFAULT 0,   -- occurrences in the corpus (prefix fallback picks the most frequent)
  PRIMARY KEY (word)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
CREATE TABLE IF NOT EXISTS vec_protocol (
  protocol_id BIGINT        NOT NULL,             -- protocol.id
  chat_id     VARCHAR(60)   NOT NULL,
  sha         CHAR(40)      NOT NULL,             -- sha1 of protocol.text the vector was computed from
  scale       FLOAT         NOT NULL,             -- 1 / |v|: cosine = dot(query, v) * scale
  v           VARBINARY(256) NOT NULL,            -- int8
  PRIMARY KEY (protocol_id),
  KEY ix_vec_protocol_chat (chat_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
-- Aliases: names of one thing (Wikidata labels and aliases in ru/en/uk/nl, variants suggested by a model, own names
-- of the environment). A query that contains a term gets the other terms of its group as extra full-text words.
-- env '' = for every environment (general terms), otherwise only for that environment (own names, people).
CREATE TABLE IF NOT EXISTS aliases (
  id          BIGINT        NOT NULL AUTO_INCREMENT,
  env         VARCHAR(40)   NOT NULL DEFAULT '',
  grp         VARCHAR(80)   NOT NULL,             -- Wikidata Q-id or own key
  term        VARCHAR(120)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, -- lower case, words separated by one space
  source      VARCHAR(12)   NOT NULL,             -- wikidata|model|user
  created_utc DATETIME(3)   NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_aliases (env, grp, term),
  KEY ix_aliases_term (term)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '20' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19');

-- Schema 21 (2026-11-11, Freedom F1; web/agent/members-lib.php). members: the people of this workbench (owner, family or
-- team). Replaces the e-mail and environment constants in the PHP sources: sign-in to the API, push targets, names in
-- family chats, geo users, site agent rights. Rows are written by the installer or db/members.php, never by schema.sql.
CREATE TABLE IF NOT EXISTS members (
  env         VARCHAR(40)   NOT NULL,             -- environment id: [a-z0-9-]
  email       VARCHAR(190)  NOT NULL,             -- Google account, lower case
  name        VARCHAR(80)   NOT NULL,             -- display name (family chat, notifications)
  color       VARCHAR(16)   NOT NULL DEFAULT '',  -- CSS color of the member in CO Web, '' = default
  role        VARCHAR(12)   NOT NULL DEFAULT 'member', -- owner|member; exactly one owner
  site_agent  TINYINT       NOT NULL DEFAULT 0,   -- may start the server agent (ping.php); the owner always may
  sort        INT           NOT NULL DEFAULT 0,
  updated_utc DATETIME(3)   NOT NULL,
  PRIMARY KEY (env),
  UNIQUE KEY ux_members_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '21' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20');

-- Schema 22 (2026-11-11, S1 of chat CHAT-20261111085044-HV3A; web/agent/relay-lib.php). Device relay: the site agent calls
-- tools of the home devices through the server. relay_devices: devices a Bridge serves (CO Desktop, CO Resident Macs and
-- the Bridge itself as device "h-<host>"), replaced by every relay_devices_put. relay_calls: calls of site jobs, queued by
-- the site agent (relay_call_put), claimed by that Bridge (relay_claim) and answered (relay_result). watch-cron.php expires
-- calls past deadline_utc or not claimed within 60 s (never run late) and deletes finished ones after 10 min.
CREATE TABLE IF NOT EXISTS relay_devices (
  host_id        VARCHAR(40)  NOT NULL,
  device_id      VARCHAR(40)  NOT NULL,
  environment_id VARCHAR(64)  NOT NULL,
  short_name     VARCHAR(12)  NOT NULL, -- MCP server dev_<short> on the site agent, as on the Bridge
  name           VARCHAR(120) NOT NULL,
  kind           VARCHAR(16)  NOT NULL, -- desktop|mac|home
  online         TINYINT(1)   NOT NULL DEFAULT 0,
  tools_json     MEDIUMTEXT   NOT NULL, -- MCP tools/list format
  updated_utc    DATETIME(3)  NOT NULL,
  PRIMARY KEY (host_id, device_id),
  KEY ix_relaydev_env (environment_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
CREATE TABLE IF NOT EXISTS relay_calls (
  call_id        VARCHAR(64)  NOT NULL,
  host_id        VARCHAR(40)  NOT NULL,
  device_id      VARCHAR(40)  NOT NULL,
  job_id         VARCHAR(64)  NOT NULL,
  environment_id VARCHAR(64)  NOT NULL,
  chat_id        VARCHAR(80)  NOT NULL DEFAULT '',
  tool           VARCHAR(64)  NOT NULL,
  args_json      MEDIUMTEXT   NOT NULL,
  tainted        TINYINT(1)   NOT NULL DEFAULT 0,
  deny_json      VARCHAR(2000) NOT NULL DEFAULT '[]', -- tools the chat turned off (deviceToolsDeny)
  state          ENUM('queued','claimed','done','expired') NOT NULL DEFAULT 'queued',
  cancel_req     TINYINT(1)   NOT NULL DEFAULT 0, -- 1: the site job ended while the Bridge runs it; 2: told to the Bridge
  result_json    MEDIUMTEXT   NULL,     -- {ok, content, error:{code,message}, untrusted}
  created_utc    DATETIME(3)  NOT NULL,
  claimed_utc    DATETIME(3)  NULL,
  done_utc       DATETIME(3)  NULL,
  deadline_utc   DATETIME(3)  NOT NULL,
  PRIMARY KEY (call_id),
  KEY ix_relaycall_host (host_id, state),
  KEY ix_relaycall_job (job_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '22' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20', '21');

-- Schema 23 (2026-11-11, S5 of chat CHAT-20261111085044-HV3A; web/agent/models-lib.php). Model resources: one list of
-- subscriptions (agents in the hosts' owner.json are built in), the site, subscriptions registered by devices (S4) and
-- corporate models. A row overrides a built-in entry (source 'auto': flag, card, title) or is a resource of its own
-- (source 'user' or 'device'). slave_allowed: leading agents of other environments may call it as a subagent
-- (aiwb-subtask spawn --resource), without limits or confirmations; confidential: never outside its environment.
CREATE TABLE IF NOT EXISTS model_resources (
  id            VARCHAR(80)  NOT NULL,            -- "<host>/<agent>" for built-in entries, else free [a-z0-9._/-]
  env           VARCHAR(40)  NOT NULL,            -- whose resource
  kind          VARCHAR(16)  NOT NULL,            -- subscription|site|corporate|free
  title         VARCHAR(80)  NOT NULL DEFAULT '', -- '' = the built-in title
  host_id       VARCHAR(40)  NOT NULL DEFAULT '', -- executor that runs its jobs ('' = none yet)
  agent         VARCHAR(16)  NOT NULL,            -- claude|codex|dispatcher|engie|gemini|agy
  models_json   TEXT         NOT NULL,            -- [{id,label}]
  default_model VARCHAR(80)  NULL,
  card_json     TEXT         NOT NULL,            -- {summary, strengths, limits, dataStaysIn, notes}
  slave_allowed TINYINT(1)   NOT NULL DEFAULT 0,
  confidential  TINYINT(1)   NOT NULL DEFAULT 0,
  source        VARCHAR(8)   NOT NULL,            -- auto|user|device
  updated_utc   DATETIME(3)  NOT NULL,
  updated_by    VARCHAR(40)  NOT NULL DEFAULT '',
  PRIMARY KEY (id),
  KEY ix_modelres_env (env)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '23' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20', '21', '22');

-- Schema 24 (2026-11-12, subtask A of chat CHAT-20261112071412-CJZ0; web/agent/relay-lib.php, relay-mcp.php). Claude jobs of
-- the site reach the home devices through MCP over HTTP served by the server itself (relay-mcp.php, one URL per device)
-- instead of a node process per device and job. relay_tokens: the one-time key of a job that the site agent asks for
-- (relay_token, host key) and writes into the job's mcp.json; stored as sha256, bound to the job, its environment and
-- chat, deleted when the job ends (relay_cancel) or expired (watch-cron). tainted: set once a device returned untrusted
-- content, for every later call of the job.
CREATE TABLE IF NOT EXISTS relay_tokens (
  token_hash     CHAR(64)      NOT NULL,
  job_id         VARCHAR(64)   NOT NULL,
  environment_id VARCHAR(64)   NOT NULL,
  chat_id        VARCHAR(80)   NOT NULL DEFAULT '',
  deny_json      VARCHAR(2000) NOT NULL DEFAULT '[]', -- deviceToolsDeny of the job
  tainted        TINYINT(1)    NOT NULL DEFAULT 0,
  created_utc    DATETIME(3)   NOT NULL,
  expires_utc    DATETIME(3)   NOT NULL,
  PRIMARY KEY (token_hash),
  KEY ix_relaytok_job (job_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '24' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20', '21', '22', '23');

-- Schema 25 (2026-11-12, D of chat CHAT-20261112071412-CJZ0; db-lib.php co_db_put, push-lib.php co_push_job_finished).
-- One answer per job: the chats Site served had every answer 2-3 times (CO Web does not wait for the server's answer of a
-- site job, so each open tab wrote its own copy next to finalize-lib.php's; 84 such pairs since 15.10, also on Home when
-- job_result and drive-sync finalized at once). answer_job is the job id of an assistant_message (NULL for other events);
-- the unique key makes the first writer win, co_db_put drops later copies as duplicates. Existing copies go first: a later
-- answer of the same chat and job that is an exact copy of an earlier one is deleted (the first written stays). Exact:
-- the text byte for byte and the whole event equal except its own id, sequence and write times (eventId, sequence,
-- createdUtc, completedUtc). A copy that differs in anything else is left, and the unique key then fails loudly
-- instead of losing it (all 86 copies on the owner's database were exact).
-- jobs.pushed_utc: the "job finished" push the server sends for jobs of the site agent (Bridge sends its own), once per job.
DELETE e2 FROM events e2
  JOIN events e1 ON e1.chat_id = e2.chat_id AND e1.job_id = e2.job_id AND e1.type = 'assistant_message'
   AND (e1.synced_utc < e2.synced_utc OR (e1.synced_utc = e2.synced_utc AND e1.event_id < e2.event_id))
   AND BINARY e1.text <=> BINARY e2.text
   AND JSON_EQUALS(JSON_REMOVE(e1.data, '$.eventId', '$.sequence', '$.createdUtc', '$.completedUtc'),
                   JSON_REMOVE(e2.data, '$.eventId', '$.sequence', '$.createdUtc', '$.completedUtc')) = 1
 WHERE e2.type = 'assistant_message' AND e2.job_id IS NOT NULL;
ALTER TABLE events ADD COLUMN IF NOT EXISTS answer_job VARCHAR(80) AS (IF(type = 'assistant_message', job_id, NULL)) PERSISTENT;
ALTER TABLE events ADD UNIQUE INDEX IF NOT EXISTS ux_events_answer (chat_id, answer_job);
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS pushed_utc DATETIME(3) NULL;
UPDATE meta SET v = '25' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20', '21', '22', '23', '24');

-- Schema 26 (2026-11-12, subtask B of chat CHAT-20261112071412-CJZ0, owner 12.11 #19-#23; web/agent/sandbox-lib.php).
-- Sandbox providers: where the pc_* tools of an environment that does not own the computer run (the children on the home
-- PC: WSL distro aiwb-family, own Linux user; later a Mac resident's Linux VM, Freedom peer microVMs). Each provider
-- (Bridge) replaces its rows every 30 s (sandbox_put, host key). seats_json: {env: relay device id} its jobs call; the
-- server picks for an environment its own sandbox first, then a family member's that allows it (sandbox_pick), and
-- shows/accepts only that seat (relay_devices, relay_call_put).
CREATE TABLE IF NOT EXISTS sandbox_resources (
  id           VARCHAR(80)  NOT NULL,             -- "<host>/<name>", e.g. home-main/aiwb-family
  host_id      VARCHAR(40)  NOT NULL,             -- provider that publishes it and serves its calls
  device_id    VARCHAR(40)  NOT NULL DEFAULT '',  -- relay device of the machine (h-<host>, d-...)
  owner_env    VARCHAR(64)  NOT NULL,             -- whose machine
  kind         VARCHAR(16)  NOT NULL,             -- wsl|mac-vm|microvm|container|vm
  card_json    TEXT         NOT NULL,             -- {os, tools, cpus, ramMb, diskMb, limits, notes}
  allowed_json TEXT         NOT NULL,             -- environments besides the owner that may use it
  quotas_json  TEXT         NOT NULL,             -- {env: {maxBackground, maxTimeoutSec, maxCallsPerJob, ...}}
  seats_json   TEXT         NOT NULL,             -- {env: relay device id}
  online       TINYINT(1)   NOT NULL DEFAULT 0,
  updated_utc  DATETIME(3)  NOT NULL,             -- heartbeat
  PRIMARY KEY (id),
  KEY ix_sandbox_host (host_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
UPDATE meta SET v = '26' WHERE k = 'schema' AND v IN ('1', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20', '21', '22', '23', '24', '25');
