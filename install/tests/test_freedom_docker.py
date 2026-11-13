# SPDX-License-Identifier: AGPL-3.0-or-later
"""Tests of install/freedom_docker.py without Docker, network or keyring.  python -m unittest discover install/tests"""
import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import aiwb_install  # noqa: E402
import freedom_docker as fd  # noqa: E402
import freedom_setup as fs  # noqa: E402

BASE = {
    'schema': 1, 'variant': 'C',
    'site': {'url': 'http://localhost:18080', 'db': {'name': 'co', 'user': 'co'}},
    'members': [{'env': 'anna', 'email': 'anna@example.org', 'name': 'Anna', 'role': 'owner'},
                {'env': 'boris', 'email': 'boris@example.org', 'name': 'Boris'}],
    'drive': {'root': '/srv/drive', 'workbench': 'AI-Workbench'},
    'docker': {'project': 'freedom-test', 'cli': 'wsl', 'keyring': False},
}


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix='freedom-docker-test-'))
        self.env = mock.patch.dict(os.environ, {'AIWB_INSTALL': str(self.tmp / 'install.json')})
        self.env.start()
        aiwb_install._cache = None
        self.pr = mock.patch('builtins.print')
        self.pr.start()

    def tearDown(self):
        self.pr.stop()
        self.env.stop()
        aiwb_install._cache = None

    def write(self, **over):
        d = json.loads(json.dumps(BASE))
        for k, v in over.items():
            cur = d
            parts = k.split('__')
            for p in parts[:-1]:
                cur = cur.setdefault(p, {})
            cur[parts[-1]] = v
        (self.tmp / 'install.json').write_text(json.dumps(d))
        aiwb_install._cache = None
        return d


class CheckTest(Base):
    def test_variant_rules(self):
        d = json.loads(json.dumps(BASE))
        self.assertEqual(aiwb_install.check(d), [])            # C may live on http://localhost
        d['variant'] = 'B'
        self.assertTrue(any('site.url' in e for e in aiwb_install.check(d)))
        d['site']['url'] = 'https://co.example.org'
        self.assertEqual(aiwb_install.check(d), [])
        d['variant'] = 'X'
        self.assertTrue(any('variant' in e for e in aiwb_install.check(d)))
        del d['variant']                                       # old files: variant A, https only
        self.assertEqual(aiwb_install.check(d), [])
        d['site']['url'] = 'http://localhost:18080'
        self.assertTrue(aiwb_install.check(d))

    def test_example_is_valid_and_variant_a(self):
        ex = json.loads((Path(fd.__file__).parent / 'install.example.json').read_text(encoding='utf-8'))
        self.assertEqual(aiwb_install.check(ex), [])
        self.assertEqual(ex['variant'], 'A')
        self.assertEqual(set(ex['docker']) - set(fd.DEFAULTS), set())


class SettingsTest(Base):
    def test_defaults_per_variant(self):
        self.write()
        s = fd.prepare_install()
        self.assertEqual((s['variant'], s['bind'], s['httpPort'], s['image']), ('C', '127.0.0.1', 18080, 'freedom-test-app:local'))
        self.assertEqual(aiwb_install.get('site.home'), '/home/co')
        self.assertEqual(aiwb_install.get('site.webRoot'), '/home/co/public_html')
        self.assertEqual(aiwb_install.get('site.db.host'), 'db')
        self.assertFalse(json.loads((self.tmp / 'install.json').read_text())['site'].get('home'))  # file untouched
        self.write(variant='B', site__url='https://co.example.org', docker__httpPort=0)
        s = fd.prepare_install()
        self.assertEqual((s['bind'], s['httpPort'], s['httpsPort'], s['host']), ('0.0.0.0', 80, 443, 'co.example.org'))

    def test_variant_a_is_refused(self):
        self.write(variant='A', site__url='https://co.example.org')
        with self.assertRaises(SystemExit):
            fd.prepare_install()

    def test_bad_project_name(self):
        self.write(docker__project='Bad Name')
        with self.assertRaises(SystemExit):
            fd.prepare_install()


