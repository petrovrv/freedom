---
name: aiwb-smoke
description: Live check of the AI Workbench database job queue — puts one real job (claude, chat, "reply pong") into the queue of a Bridge host and waits for its result; or prints a 24 h queue summary with failure causes (24 h / 7 d) and Bridge supervisor events. Use after a Bridge release or when jobs seem stuck.
version: 0.3.3
entry: aiwb-smoke.py
requires: [paramiko]
platform: windows
secrets: []
---

# aiwb-smoke

Проверяет очередь заданий в базе (Bridge 1.3+, `db.php` схема 4+) на живом хосте: задание кладётся прямо в таблицу `jobs` (`origin=db`, `state=inbox`), Bridge забирает его через `job_claim`, выполняет и возвращает результат. SQL выполняется на хостинге по SSH: через `hz.py` владельца в `~/.ssh` (папка — переменная `AIWB_HZ_DIR`), если он есть, иначе по `site.ssh` из install.json (`aiwb_install`), с `~/co-agent/secrets/my.cnf` сервера; база — `AIWB_DB_NAME` или `site.db.name` из install.json, окружение владельца по умолчанию — `AIWB_OWNER_ENV` или владелец из install.json. Секретов в инструменте нет.

## Вызов

```
python "G:\My Drive\AI-Workbench\tools\aiwb-smoke\aiwb-smoke.py" queue [--host home-main] [--project bridge-test] [--chat CHAT-SMOKE] [--model sonnet] [--timeout 240]
python "G:\My Drive\AI-Workbench\tools\aiwb-smoke\aiwb-smoke.py" status
```

- `queue` печатает переходы состояния с секундами от постановки (`claimed`, `completed`), число пакетов событий в `job_events` и ответ; код 0 — ответ `pong`, 1 — ошибка или таймаут (задание тогда отменяется в базе). Если сработала страховка Drive (`fallback_utc`), это видно в строке состояния.
- `status` — задания за 24 ч по происхождению и состоянию, задания из базы без результата (сколько минут ждут), время последнего взятия из базы; с 0.2.0 ещё причины упавших заданий за 24 ч / 7 дней по видам диагноза (таблица `job_failures`, схема 17: вид, число за 24 ч и 7 дней, последнее задание и первая строка ошибки) и события надзора Bridge за 24 ч (`host_events`). Пока на сервере нет схемы 17 — строка `health: not deployed (schema 17)`. Это сводка для ежедневной проверки.

## Примеры

```
python aiwb-smoke.py queue                                   # живой Bridge home-main, проект bridge-test
python aiwb-smoke.py queue --host test-dbq --project dbq-test # тестовый экземпляр (AIWB_BRIDGE_INSTANCE, свой --config)
```

## Ограничения

- Задание реальное: один короткий запуск Claude (sonnet, effort low) по подписке; рабочая папка — `projects/<project>/chats/<chat>/scratch` хоста.
- Проект должен быть разрешён Bridge для режима chat и агента claude (`bridge-test` в `config.json`).
- В чат CO ничего не пишется: у задания нет `user_message`, сервер ответ в чат не добавляет.

## Своё задание (0.3.0)

`queue --host site --env anna --mode cowork --prompt "открой linkedin.com в домашнем браузере" --tool-like mcp__dev_` ставит задание со своим текстом; `--expect` — подстрока, которая должна быть в ответе, `--tool-like` требует события задания с этим текстом (например вызов устройства `mcp__dev_<short>__…` из задания Site, S1 relay). Без `--prompt` — прежняя проверка «pong».

## История

- 0.3.3 — 2026-11-13 — ядро — Freedom: `install/aiwb_install.py` ищется в `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`; архив `C:\AIWorkbench\source` больше не нужен.
- 0.3.2 — 2026-11-12 — Freedom: без личных значений по умолчанию — база (`AIWB_DB_NAME` → install.json `site.db.name`), окружение владельца (`AIWB_OWNER_ENV` → install.json), папка hz.py (`AIWB_HZ_DIR` → `~/.ssh`); у владельца результат прежний.
- 0.3.0 — 2026-11-11 — `queue --prompt/--expect/--tool-like/--mode`: живая проверка своим заданием (вызов домашнего устройства из задания Site).
- 0.2.0 — 2026-11-03 — `status`: причины упавших заданий 24 ч / 7 д (`job_failures`) и события надзора Bridge (`host_events`), схема 17.
- 0.1.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-16 — первая версия: `queue`, `status`.

## Другая установка (Freedom F2, 0.3.1)

Без `~/.ssh/hz.py` инструмент подключается к сайту по `site.ssh` из install.json (`install/aiwb_install.py` ядра: `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`); имя базы — `site.db.name`, окружение по умолчанию — владелец из `members`. Без install.json — прежние значения.
