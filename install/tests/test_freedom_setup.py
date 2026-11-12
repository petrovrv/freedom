# SPDX-License-Identifier: AGPL-3.0-or-later
"""Tests of install/freedom_setup.py without network, hosting or keyring.  python -m unittest discover install/tests"""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import aiwb_install  # noqa: E402
import freedom_setup as fs  # noqa: E402


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix='freedom-setup-test-'))
        self.env = mock.patch.dict(os.environ, {'AIWB_INSTALL': str(self.tmp / 'install.json')})
        self.env.start()
        aiwb_install._cache = None
        self.keys = {}
        self.kg = mock.patch.object(fs, 'keyring_get', lambda s, a='default': self.keys.get((s, a)))
        self.ks = mock.patch.object(fs, 'keyring_set', lambda s, a, v: self.keys.__setitem__((s, a), v))
        self.kg.start()
        self.ks.start()

    def tearDown(self):
        self.kg.stop()
        self.ks.stop()
        self.env.stop()
        aiwb_install._cache = None

    def init(self, **kw):
        args = dict(site_url='https://co.example.org', owner_email='anna@example.org', owner_name='Anna', owner_env='anna',
                    drive_root=str(self.tmp), ssh_host='203.0.113.10', ssh_port='22', ssh_user='u1234567',
                    ssh_key=None, site_home='/home/u1234567', web_root='/home/u1234567/domains/co.example.org/public_html',
                    db_host=None, db_name='u1234567_aiwb', db_user='u1234567_aiwb', web_client_id=None,
                    project_number=None, member=['boris:boris@example.org:Boris'], force=False, no_input=True)
        args.update(kw)
        with mock.patch('builtins.print'):
            return fs.cmd_init(SimpleNamespace(**args))


class InitTest(Base):
    def test_writes_valid_install_json(self):
        self.assertEqual(self.init(), 0)
        d = aiwb_install.load(fresh=True)
        self.assertEqual(aiwb_install.check(d), [])
        self.assertEqual(d['site']['db']['name'], 'u1234567_aiwb')
        self.assertEqual([m['env'] for m in d['members']], ['anna', 'boris'])
        self.assertTrue(fs.load_state()['steps']['init']['ok'])

    def test_keeps_existing_file(self):
        self.init()
        self.assertEqual(self.init(site_url='https://other.example.org'), 0)
        self.assertEqual(aiwb_install.load(fresh=True)['site']['url'], 'https://co.example.org')

    def test_force_keeps_what_the_answers_do_not_set(self):
        self.init(web_client_id='123456789012-abc.apps.googleusercontent.com', project_number='123456789012')
        d = aiwb_install.load(fresh=True)
        d['members'][0]['color'] = '#123456'
        d['canvas'] = {'publicUrl': 'https://pc.example.net'}
        (self.tmp / 'install.json').write_text(json.dumps(d))
        self.assertEqual(self.init(force=True, site_url='https://new.example.org'), 0)
        d = aiwb_install.load(fresh=True)
        self.assertEqual(d['site']['url'], 'https://new.example.org')
        self.assertEqual(d['google']['projectNumber'], '123456789012')
        self.assertEqual(d['canvas']['publicUrl'], 'https://pc.example.net')
        self.assertEqual(d['members'][0]['color'], '#123456')

    def test_bad_input_fails(self):
        self.assertEqual(self.init(site_url='http://co.example.org/'), 1)
        self.assertFalse((self.tmp / 'install.json').exists())


class SanitizeTest(Base):
    def test_report_has_no_names_hosts_or_secrets(self):
        self.init()
        raw = ('ssh u1234567@203.0.113.10 failed for anna@example.org in /home/u1234567/domains/co.example.org; '
               'Anna; password=hunter2hunter2; token: ya29.a0AfH6SMBxxxxxxxxxxxxxxx; C:\\Users\\anna\\x; '
               'key sk-ant-oat01-ABCDEFGHIJKLMNOPQRSTUVWX; boris@example.org; 4f3c2a1b9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a')
        s = fs.sanitize(raw)
        for bad in ('u1234567', '203.0.113.10', 'anna@', 'example.org', 'Anna', 'hunter2', 'ya29', 'sk-ant',
                    'boris', '4f3c2a1b9d8e'):
            self.assertNotIn(bad.lower(), s.lower(), f'{bad} leaked: {s}')
        self.assertIn('failed for', s)


