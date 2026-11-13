---
name: aiwb-modelbench
description: Quality matrix of agents and models by task type (code, docx, xlsx, translate, edit, summary, reasoning, web) — runs golden tasks on Claude/Codex subscriptions and corporate models (engie_chat), regex checks + corporate-model judge, writes report.md, recommendations.json and the hint data of the Freedom module task-hints. Use to choose an agent/model per task type or to refresh the recommendations.
version: 0.2.2
entry: aiwb-modelbench.py
requires: []
platform: windows
secrets: []
---

# aiwb-modelbench

## Назначение

aiwb-modelbench строит матрицу качества агентов и моделей AI Workbench по типам задач. По этой матрице инструмент выбирает рекомендуемую цель для каждой категории и формирует данные подсказки для CO Web.

## Команды

```
python aiwb-modelbench.py list
python aiwb-modelbench.py run [--targets ids] [--tasks ids] [--categories cats] [--job JOB-ID | --mcp-config PATH] [--min-left 30] [--parallel-cli 2] [--rerun] [--dry-run] [--out DIR]
python aiwb-modelbench.py judge [--judge-model claude-opus-5.5] [--job JOB-ID] [--rejudge]
python aiwb-modelbench.py report [--corporate auto|on|off] [--module DIR]
```

- `list` выводит цели и задачи из `tasks.json`.
- `run` выполняет пары «цель × задача».
  - `--targets`, `--tasks`, `--categories` задают фильтр по идентификаторам и категориям.
  - `--job JOB-ID` указывает задание Claude или Dispatcher на домашнем ПК с device MCP. Скрипт находит `%LOCALAPPDATA%\AIWorkbenchBridge\jobs\<id>-*\mcp-devices.json`.
  - `--mcp-config PATH` задаёт путь к конфигурации MCP напрямую, вместо `--job`.
  - `--min-left` задаёт минимальный остаток окна лимита в процентах (по умолчанию 30).
  - `--parallel-cli` задаёт число параллельных вызовов claude/codex CLI (по умолчанию 2).
  - `--rerun` повторяет пары со статусом `ok`.
  - `--dry-run` выводит план без вызовов.
  - `--out` задаёт папку вывода.
- `judge` оценивает ответы корпоративной моделью (по умолчанию `claude-opus-5.5`). `--rejudge` повторно оценивает уже оценённые ответы.
- `report` формирует `report.md`, `recommendations.json`, `hint-data.js` и `taskHintData.ts` из `results.jsonl`. `--module <repo>/modules/task-hints` сразу пишет `ui/hint-data.js` модуля Freedom `task-hints` (потом подписать и поставить модуль, см. его README).

Каждая пара «модель × задача» выполняется один раз. Пары со статусом `ok` без `--rerun` не повторяются, поэтому повторный `run` выполняет только пары с ошибками и таймаутами.

## Файлы и вывод

| Файл | Содержимое |
|---|---|
| `aiwb-modelbench.py` | CLI |
| `runners.py` | `EngieClient` — MCP stdio-клиент device proxy, вызывает `engie_chat`; `run_cli` — запускает claude/codex CLI с аргументами Bridge из `%LOCALAPPDATA%\AIWorkbenchBridge\config.json` |
| `tasks.json` | цели и задачи |

Папка вывода по умолчанию: `G:\My Drive\AI-Workbench\lab\model-bench`.

| Файл | Содержимое |
|---|---|
| `results.jsonl` | Журнал только с дозаписью. Для пары «цель + задача» действует последняя строка. Строки заголовка и окончания запуска содержат снимки `limits.json`. |
| `report.md` | Матрица и таблица рекомендаций. |
| `recommendations.json` | Рекомендации по категориям в машиночитаемом виде. |
| `hint-data.js` | Данные подсказки модуля Freedom `task-hints` (CO Web 0.103+). Копируется в `modules/task-hints/ui/` (или `report --module`). |
| `taskHintData.ts` | Данные подсказки CO Web до 0.102 (раньше копировались в `co-web/src`). |

### Категории и задачи

Категории: `code`, `docx`, `xlsx`, `translate`, `edit`, `summary`, `reasoning`, `web`.

Всего 13 задач. Текстовые задачи выполняются для всех целей. Две задачи cowork (создать файл docx/xlsx и прочитать его обратно) выполняются только для claude и codex.

### Цели

