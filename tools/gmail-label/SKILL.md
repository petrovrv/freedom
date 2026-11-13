---
name: gmail-label
description: Gmail over IMAP — find messages by a Gmail search query (or a named preset from install.json, such as job-echo for automatic job-application acknowledgements) and add a label, mark read, archive; dry run by default. Use to sort mail when the Gmail connector cannot create or set labels.
version: 0.6.2
entry: gmail-label.py
requires: [keyring]
platform: windows
secrets: [aiwb/gmail]
---

# gmail-label

Сортирует почту Gmail по IMAP: ищет письма поисковым запросом Gmail (X-GM-RAW), ставит метку (создаёт её, если нет), помечает прочитанными и убирает из «Входящих». Ничего не удаляет и не отправляет. Нужен, потому что у коннектора Gmail нет прав на метки, а у токена Bridge нет доступа к Gmail.

## Вызов

```
python gmail-label.py [--account ADDR] (--query "<gmail search>" | --preset job-echo [--query "<доп. условия>"])
                      [--label "Jobs/Auto-replies"] [--read] [--archive] [--apply] [--max 500] [--json]
python gmail-label.py --query="<gmail search>" --senders [--max 60000] [--json]
python gmail-label.py --rules rules.json [--query "<доп. условия>"] [--apply] [--samples 3] [--max 5000] [--json]
python gmail-label.py --query "label:Marketing from:shop.be" --unarchive --apply
```

- Без `--apply` — только список найденного (колонки: U = непрочитано, I = во «Входящих»).
- `--account` — по умолчанию `mail.address` из install.json, иначе e-mail владельца (участник с `role: "owner"` в `members`). install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`.
- `--preset NAME` — именованный запрос из install.json `mail.labelPresets` (`{"mail": {"labelPresets": {"job-echo": "<gmail query>"}}}`); в `--rules` — поле `preset`. Встроенных пресетов в коде нет. Пример — `job-echo`: автоматические эхо-ответы на отклики — LinkedIn «your application was sent to / viewed by», ATS «thank you for applying», «bedankt voor je sollicitatie», «Your Job Application:», автоответы на письма-сollicitaties, InMail «Thank you for sending your application». Письма с interview/gesprek/uitnodiging/invitation/unfortunately/helaas в теме исключены.
- `--senders` (только чтение): сводка по доменам отправителей — число писем, первая/последняя дата, имена, адреса, 3 свежие темы; поддомены схлопываются (mail.notion.so → notion.so). Для инвентаризации сервисов, с которыми имеет дело владелец. Запрос, начинающийся с `-`, передавать как `--query=-in:sent`.
- `--rules FILE`: несколько правил за один прогон (одно IMAP-соединение). Формат: `{"base": "<условие ко всем правилам>", "rules": [{"name", "query" | "preset", "label", "read", "archive", "enabled"}]}`; правила идут по порядку, письмо, пойманное ранее, дальше не считается. Вывод — счётчик и `--samples` свежих писем на правило. Пример — `mail-rules.json` проекта, который разбирает почту по расписанию.
- `--unarchive`: вернуть найденное во «Входящие» (отмена `--archive`).
- Пароль приложения IMAP — keyring `aiwb/gmail` / `<account>` (тот же, что у gmail-draft и gmail-attachments).

## Примеры

```
python gmail-label.py --preset job-echo --query "newer_than:7d"
python gmail-label.py --preset job-echo --query "newer_than:7d" --label "Jobs/Auto-replies" --read --archive --apply
python gmail-label.py --query 'label:"Jobs/Auto-replies" is:unread'
```

## Ограничения

- Фильтры Gmail (автоматически для новых писем) по IMAP создать нельзя — нужен веб-интерфейс Gmail или Gmail API со scope gmail.settings.basic. Поэтому запускать регулярно (например, в каждом прогоне поиска вакансий).
- В Gmail-запросе `{}` — это OR, группировка AND — круглые скобки.

## История

- 0.6.2 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (`--account` — `mail.address`, иначе e-mail владельца из `members`; пресеты, в том числе job-echo, — `mail.labelPresets`).
- 0.6.1 — 2026-11-08 — метки с не-ASCII именами (кириллица) кодируются в IMAP modified UTF-7; раньше `--label "Archive/до-2026-10"` падал с UnicodeEncodeError. Код: Claude.
- 0.6.0 — 2026-11-07 — `--rules FILE` (набор правил за один прогон, dry run по умолчанию) и `--unarchive`. Проверено: первый разбор ящика владельца — 4028 писем по 10 правилам (эхо откликов, job alerts, коды, доставка, опросы, реклама, поиски-подписки, DMARC). Код: Claude.

- 0.5.0 — 2026-11-04 — `--body` включает текст письма и Message-ID в JSON; чтение через BODY.PEEK без отметки прочитанным. HTML преобразуется в текст, вложения не выводятся.

- 0.4.0 — 2026-11-03 — режим `--senders` (сводка по доменам отправителей); заголовки с кодировкой unknown-8bit больше не роняют разбор. Код: Claude.

- 0.3.0 — 2026-11-02 — встроен read-guard: темы/отправители с признаками prompt injection в выводе оборачиваются как данные. Код: Claude.
- 0.2.0 — 2026-11-01 — FETCH и STORE пачками по 300 UID вместо одного письма за раз: 890 писем — 30 с вместо > 30 мин. Код: Claude.
- 0.1.0 — 2026-10-30 — первая версия: поиск, метка, прочитано, архив; пресет job-echo. Проверено: 42 эхо-письма откликов владельца помечены `Jobs/Auto-replies`, прочитаны и убраны из «Входящих»; приглашение на собеседование не задето. Код: Claude.