class GeneratedFilesTest(Base):
    def test_caddyfile(self):
        self.write()
        c = fd.caddyfile(fd.prepare_install())
        self.assertIn(':80 {', c)
        self.assertIn('auto_https off', c)
        self.assertIn('reverse_proxy https://app:443', c)
        self.write(variant='B', site__url='https://co.example.org', docker__tls='internal')
        c = fd.caddyfile(fd.prepare_install())
        self.assertIn('co.example.org {', c)
        self.assertIn('tls internal', c)
        self.write(variant='B', site__url='https://co.example.org', docker__tls='auto', docker__acmeEmail='anna@example.org')
        c = fd.caddyfile(fd.prepare_install())
        self.assertNotIn('tls internal', c)
        self.assertIn('email anna@example.org', c)

    def test_env_has_no_secrets(self):
        self.write()
        e = fd.env_text(fd.prepare_install())
        self.assertIn('COMPOSE_PROJECT_NAME=freedom-test', e)
        self.assertIn('HTTP_PORT=18080', e)
        body = ''.join(l for l in e.lower().splitlines(True) if not l.startswith('#'))
        self.assertNotRegex(body, r'password|secret|key=')

    def test_server_config_points_the_agent_inside_the_stack(self):
        self.write()
        fd.prepare_install()
        c = fd.server_config_text()
        self.assertIn("'web_dir' => '/home/co/public_html/agent'", c)
        self.assertIn("'site_url' => 'https://app',", c)
        self.assertEqual(c.count("'site_url'"), 1)

    def test_cron_lines_are_the_panel_lines(self):
        lines = fd.cron_lines('/home/co')
        self.assertEqual(len(lines), len(fs.CRON_LINES))
        for l, panel in zip(lines, fs.CRON_LINES):
            self.assertTrue(panel.format(home='/home/co').startswith(l))
            self.assertNotIn('/dev/null', l)
            self.assertEqual(len(l.split(None, 5)), 6)

    def test_stack_files_exist_with_lf(self):
        for name in fd.STACK_FILES:
            data = (fd.DOCKER_DIR / name).read_bytes()
            self.assertNotIn(b'\r\n', data, name)
        compose = (fd.DOCKER_DIR / 'compose.yaml').read_text()
        for svc in ('db:', 'app:', 'cron:', 'caddy:', 'pid: "service:app"', './secrets/db_password', 'cpus:'):
            self.assertIn(svc, compose)
        self.assertNotRegex(compose, r'PASSWORD:\s*\S')       # passwords only through *_FILE secrets
        self.assertIn('127.0.0.1', (fd.DOCKER_DIR / 'compose.variant-c.yaml').read_text())


class FakeHost:
    def __init__(self):
        self.calls = []
        self.files = {}
        self.cli = 'docker'

    def text(self, args, stdin=None, timeout=600):
        self.calls.append((args, stdin))
        return 0, ''

    def run(self, args, stdin=None, timeout=600):
        self.calls.append((args, stdin))
        return 0, b''

    def secret(self, name):
        return self.files.get(name)

    def set_secret(self, name, value):
        self.files[name] = value


class ApacheConfTest(unittest.TestCase):
    def test_authorization_reaches_php(self):
        # rehearsal 13.11: without these lines mod_php never saw "Authorization: Bearer", every bearer call got 403
        conf = (fd.DOCKER_DIR / 'apache-site.conf').read_text(encoding='utf-8')
        self.assertIn('CGIPassAuth On', conf)
        self.assertIn('SetEnvIf Authorization "(.+)" HTTP_AUTHORIZATION=$1', conf)


