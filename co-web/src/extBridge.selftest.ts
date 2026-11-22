// SPDX-License-Identifier: AGPL-3.0-or-later
// node src/extBridge.selftest.ts - the message checks of the ext/1 bridge (Freedom F10).
import { callRefusal, callRoute, capabilityRefusal, moduleUrl, parseExtMessage, parseHint, shellApps, shellOpen, shellSettings, type ModuleInfo } from './extBridge.ts';

let fail = 0;
function ok(name: string, cond: boolean): void {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
}

ok('url of a module page', moduleUrl({ id: 'geo-presence', version: '0.5.0' }, 'ui/header.html') === '/agent/module.php?m=geo-presence&f=ui%2Fheader.html&v=0.5.0');
ok('no path outside ui/', moduleUrl({ id: 'geo-presence', version: '1' }, 'server/ops.php') === null);
ok('no ..', moduleUrl({ id: 'geo-presence', version: '1' }, 'ui/../server/x.html') === null);
ok('bad id', moduleUrl({ id: '../x', version: '1' }, 'ui/a.html') === null);

ok('hello', parseExtMessage({ ext: 1, t: 'hello' })?.t === 'hello');
ok('other ext version ignored', parseExtMessage({ ext: 2, t: 'hello' }) === null);
ok('not an object', parseExtMessage('hello') === null);
const c = parseExtMessage({ ext: 1, t: 'call', id: 'c1', op: 'geo_state', args: { a: 1 } });
ok('call parsed', c?.t === 'call' && c.op === 'geo_state');
ok('args cannot carry another op', parseExtMessage({ ext: 1, t: 'call', id: 'c1', op: 'geo_state', args: { op: 'dossier_get' } }) === null);
ok('args cannot carry gz', parseExtMessage({ ext: 1, t: 'call', id: 'c1', op: 'geo_state', args: { gz: 'x' } }) === null);
ok('array args refused', parseExtMessage({ ext: 1, t: 'call', id: 'c1', op: 'x', args: [1] }) === null);
const huge = parseExtMessage({ ext: 1, t: 'call', id: 'c1', op: 'x', args: { s: 'x'.repeat(20000) } });
ok('huge args refused with an answer', huge?.t === 'call' && huge.error === 'too_large' && Object.keys(huge.args).length === 0);
ok('bad call id refused', parseExtMessage({ ext: 1, t: 'call', id: '<script>', op: 'x', args: {} }) === null);
const s = parseExtMessage({ ext: 1, t: 'size', w: 5000, h: -3, overlay: true });
ok('size clamped', s?.t === 'size' && s.w === 800 && s.h === 0 && s.overlay);

const m = { enabled: true, uiOps: ['geo_state'], personal: null };
ok('declared op allowed', callRefusal(m, 'geo_state') === '');
ok('core op refused', callRefusal(m, 'events') === 'op_not_declared');
ok('disabled module refused', callRefusal({ ...m, enabled: false }, 'geo_state') === 'module_disabled');

