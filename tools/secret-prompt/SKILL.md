---
name: secret-prompt
description: Show a small topmost window on the home PC desktop where the user types a password/token; it is saved to keyring (service aiwb/<name>) and never passes through chat, stdout or files. Use when a task needs a secret from the user.
version: 0.3.1
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

Коды выхода: 0 сохранено, 1 ошибка keyring или окна, 2 отмена, 3 истёк `--timeout`, 4 нет значения (`--check`).

## Примеры

```
python secret-prompt.py --service aiwb/co-mysql --account co_aiwb --label "Пароль MySQL-пользователя co_aiwb (панель хостинга → Databases)"
```

Прочитать из кода: `keyring.get_password("aiwb/co-mysql", "co_aiwb")`.

## Ограничения

- Окно появляется на рабочем столе того Windows-сеанса, где запущен агент (домашний ПК, сеанс владельца). С телефона или рабочего ноутбука его не видно.
- Агент ждёт ответа до `--timeout` секунд; задание в это время занято.
- Windows Credential Manager держит не больше 2560 байт (1280 символов). Длиннее — `secret-prompt` сам кладёт кусками: `<account>#1..N` + в `<account>` метка `aiwb-chunked:N`. Читать такие значения: `sys.path.insert(0, r"G:\My Drive\AI-Workbench\tools\secret-prompt"); from aiwb_secret import get_secret` (короткие значения `get_secret` возвращает как есть).
- Python keyring хранит значение в Credential Manager как UTF-16LE (C#-код, читающий его как UTF-8, должен убрать символы `\0`).

## История

- 0.3.1 — 2026-11-12 — Freedom: примеры без личных учётных записей; поведение не изменилось.
- 0.3.0 — 2026-10-28 — длинные секреты (cookie > 1280 символов) хранятся кусками, `aiwb_secret.py` (`get_secret`/`set_secret`); раньше окно падало с `error`.
- 0.2.0 — 2026-10-18 — вставка работает: Ctrl+V при русской раскладке (Tk ловил только латинскую v), Shift+Insert, контекстное меню, кнопка Paste; пробелы по краям обрезаются вместо ошибки.
- 0.1.1 — 2026-10-17 — `platform: windows` (MCP-сервер aiwb-tools не предлагает инструмент на сайте).
- 0.1.0 — 2026-10-15 — первая версия.
