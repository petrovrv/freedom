---
name: aiwb-chat
description: Read messages of an AI Workbench chat from the CO database — one message by its #number (with neighbours), word search inside the chat, the latest messages — find which chats of the environment talked about something (messages + protocol + vectors + aliases, any language), and post a message into the family chat as AI Workbench (instructions for family members). Use when you work from the chat protocol and need the exact wording of an earlier message, need an earlier chat on the same topic, or must tell the family something.
version: 0.3.1
entry: aiwb-chat.py
requires: [keyring]
platform: windows
secrets: [aiwb/co-push]
---

# aiwb-chat

Читает сообщения чата AI Workbench из базы CO (`db.php` op `chat_read`, ключ домашнего ПК). Нужен агентам, которые получили протокол чата вместо полной истории: в протоколе у записей есть номера сообщений `#N`, этим инструментом агент открывает исходное сообщение.

## Вызов

```
python aiwb-chat.py get CHAT SEQ [--around N]      # сообщение #SEQ (N соседей с каждой стороны, до 5)
python aiwb-chat.py search CHAT WORDS... [--limit N]  # поиск по словам внутри чата (до 20 результатов, отрывки)
python aiwb-chat.py list CHAT [--limit N]          # последние сообщения (до 50, первые 120 символов)
python aiwb-chat.py find WORDS... [--chat CHAT | --env ENV] [--self] [--limit N]  # в каких чатах окружения об этом говорили
python aiwb-chat.py family [--env ENV]             # семейные чаты окружения (по умолчанию — окружение владельца)
python aiwb-chat.py post (--text T | --file F|-) [--chat CHAT] [--env ENV] [--reply-to EVT]  # сообщение в семейный чат от AI Workbench
# --json — сырой ответ сервера
```

`CHAT` — id чата (`CHAT-20261017132550-3D8A`); его называет блок `[5. Memory]` в начале сессии.

`find` (0.2.0, схема 20, `db.php` op `find_chats`): ищет по всем чатам окружения (свои и открытые ему) — сообщения, записи протокола, векторы протокола (вопрос можно задать своими словами и по-английски) и псевдонимы (`aiwb-assoc`). Окружение берётся из чата `--chat` (по умолчанию `$AIWB_CHAT_ID`, сам он в выдачу не попадает без `--self`), иначе `--env` (по умолчанию — окружение владельца). Вывод: чат, название, проект, дата, в каких списках найден, отрывки `#N: …` и `#N (protocol): …` — номер сообщения для `get`.

`post` (0.3.0, `db.php` op `family_post`): пишет в семейный чат (вкладка «👪 Family» правой панели CO Web; комната — чат с флагом family в скрытом проекте Family) сообщение от имени **AI Workbench** — пузырь агента, push всем участникам семьи. Без `--chat` берётся единственный семейный чат окружения `--env`; если их несколько, сервер перечисляет id. Текст — Markdown, можно в несколько строк (до 20 000 символов); `--file -` читает stdin. В событии запоминается чат-источник (`postedFrom` = `$AIWB_CHAT_ID`). `family` показывает id, название, проект и владельца.

Когда писать: инструкции для членов семьи (Маки, CO, установка) и то, что владелец поручил сказать семье. Никогда от имени владельца или детей, без подписи владельца; не дублировать одно сообщение; личное и корпоративное не выносить. В ответе своего чата написать, что и в какой чат отправлено.

## Примеры

```
python aiwb-chat.py get CHAT-20261017132550-3D8A 8
python aiwb-chat.py search CHAT-20261017132550-3D8A пик контекста
python aiwb-chat.py find почему ночная копия базы растёт
python aiwb-chat.py find "why couldn't ONLYOFFICE be reached from outside" --env owner
python aiwb-chat.py family
python aiwb-chat.py post --file tmp/instr.md
```

## Настройки

- Адрес сайта: `AIWB_DB_URL` (полный адрес `db.php`), иначе `site.url` из install.json + `/agent/db.php`.
- Окружение по умолчанию (`find` без чата, `family`, `post`): `AIWB_OWNER_ENV`, иначе `env` участника с `role: "owner"` в `members` install.json.
- install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json` (Windows), иначе `~/.config/aiwb/install.json`.

## Ограничения

- Только сообщения пользователя и итоговые ответы агентов; вывода инструментов в базе нет.
- Ключ — keyring `aiwb/co-push` (account `default`), есть только на домашнем ПК; на сайте инструмент не работает.
- Текст одного сообщения обрезается на 60 000 символах.
- В песочнице Codex без сети (`CODEX_SANDBOX_NETWORK_DISABLED=1`) соединение блокирует песочница, а не сервер: инструмент сразу пишет подсказку — вызвать через MCP `tool_run aiwb-chat` или с повышением прав. Bridge 1.5.3+ открывает сеть песочнице Codex, если у чата разрешён веб.

## История

- 0.3.1 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (адрес сайта — `site.url`, окружение по умолчанию — владелец из `members`).

- 0.3.0 — 2026-11-09 — `family` и `post`: агент сам пишет в семейный чат от имени AI Workbench (решение владельца 09.11).

- 0.2.0 — 2026-11-09 — `find`: поиск чатов окружения (сообщения + протокол + векторы + псевдонимы, схема 20).

- 0.1.1 — 2026-10-24 — понятная ошибка, когда сеть закрыта песочницей Codex (раньше `WinError 10061` выглядел как отказ базы CO).
- 0.1.0 — 2026-10-18 — первая версия (живой протокол чата, CO Web 0.36.0). Код — корпоративная модель.
