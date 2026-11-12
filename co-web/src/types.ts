// SPDX-License-Identifier: AGPL-3.0-or-later
import type { ChatParent } from './lineage';
import type { AgentId, EnvironmentId } from './config';

export interface Project {
  projectId: string;
  environmentId: EnvironmentId;
  title: string;
  description?: string;
  defaultAgentId: AgentId;
  createdUtc: string;
  updatedUtc: string;
  archived: boolean;
  // 0.28.0: work (default), service (system/admin), sandbox (tests); corporate = holds employer data (flag only for now)
  kind?: 'work' | 'service' | 'sandbox';
  corporate?: boolean;
  // Folders on the home computer / Google Drive attached as read-only or writable context.
  contextFolders?: ContextFolder[];
  // 0.78.0: new chats of the project start in study mode (studyMode.ts); a chat's own switch wins
  studyDefault?: boolean;
  // Folder exists in Drive but has no project.json (created before CO).
  legacy?: boolean;
  folderId: string;
  // 0.45.0: from the server, never written to project.json
  shareRole?: 'editor' | 'viewer';
  shareVia?: 'project' | 'chat';
  sharedChatIds?: string[];
  // 0.90.0: the hidden project that holds the family room (right-panel tab "Family"); not listed in the sidebar
  family?: boolean;
}

export interface Chat {
  chatId: string;
  projectId: string;
  title: string;
  rootBranchId: string;
  createdUtc: string;
  updatedUtc: string;
  defaultMode?: Mode;
  importedFrom?: { source: 'claude' | 'chatgpt'; id: string; importedUtc: string };
  // Set when the chat moved to another project: provider sessions from before are not resumed.
  movedUtc?: string;
  legacy?: boolean;
  folderId: string;
  permissions?: ChatPermissions;
  // The title came from the first message and is replaced by the agent's [[chat-title]]; false after a manual rename.
  titleAuto?: boolean;
  // CO Web 0.17.0: the project's "new chat" view before the first message; created on Drive by the first send.
  draft?: boolean;
  // CO Web 0.25.0: hidden from the chat list, shown under "Archived (N)"; opens and works as usual.
  archived?: boolean;
  // 0.26.0: automatic summary written by the server (Claude Haiku), from the database only
  summary?: string;
  // 0.33.0: branch / follow-up / side question / subtask of another chat (lineage.ts)
  parent?: ChatParent;
  // 0.36.0: what the agent is told (contextLevels.ts); absent = 'full'
  contextMode?: 'full' | 'reference' | 'clean';
  shareRole?: 'editor' | 'viewer'; // 0.45.0: role of a chat shared one by one (server)
  // 0.71.0: false = no automatic retry on the other agent after a limit / sign-in / model error (autoswitch-lib.php)
  autoSwitch?: boolean;
  // 0.78.0: study mode of this chat (absent = the project's studyDefault) and who switched it when (studyMode.ts)
  study?: boolean;
  studyLog?: StudySwitch[];
  // 0.80.0: the chat of a document in the study viewer (path relative to AI-Workbench); one chat per document
  studyDoc?: string;
  // 0.87.0: agent browser settings of this chat over the environment defaults (browser-lib.php); only the keys set
  browser?: BrowserSettings;
  // 0.89.0: family chat (FamilyChat.tsx) - people write to each other, the agent (@co) answers when called; the server
  // pushes new messages to the other members (family-lib.php)
  family?: boolean;
}

// 0.87.0: agent browser (CO Desktop 0.9.9, browser-agent 0.15.0): tabs one chat may hold (0 = unlimited), minutes an
// idle tab lives, minutes with typed form data, a tab with an unsolved captcha never expires.
export interface BrowserSettings {
  tabs?: number;
  idleMin?: number;
  formIdleMin?: number;
  captchaKeep?: boolean;
}

export interface StudySwitch {
  on: boolean;
  by: string;
  utc: string;
}

export type Mode = 'chat' | 'cowork';

// What agents may do in a chat (Bridge 1.1 "permissions"); sent with every message.
// 0.37.0: environment template (new-chat screen), stored per environment in the database
export interface EnvTemplate {
  name: string;
  agent: string;
  mode: Mode;
  model: string | null;
  effort: string | null;
  hostId?: string;
  permissions?: ChatPermissions;
  useDevice?: boolean;
  useCorporate?: boolean; // ignored while the corporate connector is paused (0.66.0)
  useEngie?: boolean; // legacy name of useCorporate; old templates may still carry it
}

export interface ChatPermissions {
  files: 'read' | 'write';
  shell: boolean;
  web: boolean;
}

export const DEFAULT_PERMISSIONS: ChatPermissions = { files: 'write', shell: true, web: true };

