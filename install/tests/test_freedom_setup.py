# SPDX-License-Identifier: AGPL-3.0-or-later
"""Tests of install/freedom_setup.py without network, hosting or keyring.  python -m unittest discover install/tests"""
import json
import re
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

    def test_report_before_install_json(self):
        """A friend whose init failed: no install.json, JSON-escaped paths, a space in the profile folder."""
        rc = self.init(site_url='https://my-family-site.example.com', ssh_host='srv123.hosting-provider.be',
                       owner_name='Zebulon', member=['bad-member'])
        self.assertEqual(rc, 1)
        self.assertFalse(aiwb_install.path().is_file())
        fs.save_step('preflight', False, 'drive C:\\Users\\Jane Doe\\My Drive missing; D:\\Jane\\x')
        fs.out('FAIL', 'init: cannot reach https://my-family-site.example.com (srv123.hosting-provider.be) for Zebulon; '
                       'db u1234567_co; C:\\Users\\Jane Doe\\AppData; other.example.net')
        rep = fs.report_text()
        for bad in ('Jane', 'Doe', 'my-family-site', 'hosting-provider', 'Zebulon', 'u1234567', 'other.example'):
            self.assertNotIn(bad.lower(), rep.lower(), f'{bad} leaked: {rep}')
        for keep in ('freedom_setup.py', 'preflight', 'My Drive', 'AppData'):
            self.assertIn(keep, rep)

    def test_report_never_carries_the_invite_secret(self):
        with mock.patch.object(fs, 'keyring_set', side_effect=RuntimeError('no keyring')), \
                mock.patch.object(fs, 'post_json', lambda *a, **k: (200, {'ok': True, 'live': True, 'reports': []})), \
                mock.patch('builtins.print'):
            self.assertEqual(fs.cmd_invite(SimpleNamespace(code=invite_code(), none=False)), 0)
        self.assertEqual(fs.load_state()['invite']['k'], 'K' * 32)     # fallback: kept next to the state
        self.assertEqual(fs.invite_info()['k'], 'K' * 32)
        self.assertNotIn('KKKKKKKKKKKKKKKK', fs.report_text())


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


