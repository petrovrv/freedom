// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/assistantCall.selftest.ts - 0.108.0 the assistant's name and call words in a family chat
import { applyAssistant, assistantName, callsAssistant } from './members.ts';

let bad = 0;
const ok = (name: string, v: boolean) => { if (!v) { bad++; console.log('FAIL', name); } };

ok('default name', assistantName() === 'AI Workbench');
ok('@co calls', callsAssistant('@co привет') && callsAssistant('эй @со'));
ok('no call words yet', !callsAssistant('@джарвис привет'));
applyAssistant('Джарвис', ['джарвис', 'jarvis', 'bad word', 5]);
ok('name set', assistantName() === 'Джарвис');
ok('@джарвис calls', callsAssistant('@Джарвис, что там?') && callsAssistant('ну @jarvis') && callsAssistant('@JARVIS'));
ok('@co still calls', callsAssistant('@co'));
ok('not inside a word or mail', !callsAssistant('@джарвисы') && !callsAssistant('x@jarvis.be') && !callsAssistant('джарвис без собаки'));
applyAssistant('', null);
ok('empty falls back', assistantName() === 'AI Workbench' && !callsAssistant('@jarvis'));

console.log(bad ? `${bad} failed` : 'assistantCall: all passed');
process.exit(bad ? 1 : 0);
