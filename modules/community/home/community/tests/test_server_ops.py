# SPDX-License-Identifier: AGPL-3.0-or-later
"""Server part of the module (server/ops.php) with a stub of the core: push op stores the view, user op gives it to
the owner only, a bad or too large view is refused. Needs php (AIWB_PHP or the build's php.exe); skipped without."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest

OPS = pathlib.Path(__file__).resolve().parents[3] / "server" / "ops.php"
PHP = next((p for p in (os.environ.get("AIWB_PHP"), os.path.join(os.environ.get("LOCALAPPDATA", ""), "AIWorkbenchBuild", "php", "php.exe"),
                        shutil.which("php")) if p and os.path.isfile(p)), None)

SCRIPT = r"""<?php
declare(strict_types=1);
function co_push_write_atomic(string $p, string $d, int $m = 0600): void { file_put_contents($p, $d); }
$h = include $argv[1];
$c = ['stateDir' => $argv[2], 'owner' => true];
$out = [];
$out[] = $h['user']['community_page']($c, []);
$out[] = $h['push']['community_page_put']($c, ['view' => ['v' => 2]]);
$out[] = $h['push']['community_page_put']($c, ['view' => ['v' => 1, 'drafts' => [str_repeat('x', 210000)]]]);
$out[] = $h['push']['community_page_put']($c, ['view' => ['v' => 1, 'updated' => 5, 'drafts' => []]]);
$out[] = $h['user']['community_page']($c, []);
$out[] = $h['user']['community_page'](['stateDir' => $argv[2], 'owner' => false], []);
echo json_encode($out);
"""


@unittest.skipIf(PHP is None, "php not found (AIWB_PHP)")
class ServerOps(unittest.TestCase):
    def test_ops(self):
        d = tempfile.mkdtemp()
        try:
            script = os.path.join(d, "t.php")
            pathlib.Path(script).write_text(SCRIPT, encoding="utf-8")
            r = subprocess.run([PHP, script, str(OPS), d], capture_output=True, text=True, timeout=60)
            self.assertEqual(r.returncode, 0, r.stderr + r.stdout)
            empty, bad, big, put, read, member = json.loads(r.stdout)
            self.assertEqual(empty, {"ok": True, "view": None})
            self.assertEqual(bad["error"], "bad_view")
            self.assertEqual(big["error"], "too_large")
            self.assertTrue(put["ok"])
            self.assertEqual(read["view"]["updated"], 5)
            self.assertEqual(member["error"], "owner_only")
        finally:
            shutil.rmtree(d, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
