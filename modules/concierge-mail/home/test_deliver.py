import tempfile
import unittest
from datetime import datetime
from pathlib import Path

import deliver


class FakeSite:
    def __init__(self, found=None):
        self.found = found if found is not None else [
            {'id': 'devices', 'kind': 'core', 'title': 'CO push', 'ready': True, 'devices': 2},
            {'id': 'chatapp/say', 'kind': 'module', 'module': 'chatapp', 'title': 'Chat app', 'ready': False, 'note': 'turn it on'},
            {'id': 'speaker/out', 'kind': 'module', 'module': 'speaker', 'title': 'Speaker', 'ready': None},
        ]
        self.sent = []

    def channels(self, env):
        return self.found

    def notify(self, env, title, body, channels):
        self.sent.append((env, title, body, channels))
        return {'sent': 1}


class DeliverTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.site = FakeSite()
        self.lines = ['Your turn (2): a; b', 'Drafts waiting (1): c']
        self.briefing = lambda accounts, lang: list(self.lines)

    def tearDown(self):
        self.temp.cleanup()

    def test_channels_suggests_only_ready(self):
        r = deliver.channels(self.site, 'me')
        self.assertEqual(r['suggested'], ['devices'])
        self.assertEqual(len(r['channels']), 3)

    def test_nothing_is_sent_before_a_choice(self):
        self.assertFalse(deliver.send(self.root, self.site, self.briefing)['sent'])
        self.assertFalse(deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, 21, 8, 0))['sent'])
        self.assertEqual(self.site.sent, [])

    def test_set_checks_channels_and_warns_not_ready(self):
        with self.assertRaises(ValueError):
            deliver.configure(self.root, self.site, 'me', chosen=['nope/x'])
        r = deliver.configure(self.root, self.site, 'me', chosen=['devices', 'chatapp/say'], at='07:15', lang='ru')
        self.assertEqual(r['config']['channels'], ['devices', 'chatapp/say'])
        self.assertTrue(r['config']['enabled'])
        self.assertEqual(len(r['warnings']), 1)
        self.assertIn('turn it on', r['warnings'][0])
        with self.assertRaises(ValueError):
            deliver.configure(self.root, self.site, 'me', at='7h')

    def test_send_goes_only_to_chosen_channels(self):
        deliver.configure(self.root, self.site, 'me', chosen=['speaker/out'], lang='ru')
        synced = []
        r = deliver.send(self.root, self.site, self.briefing, sync=synced.append)
        self.assertTrue(r['sent'])
        self.assertEqual(self.site.sent, [('me', 'Почта утром', '\n'.join(self.lines), ['speaker/out'])])
        self.assertEqual(synced, [])  # no accounts chosen: nothing synced

    def test_dry_run_sends_nothing(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], accounts=['a@example.org'])
        synced = []
        r = deliver.send(self.root, self.site, self.briefing, sync=synced.append, dry_run=True)
        self.assertFalse(r['sent'])
        self.assertEqual(synced, ['a@example.org'])
        self.assertEqual(self.site.sent, [])

    def test_failed_sync_still_briefs(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], accounts=['a@example.org'])

        def boom(account):
            raise OSError('imap down')
        r = deliver.send(self.root, self.site, self.briefing, sync=boom)
        self.assertTrue(r['sent'])
        self.assertIn('sync failed', r['synced'][0])

    def test_tick_once_a_day_in_window(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], at='07:30')
        tick = lambda h, m, d=21: deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, d, h, m))
        self.assertFalse(tick(7, 29)['sent'])
        self.assertTrue(tick(7, 40)['sent'])
        self.assertFalse(tick(8, 0)['sent'])
        self.assertFalse(tick(11, 0, 22)['sent'])  # PC woke up too late: skip the stale day
        self.assertTrue(tick(7, 31, 23)['sent'])
        self.assertEqual(len(self.site.sent), 2)

    def test_window_across_midnight(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], at='23:59')
        tick = lambda d, h, m: deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, d, h, m))
        self.assertFalse(tick(21, 23, 55)['sent'])
        self.assertTrue(tick(22, 0, 5)['sent'])
        self.assertFalse(tick(22, 0, 15)['sent'])
        self.assertTrue(tick(22, 23, 59)['sent'])

    def test_set_during_send_is_kept(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], accounts=['a@example.org'])
        off = lambda account: deliver.configure(self.root, self.site, 'me', off=True)
        self.assertTrue(deliver.send(self.root, self.site, self.briefing, sync=off)['sent'])
        self.assertFalse(deliver.load(self.root)['enabled'])

    def test_off_keeps_choice(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'])
        cfg = deliver.configure(self.root, self.site, 'me', off=True)['config']
        self.assertEqual(cfg['channels'], ['devices'])
        self.assertFalse(cfg['enabled'])
        self.assertFalse(deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, 21, 7, 45))['sent'])

    def test_failed_device_push_is_retried(self):
        deliver.configure(self.root, self.site, 'me', chosen=['devices'], at='07:30')
        self.site.notify = lambda *a: {'sent': 0, 'failed': 1}
        tick = lambda m: deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, 21, 7, m))
        self.assertFalse(tick(40)['sent'])
        self.site.notify = lambda *a: {'sent': 1}
        self.assertTrue(tick(50)['sent'])

    def test_module_channel_not_ready_is_retried(self):
        deliver.configure(self.root, self.site, 'me', chosen=['chatapp/say'], at='07:30')
        self.site.notify = lambda *a: {'sent': 0}
        tick = lambda m: deliver.tick(self.root, self.site, self.briefing, now=datetime(2026, 11, 21, 7, m))
        self.assertFalse(tick(40)['sent'])  # chatapp/say says ready False
        self.site.found[1]['ready'] = True
        self.assertTrue(tick(50)['sent'])

    def test_set_without_env_keeps_the_recipient(self):
        deliver.configure(self.root, self.site, 'kid', chosen=['devices'])
        cfg = deliver.configure(self.root, self.site, deliver.load(self.root).get('env'), at='08:00')['config']
        self.assertEqual(cfg['env'], 'kid')

    def test_body_fits_a_push(self):
        lines = [f'line {i} ' + 'x' * 200 for i in range(6)]
        text = deliver.body(lines)
        self.assertLessEqual(len(text), deliver.BODY_MAX)
        self.assertTrue(text.startswith('line 0'))
        self.assertTrue(text.endswith('… +4'))
        self.assertEqual(deliver.body(['short']), 'short')


if __name__ == '__main__':
    unittest.main()
