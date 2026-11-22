<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// modules-lib.php (schema 27, Freedom F10, SPEC §3.6) and the module geo-presence against a throwaway MariaDB (never
// production): install with sha256 of the files, switch, dispatch of declared ops, the person's own switch, tampered
// files, a module that is not standard, core ops a manifest cannot take, update with grown permissions and snapshot,
// pre-F10 data moved by the install hook, remove keep / erase.
// Driver: python tests/actions-test.py --test modules-test.php (seeds owner, sam, alex)
declare(strict_types=1);
require __DIR__ . '/../web/agent/modules-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (Throwable) { return true; }
};
// what install/freedom_modules.py does before op module_install: upload the files, hash them
$upload = static function (string $src, string $id, ?array $man = null): array {
    $dst = co_module_home($id);
    co_module_rmtree($dst);
    $files = [];
    $it = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS));
    foreach ($it as $f) {
        $rel = str_replace('\\', '/', substr($f->getPathname(), strlen($src) + 1));
        @mkdir(dirname("$dst/$rel"), 0700, true);
        copy($f->getPathname(), "$dst/$rel");
        if ($rel !== 'module.json' && $rel !== 'README.md') $files[$rel] = hash_file('sha256', "$dst/$rel");
    }
    $man ??= json_decode((string)file_get_contents("$src/module.json"), true);
    return ['manifest' => $man, 'files' => $files];
};
$src = realpath(__DIR__ . '/../modules/geo-presence');
$man = json_decode((string)file_get_contents("$src/module.json"), true);

$ok('nothing installed', co_modules_list($db, 'owner', true)['modules'] === []);
$ok('no module op before install', co_module_for_op($db, 'user', 'geo_state') === null);

// data of the core before F10 (state dir) must move into the module's folder
co_push_write_atomic(co_push_state_dir() . '/geo-privacy.json', json_encode(['sam' => ['mode' => 'home', 'ts' => 5]]));

$p = $upload($src, 'geo-presence');
$r = co_module_install($db, $p + ['standard' => true, 'publisher' => 'ed25519:test', 'people' => ['owner' => 'auto']]);
$ok('install ok, stays off', $r['ok'] && !$r['enabled'] && !$r['updated']);
$ok('install hook moved pre-F10 data', in_array('geo-privacy.json', $r['hook']['moved'] ?? [], true)
    && is_file(co_module_state_dir('geo-presence') . '/geo-privacy.json') && !is_file(co_push_state_dir() . '/geo-privacy.json'));
$ok('installer default switch for owner', ($r['people']['owner'] ?? false) === true);
$ok('disabled: op not dispatched', co_module_for_op($db, 'user', 'geo_state') === null);
$ok('disabled: hidden from members', co_modules_list($db, 'sam', false)['modules'] === []);
$ok('disabled: owner sees it', count(co_modules_list($db, 'owner', true)['modules']) === 1);

co_module_set($db, 'geo-presence', true, 'owner');
$ok('enabled: user op found', (co_module_for_op($db, 'user', 'geo_state')['id'] ?? '') === 'geo-presence');
$ok('enabled: push op found', (co_module_for_op($db, 'push', 'geo_put')['id'] ?? '') === 'geo-presence');
$ok('user op not on the push channel', co_module_for_op($db, 'push', 'geo_state') === null);
$list = co_modules_list($db, 'sam', false)['modules'];
$ok('member sees the header page and uiOps', $list[0]['ui'][0]['slot'] === 'ui.header' && $list[0]['uiOps'] === ['geo_state', 'geo_answer', 'geo_privacy']);
$ok('member switch defaults to ask', $list[0]['personal']['mode'] === 'ask');

// geo_put from the home PC: only members whose switch is auto get into the header
$put = ['members' => [['user' => 'owner', 'name' => 'O', 'state' => 'home'], ['user' => 'sam', 'name' => 'S', 'state' => 'there', 'place' => 'Library']],
    'suggestions' => [['id' => 'abcd-1', 'user' => 'sam', 'area' => 'x']]];
$res = json_decode(json_encode(co_module_call($db, 'push', 'geo_put', $put)), true);
$ok('geo_put ok', ($res['ok'] ?? false) === true);
$ok('ask member: PC told off', ($res['privacy']['sam']['mode'] ?? '') === 'off');
$st = co_module_call($db, 'user', 'geo_state', [], 'owner', true);
$ok('ask member not shown', array_column($st['members'], 'user') === ['owner'] && $st['suggestions'] === []);

