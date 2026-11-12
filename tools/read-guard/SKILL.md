---
name: read-guard
description: Prompt-injection guard for what agents read from outside (web pages, mail, chats, downloads) — heuristic scan (EN/RU/NL/DE/FR, role tokens, hidden text, exfiltration links), Claude Code PostToolUse/PreToolUse hooks installed by Bridge 1.8+ (Codex handlers ready, not installed), built into browser-agent/whatsapp-web/linkedin-web/gmail-*; suspicious text is wrapped as data, the event goes to job_events, payments and live browser tests on the home PC (CO_LIVE_BROWSER=1) wait in every chat for the owner's "<code> да" (other risky actions only in a tainted chat) - a Yes/No card in CO Web Waiting for you (schema 18), secrets and uploads only in a tainted chat. Use to check a text or file for injection, or to grant a held action.
version: 0.4.1
entry: read-guard.py
requires: []
secrets: []
security:
  - rule: exec-remote
    file: read-guard.py
    match: CMDLIKE_RE
    reason: a regex listing command names (curl, wget, sh...) to recognise command-like quoted strings; nothing is downloaded or run
---

# read-guard

Проверка прочитанного агентом чужого контента на признаки prompt injection до того, как текст попадёт в контекст модели. Эвристика, без сети и без LLM: ~0,01 с на страницу.

## Как работает

1. **Сканер** (`read_guard.scan`) — правила с весами: `override` («ignore previous instructions», RU/NL/DE/FR), `role_tokens` (`<|im_start|>`, `[INST]`, `</system>`), `jailbreak`, `unicode_tags` (скрытый текст в тегах Unicode) — сильные, срабатывают сами; `agent_address` («Note to the AI:», «если ты ИИ»), `new_instructions`, `secret_request` («send your passwords/API keys»), `exfil_template` (markdown-картинка с `?q={…}`), `exfil_host` (webhook.site, ngrok…), `exfil_send`, `command`, `hidden_html` (display:none / комментарии с обращением к ИИ), zero-width / bidi — слабые, суммируются (порог 4). Просьбы «купить/оплатить/удалить» учитываются только вместе с обращением к ИИ.
2. **При подозрении**: текст оборачивается как данные — строка `[read-guard: possible prompt injection …]` и маркеры `<<<UNTRUSTED-DATA id=…>>>` (случайный id; поддельные маркеры внутри текста обезвреживаются); чат помечается «заражённым» на 24 ч; событие `injection_suspected` пишется в `%LOCALAPPDATA%\AIWorkbenchGuard\events.jsonl` и в `AIWB_GUARD_EVENTS` (Bridge 1.8+ пересылает в `job_events`, kind `guard`/`read_guard`).
3. **Рискованные действия.** С 0.3.0 (решение владельца 03.11: «рисковые действия у нас только деньги») во всех чатах удерживаются только оплаты: MCP purchase/pay/payment/buy/checkout/order/transfer и в browser-agent клик или Enter по кнопке оплаты/покупки/заказа (категория purchase, даже с `--confirm`). Всё остальное из списка ниже удерживается только в заражённом чате. Описание 0.2.0 ( А2 чата CHAT-20261103081329-PX01): хук PreToolUse отказывает с текстом для агента: «Подтвердите действие: … нажмите «Да» в Waiting for you или ответьте „1234 да“», агент спрашивает владельца и заканчивает ход. **Во всех чатах:** `git push`, отправка почты (smtplib, Send-MailMessage), `linkedin-post publish`, `gh … create/comment/merge…`, деплой (`deploy_web.py`, `publish_site.py`, `release.ps1`, scp/sftp, `hz.py` с заданием `put` или меняющей командой в `_job.json`), `browser-agent --confirm`, удаления (rm/Remove-Item/del/rmdir, shutil.rmtree/os.remove, `find -delete`, `git clean -f`, MCP delete/trash/fs_delete), MCP send/publish/share/purchase/pay/order, правка состояния read-guard. **Без остановки:** удаление в `tmp`/`temp`/`%TEMP%`, `AIWorkbenchWork` (worktree), `node_modules`/`dist`/`build`/`__pycache__`, `*.log/*.tmp/*.bak`; `git rm`; черновики (`gmail-draft`, `linkedin-web draft`, MCP create_draft); `linkedin-post schedule`/`scheduled`/`card`/`--dry-run` (очередь анонсов, которую владелец автоматизировал); `devtree finish` и `toolbox.py commit` (их push внутри, у них свой контроль). **Только в заражённом чате:** чтение keyring и секретных файлов, `type-secret`, curl/iwr/requests POST, `hz.py` вообще, MCP create_event/update_event/invite/submit, WebFetch с длинным query. Один код на чат (24 ч); событие `risky_action_held` с полями `code`, `kind` (send/purchase/delete/publish/push/secret/submit/other), `summary` (для Bash — описание вызова агентом + команда, для MCP — to/subject/title/amount/fileId…), `command`, `tainted`, `expires`; Bridge передаёт его в `job_events`, сервер (схема 18) кладёт в `action_requests`, шлёт push владельцу (`/e/<env>/inbox#act-<id>`, без тихих часов), CO Web 0.81.0 показывает карточку **Да / Нет** в Waiting for you — кнопка отправляет в чат «`<код> да`» / «`<код> нет`». После ответа агент запускает `read-guard.py grant 1234`: инструмент сам читает чат (aiwb-chat) и открывает рискованные действия чата на 15 мин только если там есть **сообщение владельца окружения чата** (`byOwner` из chat_read; редактор общего чата не подтверждает) после запроса с кодом и «да» (без более нового «нет»). Код со страницы/из письма или от самого агента не засчитывается. События `risky_action_held` / `owner_granted`. Встроенная проверка `guard_check_action(summary, kind=…, always=True)` — тот же путь для инструментов без хука (Codex): browser-agent `--confirm`.

