// SPDX-License-Identifier: AGPL-3.0-or-later
// Self-check of chat modes of modules (ext/1 chat.mode, F10), the message views / side jobs / file actions / navigation
// checks of extBridge.ts and formulas (run: npm test, or node src/moduleModes.selftest.ts).
import { inheritedModes, linesBetween, modeLines, modeOn, modePatch, modesText, switchWho, type ModeDecl } from './moduleModes.ts';
import { fileActionsFor, filePathOk, navTarget, parseExtMessage, reqRefusal, sideJobOf, sideReplies, siblingOk, viewMessage, type ModuleInfo } from './extBridge.ts';
import { cutMath, pasteMath } from './mathText.ts';
import { marked } from 'marked';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

// a chat document with mode fields (Chat has no index signature)
const c = (o: Record<string, unknown>) => o as { draft?: boolean };
const MODE: ModeDecl = { key: 'tutor', label: 'Tutor mode', short: 'Tutor', icon: 'T', rules: 'RULES', offNote: 'OFF', hintOn: '', hintOff: '',
  projectLabel: 'Tutor for new chats', newProjectHint: '', inherit: true };
const OTHER: ModeDecl = { ...MODE, key: 'brief', rules: 'BRIEF', offNote: '', inherit: false };

// chat switch wins over the project default
assert.equal(modeOn({}, {}, 'tutor'), false, 'off by default');
assert.equal(modeOn({ draft: true }, { tutorDefault: true }, 'tutor'), true, 'project default for a new chat');
assert.equal(modeOn({}, { tutorDefault: true }, 'tutor'), false, 'existing chat ignores the project default');
assert.equal(modeOn(c({ tutor: false }), { tutorDefault: true }, 'tutor'), false, 'chat off wins');
assert.equal(modeOn(c({ tutor: true }), {}, 'tutor'), true, 'chat on');

// text: rules while on, a note only after it was switched off
assert.equal(modesText({}, {}, [MODE]), '', 'never on: nothing');
assert.equal(modesText({ draft: true }, { tutorDefault: true }, [MODE]), 'RULES', 'default on: rules');
const p1 = modePatch({ draft: true }, { tutorDefault: true }, 'tutor', 'alex@example.org');
assert.equal(p1.tutor, false, 'patch flips the effective state');
assert.equal((p1.tutorLog as unknown[]).length, 1, 'patch logs');
assert.equal(modesText(p1, { tutorDefault: true }, [MODE]), 'OFF', 'switched off: note');
const p2 = modePatch(p1, { tutorDefault: true }, 'tutor', 'alex@example.org');
assert.ok(p2.tutor === true && (p2.tutorLog as { on: boolean }[]).length === 2 && (p2.tutorLog as { on: boolean }[])[1].on, 'second switch');
let log = p2.tutorLog;
for (let i = 0; i < 60; i++) log = modePatch(c({ tutor: i % 2 === 0, tutorLog: log }), {}, 'tutor', 'x').tutorLog;
assert.equal((log as unknown[]).length, 50, 'log capped');
assert.equal(modesText(c({ tutor: true, brief: true }), {}, [MODE, OTHER]), 'RULES\n\nBRIEF', 'two modes: both rules');
assert.equal(JSON.stringify(inheritedModes(c({ tutor: true, brief: true }), {}, [MODE, OTHER])), '{"tutor":true}', 'only inheriting modes are kept');
assert.equal(switchWho('alex@example.org', { 'alex@example.org': 'Alex' }), 'Alex', 'who: known e-mail');
assert.equal(switchWho('alex.smith@x.be'), 'Alex', 'who: first name');

const chat = { tutorLog: [{ on: true, by: 'a', utc: '2026-11-03T10:00:00Z' }, { on: false, by: 'a', utc: '2026-11-03T12:00:00Z' }], briefLog: [{ on: true, by: 'b', utc: '2026-11-03T11:00:00Z' }] };
const lines = modeLines(chat, [MODE, OTHER]);
assert.equal(lines.map((l) => l.mode.key).join(','), 'tutor,brief,tutor', 'lines of all modes by time');
assert.equal(linesBetween(lines, null, '2026-11-03T11:00:00Z').length, 2, 'lines before first message');
assert.equal(linesBetween(lines, '2026-11-03T11:00:00Z', null).length, 1, 'tail lines');

