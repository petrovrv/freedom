<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Local throwaway MariaDB only: python co-agent/tests/actions-test.py --test app-relay-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/apps-lib.php';
$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $fn): bool {
    try { $fn(); return false; } catch (InvalidArgumentException) { return true; }
};
$install = static function (string $id, string $from, ?array $manifest = null, ?string $serverCode = null) use ($db): void {
    $src = __DIR__ . '/../modules/' . $from;
    $dest = co_module_home($id);
    $files = [];
    foreach (new RecursiveIteratorIterator(new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS)) as $f) {
        $rel = str_replace('\\', '/', substr($f->getPathname(), strlen($src) + 1));
        @mkdir(dirname("$dest/$rel"), 0700, true);
        if ($rel === 'server/ops.php' && $serverCode !== null) file_put_contents("$dest/$rel", $serverCode);
        else copy($f->getPathname(), "$dest/$rel");
        if ($rel !== 'module.json' && $rel !== 'README.md') $files[$rel] = hash_file('sha256', "$dest/$rel");
    }
    $man = $manifest ?? json_decode((string)file_get_contents("$src/module.json"), true);
    file_put_contents("$dest/module.json", json_encode($man));
    co_module_install($db, ['manifest' => $man, 'files' => $files, 'standard' => true, 'publisher' => 'ed25519:test']);
    co_module_set($db, $id, true, 'owner');
};
$schema = ['type' => 'object', 'properties' => ['query' => ['type' => 'string']], 'required' => ['query'], 'additionalProperties' => false];
$man = json_decode((string)file_get_contents(__DIR__ . '/../modules/smart-home/module.json'), true);
$man['id'] = 'relay-test';
$man['version'] = '1.0.0';
$man['provides']['actions'] = [
    ['op' => 'read', 'description' => 'Read test', 'args' => $schema, 'outward' => false, 'via' => 'home-relay'],
    ['op' => 'send', 'description' => 'Send test', 'args' => $schema, 'outward' => true, 'via' => 'home-relay'],
    ['op' => 'home_state', 'description' => 'Unknown via', 'outward' => false, 'via' => 'future-relay'],
];
$install('relay-test', 'smart-home', $man);
$install('home-relay', 'home-relay');
foreach ([['J-OK', 'owner', 'claimed', []], ['J-OTHER', 'sam', 'claimed', []],
          ['J-RESTRICTED', 'owner', 'claimed', ['restricted' => true]],
          ['J-READONLY', 'owner', 'claimed', ['permissions' => ['files' => 'read']]],
          ['J-DONE', 'owner', 'completed', []]] as [$id, $env, $state, $req]) {
    $st = $db->prepare("INSERT INTO jobs (job_id, env, project_id, chat_id, state, request, sha, synced_utc) VALUES (?, ?, 'TEST', ?, ?, ?, '', UTC_TIMESTAMP(3))");
    $st->execute([$id, $env, 'CHAT-' . $id, $state, json_encode((object)$req)]); $st->close();
}
$in = ['jobId' => 'J-OK', 'app' => 'relay-test', 'action' => 'read'];
$ok('relay action needs no user server op; valid provider ids preserved', array_keys(co_app_actions(['manifest' => $man])) === ['read', 'send', 'home_state']);
$ok('missing provider fails closed without direct fallback', co_app_relay_status($db, ['action' => 'home_state'] + $in)['error'] === 'relay_disabled'
    && co_app_action($db, ['action' => 'home_state'] + $in)['error'] === 'relay_envelope_required');
$ok('plaintext read refused', co_app_action($db, $in + ['args' => ['query' => 'private']])['error'] === 'relay_envelope_required');
$ok('plaintext outward refused without card', co_app_action($db, ['action' => 'send'] + $in)['error'] === 'relay_approval_unavailable' && co_app_requests($db, 'owner') === []);
$ok('relay outward status unavailable without worker; direct call refused', co_app_relay_status($db, ['action' => 'send'] + $in)['error'] === 'relay_unavailable'
    && co_app_relay_call($db, ['action' => 'send'] + $in)['error'] === 'relay_approval_unavailable');
$ok('restricted status/call refused', co_app_relay_status($db, ['jobId' => 'J-RESTRICTED'] + $in)['error'] === 'not_allowed'
    && co_app_relay_call($db, ['jobId' => 'J-RESTRICTED'] + $in)['error'] === 'not_allowed');