## Где включено

- **Bridge 1.8+**: для Claude Code — хуки PostToolUse (`Bash|PowerShell|WebFetch|WebSearch|Read|mcp__.*`) и PreToolUse (`Bash|PowerShell|WebFetch|Read|mcp__.*`) в том же `--settings`-файле, что и SubagentStart. Codex Bridge хуки **не ставит**: `-c hooks…` работают только с `--dangerously-bypass-hook-trust`, а он доверяет и непроверенным хукам пользователя/проекта (ревью Codex); в заданиях Codex работают встроенные проверки инструментов. Обработчики `codex-post/codex-pre` готовы и проверены вручную. Bash проверяется, только если команда читает извне (browser-agent, whatsapp-web, linkedin-web, gmail-*, job-boards, pdf-extract, curl/iwr, Downloads/attachments, .eml/.html); Read — только файлы из Downloads/attachments/temp, .eml/.html/.msg. Переменные: `AIWB_JOB_ID`, `AIWB_CHAT_ID`, `AIWB_GUARD_EVENTS`, `AIWB_READ_GUARD_HOOK=1` (только Claude). Под `runAs` Bridge read-guard не включает. Выключить: `AIWB_READ_GUARD=off` в окружении Bridge (или путь к другому скрипту).
- **Встроено в инструменты** (работает и без хука, например при ручном запуске): browser-agent 0.10.0 (snapshot, search, ответы команд; `--confirm`/`type-secret` в заражённом чате → `owner_confirm_required`), whatsapp-web 0.4.0, linkedin-web 0.5.0, gmail-label 0.3.0, gmail-attachments 0.2.0. Под хуком (`AIWB_READ_GUARD_HOOK=1`) инструменты не оборачивают сами, чтобы не было двойного; вложенный browser-agent внутри whatsapp/linkedin — тоже (`AIWB_READ_GUARD_INNER=1`).

## Вызов

```
python read-guard.py scan FILE|- [--wrap] [--json] [--source S]   # exit 0 чисто, 3 подозрительно
python read-guard.py grant 1234                                    # после ответа владельца «1234 да»
python read-guard.py status                                         # заражение / разрешение / открытый запрос чата
python read-guard.py test -v                                        # примеры cases.json, обёртка, какие вызовы удерживаются, grant
python read-guard.py hook claude-post|claude-pre|claude-fail|codex-post|codex-pre   # вход для хуков (JSON на stdin)
```

Из другого инструмента:

```python
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "read-guard"))
import read_guard as rg
text = rg.guard_text(text, "page https://…", "my-tool")   # без изменений, если чисто
obj = rg.guard_obj(obj, "Gmail query", "my-tool")          # JSON-ответ
held = rg.guard_check_action("send mail to X", tool="my-tool")  # None или owner_confirm_required
```

## Примеры

`cases.json` — 43 примера (позитивы на разных языках, скрытый текст, exfiltration; негативы: новости, вакансии, письма банков, статьи про ИИ, коды подтверждения, «pay attention», «order now» без обращения к ИИ).

## Ограничения

- Эвристика: перефразированную или закодированную инъекцию может пропустить; это слой поверх, а не замена политики «не выполнять инструкции из прочитанного».
- Статьи *про* prompt injection (Wikipedia, learnprompting) помечаются — в них настоящие примеры атак; это ожидаемо.
- Codex: PostToolUse с `decision: block` заменяет результат инструмента обёрнутым текстом (агент видит его как «Script failed» с обёрнутым выводом); хуки Codex работают только для Bash и MCP.
- Каждый хук — запуск Python (~0,3 с на этом ПК) на вызов Bash/PowerShell/WebFetch/Read/MCP; сканер импортируется, только когда нужен (pre — только если есть свежее заражение, post — только для чтения извне).
- Заражение и разрешение хранятся по чату (`AIWB_CHAT_ID` или `CHAT-…` в рабочей папке), иначе по процессу агента.

