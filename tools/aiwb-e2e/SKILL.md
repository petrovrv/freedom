---
name: aiwb-e2e
description: End-to-end check of CO Web - Playwright signs in as the owner, sends "reply pong" to Claude in a test chat, waits for the reply through the job queue, deletes the chat; exit 0 green / 1 red / 2 cannot run. Used by devtree finish as a merge gate. Also `shot` - screenshot of any CO Web page (local build or prod) signed in as the owner.
version: 0.6.4
entry: aiwb-e2e.py
requires: [requests, paramiko, node 22+, "playwright + chromium (npm, local)"]
platform: windows
secrets: []
---
# aiwb-e2e

## Назначение
Сквозная проверка CO Web. Скрипт входит под владельцем, отправляет Claude «reply pong» в тестовом чате, ждёт ответа через очередь заданий и удаляет чат. Служит гейтом слияния в `devtree finish` (devtree 0.5.0+).

## Вызов
```
python "G:\My Drive\AI-Workbench\tools\aiwb-e2e\aiwb-e2e.py" run [--host auto|home-main|site] [--project bridge-test] [--agent claude] [--model-hint sonnet] [--prompt TEXT] [--expect pong] [--timeout 300] [--queue-wait 600] [--dist PATH] [--keep] [--headed] [--no-db] [--json]
```

## Как входит
- Берёт Google access token Bridge из Windows Credential Manager (`AIWorkbench.Bridge.Google`), почту определяет через Drive `about`.
- До загрузки страницы Playwright задаёт `window.coDesktop.getAccessToken`, и CO Web входит так же, как внутри CO Desktop.
- Токен передаётся в node через stdin и не печатается.

## Сценарий
- Проект `<сайт>/e/<окружение владельца>/p/bridge-test`: архивная песочница (сайт — `--base`, по умолчанию `AIWB_SITE_URL` или `site.url` из install.json; окружение — `--env`, по умолчанию `AIWB_OWNER_ENV` или владелец из install.json).
- Агент Claude, модель sonnet, effort low.
- Сообщение: `[aiwb-e2e RUNID] reply pong`.
- Ждёт `CHAT-id` в адресе и финальное `.msg.assistant.status-completed`, затем проверяет текст по regex `--expect`.
- Удаляет чат кнопкой Delete (при `--keep` оставляет).

## Очередь
- Перед отправкой ждёт свободный слот Bridge. Занятость = задания в `G:\My Drive\AI-Workbench\transport\hosts\home-main\claimed` + claimed в базе + inbox в базе за 3 ч (задание, взятое Bridge через Drive, в базе остаётся inbox). Лимит: `maxParallelJobs` из `config.json` Bridge (6).
- `--host auto`: home-main, если слот освободился за `--queue-wait`, иначе site.
- После прогона печатает задание из таблицы `jobs`: origin, время claimed/completed в секундах.
- Если результат не зелёный, отменяет в базе незавершённое задание тестового чата.

## --dist
Статика берётся из локальной сборки через перехват запросов Playwright. Пути `/agent/` и `/auth/` идут на прод.

## Скриншот страницы (`shot`, с 0.2.0)
```
python "G:\My Drive\AI-Workbench\tools\aiwb-e2e\aiwb-e2e.py" shot --path /e/anna/p/AI-Workbench/c/CHAT-... --out shot.png [--dist <worktree>\co-web\dist] [--mobile] [--click SEL] [--wait-selector ".chat-list li"] [--settle-ms 2500] [--storage co.leftW=430] [--clip-selector "aside.left"] [--eval "JS"] [--json]
```
- Вход и `--dist` — как у `run`; заданий не создаёт. `--storage KEY=VALUE` (повторяемый) кладёт значения в localStorage до загрузки; `--click` — нажать до ожидания (на мобильном — `button[aria-label="Projects"]` открывает левую панель); `--eval` — выражение JS после паузы, результат печатается.
- Из Git Bash задавать `MSYS_NO_PATHCONV=1`, иначе `/e/...` превращается в `E:/...`.
- `--steps FILE` (с 0.5.0): JSON-список шагов после загрузки и паузы, до итогового снимка: `{"click": sel}`, `{"fill": sel, "text": "…"}`, `{"press": "Enter", "on": sel}` (без `on` — клавиатура страницы), `{"wait": sel, "timeout": ms}`, `{"sleep": ms}`, `{"select": sel, "value": "editor"}` (с 0.5.1), `{"files": "input[type=file]", "paths": ["C:/…"]}`, `{"eval": "JS"}` (результат в `steps`), `{"shot": "C:/…/x.png"}`; у любого шага `"optional": true` — ошибка не прерывает прогон. Так проверяются сценарии UI (новый чат, отправка, загрузка фото, реакции) на `--dist` локальной сборки. Шаги работают на проде под владельцем: созданные чаты удалять. В Git Bash путь `--path /e/...` передавать с `MSYS_NO_PATHCONV=1`, иначе он станет `E:/...`.
- `--browser msedge` (с 0.4.0): установленный Edge через драйвер chromium; брать, если встроенный headless-shell грузит страницы десятки секунд (так было 08.11: example.com 45 с против 3 с у Edge, shot падал по таймауту).
- `--browser webkit|firefox|chromium` (с 0.3.0, по умолчанию chromium): движок; `webkit` — проверка под Safari (MacBook). Один раз: `cd %USERPROFILE%\aiwb-site && npx playwright install webkit`. `navigator.platform` у WebKit на Windows — не Mac, подсказки ⌘ в CO Web так не проверить.
- Коды: 0 снято, 1 ошибка страницы (снимок всё равно сохраняется), 2 запуск невозможен.

