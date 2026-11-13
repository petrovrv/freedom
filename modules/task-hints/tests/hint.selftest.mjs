// SPDX-License-Identifier: AGPL-3.0-or-later
// node modules/task-hints/tests/hint.selftest.mjs - keyword classifier and hints of ui/hint.js with ui/hint-data.js.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const ui = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'ui');
const g = {};
g.window = g;
vm.createContext(g);
vm.runInContext(readFileSync(resolve(ui, 'hint-data.js'), 'utf8'), g);
vm.runInContext(readFileSync(resolve(ui, 'hint.js'), 'utf8'), g);
const { classifyTask, hintFor } = g.taskHints;

let failed = 0;
const ok = (name, cond) => { if (!cond) failed++; console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`); };
const cases = [
  ['Fix this bug in my python function', 'code'],
  ['Add comments to the attached .docx document', 'docx'],
  ['Build a pivot table in Excel from this csv', 'xlsx'],
  ['Переведи этот текст на английский', 'translate'],
  ['Proofread and polish this paragraph', 'edit'],
  ['Fais une synthèse de ce rapport', 'summary'],
  ['Рассчитай КПД турбины при этих параметрах', 'reasoning'],
  ['Search the web for the latest news on hydrogen', 'web'],
  ['hi', null],
];
for (const [text, want] of cases) ok(`classify "${text}" -> ${want}`, classifyTask(text) === want);
const h = hintFor('Build a pivot table in Excel from this csv');
ok('hint: text, title with the bench date, picks best first', !!h && h.text === 'Recommended for excel:'
  && /model bench of \d{4}-\d{2}-\d{2}/.test(h.title) && h.picks.length >= 1
  && h.picks.every((p) => /^[a-z0-9_-]+$/.test(p.agent) && typeof p.label === 'string'));
ok('no hint for a short text', hintFor('ok') === null);

// what the page sends CO Web over ext/1 when the composer text changes (only on a change)
const sent = [];
const handlers = {};
const p = { coExt: { on: (t, fn) => { handlers[t] = fn; }, hint: (x) => sent.push(x) } };
p.window = p;
vm.createContext(p);
vm.runInContext(readFileSync(resolve(ui, 'hint-data.js'), 'utf8'), p);
vm.runInContext(readFileSync(resolve(ui, 'hint.js'), 'utf8'), p);
handlers.composer('Fix this bug in my python function');
handlers.composer('Fix this bug in my python function please');
handlers.composer('');
ok('ext/1: one hint per change, null when the text goes', sent.length === 2 && sent[0] && sent[0].picks.length >= 1 && sent[1] === null);

if (failed) { console.log(`${failed} failed`); process.exit(1); }
console.log('all passed');
