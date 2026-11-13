// SPDX-License-Identifier: AGPL-3.0-or-later
// Study module: the messageViews of module.json, applied by the core (co-web extBridge.viewMessage), show a viewer
// question exactly as askDisplay() of the pre-module core did (run: node modules/study/tests/views.selftest.ts).
import { readFileSync } from 'node:fs';
import { askDisplay, askMessage, WTF_TEXT } from '../ui-src/studyDoc.ts';
import { viewMessage } from '../../../co-web/src/extBridge.ts';

declare const process: { exitCode?: number };
const man = JSON.parse(readFileSync(new URL('../module.json', import.meta.url), 'utf-8'));
const views = man.provides.messageViews;
const texts = [
  askMessage({ tag: { doc: 'Lecture 3.pdf', page: 7, shape: 'page', wtf: false }, question: 'Why?', path: 'projects/P/x/Lecture 3.pdf', pagesNote: 'pages 6-8', hasImage: false }),
  askMessage({ tag: { doc: 'L.pdf', page: 2, shape: 'area', rect: { x: 0.1, y: 0.2, w: 0.3, h: 0.4 }, wtf: true }, question: '', path: 'projects/P/L.pdf', selected: 'abc', pagesNote: 'pages 1-3', hasImage: true }),
  askMessage({ tag: { doc: 'L.pdf', page: 1, shape: 'text', wtf: false }, question: 'What is ' + WTF_TEXT.slice(0, 10), path: 'projects/P/L.pdf', selected: 'x', pagesNote: 'page 1', hasImage: false }),
  'plain message',
];
let fail = 0;
for (const t of texts) {
  const a = JSON.stringify(askDisplay(t));
  const b = JSON.stringify(viewMessage(views, t));
  if (a !== b) { fail++; console.error('differs:\n  old ' + a + '\n  new ' + b); }
}
if (fail) process.exitCode = 1;
else console.log('study views selftest ok');
