<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// apps-lib.php (Freedom F14b-1, SPEC §3.6a item 5): actions of apps for an agent job against a throwaway MariaDB (never
// production) with the module smart-home: the job decides the env (owner, member, nobody), finished / unknown /
// restricted jobs get nothing, outward actions are refused, arguments are checked against the schema, the handler sees
// the person's rights and actor=agent, and the env's open app pages get a refresh event.
// Driver: python tests/actions-test.py --test apps-test.php (seeds owner, sam, alex)
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
$upload = static function (string $src, string $id): array {
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
    return ['manifest' => json_decode((string)file_get_contents("$src/module.json"), true), 'files' => $files];
};
$job = static function (string $id, string $env, string $state = 'claimed', array $req = [], ?string $result = null) use ($db): void {
    $st = $db->prepare("INSERT INTO jobs (job_id, env, project_id, chat_id, state, request, result, sha, synced_utc)
                        VALUES (?, ?, 'PRJ-T', ?, ?, ?, ?, '', UTC_TIMESTAMP(3))");
    $chat = 'CHAT-' . $id;
    $r = json_encode((object)$req);
    $st->bind_param('ssssss', $id, $env, $chat, $state, $r, $result);
    $st->execute();
    $st->close();
};

$src = realpath(__DIR__ . '/../modules/smart-home');
$p = $upload($src, 'smart-home');
co_module_install($db, $p + ['standard' => true, 'publisher' => 'ed25519:test']);
co_module_config_put('smart-home', $p['manifest'], ['members' => 'sam']);
$job('J-OWNER', 'owner');
$job('J-SAM', 'sam');
$job('J-ALEX', 'alex');
$job('J-DONE', 'owner', 'completed', [], '{}');
$job('J-RESTR', 'owner', 'claimed', ['restricted' => true]);
$job('J-RO', 'owner', 'claimed', ['permissions' => ['files' => 'read']]);

$ok('module off: no apps', co_app_list($db, ['jobId' => 'J-OWNER'])['apps'] === []);
co_module_set($db, 'smart-home', true, 'owner');

$l = co_app_list($db, ['jobId' => 'J-OWNER']);
$home = $l['apps'][0] ?? [];
$ok('owner job: the Home app with its face', ($home['app'] ?? '') === 'smart-home' && ($home['icon'] ?? '') === '🏠' && $l['env'] === 'owner');
$acts = array_column($home['actions'] ?? [], null, 'op');
$ok('two actions, outward marked', ($acts['home_state']['outward'] ?? null) === false && ($acts['home_control']['outward'] ?? null) === true
    && isset($acts['home_control']['note']));
$ok('schema reduced and closed', ($acts['home_control']['args']['required'] ?? []) === ['entity', 'action', 'requestKey']
    && ($acts['home_control']['args']['additionalProperties'] ?? true) === false);
$ok('unknown job refused', $throws(fn() => co_app_list($db, ['jobId' => 'J-NOPE']), 'unknown job'));
$ok('missing job id refused', $throws(fn() => co_app_list($db, []), 'jobId'));
$ok('finished job refused', $throws(fn() => co_app_action($db, ['jobId' => 'J-DONE', 'app' => 'smart-home', 'action' => 'home_state']), 'finished'));
$ok('restricted job: no apps', co_app_list($db, ['jobId' => 'J-RESTR'])['restricted'] === true
    && co_app_action($db, ['jobId' => 'J-RESTR', 'app' => 'smart-home', 'action' => 'home_state'])['error'] === 'not_allowed');

$ok('read-only chat: list yes, action no', count(co_app_list($db, ['jobId' => 'J-RO'])['apps']) === 1
    && co_app_action($db, ['jobId' => 'J-RO', 'app' => 'smart-home', 'action' => 'home_state'])['error'] === 'read_only');
$ev0 = co_app_events($db, 'owner', -1);
$r = co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('owner reads the house', $r['ok'] === true && ($r['result']['online'] ?? null) === false && is_array($r['result']['entities'] ?? null));
$ev = co_app_events($db, 'owner', $ev0['seq']);
$ok('refresh event for the owner\'s pages', $ev['seq'] === $ev0['seq'] + 1 && ($ev['events'][count($ev['events']) - 1]['app'] ?? '') === 'smart-home');
$ok('since -1 gives only the cursor', co_app_events($db, 'owner', -1)['events'] === [] && co_app_events($db, 'owner', 0)['events'] !== []);
$ok('no event for another env', co_app_events($db, 'sam', -1)['seq'] === 0);

$r = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('member listed in the module config reads it too', $r['ok'] === true);
$r = co_app_action($db, ['jobId' => 'J-ALEX', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('member without access gets the module\'s own refusal', $r['ok'] === false && ($r['result']['error'] ?? '') === 'home_forbidden');
$ok('a refused action refreshes nothing', co_app_events($db, 'alex', -1)['seq'] === 0);

// F14b-2: an outward action becomes a card; only the person of the env runs it, once, with the card's etag
$ctl = ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_control',
    'args' => ['entity' => 'light.x', 'action' => 'turn_on', 'requestKey' => str_repeat('a', 20)]];
$r = co_app_action($db, $ctl);
$rid = (string)($r['requestId'] ?? '');
$ok('outward action waits for the person', $r['ok'] === false && ($r['pending'] ?? false) === true && $r['status'] === 'pending'
    && preg_match('/^[0-9a-f]{16}$/', $rid) === 1);
$ok('the same call again gives the same card', (co_app_action($db, $ctl)['requestId'] ?? '') === $rid);
$ok('outward args are still checked', $throws(fn() => co_app_action($db, ['args' => ['entity' => 'light.x']] + $ctl), 'args.action'));
$cards = co_app_requests_inbox($db, 'owner');
$card = $cards[0]['app'] ?? [];
$ok('one card in the owner\'s inbox, args in words', count($cards) === 1 && $cards[0]['id'] === "app:$rid" && $cards[0]['kind'] === 'app'
    && $card['title'] === 'Дом' && ($card['args'][0]['about'] ?? '') !== '' && $card['etag'] !== '');
$ok('no card for another env', co_app_requests_inbox($db, 'sam') === []);
$st = co_app_action_status($db, ['jobId' => 'J-OWNER', 'requestId' => $rid]);
$ok('status: pending', $st['ok'] === true && $st['status'] === 'pending');
$ok('status from a restricted job refused', co_app_action_status($db, ['jobId' => 'J-RESTR', 'requestId' => $rid])['error'] === 'not_allowed');
$ok('status from another env does not see it', co_app_action_status($db, ['jobId' => 'J-SAM', 'requestId' => $rid])['error'] === 'unknown_request');
$ok('another env cannot decide it', co_app_request_decide($db, 'sam', false, 'sam@example.org', ['id' => $rid, 'decision' => 'run', 'etag' => $card['etag']])['error'] === 'not_found');
$ok('a wrong etag is refused', co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'decision' => 'run', 'etag' => 'x'])['error'] === 'etag_mismatch');
$ok('bad decision refused', $throws(fn() => co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'decision' => 'yes'])));
$ev0 = co_app_events($db, 'owner', -1);
$d = co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'decision' => 'run', 'etag' => $card['etag']]);
$ok('run: the handler ran with the person\'s rights (house offline here)', $d['status'] === 'failed' && ($d['result']['error'] ?? '') === 'home_offline');
$ok('a second click is refused', co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'decision' => 'run', 'etag' => $card['etag']])['error'] === 'already_decided');
$st = co_app_action_status($db, ['jobId' => 'J-OWNER', 'requestId' => $rid]);
$ok('status: the outcome for the agent', $st['status'] === 'failed' && ($st['result']['error'] ?? '') === 'home_offline' && isset($st['decidedUtc']) && !isset($st['by']));
$ok('the card left the inbox', co_app_requests_inbox($db, 'owner') === []);
$ok('a failed run refreshes nothing', co_app_events($db, 'owner', -1)['seq'] === $ev0['seq']);
// decline, expiry, the pending limit
$r2 = co_app_action($db, ['args' => ['entity' => 'light.y', 'action' => 'turn_off', 'requestKey' => str_repeat('b', 20)]] + $ctl);
$ok('decline', co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $r2['requestId'], 'decision' => 'deny'])['status'] === 'denied'
    && co_app_action_status($db, ['jobId' => 'J-OWNER', 'requestId' => $r2['requestId']])['status'] === 'denied');
