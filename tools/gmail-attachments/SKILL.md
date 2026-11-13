---
name: gmail-attachments
description: Download (or list) Gmail attachments over IMAP by a Gmail search query; use when the Gmail connector cannot fetch attachments.
version: 0.2.2
entry: gmail-attachments.py
requires: [keyring]
secrets: [aiwb/gmail / <Gmail address>]
---

# gmail-attachments

Ищет письма в Gmail обычным поисковым запросом Gmail (`from:`, `subject:`, `has:attachment`, `after:` …) и сохраняет вложения в папку. Нужен, потому что Gmail-коннектор claude.ai вложения не скачивает, а браузер агенту на managed-ПК запрещён.

Доступ — IMAP `imap.gmail.com:993` с паролем приложения Google (16 символов). Пароль лежит в keyring: служба `aiwb/gmail`, учётная запись — адрес Gmail (по умолчанию `mail.address` из install.json, иначе e-mail владельца — участника с `role: "owner"` в `members`; install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`). Ищет во «Всей почте» (папка с флагом `\All`, имя не зависит от языка интерфейса). Почту только читает (`readonly`, `BODY.PEEK`), письма не помечаются прочитанными.

## Вызов

```
python gmail-attachments.py login [--account ADDR]
python gmail-attachments.py fetch "<gmail query>" [--out DIR] [--ext pdf docx] [--limit 50] [--list] [--date-prefix] [--account ADDR] [--mailbox FOLDER]
```

- `login` — открывает на рабочем столе окно ввода пароля приложения, проверяет вход в IMAP, сохраняет пароль в keyring и показывает результат в окне (при ошибке — текст ответа Gmail, до трёх попыток). Пользователь создаёт пароль: Google Account → Security → 2-Step Verification включена → App passwords (https://myaccount.google.com/apppasswords).
- `fetch` — выводит JSON: письма (uid, дата, от кого, тема) и вложения (имя, размер, путь). `--list` — ничего не сохранять, только показать. `--limit` — N самых новых писем (0 — все). Существующие файлы не перезаписываются: добавляется ` (2)`.

## Примеры

```
python gmail-attachments.py fetch "from:fod.fgov.be has:attachment after:2025/01/01" --list
python gmail-attachments.py fetch "subject:invoice has:attachment" --ext pdf --date-prefix --out "G:\My Drive\...\Bijlagen"
python gmail-attachments.py fetch "rfc822msgid:<id@mail>" --out .
```

## Ограничения

- Нужны 2-Step Verification и пароль приложения; при Advanced Protection Google пароли приложений не выдаёт. Для Google Workspace IMAP должен быть разрешён в админке.
- Вложения внутри вложенных писем (.eml) сохраняются как отдельные части, если у них есть имя файла; встроенные картинки без имени пропускаются.
- Ссылки на файлы Google Drive в письме — не вложения, их инструмент не скачивает.

## История

- 0.2.2 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (адрес по умолчанию — `mail.address`, иначе e-mail владельца из `members`).
- 0.2.1 — 2026-11-11 — argparse без сокращений длинных ключей (`allow_abbrev=False` у парсера и подкоманд): `--conf` больше не значит `--confirm` (ревью Codex S1-01, этап R2; Bridge 1.14 и сам отказывает таким ключам с сайта).
- 0.1.0 — 2026-10-15 — первая версия (вложения FOD для досье недвижимости).
- 0.1.1 — 2026-10-15 — `login` показывает успех или ошибку входа в окне (под pythonw ошибка терялась), до трёх попыток.
- 0.1.2 — 2026-10-15 — пароль, набранный в русской раскладке, переводится в латиницу (ЙЦУКЕН → QWERTY), пробелы убираются.
- 0.1.3 — 2026-10-15 — исправлен выбор папки «Вся почта» (имя приходило в кавычках, EXAMINE падал с BAD).
- 0.2.0 — 2026-11-02 — встроен read-guard: темы/отправители/имена файлов с признаками prompt injection в JSON оборачиваются как данные. Код: Claude.
