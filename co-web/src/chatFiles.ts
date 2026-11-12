// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.55.0: chat files as version streams (docs/SPEC-chat-files.md). A stream is one live Drive file of the chat; its
// versions are the Drive revisions of that file, linked to messages by md5. The list comes from the messages (artifacts,
// attachments) with the server registry (chat_files, db.php files_*) on top: kind, merges, branches, annotations.
// Drive work (merge, branch, make main, delete a version) is done here with the user's token, the server records it.

import type { ArtifactRecord, AttachmentRef, ChatEvent } from './types';
import type { FileRow } from './dbapi';
import { dbFilesOp } from './dbapi';
import {
  children, createFile, deleteRevision, ensurePath, fileMeta, getBytes, getRevisionBytes, listRevisions, moveFile, replaceContent,
  resolvePath, resolveFile, trashFile, type DriveFileMeta, type Revision,
} from './drive';
import { md5Hex } from './md5';
import { sha256Hex } from './util';

export interface VersionRef {
  seq: number;
  origin: 'agent' | 'user';
  md5: string | null;
  sha256: string;
  size: number;
  path: string; // relative to AI-Workbench: the live file, or a legacy copy in artifacts/<job>/
  legacy: boolean; // a copy (before Bridge 1.6), opened by its own path
  utc: string;
  fileId?: string; // legacy attachment: its persistent copy
}

export interface ChatStream {
  key: string; // path of the live file relative to AI-Workbench
  name: string;
  versions: VersionRef[]; // from the messages, oldest first
  row: FileRow | null;
  members: { id: number; name: string; path: string; fileId: string | null; utc: string; legacy?: boolean; trashedUtc?: string }[];
}

const TEMP_EXT = new Set(['py', 'ps1', 'psm1', 'sh', 'bash', 'bat', 'cmd', 'js', 'mjs', 'cjs', 'ts', 'tsx', 'jsx', 'cs', 'java', 'go', 'rs',
  'rb', 'php', 'pl', 'lua', 'sql', 'r', 'vbs', 'ipynb', 'log']);
const TEMP_DIRS = new Set(['tmp', 'temp', '_tmp', 'scratch-tmp']);

// Days a service file (kind temp) lives after its last change; CO_FILES_TEMP_DAYS in files-lib.php.
export const TEMP_DAYS = 7;

// Same rule as co_files_kind (files-lib.php): code, logs and tmp/ folders are service files and expire TEMP_DAYS after their last change.
export function defaultKind(name: string): 'doc' | 'temp' {
  const parts = name.toLowerCase().replace(/\\/g, '/').split('/');
  const base = parts.pop() || '';
  if (parts.some((p) => TEMP_DIRS.has(p))) return 'temp';
  const dot = base.lastIndexOf('.');
  return dot >= 0 && TEMP_EXT.has(base.slice(dot + 1)) ? 'temp' : 'doc';
}

// 0.56.0: files folded under a message into one "N working files" line, in every chat. Temporary files (code, logs,
// tmp/), agent data and state (json, txt, tsv, yaml, ...), files without an extension and near-empty files. Display only:
// the kind (and so the 30-day expiry) of data files does not change.
const WORKING_EXT = new Set(['json', 'jsonl', 'ndjson', 'txt', 'tsv', 'yaml', 'yml', 'xml', 'ini', 'toml', 'cfg', 'conf', 'env',
  'dat', 'tmp', 'bak', 'old', 'orig', 'pkl', 'pickle', 'npy', 'npz', 'parquet', 'db', 'sqlite', 'sqlite3', 'lock', 'pid', 'out', 'err',
  'map', 'cache', 'state']);
export function isWorkingFile(name: string, sizeBytes?: number): boolean {
  const n = name.replace(/\\/g, '/');
  if (defaultKind(n) === 'temp') return true;
  const parts = n.toLowerCase().split('/');
  const base = parts.pop() || '';
  if (base.startsWith('.') || parts.some((p) => p.startsWith('.') || p === '__pycache__' || p === 'node_modules')) return true;
  const dot = base.lastIndexOf('.');
  if (dot <= 0) return true;
  if (WORKING_EXT.has(base.slice(dot + 1))) return true;
  return sizeBytes !== undefined && sizeBytes < 16;
}

export function kindOf(s: ChatStream): 'doc' | 'temp' {
  return s.row?.kind ?? defaultKind(s.name);
}

const chatRoot = (projectId: string, chatId: string) => `projects/${projectId}/chats/${chatId}`;
export const scratchPath = (projectId: string, chatId: string, rel: string) => `${chatRoot(projectId, chatId)}/scratch/${rel}`;

