---
name: voice
description: Voice pipeline of Freedom (module voice) — the hub on the family server (speech to text and text to speech through speaches/piper, short home commands to Home Assistant, the rest to the CO agent of a chosen chat, answer as audio, Cast speakers through HA; music by voice - Spotify/YouTube Music through Music Assistant on the speakers, replies duck the music) and the voice point on a device (wake word with openWakeWord + VAD, Windows/Linux, also headless; always-on listening only after the device owner turns it on); speaker recognition in two levels (wake-word verifier on the device, voiceprints on the hub that route a question to the recognised person's chat, strangers get home commands only); say/ask/check for tests. Use to run or check the voice assistant, speak a text on a home speaker, or set up a voice point.
version: 0.3.10
entry: voice.py
requires: [python 3.10+, hub - standard library (speaker recognition - sherpa-onnx numpy and an ONNX speaker model), point - sounddevice openwakeword onnxruntime scikit-learn (own venv, setup-point)]
platform: any
secrets: [aiwb/voice-hub default (point, say, ask, check - the hub key), hub - files or env named in its config (hub key, speaches key, HA token, CO host key)]
---

# voice

Голосовой конвейер Freedom (модуль `voice`, SPEC §3.8). Тяжёлая работа идёт на сервере семьи, устройство только слушает и говорит.

## Части

| Команда | Где | Что делает |
|---|---|---|
| `hub` | сервер семьи (Docker рядом со speaches) | HTTP API: `/v1/voice/turn` (аудио или текст → STT → HA → агент → TTS), `/speak`, `/transcribe`, `/home`, `/status`, `/health`; клипы для Cast `/a/<id>.mp3` |
| `point` | ПК, мини-ПК, Linux без экрана | слово-пробуждение (openWakeWord), запись до паузы (silero VAD), фраза → хаб, ответ → динамик устройства или Cast |
| `point-config` | устройство | настройки точки; `--always-listen on` включает только владелец устройства (вопрос с подтверждением) |
| `setup-point` | устройство | venv точки `%LOCALAPPDATA%\AIWorkbench\venvs\voice` (Linux `~/.local/share/AIWorkbench/venvs/voice`) |
| `enroll` | устройство | образцы голоса человека: верификатор слова-пробуждения на устройстве (уровень 1) + отпечаток голоса на хабе (уровень 2); `--forget` удаляет |
| `whois` | где угодно | чей голос в WAV (по ключу точки) |
| `say`, `ask`, `check` | где угодно | проверка: сказать текст, один ход из WAV или текста, состояние хаба |

## Вызов

```
python voice.py hub --config /srv/voice/hub.json [--listen 0.0.0.0:8095]
python voice.py setup-point
python voice.py point-config --hub https://<server>/voice --set-key [--wake-word hey_jarvis] [--lang ru] [--speaker living] [--input-device 1]
python voice.py point-config --always-listen on        # только владелец устройства
python voice.py point-config --output-device "Poly Blackwir, MME"   # куда сигналы и ответы (часть имени + API, или номер; "" = системное)
python voice.py point [--once] [--wake alexa]
python voice.py say "Ужин готов" [--speaker living] [--lang ru]
python voice.py ask --text "который час" [--lang ru] [--route auto|home|agent] [--quiet]
python voice.py ask --wav phrase.wav
python voice.py check                                  # статус хаба через его API
python voice.py check --config-hub /srv/voice/hub.json # на сервере
```

## Маршрут фразы

1. STT: короткая фраза (< `shortSeconds`, 7 с) — `sttModelShort` (small, ~1 с на мини-ПК), длинная — `sttModel` (large-v3-turbo, ~5 с). Язык определяет Whisper в пределах `languages`.
2. Home Assistant `conversation/process` на языке фразы. Ответ без ошибки — это команда дома, её ответ озвучивается (0,05–1 с).
3. Любая ошибка HA (`no_intent_match`, `no_valid_targets`, …) и фраза с обращением «агент, …» / «Claude, …» — вопрос агенту CO: хаб вызывает host op модуля `voice_ask` (чат `co.chatId`, автор `co.env`), ждёт `voice_answer` до `waitSeconds` и озвучивает ответ без markdown.
4. TTS: piper-голос языка (`speaches.voices`), формат mp3 или wav; с `speaker` ответ уходит на Cast-динамик через HA `media_player.play_media` (клип по адресу `publicAudioBase`, 10 мин).

`route=client` (CO Web): хаб не ждёт агента, а возвращает `{route: agent, text}` — страница сама отправляет текст в свой чат.

## Музыка (0.3.0)

Нужны Music Assistant и его интеграция в Home Assistant (`music_assistant`): MA сам ищет в Spotify, YouTube Music и других подключённых сервисах и играет на Cast-колонках.

- «поставь / сыграй / play / speel …», а также «включи …» со словом про музыку (песню, плейлист, альбом, радио, Spotify, YouTube) — сразу в музыку, без HA.
- «включи Шостаковича» без такого слова сначала идёт в HA. Если HA ответил `no_intent_match` или `no_valid_targets` и во фразе нет слов про дом (свет, отопление, ворота…), это музыка.
- Что разбирается: тип (`плейлист`, `альбом`, `песню`, `радио X` = radio mode), источник (`из спотифая`, `с ютуб музыки` — поиск только в нём), колонка (`на колонке в гостиной` — по `aliases`, `везде` — все плееры). «что-нибудь спокойное для работы» и «музыку для сна» — поиск плейлиста.
- Управление короткими фразами: пауза/стоп, продолжи, дальше/следующий, назад, громче/тише (шаг `volumeStep`), «что играет».
- После «включи» хаб не говорит в ту же колонку, чтобы не перебить музыку. Ответ на колонку, которая есть в `music.players`, идёт через `music_assistant.play_announcement`: музыка затихает и потом продолжается.

```json
"music": {"players": {"living": "media_player.living_room_left_2"}, "default": "living",
          "aliases": {"гостин": "living", "living": "living", "woonkamer": "living"},
          "volumeStep": 0.1, "announceVolume": null, "configEntryId": ""}
```

`players` — плееры Music Assistant (сущности HA интеграции MA, не сами Cast). В `homeAssistant.speakers` для объявлений поверх музыки ставят их же.

## Конфигурация хаба (`hub.json`, без секретов)

```json
{"listen": "0.0.0.0:8095", "languages": ["ru", "uk", "en", "nl"], "defaultLanguage": "ru",
 "speaches": {"url": "http://127.0.0.1:8000", "key": {"file": "/run/secrets/speaches_key"},
              "sttModelShort": "Systran/faster-whisper-small", "voices": {"ru": "speaches-ai/piper-ru_RU-irina-medium"}},
 "homeAssistant": {"url": "http://127.0.0.1:8123", "token": {"file": "/run/secrets/ha_token"},
                   "speakers": {"living": "media_player.living_room"}},
 "co": {"dbUrl": "https://<site>/agent/db.php", "hostKey": {"file": "/run/secrets/co_host_key"},
        "chatId": "CHAT-...", "waitSeconds": 150},
 "points": {"kitchen": {"key": {"file": "/run/secrets/point_kitchen"}, "env": "<whose questions>",
                        "chat": "CHAT-...", "chats": [], "speaker": "", "shell": false}},
 "publicAudioBase": "http://<server LAN address>:8095",
 "key": {"file": "/run/secrets/hub_key"}}
```

Ключи: `key` — ключ клиента (сервер CO для голосового режима CO Web): распознавание, команды дома, синтез, но никогда не агент. У каждой голосовой точки свой ключ в `points` — с ним хаб спрашивает агента от имени `env` в чате `chat` (запрошенный чат — только из `chats`), без оболочки, пока `shell` не `true`: говорить с точкой может любой рядом. Точки без записи в `points` агента не спрашивают.

Секрет — `{"file": ...}`, `{"env": ...}` или `{"keyring": service}`; переменные `VOICE_HUB_KEY`, `VOICE_SPEACHES_KEY`, `VOICE_HA_TOKEN`, `VOICE_CO_HOST_KEY` важнее. Голоса piper загружаются в speaches один раз: `POST /v1/models/speaches-ai/piper-ru_RU-irina-medium`.

Docker (рядом со speaches; `network_mode: host`, пользователь 65534, только чтение):

```yaml
services:
  voice-hub:
    image: python:3.12-slim
    restart: unless-stopped
    network_mode: host
    user: "65534:65534"
    read_only: true
    command: ["python", "-u", "/app/voice.py", "hub", "--config", "/app/hub.json"]
    volumes: ["./voice.py:/app/voice.py:ro", "./hub.json:/app/hub.json:ro", "./secrets:/run/secrets:ro"]
```

Снаружи — Tailscale Funnel по пути: `tailscale funnel --bg --set-path=/voice http://127.0.0.1:8095` (хаб понимает и `/voice/v1/...`). Сервер CO знает адрес и ключ из `~/co-agent/secrets/voice.json` `{"hub": "https://<server>/voice", "key": "..."}`.

## Голосовая точка

- Индикатор: файл `%LOCALAPPDATA%\AIWorkbench\voice-point.state.json` (`off` / `listening` / `recording` / `thinking` / `speaking` / `error`) и строка в журнале; звуковой сигнал перед записью и после неё.
- Журнал точки: `%LOCALAPPDATA%\AIWorkbench\voice-point.log` (≤ 1 МБ + `.1`): старт, срабатывания слова, отказы проверки голоса, «почти срабатывания» (оценка от `nearMiss` 0,1 до порога), раз в `heartbeatMinutes` (10) пик уровня и лучшая оценка слова. На всякий случай (поток микрофона Windows может «умереть», продолжая отдавать блоки) точка переоткрывает его после `zeroOnlySeconds` (15) одних нулей и для надёжности каждые `reopenMinutes` (10) в ожидании слова; `0` выключает. Bridge хранит stderr службы в `%LOCALAPPDATA%\AIWorkbench\modules\.logs\voice\point.log`.
- Выключение: `point-config --always-listen off` (точка замечает за ~5 с) или файл `voice-point.stop` рядом с индикатором.
- Служба модуля (`provides.services` `point`) идёт под Bridge; пока постоянное прослушивание выключено, она только ждёт переключателя.
- Слова-пробуждения из коробки: `hey_jarvis`, `alexa`, `hey_mycroft`, `hey_rhasspy` или путь к своей модели `.onnx`.
- Колонка (`outputDevice`): пока точка работает, в неё непрерывно пишется тишина (`outputKeepAlive`, по умолчанию включено, если задан `outputDevice`). Bluetooth-колонка рвёт связь через несколько секунд тишины, и каждый следующий звук ждал её ~5 с; с открытым потоком сигнал звучит через ~0,3 с. Выключить: `"outputKeepAlive": false` (поток звука может мешать сну ноутбука).
- «Секунду.»: если хаб не ответил за `fillerAfter` с (4, `0` — выкл.), точка говорит короткую фразу (`fillerText` или по `lang`), чтобы долгий ответ агента не звучал как зависание. Клип озвучивает хаб один раз, он хранится рядом с настройками (`voice-filler-<lang>.wav`).
- Модель агента выбирает сервер (модуль voice): голосовые вопросы к claude идут на быструю модель (`haiku`), а вопрос, начинающийся словами «сложный вопрос» (uk «складне питання», en «hard/complex question», nl «moeilijke vraag»), — на модель последнего набранного хода чата.
- Windows: onnxruntime 1.20.1 (новее требует VC++ runtime 14.40+, иначе «DLL initialization routine failed»).

## Узнавание голоса (0.2.0)

Два уровня, оба выключены, пока никто не записал образцы:

1. **Устройство: верификатор слова-пробуждения.** `enroll --person <id>` записывает 20 раз слово-пробуждение и 8 фраз, а для сравнения берёт то же слово, сказанное синтетическими голосами хаба. По этим записям обучается верификатор openWakeWord (логистическая регрессия на его признаках): `%LOCALAPPDATA%\AIWorkbench\voice-verifier-<wake>.pkl`, ключ `verifier{wake: path}` в `voice-point.json`. После этого точка просыпается только на голоса людей, записанных на этом устройстве: если сырая оценка выше `verifierGate` (0.3), её заменяет вероятность верификатора. Записи остаются только на этом устройстве (`voice-enroll/<id>`): по ним верификатор переобучается, когда добавляют следующего человека. `--forget` удаляет их и отпечаток на хабе.
2. **Хаб: кто говорит.** Хаб отправляет фразы в `/v1/voice/enroll`, считает по ним отпечаток голоса (эмбеддинг sherpa-onnx, по умолчанию WeSpeaker ResNet34) и хранит только среднее число-вектор: `speakerId.dir/<id>.json`, аудио на хабе не остаётся. На каждом ходе точки с `people` хаб сравнивает голос, параллельно с распознаванием речи (~0,05 с). Узнанный человек получает свои `env`, чат и права из `people`. Незнакомый голос (`unknown: "home"`, по умолчанию) управляет только домом, агент отвечает «Голос не узнан». `unknown: "point"` — как раньше, вопрос идёт от имени точки. Текстовые ходы (`ask --text`) голос не проверяют: их делает тот, у кого ключ точки, то есть хозяин устройства.
3. **Проверка слова-пробуждения через хаб** (`wakeCheck: true` в `voice-point.json`, ставит `enroll`). Верификатор openWakeWord различает скорее слово, чем голос, поэтому точка после срабатывания шлёт последние ~2 с на `/v1/voice/whois` и подаёт сигнал, только если лучшая оценка среди людей точки ≥ `wakeCheckThreshold` (сама точка, без порога хаба 0,65) (0.72; по 1 с слова свой голос даёт 0,5–0,9, на ПК хозяина стоит 0.45 — кто говорит, хаб всё равно решает по всей фразе). Хаб недоступен — точка просыпается, голос проверяется по всей фразе.
   Проверяемый кусок — последние ~2,4 с, включая 0,4 с после срабатывания (конец слова); последний сохраняется в `voice-wake-last.wav` рядом с настройками точки (только на устройстве) для подбора порога.

Порог `speakerId.threshold` (0.65) и отрыв от второго человека `margin` (0.05) подбираются по `whois`: свой голос даёт 0,8–0,9, синтетические чужие 0,45–0,7. Записывать и удалять людей можно только ключом точки и только для `people` этой точки. Хозяин устройства запускает запись, человек присутствует и согласен: отпечаток голоса — биометрия. Узнавание по голосу — удобство, а не защита: запись или клон голоса проходят. Поэтому секреты, оплату и отправку писем голосом не разрешать.

```json
"people": {"me": {"env": "<env>", "chat": "CHAT-...", "shell": false}},
"speakerId": {"model": "/app/models/wespeaker_en_voxceleb_resnet34_LM.onnx", "dir": "/app/state/voiceprints", "threshold": 0.65},
"points": {"home-pc": {"key": {"file": "..."}, "env": "<env>", "people": ["me"], "unknown": "home"}}
```

Образ хаба с узнаванием: `FROM python:3.12-slim` + `RUN pip install sherpa-onnx numpy`. Модель (26 МБ): `https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx`. В compose: `./models:/app/models:ro`, `./state:/app/state` (владелец 65534), `mem_limit` 768m.

```
python voice.py enroll --person me --lang ru --gui    # окно Tk поверх остальных (консоль задания агента не видна): Start, затем по подсказкам; итог в voice-enroll/last-result.json
python voice.py enroll --person me --lang ru --gui --pace 1.3   # медленнее: пауза перед сигналом, длиннее окна записи и пауза конца фразы
python voice.py enroll --person me --lang ru --gui --wake-clips 6 --append   # дозапись в других условиях (гарнитура/даль/комната): клипы этой записи добавляются к отпечатку, не заменяют
python voice.py enroll --person me --forget
python voice.py whois phrase.wav
```

## История

- 0.3.7 — 2026-11-14 — `outputKeepAlive` (Bluetooth-колонка не засыпает: сигналы и ответы без 5-секундной задержки), «Секунду.» через `fillerAfter` с ожидания хаба.
- 0.3.6 — 2026-11-14 — сигналы длиннее и громче, с 0,5 с разгона для спящей Bluetooth-колонки.
- 0.3.0 — 2026-11-14 — музыка: маршрут `music` (Music Assistant через HA: play_media/search по имени, источнику, типу, колонке; пауза, дальше, громкость, «что играет»), ответы на колонку MA через play_announcement; в `/turn` route `music`.
- 0.2.0 — 2026-11-14 — узнавание голоса: `enroll` (верификатор слова-пробуждения на устройстве + отпечаток на хабе), `whois`, на хабе `people`/`speakerId`, `/v1/voice/enroll|forget|whois`, незнакомый голос получает только команды дома; `setup-point` ставит scikit-learn и scipy.
- 0.1.1 — 2026-11-14 — по ревью: ключ проверяется до чтения тела, длина и число одновременных запросов ограничены; свой ключ у каждой точки (`points`: чей вопрос, чат, оболочка), ключ клиента агента не зовёт; голосовой режим CO Web (`route=client`) снова проходит через HA; JSON `speak: false`; язык без голоса не роняет синтез; точка без настроек ждёт, а не завершается; причины отказа агента озвучиваются.
- 0.1.0 — 2026-11-14 — первая версия: hub (speaches STT/TTS, HA, агент через voice_ask, Cast), point (openWakeWord + VAD), say/ask/check, setup-point.
