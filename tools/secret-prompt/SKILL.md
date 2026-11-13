---
name: secret-prompt
description: Show a small topmost window on the home PC desktop where the user types a password/token; it is saved to keyring (service aiwb/<name>) and never passes through chat, stdout or files. Use when a task needs a secret from the user. The home PC keyring is the owner's: a household member's job is refused (own_device_first; aiwb_secret 0.4.0 is the one place that decides, Bridge 1.18+ also routes all python keyring calls of such a job to it).
version: 0.4.0
entry: secret-prompt.py
requires: [keyring]
platform: windows
secrets: []
---

# secret-prompt

Окно ввода секрета на рабочем столе домашнего ПК. Значение сразу уходит в keyring (Windows Credential Manager), в чат, вывод и файлы не попадает. Агент получает только «saved … (N chars)».

## Вызов

```
python secret-prompt.py --service aiwb/<service> [--account <login>] [--label "<текст>"] [--confirm] [--timeout 600]
python secret-prompt.py --service aiwb/<service> [--account <login>] --check
```

`--check` — без окна: `present` / `missing` (коды 0 / 4). `--overwrite` — не показывать пометку «значение уже есть».

Вставка: Ctrl+V в любой раскладке (по коду клавиши), Shift+Insert, правая кнопка мыши → Paste, кнопка Paste. Пробелы и перевод строки по краям обрезаются. Копирование и вырезание из поля отключены.

Коды выхода: 0 сохранено, 1 ошибка keyring или окна, 2 отмена, 3 истёк `--timeout`, 4 нет значения (`--check`), 5 отказ: задание члена семьи (`own_device_first`).

## Примеры

```
python secret-prompt.py --service aiwb/co-mysql --account co_aiwb --label "Пароль MySQL-пользователя co_aiwb (панель хостинга → Databases)"
```

Прочитать из кода: `keyring.get_password("aiwb/co-mysql", "co_aiwb")`.

## Задания членов семьи: отказ (с 0.4.0)

Связка ключей домашнего ПК (Windows Credential Manager) принадлежит хозяину. Задание члена семьи её не читает и не пишет — правило ядра Freedom «у каждого своя связка ключей».

- Роль задания определяет `aiwb_secret.job_role()`: `AIWB_JOB_ROLE` от Bridge 1.18+ (`owner`/`member`); без неё (и только внутри задания агента: задана `AIWB_JOB_ENV`, `AIWB_JOB_ID` или `AIWB_CHAT_ID`) задание считается заданием члена семьи, если его окружение (`AIWB_JOB_ENV`, `project.json` рабочей папки или чата `AIWB_CHAT_ID`) известно и не совпадает с окружением хозяина (`members[].role == "owner"` в install.json). Это тот же способ, что у browser-agent `own_device_first`. В остальных случаях задание считается заданием хозяина.
- Для `member` функции `get_secret`/`set_secret`/`guard()` бросают `MemberRefused` (подкласс `keyring.errors.KeyringError`, код `own_device_first`, текст с подсказкой). `secret-prompt.py` в этом случае окно не показывает и завершается с кодом 5.
- Инструменты, которые вызывают `keyring` напрямую (gmail-*, 2dehands, aiwb-chat, aiwb-dossier, aiwb-subtask, linkedin-web, browser-agent `type-secret`…), получают тот же отказ без правки кода. Для задания члена семьи Bridge 1.18+ ставит `PYTHON_KEYRING_BACKEND=aiwb_secret.MemberRefusedKeyring` и добавляет в `PYTHONPATH` эту папку и `member_site` (там `sitecustomize.py` заново ставит бэкенд в каждом python задания: Codex убирает из окружения своей оболочки переменные с KEY/SECRET/TOKEN в имени). Если модуль не найден, keyring не загрузится и тоже откажет. У бэкенда priority −1: у процессов хозяина, которые импортируют `aiwb_secret`, keyring по-прежнему выбирает Windows Credential Manager (с priority 1 он попадал бы в chainer, и отсутствующий секрет хозяина давал бы отказ — найдено ревью).
- Где вводить секрет ребёнка: инструмент своего устройства `secret_prompt` (`mcp__dev_<device>__secret_prompt`; Mac — CO Resident 0.5.2+), в песочнице — `aiwb-secret` (wsl-family-sandbox 0.2.0+).
- Это барьер, а не изоляция: задание всё ещё идёт под учётной записью Windows хозяина. Следующий шаг описан в SPEC Freedom (правило «у каждого своя связка ключей»).
- Тесты: `python test_aiwb_secret.py` (роль owner — чтение и запись как раньше; member — отказ, отказ `secret-prompt.py` с кодом 5, отказ любого прямого вызова keyring через бэкенд и через `member_site` без переменной; импорт `aiwb_secret` не меняет выбор keyring у хозяина; настоящий keyring не трогается; 7 тестов).

