// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.132.0 (plan 10g B, SPEC §3.6a): a file opens with one click inside CO. A module declares the app that opens
// an extension by click (ext/1 fileActions `default`: true = all its exts, or a list of exts); without one, pictures and
// text files open in the core's own viewer (FileViewer.tsx); anything else keeps the old click (download). Download and
// the other ways to open stay in the ⋯ menu. "Ask in chat" about a selection of an open file is one message format for
// every viewer (ext/1 `file.ask`, the core viewer).
import { fileActionsFor, type FileAction, type ModuleInfo } from './extBridge.ts';

const IMAGE = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg', 'ico', 'avif']);
const TEXT = new Set(['txt', 'md', 'markdown', 'csv', 'tsv', 'json', 'jsonl', 'ndjson', 'html', 'htm', 'xml', 'yml', 'yaml', 'log',
  'ini', 'toml', 'cfg', 'conf', 'env', 'tex', 'bib', 'rst', 'srt', 'vtt',
  'py', 'js', 'mjs', 'cjs', 'ts', 'tsx', 'jsx', 'cs', 'java', 'kt', 'go', 'rs', 'rb', 'php', 'pl', 'lua', 'r', 'm', 'swift',
  'c', 'h', 'cpp', 'hpp', 'cc', 'sh', 'bash', 'ps1', 'psm1', 'bat', 'cmd', 'vbs', 'sql', 'css', 'scss', 'less', 'gradle', 'props',
  'csproj', 'sln', 'dockerfile', 'makefile', 'ipynb']);

export const fileExt = (name: string): string => /\.([^./\\]+)$/.exec(name.toLowerCase())?.[1] || '';

/** What the core's own viewer shows of this file: a picture, text, or nothing (the file opens elsewhere). */
export function coreViewKind(name: string): 'image' | 'text' | null {
  const e = fileExt(name);
  if (IMAGE.has(e)) return 'image';
  if (TEXT.has(e)) return 'text';
  return null;
}

/** Does this file action open the extension by click (ext/1 fileActions default: true, or a list of exts)? */
export function isDefaultFor(a: Pick<FileAction, 'default'>, ext: string): boolean {
  const d = a.default;
  return d === true || (Array.isArray(d) && d.includes(ext));
}

/** The file action that opens this file by click: the first enabled module (in the order of the module list) that
 *  declares it the default for the file's extension; null when none does. */
export function defaultFileAction(modules: ModuleInfo[], name: string): { m: ModuleInfo; a: FileAction } | null {
  const e = fileExt(name);
  return fileActionsFor(modules, name).find(({ a }) => isDefaultFor(a, e)) ?? null;
}

export const FILE_QUOTE_MAX = 4000;

/** The text "Ask in chat" puts into the composer of the file's chat: the file and the quoted fragment; the person
 *  writes the question after it and sends it (nothing is sent by itself). */
export function fileAskText(path: string, quote: string): string {
  const q = quote.replace(/\r\n?/g, '\n').trim().slice(0, FILE_QUOTE_MAX);
  return `About the file ${path}:\n«${q}»\n\n`;
}

/**
 * The CO address of the chat a file belongs to (projects/<p>/chats/<c>/… — its own chat; branches and subfolders
 * included), else the chat open in this window (`here`, a path /e/<env>/p/<p>/c/<c>), else null.
 */
export function fileChatPath(env: string, path: string, here: string): string | null {
  const m = /^projects\/([^/]+)\/chats\/(CHAT-\d{14}-[A-Z0-9]{4})(?:\/|$)/.exec(path);
  if (env && m) return `/e/${encodeURIComponent(env)}/p/${encodeURIComponent(m[1])}/c/${m[2]}`;
  const h = /^\/e\/[^/]+\/p\/[^/]+\/c\/CHAT-\d{14}-[A-Z0-9]{4}/.exec(here);
  return h ? h[0] : null;
}
