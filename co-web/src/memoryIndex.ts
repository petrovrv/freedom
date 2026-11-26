// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.73.0: memory on demand. A project whose context/memory.md holds the marker line MEMORY_INDEX_MARKER gives
// new agent sessions an index of the file (one line per entry, long entries cut) plus the commands that read a full
// entry; the full file is still sent when the agent cannot run them (the site executor, the gateway agents) or the file
// is small. Entry numbers are the order of "- " lines, the same as aiwb-memory get/list -n and the server's
// co_memory_parse_lines.

import { drivePath } from './members.ts';

export const MEMORY_INDEX_MARKER = '<!-- memory: index -->';
// 0.130.0 (C0J6): a memory.md longer than this gets the index without the marker (Freedom 75k and Job_Search 50k
// characters went in full into every new session); the marker MEMORY_FULL_MARKER keeps a big file in full.
export const MEMORY_FULL_MARKER = '<!-- memory: full -->';
export const MEMORY_AUTO_INDEX_CHARS = 12000;

// Entries up to this length are given in full; longer ones are cut to INDEX_CUT characters.
const FULL_LEN = 160;
const INDEX_CUT = 110;
// The newest entries are given in full whatever their length (the current decisions).
const NEWEST_FULL = 8;
// Below this number of entries the index saves too little.
const MIN_ENTRIES = 20;

const aiwbMemory = (): string => `python "${drivePath('AI-Workbench/tools/aiwb-memory/aiwb-memory.py')}"`;

export function memoryIndexWanted(md: string): boolean {
  const lines = md.split(/\r?\n/).map((l) => l.trim());
  if (lines.includes(MEMORY_INDEX_MARKER)) return true;
  return !lines.includes(MEMORY_FULL_MARKER) && md.length > MEMORY_AUTO_INDEX_CHARS;
}

function isEntry(line: string): boolean {
  return line.startsWith('- ') && line.slice(2).trim() !== '';
}

// The index text, or null when the file is better sent in full (no marker, too few entries).
export function memoryIndex(md: string, projectId: string): string | null {
  if (!memoryIndexWanted(md)) return null;
  const lines = md.replace(/^\uFEFF/, '').split(/\r?\n/).map((l) => l.replace(/\s+$/, ''));
  const total = lines.filter(isEntry).length;
  if (total < MIN_ENTRIES) return null;
  const out: string[] = [];
  let n = 0;
  let cut = 0;
  for (const l of lines) {
    if (l.trim() === MEMORY_INDEX_MARKER || l.trim() === MEMORY_FULL_MARKER) continue;
    if (!isEntry(l)) {
      out.push(l);
      continue;
    }
    n++;
    const body = l.slice(2);
    if (body.length <= FULL_LEN || n > total - NEWEST_FULL) {
      out.push(`[${n}] ${body}`);
    } else {
      out.push(`[${n}] ${body.slice(0, INDEX_CUT).replace(/\s+\S*$/, '')} …`);
      cut++;
    }
  }
  const p = JSON.stringify(projectId);
  const head = [
    `This is an index of the project memory (context/memory.md, ${total} entries, ${cut} of them cut and marked …; the ` +
      `newest ${NEWEST_FULL} are complete). Before relying on a cut entry or when the task touches its subject, read it in full:`,
    `  ${aiwbMemory()} --project ${p} get 12 40`,
    `  ${aiwbMemory()} --project ${p} search "words"   (add --archive to include superseded entries; asof YYYY-MM-DD shows the memory as of a date)`,
    `or read ${drivePath(`AI-Workbench/projects/${projectId}/context/memory.md`)} directly.`,
  ];
  return [...head, '', ...out].join('\n').replace(/\n{3,}/g, '\n\n').trim();
}