## Коды выхода
- `0`: зелёный.
- `1`: красный.
- `2`: запуск невозможен (нет токена, node или playwright, либо хост занят).

## Файлы
- `aiwb-e2e.py` — обёртка; `e2e.mjs` — сценарий Playwright (конфиг на stdin, результат — последняя строка stdout); `e2e_db.py` — база по SSH через `hz.py` владельца в `~/.ssh` (папка — `AIWB_HZ_DIR`), без него — `site.ssh` из install.json; имя базы — `AIWB_DB_NAME` или `site.db.name` из install.json.
- Скриншот при неудаче и `runs.jsonl` пишутся в `%LOCALAPPDATA%\AIWorkbenchWork\e2e`.
- Модуль playwright: `%USERPROFILE%/aiwb-site/node_modules` (переопределяется переменной `PW_MODULE`).

## Примеры
```
python "G:\My Drive\AI-Workbench\tools\aiwb-e2e\aiwb-e2e.py" run
python "G:\My Drive\AI-Workbench\tools\aiwb-e2e\aiwb-e2e.py" run --host site --expect zzz-never-zzz   # намеренно красный
python "G:\My Drive\AI-Workbench\tools\aiwb-e2e\aiwb-e2e.py" run --host auto --dist "<worktree>\co-web\dist"
```

## Ограничения
- Задание реальное: каждый прогон делает один короткий запуск Claude.
- Если на home-main заняты все 6 слотов, скрипт ждёт или (`--host auto`) уходит на site.
- 18.10 удаление чата в CO Web один раз не отменило задание в базе, поэтому e2e сам отменяет оставшиеся задания.

## История
- 0.6.4 — 2026-11-13 — ядро — Freedom: `install/aiwb_install.py` ищется в `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`; архив `C:\AIWorkbench\source` больше не нужен.
- 0.6.2 — 2026-11-12 — Freedom: без личных значений по умолчанию — сайт (`AIWB_SITE_URL` → install.json), окружение владельца (`AIWB_OWNER_ENV` → install.json), база (`AIWB_DB_NAME` → install.json `site.db.name`), папка hz.py (`AIWB_HZ_DIR` → `~/.ssh`), playwright из `~/aiwb-site`; у владельца результат прежний.
- 0.6.0 — 2026-11-11 — `run` выбирает агента и модель и в новом окне выбора CO Web 0.100.0 (S6, кнопка `button.mp-btn`, строки `.mp-row[data-agent][data-host]`, модели `[data-model]`); старые списки Agent / Model по-прежнему поддерживаются.
- 0.5.2 — 2026-11-11 — `e2e.mjs`/`shot.mjs` ставят и `co.hostPref` (CO Web 0.98.0, S2: светофор вместо переключателя Home · Site, исполнитель auto/home/site); без этого `--host site` уходил на home-main.
- 0.5.1 (2026-11-09): шаг `select` (выпадающий список).
- 0.5.0 (2026-11-09): `shot --steps FILE` — сценарий UI-шагов (click/fill/press/wait/sleep/files/eval/shot).
- 0.4.0 (2026-11-08): `shot --browser msedge`; проверка входа читает и `data-email` в шапке (CO Web 0.86.0 убрал email в меню ☰), уборка жмёт «Delete chat» во вкладке Chat.
- 0.3.0 (2026-11-03): `shot --browser webkit` — снимок в движке Safari (учебный вьювер CO Web 0.80.0 для MacBook); WebKit 27.2 поставлен на домашнем ПК.
- 0.2.2 (2026-11-02): закрытие Chromium ограничено 15 с, node завершается сам после вывода результата. 02.11 `browser.close()` висел после зелёного прогона (ответ за 101 с, чат удалён), node убивали через 600 с, и прогон считался «не смог запуститься».
- 0.2.1 (2026-11-01): прогон на `site` выключает инструменты устройства (`co.useDevice=off`): эмулированный CO Desktop со спаренным устройством заставлял CO Web слать задание на Home. Задание ушло не на тот хост — красный с причиной; занятость проверяется на хосте, куда ушло задание; в отчёте по заданию `hostId` и `driveFallback`.
- 0.2.0 (2026-10-18): команда `shot` (`shot.mjs`).
- 0.1.0 (2026-10-18): первая версия. На site ответ приходит через 25–45 с после отправки, прогон занимает около 60 с.

## Другая установка (Freedom F2, 0.6.1)

Без `~/.ssh/hz.py` проверки базы идут по `site.ssh` из install.json (`install/aiwb_install.py` ядра: `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`), затем `repo` из `%LOCALAPPDATA%\AIWorkbench\devtree.json`, затем `C:\AIWorkbench\freedom`); `--base` и `--env` по умолчанию — `site.url` и владелец из install.json, имя базы — `site.db.name`. Без install.json — прежние значения.