class LayoutDeployTest(Base):
    def test_layout_parts(self):
        self.init()
        home, web, parts = fs.layout()
        self.assertTrue(any(r.endswith('/agent/db.php') for _, r in parts['agent']))
        self.assertTrue(any(r == f'{web}/auth/login.php' for _, r in parts['auth']))
        self.assertTrue(any(r == f'{home}/co-agent/cron/drive-sync.php' for _, r in parts['home']))
        self.assertTrue(any(r == f'{home}/co-agent/executor/run.sh' for _, r in parts['home']))
        everything = [r for p in parts.values() for _, r in p]
        self.assertFalse([r for r in everything if '/tests/' in r or r.endswith(('.py', 'desktop.ini'))])
        self.assertEqual(parts['corporate'], [])  # connector off by default

    def deploy(self, root, *extra):
        a = SimpleNamespace(apply='apply' in extra, diff='diff' in extra, parts='agent,auth,home,config,secrets',
                            build=False, transport='local', local_root=str(root), no_bak=False, verbose=False,
                            rewrite_config='rewrite' in extra, host_key=None)
        with mock.patch('builtins.print'):
            return fs.cmd_site_deploy(a)

    def test_local_apply_is_idempotent_and_keeps_old_copies(self):
        self.init()
        self.keys[('aiwb/co-mysql', 'u1234567_aiwb')] = 'pw-123'
        root = self.tmp / 'host'
        self.assertEqual(self.deploy(root, 'apply'), 0)
        home = root / 'home' / 'u1234567'
        db = json.loads((home / 'co-agent' / 'secrets' / 'db.json').read_text())
        self.assertEqual(db, {'host': 'localhost', 'db': 'u1234567_aiwb', 'user': 'u1234567_aiwb', 'password': 'pw-123'})
        key = (home / 'co-agent' / 'secrets' / 'push-notify-key').read_text().strip()
        self.assertEqual(self.keys[('aiwb/co-push', 'default')], key)
        self.assertIn("'web_dir' => '/home/u1234567/domains/co.example.org/public_html/agent'",
                      (home / 'co-agent' / 'config.php').read_text())
        lib = home / 'domains' / 'co.example.org' / 'public_html' / 'agent' / 'lib.php'
        lib.write_text('<?php // changed on the server\n')
        self.assertEqual(self.deploy(root, 'apply'), 0)
        self.assertIn('co_agent_home', lib.read_text())
        self.assertTrue(list(lib.parent.glob('lib.php.bak-*')))
        st = fs.load_state()['steps']['site-deploy']
        self.assertIn("'changed': 1", st['summary'])
        # secrets are made once and never overwritten
        self.assertEqual((home / 'co-agent' / 'secrets' / 'push-notify-key').read_text().strip(), key)

    def test_live_config_php_is_kept(self):
        self.init()
        self.keys[('aiwb/co-mysql', 'u1234567_aiwb')] = 'pw'
        root = self.tmp / 'host'
        cfg = root / 'home' / 'u1234567' / 'co-agent' / 'config.php'
        cfg.parent.mkdir(parents=True)
        cfg.write_text("<?php return ['site_name' => 'Mine'];")
        self.assertEqual(self.deploy(root, 'apply'), 0)
        self.assertIn('site_name', cfg.read_text())
        self.assertEqual(self.deploy(root, 'apply', 'rewrite'), 0)
        self.assertIn("'web_dir'", cfg.read_text())

    def test_my_cnf_quotes_and_has_no_database_for_mysqldump(self):
        c = fs.my_cnf('u', 'p#w "x" ', 'localhost')
        self.assertIn('password="p#w \\"x\\" "', c)
        self.assertNotIn('database', c)

    def test_norm_ignores_license_line_and_crlf(self):
        a = b'<?php\n// SPDX-License-Identifier: AGPL-3.0-or-later\n// x\nfoo();\n'
        self.assertEqual(fs._norm(a), fs._norm(b'<?php\r\n// x\r\nfoo();\r\n'))
        self.assertNotEqual(fs._norm(a), fs._norm(b'<?php\n// x\nbar();\n'))


class GoogleTest(Base):
    def test_import_web_client(self):
        self.init()
        f = self.tmp / 'client_secret.json'
        f.write_text(json.dumps({'web': {'client_id': '123456789012-abcdef.apps.googleusercontent.com',
                                         'client_secret': 'GOCSPX-xyz'}}))
        desk = self.tmp / 'desk.json'
        desk.write_text(json.dumps({'installed': {'client_id': '123456789012-zzz.apps.googleusercontent.com',
                                                  'client_secret': 's'}}))
        with mock.patch('builtins.print'), mock.patch.object(fs, '_redirect', return_value=None):
            rc = fs.cmd_google(SimpleNamespace(import_web_client=str(f), desktop_client=str(desk)))
        self.assertEqual(rc, 0)
        d = aiwb_install.load(fresh=True)
        self.assertEqual(d['google']['projectNumber'], '123456789012')
        self.assertEqual(json.loads(self.keys[('aiwb/google-oauth-web', 'default')])['client_secret'], 'GOCSPX-xyz')
        self.assertNotIn('GOCSPX', (self.tmp / 'install.json').read_text())

    def test_desktop_client_of_another_project_fails(self):
        self.init(web_client_id='123456789012-abcdef.apps.googleusercontent.com', project_number='123456789012')
        self.keys[('aiwb/google-oauth-web', 'default')] = '{}'
        desk = self.tmp / 'desk.json'
        desk.write_text(json.dumps({'installed': {'client_id': '999-zzz.apps.googleusercontent.com'}}))
        with mock.patch('builtins.print'), mock.patch.object(fs, '_redirect', return_value=None):
            self.assertEqual(fs.cmd_google(SimpleNamespace(import_web_client=None, desktop_client=str(desk))), 1)



