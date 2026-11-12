// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: web device write tools - fs_write, fs_edit, fs_mkdir (confirmed), fs_delete, fs_move (always confirmed).
import { type ToolCall, parsePath, applyEdit, utf8Bytes, MAX_RESULT } from './webdevCore';
import {
  type WebFolder, type Confirm, fail, argStr, argBool, resolveFolder, getDir, getEntry, exists,
  dirEntries, base64ToBytes, decodeText, writeFile, countFiles, hasMove, moveHandle,
} from './webdevFsUtil';

// Parse a path that must point at an item inside a connected folder
function target(path: string): { folderId: string; parts: string[] } {
  const t = parsePath(path);
  if (!t || t.parts.length === 0) fail('invalid_path', 'Path must name an item inside a connected folder');
  return t;
}

function denied(): never {
  return fail('denied', 'The user denied the request.');
}

export async function fsWrite(call: ToolCall, args: Record<string, unknown>, folders: WebFolder[], confirm: Confirm): Promise<unknown> {
  const path = argStr(args, 'path');
  const content = argStr(args, 'content');
  const encoding = argStr(args, 'encoding', false) || 'utf8';
  const overwrite = argBool(args, 'overwrite', false);
  if (encoding !== 'utf8' && encoding !== 'base64') fail('invalid_args', 'encoding must be utf8 or base64');
  if (utf8Bytes(content) > MAX_RESULT) fail('too_large', 'Content over 1 MB');
  const t = target(path);
  const data: Uint8Array | string = encoding === 'base64' ? base64ToBytes(content) : content;
  const size = typeof data === 'string' ? utf8Bytes(data) : data.length;
  const root = (await resolveFolder(folders, t.folderId, true)).handle;
  let old: string | null = null; // signature of the file this write replaces
  if (await exists(root, t.parts)) {
    if (!overwrite) fail('exists', 'File exists; pass overwrite:true');
    const entry = await getEntry(root, t.parts);
    if (entry.kind === 'directory') fail('invalid_args', 'Is a directory');
    old = await treeSignature(entry);
  }
  if (!(await confirm({ call, kind: 'write', path, size, replaces: old !== null }))) denied();
  // the target must be as the user saw it: not created, changed or removed while the prompt was open
  const now = (await exists(root, t.parts)) ? await treeSignature(await getEntry(root, t.parts)) : null;
  if (now !== old) fail(old === null ? 'exists' : 'conflict', 'The file changed while waiting for confirmation; nothing was written');
  await writeFile(root, t.parts, data);
  return { path, size };
}

type Snapshot = { text: string; size: number; lastModified: number };

// Reads a whole UTF-8 text file
async function snapshot(root: FileSystemDirectoryHandle, parts: string[]): Promise<Snapshot> {
  const entry = await getEntry(root, parts);
  if (entry.kind !== 'file') fail('invalid_args', 'Is a directory');
  const file = await (entry as FileSystemFileHandle).getFile();
  const bytes = new Uint8Array(await file.arrayBuffer());
  const decoded = decodeText(bytes);
  if (!decoded || decoded.used < bytes.length) fail('invalid_args', 'Not a UTF-8 text file');
  return { text: decoded.text, size: file.size, lastModified: file.lastModified };
}

export async function fsEdit(call: ToolCall, args: Record<string, unknown>, folders: WebFolder[], confirm: Confirm): Promise<unknown> {
  const path = argStr(args, 'path');
  const oldText = argStr(args, 'oldText');
  const newText = argStr(args, 'newText');
  const replaceAll = argBool(args, 'replaceAll', false);
  const t = target(path);
  const root = (await resolveFolder(folders, t.folderId, true)).handle;
  const first = await snapshot(root, t.parts);
  const result = applyEdit(first.text, oldText, newText, replaceAll);
  const ok = await confirm({ call, kind: 'edit', path, before: oldText, after: newText } /* full text: the user approves exactly this */);
  if (!ok) denied();
  // the user may have edited the file while the prompt was open
  const now = await snapshot(root, t.parts);
  if (now.lastModified !== first.lastModified || now.size !== first.size || now.text !== first.text) {
    fail('conflict', 'The file changed while waiting for confirmation; read it again');
  }
  await writeFile(root, t.parts, result.text);
  return { path, replacements: result.count };
}

