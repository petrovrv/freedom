<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Delete a message / rewrite from here (msgedit-lib.php, CO Web 0.133.0) against a throwaway MariaDB (never production):
// soft hide with data.deleted, the answer goes with its message, a rewrite hides everything from N on, takes the protocol
// back and moves the secretary to N-1, stops running jobs, lists memory/dossier traces; editors only their own messages;
// an older client copy does not bring a hidden message back; restore within 30 days.
// Driver: python tests/actions-test.py --test msgedit-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';
require __DIR__ . '/../web/agent/msgedit-lib.php';
require __DIR__ . '/../cron/protocol-apply.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};

$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-own', 'owner', 'Mine', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc, protocol_seq) VALUES
    ('CHAT-M', 'P-own', 'Messages', '{}', 'x', UTC_TIMESTAMP(3), 6)");

$ev = static function (int $seq, string $type, ?string $job, string $text): array {
    return ['eventId' => sprintf('EVT-%02d', $seq), 'chatId' => 'CHAT-M', 'branchId' => 'BR-main', 'sequence' => $seq,
        'type' => $type, 'jobId' => $job, 'text' => $text, 'createdUtc' => '2026-11-26T10:00:00.000Z'];
};
// #1/#2 owner's turn, #3/#4 guest's turn, #5/#6 owner's turn, #7 owner's message with a running job
$events = [$ev(1, 'user_message', 'J1', 'first question'), $ev(2, 'assistant_message', 'J1', 'first answer'),
    $ev(3, 'user_message', 'J2', 'guest question'), $ev(4, 'assistant_message', 'J2', 'guest answer'),
    $ev(5, 'user_message', 'J3', 'third question'), $ev(6, 'assistant_message', 'J3', 'third answer'),
    $ev(7, 'user_message', 'J4', 'fourth question')];
