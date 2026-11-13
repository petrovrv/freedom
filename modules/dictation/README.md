# Module dictation (voice input)

The 🎤 button next to the message box. Until CO Web 0.102 it was part of the core (`co-web/src/dictation.ts`,
`voice.ts`, the button in `ChatView.tsx`, `web/agent/transcribe.php`); now the composer only has a slot for input
actions of modules (ext/1 `ui.composer`, below).

| Layer | Here |
|---|---|
| `ui.composer` | `ui/composer.html` + `composer.js` + `composer.css` — button, language (RU/UK/EN/NL/FR/DE, kept per browser), engine choice |
| server (standard module) | `server/ops.php` — user op `dictation_status`; upload `transcribe` (agent/module.php?m=dictation&u=transcribe) forwards the audio to OpenAI, Groq or your own Whisper server |
| data | none; the key stays in `~/co-agent/secrets/transcribe.json` (below) |

`transcribe.json` (0600): one provider `{"provider": "openai"|"groq"|"local", "key", "model"?, "url"?, "prompt"?}` or a
chain `{"providers": [{...}, {...}]}` tried in order while one is unreachable or answers 5xx/429 (a `local` one: any error).
`local` is any OpenAI-compatible `/v1/audio/transcriptions` over HTTPS (`url` required; a bare origin gets
`/v1/audio/transcriptions` appended), e.g. [speaches](https://github.com/speaches-ai/speaches) (faster-whisper) on a
home server behind Tailscale Funnel with its `API_KEY`; default model `deepdml/faster-whisper-large-v3-turbo-ct2`,
connect timeout 5 s so a switched-off server hands over quickly. `prompt` is a vocabulary hint (names, brands) put before
the page's prompt. Example — home server first, Groq as the fallback:

    {"providers": [
      {"provider": "local", "url": "https://homehub.example.ts.net:8443", "key": "...", "prompt": "Colruyt, OpenMC"},
      {"provider": "groq", "key": "gsk_..."}
    ]}

When every service fails the page switches to the browser's recognition for the rest of its life (that recording is lost).

Engines, as before: CO Desktop → Windows voice typing (Win+H) types into the box; a configured speech-to-text key →
record, transcribe on the server, text after the box text; otherwise the browser's speech recognition with the text
appearing as it is spoken.

Install (same deploy step as the new CO Web, so the button never disappears):

    python install/freedom_modules.py install modules/dictation --enable

## ext/1 `ui.composer` and the capabilities of module pages (core: `co-web/src/extBridge.ts`, `ModuleSlot.tsx`, `composerMedia.ts`, `public/ext/ext1.js`)

A module page in the slot `ui.composer` sits in the action row of the message box. A sandboxed frame has an opaque
origin, and browsers refuse it the microphone (`SecurityError: Invalid security origin`), so CO Web captures on the
page's behalf; each capability needs a permission in the manifest and works only in the composer slot:

| Call (page, `ext1.js`) | Permission | What CO Web does |
|---|---|---|
| `coExt.input(text, {interim, done})`, `coExt.focusInput()` | `composer.input` | puts the session text after the box text of the session's start (interim as a tail); `done` ends the session; focus = caret to the end |
| `coExt.audio.start(maxSeconds)` / `stop()` / `cancel()` | `media.audio` | records the microphone; `stop` returns `{file: Blob, filename, type}`; events `audio` `{recording, seconds}` |
| `coExt.speech.start(lang)` / `stop()` / `platform()` | `media.speech` | runs the browser's speech recognition, events `speech` `{final, interim, active, error?}`; `platform()` focuses the box and starts the platform dictation (CO Desktop: Windows voice typing) |
| `coExt.upload(name, blob, filename, fields)` | `server.uploads` lists `name` | POST multipart `file` + fields to `agent/module.php?m=<id>&u=<name>` as the signed-in member; the handler (`'uploads' => [name => fn(ctx)]`) answers JSON |
| `coExt.storage.get(key)` / `set(key, value)` | — | up to 4 KB per key in this browser (`co.ext.<module>.<key>`) |

Context: `ctx.media = {audio, speech, platform}` tells the composer page what this browser can do. Event `sent`: the
message went out (speech recognition is stopped without a last result).
