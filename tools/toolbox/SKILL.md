---
name: toolbox
description: Manage this tool library — search for an existing tool, create a new one from the template, validate, security-scan tools (secrets, hosts, remote exec, TLS off, writes), regenerate INDEX.md, commit, archive an unused tool; tools of Freedom modules (install a tool from a module or export folder, module on/off/remove — tools of a switched-off module are refused by the MCP server).
version: 1.7.0
entry: toolbox.py
requires: [git]
secrets: []
---

# toolbox

Обслуживает библиотеку `G:\My Drive\AI-Workbench\tools`. Правила библиотеки — `../README.md`.

## Вызов

Из папки `tools`:

```
python toolbox/toolbox.py list                      # одна строка на инструмент
python toolbox/toolbox.py search pdf merge          # SKILL.md содержит все слова
python toolbox/toolbox.py new outlook-export        # новый инструмент из _template
python toolbox/toolbox.py check                     # проверить frontmatter, entry и безопасность всех инструментов
python toolbox/toolbox.py scan [name ...] [--json] [-v]  # только проверка безопасности (без имён — вся библиотека)
python toolbox/toolbox.py index                     # пересобрать INDEX.md
python toolbox/toolbox.py commit outlook-export -m "Add outlook-export"
python toolbox/toolbox.py archive outlook-export -m "no agent job used it for 60 days"
python toolbox/toolbox.py install gmail-draft --from <folder with gmail-draft/> [--update]   # 1.7.0
python toolbox/toolbox.py module mail-sheets --state enabled|disabled|removed [--tools a,b] [--version V]
python toolbox/toolbox.py modules [--json]
```

`index` и `commit` переписывают `INDEX.md` и блок между `<!-- aiwb-tools-index:begin -->` и `<!-- aiwb-tools-index:end -->` в `%USERPROFILE%\.claude\CLAUDE.md` и (с 1.2.0) в `%USERPROFILE%\.codex\AGENTS.md` (или `%CODEX_HOME%\AGENTS.md`), чтобы каждая сессия Claude и Codex на этом компьютере видела список инструментов. Codex в режиме Chat запускается с `project_doc_max_bytes=0` и `AGENTS.md` не читает; список видит Codex в режиме Cowork.

`commit` коммитит только перечисленные папки и `INDEX.md`; занятый `index.lock` ждёт до 60 с. С 1.5.0 перед коммитом проверяет безопасность этих инструментов: при ошибке (`error`) без исключения коммит не делается, код выхода 1.

`archive` (с 1.6.0) переносит инструмент в `_archive/<имя>` (`git mv`), дописывает в «Историю» его SKILL.md строку `- archived — дата — причина`, пересобирает `INDEX.md` и коммитит перенос; дальше как у `commit` — push, зеркало, сайт. Папки на `_` не видны в `list`, `INDEX.md`, MCP `aiwb-tools` и на сайте. Вернуть: `git mv _archive/<имя> <имя>`, затем `commit <имя>`. Архив предлагает ночной проход по заданиям (CO Web → Waiting for you → Tool library, `cron/tool-learn.php`): принятое предложение выполняет агент-задание, которое перед `archive` проверяет задачи планировщика и `schedules.json`.

## Инструменты модулей Freedom (с 1.7.0)

Модуль Freedom (SPEC §3.6, `install/freedom_modules.py` в исходниках) перечисляет свои инструменты библиотеки в `module.json` → `provides.tools`. Установщик вызывает toolbox сам:

