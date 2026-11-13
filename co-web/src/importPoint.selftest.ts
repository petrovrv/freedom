// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/importPoint.selftest.ts - checks of what an import module hands the core (point `import`, Freedom F10).
import { importLabel, importSpeaker, parseImportWrite, setImportSources } from './importPoint.ts';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}
function err(f: () => unknown): string {
  try { f(); return ''; } catch (e) { return (e as Error).message; }
}

const conv = { source: 'svc', id: 'abc', title: ' Hello ', createdUtc: '2026-01-01T00:00:00Z', updatedUtc: '', messages: [
  { role: 'user', text: 'hi', attachments: [{ name: 'a.txt', text: 'body' }, { name: 'b.png' }] },
  { role: 'assistant', text: 'hello', createdUtc: '2026-01-01T00:01:00Z' },
] };
const w = parseImportWrite({ sourceLabel: 'Service', conversation: conv, target: { kind: 'new', projectId: 'svc-imported', title: 'Imported', docs: [{ name: 'd.md', content: '# D' }] } });
ok('write parsed, title trimmed', w.conversation.title === 'Hello' && w.conversation.messages.length === 2);
ok('long title cut, not refused', parseImportWrite({ conversation: { ...conv, title: 'x'.repeat(5000) }, target: { kind: 'existing', projectId: 'p1' } }).conversation.title.length === 300);
ok('attachment text kept, name-only attachment kept', w.conversation.messages[0].attachments?.[0].text === 'body' && w.conversation.messages[0].attachments?.[1].text === undefined);
ok('new target with docs', w.target.kind === 'new' && w.target.docs?.length === 1);
ok('existing target', parseImportWrite({ sourceLabel: 'S', conversation: conv, target: { kind: 'existing', projectId: 'p1' } }).target.kind === 'existing');
ok('bad source refused', err(() => parseImportWrite({ sourceLabel: 'S', conversation: { ...conv, source: 'A B' }, target: { kind: 'existing', projectId: 'p1' } })) === 'bad source');
ok('bad role refused', err(() => parseImportWrite({ sourceLabel: 'S', conversation: { ...conv, messages: [{ role: 'system', text: 'x' }] }, target: { kind: 'existing', projectId: 'p1' } })).startsWith('bad message'));
ok('bad project id refused', err(() => parseImportWrite({ sourceLabel: 'S', conversation: conv, target: { kind: 'existing', projectId: '../x' } })) === 'bad projectId');
ok('bad time refused', err(() => parseImportWrite({ sourceLabel: 'S', conversation: { ...conv, createdUtc: 'yesterday' }, target: { kind: 'existing', projectId: 'p1' } })) === 'bad createdUtc');
ok('unknown target kind refused', err(() => parseImportWrite({ sourceLabel: 'S', conversation: conv, target: { kind: 'other', projectId: 'p1' } })) === 'bad target kind');

ok('label of an unknown source is its name', importLabel('svc') === 'svc' && importSpeaker('svc') === 'svc');
setImportSources([{ source: 'svc', label: 'Service', speaker: 'Bot (Service)' }, { source: 'BAD SOURCE', label: 'x' }, { source: 'two' }]);
ok('labels from enabled modules', importLabel('svc') === 'Service' && importSpeaker('svc') === 'Bot (Service)');
ok('speaker defaults to the label, label to the source', importLabel('two') === 'two' && importSpeaker('two') === 'two');
ok('malformed source ignored', importLabel('BAD SOURCE') === 'BAD SOURCE');
setImportSources([]);
ok('module off: raw names again', importLabel('svc') === 'svc');

if (fail) throw new Error(`importPoint selftest: ${fail} failed`);
console.log('importPoint selftest OK');
