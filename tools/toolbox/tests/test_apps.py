"""toolbox 1.8.0/1.9.0: app_list / app_action / app_action_status of the aiwb-tools MCP server against a local fake db.php."""
import base64
import gzip
import json
import os
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import apps  # noqa: E402
import mcp_server  # noqa: E402

SEEN = []


class Fake(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        req = json.loads(gzip.decompress(base64.b64decode(body["gz"])))
        SEEN.append((self.headers.get("X-CO-Push-Key"), req))
        if req["op"] == "app_list":
            out = {"env": "env-a", "apps": [{"app": "smart-home", "actions": [
                {"op": "home_state", "outward": False}, {"op": "home_control", "outward": True},
                {"op": "boom", "outward": False}]}]}
        elif req["op"] == "app_action_status":
            out = {"ok": True, "requestId": req["requestId"], "status": "denied"} if req["requestId"] == "abc" else                 {"ok": False, "error": "unknown_request", "message": "No such request"}
        elif req["action"] == "home_control":
            out = {"ok": False, "pending": True, "status": "pending", "requestId": "abc", "expiresUtc": "2026-11-23T12:00:00Z",
                   "message": "waits for the person"}
        elif req["action"] == "boom":
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b'{"error":"args.x is not an argument of this action"}')
            return
        else:
            out = {"ok": True, "result": {"online": True, "entities": []}}
        data = json.dumps(out).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(data)


class AppsTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = HTTPServer(("127.0.0.1", 0), Fake)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.key = tempfile.NamedTemporaryFile("w", delete=False, suffix=".key")
        cls.key.write("k-test\n")
        cls.key.close()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        os.unlink(cls.key.name)

    def setUp(self):
        SEEN.clear()
        self.env = dict(os.environ)
        os.environ.update(AIWB_DB_URL="http://127.0.0.1:%d/agent/db.php" % self.srv.server_port,
                          AIWB_DB_KEY_FILE=self.key.name, AIWB_JOB_ID="JOB-T-1")

    def tearDown(self):
        os.environ.clear()
        os.environ.update(self.env)

    def call(self, name, arguments):
        r = mcp_server.handle({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": arguments}})
        return r["result"]["content"][0]["text"], r["result"]["isError"]

    def test_tools_listed(self):
        names = [t["name"] for t in mcp_server.handle({"jsonrpc": "2.0", "id": 1, "method": "tools/list"})["result"]["tools"]]
        self.assertIn("app_list", names)
        self.assertIn("app_action", names)
        self.assertIn("app_action_status", names)

    def test_list_sends_job_and_key(self):
        text, err = self.call("app_list", {})
        self.assertFalse(err)
        self.assertIn("smart-home", text)
        self.assertEqual(SEEN[0], ("k-test", {"op": "app_list", "jobId": "JOB-T-1"}))

    def test_action_ok(self):
        text, err = self.call("app_action", {"app": "smart-home", "action": "home_state"})
        self.assertFalse(err)
        self.assertEqual(json.loads(text), {"online": True, "entities": []})
        self.assertEqual(SEEN[1][1]["args"], {})

    def test_outward_pending(self):
        text, err = self.call("app_action", {"app": "smart-home", "action": "home_control", "args": {"entity": "light.x"}})
        self.assertFalse(err)
        self.assertEqual(json.loads(text)["status"], "pending")
        self.assertEqual(json.loads(text)["requestId"], "abc")

    def test_status(self):
        text, err = self.call("app_action_status", {"requestId": "abc"})
        self.assertFalse(err)
        self.assertEqual(json.loads(text), {"requestId": "abc", "status": "denied"})
        self.assertEqual(SEEN[0][1], {"op": "app_action_status", "requestId": "abc", "jobId": "JOB-T-1"})
        text, err = self.call("app_action_status", {"requestId": "zzz"})
        self.assertTrue(err)
        self.assertIn("No such request", text)
        self.assertTrue(self.call("app_action_status", {})[1])

    def test_bad_request_message(self):
        text, err = self.call("app_action", {"app": "smart-home", "action": "boom"})
        self.assertTrue(err)
        self.assertIn("args.x", text)

    def test_needs_job(self):
        del os.environ["AIWB_JOB_ID"]
        text, err = self.call("app_list", {})
        self.assertTrue(err)
        self.assertIn("AIWB_JOB_ID", text)
        self.assertEqual(SEEN, [])

    def test_bad_args(self):
        self.assertTrue(self.call("app_action", {"app": "smart-home"})[1])
        self.assertTrue(self.call("app_action", {"app": "smart-home", "action": "home_state", "args": [1]})[1])


class RelayRouteTest(unittest.TestCase):
    def setUp(self):
        self.params = {"query": "private-test-value"}
        self.descriptor = {"op": "lookup", "via": "home-relay", "outward": False}
        self.listing = {"apps": [{"app": "sample-app", "actions": [self.descriptor]}]}
        self.status = {"ok": True, "via": "home-relay", "app": "sample-app", "action": "lookup",
                       "outward": False, "env": "env-a", "version": "1.0.0",
                       "relay": {"pub": "public-test-key", "online": True, "served": True}}
        self.envelope = {"id": "a" * 32, "module": "sample-app", "pk": "public", "iv": "iv", "ct": "ciphertext"}

    def run_action(self):
        return apps.app_action({"app": "sample-app", "action": "lookup", "args": self.params})

    def test_ciphertext_only_and_bound_version(self):
        with patch.object(apps, "_call", side_effect=[(self.listing, None), (self.status, None),
                          ({"ok": True, "envelope": {"iv": "answer-iv", "ct": "answer-ct"}}, None)]) as call, \
                patch.object(apps, "_relay_client") as factory:
            client = factory.return_value.return_value
            client.seal.return_value = self.envelope
            client.open.return_value = {"found": 2}
            text, failed = self.run_action()
            self.assertFalse(failed)
            self.assertEqual(json.loads(text), {"found": 2})
            factory.return_value.assert_called_once_with("env-a", "sample-app", "public-test-key")
            client.seal.assert_called_once_with("lookup", self.params, app_version="1.0.0")
            self.assertNotIn("private-test-value", json.dumps(call.call_args_list, default=str))
            self.assertEqual(call.call_args.args, ("app_relay_call", {"app": "sample-app", "action": "lookup",
                             "version": "1.0.0", "envelope": {k: self.envelope[k] for k in ("id", "pk", "iv", "ct")}}))
            client.open.assert_called_once_with("a" * 32, {"iv": "answer-iv", "ct": "answer-ct"})
            client.discard.assert_called_once_with("a" * 32)

    def test_discovery_failure_never_falls_back_to_plaintext(self):
        with patch.object(apps, "_call", return_value=(None, "offline")) as call:
            self.assertTrue(self.run_action()[1])
            self.assertEqual(call.call_count, 1)
            self.assertEqual(call.call_args.args, ("app_list", {}))

    def test_unknown_transport_and_invalid_outward_are_closed(self):
        for change in ({"outward": "true"}, {"via": "unknown"}):
            with self.subTest(change=change):
                original = dict(self.descriptor)
                self.descriptor.update(change)
                with patch.object(apps, "_call", return_value=(self.listing, None)) as call:
                    self.assertTrue(self.run_action()[1])
                    self.assertEqual(call.call_count, 1)
                self.descriptor.clear()
                self.descriptor.update(original)

    def test_status_identity_and_availability(self):
        for field, value in (("app", "another-app"), ("action", "write"), ("outward", True),
                             ("version", ""), ("env", ""), ("relay", {"online": False})):
            with self.subTest(field=field), patch.object(apps, "_call", side_effect=[
                    (self.listing, None), (dict(self.status, **{field: value}), None)]) as call:
                self.assertTrue(self.run_action()[1])
                self.assertEqual(call.call_count, 2)

    def test_timeout_is_not_retried_or_exposed(self):
        with patch.object(apps, "_call", side_effect=[(self.listing, None), (self.status, None),
                          (None, "private-test-value")]) as call, patch.object(apps, "_relay_client") as factory:
            client = factory.return_value.return_value
            client.seal.return_value = self.envelope
            text, failed = self.run_action()
            self.assertTrue(failed)
            self.assertNotIn("private-test-value", text)
            self.assertIn("outcome unknown", text)
            self.assertEqual(call.call_count, 3)
            client.open.assert_not_called()
            client.discard.assert_called_once()

    def test_bad_crypto_answer_never_becomes_success(self):
        with patch.object(apps, "_call", side_effect=[(self.listing, None), (self.status, None),
                          ({"ok": True, "envelope": {}}, None)]), patch.object(apps, "_relay_client") as factory:
            client = factory.return_value.return_value
            client.seal.return_value = self.envelope
            client.open.side_effect = ValueError("private-test-value")
            text, failed = self.run_action()
            self.assertTrue(failed)
            self.assertNotIn("private-test-value", text)
            client.discard.assert_called_once()


if __name__ == "__main__":
    unittest.main()
