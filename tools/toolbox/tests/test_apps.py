"""toolbox 1.8.0: app_list / app_action of the aiwb-tools MCP server against a local fake db.php."""
import base64
import gzip
import json
import os
import sys
import tempfile
import threading
import unittest
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
            out = {"env": "env-a", "apps": [{"app": "smart-home", "actions": [{"op": "home_state", "outward": False}]}]}
        elif req["action"] == "home_control":
            out = {"ok": False, "error": "outward", "message": "only the person runs it"}
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

    def test_list_sends_job_and_key(self):
        text, err = self.call("app_list", {})
        self.assertFalse(err)
        self.assertIn("smart-home", text)
        self.assertEqual(SEEN[0], ("k-test", {"op": "app_list", "jobId": "JOB-T-1"}))

    def test_action_ok(self):
        text, err = self.call("app_action", {"app": "smart-home", "action": "home_state"})
        self.assertFalse(err)
        self.assertEqual(json.loads(text), {"online": True, "entities": []})
        self.assertEqual(SEEN[0][1]["args"], {})

    def test_outward_refused(self):
        text, err = self.call("app_action", {"app": "smart-home", "action": "home_control", "args": {"entity": "light.x"}})
        self.assertTrue(err)
        self.assertIn("only the person", text)

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


if __name__ == "__main__":
    unittest.main()
