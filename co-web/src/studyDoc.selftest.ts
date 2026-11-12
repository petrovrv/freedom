// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.80.0: self-check of the study viewer helpers (run: npm test, or node src/studyDoc.selftest.ts).
import { askDisplay, askMessage, docTag, driveTarget, jpegPdf, parseDocTag, studyKind, studyPdfName, WTF_TEXT } from './studyDoc.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

// formats
assert.equal(studyKind('Lecture 3.PDF'), 'pdf', 'pdf');
assert.equal(studyKind('slides.pptx'), 'drive', 'pptx via Drive');
assert.equal(studyKind('notes.md'), 'drive', 'md via Drive');
assert.equal(studyKind('scan.JPG'), 'image', 'jpg');
assert.equal(studyKind('x.study.pdf'), null, 'own copy is not offered');
assert.equal(studyKind('a.zip'), null, 'zip');
assert.equal(driveTarget('a.xlsx'), 'application/vnd.google-apps.spreadsheet', 'xlsx target');
assert.equal(studyPdfName('Lecture 3.pptx'), 'Lecture 3.pptx.study.pdf', 'copy name');
assert.ok(studyPdfName('a.docx') !== studyPdfName('a.pptx'), 'same stem, separate copies');

// tags round trip
const t = { doc: 'Lecture 3.pdf', page: 7, shape: 'area' as const, rect: { x: 0.12345, y: 0.5, w: 0.3, h: 1.2 }, wtf: true };
const s = docTag(t);
assert.equal(s, '[doc: Lecture 3.pdf, p. 7, area 0.123 0.500 0.300 1.000] WTF', 'tag text');
const p = parseDocTag(s + '\n\nmore');
assert.ok(p && p.page === 7 && p.shape === 'area' && p.wtf && p.rect && p.rect.x === 0.123 && p.rect.h === 1, 'tag parse');
assert.equal(parseDocTag('[doc: a, b.pdf, p. 1, page]')?.doc, undefined, 'comma in name is not parsed (the writer strips it)');
assert.equal(parseDocTag(docTag({ doc: 'a, b.pdf', page: 1, shape: 'page', wtf: false }))?.doc, 'a  b.pdf', 'writer strips commas');
assert.equal(parseDocTag('hello [doc: x.pdf, p. 1, page]'), null, 'tag only at the start');
assert.equal(parseDocTag(undefined), null, 'no text');

const m = askMessage({ tag: { ...t, wtf: false }, question: ' Why minus? ', path: 'projects/P/chats/C/scratch/Lecture 3.pdf', selected: 'E = mc^2', pagesNote: 'pages 6-8', hasImage: true });
assert.ok(m.startsWith('[doc: Lecture 3.pdf, p. 7, area') && m.includes('Why minus?') && m.includes('«E = mc^2»') && m.includes('AI-Workbench/projects/P/chats/C/scratch/Lecture 3.pdf') && m.includes('PNG'), 'ask message');
assert.ok(askMessage({ tag: t, question: '', path: 'x.pdf', pagesNote: 'page 7', hasImage: false }).includes(WTF_TEXT), 'wtf text');

const d = askDisplay(m);
assert.ok(d && d.label === '📖 Lecture 3.pdf · p. 7 · area' && d.text.startsWith('Why minus?') && !d.text.includes('[Study viewer') && d.text.includes('«E = mc^2»'), 'display of a question');
assert.equal(askDisplay(askMessage({ tag: t, question: '', path: 'x.pdf', pagesNote: 'page 7', hasImage: false }))?.text, 'WTF?! Explain this from scratch.', 'display of WTF');
assert.equal(askDisplay('plain'), null, 'plain message');

// pdf writer: header, xref offsets point at objects
const jpg = new Uint8Array([0xff, 0xd8, 0xff, 0xd9]);
const pdf = jpegPdf([{ bytes: jpg, width: 100, height: 200 }, { bytes: jpg, width: 40, height: 40 }]);
const txt = new TextDecoder('latin1').decode(pdf);
assert.ok(txt.startsWith('%PDF-1.4'), 'pdf header');
assert.ok(txt.includes('/Count 2') && txt.includes('/MediaBox [0 0 75 150]'), 'pages and size');
const xref = Number(/startxref\n(\d+)/.exec(txt)?.[1]);
const enc = new TextEncoder();
const at = (needle: string) => { const n = enc.encode(needle); outer: for (let i = 0; i < pdf.length; i++) { for (let j = 0; j < n.length; j++) if (pdf[i + j] !== n[j]) continue outer; return i; } return -1; };
assert.equal(xref, at('xref\n'), 'startxref offset');
const entries = txt.slice(txt.indexOf('xref\n')).split('\n').slice(3, 11).map((l) => Number(l.slice(0, 10)));
entries.forEach((off, i) => assert.equal(off, at(`${i + 1} 0 obj`), `xref of object ${i + 1}`));

if (failures.length) {
  console.error('studyDoc selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else console.log('studyDoc selftest ok');