// capabilities (composer input, microphone, speech, uploads, storage)
const inp = parseExtMessage({ ext: 1, t: 'input', text: 'привет', interim: 'мир', done: false });
ok('input parsed', inp?.t === 'input' && inp.text === 'привет' && inp.interim === 'мир' && !inp.done);
ok('huge input refused', parseExtMessage({ ext: 1, t: 'input', text: 'x'.repeat(30000) }) === null);
const au = parseExtMessage({ ext: 1, t: 'audio', id: 'a1', action: 'start', maxSeconds: 99999 });
ok('audio parsed, max clamped', au?.t === 'audio' && au.maxSeconds === 600);
ok('unknown audio action refused', parseExtMessage({ ext: 1, t: 'audio', id: 'a1', action: 'play' }) === null);
const sp = parseExtMessage({ ext: 1, t: 'speech', id: 's1', action: 'start', lang: 'ru-RU' });
ok('speech parsed', sp?.t === 'speech' && sp.lang === 'ru-RU');
const spBad = parseExtMessage({ ext: 1, t: 'speech', id: 's1', action: 'start', lang: '<x>' });
ok('bad speech lang dropped', spBad?.t === 'speech' && spBad.lang === '');
const blob = new Blob(['abc'], { type: 'audio/webm' });
const up = parseExtMessage({ ext: 1, t: 'upload', id: 'u1', name: 'transcribe', file: blob, filename: 'audio.webm', fields: { lang: 'ru' } });
ok('upload parsed', up?.t === 'upload' && up.name === 'transcribe' && up.fields.lang === 'ru');
ok('upload field "file" refused', parseExtMessage({ ext: 1, t: 'upload', id: 'u1', name: 'x', file: blob, fields: { file: 'a' } }) === null);
ok('upload without a blob refused', parseExtMessage({ ext: 1, t: 'upload', id: 'u1', name: 'x', file: 'abc' }) === null);
const upBad = parseExtMessage({ ext: 1, t: 'upload', id: 'u1', name: 'x', file: blob, filename: '../a b' });
ok('upload filename sanitised', upBad?.t === 'upload' && upBad.filename === 'file');
ok('storage parsed', parseExtMessage({ ext: 1, t: 'storage', id: 'k1', action: 'set', key: 'lang', value: 'ru-RU' })?.t === 'storage');
ok('storage big value refused', parseExtMessage({ ext: 1, t: 'storage', id: 'k1', action: 'set', key: 'lang', value: 'x'.repeat(5000) }) === null);
const mod = { enabled: true, permissions: ['composer.input', 'media.audio'], uploads: ['transcribe'] };
ok('composer input allowed with the permission in the composer', !!inp && capabilityRefusal(mod, 'ui.composer', inp) === '');
ok('composer input refused in the header', !!inp && capabilityRefusal(mod, 'ui.header', inp) === 'composer_only');
ok('speech refused without its permission', !!sp && capabilityRefusal(mod, 'ui.composer', sp) === 'permission_not_declared');
ok('declared upload allowed', !!up && capabilityRefusal(mod, 'ui.composer', up) === '');
ok('undeclared upload refused', !!up && capabilityRefusal({ ...mod, uploads: [] }, 'ui.composer', up) === 'upload_not_declared');
ok('disabled module refused', !!inp && capabilityRefusal({ ...mod, enabled: false }, 'ui.composer', inp) === 'module_disabled');
// voice mode (composer.send, media.play, silence stop)
const au2 = parseExtMessage({ ext: 1, t: 'audio', id: 'a2', action: 'start', maxSeconds: 30, silenceMs: 50 });
ok('silenceMs clamped up', au2?.t === 'audio' && au2.silenceMs === 300);
ok('no silenceMs = 0', au?.t === 'audio' && au.silenceMs === 0);
const snd = parseExtMessage({ ext: 1, t: 'send', id: 'n1', text: 'hello' });
ok('send parsed', snd?.t === 'send' && snd.text === 'hello');
ok('empty send refused', parseExtMessage({ ext: 1, t: 'send', id: 'n1', text: '  ' }) === null);
const pl = parseExtMessage({ ext: 1, t: 'play', id: 'p1', action: 'play', bytes: new ArrayBuffer(8), type: 'audio/mpeg' });
ok('play parsed', pl?.t === 'play' && pl.type === 'audio/mpeg');
ok('play of a non-audio type refused', parseExtMessage({ ext: 1, t: 'play', id: 'p1', action: 'play', bytes: new ArrayBuffer(8), type: 'text/html' }) === null);
ok('play stop parsed', parseExtMessage({ ext: 1, t: 'play', id: 'p1', action: 'stop' })?.t === 'play');
ok('send refused without composer.send', !!snd && capabilityRefusal(mod, 'ui.composer', snd) === 'permission_not_declared');
ok('send allowed with composer.send', !!snd && capabilityRefusal({ ...mod, permissions: ['composer.send'] }, 'ui.composer', snd) === '');
ok('play refused outside the composer', !!pl && capabilityRefusal({ ...mod, permissions: ['media.play'] }, 'ui.header', pl) === 'composer_only');
const d = { ...m, device: { tools: [], ops: ['geo_folder_add'], default: 'on' as const, warning: '' } };
ok('server op routed to the server', JSON.stringify(callRoute(d, 'geo_state', true)) === '{"route":"server"}');
ok('device op routed to the device', JSON.stringify(callRoute(d, 'geo_folder_add', true)) === '{"route":"device"}');
ok('device op outside the app refused', JSON.stringify(callRoute(d, 'geo_folder_add', false)) === '{"error":"device_op_unavailable"}');
ok('undeclared op refused', JSON.stringify(callRoute(d, 'fs_delete', true)) === '{"error":"op_not_declared"}');
ok('device op of a disabled module refused', JSON.stringify(callRoute({ ...d, enabled: false }, 'geo_folder_add', true)) === '{"error":"module_disabled"}');
ok('no device block: refused', JSON.stringify(callRoute({ ...m, device: null }, 'geo_folder_add', true)) === '{"error":"op_not_declared"}');

