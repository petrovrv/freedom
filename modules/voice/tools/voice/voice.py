# SPDX-License-Identifier: AGPL-3.0-or-later
"""voice - the voice pipeline of Freedom (module voice, SPEC §3.8).

  hub    the family server part: HTTP API for voice turns. Speech to text and text to speech go to an OpenAI-compatible
         server (speaches: faster-whisper + piper), short home commands to Home Assistant (conversation API), the rest
         to the CO agent of a chosen chat (host op voice_ask of the module's server code). Standard library only.
  point  a voice point on a device (Windows, Linux, also headless): wake word on the device (openWakeWord + its silero
         VAD), the phrase goes to the hub, the answer plays on the device's speaker or on a Cast speaker through HA.
         Always-on listening is off until the device's owner turns it on (point-config --always-listen on).
  enroll record a person's voice: a wake-word verifier on the device (level 1) and a voiceprint on the hub (level 2,
         sherpa-onnx speaker embeddings) that routes the person's questions to their own chat; strangers get home only.
  whois  whose voice is in a WAV file.
  say    speak a text through the hub (on this device or on a Cast speaker) - a check and a tool for agents.
  ask    one turn with a recorded WAV file or a text (no microphone) - a check.
  check  is the hub configured and are speaches / HA / CO reachable.

Secrets never live in files of this tool: the hub reads them from environment variables or files named in its config
(a Docker secret, a systemd credential), the point and say from keyring aiwb/voice-hub (or VOICE_HUB_KEY).
"""
import argparse
import base64
import gzip
import hmac
import io
import json
import os
import platform
import re
import secrets
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = "0.2.0"
UA = f"freedom-voice/{VERSION}"
LANGS = ("ru", "uk", "en", "nl", "fr", "de")
WHISPER_NAMES = {"russian": "ru", "ukrainian": "uk", "english": "en", "dutch": "nl", "flemish": "nl", "french": "fr",
                 "german": "de"}
DEFAULT_VOICES = {
    "ru": "speaches-ai/piper-ru_RU-irina-medium",
    "uk": "speaches-ai/piper-uk_UA-ukrainian_tts-medium",
    "en": "speaches-ai/piper-en_US-amy-medium",
    "nl": "speaches-ai/piper-nl_BE-nathalie-medium",
}
# what the hub says itself when something fails (the point has no screen)
SAY = {
    "ru": {"busy": "Агент ещё отвечает на прошлый вопрос.", "agent_off": "Агент не настроен.", "agent_wait": "Агент не ответил вовремя.", "fail": "Не получилось.",
           "unheard": "Не расслышала.", "stranger": "Голос не узнан. Могу только управлять домом."},
    "uk": {"busy": "Агент ще відповідає на попереднє питання.", "agent_off": "Агента не налаштовано.", "agent_wait": "Агент не відповів вчасно.", "fail": "Не вийшло.",
           "unheard": "Не розчула.", "stranger": "Голос не впізнано. Можу лише керувати домом."},
    "en": {"busy": "The agent is still answering the last question.", "agent_off": "The agent is not set up.", "agent_wait": "The agent did not answer in time.",
           "fail": "That did not work.", "unheard": "I did not catch that.", "stranger": "I do not know this voice. I can only control the home."},
    "nl": {"busy": "De agent beantwoordt nog de vorige vraag.", "agent_off": "De agent is niet ingesteld.", "agent_wait": "De agent antwoordde niet op tijd.",
           "fail": "Dat lukte niet.", "unheard": "Ik heb het niet verstaan.", "stranger": "Ik ken deze stem niet. Ik kan alleen het huis bedienen."},
}
# Any HA error goes to the agent: no_intent_match (not a home phrase) but also no_valid_targets and the like (HA
# matched a sentence pattern by chance: "сколько будет семь умножить на восемь" -> "no object ..."); the agent can
# still answer and can reach HA itself. homeAssistant.keepErrors lists codes HA should answer anyway.
HA_KEEP_ERRORS = set()
AGENT_PREFIX = re.compile(r"^\s*(агент|агенте|клод|claude|agent|асистент|assistant)[\s,:.!-]+", re.I)


def say(lang, key):
    return SAY.get(lang, SAY["en"])[key]


# ------------------------------------------------------------------ config and secrets
def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def secret(spec, env_name=None):
    """A secret from an environment variable, a file (Docker secret, systemd credential) or keyring - never inline."""
    if env_name and os.environ.get(env_name):
        return os.environ[env_name].strip()
    if not spec:
        return ""
    if isinstance(spec, dict):
        if spec.get("env") and os.environ.get(spec["env"]):
            return os.environ[spec["env"]].strip()
        if spec.get("file"):
            p = os.path.expandvars(os.path.expanduser(spec["file"]))
            if os.path.isfile(p):
                with open(p, encoding="utf-8") as f:
                    return f.read().strip()
        if spec.get("keyring"):
            try:
                import keyring
                return (keyring.get_password(spec["keyring"], spec.get("account", "default")) or "").strip()
            except Exception:
                return ""
    return ""


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, file=sys.stderr, flush=True)


