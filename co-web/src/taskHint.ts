// SPDX-License-Identifier: AGPL-3.0-or-later
// Task-type hint: keyword heuristic that suggests an agent next to the picker (never switches it).
import hintData from './taskHintData';
import type { AgentId } from './config';

export type TaskCategory = 'code' | 'docx' | 'xlsx' | 'translate' | 'edit' | 'summary' | 'reasoning' | 'web';

export const CATEGORY_LABEL: Record<TaskCategory, string> = {
  code: 'Code',
  docx: 'Word documents',
  xlsx: 'Excel',
  translate: 'Translation',
  edit: 'Editing',
  summary: 'Summary',
  reasoning: 'Reasoning and calculation',
  web: 'Web search',
};

// Lowercase keywords: strings are substring matches, regexes guard short or ambiguous terms.
export const KEYWORDS: Record<TaskCategory, (string | RegExp)[]> = {
  code: [
    'python', 'javascript', 'typescript', 'script', 'function', 'bug', 'stack trace', 'traceback', 'refactor',
    'compile', 'regex', 'sql', /\bapi\b/, /\bcode\b/, 'код', 'скрипт', 'функци', 'fonction', /```/,
    /\.(py|ts|js|cs|php)\b/,
  ],
  docx: [
    'docx', /\bword\b/, 'document', 'comments', 'redline', 'tracked changes', 'документ', 'ворд',
    'замечани', 'рецензи', /правк\S* в документ/, 'commentaires',
  ],
  xlsx: [
    'xlsx', /\bexcel\b/, 'spreadsheet', 'sheet', 'formula', 'pivot', 'csv', 'таблиц', 'эксель',
    /формул(?!ир)/, 'столбц', 'tableur', 'feuille', 'vlookup',
  ],
  translate: [
    'translat', 'into english', 'into french', 'en français', 'en anglais', 'переведи', 'перевод',
    'на английский', 'на французский', 'traduire', 'traduis', 'traduction',
  ],
  edit: [
    'rewrite', 'proofread', 'polish', 'edit the text', 'shorten', 'improve the text', 'перепиши',
    'отредактируй', 'редактур', 'сократи', 'улучши текст', 'исправь стиль', 'reformule', 'corrige le texte',
  ],
  summary: [
    'summar', 'tl;dr', 'key points', 'minutes', 'резюме', 'кратко', 'суммаризир', 'выжимк',
    'итоги встречи', 'протокол встречи', 'résume', 'synthèse', 'compte rendu',
  ],
  reasoning: [
    'calculat', /\bcompute\b/, 'estimate', 'efficiency', 'heat rate', 'pressure drop', 'solve', /\bprove\b/,
    'how many', 'рассчитай', 'посчитай', 'расчёт', 'расчет', 'вычисли', 'оцени', 'кпд', /(^|\s)реши/, 'calcule',
  ],
  web: [
    'search the web', 'google', 'latest', 'news', 'current price', 'today', 'find online', 'website', /\burl\b/,
    'найди в интернете', 'поищи', 'последн', 'новост', 'актуальн', 'cherche', 'actualité',
  ],
};

// Tie-break order: earlier wins on equal score.
export const PRIORITY: readonly TaskCategory[] = [
  'code', 'xlsx', 'docx', 'translate', 'summary', 'edit', 'web', 'reasoning',
];

const hit = (t: string, k: string | RegExp): boolean => (typeof k === 'string' ? t.includes(k) : k.test(t));

export function classifyTask(text: string): TaskCategory | null {
  const t = text.toLowerCase();
  if (t.trim().length < 12) return null;
  let best: TaskCategory | null = null;
  let bestScore = 0;
  for (const cat of PRIORITY) {
    const score = KEYWORDS[cat].reduce((n, k) => n + (hit(t, k) ? 1 : 0), 0);
    if (score > bestScore) {
      best = cat;
      bestScore = score;
    }
  }
  return best;
}

// agent: claude | codex or an agent of the corporate connector (corporate.ts); the data names them as the bench did.
export interface Pick { agent: AgentId; model: string | null; label: string }

// Generated from aiwb-modelbench recommendations.json (tool library); refresh by re-running the bench and
// copying its taskHintData.ts over this folder's copy.
export const BENCH_DATE: string = hintData.benchDate;

export const RECOMMENDATIONS = hintData.categories as Partial<Record<TaskCategory, { primary: Pick; fallback: Pick | null }>>;

// Hint only: returns a suggestion, never switches the agent.
export function recommendFor(
  text: string,
  isAvailable: (p: Pick) => boolean,
): { category: TaskCategory; pick: Pick; isFallback: boolean } | null {
  const category = classifyTask(text);
  if (category === null) return null;
  const rec = RECOMMENDATIONS[category];
  if (!rec || !rec.primary) return null; // category not covered by the bench
  // A pick is usable only if this host offers its agent and model (e.g. claude-sonnet-5.5 may be missing).
  if (isAvailable(rec.primary)) return { category, pick: rec.primary, isFallback: false };
  if (rec.fallback && isAvailable(rec.fallback)) return { category, pick: rec.fallback, isFallback: true };
  return null;
}

// Returns failure messages; empty array means all checks passed.
export function taskHintSelfTest(): string[] {
  const cases: Array<[string, TaskCategory | null]> = [
    ['Fix this bug in my python function', 'code'],
    ['Add comments to the attached .docx document', 'docx'],
    ['Build a pivot table in Excel from this csv', 'xlsx'],
    ['Переведи этот текст на английский', 'translate'],
    ['Proofread and polish this paragraph', 'edit'],
    ['Fais une synthèse de ce rapport', 'summary'],
    ['Рассчитай КПД турбины при этих параметрах', 'reasoning'],
    ['Search the web for the latest news on hydrogen', 'web'],
  ];
  const failures: string[] = [];
  for (const [text, expected] of cases) {
    const got = classifyTask(text);
    if (got !== expected) failures.push(`"${text}": expected ${expected}, got ${got}`);
  }
  return failures;
}