export async function fsMkdir(call: ToolCall, args: Record<string, unknown>, folders: WebFolder[], confirm: Confirm): Promise<unknown> {
  const path = argStr(args, 'path');
  const t = target(path);
  const root = (await resolveFolder(folders, t.folderId, true)).handle;
  if (await exists(root, t.parts)) {
    const entry = await getEntry(root, t.parts);
    if (entry.kind === 'directory') return { path, created: false };
    fail('exists', 'A file with this name exists');
  }
  if (!(await confirm({ call, kind: 'mkdir', path }))) denied();
  await getDir(root, t.parts, true);
  return { path, created: true };
}

export async function fsDelete(call: ToolCall, args: Record<string, unknown>, folders: WebFolder[], confirm: Confirm): Promise<unknown> {
  const path = argStr(args, 'path');
  const recursive = argBool(args, 'recursive', false);
  const t = target(path);
  const root = (await resolveFolder(folders, t.folderId, true)).handle;
  const entry = await getEntry(root, t.parts);
  let items = 1;
  if (entry.kind === 'directory') {
    const dir = entry as FileSystemDirectoryHandle;
    let hasChild = false;
    for await (const child of dirEntries(dir)) { hasChild = !!child; break; }
    if (hasChild && !recursive) fail('invalid_args', 'Directory not empty; pass recursive:true');
    items = await countFiles(dir);
  }
  const before = await treeSignature(entry);
  if (!(await confirm({ call, kind: 'delete', path, items }))) denied();
  // the user saw this item count; files added or changed meanwhile are not deleted unseen
  if ((await treeSignature(await getEntry(root, t.parts))) !== before) fail('conflict', 'The item changed while waiting for confirmation; nothing was deleted');
  const parent = await getDir(root, t.parts.slice(0, -1));
  await parent.removeEntry(t.parts[t.parts.length - 1], { recursive });
  return { path, deleted: items };
}

// Recursively copy a file or directory handle into destDir under name
async function copy(handle: FileSystemHandle, destDir: FileSystemDirectoryHandle, name: string): Promise<void> {
  if (handle.kind === 'file') {
    const file = await (handle as FileSystemFileHandle).getFile();
    const fh = await destDir.getFileHandle(name, { create: true });
    const w = await fh.createWritable();
    try { await w.write(file); } finally { await w.close(); }
    return;
  }
  const dir = await destDir.getDirectoryHandle(name, { create: true });
  for await (const [n, child] of dirEntries(handle as FileSystemDirectoryHandle)) await copy(child, dir, n);
}

// Change detector for copy + delete moves: every file with size and mtime, every directory
async function treeSignature(h: FileSystemHandle): Promise<string> {
  if (h.kind === 'file') {
    const f = await (h as FileSystemFileHandle).getFile();
    return `${h.name}|${f.size}|${f.lastModified}`;
  }
  const lines: string[] = [];
  const walk = async (dir: FileSystemDirectoryHandle, prefix: string): Promise<void> => {
    for await (const [name, child] of dirEntries(dir)) {
      const path = prefix + name;
      if (child.kind === 'file') {
        const f = await (child as FileSystemFileHandle).getFile();
        lines.push(`${path}|${f.size}|${f.lastModified}`);
      } else {
        lines.push(`${path}/`);
        await walk(child as FileSystemDirectoryHandle, `${path}/`);
      }
    }
  };
  await walk(h as FileSystemDirectoryHandle, '');
  return lines.sort().join('\n');
}

// True when the destination parent (destRoot + parts) is src or lies inside it; checks the deepest existing directory
async function landsInside(src: FileSystemDirectoryHandle, destRoot: FileSystemDirectoryHandle, destParentParts: string[]): Promise<boolean> {
  const inside = async (d: FileSystemDirectoryHandle): Promise<boolean> =>
    (await src.isSameEntry(d)) || (await src.resolve(d)) !== null;
  if (await inside(destRoot)) return true;
  let d = destRoot;
  for (const part of destParentParts) {
    try {
      d = await getDir(d, [part]);
    } catch (e) {
      if (e instanceof DOMException && (e.name === 'NotFoundError' || e.name === 'TypeMismatchError')) break;
      throw e;
    }
  }
  return d !== destRoot && (await inside(d));
}

