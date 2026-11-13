<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// relay-lib.php (schema 22, S1) against a throwaway MariaDB (never production): devices of a Bridge, calls of the site
// agent to them, claim/result by the Bridge, environment checks, expiry.
// Driver: python tests/actions-test.py --test relay-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/relay-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (InvalidArgumentException) { return true; }
};

$tools = [['name' => 'browser_navigate', 'description' => 'x', 'inputSchema' => ['type' => 'object']]];
$pc = ['deviceId' => 'd-b903ab04eea11ba2', 'environmentId' => 'owner', 'short' => 'homepc', 'name' => 'Home PC', 'kind' => 'desktop', 'online' => true, 'tools' => $tools];
$mac = ['deviceId' => 'd-09d2dfcd75465b6d', 'environmentId' => 'alex', 'short' => 'macalex', 'name' => 'Mac', 'kind' => 'mac', 'online' => false, 'tools' => $tools];
$home = ['deviceId' => 'h-home-main', 'environmentId' => 'owner', 'short' => 'home', 'name' => 'Bridge', 'kind' => 'home', 'online' => true, 'tools' => [['name' => 'tool_run']]];

$r = relay_devices_put($db, ['host' => 'home-main', 'devices' => [$pc, $mac, $home]]);
$ok('put three devices', $r['devices'] === 3);
$ok('bad kind refused', $throws(fn() => relay_devices_put($db, ['host' => 'home-main', 'devices' => [['kind' => 'web'] + $pc]])));
$ok('web device id refused', $throws(fn() => relay_devices_put($db, ['host' => 'home-main', 'devices' => [['deviceId' => 'w-0123456789abcdef'] + $pc]])));
$ok('duplicate short refused', $throws(fn() => relay_devices_put($db, ['host' => 'home-main', 'devices' => [$pc, ['deviceId' => 'd-0000000000000001'] + $pc]])));
$ok('bad tool name refused', $throws(fn() => relay_devices_put($db, ['host' => 'home-main', 'devices' => [['tools' => [['name' => 'a b']]] + $pc]])));

$own = relay_devices($db, ['env' => 'owner'])['devices'];
$shorts = array_column($own, 'short');
sort($shorts);
$ok('owner sees pc and home only', $shorts === ['home', 'homepc']);
$ok('tools come back', $own[0]['tools'] !== [] && isset($own[0]['online']));
$a = relay_devices($db, ['env' => 'alex'])['devices'];
$ok('alex sees the mac offline', count($a) === 1 && $a[0]['short'] === 'macalex' && $a[0]['online'] === false);

