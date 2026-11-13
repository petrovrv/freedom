// SPDX-License-Identifier: AGPL-3.0-or-later
// Minimal Google Drive REST v3 client over fetch.

import { getToken, markExpired } from './auth';
import { sleep } from './util';

export const FOLDER_MIME = 'application/vnd.google-apps.folder';
const API = 'https://www.googleapis.com/drive/v3';
const UPLOAD = 'https://www.googleapis.com/upload/drive/v3';
const FILE_FIELDS = 'id,name,mimeType,size,createdTime,modifiedTime,parents,webViewLink';

export interface DriveFile {
  id: string;
  name: string;
  mimeType: string;
  size?: string;
  createdTime?: string;
  modifiedTime?: string;
  parents?: string[];
  webViewLink?: string;
}

export class DriveAuthError extends Error {
  constructor() {
    super('Google Drive session expired.');
  }
}

export class DriveError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function request(url: string, init: RequestInit = {}, attempt = 0): Promise<Response> {
  const token = getToken();
  if (!token) throw new DriveAuthError();
  const headers = new Headers(init.headers);
  headers.set('Authorization', `Bearer ${token}`);
  let r: Response;
  try {
    r = await fetch(url, { ...init, headers });
  } catch (e) {
    if (attempt < 3) {
      await sleep(500 * 2 ** attempt);
      return request(url, init, attempt + 1);
    }
    throw new DriveError(0, `Network error: ${(e as Error).message}`);
  }
  if (r.status === 401) {
    markExpired();
    throw new DriveAuthError();
  }
  if ((r.status === 429 || r.status >= 500) && attempt < 4) {
    await sleep(700 * 2 ** attempt + Math.random() * 300);
    return request(url, init, attempt + 1);
  }
  if (!r.ok) {
    let msg = `${r.status}`;
    try {
      const j = await r.json();
      msg = j?.error?.message || msg;
    } catch {
      /* body not JSON */
    }
    throw new DriveError(r.status, `Drive: ${msg}`);
  }
  return r;
}

export function q(s: string): string {
  return `'${s.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
}

// limit: stop paging once this many files are collected (full-text searches can match a lot).
export async function list(query: string, fields = FILE_FIELDS, orderBy?: string, limit = Infinity): Promise<DriveFile[]> {
  const out: DriveFile[] = [];
  let pageToken = '';
  do {
    const u = new URL(`${API}/files`);
    u.searchParams.set('q', query);
    u.searchParams.set('fields', `nextPageToken,files(${fields})`);
    u.searchParams.set('pageSize', '1000');
    u.searchParams.set('supportsAllDrives', 'true');
    u.searchParams.set('includeItemsFromAllDrives', 'true');
    if (orderBy) u.searchParams.set('orderBy', orderBy);
    if (pageToken) u.searchParams.set('pageToken', pageToken);
    const j = await (await request(u.toString())).json();
    out.push(...j.files);
    pageToken = j.nextPageToken || '';
  } while (pageToken && out.length < limit);
  return out;
}

export function children(folderId: string): Promise<DriveFile[]> {
  return list(`${q(folderId)} in parents and trashed=false`);
}

// Folder id cache keyed by parentId/name. Folder ids are stable for our purposes.
const folderCache = new Map<string, string>();
const inflight = new Map<string, Promise<string>>();

export async function findChild(parentId: string, name: string, foldersOnly = false): Promise<DriveFile | null> {
  const mime = foldersOnly ? ` and mimeType=${q(FOLDER_MIME)}` : '';
  const res = await list(`${q(parentId)} in parents and name=${q(name)} and trashed=false${mime}`);
  if (!res.length) return null;
  // Duplicate names can appear through concurrent writers; the oldest wins deterministically.
  res.sort((a, b) => (a.createdTime || '').localeCompare(b.createdTime || ''));
  return res[0];
}

export async function resolveFolder(parentId: string, name: string): Promise<string | null> {
  const key = `${parentId}/${name}`;
  const cached = folderCache.get(key);
  if (cached) return cached;
  const f = await findChild(parentId, name, true);
  if (f) folderCache.set(key, f.id);
  return f ? f.id : null;
}

// 0.59.0: same-named sibling folders (CO Web and Bridge creating one chat folder at once, 29.10) — every one is tried,
// oldest first, and the branch where the rest of the path (and the leaf, for a file) exists wins; the cache keeps it.
async function walk<T>(rootId: string, parts: string[], leaf: (dirId: string) => Promise<T | null>): Promise<T | null> {
  if (!parts.length) return leaf(rootId);
  const [head, ...rest] = parts;
  const key = `${rootId}/${head}`;
  const cached = folderCache.get(key);
  if (cached) {
    const hit = await walk(cached, rest, leaf);
    if (hit) return hit;
  }
  const res = await list(`${q(rootId)} in parents and name=${q(head)} and trashed=false and mimeType=${q(FOLDER_MIME)}`);
  res.sort((a, b) => (a.createdTime || '').localeCompare(b.createdTime || ''));
  for (const f of res) {
    if (f.id === cached) continue;
    const hit = await walk(f.id, rest, leaf);
    if (hit) {
      folderCache.set(key, f.id);
      return hit;
    }
  }
  return null;
}

export function resolvePath(rootId: string, parts: string[]): Promise<string | null> {
  return walk(rootId, parts, async (id) => id);
}

// A file by its folder path and name, across same-named sibling folders.
export function resolveFile(rootId: string, dirParts: string[], name: string): Promise<DriveFile | null> {
  return walk(rootId, dirParts, (id) => findChild(id, name));
}

export async function ensureFolder(parentId: string, name: string): Promise<string> {
  const key = `${parentId}/${name}`;
  const cached = folderCache.get(key);
  if (cached) return cached;
  const running = inflight.get(key);
  if (running) return running;
  const p = (async () => {
    const existing = await findChild(parentId, name, true);
    if (existing) {
      folderCache.set(key, existing.id);
      return existing.id;
    }
    const r = await request(`${API}/files?fields=id&supportsAllDrives=true`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, mimeType: FOLDER_MIME, parents: [parentId] }),
    });
    const j = await r.json();
    folderCache.set(key, j.id);
    return j.id as string;
  })();
  inflight.set(key, p);
  try {
    return await p;
  } finally {
    inflight.delete(key);
  }
}

export async function ensurePath(rootId: string, parts: string[]): Promise<string> {
  let id = rootId;
  for (const p of parts) id = await ensureFolder(id, p);
  return id;
}

export async function getMeta(fileId: string): Promise<DriveFile> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}?fields=${FILE_FIELDS}&supportsAllDrives=true`);
  return r.json();
}