class PcAccessTest(Base):
    """Step 6a (SPEC 3.3): no real WSL or Bridge; wsl.exe and the sandbox tool are mocked."""

    def setUp(self):
        super().setUp()
        self.init(member=['boris:boris@example.org:Boris', '9cora:cora@example.org:Cora', 'root:r@example.org:R'])
        self.cfg = self.tmp / 'AIWorkbenchBridge' / 'config.json'
        self.cfg.parent.mkdir()
        self.cfg.write_text(json.dumps({'hostId': 'home-main', 'devices': {'relayTools': ['x']}}), encoding='utf-8')
        self.p = [mock.patch.object(fs, 'bridge_config_path', lambda: self.cfg),
                  mock.patch.object(fs, 'sandbox_tool', lambda: self.tmp / 'wsl-family-sandbox.py'),
                  mock.patch.object(fs.os, 'name', 'nt'), mock.patch('builtins.print')]
        for x in self.p:
            x.start()

    def tearDown(self):
        for x in reversed(self.p):
            x.stop()
        super().tearDown()

    def run_step(self, distros, users=None, version=(2, 6), **kw):
        """users None: /etc/aiwb-family/users unreadable (distro not set up)."""
        calls = []

        def wsl(args, timeout=60):
            if args[:1] == ['-l']:
                return 0, list(distros)
            return (1, []) if users is None else (0, list(users))

        def sh(args, timeout=120, cwd=None, env=None):
            calls.append(args[2:])
            return 0, 'ok'
        a = dict(apply=False, check=False, off=False)
        a.update(kw)
        with mock.patch.object(fs, 'wsl_lines', wsl), mock.patch.object(fs, 'sh', sh), \
                mock.patch.object(fs, 'wsl_version', lambda: version):
            rc = fs.cmd_pc_access(SimpleNamespace(**a))
        return rc, calls, json.loads(self.cfg.read_text(encoding='utf-8'))['devices']

    def test_plan_changes_nothing(self):
        rc, calls, dev = self.run_step(['Ubuntu'])
        self.assertEqual((rc, calls, dev), (0, [], {'relayTools': ['x']}))

    def test_apply_creates_sandbox_for_members_only(self):
        rc, calls, dev = self.run_step(['Ubuntu'], apply=True)
        self.assertEqual(rc, 0)
        self.assertEqual(calls, [['create', '--users', 'boris:3001']])   # not the owner, an invalid id or root
        self.assertEqual(dev['pcFamily'], {'boris': 'boris'})
        self.assertEqual(dev['relayTools'], ['x'])
        self.assertTrue((self.cfg.parent / 'config.json.bak-pc-access').is_file())

    def test_half_set_up_distro_runs_create_again(self):
        rc, calls, _ = self.run_step(['aiwb-family'], users=None, apply=True)
        self.assertEqual(calls, [['create', '--users', 'boris:3001']])

    def test_apply_adds_missing_user_to_existing_sandbox(self):
        rc, calls, _ = self.run_step(['aiwb-family'], users=[], apply=True)
        self.assertEqual(calls, [['add-user', 'boris']])
        rc, calls, _ = self.run_step(['aiwb-family'], users=['boris'], apply=True)
        self.assertEqual(calls, [])

    def test_no_or_old_wsl_skips_sandbox(self):
        for version in (None, (2, 4)):
            rc, calls, dev = self.run_step([], version=version, apply=True)
            self.assertEqual((rc, calls), (0, []))
            self.assertNotIn('pcFamily', dev)

    def test_off_then_apply_switches_on_again(self):
        rc, _, dev = self.run_step(['aiwb-family'], users=['boris'], off=True)
        self.assertEqual((dev['pcTools'], dev['pcFamilyTools']), (False, False))
        rc, _, dev = self.run_step(['aiwb-family'], users=['boris'], apply=True)
        self.assertEqual((dev['pcTools'], dev['pcFamilyTools'], dev['pcFamily']), (True, True, {'boris': 'boris'}))

    def test_environment_unknown_to_bridge_gets_no_seat(self):
        self.cfg.write_text(json.dumps({'chatProjectEnvironments': {'anna': {}}, 'devices': {}}), encoding='utf-8')
        rc, calls, dev = self.run_step(['Ubuntu'], apply=True)
        self.assertEqual((rc, calls, dev), (0, [], {}))

    def test_no_bridge(self):
        self.cfg.unlink()
        self.assertEqual(fs.cmd_pc_access(SimpleNamespace(apply=True, check=False, off=False)), 1)


if __name__ == '__main__':
    unittest.main()