// R2: the server knows the job (jobs row) and its chat; the device policy comes from them
$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-own', 'owner', 'Mine', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES
    ('CHAT-1', 'P-own', 'Plain', '{}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-S', 'P-own', 'Shared with sam', '{}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-B', 'P-own', 'Subtask of the shared chat', '{\"parent\":{\"chatId\":\"CHAT-S\",\"kind\":\"subtask\"}}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-C', 'P-own', 'Subtask of the subtask', '{\"parent\":{\"chatId\":\"CHAT-B\",\"kind\":\"subtask\"}}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-L1', 'P-own', 'Loop 1', '{\"parent\":{\"chatId\":\"CHAT-L2\"}}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-L2', 'P-own', 'Loop 2', '{\"parent\":{\"chatId\":\"CHAT-L1\"}}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-R', 'P-own', 'Marked by the server', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('chat', 'CHAT-S', 'sam', 'editor', UTC_TIMESTAMP())");
$db->query("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, state, request, sha, synced_utc) VALUES
    ('J-1', 'site', 'owner', 'P-own', 'CHAT-1', 'claimed', '{}', '', UTC_TIMESTAMP(3)),
    ('J-S', 'site', 'owner', 'P-own', 'CHAT-S', 'claimed', '{}', '', UTC_TIMESTAMP(3)),
    ('J-C', 'site', 'owner', 'P-own', 'CHAT-C', 'claimed', '{}', '', UTC_TIMESTAMP(3)),
    ('J-L', 'site', 'owner', 'P-own', 'CHAT-L1', 'claimed', '{}', '', UTC_TIMESTAMP(3)),
    ('J-R', 'site', 'owner', 'P-own', 'CHAT-R', 'claimed', '{\"restricted\":true}', '', UTC_TIMESTAMP(3))");

$put = static fn(array $x) => relay_call_put($db, $x + ['env' => 'owner', 'jobId' => 'J-1', 'chatId' => 'CHAT-1', 'deviceId' => 'd-b903ab04eea11ba2', 'tool' => 'browser_navigate', 'args' => ['url' => 'https://www.linkedin.com/']]);
$c = $put([]);
$ok('call queued', $c['ok'] === true && preg_match('/^rc-[0-9a-f]{32}$/', $c['callId']) === 1);
$ok('device of another environment refused', $put(['deviceId' => 'd-09d2dfcd75465b6d'])['error']['code'] === 'not_allowed');
$ok('list args refused', $throws(fn() => $put(['args' => [1, 2]])));
$ok('bad tool refused', $throws(fn() => $put(['tool' => 'x;y'])));

// R2 (review S1-02): editor of a shared chat -> Site job without devices -> the relay gives and runs nothing
$ok('shared chat job: no devices listed', relay_devices($db, ['env' => 'owner', 'jobId' => 'J-S', 'chatId' => 'CHAT-S']) === ['devices' => [], 'restricted' => true]);
$ok('shared chat job: call refused', $put(['jobId' => 'J-S', 'chatId' => 'CHAT-S'])['ok'] === false);
$ok('plain job lists devices', count(relay_devices($db, ['env' => 'owner', 'jobId' => 'J-1', 'chatId' => 'CHAT-1'])['devices']) === 2);
$ok('subtask two levels below a shared chat: refused', $put(['jobId' => 'J-C', 'chatId' => 'CHAT-C'])['ok'] === false);
$ok('job queued restricted: refused', $put(['jobId' => 'J-R', 'chatId' => 'CHAT-R'])['ok'] === false);
$ok('parent loop ends, not shared', $put(['jobId' => 'J-L', 'chatId' => 'CHAT-L1'])['ok'] === true);
$ok('job of another env: refused', $put(['jobId' => 'J-1', 'env' => 'alex', 'deviceId' => 'd-09d2dfcd75465b6d'])['ok'] === false);
$ok('job of another chat: refused', $put(['jobId' => 'J-1', 'chatId' => 'CHAT-S'])['ok'] === false);
$ok('unknown job and chat: refused', $put(['jobId' => 'J-none', 'chatId' => 'CHAT-none'])['ok'] === false);
$ok('unknown job without chat: refused', $put(['jobId' => 'J-none', 'chatId' => ''])['ok'] === false);
$ok('job not synced yet, plain chat: allowed', $put(['jobId' => 'J-new', 'chatId' => 'CHAT-1'])['ok'] === true);
$ok('job not synced yet, shared chat: refused', $put(['jobId' => 'J-new', 'chatId' => 'CHAT-S'])['ok'] === false);
// job_submit (CO Web): an editor's Site job in the shared chat without devices is stored restricted
$req = ['agent' => 'claude', 'hostId' => 'site'];
$ok('submit: shared chat, no devices -> allowed, restricted', share_submit_policy($db, 'P-own', 'CHAT-S', $req, false) === null && $req['restricted'] === true);
$req = ['agent' => 'claude', 'devices' => ['d-b903ab04eea11ba2']];
$ok('submit: shared chat with devices refused', share_submit_policy($db, 'P-own', 'CHAT-S', $req, false) === SHARE_REFUSED);
$req = ['agent' => 'claude'];
$ok('submit: subtask below the shared chat, confidential agent refused', share_submit_policy($db, 'P-own', 'CHAT-C', $req, true) === SHARE_REFUSED);
$req = ['agent' => 'claude', 'restricted' => false, 'devices' => ['d-b903ab04eea11ba2']];
$ok('submit: plain chat keeps devices, client flag dropped', share_submit_policy($db, 'P-own', 'CHAT-1', $req, false) === null && !isset($req['restricted']));
$db->query("DELETE FROM relay_calls WHERE call_id <> '" . $c['callId'] . "'");

$g = relay_call_get($db, ['callId' => $c['callId']]);
$ok('queued before claim', $g['state'] === 'queued');
$cl = relay_claim($db, ['host' => 'home-main', 'waitSec' => 1]);
$ok('claimed by the host', count($cl['calls']) === 1 && $cl['calls'][0]['tool'] === 'browser_navigate' && $cl['calls'][0]['chatId'] === 'CHAT-1'
    && json_decode($cl['calls'][0]['argsJson'], true)['url'] === 'https://www.linkedin.com/');
$ok('other host claims nothing', relay_claim($db, ['host' => 'other', 'waitSec' => 0])['calls'] === []);
$ok('not claimed twice', relay_claim($db, ['host' => 'home-main', 'waitSec' => 0])['calls'] === []);
$ok('result by another host ignored', relay_result($db, ['host' => 'other', 'callId' => $c['callId'], 'result' => ['ok' => true]])['ok'] === false);
$ok('result stored', relay_result($db, ['host' => 'home-main', 'callId' => $c['callId'], 'result' => ['ok' => true, 'content' => 'navigated', 'untrusted' => true]])['ok'] === true);
$g = relay_call_get($db, ['callId' => $c['callId'], 'waitSec' => 1]);
$ok('site agent reads the result', $g['state'] === 'done' && $g['result']['content'] === 'navigated' && $g['result']['untrusted'] === true);
$ok('second result ignored', relay_result($db, ['host' => 'home-main', 'callId' => $c['callId'], 'result' => ['ok' => false]])['ok'] === false);

$c2 = $put(['timeoutSec' => 10]);
$db->query("UPDATE relay_calls SET deadline_utc = UTC_TIMESTAMP(3) - INTERVAL 1 SECOND WHERE call_id = '" . $c2['callId'] . "'");
$ok('overdue call reads expired', relay_call_get($db, ['callId' => $c2['callId']])['state'] === 'expired');
$ok('overdue call is not claimed', relay_claim($db, ['host' => 'home-main'])['calls'] === []);
$ok('cleanup expires it', relay_cleanup($db)['expired'] === 1);
$db->query('UPDATE relay_calls SET done_utc = UTC_TIMESTAMP(3) - INTERVAL 11 MINUTE');
$ok('cleanup deletes finished calls', relay_cleanup($db)['deleted'] === 2);
$ok('missing call', relay_call_get($db, ['callId' => $c2['callId']])['state'] === 'missing');

// deny list (deviceToolsDeny), pickup window, cancel
$ok('denied tool refused at put', $put(['tool' => 'corp_chat', 'deny' => ['corp_chat']])['error']['code'] === 'tool_off');
$ok('bad deny refused', $throws(fn() => $put(['deny' => 'corp_chat'])));
$c3 = $put(['deny' => ['corp_chat']]);
$cl = relay_claim($db, ['host' => 'home-main']);
$ok('deny travels to the Bridge', $cl['calls'][0]['deny'] === ['corp_chat']);
$c4 = $put([]);
$db->query("UPDATE relay_calls SET created_utc = UTC_TIMESTAMP(3) - INTERVAL 61 SECOND WHERE call_id = '" . $c4['callId'] . "'");
$ok('call not claimed in 60 s is never claimed', relay_claim($db, ['host' => 'home-main'])['calls'] === []);
$ok('it reads unclaimed', relay_call_get($db, ['callId' => $c4['callId']])['state'] === 'unclaimed');
$ok('and is withdrawn', relay_call_get($db, ['callId' => $c4['callId']])['state'] === 'expired');
$c5 = $put(['jobId' => 'J-2']);
$c6 = $put(['jobId' => 'J-2']);
$ok('one call withdrawn', relay_cancel($db, ['callId' => $c5['callId']])['cancelled'] === 1);
$ok('job end withdraws the rest', relay_cancel($db, ['jobId' => 'J-2'])['cancelled'] === 1);
$ok('withdrawn calls are not claimed', relay_claim($db, ['host' => 'home-main'])['calls'] === []);
$ok('claimed call is not withdrawn', relay_cancel($db, ['callId' => $c3['callId']])['cancelled'] === 0);
$ok('job end flags its running call', relay_cancel($db, ['jobId' => 'J-1'])['running'] === 1);
$cl = relay_claim($db, ['host' => 'home-main', 'waitSec' => 5]);
$ok('Bridge learns the ended job at once', $cl['cancelJobs'] === ['J-1'] && $cl['calls'] === []);
$ok('repeated until acknowledged', relay_claim($db, ['host' => 'home-main'])['cancelJobs'] === ['J-1']);
$ok('acknowledged: told no more', relay_claim($db, ['host' => 'home-main', 'ack' => ['J-1']])['cancelJobs'] === []);
$c7 = $put(['jobId' => 'J-3']);
relay_claim($db, ['host' => 'home-main']);
relay_cancel($db, ['jobId' => 'J-3']);
$c8 = $put(['jobId' => 'J-4']);
$cl = relay_claim($db, ['host' => 'home-main', 'max' => 0]);
relay_claim($db, ['host' => 'home-main', 'max' => 0, 'ack' => ['J-3']]);
$ok('max 0: cancellations only, nothing claimed', $cl['cancelJobs'] === ['J-3'] && $cl['calls'] === []
    && relay_call_get($db, ['callId' => $c8['callId']])['state'] === 'queued');

$db->query('UPDATE relay_devices SET updated_utc = UTC_TIMESTAMP(3) - INTERVAL 2 MINUTE');
$ok('silent Bridge: no devices listed', relay_devices($db, ['env' => 'owner'])['devices'] === []);
$ok('silent Bridge: call refused', $put([])['error']['code'] === 'home_offline');
$ok('empty put clears the host', relay_devices_put($db, ['host' => 'home-main', 'devices' => []])['devices'] === 0
    && (int)$db->query('SELECT COUNT(*) FROM relay_devices')->fetch_row()[0] === 0);

// S2: host_light — queue per executor and the relay heartbeat of the environment
$db->query("INSERT INTO jobs (job_id, host_id, env, state, sha, synced_utc, queued_utc, claimed_utc, completed_utc) VALUES
    ('J-q1', 'home-main', 'owner', 'inbox', '', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), NULL, NULL),
    ('J-r1', 'home-main', 'owner', 'claimed', '', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), NULL),
    ('J-c1', 'site', 'alex', 'completed', '', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)),
    ('J-old', 'home-main', 'owner', 'inbox', '', UTC_TIMESTAMP(3) - INTERVAL 3 DAY, UTC_TIMESTAMP(3) - INTERVAL 3 DAY, NULL, NULL)");