$ok('owner sets a default for sam', co_module_person_set($db, 'geo-presence', 'sam', 'auto', 'owner')['ok']);
$res = json_decode(json_encode(co_module_call($db, 'push', 'geo_put', $put)), true);
$st = co_module_call($db, 'user', 'geo_state', [], 'sam', false);
$ok('auto member shown', array_column($st['members'], 'user') === ['owner', 'sam']);
$ok('privacy from before F10 still applies (mode home: no place)', ($st['members'][1]['place'] ?? 'x') === '' && ($res['privacy']['sam']['mode'] ?? '') === 'home');
$ok('sam chooses off', co_module_person_set($db, 'geo-presence', 'sam', 'off', 'sam')['ok']);
$ok('owner cannot override sam', (co_module_person_set($db, 'geo-presence', 'sam', 'auto', 'owner')['error'] ?? '') === 'person_decides');
$st = co_module_call($db, 'user', 'geo_state', [], 'owner', true);
$ok('off member hidden at once (before the next put)', array_column($st['members'], 'user') === ['owner']);
$ok('switch of a non-member refused', (co_module_person_set($db, 'geo-presence', 'nobody', 'auto', 'nobody')['error'] ?? '') === 'bad_env');
$people = co_modules_list($db, 'owner', true)['modules'][0]['people'];
$samRow = array_values(array_filter($people, fn($p) => $p['env'] === 'sam'))[0] ?? [];
$ok('owner sees every switch and whose choice it is', count($people) === 3 && ($samRow['own'] ?? false) === true);

// a tampered server file is never included (a copy under another id: the load cache of this request has the original)
$m = co_modules($db, true)['geo-presence'];
co_module_copy(co_module_home('geo-presence'), co_module_home('tamper-check'));
file_put_contents(co_module_home('tamper-check') . '/server/geo-lib.php', "\n// changed", FILE_APPEND);
$ok('tampered file refused by hash', $throws(fn() => co_module_load(array_merge($m, ['id' => 'tamper-check']))));
co_module_rmtree(co_module_home('tamper-check'));
$r = co_module_install($db, ['manifest' => $man, 'files' => array_merge($m['files'], ['server/ops.php' => str_repeat('0', 64)])]);
$ok('install refuses files that do not match', ($r['error'] ?? '') === 'file_mismatch');

// a module not signed by the maintainers brings no PHP
$ops = static fn(array $user): array => array_merge($man['server'], ['ops' => ['user' => $user, 'push' => [], 'host' => []]]);
$r = co_module_install($db, $upload($src, 'thief', array_merge($man, ['id' => 'thief', 'server' => $ops(['geo_privacy'])])) + ['standard' => false]);
$ok('op of another module refused', ($r['error'] ?? '') === 'op_taken' && ($r['by'] ?? '') === 'geo-presence');
$p3 = $upload($src, 'other', array_merge($man, ['id' => 'other', 'server' => $ops(['other_state'])]));
$r = co_module_install($db, $p3 + ['standard' => false]);
co_module_set($db, 'other', true, 'owner');
$ok('non-standard installs', $r['ok'] && $r['hook'] === null);
$ok('non-standard: no server code', $throws(fn() => co_module_load(co_modules($db, true)['other'])));
co_module_remove($db, ['id' => 'other', 'data' => 'erase']);

// a manifest cannot hand its page a core op
$p4 = $upload($src, 'greedy', array_merge($man, ['id' => 'greedy', 'server' => $ops(['events', 'greedy_state'])]));
co_module_install($db, $p4 + ['standard' => false]); // not loaded: the list alone is under test
co_module_set($db, 'greedy', true, 'owner');
$g = array_values(array_filter(co_modules_list($db, 'owner', true, ['events', 'chats'])['modules'], fn($x) => $x['id'] === 'greedy'))[0];
$ok('core op removed from uiOps', !in_array('events', $g['uiOps'], true) && in_array('greedy_state', $g['uiOps'], true));
co_module_remove($db, ['id' => 'greedy', 'data' => 'erase']);

// ext range
$r = co_module_install($db, ['manifest' => array_merge($man, ['id' => 'future', 'ext' => '>=2']), 'files' => []]);
$ok('incompatible ext refused', ($r['error'] ?? '') === 'ext_incompatible');
$ok('ext ranges', co_module_ext_ok('>=1 <2') && co_module_ext_ok('1') && co_module_ext_ok('') && !co_module_ext_ok('<1') && !co_module_ext_ok('x'));

