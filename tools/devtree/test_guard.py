"""devtree 0.8.0: config file for the main tree, .devtree.json guard before merge and push, hooksPath."""
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
DEVTREE = HERE / "devtree.py"
ID = ["-c", "user.name=t", "-c", "user.email=t@example.org"]

GUARD = """import subprocess, sys
diff = subprocess.run(["git", "log", "-p", sys.argv[1]], capture_output=True, text=True).stdout
sys.exit(2 if "SECRET" in diff else 0)
"""


def git(cwd, *args):
    return subprocess.run(["git", *ID, *args], cwd=cwd, capture_output=True, text=True, check=True).stdout.strip()


class GuardTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        t = pathlib.Path(self.tmp.name)
        self.origin, self.repo, self.work, self.locks = t / "origin.git", t / "main", t / "work", t / "locks"
        git(t, "init", "-q", "--bare", "-b", "main", str(self.origin))
        git(t, "clone", "-q", str(self.origin), str(self.repo))
        git(self.repo, "checkout", "-q", "-b", "main")
        (self.repo / ".devtree.json").write_text(json.dumps({"guard": ["python", "guard.py", "{range}"],
                                                             "hooksPath": "hooks"}), encoding="utf-8")
        (self.repo / "guard.py").write_text(GUARD, encoding="utf-8")
        (self.repo / "README.md").write_text("x\n", encoding="utf-8")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "init")
        git(self.repo, "push", "-q", "origin", "main")
        cfg = t / "devtree.json"
        cfg.write_text(json.dumps({"repo": str(self.repo), "mirror": "", "workRoot": str(self.work),
                                   "locks": str(self.locks)}), encoding="utf-8")
        self.env = {k: v for k, v in os.environ.items() if k not in ("AIWB_SOURCE", "AIWB_MIRROR")}
        self.env.update(AIWB_DEVTREE_CONFIG=str(cfg), GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.org",
                        GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.org")

    def tearDown(self):
        subprocess.run(["git", "worktree", "prune"], cwd=self.repo, capture_output=True)
        self.tmp.cleanup()

    def devtree(self, *args):
        p = subprocess.run([sys.executable, str(DEVTREE), "--json", *args], capture_output=True, text=True,
                           env=self.env, cwd=self.tmp.name)
        return p.returncode, json.loads(p.stdout or "{}")

    def finish(self, name):
        return self.devtree("finish", name, "-m", "change", "--review", "off", "--e2e", "off")

    def test_config_file_selects_main_tree_and_guard_blocks(self):
        rc, d = self.devtree("start", "a")
        self.assertEqual(rc, 0, d)
        wt = pathlib.Path(d["path"])
        self.assertTrue(str(wt).startswith(str(self.work)))  # workRoot from the config file
        self.assertEqual(git(self.repo, "config", "--get", "core.hooksPath"), "hooks")
        (wt / "a.txt").write_text("SECRET\n", encoding="utf-8")
        rc, d = self.finish("a")
        self.assertEqual(rc, 7, d)
        self.assertFalse(d["guard"]["ok"])
        self.assertEqual(git(self.repo, "rev-list", "--count", "main"), "1")  # nothing merged
        # --force does not bypass the guard
        rc, d = self.devtree("finish", "a", "-m", "x", "--review", "off", "--e2e", "off", "--force")
        self.assertEqual(rc, 7, d)
        # rewrite the branch (nothing is public yet) and finish
        (wt / "a.txt").write_text("fine\n", encoding="utf-8")
        git(wt, "commit", "-q", "-a", "--amend", "-m", "change")
        rc, d = self.finish("a")
        self.assertEqual(rc, 0, d)
        self.assertTrue(d["guard"]["ok"])
        self.assertTrue(d["publish"]["pushed"])
        self.assertEqual(git(self.origin, "rev-parse", "main"), git(self.repo, "rev-parse", "main"))

    def test_push_guard_refuses_unpushed_commit_on_main(self):
        (self.repo / "b.txt").write_text("SECRET\n", encoding="utf-8")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "direct")
        rc, d = self.devtree("sync")
        self.assertEqual(rc, 7, d)
        self.assertIs(d["publish"]["guard"], False)
        self.assertNotEqual(git(self.origin, "rev-parse", "main"), git(self.repo, "rev-parse", "main"))

    def test_repo_without_devtree_json_is_unchanged(self):
        git(self.repo, "rm", "-q", ".devtree.json")
        git(self.repo, "commit", "-q", "-m", "no guard")
        rc, d = self.devtree("start", "b")
        self.assertEqual(rc, 0, d)
        (pathlib.Path(d["path"]) / "c.txt").write_text("SECRET\n", encoding="utf-8")
        rc, d = self.finish("b")
        self.assertEqual(rc, 0, d)
        self.assertNotIn("guard", d)


if __name__ == "__main__":
    unittest.main()