# ------------------------------------------------------------------ HTTP helpers
def http(method, url, body=None, headers=None, timeout=30):
    req = urllib.request.Request(url, data=body, method=method, headers={"User-Agent": UA, **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.headers.get("Content-Type", ""), r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.headers.get("Content-Type", ""), e.read()


def multipart(fields, files):
    """fields {name: str}, files {name: (filename, bytes, type)} -> (body, content type)."""
    b = "----freedomvoice" + secrets.token_hex(8)
    out = io.BytesIO()
    for k, v in fields.items():
        out.write(f'--{b}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode())
    for k, (fn, data, typ) in files.items():
        out.write(f'--{b}\r\nContent-Disposition: form-data; name="{k}"; filename="{fn}"\r\n'
                  f'Content-Type: {typ}\r\n\r\n'.encode())
        out.write(data)
        out.write(b"\r\n")
    out.write(f"--{b}--\r\n".encode())
    return out.getvalue(), f"multipart/form-data; boundary={b}"


def parse_multipart(body, ctype):
    """Minimal multipart/form-data reader -> ({field: str}, {field: (filename, bytes, type)})."""
    m = re.search(r'boundary="?([^";]+)"?', ctype)
    if not m:
        raise ValueError("no boundary")
    sep = b"--" + m.group(1).encode()
    fields, files = {}, {}
    for part in body.split(sep)[1:]:
        if part.startswith(b"--"):
            break
        head, _, data = part.partition(b"\r\n\r\n")
        data = data[:-2] if data.endswith(b"\r\n") else data
        h = head.decode("utf-8", "replace")
        name = re.search(r'name="([^"]*)"', h)
        if not name:
            continue
        fn = re.search(r'filename="([^"]*)"', h)
        typ = re.search(r"Content-Type:\s*([^\r\n]+)", h, re.I)
        if fn:
            files[name.group(1)] = (fn.group(1), data, typ.group(1).strip() if typ else "application/octet-stream")
        else:
            fields[name.group(1)] = data.decode("utf-8", "replace")
    return fields, files


def norm_lang(code, allowed, default):
    c = (code or "").strip().lower()
    c = WHISPER_NAMES.get(c, c)[:2]
    return c if c in allowed else default


def audio_seconds(data, typ=""):
    """Length of a recording: exact for WAV, estimated from the size for compressed audio (opus/aac ~32 kbit/s)."""
    if data[:4] == b"RIFF":
        try:
            with wave.open(io.BytesIO(data)) as w:
                return w.getnframes() / float(w.getframerate() or 16000)
        except (wave.Error, EOFError):
            pass
    return len(data) / 4000.0


def wav_bytes(pcm16, rate=16000):
    out = io.BytesIO()
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm16)
    return out.getvalue()


def wav_samples(data):
    """WAV bytes -> (rate, float32 mono samples in -1..1). 16-bit PCM only (what points and piper produce)."""
    import numpy as np
    with wave.open(io.BytesIO(data)) as w:
        if w.getsampwidth() != 2:
            raise ValueError("16-bit WAV only")
        rate, ch, frames = w.getframerate(), w.getnchannels(), w.readframes(w.getnframes())
    a = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        a = a.reshape(-1, ch).mean(axis=1)
    return rate, a


# ------------------------------------------------------------------ who is speaking (level 2)
class Voiceprints:
    """Speaker recognition on the hub: a speaker embedding of the phrase (sherpa-onnx with a WeSpeaker/3D-Speaker ONNX
    model) is compared with the enrolled people (cosine to the mean of their enrollment clips). Only embeddings are
    stored (one JSON per person in `dir`), never audio. A convenience, not a lock: a recording or a cloned voice passes,
    so a recognised voice gets the person's chat and permissions, never secrets or payments."""

    def __init__(self, cfg):
        cfg = cfg or {}
        self.model = cfg.get("model") or ""
        self.dir = cfg.get("dir") or "/app/state/voiceprints"
        self.threshold = float(cfg.get("threshold") or 0.5)
        self.margin = float(cfg.get("margin") or 0.05)  # best minus second best person
        self.min_s = float(cfg.get("minSeconds") or 1.0)
        self.threads = int(cfg.get("threads") or 2)
        self.ok = bool(self.model) and os.path.isfile(self.model)
        self._ex = None
        self._lock = threading.Lock()

    def _extractor(self):
        if self._ex is None:
            import sherpa_onnx
            self._ex = sherpa_onnx.SpeakerEmbeddingExtractor(
                sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=self.model, num_threads=self.threads))
        return self._ex

    def embed(self, wav):
        import numpy as np
        rate, a = wav_samples(wav)
        if a.size < rate * self.min_s:
            raise ValueError("too short for a voiceprint")
        with self._lock:
            ex = self._extractor()
            s = ex.create_stream()
            s.accept_waveform(rate, a)
            s.input_finished()
            if not ex.is_ready(s):
                raise ValueError("too short for a voiceprint")
            v = np.array(ex.compute(s), dtype=np.float32)
        return v / (np.linalg.norm(v) or 1.0)

    def _file(self, person):
        return os.path.join(self.dir, person + ".json")

    def load(self, person):
        import numpy as np
        try:
            d = load_json(self._file(person))
        except (OSError, ValueError):
            return None
        if d.get("model") != os.path.basename(self.model):  # enrolled with another model: not comparable
            return None
        v = np.array(d["mean"], dtype=np.float32)
        return v / (np.linalg.norm(v) or 1.0), int(d.get("n") or 1)

    def enrolled(self):
        try:
            return sorted(f[:-5] for f in os.listdir(self.dir) if f.endswith(".json"))
        except OSError:
            return []

    def enroll(self, person, wavs, replace=False):
        """Add clips to the person's voiceprint; returns {person, n, clips, self: cosine of each clip to the mean}."""
        import numpy as np
        vs, skipped = [], 0
        for w in wavs:
            try:
                vs.append(self.embed(w))
            except ValueError:
                skipped += 1
        if not vs:
            raise ValueError("no usable clip (16-bit WAV, at least %.1f s of speech each)" % self.min_s)
        old = None if replace else self.load(person)
        total = np.sum(vs, axis=0) + (old[0] * old[1] if old else 0)
        n = len(vs) + (old[1] if old else 0)
        mean = total / n
        unit = mean / (np.linalg.norm(mean) or 1.0)
        os.makedirs(self.dir, exist_ok=True)
        tmp = self._file(person) + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"person": person, "model": os.path.basename(self.model), "n": n,
                       "mean": [round(float(x), 6) for x in mean],
                       "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}, f)
        os.replace(tmp, self._file(person))
        return {"person": person, "n": n, "clips": len(vs), "skipped": skipped,
                "self": [round(float(v @ unit), 3) for v in vs]}

    def forget(self, person):
        try:
            os.remove(self._file(person))
            return True
        except OSError:
            return False

    def match(self, wav, people):
        """-> {person or None, score, scores{}} among `people` (ids); person only above threshold and margin."""
        v = self.embed(wav)
        scores = {}
        for p in people:
            ref = self.load(p)
            if ref is not None:
                scores[p] = round(float(v @ ref[0]), 3)
        ranked = sorted(scores.items(), key=lambda kv: -kv[1])
        best = ranked[0] if ranked else (None, 0.0)
        second = ranked[1][1] if len(ranked) > 1 else -1.0
        ok = best[0] is not None and best[1] >= self.threshold and best[1] - second >= self.margin
        return {"person": best[0] if ok else None, "score": best[1], "scores": scores}


# ------------------------------------------------------------------ the hub
class Hub:
    def __init__(self, cfg):
        self.cfg = cfg
        # the client key (the CO server, CO Web's voice mode): recognition, home commands, speech - never the agent;
        # each voice point has its own key with its own person (env), chat and agent permissions (points{})
        self.key = secret(cfg.get("key"), "VOICE_HUB_KEY")
        # people a point may recognise by voice (level 2): id -> their env, chat(s) and shell permission
        self.people = {}
        for pid, pp in (cfg.get("people") or {}).items():
            if re.fullmatch(r"[A-Za-z0-9_][\w-]{0,39}", pid) and isinstance(pp, dict):
                self.people[pid] = {"env": pp.get("env") or "", "chat": pp.get("chat") or "",
                                    "chats": list(pp.get("chats") or []), "shell": bool(pp.get("shell", False))}
        self.vp = Voiceprints(cfg.get("speakerId"))
        self.points = {}
        for name, pc in (cfg.get("points") or {}).items():
            k = secret((pc or {}).get("key"))
            if k and re.fullmatch(r"[\w.-]{1,40}", name):
                self.points[name] = {"key": k, "env": pc.get("env") or "", "chat": pc.get("chat") or "",
                                     "chats": list(pc.get("chats") or []), "speaker": pc.get("speaker") or "",
                                     "shell": bool(pc.get("shell", False)),
                                     # who this point recognises; a voice of nobody of them: "home" = home
                                     # commands only (default once people are set), "point" = as the point's env
                                     "people": [p for p in (pc.get("people") or []) if p in self.people],
                                     "unknown": pc.get("unknown") if pc.get("unknown") in ("home", "point") else "home"}
        self.slots = threading.BoundedSemaphore(int(cfg.get("maxConcurrent") or 6))
        self.langs = tuple(cfg.get("languages") or ("ru", "uk", "en", "nl"))
        self.default_lang = cfg.get("defaultLanguage") or self.langs[0]
        s = cfg.get("speaches") or {}
        self.sp_url = (s.get("url") or "http://127.0.0.1:8000").rstrip("/")
        self.sp_key = secret(s.get("key"), "VOICE_SPEACHES_KEY")
        self.stt_model = s.get("sttModel") or "deepdml/faster-whisper-large-v3-turbo-ct2"
        # short phrases (commands) go to a small model: ~1 s instead of ~5 s on a mini-PC CPU; "" = always sttModel
        self.stt_short = s.get("sttModelShort", "Systran/faster-whisper-small")
        self.short_s = float(s.get("shortSeconds") or 7)
        self.voices = {**DEFAULT_VOICES, **(s.get("voices") or {})}
        self.prompt = s.get("prompt") or ""
        h = cfg.get("homeAssistant") or {}
        self.ha_url = (h.get("url") or "").rstrip("/")
        self.ha_token = secret(h.get("token"), "VOICE_HA_TOKEN")
        self.ha_agent = h.get("agentId") or ""
        self.cast = h.get("speakers") or {}  # name -> media_player entity
        self.keep_errors = set(h.get("keepErrors") or HA_KEEP_ERRORS)
        c = cfg.get("co") or {}
        self.co_url = (c.get("dbUrl") or "").strip()
        self.co_key = secret(c.get("hostKey"), "VOICE_CO_HOST_KEY")
        self.co_chat = c.get("chatId") or ""  # default chat of points without their own
        self.co_wait = int(c.get("waitSeconds") or 120)
        self.public = (cfg.get("publicAudioBase") or "").rstrip("/")  # LAN address Cast speakers fetch replies from
        self.clips = {}  # id -> (expires, type, bytes)
        self.lock = threading.Lock()

    # -- engines
    def _sp_headers(self):
        return {"Authorization": f"Bearer {self.sp_key}"} if self.sp_key else {}

    def transcribe(self, audio, filename, typ, lang=None):
        model = self.stt_short if self.stt_short and audio_seconds(audio, typ) < self.short_s else self.stt_model
        fields = {"model": model, "response_format": "verbose_json", "temperature": "0"}
        if lang:
            fields["language"] = lang
        if self.prompt:
            fields["prompt"] = self.prompt
        body, ct = multipart(fields, {"file": (filename, audio, typ)})
        code, _, data = http("POST", self.sp_url + "/v1/audio/transcriptions", body,
                             {**self._sp_headers(), "Content-Type": ct}, timeout=120)
        if code != 200:
            raise RuntimeError(f"speech to text: HTTP {code} {data[:200]!r}")
        d = json.loads(data)
        return (d.get("text") or "").strip(), norm_lang(d.get("language") or lang, self.langs, self.default_lang)

    def speak(self, text, lang, fmt="mp3"):
        voice = self.voices.get(lang) or self.voices.get(self.default_lang) or next(iter(self.voices.values()), None)
        if not voice:
            raise RuntimeError("text to speech: no voice configured")
        body = json.dumps({"model": voice, "input": text[:4000], "voice": voice.rsplit("-", 2)[-2] if "-" in voice else "default",
                           "response_format": fmt}).encode()
        code, ct, data = http("POST", self.sp_url + "/v1/audio/speech", body,
                              {**self._sp_headers(), "Content-Type": "application/json"}, timeout=120)
        if code != 200:
            raise RuntimeError(f"text to speech: HTTP {code} {data[:200]!r}")
        return data, {"mp3": "audio/mpeg", "wav": "audio/wav", "opus": "audio/ogg", "flac": "audio/flac"}.get(fmt, ct)

    def home(self, text, lang):
        """Home Assistant conversation: {handled, reply, code}; handled False = HA did not understand (agent's turn)."""
        if not self.ha_url or not self.ha_token:
            return {"handled": False, "reply": "", "code": "ha_off"}
        body = {"text": text, "language": lang}
        if self.ha_agent:
            body["agent_id"] = self.ha_agent
        code, _, data = http("POST", self.ha_url + "/api/conversation/process", json.dumps(body).encode(),
                             {"Authorization": f"Bearer {self.ha_token}", "Content-Type": "application/json"}, timeout=15)
        if code != 200:
            return {"handled": False, "reply": "", "code": f"http_{code}"}
        r = (json.loads(data).get("response") or {})
        err = (r.get("data") or {}).get("code") if r.get("response_type") == "error" else None
        reply = ((r.get("speech") or {}).get("plain") or {}).get("speech") or ""
        return {"handled": err is None or err in self.keep_errors, "reply": reply, "code": err or r.get("response_type") or ""}

    def co(self, op, payload, timeout=30):
        raw = gzip.compress(json.dumps({"op": op, **payload}, ensure_ascii=False).encode())
        body = json.dumps({"gz": base64.b64encode(raw).decode()}).encode()
        code, _, data = http("POST", self.co_url, body, {"X-CO-Push-Key": self.co_key, "Content-Type": "application/json"},
                             timeout=timeout)
        try:
            d = json.loads(data)
        except ValueError:
            d = {"error": f"HTTP {code}"}
        if code != 200 or d.get("error"):
            raise RuntimeError(f"CO {op}: {d.get('error') or code}")
        return d

    def agent(self, text, lang, chat=None, point=None):
        """The CO agent of the point's chat answers as the point's person; (None, reason) when it cannot."""
        if not point:
            return None, "agent_off"
        chat = chat if chat and chat in point["chats"] + [point["chat"]] else (point["chat"] or self.co_chat)
        if not (self.co_url and self.co_key and chat and point["env"]):
            return None, "agent_off"
        try:
            job = self.co("voice_ask", {"chatId": chat, "env": point["env"], "text": text, "lang": lang,
                                        "point": point["name"], "shell": point["shell"]})
        except RuntimeError as e:
            log("agent", e)
            return None, "fail"
        if job.get("busy"):
            return None, "busy"
        if job.get("error") or not job.get("jobId"):
            log("agent", job.get("error"))
            return None, "fail"
        end = time.time() + self.co_wait
        while time.time() < end:
            time.sleep(1.5)
            try:
                r = self.co("voice_answer", {"jobId": job["jobId"]})
            except RuntimeError as e:
                log("agent", e)
                continue
            if r.get("answer"):
                return r["answer"], "ok"
            if r.get("state") in ("completed", "failed", "cancelled", "rejected"):
                return None, "fail"
        return None, "agent_wait"

    def clip(self, data, typ):
        cid = secrets.token_urlsafe(18)
        with self.lock:
            now = time.time()
            for k in [k for k, v in self.clips.items() if v[0] < now]:
                del self.clips[k]
            self.clips[cid] = (now + 600, typ, data)
        return cid

    def cast_play(self, speaker, data, typ):
        ent = self.cast.get(speaker, speaker if speaker.startswith("media_player.") else "")
        if not ent or not self.public or not self.ha_token:
            raise RuntimeError("cast: speaker, publicAudioBase and the HA token are required")
        ext = "mp3" if "mpeg" in typ else "wav"
        url = f"{self.public}/a/{self.clip(data, typ)}.{ext}"
        body = json.dumps({"entity_id": ent, "media_content_id": url, "media_content_type": "music",
                           "announce": True}).encode()
        code, _, resp = http("POST", self.ha_url + "/api/services/media_player/play_media", body,
                             {"Authorization": f"Bearer {self.ha_token}", "Content-Type": "application/json"}, timeout=15)
        if code != 200:
            raise RuntimeError(f"cast: HTTP {code} {resp[:200]!r}")
        return ent

    def clean(self, text):
        """Agent answers are markdown; the speaker reads plain sentences."""
        t = re.sub(r"```.*?```", " ", text, flags=re.S)
        t = re.sub(r"`([^`]*)`", r"\1", t)
        t = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", t)
        t = re.sub(r"https?://\S+", "", t)
        t = re.sub(r"^[#>*\-\s|]+", "", t, flags=re.M)
        t = re.sub(r"[*_~|]+", "", t)
        return re.sub(r"\s+", " ", t).strip()[:1500]

    # -- one voice turn
    def turn(self, text=None, audio=None, filename="audio.wav", typ="audio/wav", lang=None, route="auto",
             speak=True, fmt="mp3", chat=None, point=None, speaker=""):
        t0 = time.time()
        tm = {}
        out = {"route": None}
        who = None  # recognised voice; None = not checked or not recognised
        check = bool(point and point.get("people") and audio is not None)
        th = None
        if check and self.vp.ok and audio[:4] == b"RIFF":
            res = {}

            def _who():
                t1 = time.time()
                try:
                    res.update(self.vp.match(audio, point["people"]))
                except Exception as e:  # noqa: BLE001 - not recognised, never a failed turn
                    res["error"] = str(e)[:120]
                tm["voice"] = round(time.time() - t1, 2)
            th = threading.Thread(target=_who, daemon=True)  # in parallel with speech to text
            th.start()
        if audio is not None:
            text, lang = self.transcribe(audio, filename, typ, lang if lang in self.langs else None)
            tm["stt"] = round(time.time() - t0, 2)
        if th is not None:
            th.join(30)
            who = res.get("person")
            out["speaker"] = {"person": who, "score": res.get("score"), **({"error": res["error"]} if "error" in res else {})}
        elif check:
            out["speaker"] = {"person": None, "error": "speaker recognition off" if not self.vp.ok else "not a WAV"}
        asker = point  # whose agent answers: the recognised person, the point itself, or nobody
        if check:
            if who:
                asker = {**point, **self.people[who], "person": who}
            elif point["unknown"] == "home":
                asker = None
        lang = norm_lang(lang, self.langs, self.default_lang)
        text = (text or "").strip()
        out.update(text=text, lang=lang)
        reply = ""
        if not text or len(re.sub(r"\W", "", text)) < 2:
            out["route"], reply = "none", say(lang, "unheard")
        else:
            forced = AGENT_PREFIX.match(text)
            if forced:
                text = text[forced.end():].strip() or text
                if route != "client":
                    route = "agent"
                else:  # CO Web sends it into its chat itself
                    out.update(route="agent", text=text, timings=tm)
                    return out
            if route in ("auto", "home", "client"):
                t1 = time.time()
                h = self.home(text, lang)
                tm["home"] = round(time.time() - t1, 2)
                out["home"] = h["code"]
                if h["handled"] or route == "home":
                    out["route"], reply = "home", h["reply"] or say(lang, "fail")
            if out["route"] is None:
                if route == "client":  # the caller (CO Web) sends the phrase into its own chat
                    out["route"] = "agent"
                    out["timings"] = tm
                    return out
                t1 = time.time()
                if point and asker is None:
                    ans, why = None, "stranger"
                else:
                    ans, why = self.agent(text, lang, chat, asker)
                tm["agent"] = round(time.time() - t1, 2)
                out["route"] = "agent"
                reply = self.clean(ans) if ans else say(lang, why)
        out["reply"] = reply
        if speak and reply:
            t1 = time.time()
            data, atype = self.speak(reply, lang, fmt)
            tm["tts"] = round(time.time() - t1, 2)
            if speaker:
                out["cast"] = self.cast_play(speaker, data, atype)
            else:
                out["audio"] = {"type": atype, "b64": base64.b64encode(data).decode()}
        tm["total"] = round(time.time() - t0, 2)
        out["timings"] = tm
        return out


def make_handler(hub):
    class H(BaseHTTPRequestHandler):
        server_version = UA
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *a):
            pass

        def _send(self, code, body, ctype="application/json"):
            if isinstance(body, (dict, list)):
                body = json.dumps(body, ensure_ascii=False).encode()
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)

        def _caller(self):
            """("client", None) for the client key, ("point", {...}) for a point's key, (None, None) otherwise."""
            got = (self.headers.get("Authorization") or "").removeprefix("Bearer ").strip().encode()
            if not got:
                return None, None
            found = (None, None)
            for name, pc in hub.points.items():  # compare with every key (no early exit)
                if hmac.compare_digest(got, pc["key"].encode()):
                    found = ("point", {**pc, "name": name})
            if hub.key and hmac.compare_digest(got, hub.key.encode()):
                found = ("client", None)
            return found

        def _path(self):
            # behind Tailscale Funnel the hub may sit under a path (/voice); the API keeps its own names
            p = urllib.parse.urlsplit(self.path).path
            return re.sub(r"^/voice(?=/)", "", p)

        def do_GET(self):
            p = self._path()
            if p == "/v1/voice/health":
                return self._send(200, {"ok": True, "version": VERSION})
            m = re.fullmatch(r"/a/([A-Za-z0-9_-]{20,})\.(mp3|wav)", p)
            if m:  # a reply clip for a Cast speaker (unguessable id, 10 min)
                c = hub.clips.get(m.group(1))
                if c and c[0] > time.time():
                    return self._send(200, c[2], c[1])
            return self._send(404, {"error": "not_found"})

        def do_POST(self):
            p = self._path()
            role, point = self._caller()
            if role is None:  # before reading anything of the body
                self.close_connection = True
                return self._send(401, {"error": "unauthorized"})
            try:
                n = int(self.headers.get("Content-Length") or "0")
            except ValueError:
                n = -1
            if n < 0 or n > 25 * 1024 * 1024:
                self.close_connection = True
                return self._send(413, {"error": "bad_length"})
            if not hub.slots.acquire(timeout=30):
                self.close_connection = True
                return self._send(503, {"error": "busy"})
            try:
                return self._post(p, role, point, self.rfile.read(n) if n else b"")
            finally:
                hub.slots.release()

        def _post(self, p, role, point, body):
            q = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(self.path).query))
            ctype = self.headers.get("Content-Type") or ""
            files = {}
            try:
                if ctype.startswith("application/json"):
                    q.update(json.loads(body or b"{}"))
                    audio, fn, typ = None, "audio", ""
                elif ctype.startswith("multipart/"):
                    fields, files = parse_multipart(body, ctype)
                    q.update(fields)
                    fn, audio, typ = files.get("file", ("audio.wav", None, "audio/wav"))
                else:
                    audio, fn, typ = body, "audio." + (ctype.split("/")[-1].split(";")[0] or "wav"), ctype or "audio/wav"
                if p == "/v1/voice/turn":
                    if audio is None and not q.get("text"):
                        return self._send(400, {"error": "audio or text required"})
                    route = q.get("route") if q.get("route") in ("auto", "home", "agent", "client") else "auto"
                    if role == "client" and route != "home":
                        route = "client"  # the client key never makes the hub ask the agent
                    speaker = q.get("speaker") or (point or {}).get("speaker") or ""
                    r = hub.turn(text=q.get("text"), audio=audio, filename=fn, typ=typ, lang=q.get("lang"),
                                 route=route, speak=q.get("speak", "1") not in (False, 0, "0", "false", "False"),
                                 fmt=q.get("format") or "mp3", chat=q.get("chat"), point=point, speaker=speaker)
                    log("turn", r.get("route"), r.get("lang"), r.get("timings"))
                    return self._send(200, r)
                if p == "/v1/voice/transcribe":
                    text, lang = hub.transcribe(audio, fn, typ, q.get("lang"))
                    return self._send(200, {"text": text, "lang": lang})
                if p == "/v1/voice/speak":
                    lang = norm_lang(q.get("lang"), hub.langs, hub.default_lang)
                    data, atype = hub.speak(str(q.get("text") or "")[:4000], lang, q.get("format") or "mp3")
                    if q.get("speaker"):
                        return self._send(200, {"cast": hub.cast_play(q["speaker"], data, atype)})
                    return self._send(200, data, atype)
                if p == "/v1/voice/home":
                    lang = norm_lang(q.get("lang"), hub.langs, hub.default_lang)
                    return self._send(200, hub.home(str(q.get("text") or ""), lang))
                if p == "/v1/voice/status":
                    return self._send(200, hub_status(hub))
                if p in ("/v1/voice/enroll", "/v1/voice/forget", "/v1/voice/whois"):
                    # voiceprints: only with a point's key and only for the people of that point (its owner runs
                    # the enrollment with the person present - each person records their own voice, by consent)
                    if role != "point":
                        return self._send(403, {"error": "a voice point key is required"})
                    if not hub.vp.ok:
                        return self._send(409, {"error": "speaker recognition is off (speakerId.model)"})
                    if p == "/v1/voice/whois":
                        if audio is None:
                            return self._send(400, {"error": "audio required"})
                        return self._send(200, hub.vp.match(audio, point["people"]))
                    person = str(q.get("person") or "")
                    if person not in point["people"]:
                        return self._send(403, {"error": "person is not one of this point's people"})
                    if p == "/v1/voice/forget":
                        return self._send(200, {"person": person, "forgotten": hub.vp.forget(person)})
                    wavs = [d for k, (_, d, _) in sorted(files.items()) if k.startswith("file")] or \
                           ([audio] if audio is not None else [])
                    if not wavs or len(wavs) > 60:
                        return self._send(400, {"error": "1-60 WAV clips required (file0, file1, ...)"})
                    r = hub.vp.enroll(person, wavs, replace=str(q.get("replace", "")) in ("1", "true", "True"))
                    log("enroll", point["name"], person, r["n"], r["skipped"])
                    return self._send(200, r)
            except Exception as e:  # noqa: BLE001 - the client gets the reason, the hub keeps running
                log("error", p, e)
                return self._send(502, {"error": str(e)[:300]})
            return self._send(404, {"error": "not_found"})
    return H


