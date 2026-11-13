"""toolbox 1.7.0: tools of Freedom modules (install, module on/off/remove, MCP refusal, CLAUDE.md block).
Run: python toolbox/tests/test_modules.py (temporary library; never touches the real one or CLAUDE.md)."""
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parents[1]


def make_tool(root: pathlib.Path, name: str, version: str = "0.1.0"):
    d = root / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "SKILL.md").write_text(f"---\nname: {name}\ndescription: test tool {name}\nversion: {version}\nentry: {name}.py\n---\n\n# {name}\n",
                                encoding="utf-8")
    (d / f"{name}.py").write_text("print('hi')\n", encoding="utf-8")


class ToolModules(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.lib = self.tmp / "lib"
        (self.lib / ".git").mkdir(parents=True)          # toolbox writes to AIWB_TOOLS only when it is a working copy
        (self.lib / "toolbox").mkdir()
        for f in ("toolbox.py", "mcp_server.py", "modstate.py", "security.py", "SKILL.md"):
            (self.lib / "toolbox" / f).write_bytes((HERE / f).read_bytes())
        make_tool(self.lib, "core-tool")
        self.src = self.tmp / "src"
        make_tool(self.src, "mod-tool", "0.2.0")
        self.claude = self.tmp / "CLAUDE.md"
        self.claude.write_text("x\n<!-- aiwb-tools-index:begin -->\n<!-- aiwb-tools-index:end -->\n", encoding="utf-8")
        self.env = dict(os.environ, AIWB_TOOLS=str(self.lib), AIWB_TOOLS_MIRROR="", AIWB_CLAUDE_MD=str(self.claude),
                        AIWB_AGENTS_MD=str(self.tmp / "AGENTS.md"), AIWB_TOOL_MODULES=str(self.tmp / "tool-modules.json"),
                        PYTHONIOENCODING="utf-8")

    def tb(self, *args):
        return subprocess.run([sys.executable, str(self.lib / "toolbox" / "toolbox.py"), *args], env=self.env,
                              capture_output=True, text=True, encoding="utf-8")

    def mcp(self, name, args):
        msgs = [{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
                {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": name, "arguments": args}}]
        r = subprocess.run([sys.executable, str(self.lib / "toolbox" / "mcp_server.py")], env=self.env,
                           input="".join(json.dumps(m) + "\n" for m in msgs), capture_output=True, text=True, encoding="utf-8")
        out = [json.loads(x) for x in r.stdout.splitlines() if x.strip()]
        res = next(o for o in out if o.get("id") == 2)["result"]
        return res["content"][0]["text"], res.get("isError", False)

    def test_cycle(self):
        r = self.tb("install", "mod-tool", "--from", str(self.src))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertTrue((self.lib / "mod-tool" / "SKILL.md").exists())
        # same version again: present, nothing copied
        self.assertIn('"present"', self.tb("install", "mod-tool", "--from", str(self.src)).stdout)
        # newer offered: refused without --update, replaced with it
        make_tool(self.src, "mod-tool", "0.3.0")
        self.assertEqual(self.tb("install", "mod-tool", "--from", str(self.src)).returncode, 1)
        self.assertEqual(self.tb("install", "mod-tool", "--from", str(self.src), "--update").returncode, 0)
        self.assertIn("0.3.0", (self.lib / "mod-tool" / "SKILL.md").read_text(encoding="utf-8"))

        # a module that owns it, switched off: the MCP server refuses it, CLAUDE.md does not offer it
        self.assertEqual(self.tb("module", "mail", "--state", "disabled", "--tools", "mod-tool", "--version", "0.1.0").returncode, 0)
        text, err = self.mcp("tool_run", {"name": "mod-tool", "args": []})
        self.assertTrue(err)
        self.assertIn("module mail is off", text)
        text, _ = self.mcp("tool_list", {})
        self.assertIn("unavailable here: mod-tool (module mail is off", text)
        self.assertIn("core-tool", text)
        md = self.claude.read_text(encoding="utf-8")
        self.assertIn("core-tool", md)
        self.assertNotIn("mod-tool", md)
        self.assertIn("mod-tool", (self.lib / "INDEX.md").read_text(encoding="utf-8"))  # INDEX.md: the whole library

        # on: runs and is offered; a second owner that is on keeps it available when the first goes off
        self.tb("module", "mail", "--state", "enabled")
        text, err = self.mcp("tool_run", {"name": "mod-tool", "args": []})
        self.assertFalse(err, text)
        self.assertIn("hi", text)
        self.assertIn("mod-tool", self.claude.read_text(encoding="utf-8"))
        self.tb("module", "other", "--state", "enabled", "--tools", "mod-tool")
        self.tb("module", "mail", "--state", "disabled")
        self.assertFalse(self.mcp("tool_run", {"name": "mod-tool", "args": []})[1])
        st = json.loads(self.tb("modules", "--json").stdout)
        self.assertEqual(st["modules"]["mail"]["tools"], ["mod-tool"])   # tools kept when --tools is omitted
        self.assertFalse(st["modules"]["mail"]["enabled"])

        # removed: the record goes, the files stay
        self.tb("module", "other", "--state", "removed")
        self.tb("module", "mail", "--state", "removed")
        self.assertEqual(json.loads(self.tb("modules", "--json").stdout)["modules"], {})
        self.assertTrue((self.lib / "mod-tool" / "SKILL.md").exists())
        self.assertFalse(self.mcp("tool_run", {"name": "mod-tool", "args": []})[1])

    def test_no_state_file_changes_nothing(self):
        text, err = self.mcp("tool_run", {"name": "core-tool", "args": []})
        self.assertFalse(err, text)
        self.assertIn("no module tools recorded", self.tb("modules").stdout)

    def test_bad_id(self):
        self.assertEqual(self.tb("module", "Bad_Id", "--state", "enabled").returncode, 1)


if __name__ == "__main__":
    unittest.main()
