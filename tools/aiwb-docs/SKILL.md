---
name: aiwb-docs
description: Full-text search over documents of all AI Workbench projects (docx, pdf, xlsx, pptx, md, txt, csv, html, eml) through the database index; also the indexer that keeps it current (Windows task AIWorkbench\DocsIndex, hourly). Use to find which project file mentions something.
version: 0.2.1
entry: aiwb-docs.py
requires: [keyring, python-docx, openpyxl, python-pptx, pymupdf]
platform: windows
secrets: [aiwb/co-push]
---

# aiwb-docs

Поиск по тексту документов всех проектов (`G:\My Drive\AI-Workbench\projects\**`) через индекс в базе (схема 7, таблица `docs`, FULLTEXT). Тот же индекс ищет CO Web.

## Вызов

```
python aiwb-docs.py search "<слова>" [--project <id>] [--env ENV] [--limit 10] [--json]
python aiwb-docs.py index [--project <id>] [--limit N] [--dry-run] [-v] [--log <файл>]
python aiwb-docs.py stats
```

- `search` — документы по релевантности: полный путь, дата изменения, 1–2 фрагмента с найденными словами. Поиск по словам (MariaDB natural language), без морфологии: пробуйте разные формы и языки.
- `index` — извлекает текст новых и изменённых файлов (по mtime и размеру) и отправляет в базу; удалённые с диска файлы убирает из индекса. Индексирует `scratch` чатов; пропускает `artifacts`, `tmp`, `events`, `.git`, `node_modules`, папки с точкой, файлы больше 60 МБ, временные `~$`. Повреждённые файлы индексируются только по имени.
- Задача Windows `\AIWorkbench\DocsIndex` на домашнем ПК запускает `index` каждый час (pythonw, лог `%LOCALAPPDATA%\AIWorkbenchBridge\logs\docs-index.log`).

## Примеры

```
python aiwb-docs.py search "договор аренды" --project house-renovation
python aiwb-docs.py search "actuator datasheet" --json --limit 5
```

## Ограничения

- Ключ домашнего ПК в keyring `aiwb/co-push` (account `default`); запросы идут на `db.php` сайта (`AIWB_DB_URL`, иначе `site.url` из install.json + `/agent/db.php`) в gzip+base64 (WAF хостинга). Папка проектов — `AIWB_ROOT`, иначе `drive.root`\`drive.workbench` из install.json, + `projects` (install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`).
- Индексируются только проекты, лежащие на этом Google Drive (окружение владельца); проекты других окружений — если их папки синхронизированы сюда.
- Текст файла — до 600 000 символов, кусками по ~3000.

## История

- 0.2.1 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (сайт — `site.url`, папка проектов — `drive.root`/`drive.workbench`).
- 0.2.0 — 2026-10-28 — индексирует `scratch` чатов (живые файлы чата, одна копия, CO Web 0.55 / Bridge 1.6) и пропускает `artifacts/` (старые копии по заданиям, раньше давали 52 из 64 МБ повторов в `docs`), `tmp/`, `temp/` и папки с точкой (`.versions`, `.co-attachments`).
- 0.1.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-16 — первая версия.