// Mirrors co_files_key (files-lib.php).
export function artifactKey(a: ArtifactRecord, projectId: string, chatId: string): { key: string; name: string; legacy: boolean } | null {
  // Codex on Windows may report "dir\file": one form for the key (as in files-lib.php)
  const p = (a.driveRelativePath || '').replace(/\\/g, '/');
  a = { ...a, sourceRelativePath: (a.sourceRelativePath || '').replace(/\\/g, '/') };
  if (!p || p.includes(':') || p.startsWith('/')) return null;
  const m = !a.ref ? /\/artifacts\/[^/]+\/(.+)$/.exec(p) : null;
  if (m) {
    const src = a.sourceRelativePath || m[1];
    return { key: scratchPath(projectId, chatId, src), name: src, legacy: true };
  }
  return { key: p, name: a.sourceRelativePath || p.split('/').pop() || p, legacy: false };
}

export function attachmentKey(a: AttachmentRef, projectId: string, chatId: string, jobId?: string, branchId?: string): { key: string; name: string; legacy: boolean } | null {
  if (a.streamPath) return { key: a.streamPath, name: a.target || a.streamPath.split('/').pop() || a.streamPath, legacy: false };
  if (!a.path || !jobId) return null;
  return { key: `${chatRoot(projectId, chatId)}/branches/${branchId || 'BR-root'}/attachments/${jobId}/${a.path}`, name: a.displayName || a.path, legacy: true };
}

export function buildStreams(events: ChatEvent[], rows: FileRow[] | null, projectId: string, chatId: string): ChatStream[] {
  const byKey = new Map<string, ChatStream>();
  const add = (k: { key: string; name: string; legacy: boolean }, v: VersionRef) => {
    let s = byKey.get(k.key);
    if (!s) byKey.set(k.key, (s = { key: k.key, name: k.name, versions: [], row: null, members: [] }));
    if (!s.versions.some((x) => x.sha256 === v.sha256 && x.path === v.path)) s.versions.push(v);
  };
  for (const e of events) {
    if (e.chatId && e.chatId !== chatId) continue; // inherited messages of a parent chat
    if (e.type === 'assistant_message') {
      for (const a of e.artifacts || []) {
        const k = artifactKey(a, projectId, chatId);
        if (k) add(k, { seq: e.sequence, origin: 'agent', md5: a.md5 || null, sha256: (a.sha256 || '').toLowerCase(), size: a.sizeBytes, path: a.driveRelativePath, legacy: k.legacy, utc: e.completedUtc || e.createdUtc });
      }
    } else if (e.type === 'user_message') {
      for (const a of e.attachments || []) {
        const k = attachmentKey(a, projectId, chatId, e.jobId, e.branchId);
        if (k) add(k, { seq: e.sequence, origin: 'user', md5: a.md5 || null, sha256: (a.sha256 || '').toLowerCase(), size: a.sizeBytes, path: k.key, legacy: k.legacy, utc: e.createdUtc, fileId: a.fileId });
      }
    }
  }
  const byId = new Map<number, ChatStream>();
  for (const r of rows || []) {
    let s = byKey.get(r.path);
    if (!s) byKey.set(r.path, (s = { key: r.path, name: r.name, versions: [], row: null, members: [] }));
    s.row = r;
    s.members = r.extra?.members || [];
    byId.set(r.id, s);
  }
  // Merged streams list their versions under the stream they joined.
  for (const s of [...byKey.values()]) {
    if (s.row?.state !== 'merged' || s.row.mergedInto == null) continue;
    let into = byId.get(s.row.mergedInto);
    for (let hops = 0; into?.row?.state === 'merged' && into.row.mergedInto != null && hops < 10; hops++) into = byId.get(into.row.mergedInto);
    if (into && into !== s) {
      into.versions.push(...s.versions);
      into.versions.sort((a, b) => a.seq - b.seq);
    }
  }
  for (const s of byKey.values()) s.versions.sort((a, b) => a.seq - b.seq);
  return [...byKey.values()].sort((a, b) => headUtc(b).localeCompare(headUtc(a)));
}

export function headUtc(s: ChatStream): string {
  return s.row?.head.utc || s.versions[s.versions.length - 1]?.utc || '';
}

export function visible(s: ChatStream, showHidden = false): boolean {
  const st = s.row?.state || 'live';
  return st === 'live' || (showHidden && (st === 'hidden' || st === 'gone'));
}

// ---------- matching an upload to a stream (ping-pong) ----------