- `install NAME --from DIR` — копирует `DIR/NAME` в библиотеку (папка `tools/` модуля или выгрузки Freedom). Инструмент той же или более новой версии уже есть — ничего не делает (`present`); более старая — заменяет только с `--update`. Копия проходит проверку безопасности; при ошибке не ставится. Коммит не делается (`commit NAME`, если библиотека — git).
- `module ID --state enabled|disabled|removed [--tools a,b]` — записывает, какие инструменты принадлежат модулю и включён ли он. Файл состояния — `tool-modules.json` рядом с `install.json` (`%LOCALAPPDATA%\AIWorkbench\`; `AIWB_TOOL_MODULES`), вне библиотеки: библиотека — git с зеркалом и копией на сайте, а выключатель — факт одной установки. `removed` снимает запись, файлы инструментов остаются.
- Инструмент, которым владеют только выключенные модули, `mcp_server.py` показывает в `tool_list` как «unavailable here: … (module X is off …)», а `tool_run` отказывает с этой причиной. В блок `CLAUDE.md`/`AGENTS.md` он не попадает; `INDEX.md` (в git) по-прежнему перечисляет всю библиотеку. Инструменты, которых нет ни в одном модуле (ядро, свои инструменты владельца), не затрагиваются; без файла состояния всё работает как до 1.7.0.
- Копия библиотеки на сайте (`site-sync`) файла состояния не получает: выключенный модуль на сайте пока не отключает инструмент.
- Тест: `python toolbox/tests/test_modules.py` (временная библиотека; `AIWB_CLAUDE_MD`, `AIWB_AGENTS_MD`, `AIWB_TOOL_MODULES` уводят записи от настоящих файлов).

## Проверка безопасности (с 1.5.0)

Статический разбор кода инструмента (`security.py`, только stdlib): `.py` — через `ast`, `.ps1`, `.js`, `.mjs`, `.ts`, `.sh`, `.cmd`, `.bat` — регулярными выражениями по строкам (строки-комментарии пропускаются). Запуск — `scan`, а также в составе `check` (вся библиотека) и `commit` (коммитируемые инструменты). Ошибка (`error`) даёт код выхода 1, предупреждение (`warn`) только печатается.

| Правило | Уровень | Что ищет |
|---|---|---|
| `secret-literal` | error | ключи и токены в коде: блок PRIVATE KEY, AWS, GitHub, Google (API key, OAuth access/refresh, client secret), Anthropic, OpenAI, Slack, JWT; строка ≥ 12 символов с цифрами и энтропией ≥ 3,3 в переменной, ключе словаря или аргументе с именем password, secret, token, api_key и т. п. (заглушки `<...>`, `xxx`, `example`, `changeme`, пути, URL пропускаются) |
| `exec-remote` | error | `eval`/`exec`/`compile` или `subprocess` от результата `requests`/`urlopen`/`httpx` (и переменных, которым он присвоен); `curl … \| sh`, `iwr … \| iex`, `iex (… DownloadString/iwr)`, `eval(await fetch…)` |
| `tls-off` | error | `verify=False`, `ssl._create_unverified_context`, `CERT_NONE`, `check_hostname=False`, `-SkipCertificateCheck`, `NODE_TLS_REJECT_UNAUTHORIZED=0`, `rejectUnauthorized: false`, `ignoreHTTPSErrors: true`, `--ignore-certificate-errors`, `curl -k/--insecure`, `wget --no-check-certificate` |
| `destructive` | error | `shutil.rmtree`, `rm -rf`, `Remove-Item -Recurse` по буквальному корневому пути (корень диска, `C:\Users[\имя]`, `/home`, `~`, `G:\My Drive[\AI-Workbench]`) |
| `host-not-allowed` | warn | хост в URL-строке кода вне `ALLOWED_HOSTS` (`security.py`: localhost, свой сайт — `site.url` из install.json или `AIWB_SITE_URL` вместе с родительским доменом, плюс список `AIWB_ALLOWED_HOSTS` через запятую; Google, GitHub, Anthropic/claude.ai, OpenAI/chatgpt.com, LinkedIn, WhatsApp, PyPI/npm, пространства имён XML) и вне `allowed_hosts` инструмента; совпадение по суффиксу домена; адреса с подстановкой (`https://{host}`) не оцениваются |
| `write-outside` | warn | запись (`open(..., "w/a/x")`, `write_text/bytes`, `shutil.copy*/move`, `os.replace/rename`, `writeFileSync`, `Out-File`/`Set-Content`) по буквальному абсолютному пути вне `C:\AIWorkbench`, `G:\My Drive\AI-Workbench\{projects,transport,backups,lab}`, `%TEMP%` и `%LOCALAPPDATA%\AIWorkbench*` текущего пользователя; запись в папку самого инструмента (`Path(__file__)…`). Пути из аргументов и переменных окружения не проверяются |
| `shell-injection` | warn | `subprocess.*(shell=True)` и `os.system`/`os.popen` с собранной (не буквальной) командой |
| `dynamic-exec` | warn | `eval`/`exec`/`compile` не буквального кода, `eval(...)`/`new Function` в JS, `Invoke-Expression`/`iex` в PowerShell |
| `unsafe-deserialize` | warn | `pickle`/`marshal`/`dill` `load(s)`, `yaml.load` без `SafeLoader` |
| `exception` | error | исключение в SKILL.md без причины (< 10 символов) или с неизвестным правилом |
| `stale-exception` | warn | исключение, которому не соответствует ни одна находка |

Исключения и разрешённые хосты — во frontmatter `SKILL.md` инструмента (строки с отступом обычный разбор frontmatter пропускает):

```
allowed_hosts: [portal.example.org, api.example.com]
security:
  - rule: write-outside        # id правила
    file: tool.py              # необязательно: glob относительно папки инструмента
    match: .ssh                # необязательно: подстрока строки кода или описания находки
    reason: зачем это нужно и почему безопасно
```

Одно исключение снимает все подходящие находки; `scan -v` показывает и снятые находки с причиной.

## Ограничения

- Frontmatter разбирается построчно (`key: value`), без YAML: значения в одну строку; исключение — блок `security:` (список с отступом).
- Проверка безопасности эвристическая: адреса и пути, собранные во время работы (аргументы, переменные окружения, подстановки), не оцениваются; код вне `.py/.ps1/.js/.mjs/.ts/.sh/.cmd/.bat` (например, JSON, Markdown) не читается.
- Блок в `CLAUDE.md` обновляется, только если маркеры уже есть в файле. В `AGENTS.md` без маркеров блок «Current tools:» с маркерами дописывается в конец один раз.

## История

- 1.7.0 — 2026-11-12 — Freedom F10: инструменты модулей — команды `install --from`, `module --state`, `modules`; `modstate.py` (файл `tool-modules.json` рядом с install.json); `mcp_server.py` отказывает инструментам выключенного модуля, блок CLAUDE.md/AGENTS.md их не показывает; `AIWB_CLAUDE_MD`/`AIWB_AGENTS_MD` для тестов. Без файла состояния поведение прежнее.
- 1.6.2 — 2026-11-12 — Freedom: в коде нет личных имён — свой домен в `ALLOWED_HOSTS` берётся из install.json (`site.url` и родительский домен; `AIWB_SITE_URL`, `AIWB_ALLOWED_HOSTS`), разрешённые папки записи — из `%LOCALAPPDATA%` текущего пользователя, папка hz.py — `AIWB_HZ_DIR` или `~/.ssh` (исключение `write-outside` для неё больше не нужно), Python для hz.py — anaconda, если есть, иначе текущий. Результат `scan` у владельца прежний.
- 1.6.1 — 2026-11-11 — `mcp_server.py`: в `requires` проверяются как Python-модули только голые pip-спецификации (`openpyxl`, `pint>=0.20`); описания программ («node 22+», «Microsoft Edge», «playwright (npm, local)») больше не делают инструмент «недоступным»; запятые внутри скобок и кавычек не режут элемент списка. Повод: S2, `tool_run browser-agent` с Site через relay отвечал «missing python modules: node 22+…».
- 1.6.0 — 2026-11-01 — команда `archive <имя> -m <причина>`: перенос в `_archive/<имя>` с записью в «Историю» и коммитом (для предложений «archive» ночного прохода по заданиям, схема 15).
- 1.5.0 — 2026-11-01 — проверка безопасности инструментов (`security.py`): команда `scan`, `check` проверяет всю библиотеку, `commit` не коммитит инструмент с ошибками; правила secret-literal, exec-remote, tls-off, destructive (error), host-not-allowed, write-outside, shell-injection, dynamic-exec, unsafe-deserialize (warn); исключения `security:` и `allowed_hosts` в SKILL.md. `mcp_server.py` не читает строки frontmatter с отступом. Первый прогон по 35 инструментам: 0 ошибок, 14 предупреждений; 13 сняты `allowed_hosts` (job-boards, aiwb-backup-check, sap-hr), 1 — исключением (файл задания hz.py).
- 1.4.1 — 2026-10-31 — `commit`: перед обновлением зеркала снимает «фантомные» изменения (Google Drive меняет метаданные файлов, git показывал неизменённые файлы как M, и агенты принимали это за чужие правки); при реальных отличиях зеркало не трогается.
- 1.4.0 — 2026-10-17 — MCP-сервер `mcp_server.py` (stdlib, Python 3.9+): инструменты `tool_list`, `tool_help`, `tool_run`; зарегистрирован как `aiwb-tools` у Claude Code и Codex на домашнем ПК и у агента сайта. Команда `site-sync` (и автоматически после `commit`) публикует закоммиченную библиотеку в `~/co-agent/tools` на хостинге. Поле `platform: windows` в SKILL.md помечает инструменты, недоступные на сайте. Код — корпоративная модель.
- 1.3.0 — 2026-10-16 — рабочая копия библиотеки — `C:\AIWorkbench\tools` (локальный диск, `origin` — GitHub); `G:\My Drive\AI-Workbench\tools` — зеркало для запуска. `new`, `check`, `index`, `commit` работают в локальной копии, откуда бы ни был запущен `toolbox.py`; после `commit` зеркало догоняется (`fetch` + `merge --ff-only`). Переменные `AIWB_TOOLS`, `AIWB_TOOLS_MIRROR`.
- 1.1.0 — 2026-10-15 — `commit` отправляет `main` в `origin` (репозиторий библиотеки на GitHub, приватный), перед этим удаляет `desktop.ini` из `.git/refs` и `.git/logs`.
- 1.0.0 — 2026-10-15 — первая версия.
