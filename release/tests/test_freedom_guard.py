# SPDX-License-Identifier: AGPL-3.0-or-later
"""Guard (the export as a check of the public repository) and sync (one normal commit on a clone)."""
import io
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import export_freedom as ef  # noqa: E402
import freedom_guard as fg  # noqa: E402

ID = ["-c", "user.name=Freedom contributors", "-c", "user.email=noreply@example.org"]


def git(repo, *args, ident=ID):
    return subprocess.run(["git", *ident, *args], cwd=repo, capture_output=True, text=True, check=True).stdout.strip()


def write(repo, path, text):
    p = pathlib.Path(repo) / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8", newline="\n")


class GuardTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = pathlib.Path(self.tmp.name) / "freedom"
        self.repo.mkdir()
        git(self.repo, "init", "-q", "-b", "main")
        write(self.repo, "README.md", "Freedom\n")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "Freedom 0.1.0: initial")
        self.base = git(self.repo, "rev-parse", "HEAD")
        self.terms_file = pathlib.Path(self.tmp.name) / "terms.txt"
        self.terms_file.write_text("Zorglub\nzorg-host\n", encoding="utf-8")
        self.terms = ef.load_terms(self.terms_file)
        self.owner = pathlib.Path(self.tmp.name) / "owner.json"
        self.owner.write_text(json.dumps({"exclude": ["ops/publish_site.py"]}), encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def cfg(self):
        return ef.load_config(self.owner)

    def guard(self, rng=None):
        return ef.guard(self.repo, "HEAD", rng, self.cfg(), self.terms)["findings"]

    def commit(self, files, msg="core: change", ident=ID):
        for p, t in files.items():
            write(self.repo, p, t)
        git(self.repo, "add", "-A", ident=ident)
        git(self.repo, "commit", "-q", "-m", msg, ident=ident)

    def test_clean_commit_passes(self):
        self.commit({"bridge/x.cs": "// mail anna@example.org\n"})
        self.assertEqual(self.guard(f"{self.base}..HEAD"), [])

    def test_history_is_checked_not_only_the_tip(self):
        # a private word added and removed again stays in the history: refused
        self.commit({"docs/a.md": "made by Zorglub\n"})
        self.commit({"docs/a.md": "made by someone\n"})
        self.assertEqual(self.guard(), [])  # the tip alone is clean
        f = self.guard(f"{self.base}..HEAD")
        self.assertEqual([(x["file"], x["line"]) for x in f], [("docs/a.md", 1)])
        self.assertEqual(f[0]["text"], "(hidden: private term)")

    def test_message_author_and_file_names(self):
        self.commit({"docs/zorg-host-notes.md": "x\n"}, msg="core: for Zorglub")
        self.commit({"docs/b.md": "y\n"}, ident=["-c", "user.name=Somebody", "-c", "user.email=somebody@realmail.be"])
        rules = sorted({x["rule"].split(" (commit")[0] for x in self.guard(f"{self.base}..HEAD")})
        self.assertEqual(rules, ["author e-mail outside the example domains", "committer e-mail outside the example domains",
                                 "private term", "private term in a file name"])

    def test_github_noreply_identity_is_public(self):
        self.commit({"docs/c.md": "z\n"}, ident=["-c", "user.name=Freedom contributors",
                                                  "-c", "user.email=someone@users.noreply.github.com"])
        self.assertEqual(self.guard(f"{self.base}..HEAD"), [])

    def test_owner_private_file_refused(self):
        self.commit({"ops/publish_site.py": "print('hi')\n"})
        f = self.guard()
        self.assertTrue(any(x["rule"] == "private file of the owner's tree" for x in f))

    def test_secret_in_added_line_with_line_number(self):
        self.commit({"co-web/src/a.ts": "const a = 1;\n"})
        self.commit({"co-web/src/a.ts": "const a = 1;\nconst k = 'ghp_" + "A" * 36 + "';\n"})
        f = self.guard(f"{self.base}..HEAD")
        self.assertTrue(any(x["file"] == "co-web/src/a.ts" and x["line"] == 2 and x["rule"].startswith("GitHub token")
                            for x in f), f)

    def test_pre_push_hook_refuses_and_allows(self):
        self.commit({"docs/d.md": "Zorglub\n"})
        head = git(self.repo, "rev-parse", "HEAD")
        git(self.repo, "config", "freedom.terms", str(self.terms_file))
        line = f"refs/heads/main {head} refs/heads/main {self.base}"
        with redirect_stdout(io.StringIO()):
            self.assertEqual(fg.pre_push(self.repo, [line]), 2)
            git(self.repo, "reset", "-q", "--hard", self.base)
            self.commit({"docs/d.md": "fine\n"})
            head = git(self.repo, "rev-parse", "HEAD")
            self.assertEqual(fg.pre_push(self.repo, [f"refs/heads/main {head} refs/heads/main {self.base}"]), 0)
            # deleting a remote branch publishes nothing
            self.assertEqual(fg.pre_push(self.repo, [f"(delete) {'0' * 40} refs/heads/x {head}"]), 0)

    def test_quoted_paths_and_plus_lines_are_scanned(self):
        # git C-quotes names with non-ASCII bytes, tabs or quotes; an added "++ x" line reads "+++ x" in the hunk
        self.commit({'docs/café q.md': "x\nZorglub\n", "docs/p.md": "++ header-like\nZorglub\n"})
        self.commit({'docs/café q.md': "x\n", "docs/p.md": "++ header-like\n"})
        f = self.guard(f"{self.base}..HEAD")
        self.assertEqual(sorted((x["file"], x["line"]) for x in f), [('docs/café q.md', 2), ("docs/p.md", 2)])
        self.assertEqual(ef.unquote_path('"b/caf\\303\\251 \\"q\\".md"'), 'b/café "q".md')

    def test_new_remote_branch_checks_history_not_on_that_remote(self):
        # history known to another (private) remote is still checked when pushed to a new public remote
        self.commit({"docs/e.md": "Zorglub\n"})
        self.commit({"docs/e.md": "clean\n"})
        head = git(self.repo, "rev-parse", "HEAD")
        private = pathlib.Path(self.tmp.name) / "private.git"
        public = pathlib.Path(self.tmp.name) / "public.git"
        for bare in (private, public):
            git(self.tmp.name, "init", "-q", "--bare", str(bare))
        git(self.repo, "remote", "add", "private", str(private))
        git(self.repo, "remote", "add", "public", str(public))
        git(self.repo, "push", "-q", "private", "main")
        git(self.repo, "config", "freedom.terms", str(self.terms_file))
        line = f"refs/heads/main {head} refs/heads/main {'0' * 40}"
        with redirect_stdout(io.StringIO()):
            self.assertEqual(fg.pre_push(self.repo, [line], "public"), 2)
            self.assertEqual(fg.pre_push(self.repo, [line], str(public)), 2)  # a bare URL: whole history
            # history already on the public remote is not checked again: only the new commit
            git(self.repo, "push", "-q", "public", "main")
            self.commit({"docs/f.md": "clean again\n"})
            new = git(self.repo, "rev-parse", "HEAD")
            self.assertEqual(fg.pre_push(self.repo, [f"refs/heads/x {new} refs/heads/x {'0' * 40}"], "public"), 0)

    def test_user_diff_config_does_not_hide_lines(self):
        git(self.repo, "config", "diff.noprefix", "true")
        git(self.repo, "config", "diff.dstPrefix", "y/")
        self.commit({"docs/g.md": "Zorglub\n"})
        self.commit({"docs/g.md": "ok\n"})
        self.assertEqual([x["file"] for x in self.guard(f"{self.base}..HEAD")], ["docs/g.md"])

    def test_annotated_tag_tagger_and_message(self):
        git(self.repo, "tag", "-a", "v9", "-m", "release by Zorglub",
            ident=["-c", "user.name=Somebody", "-c", "user.email=somebody@realmail.be"])
        tag = git(self.repo, "rev-parse", "v9")
        f = ef.guard(self.repo, tag, None, self.cfg(), self.terms)["findings"]
        self.assertEqual(sorted(x["rule"] for x in f), ["private term", "tagger e-mail outside the example domains"])

    def test_missing_terms_file_is_an_error_when_required(self):
        with redirect_stdout(io.StringIO()):
            rc = ef.main(["guard", "--repo", str(self.repo), "--terms", str(self.terms_file) + ".missing",
                          "--require-terms"])
        self.assertEqual(rc, 1)


class SyncTest(unittest.TestCase):
    def test_sync_writes_one_commit_and_removes_stale_files(self):
        with tempfile.TemporaryDirectory() as d:
            clone = pathlib.Path(d)
            git(clone, "init", "-q", "-b", "main")
            write(clone, "README.md", "old\n")
            write(clone, "stale.txt", "x\n")
            git(clone, "add", "-A")
            git(clone, "commit", "-q", "-m", "0.1.0")
            git(clone, "config", "user.name", "Freedom contributors")
            git(clone, "config", "user.email", "noreply@example.org")
            r = ef.sync_clone({"README.md": b"new\n", "a/b.txt": b"b\n"}, clone, "core: sync", None)
            self.assertEqual(r["removed"], ["stale.txt"])
            self.assertEqual(git(clone, "log", "-1", "--format=%an|%s"), "Freedom contributors|core: sync")
            self.assertEqual(sorted(git(clone, "ls-files").split()), ["README.md", "a/b.txt"])
            self.assertIsNone(ef.sync_clone({"README.md": b"new\n", "a/b.txt": b"b\n"}, clone, "again", None)["commit"])
            # executable bits of the source commit survive (a hook without +x is ignored on Unix)
            ef.EXEC_PATHS.clear()
            ef.EXEC_PATHS.add("hooks/pre-push")
            ef.sync_clone({"README.md": b"new\n", "a/b.txt": b"b\n", "hooks/pre-push": b"#!/bin/sh\n"}, clone, "m2", None)
            self.assertTrue(git(clone, "ls-files", "-s", "hooks/pre-push").startswith("100755"))
            ef.EXEC_PATHS.clear()
            # a link in the clone is never written through
            try:
                (clone / "lnk").symlink_to(pathlib.Path(d).parent, target_is_directory=True)
            except OSError:
                pass  # no right to make symbolic links here
            else:
                git(clone, "add", "-A")
                git(clone, "commit", "-q", "-m", "link")
                with self.assertRaises(RuntimeError):
                    ef.sync_clone({"lnk": b"x", "lnk/evil.txt": b"x\n"}, clone, "m3", None)
                git(clone, "reset", "-q", "--hard")
            write(clone, "dirty.txt", "x\n")
            with self.assertRaises(RuntimeError):
                ef.sync_clone({}, clone, "m", None)


class OwnerConfigTest(unittest.TestCase):
    def test_owner_part_is_merged_and_required_for_export(self):
        with tempfile.TemporaryDirectory() as d:
            p = pathlib.Path(d) / "o.json"
            p.write_text(json.dumps({"exclude": ["x/private.py"], "rewrite": [], "moduleIdAllow": {"canvas": ["a/b.ts"]}}),
                         encoding="utf-8")
            cfg = ef.load_config(p)
            self.assertIn("x/private.py", cfg["exclude"])
            self.assertIn("release/public/", cfg["exclude"])
            self.assertIn("a/b.ts", cfg["moduleIdAllow"]["canvas"])
            public = ef.load_config(use_owner=False)
            self.assertIsNone(public["ownerConfig"])
            with self.assertRaises(RuntimeError):
                ef.build_tree(public, "HEAD", None)

    def test_public_config_names_nobody(self):
        # the public config ships: no owner identity rewrites and no private files list in it
        raw = json.loads(ef.CONFIG.read_text(encoding="utf-8"))
        self.assertNotIn("rewrite", raw)
        self.assertEqual(raw["exclude"], ["release/public/", "release/owner/"])


if __name__ == "__main__":
    unittest.main()
