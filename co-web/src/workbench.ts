// SPDX-License-Identifier: AGPL-3.0-or-later
// Domain layer over the AI-Workbench Drive tree (TZ section 7) and Bridge protocol v3.

import { QUICK_REPLIES_NOTE } from './quickReplies';
import { TITLE_REQUEST, extractTitle, stripTitleMarker } from './chatTitle';
import { publishJobCancel, publishJobReady, publishJobReadyDb } from './pubsub';
import {
  ALLOWLIST, ENVIRONMENTS, CONTEXT_PACKAGE_MAX_CHARS, DEFAULT_BRANCH, DEFAULT_TIMEOUT_MINUTES, HOST_ID,
  WORKBENCH_FOLDER_NAME, siteHost, type AgentId, type EnvironmentId,
} from './config';
import {
  FOLDER_MIME, children, copyFile, createFile, ensurePath, findChild, forgetFolder, getBytes, getJson, moveFile, trashFile,
  getText, list, q, resolveFile, resolveFolder, resolvePath, updateContent, type DriveFile,
} from './drive';
import type {
  AttachmentRef, BridgeEvent, Chat, ChatEvent, ContextFolder, FolderIndex, JobPhase, JobResult, LimitsFile, Mode, OwnerInfo, Project, ChatPermissions } from './types';
import { buildMemory, protocolUsable, type ContextMode } from './contextLevels';
import type { ChatProtocol } from './dbapi';
import { moduleList, sideJobsOf } from './moduleStore';
import { mapLimit, newChatId, newEventId, newJobId, nowUtc, safeFileName, sha256Hex, uniqueNames } from './util';
import { md5Hex } from './md5';
import { importSpeaker } from './importPoint';
import { gatewayBlocked, gatewayBlockedFields } from './gateway';
import { dbChats, dbDeleteChat, dbEvents, dbJobCancel, dbJobEvents, dbJobGet, dbJobSubmit, dbProjects, dbPut, dbReadsPrimary, dbGetProfile, dbSharedProjects } from './dbapi';
import { memoryIndex } from './memoryIndex';
import { drivePath, ownerEnv } from './members';
import { executorOf } from './executors';

// ---------- Root and host ----------

export interface Root {
  id: string;
}

export async function discoverRoot(): Promise<Root> {
  const found = await list(`name=${q(WORKBENCH_FOLDER_NAME)} and mimeType=${q(FOLDER_MIME)} and trashed=false`);
  for (const f of found) {
    if (await resolveFolder(f.id, 'transport')) return { id: f.id };
  }
  throw new Error(
    found.length
      ? 'AI-Workbench found, but it has no transport/ folder.'
      : 'AI-Workbench folder is not visible to this Google account. The owner must share it.',
  );
}

export interface HostDirs {
  hostId: string;
  hostDirId: string; // F12: '' for an executor reached through the database only (no Drive folder)
  buckets: Record<string, string>; // name -> folder id
}

// Freedom F12: an executor other than Home and Site lives in the database queue; no Drive folders are made for it.
export function dbOnlyHost(hostId: string): HostDirs {
  return { hostId, hostDirId: '', buckets: {} };
}

const BUCKETS = ['inbox', 'claimed', 'events', 'completed', 'failed', 'rejected', 'cancel'];

export async function loadHost(root: Root, hostId: string = HOST_ID): Promise<HostDirs> {
  const hostDirId = await ensurePath(root.id, ['transport', 'hosts', hostId]);
  const kids = await children(hostDirId);
  const buckets: Record<string, string> = {};
  for (const k of kids) if (k.mimeType === FOLDER_MIME && BUCKETS.includes(k.name)) buckets[k.name] = k.id;
  for (const b of BUCKETS) if (!buckets[b]) buckets[b] = await ensurePath(hostDirId, [b]);
  return { hostId, hostDirId, buckets };
}

export async function readOwner(host: HostDirs): Promise<OwnerInfo | null> {
  if (!host.hostDirId) return null;
  const f = await findChild(host.hostDirId, 'owner.json');
  return f ? getJson<OwnerInfo>(f.id) : null;
}

// Usage limits published by Bridge from provider output (Claude) and session logs (Codex).
export async function readLimits(host: HostDirs): Promise<LimitsFile | null> {
  if (!host.hostDirId) return null;
  const f = await findChild(host.hostDirId, 'limits.json');
  return f ? getJson<LimitsFile>(f.id) : null;
}

// 0.29.0: moved to routing.ts (pure, testable in node)
export { hostHealth, type HostHealth } from './routing';

// Freedom F12: every Bridge takes its jobs from the database queue and posts its events there (Home since 0.19.0, the
// other executors only so — a Linux server has no Drive); the site agent keeps its Drive inbox path.
export function usesDbQueue(hostId: string): boolean {
  return hostId !== 'site';
}

// Executor of a chat event; events written before the host switch belong to home-main.
export function eventHost(e: ChatEvent): string {
  return e.hostId || HOST_ID;
}


// ---------- Projects ----------

export async function listProjects(root: Root, env: EnvironmentId, owner?: OwnerInfo | null): Promise<Project[]> {
  // 0.15.0: project.json data from the database (one call, in parallel with the folder listing); Drive for the rest
  const dbp = dbProjects(env);
  // 0.45.0: projects shared with this environment, in parallel (never rejects)
  const dbs = dbSharedProjects(env);
  const projectsId = await ensurePath(root.id, ['projects']);
  const folders = (await children(projectsId)).filter((f) => f.mimeType === FOLDER_MIME);
  const fromDb = new Map<string, Omit<Project, 'folderId'>>();
  for (const r of (await dbp) || []) if (typeof r?.projectId === 'string') fromDb.set(r.projectId, r as Omit<Project, 'folderId'>);
  const all = await mapLimit(folders, 6, async (f): Promise<Project> => {
    const known = fromDb.get(f.name);
    if (known) return { ...known, folderId: f.id };
    const pj = await findChild(f.id, 'project.json');
    if (pj) {
      const p = await getJson<Omit<Project, 'folderId'>>(pj.id);
      return { ...p, folderId: f.id };
    }
    // Projects created before CO have no project.json; they belong to the owner.
    return {
      projectId: f.name, environmentId: ownerEnv(), title: f.name, defaultAgentId: 'claude',
      createdUtc: f.createdTime || '', updatedUtc: f.modifiedTime || '', archived: false,
      legacy: true, folderId: f.id,
    };
  });
  // A legacy folder takes the environment declared for it in the Bridge config.
  for (const p of all) {
    const reg = owner?.projects?.[p.projectId];
    if (p.legacy && reg) p.environmentId = reg.environmentId as EnvironmentId;
  }
  const own = all
    .filter((p) => p.environmentId === env) // 0.25.0: archived ones too (Shell shows them under "Archived projects")
    .sort((a, b) => (b.updatedUtc || '').localeCompare(a.updatedUtc || ''));
  // 0.45.0: append projects shared with this environment (not owned, not archived)
  const seen = new Set(own.map((p) => p.projectId));
  const shared: Project[] = [];
  for (const row of await dbs) {
    const r = row as Record<string, any>;
    if (typeof r?.projectId !== 'string' || typeof r?.folderId !== 'string') continue;
    if (r.environmentId === env || seen.has(r.projectId) || r.archived) continue;
    seen.add(r.projectId);
    shared.push({ ...(r as Omit<Project, 'folderId'>), folderId: r.folderId } as Project);
  }
  shared.sort((a, b) => (b.updatedUtc || '').localeCompare(a.updatedUtc || ''));
  return [...own, ...shared];
}

// Bridge projects of this environment that have no folder in Drive yet.
export function unregisteredBridgeProjects(owner: OwnerInfo | null, env: EnvironmentId, existing: Project[]): string[] {
  const have = new Set(existing.map((p) => p.projectId));
  return Object.entries(owner?.projects || {})
    .filter(([id, p]) => p.environmentId === env && !have.has(id))
    .map(([id]) => id);
}

export async function createProject(
  root: Root,
  input: { projectId: string; environmentId: EnvironmentId; title: string; description: string; defaultAgentId: AgentId } & Record<string, unknown>,
): Promise<Project> {
  const projectsId = await ensurePath(root.id, ['projects']);
  if (await findChild(projectsId, input.projectId, true)) throw new Error(`Project ${input.projectId} already exists.`);
  const folderId = await ensurePath(projectsId, [input.projectId]);
  const now = nowUtc();
  const p = { ...input, createdUtc: now, updatedUtc: now, archived: false };
  await createFile(folderId, 'project.json', JSON.stringify(p, null, 2), 'application/json');
  await ensurePath(folderId, ['context']);
  await ensurePath(folderId, ['files']);
  await ensurePath(folderId, ['chats']);
  void dbPut({ projects: [{ ...p, folderId }] });
  return { ...p, folderId };
}

