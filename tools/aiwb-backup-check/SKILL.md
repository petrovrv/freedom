---
name: aiwb-backup-check
description: Weekly restore test of the nightly CO database dump (backups/db) — restores the newest .sql.gz into a throwaway local MariaDB on 127.0.0.1 (never production), checks dump age < 26 h, CHECK TABLE and row counts vs production stats; push to the owner on failure. Use to verify that DB backups are restorable.
version: 0.1.2
entry: aiwb-backup-check.py
requires: [keyring, portable MariaDB 11.8 (install subcommand)]
secrets: [aiwb/co-push default]
allowed_hosts: [archive.mariadb.org]
---

# aiwb-backup-check

Еженедельная проверка резервных копий БД AI Workbench: свежий дамп восстанавливается во временный экземпляр MariaDB и сверяется с продакшеном.

## Вызов

```
python aiwb-backup-check.py install [--version 11.8.9]
python aiwb-backup-check.py run [--notify] [--json] [--keep] [--no-prod] [--max-age-hours 26] [--tolerance 0.10] [--min-slack 10] [--port 33307]
python aiwb-backup-check.py status
```

## Что проверяет

- Возраст самого нового `backups/db/db-*.sql.gz` (по mtime) ≤ 26 ч.
- Восстановление с rc = 0 в чистый временный MariaDB на `127.0.0.1:33307`: БД `aiwb_restore_test`, каталог данных `%LOCALAPPDATA%\AIWorkbench\backup-check\data`, удаляется после прогона.
- В конце дампа есть строка `Dump completed`.
- `CHECK TABLE`: все таблицы OK.
- Ключевые таблицы `projects`, `chats`, `events`, `jobs`, `kv` существуют и не пусты.
- Число строк против продакшена (`db.php op=stats`): |разница| ≤ max(10, 10 %).

## Безопасность

- Продакшен-БД не затрагивается: учётных данных БД у инструмента нет.
- Обращение к продакшену: только чтение статистики по HTTPS.
- Все клиентские подключения формируются в одном месте с `-h 127.0.0.1 --no-defaults`.

## Результат

- Код выхода: `0` — OK, `1` — проверка не пройдена, `2` — ошибка.
- Лог: `%LOCALAPPDATA%\AIWorkbenchBridge\logs\backup-check.log`.
- С `--notify` при сбое отправляется push через `push.php op=notify` (env владельца, tag `backup-check`), ключ берётся из keyring `aiwb/co-push` / `default`.

## Настройки

- Сайт (`db.php`, `push.php`): `AIWB_SITE_URL`, иначе `site.url` из install.json.
- Окружение для push: `--notify-env`, иначе `AIWB_OWNER_ENV`, иначе `env` участника с `role: "owner"` в `members` install.json.
- Папка дампов: `--backup-dir`, иначе `<AIWB_ROOT или drive.root\drive.workbench из install.json>\backups\db`.
- install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`.

## Расписание

Задача Windows `\AIWorkbench\BackupCheck`: по воскресеньям в 05:00 местного времени (после дампа 03:xx), запуск при первой возможности после пропущенного старта. Выполняется `pythonw` из зеркала на G:, команда `run --notify`.

## Ограничения

- Портативная MariaDB 11.8.9 в `%LOCALAPPDATA%\AIWorkbench\mariadb`: загрузка ~90 МБ, sha256 сверяется с archive.mariadb.org.
- Счётчики строк расходятся в течение дня: продакшен растёт после дампа в 03:00, поэтому запускать проверку вскоре после дампа.
- Локальный сервер MySQL 8 не используется: дампы MariaDB (collation uca1400, строка sandbox) в него не загружаются.

## История

- 0.1.2 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (сайт — `site.url`, push — владелец из `members`, папка дампов — `drive.root`/`drive.workbench`).
- 0.1.1 — 2026-11-01 — `allowed_hosts` для проверки безопасности toolbox 1.5.0: архив MariaDB (`install`; zip сверяется с sha256sums.txt того же архива).
- 0.1.0 — 2026-10-18 — первая версия; первый прогон: копия 18.4 МБ, восстановление 21 с, 10/10 таблиц OK.