def hub_status(hub):
    st = {"version": VERSION, "languages": list(hub.langs), "voices": {k: v for k, v in hub.voices.items() if k in hub.langs}}
    code, _, data = http("GET", hub.sp_url + "/v1/models", headers=hub._sp_headers(), timeout=5)
    models = [m.get("id") for m in json.loads(data).get("data", [])] if code == 200 else []
    st["speaches"] = {"ok": code == 200, "stt": hub.stt_model in models,
                      "tts": [l for l in hub.langs if hub.voices.get(l) in models]}
    if hub.ha_url:
        code, _, _ = http("GET", hub.ha_url + "/api/", headers={"Authorization": f"Bearer {hub.ha_token}"}, timeout=5)
        st["homeAssistant"] = {"ok": code == 200, "speakers": sorted(hub.cast)}
    st["agent"] = {"configured": bool(hub.co_url and hub.co_key), "points": sorted(hub.points)}
    st["speakerId"] = {"ok": hub.vp.ok, "model": os.path.basename(hub.vp.model) if hub.vp.model else "",
                       "people": sorted(hub.people), "enrolled": hub.vp.enrolled() if hub.vp.ok else []}
    return st


def cmd_hub(a):
    cfg = load_json(a.config) if a.config else {}
    hub = Hub(cfg)
    if not hub.key:
        sys.exit("hub: no API key (config key {file|env} or VOICE_HUB_KEY)")
    host, _, port = (a.listen or cfg.get("listen") or "127.0.0.1:8095").rpartition(":")
    srv = ThreadingHTTPServer((host or "127.0.0.1", int(port)), make_handler(hub))
    log(f"voice hub {VERSION} on {host}:{port}; speaches {hub.sp_url}; HA {'on' if hub.ha_token else 'off'}; "
        f"agent {'on' if hub.co_key and hub.co_chat else 'off'}")
    srv.serve_forever()


