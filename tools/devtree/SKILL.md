---
name: devtree
description: Separate git worktree per agent for the core repository (Freedom; main tree from devtree.json, default C:\AIWorkbench\freedom, its GitHub origin); start/finish with rebase + fast-forward under a lock, the repository's own guard before merge and push (.devtree.json, e.g. no personal data in a public repository), push to GitHub, update the Drive mirror; atomic release locks (release-co-desktop, release-bridge). Use before editing source or releasing.
version: 0.8.1
entry: devtree.py
requires: [git]
platform: windows
secrets: []
---

# devtree

Разводит работу нескольких агентов над репозиторием ядра (с 13.11.2026 — Freedom, `C:\AIWorkbench\freedom`): каждый агент правит свою ветку в отдельном worktree и вливает её в `main` под блокировкой. Блокировки выпуска закрывают сценарий 14.10, когда два агента правили одно дерево и выпустили две разные сборки CO Desktop 0.5.1.

## Где лежит репозиторий (с 0.2.0)

- Основная рабочая копия — `repo` из `devtree.json`, без него `C:\AIWorkbench\freedom` (локальный диск), `origin` — репозиторий на GitHub. Переопределяется переменной `AIWB_FREEDOM` (старое имя `AIWB_SOURCE`) или ключом `--repo`.
- `G:\My Drive\AI-Workbench\freedom` — зеркало для просмотра (`--mirror`, переменная `AIWB_MIRROR`; `""` — не обновлять). В зеркале не править и не коммитить.
- `finish` после слияния выполняет `git push origin main` и обновляет зеркало (`fetch` + `merge --ff-only`, перед этим удаляет `desktop.ini`, которые Google Drive кладёт в `.git/refs`). Ошибки отправки и зеркала — предупреждения, код выхода 0. `--no-push` — без отправки. `sync` — только отправка и зеркало.

## Когда применять

- Перед любой правкой в `source`, даже если других агентов сейчас нет.
- Перед выпуском CO Desktop или Bridge, чтобы захватить блокировку выпуска.
- Чтобы узнать, какие агенты сейчас работают, что у них не влито и какие блокировки заняты.

## Вызов

```
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" start NAME [--base main]
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" list
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" finish NAME -m "MSG" [--keep] [--review auto|on|off] [--e2e auto|on|off] [--e2e-host auto|home-main|site] [--force] [--owner-approved TEXT]
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" drop NAME [--force]
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" lock acquire RES --owner JOB-... [--ttl-min 60] [--wait-sec 0]
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" lock release RES --owner JOB-... [--force]
python "G:\My Drive\AI-Workbench\tools\devtree\devtree.py" lock status [RES]

# Глобальные ключи (для любой команды):
#   --json        вывод одним JSON-объектом
#   --repo PATH   репозиторий (по умолчанию из devtree.json, иначе C:\AIWorkbench\freedom)
#   --work-root   корень worktree (по умолчанию %LOCALAPPDATA%\AIWorkbenchWork)
#   --locks PATH  каталог блокировок (по умолчанию G:\My Drive\AI-Workbench\transport\locks)
```

`NAME` соответствует `[a-z0-9-]{1,40}`, например `job-123`.

## Порядок работы агента

1. Выполнить `start NAME`, около 10 с. Создаётся ветка `agent/NAME` и worktree `%LOCALAPPDATA%\AIWorkbenchWork\NAME`. Запомнить напечатанный путь.
2. Все правки, сборки и тесты вести только в этом пути. Основную копию и зеркало на Drive не трогать.
3. Если задание включает выпуск, захватить блокировку до выбора номера версии: `lock acquire release-co-desktop --owner JOB-... --wait-sec 120`. Код 3 означает, что выпуск ведёт другой агент; ждать его завершения и после этого заново проверить номер версии.
4. Выполнить `finish NAME -m "MSG"`. Перед слиянием идут кросс-ревью Codex и (для co-web, co-web-auth, co-agent, bridge, co-desktop) сквозная проверка aiwb-e2e.
   - Код 2: rebase отменён, напечатаны конфликтные файлы. Выполнить в worktree `git rebase main`, разрешить конфликты и повторить `finish`.
   - Код 3: основная копия грязная или `source-merge` занята. Проверить через `list` и `lock status`, затем повторить.
   - Код 6: задет защищённый путь — спросить владельца и повторить с `--owner-approved "..."`.
   - Код 4: Codex BLOCK; код 5: e2e красный (отчёт и скриншот напечатаны). Исправить и повторить `finish`; `--force` — слить всё равно.