// Writes project.json for a legacy folder so that it gets an explicit environment.
export async function adoptProject(p: Project): Promise<Project> {
  if (!p.legacy) return p;
  const now = nowUtc();
  const doc = {
    projectId: p.projectId, environmentId: p.environmentId, title: p.title, description: '',
    defaultAgentId: p.defaultAgentId, createdUtc: p.createdUtc || now, updatedUtc: now, archived: false,
  };
  await createFile(p.folderId, 'project.json', JSON.stringify(doc, null, 2), 'application/json');
  void dbPut({ projects: [{ ...doc, folderId: p.folderId }] });
  return { ...doc, folderId: p.folderId };
}

export async function readProjectInstructions(p: Project): Promise<{ text: string; fileId: string | null }> {
  return readProjectContextFile(p, 'project.md');
}

// 0.26.0: files of the project's context folder (project.md = instructions, memory.md = project memory).
export async function readProjectContextFile(p: Project, name: string): Promise<{ text: string; fileId: string | null }> {
  const ctx = await resolveFolder(p.folderId, 'context');
  if (!ctx) return { text: '', fileId: null };
  const f = await findChild(ctx, name);
  return f ? { text: await getText(f.id), fileId: f.id } : { text: '', fileId: null };
}

export async function saveProjectContextFile(p: Project, name: string, text: string, fileId: string | null): Promise<string> {
  if (fileId) {
    await updateContent(fileId, text, 'text/markdown');
    return fileId;
  }
  const ctx = await ensurePath(p.folderId, ['context']);
  return (await createFile(ctx, name, text, 'text/markdown')).id;
}

// 0.26.0: project memory for new agent sessions: context/memory.md plus automatic summaries of the 5 newest other
// chats of the project. '' when there is nothing. 0.73.0: indexOk (the agent can run aiwb-memory on the home PC) sends
// an index of memory.md instead of the whole file when the project asks for it (memoryIndex.ts).
export async function projectMemoryBlock(p: Project, currentChatId: string, indexOk = false): Promise<string> {
  const [mem, rows] = await Promise.all([
    readProjectContextFile(p, 'memory.md').catch(() => ({ text: '', fileId: null })),
    p.legacy ? Promise.resolve(null) : dbChats(p.projectId),
  ]);
  const recent = ((rows || []) as Chat[])
    .filter((c) => c.chatId !== currentChatId && !c.archived && c.summary)
    .sort((a, b) => (b.updatedUtc || '').localeCompare(a.updatedUtc || ''))
    .slice(0, 5)
    .map((c) => `- ${c.title} (${(c.updatedUtc || '').slice(0, 10)}): ${c.summary}`);
  const parts: string[] = [];
  const index = indexOk && !p.legacy ? memoryIndex(mem.text, p.projectId) : null;
  if (index) parts.push(index);
  else if (mem.text.trim()) parts.push(mem.text.trim());
  if (recent.length) parts.push('Recent chats in this project (automatic summaries):\n' + recent.join('\n'));
  return parts.length ? `[Project memory]\n${parts.join('\n\n')}\n[End of project memory]` : '';
}

export async function saveProjectInstructions(p: Project, text: string, fileId: string | null): Promise<string> {
  if (fileId) {
    // Drive keeps revision history of the overwritten content.
    await updateContent(fileId, text, 'text/markdown');
    return fileId;
  }
  const ctx = await ensurePath(p.folderId, ['context']);
  const f = await createFile(ctx, 'project.md', text, 'text/markdown');
  return f.id;
}

export async function listProjectFiles(p: Project): Promise<DriveFile[]> {
  const files = await resolveFolder(p.folderId, 'files');
  if (!files) return [];
  return (await children(files)).filter((f) => f.mimeType !== FOLDER_MIME).sort((a, b) => a.name.localeCompare(b.name));
}

export async function uploadProjectFile(p: Project, file: File): Promise<DriveFile> {
  const files = await ensurePath(p.folderId, ['files']);
  return createFile(files, safeFileName(file.name), file, file.type || 'application/octet-stream');
}

// ---------- Chats ----------

// 0.45.0: a project shared via chat only exposes its shared chats
function onlySharedChats(p: Project, list: Chat[]): Chat[] {
  if (p.shareVia !== 'chat') return list;
  const ids = new Set(p.sharedChatIds || []);
  return list.filter((c) => ids.has(c.chatId));
}

// 0.48.0: env = environment being viewed (admin preview gets that env's chats and roles)
export async function listChats(p: Project, env?: string): Promise<Chat[]> {
  // 0.15.0: chat.json data from the database (one call); Drive for chats it does not have yet
  const dbc = p.legacy ? Promise.resolve(null) : dbChats(p.projectId, env);
  if (dbReadsPrimary() && !p.legacy) try {
    // 0.18.0: chats with their Drive folder ids straight from the database (no Drive call)
    const rows = await dbc;
    if (rows && rows.every((r) => typeof r?.folderId === 'string' && r.projectId === p.projectId)) {
      const byId = new Map((rows as Chat[]).map((c) => [c.chatId, c]));
      for (const [id, x] of localChats) {
        if (Date.now() - x.at > 600000) {
          localChats.delete(id);
          continue;
        }
        const db = byId.get(id);
        if (x.chat.projectId === p.projectId && (!db || (db.updatedUtc || '') < (x.chat.updatedUtc || ''))) byId.set(id, x.chat);
        else if (x.chat.projectId !== p.projectId && db) byId.delete(id); // moved away in this browser
      }
      return onlySharedChats(p, [...byId.values()].sort((a, b) => (b.updatedUtc || b.createdUtc || '').localeCompare(a.updatedUtc || a.createdUtc || '')));
    }
  } catch (e) {
    console.warn('database read failed, using Drive', e);
  }
  const chatsId = await resolveFolder(p.folderId, 'chats');
  if (!chatsId) return [];
  const folders = (await children(chatsId)).filter((f) => f.mimeType === FOLDER_MIME);
  const fromDb = new Map<string, Omit<Chat, 'folderId'>>();
  for (const r of (await dbc) || []) {
    if (typeof r?.chatId === 'string' && r.projectId === p.projectId) fromDb.set(r.chatId, r as Omit<Chat, 'folderId'>);
  }
  const chats = await mapLimit(folders, 6, async (f): Promise<Chat> => {
    const known = fromDb.get(f.name);
    if (known) return { ...known, folderId: f.id };
    const cj = await findChild(f.id, 'chat.json');
    if (cj) return { ...(await getJson<Omit<Chat, 'folderId'>>(cj.id)), folderId: f.id };
    return {
      chatId: f.name, projectId: p.projectId, title: f.name, rootBranchId: DEFAULT_BRANCH,
      createdUtc: f.createdTime || '', updatedUtc: f.modifiedTime || '', legacy: true, folderId: f.id,
    };
  });
  return onlySharedChats(p, chats.sort((a, b) => (b.updatedUtc || b.createdUtc).localeCompare(a.updatedUtc || a.createdUtc)));
}

export async function createChat(p: Project, title: string, defaultMode: Mode = 'cowork'): Promise<Chat> {
  const chatsId = await ensurePath(p.folderId, ['chats']);
  const chatId = newChatId();
  const folderId = await ensurePath(chatsId, [chatId]);
  const now = nowUtc();
  const c = { chatId, projectId: p.projectId, title, rootBranchId: DEFAULT_BRANCH, createdUtc: now, updatedUtc: now, defaultMode };
  await createFile(folderId, 'chat.json', JSON.stringify(c, null, 2), 'application/json');
  await ensurePath(folderId, ['events']);
  // 0.59.0: scratch exists in the cloud before the first job, so Bridge waits for it on G: instead of creating a
  // second folder of the same name (Drive for desktop had not pulled the chat folder yet, 29.10).
  await ensurePath(folderId, ['scratch']);
  // Awaited (never throws): the first event's database write needs the chat row.
  await dbPut({ chats: [{ ...c, folderId }] });
  rememberChat({ ...c, folderId });
  return { ...c, folderId };
}

// 0.21.0: with database-first reads a write goes to the database first and to Drive in the background. Drive writes of
// one chat run one after another, so an older chat.json never lands after a newer one (drive-sync would copy it back).
const driveChains = new Map<string, Promise<void>>();
function driveLater(key: string, what: string, fn: () => Promise<unknown>): void {
  const run = async () => {
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        await fn();
        return;
      } catch (e) {
        if (attempt === 2) console.warn('Drive copy failed:', what, e);
        else await new Promise((r) => setTimeout(r, 3000 * (attempt + 1)));
      }
    }
  };
  const next = (driveChains.get(key) || Promise.resolve()).then(run);
  driveChains.set(key, next);
  void next.finally(() => {
    if (driveChains.get(key) === next) driveChains.delete(key);
  });
}

