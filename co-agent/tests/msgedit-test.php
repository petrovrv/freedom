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

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
