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
$ok('relay outward status/call refused', co_app_relay_status($db, ['action' => 'send'] + $in)['error'] === 'relay_approval_unavailable'
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

// The core resolves an arbitrary trusted transport module through the same contract.
$provider = json_decode((string)file_get_contents(__DIR__ . '/../modules/home-relay/module.json'), true);
$provider['id'] = 'other-relay';
$provider['server']['ops'] = ['user' => [], 'host' => [], 'push' => []];
$provider['server']['routes'] = [];
$code = <<<'PHP'
<?php
return ['relay' => [
    'status' => static fn(array $c, string $m): array => ['online' => true, 'served' => true,
        'pub' => 'fixture-public-key', 'status' => ['relayAppProtocol' => 1]],
    'appCall' => static fn(array $c, string $m, array $in, array $d): array => ['error' => 'fixture_transport_stopped'],
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
$man['provides']['actions'][0]['via'] = 'home-relay';
$st = $db->prepare('UPDATE modules SET manifest_json = ? WHERE id = ?');
$st->execute([json_encode($man), 'relay-test']); $st->close(); co_modules($db, true);
co_module_set($db, 'home-relay', false, 'owner');
$ok('disabled relay refused', co_app_relay_status($db, $in)['error'] === 'relay_disabled');
co_module_set($db, 'relay-test', false, 'owner');
$ok('disabled app refused', co_app_relay_status($db, $in)['error'] === 'unknown_relay_action');
echo $fail ? "$fail FAILED\n" : "app-relay-test OK\n";
exit($fail ? 1 : 0);
