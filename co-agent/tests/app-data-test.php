<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Apps made on the fly (Freedom F14c, SPEC §3.6a item 6) against a throwaway MariaDB (never production): a module of one
// environment (manifest envs) signed by the owner, not the maintainers - nobody else gets its pages, ops, data or
// actions; the core keeps its data (apps-lib.php co_app_data, permission data.env) per person with limits; the agent's
// core data actions write it and refresh the open app; switching off keeps the data, remove --data erase deletes it.
// Driver: python tests/actions-test.py --test app-data-test.php (seeds owner, sam, alex)
declare(strict_types=1);
require __DIR__ . '/../web/agent/apps-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f, string $like = ''): bool {
    try { $f(); return false; } catch (InvalidArgumentException $e) { return $like === '' || str_contains($e->getMessage(), $like); }
};
$job = static function (string $id, string $env, string $state = 'claimed', array $req = []) use ($db): void {
    $st = $db->prepare("INSERT INTO jobs (job_id, env, project_id, chat_id, state, request, sha, synced_utc)
                        VALUES (?, ?, 'PRJ-T', ?, ?, ?, '', UTC_TIMESTAMP(3))");
    $chat = 'CHAT-' . $id;
    $r = json_encode((object)$req);
    $st->bind_param('sssss', $id, $env, $chat, $state, $r);
    $st->execute();
    $st->close();
};
// an app module as `freedom_modules.py new --app` writes it (only the page; no server code: it is not standard)
$install = static function (string $id, array $extra = []) use ($db): array {
    $dst = co_module_home($id);
    co_module_rmtree($dst);
    @mkdir("$dst/ui", 0700, true);
    file_put_contents("$dst/ui/main.html", '<!doctype html><title>x</title>');
    $man = array_merge(['schema' => 1, 'id' => $id, 'version' => '0.1.0', 'title' => 'Покупки', 'ext' => '>=1 <2',
        'scope' => 'environment', 'envs' => ['sam'], 'permissions' => ['data.env'],
        'provides' => ['ui' => [['slot' => 'ui.page', 'id' => 'main', 'entry' => 'ui/main.html', 'title' => 'Покупки',
            'app' => true, 'icon' => '🛒', 'label' => 'Покупки']]],
        'dataPolicy' => ['onDisable' => 'keep', 'onRemove' => 'ask', 'migrations' => 'additive'], 'defaults' => ['enabled' => false]], $extra);
    return co_module_install($db, ['manifest' => $man, 'files' => ['ui/main.html' => hash_file('sha256', "$dst/ui/main.html")],
        'standard' => false, 'publisher' => 'owner']);
};
$listFor = static function (string $env, bool $owner) use ($db): ?array {
    foreach (co_modules_list($db, $env, $owner)['modules'] as $m) if ($m['id'] === 'shop') return $m;
    return null;
};

$ok('envs must be members', ($install('shop', ['envs' => ['nobody']])['error'] ?? '') === 'bad_envs'
    && ($install('shop', ['envs' => []])['error'] ?? '') === 'bad_envs' && ($install('shop', ['envs' => 'sam'])['error'] ?? '') === 'bad_envs');
$r = $install('shop');
$ok('an own app of sam installs, off', $r['ok'] === true && $r['enabled'] === false);
$ok('disabled: no data', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['error'] === 'module_disabled');
co_module_set($db, 'shop', true, 'owner');

// who sees it
$sam = $listFor('sam', false);
$ok('sam sees the app tile', ($sam['ui'][0]['app'] ?? false) === true && ($sam['envs'] ?? []) === ['sam']);
$ok('alex does not see it at all', $listFor('alex', false) === null);
$own = $listFor('owner', true);
$ok('the owner manages it but gets no page', $own !== null && $own['enabled'] === true && $own['ui'] === [] && $own['actions'] === []);
$ok('sam: data actions listed for refresh', ($sam['actions'] ?? []) === ['data_list', 'data_get', 'data_set', 'data_delete']);

// the page's data (user op app_data)
$ok('empty get', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items']) === ['ok' => true, 'value' => null, 'utc' => null]);
$w = co_app_data($db, 'shop', 'sam', 'set', ['key' => 'items', 'value' => '[{"text":"молоко"}]']);
$ok('set', $w['ok'] === true && is_string($w['utc']));
$ok('get back', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['value'] === '[{"text":"молоко"}]');
$ok('other people get nothing', co_app_data($db, 'shop', 'alex', 'get', ['key' => 'items'])['error'] === 'not_your_app'
    && co_app_data($db, 'shop', 'owner', 'set', ['key' => 'items', 'value' => 'x'])['error'] === 'not_your_app');
$ok('not a member', co_app_data($db, 'shop', 'ghost', 'get', ['key' => 'items'])['error'] === 'bad_env');
$ok('bad key', $throws(fn() => co_app_data($db, 'shop', 'sam', 'set', ['key' => '../x', 'value' => '1']), 'key'));
$ok('bad value', $throws(fn() => co_app_data($db, 'shop', 'sam', 'set', ['key' => 'a', 'value' => 5]), 'value'));
$ok('bad action', $throws(fn() => co_app_data($db, 'shop', 'sam', 'drop', ['key' => 'a']), 'action'));
$ok('value too large', co_app_data($db, 'shop', 'sam', 'set', ['key' => 'big', 'value' => str_repeat('x', CO_APP_DATA_VALUE_MAX + 1)])['error'] === 'too_large');
co_app_data($db, 'shop', 'sam', 'set', ['key' => 'note.1', 'value' => 'a']);
co_app_data($db, 'shop', 'sam', 'set', ['key' => 'note.2', 'value' => 'bb']);
$l = co_app_data($db, 'shop', 'sam', 'list', ['prefix' => 'note.']);
$ok('list with prefix, sorted, sizes', array_column($l['keys'], 'key') === ['note.1', 'note.2'] && $l['keys'][1]['size'] === 2);
$ok('null value deletes', co_app_data($db, 'shop', 'sam', 'set', ['key' => 'note.2', 'value' => null])['deleted'] === true
    && co_app_data($db, 'shop', 'sam', 'get', ['key' => 'note.2'])['value'] === null);
$ok('delete of a missing key', co_app_data($db, 'shop', 'sam', 'delete', ['key' => 'nope'])['deleted'] === false);
// limits: keys and total
for ($i = 0; $i < CO_APP_DATA_KEYS_MAX - 2; $i++) co_app_data($db, 'shop', 'sam', 'set', ['key' => "k$i", 'value' => '1']);
$ok('key limit', co_app_data($db, 'shop', 'sam', 'set', ['key' => 'one-more', 'value' => '1'])['error'] === 'too_many_keys');
for ($i = 0; $i < 16; $i++) co_app_data($db, 'shop', 'sam', 'set', ['key' => "k$i", 'value' => str_repeat('y', 65000)]);
$ok('total limit', co_app_data($db, 'shop', 'sam', 'set', ['key' => 'k20', 'value' => str_repeat('z', 65000)])['error'] === 'quota');
for ($i = 0; $i < CO_APP_DATA_KEYS_MAX - 2; $i++) co_app_data($db, 'shop', 'sam', 'delete', ['key' => "k$i"]);
$ok('the refused writes changed nothing', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['value'] === '[{"text":"молоко"}]'
    && count(co_app_data($db, 'shop', 'sam', 'list', [])['keys']) === 2);

// the agent (app_list / app_action with a job of sam; alex's job sees nothing)
$job('J-SAM', 'sam');
$job('J-ALEX', 'alex');
$job('J-OWN', 'owner');
$job('J-RO', 'sam', 'claimed', ['permissions' => ['files' => 'read']]);
$apps = co_app_list($db, ['jobId' => 'J-SAM'])['apps'];
$shop = $apps[0] ?? [];
$ok('sam\'s agent sees the app and its data actions', ($shop['app'] ?? '') === 'shop' && ($shop['icon'] ?? '') === '🛒'
    && array_column($shop['actions'] ?? [], 'op') === ['data_list', 'data_get', 'data_set', 'data_delete']
    && !in_array(true, array_column($shop['actions'], 'outward'), true));
$ok('alex\'s and the owner\'s agents do not', co_app_list($db, ['jobId' => 'J-ALEX'])['apps'] === []
    && co_app_list($db, ['jobId' => 'J-OWN'])['apps'] === []
    && co_app_action($db, ['jobId' => 'J-OWN', 'app' => 'shop', 'action' => 'data_get', 'args' => ['key' => 'items']])['error'] === 'unknown_action');
$ev0 = co_app_events($db, 'sam', -1)['seq'];
$g = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'shop', 'action' => 'data_get', 'args' => ['key' => 'items']]);
$ok('agent reads', $g['ok'] === true && $g['result']['value'] === '[{"text":"молоко"}]');
$ok('a read refreshes nothing', co_app_events($db, 'sam', -1)['seq'] === $ev0);
$s = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'shop', 'action' => 'data_set',
    'args' => ['key' => 'items', 'value' => '[{"text":"молоко"},{"text":"хлеб"}]']]);