class DockerTargetTest(Base):
    def test_put_many_sends_one_tar_with_new_files_and_a_list(self):
        h = FakeHost()
        t = fd.DockerTarget(h, 'abc123')
        bad = t.put_many([('/home/co/public_html/agent/x.php', b'<?php echo 1;', True),
                          ('/home/co/co-agent/executor/run.sh', b'#!/bin/sh\n', False)], '20260101-000000')
        self.assertEqual(bad, [])
        (args, stdin), = h.calls
        self.assertEqual(args[:6], ['docker', 'exec', '-i', '-u', 'co', 'abc123'])
        script = args[-1]
        self.assertIn('php -l', script)
        self.assertIn('.bak-20260101-000000', script)
        tar = tarfile.open(fileobj=io.BytesIO(stdin))
        names = {m.name: m for m in tar.getmembers()}
        self.assertIn('home/co/public_html/agent/x.php.new', names)
        self.assertEqual(names['home/co/co-agent/executor/run.sh.new'].mode, 0o755)
        lst = [n for n in names if n.startswith('tmp/freedom-deploy-')]
        self.assertEqual(len(lst), 1)
        self.assertEqual(tar.extractfile(lst[0]).read().decode().splitlines(),
                         ['1 /home/co/public_html/agent/x.php', '0 /home/co/co-agent/executor/run.sh'])

    def test_failures_are_reported(self):
        h = FakeHost()
        h.text = lambda args, stdin=None, timeout=600: (0, 'FAILED /home/co/a.php php -l: Parse error\n')
        self.assertEqual(fd.DockerTarget(h, 'c').put_many([('/home/co/a.php', b'<?php x(', True)], ''),
                         ['/home/co/a.php php -l: Parse error'])

    def test_secret_write_sets_the_mode_before_the_data(self):
        h = FakeHost()
        fd.DockerTarget(h, 'c').write('/home/co/co-agent/secrets/db.json', b'{}', 0o600)
        cmd = h.calls[0][0][-1]
        self.assertLess(cmd.index('chmod 600'), cmd.index('cat >'))
        self.assertIn('umask 077', cmd)


class SecretStoreTest(Base):
    def test_stack_secrets_first_keyring_only_when_enabled(self):
        h = FakeHost()
        h.files['db_password'] = 'pw-stack'
        keys = {('aiwb/co-push', 'default'): 'from-keyring'}
        with mock.patch.object(fs, 'keyring_get', lambda s, a='default': keys.get((s, a))), \
                mock.patch.object(fs, 'keyring_set', lambda s, a, v: keys.__setitem__((s, a), v)):
            s = {'dbUser': 'co', 'keyring': False}
            with fd.secret_store(h, s):
                self.assertEqual(fs.keyring_get('aiwb/co-mysql', 'co'), 'pw-stack')
                self.assertIsNone(fs.keyring_get('aiwb/co-push'))           # keyring off: not read
                fs.keyring_set('aiwb/co-push', 'default', 'new-key')
            self.assertEqual(h.files['push_key'], 'new-key')
            self.assertEqual(keys[('aiwb/co-push', 'default')], 'from-keyring')  # keyring off: not written
            s['keyring'] = True
            h.files.pop('push_key')
            with fd.secret_store(h, s):
                self.assertEqual(fs.keyring_get('aiwb/co-push'), 'from-keyring')
                fs.keyring_set('aiwb/co-push', 'default', 'k2')
            self.assertEqual(keys[('aiwb/co-push', 'default')], 'k2')
        self.assertIsNot(fs.keyring_get, None)


