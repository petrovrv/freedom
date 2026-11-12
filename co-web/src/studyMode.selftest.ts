// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.78.0: self-check of study mode and formulas (run: npm test, or node src/studyMode.selftest.ts).
import { STUDY_OFF_NOTE, STUDY_RULES, studyHeuristic, studyLinesBetween, studyOn, studyPatch, studyWho } from './studyMode.ts';
import { cutMath, pasteMath } from './mathText.ts';
import { marked } from 'marked';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

// chat switch wins over the project default
assert.equal(studyOn({}, {}), false, 'off by default');
assert.equal(studyOn({ draft: true }, { studyDefault: true }), true, 'project default for a new chat');
assert.equal(studyOn({}, { studyDefault: true }), false, 'existing chat ignores the project default');
assert.equal(studyOn({ study: false }, { studyDefault: true }), false, 'chat off wins');
assert.equal(studyOn({ study: true }, {}), true, 'chat on');

// heuristic: rules while on, a note only after it was switched off
assert.equal(studyHeuristic({}, {}), '', 'never on: nothing');
assert.equal(studyHeuristic({ draft: true }, { studyDefault: true }), STUDY_RULES, 'default on: rules');
const p1 = studyPatch({ draft: true }, { studyDefault: true }, 'alex@example.org');
assert.equal(p1.study, false, 'patch flips the effective state');
assert.equal(p1.studyLog?.length, 1, 'patch logs');
assert.equal(studyHeuristic(p1, { studyDefault: true }), STUDY_OFF_NOTE, 'switched off: note');
const p2 = studyPatch(p1, { studyDefault: true }, 'alex@example.org');
assert.ok(p2.study === true && p2.studyLog?.length === 2 && p2.studyLog[1].on, 'second switch');
let log = p2.studyLog!;
for (let i = 0; i < 60; i++) log = studyPatch({ study: i % 2 === 0, studyLog: log }, {}, 'x').studyLog!;
assert.equal(log.length, 50, 'log capped');
assert.equal(studyWho('alex@example.org', { 'alex@example.org': 'Alex' }), 'Alex', 'who: known e-mail');
assert.equal(studyWho('alex.smith@x.be'), 'Alex', 'who: first name');

const L = [{ on: true, by: 'a', utc: '2026-11-03T10:00:00Z' }, { on: false, by: 'a', utc: '2026-11-03T12:00:00Z' }];
assert.equal(studyLinesBetween(L, null, '2026-11-03T11:00:00Z').length, 1, 'lines before first message');
assert.equal(studyLinesBetween(L, '2026-11-03T11:00:00Z', null).length, 1, 'tail lines');

// formulas
const a = cutMath('Energy $E = mc^2$ and $$\\int_0^1 x\\,dx$$ here.');
assert.equal(a.html.length, 2, 'two formulas');
assert.ok(!a.text.includes('mc^2') && a.text.includes(`COMATH${a.tag}P0X`), 'placeholders');
assert.ok(pasteMath(a.text, a).includes('katex'), 'paste back');
const lit = cutMath('literal COMATHabcP0X and $x$');
assert.ok(pasteMath(lit.text, lit).includes('COMATHabcP0X'), 'literal placeholder-like text kept');
assert.equal(cutMath('It costs $5 and $10 today').html.length, 0, 'money stays text');
assert.equal(cutMath('code `$x$` and\n```\n$y$\n```').html.length, 0, 'code untouched');
assert.equal(cutMath('\\(a_1\\) and \\[b^2\\]').html.length, 2, 'bracket forms');
assert.equal(cutMath('double ``a $x$ b`` code').html.length, 0, 'double backtick code');
assert.equal(cutMath('escaped \\$x$ no').html.length, 0, 'escaped dollar');
assert.ok(!cutMath('$<img src=x onerror=alert(1)>$').html.join('').includes('<img'), 'katex escapes html');
const link = cutMath('see [a](https://x.org/$y$) and $z$');
const linkHtml = pasteMath(marked.parse(link.text, { async: false }) as string, link);
assert.ok(linkHtml.includes('href="https://x.org/$y$"'), 'math in a link URL stays source text: ' + linkHtml.slice(0, 80));
assert.ok(/<\/a> and <span class="katex"/.test(linkHtml), 'math in text still rendered');

if (failures.length) {
  console.error('studyMode selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('studyMode selftest ok');
}
