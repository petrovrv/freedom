// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.138.0 ext/1 ui.messageCard: an agent's answer may hold [[card <module>/<card> id=<opaque id>]] lines; the core shows
// the module's card page (a sandboxed iframe) there instead. The marker carries only a reference (e.g. a Gmail draft
// id), never the content: the page reads that itself (mail: end-to-end encrypted from the home server). Markers are cut
// only from finished agent answers of the person's own writable chats (ChatView), never from the shared Markdown
// component (quotes, imported or user text keep them as text), and never inside code.

import { marked } from 'marked';
import type { ModuleInfo, ModuleUi } from './extBridge';

// the whole text of a top-level paragraph (marked.lexer), nothing else in it
const CARD_TEXT = /^\[\[card ([a-z0-9][a-z0-9-]{1,40})\/([a-z0-9][a-z0-9-]{0,40}) id=([A-Za-z0-9_-]{1,128})\]\]$/;
export const MAX_CARDS = 3;

export interface CardRef { module: string; card: string; id: string }

/**
 * The cards of an answer (in order, duplicates once, at most MAX_CARDS) and its text without their markers. Uses the
 * Markdown lexer of the chat renderer (marked, GFM) and looks only at top-level paragraph tokens whose text is exactly
 * one marker; lists, quotes, code, HTML (comments too) and tables are never entered. The rest keeps its raw text.
 */
export function extractCards(text: string): { text: string; cards: CardRef[] } {
  let tokens: { type: string; raw: string; text?: string }[];
  try { tokens = marked.lexer(text, { gfm: true, breaks: false }) as unknown as typeof tokens; } catch { return { text, cards: [] }; }
  const cards: CardRef[] = [];
  const seen = new Set<string>();
  let dropped = false;
  const kept = tokens.map((t) => {
    const c = t.type === 'paragraph' ? CARD_TEXT.exec((t.text ?? '').trim()) : null;
    if (!c || !/^ {0,3}\[\[card /.test(t.raw)) return t.raw;
    const key = `${c[1]}/${c[2]}/${c[3]}`;
    if (seen.has(key)) { dropped = true; return ''; }
    if (cards.length >= MAX_CARDS) return t.raw; // over the limit: stays visible as text
    seen.add(key);
    cards.push({ module: c[1], card: c[2], id: c[3] });
    dropped = true;
    return '';
  });
  if (!dropped) return { text, cards };
  return { text: kept.join('').replace(/\n{3,}/g, '\n\n').trim(), cards };
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