$hl = json_decode(json_encode(co_host_light($db, 'owner')), true);
$ok('light: home queue', $hl['hosts']['home-main']['queued'] === 1 && $hl['hosts']['home-main']['running'] === 1);
$ok('light: site last reply', $hl['hosts']['site']['queued'] === 0 && is_string($hl['hosts']['site']['lastDoneUtc']));
$ok('light: no devices after clear', $hl['relay']['devices'] === 0 && $hl['relay']['seenUtc'] === null);
relay_devices_put($db, ['host' => 'home-main', 'devices' => [$pc, $mac, $home]]);
$hl = co_host_light($db, 'owner');
$ok('light: owner relay devices', $hl['relay']['devices'] === 2 && is_string($hl['relay']['seenUtc']));
$ok('light: alex mac offline', co_host_light($db, 'alex')['relay']['devices'] === 0);

// db.php decodes bodies as arrays: an empty "properties": {} arrives as [] and must be stored and given as {} again
$wire = json_decode('{"deviceId":"d-b903ab04eea11ba2","environmentId":"owner","short":"homepc","name":"PC","kind":"desktop","online":true,"tools":['
    . '{"name":"browser_back","inputSchema":{"type":"object","properties":{},"required":[]}},'
    . '{"name":"fs_list","inputSchema":{"type":"object","properties":{"path":{"type":"object","properties":{}}}}},{"name":"bare","inputSchema":{}},'
    . '{"name":"lit","inputSchema":{"type":"object","properties":{"o":{"const":{"properties":[]},"default":{"properties":[]},"enum":[[]]},'
    . '"inputSchema":{},"items":{"type":"array","items":{"type":"object","properties":{}}}}}}]}', true);