class InstallersTest(Base):
    """Freedom 0.4.0: Bridge config from install.json, release check of an installer, desktop-update to site + Bridge."""

    def release(self, files, sign=True):
        import freedom_release as fr
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
        rel = self.tmp / 'rel'
        rel.mkdir(exist_ok=True)
        for name, data in files.items():
            (rel / name).write_bytes(data)
        key = Ed25519PrivateKey.generate()
        m = fr.build(rel, '9.9.9')
        if sign:
            fr.sign(m, 'test', key)
        (rel / fr.MANIFEST_NAME).write_text(json.dumps(m), encoding='utf-8')
        trust = self.tmp / 'trust.json'
        trust.write_text(json.dumps({'threshold': 1, 'maintainers': [{'name': 'test', 'key': fr.public_hex(key)}]}))
        return rel, trust

    def test_bridge_config_from_install_json(self):
        self.init()
        cfg = aiwb_install.bridge_config()
        self.assertEqual(cfg['siteUrl'], 'https://co.example.org')
        self.assertEqual(cfg['push']['ownerEnv'], 'anna')
        self.assertEqual(sorted(cfg['chatProjectEnvironments']), ['anna', 'boris'])
        self.assertTrue(cfg['dbQueue']['enabled'])
        self.assertEqual(cfg['devices']['corporateDomains'], [])
        self.assertEqual([p['environmentId'] for p in cfg['projects'].values()], ['anna'])
        self.assertEqual(aiwb_install.bridge_patch(cfg), {})
        self.assertNotIn('example.com', json.dumps(cfg))

    def test_release_check(self):
        import freedom_release as fr
        rel, trust = self.release({'CO-Desktop-Setup-9.9.9.exe': b'MZ installer', 'other.tar.gz': b'x'})
        (rel / 'other.tar.gz').unlink()   # the user downloads only what they need
        f = rel / 'CO-Desktop-Setup-9.9.9.exe'
        with mock.patch.object(fr, 'DEFAULT_TRUST', trust):
            self.assertEqual(fs.release_check(f), '')
            f.write_bytes(b'MZ changed')
            self.assertIn('changed', fs.release_check(f))
        rel2, trust2 = self.release({'CO-Desktop-Setup-9.9.9.exe': b'MZ'}, sign=False)
        with mock.patch.object(fr, 'DEFAULT_TRUST', trust2):
            self.assertIn('signature', fs.release_check(rel2 / 'CO-Desktop-Setup-9.9.9.exe'))
        lone = self.tmp / 'lone' / 'CO-Desktop-Setup-1.0.0.exe'
        lone.parent.mkdir()
        lone.write_bytes(b'MZ')
        self.assertIn('freedom-manifest.json', fs.release_check(lone))

    def test_desktop_update_puts_installer_on_site_and_bridge(self):
        import freedom_release as fr
        self.init()
        rel, trust = self.release({'CO-Desktop-Setup-9.9.9.exe': b'MZ installer'})
        cfg = self.tmp / 'AIWorkbenchBridge' / 'config.json'
        cfg.parent.mkdir()
        cfg.write_text(json.dumps({'desktopUpdatesDir': str(self.tmp / 'du')}), encoding='utf-8')
        site = self.tmp / 'site'
        a = SimpleNamespace(installer=str(rel / 'CO-Desktop-Setup-9.9.9.exe'), transport='local', local_root=str(site), host_key=None)
        with mock.patch.object(fr, 'DEFAULT_TRUST', trust), mock.patch.object(fs, 'bridge_config_path', lambda: cfg), \
                mock.patch('builtins.print'):
            self.assertEqual(fs.cmd_desktop_update(a), 0)
        home = site / 'home' / 'u1234567' / 'co-desktop'
        self.assertEqual((home / 'CO-Desktop-Setup-9.9.9.exe').read_bytes(), b'MZ installer')
        latest = json.loads((home / 'latest.json').read_text())
        self.assertEqual((latest['version'], latest['size']), ('9.9.9', 12))
        self.assertTrue((self.tmp / 'du' / 'setup' / 'CO-Desktop-Setup-9.9.9.exe').is_file())

    def test_desktop_update_refuses_unsigned(self):
        import freedom_release as fr
        self.init()
        rel, trust = self.release({'CO-Desktop-Setup-9.9.9.exe': b'MZ'}, sign=False)
        a = SimpleNamespace(installer=str(rel / 'CO-Desktop-Setup-9.9.9.exe'), transport='local', local_root=str(self.tmp / 's'), host_key=None)
        with mock.patch.object(fr, 'DEFAULT_TRUST', trust), mock.patch('builtins.print'):
            self.assertEqual(fs.cmd_desktop_update(a), 1)
        self.assertFalse((self.tmp / 's').exists())

    def test_release_check_refuses_unsafe_path_and_twin_entry(self):
        import freedom_release as fr
        rel, trust = self.release({'CO-Desktop-Setup-9.9.9.exe': b'MZ'})
        man = rel / fr.MANIFEST_NAME
        m = json.loads(man.read_text())
        m['files'].append({'path': '../x.exe', 'size': 1, 'sha256': '0' * 64})
        man.write_text(json.dumps(m))
        with mock.patch.object(fr, 'DEFAULT_TRUST', trust):
            why = fs.release_check(rel / 'CO-Desktop-Setup-9.9.9.exe')
        self.assertIn('signature', why)   # any change of the signed manifest breaks the signature


def invite_code(site='https://co.example.org', i='inv-0123456789ab', k='K' * 32):
    import base64
    raw = json.dumps({'s': site, 'i': i, 'k': k}).encode()
    return 'fdm-inv1.' + base64.urlsafe_b64encode(raw).decode().rstrip('=')


