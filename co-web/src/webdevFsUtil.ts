// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: web device - File System Access helpers for webdevFs.ts.
import { type FolderAccess, type ToolCall, type ToolResult, WebDevError, errResult } from './webdevCore';

export interface WebFolder { id: string; name: string; access: FolderAccess; handle: FileSystemDirectoryHandle }

export type ConfirmKind = 'write' | 'edit' | 'mkdir' | 'delete' | 'move';

export interface ConfirmRequest {
  call: ToolCall;
  kind: ConfirmKind;
  path: string;
  to?: string;
  size?: number;
  before?: string;
  after?: string;
  items?: number;
  replaces?: boolean; // fs_write / fs_move overwrite an existing file
}

export type Confirm = (r: ConfirmRequest) => Promise<boolean>;

type Untyped = any; // eslint-disable-line @typescript-eslint/no-explicit-any

// Query current permission state; browsers without the API are treated as granted
export async function queryPerm(handle: FileSystemHandle, mode: 'read' | 'readwrite'): Promise<string> {
  const h = handle as Untyped;
  if (typeof h.queryPermission !== 'function') return 'granted';
  return await h.queryPermission({ mode });
}

// requestPermission needs a user click (Reconnect folders)
export async function requestPerm(handle: FileSystemHandle, mode: 'read' | 'readwrite'): Promise<string> {
  const h = handle as Untyped;
  if (typeof h.requestPermission !== 'function') return 'granted';
  return await h.requestPermission({ mode });
}

// Typed wrapper over the async entries() iterator
export async function* dirEntries(dir: FileSystemDirectoryHandle): AsyncGenerator<[string, FileSystemHandle]> {
  for await (const [name, h] of (dir as Untyped).entries() as AsyncIterable<[string, FileSystemHandle]>) yield [name, h];
}

export function hasMove(h: FileSystemHandle): boolean {
  return typeof (h as Untyped).move === 'function';
}

export async function moveHandle(h: FileSystemHandle, dest: FileSystemDirectoryHandle, name: string): Promise<void> {
  await (h as Untyped).move(dest, name);
}

export function fail(code: string, message: string): never {
  throw new WebDevError(code, message);
}

export function argStr(a: Record<string, unknown>, k: string, required = true): string {
  const v = a[k];
  if (v === undefined || v === null) {
    if (required) fail('invalid_args', `Missing required argument: ${k}`);
    return '';
  }
  if (typeof v !== 'string') fail('invalid_args', `Argument ${k} must be a string`);
  return v;
}

export function argBool(a: Record<string, unknown>, k: string, def: boolean): boolean {
  const v = a[k];
  if (v === undefined || v === null) return def;
  if (typeof v !== 'boolean') fail('invalid_args', `Argument ${k} must be a boolean`);
  return v;
}

export function argInt(a: Record<string, unknown>, k: string, def: number, min: number, max: number): number {
  const v = a[k];
  if (v === undefined || v === null) return def;
  if (typeof v !== 'number' || !Number.isInteger(v)) fail('invalid_args', `Argument ${k} must be an integer`);
  if (v < min || v > max) fail('invalid_args', `Argument ${k} must be between ${min} and ${max}`);
  return v;
}

export async function resolveFolder(folders: WebFolder[], folderId: string, needWrite: boolean): Promise<WebFolder> {
  const f = folders.find((x) => x.id === folderId);
  if (!f) fail('not_permitted', 'Unknown folder');
  if (needWrite && f.access === 'read') fail('read_only', 'Folder is read-only');
  const state = await queryPerm(f.handle, f.access === 'write' ? 'readwrite' : 'read');
  if (state !== 'granted') fail('permission_lost', 'Folder permission lost; click Reconnect folders in CO');
  return f;
}

export async function getDir(root: FileSystemDirectoryHandle, parts: string[], create = false): Promise<FileSystemDirectoryHandle> {
  let dir = root;
  for (const p of parts) dir = await dir.getDirectoryHandle(p, { create });
  return dir;
}

// Resolve a file or directory; NotFoundError propagates to caller
export async function getEntry(root: FileSystemDirectoryHandle, parts: string[]): Promise<FileSystemHandle> {
  if (parts.length === 0) return root;
  const parent = await getDir(root, parts.slice(0, -1));
  const name = parts[parts.length - 1];
  try {
    return await parent.getFileHandle(name);
  } catch (e) {
    if (e instanceof DOMException && e.name === 'TypeMismatchError') return await parent.getDirectoryHandle(name);
    throw e;
  }
}

export async function exists(root: FileSystemDirectoryHandle, parts: string[]): Promise<boolean> {
  try {
    await getEntry(root, parts);
    return true;
  } catch (e) {
    if (e instanceof DOMException && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError')) return false;
    throw e;
  }
}

export function mapError(call: ToolCall, e: unknown): ToolResult {
  if (e instanceof WebDevError) return errResult(call, e.code, e.message);
  if (e instanceof DOMException) {
    switch (e.name) {
      case 'NotFoundError':
      case 'TypeMismatchError': return errResult(call, 'not_found', e.message || 'Path not found');
      case 'NotAllowedError':
      case 'SecurityError': return errResult(call, 'permission_lost', 'Folder permission lost; click Reconnect folders in CO');
      case 'QuotaExceededError': return errResult(call, 'too_large', e.message || 'Storage quota exceeded');
    }
  }
  return errResult(call, 'internal', e instanceof Error ? e.message : String(e));
}

export function toIso(ms: number): string | null {
  return Number.isFinite(ms) ? new Date(ms).toISOString() : null;
}

export function bytesToBase64(b: Uint8Array): string {
  const CHUNK = 0x8000;
  let s = '';
  for (let i = 0; i < b.length; i += CHUNK) s += String.fromCharCode(...b.subarray(i, i + CHUNK));
  return btoa(s);
}

export function base64ToBytes(s: string): Uint8Array {
  let bin = '';
  try {
    bin = atob(s.replace(/\s+/g, ''));
  } catch {
    fail('invalid_args', 'Invalid base64 content');
  }
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

// Strict UTF-8 decode; tolerates a truncated multi-byte sequence at the end
export function decodeText(b: Uint8Array): { text: string; used: number } | null {
  if (b.includes(0)) return null;
  const dec = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }); // keep a BOM: fs_edit writes the text back
  for (let trim = 0; trim <= 3 && trim <= b.length; trim++) {
    const used = b.length - trim;
    try {
      return { text: dec.decode(b.subarray(0, used)), used };
    } catch {
      // retry with fewer trailing bytes
    }
  }
  return null;
}

export async function writeFile(root: FileSystemDirectoryHandle, parts: string[], data: Uint8Array | string): Promise<void> {
  if (parts.length === 0) fail('invalid_args', 'Cannot write to folder root');
  const parent = await getDir(root, parts.slice(0, -1), true);
  const fh = await parent.getFileHandle(parts[parts.length - 1], { create: true });
  const w = await fh.createWritable();
  try {
    await w.write(data as FileSystemWriteChunkType);
    await w.close();
  } catch (e) {
    try { await w.abort(); } catch { /* ignore abort failure */ }
    throw e;
  }
}

// Recursive count of files below a directory
export async function countFiles(dir: FileSystemDirectoryHandle): Promise<number> {
  let n = 0;
  for await (const [, h] of dirEntries(dir)) {
    if (h.kind === 'file') n++;
    else n += await countFiles(h as FileSystemDirectoryHandle);
  }
  return n;
}
