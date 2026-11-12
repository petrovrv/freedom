---
name: docx-diff
description: Compare two revisions of a Word .docx (paragraphs and table rows, word-level inline diff, tracked changes accepted or not); output text, JSON, Markdown or a colored .docx report.
version: 0.1.1
entry: docx-diff.py
requires: [python-docx]
secrets: []
---

# docx-diff

Сравнение двух ревизий Word-документа: что изменено, добавлено, удалено — по абзацам и строкам таблиц, с подсветкой слов.

## Вызов

```
python docx-diff.py OLD.docx NEW.docx [--format text|json|md|docx] [--out report.docx]
       [--accept-changes] [--headers] [--ignore-case] [--ignore-whitespace] [--min-ratio 0.5]
```

`text`: `~ §N` изменён (`[-было-]{+стало+}`), `+` добавлен, `-` удалён; §N — номер блока в новой версии (для удалённых — в старой). `docx` — отчёт с красным зачёркнутым и синим подчёркнутым текстом.

## Ограничения

- Форматирование (жирный, стили, кроме заголовков) не сравнивается — только текст.
- Для правок в самом документе с отслеживанием изменений — инструмент `docx-ops`.

## История

- 0.1.1 — 2026-11-12 — Freedom: история и примеры без личных и корпоративных имён; поведение не изменилось.
- 0.1.0 — 2026-10-16 — первая версия (код — корпоративная модель claude-opus-5.5).