// patch: fields of the chat document, plus the fields of chat modes of modules (<key>, <key>Log, <key>Doc; F10)
export async function updateChatMeta(chat: Chat, patch: Partial<Pick<Chat, 'title' | 'titleAuto' | 'defaultMode' | 'projectId' | 'movedUtc' | 'permissions' | 'archived' | 'parent' | 'contextMode' | 'autoSwitch' | 'browser' | 'family'>> & Record<string, unknown>): Promise<Chat> {
  const next = { ...chat, ...patch, updatedUtc: nowUtc() };
  // 0.45.0: shareRole is server-only, never written to chat.json
  const { folderId, legacy, shareRole, ...doc } = next;
  void folderId;
  void legacy;
  void shareRole;
  const writeDrive = async () => {
    const cj = await findChild(chat.folderId, 'chat.json');
    if (cj) await updateContent(cj.id, JSON.stringify(doc, null, 2), 'application/json');
    else await createFile(chat.folderId, 'chat.json', JSON.stringify(doc, null, 2), 'application/json');
  };
  rememberChat(next);
  if (dbReadsPrimary() && !chat.legacy && (await dbPut({ chats: [{ ...doc, folderId: chat.folderId }] }))) {
    driveLater(chat.chatId, 'chat.json', writeDrive);
    return next;
  }
  await writeDrive();
  void dbPut({ chats: [{ ...doc, folderId: chat.folderId }] });
  return next;
}

// ---------- Canonical chat events (one immutable file per event) ----------

const eventCache = new Map<string, ChatEvent>(); // Drive fileId -> event
const segmentCache = new Map<string, ChatEvent[]>(); // Drive fileId -> sealed segment

// 0.18.0: chats this browser created or changed in the last 10 minutes, merged into database-first lists until the
// database has the same or a newer version.
const localChats = new Map<string, { chat: Chat; at: number }>(); // chatId -> chat
function rememberChat(chat: Chat) {
  localChats.set(chat.chatId, { chat, at: Date.now() });
}

// 0.18.0: events this browser wrote in the last 10 minutes, merged into database-first reads until the database has
// them (write-through failed or is still in flight).
const localWrites = new Map<string, { ev: ChatEvent; at: number }[]>(); // chatId -> events
function rememberLocal(ev: ChatEvent) {
  const list = (localWrites.get(ev.chatId) || []).filter((x) => Date.now() - x.at < 600000);
  list.push({ ev, at: Date.now() });
  localWrites.set(ev.chatId, list);
}

function withArtifactPaths(chat: Chat, events: ChatEvent[]): ChatEvent[] {
  return events.map((e) =>
    e.artifacts?.length
      ? { ...e, artifacts: e.artifacts.map((a) => ({ ...a, driveRelativePath: currentArtifactPath(chat, a.driveRelativePath) })) }
      : e,
  );
}

export async function loadChatEvents(chat: Chat): Promise<ChatEvent[]> {
  // 0.18.0: the database is the primary read source (no Drive call); imported chats keep their sealed segments on Drive.
  if (!chat.importedFrom && !chat.legacy) {
    // 0.24.0: messages live only in the database (old Drive message files were removed).
    const rows = await dbEvents(chat.chatId);
    if (rows) {
      const byId = new Map<string, ChatEvent>();
      for (const r of rows) if (typeof r?.eventId === 'string') byId.set(r.eventId, r as ChatEvent);
      for (const x of localWrites.get(chat.chatId) || []) {
        if (Date.now() - x.at < 600000 && !byId.has(x.ev.eventId)) byId.set(x.ev.eventId, x.ev);
      }
      const sorted = [...byId.values()].sort((a, b) => a.sequence - b.sequence || a.createdUtc.localeCompare(b.createdUtc));
      return withArtifactPaths(chat, sorted);
    }
    throw new Error('Messages are stored in the CO database, which did not answer. Try again in a minute.');
  }
  const evDir = await resolveFolder(chat.folderId, 'events');
  if (!evDir) return [];
  const files = (await children(evDir)).filter((f) => f.name.endsWith('.json'));
  // 0.15.0: events not in the cache come from the database in one call; files it does not have yet (written in
  // the last minute) are downloaded from Drive as before.
  const missing = files.filter((f) => !f.name.endsWith('.segment.json') && !eventCache.has(f.id));
  if (missing.length > 2) {
    const fromDb = new Map<string, ChatEvent>();
    for (const r of (await dbEvents(chat.chatId)) || []) if (typeof r?.eventId === 'string') fromDb.set(r.eventId, r as ChatEvent);
    for (const f of missing) {
      const m = /(EVT-[A-Za-z0-9-]+)\.json$/.exec(f.name);
      const ev = m ? fromDb.get(m[1]) : undefined;
      if (ev) eventCache.set(f.id, ev);
    }
  }
  const segCache = segmentCache;
  const loaded = await mapLimit(files, 6, async (f): Promise<ChatEvent[]> => {
    if (f.name.endsWith('.segment.json')) {
      // Sealed segment: many immutable events in one file (imports, TZ section 8).
      const cachedSeg = segCache.get(f.id);
      if (cachedSeg) return cachedSeg;
      try {
        const seg = await getJson<{ events: ChatEvent[] }>(f.id);
        segCache.set(f.id, seg.events || []);
        return seg.events || [];
      } catch {
        return [];
      }
    }
    const cached = eventCache.get(f.id);
    if (cached) return [cached];
    try {
      const e = await getJson<ChatEvent>(f.id);
      eventCache.set(f.id, e);
      return [e];
    } catch {
      return []; // file still syncing or malformed; picked up on the next load
    }
  });
  const sorted = loaded.flat().sort(
    (a, b) => a.sequence - b.sequence || a.createdUtc.localeCompare(b.createdUtc),
  );
  return sorted.map((e) =>
    e.artifacts?.length
      ? { ...e, artifacts: e.artifacts.map((a) => ({ ...a, driveRelativePath: currentArtifactPath(chat, a.driveRelativePath) })) }
      : e,
  );
}

export async function appendEvent(
  chat: Chat,
  existing: ChatEvent[],
  partial: Omit<ChatEvent, 'schemaVersion' | 'eventId' | 'sequence' | 'createdUtc' | 'chatId' | 'branchId'> & { branchId?: string },
): Promise<ChatEvent> {
  const sequence = existing.reduce((m, e) => Math.max(m, e.sequence), 0) + 1;
  const ev: ChatEvent = {
    schemaVersion: 1, eventId: newEventId(), sequence, createdUtc: nowUtc(),
    chatId: chat.chatId, branchId: partial.branchId || DEFAULT_BRANCH, ...partial,
  };
  const name = `${String(sequence).padStart(6, '0')}-${ev.type}-${ev.eventId}.json`;
  const writeDrive = async () => {
    const evDir = await ensurePath(chat.folderId, ['events']);
    const f = await createFile(evDir, name, JSON.stringify(ev, null, 2), 'application/json');
    eventCache.set(f.id, ev);
  };
  rememberLocal(ev);
  // 0.24.0: messages live only in the database; the Drive file is written only when the database call fails
  // (drive-sync imports it later).
  if (!chat.legacy && !chat.importedFrom && (await dbPut({ events: [ev] }))) return ev;
  await writeDrive();
  void dbPut({ events: [ev] });
  return ev;
}

// ---------- Conversation / context package ----------

export interface LiveContext {
  protocol: ChatProtocol | null;
  contextMode: ContextMode;
  title: string;
  chatId: string;
  parentProtocol?: string;
  nowMs: number;
}

export interface ConversationPlan {
  operation: 'new' | 'resume';
  sessionId: string | null;
  preamble: string;
  contextPack: 'resume' | 'protocol' | 'history' | 'none';
  reason?: string;
}

// 0.36.0 (live protocol): with a usable protocol a session is resumed only within 30 min and below 150k per turn.
const RESUME_MAX_PAUSE_MS = 30 * 60 * 1000;
const RESUME_MAX_SESSION_CONTEXT = 150000;

function agentName(a?: string) {
  return a === 'codex' ? 'Codex' : a === 'claude' ? 'Claude' : a || 'Agent';
}

// 0.89.0: a person's name by Google identity (family chats)
export function familyName(email?: string): string {
  const env = email ? ALLOWLIST[email.toLowerCase()]?.env : undefined;
  return (env && ENVIRONMENTS.find((x) => x.id === env)?.displayName) || email || 'Someone';
}

export function speaker(e: ChatEvent): string {
  if (e.imported) return importSpeaker(e.imported.source);
  return agentName(e.agent);
}

function transcript(events: ChatEvent[]): string[] {
  const out: string[] = [];
  for (const e of events) {
    if (e.type === 'user_message' && (e.text || (e.family && e.attachments?.length))) {
      const att = e.attachments?.length ? `\n(attachments: ${e.attachments.map((a) => a.displayName).join(', ')})` : '';
      // 0.89.0: family chats - people write to each other; a message names its author (and @co when it called the agent)
      if (e.family) {
        if (e.coFor) continue;
        out.push(`${familyName(e.author)}${e.jobId ? ' → @co' : ''}:\n${e.text || ''}${att}`);
        continue;
      }
      const where = e.imported && e.attachments?.some((x) => x.path) ? ' — copies are in ./imported/' : '';
      out.push(e.imported ? `User:\n${e.text}${att}${where}` : `User → ${agentName(e.agent)}:\n${e.text}${att}`);
    } else if (e.type === 'assistant_message') {
      const files = e.imported && e.artifacts?.length
        ? `\n(files produced: ${e.artifacts.map((x) => x.sourceRelativePath).join(', ')}; copies are in ./imported/)`
        : '';
      out.push(`${speaker(e)}${e.imported ? '' : ` (${e.status})`}:\n${stripTitleMarker(e.text || '') || e.error || ''}${files}`);
    }
  }
  return out;
}