const NOISE = /\b(copy|копия|final|финал|draft|черновик|edited|edit|rev\d*|v\d+|ver\d+|version\d*|comments?|reviewed|upd|updated|new)\b/g;

export function normName(name: string): { stem: string; ext: string } {
  const base = (name.split('/').pop() || name).normalize('NFC').toLowerCase();
  const dot = base.lastIndexOf('.');
  const ext = dot > 0 ? base.slice(dot + 1) : '';
  let stem = dot > 0 ? base.slice(0, dot) : base;
  stem = stem.replace(/\(\d+\)/g, ' ').replace(/[_\-.\s]+/g, ' ');
  stem = stem.replace(/\b(19|20)\d{2}[ ]?\d{2}[ ]?\d{2}\b/g, ' ').replace(NOISE, ' ');
  return { stem: stem.replace(/\s+/g, ' ').trim(), ext };
}

// The stream an uploaded file continues: the same name, or the same name after dropping noise ("(1)", "_v2", "final",
// "copy", dates, a trailing 2-3 letter tag such as initials). Only a unique match counts.
export function matchUpload(fileName: string, streams: ChatStream[]): ChatStream | null {
  const live = streams.filter((s) => visible(s) && !s.key.includes('/.co-attachments/'));
  const exact = live.filter((s) => (s.name.split('/').pop() || '').toLowerCase() === fileName.toLowerCase());
  if (exact.length === 1) return exact[0];
  const u = normName(fileName);
  if (!u.stem) return null;
  const same = (f: (s: string) => string) => live.filter((s) => {
    const n = normName(s.name);
    return n.ext === u.ext && n.stem !== '' && f(n.stem) === f(u.stem);
  });
  let hits = same((x) => x);
  if (hits.length === 1) return hits[0];
  const dropTag = (x: string) => x.replace(/ [a-zа-я]{2,3}$/u, '');
  hits = same(dropTag);
  return hits.length === 1 ? hits[0] : null;
}

// Scratch-relative target of an upload: the matched stream's path, else a name no stream of the chat uses.
export function uploadTarget(fileName: string, match: ChatStream | null, streams: ChatStream[], projectId: string, chatId: string): string {
  const prefix = `${chatRoot(projectId, chatId)}/scratch/`;
  if (match && match.key.startsWith(prefix)) return match.key.slice(prefix.length);
  const taken = new Set(streams.filter((s) => s.key.startsWith(prefix)).map((s) => s.key.slice(prefix.length).toLowerCase()));
  let name = fileName;
  for (let i = 2; taken.has(name.toLowerCase()); i++) {
    const dot = fileName.lastIndexOf('.');
    name = dot > 0 ? `${fileName.slice(0, dot)} (${i})${fileName.slice(dot)}` : `${fileName} (${i})`;
  }
  return name;
}

// ---------- Drive ----------

export async function locate(rootId: string, path: string): Promise<DriveFileMeta | null> {
  const parts = path.split('/').filter(Boolean);
  const name = parts.pop();
  if (!name) return null;
  const f = await resolveFile(rootId, parts, name);
  return f ? fileMeta(f.id) : null;
}

export interface FetchedVersion {
  bytes: ArrayBuffer;
  // 'current': the live file is this version; 'revision': an older Drive revision; 'copy': a legacy copy;
  // 'expired': the version is gone (Drive keeps revisions 30 days), these are the current bytes
  source: 'current' | 'revision' | 'copy' | 'expired';
}

// The bytes of a message's file version.
export async function fetchVersion(rootId: string, rec: { path: string; md5?: string | null; sha256?: string; legacy?: boolean; fileId?: string }): Promise<FetchedVersion> {
  if (rec.legacy) {
    const copy = rec.fileId ? await fileMeta(rec.fileId).catch(() => null) : await locate(rootId, rec.path);
    if (copy) return { bytes: await getBytes(copy.id), source: 'copy' };
    throw new Error('This version expired (old copies are kept 30 days).');
  }
  const live = await locate(rootId, rec.path);
  if (!live) throw new Error('File not found in Drive (it may still be uploading, or it was deleted).');
  const sha = (rec.sha256 || '').toLowerCase();
  if ((rec.md5 && live.md5Checksum === rec.md5) || (!rec.md5 && sha && live.sha256Checksum === sha)) return { bytes: await getBytes(live.id), source: 'current' };
  if (rec.md5) {
    const revs = await listRevisions(live.id);
    const hit = [...revs].reverse().find((r) => r.md5Checksum === rec.md5);
    if (hit) return { bytes: await getRevisionBytes(live.id, hit.id), source: 'revision' };
  }
  return { bytes: await getBytes(live.id), source: 'expired' };
}

