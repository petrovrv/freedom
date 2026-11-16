// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/familyFollow.selftest.ts - 0.107.0 follow window of @co in a family chat
import { followsTopic, FOLLOW_MESSAGES } from './familyFollow.ts';
import type { ChatEvent } from './types.ts';

let bad = 0;
const ok = (name: string, cond: boolean) => { console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`); if (!cond) bad++; };
const now = Date.parse('2026-11-16T20:00:00Z');
const ago = (min: number) => new Date(now - min * 60000).toISOString();
const person = (text = 'hi'): ChatEvent => ({ type: 'user_message', family: true, author: 'a@x', text, eventId: Math.random().toString(36), createdUtc: ago(1) } as unknown as ChatEvent);
const agent = (min: number, text = 'answer', status = 'completed'): ChatEvent => ({ type: 'assistant_message', text, status, completedUtc: ago(min), createdUtc: ago(min), eventId: 'a' + min } as unknown as ChatEvent);

ok('no agent answer: no follow', !followsTopic([person(), person()], now));
ok('right after an answer: follow', followsTopic([person('@co x'), agent(2), person()], now));
ok('answer too old: no follow', !followsTopic([person('@co x'), agent(45)], now));
ok('silent answers do not extend', !followsTopic([person('@co x'), agent(40), person(), agent(1, '[[silent]]')], now));
ok('silent skipped, spoken one counts', followsTopic([person('@co x'), agent(5), person(), agent(1, '[[silent]]')], now));
ok('failed answer does not count', !followsTopic([person('@co x'), agent(1, 'err', 'failed')], now));
ok(`within ${FOLLOW_MESSAGES} messages`, followsTopic([agent(3), ...Array.from({ length: FOLLOW_MESSAGES }, () => person())], now));
ok('too many messages since', !followsTopic([agent(3), ...Array.from({ length: FOLLOW_MESSAGES + 1 }, () => person())], now));
if (bad) throw new Error(`familyFollow selftest: ${bad} failed`);
console.log('familyFollow selftest ok');
