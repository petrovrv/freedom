// SPDX-License-Identifier: AGPL-3.0-or-later
// node modules/chat-import/tests/parse.selftest.ts - parsers of the claude.ai and ChatGPT exports, export detection.
import { detect } from '../src/parse.ts';

let failed = 0;
const ok = (name: string, cond: boolean) => {
  if (!cond) failed++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
};

const claude = detect({
  'conversations.json': [{
    uuid: 'c1', name: 'Pump sizing', created_at: '2026-01-02T10:00:00Z', updated_at: '2026-01-03T10:00:00Z', project_uuid: 'p1',
    chat_messages: [
      { sender: 'human', content: [{ type: 'text', text: 'Size a pump' }], created_at: '2026-01-02T10:00:00Z',
        attachments: [{ file_name: 'spec.txt', extracted_content: 'flow 10 m3/h' }] },
      { sender: 'assistant', content: [{ type: 'text', text: 'Here is how' }] },
      { sender: 'system', text: 'ignored' },
    ],
  }],
  'projects.json': [{ uuid: 'p1', name: 'Pumps', prompt_template: 'Be brief', docs: [{ filename: 'a.md', content: '# A' }] }],
});
ok('claude: source and one conversation', claude.source === 'claude' && claude.conversations.length === 1);
ok('claude: roles, attachment text', claude.conversations[0].messages.length === 2 && claude.conversations[0].messages[0].attachments?.[0].text === 'flow 10 m3/h');
ok('claude: project with instructions and docs', claude.projects[0].instructions === 'Be brief' && claude.projects[0].docs.length === 1 && claude.conversations[0].projectKey === 'p1');

const gpt = detect({
  'chat/conversations.json': [{
    conversation_id: 'g1', title: '', create_time: 1767348000, update_time: 1767348600, current_node: 'n3', gizmo_id: 'g-p-abcdef123456',
    mapping: {
      n0: { parent: null, children: ['n1'], message: null },
      n1: { parent: 'n0', children: ['n2'], message: { author: { role: 'user' }, content: { content_type: 'text', parts: ['Hello there, what is new?'] } } },
      n2: { parent: 'n1', children: ['n3'], message: { author: { role: 'assistant' }, content: { content_type: 'text', parts: ['Part one'] } } },
      n3: { parent: 'n2', children: [], message: { author: { role: 'assistant' }, content: { content_type: 'code', text: 'x = 1' } } },
    },
  }],
});
ok('chatgpt: active branch, merged assistant parts', gpt.source === 'chatgpt' && gpt.conversations[0].messages.length === 2 && gpt.conversations[0].messages[1].text.includes('x = 1'));
ok('chatgpt: title from the first user message', gpt.conversations[0].title.startsWith('Hello there'));
ok('chatgpt: project without a name gets a warning', gpt.projects.length === 1 && !gpt.projects[0].named && gpt.warnings.length === 1);

let threw = '';
try { detect({ 'x.json': [] }); } catch (e) { threw = (e as Error).message; }
ok('no conversations.json: clear error', threw.includes('No conversations.json'));

if (failed) { console.log(`${failed} failed`); process.exit(1); }
console.log('all passed');