5. Сверить напечатанный новый HEAD `main` с ожидаемым.
6. После публикации выпуска выполнить `lock release RES --owner JOB-...`. Если выпуск длится дольше TTL, продлить блокировку повторным `lock acquire` с тем же владельцем.
7. Если задание отменено, выполнить `drop NAME`. `--force` указывать, только если невлитые коммиты точно не нужны.

## Блокировки

- Файл блокировки: `G:\My Drive\AI-Workbench\transport\locks\RES.lock`. Создаётся атомарно (`O_CREAT|O_EXCL`) и содержит JSON с полями `owner`, `acquiredUtc`, `ttlMinutes`, `pid`, `host`.
- Владелец указывается как id задания (`JOB-...`).
- Повторный захват тем же владельцем продлевает блокировку.
- Блокировку другого владельца команда ждёт до `--wait-sec` секунд и затем завершается с кодом 3.
- Просроченная блокировка, а также нечитаемая дольше 60 с, переименовывается в `RES.lock.stale-<epoch>` и захватывается.
- Освободить блокировку может только владелец или вызов с `--force`.

| Ресурс | Назначение |
|---|---|
| `release-co-desktop` | выпуск CO Desktop (заменяет ручной `desktop-updates\RELEASE.lock`) |
| `release-bridge` | выпуск Bridge |
| `source-merge` | внутренний ресурс `finish`: TTL 10 мин, ожидание до 120 с; вручную не захватывать |

Внутри `source-merge` команда `finish` проверяет, что основная копия чистая (кроме `desktop.ini`) и стоит на `main`, после чего делает `merge --ff-only`. Если `main` ушёл вперёд, выполняется один повторный rebase.

## Защищённые пути (с 0.6.0)

- Список — файл `protected-paths.txt` в корне репозитория: по шаблону на строку, `#` — комментарий. `dir/` — всё под папкой; шаблон без `/` — имя файла в любой папке (`.htaccess`, `*.pfx`); иначе `fnmatch` по пути от корня (`*` проходит и через `/`); регистр не важен.
- Действуют шаблоны из `main` и из ветки вместе: ветка может добавить строку, но не снять защиту. Сам `protected-paths.txt` защищён всегда.
- `finish` смотрит `git diff main...HEAD` (до кросс-ревью и ещё раз после rebase). Если задет защищённый путь, а `--owner-approved` нет, — код 6, слияния нет; `--force` эту проверку **не** обходит.
- Порядок: показать владельцу список файлов и правку, получить его отдельное «да» (даже после зелёного ревью Codex), повторить `finish ... --owner-approved "<номер сообщения чата / цитата владельца>"`. Без настоящего «да» владельца ключ не ставить.
- Подтверждение пишется в журнал (время, ветка, HEAD, файлы, текст): `%LOCALAPPDATA%\AIWorkbenchWork\reviews\protected-approvals.jsonl` и `transport\devtree-approvals.jsonl` (рядом с папкой блокировок). Поле `protected` (и `ownerApproved`) — в `--json`.

## Сквозная проверка (e2e, с 0.5.0)

