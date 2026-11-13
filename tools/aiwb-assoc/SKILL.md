---
name: aiwb-assoc
description: Associative chat search of AI Workbench (schema 20) — the indexer of protocol vectors (potion-multilingual-128M, static model, on the home PC; hourly task AIWorkbench\AssocIndex) and of the word table the server builds query vectors from, plus the alias dictionary (other names of things — own names suggested by a model, Wikidata labels in ru/en/uk/nl) that widens full-text queries. Use to rebuild or inspect vectors and aliases, or to add an alias by hand; the search itself is aiwb-chat find and CO Web AI search.
version: 0.2.2
entry: aiwb-assoc.py
requires: [keyring, numpy, tokenizers]
platform: windows
secrets: [aiwb/co-push]
allowed_hosts: [huggingface.co, www.wikidata.org]
---

# aiwb-assoc

Поиск по чатам на сервере (`search-lib.php`, схема 20) сливает три списка по рангам (RRF): полнотекстовый поиск по сообщениям, по записям протокола и близость векторов записей протокола. Этот инструмент готовит данные для двух последних частей:

- **векторы** записей протокола: модель `minishlab/potion-multilingual-128M` (статическая, 256 измерений, без нейросети при расчёте), int8 в таблице `vec_protocol`;
- **таблица слов** `vec_words`: для каждого слова сумма сырых векторов его токенов и число токенов. PHP на Hostinger модель не запустит, поэтому вектор запроса сервер собирает сам: сумма по словам запроса / число токенов, нормировка (так модель и считает, mean pooling). Неизвестную словоформу сервер ищет по самому частому слову с тем же префиксом;
- **псевдонимы** `aliases`: группы имён одного предмета. Если в запросе есть одно имя группы, остальные идут в полнотекстовый запрос.

Замер 08.11 (22 золотых вопроса, все чаты окружения владельца, MRR): ключевые слова 0,66 → 0,87, обычный вопрос 0,47 → 0,69, вопрос по-английски 0,29 → 0,48. Отчёт: `report-embeddings.md` в чате замера.

## Вызов

```
python aiwb-assoc.py index [--full-vocab] [--limit N]
python aiwb-assoc.py aliases build [--env ENV] [--top 900] [--model sonnet] [--replace] [--dry-run] [--max-share 0.05] [--min-chats 10]
python aiwb-assoc.py aliases prune [--max-share 0.05] [--min-chats 10] [--dry-run]
python aiwb-assoc.py aliases drop бэкап "google drive" [--undo]
python aiwb-assoc.py aliases list [--env ENV] [--grep слово]
python aiwb-assoc.py aliases add "ONLYOFFICE" "офисный редактор" [--env ENV | --env ""] [--grp key]
python aiwb-assoc.py aliases rm GRP [--env ENV | --env "*"]
python aiwb-assoc.py vec "текст" [--env ENV] [--limit 10]
```

