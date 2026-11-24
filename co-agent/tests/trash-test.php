<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// CO trash on the server (Freedom F15.2) against a throwaway MariaDB (never production): a chat with trashedUtc gets no
// new job (job_submit and the auto-switch go through share_job_insert_untrashed, check and INSERT under the chat row
// lock), and the 30-day clean-up (delete_chat ifTrashedUtc) holds the same row, so a job and the clean-up never cross.
// Driver: python tests/actions-test.py --test trash-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};

$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-own', 'owner', 'Mine', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES
    ('CHAT-A', 'P-own', 'Plain', '{}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-T', 'P-own', 'Trashed', '{\"archived\":true,\"trashedUtc\":\"2026-11-24T08:00:00.000Z\"}', 'x', UTC_TIMESTAMP(3)),
    ('CHAT-R', 'P-own', 'Restored', '{\"archived\":false}', 'x', UTC_TIMESTAMP(3))");

$ok('trashedUtc of a trashed chat', share_chat_trashed($db, 'CHAT-T') === '2026-11-24T08:00:00.000Z');
$ok('plain chat: empty', share_chat_trashed($db, 'CHAT-A') === '');
$ok('no row: null', share_chat_trashed($db, 'CHAT-none') === null);

$insert = static function (string $jobId, string $chatId) use ($db): callable {
    return static function () use ($db, $jobId, $chatId): void {
        $st = $db->prepare("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, state, origin, queued_utc, request, sha, synced_utc)
                            VALUES (?, 'site', 'owner', 'P-own', ?, 'claude', 'inbox', 'db', UTC_TIMESTAMP(3), '{}', '', UTC_TIMESTAMP(3))");
        $st->execute([$jobId, $chatId]);
        $st->close();
    };
};
$jobs = static fn(string $chatId): int => (int)share_q($db, 'SELECT COUNT(*) AS n FROM jobs WHERE chat_id = ?', [$chatId])[0]['n'];

$ok('job for a plain chat queued', share_job_insert_untrashed($db, 'CHAT-A', $insert('J-A', 'CHAT-A')) === true && $jobs('CHAT-A') === 1);
$ok('job for a trashed chat refused', share_job_insert_untrashed($db, 'CHAT-T', $insert('J-T', 'CHAT-T')) === false && $jobs('CHAT-T') === 0);
$ok('job for a chat not yet in the database queued', share_job_insert_untrashed($db, 'CHAT-new', $insert('J-N', 'CHAT-new')) === true);
$dup = false;
try {
    share_job_insert_untrashed($db, 'CHAT-A', $insert('J-A', 'CHAT-A'));
} catch (mysqli_sql_exception $e) {
    $dup = $e->getCode() === 1062;
}
$ok('duplicate job id: error passed on, transaction closed', $dup && share_q($db, 'SELECT @@in_transaction AS t', [])[0]['t'] == 0);

// inside a caller's transaction the guard uses that transaction (no silent commit of the outer one)
$db->begin_transaction();
$ok('inside an open transaction: queued', share_job_insert_untrashed($db, 'CHAT-A', $insert('J-O', 'CHAT-A')) === true
    && (int)share_q($db, 'SELECT @@in_transaction AS t', [])[0]['t'] === 1);
$db->rollback();
$ok('the outer rollback takes the job back', (int)share_q($db, "SELECT COUNT(*) AS n FROM jobs WHERE job_id = 'J-O'", [])[0]['n'] === 0);

$ok('active jobs counted', share_chat_active_jobs($db, 'CHAT-A') === 1);
$db->query("UPDATE jobs SET state = 'done', result = '{}' WHERE job_id = 'J-A'");
$ok('finished job not active', share_chat_active_jobs($db, 'CHAT-A') === 0);

// the row lock: while the clean-up holds a trashed chat, a second connection that wants to queue a job for it waits
$port = (int)ini_get('mysqli.default_port');
$other = new mysqli('127.0.0.1', 't', 't', 'co', $port);
$other->query('SET SESSION innodb_lock_wait_timeout = 1');
$db->begin_transaction();
$ok('clean-up reads the trash mark under the lock', share_chat_trashed($db, 'CHAT-T', true) === '2026-11-24T08:00:00.000Z');
$waited = false;
try {
    $other->begin_transaction();
    $other->query("SELECT data FROM chats WHERE chat_id = 'CHAT-T' FOR UPDATE");
} catch (mysqli_sql_exception $e) {
    $waited = $e->getCode() === 1205; // lock wait timeout
}
$other->rollback();
$ok('a concurrent job check waits for the clean-up', $waited);
$db->rollback();
$other->begin_transaction();
$r = $other->query("SELECT data FROM chats WHERE chat_id = 'CHAT-T' FOR UPDATE");
$ok('after the clean-up the check goes on', $r !== false && $r->num_rows === 1);
$other->rollback();

// restore then a job: the clean-up with the old mark keeps the chat; a job queued before trashing keeps it too
$db->query("UPDATE chats SET data = '{\"archived\":false}' WHERE chat_id = 'CHAT-T'");
$ok('restored chat takes a job', share_job_insert_untrashed($db, 'CHAT-T', $insert('J-T2', 'CHAT-T')) === true);
$ok('restored chat no longer matches the old mark', share_chat_trashed($db, 'CHAT-T') !== '2026-11-24T08:00:00.000Z');
$db->query("UPDATE chats SET data = '{\"archived\":true,\"trashedUtc\":\"2026-11-24T09:00:00.000Z\"}' WHERE chat_id = 'CHAT-T'");
$ok('trashed again with a running job: clean-up would keep it', share_chat_trashed($db, 'CHAT-T') === '2026-11-24T09:00:00.000Z'
    && share_chat_active_jobs($db, 'CHAT-T') === 1);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
