// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.132.0: self-check of one-click file opening (run: npm test, or node src/fileOpen.selftest.ts).
import { coreViewKind, defaultFileAction, fileAskText, fileChatPath, isDefaultFor } from './fileOpen.ts';
import type { ModuleInfo } from './extBridge.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

const page = (target: 'self' | 'blank') => ({ page: 'editor', param: 'path', target });
const mod = (id: string, enabled: boolean, fileActions: unknown[]) => ({ id, enabled, ui: [], fileActions } as unknown as ModuleInfo);
const canvas = mod('canvas', true, [
  { id: 'office', label: 'Open in canvas', title: '', exts: ['docx', 'xlsx', 'pptx'], exclude: '', default: true, open: page('blank') },
  { id: 'notebook', label: 'Open in canvas', title: '', exts: ['md', 'txt'], exclude: '', default: true, open: page('blank') },
]);
const study = mod('study', true, [
  { id: 'viewer', label: 'Open in study viewer', title: '', exts: ['pdf', 'docx', 'md', 'png'], exclude: '', default: ['pdf'], open: { mode: 'study' } },
]);
const off = mod('other', false, [{ id: 'x', label: 'X', title: '', exts: ['pdf'], exclude: '', default: true, open: page('self') }]);
const plain = mod('plain', true, [{ id: 'y', label: 'Y', title: '', exts: ['csv'], exclude: '', open: page('self') }]);

assert.equal(defaultFileAction([off, canvas, study], 'Report.DOCX')?.m.id, 'canvas', 'docx -> canvas (a switched-off module is skipped)');
assert.equal(defaultFileAction([canvas, study], 'notes.md')?.a.id, 'notebook', 'md -> canvas notebook');
assert.equal(defaultFileAction([canvas, study], 'paper.pdf')?.m.id, 'study', 'pdf -> study (default list)');
assert.equal(defaultFileAction([study], 'a.docx'), null, 'study is not the default for docx');
assert.equal(defaultFileAction([study], 'pic.png'), null, 'study is not the default for png');
assert.equal(defaultFileAction([plain], 'data.csv'), null, 'an action without default stays in the menu only');
assert.equal(defaultFileAction([off], 'paper.pdf'), null, 'disabled module');
assert.ok(isDefaultFor({ default: true }, 'x') && !isDefaultFor({}, 'x') && !isDefaultFor({ default: ['a'] }, 'b'), 'isDefaultFor');

assert.equal(coreViewKind('photo.JPG'), 'image', 'image');
assert.equal(coreViewKind('icon.svg'), 'image', 'svg as a picture (img runs no script)');
assert.equal(coreViewKind('page.html'), 'text', 'html as text');
assert.equal(coreViewKind('data.csv'), 'text', 'csv');
assert.equal(coreViewKind('run.py'), 'text', 'code');
assert.equal(coreViewKind('archive.zip'), null, 'zip: no core view');
assert.equal(coreViewKind('noext'), null, 'no extension');

const t = fileAskText('projects/P/chats/CHAT-20260102120000-WXYZ/scratch/a.md', '  line one\r\nline two  ');
assert.equal(t, 'About the file projects/P/chats/CHAT-20260102120000-WXYZ/scratch/a.md:\n«line one\nline two»\n\n', 'ask text');
assert.ok(fileAskText('p', 'x'.repeat(10000)).length < 4100, 'quote capped');

assert.equal(fileChatPath('demo', 'projects/Project One/chats/CHAT-20260101120000-ABCD/scratch/x.docx', '/e/demo'),
  '/e/demo/p/Project%20One/c/CHAT-20260101120000-ABCD', 'chat of the file');
assert.equal(fileChatPath('demo', 'projects/P/chats/CHAT-20260101120000-ABCD/branches/BR-1/x.md', '/e/demo'),
  '/e/demo/p/P/c/CHAT-20260101120000-ABCD', 'branch file -> its chat');
assert.equal(fileChatPath('demo', 'projects/P/files/x.md', '/e/demo/p/P/c/CHAT-20260102120000-WXYZ'),
  '/e/demo/p/P/c/CHAT-20260102120000-WXYZ', 'project file -> the open chat');
assert.equal(fileChatPath('demo', 'projects/P/files/x.md', '/e/demo/p/P'), null, 'no chat');

if (failures.length) {
  console.error('fileOpen selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('fileOpen selftest OK');
}
