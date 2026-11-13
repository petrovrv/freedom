# SPDX-License-Identifier: AGPL-3.0-or-later
"""Bridge on Linux (Freedom F3a, Bridge 1.19.0): config.json of bridge/install.sh, the wizard's bridge step, the Linux
builds of release/build_installers.py and the installer script itself."""
import json
import os
import shutil
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'release'))
import aiwb_install  # noqa: E402
import build_installers  # noqa: E402
import freedom_setup as fs  # noqa: E402
from test_freedom_setup import Base  # noqa: E402

REPO = Path(__file__).resolve().parents[2]


class PosixConfigTest(Base):
    def test_windows_drive_root_becomes_a_local_folder(self):
        self.init(drive_root='G:\\My Drive')
        cfg = aiwb_install.bridge_config(posix=True)
        self.assertEqual(cfg['agents']['claude']['executable'], 'claude')
        self.assertEqual(cfg['agents']['codex']['executable'], 'codex')
        self.assertTrue(cfg['driveRoot'].endswith('/AIWorkbench'), cfg['driveRoot'])
        self.assertEqual(cfg['driveReadyPath'], cfg['driveRoot'])
        self.assertNotIn('gdrive', cfg['contextRoots'])
        self.assertEqual(cfg['chatFolderWaitSeconds'], 0)
        ws = [p['workspace'] for p in cfg['projects'].values()]
        self.assertTrue(ws and all(w.startswith(cfg['driveRoot'] + '/projects/') for w in ws), ws)
        self.assertNotIn('\\', json.dumps([cfg['driveRoot'], ws]))
        self.assertEqual(aiwb_install.bridge_patch(cfg), {})

    def test_posix_drive_root_is_kept(self):
        self.init(drive_root='/srv/drive')
        cfg = aiwb_install.bridge_config(posix=True)
        self.assertEqual(cfg['driveRoot'], '/srv/drive/AI-Workbench')
        self.assertEqual(cfg['contextRoots']['gdrive']['path'], '/srv/drive')
        self.assertNotIn('chatFolderWaitSeconds', cfg)   # a synced folder: the Drive wait stays

    def test_aiwb_root_wins(self):
        self.init(drive_root='G:\\My Drive')
        with mock.patch.dict(os.environ, {'AIWB_ROOT': '/opt/aiwb'}):
            self.assertEqual(aiwb_install.bridge_config(posix=True)['driveRoot'], '/opt/aiwb')

    def test_windows_form_unchanged(self):
        self.init()
        cfg = aiwb_install.bridge_config(posix=False)
        self.assertEqual(cfg['agents']['claude']['executable'], 'claude.exe')
        self.assertNotIn('chatFolderWaitSeconds', cfg)

    def test_cli_flag(self):
        self.init(drive_root='G:\\My Drive')
        out = self.tmp / 'cfg.json'
        with mock.patch('builtins.print'):
            self.assertEqual(aiwb_install.main(['x', 'bridge-config', str(out), '--posix']), 0)
        self.assertEqual(json.loads(out.read_text(encoding='utf-8'))['agents']['claude']['executable'], 'claude')


class WizardTest(Base):
    def test_linux_args(self):
        a = SimpleNamespace(no_start=True, deps=True, binary='rel/BridgeAgent-1.19.0-linux-x64', host_id='srv-1')
        args = fs.bridge_args(a, True)
        self.assertEqual(args[:2], ['bash', str(REPO / 'bridge' / 'install.sh')])
        self.assertIn('--no-start', args)
        self.assertIn('--deps', args)
        self.assertEqual(args[args.index('--host-id') + 1], 'srv-1')
        self.assertTrue(Path(args[args.index('--binary') + 1]).is_absolute())
        self.assertEqual(args[args.index('--python') + 1], sys.executable)

    def test_windows_args(self):
        args = fs.bridge_args(SimpleNamespace(no_start=False), False)
        self.assertIn(str(REPO / 'bridge' / 'install.ps1'), args)
        self.assertNotIn('-NoStart', args)

    def test_config_path_is_bridge_state_root(self):
        if os.name == 'nt' or sys.platform == 'darwin':
            self.skipTest('Linux path')
        with mock.patch.dict(os.environ, {'XDG_DATA_HOME': '/x/share'}):
            self.assertEqual(fs.bridge_config_path(), Path('/x/share/AIWorkbenchBridge/config.json'))


class ReleaseTest(unittest.TestCase):
    def test_bridge_version(self):
        src = (REPO / 'bridge' / 'BridgeAgent.cs').read_text(encoding='utf-8')
        self.assertIn(f'BridgeVersion = "{build_installers.bridge_version()}"', src)

    def test_leak_check_files(self):
        import tempfile
        with tempfile.TemporaryDirectory() as t:
            f = Path(t) / 'BridgeAgent.dll'
            f.write_bytes(b'abc secretword def' + 'other'.encode('utf-16-le'))
            self.assertEqual(len(build_installers.leak_check_files([f], ['secretword'])), 1)
            self.assertEqual(len(build_installers.leak_check_files([f], ['other'])), 1)
            self.assertEqual(build_installers.leak_check_files([f], ['secret']), [])   # whole words only

    def test_rids(self):
        self.assertEqual(build_installers.LINUX_RIDS, ('linux-x64', 'linux-arm64'))


class InstallShTest(unittest.TestCase):
    SH = REPO / 'bridge' / 'install.sh'

    def test_lf_and_executable(self):
        self.assertNotIn(b'\r\n', self.SH.read_bytes())
        r = subprocess.run(['git', 'ls-files', '-s', 'bridge/install.sh'], cwd=REPO, capture_output=True, text=True)
        if r.returncode == 0 and r.stdout:
            self.assertTrue(r.stdout.startswith('100755'), r.stdout)

    def test_syntax(self):
        bash = shutil.which('bash')
        if not bash:
            self.skipTest('no bash')
        # relative path: the bash found may be the one of WSL (System32), which reads no Windows paths
        r = subprocess.run([bash, '-n', self.SH.name], cwd=self.SH.parent, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_no_secret_in_files(self):
        text = self.SH.read_text(encoding='utf-8')
        # secrets go only through secret-tool / the keyring; the keyring's own unlock password is the one file (0600)
        self.assertIn('umask 077', text)
        self.assertNotIn('co-push', text)


if __name__ == '__main__':
    unittest.main()