$r3 = co_app_action($db, ['args' => ['entity' => 'light.z', 'action' => 'turn_off', 'requestKey' => str_repeat('c', 20)]] + $ctl);
co_app_requests_locked($db, 'owner', static function (array &$l) use ($r3): void {
    foreach ($l as &$q) if ($q['id'] === $r3['requestId']) $q['expires'] = time() - 1;
});
$e3 = co_app_requests_inbox($db, 'owner');
$ok('an expired card cannot run', $e3 === [] && co_app_request_decide($db, 'owner', true, 'owner@example.org',
    ['id' => $r3['requestId'], 'decision' => 'run', 'etag' => 'x'])['error'] === 'expired');
for ($i = 0; $i < CO_APP_REQ_PENDING_MAX; $i++) {
    co_app_action($db, ['args' => ['entity' => 'light.n' . $i, 'action' => 'turn_on', 'requestKey' => str_repeat('d', 20)]] + $ctl);
}
$keep = [['id' => 'p', 'state' => 'pending']];
for ($i = 0; $i < CO_APP_REQ_KEEP + 5; $i++) $keep[] = ['id' => "d$i", 'state' => 'done'];
$keep[] = ['id' => 'r', 'state' => 'running'];
$pruned = array_column(co_app_requests_prune($keep), 'id');
$ok('pruning keeps pending and running, drops the oldest decided', count($pruned) === CO_APP_REQ_KEEP && in_array('p', $pruned, true)
    && in_array('r', $pruned, true) && !in_array('d0', $pruned, true) && in_array('d' . (CO_APP_REQ_KEEP + 4), $pruned, true));