## На Mac члена семьи (CO Resident 0.5.2+)

Если секрет нужен инструментам, которые работают на Mac члена семьи, это окно не использовать: секреты Mac хранятся в его собственной связке ключей (login Keychain), на домашний ПК не переносятся. Нужно вызвать устройственный инструмент Mac `secret_prompt`:

```
secret_prompt  name=gmail-imap  account=<login>  label="Пароль приложения Gmail (myaccount.google.com → App passwords)"
```

- Человек за Mac видит окно с полем ввода, где символы скрыты. Значение сохраняется в Keychain как service `aiwb/<name>`, account `<login>` (по умолчанию `default`). В ответ агент получает только `saved` / `cancelled` / `timeout` / `waiting`.
- Ответ `waiting` значит, что окно открыто (держится до 10 мин). Состояние проверять через `secret_list name=<name>`; `secret_prompt` повторно не вызывать.
- Инструменты, запущенные **на Mac**, читают значение так же, как на ПК: `keyring.get_password("aiwb/<name>", "<login>")`. При первом чтении macOS один раз спросит разрешение: нажать «Всегда разрешать». Для ASCII-значений без запроса работает `security find-generic-password -s aiwb/<name> -a <login> -w`.
- `secret_delete name=<name> account=<login>` удаляет запись; каждый вызов подтверждается на Mac.

## Ограничения

- Окно появляется на рабочем столе того Windows-сеанса, где запущен агент (домашний ПК, сеанс владельца). С телефона или рабочего ноутбука его не видно.
- Агент ждёт ответа до `--timeout` секунд; задание в это время занято.
- Windows Credential Manager держит не больше 2560 байт (1280 символов). Длиннее — `secret-prompt` сам кладёт кусками: `<account>#1..N` + в `<account>` метка `aiwb-chunked:N`. Читать такие значения: `sys.path.insert(0, r"G:\My Drive\AI-Workbench\tools\secret-prompt"); from aiwb_secret import get_secret` (короткие значения `get_secret` возвращает как есть).
- Python keyring хранит значение в Credential Manager как UTF-16LE (C#-код, читающий его как UTF-8, должен убрать символы `\0`).

## История

- 0.4.0 — 2026-11-13 — у каждого своя связка ключей: `aiwb_secret.job_role()`/`guard()`/`MemberRefused` (`own_device_first`) и бэкенд `MemberRefusedKeyring` для Bridge 1.18+; `get_secret`/`set_secret` и `secret-prompt.py` (код 5) отказывают заданию члена семьи; у хозяина всё работает как раньше. Тест `test_aiwb_secret.py`. Решение владельца 13.11 (чат QCJE).

- 0.3.2 — 2026-11-13 — раздел «На Mac члена семьи»: на Mac секреты запрашивает устройственный инструмент `secret_prompt` CO Resident 0.5.2 (своя связка ключей Mac), а не это окно; код не изменился.
- 0.3.1 — 2026-11-12 — Freedom: примеры без личных учётных записей; поведение не изменилось.
- 0.3.0 — 2026-10-28 — длинные секреты (cookie > 1280 символов) хранятся кусками, `aiwb_secret.py` (`get_secret`/`set_secret`); раньше окно падало с `error`.
- 0.2.0 — 2026-10-18 — вставка работает: Ctrl+V при русской раскладке (Tk ловил только латинскую v), Shift+Insert, контекстное меню, кнопка Paste; пробелы по краям обрезаются вместо ошибки.
- 0.1.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-15 — первая версия.
