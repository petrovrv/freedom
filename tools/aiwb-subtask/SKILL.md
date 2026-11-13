---
name: aiwb-subtask
description: Nested sessions for AI Workbench agents — start subtasks that run in parallel as child chats (own agent/model/host, visible in CO Web with status icons), then wait and collect their answers into your reply; list the model resources (own subscriptions, the site, family models allowed as slaves, corporate) with their capability cards and call any of them by --resource, on the home PC and on the site. Use when a task splits into independent parts (research several sources, per-file work, compare options) or another model suits a part better.
version: 0.7.0
entry: aiwb-subtask.py
requires: [keyring (home PC), requests (home PC), paramiko (home PC)]
secrets: [aiwb/co-push]
---

# aiwb-subtask

Вложенные сессии: агент в чате AI Workbench запускает подзадачи, каждая — отдельный дочерний чат «⤷ …» в том же проекте со своей сессией агента в очереди заданий. Подзадачи идут параллельно, видны в CO Web (значок статуса, ссылка на родителя), а родительский агент ждёт их ответы и сводит в свой.

## Вызов

```
python aiwb-subtask.py models [--json] [--parent CHAT-ID | --env ENV]
python aiwb-subtask.py spawn --resource ID --prompt "<задача>" [--title T] [--model M] [--mode chat|cowork] [--parent CHAT-ID] [--timeout-minutes 120] [--device d-<16hex> ...] [--again]
python aiwb-subtask.py spawn --prompt "<самодостаточная задача>" [--title T] [--agent claude|codex|dispatcher] [--model sonnet] [--mode chat|cowork] [--host <id исполнителя>] [--parent CHAT-ID] [--top-level] [--timeout-minutes 120]
    [--device d-<16hex> ...] [--context-folder rootId:relativePath[:read|write] ...]
python aiwb-subtask.py wait JOB-ID [JOB-ID ...] [--timeout 1800]
python aiwb-subtask.py status JOB-ID [JOB-ID ...]
python aiwb-subtask.py post --chat CHAT-ID --prompt "<текст>" [--author "agent · <откуда>"] [--agent ...] [--model ...] [--host ...] [--timeout-minutes 120]
python aiwb-subtask.py hosts [--env ENV] [--json]
```

`post` — сообщение в уже существующий чат (например, передать идею в соседний чат проекта): событие user_message с указанным автором + задание, которое продолжает сессию агента этого чата (агент, режим, модель, устройства, права и папки контекста берутся из его последнего завершённого задания). Не ставит, если в чате есть незавершённое задание. Печатает `{chatId, jobId, resume}`; ответ — `wait JOB-ID`.

`--top-level` — обычный чат верхнего уровня в проекте родителя (без «⤷», без связи `parent`, автор сообщения `user`, текст без пометки подзадачи); удобно, чтобы завести рабочие чаты проекта (например, при передаче дел).

`spawn` печатает JSON `{chatId, jobId, title}`; родительский чат по умолчанию берётся из текущей папки (`…\chats\CHAT-…\scratch`). `wait` печатает ответы всех подзадач и выходит с 1, если хоть одна не `completed`.

## Ресурсы-модели и слейвы (0.6.0, S5)

`models` печатает ресурсы, которые может звать этот чат (окружение его проекта): свои (подписки хостов `home-main/claude`, `home-main/codex`, `site/claude`, корпоративный `home-main/dispatcher`, подписки, зарегистрированные устройствами) и чужие с флагом «slave allowed». У каждого — карточка (`summary`, `strengths`, `limits`, `dataStaysIn`), модели, хост, online/offline. Флаг и карточку задаёт владелец ресурса в CO Web: ☰ → Host and devices → Models.

`spawn --resource ID` — подзадача на этом ресурсе: сервер (db.php `subtask_spawn`) сам создаёт дочерний чат на Drive и в базе и ставит задание на хост ресурса (агент и модель по умолчанию — из ресурса, `--model` — другая модель ресурса). Без лимитов и подтверждений. Повтор той же команды в том же задании (тот же `AIWB_JOB_ID`, промпт, ресурс, модель, заголовок, устройства) возвращает уже запущенную подзадачу (поле `note`), а не вторую: ключ `requestKey` клиент считает до отправки, сервер хранит сутки; таймаут, сетевой сбой и HTTP 5xx клиент сам повторяет с тем же ключом. Ещё один запуск того же нарочно — `--again`. Подзадача общего чата (и подзадачи подзадач на любой глубине) — без устройств и корпоративных ресурсов (`shared_chat_no_corporate`). Нельзя: чужой ресурс без флага (`slave_not_allowed`), конфиденциальный (корпоративный) чужого окружения (`confidential`), ресурс без очереди на хосте (`host_unknown`). Выбор — по карточке и матрице aiwb-modelbench; корпоративное (`dataStaysIn: corporate`) не отдавать чужим ресурсам.