relay_devices_put($db, ['host' => 'home-main', 'devices' => [$wire, $home]]);
$raw = $db->query("SELECT tools_json FROM relay_devices WHERE short_name = 'homepc'")->fetch_row()[0];
$ok('empty properties stored as objects', str_contains($raw, '"properties":{}') && str_contains($raw, '"fs_list","inputSchema":{"type":"object","properties":{"path":{"type":"object","properties":{}}}}')
    && str_contains($raw, '"required":[]') && str_contains($raw, '"name":"bare","inputSchema":{"type":"object"}'));
$ok('a property named like a keyword stays an open schema', str_contains($raw, '"inputSchema":{},"items":{"type":"array","items":{"type":"object","properties":{}}}'));
$ok('literals (const, default, enum) unchanged', str_contains($raw, '{"const":{"properties":[]},"default":{"properties":[]},"enum":[[]]}'));
$listed = json_encode(relay_devices($db, ['env' => 'owner']));
$ok('and listed as objects', str_contains($listed, '"browser_back","inputSchema":{"type":"object","properties":{}'));
relay_devices_put($db, ['host' => 'home-main', 'devices' => [$pc, $mac, $home]]);

// Schema 24: the one-time key of a job for relay-mcp.php
$tk = relay_token($db, ['env' => 'owner', 'jobId' => 'J-1', 'chatId' => 'CHAT-1', 'deny' => ['corp_chat'], 'ttlSec' => 600]);
$ok('key issued', $tk['ok'] === true && preg_match('/^rt-[0-9a-f]{48}$/', $tk['token']) === 1 && $tk['ttlSec'] === 600);
$row = $db->query("SELECT token_hash FROM relay_tokens WHERE job_id = 'J-1'")->fetch_row();
$ok('stored as a hash only', $row[0] === hash('sha256', $tk['token']) && !str_contains($row[0], substr($tk['token'], 3)));
$tj = relay_token_job($db, $tk['token']);
$ok('key gives job, env, chat, deny', $tj['jobId'] === 'J-1' && $tj['env'] === 'owner' && $tj['chatId'] === 'CHAT-1' && $tj['deny'] === ['corp_chat'] && $tj['tainted'] === false);
$ok('unknown key', relay_token_job($db, 'rt-' . str_repeat('0', 48)) === null && relay_token_job($db, 'x') === null);
$ok('no key for a shared chat job', relay_token($db, ['env' => 'owner', 'jobId' => 'J-S', 'chatId' => 'CHAT-S'])['restricted'] === true);
$ok('no key for a restricted job', relay_token($db, ['env' => 'owner', 'jobId' => 'J-R', 'chatId' => 'CHAT-R'])['ok'] === false);
$ok('no key for another env', relay_token($db, ['env' => 'alex', 'jobId' => 'J-1', 'chatId' => 'CHAT-1'])['ok'] === false);
$ok('no key for an unknown job', relay_token($db, ['env' => 'owner', 'jobId' => 'J-none', 'chatId' => 'CHAT-none'])['ok'] === false);
$ok('ttl capped', relay_token($db, ['env' => 'owner', 'jobId' => 'J-L', 'chatId' => 'CHAT-L1', 'ttlSec' => 999999])['ttlSec'] === RELAY_TOKEN_MAX_SEC);
relay_token_taint($db, $tj['hash']);
$ok('taint sticks to the key', relay_token_job($db, $tk['token'])['tainted'] === true);
$dv = relay_device_by_short($db, 'owner', 'home');
$ok('device by short', $dv['deviceId'] === 'h-home-main' && $dv['kind'] === 'home' && $dv['tools'][0]->name === 'tool_run');
$ok('device of another env by short: none', relay_device_by_short($db, 'alex', 'home') === null);
$cj = $put([]);
$ok('call belongs to its job', relay_call_job($db, $cj['callId']) === 'J-1' && relay_call_job($db, 'rc-' . str_repeat('0', 32)) === null);
relay_cancel($db, ['jobId' => 'J-1']);
$ok('job end deletes its key', relay_token_job($db, $tk['token']) === null);
$tl = relay_token($db, ['env' => 'owner', 'jobId' => 'J-1', 'chatId' => 'CHAT-1', 'ttlSec' => 60]);
$db->query("UPDATE relay_tokens SET expires_utc = UTC_TIMESTAMP(3) - INTERVAL 1 SECOND WHERE job_id = 'J-1'");
$ok('expired key refused', relay_token_job($db, $tl['token']) === null);
$ok('cleanup deletes expired keys', relay_cleanup($db)['tokens'] === 1);