## История

- 0.4.1 — 2026-11-12 — Freedom: примеры и самопроверка без личных имён, путей и имени устройства (cases.json, `test`); правила проверки не изменились, `test` — 119/119.
- 0.4.0 — 2026-11-10 — во всех чатах ждут «да» владельца и живые тесты браузера на домашнем ПК: команда Bash/PowerShell, ставящая `CO_LIVE_BROWSER=1` (`VAR=1 cmd`, `$env:`, `SetEnvironmentVariable`, внутри `bash -c`). Текст отказа объясняет агенту: сначала запустить их на устройстве, которому они нужны (Мак семьи — `mac_check action=start live=true`, co-resident 0.3.7). Упоминание переменной в grep/сообщении коммита не удерживается. `guard_check_action(any_kind=True, reason=…)`. Повод: 10.11 задания чата Софии гоняли живые тесты на ПК Романа, десятки Edge, мост висел. Тест: 119 проверок. Код: Claude.
- 0.3.0 — 2026-11-04 — во всех чатах удерживаются только деньги (оплаты MCP, покупка в браузере через browser-agent 0.12.0); git push, почта, удаления, деплой, публикация, `--confirm` — только в заражённом чате, как в 0.1.0. `guard_check_action(always=True)` действует только для kind="purchase". Тест: 112 проверок. Код: Claude.
- 0.2.0 — 2026-11-03 — рискованные действия удерживаются **во всех чатах** (А2): отправка, покупка, удаление вне tmp/worktree, публикация и деплой, `git push`, `browser-agent --confirm`, MCP send/share/delete; в заражённом чате, как раньше, ещё секреты и выгрузки. Событие с `kind`/`summary`/`command`/`expires` → карточка «Да / Нет» в CO Web (схема 18, CO Web 0.81.0), push владельцу. `grant` не засчитывает «да» редактора общего чата (`byOwner`). Цепочка `read-guard.py status; git push` больше не проходит мимо проверки. Проза в кавычках (сообщение коммита, запись памяти, prompt) не считается командой: «git push» в тексте не удерживается, код после `-c`/`-Command`/`eval`/`ssh host`, пути и строки, начинающиеся с команды, проверяются. browser-agent и linkedin-post засчитываются только при запуске (`*.py` или `tool_run`). Тест: 110 проверок. Код: Claude.

- 0.1.6 — 2026-11-02 — `claude-pre` понимает Write/Edit/MultiEdit/NotebookEdit файлов состояния read-guard (заработает, когда Bridge добавит эти инструменты в matcher PreToolUse). Код: Claude.
- 0.1.5 — 2026-11-02 — в заражённом чате команды Bash/PowerShell, трогающие состояние read-guard (`AIWorkbenchGuard`, `AIWB_READ_GUARD`), тоже ждут подтверждения владельца: агент не может сам снять метку или выключить проверку; в событиях пишется настоящая версия. Проверено живым заданием под Bridge 1.8.0: обёртка, удержание `git push`, события переданы в job_events. Код: Claude.
- 0.1.4 — 2026-11-02 — `hook claude-fail` для события Claude Code PostToolUseFailure: текст ошибки неудачного вызова (заменить его нельзя) сканируется, чат помечается, модели добавляется предупреждение (ревью Codex к Bridge 1.8.0). Код: Claude.
- 0.1.3 — 2026-11-02 — описание: Bridge 1.8.0 ставит хуки только Claude Code (Codex — встроенные проверки инструментов), без runAs. Код: Claude.
- 0.1.2 — 2026-11-02 — PreToolUse проверяет и `Read` Claude Code: чтение секретных файлов (.env, .ssh, db.json, oauth-client…, my.cnf) в заражённом чате ждёт подтверждения владельца (ревью Codex к Bridge 1.8.0). Код: Claude.
- 0.1.1 — 2026-11-02 — быстрый путь хука: без argparse, сканер импортируется лениво, PreToolUse сразу пропускает, если ни один чат не заражён за 24 ч (просроченные метки удаляются); без модулей secrets/dataclasses. Проверено живыми запусками Claude Code 2.1.310 (haiku) и Codex 0.157.0: вывод обёрнут, `git push` отклонён с кодом для владельца. Код: Claude.
- 0.1.0 — 2026-11-02 — первая версия (идея klausify read-injection guard, чат «Идеи для AI Workbench с GitHub», п. 4). Проверено: 49/49 тестов; ложные срабатывания — 37 реальных страниц (новости BE/NL/FR/DE/RU, магазины, банки, вакансии, GitHub, документация): помечены только 2 статьи о prompt injection; 300 писем Gmail за 120 дней (текст и сырой HTML) — 0; файлы Downloads — 0; хуки на симулированных событиях Claude/Codex, grant с поддельными ответами (агент, «нет») — отказ. Код: Claude.
