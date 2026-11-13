// SPDX-License-Identifier: AGPL-3.0-or-later
// Task-type hint: the ui.hint page of the module task-hints (Freedom F10; was co-web/src/taskHint.ts of CO Web
// 0.43.0-0.102.0, same keywords and picks). A hidden page: CO Web sends it the composer text (permission composer.read),
// it guesses the type of the task by keywords and answers with the agents the model bench (aiwb-modelbench,
// ui/hint-data.js) found best for that type. CO Web shows the first pick its host offers and never switches by itself.
(function (g) {
  'use strict';
  var DATA = g.TASK_HINT_DATA || { benchDate: '', categories: {} };

  var CATEGORY_LABEL = {
    code: 'Code', docx: 'Word documents', xlsx: 'Excel', translate: 'Translation', edit: 'Editing', summary: 'Summary',
    reasoning: 'Reasoning and calculation', web: 'Web search',
  };

  // lowercase keywords: strings are substring matches, regexes guard short or ambiguous terms
  var KEYWORDS = {
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

  // tie-break order: earlier wins on equal score
  var PRIORITY = ['code', 'xlsx', 'docx', 'translate', 'summary', 'edit', 'web', 'reasoning'];

  function hit(t, k) { return typeof k === 'string' ? t.indexOf(k) >= 0 : k.test(t); }

  function classifyTask(text) {
    var t = String(text || '').toLowerCase();
    if (t.trim().length < 12) return null;
    var best = null, bestScore = 0;
    PRIORITY.forEach(function (cat) {
      var score = KEYWORDS[cat].reduce(function (n, k) { return n + (hit(t, k) ? 1 : 0); }, 0);
      if (score > bestScore) { best = cat; bestScore = score; }
    });
    return best;
  }

  /** The hint for a text: {text, title, picks} (best pick first) or null. */
  function hintFor(text) {
    var cat = classifyTask(text);
    if (!cat) return null;
    var rec = DATA.categories[cat];
    if (!rec || !rec.primary) return null; // category not covered by the bench
    var picks = [rec.primary].concat(rec.fallback ? [rec.fallback] : []).map(function (p) {
      return { agent: p.agent, model: p.model == null ? null : p.model, label: p.label };
    });
    return {
      text: 'Recommended for ' + CATEGORY_LABEL[cat].toLowerCase() + ':',
      title: 'By task type (keyword guess), from the model bench of ' + DATA.benchDate,
      picks: picks,
    };
  }

  g.taskHints = { classifyTask: classifyTask, hintFor: hintFor };

  if (g.coExt) {
    var last = '';
    g.coExt.on('composer', function (text) {
      var h = hintFor(text);
      var key = JSON.stringify(h);
      if (key === last) return;
      last = key;
      g.coExt.hint(h);
    });
  }
})(typeof window !== 'undefined' ? window : globalThis);