// message views: a tag of a module page shown as a label, technical tail stripped, a long text replaced
const views = [
  { match: '^\\s*\\[doc: ([^\\],]+), p\\. (\\d+), page\\]', label: 'D $1 · p. $2', strip: ['\\n*\\[Viewer: [\\s\\S]*\\]\\s*$'], replace: [['LONG TEXT \\(x\\)', 'short']] as [string, string][] },
  { match: '^\\s*\\[doc: ([^\\],]+), p\\. (\\d+), (area|text)', label: 'D $1 · p. $2 · $3', strip: [], replace: [] as [string, string][] },
];
const v1 = viewMessage(views, '[doc: a.pdf, p. 7, page]\n\nLONG TEXT (x)\n\n[Viewer: note]');
assert.ok(v1?.label === 'D a.pdf · p. 7' && v1.text === 'short', 'view: page ' + JSON.stringify(v1));
assert.equal(viewMessage(views, '[doc: a.pdf, p. 3, area 0.100 0.100 0.200 0.200] hi')?.label, 'D a.pdf · p. 3 · area', 'view: second entry');
assert.equal(viewMessage(views, 'plain'), null, 'view: no match');
assert.equal(viewMessage([{ match: '(', label: 'x', strip: [], replace: [] }], '('), null, 'view: bad regex ignored');

// side jobs
const job = { key: 'sidekey', label: 'S', list: 'threads', item: 'reply', count: 'ops', countNoun: 'change' };
assert.equal(sideJobOf([job], { sidekey: { path: 'p/x.docx', file: 'x.docx' } })?.file, 'x.docx', 'side job found');
assert.equal(sideJobOf([job], { other: 1 }), null, 'not a side job');
assert.equal(sideReplies(job, 'ok:\n```json\n{"threads":[{"reply":"A","ops":[1,2]},{"reply":"B"}]}\n```').join('|'), 'A (2 changes)|B', 'side replies');
assert.equal(sideReplies(job, 'not json').join('|'), 'not json', 'side replies: plain text');

// file actions
const mod = { id: 'm', enabled: true, fileActions: [{ id: 'a', label: 'Open', title: '', exts: ['pdf', 'docx'], exclude: '\\.copy\\.pdf$', open: { mode: 'tutor' } }] } as unknown as ModuleInfo;
assert.equal(fileActionsFor([mod], 'A.PDF').length, 1, 'file action by extension');
assert.equal(fileActionsFor([mod], 'a.copy.pdf').length, 0, 'file action excluded');
assert.equal(fileActionsFor([{ ...mod, enabled: false }], 'a.pdf').length, 0, 'disabled module: no action');

// navigation and core services
const nm = { enabled: true, permissions: ['ui.navigate'], navigate: ['https://pc.example.net', '/auth/login.php'] };
const here = 'https://co.example.org/x/m/page?path=x';
assert.equal(navTarget(nm, 'https://pc.example.net/cb/edit#t', here), 'https://pc.example.net/cb/edit#t', 'nav: declared origin');
assert.equal(navTarget(nm, '/auth/login.php?add=gmail', here), 'https://co.example.org/auth/login.php?add=gmail', 'nav: declared path');
assert.equal(navTarget(nm, '/agent/db.php', here), null, 'nav: other path of the site');
assert.equal(navTarget(nm, 'javascript:alert(1)', here), null, 'nav: javascript');
assert.equal(navTarget(nm, 'https://evil.example.com/', here), null, 'nav: other origin');
assert.equal(navTarget(nm, 'http://pc.example.net/', here), null, 'nav: not https');
assert.equal(navTarget({ ...nm, permissions: [] }, 'https://pc.example.net/', here), null, 'nav: no permission');
assert.equal(reqRefusal({ enabled: true, permissions: ['files.read'] }, 'file.get', 'ui.panel'), '', 'req: allowed');
assert.equal(reqRefusal({ enabled: true, permissions: [] }, 'file.get', 'ui.panel'), 'permission_not_declared', 'req: no permission');
assert.equal(reqRefusal({ enabled: true, permissions: ['chat.post'] }, 'chat.ask', 'ui.page'), 'not_a_panel', 'req: chat only from a panel');
assert.equal(reqRefusal({ enabled: true, permissions: [] }, 'toString', 'ui.panel'), 'unknown_request', 'req: unknown');
assert.ok(filePathOk('projects/P/chats/C/scratch/a.pdf') && !filePathOk('secrets/x') && !filePathOk('projects/../x') && !filePathOk('projects/a//b'), 'file paths');
assert.ok(siblingOk('projects/P/a.pptx', 'a.pptx.view.pdf') && !siblingOk('projects/P/a.pptx', 'b.pdf') && !siblingOk('projects/P/a.pptx', 'a.pptx/../x'), 'sibling names');
const big = parseExtMessage({ ext: 1, t: 'req', id: 'r1', name: 'chat.ask', args: { text: 'x', files: [{ name: 'a.png', type: 'image/png', bytes: new ArrayBuffer(1_000_000) }] } });
assert.equal(big?.t, 'req', 'req with a file');
assert.equal(parseExtMessage({ ext: 1, t: 'req', id: 'r1', name: 'bad name', args: {} }), null, 'req: bad name');
assert.equal(parseExtMessage({ ext: 1, t: 'navigate', url: 'https://x', target: 'blank' })?.t, 'navigate', 'navigate parsed');

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
  console.error('moduleModes selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('moduleModes selftest ok');
}