$ok('readonly status/call refused', co_app_relay_status($db, ['jobId' => 'J-READONLY'] + $in)['error'] === 'read_only'
    && co_app_relay_call($db, ['jobId' => 'J-READONLY'] + $in)['error'] === 'read_only');
$ok('finished/unknown job refused', $throws(fn() => co_app_relay_status($db, ['jobId' => 'J-DONE'] + $in))
    && $throws(fn() => co_app_relay_call($db, ['jobId' => 'J-MISSING'] + $in)));
$ok('unpaired status refused', co_app_relay_status($db, $in)['error'] === 'relay_unavailable');
$ok('stale version refused before queue', co_app_relay_call($db, $in + ['version' => '0.0.1'])['error'] === 'app_changed');
$ok('plaintext/forged authority refused', co_app_relay_call($db, $in + ['version' => '1.0.0', 'args' => []])['error'] === 'relay_envelope_required'
    && co_app_relay_call($db, $in + ['version' => '1.0.0', 'dispatch' => []])['error'] === 'relay_envelope_required');
$ok('bad envelope refused', co_app_relay_call($db, $in + ['version' => '1.0.0', 'envelope' => []])['error'] === 'bad_envelope');
$m = co_modules($db)['home-relay'];
$ctx = co_module_ctx($db, $m, 'owner', true, 'owner@example.org');
$workerToken = co_relay_worker_token($ctx, ['env' => 'owner'])['token']; // local throwaway pairing only; token never printed
$pipes = [];
$proc = proc_open(['python', __DIR__ . '/../modules/home-relay/home/app_test_fixture.py', $ctx['stateDir']],
    [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
if (!is_resource($proc)) throw new RuntimeException('fixture start failed');
try {
    fclose($pipes[0]);
    $envelope = json_decode((string)fgets($pipes[1]), true);
    if (!is_array($envelope)) throw new RuntimeException('fixture envelope failed: ' . stream_get_contents($pipes[2]));
    $s = co_app_relay_status($db, $in);
    $ok('status derives env/action/schema and offers worker key', $s['ok'] && $s['env'] === 'owner' && $s['action'] === 'read'
        && $s['version'] === '1.0.0' && $s['outward'] === false && $s['relay']['online'] && $s['relay']['served'] && strlen($s['relay']['pub']) > 50);
    $ok('other environment cannot see worker', co_app_relay_status($db, ['jobId' => 'J-OTHER'] + $in)['error'] === 'relay_unavailable');
    $ok('envelope module cannot switch app', co_app_relay_call($db, $in + ['version' => '1.0.0', 'envelope' => ['module' => 'other'] + $envelope])['error'] === 'bad_envelope');
    $statusPath = co_relay_status_file($ctx, 'owner');
    $goodStatus = file_get_contents($statusPath);
    $oldStatus = json_decode((string)$goodStatus, true);
    unset($oldStatus['modules']['relay-test']['relayAppProtocol']);
    file_put_contents($statusPath, json_encode($oldStatus));
    $ok('old worker cannot silently downgrade agent dispatch', co_app_relay_status($db, $in)['error'] === 'relay_upgrade_required'
        && co_app_relay_call($db, $in + ['version' => '1.0.0', 'envelope' => $envelope])['error'] === 'relay_upgrade_required');
    file_put_contents($statusPath, $goodStatus);
    $r = co_app_relay_call($db, $in + ['version' => '1.0.0', 'envelope' => $envelope]);
    $ok('PHP core -> queue -> Python worker -> authenticated encrypted reply', $r['ok'] === true && isset($r['envelope']['iv'], $r['envelope']['ct'])
        && !str_contains(json_encode($r), 'private-fixture-data'));
    $ok('worker verifies exact trusted dispatch and local handler', trim((string)fgets($pipes[1])) === 'fixture PASS');
    fclose($pipes[1]);
    $err = stream_get_contents($pipes[2]); fclose($pipes[2]);
    $exit = proc_close($proc); $proc = null;
    $ok('fixture child exits successfully', $exit === 0 && $err === '');
} finally {
    if (is_resource($proc)) proc_terminate($proc);
}
// Stored capable status cannot let a current legacy poll steal an app dispatch.
$pipes = [];
$proc = proc_open(['python', __DIR__ . '/../modules/home-relay/home/approval_test_fixture.py', $ctx['stateDir']],
    [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
if (!is_resource($proc)) throw new RuntimeException('approval fixture start failed');
try {
    if (trim((string)fgets($pipes[1])) !== 'ready') throw new RuntimeException('approval fixture not ready');
    $command = static function (array $v) use ($pipes): array {
        fwrite($pipes[0], json_encode($v) . "\n"); fflush($pipes[0]);
        $r = json_decode((string)fgets($pipes[1]), true);
        if (!is_array($r)) throw new RuntimeException('approval fixture command failed');
        return $r;
    };
    $e = $command(['op' => 'seal', 'phase' => 'stage']);
    $r = co_app_relay_propose($db, ['jobId' => 'J-OK', 'app' => 'relay-test', 'action' => 'send', 'version' => '1.0.0', 'envelope' => $e]);
    $stage = $command(['op' => 'open', 'id' => $e['id'], 'envelope' => $r['envelope']]);
    $ok('full stage PHP -> encrypted queue -> durable worker acknowledgement', $stage['requestId'] === $r['requestId'] && $stage['revision'] === $r['revision']);
    $rid = $r['requestId']; $revision = $r['revision'];
    $q = array_values(array_filter(co_app_requests($db, 'owner'), static fn($x) => $x['id'] === $rid))[0];
    $binding = ['requestId' => $rid, 'revision' => $revision];
    $e = $command(['op' => 'seal', 'phase' => 'preview', 'binding' => $binding]);
    $r = co_app_request_relay($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'etag' => $q['etag'], 'phase' => 'preview', 'envelope' => $e]);
    $preview = $command(['op' => 'open', 'id' => $e['id'], 'envelope' => $r['envelope']]);
    $ok('full preview uses separate browser key and private stored args', $preview['args']['query'] === 'private-target' && !str_contains(json_encode($r), 'private-target'));
    $e = $command(['op' => 'seal', 'phase' => 'run', 'binding' => $binding + ['previewToken' => $preview['previewToken']]]);
    $r = co_app_request_relay($db, 'owner', true, 'owner@example.org', ['id' => $rid, 'etag' => $q['etag'], 'phase' => 'run', 'envelope' => $e]);
    $result = $command(['op' => 'open', 'id' => $e['id'], 'envelope' => $r['envelope']]);
    $ok('full Run consumes proposal and returns private receipt', $result['state'] === 'done' && $result['result']['result']['receipt'] === 'private-receipt'
        && !str_contains(json_encode(co_app_requests($db, 'owner')), 'private-'));
    $e = $command(['op' => 'seal', 'phase' => 'result', 'binding' => $binding]);
    $r = co_app_relay_result($db, ['jobId' => 'J-OK', 'requestId' => $rid, 'envelope' => $e]);
    $result = $command(['op' => 'open', 'id' => $e['id'], 'envelope' => $r['envelope']]);
    $ok('full agent fresh-key result recovers durable private outcome', $result['state'] === 'done' && $result['requestId'] === $rid);
    fwrite($pipes[0], "{\"op\":\"finish\"}\n"); fflush($pipes[0]);
    $ok('synthetic outward handler called exactly once', trim((string)fgets($pipes[1])) === 'approval-fixture PASS');
    fclose($pipes[0]); fclose($pipes[1]); $err = stream_get_contents($pipes[2]); fclose($pipes[2]);
    $exit = proc_close($proc); $proc = null;
    $ok('approval fixture exits without errors', $exit === 0 && $err === '');
} finally { if (is_resource($proc)) proc_terminate($proc); }
$qid = str_repeat('b', 32);
$dir = co_relay_dir($ctx, 'owner');
$queued = ['id' => $qid, 'module' => 'relay-test', 'dispatch' => ['kind' => 'app-action-v1']];
file_put_contents("$dir/$qid.req", json_encode($queued));
[$code, $legacy] = co_relay_worker($ctx, ['env' => 'owner', 'token' => $workerToken, 'nowait' => true,
    'status' => ['modules' => ['relay-test' => []]]]);
$ok('current legacy poll cannot claim dispatch even after new worker', $code === 200 && $legacy['requests'] === [] && is_file("$dir/$qid.req"));
[$code, $current] = co_relay_worker($ctx, ['env' => 'owner', 'token' => $workerToken, 'nowait' => true,
    'status' => ['modules' => ['relay-test' => ['relayAppProtocol' => 1]]]]);
$ok('current capable poll receives dispatch', $code === 200 && ($current['requests'][0]['id'] ?? '') === $qid && !is_file("$dir/$qid.req"));
$legacyId = str_repeat('c', 32);
file_put_contents("$dir/$legacyId.req", json_encode(['id' => $legacyId, 'module' => 'relay-test']));
[$code, $legacy] = co_relay_worker($ctx, ['env' => 'owner', 'token' => $workerToken, 'nowait' => true,
    'status' => ['modules' => ['relay-test' => ['relayAppProtocol' => 1]]]]);
$ok('legacy calls cannot reach old worker lacking enforced page policy', $legacy['requests'] === [] && is_file("$dir/$legacyId.req"));
[$code, $current] = co_relay_worker($ctx, ['env' => 'owner', 'token' => $workerToken, 'nowait' => true,
    'status' => ['modules' => ['relay-test' => ['relayAppProtocol' => 1, 'relayApprovalProtocol' => 1]]]]);
$ok('legacy call reaches only policy enforcing worker', ($current['requests'][0]['id'] ?? '') === $legacyId);

// The core resolves an arbitrary trusted transport module through the same contract.
$provider = json_decode((string)file_get_contents(__DIR__ . '/../modules/home-relay/module.json'), true);
$provider['id'] = 'other-relay';
$provider['server']['ops'] = ['user' => [], 'host' => [], 'push' => []];
$provider['server']['routes'] = [];
$code = <<<'PHP'
<?php
return ['relay' => [
    'status' => static fn(array $c, string $m): array => ['online' => true, 'served' => true,
        'pub' => 'fixture-public-key', 'status' => ['relayAppProtocol' => 1, 'relayApprovalProtocol' => 1]],
    'appCall' => static function (array $c, string $m, array $in, array $d): array {
        $GLOBALS['test_dispatch'] = $d;
        return $d['kind'] === 'app-approval-v1' ? ['iv' => 'encrypted', 'ct' => 'encrypted'] : ['error' => 'fixture_transport_stopped'];
    },
]];
PHP;
$install('other-relay', 'home-relay', $provider, $code);
$man['provides']['actions'][0]['via'] = 'other-relay';
$st = $db->prepare('UPDATE modules SET manifest_json = ? WHERE id = ?');
$st->execute([json_encode($man), 'relay-test']); $st->close();
co_modules($db, true);
$alt = co_app_relay_status($db, $in);
$ok('alternate transport resolved through declared via', $alt['ok'] === true && $alt['via'] === 'other-relay');
$ok('alternate transport call uses provider contract', co_app_relay_call($db, $in + ['version' => '1.0.0',
    'envelope' => ['id' => str_repeat('d', 32), 'pk' => 'x', 'iv' => 'x', 'ct' => 'x']])['error'] === 'fixture_transport_stopped');
$db->query("UPDATE modules SET standard = 0 WHERE id = 'other-relay'"); co_modules($db, true);
$ok('untrusted provider refused', co_app_relay_status($db, $in)['error'] === 'relay_disabled');
$db->query("UPDATE modules SET standard = 1 WHERE id = 'other-relay'"); co_modules($db, true);
$man['provides']['actions'][1]['via'] = 'other-relay';
$st = $db->prepare('UPDATE modules SET manifest_json = ? WHERE id = ?');
$st->execute([json_encode($man), 'relay-test']); $st->close(); co_modules($db, true);
$proposal = ['jobId' => 'J-OK', 'app' => 'relay-test', 'action' => 'send', 'version' => '1.0.0',
    'envelope' => ['id' => str_repeat('e', 32), 'pk' => 'sealed', 'iv' => 'sealed', 'ct' => 'sealed']];
$ok('outward status advertises approval capability', co_app_relay_status($db, $proposal)['outward'] === true
    && co_app_relay_status($db, $proposal)['approvalProtocol'] === 1);
$ok('restricted and readonly cannot stage', co_app_relay_propose($db, ['jobId' => 'J-RESTRICTED'] + $proposal)['error'] === 'not_allowed'
    && co_app_relay_propose($db, ['jobId' => 'J-READONLY'] + $proposal)['error'] === 'read_only');
$ok('plaintext/forged dispatch never reaches approval provider', co_app_relay_propose($db, $proposal + ['args' => ['query' => 'private']])['error'] === 'bad_envelope'
    && co_app_relay_propose($db, $proposal + ['dispatch' => []])['error'] === 'bad_envelope');
$r = co_app_relay_propose($db, $proposal); $rid = $r['requestId'];
$rows = co_app_requests($db, 'owner'); $q = array_values(array_filter($rows, static fn($x) => $x['id'] === $rid))[0];
$ok('stage persists only metadata with server-generated immutable binding', $r['ok'] && $q['state'] === 'pending'
    && !isset($q['args'], $q['argsView']) && $q['revision'] === $r['revision']
    && $GLOBALS['test_dispatch']['requestId'] === $rid && $GLOBALS['test_dispatch']['actor'] === 'agent');
$ui = ['id' => $rid, 'etag' => $q['etag']];
$ok('other environment cannot inspect card', co_app_request_relay($db, 'sam', false, 'sam@example.org', $ui + ['phase' => 'status'])['error'] === 'not_found');
$ok('old plaintext Run cannot bypass preview', co_app_request_decide($db, 'owner', true, 'owner@example.org', $ui + ['decision' => 'run'])['error'] === 'relay_preview_required');
$ok('stale etag cannot inspect card', co_app_request_relay($db, 'owner', true, 'owner@example.org', ['etag' => 'bad'] + $ui + ['phase' => 'preview'])['error'] === 'etag_mismatch');
$db->query("UPDATE jobs SET state='completed' WHERE job_id='J-OK'");
$s = co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'status']);
$ok('person can review after original job finishes', $s['ok'] && $s['revision'] === $q['revision']);
$pre = co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'preview', 'envelope' => $proposal['envelope']]);
$ok('private preview is ciphertext only and user dispatch bound', $pre['ok'] && $GLOBALS['test_dispatch']['phase'] === 'preview'
    && $GLOBALS['test_dispatch']['actor'] === 'person' && $GLOBALS['test_dispatch']['env'] === 'owner');