// update: same switch, snapshot of the data, more permissions -> off until the owner agrees
$p = $upload($src, 'geo-presence', array_merge($man, ['version' => '0.5.1']));
$r = co_module_install($db, $p + ['standard' => true]);
$ok('update keeps it on, snapshot taken', $r['ok'] && $r['updated'] && $r['enabled'] && $r['snapshot']);
sleep(1); // snapshot names carry the second
$p = $upload($src, 'geo-presence', array_merge($man, ['version' => '0.6.0', 'permissions' => array_merge($man['permissions'], ['net.any'])]));
$r = co_module_install($db, $p + ['standard' => true]);
$ok('grown permissions switch it off', $r['ok'] && !$r['enabled'] && $r['grew'] === ['net.any']);
sleep(1);
$p = $upload($src, 'geo-presence', array_merge($man, ['version' => '0.6.1', 'permissions' => array_merge($man['permissions'], ['net.any'])]));
co_module_install($db, $p + ['standard' => true]);
@mkdir(dirname(co_module_state_dir('geo')) . '/.snap/geo@0.1@20000101000000', 0700, true);  // another module "geo"
co_module_snapshot('geo', '0.2');
$snaps = glob(dirname(co_module_state_dir('geo-presence')) . '/.snap/geo-presence@*') ?: [];
$ok('a module "geo" keeps the snapshots of "geo-presence"', count($snaps) === 2);
$ok('at most two snapshots', count($snaps) === 2);
$ok('snapshot holds the data', is_file(end($snaps) . '/geo-privacy.json'));
// a failing install hook drops the new registration (an update puts the old row back the same way; the load cache of
// this one process keeps the first geo-presence handlers, so a fresh id is used)
$bad = $upload($src, 'broken', array_merge($man, ['id' => 'broken', 'server' => $ops(['broken_state'])]));
file_put_contents(co_module_home('broken') . '/server/ops.php', "<?php
throw new RuntimeException('hook broke');
");
$bad['files']['server/ops.php'] = hash_file('sha256', co_module_home('broken') . '/server/ops.php');
$r = co_module_install($db, $bad + ['standard' => true]);
$ok('failing hook: refused, no row left', ($r['error'] ?? '') === 'install_hook_failed' && !isset(co_modules($db, true)['broken']));
$ok('switches survive updates', co_module_person_mode($db, co_modules($db, true)['geo-presence'], 'sam') === 'off');

// ext/1 device.tools: what a device sees of an enabled module (tools with risk class, device ops, device/ files)
$devSrc = sys_get_temp_dir() . '/co-devmod-' . getmypid();
co_module_rmtree($devSrc);
@mkdir("$devSrc/device", 0700, true);
@mkdir("$devSrc/ui", 0700, true);
file_put_contents("$devSrc/device/hello.py", "print('{}')\n");
file_put_contents("$devSrc/ui/settings.html", "<!doctype html><title>s</title>\n");
$devMan = ['schema' => 1, 'id' => 'devmod', 'version' => '0.1.0', 'title' => 'Device module', 'ext' => '>=1 <2', 'permissions' => ['device.tools'],
    'provides' => ['ui' => [['slot' => 'ui.settings', 'id' => 'settings', 'entry' => 'ui/settings.html']],
        'deviceTools' => [
            ['name' => 'devmod_hello', 'class' => 'R', 'description' => 'says hello', 'run' => 'device/hello.py', 'untrusted' => false],
            ['name' => 'devmod_native', 'class' => 'X', 'input' => ['type' => 'object']],
            ['name' => 'Bad Name', 'class' => 'R'],
            ['name' => 'devmod_badclass', 'class' => 'Z'],
            ['name' => 'devmod_outside', 'class' => 'R', 'run' => 'server/ops.php'],
            ['name' => 'devmod_missing', 'class' => 'R', 'run' => 'device/none.py']],
        'deviceOps' => ['devmod_folder_add', 'X bad']],
    'device' => ['default' => 'off', 'warning' => 'Your data, your choice.', 'ops' => 'device/hello.py']];
$r = co_module_install($db, $upload($devSrc, 'devmod', $devMan) + ['standard' => false]);
$ok('device module installs', $r['ok'] ?? false);
$row = array_values(array_filter(co_modules_list($db, 'sam', false)['modules'], fn($x) => $x['id'] === 'devmod'));
$ok('disabled device module: not listed for members', $row === []);
co_module_set($db, 'devmod', true, 'owner');
$row = array_values(array_filter(co_modules_list($db, 'sam', false)['modules'], fn($x) => $x['id'] === 'devmod'))[0] ?? [];
$dev = json_decode(json_encode($row['device'] ?? null), true);
$ok('device tools: only valid ones', array_column($dev['tools'] ?? [], 'name') === ['devmod_hello', 'devmod_native']);
$ok('device tool keeps class, run and untrusted', ($dev['tools'][0]['class'] ?? '') === 'R' && $dev['tools'][0]['run'] === 'device/hello.py'
    && $dev['tools'][0]['untrusted'] === false && $dev['tools'][1]['untrusted'] === true && $dev['tools'][1]['run'] === '');
$ok('device ops: only valid names', ($dev['ops'] ?? null) === ['devmod_folder_add']);
$ok('device files with sha256', array_keys($dev['files'] ?? []) === ['device/hello.py'] && $dev['files']['device/hello.py'] === hash_file('sha256', "$devSrc/device/hello.py"));
$ok('device default, warning and ops file', ($dev['default'] ?? '') === 'off' && ($dev['warning'] ?? '') === 'Your data, your choice.' && ($dev['opsRun'] ?? '') === 'device/hello.py');
$ok('settings page listed', ($row['ui'][0]['slot'] ?? '') === 'ui.settings');
co_module_set($db, 'devmod', false, 'owner');
$owner = array_values(array_filter(co_modules_list($db, 'owner', true)['modules'], fn($x) => $x['id'] === 'devmod'))[0] ?? [];
$ok('disabled: no device block', array_key_exists('device', $owner) && $owner['device'] === null);
co_module_remove($db, ['id' => 'devmod', 'data' => 'erase']);
// without the permission device.tools a device gets nothing of the module
$noPerm = $devMan;
$noPerm['permissions'] = [];
$r = co_module_install($db, $upload($devSrc, 'devmod', $noPerm) + ['standard' => false]);
co_module_set($db, 'devmod', true, 'owner');
$row = array_values(array_filter(co_modules_list($db, 'sam', false)['modules'], fn($x) => $x['id'] === 'devmod'))[0] ?? [];
$dev = json_decode(json_encode($row['device'] ?? null), true);
$ok('no device.tools permission: no tools, ops or files', ($r['ok'] ?? false) && $dev['tools'] === [] && $dev['ops'] === [] && $dev['files'] === []);
co_module_remove($db, ['id' => 'devmod', 'data' => 'erase']);
co_module_rmtree($devSrc);

// remove
$ok('remove keep', co_module_remove($db, ['id' => 'geo-presence', 'data' => 'keep'])['ok']);
$ok('data kept', is_file(co_module_state_dir('geo-presence') . '/geo-privacy.json'));
$ok('gone from the list', co_modules_list($db, 'owner', true)['modules'] === []);
$p = $upload($src, 'geo-presence');
co_module_install($db, $p + ['standard' => true]);
$ok('reinstall finds the switches', co_module_person_mode($db, co_modules($db, true)['geo-presence'], 'sam') === 'off');
co_module_remove($db, ['id' => 'geo-presence', 'data' => 'erase']);
$ok('erase deletes the snapshots too', (glob(dirname(co_module_state_dir('geo-presence')) . '/.snap/geo-presence@*') ?: []) === []);
$ok('erase deletes data and switches', !is_file(co_module_state_dir('geo-presence') . '/geo-privacy.json') && co_module_people($db, 'geo-presence') === []);
$ok('journal written', (int)$db->query("SELECT COUNT(*) FROM module_log WHERE module_id = 'geo-presence'")->fetch_row()[0] >= 8);

// F10 modules beyond the probe: a ui.page with a sidebar label and import sources, a hidden ui.hint page
foreach (['chat-import', 'task-hints'] as $mid) {
    $p = $upload(realpath(__DIR__ . "/../modules/$mid"), $mid);
    $ok("$mid installs", co_module_install($db, $p + ['standard' => true])['ok']);
    co_module_set($db, $mid, true, 'owner');
}
$byId = array_column(co_modules_list($db, 'sam', false)['modules'], null, 'id');
$imp = $byId['chat-import'] ?? [];
$ok('ui.page with its label', ($imp['ui'][0]['slot'] ?? '') === 'ui.page' && ($imp['ui'][0]['label'] ?? '') === 'Import');
$ok('ui.page is an app of the launcher with its icon', ($imp['ui'][0]['app'] ?? false) === true && ($imp['ui'][0]['icon'] ?? '') === '📥');
$ok('import sources listed', array_column($imp['imports'] ?? [], 'label', 'source') === ['claude' => 'claude.ai', 'chatgpt' => 'ChatGPT']);
$ok('no ops for a page without server code', ($imp['uiOps'] ?? null) === []);
$ok('ui.hint slot listed', (($byId['task-hints'] ?? [])['ui'][0]['slot'] ?? '') === 'ui.hint');
co_module_set($db, 'chat-import', false, 'owner');
$ok('disabled: no import sources for anybody', !isset(array_column(co_modules_list($db, 'sam', false)['modules'], null, 'id')['chat-import'])
    && (array_column(co_modules_list($db, 'owner', true)['modules'], null, 'id')['chat-import']['imports'] ?? null) === []);
$ok('switched off: its source labels stay for chats imported before', array_column(co_modules_list($db, 'sam', false)['importLabels'], 'speaker', 'source')
    === ['claude' => 'Claude (claude.ai)', 'chatgpt' => 'ChatGPT']);
foreach (['chat-import', 'task-hints'] as $mid) co_module_remove($db, ['id' => $mid, 'data' => 'erase']);

echo $fail ? "$fail FAILED\n" : "modules-test OK\n";
exit($fail ? 1 : 0);
