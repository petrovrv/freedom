---
name: xlsx-tool
description: Inspect, read, search and edit Excel .xlsx/.xlsm from the command line (sheets info, ranges as md/csv/json, find, set cells/formulas keeping styles, export CSV). Use instead of ad-hoc openpyxl code.
version: 0.2.1
entry: xlsx-tool.py
requires: [openpyxl]
secrets: []
---

# xlsx-tool

Чтение и правка Excel из командной строки с выводом, удобным агенту; форматирование и макросы (.xlsm) сохраняются.

## Вызов

```
python xlsx-tool.py info FILE
python xlsx-tool.py read FILE [--sheet S] [--range A1:D20] [--formulas] [--format csv|json|md] [--header] [--max-rows 200]
python xlsx-tool.py find FILE TEXT [--sheet S] [--regex]
python xlsx-tool.py set FILE CELL VALUE [--sheet S] [--out OUT]
python xlsx-tool.py set-many FILE edits.json [--out OUT]      # [{"sheet","cell","value"}]
python xlsx-tool.py to-csv FILE OUTDIR
```

VALUE: `=...` — формула, число, `YYYY-MM-DD` — дата, `true/false`, иначе текст; `'...` — текст как есть без апострофа (например `'2026-11-09` остаётся строкой). Без `--out` файл правится на месте, копия — `FILE.bak`.

## Ограничения

- Формулы не пересчитываются (openpyxl): после `set` значения формул обновит Excel. Если у формул нет сохранённых значений, инструмент предупреждает.
- Диаграммы и сводные таблицы openpyxl может потерять при сохранении — для таких книг писать в `--out`.

## История

- 0.2.1 — 2026-11-12 — Freedom: история и примеры без личных и корпоративных имён; поведение не изменилось.
- 0.2.0 — 2026-11-09 — значение с ведущим апострофом пишется как текст (даты/числа строкой). Код: Claude.
- 0.1.0 — 2026-10-16 — первая версия (код — корпоративная модель claude-opus-5.5).