// Schema 26: sandbox providers - the pick (own, then a family member's, else none) decides which seat a member sees
$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-kid', 'kid', 'Kid', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES ('CHAT-K', 'P-kid', 'Homework', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, state, request, sha, synced_utc) VALUES
    ('J-K', 'site', 'kid', 'P-kid', 'CHAT-K', 'claimed', '{}', '', UTC_TIMESTAMP(3))");
$pcTools = [['name' => 'pc_shell'], ['name' => 'pc_read']];
$seatHome = ['deviceId' => 'h-home-main-kid', 'environmentId' => 'kid', 'short' => 'home', 'name' => 'PC (sandbox kid)', 'kind' => 'home', 'online' => true, 'tools' => $pcTools];
relay_devices_put($db, ['host' => 'home-main', 'devices' => [$pc, $home, $seatHome]]);
$wsl = ['id' => 'home-main/aiwb-family', 'deviceId' => 'h-home-main', 'ownerEnv' => 'owner', 'kind' => 'wsl', 'online' => true,
    'card' => ['os' => 'Ubuntu 24.04', 'tools' => ['gcc', 'python3']], 'allowed' => ['kid'], 'quotas' => ['kid' => ['maxBackground' => 3]],
    'seats' => ['kid' => 'h-home-main-kid']];
