# Module voice (voice assistant)

Any device with Freedom can be a voice point: it listens, the family server recognises and answers, the device speaks.
Design: SPEC §3.8 of the Freedom project (device roles, voice point protocol).

| Layer | Here |
|---|---|
| hub (family server) | `tools/voice` — `voice.py hub`: speech to text and text to speech through an OpenAI-compatible server ([speaches](https://github.com/speaches-ai/speaches): faster-whisper + piper voices), home commands to Home Assistant (`/api/conversation/process`), the rest to the CO agent of a chat; answers as audio, also on Cast speakers through HA. Standard library only; runs in `python:3.12-slim` next to speaches |
| voice point (device) | `voice.py point`: wake word on the device (openWakeWord + silero VAD), Windows and Linux, also without a screen; service `point` of this module under Bridge. Always-on listening stays off until the owner of the device turns it on (`point-config --always-listen on`) |
| `ui.composer` | `ui/composer.*` — 🗣 Talk in CO Web/PWA: records until a pause, home commands are answered aloud at once, anything else is sent into the chat (`🎙 <text>`) and the agent's answer is read aloud; ↻ keeps the conversation going |
| server | `server/ops.php` — user ops `voice_status`, `voice_speak`; upload `turn` (to the hub); host ops `voice_ask`, `voice_answer` (the hub puts a phrase of a voice point into a chat and reads the answer) |
| context | `context/voice.md` — messages starting with 🎙 get short, plain answers |
| data | none on the server; the hub address and key in `~/co-agent/secrets/voice.json` (0600): `{"hub": "https://<family server>/voice", "key": "..."}` |

ext/1 capabilities the composer page uses (core: `co-web/src/extBridge.ts`, `ModuleSlot.tsx`, `composerMedia.ts`,
`public/ext/ext1.js`; reference `modules/EXT1.md`): `media.audio` with `silenceMs` (stops after a pause),
`media.play` (CO Web plays the answer — a sandboxed frame has no media), `composer.send` (the phrase goes out as the
chat's next message) and `chat.read` in the composer slot (to see the answer arrive).

Phrase route (the hub): short phrase → small Whisper model (~1 s on a mini-PC), long → large-v3-turbo; Home Assistant
first; an HA error or "agent, …" → the agent; reply → piper voice of the phrase's language. Measured on a 20-thread
mini-PC: home command end to end 0.05–0.6 s after recognition, recognition 1–5 s (much more while the CPU is busy with
other heavy work: give speaches CPU priority, e.g. a lower `CPUWeight` of `user.slice`).

Install:

    python install/freedom_modules.py install modules/voice --enable
    # family server: tools/voice/SKILL.md (hub.json, secrets as files, docker compose, Funnel path /voice)
    # each device that should listen: voice.py setup-point; point-config --hub ... --set-key; point-config --always-listen on