export interface VersionItem {
  fileId: string | null; // physical Drive file (live or merged member); null = legacy copy by path
  revisionId: string | null;
  utc: string;
  md5: string | null;
  size: number | null;
  by: 'agent' | 'user' | 'drive';
  seq: number | null; // message
  head: boolean;
  member: boolean; // from a stream merged into this one
  legacyPath?: string;
  legacyFileId?: string;
  restored?: boolean;
}

// All versions of a stream, newest first: Drive revisions of the live file and of merged members, legacy copies.
export async function listVersions(rootId: string, s: ChatStream): Promise<{ items: VersionItem[]; live: DriveFileMeta | null }> {
  const byMd5 = new Map<string, VersionRef>();
  for (const v of s.versions) if (v.md5) byMd5.set(v.md5, v);
  const restored = new Set((s.row?.extra?.restored || []).map((r) => r.md5).filter(Boolean) as string[]);
  const live = await locate(rootId, s.key);
  const items: VersionItem[] = [];
  const fromRevs = (fileId: string, revs: Revision[], member: boolean, isLive: boolean) => {
    revs.forEach((r, i) => {
      const ref = r.md5Checksum ? byMd5.get(r.md5Checksum) : undefined;
      items.push({
        fileId, revisionId: r.id, utc: r.modifiedTime, md5: r.md5Checksum || null, size: r.size ? Number(r.size) : null,
        by: ref ? ref.origin : 'drive', seq: ref ? ref.seq : null, head: isLive && i === revs.length - 1, member,
        restored: !!r.md5Checksum && restored.has(r.md5Checksum) && !ref,
      });
    });
  };
  if (live) fromRevs(live.id, await listRevisions(live.id).catch(() => []), false, true);
  for (const m of s.members) {
    if (!m.fileId || m.trashedUtc) continue;
    const revs = await listRevisions(m.fileId).catch(() => null);
    if (revs) fromRevs(m.fileId, revs, true, false);
  }
  for (const v of s.versions) {
    if (!v.legacy) continue;
    items.push({ fileId: null, revisionId: null, utc: v.utc, md5: v.md5, size: v.size, by: v.origin, seq: v.seq, head: false, member: false, legacyPath: v.path, legacyFileId: v.fileId });
  }
  if (!live && items.length) {
    // The live file is gone (agent deleted it, or a legacy stream): the newest version acts as the head.
    items.sort((a, b) => b.utc.localeCompare(a.utc));
    items[0].head = true;
  }
  items.sort((a, b) => b.utc.localeCompare(a.utc));
  return { items, live };
}

export async function versionBytes(v: VersionItem): Promise<ArrayBuffer> {
  if (v.fileId && v.revisionId) return getRevisionBytes(v.fileId, v.revisionId);
  if (v.legacyFileId) return getBytes(v.legacyFileId);
  throw new Error('No bytes for this version.');
}

export async function legacyBytes(rootId: string, v: VersionItem): Promise<ArrayBuffer> {
  if (v.legacyFileId) return getBytes(v.legacyFileId);
  const f = v.legacyPath ? await locate(rootId, v.legacyPath) : null;
  if (!f) throw new Error('This version expired (old copies are kept 30 days).');
  return getBytes(f.id);
}

export async function bytesOf(rootId: string, v: VersionItem): Promise<ArrayBuffer> {
  return v.fileId ? versionBytes(v) : legacyBytes(rootId, v);
}

async function headOf(bytes: ArrayBuffer) {
  return { md5: md5Hex(bytes), sha256: await sha256Hex(bytes), size: bytes.byteLength };
}

// "Make main": the content of an older version becomes the newest revision of the live file.
export async function makeMain(rootId: string, chatId: string, s: ChatStream, v: VersionItem): Promise<void> {
  if (!s.row) throw new Error('The file is not registered yet; try again in a minute.');
  const live = await locate(rootId, s.key);
  if (!live) throw new Error('The live file is gone; use Branch to restore this version as a file.');
  const bytes = await bytesOf(rootId, v);
  await replaceContent(live.id, bytes, live.mimeType || 'application/octet-stream');
  await dbFilesOp('files_restored', { chatId, id: s.row.id, head: { ...(await headOf(bytes)), revisionId: v.revisionId } });
}

