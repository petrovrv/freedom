---
name: pdf-extract
description: Extract text, tables (Markdown/CSV/JSON) and search hits from PDFs with PyMuPDF; detects scanned pages. Use for specs, datasheets, reports.
version: 0.1.1
entry: pdf-extract.py
requires: [pymupdf]
secrets: []
---

# pdf-extract

Текст, таблицы и поиск в PDF (PyMuPDF), с номерами страниц.

## Вызов

```
python pdf-extract.py info FILE [--tables]
python pdf-extract.py text FILE [--pages 1-3,5] [--layout]
python pdf-extract.py tables FILE [--pages SPEC] [--format md|csv|json] [--out DIR] [--strategy lines|text]
python pdf-extract.py grep FILE PATTERN [--regex] [--context 80] [--pages SPEC]
```

## Ограничения

- Сканы без текстового слоя (`likely_scanned_pages` в `info`) не распознаются — OCR нет.
- Таблицы без линий: `--strategy text`; результат проверять глазами.

## История

- 0.1.1 — 2026-11-12 — Freedom: история и примеры без личных и корпоративных имён; поведение не изменилось.
- 0.1.0 — 2026-10-16 — первая версия (код — корпоративная модель claude-opus-5.5).