class InviteTest(Base):
    """SPEC 7.5: invite code as the first step, report --send after the friend's yes, answers."""

    def run_cmd(self, fn, posts=None, tty=False, inputs=(), **kw):
        calls = []

        def fake_post(url, obj, timeout=20):
            calls.append((url, obj))
            return (posts or {}).get(url.rsplit('r=', 1)[1], (200, {'ok': True, 'reports': []}))
        ins = iter(inputs)
        with mock.patch.object(fs, 'post_json', fake_post), mock.patch('builtins.print'),                 mock.patch('builtins.input', lambda *_: next(ins)), mock.patch.object(fs.sys.stdin, 'isatty', lambda: tty):
            rc = fn(SimpleNamespace(**kw))
        return rc, calls

    def test_first_step_is_invite(self):
        self.assertEqual(fs.STEPS[0], 'invite')

    def test_parse_rejects_bad_codes(self):
        for bad in ('hello', 'fdm-inv1.' + 'A' * 30, invite_code(site='http://evil.example'), invite_code(i='../x'),
                    invite_code(k='short'), invite_code(site='https://a.example/p?x=1')):
            with self.assertRaises(ValueError):
                fs.parse_invite(bad)
        self.assertEqual(fs.parse_invite(' ' + invite_code() + '\n')['host'], 'co.example.org')
        self.assertEqual(fs.parse_invite(invite_code(site='http://localhost:8080'))['host'], 'localhost')

    def test_invite_stored_with_secret_in_keyring(self):
        rc, calls = self.run_cmd(fs.cmd_invite, code=invite_code(), none=False)
        self.assertEqual(rc, 0)
        self.assertTrue(calls[0][0].endswith('module.php?m=invite-help&r=answers'))
        st = fs.load_state()
        self.assertEqual(st['invite'], {'site': 'https://co.example.org', 'id': 'inv-0123456789ab', 'host': 'co.example.org'})
        self.assertNotIn('KKKKKKKK', fs.state_path().read_text(encoding='utf-8'))
        self.assertEqual(self.keys[('aiwb/freedom-invite', 'default')], 'K' * 32)
        self.assertTrue(st['steps']['invite']['ok'])

    def test_revoked_code_fails_offline_is_kept(self):
        rc, _ = self.run_cmd(fs.cmd_invite, posts={'answers': (403, {'error': 'forbidden'})}, code=invite_code(), none=False)
        self.assertEqual(rc, 1)
        rc, _ = self.run_cmd(fs.cmd_invite, posts={'answers': (200, {'ok': True, 'live': False, 'reports': []})},
                             code=invite_code(), none=False)   # what the server says for a revoked or expired invite
        self.assertEqual(rc, 1)
        self.assertNotIn('invite', fs.load_state())
        rc, _ = self.run_cmd(fs.cmd_invite, posts={'answers': (0, {'error': 'URLError'})}, code=invite_code(), none=False)
        self.assertEqual(rc, 0)
        self.assertEqual(fs.load_state()['invite']['host'], 'co.example.org')

    def test_enter_means_no_invite(self):
        rc, calls = self.run_cmd(fs.cmd_invite, tty=True, inputs=[''], code=None, none=False)
        self.assertEqual(rc, 0)
        self.assertEqual(calls, [])
        self.assertIsNone(fs.load_state()['invite'])
        self.assertIn('forum', fs.load_state()['steps']['invite']['summary'])

    def test_no_tty_without_code_asks_for_it(self):
        rc, _ = self.run_cmd(fs.cmd_invite, code=None, none=False)
        self.assertEqual(rc, 1)

    def test_send_needs_yes_and_sends_sanitized(self):
        self.init()
        self.run_cmd(fs.cmd_invite, code=invite_code(), none=False)
        fs.out('FAIL', 'subs: ssh u1234567@203.0.113.10 failed for anna@example.org')
        fs.save_step('subs', False, 'boom')
        rep = dict(out=None, send=True, yes=False, answers=False)
        rc, calls = self.run_cmd(fs.cmd_report, tty=True, inputs=['n'], **rep)
        self.assertEqual((rc, calls), (0, []))
        rc, calls = self.run_cmd(fs.cmd_report, posts={'intake': (200, {'ok': True, 'id': 'r-20261114000000-0123abcd'})},
                                 tty=True, inputs=['y'], **rep)
        self.assertEqual(rc, 0)
        url, body = calls[0]
        self.assertTrue(url.endswith('r=intake'))
        self.assertEqual(body['step'], 'subs')
        self.assertEqual(body['k'], 'K' * 32)
        for leak in ('u1234567', '203.0.113.10', 'anna@example.org'):
            self.assertNotIn(leak, body['text'])
        self.assertEqual(fs.load_state()['inviteSent'], ['r-20261114000000-0123abcd'])
        rc, calls = self.run_cmd(fs.cmd_report, **rep)   # no terminal, no --yes: refused
        self.assertEqual((rc, calls), (1, []))
        rc, calls = self.run_cmd(fs.cmd_report, **{**rep, 'yes': 'abcdef'})   # a check of another text: refused
        self.assertEqual((rc, calls), (1, []))
        text = fs.report_text()
        h = fs.report_hash(text)
        rc, calls = self.run_cmd(fs.cmd_report, posts={'intake': (200, {'ok': True, 'id': 'r-20261114000001-0123abcd'})},
                                 **{**rep, 'yes': h})      # the friend's own AI after showing the text: --yes CHECK
        self.assertEqual(rc, 0)
        self.assertEqual(calls[0][1]['text'], text)

    def test_yes_check_from_previous_run_works(self):
        # the friend's AI: run N prints the text and its check, run N+1 sends with that check (refusals must not change the text)
        self.init()
        self.run_cmd(fs.cmd_invite, code=invite_code(), none=False)
        fs.save_step('subs', False, 'boom')
        rep = dict(out=None, send=True, yes=False, answers=False)
        printed = []

        def run(**kw):
            calls = []

            def fake_post(url, obj, timeout=20):
                calls.append((url, obj))
                return (200, {'ok': True, 'id': 'r-20261114000002-0123abcd'})
            with mock.patch.object(fs, 'post_json', fake_post), \
                    mock.patch('builtins.print', lambda *a, **k: printed.append(' '.join(map(str, a)))), \
                    mock.patch.object(fs.sys.stdin, 'isatty', lambda: False):
                return fs.cmd_report(SimpleNamespace(**{**rep, **kw})), calls

        def last_check():
            return re.findall(r'\(check ([0-9a-f]{6})\)', '\n'.join(printed))[-1]
        self.assertEqual(run(), (1, []))
        h1 = last_check()
        self.assertEqual(run(yes='000000'), (1, []))           # a wrong check is refused ...
        self.assertEqual(last_check(), h1)                    # ... and does not change the text
        rc, calls = run(yes=h1)
        self.assertEqual(rc, 0)
        self.assertEqual(len(calls), 1)

    def test_send_without_invite_fails(self):
        rc, calls = self.run_cmd(fs.cmd_report, out=None, send=True, yes=True, answers=False)
        self.assertEqual((rc, calls), (1, []))

    def test_answers(self):
        self.run_cmd(fs.cmd_invite, code=invite_code(), none=False)
        rc, calls = self.run_cmd(fs.cmd_report, posts={'answers': (200, {'ok': True, 'reports': [
            {'id': 'r-1', 'step': 'subs', 'status': 'answered', 'answer': 'Update the wizard'}]})},
            out=None, send=False, yes=False, answers=True)
        self.assertEqual(rc, 0)
        self.assertEqual(fs._printable('ok\x1b[2J\x1b]0;evil\x07 done\x07\nnext\tline\x9b'), 'ok done\nnext\tline')
        self.assertEqual(calls[0][1], {'i': 'inv-0123456789ab', 'k': 'K' * 32})


class CliPathTest(unittest.TestCase):
    def test_fresh_install_folder_goes_into_path(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / '.local' / 'bin').mkdir(parents=True)
            with mock.patch.object(Path, 'home', return_value=Path(d)), \
                    mock.patch.dict(os.environ, {'PATH': 'C:/x', 'APPDATA': str(Path(d) / 'none')}):
                added = fs.ensure_cli_path()
                self.assertEqual(added, [str(Path(d) / '.local' / 'bin')])
                self.assertTrue(os.environ['PATH'].startswith(str(Path(d) / '.local' / 'bin')))
                self.assertEqual(fs.ensure_cli_path(), [])          # second call: already there

