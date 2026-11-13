---
name: claude-ai-sync
description: One-way sync of claude.ai chats/projects and local Cowork sessions (Claude Desktop) into AI Workbench — fresh chats in full (messages in the CO database, artifacts/files into scratch), old ones as pointer indexes; claude.ai projects become AIWB projects or folder-chats with subchats per a plan. Use to migrate or nightly-sync claude.ai work, or to pull an old claude.ai chat in full.
version: 0.3.2
entry: claude-ai-sync.py
requires: [curl_cffi, keyring, requests, websocket-client]
secrets: [aiwb/claude-ai (sessionKey), aiwb/co-push (default)]
---

# claude-ai-sync

Односторонняя синхронизация claude.ai → AI Workbench. claude.ai отдаёт API только браузерному отпечатку, поэтому запросы идут через `curl_cffi` (impersonate chrome) с cookie `sessionKey` из keyring `aiwb/claude-ai` / `sessionKey`.

С 0.3.0 ключ берётся сам из браузера CO (Edge CO Desktop, вход в claude.ai): `check` и `sync` читают `sessionKey` по CDP (`browser-agent/co_cookies.py`) и, если он отличается от keyring и работает, сохраняют его. Значение не печатается. Если браузер CO закрыт или не залогинен, остаётся ключ из keyring; ручной ввод — `secret-prompt --service aiwb/claude-ai --account sessionKey`.

Сайт и окружение владельца берутся из `install.json` (`site.url`, член с `role: owner`) или из `AIWB_SITE_URL` / `AIWB_OWNER_ENV`; без них — первая установка. Инструмент входит в модуль Freedom `chat-import` (`modules/chat-import`).

## Вызов

```
python claude-ai-sync.py fetch [--force] [--limit N]   # проекты (+файлы знаний) и чаты claude.ai в кеш, инкрементально по updated_at
python claude-ai-sync.py cowork                          # сессии Cowork этого ПК (%APPDATA%\Claude\local-agent-mode-sessions\*\*\local_*, audit.jsonl) в кеш
python claude-ai-sync.py index [--tsv]                   # сводка кеша (дата последнего сообщения, число сообщений, начало)
python claude-ai-sync.py import [--only id,..] [--dry-run] [--no-files] [--plan p.json]
python claude-ai-sync.py pull <chat-uuid|local_...> ...   # перенести старый чат целиком (ставит full в плане)
python claude-ai-sync.py sync                            # check + fetch + cowork + import — для расписания
python claude-ai-sync.py check [--no-browser]            # взять новый ключ из браузера CO, проверить; код 3 + push, если не жив
python claude-ai-sync.py refresh-cookie                  # sessionKey из браузера CO → keyring + проверка
```

Кеш: `%LOCALAPPDATA%\AIWorkbench\claude-ai\` — `conv/<uuid>.json` (полные разговоры), `projects/`, `cowork/`, `stats.json`, `plan.json` (раскладка), `state.json` (что куда перенесено: chatId, номера сообщений).

## План (`plan.json`)

- `fresh_after` (дата) и `min_messages`: чат с последним сообщением не раньше даты переносится целиком, остальные — указателями.
- `targets`: новые проекты AIWB (`title`, `description`, `instructions` → `context/project.md`).
- `groups`: uuid проекта claude.ai → `{target, hub}`. `hub: true` — проект становится чатом-папкой «📁 <имя>» в target, его чаты — подчатами (`parent.kind = subtask`, `seq 0`); `hub: false` — чаты лежат прямо в проекте.
- `chats`: переопределение для отдельного чата или сессии Cowork: `{target, hub, full}`; `hub` может быть своим ключом из `hubs` (`{title}`).
- `skip`, `default_target` (по умолчанию `Without-a-project`).

## Что пишется в AI Workbench

- `projects/<id>/project.json`, `chats/<chatId>/chat.json` (поле `claudeAi`: id, kind, url, syncedUtc) — на Drive; те же записи и сообщения — в базу (`db.php put`, ключ `aiwb/co-push`). Сообщения — обычные события (`imported.source = claude`), поэтому работают поиск, сводки, протокол, и чат можно продолжать в CO.
- Id событий детерминированы (`EVT-cai-<uuid сообщения>`), повторный запуск ничего не дублирует; новые сообщения из claude.ai дописываются после последнего номера в базе. Название, архив и перенос чата в CO сохраняются.
- `scratch/artifacts/` — последняя версия каждого артефакта; `scratch/outputs/`, `scratch/uploads/` — файлы чата через `wiggle/download-file` (что ещё живо на claude.ai, ≤50 МБ); `scratch/imported/` — текст вложений; для Cowork — `outputs/` сессии (≤50 МБ на файл, ≤300 МБ на сессию).
- Указатели: `context/claude-ai-index.md` в каждом проекте, тот же список — первым сообщением в чате-папке или в чате «📇 Архив claude.ai». Файлы знаний: `files/claude-ai/` (плоский проект) или `scratch/knowledge/` чата-папки.
- `imports/index.json` — ключи `claude:<uuid>`, чтобы импорт из выгрузки в CO Web не задвоил чаты.

## Раскладка новых чатов (ежедневно)

```
python claude-ai-sync.py sync --stage fetch      # только выкачка
python claude-ai-sync.py pending                 # JSON: новые свежие чаты без решения в плане + новые проекты-источники
python claude-ai-sync.py projects                # проекты AI Workbench (id, название, описание)
python claude-ai-sync.py assign <id> [<id> …] --target <ProjectId> [--hub KEY --hub-title T]
python claude-ai-sync.py assign <id> --target <NewId> --title "Название" --description "…"   # новый проект создаётся при импорте
python claude-ai-sync.py assign <source-project-id> --project --target <ProjectId> [--hub-folder]
python claude-ai-sync.py sync --stage import     # перенос по плану
```

Решения пишутся в `plan.json` (`chats`, `groups`, `targets`). Чат, уже перенесённый раньше, не двигается. Расписание «Синхронизация чатов Claude + ChatGPT» (`projects/Claude_Chats/schedules.json`, `chats-sync`, 04:30) делает fetch → pending → assign (агент выбирает проект по названию и первым словам или создаёт новый) → import для обоих инструментов. Общий код раскладки — в `claude-ai-sync` (`pending`, `plan_assign`, `aiwb_projects`).

## Ограничения

- Только текущая ветка разговора; thinking не переносится, вызовы инструментов — одной строкой (черновики `message_compose` — полностью).
- Старые файлы claude.ai со временем пропадают — `outputs/` старых чатов могут не скачаться.
- Перенос чата между проектами по изменённому плану касается только ещё не перенесённых чатов.
- Cowork — только с этого ПК.

## История

- 0.3.2 — 2026-11-13 — без личных значений по умолчанию: сайт и окружение владельца только из AIWB_SITE_URL/AIWB_OWNER_ENV или install.json (site.url, members role owner), иначе нейтральные заглушки (Freedom export).
- 0.3.0 — 2026-11-05 — `refresh-cookie`, автообновление sessionKey из браузера CO в `check`/`sync` (sync проверяет ключ на любой стадии). Код: Claude.
- 0.2.0 — 2026-11-01 — раскладка новых чатов: `projects`, `pending`, `assign` (в т.ч. новый проект), `sync --stage fetch|import`; общий код для chatgpt-sync. Ежедневное расписание chats-sync.
- 0.1.0 — 2026-10-28 — первая версия: fetch, cowork, import по плану, pull, sync, check.
- 0.1.1 — 2026-10-28 — outputs Cowork: пропуск node_modules/.git, ошибка одного файла не роняет чат.