# ------------------------------------------------------------------ client side (point, say, ask)
def client_cfg_path():
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.config")
    return os.path.join(base, "AIWorkbench", "voice-point.json")


def client_cfg(path=None):
    p = path or client_cfg_path()
    c = load_json(p) if os.path.isfile(p) else {}
    c.setdefault("hub", os.environ.get("VOICE_HUB_URL", ""))
    c.setdefault("wakeWord", "hey_jarvis")
    c.setdefault("alwaysListen", False)
    c.setdefault("threshold", 0.5)
    c.setdefault("silenceMs", 900)
    c.setdefault("maxSeconds", 15)
    c.setdefault("lang", "")
    c.setdefault("speaker", "")  # "" = this device; a Cast speaker name of the hub config otherwise
    c.setdefault("point", platform.node() or "point")
    return c


def save_client_cfg(c, path=None):
    p = path or client_cfg_path()
    os.makedirs(os.path.dirname(p), exist_ok=True)
    tmp = p + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(c, f, ensure_ascii=False, indent=1)
    os.replace(tmp, p)


def hub_key():
    k = os.environ.get("VOICE_HUB_KEY")
    if k:
        return k.strip()
    try:
        import keyring
        return (keyring.get_password("aiwb/voice-hub", "default") or "").strip()
    except Exception:
        return ""


