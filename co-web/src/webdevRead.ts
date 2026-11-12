// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: web device read tools - fs_list, fs_stat, fs_read, fs_search (silent, no confirmation).
import { parsePath, joinPath, globToRegExp } from './webdevCore';
import { type WebFolder, fail, argStr, argBool, argInt, resolveFolder, getEntry, dirEntries, toIso, bytesToBase64, decodeText } from './webdevFsUtil';

type Entry = { path: string; kind: 'file' | 'dir'; size: number; mtimeUtc: string | null };
type Hit = { path: string; line?: number; snippet?: string };
type Target = { folderId: string; parts: string[]; path: string; entry: FileSystemHandle };

const MAX_SEARCH_SIZE = 2 * 1024 * 1024;
const BINARY_SNIFF = 8192;

// Builds a dir entry
const dirInfo = (path: string): Entry => ({ path, kind: 'dir', size: 0, mtimeUtc: null });

// Builds a file entry from its handle
async function fileInfo(h: FileSystemFileHandle, path: string): Promise<Entry> {
  const f = await h.getFile();
  return { path, kind: 'file', size: f.size, mtimeUtc: toIso(f.lastModified) };
}

// Dirs first, then by name
function sortEntries(a: [string, FileSystemHandle], b: [string, FileSystemHandle]): number {
  if (a[1].kind !== b[1].kind) return a[1].kind === 'directory' ? -1 : 1;
  return a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0;
}

// Resolves the path arg; null means virtual root
async function resolveTarget(args: Record<string, unknown>, folders: WebFolder[]): Promise<Target | null> {
  const p = parsePath(argStr(args, 'path'));
  if (!p) return null;
  const folder = await resolveFolder(folders, p.folderId, false);
  const entry = await getEntry(folder.handle, p.parts);
  return { folderId: p.folderId, parts: p.parts, path: joinPath(p.folderId, p.parts), entry };
}

// Collects and sorts the children of a directory
async function sortedChildren(dir: FileSystemDirectoryHandle): Promise<[string, FileSystemHandle][]> {
  const items: [string, FileSystemHandle][] = [];
  for await (const [name, h] of dirEntries(dir)) items.push([name, h]);
  return items.sort(sortEntries);
}

export async function fsList(args: Record<string, unknown>, folders: WebFolder[]): Promise<unknown> {
  const recursive = argBool(args, 'recursive', false);
  const max = argInt(args, 'max', 500, 1, 10000);
  const t = await resolveTarget(args, folders);
  if (!t) {
    return { path: '', entries: folders.map((f) => ({ path: f.id, kind: 'dir', size: 0, mtimeUtc: null, name: f.name, access: f.access })) };
  }
  if (t.entry.kind !== 'directory') fail('invalid_args', 'Not a directory');
  const entries: Entry[] = [];
  let truncated = false;
  const queue: [FileSystemDirectoryHandle, string[]][] = [[t.entry as FileSystemDirectoryHandle, t.parts]];
  outer: while (queue.length) {
    const [dir, parts] = queue.shift()!;
    for (const [name, h] of await sortedChildren(dir)) {
      if (entries.length >= max) { truncated = true; break outer; }
      const cp = [...parts, name];
      const p = joinPath(t.folderId, cp);
      if (h.kind === 'file') entries.push(await fileInfo(h as FileSystemFileHandle, p));
      else {
        entries.push(dirInfo(p));
        if (recursive) queue.push([h as FileSystemDirectoryHandle, cp]);
      }
    }
  }
  return truncated ? { path: t.path, entries, truncated } : { path: t.path, entries };
}

export async function fsStat(args: Record<string, unknown>, folders: WebFolder[]): Promise<unknown> {
  const t = await resolveTarget(args, folders);
  if (!t) return dirInfo('');
  return t.entry.kind === 'file' ? fileInfo(t.entry as FileSystemFileHandle, t.path) : dirInfo(t.path);
}

export async function fsRead(args: Record<string, unknown>, folders: WebFolder[]): Promise<unknown> {
  const offset = argInt(args, 'offset', 0, 0, Number.MAX_SAFE_INTEGER);
  const maxBytes = argInt(args, 'maxBytes', 200000, 1, 1000000);
  const t = await resolveTarget(args, folders);
  if (!t || t.entry.kind !== 'file') fail('invalid_args', 'Is a directory');
  const file = await (t.entry as FileSystemFileHandle).getFile();
  const size = file.size;
  const bytes = new Uint8Array(await file.slice(offset, offset + maxBytes).arrayBuffer());
  const dec = decodeText(bytes);
  if (dec) return { path: t.path, text: dec.text, size, offset, bytesRead: dec.used, eof: offset + dec.used >= size };
  return { path: t.path, data: bytesToBase64(bytes), encoding: 'base64', size, offset, bytesRead: bytes.length, eof: offset + bytes.length >= size };
}

export async function fsSearch(args: Record<string, unknown>, folders: WebFolder[]): Promise<unknown> {
  const query = argStr(args, 'query').trim();
  if (!query) fail('invalid_args', 'query must not be empty');
  const glob = argStr(args, 'glob', false);
  const re = glob ? globToRegExp(glob) : null;
  const max = argInt(args, 'max', 100, 1, 1000);
  const q = query.toLowerCase();
  const hits: Hit[] = [];
  let truncated = false;
  let skipped = 0; // files that could not be read
  // Adds a hit; false when the limit is reached
  const add = (h: Hit): boolean => {
    if (hits.length >= max) { truncated = true; return false; }
    hits.push(h);
    return true;
  };
  // Searches one file; false means stop
  const searchFile = async (h: FileSystemFileHandle, name: string, path: string): Promise<boolean> => {
    if (re && !re.test(name)) return true;
    if (name.toLowerCase().includes(q) && !add({ path })) return false;
    let bytes: Uint8Array;
    try {
      const file = await h.getFile();
      if (file.size > MAX_SEARCH_SIZE) return true;
      bytes = new Uint8Array(await file.arrayBuffer());
    } catch { skipped++; return true; }
    if (bytes.subarray(0, BINARY_SNIFF).includes(0)) return true;
    const lines = new TextDecoder('utf-8', { fatal: false }).decode(bytes).split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      if (lines[i].toLowerCase().includes(q) && !add({ path, line: i + 1, snippet: lines[i].trim().slice(0, 200) })) return false;
    }
    return true;
  };
  const done = () => ({ hits, ...(truncated ? { truncated } : {}), ...(skipped ? { skipped } : {}) });
  const starts: [string, FileSystemDirectoryHandle, string[]][] = [];
  const t = await resolveTarget(args, folders);
  if (!t) {
    for (const f of folders) {
      try { starts.push([f.id, (await resolveFolder(folders, f.id, false)).handle, []]); } catch { skipped++; }
    }
  } else if (t.entry.kind === 'file') {
    await searchFile(t.entry as FileSystemFileHandle, t.entry.name, t.path);
    return done();
  } else starts.push([t.folderId, t.entry as FileSystemDirectoryHandle, t.parts]);
  for (const [folderId, root, rootParts] of starts) {
    const queue: [FileSystemDirectoryHandle, string[]][] = [[root, rootParts]];
    while (queue.length) {
      const [dir, parts] = queue.shift()!;
      for (const [name, h] of await sortedChildren(dir)) {
        const cp = [...parts, name];
        if (h.kind === 'directory') queue.push([h as FileSystemDirectoryHandle, cp]);
        else if (!(await searchFile(h as FileSystemFileHandle, name, joinPath(folderId, cp)))) return done();
      }
    }
  }
  return done();
}
