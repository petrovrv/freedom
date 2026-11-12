<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// subs-lib.php (S4) against a throwaway MariaDB (never production): device registration of CLI subscriptions as model
// resources, the Bridge computer's built-in entries, sign-out, names of other environments.
// Driver: python tests/actions-test.py --test subs-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/subs-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (InvalidArgumentException) { return true; }
};
$q = static function (string $sql, array $args = []) use ($db): void {
    $st = $db->prepare($sql);
    $st->execute($args);
    $st->close();
};

foreach (['kv', 'model_resources'] as $t) $db->query("DELETE FROM $t");
$home = ['hostId' => 'home-main', 'updatedUtc' => gmdate('Y-m-d\TH:i:s\Z'), 'agents' => ['claude' => ['displayName' => 'Claude'], 'codex' => ['displayName' => 'GPT']]];
$q("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES ('host:home-main', 'owner', ?, 'x', UTC_TIMESTAMP(3))", [json_encode($home)]);

$dev = ['id' => 'd-abcd0123', 'short' => 'mac1', 'name' => 'Mac of Sam'];
$r = subs_put($db, ['device' => $dev, 'subs' => [
    ['agent' => 'claude', 'installed' => true, 'loggedIn' => true, 'account' => 'sam@example.org', 'plan' => 'pro', 'version' => '2.1.3'],
    ['agent' => 'codex', 'installed' => false, 'loggedIn' => null],
    ['agent' => 'agy', 'installed' => true, 'loggedIn' => null],
    ['agent' => 'bash', 'installed' => true, 'loggedIn' => true],
]], 'sam');
$ok('registered claude only', $r['registered'] === ['sam/mac1-abcd-claude'] && $r['removed'] === []);
$all = models_all($db);
$c = $all['sam/mac1-abcd-claude'] ?? null;
$ok('resource of sam, from the device', $c !== null && $c['env'] === 'sam' && $c['source'] === 'device' && $c['kind'] === 'subscription' && $c['host'] === '');
$ok('title and card notes', $c['title'] === 'Claude Code · Mac of Sam' && str_contains($c['card']['notes'], 'sam@example.org (pro), CLI 2.1.3'));
$ok('card says no jobs there yet', str_contains($c['card']['limits'], 'no Bridge there'));
$ok('not callable (no host)', !array_column(models_list($db, 'sam')['resources'], null, 'id')['sam/mac1-abcd-claude']['callable']);
$ok('hidden from others until allowed', !isset(array_column(models_list($db, 'owner')['resources'], null, 'id')['sam/mac1-abcd-claude']));

// the owner of the resource allows it as a slave in CO Web; the next report keeps the flag and the limits text once
models_put($db, ['id' => 'sam/mac1-abcd-claude', 'slaveAllowed' => true], 'user', 'sam', false);
subs_put($db, ['device' => $dev, 'subs' => [['agent' => 'claude', 'installed' => true, 'loggedIn' => true, 'account' => 'sam@example.org']]], 'sam');
$c = models_all($db)['sam/mac1-abcd-claude'];
$ok('flag kept on re-report', $c['slaveAllowed'] === true);
$ok('limits not repeated', substr_count($c['card']['limits'], 'no Bridge there') === 1);

// another environment reporting the same device name gets its own id; it can never touch sam's
subs_put($db, ['device' => $dev, 'subs' => [['agent' => 'claude', 'installed' => true, 'loggedIn' => false]]], 'kim');
$ok('kim sign-out does not remove sam', isset(models_all($db)['sam/mac1-abcd-claude']));

// sign-out removes the device's resource
$r = subs_put($db, ['device' => $dev, 'subs' => [['agent' => 'claude', 'installed' => true, 'loggedIn' => false]]], 'sam');
$ok('signed out: removed', $r['removed'] === ['sam/mac1-abcd-claude'] && !isset(models_all($db)['sam/mac1-abcd-claude']));

// the Bridge computer of the owner: the built-in entry gets the account in its card, no new resource
$r = subs_put($db, ['device' => ['id' => 'd-11112222', 'short' => 'shuttle', 'name' => 'Home PC', 'hostId' => 'home-main'],
    'subs' => [['agent' => 'codex', 'installed' => true, 'loggedIn' => true, 'account' => 'o@example.org', 'plan' => 'plus']]], 'owner');
$all = models_all($db);
$ok('built-in updated', $r['registered'] === ['home-main/codex'] && str_contains($all['home-main/codex']['card']['notes'], 'o@example.org (plus)'));
$ok('built-in keeps its default card', str_contains($all['home-main/codex']['card']['strengths'], 'Excel'));
$ok('no device entry for the host', !isset($all['owner/shuttle-1111-codex']));
// a member naming the owner's host gets an own entry, the built-in stays the owner's
$r = subs_put($db, ['device' => ['id' => 'd-22223333', 'short' => 'pc2', 'name' => 'PC2', 'hostId' => 'home-main'],
    'subs' => [['agent' => 'codex', 'installed' => true, 'loggedIn' => true]]], 'sam');
$ok('member cannot write the host entry', $r['registered'] === ['sam/pc2-2222-codex']);

$ok('bad device id rejected', $throws(fn() => subs_put($db, ['device' => ['id' => 'x', 'short' => 'pc'], 'subs' => []], 'sam')));
// the site is never a device's host
$r = subs_put($db, ['device' => ['id' => 'd-55556666', 'short' => 'pc5', 'hostId' => 'site'], 'subs' => [['agent' => 'claude', 'loggedIn' => true]]], 'owner');
$ok('site host ignored', $r['registered'] === ['owner/pc5-5555-claude']);
// same short name, another computer: own entry
$r = subs_put($db, ['device' => ['id' => 'd-ffff0000', 'short' => 'mac1'], 'subs' => [['agent' => 'codex', 'loggedIn' => true]]], 'sam');
$ok('same short, other device', $r['registered'] === ['sam/mac1-ffff-codex']);
// a title the user edited survives the next report
models_put($db, ['id' => 'sam/mac1-ffff-codex', 'title' => 'My codex'], 'user', 'sam', false);
subs_put($db, ['device' => ['id' => 'd-ffff0000', 'short' => 'mac1'], 'subs' => [['agent' => 'codex', 'loggedIn' => true]]], 'sam');
$ok('edited title kept', models_all($db)['sam/mac1-ffff-codex']['title'] === 'My codex');
$ok('bad short rejected', $throws(fn() => subs_put($db, ['device' => ['id' => 'd-12341234', 'short' => 'Bad Name'], 'subs' => []], 'sam')));
$ok('bad host rejected', $throws(fn() => subs_put($db, ['device' => ['id' => 'd-12341234', 'short' => 'pc', 'hostId' => '../x'], 'subs' => []], 'sam')));
$r = subs_put($db, ['device' => ['id' => 'd-33334444', 'short' => 'pc3', 'name' => "<b>x</b>"], 'subs' => [['agent' => 'claude', 'loggedIn' => true, 'account' => 'not an email']]], 'sam');
$c = models_all($db)['sam/pc3-3333-claude'];
$ok('markup and odd account dropped', $c['title'] === 'Claude Code · pc3' && !str_contains($c['card']['notes'], 'not an email'));

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
