// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.57.0: quick replies. The agent ends an answer with [[options: A | B | C]] (buttons, a click sends the label as the
// user's message) or writes short questions as [[ask: question | suggestion | …]] lines (a numbered list with an input
// field each, sent together as one message). The markers are cut from the shown text; the stored text keeps them, so
// other agents see what was offered.

const OPTIONS_RE = /[ \t]*\[\[\s*(?:options|choices)\s*:\s*([^\]\n]{1,600}?)\s*\]\][ \t]*/gi;
const ASK_RE = /(?:^[ \t]*(?:[-*]\s+|\d+[.)]\s+))?[ \t]*\[\[\s*ask\s*:\s*([^\]\n]{1,600}?)\s*\]\][ \t]*/gim;

const CODE_RE = /(```[\s\S]*?(?:```|$)|`[^`\n]*`)/;

const MAX_OPTIONS = 8;
const MAX_ASKS = 12;

export interface AskItem {
  question: string;
  suggestions: string[];
}

export interface QuickReplies {
  text: string; // the answer without the markers
  options: string[];
  asks: AskItem[];
}

const splitBar = (s: string) => s.split('|').map((x) => x.replace(/\s+/g, ' ').trim()).filter(Boolean);

// 0.82.1: [[silent]] at the start of an answer = no push (the server drops it, A1 observer); not shown.
const SILENT_RE = /^\s*\[\[\s*silent\s*\]\][ \t]*\n?/i;

export function extractQuickReplies(text: string): QuickReplies {
  text = text.replace(SILENT_RE, '');
  const options: string[] = [];
  const asks: AskItem[] = [];
  // A marker over the limits stays in the text as it is, so nothing the agent wrote disappears.
  // Markers inside code (fenced blocks and `inline`) are examples, not replies: only the parts between code are parsed.
  const parts = text.split(CODE_RE);
  const outside = (fn: (s: string) => string) => parts.forEach((p, i) => { if (i % 2 === 0) parts[i] = fn(p); });
  outside((p) => p.replace(OPTIONS_RE, (m, body: string) => {
    const list = splitBar(body).filter((o) => !options.includes(o));
    if (!list.length || options.length + list.length > MAX_OPTIONS) return m;
    options.push(...list);
    return '';
  }));
  outside((p) => p.replace(ASK_RE, (m, body: string) => {
    const [question, ...suggestions] = splitBar(body);
    if (!question || asks.length >= MAX_ASKS) return m;
    asks.push({ question, suggestions });
    return '';
  }));
  if (!options.length && !asks.length) return { text, options, asks };
  return { text: parts.join('').replace(/\n{3,}/g, '\n\n').trimEnd(), options, asks };
}

export const stripQuickReplies = (text: string) => extractQuickReplies(text).text;

// The message the form sends: one line per answered question.
export function formatAnswers(asks: AskItem[], answers: string[]): string {
  return asks
    .map((a, i) => ((answers[i] || '').trim() ? `${i + 1}. ${a.question} — ${answers[i].trim()}` : ''))
    .filter(Boolean)
    .join('\n');
}

// Instruction for the agent (part of the environment note).
export const QUICK_REPLIES_NOTE =
  '- Quick replies: when you ask the user to choose or confirm (for example whether to run a command or which option to take), ' +
  'end the answer with one line [[options: <choice 1> | <choice 2> | …]] (2–6 short choices, the user does not need to type "go ahead"); ' +
  'CO shows them as buttons under your answer and a click sends that text as the reply. When you have several short questions, ' +
  'write each as its own line [[ask: <question>]] or [[ask: <question> | <suggested answer> | …]]; CO shows them as a numbered list ' +
  'with an input field each and sends all answers in one message. Questions that need a long answer stay in normal text. ' +
  'The markers are removed from the displayed text; the user can always answer in the chat instead.';