- `finish` после кросс-ревью Codex, до rebase и блокировки source-merge, запускает `aiwb-e2e`, если diff `main...HEAD` затрагивает `co-web/`, `co-web-auth/`, `co-agent/`, `bridge/` или `co-desktop/`.
- Правка `co-web`: `npm run build` в worktree, затем прогон с `--dist` (локальная сборка, API прод). Сбой сборки = красный.
- Остальные компоненты: смоук против прод-сайта (`finish` не выпускает Bridge и CO Desktop).
- Красный: слияние остановлено, код 5. `--force`: слить всё равно.
- Не смог запуститься (хост занят, нет токена): `auto` — предупреждение и слияние; `on` — код 5.
- Ключи:
  - `--e2e auto|on|off` (`auto`);
  - `--e2e-host auto|home-main|site` (`auto`: `home-main`, если слот Bridge освободился за `--e2e-queue-wait 120` с, иначе `site`);
  - `--e2e-timeout 1500`;
  - `--e2e-expect REGEX` (проверка красного пути).
- Результат: поле `e2e` в `--json`.
- Прогон идёт вне блокировки source-merge, каждый в своём тестовом чате: параллельные `finish` не ждут друг друга. Типично 60–90 с (со сборкой `co-web`).

## Основная копия из конфига и guard репозитория (с 0.8.0)

- **Какое дерево основное.** Порядок: ключ `--repo`/`--mirror`/`--work-root`/`--locks` > переменные `AIWB_FREEDOM` (или `AIWB_SOURCE`), `AIWB_MIRROR` > файл `%LOCALAPPDATA%\AIWorkbench\devtree.json` (другой путь — `AIWB_DEVTREE_CONFIG`) с полями `repo`, `mirror`, `workRoot`, `locks` > встроенные значения (`C:\AIWorkbench\freedom`, зеркало `G:\My Drive\AI-Workbench\freedom`; до 0.8.1 — `source`). Переключение основной копии (например, на клон публичного репозитория) — правка одного файла, командные строки агентов не меняются.
- **`.devtree.json` в корне репозитория** (необязателен): `{"guard": ["python", "release/freedom_guard.py", "range", "{range}"], "hooksPath": "release/hooks"}`.
  - `guard` — команда проверки (подстановки `{range}` = `base..head`, `{base}`, `{head}`; `python` заменяется на текущий интерпретатор). `finish` запускает её в worktree после rebase на диапазоне `main..HEAD` ветки; `finish`/`sync` — перед `git push` на `origin/main..main`. Ненулевой код — **код 7**, слияния (или отправки) нет; `--force` её **не** обходит: опубликованное в истории публичного репозитория не забрать. Исправлять — переписать коммиты ветки (amend/rebase), они ещё не опубликованы. Поле `guard` в `--json`.
  - `hooksPath` — `start`, `finish` и `sync` ставят `git config core.hooksPath` (например, хук pre-push с той же проверкой на любой ручной `git push`).

## Коды выхода

| Код | Значение |
|---|---|
| 0 | успех |
| 1 | ошибка или неверный вызов |
| 2 | конфликт rebase (rebase отменён, файлы напечатаны) |
| 3 | основная копия грязная или блокировка занята |
| 4 | кросс-ревью Codex: BLOCK (или сбой при `--review on`) |
| 5 | e2e красный или (при `--e2e on`) не запустился; слияние не выполнено |
| 6 | задет защищённый путь (`protected-paths.txt`) без `--owner-approved`; слияние не выполнено |
| 7 | guard репозитория (`.devtree.json`) отказал; слияние или отправка не выполнены (с 0.8.0) |

## Ограничения

- Блокировки атомарны только в пределах одного компьютера. Все агенты, правящие `source`, работают на домашнем ПК; Drive синхронизирует файлы блокировок с задержкой.
- `co-web` правится в `%USERPROFILE%\co-web` (из-за `node_modules`), devtree его не покрывает.
- Сборки идут из worktree, выходные файлы по-прежнему пишутся в `%LOCALAPPDATA%\AIWorkbenchBuild`.
- Агент, правящий прямо в основной копии, блокирует `finish` остальных агентов (код 3), пока не закоммитит свои изменения.

