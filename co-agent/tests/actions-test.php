<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Schema 18 confirmation cards against a throwaway MariaDB (never production): actions-lib.php filing of read-guard
// events, the "Waiting for you" card, action_decide rights, chat_read byOwner. Driver: tests/actions-test.py (starts the
// server, applies schema.sql, lays out co-agent/secrets/db.json and runs this file with php -d mysqli.default_port=...).
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/actions-lib.php';
require __DIR__ . '/../web/agent/inbox-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static function (string $sql, array $p = []) use ($db): array {
    $st = $db->prepare($sql);
    $st->execute($p);
    $r = $st->get_result();
    $rows = $r ? $r->fetch_all(MYSQLI_ASSOC) : [];
    $st->close();
    return $rows;
};

foreach (['action_requests', 'events', 'jobs', 'chats', 'projects', 'shares', 'kv'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES ('CHAT-20261103000000-AAAA', 'P1', 'Mail chat', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO jobs (job_id, env, project_id, chat_id, state, sha, synced_utc) VALUES ('JOB-1', 'owner', 'P1', 'CHAT-20261103000000-AAAA', 'claimed', 'x', UTC_TIMESTAMP())");
$chat = 'CHAT-20261103000000-AAAA';
$ev = static fn(array $e): array => ['source' => 'guard', 'type' => 'read_guard', 'content' => json_encode($e)];
$held = ['type' => 'risky_action_held', 'scope' => $chat, 'code' => '4821', 'kind' => 'push', 'summary' => 'git push: Push to GitHub',
    'command' => 'git push origin main', 'tainted' => false];

co_actions_from_batch($db, 'JOB-1', ['events' => [
    ['source' => 'agent', 'type' => 'stdout', 'content' => '{}'],
    $ev(['type' => 'injection_suspected', 'scope' => $chat]),
    $ev($held),
]]);
$rows = $q('SELECT * FROM action_requests');
$ok('one request filed', count($rows) === 1 && $rows[0]['code'] === '4821' && $rows[0]['kind'] === 'push' && $rows[0]['env'] === 'owner');
co_actions_from_batch($db, 'JOB-1', ['events' => [$ev(['kind' => 'delete', 'summary' => 'delete: SPEC.md'] + $held)]]);
$rows = $q('SELECT * FROM action_requests');
$acts = json_decode($rows[0]['actions'], true);
$ok('same code extends the request', count($rows) === 1 && count($acts) === 2 && $rows[0]['kind'] === 'delete');
co_actions_from_batch($db, 'JOB-1', ['events' => [$ev(['code' => '12345'] + $held), $ev(['scope' => 'CHAT-20261103000000-BBBB', 'code' => '1111'] + $held)]]);
$ok('bad code and foreign chat ignored', count($q('SELECT id FROM action_requests')) === 1);

// assistant answer asking for the code -> one card, no second "confirm" item for the same chat
$q("INSERT INTO events (event_id, chat_id, seq, type, writer, created_utc, text, data, sha, synced_utc) VALUES
    ('E1', ?, 1, 'user_message', 'owner@example.org', UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE, 'push it', '{}', 'x', UTC_TIMESTAMP()),
    ('E2', ?, 2, 'assistant_message', NULL, UTC_TIMESTAMP(3) + INTERVAL 1 SECOND, 'Подтвердите действие: git push. Ответьте „4821 да“.', '{\"status\":\"completed\"}', 'x', UTC_TIMESTAMP())",
    [$chat, $chat]);
$db->query("UPDATE jobs SET state = 'completed', result = '{}'");
$list = co_inbox_list($db, 'owner', 'owner@example.org');
$cards = array_values(array_filter($list['items'], static fn($i) => $i['kind'] === 'action'));
$same = array_values(array_filter($list['items'], static fn($i) => ($i['chatId'] ?? '') === $chat));
$ok('card listed for the owner', count($cards) === 1 && $cards[0]['code'] === '4821' && $cards[0]['eventId'] === 'E2'
    && $cards[0]['action']['title'] === 'Delete' && count($cards[0]['action']['actions']) === 2);
$ok('no duplicate confirm item', count($same) === 1);
$ok('card unread', $cards[0]['unread'] === true);
$other = co_inbox_list($db, 'owner', 'alex@example.org');
$ok('not listed for another person', count(array_filter($other['items'], static fn($i) => $i['kind'] === 'action')) === 0);

// shared editor cannot decide; owner can
$id = (int)$rows[0]['id'];
$r = co_action_decide($db, 'sam', 'sam@example.org', $id, 'approved');
$ok('editor of another env refused', $r['ok'] === false && $r['status'] === 403);
$r = co_action_decide($db, 'owner', 'owner@example.org', $id, 'approved');
$ok('owner decides', $r['ok'] === true && $r['changed'] === true && $r['code'] === '4821');
$ok('decision alone keeps the card until the message is in the chat', count(array_filter(co_inbox_list($db, 'owner', 'owner@example.org')['items'], static fn($i) => $i['kind'] === 'action')) === 1);
// an editor of a shared project answering with the code does not hide the owner's card
$q("INSERT INTO shares (kind, target_id, env, role, created_utc, added_by) VALUES ('project', 'P1', 'sam', 'editor', UTC_TIMESTAMP(), 'owner@example.org')");
$q("INSERT INTO events (event_id, chat_id, seq, type, writer, author, created_utc, text, data, sha, synced_utc) VALUES
    ('E4', ?, 4, 'user_message', 'sam@example.org', 'owner@example.org', UTC_TIMESTAMP(3) + INTERVAL 3 SECOND, '4821 да', '{}', 'x', UTC_TIMESTAMP())", [$chat]);
$ok('editor answer keeps the card', count(array_filter(co_inbox_list($db, 'owner', 'owner@example.org')['items'], static fn($i) => $i['kind'] === 'action')) === 1);

// the same code held again after a "no" reopens the row; a typed reply with the code hides it
co_actions_from_batch($db, 'JOB-1', ['events' => [$ev($held)]]);
$ok('request reopened', $q('SELECT state FROM action_requests WHERE id = ?', [$id])[0]['state'] === 'pending');
$q("INSERT INTO events (event_id, chat_id, seq, type, writer, created_utc, text, data, sha, synced_utc) VALUES
    ('E3', ?, 3, 'user_message', 'owner@example.org', UTC_TIMESTAMP(3) + INTERVAL 2 SECOND, '4821 да', '{}', 'x', UTC_TIMESTAMP())", [$chat]);
$ok('typed answer hides the card', count(array_filter(co_inbox_list($db, 'owner', 'owner@example.org')['items'], static fn($i) => $i['kind'] === 'action')) === 0);

// chat_read: byOwner only for the env's own person
$q("INSERT INTO events (event_id, chat_id, seq, type, writer, created_utc, text, data, sha, synced_utc) VALUES
    ('E5', ?, 5, 'user_message', NULL, UTC_TIMESTAMP(3) + INTERVAL 4 SECOND, 'old', '{}', 'x', UTC_TIMESTAMP())", [$chat]);
$read = db_chat_read(['chatId' => $chat, 'limit' => 10]);
$by = [];
foreach ($read['messages'] as $m) $by[$m['seq']] = $m['byOwner'];
$ok('byOwner from writer (author forged as owner ignored), no writer in a shared chat = false',
    ($by[3] ?? 'x') === true && ($by[4] ?? 'x') === false && ($by[5] ?? 'x') === false && array_key_exists(2, $by) && $by[2] === null);
$q("DELETE FROM shares");
$read = db_chat_read(['chatId' => $chat, 'limit' => 10]);
$by5 = 'x';
foreach ($read['messages'] as $m) if ($m['seq'] === 5) $by5 = $m['byOwner'];
$ok('no writer in an unshared chat = unknown', $by5 === null);

// expiry
$db->query('UPDATE action_requests SET expires_utc = UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE, created_utc = UTC_TIMESTAMP(3) + INTERVAL 1 HOUR');
$ok('expired request not listed', count(co_actions_list($db, 'owner')) === 0);

echo $fail ? "$fail failed\n" : "all OK\n";
exit($fail ? 1 : 0);