| Группа | Модели | Ограничения |
|---|---|---|
| Корпоративная модель (`engie_chat`) | gpt-6.1-sol, gpt-6-sol, gpt-6-luna, mistral-large-3, claude-opus-5.5, claude-sonnet-5.5, claude-sonnet-5, claude-haiku-4.5 | Категория `web` выполняется только для моделей gpt и mistral-large: у моделей Claude в AWS нет инструмента `web_search`. |
| Подписка Claude Code | sonnet, opus 5.5 | opus 5.5 выполняется только в категориях `code`, `docx`, `xlsx`, `reasoning`, `edit`. |
| Подписка Codex | gpt-6-luna, gpt-6-sol, gpt-6-astra | gpt-6-astra выполняется только в категориях `code`, `reasoning`, `docx`. |

Поле `"disabled": "<причина>"` исключает цель из запуска. Цель codex gpt-6.1-sol отключена: Codex с учётной записью ChatGPT отвечает «model is not supported».

## Оценка и рекомендация

### Оценка

1. Сначала выполняется детерминированная проверка регулярным выражением. При несовпадении ответ получает 0.
2. Задачи с рубрикой оценивает корпоративная модель (по умолчанию `claude-opus-5.5`). Один вызов содержит до 5 анонимизированных ответов. Каждый ответ получает оценку 0–10 с обоснованием.

Смещение: судья относится к семейству Claude и одновременно участвует в сравнении.

### Рекомендация по категории

1. Для каждой цели вычисляется средняя оценка.
2. В кандидаты попадают цели, средняя оценка которых отстоит от максимальной не более чем на 0,5.
3. Среди кандидатов выбирается тариф в порядке: engie, лёгкая подписка, тяжёлая подписка. При равном тарифе выбирается цель с меньшим временем ответа.
4. Резервная цель — цель с наибольшей оценкой среди агентов, отличных от агента рекомендованной цели.

## Корпоративные модели и модуль corporate (с 0.2.2)

Корпоративные цели (`tier: engie`, агенты `engie` и `dispatcher`) участвуют, только пока включён модуль Freedom `corporate`: `--corporate auto` (по умолчанию) смотрит его в `tool-modules.json` (toolbox, `modstate.py`), без записи — `connectors.corporate` в install.json, без него — выключено. Выключен — `run` их пропускает (кроме явно названных в `--targets`), `report` не рекомендует их и не пишет в `recommendations.json`, `hint-data.js` и `taskHintData.ts`; в матрице `report.md` старые оценки остаются, строка «Corporate models» говорит, учтены ли они. `--corporate on|off` — вручную. Подписи корпоративных целей в отчёте и подсказках не называют работодателя: скобка в конце подписи заменяется на «(corporate)».

## Ограничения и известные проблемы

- **Контроль лимитов.** Перед каждым вызовом Claude или Codex скрипт читает `transport/hosts/home-main/limits.json`. Если в любом окне этого агента использовано более `100 − min-left` процентов, пара записывается как `skipped` и не получает оценку.
- **Зависимость корпоративной модели от задания.** Её вызовам нужно запущенное задание Claude или Dispatcher на домашнем ПК с device MCP. Токены корпоративной модели берутся из `usage-ledger.jsonl` этого задания.
- **Таймауты.** Вызов устройства завершается через 120 с (лимит CO Desktop). 2026-10-18 накладные расходы одного вызова составляли 40–60 с, поэтому ответы с генерацией дольше 60–80 с завершаются таймаутом.
- **Время корпоративной модели.** Время её ответа включает ожидание за вызовами других агентов на том же устройстве.
- **Длительность.** Полная матрица выполняется около 2 ч, когда устройство используют другие агенты.
- **Расход подписки.** 32 запуска Codex израсходовали около 19 % 5-часового окна подписки.

## История

- **0.2.2** — 2026-11-13 — корпоративные цели только при включённом модуле `corporate` (`--corporate auto|on|off` у `run` и `report`): иначе их нет в рекомендациях и данных подсказки task-hints; имя работодателя в подписях больше не вписывается (у корпоративных целей всегда «(corporate)»). Проверено на results.jsonl 18.10 без новых прогонов: выключено — 0 корпоративных записей в hint-data.js, включено — подписи только «(corporate)».
- **0.2.1** — 2026-11-13 — нейтральные названия для экспорта Freedom: подписи корпоративных моделей «(corporate)» вместо бренда работодателя в tasks.json, SKILL.md и комментариях; поведение не изменилось.
- **0.1.0** — 2026-10-18 — первая версия, код написан корпоративной моделью claude-opus-5.5.