## История

- 0.8.1 — 2026-11-13 — по умолчанию ядро Freedom: основная копия `C:\AIWorkbench\freedom`, зеркало `G:\My Drive\AI-Workbench\freedom` (если нет `devtree.json`); переменная `AIWB_FREEDOM`, `AIWB_SOURCE` читается как старое имя. Архив `C:\AIWorkbench\source` больше не нужен.
- 0.8.0 — 2026-11-13 — основная копия, зеркало, корень worktree и блокировки из `%LOCALAPPDATA%\AIWorkbench\devtree.json` (`AIWB_DEVTREE_CONFIG`), обратная совместимость; `.devtree.json` репозитория: `guard` перед слиянием и перед `git push` (код 7, `--force` не обходит), `hooksPath`. Подготовка разворота разработки Freedom (ядро правится в клоне публичного репозитория). Тесты `test_guard.py` (временные репозитории): конфиг выбирает дерево, guard блокирует слияние и отправку, без `.devtree.json` поведение прежнее.

- 0.7.1 — 2026-11-12 — Freedom: документация без личных путей, имени репозитория владельца и корпоративных имён; код и CLI не изменились.
- 0.6.0 — 2026-11-01 — защищённые пути (идея razzant/claudexor): `protected-paths.txt` в репозитории, `finish` без `--owner-approved` отказывает кодом 6 (и с `--force`), подтверждение пишется в журнал. Проверено в одноразовом клоне: правка `co-desktop/installer/` с `--force` — код 6, с `--owner-approved` — слияние и запись в оба журнала; правка самого списка и `.htaccess` — код 6; обычная правка — слияние.
- 0.5.0 — 2026-10-18 — `finish` запускает сквозную проверку `aiwb-e2e` перед слиянием (ключи `--e2e*`, код 5, поле `e2e` в `--json`); код — корпоративная модель. Проверено в одноразовом клоне: с `--e2e-expect zzz-never-zzz` код 5 и `main` не сдвинулся, без него GREEN и слияние, правка в `docs/` — e2e пропущен.
- 0.4.0 — 2026-10-17 — кросс-ревью: `finish` перед слиянием отдаёт diff `main...HEAD` Codex (`codex exec`, только чтение) (модель `--review-model`, по умолчанию gpt-6-sol; `--review auto|on|off`, auto — только если в diff есть код). Отчёт — `AIWorkbenchWork\reviews\<имя>-<время>.md`; `VERDICT: BLOCK` останавливает слияние (код 4), `--force` — слить всё равно. Сбой или таймаут Codex в режиме auto не мешает слиянию. Код — корпоративная модель.
- 0.3.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-15 — первая версия.
- 0.3.0 — 2026-10-15 — `start` подключает `node_modules` подпроектов (`co-web`) из основной копии junction-ссылкой, `finish`/`drop` снимают ссылку до удаления worktree; сборка и `tsc` CO Web работают прямо в worktree.
- 0.2.0 — 2026-10-15 — основная копия на локальном диске `C:\AIWorkbench\source` с `origin` на GitHub; `finish` отправляет `main` на GitHub и обновляет зеркало на Drive; команда `sync`; ключи `--mirror`, `--no-push`.
- 0.1.1 — 2026-10-15 — `finish` до и после слияния переиндексирует файлы, которые git на Google Drive ошибочно считает изменёнными (неверный размер в индексе, содержимое совпадает с HEAD).

## 0.7.0 - live review model discovery

Before each review, query the authenticated `codex app-server` using `model/list`.
Use the catalog default, or `--review-model` if that preference is available.
Log any replacement of an unavailable preference. Choose a supported reasoning effort.
On `model_not_found`, refresh the catalog and retry once with another available model.
Do not retry authentication/network errors. If discovery fails, preserve the existing
review policy: warn in auto mode, stop in on mode. Never guess a fallback model name.
