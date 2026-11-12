---
name: aiwb-memory
description: Project memory of AI Workbench — add, list, search, get by number or remove dated facts/decisions in projects/<id>/context/memory.md, which CO Web gives to every new agent session of the project (CO Web 0.70.0+ may send only an index); asof shows the memory as of a date with the superseded entries of memory-archive.md. Use when a durable fact or decision of the project appears, or to read a full entry from the memory index.
version: 0.2.0
entry: aiwb-memory.py
requires: []
platform: windows
secrets: []
---

# aiwb-memory

Память проекта AI Workbench: файл `projects/<id>/context/memory.md` — короткие датированные записи «что решили / что важно знать». CO Web (0.26.0+) вставляет этот файл вместе с кратким содержанием 5 последних чатов проекта в начало каждой новой сессии агента; пользователь правит его на вкладке Project.

## Вызов

```
python aiwb-memory.py [--project <id>] add "<текст>" [--tag decision|fact|todo]
python aiwb-memory.py [--project <id>] list [--grep слово] [--last N] [-n]
python aiwb-memory.py [--project <id>] get <номер> [<номер> ...]
python aiwb-memory.py [--project <id>] search "<слова>" [--archive] [--limit 15]
python aiwb-memory.py [--project <id>] asof YYYY-MM-DD
python aiwb-memory.py [--project <id>] remove "<подстрока>" [--all]
python aiwb-memory.py [--project <id>] path | show
```

Без `--project` проект определяется по текущей папке (`...\projects\<id>\...`, например scratch чата).

- Номер записи — порядковый номер строки `- …` в файле; те же номера в индексе памяти, который CO Web 0.70.0+ даёт агенту вместо всего файла, если в memory.md есть строка `<!-- memory: index -->` (длинные записи там обрезаны и помечены `…`; полный текст — `get`).
- `search` — записи, где встречается больше слов запроса (подстроки без учёта регистра), сначала лучшие; `--archive` добавляет заменённые и удалённые записи из `context/memory-archive.md`.
- `memory-archive.md` пишет сервер, когда владелец принимает предложение ночной чистки (CO Web → Project → Memory → Revisions): `<запись> — superseded YYYY-MM-DD by: <новая запись>` или `<запись> — dropped YYYY-MM-DD: <причина>`. `asof` собирает из memory.md и архива записи, действовавшие на дату.

## Примеры

```
python aiwb-memory.py add "Сообщения чатов хранятся только в базе; на Drive только файлы" --tag decision
python aiwb-memory.py --project AI-Workbench list --last 10
python aiwb-memory.py --project AI-Workbench search "CO Desktop обновление" --archive
python aiwb-memory.py --project AI-Workbench get 12 40
```

## Ограничения

- Записывать только устойчивые факты и решения, одна строка на запись; не дублировать то, что уже в CONTEXT.md или в коде.
- Секреты не записывать никогда.
- Повтор того же текста пропускается («already present»).

## История

- 0.2.0 — 2026-11-01 — `get`, `search`, `asof`, `list -n`; пустая строка `- ` не считается записью.
- 0.1.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-16 — первая версия.