$run = co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'run', 'envelope' => $proposal['envelope']]);
$ok('Run mints short-lived bound grant without execution success claim', $run['ok'] && !isset($run['result'])
    && $GLOBALS['test_dispatch']['phase'] === 'run' && $GLOBALS['test_dispatch']['grantExpires'] > time()
    && $GLOBALS['test_dispatch']['grantExpires'] <= time() + 60);
$ok('Run cannot be repeated', co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'run', 'envelope' => $proposal['envelope']])['error'] === 'already_decided');
$ok('running private card remains in inbox after reload', count(array_filter(co_app_requests_inbox($db, 'owner'), static fn($x) => $x['app']['requestId'] === $rid && $x['app']['status'] === 'running')) === 1);
$poll = co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'result', 'envelope' => $proposal['envelope']]);
$ok('person can decrypt result after Run using own fresh key', $poll['ok'] && $GLOBALS['test_dispatch']['phase'] === 'result' && $GLOBALS['test_dispatch']['actor'] === 'person');
$db->query("UPDATE jobs SET state='claimed' WHERE job_id='J-OK'");
$poll = co_app_relay_result($db, ['jobId' => 'J-OK', 'requestId' => $rid, 'envelope' => $proposal['envelope']]);
$ok('agent result is encrypted and reauthorizes live job', $poll['ok'] && $GLOBALS['test_dispatch']['actor'] === 'agent'
    && co_app_relay_result($db, ['jobId' => 'J-READONLY', 'requestId' => $rid])['error'] === 'not_allowed');