// Renames a handle inside dir; copy + remove when move() is unavailable
async function relocate(handle: FileSystemHandle, dir: FileSystemDirectoryHandle, name: string): Promise<void> {
  if (hasMove(handle)) { await moveHandle(handle, dir, name); return; }
  const oldName = handle.name;
  await copy(handle, dir, name);
  await dir.removeEntry(oldName, { recursive: true });
}

export async function fsMove(call: ToolCall, args: Record<string, unknown>, folders: WebFolder[], confirm: Confirm): Promise<unknown> {
  const from = argStr(args, 'from');
  const to = argStr(args, 'to');
  const overwrite = argBool(args, 'overwrite', false);
  const src = target(from);
  const dst = target(to);
  const sf = await resolveFolder(folders, src.folderId, true);
  const df = await resolveFolder(folders, dst.folderId, true);
  const same = sf.id === df.id;
  const inside = dst.parts.length >= src.parts.length && src.parts.every((p, i) => dst.parts[i] === p);
  if (same && inside) fail('invalid_args', 'Cannot move a folder into itself');
  const srcEntry = await getEntry(sf.handle, src.parts);
  // connected folders may overlap (a folder and its subfolder added separately): no move of a directory into itself
  if (srcEntry.kind === 'directory' && (await landsInside(srcEntry as FileSystemDirectoryHandle, df.handle, dst.parts.slice(0, -1)))) {
    fail('invalid_args', 'Cannot move a folder into itself');
  }
  const replaces = await exists(df.handle, dst.parts);
  const oldTarget = replaces ? await treeSignature(await getEntry(df.handle, dst.parts)) : null;
  if (replaces) {
    if (!overwrite) fail('exists', 'Target exists; pass overwrite:true');
    const existing = await getEntry(df.handle, dst.parts);
    // overlapping connected folders can name one file twice
    if (await srcEntry.isSameEntry(existing)) fail('invalid_args', 'Source and target are the same item');
    if (existing.kind === 'directory') fail('exists', 'Target directory exists');
  }
  if (!(await confirm({ call, kind: 'move', path: from, to, replaces }))) denied();
  const destParent = await getDir(df.handle, dst.parts.slice(0, -1), true);
  const newName = dst.parts[dst.parts.length - 1];
  // re-evaluate the target after the prompt: replacing a file the user was not told about needs a new request
  const targetNow = await exists(df.handle, dst.parts);
  if (targetNow && !replaces) fail('exists', 'Target was created meanwhile; ask again with overwrite:true');
  if (replaces && (!targetNow || (await treeSignature(await getEntry(df.handle, dst.parts))) !== oldTarget)) {
    fail('conflict', 'The target changed while waiting for confirmation; nothing was moved');
  }
  let bakName: string | null = null;
  if (targetNow) {
    const old = await destParent.getFileHandle(newName).catch(() => fail('exists', 'Target directory exists'));
    bakName = '.' + newName + '.co-bak-' + Date.now();
    await relocate(old, destParent, bakName); // keep the old target until the move succeeds
  }
  const native = same && hasMove(srcEntry);
  try {
    if (native) await moveHandle(srcEntry, destParent, newName);
    else {
      const before = await treeSignature(srcEntry);
      await copy(srcEntry, destParent, newName);
      // a file added or edited during the copy would be lost with the source
      if ((await treeSignature(srcEntry)) !== before) fail('conflict', 'The source changed while it was copied; nothing was moved');
    }
  } catch (e) {
    await destParent.removeEntry(newName, { recursive: true }).catch(() => {}); // drop a partial target
    if (bakName) {
      try {
        await relocate(await destParent.getFileHandle(bakName), destParent, newName); // restore the previous target
      } catch {
        fail('internal', `Move failed and the previous target could not be restored; it is kept as ${bakName}`);
      }
    }
    throw e;
  }
  if (bakName) await destParent.removeEntry(bakName).catch(() => {});
  // copy path: remove the source only once the target is complete
  if (!native) {
    const srcParent = await getDir(sf.handle, src.parts.slice(0, -1));
    await srcParent.removeEntry(src.parts[src.parts.length - 1], { recursive: true });
  }
  return { from, to };
}
