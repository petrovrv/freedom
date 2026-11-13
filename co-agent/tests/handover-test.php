<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// S8: co_queue_handover (finalize-lib.php) against a throwaway MariaDB (never production): an 'auto' database job its
// executor has not taken within 60 s goes once to the other live executor that fits it; nothing else moves.
// Driver: python tests/actions-test.py --test handover-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/finalize-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};

$owner = static function (string $host, array $o) use ($db): void {
    $data = json_encode($o);
    $st = $db->prepare('REPLACE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
    $st->execute(['host:' . $host, 'owner', $data, hash('sha256', $data)]);
};
$poll = static function (string $host, int $agoSec) use ($db): void {
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute([co_queue_poll_key($host), gmdate('Y-m-d H:i:s', time() - $agoSec)]);
};
$n = 0;
$job = static function (string $host, array $req = [], int $ageSec = 90, array $cols = []) use ($db, &$n): string {
    $id = 'JOB-S8-' . (++$n);
    $req += ['schemaVersion' => 3, 'hostId' => $host, 'jobId' => $id, 'environmentId' => 'owner', 'projectId' => 'P', 'chatId' => 'CHAT-1',
        'agent' => 'claude', 'instruction' => 'hello', 'conversation' => ['operation' => 'new', 'sessionId' => null], 'attachments' => [],
        'hostAuto' => true];
    $cols += ['state' => 'inbox', 'cancel_requested' => 0, 'fallback' => null];
    $st = $db->prepare("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, state, origin, queued_utc, request, sha, synced_utc,
                                          cancel_requested, fallback_utc)
                        VALUES (?, ?, 'owner', 'P', 'CHAT-1', ?, ?, 'db', UTC_TIMESTAMP(3) - INTERVAL ? SECOND, ?, 'x', UTC_TIMESTAMP(3), ?, ?)");
    $st->execute([$id, $host, $req['agent'], $cols['state'], $ageSec, json_encode($req), $cols['cancel_requested'], $cols['fallback']]);
    return $id;
};
$row = static function (string $id) use ($db): array {
    $st = $db->prepare('SELECT host_id, state, request FROM jobs WHERE job_id = ?');
    $st->execute([$id]);
    $r = $st->get_result()->fetch_assoc();
    return ['host' => $r['host_id'], 'state' => $r['state'], 'req' => json_decode((string)$r['request'], true)];
};
$now = gmdate('Y-m-d\TH:i:s\Z');
$old = gmdate('Y-m-d\TH:i:s\Z', time() - 3600);

// Site alive (fresh owner.json, Claude only), Home draining for an update
// F10: the gateway agents come from the installed modules; fixture: the corporate module installed and disabled
$st = $db->prepare("REPLACE INTO modules (id, version, enabled, standard, publisher, manifest_json, files_json, installed_utc, updated_utc)
    VALUES ('corporate', '0.1.0', 0, 1, 'test', ?, '{}', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
$st->execute([(string)file_get_contents(__DIR__ . '/../modules/corporate/module.json')]);
co_gateways(null, true);
$ok('gateway agent never moves', co_handover_blocked(['hostAuto' => true, 'agent' => 'dispatcher'], 'site') === 'gateway'
    && co_handover_blocked(['hostAuto' => true, 'agent' => 'claude'], 'site') === null);
$owner('site', ['hostId' => 'site', 'updatedUtc' => $now, 'agents' => ['claude' => []]]);
$poll('site', 3); // site agent 1.6.0 takes its jobs from the database
$owner('home-main', ['hostId' => 'home-main', 'updatedUtc' => $now, 'state' => 'updating', 'agents' => ['claude' => [], 'codex' => [], 'dispatcher' => []]]);
$poll('home-main', 5);

// starvation: older auto jobs that cannot move (GPT not on the site, resumed sessions, context folders) must not hide
// a movable one
for ($i = 0; $i < 60; $i++) $job('home-main', ['agent' => 'codex'], 600);
for ($i = 0; $i < 30; $i++) $job('home-main', ['conversation' => ['operation' => 'resume', 'sessionId' => 's-' . $i]], 600);
for ($i = 0; $i < 30; $i++) $job('home-main', ['contextFolders' => [['rootId' => 'r', 'relativePath' => 'x']]], 600);
$a = $job('home-main');                                                    // the 11.11 case: first message of a new chat
$uev = ['eventId' => 'EVT-S8-1', 'chatId' => 'CHAT-1', 'type' => 'user_message', 'jobId' => $a, 'hostId' => 'home-main', 'text' => 'hello'];
$st = $db->prepare("INSERT INTO events (event_id, chat_id, seq, type, job_id, data, sha, synced_utc) VALUES ('EVT-S8-1', 'CHAT-1', 1, 'user_message', ?, ?, 'x', UTC_TIMESTAMP(3))");
$st->execute([$a, json_encode($uev)]);
$young = $job('home-main', [], 20);                                        // not 60 s yet
$manual = $job('home-main', ['hostAuto' => false]);                        // the user chose Home by hand
$resume = $job('home-main', ['conversation' => ['operation' => 'resume', 'sessionId' => 's-1']]);
$codex = $job('home-main', ['agent' => 'codex']);                          // the site has no GPT
$corp = $job('home-main', ['agent' => 'dispatcher']);                      // gateway agent
$dev = $job('home-main', ['devices' => ['d-b903ab04eea11ba2']]);           // Claude + device: relay on the site
$web = $job('home-main', ['devices' => ['w-a0275c0ec9f70721']]);           // browser device: not relayed
$ctx = $job('home-main', ['contextFolders' => [['rootId' => 'r', 'relativePath' => 'x']]]);
$fb = $job('home-main', [], 200, ['fallback' => gmdate('Y-m-d H:i:s')]); // has a Drive copy
$cancel = $job('home-main', [], 90, ['cancel_requested' => 1]);
$claimed = $job('home-main', [], 90, ['state' => 'claimed']);
$moved = $job('home-main', ['handover' => ['from' => 'site']]);           // moved once already

$out = co_queue_handover();
$ok('two jobs moved', count($out) === 2);
$r = $row($a);
$ok('11.11 job -> site', $r['host'] === 'site' && $r['state'] === 'inbox' && $r['req']['hostId'] === 'site'
    && ($r['req']['handover']['from'] ?? '') === 'home-main' && str_starts_with($r['req']['instruction'], '[CO: this job was queued for Home')
    && str_ends_with($r['req']['instruction'], 'hello'));
$ok('claude with a device -> site (relay)', $row($dev)['host'] === 'site');
$ev = json_decode((string)$db->query("SELECT data FROM events WHERE event_id = 'EVT-S8-1'")->fetch_row()[0], true);
$ok('user_message names the new host', $ev['hostId'] === 'site' && $ev['text'] === 'hello');
foreach (['young' => $young, 'manual' => $manual, 'resume' => $resume, 'codex' => $codex, 'gateway' => $corp, 'browser device' => $web,
          'context folders' => $ctx, 'Drive copy' => $fb, 'cancelled' => $cancel, 'claimed' => $claimed, 'moved once' => $moved] as $k => $id) {
    $ok("$k stays at home", $row($id)['host'] === 'home-main');
}
$ok('second run moves nothing', co_queue_handover() === []);

// Site -> Home: the Bridge is back (running, polled 5 s ago); a site job nobody took
$owner('home-main', ['hostId' => 'home-main', 'updatedUtc' => $now, 'state' => 'running', 'agents' => ['claude' => [], 'codex' => []]]);
$s = $job('site');
$sManual = $job('site', ['hostAuto' => false]);                            // e.g. aiwb-subtask --resource site/claude
$out = co_queue_handover();
$ok('site job -> home', $out === ["$s site>home-main"] && $row($s)['host'] === 'home-main' && $row($s)['req']['hostId'] === 'home-main');
$ok('explicit site job stays', $row($sManual)['host'] === 'site');
$ok('the moved 11.11 job does not come back', $row($a)['host'] === 'site');

// Bridge not polling for 2 min: not alive, nothing goes there
$poll('home-main', 120);
$s2 = $job('site');
co_queue_handover();
$ok('silent Bridge gets nothing', $row($s2)['host'] === 'site');
// stale Home owner.json
$poll('home-main', 5);
$owner('home-main', ['hostId' => 'home-main', 'updatedUtc' => $old, 'agents' => ['claude' => []]]);
co_queue_handover();
$ok('stale Home gets nothing', $row($s2)['host'] === 'site');
// Site not taking jobs from the database (no job_claim for 2 min, e.g. site agent before 1.6.0): Home jobs stay
$poll('site', 120);
$h = $job('home-main');
co_queue_handover();
$ok('dead site gets nothing', $row($h)['host'] === 'home-main');
// Site paused (polling again)
$poll('site', 3);
$owner('site', ['hostId' => 'site', 'updatedUtc' => $now, 'state' => 'paused', 'agents' => ['claude' => []]]);
co_queue_handover();
$ok('paused site gets nothing', $row($h)['host'] === 'home-main');
// Old jobs (over 6 h) are left alone
$owner('site', ['hostId' => 'site', 'updatedUtc' => $now, 'agents' => ['claude' => []]]);
$poll('site', 3);
$ancient = $job('home-main', [], 7 * 3600);
co_queue_handover();
$ok('job over 6 h stays', $row($ancient)['host'] === 'home-main');
$ok('fresh site takes the waiting one', $row($h)['host'] === 'site');

echo $fail ? "FAILED $fail\n" : "ALL OK\n";
exit($fail ? 1 : 0);
