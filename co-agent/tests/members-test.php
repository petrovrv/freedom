<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// members-lib.php (schema 21, Freedom F1) against a throwaway MariaDB (never production): who is a member, who is the
// owner, who may start the server agent, and the cached list when the database is down.
// Driver: python tests/actions-test.py --test members-test.php (the driver seeds owner, sam, alex)
declare(strict_types=1);
require __DIR__ . '/../web/agent/members-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (InvalidArgumentException) { return true; }
};

$ok('seeded members in sort order', co_member_envs() === ['owner', 'sam', 'alex']);
$ok('email -> env', co_push_users() === ['owner@example.org' => 'owner', 'sam@example.org' => 'sam', 'alex@example.org' => 'alex']);
$ok('owner env and email', co_owner_env() === 'owner' && co_owner_email() === 'owner@example.org');
$ok('owner check', co_is_owner_env('owner') && !co_is_owner_env('sam') && !co_is_owner_env('') && !co_is_owner_env(null));
$ok('names', co_member_names() === ['owner' => 'Олег', 'sam' => 'Сэм', 'alex' => 'Алекс']);
$ok('site agent: owner and flagged members', co_site_agent_emails() === ['owner@example.org', 'sam@example.org']);

$r = co_member_put($db, ['env' => 'kim', 'email' => ' Kim@Example.org ', 'name' => 'Ким', 'color' => '#a0c', 'sort' => 3]);
$ok('add lower-cases the email', $r['email'] === 'kim@example.org' && $r['role'] === 'member' && $r['siteAgent'] === false);
$ok('bad env refused', $throws(fn() => co_member_put($db, ['env' => 'Bad Env', 'email' => 'x@example.org', 'name' => 'X'])));
$ok('bad email refused', $throws(fn() => co_member_put($db, ['env' => 'x', 'email' => 'nope', 'name' => 'X'])));
$ok('bad role refused', $throws(fn() => co_member_put($db, ['env' => 'x', 'email' => 'x@example.org', 'name' => 'X', 'role' => 'admin'])));
$ok('bad color refused', $throws(fn() => co_member_put($db, ['env' => 'x', 'email' => 'x@example.org', 'name' => 'X', 'color' => 'red;}'])));

co_member_put($db, ['env' => 'kim', 'email' => 'kim@example.org', 'name' => 'Ким', 'role' => 'owner', 'sort' => 3]);
$ok('new owner demotes the old one', co_owner_env() === 'kim' && co_member('owner')['role'] === 'member'
    && count(array_filter(co_members(), static fn($m) => $m['role'] === 'owner')) === 1);
$ok('owner cannot be deleted', $throws(fn() => co_member_delete($db, 'kim')));
$ok('owner cannot be demoted directly', $throws(fn() => co_member_put($db, ['env' => 'kim', 'email' => 'kim@example.org', 'name' => 'Ким', 'role' => 'member'])));
$ok('change without role keeps the owner', co_member_put($db, ['env' => 'kim', 'email' => 'kim@example.org', 'name' => 'Ким', 'color' => '#c00'])['role'] === 'owner');
co_member_put($db, ['env' => 'owner', 'email' => 'owner@example.org', 'name' => 'Олег', 'role' => 'owner', 'siteAgent' => true]);
$ok('delete a member', co_member_delete($db, 'kim') && co_member('kim') === null && co_owner_env() === 'owner');

$cache = co_agent_home() . '/co-agent/state/members.json';
$ok('last good list cached', is_file($cache) && count(json_decode((string)file_get_contents($cache), true)) === 3);
$db->query("RENAME TABLE members TO members_off");
$ok('database error -> cached list', co_members(true) !== [] && co_owner_env() === 'owner');
$db->query("RENAME TABLE members_off TO members");

echo $fail ? "$fail FAILED\n" : "all ok\n";
exit($fail ? 1 : 0);
