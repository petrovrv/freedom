// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.138.0 ext/1 ui.messageCard: an agent's answer may hold [[card <module>/<card> id=<opaque id>]] lines; the core shows
// the module's card page (a sandboxed iframe) there instead. The marker carries only a reference (e.g. a Gmail draft
// id), never the content: the page reads that itself (mail: end-to-end encrypted from the home server). Markers are cut
// only from finished agent answers of the person's own writable chats (ChatView), never from the shared Markdown
// component (quotes, imported or user text keep them as text), and never inside code.

import type { ModuleInfo, ModuleUi } from './extBridge';

// a paragraph of its own (blank line or start before, blank line or end after), at most 3 spaces of indent
const CARD_LINE = /^ {0,3}\[\[card ([a-z0-9][a-z0-9-]{1,40})\/([a-z0-9][a-z0-9-]{0,40}) id=([A-Za-z0-9_-]{1,128})\]\] *$/;
const FENCE = /^ {0,3}(`{3,}|~{3,})/;
// raw HTML blocks that run across blank lines until their closing tag (CommonMark HTML block type 1)
const RAW_OPEN = /^ {0,3}<(pre|script|style|textarea)(?:\s|>|$)/i;
export const MAX_CARDS = 3;

export interface CardRef { module: string; card: string; id: string }

/**
 * The cards of an answer (in order, duplicates once, at most MAX_CARDS) and its text without their markers. A
 * conservative line scanner: never inside a fenced block (``` or ~~~, any length), an indented block, a raw HTML block,
 * a quote or list item, or a paragraph (where a code span could hold it); over the limit the line stays text.
 */
export function extractCards(text: string): { text: string; cards: CardRef[] } {
  const lines = text.split('\n');
  const cards: CardRef[] = [];
  const seen = new Set<string>();
  const drop = new Set<number>();
  let fence = '';
  let raw = '';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (fence) {
      const m = FENCE.exec(line);
      if (m && m[1][0] === fence[0] && m[1].length >= fence.length && !line.slice(line.indexOf(m[1]) + m[1].length).trim()) fence = '';
      continue;
    }
    if (raw) {
      if (new RegExp(`</${raw}>`, 'i').test(line)) raw = '';
      continue;
    }
    const f = FENCE.exec(line);
    if (f) { fence = f[1]; continue; }
    const r = RAW_OPEN.exec(line);
    if (r) { if (!new RegExp(`</${r[1]}>`, 'i').test(line)) raw = r[1].toLowerCase(); continue; }
    const c = CARD_LINE.exec(line);
    if (!c) continue;
    const blank = (j: number) => j < 0 || j >= lines.length || !lines[j].trim();
    if (!blank(i - 1) || !blank(i + 1)) continue;
    const key = `${c[1]}/${c[2]}/${c[3]}`;
    if (seen.has(key)) { drop.add(i); continue; }
    if (cards.length >= MAX_CARDS) continue;
    seen.add(key);
    cards.push({ module: c[1], card: c[2], id: c[3] });
    drop.add(i);
  }
  if (!drop.size) return { text, cards };
  return { text: lines.filter((_, i) => !drop.has(i)).join('\n').replace(/\n{3,}/g, '\n\n').trim(), cards };
}

/** The page that shows a card: an enabled module that declares a ui.messageCard with that id; null = text fallback. */
export function cardPage(modules: ModuleInfo[], ref: CardRef): { m: ModuleInfo; ui: ModuleUi } | null {
  const m = modules.find((x) => x.id === ref.module);
  const ui = m?.enabled ? m.ui.find((u) => u.slot === 'ui.messageCard' && u.id === ref.card) : undefined;
  return m && ui ? { m, ui } : null;
}

/**
 * Whether cards may show live in a chat: only the person's own writable chat, and only after the sharing lookup for
 * exactly this chat came back with no shares (`checked` = the key it was made for); unknown, failed or stale = no.
 */
export function cardsLive(o: { readOnly: boolean; shared: boolean; family: boolean; draft: boolean; shareRole?: string; checked: string | null; key: string }): boolean {
  return !o.readOnly && !o.shared && !o.family && !o.draft && !o.shareRole && o.checked === o.key;
}
