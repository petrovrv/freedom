// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.82.0: Ask / WTF about a fragment of an agent's answer (select text in the chat, as in the study viewer).
// Pure helpers: the message the chat sends ("[quote: #12]" tag + question + the fragment + a note for the agent) and
// what a person sees of it (a label and the quoted fragment instead of the tag and the note).

export const QUOTE_MAX = 2000;

export const QUOTE_WTF_TEXT =
  'I do not understand this part of your answer at all. Explain it again from scratch and more simply: every symbol and term, step by step, starting from what I would need to know first.';

const TAG_RE = /^\s*\[quote: #(\d+)\]( WTF)?/;
const NOTE_RE = /\n*\[Chat quote: [^\]]*\]\s*$/;
const FRAG_RE = /\n*Fragment of your answer #\d+: «([\s\S]*)»\s*$/;

export function quoteMessage(o: { seq: number; quote: string; question: string; wtf: boolean }): string {
  const quote = o.quote.trim().slice(0, QUOTE_MAX);
  return [
    `[quote: #${o.seq}]${o.wtf ? ' WTF' : ''}`,
    '',
    o.wtf ? QUOTE_WTF_TEXT : o.question.trim(),
    '',
    `Fragment of your answer #${o.seq}: «${quote}»`,
    '',
    `[Chat quote: the question is about the fragment above, selected in your answer #${o.seq} of this chat. Answer about that fragment, in the language of the chat.]`,
  ].join('\n');
}

export interface QuoteView {
  seq: number;
  wtf: boolean;
  label: string;
  quote: string;
  text: string;
}

export function quoteDisplay(text: string | undefined): QuoteView | null {
  const m = TAG_RE.exec(text || '');
  if (!m) return null;
  let body = (text || '').replace(TAG_RE, '').replace(NOTE_RE, '');
  const f = FRAG_RE.exec(body);
  const quote = f ? f[1].trim() : '';
  if (f) body = body.slice(0, f.index);
  body = body.replace(QUOTE_WTF_TEXT, 'WTF?! Explain this again from scratch.').trim();
  const seq = Number(m[1]);
  return { seq, wtf: !!m[2], label: `❝ answer #${seq}`, quote, text: body };
}