$ok('pending cards are limited', co_app_action($db, ['args' => ['entity' => 'light.more', 'action' => 'turn_on', 'requestKey' => str_repeat('d', 20)]] + $ctl)['error'] === 'too_many_pending');
$ok('the member\'s own card is theirs', ($rs = co_app_action($db, ['jobId' => 'J-SAM'] + $ctl)) && co_app_requests_inbox($db, 'sam')[0]['app']['requestId'] === $rs['requestId']
    && co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rs['requestId'], 'decision' => 'deny'])['error'] === 'not_found');
// a changed module (new version) refuses an old card
$rs2 = co_app_requests_inbox($db, 'sam')[0]['app'];
$db->query("UPDATE modules SET version = '9.9.9' WHERE id = 'smart-home'");
co_modules($db, true);
$ok('a card from an older version of the app is refused', co_app_request_decide($db, 'sam', false, 'sam@example.org',
    ['id' => $rs2['requestId'], 'decision' => 'run', 'etag' => $rs2['etag']])['result']['error'] === 'app_changed');
$db->query("UPDATE modules SET version = '{$p['manifest']['version']}' WHERE id = 'smart-home'");
co_modules($db, true);
// a run that works: the house reports itself online with light.ok, the person's click queues the command
$hm = co_modules($db)['smart-home'];
co_module_load($hm)['host']['home_exchange'](co_module_ctx($db, $hm), ['entities' => [['id' => 'light.ok', 'name' => 'ok', 'state' => 'off',
    'actions' => ['turn_on', 'turn_off'], 'attrs' => []]], 'results' => [], 'online' => true, 'updated' => time(), 'hostNow' => time()]);
co_app_requests_locked($db, 'owner', static function (array &$l): void {
    foreach ($l as &$q) if ($q['state'] === 'pending') $q['state'] = 'denied';
});
$rk = co_app_action($db, ['args' => ['entity' => 'light.ok', 'action' => 'turn_on', 'requestKey' => str_repeat('e', 20)]] + $ctl);
$ck = co_app_requests_inbox($db, 'owner');
$ev0 = co_app_events($db, 'owner', -1);
$d = co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $rk['requestId'], 'decision' => 'run', 'etag' => $ck[0]['app']['etag'] ?? '']);
$ok('run: done, the command is queued for the house', count($ck) === 1 && $d['ok'] === true && $d['status'] === 'done' && is_string($d['result']['id'] ?? null));
$ok('a run refreshes the app pages', (co_app_events($db, 'owner', $ev0['seq'])['events'][0]['action'] ?? '') === 'home_control');
$ok('status: done', co_app_action_status($db, ['jobId' => 'J-OWNER', 'requestId' => $rk['requestId']])['status'] === 'done');
$ok('an op that is not an action is unknown', co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_pair_start'])['error'] === 'unknown_action');
$ok('another app is unknown', co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'nope', 'action' => 'home_state'])['error'] === 'unknown_action');
$ok('extra argument refused', $throws(fn() => co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_state', 'args' => ['x' => 1]]), 'args.x'));

// the argument checker on its own
$s = co_app_schema(['properties' => ['n' => ['type' => 'integer'], 'k' => ['type' => 'string', 'enum' => ['a', 'b']], 'bad' => ['type' => 'array']], 'required' => ['n', 'bad']]);
$ok('unsupported types dropped from the schema', !isset(((array)$s['properties'])['bad']) && $s['required'] === ['n']);
$ok('required checked', $throws(fn() => co_app_args($s, []), 'args.n'));
$ok('type checked', $throws(fn() => co_app_args($s, ['n' => '1']), 'integer'));
$ok('enum checked', $throws(fn() => co_app_args($s, ['n' => 1, 'k' => 'c']), 'one of'));
$ok('a list is not an object', $throws(fn() => co_app_args($s, [1]), 'object'));
$ok('good args pass', co_app_args($s, ['n' => 2, 'k' => 'b']) === ['n' => 2, 'k' => 'b']);

// a manifest action whose op is not a user op of the module is ignored by the server
$bad = $p['manifest'];
$bad['provides']['actions'][] = ['op' => 'home_exchange', 'description' => 'host op', 'outward' => false];
$bad['provides']['actions'][] = ['op' => 'home_pair_state', 'description' => 'no outward flag'];
$ok('actions outside server.ops.user or without outward are dropped', array_keys(co_app_actions(['manifest' => $bad])) === ['home_state', 'home_control']);

co_module_remove($db, ['id' => 'smart-home', 'data' => 'erase']);
echo $fail ? "$fail FAILED\n" : "apps-test OK\n";
exit($fail ? 1 : 0);
