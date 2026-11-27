// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.138.0: self-check of message-card markers (run: npm test, or node src/messageCards.selftest.ts).
import { MAX_CARDS, cardPage, cardsLive, extractCards } from './messageCards.ts';
import type { ModuleInfo } from './extBridge.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

const one = extractCards('Draft is ready.\n\n[[card concierge-mail/draft id=r-123_X]]\n\nChange the tone if needed.');
assert.equal(one.cards.length, 1, 'one card');
assert.equal(JSON.stringify(one.cards[0]), JSON.stringify({ module: 'concierge-mail', card: 'draft', id: 'r-123_X' }), 'card ref');
assert.equal(one.text, 'Draft is ready.\n\nChange the tone if needed.', 'marker cut');

const code = extractCards('```\n[[card concierge-mail/draft id=a]]\n```\nand `[[card concierge-mail/draft id=b]]`');
assert.equal(code.cards.length, 0, 'no card in code');
assert.ok(code.text.includes('id=a') && code.text.includes('id=b'), 'code kept');

const inline = extractCards('See [[card concierge-mail/draft id=a]] here');
const M = '[[card concierge-mail/draft id=a]]';
for (const [name, t] of [
  ['tilde fence', `~~~

${M}

~~~`], ['long backtick fence', `\`\`\`\`
\`\`\`

${M}

\`\`\`\`
`],
  ['unclosed fence', `\`\`\`js

${M}
`], ['indented code', `text

    ${M}
`], ['tab indent', `	${M}`],
  ['quote', `> ${M}`], ['list item', `- ${M}`], ['paragraph line', `before
${M}
after`],
  ['html comment', `<!--

${M}

-->`], ['loose list continuation', `- item

  ${M}

- next`],
  ['table cell', `| a |
|---|
| ${M} |`], ['emphasis around', `*${M}*`],
  ['multi-line code span', `a \`b
${M}
c\` d`], ['raw html pre', `<pre>

${M}

</pre>`], ['raw script', `<script>

${M}
`],
] as const) assert.equal(extractCards(t).cards.length, 0, 'no card in ' + name);
assert.equal(extractCards(`\`\`\`
code
\`\`\`

${M}`).cards.length, 1, 'card after a closed fence');
assert.equal(extractCards(`~~~
\`\`\`
~~~

${M}`).cards.length, 1, 'other fence char does not close');
assert.equal(extractCards(`<pre>x</pre>

${M}`).cards.length, 1, 'one-line raw html closes itself');
assert.equal(inline.cards.length, 0, 'only a line of its own is a card');

const bad = extractCards('[[card concierge-mail/draft id=a/../b]]\n[[card Concierge/draft id=a]]\n[[card concierge-mail/draft id=a b]]');
assert.equal(bad.cards.length, 0, 'bad ids refused');

const many = extractCards(Array.from({ length: MAX_CARDS + 2 }, (_, i) => `[[card m1/draft id=x${i}]]`).join('\n\n') + '\n\n[[card m1/draft id=x0]]');
assert.equal(many.cards.length, MAX_CARDS, 'at most MAX_CARDS');
assert.ok(many.text.includes(`id=x${MAX_CARDS}`), 'over the limit stays as text');
assert.ok(!/id=x0\b/.test(many.text), 'duplicate cut once shown');

const plain = 'No cards here.\n\n\n\nKept as is.';
assert.equal(extractCards(plain).text, plain, 'text without cards untouched');
const rawKept = extractCards('# T\n\n- a\n- b\n\n' + M + '\n\n```js\nx\n```');
assert.equal(rawKept.text, '# T\n\n- a\n- b\n\n```js\nx\n```', 'raw text of other blocks kept');

const mod = (id: string, enabled: boolean, ui: { slot: string; id: string }[]) =>
  ({ id, enabled, ui: ui.map((u) => ({ ...u, entry: 'ui/x.html', title: 'X' })) }) as unknown as ModuleInfo;
const mods = [mod('concierge-mail', true, [{ slot: 'ui.messageCard', id: 'draft' }, { slot: 'ui.page', id: 'mail' }]),
  mod('other', false, [{ slot: 'ui.messageCard', id: 'draft' }])];
assert.ok(cardPage(mods, { module: 'concierge-mail', card: 'draft', id: 'a' }), 'declared card found');
assert.equal(cardPage(mods, { module: 'concierge-mail', card: 'mail', id: 'a' }), null, 'a page that is no card');
assert.equal(cardPage(mods, { module: 'other', card: 'draft', id: 'a' }), null, 'disabled module');
assert.equal(cardPage(mods, { module: 'missing', card: 'draft', id: 'a' }), null, 'unknown module');

const base = { readOnly: false, shared: false, family: false, draft: false, checked: 'p/c', key: 'p/c' };
assert.ok(cardsLive(base), 'own chat, sharing checked');
assert.ok(!cardsLive({ ...base, checked: null }), 'sharing not checked yet / failed');
assert.ok(!cardsLive({ ...base, checked: 'p/other' }), 'stale check of another chat');
for (const k of ['readOnly', 'shared', 'family', 'draft'] as const) assert.ok(!cardsLive({ ...base, [k]: true }), k);
assert.ok(!cardsLive({ ...base, shareRole: 'editor' }), 'a project shared to me');

if (failures.length) { console.error('messageCards selftest FAILED:\n' + failures.join('\n')); process.exitCode = 1; }
else console.log('messageCards selftest ok');