$ok('status gives binding for fresh-key encrypted polling', co_app_action_status($db, ['jobId' => 'J-OK', 'requestId' => $rid])['revision'] === $q['revision']);
$db->query("UPDATE modules SET version='2.0.0' WHERE id='relay-test'"); co_modules($db, true);
$ok('module update invalidates pending/result access', co_app_request_relay($db, 'owner', true, 'owner@example.org', $ui + ['phase' => 'status'])['error'] === 'app_changed');
$db->query("UPDATE modules SET version='1.0.0' WHERE id='relay-test'"); co_modules($db, true);
$q2 = co_app_relay_propose($db, $proposal);
$ok('Decline closes proposal card without outward grant', co_app_request_decide($db, 'owner', true, 'owner@example.org', ['id' => $q2['requestId'], 'decision' => 'deny'])['status'] === 'denied');
$ok('staging times out after request deadline', co_app_requests_expire([['state' => 'staging', 'expires' => 1]])[0]['state'] === 'expired');
$man['provides']['actions'][0]['via'] = 'home-relay';
$st = $db->prepare('UPDATE modules SET manifest_json = ? WHERE id = ?');
$st->execute([json_encode($man), 'relay-test']); $st->close(); co_modules($db, true);
co_module_set($db, 'home-relay', false, 'owner');
$ok('disabled relay refused', co_app_relay_status($db, $in)['error'] === 'relay_disabled');
co_module_set($db, 'relay-test', false, 'owner');
$ok('disabled app refused', co_app_relay_status($db, $in)['error'] === 'unknown_relay_action');
echo $fail ? "$fail FAILED\n" : "app-relay-test OK\n";
exit($fail ? 1 : 0);