- `index` — новые и изменённые записи протокола получают векторы, векторы удалённых и неактивных записей уходят; новые слова (из протокола сразу, из сообщений при 2+ вхождениях) идут в `vec_words`. Раз в 7 дней (или `--full-vocab`) заново читает тексты всех сообщений для словаря. Первый прогон ~10–15 мин (модель 0,5 ГБ грузится ~10 с, 10 тыс. записей ~10 с, 130 тыс. слов ~1 мин, остальное — передача).
- `aliases build` — кандидаты: слова латиницей и с большой буквы и пары таких слов, встреченные в протоколах 3+ чатов окружения, частые длинные русские слова, имена из досье (people). Модель (Claude через `claude -p`, по умолчанию sonnet) даёт группы имён и английское имя для Wikidata; поиск в Wikidata, модель выбирает объект (отсекает созвездия и однофамильцев), из Wikidata берутся метки и псевдонимы ru/en/uk/nl. Свои группы пишутся с `env` окружения (`own:<имя>`, source model), группы Wikidata — общими (`env=''`, grp = Q-id). Ответы модели и Wikidata кешируются в `%LOCALAPPDATA%\AIWorkbench\aiwb-assoc\aliases-<env>.json`: повторный прогон спрашивает только новые термины. `--replace` удаляет свои группы окружения, которых в этом прогоне нет.
- **Частые имена** (0.2.0): build и prune выкидывают из групп модели и Wikidata имена короче 3 символов и имена из протоколов больше чем max(`--min-chats` 10, `--max-share` 5%) чатов: своего окружения, если в нём 100+ чатов с протоколом, иначе всех окружений вместе (общие группы — всегда по всем). Такое имя («Google Drive», json, «бэкап»), добавленное к запросу, тянет чаты, где оно просто упомянуто. Группы, где осталось меньше двух имён, удаляются; группы пользователя (source user) не трогаются. `--max-share 0` в build выключает правило.
- `aliases prune` — то же правило по всем сохранённым группам всех окружений (после роста протоколов).
- `aliases drop` — ручной стоп-лист `%LOCALAPPDATA%\AIWorkbench\aiwb-assoc\aliases-drop.json`: имена убираются из всех групп сразу и при каждой пересборке; `--undo` снимает (вернёт пересборка окружения).
- `aliases add/rm` — правка вручную (source user). `--env ""` — для всех окружений.
- `vec` — отладка: ближайшие записи протокола к тексту по полной модели (локально, без таблицы слов).

Модель лежит в `%LOCALAPPDATA%\AIWorkbench\models\potion-multilingual-128M` (скачивается с Hugging Face при первом запуске), состояние словаря — `%LOCALAPPDATA%\AIWorkbench\aiwb-assoc\` (`state.json`, `counts.json`).

## Настройки

- Сайт: `AIWB_DB_URL` (полный адрес `db.php`), иначе `site.url` из install.json + `/agent/db.php`.
- Окружение по умолчанию (`--env`): `AIWB_ENV`, иначе `AIWB_OWNER_ENV`, иначе `env` участника с `role: "owner"` в `members` install.json.
- Контакт в User-Agent (Wikidata, Hugging Face): `AIWB_CONTACT_EMAIL`, иначе `contact.email`, иначе `contact.url`, иначе `site.url` из install.json.
- install.json: `AIWB_INSTALL`, иначе `%LOCALAPPDATA%\AIWorkbench\install.json`.

## Серверные операции (db.php, ключ домашнего ПК, тело gzip)

`vec_todo`, `vec_put`, `vec_words`, `vec_texts`, `protocol_texts`, `alias_put`, `alias_list`, `alias_delete`; поиск — `find_chats` (aiwb-chat find) и `search_chats` (CO Web, параметр `query`).

## Ограничения

- Перефраз без общих слов и без псевдонима векторы не находят (замер: MRR 0,20): «офисный редактор» найдёт ONLYOFFICE, только если такая группа есть в `aliases`.
- Вектор запроса из таблицы слов чуть хуже полной модели (английский 0,51 против 0,53 на 22 вопросах), зато сервер обходится без модели.
- Трансформерные модели (e5-large, MiniLM) на процессоре домашнего ПК не досчитались; potion — компромисс по скорости.
- Регистр: слово хранится в нижнем регистре, вектор считается по самой частой форме написания.

## История

- 0.2.2 — 2026-11-13 — тексты без имён членов семьи (экспорт Freedom); поведение не изменилось.
- 0.2.1 — 2026-11-12 — Freedom export: personal values from install.json, neutral docs (сайт — `site.url`, окружение по умолчанию — владелец из `members`, контакт User-Agent — `contact.email`).
- 0.2.0 — 2026-11-09 — отсев частых имён в build, команды `aliases prune` и `aliases drop`; собраны псевдонимы окружений членов семьи. Замер 22 вопросов (MRR, ключевые слова/вопрос/перефраз/англ.): 0,85/0,74/0,20/0,49 → 0,87/0,72/0,20/0,54.
- 0.1.1 — 2026-11-09 — `index --log`; сборщик отбрасывает составные группы («Аня и Маша», «помощь Маше»): одна группа = один предмет.
- 0.1.0 — 2026-11-09 — первая версия: index (векторы протокола + таблица слов), aliases build/list/add/rm (модель + Wikidata), vec.