$ok('agent writes', $s['ok'] === true && co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['value'] === '[{"text":"молоко"},{"text":"хлеб"}]');
$ev = co_app_events($db, 'sam', $ev0);
$ok('a write refreshes sam\'s open app', $ev['seq'] === $ev0 + 1 && $ev['events'][0]['app'] === 'shop' && $ev['events'][0]['action'] === 'data_set');
$ok('schema checked', $throws(fn() => co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'shop', 'action' => 'data_set', 'args' => ['key' => 'a']]), 'args.value'));
$ok('an agent value longer than 64 K characters: refused by the schema', $throws(fn() => co_app_action($db, ['jobId' => 'J-SAM',
    'app' => 'shop', 'action' => 'data_set', 'args' => ['key' => 'big', 'value' => str_repeat('x', CO_APP_DATA_VALUE_MAX + 1)]]), 'up to 65536'));
$long = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'shop', 'action' => 'data_set', 'args' => ['key' => 'long', 'value' => str_repeat('щ', 30000)]]);
$ok('a value of 30000 letters (60 KB) passes', $long['ok'] === true);
$bad = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'shop', 'action' => 'data_set', 'args' => ['key' => 'big', 'value' => str_repeat('щ', 40000)]]);
$ok('80 KB is a failed action (too_large)', $bad['ok'] === false && $bad['result']['error'] === 'too_large');
co_app_data($db, 'shop', 'sam', 'delete', ['key' => 'long']);
$ok('read-only chat: no write', co_app_action($db, ['jobId' => 'J-RO', 'app' => 'shop', 'action' => 'data_set', 'args' => ['key' => 'a', 'value' => '1']])['error'] === 'read_only');
$st = $db->prepare("SELECT COUNT(*) FROM module_log WHERE module_id = 'shop' AND action = 'action'");
$st->execute();
$ok('agent actions are journaled', (int)$st->get_result()->fetch_row()[0] >= 3);
$st->close();