def hub_call(c, path, payload=None, audio=None, timeout=180, audios=None):
    url = c["hub"].rstrip("/") + path
    h = {"Authorization": f"Bearer {hub_key()}"}
    if audios:
        body, ct = multipart({k: str(v) for k, v in (payload or {}).items()},
                             {f"file{i:02d}": (f"clip{i:02d}.wav", d, "audio/wav") for i, d in enumerate(audios)})
    elif audio is not None:
        body, ct = multipart({k: str(v) for k, v in (payload or {}).items()}, {"file": ("phrase.wav", audio, "audio/wav")})
    else:
        body, ct = json.dumps(payload or {}).encode(), "application/json"
    code, rtype, data = http("POST", url, body, {**h, "Content-Type": ct}, timeout=timeout)
    if code != 200:
        raise RuntimeError(f"hub {path}: HTTP {code} {data[:200]!r}")
    return json.loads(data) if rtype.startswith("application/json") else data


def play_wav(data):
    import numpy as np
    import sounddevice as sd
    with wave.open(io.BytesIO(data)) as w:
        rate, ch, frames = w.getframerate(), w.getnchannels(), w.readframes(w.getnframes())
    a = np.frombuffer(frames, dtype=np.int16)
    if ch > 1:
        a = a.reshape(-1, ch)
    sd.play(a, rate)
    sd.wait()


def beep(kind="start"):
    try:
        import numpy as np
        import sounddevice as sd
        f, d = (880, 0.12) if kind == "start" else (520, 0.10)
        t = np.linspace(0, d, int(16000 * d), False)
        sd.play((0.25 * np.sin(2 * np.pi * f * t) * np.hanning(t.size)).astype(np.float32), 16000)
        sd.wait()
    except Exception:
        pass


def play_reply(c, r):
    a = r.get("audio")
    if a and a.get("b64"):
        play_wav(base64.b64decode(a["b64"]))


