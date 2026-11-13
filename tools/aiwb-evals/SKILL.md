---
name: aiwb-evals
description: Golden-task suite of AI Workbench — 18 real jobs through the database queue (Claude/Codex/Dispatcher, home and site, permissions, device tools, corporate-model delegation, no-send, secrets, tool library) with PASS/FAIL checks. Run after every Bridge release (and after big CO Web/site changes).
version: 0.1.4
entry: aiwb-evals.py
requires: [paramiko]
platform: windows
secrets: []
---

# aiwb-evals

Эталонные задачи AI Workbench: настоящие задания через очередь в базе, ответ и журнал событий проверяются правилами из `cases.json`.

## Вызов

```
python aiwb-evals.py list
python aiwb-evals.py run [--only id1,id2] [--skip id] [--parallel 3] [--host site] [--device d-…] [--json out.json]
```

Выход 1, если хоть один сценарий FAIL. `xfail` в сценарии — известная проблема: показывается как XFAIL/XPASS и не валит прогон. Устройство по умолчанию — `AIWB_EVALS_DEVICE`, иначе `evals.device` из install.json (ПК, где открыта сессия корпоративной модели).

Проверки сценария (`expect`): `status`, `contains`, `not_contains`, `regex`, `tools_used`, `tools_not_used` (подстрока журнала `job_events`, без экранирования; элемент `tools_used` может быть списком — хватает любой из подстрок), `tool_input_not_used` (подстрока только внутри вызовов инструментов, не в тексте ответа), `max_seconds`.

## Настройки

- SSH к хостингу: `hz.py` владельца (`AIWB_HZ_DIR`, иначе `~/.ssh`), иначе `site.ssh` из install.json (через `install/aiwb_install.py` дерева исходников, `AIWB_SOURCE`).
- База: `AIWB_DB_NAME`, иначе `site.db.name` из install.json.
- Окружение заданий: `AIWB_OWNER_ENV`, иначе `env` участника с `role: "owner"` в `members` install.json (сценарий может задать своё `env`).
- Устройство для `$DEVICE`: `--device`, иначе `AIWB_EVALS_DEVICE`, иначе `evals.device` из install.json (`"evals": {"device": "d-…"}`).

## Когда запускать

- После каждого выпуска Bridge, когда launcher переключился на новую версию (порядок в `context/release.md`). FAIL → разобраться или вернуть прошлую версию.
- После заметных изменений CO Web, агента сайта, библиотеки инструментов.

## Ограничения

- Прогон стоит ~18 заданий на подписках (Sonnet, GPT-6 Luna, Opus 5.5 у Dispatcher) и занимает 4–5 минут; задания сайта ждут опроса очереди до ~3 минут.
- Вызовы инструментов видны в журнале только у Claude на домашнем ПК; Codex и агент сайта проверяются по ответу.
- Чаты заданий — `CHAT-EVAL-<hash>` в проекте `bridge-test` (sandbox, в архиве).

## История

- 0.1.4 — 2026-11-13 — Codex на хосте site: сценарий `codex-chat-site-refused` ждёт отказа «send the message to Home» (с агента сайта 1.2.1 GPT там не предлагается — лимит потоков хостинга); ответом считается `agentFinalResponse`, иначе `error` результата; у `codex-cowork-home-write-file` снята пометка известной проблемы (запись на G: проходит: 2 из 2 прогонов против Bridge 1.17.0); короткие сбои MySQL хостинга (ERROR 2002/2013/1040) повторяются, а не валят сценарий.
- 0.1.3 — 2026-11-13 — проверки без ложных FAIL: запись файла через Write, PowerShell или Bash; секрет — `tool_input_not_used` (ответ может показать пользователю команду), удаление — «didn't exist», краткое изложение — pump или P-101.
- 0.1.2 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (SSH — `site.ssh`, база — `site.db.name`, окружение — владелец из `members`, устройство — `evals.device`; сценарии корпоративной модели названы нейтрально).
- 0.1.1 — 2026-10-17 — сценарий connectors_status без живой проверки (check=false): живые проверки открывали сайты в браузере CO пользователя. Прогон против Bridge 1.3.4: 18/18 (1 известная проблема).
- 0.1.0 — 2026-10-17 — первая версия (код и сценарии — корпоративная модель claude-opus-5.5); первый зелёный прогон 17/18 + 1 известная проблема.
