---
name: voice
description: Voice pipeline of Freedom (module voice) — the hub on the family server (speech to text and text to speech through speaches/piper, short home commands to Home Assistant, the rest to the CO agent of a chosen chat, answer as audio, Cast speakers through HA) and the voice point on a device (wake word with openWakeWord + VAD, Windows/Linux, also headless; always-on listening only after the device owner turns it on); say/ask/check for tests. Use to run or check the voice assistant, speak a text on a home speaker, or set up a voice point.
version: 0.1.0
entry: voice.py
requires: [python 3.10+, hub - standard library only, point - sounddevice openwakeword onnxruntime (own venv, setup-point)]
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
| `say`, `ask`, `check` | где угодно | проверка: сказать текст, один ход из WAV или текста, состояние хаба |

## Вызов

```
python voice.py hub --config /srv/voice/hub.json [--listen 0.0.0.0:8095]
python voice.py setup-point
python voice.py point-config --hub https://<server>/voice --set-key [--wake-word hey_jarvis] [--lang ru] [--speaker living] [--input-device 1]
python voice.py point-config --always-listen on        # только владелец устройства
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

## Конфигурация хаба (`hub.json`, без секретов)

```json
{"listen": "0.0.0.0:8095", "languages": ["ru", "uk", "en", "nl"], "defaultLanguage": "ru",
 "speaches": {"url": "http://127.0.0.1:8000", "key": {"file": "/run/secrets/speaches_key"},
              "sttModelShort": "Systran/faster-whisper-small", "voices": {"ru": "speaches-ai/piper-ru_RU-irina-medium"}},
 "homeAssistant": {"url": "http://127.0.0.1:8123", "token": {"file": "/run/secrets/ha_token"},
                   "speakers": {"living": "media_player.living_room"}},
 "co": {"dbUrl": "https://<site>/agent/db.php", "hostKey": {"file": "/run/secrets/co_host_key"},
        "chatId": "CHAT-...", "env": "<owner env>", "waitSeconds": 150},
 "publicAudioBase": "http://<server LAN address>:8095",
 "key": {"file": "/run/secrets/hub_key"}}
```

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
- Выключение: `point-config --always-listen off` (точка замечает за ~5 с) или файл `voice-point.stop` рядом с индикатором.
- Служба модуля (`provides.services` `point`) идёт под Bridge; пока постоянное прослушивание выключено, она только ждёт переключателя.
- Слова-пробуждения из коробки: `hey_jarvis`, `alexa`, `hey_mycroft`, `hey_rhasspy` или путь к своей модели `.onnx`.
- Windows: onnxruntime 1.20.1 (новее требует VC++ runtime 14.40+, иначе «DLL initialization routine failed»).

## История

- 0.1.0 — 2026-11-14 — первая версия: hub (speaches STT/TTS, HA, агент через voice_ask, Cast), point (openWakeWord + VAD), say/ask/check, setup-point.