co_db_put(['events' => $events]);
$db->query("UPDATE events SET writer = 'owner@example.org' WHERE event_id IN ('EVT-01', 'EVT-05', 'EVT-07')");
$db->query("UPDATE events SET writer = 'guest@example.org' WHERE event_id = 'EVT-03'");
foreach ([['J1', 'done', 'owner'], ['J2', 'done', 'guest'], ['J3', 'done', 'owner'], ['J4', 'claimed', 'owner']] as [$j, $state, $actor]) {
    $db->query("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, state, origin, queued_utc, request, sha, synced_utc, actor_env"
        . ($state === 'done' ? ', result' : '') . ") VALUES ('$j', 'home-main', 'owner', 'P-own', 'CHAT-M', 'claude', '$state', 'db',
        UTC_TIMESTAMP(3), '{}', '', UTC_TIMESTAMP(3), '$actor'" . ($state === 'done' ? ", '{}'" : '') . ')');
}
$proto = static function (int $no, int $msg, ?int $closed = null) use ($db): void {
    $st = $db->prepare("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, closed_seq, created_utc, updated_utc)
                        VALUES ('CHAT-M', ?, 'decision', ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
    $st->execute([$no, "entry $no", $msg, $closed === null ? 'active' : 'closed', $closed]);
};
$proto(1, 2, 6); // closed by the third answer
$proto(2, 4);
$proto(3, 6);
$db->query("INSERT INTO memory_candidates (project_id, chat_id, msg_seq, tag, text, norm_sha, day, status, created_utc)
            VALUES ('P-own', 'CHAT-M', 6, 'fact', 'learnt from the third answer', SHA1('a'), CURDATE(), 'pending', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO dossier (env, section, subject, text, source_chat, source_msg, created_utc, updated_utc)
            VALUES ('owner', 'state', 'topic', 'said in the third question', 'CHAT-M', 5, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");

$live = static fn(): array => array_map('intval', array_column(share_q($db,
    "SELECT seq FROM events WHERE chat_id = 'CHAT-M' AND JSON_EXTRACT(data, '$.deleted') IS NULL ORDER BY seq", []), 'seq'));

// rule 4: a guest (editor) cannot touch the owner's turn, can delete their own
$r = msgedit_hide($db, 'CHAT-M', 1, 'pair', 'editor', 'guest@example.org', 'guest');
$ok('editor: owner\'s message refused', ($r['error'] ?? '') === 'not_yours' && $live() === [1, 2, 3, 4, 5, 6, 7]);
$r = msgedit_hide($db, 'CHAT-M', 4, 'answer', 'editor', 'guest@example.org', 'guest');
$ok('editor: answer to own message hidden', ($r['hidden'] ?? []) === ['EVT-04'] && $live() === [1, 2, 3, 5, 6, 7]);
$ok('its protocol entry taken out', !share_q($db, "SELECT 1 FROM protocol WHERE chat_id = 'CHAT-M' AND entry_no = 2", []));
$ok('viewer refused', (msgedit_hide($db, 'CHAT-M', 3, 'pair', 'viewer', 'guest@example.org', 'guest')['error'] ?? '') === 'forbidden');
$ok('rewrite over another person\'s message refused to an editor',
    (msgedit_hide($db, 'CHAT-M', 1, 'rewrite', 'editor', 'guest@example.org', 'guest')['error'] ?? '') === 'not_yours');

// an older copy written by a client does not unhide
co_db_put(['events' => [$ev(4, 'assistant_message', 'J2', 'guest answer (old copy)')]]);
$ok('older client copy keeps the deletion', $live() === [1, 2, 3, 5, 6, 7]);

// restore: the person who deleted, within 30 days, with the protocol entry
$ok('another editor cannot restore', (msgedit_restore($db, 'CHAT-M', $r['batch'], 'editor', 'other@example.org')['error'] ?? '') === 'forbidden');
$ok('restored', (msgedit_restore($db, 'CHAT-M', $r['batch'], 'editor', 'guest@example.org')['restored'] ?? 0) === 1 && $live() === [1, 2, 3, 4, 5, 6, 7]);
$ok('protocol entry back', (bool)share_q($db, "SELECT 1 FROM protocol WHERE chat_id = 'CHAT-M' AND entry_no = 2 AND msg_seq = 4", []));

// owner deletes the guest's pair (message with its answer)
$r = msgedit_hide($db, 'CHAT-M', 3, 'pair', 'owner', 'owner@example.org', 'owner');
$ok('owner: pair hidden', $r['hidden'] === ['EVT-03', 'EVT-04'] && $live() === [1, 2, 5, 6, 7]);
$ok('a pair needs a user message', (msgedit_hide($db, 'CHAT-M', 6, 'pair', 'owner', 'owner@example.org', 'owner')['error'] ?? '') === 'not_found');
$db->query("UPDATE events SET data = JSON_SET(data, '$.deleted.utc', '2026-01-01T00:00:00.000Z') WHERE event_id IN ('EVT-03', 'EVT-04')");
$ok('restore after 30 days refused', (msgedit_restore($db, 'CHAT-M', $r['batch'], 'owner', 'owner@example.org')['error'] ?? '') === 'expired');

// rewrite from #5: #5, #6, #7 forgotten, J4 stopped, protocol back to #4, closed entry reopened, traces listed
$r = msgedit_hide($db, 'CHAT-M', 5, 'rewrite', 'owner', 'owner@example.org', 'owner');
$ok('rewrite hides from #5 on', $r['seqs'] === [5, 6, 7] && $live() === [1, 2]);
$ok('running job of a forgotten message stopped', $r['stopped'] === ['J4']
    && (int)share_q($db, "SELECT cancel_requested FROM jobs WHERE job_id = 'J4'", [])[0]['cancel_requested'] === 1);
$ok('forgotten answers named (undo of files)', $r['answers'] === ['J3']);
$ok('protocol of the forgotten messages taken out', !share_q($db, "SELECT 1 FROM protocol WHERE chat_id = 'CHAT-M' AND msg_seq >= 5", []));
$ok('entry closed by a forgotten answer reopened', share_q($db, "SELECT status, closed_seq FROM protocol WHERE chat_id = 'CHAT-M' AND entry_no = 1", [])[0]
    === ['status' => 'active', 'closed_seq' => null]);
$ok('secretary cursor at #4', (int)share_q($db, "SELECT protocol_seq FROM chats WHERE chat_id = 'CHAT-M'", [])[0]['protocol_seq'] === 4);
$ok('memory trace listed', count($r['traces']['memory']) === 1 && $r['traces']['memory'][0]['msg'] === 6);
$ok('dossier trace listed', count($r['traces']['dossier']) === 1 && $r['traces']['dossier'][0]['msg'] === 5);
$ok('rewrite needs a user message', (msgedit_hide($db, 'CHAT-M', 2, 'rewrite', 'owner', 'owner@example.org', 'owner')['error'] ?? '') === 'not_found');
$ok('chat_read leaves hidden messages out', count(share_q($db, "SELECT 1 FROM events WHERE chat_id = 'CHAT-M' AND type IN ('user_message','assistant_message')
    AND JSON_EXTRACT(data, '$.deleted') IS NULL", [])) === 2);
$ok('no transaction left open', (int)share_q($db, 'SELECT @@in_transaction AS t', [])[0]['t'] === 0);

// review 1: data.deleted is the server's - a client cannot hide (or unhide) through put_user
$forged = $ev(1, 'user_message', 'J1', 'first question');
$forged['deleted'] = ['utc' => '2026-11-26T12:00:00.000Z', 'by' => 'guest@example.org', 'batch' => 'DEL-000000000000', 'mode' => 'pair', 'at' => 1];
co_db_put(['events' => [$forged]]);
$ok('forged deleted from a client ignored', $live() === [1, 2]);
$unhide = $ev(6, 'assistant_message', 'J3', 'third answer');
$unhide['deleted'] = null;
co_db_put(['events' => [$unhide]]);
$ok('a client cannot unhide either', $live() === [1, 2]);
// a late answer of the stopped job J4 (server finalize or a CO Web tab) stays hidden with its message
co_db_put(['events' => [$ev(8, 'assistant_message', 'J4', 'late answer')]]);
$late = json_decode((string)share_q($db, "SELECT data FROM events WHERE event_id = 'EVT-08'", [])[0]['data'], true);
$ok('late answer of a forgotten message hidden in its batch', $live() === [1, 2] && ($late['deleted']['batch'] ?? '') === $r['batch']);

// review 3: a secretary batch read before the rewrite is dropped, the cursor stays
$cursor = static fn(): int => (int)share_q($db, "SELECT protocol_seq FROM chats WHERE chat_id = 'CHAT-M'", [])[0]['protocol_seq'];
$a = co_protocol_apply($db, 'CHAT-M', [5 => [], 6 => []], [], 'test', 6, null, 6);
$ok('stale batch (cursor moved back) dropped', ($a['stale'] ?? false) === true && $cursor() === 4);
$a = co_protocol_apply($db, 'CHAT-M', [5 => [], 6 => []], [], 'test', 6, null, 4);
$ok('batch with hidden messages dropped', ($a['stale'] ?? false) === true && $cursor() === 4);
co_db_put(['events' => [$ev(9, 'user_message', 'J5', 'rewritten question'), $ev(10, 'assistant_message', 'J5', 'new answer')]]);
$a = co_protocol_apply($db, 'CHAT-M', [9 => [], 10 => []], [], 'test', 10, null, 4);
$ok('fresh batch after the rewrite applied (hidden ones in between not read)', !isset($a['stale']) && $cursor() === 10);

// review 4: a family member only in chats of their own environment or the family room
$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-kid', 'kid', 'Kid', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES
    ('CHAT-K', 'P-kid', 'Kid chat', '{}', 'x', UTC_TIMESTAMP(3)), ('CHAT-F', 'P-own', 'Family', '{\"family\":true}', 'x', UTC_TIMESTAMP(3))");
$ok('member: own chat allowed', msgedit_env_allows($db, 'CHAT-K', false, 'kid'));
$ok('member: another person\'s shared chat refused', !msgedit_env_allows($db, 'CHAT-M', false, 'kid'));
$ok('member: family room allowed', msgedit_env_allows($db, 'CHAT-F', false, 'kid'));
$ok('workbench owner: any chat', msgedit_env_allows($db, 'CHAT-K', true, 'owner'));

// review 2, P1: co_db_put takes the chat row (the lock of hide/restore) before it reads the marks - while a hide holds
// it in another connection, a write of the chat's events waits
$port = (int)ini_get('mysqli.default_port');
$other = new mysqli('127.0.0.1', 't', 't', 'co', $port);
$db->query('SET SESSION innodb_lock_wait_timeout = 1');
$other->begin_transaction();
$other->query("SELECT data FROM chats WHERE chat_id = 'CHAT-M' FOR UPDATE");
$waited = false;
try {
    co_db_put(['events' => [$ev(11, 'assistant_message', 'J9', 'answer during a hide')]]);
} catch (mysqli_sql_exception $e) {
    $waited = $e->getCode() === 1205;
}
$other->rollback();
$ok('event write waits for a running hide', $waited && !share_q($db, "SELECT 1 FROM events WHERE event_id = 'EVT-11'", []));
$db->query('SET SESSION innodb_lock_wait_timeout = 50');

// review 2, P2: a rewrite reopens A that B (a forgotten message) had superseded; Restore closes A again with B's new
// number (its old one was taken meanwhile) and puts the secretary back where it was
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc, protocol_seq) VALUES ('CHAT-P', 'P-own', 'Proto', '{}', 'x', UTC_TIMESTAMP(3), 4)");
$pe = static fn(int $seq, string $type): array => ['eventId' => sprintf('EVT-P%02d', $seq), 'chatId' => 'CHAT-P', 'branchId' => 'BR-main',
    'sequence' => $seq, 'type' => $type, 'jobId' => 'JP' . intdiv($seq + 1, 2), 'text' => "p$seq", 'createdUtc' => '2026-11-26T10:00:00.000Z'];
co_db_put(['events' => [$pe(1, 'user_message'), $pe(2, 'assistant_message'), $pe(3, 'user_message'), $pe(4, 'assistant_message')]]);
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, replaced_by, closed_seq, created_utc, updated_utc) VALUES
    ('CHAT-P', 1, 'decision', 'A', 2, 'superseded', 2, 4, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)),
    ('CHAT-P', 2, 'decision', 'B', 4, 'active', NULL, NULL, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
$pRow = static fn(int $no): ?array => share_q($db, "SELECT status, replaced_by, closed_seq, text FROM protocol WHERE chat_id = 'CHAT-P' AND entry_no = ?", [$no])[0] ?? null;
$pCur = static fn(): int => (int)share_q($db, "SELECT protocol_seq FROM chats WHERE chat_id = 'CHAT-P'", [])[0]['protocol_seq'];
$rw = msgedit_hide($db, 'CHAT-P', 3, 'rewrite', 'owner', 'owner@example.org', 'owner');
$ok('rewrite: A reopened, B out, cursor at #2', $pRow(1)['status'] === 'active' && $pRow(2) === null && $pCur() === 2);
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, created_utc, updated_utc)
            VALUES ('CHAT-P', 2, 'fact', 'C', 2, 'active', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
msgedit_restore($db, 'CHAT-P', $rw['batch'], 'owner', 'owner@example.org');
$b = share_q($db, "SELECT entry_no FROM protocol WHERE chat_id = 'CHAT-P' AND text = 'B'", []);
$ok('restore: B back under a free number', $b && (int)$b[0]['entry_no'] === 3);
$ok('restore: A superseded by B again (not two active decisions)', $pRow(1) == ['status' => 'superseded', 'replaced_by' => 3, 'closed_seq' => 4, 'text' => 'A']);
$ok('restore: cursor back to #4 (restored messages not read twice)', $pCur() === 4);
// a delete (not a rewrite) of B's turn reopens A too, and its restore closes A again
$dl = msgedit_hide($db, 'CHAT-P', 3, 'pair', 'owner', 'owner@example.org', 'owner');
$ok('delete: A reopened, cursor untouched', $pRow(1)['status'] === 'active' && $pCur() === 4);
msgedit_restore($db, 'CHAT-P', $dl['batch'], 'owner', 'owner@example.org');
$ok('delete restored: A superseded by B', $pRow(1)['status'] === 'superseded' && (int)$pRow(1)['replaced_by'] === 3);

// review 3, P1: A superseded by B; delete B (A reopens), delete A, the secretary puts C under A's freed number;
// restoring B must not take C for A
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc, protocol_seq) VALUES ('CHAT-Q', 'P-own', 'Q', '{}', 'x', UTC_TIMESTAMP(3), 4)");
$qe = static fn(int $seq, string $type): array => ['eventId' => sprintf('EVT-Q%02d', $seq), 'chatId' => 'CHAT-Q', 'branchId' => 'BR-main',
    'sequence' => $seq, 'type' => $type, 'jobId' => 'JQ' . intdiv($seq + 1, 2), 'text' => "q$seq", 'createdUtc' => '2026-11-26T10:00:00.000Z'];
co_db_put(['events' => [$qe(1, 'user_message'), $qe(2, 'assistant_message'), $qe(3, 'user_message'), $qe(4, 'assistant_message')]]);
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, replaced_by, closed_seq, created_utc, updated_utc) VALUES
    ('CHAT-Q', 1, 'decision', 'A', 2, 'superseded', 2, 4, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)),
    ('CHAT-Q', 2, 'decision', 'B', 4, 'active', NULL, NULL, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
$qBy = static fn(string $text): ?array => share_q($db, "SELECT entry_no, status, replaced_by, closed_seq FROM protocol WHERE chat_id = 'CHAT-Q' AND text = ?", [$text])[0] ?? null;
$dB = msgedit_hide($db, 'CHAT-Q', 3, 'pair', 'owner', 'owner@example.org', 'owner');
$dA = msgedit_hide($db, 'CHAT-Q', 1, 'pair', 'owner', 'owner@example.org', 'owner');
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, created_utc, updated_utc)
            VALUES ('CHAT-Q', 1, 'fact', 'C', 5, 'active', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
msgedit_restore($db, 'CHAT-Q', $dB['batch'], 'owner', 'owner@example.org');
$ok('restore of B leaves the new entry C under A\'s old number alone', $qBy('C') == ['entry_no' => 1, 'status' => 'active', 'replaced_by' => null, 'closed_seq' => null]);
msgedit_restore($db, 'CHAT-Q', $dA['batch'], 'owner', 'owner@example.org');
$ok('A comes back under a free number', (int)$qBy('A')['entry_no'] === 3 && (int)$qBy('B')['entry_no'] === 2);

// review 3, P2: a rewrite hides both A and B; their numbers are taken meanwhile; Restore renumbers both and A's link
// follows B's new number
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc, protocol_seq) VALUES ('CHAT-R2', 'P-own', 'R2', '{}', 'x', UTC_TIMESTAMP(3), 4)");
$re = static fn(int $seq, string $type): array => ['eventId' => sprintf('EVT-R%02d', $seq), 'chatId' => 'CHAT-R2', 'branchId' => 'BR-main',
    'sequence' => $seq, 'type' => $type, 'jobId' => 'JR' . intdiv($seq + 1, 2), 'text' => "r$seq", 'createdUtc' => '2026-11-26T10:00:00.000Z'];
co_db_put(['events' => [$re(1, 'user_message'), $re(2, 'assistant_message'), $re(3, 'user_message'), $re(4, 'assistant_message')]]);
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, replaced_by, closed_seq, created_utc, updated_utc) VALUES
    ('CHAT-R2', 1, 'decision', 'A', 2, 'superseded', 2, 4, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)),
    ('CHAT-R2', 2, 'decision', 'B', 4, 'active', NULL, NULL, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
$rBy = static fn(string $text): ?array => share_q($db, "SELECT entry_no, status, replaced_by FROM protocol WHERE chat_id = 'CHAT-R2' AND text = ?", [$text])[0] ?? null;
$rr = msgedit_hide($db, 'CHAT-R2', 1, 'rewrite', 'owner', 'owner@example.org', 'owner');
$db->query("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, status, created_utc, updated_utc) VALUES
    ('CHAT-R2', 1, 'fact', 'X', 5, 'active', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)), ('CHAT-R2', 2, 'fact', 'Y', 6, 'active', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))");
msgedit_restore($db, 'CHAT-R2', $rr['batch'], 'owner', 'owner@example.org');
$a2 = $rBy('A');
$b2 = $rBy('B');
$ok('both renumbered, A superseded by B\'s new number', $a2 && $b2 && (int)$a2['entry_no'] === 3 && (int)$b2['entry_no'] === 4
    && $a2['status'] === 'superseded' && (int)$a2['replaced_by'] === 4);
$ok('entries X and Y untouched', $rBy('X')['status'] === 'active' && $rBy('Y')['status'] === 'active' && $rBy('Y')['replaced_by'] === null);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