export async function getBytes(fileId: string): Promise<ArrayBuffer> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}?alt=media&supportsAllDrives=true`);
  return r.arrayBuffer();
}

export async function getText(fileId: string): Promise<string> {
  const buf = await getBytes(fileId);
  return new TextDecoder('utf-8').decode(buf);
}

export async function getJson<T>(fileId: string): Promise<T> {
  const t = await getText(fileId);
  return JSON.parse(t.replace(/^﻿/, '')) as T;
}

const SIMPLE_LIMIT = 5 * 1024 * 1024;

// Creates a new file. Bytes are uploaded exactly as given (hash-stable).
export async function createFile(
  parentId: string,
  name: string,
  data: Blob | Uint8Array | string,
  mimeType = 'application/octet-stream',
): Promise<DriveFile> {
  const blob =
    data instanceof Blob ? data : new Blob([typeof data === 'string' ? new TextEncoder().encode(data) : (data as BlobPart)]);
  const meta = { name, parents: [parentId], mimeType };
  if (blob.size <= SIMPLE_LIMIT) {
    const boundary = 'co' + Math.random().toString(36).slice(2);
    const body = new Blob([
      `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${JSON.stringify(meta)}\r\n`,
      `--${boundary}\r\nContent-Type: ${mimeType}\r\n\r\n`,
      blob,
      `\r\n--${boundary}--`,
    ]);
    const r = await request(`${UPLOAD}/files?uploadType=multipart&supportsAllDrives=true&fields=${FILE_FIELDS}`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/related; boundary=${boundary}` },
      body,
    });
    return r.json();
  }
  // Resumable upload in one PUT; the session URL itself carries authorization.
  const init = await request(`${UPLOAD}/files?uploadType=resumable&supportsAllDrives=true&fields=${FILE_FIELDS}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json; charset=UTF-8',
      'X-Upload-Content-Type': mimeType,
      'X-Upload-Content-Length': String(blob.size),
    },
    body: JSON.stringify(meta),
  });
  const session = init.headers.get('Location');
  if (!session) throw new DriveError(0, 'Drive did not return an upload session.');
  const r = await request(session, { method: 'PUT', body: blob });
  return r.json();
}

export async function updateContent(fileId: string, data: string, mimeType = 'text/plain'): Promise<void> {
  await request(`${UPLOAD}/files/${encodeURIComponent(fileId)}?uploadType=media&supportsAllDrives=true`, {
    method: 'PATCH',
    headers: { 'Content-Type': `${mimeType}; charset=UTF-8` },
    body: new TextEncoder().encode(data),
  });
}

export async function copyFile(fileId: string, parentId: string, name: string): Promise<DriveFile> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}/copy?supportsAllDrives=true&fields=${FILE_FIELDS}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, parents: [parentId] }),
  });
  return r.json();
}

// Moves a file or folder; its id and contents stay the same.
export async function moveFile(fileId: string, fromParent: string, toParent: string): Promise<void> {
  const u = `${API}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true&addParents=${encodeURIComponent(toParent)}&removeParents=${encodeURIComponent(fromParent)}&fields=id`;
  await request(u, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: '{}' });
}

export function forgetFolder(parentId: string, name: string) {
  folderCache.delete(`${parentId}/${name}`);
}

export async function trashFile(fileId: string): Promise<void> {
  await request(`${API}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ trashed: true }),
  });
}