class Indicator:
    """«Listening» indicator: a state file other apps (CO Desktop, a tray) read, plus the console line."""

    def __init__(self):
        base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/state")
        self.path = os.path.join(base, "AIWorkbench", "voice-point.state.json")
        os.makedirs(os.path.dirname(self.path), exist_ok=True)

    def set(self, state, **kw):
        d = {"state": state, "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "pid": os.getpid(), **kw}
        try:
            with open(self.path + ".tmp", "w", encoding="utf-8") as f:
                json.dump(d, f, ensure_ascii=False)
            os.replace(self.path + ".tmp", self.path)
        except OSError:
            pass
        log("point:", state, kw.get("text", ""))


def record_phrase(stream_read, vad, silence_ms, max_s, pre=b""):
    """Read 80 ms blocks until the speaker stops (silero VAD) or max_s; returns 16 kHz PCM16 bytes."""
    import numpy as np
    out = [pre]
    heard, quiet, t0 = False, 0, time.time()
    while time.time() - t0 < max_s:
        block = stream_read()
        out.append(block.tobytes())
        p = vad.predict(block) if vad else 1.0
        if p >= 0.5:
            heard, quiet = True, 0
        else:
            quiet += 80
        if heard and quiet >= silence_ms:
            break
        if not heard and time.time() - t0 > 4:  # nothing said after the wake word
            break
    return b"".join(out), heard


def cmd_point(a):
    c = client_cfg(a.config)
    if a.hub:
        c["hub"] = a.hub
    if not c["hub"] or not hub_key():
        # a service of the module starts on every Bridge: an unconfigured point waits instead of exiting (restarts)
        Indicator().set("off", reason="not configured: point-config --hub URL --set-key")
        while True:
            time.sleep(30)
            c = client_cfg(a.config)
            if a.hub:
                c["hub"] = a.hub
            if c["hub"] and hub_key():
                break
    try:
        import sounddevice as sd
        from openwakeword.model import Model
        from openwakeword.vad import VAD
    except ImportError:
        py = venv_python()
        if py and os.path.abspath(py) != os.path.abspath(sys.executable):
            # the service runs with the system Python; the point's packages live in their own venv (setup-point)
            import subprocess
            return subprocess.call([py, os.path.abspath(__file__)] + sys.argv[1:])
        sys.exit("point: packages missing - run: voice.py setup-point")
    ind = Indicator()
    wake = a.wake or c["wakeWord"]
    if not a.once and not c["alwaysListen"]:
        # off until the device's owner turns it on; the service keeps running and picks the switch up
        ind.set("off", reason="always-on listening is off (point-config --always-listen on, by the device owner)")
        while not client_cfg(a.config)["alwaysListen"]:
            time.sleep(10)
        c = client_cfg(a.config)
    vad = VAD()
    model = None if a.once else wake_model(Model, wake, c)
    blk = 1280  # 80 ms at 16 kHz, what openWakeWord expects
    stream = sd.InputStream(samplerate=16000, channels=1, dtype="int16", blocksize=blk, device=c.get("inputDevice"))
    stream.start()

    def read():
        data, _ = stream.read(blk)
        return data[:, 0].copy()

    stop_file = os.path.join(os.path.dirname(Indicator().path), "voice-point.stop")
    try:
        while True:
            if not a.once:
                ind.set("listening", wake=wake)
                model.reset()
                n = 0
                while True:
                    n += 1
                    if n % 60 == 0 and not client_cfg(a.config)["alwaysListen"]:  # switched off (every ~5 s)
                        ind.set("off", reason="always-on listening was turned off")
                        return 0
                    if os.path.exists(stop_file):
                        os.remove(stop_file)
                        ind.set("off", reason="stopped")
                        return 0
                    scores = model.predict(read())
                    if max(scores.values()) >= c["threshold"]:
                        break
            stream.stop()
            beep("start")
            stream.start()
            ind.set("recording")
            pcm, heard = record_phrase(read, vad, c["silenceMs"], c["maxSeconds"])
            stream.stop()
            if not heard:
                beep("end")
                ind.set("idle", text="(nothing heard)")
            else:
                beep("end")
                ind.set("thinking")
                try:
                    r = hub_call(c, "/v1/voice/turn", {"lang": c["lang"] or "", "format": "wav", "point": c["point"],
                                                       "speaker": c["speaker"]}, audio=wav_bytes(pcm))
                    ind.set("speaking", text=f'{r.get("text")} -> [{r.get("route")}] {r.get("reply")}',
                            timings=r.get("timings"))
                    play_reply(c, r)
                except Exception as e:  # noqa: BLE001
                    ind.set("error", text=str(e)[:200])
            if a.once:
                return 0
            stream.start()
    finally:
        stream.close()


def wake_model(Model, wake, c):
    """openWakeWord model; with a voice verifier of this device (level 1, `enroll`) the wake word counts only in the
    enrolled voices: a raw score above verifierGate is replaced by the verifier's probability."""
    kw = {"wakeword_models": [wake], "inference_framework": "onnx"}
    ver = c.get("verifier") or {}
    path = ver.get(wake) if isinstance(ver, dict) else None
    if path and os.path.isfile(path):  # a pickle written by this tool on this device (enroll), never downloaded
        kw["custom_verifier_models"] = {os.path.splitext(os.path.basename(wake))[0]: path}
        kw["custom_verifier_threshold"] = float(c.get("verifierGate") or 0.3)
    return Model(**kw)


# ------------------------------------------------------------------ enrollment (levels 1 and 2)
ENROLL_TEXT = {
    "ru": {"intro": "Запись образцов голоса для «{person}». Говорите обычным голосом с того места, откуда будете "
                    "обращаться к точке. Образцы остаются на этом устройстве и на сервере семьи (только числа).",
           "wake": "[{i}/{n}] После сигнала скажите: {phrase}", "phrase": "[{i}/{n}] После сигнала прочитайте: {text}",
           "again": "  Не узнала слово-пробуждение, ещё раз.", "ok": "  принято", "quiet": "  Тишина, ещё раз.",
           "train": "Обучаю верификатор на устройстве...", "hub": "Отправляю образцы на сервер семьи...",
           "done": "Готово. Можно закрыть окно.", "enter": "Нажмите Enter, чтобы начать... "},
    "en": {"intro": "Recording voice samples for '{person}'. Speak normally from where you will talk to the point. "
                    "Samples stay on this device and on the family server (numbers only).",
           "wake": "[{i}/{n}] After the beep say: {phrase}", "phrase": "[{i}/{n}] After the beep read: {text}",
           "again": "  Wake word not recognised, once more.", "ok": "  ok", "quiet": "  Silence, once more.",
           "train": "Training the verifier on this device...", "hub": "Sending samples to the family server...",
           "done": "Done. You can close this window.", "enter": "Press Enter to start... "},
}
ENROLL_PHRASES = {
    "ru": ["Включи свет на кухне и сделай потеплее.", "Какая завтра погода в Генте?", "Напомни мне в пятницу позвонить врачу.",
           "Сколько будет двадцать три умножить на семь?", "Расскажи коротко, что нового в мире.",
           "Поставь таймер на пятнадцать минут.", "Съешь же ещё этих мягких французских булок да выпей чаю.",
           "Какие у меня встречи на следующей неделе?"],
    "en": ["Turn on the kitchen light and make it a bit warmer.", "What is the weather tomorrow?",
           "Remind me on Friday to call the doctor.", "What is twenty three times seven?",
           "Tell me briefly what is new in the world.", "Set a timer for fifteen minutes.",
           "The quick brown fox jumps over the lazy dog.", "What meetings do I have next week?"],
}


def enroll_dir(person=None):
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/share")
    d = os.path.join(base, "AIWorkbench", "voice-enroll")
    return os.path.join(d, person) if person else d


def resample16k(rate, a):
    import numpy as np
    if rate == 16000:
        return a
    n = int(round(a.size * 16000 / rate))
    return np.interp(np.linspace(0, a.size - 1, n), np.arange(a.size), a).astype(np.float32)


def save_wav16(path, a):
    import numpy as np
    pcm = (np.clip(a, -1, 1) * 32767).astype(np.int16).tobytes()
    with open(path, "wb") as f:
        f.write(wav_bytes(pcm))


def wake_score(model, a16):
    """Max raw wake score over a 16 kHz float clip (with 1 s of silence before, as the point hears it)."""
    import numpy as np
    model.reset()
    pcm = (np.concatenate([np.zeros(16000, np.float32), a16, np.zeros(8000, np.float32)]) * 32767).astype(np.int16)
    return max((max(model.predict(pcm[i:i + 1280]).values()) for i in range(0, pcm.size - 1280, 1280)), default=0.0)


def train_verifier(wake, c, negatives_dir):
    """Verifier of this device: positives = wake clips of every person enrolled here, negatives = their other speech and
    the wake word in synthetic voices (what must not wake the point). -> path or None (nobody enrolled)."""
    import glob
    from openwakeword.custom_verifier_model import train_custom_verifier
    pos = sorted(glob.glob(os.path.join(enroll_dir(), "*", "wake", "*.wav")))
    if not pos:
        return None
    neg = sorted(glob.glob(os.path.join(enroll_dir(), "*", "speech", "*.wav")) +
                 glob.glob(os.path.join(negatives_dir, "*.wav")))
    name = os.path.splitext(os.path.basename(wake))[0]
    out = os.path.join(os.path.dirname(client_cfg_path()), f"voice-verifier-{name}.pkl")
    import contextlib
    with contextlib.redirect_stdout(sys.stderr):
        train_custom_verifier(positive_reference_clips=pos, negative_reference_clips=neg, output_path=out + ".tmp",
                              model_name=wake, inference_framework="onnx")
    os.replace(out + ".tmp", out)
    return out


def synth_negatives(c, wake, lang_list):
    """The wake word said by the hub's synthetic voices (each language) -> 16 kHz WAV clips in a temp folder."""
    import tempfile
    d = tempfile.mkdtemp(prefix="voice-neg-")
    phrase = wake.replace("_", " ")
    for i, lang in enumerate(lang_list):
        for j, t in enumerate((phrase, phrase.title() + "!", phrase + ", " + {"ru": "который час", "uk": "котра година",
                                                                             "nl": "hoe laat is het"}.get(lang, "what time is it"))):
            try:
                data = hub_call(c, "/v1/voice/speak", {"text": t, "lang": lang, "format": "wav"})
                rate, a = wav_samples(data)
                save_wav16(os.path.join(d, f"tts-{lang}-{j}.wav"), resample16k(rate, a))
            except Exception as e:  # noqa: BLE001
                log("negatives", lang, e)
    return d


def cmd_enroll(a):
    c = client_cfg(a.config)
    if a.hub:
        c["hub"] = a.hub
    if not re.fullmatch(r"[A-Za-z0-9_][\w-]{0,39}", a.person or ""):
        sys.exit("enroll: --person ID (letters, digits, _ -), the id of the person in the hub config")
    try:
        import numpy as np
        import sounddevice as sd
        from openwakeword.model import Model
        from openwakeword.vad import VAD
        import sklearn  # noqa: F401 - the verifier
    except ImportError:
        py = venv_python()
        if py and os.path.abspath(py) != os.path.abspath(sys.executable):
            import subprocess
            return subprocess.call([py, os.path.abspath(__file__)] + sys.argv[1:])
        sys.exit("enroll: packages missing - run: voice.py setup-point")
    wake = a.wake or c["wakeWord"]
    pdir = enroll_dir(a.person)
    if a.forget:
        import shutil
        shutil.rmtree(pdir, ignore_errors=True)
        r = {"person": a.person, "local": "deleted"}
        neg = synth_negatives(c, wake, ["ru", "en"]) if os.path.isdir(enroll_dir()) and os.listdir(enroll_dir()) else None
        path = train_verifier(wake, c, neg) if neg else None
        c["verifier"] = {**(c.get("verifier") or {}), wake: path} if path else \
            {k: v for k, v in (c.get("verifier") or {}).items() if k != wake}
        save_client_cfg(c, a.config)
        r["verifier"] = path
        if c["hub"] and hub_key() and not a.no_hub:
            r["hub"] = hub_call(c, "/v1/voice/forget", {"person": a.person})
        print(json.dumps(r, ensure_ascii=False))
        return 0
    L = ENROLL_TEXT.get(a.lang or c["lang"] or "en", ENROLL_TEXT["en"])
    phrases = ENROLL_PHRASES.get(a.lang or c["lang"] or "en", ENROLL_PHRASES["en"])[:a.phrases]
    print(L["intro"].format(person=a.person))
    if not a.no_wait:
        input(L["enter"])
    os.makedirs(os.path.join(pdir, "wake"), exist_ok=True)
    os.makedirs(os.path.join(pdir, "speech"), exist_ok=True)
    dev = a.input_device if a.input_device is not None else c.get("inputDevice")
    if isinstance(dev, str) and dev.isdigit():
        dev = int(dev)
    oww = Model(wakeword_models=[wake], inference_framework="onnx")
    vad = VAD()

    def rec(seconds, until_silence=False):
        beep("start")
        blocks, heard, quiet = [], False, 0
        with sd.InputStream(samplerate=16000, channels=1, dtype="int16", blocksize=1280, device=dev) as st:
            t0 = time.time()
            while time.time() - t0 < seconds:
                b, _ = st.read(1280)
                b = b[:, 0].copy()
                blocks.append(b)
                if vad.predict(b) >= 0.5:
                    heard, quiet = True, 0
                else:
                    quiet += 80
                if until_silence and heard and quiet >= 900:
                    break
        return np.concatenate(blocks).astype(np.float32) / 32768.0, heard

    wake_phrase = wake.replace("_", " ")
    i, tries = 0, 0
    while i < a.wake_clips and tries < a.wake_clips * 2:
        tries += 1
        print(L["wake"].format(i=i + 1, n=a.wake_clips, phrase=wake_phrase), flush=True)
        clip, heard = rec(2.5)
        s = wake_score(oww, clip)
        if s < 0.5:
            print(L["again"] if heard else L["quiet"], f"({s:.2f})", flush=True)
            continue
        # 1 s of silence before (the model warms up on the first frames), as the point hears it
        save_wav16(os.path.join(pdir, "wake", f"w{i:02d}.wav"), np.concatenate([np.zeros(16000, np.float32), clip]))
        print(L["ok"], f"({s:.2f})", flush=True)
        i += 1
    for j, text in enumerate(phrases):
        for _ in range(2):
            print(L["phrase"].format(i=j + 1, n=len(phrases), text=text), flush=True)
            clip, heard = rec(10, until_silence=True)
            if heard:
                save_wav16(os.path.join(pdir, "speech", f"s{j:02d}.wav"), clip)
                print(L["ok"], flush=True)
                break
            print(L["quiet"], flush=True)
    r = {"person": a.person, "wakeClips": i, "speechClips": len(os.listdir(os.path.join(pdir, "speech")))}
    if not a.no_verifier and i:
        print(L["train"], flush=True)
        neg = synth_negatives(c, wake, ["ru", "uk", "en", "nl"]) if c["hub"] and hub_key() else enroll_dir("_none")
        path = train_verifier(wake, c, neg)
        c["verifier"] = {**(c.get("verifier") or {}), wake: path}
        save_client_cfg(c, a.config)
        r["verifier"] = path
        r["check"] = verifier_check(Model, wake, c, pdir, neg)
    if not a.no_hub and c["hub"] and hub_key():
        print(L["hub"], flush=True)
        import glob
        clips = sorted(glob.glob(os.path.join(pdir, "speech", "*.wav")))
        wav_list = []
        for p in clips:
            with open(p, "rb") as f:
                wav_list.append(f.read())
        r["hub"] = hub_call(c, "/v1/voice/enroll", {"person": a.person, "replace": "1"}, audios=wav_list)
    print(L["done"], flush=True)
    print(json.dumps(r, ensure_ascii=False, indent=1))
    if not a.no_wait:
        input()
    return 0


def verifier_check(Model, wake, c, pdir, negdir):
    """How the trained verifier scores its own clips: own wake clips should pass, synthetic voices should not."""
    import glob
    m = wake_model(Model, wake, c)

    def frac(paths):
        ok = 0
        for p in paths:
            rate, a = wav_samples(open(p, "rb").read())
            ok += wake_score(m, resample16k(rate, a)) >= float(c.get("threshold") or 0.5)
        return f"{ok}/{len(paths)}"
    return {"ownWakePass": frac(sorted(glob.glob(os.path.join(pdir, "wake", "*.wav")))),
            "syntheticPass": frac(sorted(glob.glob(os.path.join(negdir, "tts-*.wav"))))}


def venv_dir():
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~/.local/share")
    return os.path.join(base, "AIWorkbench", "venvs", "voice")


def venv_python():
    d = venv_dir()
    p = os.path.join(d, "Scripts", "python.exe") if os.name == "nt" else os.path.join(d, "bin", "python")
    return p if os.path.isfile(p) else None


def cmd_setup_point(a):
    """Own venv for the point: sounddevice, openWakeWord (onnx) and its models. On Windows onnxruntime 1.20.1:
    newer builds need the VC++ 2015-2022 runtime 14.40+ and fail to load on older ones."""
    import subprocess
    import venv
    d = venv_dir()
    if not venv_python():
        venv.create(d, with_pip=True)
    py = venv_python()
    ort = "onnxruntime==1.20.1" if os.name == "nt" and not a.latest else "onnxruntime"
    subprocess.check_call([py, "-m", "pip", "install", "-q", "--disable-pip-version-check", "numpy", "sounddevice",
                           "openwakeword", ort, "keyring", "scikit-learn", "scipy", "tqdm"])
    subprocess.check_call([py, "-c", "import openwakeword.utils as u; u.download_models()"])
    print(json.dumps({"venv": d, "python": py}))
    return 0


def cmd_point_config(a):
    c = client_cfg(a.config)
    for k in ("hub", "wake_word", "lang", "speaker", "point"):
        v = getattr(a, k)
        if v is not None:
            c[{"wake_word": "wakeWord"}.get(k, k)] = v
    if a.input_device is not None:
        c["inputDevice"] = None if a.input_device == "" else (int(a.input_device) if a.input_device.isdigit() else a.input_device)
    if a.always_listen is not None:
        on = a.always_listen == "on"
        if on and not a.yes:
            print("Always-on listening keeps the microphone of this device open and sends what follows the wake word "
                  "to the family server. Turn it on only on your own device, and tell the people around it.")
            if input("Type 'yes' to turn it on: ").strip().lower() != "yes":
                return 1
        c["alwaysListen"] = on
        c["alwaysListenBy"] = {"on": on, "user": os.environ.get("USERNAME") or os.environ.get("USER") or "",
                               "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    if a.set_key:
        import getpass
        import keyring
        keyring.set_password("aiwb/voice-hub", "default", getpass.getpass("Hub key: ").strip())
    save_client_cfg(c, a.config)
    print(json.dumps({k: v for k, v in c.items()}, ensure_ascii=False, indent=1))
    return 0


def cmd_say(a):
    c = client_cfg(a.config)
    if a.hub:
        c["hub"] = a.hub
    speaker = a.speaker if a.speaker is not None else c["speaker"]
    p = {"text": a.text, "lang": a.lang or c["lang"] or "", "format": "wav", "speaker": speaker}
    r = hub_call(c, "/v1/voice/speak", p)
    if isinstance(r, dict):
        print(json.dumps(r, ensure_ascii=False))
    else:
        play_wav(r)
    return 0


def cmd_ask(a):
    c = client_cfg(a.config)
    if a.hub:
        c["hub"] = a.hub
    p = {"lang": a.lang or "", "format": "wav", "route": a.route, "point": c["point"], "speak": "0" if a.quiet else "1"}
    if a.speaker is not None:
        p["speaker"] = a.speaker
    if a.wav:
        with open(a.wav, "rb") as f:
            r = hub_call(c, "/v1/voice/turn", p, audio=f.read())
    else:
        r = hub_call(c, "/v1/voice/turn", {**p, "text": a.text})
    audio = r.pop("audio", None)
    print(json.dumps(r, ensure_ascii=False, indent=1))
    if audio and not a.quiet:
        play_wav(base64.b64decode(audio["b64"]))
    return 0


def cmd_whois(a):
    c = client_cfg(a.config)
    if a.hub:
        c["hub"] = a.hub
    with open(a.wav, "rb") as f:
        print(json.dumps(hub_call(c, "/v1/voice/whois", {}, audio=f.read()), ensure_ascii=False))
    return 0


def cmd_check(a):
    if a.config_hub:
        hub = Hub(load_json(a.config_hub))
        print(json.dumps(hub_status(hub), ensure_ascii=False, indent=1))
        return 0
    c = client_cfg(a.config)
    print(json.dumps(hub_call(c, "/v1/voice/status", {}), ensure_ascii=False, indent=1))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(prog="voice", description=__doc__.split("\n")[0])
    ap.add_argument("--version", action="version", version=VERSION)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("hub", help="run the voice hub (family server)")
    s.add_argument("--config", help="hub config JSON (voice-hub.json)")
    s.add_argument("--listen", help="host:port (default 127.0.0.1:8095)")
    s.set_defaults(fn=cmd_hub)
    s = sub.add_parser("point", help="run a voice point on this device")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.add_argument("--wake", help="openWakeWord model (hey_jarvis, alexa, hey_mycroft, or a path to .onnx)")
    s.add_argument("--once", action="store_true", help="record one phrase now, no wake word")
    s.set_defaults(fn=cmd_point)
    s = sub.add_parser("setup-point", help="create the venv of the voice point (sounddevice, openWakeWord)")
    s.add_argument("--latest", action="store_true", help="newest onnxruntime also on Windows")
    s.set_defaults(fn=cmd_setup_point)
    s = sub.add_parser("point-config", help="settings of the voice point of this device")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.add_argument("--wake-word")
    s.add_argument("--lang")
    s.add_argument("--speaker", help="'' = this device, or a Cast speaker name from the hub config")
    s.add_argument("--point", help="name of this point")
    s.add_argument("--input-device", help="sounddevice input (index or name; '' = default)")
    s.add_argument("--always-listen", choices=["on", "off"])
    s.add_argument("--yes", action="store_true", help="owner confirms always-on listening without the prompt")
    s.add_argument("--set-key", action="store_true", help="type the hub key into keyring aiwb/voice-hub")
    s.set_defaults(fn=cmd_point_config)
    s = sub.add_parser("enroll", help="record a person's voice: verifier of the wake word here + voiceprint on the hub")
    s.add_argument("--person", required=True, help="person id of the hub config (people{})")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.add_argument("--wake", help="wake word model (default: the point's)")
    s.add_argument("--lang", help="prompts and phrases: ru or en")
    s.add_argument("--input-device", help="sounddevice input (index or name)")
    s.add_argument("--wake-clips", type=int, default=20)
    s.add_argument("--phrases", type=int, default=8)
    s.add_argument("--no-verifier", action="store_true", help="level 2 only")
    s.add_argument("--no-hub", action="store_true", help="level 1 only")
    s.add_argument("--no-wait", action="store_true", help="no Enter before and after (not in a window)")
    s.add_argument("--forget", action="store_true", help="delete the person's samples here and their voiceprint")
    s.set_defaults(fn=cmd_enroll)
    s = sub.add_parser("whois", help="whose voice is in a WAV file (the hub's speaker recognition)")
    s.add_argument("wav")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.set_defaults(fn=cmd_whois)
    s = sub.add_parser("say", help="speak a text through the hub")
    s.add_argument("text")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.add_argument("--lang")
    s.add_argument("--speaker")
    s.set_defaults(fn=cmd_say)
    s = sub.add_parser("ask", help="one voice turn from a WAV file or a text")
    g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("--wav")
    g.add_argument("--text")
    s.add_argument("--config")
    s.add_argument("--hub")
    s.add_argument("--lang")
    s.add_argument("--route", default="auto", choices=["auto", "home", "agent"])
    s.add_argument("--speaker")
    s.add_argument("--quiet", action="store_true", help="no audio")
    s.set_defaults(fn=cmd_ask)
    s = sub.add_parser("check", help="status of the hub and its engines")
    s.add_argument("--config")
    s.add_argument("--config-hub", help="check a hub config directly (on the family server)")
    s.set_defaults(fn=cmd_check)
    a = ap.parse_args(argv)
    return a.fn(a) or 0


if __name__ == "__main__":
    sys.exit(main())
