---
name: aiwb-dossier
description: Dossier of an AI Workbench environment in the CO database — facts about the user (me), current affairs (state) and people around them (people), kept by the protocol secretary; list, full-text search, the session block, add/edit/close/delete rows, bulk import, contradiction check by a model (newer row wins). Use when you need a person's name, relation, dates or the user's current situation, or when a dossier fact is wrong or outdated.
version: 0.2.2
entry: aiwb-dossier.py
requires: [keyring]
platform: windows
secrets: [aiwb/co-push]
---

# aiwb-dossier

Досье окружения (схема 19, CO Web 0.85.0): что агенты знают о пользователе. Разделы: `me` — факты о человеке, `state` — текущие дела, `people` — люди вокруг, одна строка на один факт, `subject` — имя. Строки добавляет и обновляет секретарь протокола сам, без проверки (решение владельца 07.11). Человек правит их в CO Web: Inspector проекта → Dossier. Новые сессии получают блок досье в уровне 4. В общих проектах и чатах в блок идут только `people`.

## Вызов

```
python aiwb-dossier.py [--env ENV] list [--all] [--section me|people|state]
python aiwb-dossier.py search WORDS... [--limit 30]
python aiwb-dossier.py context [--people-only]          # блок, который получают новые сессии
python aiwb-dossier.py add SECTION SUBJECT TEXT [--chat CHAT-… --msg N --date YYYY-MM-DD]
python aiwb-dossier.py edit d123 [--text …] [--subject …] [--section …]
python aiwb-dossier.py close d123 | delete d123 | restore d123
python aiwb-dossier.py import rows.json [--model haiku]  # [{section, subject, text, chatId?, msg?, date?}], пачками по 400
python aiwb-dossier.py check [--model sonnet] [--apply]  # противоречия между строками; --apply правит/удаляет старые
# --json — сырой ответ сервера
```

## Примеры

```
python aiwb-dossier.py search Мария
python aiwb-dossier.py add people "Иван Иванов" "брат, живёт в Риге" --chat CHAT-20261107201900-T0NA --msg 5
python aiwb-dossier.py edit d42 --text "С 01.10.2026 работает в новой компании"
```

## check

Все активные строки уходят одним запросом в `claude -p` (без инструментов и MCP, ~1 мин на 230 строк). Модель ищет противоречия: разный возраст или дата рождения, перепутанные имена, разные вуз/город/работа на один момент, старый план как текущий факт. Правило: при конфликте права более свежая строка (по дате), особенно если её подтверждают несколько строк; строки `import` из старых чатов часто устарели. Для каждого конфликта — исправление старой строки (`edit` с новым текстом или `delete`) или `ask` с вопросом владельцу, если по строкам не понять. Без `--apply` только печатает; с `--apply` выполняет `edit`/`delete`, `ask` никогда не применяется. `--json` — `{rows, conflicts:[{ids, person, problem, fix:{id, action, text}, question, applied}], applied}`.

Повод: 10.11.2026 строка d128 из импорта claude.ai (19.10) перепутала двух членов семьи, и агент приписал одной из них чужой год учёбы. Регулярно check запускает расписание «Сторож досье» (`dossier-watch`, проект AI-Workbench).

## Настройки

- Адрес сайта: `AIWB_DB_URL` (полный адрес `db.php`), иначе `site.url` из install.json + `/agent/db.php`.
- Окружение по умолчанию: `--env`, иначе `AIWB_ENV`, иначе `AIWB_OWNER_ENV`, иначе `env` участника с `role: "owner"` в `members` install.json.
- install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json` (Windows), иначе `~/.config/aiwb/install.json`.

## Ограничения

- Ключ домашнего ПК — keyring `aiwb/co-push` (account `default`); на сайте не работает. Запросы идут в gzip (WAF хостинга).
- Пароли, токены, ключи, PIN, номера карт и счетов сервер не принимает: строка пропускается.
- `edit` правит строку на месте. Обновление от секретаря создаёт новую строку, а старая получает статус `replaced`.
- Номер `d<id>` виден в блоке досье и в подсказках секретаря.

## История

- 0.2.2 — 2026-11-13 — тексты без имён членов семьи (экспорт Freedom); поведение не изменилось.
- 0.2.1 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (адрес сайта — `site.url`, окружение по умолчанию — владелец из `members`).
- 0.2.0 — 2026-11-10 — `check`: противоречия между строками через `claude -p`, свежее побеждает, `--apply`.
- 0.1.0 — 2026-11-07 — первая версия (схема 19, db.php op `dossier_host`).