// 0.86.1: chat.json written by hand or by a schedule may carry files: true / "True" (08.11, Bridge rejected every
// resend). Coerce to the two allowed values: true/rw -> write, ro -> read, anything else -> the default.
export function chatPermissions(p: unknown): ChatPermissions {
  if (!p || typeof p !== 'object') return DEFAULT_PERMISSIONS;
  const r = p as Record<string, unknown>;
  const f = String(r.files ?? '').trim().toLowerCase();
  const files: ChatPermissions['files'] = f === 'read' || f === 'ro' || f === 'readonly' || f === 'read-only' ? 'read' : 'write';
  return { files, shell: r.shell !== false, web: r.web !== false };
}

export interface ContextFolder {
  id: string;
  rootId: string;
  relativePath: string;
  label: string;
  access: 'read' | 'write';
}

export interface FolderNode {
  n: string;
  c?: FolderNode[] | null;
  m?: boolean;
}

export interface FolderIndex {
  rootId: string;
  relativePath: string;
  generatedUtc: string;
  depth: number;
  truncated: boolean;
  error?: string | null;
  tree: FolderNode;
}

export interface Usage {
  inputTokens: number;
  cachedInputTokens: number;
  cacheCreationInputTokens?: number;
  outputTokens: number;
  reasoningOutputTokens?: number;
  costUsd?: number | null;
  durationMs?: number | null;
  numTurns?: number | null;
}

export interface ExportSkip {
  path: string;
  reason: string;
}

export interface AttachmentRef {
  displayName: string;
  path: string; // transport-relative path inside attachments/
  sizeBytes: number;
  sha256: string;
  fileId?: string; // persistent copy in branch attachments; 0.55.0 with target: the request package copy
  // 0.55.0: the chat file this upload became (Bridge 1.6.0 "chat-files"): scratch-relative target, its path relative to
  // AI-Workbench, md5 of the content (= md5Checksum of the Drive revision)
  target?: string;
  streamPath?: string;
  md5?: string;
}

export interface ArtifactRecord {
  sourceRelativePath: string;
  driveRelativePath: string;
  sizeBytes: number;
  sha256: string;
  origin?: 'declared' | 'auto' | 'imported';
  // Bridge 1.6.0 / site agent 1.1.0: md5 of the content; ref = driveRelativePath is the live file (no copy), the
  // version is the Drive revision with this md5
  md5?: string;
  ref?: boolean;
}

export type ChatEventType =
  | 'user_message'
  | 'assistant_message'
  | 'cancel_requested'
  | 'error'
  | 'reaction'; // 0.89.0: family chats - an emoji on a message (target), remove: true takes it back

export interface ChatEvent {
  schemaVersion: 1;
  eventId: string;
  sequence: number;
  type: ChatEventType;
  createdUtc: string;
  chatId: string;
  branchId: string;
  jobId?: string;
  // Executor that ran the job; absent on events written before the host switch (home-main).
  hostId?: string;
  agent?: AgentId;
  text?: string;
  author?: string;
  attachments?: AttachmentRef[];
  returnFiles?: string[];
  conversation?: { operation: 'new' | 'resume' | 'fork'; sessionId: string | null };
  mode?: Mode;
  model?: string | null;
  effort?: string | null;
  modelReported?: string | null;
  usage?: Usage | null;
  exportSkipped?: ExportSkip[];
  imported?: { source: 'claude' | 'chatgpt'; originalId?: string };
  // user_message: the instruction carried the AI Workbench environment note.
  workbenchNote?: boolean;
  // 0.52.0: assistant_message - the job could not reach the corporate model (Bridge 1.5.1 reason); user_message - sent
  // by "Resume all". engie*: legacy wire names (server and Bridge before Freedom F1 5b), read with corporateBlocked().
  corporateBlocked?: string;
  engieBlocked?: string;
  corporateResume?: boolean;
  engieResume?: boolean;
  // user_message (0.36.0): context mode of the turn and what the new/resumed session got (protocol, history, resume).
  contextMode?: 'full' | 'reference' | 'clean';
  contextPack?: 'resume' | 'protocol' | 'history' | 'none';
  contextReason?: string;
  // user_message (0.19.0): the job went through the database queue, not the Drive inbox.
  queue?: 'db';
  // user_message (0.60.0): a follow-up the server wrote after Bridge delivered it to the running job (no jobId of its own)
  followupOf?: string;
  // user_message (0.71.0): the server retried the failed job with this id on the other agent (autoswitch-lib.php)
  autoSwitchFrom?: string;
  // user_message and assistant_message (0.65.0, canvas M3): a job about comment threads of a document open in canvas;
  // its own agent session, shown as one collapsed row and left out of the chat conversation
  canvas?: { path: string; file: string };
  // assistant_message
  status?: string;
  sessionId?: string | null;
  artifacts?: ArtifactRecord[];
  missingArtifacts?: string[];
  error?: string | null;
  completedUtc?: string;
  // user_message (0.85.0): secretary hints sent with the message (other chats that matched its text)
  hints?: { chatId: string; projectId: string; env: string; msg: number }[];
  // assistant_message: chat title the agent proposed in its answer ([[chat-title: …]], stripped from text).
  autoTitle?: string;
  // 0.89.0 family chats: user_message of a person (no job unless @co was called); reaction - emoji on target event
  family?: boolean;
  emoji?: string;
  target?: string;
  remove?: boolean;
  replyTo?: string;
  // 0.89.0: @co while the agent was busy - the message waits (coPending) and its own turn starts later (coFor = its eventId)
  coPending?: boolean;
  coFor?: string;
}

