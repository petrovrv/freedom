// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: web device (lab/web-device/SPEC.md) - ids, path rules, fs_edit logic, result envelopes, tools/list.
// Pure module: runs in node for webdevCore.selftest.ts.
export type FolderAccess = 'read' | 'write';

export interface ToolCall { v: number; callId: string; runId: string; taskId?: string; deviceId: string; tool: string; argsJson: string; issuedUtc: string; tainted?: boolean; timeoutMs?: number }

export interface ToolResult { v: 1; callId: string; runId: string; deviceId: string; tool: string; ok: boolean; error: null | { code: string; message: string }; content: string; untrusted: false; issuedUtc: string }

export interface ToolDef { name: string; description: string; inputSchema: Record<string, unknown> }

export class WebDevError extends Error {
  code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
    this.name = 'WebDevError';
  }
}

// device ids
export function newDeviceId(rand: (n: number) => Uint8Array): string {
  const b = rand(8);
  if (b.length < 8) throw new WebDevError('invalid_args', 'rand returned too few bytes');
  let hex = '';
  for (let i = 0; i < 8; i++) hex += b[i].toString(16).padStart(2, '0');
  return 'w-' + hex;
}

export function isDeviceId(s: string): boolean {
  return typeof s === 'string' && /^w-[0-9a-f]{16}$/.test(s);
}

export function deviceShort(id: string): string {
  return 'web' + id.slice(2, 8);
}

// folder ids
export function slugFolderId(name: string, taken: string[]): string {
  let base = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40).replace(/-+$/, '');
  if (!base) base = 'folder';
  const set = new Set(taken);
  if (!set.has(base)) return base;
  for (let n = 2; ; n++) {
    const id = base + '-' + n;
    if (!set.has(id)) return id;
  }
}

// paths: "<folderId>/<relative path>"; null = the root (connected folders)
export function parsePath(path: string): { folderId: string; parts: string[] } | null {
  if (typeof path !== 'string') throw new WebDevError('invalid_path', 'Path must be a string');
  if (path === '' || path === '/') return null;
  if (path.includes('\\')) throw new WebDevError('invalid_path', 'Path must not contain backslashes');
  if (path.startsWith('/')) throw new WebDevError('invalid_path', 'Path must not start with a slash');
  if (/^[A-Za-z]:/.test(path)) throw new WebDevError('invalid_path', 'Path must not start with a drive letter');
  const p = path.endsWith('/') ? path.slice(0, -1) : path;
  const segs = p.split('/');
  for (const s of segs) {
    if (s === '' || s === '.' || s === '..') throw new WebDevError('invalid_path', 'Invalid path segment: ' + JSON.stringify(s));
  }
  return { folderId: segs[0], parts: segs.slice(1) };
}

export function joinPath(folderId: string, parts: string[]): string {
  return [folderId, ...parts].join('/');
}

// fs_edit
export function applyEdit(text: string, oldText: string, newText: string, replaceAll: boolean): { text: string; count: number } {
  if (!oldText) throw new WebDevError('invalid_args', 'oldText must not be empty');
  const pieces = text.split(oldText);
  const count = pieces.length - 1;
  if (count === 0) throw new WebDevError('not_found', 'oldText not found');
  if (count > 1 && !replaceAll) throw new WebDevError('invalid_args', 'oldText matches ' + count + ' times; pass replaceAll or a longer oldText');
  return { text: pieces.join(newText), count };
}

// fs_search globs: * and ? only
export function globToRegExp(glob: string): RegExp {
  let src = '';
  for (const ch of glob) {
    if (ch === '*') src += '.*';
    else if (ch === '?') src += '.';
    else src += ch.replace(/[.+^${}()|[\]\\/-]/g, '\\$&');
  }
  return new RegExp('^' + src + '$', 'i');
}

// results
export const MAX_RESULT = 1048576;
const enc = new TextEncoder();

export function utf8Bytes(s: string): number {
  return enc.encode(s).length;
}