// switched off: data kept; on again: it is there; remove keep / erase
co_module_set($db, 'shop', false, 'owner');
$ok('off: no data, no actions', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['error'] === 'module_disabled'
    && co_app_list($db, ['jobId' => 'J-SAM'])['apps'] === []);
co_module_set($db, 'shop', true, 'owner');
$ok('on again: data kept', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['value'] === '[{"text":"молоко"},{"text":"хлеб"}]');
co_module_remove($db, ['id' => 'shop', 'data' => 'keep']);
$install('shop');
co_module_set($db, 'shop', true, 'owner');
$ok('remove --data keep, install again: data back', co_app_data($db, 'shop', 'sam', 'get', ['key' => 'items'])['value'] !== null);
co_module_remove($db, ['id' => 'shop', 'data' => 'erase']);
$st = $db->prepare("SELECT COUNT(*) FROM kv WHERE name = 'app-data.shop'");
$st->execute();
$ok('remove --data erase deletes the data', (int)$st->get_result()->fetch_row()[0] === 0);
$st->close();

// a module without envs is for everybody; without data.env it has no data
$r = $install('pub', ['envs' => null, 'permissions' => []]);
co_module_set($db, 'pub', true, 'owner');
$ok('no envs: everybody', $r['ok'] === true && co_module_for_env(co_modules($db)['pub'], 'alex'));
$ok('no data.env: refused', co_app_data($db, 'pub', 'alex', 'get', ['key' => 'a'])['error'] === 'permission_not_declared'
    && co_app_data_actions(co_modules($db)['pub']) === []);
co_module_remove($db, ['id' => 'pub', 'data' => 'erase']);

echo $fail ? "$fail FAILED\n" : "app-data-test OK\n";
exit($fail ? 1 : 0);