$ok('sandbox put', sandbox_put($db, ['host' => 'home-main', 'sandboxes' => [$wsl]])['sandboxes'] === 1);
$ok('sandbox id must start with the host', $throws(fn() => sandbox_put($db, ['host' => 'home-main', 'sandboxes' => [['id' => 'other/x'] + $wsl]])));
$ok('seat of an env that is not allowed refused', $throws(fn() => sandbox_put($db, ['host' => 'home-main', 'sandboxes' => [['seats' => ['stranger' => 'h-home-main-x']] + $wsl]])));
$ok('bad kind refused', $throws(fn() => sandbox_put($db, ['host' => 'home-main', 'sandboxes' => [['kind' => 'docker!'] + $wsl]])));
$ok('seat on a device of another host refused', $throws(fn() => sandbox_put($db, ['host' => 'home-main', 'sandboxes' => [['seats' => ['kid' => 'h-mac-kid-kid']] + $wsl]])));
$ok('family member\'s sandbox picked', (sandbox_pick($db, 'kid')['id'] ?? '') === 'home-main/aiwb-family');
$ok('no sandbox for a stranger', sandbox_pick($db, 'alex') === null);
$ok('owner gets no seat (works on the whole PC)', sandbox_pick($db, 'owner') === null);
$kidDevs = array_column(relay_devices($db, ['env' => 'kid'])['devices'], 'deviceId');
$ok('kid sees the picked seat', $kidDevs === ['h-home-main-kid']);
$ok('kid can call the picked seat', relay_call_put($db, ['env' => 'kid', 'jobId' => 'J-K', 'chatId' => 'CHAT-K', 'deviceId' => 'h-home-main-kid', 'tool' => 'pc_shell', 'args' => ['command' => 'gcc --version']])['ok'] === true);
// the kid's own sandbox (a Mac VM of another provider) wins over the family member's
relay_devices_put($db, ['host' => 'mac-kid', 'devices' => [['deviceId' => 'h-mac-kid-kid', 'environmentId' => 'kid', 'short' => 'macvm', 'name' => 'Mac VM', 'kind' => 'home', 'online' => true, 'tools' => $pcTools]]]);
sandbox_put($db, ['host' => 'mac-kid', 'sandboxes' => [['id' => 'mac-kid/linux', 'deviceId' => 'h-mac-kid', 'ownerEnv' => 'kid', 'kind' => 'mac-vm', 'online' => true, 'seats' => ['kid' => 'h-mac-kid-kid']]]]);
$ok('own sandbox first', (sandbox_pick($db, 'kid')['id'] ?? '') === 'mac-kid/linux');
$kidDevs = array_column(relay_devices($db, ['env' => 'kid'])['devices'], 'deviceId');
$ok('only the own seat is shown', $kidDevs === ['h-mac-kid-kid']);
$r = relay_call_put($db, ['env' => 'kid', 'jobId' => 'J-K', 'chatId' => 'CHAT-K', 'deviceId' => 'h-home-main-kid', 'tool' => 'pc_shell', 'args' => ['command' => 'x']]);
$ok('the other seat is refused', $r['ok'] === false && $r['error']['code'] === 'sandbox_not_selected');
// own sandbox offline -> back to the family member's
sandbox_put($db, ['host' => 'mac-kid', 'sandboxes' => [['id' => 'mac-kid/linux', 'deviceId' => 'h-mac-kid', 'ownerEnv' => 'kid', 'kind' => 'mac-vm', 'online' => false, 'seats' => ['kid' => 'h-mac-kid-kid']]]]);
$ok('offline own sandbox: family member\'s again', (sandbox_pick($db, 'kid')['id'] ?? '') === 'home-main/aiwb-family');
$db->query("UPDATE sandbox_resources SET updated_utc = UTC_TIMESTAMP(3) - INTERVAL 5 MINUTE WHERE host_id = 'home-main'");
$ok('silent provider: no sandbox', sandbox_pick($db, 'kid') === null);
$lst = sandbox_list($db, ['env' => 'kid']);
$ok('list for the kid', count($lst['sandboxes']) === 2 && $lst['pick'] === null && $lst['sandboxes'][0]['card']['os'] === 'Ubuntu 24.04' && $lst['sandboxes'][0]['online'] === false);
$ok('list for a stranger is empty', sandbox_list($db, ['env' => 'alex'])['sandboxes'] === []);
sandbox_put($db, ['host' => 'home-main', 'sandboxes' => []]);
sandbox_put($db, ['host' => 'mac-kid', 'sandboxes' => []]);
$ok('empty put clears the provider', sandbox_list($db, [])['sandboxes'] === []);

echo $fail ? "FAILED: $fail\n" : "all ok\n";
exit($fail ? 1 : 0);