// 0.89.0: the newest messages of a family chat with their authors' names (hidden preamble of an @co turn)
export function familyTranscript(events: ChatEvent[], budget = 12000): string {
  return fit(transcript(events.filter((e) => e.type === 'user_message' || e.type === 'assistant_message').slice(-40)), budget);
}

function fit(blocks: string[], budget: number): string {
  // Keep the most recent turns; drop from the oldest.
  const kept: string[] = [];
  let used = 0;
  for (let i = blocks.length - 1; i >= 0; i--) {
    if (used + blocks[i].length > budget) {
      kept.unshift('[… earlier turns omitted …]');
      break;
    }
    kept.unshift(blocks[i]);
    used += blocks[i].length;
  }
  return kept.join('\n\n');
}

export function planConversation(
  events: ChatEvent[],
  agent: AgentId,
  levels: string,
  forceNew = false,
  mode: Mode = 'cowork',
  sessionsSinceUtc?: string,
  hostId: string = HOST_ID,
  live?: LiveContext,
): ConversationPlan {
  let lastIdx = -1;
  if (!forceNew) {
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      // A move to another project changes the working directory of both modes.
      if (sessionsSinceUtc && (e.completedUtc || e.createdUtc) < sessionsSinceUtc) break;
      // Provider sessions are bound to the working directory, so resume needs the same mode.
      // Provider sessions live on the machine that ran them, so resume also needs the same host.
      if (e.type === 'assistant_message' && e.agent === agent && e.sessionId && (e.mode || 'cowork') === mode && eventHost(e) === hostId) {
        lastIdx = i;
        break;
      }
    }
  }

  let reason: string | undefined;
  if (lastIdx >= 0 && live) {
    const cand = events[lastIdx];

    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      if (e.type !== 'user_message') continue;
      if (e.conversation?.sessionId === cand.sessionId || (cand.jobId && e.jobId === cand.jobId)) {
        if ((e.contextMode ?? 'full') !== live.contextMode) reason = 'context mode changed';
        break;
      }
    }

    if (!reason && protocolUsable(live.protocol)) {
      const lastMs = Date.parse(cand.completedUtc || cand.createdUtc);
      if (live.nowMs - lastMs > RESUME_MAX_PAUSE_MS) {
        reason = 'pause over 30 min';
      } else if (cand.usage) {
        const u = cand.usage;
        const perTurn = (u.inputTokens + u.cachedInputTokens + (u.cacheCreationInputTokens || 0)) / Math.max(1, u.numTurns || 1);
        if (perTurn > RESUME_MAX_SESSION_CONTEXT) reason = 'session context over 150k';
      }
    }

    if (reason) lastIdx = -1;
  }

  if (lastIdx >= 0) {
    const later = transcript(events.slice(lastIdx + 1));
    const preamble = later.length
      ? `[CO context: ${events.slice(lastIdx + 1).some((e) => e.family) ? 'the family chat continued' : 'the conversation continued with another agent'} since your last turn]\n\n${fit(later, CONTEXT_PACKAGE_MAX_CHARS)}\n\n[End of CO context]\n\n`
      : '';
    return { operation: 'resume', sessionId: events[lastIdx].sessionId!, preamble, contextPack: 'resume' };
  }

  const parts: string[] = [];
  if (levels.trim()) parts.push(levels);

  let contextPack: ConversationPlan['contextPack'];
  if (live) {
    const mem = buildMemory(events, live.protocol, {
      title: live.title,
      chatId: live.chatId,
      minTail: 6,
      maxChars: CONTEXT_PACKAGE_MAX_CHARS,
      retrievalHint: hostId === HOST_ID,
      parentProtocol: live.parentProtocol,
    });
    const hasText = mem.text.trim().length > 0;
    if (hasText) parts.push(mem.text);
    contextPack = mem.usedProtocol ? 'protocol' : hasText ? 'history' : 'none';
  } else {
    const history = transcript(events);
    if (history.length) {
      parts.push(
        `[CO context: earlier conversation in this chat; you are starting a new session]\n\n${fit(history, CONTEXT_PACKAGE_MAX_CHARS)}\n\n[End of CO context]`,
      );
    }
    contextPack = history.length ? 'history' : 'none';
  }

  return {
    operation: 'new',
    sessionId: null,
    preamble: parts.length ? parts.join('\n\n') + '\n\nCurrent request:\n' : '',
    contextPack,
    ...(reason ? { reason } : {}),
  };
}

// Tells the agent where it runs. Sent once per provider session: with every new session and
// with the first resumed turn of a session that started before the note existed.
// clean (0.36.0, Clean view): without project and chat names and without the pointer to the system docs.
export function workbenchNote(hostId: string, project: Project, chat: Chat, mode: Mode, clean = false): string {
  const chatDir = `AI-Workbench/projects/${project.projectId}/chats/${chat.chatId}`;
  const executor =
    hostId === 'site'
      ? `the server agent on ${siteHost()} (shared hosting, Linux, no root). ` +
        'The home computer, its files and ' +
        'CO Desktop devices are not reachable from here, and Google Drive is not mounted: the working directory is a local ' +
        'folder on the server.'
      : hostId && hostId !== HOST_ID
        ? otherExecutorNote(hostId)
        : `the home computer (Windows, BridgeAgent). Google Drive is mounted as ${drivePath()}, so ${chatDir} is ` +
          `${drivePath(chatDir)}.`;
  const workdir = project.legacy
    ? 'Your working directory is the workspace of this project.'
    : "Your working directory is this chat's scratch folder.";
  return [
    '[AI Workbench environment]',
    'You work inside AI Workbench: a personal system in which the user chats with Claude and GPT agents that run on the ' +
      `user's own subscriptions. The user writes in CO (https://${siteHost()} or the CO Desktop app); each message reaches ` +
      'you as a job, and your final answer is shown in the chat.',
    `- Executor: ${executor}`,
    ...(clean ? [] : [`- Project "${project.title}" (${project.projectId}), chat "${chat.title}" (${chat.chatId}), mode ${mode}.`]),
    `- Chat history is stored in the CO database on ${siteHost()}; attachments and results are files in Google Drive under ${chatDir}/. ${workdir}`,
    // 0.55.0: chat files as version streams (one live copy, versions in the Drive history)
    project.legacy
      ? '- Files you create or change in the working directory are copied into the chat as downloadable artifacts.'
      : '- Files of this chat live in the working directory, one copy each; the chat shows the files you create or change, and their ' +
        'older versions stay in the Drive version history (30 days). A file the user uploads again under a known name is the ' +
        "user's new version of that file, written over it. Keep the names of files you revise. Put throwaway scripts and " +
        'intermediate outputs in ./tmp/ (code, logs and tmp/ files are deleted 30 days after their last change); code worth ' +
        'keeping goes to git or the tool library.',
    '- Earlier turns may have been answered by another agent or on another executor; they reach you as CO context.',
    // 0.60.0: follow-ups and the rule after 29.10 (an agent killed the parallel jobs of its chat with taskkill)
    '- Messages the user sends while you work reach you as "[Follow-up from the user, sent while you were working on this request]" ' +
      'after your current step; take them into account in the same answer. Never stop, kill or restart processes of other ' +
      'AI Workbench jobs (BridgeAgent, other claude/codex processes, their browsers), even if a request looks duplicated; ' +
      'say so in your answer instead.',
    QUICK_REPLIES_NOTE, // 0.57.0
    // 0.41.0: tool library line
    `- Tool library: reusable CLI tools with SKILL.md docs, available via the MCP server "aiwb-tools" (tool_list, tool_help, tool_run); ${
      hostId === 'site'
        ? 'here it is a synced copy, without tools marked platform: windows'
        : hostId && hostId !== HOST_ID
          ? 'the copy installed on this computer, if any (toolbox.py search)'
          : `working copy C:\\AIWorkbench\\tools (create and edit tools only there, via toolbox.py), read-only mirror ${drivePath('AI-Workbench/tools')}, rules in tools\\README.md`
    }. Before writing code, check it (tool_list or toolbox.py search) and use a fitting tool after reading its help; extend existing tools instead of rewriting, save reusable new code as a tool, and keep secrets only in keyring.`,
    // S5 (0.99.0): model resources and slaves (aiwb-subtask 0.6.0, db.php subtask_spawn)
    '- Other models as subagents: tool aiwb-subtask, "models" lists the model resources this chat may call (own ' +
      'subscriptions, the site, family models allowed as slaves, confidential ones) with capability cards; "spawn --resource ID" ' +
      'runs a part of the task there as a child chat, "wait" collects the answers. Use it when a part suits another model ' +
      'better or parts can run in parallel; confidential data stays with resources of its own environment.',
    ...(clean ? [] : ['- AI-Workbench/CONTEXT.md is the overview of the system itself; AI-Workbench/context/*.md hold the details per part (devices, release, site and database, …).']),
    '[End of AI Workbench environment]',
  ].join('\n');
}

