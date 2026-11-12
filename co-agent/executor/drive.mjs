// SPDX-License-Identifier: AGPL-3.0-or-later
// Minimal Google Drive v3 client for the server agent (no dependencies, Node 24 fetch).
// Credentials: the owner's refresh token handed over by /agent/ping.php and the CO web OAuth client.

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

const API = 'https://www.googleapis.com/drive/v3';
const UPLOAD = 'https://www.googleapis.com/upload/drive/v3';
export const FOLDER_MIME = 'application/vnd.google-apps.folder';
const SESSION_FILE = join(homedir(), 'co-agent', 'secrets', 'google-session.json');

let client = null;
let token = null;
let tokenExp = 0;
let tokenRt = null;

function oauthClient() {
  if (!client) {
    // config.php is PHP; let PHP itself print the two public-to-us fields.
    const out = execFileSync('php', ['-r', `$c = require '${join(homedir(), 'co-auth', 'config.php')}'; echo json_encode(['id' => $c['client_id'], 'secret' => $c['client_secret']]);`]);
    client = JSON.parse(out.toString());
  }
  return client;
}

export function hasSession() {
  try {
    return !!JSON.parse(readFileSync(SESSION_FILE, 'utf8')).rt;
  } catch {
    return false;
  }
}

export class AuthError extends Error {}

async function accessToken() {
  const rt = JSON.parse(readFileSync(SESSION_FILE, 'utf8')).rt;
  if (token && rt === tokenRt && Date.now() < tokenExp - 120000) return token;
  const c = oauthClient();
  const r = await fetch('https://oauth2.googleapis.com/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: c.id, client_secret: c.secret, refresh_token: rt, grant_type: 'refresh_token' }),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok || !j.access_token) throw new AuthError(`Google token refresh failed: ${r.status} ${j.error || ''}`);
  token = j.access_token;
  tokenRt = rt;
  tokenExp = Date.now() + (j.expires_in || 3599) * 1000;
  return token;
}

async function call(method, url, { body, headers = {}, raw = false } = {}) {
  for (let attempt = 0; ; attempt++) {
    const r = await fetch(url, { method, body, headers: { Authorization: `Bearer ${await accessToken()}`, ...headers } });
    if (r.status === 401 && attempt === 0) {
      token = null;
      continue;
    }
    if ((r.status === 429 || r.status >= 500 || (r.status === 403 && attempt < 4)) && attempt < 5) {
      const text = await r.text();
      if (r.status !== 403 || /rate|quota|limit/i.test(text)) {
        await new Promise((res) => setTimeout(res, 500 * 2 ** attempt));
        continue;
      }
      throw new Error(`Drive ${method} ${r.status}: ${text.slice(0, 300)}`);
    }
    if (!r.ok) throw new Error(`Drive ${method} ${r.status}: ${(await r.text()).slice(0, 300)}`);
    if (raw) return Buffer.from(await r.arrayBuffer());
    return r.status === 204 ? null : r.json();
  }
}

export const q = (s) => `'${String(s).replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
const FIELDS = 'id,name,mimeType,parents,size,modifiedTime,createdTime,md5Checksum,appProperties';

export async function list(query, fields = FIELDS) {
  const out = [];
  let pageToken;
  do {
    const u = new URL(`${API}/files`);
    u.searchParams.set('q', query);
    u.searchParams.set('fields', `nextPageToken,files(${fields})`);
    u.searchParams.set('pageSize', '1000');
    u.searchParams.set('supportsAllDrives', 'true');
    u.searchParams.set('includeItemsFromAllDrives', 'true');
    if (pageToken) u.searchParams.set('pageToken', pageToken);
    const j = await call('GET', u);
    out.push(...j.files);
    pageToken = j.nextPageToken;
  } while (pageToken);
  return out;
}

export const children = (folderId) => list(`${q(folderId)} in parents and trashed=false`);

export async function findChild(folderId, name, folder = null) {
  let query = `${q(folderId)} in parents and name=${q(name)} and trashed=false`;
  if (folder === true) query += ` and mimeType=${q(FOLDER_MIME)}`;
  const f = await list(query);
  return f[0] || null;
}

const folderCache = new Map(); // parentId/name -> id

export async function ensureFolder(parentId, name) {
  const key = `${parentId}/${name}`;
  if (folderCache.has(key)) return folderCache.get(key);
  let f = await findChild(parentId, name, true);
  if (!f) f = await call('POST', `${API}/files?supportsAllDrives=true&fields=id`, {
    body: JSON.stringify({ name, mimeType: FOLDER_MIME, parents: [parentId] }),
    headers: { 'Content-Type': 'application/json' },
  });
  folderCache.set(key, f.id);
  return f.id;
}

export async function ensurePath(rootId, parts) {
  let id = rootId;
  for (const p of parts) id = await ensureFolder(id, p);
  return id;
}

export async function resolvePath(rootId, parts) {
  let id = rootId;
  for (const p of parts) {
    const key = `${id}/${p}`;
    if (folderCache.has(key)) {
      id = folderCache.get(key);
      continue;
    }
    const f = await findChild(id, p, true);
    if (!f) return null;
    folderCache.set(key, f.id);
    id = f.id;
  }
  return id;
}

export function forgetFolder(parentId, name) {
  folderCache.delete(`${parentId}/${name}`);
}

function multipart(meta, data, mime) {
  const boundary = `co${Math.random().toString(36).slice(2)}`;
  const head = Buffer.from(`--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${JSON.stringify(meta)}\r\n--${boundary}\r\nContent-Type: ${mime}\r\n\r\n`);
  const tail = Buffer.from(`\r\n--${boundary}--`);
  return { body: Buffer.concat([head, Buffer.isBuffer(data) ? data : Buffer.from(data), tail]), type: `multipart/related; boundary=${boundary}` };
}

export async function createFile(parentId, name, data, mime = 'application/octet-stream', appProperties) {
  const { body, type } = multipart({ name, parents: [parentId], ...(appProperties ? { appProperties } : {}) }, data, mime);
  return call('POST', `${UPLOAD}/files?uploadType=multipart&supportsAllDrives=true&fields=${FIELDS}`, { body, headers: { 'Content-Type': type } });
}

export async function updateContent(fileId, data, mime = 'application/octet-stream') {
  return call('PATCH', `${UPLOAD}/files/${fileId}?uploadType=media&supportsAllDrives=true&fields=${FIELDS}`, {
    body: Buffer.isBuffer(data) ? data : Buffer.from(data),
    headers: { 'Content-Type': mime },
  });
}

export async function upsertFile(parentId, name, data, mime) {
  const f = await findChild(parentId, name);
  return f ? updateContent(f.id, data, mime) : createFile(parentId, name, data, mime);
}

export const getBytes = (fileId) => call('GET', `${API}/files/${fileId}?alt=media&supportsAllDrives=true`, { raw: true });

export async function getJson(fileId) {
  return JSON.parse((await getBytes(fileId)).toString('utf8').replace(/^﻿/, ''));
}

export async function moveFile(fileId, fromId, toId) {
  return call('PATCH', `${API}/files/${fileId}?addParents=${toId}&removeParents=${fromId}&supportsAllDrives=true&fields=id,parents`, {
    body: '{}',
    headers: { 'Content-Type': 'application/json' },
  });
}

export async function trashFile(fileId) {
  return call('PATCH', `${API}/files/${fileId}?supportsAllDrives=true&fields=id`, {
    body: JSON.stringify({ trashed: true }),
    headers: { 'Content-Type': 'application/json' },
  });
}
