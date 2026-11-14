# SPDX-License-Identifier: AGPL-3.0-or-later
"""voice - the voice pipeline of Freedom (module voice, SPEC §3.8).

  hub    the family server part: HTTP API for voice turns. Speech to text and text to speech go to an OpenAI-compatible
         server (speaches: faster-whisper + piper), short home commands to Home Assistant (conversation API), the rest
         to the CO agent of a chosen chat (host op voice_ask of the module's server code). Standard library only.
  point  a voice point on a device (Windows, Linux, also headless): wake word on the device (openWakeWord + its silero
         VAD), the phrase goes to the hub, the answer plays on the device's speaker or on a Cast speaker through HA.
         Always-on listening is off until the device's owner turns it on (point-config --always-listen on).
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

VERSION = "0.1.0"
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
    "ru": {"agent_off": "Агент не настроен.", "agent_wait": "Агент не ответил вовремя.", "fail": "Не получилось.",
           "unheard": "Не расслышала."},
    "uk": {"agent_off": "Агента не налаштовано.", "agent_wait": "Агент не відповів вчасно.", "fail": "Не вийшло.",
           "unheard": "Не розчула."},
    "en": {"agent_off": "The agent is not set up.", "agent_wait": "The agent did not answer in time.",
           "fail": "That did not work.", "unheard": "I did not catch that."},
    "nl": {"agent_off": "De agent is niet ingesteld.", "agent_wait": "De agent antwoordde niet op tijd.",
           "fail": "Dat lukte niet.", "unheard": "Ik heb het niet verstaan."},
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


# ------------------------------------------------------------------ the hub
class Hub:
    def __init__(self, cfg):
        self.cfg = cfg
        self.key = secret(cfg.get("key"), "VOICE_HUB_KEY")
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
        self.co_chat = c.get("chatId") or ""
        self.co_env = c.get("env") or ""
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
        voice = self.voices.get(lang) or self.voices.get(self.default_lang)
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

    def agent(self, text, lang, chat=None, point=""):
        """The CO agent of the voice chat answers; None when it is not set up or does not answer in time."""
        chat = chat or self.co_chat
        if not (self.co_url and self.co_key and chat):
            return None, "agent_off"
        job = self.co("voice_ask", {"chatId": chat, "env": self.co_env, "text": text, "lang": lang, "point": point})
        if job.get("error") or not job.get("jobId"):
            return None, "fail"
        end = time.time() + self.co_wait
        while time.time() < end:
            time.sleep(1.5)
            r = self.co("voice_answer", {"jobId": job["jobId"]})
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
             speak=True, fmt="mp3", chat=None, point="", speaker=""):
        t0 = time.time()
        tm = {}
        out = {"route": None}
        if audio is not None:
            text, lang = self.transcribe(audio, filename, typ, lang if lang in self.langs else None)
            tm["stt"] = round(time.time() - t0, 2)
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
                route = "agent"
            if route in ("auto", "home"):
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
                ans, why = self.agent(text, lang, chat, point)
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
            self.end_headers()
            self.wfile.write(body)

        def _authed(self):
            got = (self.headers.get("Authorization") or "").removeprefix("Bearer ").strip()
            return bool(hub.key) and hmac.compare_digest(got.encode(), hub.key.encode())

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
            n = int(self.headers.get("Content-Length") or 0)
            if n > 25 * 1024 * 1024:
                return self._send(413, {"error": "too_large"})
            body = self.rfile.read(n) if n else b""
            if not self._authed():
                return self._send(401, {"error": "unauthorized"})
            q = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(self.path).query))
            ctype = self.headers.get("Content-Type") or ""
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
                    r = hub.turn(text=q.get("text"), audio=audio, filename=fn, typ=typ, lang=q.get("lang"),
                                 route=q.get("route") or "auto", speak=str(q.get("speak", "1")) not in ("0", "false"),
                                 fmt=q.get("format") or "mp3", chat=q.get("chat"), point=str(q.get("point") or "")[:40],
                                 speaker=q.get("speaker") or "")
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
    st["agent"] = {"configured": bool(hub.co_url and hub.co_key and hub.co_chat)}
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


def hub_call(c, path, payload=None, audio=None, timeout=180):
    url = c["hub"].rstrip("/") + path
    h = {"Authorization": f"Bearer {hub_key()}"}
    if audio is not None:
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
        sys.exit("point: set the hub (point-config --hub URL) and the key (keyring aiwb/voice-hub or VOICE_HUB_KEY)")
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
    model = None if a.once else Model(wakeword_models=[wake], inference_framework="onnx")
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
                           "openwakeword", ort, "keyring"])
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
