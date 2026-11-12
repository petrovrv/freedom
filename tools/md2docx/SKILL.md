---
name: md2docx
description: Convert simple Markdown (headings, lists, pipe tables, bold, URLs) into a clean Word .docx via python-docx.
version: 0.1.0
entry: md2docx.py
requires: [python-docx]
secrets: []
---

# md2docx

Превращает простой Markdown в аккуратный Word-документ: заголовки, абзацы, маркированные и нумерованные списки, таблицы с затенённой шапкой, **жирный**, кликабельные ссылки. Подходит для отчётов и досье, которые надо отдать людям в .docx.

## Вызов

```
python md2docx.py input.md output.docx [--font Calibri] [--size 10.5]
```

## Примеры

```
python md2docx.py dossier.md "G:\My Drive\...\Dossier.docx"
```

## Ограничения

- Нет курсива, вложенных таблиц, картинок и блоков кода.
- Строки абзаца, идущие подряд, склеиваются через перенос строки.
- Вложенные маркированные списки — только второй уровень (отступ ≥ 2 пробела).

## История

- 0.1.0 — 2026-10-15 — первая версия (досье Koolstraat).
