// SPDX-License-Identifier: AGPL-3.0-or-later
// Smart chat titles. On the first message the chat gets an interim title from a heuristic (the
// question openers "can you / можешь / как …" are cut off), and the agent is asked to add a title
// marker to its first answer. The marker is stripped from the answer and becomes the final title,
// so naming costs no extra job and the text never leaves the model the user chose (a confidential model stays confidential).

const MARKER_RE = /\[\[\s*chat-title\s*:\s*([^\]\n]{1,120}?)\s*\]\]/gi;

export const TITLE_REQUEST =
  '\n\n[CO: this is the first message of a new chat. After your complete answer, on a separate last line, write ' +
  '[[chat-title: <title>]] where <title> is a short name for this chat: 2–6 words, in the language of the user\'s ' +
  'message, a noun phrase describing the topic (not a question, no modal verbs, no quotes, no final period). ' +
  'This line is removed before the user sees your answer.]';

// Removes title markers from an agent answer and returns the last proposed title.
export function extractTitle(text: string): { text: string; title: string | null } {
  let title: string | null = null;
  const stripped = text.replace(MARKER_RE, (_m, t: string) => {
    title = t;
    return '';
  });
  if (title === null) return { text, title: null };
  return { text: stripped.replace(/\n{3,}/g, '\n\n').trimEnd(), title: cleanTitle(title) };
}

export const stripTitleMarker = (text: string) => extractTitle(text).text;

function cleanTitle(t: string): string | null {
  const c = t.replace(/[«»"“”'`*_#]/g, '').replace(/\s+/g, ' ').replace(/[.:;,\s]+$/, '').trim();
  if (c.length < 2) return null;
  return clip(c[0].toUpperCase() + c.slice(1), 70);
}

function clip(s: string, max: number): string {
  if (s.length <= max) return s;
  const cut = s.slice(0, max);
  const sp = cut.lastIndexOf(' ');
  return (sp > max * 0.5 ? cut.slice(0, sp) : cut).replace(/[,.;:\s-]+$/, '') + '…';
}

// Leading words that say nothing about the topic. Applied repeatedly, longest phrases first.
const OPENERS = [
  // greetings and politeness
  'привет', 'здравствуй(те)?', 'добрый (день|вечер|утро)', 'доброе утро', 'слушай(те)?', 'смотри', 'так', 'ну', 'итак',
  'пожалуйста', 'плиз', 'hi', 'hello', 'hey', 'please', 'ok', 'okay', 'so',
  // modal and request openers
  'а можно ли', 'можно ли', 'можно', 'можешь ли', 'можешь', 'можете', 'могу ли я', 'могу ли', 'не мог(ла)? бы ты', 'не мог(ла)? бы',
  'не могли бы вы', 'не могли бы', 'ты можешь', 'вы можете', 'подскажи(те)?', 'скажи(те)?', 'расскажи(те)?', 'объясни(те)?',
  'помоги(те)?', 'я хочу', 'хочу', 'я хотел(а)? бы', 'хотел(а)? бы', 'мне нужно', 'мне надо', 'нужно', 'надо', 'нам нужно',
  'давай(те)?', 'сделай(те)?', 'посмотри(те)?', 'глянь(те)?', 'проверь(те)?', 'как бы', 'как мне', 'как можно', 'как',
  'что делать если', 'есть ли', 'есть', 'ты', 'вы', 'помочь',
  'can you', 'could you', 'would you', 'will you', 'can i', 'could i', 'is it possible to', 'i want to', 'i would like to',
  "i'd like to", 'i need to', 'help me', 'how do i', 'how can i', 'how to', 'how', 'let\'s', 'tell me', 'explain',
  'what is', "what's", 'is there',
];
const OPENER_RE = new RegExp(`^(?:${OPENERS.join('|')})(?:\\s+ли)?(?=[\\s,!.:?]|$)[\\s,!.:?-]*`, 'i');
const PREP_RE = /^(?:на|в|во|про|о|об|для|за|по|с|со|at|on|into|for|about|to|the|a|an|мне|нам|ещё|еще)\s+/i;

// Interim title: first meaningful sentence of the message without the question openers.
export function heuristicTitle(message: string): string {
  const flat = message.replace(/```[\s\S]*?```/g, ' ').replace(/https?:\/\/\S+/g, (u) => u.replace(/^https?:\/\/(www\.)?/, '').split('/')[0]);
  const sentences = flat.split(/(?<=[.!?\n])\s+|\n+/).map((s) => s.replace(/\s+/g, ' ').trim()).filter(Boolean);
  for (const s of sentences) {
    let t = s;
    for (let i = 0; i < 6; i++) {
      const next = t.replace(OPENER_RE, '');
      if (next === t) break;
      t = next;
    }
    if (t !== s) t = t.replace(PREP_RE, ''); // "посмотри на сайт" → "сайт", but "На сайте…" stays
    t = t.replace(/\s*\([^)]*\)$/, '').replace(/[?!.…:;,\s]+$/, '').trim();
    if (t.length >= 3) return clip(t[0].toUpperCase() + t.slice(1), 60);
  }
  return clip(flat.replace(/\s+/g, ' ').trim(), 60) || 'Chat';
}
