---
name: aiwb-budget
description: Token budget governor for scheduled campaigns — reads the weekly subscription usage from limits.json (Bridge) and says how many agent jobs a dispatcher tick may start now, so that by the end of budget day k the week's used share stays ≤ k/7 (carry-over allowed), sequential by day and parallel at night; JSONL log and spend per day. Use in schedule ticks that spawn subtasks.
version: 0.2.1
entry: aiwb-budget.py
requires: []
secrets: []
---

# aiwb-budget

Ограничитель расхода для заданий по расписанию. Берёт недельное окно подписки (`windowMinutes` 10080) агента из `transport\hosts\*\limits.json` (самое свежее по `observedUtc`, окна с прошедшим сбросом пропускаются). Неделя делится на 7 бюджетных суток от момента сброса: к концу суток k расход ≤ k·100/7 % минус резерв на собственные чаты владельца. Недобор прошлых суток переносится, среднее за сутки не превышает 1/7 недели.

## Вызов

```
python aiwb-budget.py check [--running N] [--job-cost 2] [--reserve 3] [--day-parallel 1] [--night-parallel 3]
                            [--day-share 0.5] [--night-start 23] [--night-end 8] [--agent claude] [--log state.jsonl] [--note text] [--strict-stale]
python aiwb-budget.py history --log state.jsonl
```

`check` печатает JSON: `usedPercent`, `budgetDay`, `ceilingPercent`, `headroomPercent`, `mode` (day/night по местному времени), `maxParallel`, `allowSpawn` = min(maxParallel − running, ⌊headroom / jobCost⌋), `reason`. `--day-share 0.5` — днём доступна только половина сегодняшней 1/7, остальное ночью. Код 0; 2 — окна нет (тогда не запускать). `--log` дописывает строку снимка; `history` показывает расход по бюджетным суткам.

## Примеры

```
python aiwb-budget.py check --running 1 --job-cost 3 --log C:\Users\me\campaign\budget.jsonl --note tick
```

## Ограничения

- `limits.json` обновляется, только когда на хосте работает агент этого типа; при возрасте > `--stale-minutes` в `reason` пишется предупреждение (`--strict-stale` — тогда не запускать).
- Расход общий на всю подписку: чаты владельца и другие расписания тоже его тратят — для этого `--reserve`.
- Стоимость одного задания (`--job-cost`) — оценка; уточнять по `history`.

## История

- 0.2.1 — 2026-11-12 — Freedom: примеры без личных путей и доменов; поведение не изменилось.
- 0.2.0 — 2026-11-06 — `--day-share`: днём тратится только доля суточной 1/7.
- 0.1.0 — 2026-11-06 — первая версия (кампания норм Buildwise для сайта инструментов).
