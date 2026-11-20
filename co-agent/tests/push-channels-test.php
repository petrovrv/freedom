<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Delivery channels of a push (push-lib.php co_push_channels, co_push_notify with channels) against a throwaway MariaDB
// (never production): the core's devices channel, a module's notify hook with and without its status hook, a push that
// goes only to the channels it names. Driver: python tests/actions-test.py --test push-channels-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/push-lib.php';
require __DIR__ . '/../web/agent/modules-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};

// a standard module with two notify hooks: "say" answers status, "log" does not
$id = 'notifytest';
$dst = co_module_home($id);
co_module_rmtree($dst);
@mkdir("$dst/server", 0700, true);
$log = sys_get_temp_dir() . '/notifytest-' . getmypid() . '.json';
@unlink($log);
file_put_contents("$dst/server/ops.php", '<?php
$log = ' . var_export($log, true) . ';
$put = static function (string $h, array $in) use ($log): void {
    $all = is_file($log) ? json_decode((string)file_get_contents($log), true) : [];
    $all[] = [$h, $in["envs"] ?? [], $in["title"] ?? ""];
    file_put_contents($log, json_encode($all));
};
return ["user" => [], "push" => [], "host" => [], "notify" => [
    "say" => ["push" => static fn(array $c, array $in) => $put("say", $in),
              "status" => static fn(array $c, array $in): array => ["ready" => ($in["env"] ?? "") === "owner", "note" => "n"]],
    "log" => ["push" => static fn(array $c, array $in) => $put("log", $in)],
]];
');
$man = ['schema' => 1, 'id' => $id, 'version' => '0.1.0', 'title' => 'Notify test', 'ext' => '>=1 <2', 'scope' => 'environment',
    'permissions' => [], 'server' => ['entry' => 'server/ops.php', 'ops' => ['user' => [], 'push' => [], 'host' => []]],
    'provides' => ['notify' => [['id' => 'say', 'hooks' => ['push', 'status']], ['id' => 'log', 'hooks' => ['push']]]],
    'defaults' => ['enabled' => false], 'dataPolicy' => ['onDisable' => 'keep', 'onRemove' => 'keep', 'migrations' => 'additive']];
$r = co_module_install($db, ['manifest' => $man, 'files' => ['server/ops.php' => hash_file('sha256', "$dst/server/ops.php")], 'standard' => true]);
$ok('test module installs', $r['ok'] ?? false);

$ch = co_push_channels('owner');
$ok('off module: only the core channel', array_column($ch, 'id') === ['devices']);
$ok('no devices subscribed: not ready', $ch[0]['ready'] === false && $ch[0]['devices'] === 0);

co_module_set($db, $id, true, 'owner');
$ch = array_column(co_push_channels('owner'), null, 'id');
$ok('module channels listed', array_keys($ch) === ['devices', "$id/say", "$id/log"]);
$ok('status hook: ready + note', $ch["$id/say"]['ready'] === true && $ch["$id/say"]['note'] === 'n' && $ch["$id/say"]['title'] === 'Notify test');
$ok('no status hook: ready unknown', $ch["$id/log"]['ready'] === null);
$ok('status per env', array_column(co_push_channels('sam'), null, 'id')["$id/say"]['ready'] === false);

$seen = static function () use ($log): array {
    $all = is_file($log) ? json_decode((string)file_get_contents($log), true) : [];
    @unlink($log);
    return $all;
};
co_push_notify('owner', 'T1', 'b', '/', 't');
$ok('no channels: every hook', array_column($seen(), 0) === ['say', 'log']);
co_push_notify('owner', 'T2', 'b', '/', 't', null, ["$id/log"]);
$got = $seen();
$ok('named channel only', array_column($got, 0) === ['log'] && $got[0][1] === ['owner'] && $got[0][2] === 'T2');
co_push_notify('owner', 'T3', 'b', '/', 't', null, ['devices']);
$ok('devices only: no hook', $seen() === []);
co_push_notify('owner', 'T4', 'b', '/', 't', null, ['../x', 42, "$id/say"]);
$ok('bad ids dropped', array_column($seen(), 0) === ['say']);
$ok('channel ids follow the manifest rules', co_push_channel_ok(str_repeat('a', 63) . '/say') && !co_push_channel_ok(str_repeat('a', 64) . '/say')
    && !co_push_channel_ok('a/b/c') && !co_push_channel_ok('a/1x'));
co_push_notify('owner', 'T5', 'b', '/', 't', null, []);
$ok('empty list: nowhere', $seen() === []);
$args = null;
co_push_notify('owner', 'T6', 'b', '/', 't', static function (...$a) use (&$args): array { $args = $a; return ['sent' => 0]; });
$ok('no channels: senders get 3 args (old $send keep working)', count($args) === 3);

co_module_set($db, $id, false, 'owner');
co_module_rmtree($dst);
echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
