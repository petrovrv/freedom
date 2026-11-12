// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.82.0: self-check of the chat quote helpers (run: npm test, or node src/chatQuote.selftest.ts).
import { QUOTE_MAX, QUOTE_WTF_TEXT, quoteDisplay, quoteMessage } from './chatQuote.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

const m = quoteMessage({ seq: 12, quote: '  the wave function $\\psi$ is normalised\n\nso that  ', question: ' why squared? ', wtf: false });
assert.ok(m.startsWith('[quote: #12]\n\nwhy squared?'), 'tag and question first');
assert.ok(m.includes('Fragment of your answer #12: «the wave function $\\psi$ is normalised\n\nso that»'), 'fragment trimmed, kept multi-line');
const d = quoteDisplay(m);
assert.ok(d && d.seq === 12 && !d.wtf, 'parse');
assert.equal(d?.text, 'why squared?', 'display text without the note');
assert.equal(d?.quote, 'the wave function $\\psi$ is normalised\n\nso that', 'display quote');
assert.equal(d?.label, '❝ answer #12', 'label');

const w = quoteMessage({ seq: 3, quote: 'x', question: 'ignored', wtf: true });
assert.ok(w.startsWith('[quote: #3] WTF') && w.includes(QUOTE_WTF_TEXT) && !w.includes('ignored'), 'wtf message');
const wd = quoteDisplay(w);
assert.ok(wd && wd.wtf && wd.text === 'WTF?! Explain this again from scratch.', 'wtf display');

assert.equal(quoteDisplay('hello [quote: #1]'), null, 'tag only at the start');
assert.equal(quoteDisplay(undefined), null, 'no text');
assert.equal(quoteMessage({ seq: 1, quote: 'a'.repeat(QUOTE_MAX + 50), question: 'q', wtf: false }).split('«')[1].split('»')[0].length, QUOTE_MAX, 'fragment capped');
// a quote that itself contains » or ] still round-trips
const tricky = quoteDisplay(quoteMessage({ seq: 5, quote: 'a » b ] c', question: 'q', wtf: false }));
assert.equal(tricky?.quote, 'a » b ] c', 'quote with » and ]');

if (failures.length) {
  console.error('chatQuote selftest FAILED:\n' + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log('chatQuote selftest ok');
}
