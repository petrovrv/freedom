<?php
// php tests/server.php  (from the module folder): cal_status / cal_call over the home-relay lib, without a database
declare(strict_types=1);
function co_push_write_atomic(string $p, string $v, int $m = 0600): void { file_put_contents($p . '.t', $v); rename($p . '.t', $p); }
$relay = require __DIR__ . '/../../home-relay/server/ops.php';
$h = require __DIR__ . '/../server/ops.php';
$d = sys_get_temp_dir() . '/cal-test-' . bin2hex(random_bytes(5));
mkdir($d);
$checks = 0;
function check(bool $v, string $what): void { global $checks; if (!$v) throw new Exception("failed: $what"); $checks++; }
$rc = ['stateDir' => $d, 'myEnv' => 'enva', 'owner' => true];
$cal = ['myEnv' => 'enva', 'relay' => [$relay['relay'], $rc]];
$sealed = ['id' => str_repeat('a', 32), 'pk' => base64_encode('pk'), 'iv' => base64_encode('iv'), 'ct' => base64_encode('ciphertext')];
try {
    $st = $h['user']['cal_status']($cal, []);
    check($st['paired'] === false && $st['online'] === false, 'not paired at first');
    check(($h['user']['cal_call']($cal, $sealed)['error'] ?? '') === 'not_paired', 'call without worker');
    $t = $relay['push']['relay_worker_token']($rc, ['env' => 'enva'])['token'];
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'), 'modules' => ['concierge-mail' => ['bound' => true]]]]);
    check($h['user']['cal_status']($cal, [])['paired'] === false, 'worker without the calendar handler: not connected');
    check(($h['user']['cal_call']($cal, $sealed)['error'] ?? '') === 'not_served', 'call refused');
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'), 'modules' => ['calendar' => ['bound' => false]]]]);
    $st = $h['user']['cal_status']($cal, []);
    check($st['paired'] === true && $st['online'] === false, 'calendar bound elsewhere: offline');
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'),
        'modules' => ['calendar' => ['bound' => true, 'auth' => 'ok', 'next_start' => '2026-11-24T09:00:00Z', 'next_end' => 'evil<script>',
            'conflicts' => 2, 'synced' => '2026-11-23T18:00:00Z', 'account' => 'me@example.org']]]]);
    $st = $h['user']['cal_status']($cal, []);
    check($st['paired'] && $st['online'] && $st['nextStart'] === '2026-11-24T09:00:00Z' && $st['nextEnd'] === '' && $st['conflicts'] === 2
        && $st['auth'] === 'ok' && $st['account'] === 'me@example.org' && $st['pub'] === base64_encode('P'), 'flat status for the page and the tray');
    check(!array_key_exists('next_title', $st), 'no titles through the site');
    check(($h['user']['cal_status'](['myEnv' => 'envb', 'relay' => [$relay['relay'], ['myEnv' => 'envb'] + $rc]], [])['paired'] ?? null) === false,
        'another environment sees no worker of enva');
    echo "PHP checks: $checks passed\n";
} finally {
    exec('rm -rf ' . escapeshellarg($d));
}
