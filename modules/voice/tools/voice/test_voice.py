# SPDX-License-Identifier: AGPL-3.0-or-later
"""Tests of the voice hub with fake speaches, Home Assistant and CO servers on local ports: python test_voice.py"""
import base64
import gzip
import importlib.util
import json
import os
import sys
import threading
import unittest
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("voice", os.path.join(HERE, "voice.py"))
voice = importlib.util.module_from_spec(spec)
spec.loader.exec_module(voice)

CALLS = []


class Fake(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _json(self, obj, code=200):
        b = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        if self.path == "/v1/models":
            return self._json({"data": [{"id": "small"}, {"id": "turbo"}, {"id": voice.DEFAULT_VOICES["ru"]}]})
        return self._json({"message": "API running."})

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        CALLS.append((self.path, self.headers.get("Authorization"), body))
        if self.path == "/v1/audio/transcriptions":
            f, files = voice.parse_multipart(body, self.headers["Content-Type"])
            return self._json({"text": "который час" if b"time" in files["file"][1] else "расскажи анекдот",
                               "language": "russian", "model": f["model"]})
        if self.path == "/v1/audio/speech":
            d = json.loads(body)
            data = ("AUDIO:" + d["input"]).encode()
            self.send_response(200)
            self.send_header("Content-Type", "audio/mpeg")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            return self.wfile.write(data)
        if self.path == "/api/conversation/process":
            d = json.loads(body)
            if "час" in d["text"]:
                return self._json({"response": {"response_type": "action_done", "speech": {"plain": {"speech": "10:42"}}}})
            if "умножить" in d["text"]:
                return self._json({"response": {"response_type": "error", "data": {"code": "no_valid_targets"},
                                                "speech": {"plain": {"speech": "Нет объекта"}}}})
            return self._json({"response": {"response_type": "error", "data": {"code": "no_intent_match"},
                                            "speech": {"plain": {"speech": "Не понял"}}}})
        if self.path == "/db.php":
            inner = json.loads(gzip.decompress(base64.b64decode(json.loads(body)["gz"])))
            if inner["op"] == "voice_ask":
                return self._json({"jobId": "JOB-1"})
            if inner["op"] == "voice_answer":
                return self._json({"state": "completed", "answer": "**Кот** зашёл в бар. [ссылка](https://x.y)"})
        return self._json({"error": "unknown"}, 404)


def serve(handler):
    s = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=s.serve_forever, daemon=True).start()
    return s


class HubTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fake = serve(Fake)
        base = f"http://127.0.0.1:{cls.fake.server_port}"
        os.environ["VOICE_HUB_KEY"] = "k1"
        os.environ["T_PT"] = "p1"
        cls.hub = voice.Hub({
            "speaches": {"url": base, "sttModel": "turbo", "sttModelShort": "small"},
            "homeAssistant": {"url": base, "token": {"env": "T_HA"}},
            "co": {"dbUrl": base + "/db.php", "hostKey": {"env": "T_CO"}, "chatId": "CHAT-0", "waitSeconds": 10},
            "points": {"kitchen": {"key": {"env": "T_PT"}, "env": "me", "chat": "CHAT-1", "chats": ["CHAT-2"]}},
        })
        cls.hub.ha_token, cls.hub.co_key = "ha", "co"
        cls.srv = serve(voice.make_handler(cls.hub))
        cls.url = f"http://127.0.0.1:{cls.srv.server_port}"

    def setUp(self):
        CALLS.clear()

    def post(self, path, payload, key="k1"):
        req = urllib.request.Request(self.url + path, json.dumps(payload).encode(),
                                     {"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_auth(self):
        try:
            self.assertEqual(self.post("/v1/voice/turn", {"text": "x"}, key="bad")[0], 401)
        except (ConnectionAbortedError, ConnectionResetError):
            pass  # the hub closes before reading the body; Windows may reset the socket before the 401 is read
        with urllib.request.urlopen(self.url + "/voice/v1/voice/health") as r:  # Funnel path prefix, no key
            self.assertTrue(json.loads(r.read())["ok"])

    def test_home_command_text(self):
        code, r = self.post("/v1/voice/turn", {"text": "который час", "lang": "ru"})
        self.assertEqual((code, r["route"], r["reply"]), (200, "home", "10:42"))
        self.assertEqual(base64.b64decode(r["audio"]["b64"]), "AUDIO:10:42".encode())

    def asks(self):
        return [json.loads(gzip.decompress(base64.b64decode(json.loads(c[2])["gz"]))) for c in CALLS if c[0] == "/db.php"]

    def test_ha_error_goes_to_agent_and_markdown_is_cleaned(self):
        code, r = self.post("/v1/voice/turn", {"text": "сколько будет семь умножить на восемь", "lang": "ru", "speak": False}, key="p1")
        self.assertEqual((r["route"], r["home"]), ("agent", "no_valid_targets"))
        self.assertEqual(r["reply"], "Кот зашёл в бар. ссылка")
        self.assertNotIn("audio", r)  # JSON false = no speech
        ask = self.asks()[0]
        self.assertEqual((ask["op"], ask["chatId"], ask["env"], ask["point"], ask["shell"]), ("voice_ask", "CHAT-1", "me", "kitchen", False))

    def test_point_chat_must_be_allowed(self):
        self.post("/v1/voice/turn", {"text": "агент, привет", "lang": "ru", "speak": "0", "chat": "CHAT-2"}, key="p1")
        self.post("/v1/voice/turn", {"text": "агент, привет", "lang": "ru", "speak": "0", "chat": "CHAT-EVIL"}, key="p1")
        self.assertEqual([a["chatId"] for a in self.asks() if a["op"] == "voice_ask"], ["CHAT-2", "CHAT-1"])

    def test_client_key_never_reaches_the_agent(self):
        code, r = self.post("/v1/voice/turn", {"text": "агент, расскажи анекдот", "lang": "ru", "route": "agent", "speak": "0"})
        self.assertEqual((r["route"], r.get("reply")), ("agent", None))
        self.assertEqual(self.asks(), [])

    def test_bad_length_refused_before_reading(self):
        import http.client
        c = http.client.HTTPConnection("127.0.0.1", self.srv.server_port, timeout=5)
        c.putrequest("POST", "/v1/voice/turn")
        c.putheader("Authorization", "Bearer k1")
        c.putheader("Content-Length", "-1")
        c.endheaders()
        self.assertEqual(c.getresponse().status, 413)
        c = http.client.HTTPConnection("127.0.0.1", self.srv.server_port, timeout=5)
        c.putrequest("POST", "/v1/voice/turn")
        c.putheader("Content-Length", str(100 * 1024 * 1024))
        c.endheaders()  # no body sent: the hub answers 401 without waiting for it
        self.assertEqual(c.getresponse().status, 401)

    def test_agent_prefix_skips_home(self):
        code, r = self.post("/v1/voice/turn", {"text": "Агент, который час", "lang": "ru", "speak": "0"}, key="p1")
        self.assertEqual(r["route"], "agent")
        self.assertFalse(any(c[0] == "/api/conversation/process" for c in CALLS))

    def test_client_route_does_not_wait_for_agent(self):
        code, r = self.post("/v1/voice/turn", {"text": "расскажи анекдот", "lang": "ru", "route": "client"})
        self.assertEqual((r["route"], r["text"]), ("agent", "расскажи анекдот"))
        self.assertNotIn("audio", r)
        self.assertFalse(any(c[0] == "/db.php" for c in CALLS))

    def test_client_route_still_answers_home_commands(self):
        code, r = self.post("/v1/voice/turn", {"text": "который час", "lang": "ru", "route": "client"})
        self.assertEqual((r["route"], r["reply"]), ("home", "10:42"))
        self.assertIn("audio", r)

    def test_audio_short_phrase_uses_small_model(self):
        wav = voice.wav_bytes(b"\x00\x00" * 16000)  # 1 s
        body, ct = voice.multipart({"speak": "0"}, {"file": ("p.wav", wav[:-4] + b"time", "audio/wav")})
        req = urllib.request.Request(self.url + "/v1/voice/turn", body, {"Authorization": "Bearer k1", "Content-Type": ct})
        r = json.loads(urllib.request.urlopen(req, timeout=20).read())
        self.assertEqual((r["route"], r["lang"]), ("home", "ru"))
        f, _ = voice.parse_multipart(CALLS[0][2], "multipart/form-data; boundary=" + CALLS[0][2].split(b"\r\n")[0][2:].decode())
        self.assertEqual(f["model"], "small")

    def test_empty_phrase(self):
        code, r = self.post("/v1/voice/turn", {"text": " . ", "lang": "en", "speak": "0"})
        self.assertEqual((r["route"], r["reply"]), ("none", voice.SAY["en"]["unheard"]))

    def test_speak_returns_audio(self):
        req = urllib.request.Request(self.url + "/v1/voice/speak", json.dumps({"text": "Привет", "lang": "ru"}).encode(),
                                     {"Authorization": "Bearer k1", "Content-Type": "application/json"})
        with urllib.request.urlopen(req) as r:
            self.assertEqual((r.headers["Content-Type"], r.read()), ("audio/mpeg", "AUDIO:Привет".encode()))

    def test_helpers(self):
        self.assertEqual(voice.norm_lang("dutch", ("ru", "nl"), "ru"), "nl")
        self.assertEqual(voice.norm_lang("ja", ("ru", "nl"), "ru"), "ru")
        self.assertAlmostEqual(voice.audio_seconds(voice.wav_bytes(b"\x00\x00" * 8000)), 0.5)
        self.assertEqual(voice.audio_seconds(b"x" * 8000, "audio/webm"), 2.0)


class FakeVP:
    """Speaker recognition stand-in: the WAV's last 4 bytes name the speaker (b'ALIC' -> alice, else nobody)."""
    ok = True
    model = "fake.onnx"

    def __init__(self):
        self.enrolled_clips = {}

    def match(self, wav, people):
        p = "alice" if wav.endswith(b"ALIC") and "alice" in people else None
        return {"person": p, "score": 0.8 if p else 0.2, "scores": {}}

    def enroll(self, person, wavs, replace=False):
        self.enrolled_clips[person] = len(wavs)
        return {"person": person, "n": len(wavs), "clips": len(wavs), "skipped": 0, "self": []}

    def forget(self, person):
        return self.enrolled_clips.pop(person, None) is not None

    def enrolled(self):
        return sorted(self.enrolled_clips)


class SpeakerTest(HubTest):
    """Level 2: a point with people answers as the recognised person; a stranger gets home commands only."""

    @classmethod
    def setUpClass(cls):
        cls.fake = serve(Fake)
        base = f"http://127.0.0.1:{cls.fake.server_port}"
        os.environ["VOICE_HUB_KEY"] = "k1"
        os.environ["T_PT"] = "p1"
        os.environ["T_P2"] = "p2"
        cls.hub = voice.Hub({
            "speaches": {"url": base, "sttModel": "turbo", "sttModelShort": "small"},
            "homeAssistant": {"url": base, "token": {"env": "T_HA"}},
            "co": {"dbUrl": base + "/db.php", "hostKey": {"env": "T_CO"}, "chatId": "CHAT-0", "waitSeconds": 10},
            "people": {"alice": {"env": "alice", "chat": "CHAT-A"}, "kid": {"env": "kid", "chat": "CHAT-K"},
                       "../x": {"env": "evil"}},
            "points": {"kitchen": {"key": {"env": "T_PT"}, "env": "me", "chat": "CHAT-1", "chats": ["CHAT-2"]},
                       "pc": {"key": {"env": "T_P2"}, "env": "alice", "chat": "CHAT-1", "people": ["alice", "../x"]}},
        })
        cls.hub.ha_token, cls.hub.co_key = "ha", "co"
        cls.hub.vp = FakeVP()
        cls.srv = serve(voice.make_handler(cls.hub))
        cls.url = f"http://127.0.0.1:{cls.srv.server_port}"

    def turn_audio(self, tail, key="p2"):
        wav = voice.wav_bytes(b"\x00\x00" * 16000) + tail  # transcribed as "расскажи анекдот" -> agent
        body, ct = voice.multipart({"speak": "0"}, {"file": ("p.wav", wav, "audio/wav")})
        req = urllib.request.Request(self.url + "/v1/voice/turn", body, {"Authorization": f"Bearer {key}", "Content-Type": ct})
        return json.loads(urllib.request.urlopen(req, timeout=20).read())

    def test_bad_person_ids_dropped(self):
        self.assertEqual(sorted(self.hub.people), ["kid", "alice"])
        self.assertEqual(self.hub.points["pc"]["people"], ["alice"])
        self.assertEqual(self.hub.points["pc"]["unknown"], "home")

    def test_recognised_voice_asks_as_the_person(self):
        r = self.turn_audio(b"ALIC")
        self.assertEqual((r["route"], r["speaker"]["person"]), ("agent", "alice"))
        ask = [a for a in self.asks() if a["op"] == "voice_ask"][0]
        self.assertEqual((ask["env"], ask["chatId"]), ("alice", "CHAT-A"))

    def test_stranger_gets_home_only(self):
        r = self.turn_audio(b"XXXX")
        self.assertEqual((r["route"], r["speaker"]["person"], r["reply"]), ("agent", None, voice.SAY["ru"]["stranger"]))
        self.assertEqual([a for a in self.asks() if a["op"] == "voice_ask"], [])

    def test_point_without_people_unchanged(self):
        r = self.turn_audio(b"XXXX", key="p1")
        self.assertNotIn("speaker", r)
        self.assertEqual([a["env"] for a in self.asks() if a["op"] == "voice_ask"], ["me"])

    def enroll(self, person, key="p2"):
        wav = voice.wav_bytes(b"\x00\x00" * 16000)
        body, ct = voice.multipart({"person": person}, {"file00": ("a.wav", wav, "audio/wav"), "file01": ("b.wav", wav, "audio/wav")})
        req = urllib.request.Request(self.url + "/v1/voice/enroll", body, {"Authorization": f"Bearer {key}", "Content-Type": ct})
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_enroll_only_own_people_with_point_key(self):
        self.assertEqual(self.enroll("alice"), (200, {"person": "alice", "n": 2, "clips": 2, "skipped": 0, "self": []}))
        self.assertEqual(self.enroll("kid")[0], 403)          # not a person of this point
        self.assertEqual(self.enroll("alice", key="p1")[0], 403)  # kitchen has no people
        self.assertEqual(self.enroll("alice", key="k1")[0], 403)  # client key never enrolls
        self.assertEqual(self.post("/v1/voice/forget", {"person": "alice"}, key="p2"), (200, {"person": "alice", "forgotten": True}))


class VoiceprintMathTest(unittest.TestCase):
    def test_enroll_match_and_model_change(self):
        import tempfile
        import numpy as np
        d = tempfile.mkdtemp()
        model = os.path.join(d, "m.onnx")
        open(model, "wb").close()
        vp = voice.Voiceprints({"model": model, "dir": os.path.join(d, "vp"), "threshold": 0.6})
        vecs = {b"A": np.array([1, 0, 0], np.float32), b"B": np.array([0, 1, 0], np.float32),
                b"C": np.array([0.9, 0.1, 0], np.float32)}
        vp.embed = lambda w: vecs[w[:1]] / np.linalg.norm(vecs[w[:1]])
        vp.enroll("a", [b"A1", b"C1"])
        vp.enroll("b", [b"B1"])
        self.assertEqual(vp.match(b"A2", ["a", "b"])["person"], "a")
        self.assertEqual(vp.match(b"B2", ["a"])["person"], None)  # b is not asked for
        self.assertEqual(vp.enrolled(), ["a", "b"])
        vp.model = os.path.join(d, "other.onnx")  # another model: old voiceprints are not comparable
        self.assertEqual(vp.match(b"A2", ["a", "b"])["person"], None)


if __name__ == "__main__":
    sys.exit(unittest.main(verbosity=1))
