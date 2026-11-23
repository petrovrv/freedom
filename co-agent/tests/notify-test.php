<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Notifications of apps (apps-lib.php co_app_notify, Freedom F14b-3, SPEC §3.6a item 8) against a throwaway MariaDB
// (never production): the permission notify.push, the person's env only, the push to the core's devices channel and to
// the notify hooks of the other modules (never the sender's own), the click address, tag repeats, the hourly limit,
// the person's switch, modules_list, the journal. Driver: python tests/actions-test.py --test notify-test.php
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

$log = sys_get_temp_dir() . '/notify-test-' . getmypid() . '.json';
@unlink($log);
$seen = static function () use ($log): array {
    $all = is_file($log) ? json_decode((string)file_get_contents($log), true) : [];
    @unlink($log);
    return $all;
};
// a standard module: an app page, a user op that notifies through $ctx['notify'], its own notify hook (must not get its
// own pushes back); $perm: whether the manifest declares notify.push
$install = static function (string $id, bool $perm, bool $app = true) use ($db, $log): array {
    $dst = co_module_home($id);
    co_module_rmtree($dst);
    @mkdir("$dst/server", 0700, true);
    file_put_contents("$dst/server/ops.php", '<?php
$log = ' . var_export($log, true) . ';
return ["user" => ["' . str_replace('-', '_', $id) . '_ping" => static fn(array $c, array $in): array => $c["notify"]($in)],
    "push" => [], "host" => [], "notify" => [
    "copy" => ["push" => static function (array $c, array $in) use ($log): void {
        $all = is_file($log) ? json_decode((string)file_get_contents($log), true) : [];
        $all[] = [' . var_export($id, true) . ', $in["envs"] ?? [], $in["title"] ?? "", $in["url"] ?? ""];
        file_put_contents($log, json_encode($all));
    }],
]];
');
    $ui = $app ? [['slot' => 'ui.page', 'id' => 'main', 'entry' => 'ui/main.html', 'title' => 'Main', 'app' => true, 'icon' => '✉', 'label' => 'Mail'],
        ['slot' => 'ui.page', 'id' => 'read', 'entry' => 'ui/read.html', 'title' => 'Read']] : [];
    $man = ['schema' => 1, 'id' => $id, 'version' => '0.1.0', 'title' => "T $id", 'ext' => '>=1 <2', 'scope' => 'environment',
        'permissions' => $perm ? ['notify.push'] : [],
        'server' => ['entry' => 'server/ops.php', 'ops' => ['user' => [str_replace('-', '_', $id) . '_ping'], 'push' => [], 'host' => []]],
        'provides' => ['ui' => $ui, 'notify' => [['id' => 'copy', 'hooks' => ['push']]]],
        'defaults' => ['enabled' => false], 'dataPolicy' => ['onDisable' => 'keep', 'onRemove' => 'keep', 'migrations' => 'additive']];
    $r = co_module_install($db, ['manifest' => $man, 'files' => ['server/ops.php' => hash_file('sha256', "$dst/server/ops.php")], 'standard' => true]);
    return $r;
};

$ok('module with notify.push installs', ($install('notemail', true)['ok'] ?? false));
$ok('module without it installs', ($install('noteplain', false, false)['ok'] ?? false));
$n = ['title' => 'New letter', 'body' => "From: Ann\nAbout the trip"];
$ok('disabled module: refused', co_app_notify($db, 'notemail', 'owner', $n, 'page')['error'] === 'module_disabled');
co_module_set($db, 'notemail', true, 'owner');
co_module_set($db, 'noteplain', true, 'owner');
$ok('no permission: refused', co_app_notify($db, 'noteplain', 'owner', $n, 'page')['error'] === 'permission_not_declared');
$ok('unknown env: refused', co_app_notify($db, 'notemail', 'nobody', $n, 'page')['error'] === 'bad_env');
$ok('all envs: refused', co_app_notify($db, 'notemail', '*', $n, 'page')['error'] === 'bad_env');
$seen();

$r = co_app_notify($db, 'notemail', 'sam', $n, 'page');
$got = $seen();
$ok('sent (no devices subscribed here)', $r['ok'] === true && $r['sent'] === 0);
$ok('copy to the other module, not to the sender', array_column($got, 0) === ['noteplain'] && $r['modules'] === ['noteplain/copy']);
$ok('only the person of the env', ($got[0][1] ?? null) === ['sam']);
$ok('title carries the app', ($got[0][2] ?? '') === '✉ Mail · New letter');
$ok('click opens the app in the shell', ($got[0][3] ?? '') === '/e/sam/m/notemail/main');

co_app_notify($db, 'notemail', 'sam', $n + ['open' => ['page' => 'read', 'params' => ['id' => 'a b/1']]], 'page');
$ok('params: the bare page with its query', ($seen()[0][3] ?? '') === '/x/notemail/read?id=a%20b%2F1');
$ok('open.page must be a page of the module', $throws(fn() => co_app_notify($db, 'notemail', 'sam', $n + ['open' => ['page' => 'nope']], 'page'), 'open.page'));
$ok('open.params must be strings', $throws(fn() => co_app_notify($db, 'notemail', 'sam', $n + ['open' => ['params' => ['a' => 1]]], 'page'), 'open.params'));
$ok('too long an address: refused', $throws(fn() => co_app_notify($db, 'notemail', 'sam', $n + ['open' => ['params' => ['a' => str_repeat('я', 300), 'b' => str_repeat('я', 300)]]], 'page'), '1000'));
$ok('title required', $throws(fn() => co_app_notify($db, 'notemail', 'sam', ['title' => '  '], 'page'), 'title'));
$ok('bad tag', $throws(fn() => co_app_notify($db, 'notemail', 'sam', $n + ['tag' => '../x'], 'page'), 'tag'));
$ok('long title cut', ($r = co_app_notify($db, 'notemail', 'alex', ['title' => str_repeat('я', 200)], 'page'))['ok']
    && mb_strlen($seen()[0][2] ?? '') === mb_strlen('✉ Mail · ') + 80);

// tag: the same text again within 10 min is not sent; another text with the same tag is
$t = $n + ['tag' => 'inbox'];
$ok('tagged: sent', co_app_notify($db, 'notemail', 'alex', $t, 'page')['sent'] === 0 && count($seen()) === 1);
$d = co_app_notify($db, 'notemail', 'alex', $t, 'page');
$ok('same tag, same text: not sent again', !empty($d['duplicate']) && $seen() === []);
$ok('same tag, new text: sent', co_app_notify($db, 'notemail', 'alex', ['title' => 'Two letters', 'tag' => 'inbox'], 'page')['ok'] && count($seen()) === 1);

// the hourly limit per module and env (alex has used 3 of 10)
for ($i = 0; $i < 7; $i++) co_app_notify($db, 'notemail', 'alex', ['title' => "n$i"], 'page');
$seen();
$lim = co_app_notify($db, 'notemail', 'alex', ['title' => 'eleventh'], 'page');
$ok('11th in an hour: rate_limited with retryAfter', $lim['error'] === 'rate_limited' && $lim['retryAfter'] > 3000 && $lim['retryAfter'] <= 3600 && $seen() === []);
$ok('the limit is per env', co_app_notify($db, 'notemail', 'owner', ['title' => 'x'], 'page')['ok'] === true);
$seen();

// the person's switch
$ok('modules_list: switch shown, on', (array_column(co_modules_list($db, 'sam', false)['modules'], null, 'id')['notemail']['notify'] ?? null) === ['on' => true]);
$ok('modules_list: no switch without the permission', !isset(array_column(co_modules_list($db, 'sam', false)['modules'], null, 'id')['noteplain']['notify']));
$ok('switch off', co_app_notify_set($db, 'sam', 'notemail', false) === ['ok' => true, 'on' => false]);
$ok('off: refused', co_app_notify($db, 'notemail', 'sam', ['title' => 'x'], 'page')['error'] === 'notify_off' && $seen() === []);
$ok('off is per person', co_app_notify($db, 'notemail', 'owner', ['title' => 'y'], 'page')['ok'] === true);
$seen();
$ok('modules_list: off', array_column(co_modules_list($db, 'sam', false)['modules'], null, 'id')['notemail']['notify'] === ['on' => false]);
co_app_notify_set($db, 'sam', 'notemail', true);
$ok('on again', co_app_notify($db, 'notemail', 'sam', ['title' => 'z'], 'page')['ok'] === true);
$seen();
$ok('switch of a module without the permission', co_app_notify_set($db, 'sam', 'noteplain', false)['error'] === 'not_found');

// $ctx['notify'] from server code: a user op notifies only its caller; a push/host op names the env
$ok('user op: its caller', co_module_call($db, 'user', 'notemail_ping', ['title' => 'from op'], 'sam')['ok'] === true
    && ($seen()[0][1] ?? null) === ['sam']);
$ok('user op: not another env', co_module_call($db, 'user', 'notemail_ping', ['title' => 'x', 'env' => 'owner'], 'sam')['error'] === 'not_your_env');
$ctx = co_module_ctx($db, co_modules($db)['notemail']);
$ok('push/host op: env required', $ctx['notify'](['title' => 'x'])['error'] === 'env_required');
$ok('push/host op: names the env', $ctx['notify'](['title' => 'svc', 'env' => 'owner'])['ok'] === true && ($seen()[0][1] ?? null) === ['owner']);
$ok('bad fields come back as bad_request', $ctx['notify'](['title' => '', 'env' => 'owner'])['error'] === 'bad_request');

// the journal
$st = $db->prepare("SELECT COUNT(*) FROM module_log WHERE module_id = 'notemail' AND action = 'notify'");
$st->execute();
$rows = (int)$st->get_result()->fetch_row()[0];
$st->close();
$ok('every sent push is a notify row of the journal', $rows === 17);
$ok('person off: refused', (function () use ($db): bool {
    $st = $db->prepare("UPDATE modules SET manifest_json = JSON_SET(manifest_json, '$.personal', JSON_OBJECT('about', 'members')) WHERE id = 'notemail'");
    $st->execute();
    $st->close();
    co_modules($db, true);
    co_module_person_set($db, 'notemail', 'alex', 'off', 'alex');
    return co_app_notify($db, 'notemail', 'alex', ['title' => 'x'], 'page')['error'] === 'person_off';
})());

foreach (['notemail', 'noteplain'] as $id) {
    co_module_set($db, $id, false, 'owner');
    co_module_rmtree(co_module_home($id));
}
@unlink($log);
echo $fail ? "$fail FAILED\n" : "notify test OK\n";
exit($fail ? 1 : 0);