Работает и на сервере (ведущий агент на Site): `models`, `spawn --resource`, `wait`, `status` ходят только в db.php; агент сайта 1.5.0+ передаёт `AIWB_DB_URL`, `AIWB_DB_KEY_FILE`, `AIWB_CHAT_ID`. Без `--resource` (`spawn`, `post`) — как раньше, только на домашнем ПК.

## Как пользоваться

1. Разбить задачу на независимые части; каждая подзадача получает полный самодостаточный текст (истории родителя у неё нет).
2. Запустить все `spawn` сразу, собрать `jobId`.
3. `wait` всех — и свести ответы в свой ответ пользователю, со ссылками на дочерние чаты.
4. Дешёвые подзадачи (поиск, выжимки) — `--model sonnet` или `--agent codex`; тяжёлые — модель по умолчанию.

`--timeout-minutes` — лимит задания (по умолчанию 120; 0 — значение режима на хосте; Bridge ограничивает `maxProcessTimeoutMinutes`).

## Ограничения

- `spawn` без `--resource` и `post` — только на домашнем ПК (SSH к базе, Google-доступ Bridge, ключ `aiwb/co-push`). `wait`/`status` идут через db.php `subtask_poll`, на старом сервере — через SSH.
- Права подзадачи: файлы write, shell, web. Устройства (engie_chat, Outlook и т. п.) — только с `--device`; `--agent dispatcher` требует `--device` (модель по умолчанию `claude-opus-5.5`). Папки контекста — `--context-folder gdrive:AI-Workbench:write`.
- Отчёт в родительский чат делает сам родительский агент своим ответом.

## История

- 0.7.0 — 2026-11-13 — Freedom F12 (много исполнителей, схема 28): `--host` у `spawn`/`post` — любой id исполнителя (`home-main`, `site` или зарегистрированный Bridge на другом ПК, Маке, Linux-сервере), неизвестный id отклоняется до постановки задания; новая команда `hosts` — список исполнителей (db.php `executors_host`: вид, ОС, чей, версия, берёт ли задания, агенты).
- 0.6.3 — 2026-11-13 — ядро — Freedom: `install/aiwb_install.py` ищется в `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`; архив `C:\AIWorkbench\source` больше не нужен.
- 0.6.2 — 2026-11-12 — Freedom: SSH — hz.py из `~/.ssh` (`AIWB_HZ_DIR`), без него install.json `site.ssh`; база — `AIWB_DB_NAME` или install.json `site.db.name`; адрес db.php — `AIWB_DB_URL` или install.json `site.url`; личных имён в коде нет, поведение у владельца прежнее.
- 0.6.1 — 2026-11-11 — R2 (ревью Codex S5-02): `spawn --resource` идемпотентен — `requestKey` (sha256 от задания, родителя, ресурса и промпта), повтор при сбое/таймауте/5xx/`in_progress` с тем же ключом, `--again` для нарочного повтора; сервер (db.php `subtask_spawn`) отдаёт тот же чат и задание. Наследование запретов общего чата по цепочке подзадач (S5-01) — на сервере.
- 0.6.0 — 2026-11-11 — S5: `models` (ресурсы-модели с карточками и флагом slave allowed), `spawn --resource` через сервер (db.php `subtask_spawn`, схема 23), `wait`/`status` через `subtask_poll`; работает и на Site (ключ из `AIWB_DB_KEY_FILE`, адрес `AIWB_DB_URL`, чат `AIWB_CHAT_ID`); keyring/requests/hz импортируются только там, где нужны.
- 0.5.2 — 2026-11-02 — `post` с другим `--host` (или агентом), чем у последнего задания, начинает новую сессию: сессия провайдера есть только на том хосте, где создана.
- 0.5.1 — 2026-11-02 — `post` в чат без завершённых заданий (первое упало) берёт режим, права, папки и устройства из последнего задания чата (новая сессия).
- 0.5.0 — 2026-10-19 — `--timeout-minutes` для `spawn` и `post`, по умолчанию 120 мин (было 0 — значение режима).
- 0.4.0 — 2026-10-19 — `post`: сообщение в существующий чат с продолжением его сессии.

- 0.3.0 — 2026-10-19 — `--top-level` (код — корпоративная модель claude-opus-5.5).
- 0.2.0 — 2026-10-18 — `--agent dispatcher`, `--device`, `--context-folder` (код — корпоративная модель claude-opus-5.5).

- 0.1.0 — 2026-10-17 — первая версия (код — корпоративная модель claude-opus-5.5); проверено: две параллельные подзадачи (Claude sonnet + Codex) отработали и записали ответы в дочерние чаты.
