// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.85.0 (schema 19): secretary hints and the dossier. For every message CO Web asks the server (db.php op related) for
// protocol entries of other chats and dossier rows that match the text - a mechanical full-text search, no model - and
// sends them to the agent as a hidden block before the message; the user message keeps the list for the "related" row.
// New sessions get the dossier block (op dossier_context) in level 4.

import { drivePath } from './members.ts';

export interface RelatedHit {
  chatId: string;
  projectId: string;
  env: string;
  title: string;
  no: number;
  msg: number;
  section: string;
  text: string;
  date: string;
}

export interface RelatedDossier {
  id: number;
  section: string;
  subject: string;
  text: string;
  date: string | null;
}

export interface Related {
  hits: RelatedHit[];
  dossier: RelatedDossier[];
}

// Shorter messages ("yes", "go on") get no hints: the search would only find noise.
export const HINTS_MIN_CHARS = 20;

export function wantsHints(text: string): boolean {
  return text.trim().length >= HINTS_MIN_CHARS;
}

export function hintsBlock(r: Related | null, homeHost: boolean): string {
  if (!r || (!r.hits.length && !r.dossier.length)) return '';
  const lines: string[] = [
    '[Secretary hints: entries of other chats and of the dossier that match this message (mechanical search, may be irrelevant; do not mention them unless useful)]',
  ];
  for (const h of r.hits) {
    lines.push(`- "${h.title}" (${h.chatId} msg ${h.msg}, ${h.date}, ${h.section}): ${h.text}`);
  }
  for (const d of r.dossier) {
    lines.push(`- dossier d${d.id} ${d.section}${d.subject ? ` [${d.subject}]` : ''}: ${d.text}${d.date ? ` (${d.date})` : ''}`);
  }
  if (homeHost && r.hits.length) {
    lines.push(`Open a source message: python "${drivePath('AI-Workbench/tools/aiwb-chat/aiwb-chat.py')}" get <chat> <msg>`);
  }
  lines.push('[End of secretary hints]');
  return lines.join('\n') + '\n\n';
}

// What the user message keeps for the UI: pointers only, no titles, excerpts or dossier text (review: the chat may be
// shared later with people who cannot read the source chats; opening a pointer checks access as usual)
export interface StoredHint {
  chatId: string;
  projectId: string;
  env: string;
  msg: number;
}

export function storedHints(r: Related | null): StoredHint[] | undefined {
  if (!r?.hits.length) return undefined;
  return r.hits.map((h) => ({ chatId: h.chatId, projectId: h.projectId, env: h.env, msg: h.msg }));
}

export function parseRelated(j: Record<string, unknown>): Related {
  const hits = Array.isArray(j.hits) ? (j.hits as RelatedHit[]).filter((h) => h && typeof h.chatId === 'string' && typeof h.text === 'string') : [];
  const dossier = Array.isArray(j.dossier) ? (j.dossier as RelatedDossier[]).filter((d) => d && typeof d.id === 'number' && typeof d.text === 'string') : [];
  return { hits, dossier };
}

export interface DossierRow {
  id: number;
  section: 'me' | 'people' | 'state';
  subject: string;
  text: string;
  status: 'active' | 'replaced' | 'closed' | 'deleted';
  replacedBy: number | null;
  chatId: string | null;
  msg: number | null;
  date: string | null;
  author: string;
  updatedUtc: string;
}

export const DOSSIER_SECTION_LABEL: Record<DossierRow['section'], string> = {
  me: 'About me',
  state: 'Current affairs',
  people: 'People around me',
};
