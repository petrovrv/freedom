// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.73.0: self-check of the memory index (run: npm test, or node src/memoryIndex.selftest.ts).
import { MEMORY_INDEX_MARKER, memoryIndex, memoryIndexWanted } from './memoryIndex.ts';

declare const process: { exitCode?: number };
const failures: string[] = [];
const assert = {
  ok: (v: unknown, what: string) => { if (!v) failures.push(what); },
  equal: (a: unknown, b: unknown, what = 'equal') => { if (a !== b) failures.push(what + ': ' + String(a)); },
};

const long = 'x'.repeat(50) + ' ' + 'y'.repeat(150);
const entries = Array.from({ length: 30 }, (_, i) => `- 2026-10-${String(i + 1).padStart(2, '0')}: [fact] ${i % 2 ? long : 'short entry ' + i}`);
const md = ['# Project memory', MEMORY_INDEX_MARKER, '', ...entries, '- ', 'note line'].join('\r\n');

assert.equal(memoryIndexWanted(md), true, 'marker found');
assert.equal(memoryIndex(md.replace(MEMORY_INDEX_MARKER, ''), 'P'), null, 'no marker: full file');
assert.equal(memoryIndex([MEMORY_INDEX_MARKER, ...entries.slice(0, 5)].join('\n'), 'P'), null, 'few entries: full file');

const ix = memoryIndex(md, 'P') as string;
assert.ok(ix.includes('--project "P" get'), 'commands');
assert.ok(!ix.includes(MEMORY_INDEX_MARKER), 'marker dropped');
assert.ok(ix.includes('[1] 2026-10-01: [fact] short entry 0'), 'short entries in full');
assert.ok(ix.includes('[2] 2026-10-02: [fact] ' + 'x'.repeat(50) + ' …'), 'long entries cut at a word');
assert.ok(ix.includes('[30] 2026-10-30: [fact] ' + long), 'newest entries in full');
assert.ok(!ix.includes('[31]'), 'empty "- " line is not an entry');
assert.ok(ix.includes('note line') && ix.includes('# Project memory'), 'other lines kept');
assert.ok(ix.includes('30 entries, 11 of them cut'), 'counts');
if (failures.length > 0) {
  for (const f of failures) console.error('FAIL: ' + f);
  process.exitCode = 1;
} else {
  console.log('memoryIndex: ok');
}
