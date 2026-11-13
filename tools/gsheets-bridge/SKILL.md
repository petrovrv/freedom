---
name: gsheets-bridge
description: Read and write Google Sheets (tabs list, get range, update range, append rows) with the Bridge Google OAuth token from Credential Manager; use for live registers in Google Sheets when the Sheets connector is not available or a script must write.
version: 0.2.0
entry: gsheets-bridge.py
requires: [requests]
secrets: [Windows Credential Manager AIWorkbench.Bridge.Google (JSON client_id/client_secret/refresh_token, scope drive)]
---

# gsheets-bridge

Чтение и запись Google Sheets через Sheets API v4 токеном Bridge (запись `AIWorkbench.Bridge.Google` в Credential Manager, scope drive). Работает только на домашнем ПК (Windows).

## Вызов

```
python gsheets-bridge.py <spreadsheet id|URL> meta
python gsheets-bridge.py <id> get "'Run log'!A1:F" [--render FORMULA] [-o rows.json]
python gsheets-bridge.py <id> update "'Applications'!A173:W173" rows.json [--raw]
python gsheets-bridge.py <id> append "'Run log'!A:F" rows.json
python gsheets-bridge.py <id> add-sheet "New tab" [--rows 200 --cols 26 --frozen 1]
python gsheets-bridge.py <id> batch requests.json
```

`add-sheet` создаёт вкладку и печатает её `sheetId`. `batch` — сырой `spreadsheets.batchUpdate` (JSON-массив запросов: `repeatCell`, `updateDimensionProperties`, `setBasicFilter` и т. п.) для форматирования.

`rows.json` — JSON-массив строк (массивов). `-` вместо файла — чтение из stdin. По умолчанию `USER_ENTERED` (формулы и даты разбираются как при вводе руками); `--raw` пишет как текст.

## Примеры

```
python gsheets-bridge.py 14qzamnq...BoBE get "'Applications'!A8:W" -o apps.json
echo [["2026-10-20 09:40","...","0"]] | python gsheets-bridge.py 14qzamnq...BoBE append "'Run log'!A:F" -
```

## Ограничения

- После записи перечитайте диапазон `get`, чтобы подтвердить запись.
- `append` ищет конец таблицы сам; если в листе есть пустые разрывы, надёжнее `update` с явным номером строки.
- `update`/`append` пишут только значения; формат — через `batch`.

## История

- 0.1.0 — 2026-10-20 — первая версия (meta/get/update/append).
- 0.2.0 — 2026-10-27 — команды add-sheet и batch (новая вкладка, форматирование).