// generic core calls: only the listed ones, only with the permission the manifest declares
const imp = { enabled: true, uiOps: [], personal: null, permissions: ['import.write'] };
ok('core call with its permission allowed', callRefusal(imp, 'core.import.write') === '');
ok('core call without the permission refused', callRefusal({ ...imp, permissions: [] }, 'core.import.write') === 'permission_not_declared');
ok('unknown core call refused', callRefusal(imp, 'core.dossier.read') === 'core_call_unknown');
ok('core call of a disabled module refused', callRefusal({ ...imp, enabled: false }, 'core.import.context') === 'module_disabled');
ok('a core call may carry a whole conversation', parseExtMessage({ ext: 1, t: 'call', id: 'c2', op: 'core.import.write', args: { s: 'x'.repeat(200000) } })?.t === 'call');
ok('a module op may not', (parseExtMessage({ ext: 1, t: 'call', id: 'c2', op: 'geo_state', args: { s: 'x'.repeat(200000) } }) as { error?: string } | null)?.error === 'too_large');

// ui.hint
const pick = { agent: 'codex', model: 'gpt-x', label: 'GPT X' };
const hm = parseExtMessage({ ext: 1, t: 'hint', hint: { text: 'Recommended for code:', title: 't', picks: [pick, { agent: 'claude', model: null, label: 'Claude' }] } });
ok('hint parsed with its picks in order', hm?.t === 'hint' && hm.hint?.picks.length === 2 && hm.hint.picks[1].model === null);
ok('hint null clears', (parseExtMessage({ ext: 1, t: 'hint', hint: null }) as { hint: unknown } | null)?.hint === null);
ok('hint without picks dropped', parseHint({ text: 'x', title: '', picks: [] }) === null);
ok('hint with a bad agent dropped', parseHint({ text: 'x', title: '', picks: [{ agent: 'A B', model: null, label: 'x' }] }) === null);
ok('hint with control characters dropped', parseHint({ text: 'x', title: '', picks: [pick] }) === null);
ok('hint text too long dropped', parseHint({ text: 'x'.repeat(200), title: '', picks: [pick] }) === null);

console.log('extBridge selftest OK');

// ext/1 shell: the launcher lists only ui.page entries with app: true of enabled modules
{
  const mk = (id: string, enabled: boolean, ui: ModuleInfo['ui']): ModuleInfo => ({ id, version: '1', title: id, description: '', enabled, standard: true, permissions: [], ui, uiOps: [], personal: null });
  const list = [
    mk('home', true, [{ slot: 'ui.page', id: 'home', entry: 'ui/home.html', title: 'Дом', app: true, icon: '🏠' }, { slot: 'ui.header', id: 'pair', entry: 'ui/h.html', title: 'x' }]),
    mk('imp', true, [{ slot: 'ui.page', id: 'import', entry: 'ui/i.html', title: 'Import', label: 'import…', app: true }]),
    mk('old', true, [{ slot: 'ui.page', id: 'p', entry: 'ui/p.html', title: 'Old page' }]),
    mk('off', false, [{ slot: 'ui.page', id: 'p', entry: 'ui/p.html', title: 'Off', app: true }]),
    mk('win', true, [{ slot: 'ui.page', id: 'ed', entry: 'ui/e.html', title: 'Editor', app: true, window: true, path: '/ed' }]),
  ];
  const apps = shellApps(list);
  ok('launcher: apps of enabled modules only', apps.map((a) => a.m.id).join() === 'home,imp,win');
  ok('launcher: icon from the manifest', apps[0].icon === '🏠');
  ok('launcher: icon falls back to the first letter', apps[1].icon === 'I' && apps[1].label === 'import…');
  ok('launcher: opens in the centre', shellOpen(apps[0], 'alice', true).href === '/e/alice/m/home/home' && shellOpen(apps[0], 'alice', true).target === 'self');
  ok('launcher: window page in its own window on a desktop app', shellOpen(apps[2], 'alice', true).target === 'blank' && shellOpen(apps[2], 'alice', true).href === '/ed');
  ok('launcher: window page in the centre in a browser', shellOpen(apps[2], 'alice', false).target === 'self');
  const set = shellSettings([
    ...list,
    mk('geo', true, [{ slot: 'ui.settings', id: 's', entry: 'ui/s.html', title: 'Geo' }]),
    mk('dom', true, [{ slot: 'ui.page', id: 'd', entry: 'ui/d.html', title: 'Dom', app: true, icon: '🏠' }, { slot: 'ui.settings', id: 's', entry: 'ui/s.html', title: 'Dom' }]),
    mk('offset', false, [{ slot: 'ui.settings', id: 's', entry: 'ui/s.html', title: 'Off' }]),
  ]);
  ok('launcher settings: modules with ui.settings, enabled only', set.map((x) => x.m.id).join() === 'geo,dom');
  ok('launcher settings: icon falls back to the first letter', set[0].icon === 'G');
  ok('launcher settings: icon of the module app', set[1].icon === '🏠');
}

if (fail) throw new Error(`extBridge selftest: ${fail} failed`);