export interface JobResult {
  schemaVersion: number;
  jobId: string;
  agent: string;
  conversationOperation?: string;
  status: string; // completed | failed | cancelled | timed_out | ...
  startedUtc?: string;
  completedUtc?: string;
  exitCode?: number;
  timedOut?: boolean;
  sessionId?: string | null;
  agentFinalResponse?: string | null;
  agentErrorMessages?: string[];
  error?: string | null;
  artifacts?: ArtifactRecord[];
  missingArtifacts?: string[];
  failedArtifacts?: unknown[];
  mode?: Mode;
  model?: string | null;
  effort?: string | null;
  modelReported?: string | null;
  usage?: Usage | null;
  exportSkipped?: ExportSkip[];
  // Bridge 1.5.1: why the corporate model was unavailable to this job (engieBlocked: legacy wire)
  corporateBlocked?: string | null;
  engieBlocked?: string | null;
}

export interface BridgeEvent {
  sequence: number;
  createdUtc: string;
  source: 'bridge' | 'agent' | string;
  type: string;
  content: string;
}

export interface CatalogModel {
  id: string;
  label?: string | null;
  description?: string | null;
  efforts?: string[] | null;
  defaultEffort?: string | null;
}

export interface OwnerAgent {
  displayName?: string;
  modes?: Mode[];
  models?: CatalogModel[];
  efforts?: string[];
  defaultModel?: string | null;
  defaultEffort?: string | null;
}

export interface OwnerProject {
  environmentId: string;
  allowedAgents: string[];
  allowedModes: Mode[];
  autoExport: boolean;
  workspaceDriveRelativePath?: string | null;
}

export interface OwnerInfo {
  hostId: string;
  machineName?: string;
  processId?: number;
  updatedUtc?: string;
  bridgeVersion?: string;
  capabilities?: string[];
  protocolMinor?: number;
  agents?: Record<string, OwnerAgent>;
  projects?: Record<string, OwnerProject>;
  chatProjectEnvironments?: Record<string, { allowedAgents: string[]; autoExport: boolean }>;
  contextRoots?: Record<string, { label: string; kind: 'local' | 'drive' }>;
  fastTransport?: { projectId: string; jobsTopic: string; eventsTopic: string; webSubscriptionPattern?: string; pubsub?: string; api?: string };
  devices?: OwnerDevice[]; // Bridge 1.4.1
  // 0.44.0: Bridge 1.4.2 keeps owner.json fresh while it takes no jobs and says why
  state?: 'running' | 'paused' | 'updating';
  updateTo?: string | null;
  runningJobs?: number;
  stateSinceUtc?: string;
}

// 0.42.0: paired CO Desktop device as Bridge 1.4.1 reports it in owner.json.
export interface OwnerDevice {
  deviceId: string;
  name: string;
  shortName: string;
  environmentId: string;
  version: string;
  lastHeartbeatUtc: string | null;
  online: boolean;
  corp?: { id?: string; state?: string } | null; // connector session (Freedom F1 5b), read with corpState()
  engie?: string; // legacy wire of corp.state: online|signin|offline|unknown|off, "" on CO Desktop before 0.7.13
}

export interface LimitWindow {
  id: string; // "session" | "week" | provider key
  key: string;
  windowMinutes: number;
  usedPercent: number;
  resetsAtUtc?: string | null;
  // Bridge 1.1.6, the corporate dispatcher's savings windows only: output tokens written by the corporate models
  // (corpTokens; engieTokens = legacy wire) and by Claude, jobs counted.
  corpTokens?: number | null;
  engieTokens?: number | null;
  claudeTokens?: number | null;
  jobs?: number | null;
}

export interface AgentLimits {
  source: string;
  observedUtc: string;
  status?: string | null;
  plan?: string | null;
  windows: LimitWindow[];
}

export interface LimitsFile {
  updatedUtc: string;
  agents: Record<string, AgentLimits>;
}

export type JobPhase =
  | 'preparing'
  | 'queued'
  | 'claimed'
  | 'running'
  | 'cancel_requested'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'rejected';
