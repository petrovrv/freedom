<?php
// php tests/server.php  (from the module folder): the postman ops without a database or web server
declare(strict_types=1);
function co_push_write_atomic(string $p, string $v, int $m = 0600): void { file_put_contents($p . '.t', $v); rename($p . '.t', $p); }
$h = require __DIR__ . '/../server/ops.php';
$d = sys_get_temp_dir() . '/mail-test-' . bin2hex(random_bytes(5));
mkdir($d);
$checks = 0;
function check(bool $v, string $what): void { global $checks; if (!$v) throw new Exception("failed: $what"); $checks++; }
$enva = ['stateDir' => $d, 'myEnv' => 'enva', 'owner' => true];
$envb = ['stateDir' => $d, 'myEnv' => 'envb', 'owner' => false];
$id = str_repeat('a', 32);
$sealed = ['id' => $id, 'pk' => base64_encode('pk'), 'iv' => base64_encode('iv'), 'ct' => base64_encode('ciphertext')];
try {
    check(($h['user']['mail_status']($enva, [])['paired'] ?? null) === false, 'not paired at first');
    check(($h['user']['mail_call']($enva, $sealed)['error'] ?? '') === 'not_paired', 'call without worker');
    check(($h['push']['mail_worker_token']($enva, ['env' => '../x'])['error'] ?? '') === 'bad_env', 'env checked');
    $t = $h['push']['mail_worker_token']($enva, ['env' => 'enva'])['token'];
    check(strlen($t) === 64 && !str_contains((string)file_get_contents("$d/workers.json"), $t), 'token shown once, only its hash kept');
    check(($h['user']['mail_status']($enva, [])['online'] ?? null) === false, 'paired but offline');
    check(co_mail_worker($enva, ['env' => 'enva', 'token' => 'wrong'])[0] === 403, 'wrong token refused');
    check(co_mail_worker($enva, ['env' => 'envb', 'token' => $t])[0] === 403, 'token of enva refused for envb');
    [$code, $ans] = co_mail_worker($enva, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'), 'unread' => 7]]);
    check($code === 200 && $ans['requests'] === [], 'worker online, nothing to do');
    $st = $h['user']['mail_status']($enva, []);
    check($st['online'] === true && $st['unread'] === 7 && $st['env'] === 'enva', 'status for the page');
    check(($h['user']['mail_status']($envb, [])['paired'] ?? null) === false, 'envb sees no worker of enva');
    check(($h['user']['mail_call']($enva, ['id' => 'x'] + $sealed)['error'] ?? '') === 'bad_request', 'bad id refused');
    // a request in the queue (what mail_call writes), taken by the worker, answered
    $q = co_mail_dir($enva, 'enva');
    co_push_write_atomic("$q/$id.req", json_encode($sealed));
    [, $ans] = co_mail_worker($enva, ['env' => 'enva', 'token' => $t, 'nowait' => 1]);
    check(count($ans['requests']) === 1 && $ans['requests'][0]['ct'] === $sealed['ct'] && is_file("$q/$id.taken"), 'worker takes the request');
    check(co_mail_dir($envb, 'envb') !== $q && !glob(co_mail_dir($envb, 'envb') . '/*.req'), 'queues are per environment');
    co_mail_worker($enva, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'replies' => [['id' => $id, 'iv' => base64_encode('i2'), 'ct' => base64_encode('answer')],
        ['id' => str_repeat('b', 32), 'iv' => base64_encode('i'), 'ct' => base64_encode('forged')]]]);
    check(json_decode((string)file_get_contents("$q/$id.res"), true)['ct'] === base64_encode('answer') && !is_file("$q/$id.taken"), 'answer stored for the page');
    check(!is_file("$q/" . str_repeat('b', 32) . '.res'), 'no answer for a request that was not taken');
    // the page call returns the waiting answer and removes it
    unlink("$q/$id.res");
    co_push_write_atomic("$q/$id.res", json_encode(['iv' => 'aQ==', 'ct' => 'YQ==']));
    $id2 = str_repeat('c', 32);
    touch("$q/$id.res", time() - 120);
    co_mail_sweep($q);
    check(!is_file("$q/$id.res"), 'old items are swept');
    $t0 = microtime(true);
    $r = $h['user']['mail_call']($enva, ['id' => $id2] + $sealed);
    check(($r['error'] ?? '') === 'timeout' && !is_file("$q/$id2.req") && microtime(true) - $t0 >= CO_MAIL_WAIT - 1, 'unanswered call times out and cleans up');
    // a claimed id cannot be claimed again, even when a new request with the same id appears
    co_push_write_atomic("$q/$id2.req", json_encode(['id' => $id2] + $sealed));
    touch("$q/$id2.taken");
    [, $ans] = co_mail_worker($enva, ['env' => 'enva', 'token' => $t, 'nowait' => 1]);
    check($ans['requests'] === [] && is_file("$q/$id2.req"), 'no claim over an existing .taken');
    // at most CO_MAIL_PENDING waiting requests per environment
    for ($i = 0; $i < CO_MAIL_PENDING; $i++) touch("$q/" . str_repeat((string)$i, 32) . '.req');
    check(($h['user']['mail_call']($enva, ['id' => str_repeat('d', 32)] + $sealed)['error'] ?? '') === 'busy', 'busy when too many wait');
    foreach (glob("$q/*.{req,taken}", GLOB_BRACE) as $f) unlink($f);
    // the worker poll releases the database while it waits
    $released = false;
    co_mail_worker($enva + ['release' => function () use (&$released) { $released = true; }], ['env' => 'enva', 'token' => $t, 'nowait' => 1]);
    check(!$released, 'no release without waiting');
    check(($h['push']['mail_worker_token']($enva, ['env' => 'enva', 'revoke' => 1])['revoked'] ?? '') === 'enva', 'revoke');
    check(co_mail_worker($enva, ['env' => 'enva', 'token' => $t])[0] === 403, 'revoked token refused');
    echo "PHP checks: $checks passed\n";
} finally {
    exec('rm -rf ' . escapeshellarg($d));
}