class ReviewFixesTest(Base):
    def test_variant_c_only_localhost_name(self):
        d = json.loads(json.dumps(BASE))
        for url, good in (('http://localhost:18080', True), ('http://127.0.0.1:18080', False),
                          ('http://localhost', False), ('https://pc.tailnet.ts.net', True)):
            d['site']['url'] = url
            self.assertEqual(aiwb_install.check(d) == [], good, url)

    def test_variant_c_refuses_a_public_bind(self):
        self.write(docker__bind='0.0.0.0')
        with self.assertRaises(SystemExit):
            fd.prepare_install()
        self.write(docker__bind='localhost')
        self.assertEqual(fd.prepare_install()['bind'], 'localhost')

    def test_cpu_limits_capped_at_host_cpus(self):
        self.write(docker__cpus=4)
        s = fd.prepare_install()
        rows = dict(l.split('=', 1) for l in fd.env_text(s, 1).splitlines() if '=' in l and not l.startswith('#'))
        for k in ('APP_CPUS', 'DB_CPUS', 'CRON_CPUS', 'CADDY_CPUS'):
            self.assertLessEqual(float(rows[k]), 1.0, k)
        self.assertEqual(float(dict(l.split('=', 1) for l in fd.env_text(s, 0).splitlines()
                                    if '=' in l and not l.startswith('#'))['APP_CPUS']), 4.0)

    def test_keyring_errors_only_warn(self):
        def boom(*a):
            raise RuntimeError('no backend')
        self.assertFalse(fd.keyring_put(boom, 'aiwb/co-push', 'default', 'x'))
        h = FakeHost()
        with mock.patch.object(fs, 'keyring_set', boom), mock.patch.object(fs, 'keyring_get', lambda *a: None):
            with fd.secret_store(h, {'dbUser': 'co', 'keyring': True}):
                fs.keyring_set('aiwb/co-push', 'default', 'k')     # must not raise
        self.assertEqual(h.files['push_key'], 'k')

    def test_keyring_switched_off_without_backend(self):
        self.write(docker__keyring=True)
        with mock.patch.object(fd, 'keyring_usable', return_value=False):
            self.assertFalse(fd.prepare_install()['keyring'])

    def test_docker_cli_refused_on_windows(self):
        self.write(docker__cli='docker')
        with mock.patch.object(fd.os, 'name', 'nt'):
            with self.assertRaises(SystemExit):
                fd.settings()

    def test_verify_connects_to_a_specific_bind(self):
        self.assertEqual(fd.check_addr({'bind': '0.0.0.0'}), '127.0.0.1')
        self.assertEqual(fd.check_addr({'bind': '192.0.2.7'}), '192.0.2.7')
        self.assertEqual(fd.check_addr({'bind': 'localhost'}), '127.0.0.1')

    def test_root_password_never_on_a_command_line(self):
        src = Path(fd.__file__).read_text(encoding='utf-8') + (fd.DOCKER_DIR / 'README.md').read_text(encoding='utf-8')
        self.assertNotRegex(src, r'-p"\$\(cat')
        self.assertIn('MYSQL_PWD=', src)

    def test_cron_state_survives_split_utf8(self):
        php = (fd.DOCKER_DIR / 'freedom-cron.php').read_text(encoding='utf-8')
        self.assertIn('JSON_INVALID_UTF8_SUBSTITUTE', php)
        self.assertNotIn("substr($r['text'], -", php)
        self.assertIn('if ($json !== false', php)


class HookTest(Base):
    def test_site_deploy_plan_without_flags(self):
        self.write()
        with mock.patch.object(fd, 'cmd_plan', return_value=0) as plan, mock.patch.object(fd, 'cmd_deploy') as dep:
            self.assertEqual(fd.site_deploy(SimpleNamespace(apply=False, diff=False)), 0)
        plan.assert_called_once()
        dep.assert_not_called()

    def test_site_deploy_diff_is_read_only(self):
        self.write()
        with mock.patch.object(fd, 'cmd_deploy', return_value=0) as dep, mock.patch.object(fd, 'cmd_up') as up:
            fd.site_deploy(SimpleNamespace(apply=False, diff=True, parts=None, build=False))
        self.assertTrue(dep.call_args[0][0].diff)
        up.assert_not_called()

    def test_wizard_hands_variants_b_c_to_docker(self):
        wizard = (fd.DOCKER_DIR.parent / 'freedom_setup.py').read_text(encoding='utf-8')
        for hook in ('freedom_docker.site_probe', 'freedom_docker.site_deploy', 'freedom_docker.site_verify'):
            self.assertIn(hook, wizard)


if __name__ == '__main__':
    unittest.main()
