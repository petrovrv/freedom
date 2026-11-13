<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F12 (schema 28, executors-lib.php, SPEC §3.1a): many executors against a throwaway MariaDB (never production) —
// registration and heartbeat, id ownership, serves, the order of the nearest live one, handover between any of them
// without a double run, the list CO Web shows, built-in models of a family member's executor.
// Driver: python tests/actions-test.py --test executors-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/finalize-lib.php';
require __DIR__ . '/../web/agent/models-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); } catch (InvalidArgumentException) { return true; }
    return false;
};
$now = gmdate('Y-m-d\TH:i:s\Z');
$poll = static function (string $host, int $agoSec) use ($db): void {
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute([co_queue_poll_key($host), gmdate('Y-m-d H:i:s', time() - $agoSec)]);
};
$age = static function (string $host, int $sec) use ($db): void {
    $st = $db->prepare('UPDATE executors SET seen_utc = UTC_TIMESTAMP(3) - INTERVAL ? SECOND WHERE host_id = ?');
    $st->execute([$sec, $host]);
};
$owner = static function (string $host, array $o) use ($db): void {
    $data = json_encode($o);
    $st = $db->prepare('REPLACE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
    $st->execute(['host:' . $host, 'owner', $data, hash('sha256', $data)]);
};
$beat = static fn(string $host, array $extra = []): array => co_exec_put($db, $extra + [
    'host' => $host, 'title' => strtoupper($host), 'os' => 'linux', 'machine' => 'box-' . $host, 'version' => '1.19.0',
    'state' => 'running', 'labels' => ['agents' => ['claude', 'codex'], 'capabilities' => ['db-queue'], 'devices' => 0],
    'owner' => ['hostId' => $host, 'updatedUtc' => gmdate('Y-m-d\TH:i:s\Z'), 'agents' => ['claude' => ['displayName' => 'Claude'], 'codex' => []]],
]);
$n = 0;
$job = static function (string $host, array $req = [], int $ageSec = 90, string $env = 'owner') use ($db, &$n): string {
    $id = 'JOB-F12-' . (++$n);
    $req += ['schemaVersion' => 3, 'hostId' => $host, 'jobId' => $id, 'environmentId' => $env, 'projectId' => 'P', 'chatId' => 'CHAT-1',
        'agent' => 'claude', 'instruction' => 'hello', 'conversation' => ['operation' => 'new', 'sessionId' => null], 'attachments' => [],
        'hostAuto' => true];
    $st = $db->prepare("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, state, origin, queued_utc, request, sha, synced_utc)
                        VALUES (?, ?, ?, 'P', 'CHAT-1', ?, 'inbox', 'db', UTC_TIMESTAMP(3) - INTERVAL ? SECOND, ?, 'x', UTC_TIMESTAMP(3))");
    $st->execute([$id, $host, $env, $req['agent'], $ageSec, json_encode($req)]);
    return $id;
};
$host = static function (string $id) use ($db): string {
    $st = $db->prepare('SELECT host_id FROM jobs WHERE job_id = ?');
    $st->execute([$id]);
    return (string)$st->get_result()->fetch_row()[0];
};
$reqOf = static function (string $id) use ($db): array {
    $st = $db->prepare('SELECT request FROM jobs WHERE job_id = ?');
    $st->execute([$id]);
    return json_decode((string)$st->get_result()->fetch_row()[0], true);
};

// --- before any registration: the two compatible ids only
$ok('home-main and site known without a row', co_exec_known($db, 'home-main') && co_exec_known($db, 'site'));
$ok('unknown id is not', !co_exec_known($db, 'linux-1') && !co_exec_known($db, 'bad id') && !co_exec_known($db, str_repeat('a', 33)));
$ok('compat ids serve everybody', co_exec_serves($db, 'home-main', 'sam') && co_exec_serves($db, 'site', 'owner'));

// --- registration
$r = $beat('linux-1');
$ok('executor_put registers', $r['ok'] === true && $r['owner'] === true && co_exec_known($db, 'linux-1'));
$row = co_exec_row($db, 'linux-1');
$ok('row has title, os, labels', $row['title'] === 'LINUX-1' && $row['os'] === 'linux' && $row['kind'] === 'bridge'
    && $row['labels']['agents'] === ['claude', 'codex'] && (int)$row['fresh'] === 1);
$ok('owner.json copied to kv', (co_exec_owner($db, 'linux-1')['hostId'] ?? '') === 'linux-1');
$ok('a Bridge cannot be the site', $throws(fn() => co_exec_put($db, ['host' => 'site', 'kind' => 'bridge'])));
$ok('invalid id refused', $throws(fn() => co_exec_put($db, ['host' => '../x'])));
$ok('unknown ownerEnv refused', $throws(fn() => $beat('linux-2', ['ownerEnv' => 'nobody'])));
$ok('labels are cleaned', co_exec_put($db, ['host' => 'linux-3', 'machine' => 'm3', 'labels' => ['agents' => ['claude', '<x>', 'claude'], 'devices' => 'many', 'serves' => ['sam', 'ghost']]])['ok']
    && co_exec_row($db, 'linux-3')['labels'] === ['agents' => ['claude'], 'capabilities' => [], 'sandboxes' => [], 'devices' => 0, 'serves' => ['sam']]);
$db->query("DELETE FROM executors WHERE host_id = 'linux-3'");
co_exec_put($db, ['host' => 'home-main', 'machine' => 'PC1']);
$ok('untitled home-main is Home, others the machine', co_exec_row($db, 'home-main')['title'] === 'Home'
    && co_exec_put($db, ['host' => 'linux-5', 'machine' => 'srv5'])['ok'] && co_exec_row($db, 'linux-5')['title'] === 'srv5');
$db->query("DELETE FROM executors WHERE host_id IN ('home-main', 'linux-5')");

// id ownership: another machine with the same id is refused while the first one beats
$r = $beat('linux-1', ['machine' => 'intruder']);
$ok('another machine gets host_taken', $r['ok'] === false && $r['error'] === 'host_taken' && co_exec_row($db, 'linux-1')['machine'] === 'box-linux-1');
$ok('job_claim of the other machine is foreign', co_exec_foreign($db, co_exec_row($db, 'linux-1'), 'intruder')
    && !co_exec_foreign($db, co_exec_row($db, 'linux-1'), 'BOX-LINUX-1') && !co_exec_foreign($db, co_exec_row($db, 'linux-1'), null));
$age('linux-1', 700);
$ok('after 10 min of silence the id may move', $beat('linux-1', ['machine' => 'new-box'])['ok'] && co_exec_row($db, 'linux-1')['machine'] === 'new-box');
$age('linux-1', 700);
$beat('linux-1'); // and back to the first machine for the rest

// --- alive
$poll('linux-1', 3);
$ok('beating and polling = alive', co_exec_alive($db, 'linux-1') !== null);
$age('linux-1', 120);
$ok('silent 2 min = not alive (owner.json copy does not count)', co_exec_alive($db, 'linux-1') === null);
$beat('linux-1', ['state' => 'paused']);
$ok('paused = not alive', co_exec_alive($db, 'linux-1') === null);
$beat('linux-1');
$db->query("UPDATE executors SET enabled = 0 WHERE host_id = 'linux-1'");
$ok('switched off = not alive, not known', co_exec_alive($db, 'linux-1') === null && !co_exec_known($db, 'linux-1'));
$ok('executor_set is for the owner only', co_exec_set($db, ['host' => 'linux-1', 'enabled' => true], false)['ok'] === false);
co_exec_set($db, ['host' => 'linux-1', 'enabled' => true], true);
$ok('switched on again', co_exec_alive($db, 'linux-1') !== null);
$ok('home-main cannot be switched here', $throws(fn() => co_exec_set($db, ['host' => 'home-main', 'enabled' => false], true)));

// --- a family member's executor
$beat('sam-mac', ['ownerEnv' => 'sam', 'os' => 'macos', 'title' => 'Sam Mac']);
$poll('sam-mac', 2);
$ok('member executor serves only its env', co_exec_serves($db, 'sam-mac', 'sam') && !co_exec_serves($db, 'sam-mac', 'owner')
    && co_exec_serves($db, 'linux-1', 'sam'));
$ok('order for the owner: home, site, others', co_exec_order($db, 'owner') === ['home-main', 'site', 'linux-1']);
$ok('order for sam: own machine first', co_exec_order($db, 'sam') === ['sam-mac', 'home-main', 'site', 'linux-1']);

// --- handover among four executors: Home draining for an update, the site silent, linux-1 and sam-mac alive
$owner('home-main', ['hostId' => 'home-main', 'updatedUtc' => $now, 'state' => 'updating', 'agents' => ['claude' => [], 'codex' => []]]);
$poll('home-main', 5);
$owner('site', ['hostId' => 'site', 'updatedUtc' => $now, 'agents' => ['claude' => []]]);
$poll('site', 300);
$beat('linux-1');
$poll('linux-1', 3);
$a = $job('home-main');
$codex = $job('home-main', ['agent' => 'codex']);
$samJob = $job('home-main', [], 90, 'sam');
$young = $job('home-main', [], 20);
$manual = $job('home-main', ['hostAuto' => false]);
$dev = $job('home-main', ['devices' => ['d-1111aaaa2222bbbb']]);   // a device nobody else serves: stays with home-main
$out = co_queue_handover();
$ok('owner job -> linux-1 (site silent)', $host($a) === 'linux-1' && $reqOf($a)['hostId'] === 'linux-1'
    && str_contains($reqOf($a)['instruction'], 'queued for Home') && str_contains($reqOf($a)['instruction'], 'executor linux-1'));
$ok('codex job -> linux-1 too', $host($codex) === 'linux-1');
$ok('sam job -> sam-mac (own machine first)', $host($samJob) === 'sam-mac');
$ok('young, manual and device jobs stay', $host($young) === 'home-main' && $host($manual) === 'home-main' && $host($dev) === 'home-main');
$ok('three moves reported', count($out) === 3);
$ok('no second move', co_queue_handover() === []);

// linux-1 goes silent with its queue; the site comes back: the moved job is not moved again, a new one goes to the site
$age('linux-1', 200);
$poll('site', 2);
$l = $job('linux-1');
co_queue_handover();
$ok('job of a silent linux-1 -> site', $host($l) === 'site');
$ok('a moved job never moves twice', $host($a) === 'linux-1');

// devices: a Claude job with a device linux-1 serves goes to linux-1, never to another Bridge
$beat('linux-1');
$poll('linux-1', 2);
$beat('linux-4');
$poll('linux-4', 2);
$st = $db->prepare("INSERT INTO relay_devices (host_id, device_id, environment_id, short_name, name, kind, online, tools_json, updated_utc)
                    VALUES ('linux-1', 'd-3333cccc4444dddd', 'owner', 'cam', 'Cam', 'desktop', 1, '[]', UTC_TIMESTAMP(3))");
$st->execute();
$ok('device of linux-1 blocks linux-4', co_handover_blocked(['hostAuto' => true, 'agent' => 'codex', 'devices' => ['d-3333cccc4444dddd']], 'linux-4', $db) === 'devices'
    && co_handover_blocked(['hostAuto' => true, 'agent' => 'codex', 'devices' => ['d-3333cccc4444dddd']], 'linux-1', $db) === null);
$poll('site', 300);
$d2 = $job('linux-4', ['agent' => 'codex', 'devices' => ['d-3333cccc4444dddd']]);
co_queue_handover();
$ok('codex job with a linux-1 device -> linux-1', $host($d2) === 'linux-1');

// a member's job never goes to the owner's... it may: the owner's executors serve all; the owner's job never to sam-mac
$homeOwnerJob = $job('linux-4', [], 90, 'owner');
$age('linux-1', 200);
$db->query("UPDATE executors SET enabled = 0 WHERE host_id = 'linux-4'");
co_queue_handover();
$ok("owner job never lands on sam's machine", $host($homeOwnerJob) !== 'sam-mac');
$db->query("UPDATE executors SET enabled = 1 WHERE host_id = 'linux-4'");

// --- no double run: a job claimed meanwhile is not moved (the UPDATE is guarded by state)
$beat('linux-1');
$poll('linux-1', 2);
$c = $job('home-main');
$db->query("UPDATE jobs SET state = 'claimed', claimed_utc = UTC_TIMESTAMP(3) WHERE job_id = '$c'");
co_queue_handover();
$ok('claimed job stays', $host($c) === 'home-main');

// --- fallback leaves the jobs of executors without a Drive inbox out (no Drive call for them)
$db->query("UPDATE jobs SET state = 'completed' WHERE state = 'inbox'");
$age('linux-4', 200);
$job('linux-4', [], 200);
$ok('no Drive copy for a Linux executor', co_queue_fallback() === []);

// --- the list CO Web shows
$owner('home-main', ['hostId' => 'home-main', 'updatedUtc' => $now, 'machineName' => 'PC', 'agents' => ['claude' => []],
    'projects' => ['P1' => ['environmentId' => 'owner', 'workspace' => 'C:\\x'], 'P2' => ['environmentId' => 'sam', 'workspace' => 'C:\\y']],
    'devices' => [['deviceId' => 'd-1', 'environmentId' => 'owner'], ['deviceId' => 'd-2', 'environmentId' => 'sam']]]);
$all = co_exec_list($db, 'owner', true)['executors'];
$ids = array_column($all, 'id');
$ok('owner sees all executors in order', $ids === ['home-main', 'site', 'linux-1', 'linux-4', 'sam-mac']);
$byId = array_column($all, null, 'id');
$ok('owner sees machine and the whole owner.json', $byId['home-main']['machine'] === 'PC' && isset($byId['home-main']['owner']['projects']['P1']));
$sam = co_exec_list($db, 'sam', false)['executors'];
$samIds = array_column($sam, 'id');
$ok('sam sees the executors that serve sam, own first', $samIds[0] === 'sam-mac' && in_array('home-main', $samIds, true));
$h = array_column($sam, null, 'id')['home-main'];
$ok('sam sees no paths, other projects or devices', !isset($h['machine']) && !isset($h['owner']['machineName'])
    && array_keys($h['owner']['projects']) === ['P2'] && !isset($h['owner']['projects']['P2']['workspace'])
    && array_column($h['owner']['devices'], 'deviceId') === ['d-2']);
$beat('owner-only', ['labels' => ['agents' => ['claude'], 'serves' => ['owner']]]);
$ok('an executor that serves only the owner is hidden from sam', !in_array('owner-only', array_column(co_exec_list($db, 'sam', false)['executors'], 'id'), true)
    && !co_exec_serves($db, 'owner-only', 'sam'));

// --- built-in models of the member's executor belong to the member
$m = models_all($db);
$ok("sam-mac/claude is sam's", ($m['sam-mac/claude']['env'] ?? '') === 'sam' && ($m['linux-1/claude']['env'] ?? '') === 'owner'
    && str_ends_with($m['sam-mac/claude']['title'], 'Sam Mac'));

// --- review fixes: members see no machine names or serves lists; Forget leaves no ghost and no resources
$co = co_exec_list($db, 'sam', false)['executors'];
$l1 = array_column($co, null, 'id')['linux-1'] ?? [];
$ok('member: no machine name as title, no serves list', ($l1['title'] ?? '') === 'LINUX-1' && $l1['serves'] === null);
co_exec_put($db, ['host' => 'linux-6', 'machine' => 'secret-box', 'labels' => ['agents' => ['claude']]]);
$ok("member sees the id, not another person's machine name", array_column(co_exec_list($db, 'sam', false)['executors'], null, 'id')['linux-6']['title'] === 'linux-6'
    && array_column(co_exec_list($db, 'owner', true)['executors'], null, 'id')['linux-6']['title'] === 'secret-box');
$owner('sam-mac', ['hostId' => 'sam-mac', 'updatedUtc' => $now, 'agents' => ['claude' => []]]);
co_exec_set($db, ['host' => 'sam-mac', 'forget' => true], true);
$ok('forgotten executor: not listed, no owner.json copy, no resources', !in_array('sam-mac', array_column(co_exec_list($db, 'sam', false)['executors'], 'id'), true)
    && co_exec_owner($db, 'sam-mac') === null && !isset(models_all($db)['sam-mac/claude']) && !co_exec_known($db, 'sam-mac'));

echo $fail ? "FAILED $fail\n" : "ALL OK\n";
exit($fail ? 1 : 0);
