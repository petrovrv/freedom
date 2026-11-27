"""0.15.0: the guard of hubctl run for an agent job (chat-mail of its own chat, --link with the project of its folder)."""
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('hubctl', Path(__file__).parent / 'hub' / 'hubctl.py')
hubctl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hubctl)

CHAT = 'CHAT-20261127091047-G4JP'
HERE = r'G:\My Drive\AI-Workbench\projects\Job_Search\chats\CHAT-20261127091047-G4JP\scratch'


class JobChatArgs(unittest.TestCase):
    def test_not_a_job_or_not_chat_mail_is_untouched(self):
        self.assertEqual(hubctl.job_chat_args(['search', 'x'], CHAT, HERE), ['search', 'x'])
        self.assertEqual(hubctl.job_chat_args(['chat-mail', '--chat', 'CHAT-20261119164352-XE5N'], '', HERE),
                         ['chat-mail', '--chat', 'CHAT-20261119164352-XE5N'])

    def test_own_chat_only(self):
        for rest in (['chat-mail', '--chat', 'CHAT-20261119164352-XE5N'], ['chat-mail', '--chat=CHAT-20261119164352-XE5N'],
                     ['chat-mail', '--chat', CHAT, '--chat', 'CHAT-20261119164352-XE5N'], ['chat-mail', '--ch', CHAT],
                     ['show', '1', 'chat-mail', '--chat', CHAT], ['chat-mail', '--chat', CHAT, '--root', '/x']):
            with self.assertRaises(SystemExit, msg=rest):
                hubctl.job_chat_args(rest, CHAT, HERE)
        self.assertEqual(hubctl.job_chat_args(['chat-mail', '--chat', CHAT, '--find', 'Ethos'], CHAT, HERE),
                         ['chat-mail', '--chat', CHAT, '--find', 'Ethos'])

    def test_link_takes_the_project_of_the_job_folder(self):
        got = hubctl.job_chat_args(['chat-mail', '--chat', CHAT, '--link', '19a1b2c3d4e5f6a7', '--title', 'Job'], CHAT, HERE)
        self.assertEqual(got[-2:], ['--project', 'Job_Search'])
        with self.assertRaises(SystemExit):  # the job never names the project
            hubctl.job_chat_args(['chat-mail', '--chat', CHAT, '--link', 't', '--project', 'Other'], CHAT, HERE)
        with self.assertRaises(SystemExit):  # a folder of another chat
            hubctl.job_chat_args(['chat-mail', '--chat', CHAT, '--link', 't'], CHAT,
                                 r'G:\x\projects\Job_Search\chats\CHAT-20261119164352-XE5N')


if __name__ == '__main__':
    unittest.main()
