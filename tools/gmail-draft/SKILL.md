---
name: gmail-draft
description: Create a Gmail draft over IMAP (never sends) — new mail or a threaded reply to the newest message matching a Gmail search, with local file attachments up to 25 MB; use when the Gmail connector cannot attach large files, and for EVERY draft that contains a URL (the connector rewrites links, even the signature link, into google.com/url?q=... redirects).
version: 0.2.3
entry: gmail-draft.py
requires: [keyring]
secrets: [aiwb/gmail / <Gmail address>]
---

# gmail-draft

Создаёт черновик в Gmail через IMAP APPEND в папку «Черновики» (флаг `\Drafts`) с вложениями из локальных файлов. Письма не отправляет. Нужен, потому что Gmail-коннектор claude.ai принимает вложения только как base64 в вызове, а это неподъёмно для PDF на несколько МБ.

Ответ: `--reply-to "<gmail query>"` находит самое новое подходящее письмо во «Всей почте», берёт адресата (Reply-To или From), тему с `RE:` и ставит `In-Reply-To`/`References` — Gmail кладёт черновик в ту же цепочку.

Пароль приложения Google — тот же, что у `gmail-attachments`: keyring `aiwb/gmail`, учётная запись — адрес Gmail. Нет записи — `python gmail-attachments.py login`.

## Вызов

```
python gmail-draft.py (--body TEXT | --body-file FILE) [--html]
    [--reply-to "<gmail query>"] [--to A ...] [--cc A ...] [--bcc A ...] [--subject S]
    [--attach PATH[=Имя.pdf] ...] [--replace "<gmail query>"]
    [--from-name NAME] [--account ADDR] [--dry-run]
```

- `--subject` обязателен без `--reply-to`; с ним — заменяет тему ответа.
- `--to` при ответе заменяет автоматического адресата.
- `--attach "C:\x\E0.1.pdf=Keuringsattest elektriciteit.pdf"` — вложить под другим именем.
- `--replace "to:a@example.org"` — после сохранения нового черновика удалить из «Черновиков» все прочие, подходящие под запрос (новый не трогается). Для пересоздания черновика (новое вложение, правка текста), чтобы остался ровно один. Запрос делайте узким: `to:` адресата, при нужде `subject:`. Удалённые перечислены в выводе `replaced`.
- `--account` — по умолчанию `mail.address` из install.json, иначе e-mail владельца (участник с `role: "owner"` в `members`); `--from-name` — по умолчанию `mail.fromName` из install.json (без него в From только адрес). install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`.
- `--dry-run` — собрать письмо и показать сводку, не загружать.
- Вывод — JSON: status, to, cc, subject, in_reply_to, attachments, ответ APPEND, replaced.

## Примеры

```
python gmail-draft.py --reply-to "from:notary@example.org Elektrisch keuringsattest" \
  --body-file body.txt \
  --attach "G:\My Drive\...\1 Elektriciteit\E0.1.pdf=Keuringsattest elektriciteit E0.1.pdf" \
           "G:\My Drive\...\EPC Certificaat.pdf"

python gmail-draft.py --to a@example.org --subject "Documenten" --body "Zie bijlage." --attach plan.pdf --dry-run
```

## Ограничения

- Ссылки сохраняются как есть (в отличие от Gmail-коннектора, который делает из них `google.com/url?q=…`).
- Сумма вложений < 25 МБ (лимит Gmail).
- Подпись Gmail не добавляется — пишите её в теле.
- Тело ответа не цитирует исходное письмо.
- Черновик появляется в Gmail через несколько секунд; проверка — Gmail-коннектор `list_drafts`.

## История

- 0.2.3 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (`--account` — `mail.address`, иначе e-mail владельца из `members`; `--from-name` — `mail.fromName`).
- 0.2.2 — 2026-11-11 — argparse без сокращений длинных ключей (`allow_abbrev=False` у парсера и подкоманд): `--conf` больше не значит `--confirm` (ревью Codex S1-01, этап R2; Bridge 1.14 и сам отказывает таким ключам с сайта).
- 0.2.1 — 2026-11-10 — документация: Gmail-коннектор заворачивает любые ссылки в черновиках (текст, HTML, домен без https) в редирект google.com/url; черновики со ссылками делать только этим инструментом, он пишет MIME как есть.
- 0.2.0 — 2026-10-31 — `--replace`: пересоздание черновика с удалением старого (9 писем с CV под каждую компанию).
- 0.1.0 — 2026-10-20 — первая версия (ответ нотариусу с тремя PDF).
