// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: self-check of the web device path rules and fs_edit logic (run: npm test, or node src/webdevCore.selftest.ts).
import {
  parsePath,
  slugFolderId,
  newDeviceId,
  deviceShort,
  isDeviceId,
  applyEdit,
  globToRegExp,
  okResult,
  errResult,
  TOOL_DEFS,
  type ToolCall,
} from './webdevCore.ts';

declare const process: { exitCode?: number };

const failures: string[] = [];
let count = 0;

function check(name: string, cond: boolean): void {
  count++;
  if (!cond) failures.push(name);
}

function throwsCode(name: string, fn: () => unknown, code: string): void {
  let actual: unknown = undefined;
  let threw = false;
  try {
    fn();
  } catch (e) {
    threw = true;
    actual = (e as { code?: unknown } | null)?.code;
  }
  check(name + (threw ? ' (got code ' + String(actual) + ')' : ' (did not throw)'), threw && actual === code);
}

const UP = '..';
const DOT = '.';
const BS = String.fromCharCode(92);
const SL = '/';

// parsePath: valid inputs
{
  const r = parsePath('docs' + SL + 'a' + SL + 'b.md');
  check('parsePath nested: folderId', r !== null && r.folderId === 'docs');
  check('parsePath nested: parts', r !== null && r.parts.join('|') === 'a|b.md');
  const root = parsePath('docs');
  check('parsePath folder only', root !== null && root.folderId === 'docs' && root.parts.length === 0);
  const rootSlash = parsePath('docs' + SL);
  check('parsePath trailing slash', rootSlash !== null && rootSlash.folderId === 'docs' && rootSlash.parts.length === 0);
  check('parsePath empty string -> null', parsePath('') === null);
  check('parsePath single slash -> null', parsePath(SL) === null);
}

// parsePath: invalid inputs
{
  const bad: Array<[string, string]> = [
    ['leading parent segment', UP + SL + 'x'],
    ['inner parent segment', 'docs' + SL + UP + SL + 'x'],
    ['leading slash', SL + 'docs'],
    ['drive letter with path', 'C:' + SL + 'x'],
    ['bare drive letter', 'c:'],
    ['backslash', 'docs' + BS + 'a'],
    ['empty segment', 'docs' + SL + SL + 'a'],
    ['dot segment', 'docs' + SL + DOT + SL + 'a'],
  ];
  for (const [label, p] of bad) throwsCode('parsePath invalid_path: ' + label, () => parsePath(p), 'invalid_path');
}

// slugFolderId
check('slugFolderId basic', slugFolderId('My Docs!', []) === 'my-docs');
check('slugFolderId collision -2', slugFolderId('My Docs!', ['my-docs']) === 'my-docs-2');
check('slugFolderId collision -3', slugFolderId('My Docs!', ['my-docs', 'my-docs-2']) === 'my-docs-3');
check('slugFolderId empty -> folder', slugFolderId('', []) === 'folder');

// Device ids
{
  const id = newDeviceId(() => new Uint8Array([0x3f, 0xa9, 0xc1, 0, 1, 2, 3, 0xff]));
  check('newDeviceId format', id === 'w-3fa9c100010203ff');
  check('deviceShort', deviceShort(id) === 'web3fa9c1' && /^[a-z0-9]{1,12}$/.test(deviceShort(id)));
  check('isDeviceId valid', isDeviceId(id) === true);
  check('isDeviceId invalid', isDeviceId('w-XYZ') === false);
}

// applyEdit
{
  const one = applyEdit('a b c', 'b', 'X', false);
  check('applyEdit single', one.text === 'a X c' && one.count === 1);
  const all = applyEdit('x x x', 'x', 'y', true);
  check('applyEdit replaceAll', all.text === 'y y y' && all.count === 3);
  throwsCode('applyEdit not_found', () => applyEdit('a b c', 'q', 'X', false), 'not_found');
  throwsCode('applyEdit ambiguous', () => applyEdit('x x', 'x', 'y', false), 'invalid_args');
  throwsCode('applyEdit empty oldText', () => applyEdit('a b c', '', 'X', false), 'invalid_args');
}

// globToRegExp
{
  const md = globToRegExp('*.md');
  check('glob *.md matches A.MD', md.test('A.MD'));
  check('glob *.md rejects a.md.txt', !md.test('a.md.txt'));
  const q = globToRegExp('a?c.txt');
  check('glob a?c.txt matches abc.txt', q.test('abc.txt'));
  check('glob a?c.txt rejects ac.txt', !q.test('ac.txt'));
}

// okResult / errResult
{
  const call: ToolCall = {
    v: 1, callId: 'c1', runId: 'r1', deviceId: 'w-3fa9c100010203ff', tool: 'fs_read', argsJson: '{}', issuedUtc: '2026-10-19T18:00:00.000Z',
  };
  const ok = okResult(call, { a: 1 });
  let parsedA: unknown = undefined;
  try { parsedA = (JSON.parse(ok.content) as { a?: unknown }).a; } catch { parsedA = undefined; }
  check('okResult', ok.ok === true && parsedA === 1 && ok.callId === 'c1' && ok.error === null && ok.untrusted === false);
  const big = okResult(call, 'x'.repeat(1100000));
  check('okResult too_large', big.ok === false && big.error?.code === 'too_large');
  const err = errResult(call, 'denied', 'no');
  check('errResult', err.ok === false && err.content === '' && err.error?.code === 'denied');
}

// TOOL_DEFS
{
  const expected = 'fs_list,fs_stat,fs_read,fs_search,fs_write,fs_edit,fs_mkdir,fs_delete,fs_move';
  check('TOOL_DEFS names', TOOL_DEFS.map((d) => d.name).join(',') === expected);
}

if (failures.length > 0) {
  for (const f of failures) console.error('FAIL: ' + f);
  console.error('webdevCore: ' + failures.length + ' of ' + count + ' checks failed');
  process.exitCode = 1;
} else {
  console.log('webdevCore: ok ' + count + ' checks');
}
