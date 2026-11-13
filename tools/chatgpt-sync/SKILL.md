---
name: chatgpt-sync
description: One-way copy of chatgpt.com (personal account) into AI Workbench — projects, chats (not archived), generated images, uploads, canvas, memories and scheduled tasks; fresh chats in full, old ones as a pointer index; same plan format and CO writer as claude-ai-sync. Use to migrate or keep syncing ChatGPT chats.
version: 0.4.0
entry: chatgpt-sync.py
requires: [curl_cffi, keyring, requests, websocket-client]
platform: windows
secrets: [aiwb/chatgpt/session-token-0, aiwb/chatgpt/session-token-1, aiwb/co-push/default]
---

# chatgpt-sync

Односторонняя синхронизация chatgpt.com → AI Workbench. Внутренний API chatgpt.com (`/backend-api`, как у веб-клиента) через `curl_cffi` (имитация Chrome, иначе Cloudflare). Запись в CO (база, `project.json`/`chat.json` на Drive, ids чатов, проекты, `resolve` плана) — общая с `claude-ai-sync`, модуль подгружается из соседней папки.

## Доступ

Cookie `__Secure-next-auth.session-token` с chatgpt.com (F12 → Application → Cookies). Обычно разбита на `.0` и `.1`:

```
python secret-prompt.py --service aiwb/chatgpt --account session-token-0
python secret-prompt.py --service aiwb/chatgpt --account session-token-1
```

Неразбитая — в account `session-token`. Длинные значения `secret-prompt` хранит кусками (`aiwb_secret.py`). Cookie живёт ~3 месяца.

С 0.4.0 cookie берётся сама из браузера CO (Edge CO Desktop, оранжевое окно), если в нём выполнен вход в chatgpt.com: `refresh-cookie` читает её по CDP (`browser-agent/co_cookies.py`, без вкладок) и кладёт в keyring, значение не печатается. `check` (и `sync`) делает это сам, когда cookie протухла или до конца меньше 14 дней. Если браузер CO закрыт или не залогинен — exit 3 и push; тогда ввод через `secret-prompt` выше. `check --no-browser` — только проверка.

## Вызов

```
python chatgpt-sync.py check [--no-browser]
python chatgpt-sync.py refresh-cookie   # cookie из браузера CO → keyring + проверка
python chatgpt-sync.py fetch [--since YYYY-MM-DD] [--no-list] [--workers 2] [--delay 0.3] [--retry-failed] [--force] [--limit N]
python chatgpt-sync.py export <zip|folder>   # официальный экспорт ChatGPT (если OpenAI его отдаёт)
python chatgpt-sync.py index [--tsv]
python chatgpt-sync.py import [--plan plan.json] [--only id,g-p-…] [--dry-run] [--no-files] [--force]
python chatgpt-sync.py pull <conversation-id> …
python chatgpt-sync.py sync            # check + fetch свежих (новее `fresh_after` плана, 1 поток) + import (для расписания)
```

Кэш: `%LOCALAPPDATA%\AIWorkbench\chatgpt\` — `chats.json`, `projects.json`, `conv/<id>.json`, `fetched.json`, `failed.json`, `stats.json`, `memories.json`, `automations.json`, `plan.json`, `state.json`.

## План

Формат как у `claude-ai-sync`: `fresh_after`, `min_messages`, `default_target`, `targets` (новые проекты CO), `groups` (проект ChatGPT `g-p-…` → `{target, hub}`; `hub: true` — проект становится чатом-папкой «📁 …» с подчатами), `chats` (по id чата: `target`, `hub`, `full`), `hubs` (заголовки своих папок), `skip`. Генератор плана для переноса 28.10.2026 — `projects/GPT_Chats/chats/CHAT-20261028202141-FYP1/scratch/make_plan.py` (правила по названиям).

## Что переносится

- Текст текущей ветки чата; вызовы инструментов — короткие отметки `_[tool: …]_` (длинные серии схлопываются); ссылки-цитаты убираются, `entity` → имя, `url` → ссылка.
- Свежие чаты (`fresh_after`): картинки генерации и пользователя → `scratch/images/`, вложения → `scratch/uploads/`, canvas → `scratch/artifacts/`. Старые — строка в `context/chatgpt-index.md` проекта и в сообщении папки.
- Архивные и временные чаты — нет. Инструкции проектов ChatGPT — только в индексе.
- Чат, перенесённый раньше, не двигается (пользователь мог его переместить в CO); новые сообщения дописываются.

## Раскладка новых чатов (ежедневно)

```
python chatgpt-sync.py sync --stage fetch      # только выкачка
python chatgpt-sync.py pending                 # JSON: новые свежие чаты без решения в плане + новые проекты-источники
python chatgpt-sync.py projects                # проекты AI Workbench (id, название, описание)
python chatgpt-sync.py assign <id> [<id> …] --target <ProjectId> [--hub KEY --hub-title T]
python chatgpt-sync.py assign <id> --target <NewId> --title "Название" --description "…"   # новый проект создаётся при импорте
python chatgpt-sync.py assign <source-project-id> --project --target <ProjectId> [--hub-folder]
python chatgpt-sync.py sync --stage import     # перенос по плану
```

Решения пишутся в `plan.json` (`chats`, `groups`, `targets`). Чат, уже перенесённый раньше, не двигается. Расписание «Синхронизация чатов Claude + ChatGPT» (`projects/Claude_Chats/schedules.json`, `chats-sync`, 04:30) делает fetch → pending → assign (агент выбирает проект по названию и первым словам или создаёт новый) → import для обоих инструментов. Общий код раскладки — в `claude-ai-sync` (`pending`, `plan_assign`, `aiwb_projects`).

## Ограничения

- Очень длинные чаты chatgpt.com отдаёт с HTTP 500 (таймаут сервера) — две попытки, затем `failed.json`, чат идёт указателем; `fetch --retry-failed` пробует снова.
- Лимит chatgpt.com считается на аккаунт: несколько параллельных выкачек (или >1–2 потоков) быстро дают сплошные 429. На 429 — пауза по `retry-after`, попытки не тратятся. Надёжно: 1 поток, `--delay 3` (~4 чата/мин).
- `pull <id>` сначала докачивает старый чат, затем переносит целиком; строка в индексе обновится при следующем полном `import`/`sync`.
- Официальный экспорт (Settings → Data controls → Export) 10.2026 не приходил (баг OpenAI); запрос требует свежего входа и проходит Cloudflare — агенту недоступен.
- Память ChatGPT и расписания (`memories.json`, `automations.json`) только выкачиваются; раскладка в профиль/память проектов и `schedules.json` — вручную.

## История

- 0.4.0 — 2026-11-05 — `refresh-cookie` и автообновление cookie в `check`/`sync` из браузера CO (CDP Storage.getCookies), меньше 14 дней или протухла. Проверено на живом входе: 2 части, срок 2027-02-03. Код: Claude.
- 0.3.0 — 2026-11-01 — раскладка новых чатов: `projects`, `pending`, `assign`, `sync --stage fetch|import` (код из claude-ai-sync); ежедневное расписание chats-sync вместо chatgpt-sync.
- 0.2.0 — 2026-11-01 — `--since`, `export` (приём архива экспорта), 429 без расхода попыток, `failed.json`/`fetched.json`/`stats.json`, ленивое чтение кэша, `sync` качает только свежие в 1 поток, `pull` докачивает старый чат. Первый перенос: 604 чата целиком, 2630 указателей.
- 0.1.0 — 2026-10-28 — первая версия.
