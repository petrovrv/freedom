<?php
// php tests/server.php  (from the module folder): mail_status / mail_call over the home-relay lib, without a database
declare(strict_types=1);
function co_push_write_atomic(string $p, string $v, int $m = 0600): void { file_put_contents($p . '.t', $v); rename($p . '.t', $p); }
$relay = require __DIR__ . '/../../home-relay/server/ops.php';
$h = require __DIR__ . '/../server/ops.php';
$d = sys_get_temp_dir() . '/mail-test-' . bin2hex(random_bytes(5));
mkdir($d);
$checks = 0;
function check(bool $v, string $what): void { global $checks; if (!$v) throw new Exception("failed: $what"); $checks++; }
$rc = ['stateDir' => $d, 'myEnv' => 'enva', 'owner' => true];
$mail = ['myEnv' => 'enva', 'relay' => [$relay['relay'], $rc]];
$sealed = ['id' => str_repeat('a', 32), 'pk' => base64_encode('pk'), 'iv' => base64_encode('iv'), 'ct' => base64_encode('ciphertext')];
try {
    $st = $h['user']['mail_status']($mail, []);
    check($st['paired'] === false && $st['online'] === false, 'not paired at first');
    check(($h['user']['mail_call']($mail, $sealed)['error'] ?? '') === 'not_paired', 'call without worker');
    $t = $relay['push']['relay_worker_token']($rc, ['env' => 'enva'])['token'];
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'), 'modules' => ['calendar' => ['n' => 1]]]]);
    check($h['user']['mail_status']($mail, [])['paired'] === false, 'worker without the mail handler: not connected');
    check(($h['user']['mail_call']($mail, $sealed)['error'] ?? '') === 'not_served', 'call refused');
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'), 'modules' => ['concierge-mail' => ['bound' => false]]]]);
    $st = $h['user']['mail_status']($mail, []);
    check($st['paired'] === true && $st['online'] === false, 'mailbox bound elsewhere or handler down: offline');
    co_relay_worker($rc, ['env' => 'enva', 'token' => $t, 'nowait' => 1, 'status' => ['pub' => base64_encode('P'),
        'modules' => ['concierge-mail' => ['bound' => true, 'unread' => 5, 'synced' => '2026-11-23T10:00:00Z', 'account' => 'me@example.org']]]]);
    $st = $h['user']['mail_status']($mail, []);
    check($st['paired'] && $st['online'] && $st['unread'] === 5 && $st['account'] === 'me@example.org' && $st['pub'] === base64_encode('P')
        && $st['env'] === 'enva', 'flat status for the page and the tray');
    $q = co_relay_dir($rc, 'enva');
    $t0 = microtime(true);
    $r = $h['user']['mail_call']($mail, $sealed);
    check(($r['error'] ?? '') === 'timeout' && microtime(true) - $t0 >= CO_RELAY_WAIT - 1, 'call goes to the relay queue');
    check(($h['user']['mail_status'](['myEnv' => 'envb', 'relay' => [$relay['relay'], ['myEnv' => 'envb'] + $rc]], [])['paired'] ?? null) === false,
        'another environment sees no worker of enva');
    // 0.12.0 mail_chats: own live chats only, words in the chat or project title
    $rows = [
        ['chat_id' => 'CHAT-20261101000000-AAAA', 'project_id' => 'Freedom', 'title' => 'Почтовый клиент', 'data' => '{}', 'ptitle' => 'Freedom', 'pdata' => '{}'],
        ['chat_id' => 'CHAT-20261101000000-BBBB', 'project_id' => 'Freedom', 'title' => "\u{2937} Ревью почты", 'data' => '{}', 'ptitle' => 'Freedom', 'pdata' => '{}'],
        ['chat_id' => 'CHAT-20261101000000-CCCC', 'project_id' => 'Jobs', 'title' => 'Stadsbader', 'data' => '{"trashedUtc":"2026-11-01"}', 'ptitle' => 'Job search', 'pdata' => '{}'],
        ['chat_id' => 'CHAT-20261101000000-DDDD', 'project_id' => 'Fam', 'title' => 'Family talk', 'data' => '{}', 'ptitle' => 'Home', 'pdata' => '{"family":true}'],
        ['chat_id' => 'CHAT-20261101000000-EEEE', 'project_id' => 'Jobs', 'title' => 'Digital Realty', 'data' => '{"archived":true}', 'ptitle' => 'Job search', 'pdata' => '{}'],
        ['chat_id' => 'CHAT-20261101000000-FFFF', 'project_id' => 'Jobs', 'title' => 'Vulcain meeting', 'data' => '{}', 'ptitle' => 'Job search', 'pdata' => '{}'],
    ];
    $ids = fn(array $r): string => implode(',', array_map(fn($x) => substr($x['chat'], -4), $r['chats']));
    check($ids($h['user']['mail_chats'](['chatRows' => $rows], [])) === 'AAAA,FFFF', 'mail_chats: no agent talks, trash, archive or family room');
    check($ids($h['user']['mail_chats'](['chatRows' => $rows], ['q' => 'JOB vulc'])) === 'FFFF', 'mail_chats: every word in the chat or project title');
    check($ids($h['user']['mail_chats'](['chatRows' => $rows], ['q' => 'почтов'])) === 'AAAA', 'mail_chats: other alphabets, any case');
    check($h['user']['mail_chats'](['myEnv' => ''], ['q' => 'x']) === ['chats' => []], 'mail_chats: no database, no chats');
    echo "PHP checks: $checks passed\n";
} finally {
    exec('rm -rf ' . escapeshellarg($d));
}