// Freedom F12: an executor other than Home and Site (a Bridge on another machine: a Linux server, a Mac, another PC)
function otherExecutorNote(hostId: string): string {
  const e = executorOf(hostId);
  const os = e ? ({ windows: 'Windows', macos: 'macOS', linux: 'Linux' } as Record<string, string>)[e.os] : undefined;
  return `the computer "${e?.title || hostId}" (${os ? `${os}, ` : ''}BridgeAgent, executor ${hostId}). This is not the home ` +
    'computer: its files are its own, and Google Drive may not be mounted there; work in the working directory below.';
}

// True when the provider session already received the environment note.
function sessionHasNote(events: ChatEvent[], sessionId: string): boolean {
  const jobs = new Set(events.filter((e) => e.type === 'assistant_message' && e.sessionId === sessionId).map((e) => e.jobId));
  return events.some((e) => e.type === 'user_message' && e.workbenchNote && (jobs.has(e.jobId) || e.conversation?.sessionId === sessionId));
}

// ---------- Sending a job (protocol v3) ----------

export interface SendInput {
  root: Root;
  host: HostDirs;
  env: EnvironmentId;
  project: Project;
  chat: Chat;
  events: ChatEvent[];
  agent: AgentId;
  text: string;
  files: File[];
  // 0.55.0: per file the scratch-relative path it becomes (a chat file); null/absent = staged under .co-attachments
  fileTargets?: (string | null)[];
  returnFiles: string[];
  projectInstructions: string;
  // 0.36.0: prebuilt context levels 1-4 (used instead of projectInstructions) and the live protocol context.
  levels?: string;
  live?: Omit<LiveContext, 'nowMs'>;
  author: string;
  forceNewSession?: boolean;
  mode?: Mode;
  model?: string | null;
  effort?: string | null;
  protocolMinor?: number;
  autoExport?: boolean;
  contextFolders?: ContextFolder[];
  // CO Desktop devices whose tools the Run may use (protocol v3 minor 2, TZ-CO-DESKTOP §8); 0.49.0: also w- web devices.
  devices?: string[];
  // 0.37.0: device tools hidden from the Run (Bridge 1.3.5 "device-tools-deny")
  deviceToolsDeny?: string[];
  permissions?: ChatPermissions;
  // 0.19.0: the host takes jobs from the database queue (Bridge 1.3.0 "db-queue"); jobs with files still use Drive.
  dbQueue?: boolean;
  // S8: chosen by the 'auto' preference: the server may move the job to the other executor when none took it in ~60 s
  hostAuto?: boolean;
  // 0.76.0: hidden preamble after a broken-off job of the chat (workPlan.ts resumeBlock); not part of the shown text
  resume?: string;
  // 0.85.0: hidden secretary hints block (secretaryHints.ts) and the hits the user message keeps for the UI
  hints?: string;
  hintsStored?: ChatEvent['hints'];
  // 0.89.0: extra keys of the user_message (family: true in family chats)
  extra?: Partial<ChatEvent>;
  onProgress?: (msg: string) => void;
}

export async function sendTurn(input: SendInput): Promise<ChatEvent> {
  const { host, project, chat, agent, text, onProgress } = input;
  const jobId = newJobId();
  const branchId = DEFAULT_BRANCH;
  const mode: Mode = input.mode || 'cowork';
  const levels = input.levels ?? (input.projectInstructions.trim() ? `[Project instructions]\n${input.projectInstructions.trim()}\n[End of project instructions]` : '');
  const plan = planConversation(input.events, agent, levels, input.forceNewSession, mode, chat.movedUtc, host.hostId, input.live ? { ...input.live, nowMs: Date.now() } : undefined);
  const withNote = plan.operation === 'new' || !sessionHasNote(input.events, plan.sessionId!);
  const note = withNote ? `${workbenchNote(host.hostId, project, chat, mode, input.live?.contextMode === 'clean')}\n\n` : '';

  // 1. Attachments: persistent copy first, then server-side copy into the request package.
  const attachments: AttachmentRef[] = [];
  let pkgId: string | null = null;
  if (input.files.length) {
    if (!host.hostDirId) throw new Error(`Files reach only Home and Site for now; ${host.hostId} takes text jobs. Choose Home or Site for this message.`);
    const names = uniqueNames(input.files.map((f) => safeFileName(f.name)));
    pkgId = await ensurePath(host.buckets.inbox, [jobId]);
    const pkgAtt = await ensurePath(pkgId, ['attachments']);
    let persistDir: string | null = null;
    for (let i = 0; i < input.files.length; i++) {
      const file = input.files[i];
      const target = input.fileTargets?.[i] || null;
      onProgress?.(`Hashing ${file.name}…`);
      const bytes = await file.arrayBuffer();
      const sha256 = await sha256Hex(bytes);
      onProgress?.(`Uploading ${file.name}…`);
      if (target) {
        // 0.55.0: one copy. The executor writes the package copy into the chat scratch (a new version of that chat
        // file); no persistent copy in branches/<b>/attachments.
        const stored = await createFile(pkgAtt, names[i], file, file.type || 'application/octet-stream');
        attachments.push({
          displayName: file.name, path: names[i], sizeBytes: file.size, sha256, fileId: stored.id,
          target, streamPath: `projects/${project.projectId}/chats/${chat.chatId}/scratch/${target}`, md5: md5Hex(bytes),
        });
        continue;
      }
      persistDir ??= await ensurePath(chat.folderId, ['branches', branchId, 'attachments', jobId]);
      const stored = await createFile(persistDir, names[i], file, file.type || 'application/octet-stream');
      await copyFile(stored.id, pkgAtt, names[i]);
      attachments.push({ displayName: file.name, path: names[i], sizeBytes: file.size, sha256, fileId: stored.id });
    }
  }

  // 2. request.json, then 3. request.ready (commit marker) last.
  onProgress?.('Queuing job…');
  const request = {
    schemaVersion: 3,
    hostId: host.hostId,
    jobId,
    environmentId: input.env,
    projectId: project.projectId,
    chatId: chat.chatId,
    branchId,
    agent,
    instruction: note + plan.preamble + (input.resume || '') + (input.hints || '') + text + (wantsTitle(chat, input.events) ? TITLE_REQUEST : ''),
    conversation: { operation: plan.operation, sessionId: plan.sessionId },
    attachments: attachments.map((a) => ({ path: a.path, sizeBytes: a.sizeBytes, sha256: a.sha256, ...(a.target ? { target: a.target } : {}) })),
    artifactPaths: input.returnFiles,
    // 0 lets Bridge v0.6 apply the mode default; v0.5 would treat 0 as the host maximum.
    timeoutMinutes: input.protocolMinor ? 0 : DEFAULT_TIMEOUT_MINUTES,
    ...(input.hostAuto ? { hostAuto: true } : {}), // S8: co_queue_handover may move it once to the other executor
    ...(input.protocolMinor
      ? {
          protocolMinor: input.devices?.length ? 2 : 1,
          ...(input.devices?.length ? { devices: input.devices } : {}),
          ...(input.devices?.length && input.deviceToolsDeny?.length ? { deviceToolsDeny: input.deviceToolsDeny } : {}),
          ...(input.permissions ? { permissions: input.permissions } : {}),
          mode,
          model: input.model || null,
          effort: input.effort || null,
          output: { autoExport: input.autoExport ?? true },
          ...(input.contextFolders?.length
            ? { contextFolders: input.contextFolders.map((f) => ({ rootId: f.rootId, relativePath: f.relativePath, access: f.access, label: f.label })) }
            : {}),
        }
      : {}),
  };
  // 0.19.0: one database call instead of three Drive writes; any failure falls back to the Drive inbox with the same
  // jobId (Bridge runs a jobId once, whichever copy it sees first).
  // 0.45.0: submit once; a server refusal is final (no Drive fallback)
  const submitResult =
    !!input.dbQueue && !pkgId && usesDbQueue(host.hostId) ? await dbJobSubmit(request) : null;
  if (submitResult !== null && typeof submitResult === 'object') throw new Error(submitResult.refused);
  const viaDb = submitResult === 'queued';
  if (viaDb) {
    void publishJobReadyDb(jobId);
  } else {
    if (!pkgId) pkgId = await ensurePath(host.buckets.inbox, [jobId]);
    const reqBytes = new TextEncoder().encode(JSON.stringify(request, null, 2));
    await createFile(pkgId, 'request.json', reqBytes, 'application/json');
    const marker = { schemaVersion: 2, payload: 'request.json', sizeBytes: reqBytes.length, sha256: await sha256Hex(reqBytes) };
    await createFile(pkgId, 'request.ready', JSON.stringify(marker), 'application/json');
    // Fast path hint for Bridge v0.9; the job itself is read from Drive either way.
    if (host.hostId === HOST_ID) void publishJobReady(jobId, pkgId);
  }

  // 4. Canonical event. Written after commit so history never shows a job that was not sent.
  return appendEvent(chat, input.events, {
    type: 'user_message', branchId, jobId, hostId: host.hostId, agent, text, author: input.author, attachments,
    returnFiles: input.returnFiles, conversation: { operation: plan.operation, sessionId: plan.sessionId },
    contextMode: input.live?.contextMode ?? 'full', contextPack: plan.contextPack, ...(plan.reason ? { contextReason: plan.reason } : {}),
    mode, model: input.model || null, effort: input.effort || null, ...(withNote ? { workbenchNote: true } : {}),
    ...(viaDb ? { queue: 'db' as const } : {}),
    ...(input.hintsStored?.length ? { hints: input.hintsStored } : {}),
    ...(input.extra || {}),
  });
}