// "Branch": a version becomes a new independent file next to the live one.
export async function branchVersion(rootId: string, chatId: string, chatFolderId: string, s: ChatStream, v: VersionItem): Promise<string> {
  if (!s.row) throw new Error('The file is not registered yet; try again in a minute.');
  const bytes = await bytesOf(rootId, v);
  const live = await locate(rootId, s.key);
  const parts = s.key.split('/');
  const base = parts.pop()!;
  const dirId = live?.parents?.[0] || (await resolvePath(rootId, parts)) || (await ensurePath(chatFolderId, ['scratch']));
  const taken = new Set((await children(dirId)).map((f) => f.name.toLowerCase()));
  const dot = base.lastIndexOf('.');
  const stem = dot > 0 ? base.slice(0, dot) : base;
  const ext = dot > 0 ? base.slice(dot) : '';
  let name = `${stem} (branch)${ext}`;
  for (let i = 2; taken.has(name.toLowerCase()); i++) name = `${stem} (branch ${i})${ext}`;
  await createFile(dirId, name, new Uint8Array(bytes), live?.mimeType || 'application/octet-stream');
  const path = [...parts, name].join('/');
  const rel = s.name.includes('/') ? `${s.name.slice(0, s.name.lastIndexOf('/') + 1)}${name}` : name;
  await dbFilesOp('files_branch', { chatId, from: s.row.id, path, name: rel, head: { ...(await headOf(bytes)), fromMd5: v.md5, revisionId: v.revisionId } });
  return name;
}

export async function deleteVersion(rootId: string, v: VersionItem): Promise<void> {
  if (v.head) throw new Error('The current version cannot be deleted.');
  if (v.fileId && v.revisionId) return deleteRevision(v.fileId, v.revisionId);
  const f = v.legacyFileId ? { id: v.legacyFileId } : v.legacyPath ? await locate(rootId, v.legacyPath) : null;
  if (f) await trashFile(f.id);
}

// Drag "from" onto "into": from's versions are listed under into; a newer head of from becomes into's newest revision;
// from's Drive file moves to scratch/.versions/ (deleted 30 days later by the daily cleanup).
export async function mergeStreams(rootId: string, chatId: string, chatFolderId: string, from: ChatStream, into: ChatStream): Promise<void> {
  if (!from.row || !into.row) throw new Error('The files are not registered yet; try again in a minute.');
  const a = await locate(rootId, from.key);
  const b = await locate(rootId, into.key);
  let head: Record<string, unknown> | undefined;
  if (a && b && headUtc(from) > headUtc(into) && a.md5Checksum !== b.md5Checksum) {
    const bytes = await getBytes(a.id);
    await replaceContent(b.id, bytes, b.mimeType || 'application/octet-stream');
    head = await headOf(bytes);
  }
  let movedFileId: string | null = null;
  let movedPath: string | null = null;
  if (a && a.parents?.[0]) {
    const versionsDir = await ensurePath(chatFolderId, ['scratch', '.versions']);
    await moveFile(a.id, a.parents[0], versionsDir);
    movedFileId = a.id;
    movedPath = `${chatRoot(...splitChat(into.key))}/scratch/.versions/${a.name}`;
  }
  await dbFilesOp('files_merge', { chatId, from: from.row.id, into: into.row.id, movedFileId, movedPath, ...(head ? { head } : {}) });
}

function splitChat(key: string): [string, string] {
  const m = /^projects\/([^/]+)\/chats\/([^/]+)\//.exec(key);
  return m ? [m[1], m[2]] : ['', ''];
}

export async function setKind(chatId: string, s: ChatStream, kind: 'doc' | 'temp'): Promise<void> {
  if (!s.row) throw new Error('The file is not registered yet; try again in a minute.');
  await dbFilesOp('files_set', { chatId, id: s.row.id, kind });
}

export async function setHidden(chatId: string, s: ChatStream, hidden: boolean): Promise<void> {
  if (!s.row) throw new Error('The file is not registered yet; try again in a minute.');
  await dbFilesOp('files_set', { chatId, id: s.row.id, state: hidden ? 'hidden' : 'live' });
}

export function describe(s: ChatStream): string {
  const n = Math.max(s.row?.versions || 0, s.versions.length);
  const who = new Set(s.versions.map((v) => (v.origin === 'user' ? 'you' : 'agent')));
  const kind = kindOf(s) === 'temp' ? ` · service file (deleted ${TEMP_DAYS} days after the last change)` : '';
  const head = n > 1 ? `stream · ${n} versions · ${[...who].join(' & ') || 'agent'}` : `file · ${[...who][0] === 'you' ? 'from you' : 'from the agent'}`;
  return head + kind + (s.row?.annotation ? `\n${s.row.annotation}` : '');
}