// untrusted:false: SPEC §5 (user-picked folders, as CO Desktop fs tools inside connected folders)
function baseResult(call: ToolCall): Pick<ToolResult, 'v' | 'callId' | 'runId' | 'deviceId' | 'tool' | 'untrusted' | 'issuedUtc'> {
  return { v: 1, callId: call.callId, runId: call.runId, deviceId: call.deviceId, tool: call.tool, untrusted: false, issuedUtc: call.issuedUtc };
}

export function okResult(call: ToolCall, content: unknown): ToolResult {
  const s = JSON.stringify(content) ?? 'null';
  if (utf8Bytes(s) > MAX_RESULT) return errResult(call, 'too_large', 'Result over 1 MB');
  return { ...baseResult(call), ok: true, error: null, content: s };
}

export function errResult(call: ToolCall, code: string, message: string): ToolResult {
  return { ...baseResult(call), ok: false, error: { code, message }, content: '' };
}

// MCP tools/list
const PATH_DESC = 'Path like folderId/relative/path inside a folder the user connected in a CO browser tab';
const str = (description: string) => ({ type: 'string', description });
const bool = (description: string, def: boolean) => ({ type: 'boolean', description, default: def });
const int = (description: string, def: number) => ({ type: 'integer', description, default: def, minimum: 0 });
const obj = (properties: Record<string, unknown>, required: string[]) => ({ type: 'object', properties, required, additionalProperties: false });

export const TOOL_DEFS: ToolDef[] = [
  { name: 'fs_list', description: 'List entries of a directory in a connected folder. Path \'\' lists the connected folders.', inputSchema: obj({ path: str(PATH_DESC + '; \'\' lists connected folders'), recursive: bool('List subdirectories recursively', false), max: int('Maximum entries to return', 500) }, ['path']) },
  { name: 'fs_stat', description: 'Get metadata (kind, size, modified time) for a file or directory in a connected folder.', inputSchema: obj({ path: str(PATH_DESC) }, ['path']) },
  { name: 'fs_read', description: 'Read a file from a connected folder, optionally from a byte offset. Text comes as text, binary as base64 data.', inputSchema: obj({ path: str(PATH_DESC), offset: int('Byte offset to start reading', 0), maxBytes: int('Maximum bytes to read', 200000) }, ['path']) },
  { name: 'fs_search', description: 'Case-insensitive search in file names and text contents under a path in connected folders (\'\' = all), optionally filtered by file name glob (* and ?). Files over 2 MB are matched by name only.', inputSchema: obj({ path: str(PATH_DESC), query: str('Text to search for'), glob: str('Optional file name glob, * and ? only'), max: int('Maximum matches to return', 100) }, ['path', 'query']) },
  { name: 'fs_write', description: 'Write a file in a connected folder, creating parent directories. Confirmed by the user in the browser.', inputSchema: obj({ path: str(PATH_DESC), content: str('File content'), encoding: { type: 'string', enum: ['utf8', 'base64'], default: 'utf8', description: 'Content encoding' }, overwrite: bool('Overwrite an existing file', false) }, ['path', 'content']) },
  { name: 'fs_edit', description: 'Replace exact text in a file in a connected folder; oldText must match once unless replaceAll. Confirmed by the user in the browser.', inputSchema: obj({ path: str(PATH_DESC), oldText: str('Exact text to replace'), newText: str('Replacement text'), replaceAll: bool('Replace every occurrence', false) }, ['path', 'oldText', 'newText']) },
  { name: 'fs_mkdir', description: 'Create a directory (and parents) in a connected folder. Confirmed by the user in the browser.', inputSchema: obj({ path: str(PATH_DESC) }, ['path']) },
  { name: 'fs_delete', description: 'Delete a file or directory in a connected folder. Always confirmed by the user.', inputSchema: obj({ path: str(PATH_DESC), recursive: bool('Delete non-empty directories recursively', false) }, ['path']) },
  { name: 'fs_move', description: 'Move or rename a file or directory within connected folders. Always confirmed by the user.', inputSchema: obj({ from: str('Source. ' + PATH_DESC), to: str('Destination. ' + PATH_DESC), overwrite: bool('Overwrite an existing destination', false) }, ['from', 'to']) },
];