// ---------- 0.55.0: revisions (chat files as version streams) ----------

export interface DriveFileMeta extends DriveFile {
  md5Checksum?: string;
  sha256Checksum?: string;
  headRevisionId?: string;
}

export interface Revision {
  id: string;
  modifiedTime: string;
  md5Checksum?: string;
  size?: string;
  keepForever?: boolean;
  originalFilename?: string;
  lastModifyingUser?: { displayName?: string; me?: boolean };
}

const META_FIELDS = 'id,name,mimeType,size,createdTime,modifiedTime,parents,webViewLink,md5Checksum,sha256Checksum,headRevisionId';

export async function fileMeta(fileId: string): Promise<DriveFileMeta> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true&fields=${META_FIELDS}`);
  return r.json();
}

// Oldest first, as Drive lists them.
export async function listRevisions(fileId: string): Promise<Revision[]> {
  const out: Revision[] = [];
  let pageToken = '';
  do {
    const u = `${API}/files/${encodeURIComponent(fileId)}/revisions?pageSize=200&fields=nextPageToken,revisions(id,modifiedTime,md5Checksum,size,keepForever,originalFilename,lastModifyingUser(displayName,me))${pageToken ? `&pageToken=${encodeURIComponent(pageToken)}` : ''}`;
    const j = await (await request(u)).json();
    out.push(...(j.revisions || []));
    pageToken = j.nextPageToken || '';
  } while (pageToken);
  return out;
}

export async function getRevisionBytes(fileId: string, revisionId: string): Promise<ArrayBuffer> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}/revisions/${encodeURIComponent(revisionId)}?alt=media`);
  return r.arrayBuffer();
}

export async function deleteRevision(fileId: string, revisionId: string): Promise<void> {
  await request(`${API}/files/${encodeURIComponent(fileId)}/revisions/${encodeURIComponent(revisionId)}`, { method: 'DELETE' });
}

// New content of an existing file = a new revision. Exact bytes (hash-stable); resumable above 5 MB.
export async function replaceContent(fileId: string, data: Blob | ArrayBuffer, mimeType = 'application/octet-stream'): Promise<DriveFileMeta> {
  const blob = data instanceof Blob ? data : new Blob([data]);
  const base = `${UPLOAD}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true&fields=${META_FIELDS}`;
  if (blob.size <= SIMPLE_LIMIT) {
    const r = await request(`${base}&uploadType=media`, { method: 'PATCH', headers: { 'Content-Type': mimeType }, body: blob });
    return r.json();
  }
  const init = await request(`${base}&uploadType=resumable`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json; charset=UTF-8', 'X-Upload-Content-Type': mimeType, 'X-Upload-Content-Length': String(blob.size) },
    body: '{}',
  });
  const session = init.headers.get('Location');
  if (!session) throw new DriveError(0, 'Drive did not return an upload session.');
  return (await request(session, { method: 'PUT', body: blob })).json();
}

// ---------- files of module pages (moduleServices.ts, ext/1 file.*) ----------

export interface PropsMeta extends DriveFile {
  appProperties?: Record<string, string>;
}

// A file of a folder with its app properties (e.g. a converted copy keeps the md5 of its original there).
export async function findWithProps(parentId: string, name: string): Promise<PropsMeta | null> {
  const r = await request(
    `${API}/files?q=${encodeURIComponent(`${q(parentId)} in parents and name=${q(name)} and trashed=false`)}&fields=files(${FILE_FIELDS},appProperties)&supportsAllDrives=true&includeItemsFromAllDrives=true&pageSize=5`,
  );
  const j = await r.json();
  return j.files?.[0] || null;
}

// Copy converted to a Google editor type (Office -> Docs/Slides/Sheets); the caller deletes it after the export.
export async function copyConverted(fileId: string, parentId: string, name: string, mimeType: string): Promise<DriveFile> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}/copy?supportsAllDrives=true&fields=${FILE_FIELDS}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify({ name, parents: [parentId], mimeType }),
  });
  return r.json();
}

export async function exportPdf(fileId: string): Promise<ArrayBuffer> {
  const r = await request(`${API}/files/${encodeURIComponent(fileId)}/export?mimeType=application%2Fpdf`);
  return r.arrayBuffer();
}

// Permanent delete (the temporary Google copy of a conversion; not for user files).
export async function deleteFile(fileId: string): Promise<void> {
  await request(`${API}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true`, { method: 'DELETE' });
}

export async function setAppProperties(fileId: string, appProperties: Record<string, string>): Promise<void> {
  await request(`${API}/files/${encodeURIComponent(fileId)}?supportsAllDrives=true`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify({ appProperties }),
  });
}