// 0.89.0: a person's message in a family chat - no job; files are stored once in the chat folder
// (branches/<branch>/attachments/family-<id>/), which every member reads through the shared AI-Workbench folder.
export async function appendFamilyMessage(
  chat: Chat, fresh: () => Promise<ChatEvent[]>,
  input: { text: string; files: File[]; author: string; replyTo?: string; coPending?: boolean; onProgress?: (s: string) => void },
): Promise<ChatEvent> {
  const attachments: AttachmentRef[] = [];
  if (input.files.length) {
    const dir = await ensurePath(chat.folderId, ['branches', DEFAULT_BRANCH, 'attachments', 'family-' + newEventId().slice(-12)]);
    const names = uniqueNames(input.files.map((f) => safeFileName(f.name)));
    for (let i = 0; i < input.files.length; i++) {
      const file = input.files[i];
      input.onProgress?.(`Uploading ${file.name}…`);
      const bytes = await file.arrayBuffer();
      const stored = await createFile(dir, names[i], file, file.type || 'application/octet-stream');
      attachments.push({ displayName: file.name, path: names[i], sizeBytes: file.size, sha256: await sha256Hex(bytes), fileId: stored.id });
    }
  }
  return appendEvent(chat, await fresh(), {
    type: 'user_message', text: input.text, author: input.author, family: true, attachments,
    ...(input.replyTo ? { replyTo: input.replyTo } : {}),
    ...(input.coPending ? { coPending: true } : {}),
  });
}

export async function appendReaction(chat: Chat, events: ChatEvent[], target: string, emoji: string, author: string, remove: boolean): Promise<ChatEvent> {
  return appendEvent(chat, events, { type: 'reaction', target, emoji, author, ...(remove ? { remove: true } : {}) });
}

export async function requestCancel(host: HostDirs, jobId: string): Promise<void> {
  const name = `${jobId}.cancel`;
  if (host.hostId === HOST_ID) void publishJobCancel(jobId);
  // S8: the database for every host - the server may have moved the job (co_queue_handover); job_claim carries it
  void dbJobCancel(jobId);
  if (!host.buckets.cancel) return; // F12: database only
  if (await findChild(host.buckets.cancel, name)) return; // idempotent
  await createFile(host.buckets.cancel, name, '', 'text/plain');
}

// ---------- Job observation ----------

export interface JobSnapshot {
  phase: JobPhase;
  result?: JobResult;
  rejection?: string;
}

const bucketPhase: Record<string, JobPhase> = { inbox: 'queued', claimed: 'running' };

export async function readJob(host: HostDirs, jobId: string, viaDb = false): Promise<JobSnapshot> {
  // 0.19.0: Bridge 1.3.0 copies every result to the database at once; a database job has no Drive inbox copy.
  // S8: for every host - the server may move a database job to the other executor (co_queue_handover).
  {
    const j = await dbJobGet(jobId);
    if (j?.result) {
      const result = j.result as JobResult;
      return { phase: result.status === 'completed' ? 'completed' : result.status === 'cancelled' ? 'cancelled' : 'failed', result };
    }
    if (j?.state === 'rejected') return { phase: 'rejected', rejection: j.rejection || 'Rejected by Bridge.' };
    if (j?.origin === 'db') {
      if (j.state === 'cancelled') return { phase: 'cancelled' };
      return { phase: j.state === 'claimed' ? 'running' : 'queued' };
    }
    if (viaDb && j !== undefined) return { phase: 'queued' }; // row not visible yet
  }
  if (!host.hostDirId) return { phase: 'queued' }; // F12: database only, the row is not visible (yet)
  const found = await list(`name=${q(jobId)} and mimeType=${q(FOLDER_MIME)} and trashed=false`);
  const byId = new Map(Object.entries(host.buckets).map(([k, v]) => [v, k]));
  let bucket: string | undefined;
  let folder: DriveFile | undefined;
  for (const f of found) {
    const b = f.parents?.map((p) => byId.get(p)).find(Boolean);
    if (b && b !== 'events') {
      bucket = b;
      folder = f;
      if (b !== 'inbox') break;
    }
  }
  if (!bucket || !folder) return { phase: 'preparing' };
  if (bucket === 'inbox' || bucket === 'claimed') return { phase: bucketPhase[bucket] };

  const files = await children(folder.id);
  if (bucket === 'rejected') {
    const rj = files.find((f) => f.name === 'rejection.json');
    let reason = 'Rejected by Bridge.';
    if (rj) {
      try {
        const r = await getJson<Record<string, unknown>>(rj.id);
        reason = String(r.reason || r.message || JSON.stringify(r));
      } catch {
        /* keep default */
      }
    }
    return { phase: 'rejected', rejection: reason };
  }
  const resFile = files.find((f) => f.name === 'result.json');
  const readyFile = files.find((f) => f.name === 'result.ready');
  if (!resFile || !readyFile) return { phase: 'running' };
  // Verify the commit marker before trusting the result (TZ section 29).
  const [bytes, marker] = await Promise.all([getBytes(resFile.id), getJson<{ sizeBytes: number; sha256: string }>(readyFile.id)]);
  if (bytes.byteLength !== marker.sizeBytes || (await sha256Hex(bytes)).toLowerCase() !== marker.sha256.toLowerCase()) {
    return { phase: 'running' }; // Drive has not delivered the final bytes yet
  }
  const result = JSON.parse(new TextDecoder().decode(bytes).replace(/^﻿/, '')) as JobResult;
  const phase: JobPhase =
    result.status === 'completed' ? 'completed' : result.status === 'cancelled' ? 'cancelled' : 'failed';
  return { phase, result };
}

export interface EventCursor {
  folderId: string | null;
  seen: Set<string>;
  // 0.19.0: last event sequence read from the database; db = the database has served this job.
  dbSeq: number;
  db: boolean;
}

export function newCursor(): EventCursor {
  return { folderId: null, seen: new Set(), dbSeq: -1, db: false };
}

// Returns only new Bridge event batches since the cursor.
export async function readBridgeEvents(host: HostDirs, jobId: string, cur: EventCursor, viaDb = false): Promise<BridgeEvent[]> {
  if (usesDbQueue(host.hostId)) {
    const batches = await dbJobEvents(jobId, cur.dbSeq);
    if (batches?.length) {
      cur.db = true;
      const out: BridgeEvent[] = [];
      for (const b of batches) {
        out.push(...((b.events || []) as BridgeEvent[]));
        cur.dbSeq = Math.max(cur.dbSeq, Number(b.lastSequence));
      }
      return out.sort((a, b) => a.sequence - b.sequence);
    }
    if (batches && (cur.db || viaDb)) return [];
  }
  if (!host.buckets.events) return []; // F12: database only
  if (!cur.folderId) {
    cur.folderId = await resolveFolder(host.buckets.events, jobId);
    if (!cur.folderId) return [];
  }
  const files = (await children(cur.folderId)).filter((f) => f.name.endsWith('.json') && !cur.seen.has(f.name));
  files.sort((a, b) => a.name.localeCompare(b.name));
  const out: BridgeEvent[] = [];
  for (const f of files) {
    try {
      const batch = await getJson<{ events: BridgeEvent[] }>(f.id);
      out.push(...batch.events);
      cur.seen.add(f.name);
    } catch {
      break; // partially synced batch; retry next poll
    }
  }
  return out.sort((a, b) => a.sequence - b.sequence);
}

