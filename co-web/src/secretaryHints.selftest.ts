// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/secretaryHints.selftest.ts (Node 22+ strips the types)
declare const process: { exitCode?: number };
import { hintsBlock, parseRelated, storedHints, strongHits, wantsHints } from './secretaryHints.ts';

let fails = 0;
const ok = (name: string, cond: boolean): void => {
  if (!cond) {
    fails++;
    console.log('FAIL ' + name);
  }
};

ok('short text gets no hints', !wantsHints('да, давай') && wantsHints('почему капча Cloudflare не проходит в браузере'));
ok('empty result gives no block', hintsBlock(null, true) === '' && hintsBlock({ hits: [], dossier: [] }, true) === '');
const r = parseRelated({
  hits: [{ chatId: 'CHAT-1', projectId: 'P', env: 'owner', title: 'Капча', no: 3, msg: 7, section: 'decision', text: 'Капча только по разрешению', date: '2026-10-31', score: 5 }, { bogus: 1 }],
  dossier: [{ id: 12, section: 'people', subject: 'Анна', text: 'дочь', date: null }, { id: 'x' }],
});
ok('parse drops malformed rows', r.hits.length === 1 && r.dossier.length === 1);
const b = hintsBlock(r, true);
ok('block lists chat, message and dossier row', b.includes('CHAT-1 msg 7') && b.includes('dossier d12 people [Анна]: дочь') && b.endsWith('[End of secretary hints]\n\n'));
ok('home host gets the aiwb-chat pointer', b.includes('aiwb-chat.py') && !hintsBlock(r, false).includes('aiwb-chat.py'));
ok('the pointer is a usable Windows path', b.includes(String.raw`"G:\My Drive\AI-Workbench\tools\aiwb-chat\aiwb-chat.py"`));
const s = storedHints(r);
ok('stored hints keep pointers only', !!s && s.length === 1 && !JSON.stringify(s).includes('дочь') && !JSON.stringify(s).includes('Капча') && s[0].msg === 7);
ok('no hits, nothing stored', storedHints({ hits: [], dossier: r.dossier }) === undefined);
// 0.130.0: a resumed session gets only the strong hits (3+ rare words), the dossier rows stay
const weak = { hits: [{ ...r.hits[0], rare: 2 }, { ...r.hits[0], chatId: 'CHAT-2', rare: 3 }], dossier: r.dossier };
const st = strongHits(weak);
ok('resume keeps hits with 3+ rare words', !!st && st.hits.length === 1 && st.hits[0].chatId === 'CHAT-2' && st.dossier.length === 1);
ok('a hit without rare (old server) is weak', strongHits(r)!.hits.length === 0 && strongHits(null) === null);

if (fails) {
  console.log(`secretaryHints selftest: ${fails} failed`);
  process.exitCode = 1;
} else console.log('secretaryHints selftest ok');
