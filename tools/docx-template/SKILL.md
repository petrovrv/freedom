---
name: docx-template
description: Fill Word .docx templates with Jinja2 tags (docxtpl) from JSON: list variables, make a sample JSON, render with rich text, images, table rows, date/number filters. Use for reports and letters from company templates.
version: 0.1.1
entry: docx-template.py
requires: [docxtpl, python-docx, jinja2]
secrets: []
---

# docx-template

Заполнение Word-шаблонов с тегами Jinja2 ({{ var }}, {%tr for %} для строк таблиц, {%p if %}) данными из JSON — отчёты и письма по шаблонам компании.

## Вызов

```
python docx-template.py vars TEMPLATE.docx [--json]
python docx-template.py make-sample TEMPLATE.docx --out sample.json
python docx-template.py render TEMPLATE.docx data.json --out result.docx [--strict]
```

Особые значения в JSON: `{"$richtext": "текст", "bold": true, "color": "C00000", "size": 12}` (в шаблоне — тег `{{r var }}`, обычный `{{ var }}` выведет пусто), `{"$image": "logo.png", "width_mm": 80}` (путь относительно data.json), `{"$listing": "строка 1
строка 2"}`. Фильтры: `{{ d|date }}` → 17.10.2026, `{{ x|num }}` → 1 234 567,50.

## Ограничения

- Теги должны стоять в шаблоне одним куском текста (Word иногда дробит их на несколько runs — перепечатать тег целиком).
- Корпоративных шаблонов с тегами пока нет: их нужно подготовить из существующих документов.

## История

- 0.1.1 — 2026-11-12 — Freedom: история и примеры без личных и корпоративных имён; поведение не изменилось.
- 0.1.0 — 2026-10-17 — первая версия (код — корпоративная модель claude-opus-5.5).