export async function finalizeTurn(
  chat: Chat, events: ChatEvent[], userEv: ChatEvent, snap: JobSnapshot, serverFinalizes = false,
): Promise<ChatEvent | null> {
  if (events.some((e) => e.type === 'assistant_message' && e.jobId === userEv.jobId)) return null;
  // 0.20.0: Bridge 1.3.0 reports the result to the database and the server writes the answer into the chat
  // (finalize-lib.php). Wait for it up to ~5 s; write it here only when the server has not.
  if (serverFinalizes && snap.result && userEv.jobId) {
    for (let i = 0; i < 4; i++) {
      const j = await dbJobGet(userEv.jobId);
      if (j?.answered) return null;
      if (j === undefined) break;
      await new Promise((r) => setTimeout(r, 1500));
    }
  }
  const r = snap.result;
  const { text, title } = extractTitle(r?.agentFinalResponse || '');
  return appendEvent(chat, events, {
    type: 'assistant_message',
    branchId: userEv.branchId,
    jobId: userEv.jobId,
    hostId: userEv.hostId,
    agent: userEv.agent,
    status: snap.phase,
    text,
    ...(title ? { autoTitle: title } : {}),
    sessionId: r?.sessionId ?? null,
    artifacts: r?.artifacts || [],
    missingArtifacts: r?.missingArtifacts || [],
    error: snap.rejection || mergeAgentErrors(r?.error, r?.agentErrorMessages),
    completedUtc: r?.completedUtc || nowUtc(),
    mode: r?.mode || userEv.mode || 'cowork',
    model: r?.model ?? userEv.model ?? null,
    effort: r?.effort ?? userEv.effort ?? null,
    modelReported: r?.modelReported ?? null,
    usage: r?.usage ?? null,
    exportSkipped: r?.exportSkipped || [],
    ...gatewayBlockedFields(gatewayBlocked(r)),
    // F10: the answer to a side job of a module carries its key too (CO Web collapses both into one row)
    ...Object.fromEntries(sideJobsOf(moduleList()).filter((j) => (userEv as unknown as Record<string, unknown>)[j.key]).map((j) => [j.key, (userEv as unknown as Record<string, unknown>)[j.key]])),
  });
}

// ---------- Artifacts ----------

export async function locateArtifact(root: Root, driveRelativePath: string): Promise<DriveFile> {
  const parts = driveRelativePath.split('/').filter(Boolean);
  const name = parts.pop()!;
  const f = await resolveFile(root.id, parts, name);
  if (f) return f;
  if (!(await resolvePath(root.id, parts))) throw new Error('Artifact folder not found in Drive (sync may still be running).');
  throw new Error('Artifact file not found in Drive (sync may still be running).');
}

export async function fetchArtifact(root: Root, driveRelativePath: string): Promise<{ bytes: ArrayBuffer; file: DriveFile }> {
  const file = await locateArtifact(root, driveRelativePath);
  return { bytes: await getBytes(file.id), file };
}

// Copies an artifact into a user folder. The canonical copy in AI-Workbench stays untouched.
export async function copyToFolder(fileId: string, folderId: string, name: string): Promise<DriveFile> {
  const taken = new Set((await children(folderId)).map((f) => f.name.toLowerCase()));
  let target = name;
  for (let i = 1; taken.has(target.toLowerCase()); i++) {
    const dot = name.lastIndexOf('.');
    target = dot > 0 ? `${name.slice(0, dot)} (${i})${name.slice(dot)}` : `${name} (${i})`;
  }
  return copyFile(fileId, folderId, target);
}

// ---------- Workspace browsing (Drive-hosted workspaces and chat scratch) ----------

export async function resolveDrivePath(root: Root, relative: string): Promise<string | null> {
  return resolvePath(root.id, relative.split('/').filter(Boolean));
}

export function chatScratchPath(chat: Chat): string {
  return `projects/${chat.projectId}/chats/${chat.chatId}/scratch`;
}

// ---------- Chat management ----------

async function assertIdle(chat: Chat) {
  const events = await loadChatEvents(chat);
  const done = new Set(events.filter((e) => e.type === 'assistant_message').map((e) => e.jobId));
  if (events.some((e) => e.type === 'user_message' && e.jobId && !done.has(e.jobId)))
    throw new Error('The chat has a running job. Wait for it to finish or stop it first.');
}

// Keeps the import index consistent with chat moves and deletions.
async function updateImportIndex(root: Root, chat: Chat, projectId: string | null) {
  if (!chat.importedFrom) return;
  const dir = await ensurePath(root.id, ['imports']);
  const f = await findChild(dir, 'index.json');
  if (!f) return;
  const idx = await getJson<{ schemaVersion?: number; entries: Record<string, { projectId: string; chatId: string; importedUtc: string }> }>(f.id);
  const key = `${chat.importedFrom.source}:${chat.importedFrom.id}`;
  if (!idx.entries?.[key]) return;
  if (projectId) idx.entries[key].projectId = projectId;
  else delete idx.entries[key];
  await updateContent(f.id, JSON.stringify({ schemaVersion: 1, entries: idx.entries }, null, 1), 'application/json');
}

export async function renameChat(chat: Chat, title: string): Promise<Chat> {
  const t = title.trim();
  if (!t) throw new Error('Title cannot be empty.');
  return updateChatMeta(chat, { title: t, titleAuto: false });
}

// First message of a chat whose title was not set by hand: ask the agent to name it.
export function wantsTitle(chat: Chat, events: ChatEvent[]): boolean {
  return chat.titleAuto !== false && !events.some((e) => e.type === 'user_message');
}

// Moves the chat folder with its whole history, files and scratch to another project.
export async function moveChat(root: Root, chat: Chat, target: Project): Promise<Chat> {
  if (target.projectId === chat.projectId) return chat;
  await assertIdle(chat);
  return moveChatFolder(root, chat, target);
}

// 0.58.0: extra = chat.json fields written together with the move (parent of a folded project's chat)
async function moveChatFolder(root: Root, chat: Chat, target: Project, extra: Partial<Pick<Chat, 'parent'>> = {}): Promise<Chat> {
  const projectsId = await ensurePath(root.id, ['projects']);
  const fromChats = await ensurePath(projectsId, [chat.projectId, 'chats']);
  const toChats = await ensurePath(target.folderId, ['chats']);
  await moveFile(chat.folderId, fromChats, toChats);
  forgetFolder(fromChats, chat.chatId);
  const moved = await updateChatMeta({ ...chat }, { ...extra, projectId: target.projectId, movedUtc: nowUtc() });
  await updateImportIndex(root, chat, target.projectId);
  return moved;
}

// ---------- 0.58.0: project menu (merge, fold into a chat with subchats, delete) ----------

export type Progress = (done: number, total: number) => void;

// Every chat folder of the project: the database list (capped at 500 rows) completed from the Drive chats folder.
export async function allProjectChats(p: Project): Promise<Chat[]> {
  const listed = (await listChats(p)).filter((c) => !c.draft);
  const chatsId = await resolveFolder(p.folderId, 'chats');
  if (!chatsId) return listed;
  const have = new Set(listed.map((c) => c.chatId));
  const missing = (await children(chatsId)).filter((f) => f.mimeType === FOLDER_MIME && !have.has(f.name));
  const extra = await mapLimit(missing, 6, async (f): Promise<Chat> => {
    const cj = await findChild(f.id, 'chat.json');
    if (cj) return { ...(await getJson<Omit<Chat, 'folderId'>>(cj.id)), projectId: p.projectId, folderId: f.id };
    return {
      chatId: f.name, projectId: p.projectId, title: f.name, rootBranchId: DEFAULT_BRANCH,
      createdUtc: f.createdTime || '', updatedUtc: f.modifiedTime || '', legacy: true, folderId: f.id,
    };
  });
  return [...listed, ...extra];
}

// All chats of the project, archived ones included; refuses when any of them has a running job.
async function idleProjectChats(p: Project): Promise<Chat[]> {
  const chats = await allProjectChats(p);
  await mapLimit(chats, 4, async (c) => {
    try {
      await assertIdle(c);
    } catch {
      throw new Error(`“${c.title}” has a running job. Wait for it to finish or stop it first.`);
    }
  });
  return chats;
}

// Chats of src move to target with history, files and tree links; src stays as an empty archived project.
export async function mergeProject(root: Root, src: Project, target: Project, progress?: Progress): Promise<number> {
  if (src.projectId === target.projectId) throw new Error('Choose another project.');
  const chats = await idleProjectChats(src);
  for (const [i, c] of chats.entries()) {
    await moveChatFolder(root, c, target);
    progress?.(i + 1, chats.length);
  }
  await saveProjectMeta(src, { archived: true });
  return chats.length;
}

// Copies a Drive folder tree (files only, Google formats too) into dest; files already there by name are kept, so a
// retry finishes an interrupted copy.
async function copyTree(fromId: string, destId: string): Promise<void> {
  const have = new Set((await children(destId)).map((f) => f.name));
  for (const f of await children(fromId)) {
    if (f.mimeType === FOLDER_MIME) await copyTree(f.id, await ensurePath(destId, [f.name]));
    else if (!have.has(f.name)) await copyFile(f.id, destId, f.name);
  }
}

// src becomes one chat "📁 <title>" in target; every chat of src moves there as its subchat (kind subtask, nothing
// inherited, as claude-ai-sync hubs). Instructions, memory and files of src are copied to the hub's scratch/project/;
// src stays archived. Refused when src already has chat trees (one level of nesting only).
export async function foldProjectIntoChat(root: Root, src: Project, target: Project, progress?: Progress): Promise<Chat> {
  if (src.projectId === target.projectId) throw new Error('Choose another project.');
  const chats = await idleProjectChats(src);
  if (chats.some((c) => c.parent)) throw new Error('The project has branched chats (chat trees); use Merge instead.');
  // The hub's first message names the source project; a retry after a failed run finds its hub by it
  const hubTitle = '📁 ' + src.title;
  const marker = `Former project **${src.title}** (\`${src.projectId}\`)`;
  let hub: Chat | undefined;
  for (const c of (await listChats(target)).filter((x) => x.title === hubTitle && !x.parent && !x.draft)) {
    if ((await loadChatEvents(c)).some((e) => e.type === 'assistant_message' && e.text?.startsWith(marker))) {
      hub = c;
      break;
    }
  }
  if (!hub) {
    hub = await createChat(target, hubTitle, chats[0]?.defaultMode || 'cowork');
    const lines = [`${marker}, moved here on ${nowUtc().slice(0, 10)} with ${chats.length} chat(s) as subchats.`];
    if (src.description) lines.push('', src.description);
    lines.push('', 'Its instructions, memory and files (context/, files/) are copied to scratch/project/ of this chat.');
    await appendEvent(hub, [], { type: 'assistant_message', text: lines.join('\n') });
  }
  const dest = await ensurePath(hub.folderId, ['scratch', 'project']);
  for (const part of ['context', 'files']) {
    const from = await resolveFolder(src.folderId, part);
    if (from) await copyTree(from, await ensurePath(dest, [part]));
  }
  const parent = { chatId: hub.chatId, seq: 0, kind: 'subtask' as const, title: hub.title };
  for (const [i, c] of chats.entries()) {
    await moveChatFolder(root, c, target, { parent });
    progress?.(i + 1, chats.length);
  }
  await saveProjectMeta(src, { archived: true });
  return hub;
}

// Moves the project folder (with all chat folders) to the Drive trash, then deletes the chats' messages from the
// database. Refused while a chat has a running job, so nothing is left running without its history. Every step before
// the trash is undoable; a database failure after it leaves orphaned rows, never lost messages of a live project.
export async function deleteProject(root: Root, p: Project, hosts: Record<string, HostDirs>, progress?: Progress): Promise<void> {
  const chats = await idleProjectChats(p);
  void hosts; // nothing runs (checked above): no job to withdraw or cancel
  // Shared copies of the project are listed from the database: archived rows are not offered
  const { folderId, legacy, shareRole, shareVia, sharedChatIds, ...doc } = p;
  void legacy;
  void shareRole;
  void shareVia;
  void sharedChatIds;
  if (!(await dbPut({ projects: [{ ...doc, archived: true, updatedUtc: nowUtc(), folderId }] }))) {
    throw new Error('The database did not answer; nothing was deleted. Try again.');
  }
  await trashFile(folderId);
  const failed: string[] = [];
  for (const [i, c] of chats.entries()) {
    if (!c.legacy && !(await dbDeleteChat(c.chatId))) failed.push(c.title);
    localChats.delete(c.chatId);
    await updateImportIndex(root, c, null).catch(() => undefined);
    progress?.(i + 1, chats.length);
  }
  if (failed.length) throw new Error(`The project is in the Drive trash, but the database kept the messages of ${failed.length} chat(s).`);
}

// Stops every unfinished job of the chat, then moves the chat folder to the Google Drive trash
// (restorable there for 30 days). Jobs still waiting in an inbox are withdrawn; claimed ones get a
// cancel marker and the executor kills the agent process.
export async function deleteChat(root: Root, chat: Chat, hosts: Record<string, HostDirs>, shared = false): Promise<void> {
  // 0.45.0: shared chats are deleted on the server first (owner only); nothing is touched on refusal
  if (shared && !(await dbDeleteChat(chat.chatId))) throw new Error('Only the project owner can delete this chat.');
  const events = await loadChatEvents(chat);
  const done = new Set(events.filter((e) => e.type === 'assistant_message').map((e) => e.jobId));
  const open = events.filter((e) => e.type === 'user_message' && e.jobId && !done.has(e.jobId));
  await Promise.all(
    open.map(async (u) => {
      const host = hosts[eventHost(u)];
      if (!host) return;
      const queued = host.buckets.inbox ? await findChild(host.buckets.inbox, u.jobId!, true) : null;
      if (queued) await trashFile(queued.id);
      else await requestCancel(host, u.jobId!);
    }),
  );
  await trashFile(chat.folderId);
  if (!shared) void dbDeleteChat(chat.chatId);
  localChats.delete(chat.chatId);
  const projectsId = await ensurePath(root.id, ['projects']);
  const chatsId = await resolvePath(projectsId, [chat.projectId, 'chats']);
  if (chatsId) forgetFolder(chatsId, chat.chatId);
  await updateImportIndex(root, chat, null);
}

// Artifact paths recorded before a move point at the old project; map them to the current one.
export function currentArtifactPath(chat: Chat, driveRelativePath: string): string {
  const m = /^projects\/[^/]+\/chats\/([^/]+)\/(.*)$/.exec(driveRelativePath);
  return m && m[1] === chat.chatId ? `projects/${chat.projectId}/chats/${chat.chatId}/${m[2]}` : driveRelativePath;
}

// ---------- Project metadata and context folders ----------

export async function saveProjectMeta(p: Project, patch: Partial<Omit<Project, 'folderId' | 'legacy' | 'projectId'>>): Promise<Project> {
  const next: Project = { ...p, ...patch, updatedUtc: nowUtc() };
  // 0.45.0: share fields come from the server, never written to project.json (kept on the returned project)
  const { folderId, legacy, shareRole, shareVia, sharedChatIds, ...doc } = next;
  void folderId;
  void legacy;
  void shareRole;
  void shareVia;
  void sharedChatIds;
  // 0.45.0: a transfer must be accepted by the database before Drive is touched
  const transfer = patch.environmentId !== undefined && patch.environmentId !== p.environmentId;
  if (transfer && !(await dbPut({ projects: [{ ...doc, folderId: p.folderId }] }))) {
    throw new Error('Transfer failed: the database did not accept it. Nothing was changed.');
  }
  const pj = await findChild(p.folderId, 'project.json');
  if (pj) await updateContent(pj.id, JSON.stringify(doc, null, 2), 'application/json');
  else await createFile(p.folderId, 'project.json', JSON.stringify(doc, null, 2), 'application/json');
  if (!transfer) void dbPut({ projects: [{ ...doc, folderId: p.folderId }] });
  return { ...next, legacy: false };
}

async function foldersDir(host: HostDirs): Promise<string> {
  if (!host.hostDirId) throw new Error(`${host.hostId} publishes no folder index on Drive`);
  return ensurePath(host.hostDirId, ['folders']);
}

// Folder tree published by Bridge (folders only, depth-limited).
export async function readFolderIndex(host: HostDirs, rootId: string): Promise<FolderIndex | null> {
  const dir = await foldersDir(host);
  const f = await findChild(dir, `${rootId}.json`);
  return f ? getJson<FolderIndex>(f.id) : null;
}

export async function requestFolderIndexRefresh(host: HostDirs): Promise<void> {
  const dir = await foldersDir(host);
  if (!(await findChild(dir, 'refresh.request'))) await createFile(dir, 'refresh.request', '', 'text/plain');
}

// Asks Bridge for two more levels below a folder; resolves when the result arrives.
export async function expandFolder(host: HostDirs, rootId: string, relativePath: string, timeoutMs = 90000): Promise<FolderIndex> {
  const dir = await foldersDir(host);
  const reqDir = await ensurePath(dir, ['requests']);
  const resDir = await ensurePath(dir, ['results']);
  const id = `REQ-${Date.now().toString(36).toUpperCase()}${Math.random().toString(36).slice(2, 6).toUpperCase()}`;
  await createFile(reqDir, `${id}.json`, JSON.stringify({ rootId, relativePath }), 'application/json');
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    await new Promise((r) => setTimeout(r, 3000));
    const f = await findChild(resDir, `${id}.json`);
    if (f) {
      const idx = await getJson<FolderIndex>(f.id);
      if (idx.error) throw new Error(idx.error);
      return idx;
    }
  }
  throw new Error('The home agent did not answer in time. Is it online?');
}

// 0.28.0: environment profile for new agent sessions; '' when empty or unreachable.
export async function envProfileBlock(env: string): Promise<string> {
  try {
    const text = (await dbGetProfile(env)).text.trim();
    return text ? `[Environment profile: ${env}]\n${text}\n[End of environment profile]` : '';
  } catch {
    return '';
  }
}

// 0.53.0: the agent's own error messages (e.g. "You've hit your usage limit ... try again at 7:33 PM") follow the
// Bridge summary so CO Web can diagnose the failure (diagnose.ts). Same rule as the server (finalize-lib.php).
function mergeAgentErrors(error: string | null | undefined, msgs: string[] | undefined): string | null {
  if (!error) return msgs?.length ? msgs.join('\n') : null;
  const extra: string[] = [];
  for (const raw of msgs ?? []) {
    if (typeof raw !== 'string') continue;
    const m = raw.trim();
    if (!m || error.includes(m)) continue;
    const cut = Array.from(m).slice(0, 1000).join('');
    if (extra.includes(cut)) continue;
    extra.push(cut);
    if (extra.length >= 5) break;
  }
  return extra.length ? error + '\n' + extra.join('\n') : error;
}
