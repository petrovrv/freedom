<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// D 12.11.2026 (schema 25) against a throwaway MariaDB (never production): one answer per job and chat, whoever writes it
// (open CO Web tabs, finalize-lib.php, a retried job_result), and the server's "job finished" push of site jobs, once.
// Driver: python tests/actions-test.py --test answer-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/finalize-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$count = static function (string $sql, array $p = []) use ($db): int {
    $st = $db->prepare($sql);
    $st->execute($p);
    return (int)$st->get_result()->fetch_row()[0];
};

$n = 0;
$chat = static function (string $title = 'Test chat') use (&$n): string {
    $id = 'CHAT-D-' . (++$n);
    co_db_put(['chats' => [['chatId' => $id, 'projectId' => 'P', 'title' => $title, 'rootBranchId' => 'BR-root']]]);
    return $id;
};
$ev = static function (string $chatId, string $eventId, string $type, string $jobId, int $seq, string $text = 'pong') : array {
    return ['schemaVersion' => 1, 'eventId' => $eventId, 'sequence' => $seq, 'createdUtc' => gmdate('Y-m-d\TH:i:s.000\Z'),
        'chatId' => $chatId, 'branchId' => 'BR-root', 'type' => $type, 'jobId' => $jobId, 'hostId' => 'site', 'agent' => 'claude',
        'status' => 'completed', 'text' => $text];
};
$answers = static fn(string $chatId, string $jobId): int =>
    $count("SELECT COUNT(*) FROM events WHERE chat_id = ? AND job_id = ? AND type = 'assistant_message'", [$chatId, $jobId]);

// ---------- co_db_put: the first answer wins ----------
$c = $chat();
co_db_put(['events' => [$ev($c, 'EVT-D-U1', 'user_message', 'JOB-D-1', 1, 'reply pong')]]);
$r1 = co_db_put(['events' => [$ev($c, 'EVT-D-A1', 'assistant_message', 'JOB-D-1', 2)]]);
$ok('first answer inserted', $r1['inserted'] === 1 && $r1['duplicates'] === []);
$r2 = co_db_put(['events' => [$ev($c, 'EVT-D-A2', 'assistant_message', 'JOB-D-1', 2)]]);
$ok('second copy (other tab) dropped', $r2['inserted'] === 0 && count($r2['duplicates']) === 1
    && $r2['duplicates'][0]['id'] === 'EVT-D-A2' && $r2['duplicates'][0]['existing'] === 'EVT-D-A1');
$ok('one answer in the chat', $answers($c, 'JOB-D-1') === 1);
$e = $ev($c, 'EVT-D-A1', 'assistant_message', 'JOB-D-1', 2, 'pong (edited)');
$r3 = co_db_put(['events' => [$e]]);
$ok('the answer itself can still be updated', $r3['updated'] === 1 && $r3['duplicates'] === []);
$r4 = co_db_put(['events' => [$e]]);
$ok('same answer again is "same"', $r4['same'] === 1 && $r4['duplicates'] === []);
// two copies in one put (two tabs batched): only the first
$r5 = co_db_put(['events' => [$ev($c, 'EVT-D-B1', 'assistant_message', 'JOB-D-2', 3), $ev($c, 'EVT-D-B2', 'assistant_message', 'JOB-D-2', 3)]]);
$ok('two copies in one put: one written', $r5['inserted'] === 1 && count($r5['duplicates']) === 1 && $answers($c, 'JOB-D-2') === 1);
// other event types of a job are not limited
co_db_put(['events' => [$ev($c, 'EVT-D-N1', 'note', 'JOB-D-1', 4), $ev($c, 'EVT-D-N2', 'note', 'JOB-D-1', 5)]]);
$ok('two notes of one job both kept', $count("SELECT COUNT(*) FROM events WHERE job_id = 'JOB-D-1' AND type = 'note'") === 2);
// answers without a job id are not limited
co_db_put(['events' => [$ev($c, 'EVT-D-X1', 'assistant_message', 'JOB-D-X', 6)]]);
$x = $ev($c, 'EVT-D-X2', 'assistant_message', 'JOB-D-X', 7);
$x['jobId'] = null;
$y = $ev($c, 'EVT-D-X3', 'assistant_message', 'JOB-D-X', 8);
$y['jobId'] = null;
$r6 = co_db_put(['events' => [$x, $y]]);
$ok('answers without jobId are not deduplicated', $r6['inserted'] === 2);

// ---------- the race: the unique key catches a copy the read did not see ----------
$c2 = $chat();
$st = $db->prepare("INSERT INTO events (event_id, chat_id, branch_id, seq, type, job_id, data, sha, synced_utc)
                    VALUES ('EVT-D-R1', ?, 'BR-root', 1, 'assistant_message', 'JOB-D-R', '{}', 'x', UTC_TIMESTAMP(3))");
$st->execute([$c2]);
$res = ['inserted' => 0, 'updated' => 0, 'same' => 0];
$w = co_db_upsert($db, 'events', ['event_id' => 'EVT-D-R2'], ['chat_id' => $c2, 'branch_id' => 'BR-root', 'seq' => 1,
    'type' => 'assistant_message', 'job_id' => 'JOB-D-R', 'data' => '{"x":2}'], 'y', 'synced_utc', $res, true);
$ok('unique key: plain insert refused, winner untouched', $w === false && $res['inserted'] === 0
    && $count("SELECT COUNT(*) FROM events WHERE job_id = 'JOB-D-R'") === 1
    && $count("SELECT COUNT(*) FROM events WHERE event_id = 'EVT-D-R1' AND data = '{}'") === 1);
try {
    $db->query("INSERT INTO events (event_id, chat_id, branch_id, seq, type, job_id, data, sha, synced_utc)
                VALUES ('EVT-D-R3', '$c2', 'BR-root', 1, 'assistant_message', 'JOB-D-R', '{}', 'x', UTC_TIMESTAMP(3))");
    $ok('unique key ux_events_answer exists', false);
} catch (mysqli_sql_exception $e) {
    $ok('unique key ux_events_answer exists', $e->getCode() === 1062);
}

// ---------- finalize: CO Web first, then the server; a retried job_result ----------
$c3 = $chat();
$req = ['jobId' => 'JOB-D-F', 'chatId' => $c3, 'hostId' => 'site', 'environmentId' => 'owner', 'projectId' => 'P', 'agent' => 'claude'];
$resOk = ['status' => 'completed', 'agentFinalResponse' => 'pong', 'completedUtc' => gmdate('Y-m-d\TH:i:s\Z')];
$db->prepare("UPDATE chats SET folder_id = 'F' WHERE chat_id = ?")->execute([$c3]);
co_db_put(['events' => [$ev($c3, 'EVT-D-FU', 'user_message', 'JOB-D-F', 1, 'reply pong')]]);
$ok('finalize writes the answer', co_finalize_job($req, $resOk) !== null && $answers($c3, 'JOB-D-F') === 1);
$ok('finalize again (job_result retried) writes nothing', co_finalize_job($req, $resOk) === null && $answers($c3, 'JOB-D-F') === 1);
$r7 = co_db_put(['events' => [$ev($c3, 'EVT-D-FW', 'assistant_message', 'JOB-D-F', 2)]]);
$ok('CO Web copy after the server is dropped', count($r7['duplicates']) === 1 && $answers($c3, 'JOB-D-F') === 1);

// ---------- job finished push ----------
$sent = [];
$send = static function (callable $filter, array $payload, string $topic) use (&$sent): array {
    $sent[] = ['filterOwner' => $filter(['env' => 'owner']), 'filterOther' => $filter(['env' => 'sam']), 'payload' => $payload, 'topic' => $topic];
    return ['sent' => 1, 'removed' => 0, 'failed' => 0, 'endpoints' => ['fcm.googleapis.com 201']];
};
$job = static function (string $id, string $host) {
    co_db_put(['jobs' => [['hostId' => $host, 'state' => 'completed',
        'request' => ['jobId' => $id, 'hostId' => $host, 'environmentId' => 'owner', 'chatId' => 'x', 'agent' => 'claude']]]]);
};
$long = static fn(array $extra = []): array => $extra + ['status' => 'completed', 'agentFinalResponse' => "**Готово.** Ответ: `pong`\n\n- пункт",
    'startedUtc' => gmdate('Y-m-d\TH:i:s\Z', time() - 120), 'completedUtc' => gmdate('Y-m-d\TH:i:s\Z')];
$c4 = $chat('Freedom F2');
$preq = ['jobId' => 'JOB-D-P1', 'chatId' => $c4, 'environmentId' => 'owner', 'projectId' => 'AI-Workbench'];
$job('JOB-D-P1', 'site');
$p1 = co_push_job_finished($db, 'site', $preq, $long(), $send);
$ok('site job: push sent', str_starts_with($p1, 'sent') && count($sent) === 1);
$pl = $sent[0]['payload'] ?? [];
$ok('push: title, plain body, chat url, tag = chatId', ($pl['title'] ?? '') === '✓ Freedom F2' && ($pl['body'] ?? '') === 'Готово. Ответ: pong - пункт'
    && ($pl['url'] ?? '') === '/e/owner/p/AI-Workbench/c/' . $c4 && ($pl['tag'] ?? '') === $c4 && ($sent[0]['topic'] ?? '') === $c4);
$ok('push goes to the job environment only', $sent[0]['filterOwner'] === true && $sent[0]['filterOther'] === false);
$ok('second result of the same job: no second push', co_push_job_finished($db, 'site', $preq, $long(), $send) === 'skip: pushed' && count($sent) === 1);
$job('JOB-D-P2', 'home-main');
$ok('Bridge host: no server push (Bridge sends its own)',
    co_push_job_finished($db, 'home-main', ['jobId' => 'JOB-D-P2'] + $preq, $long(), $send) === 'skip: bridge host' && count($sent) === 1);
$job('JOB-D-P3', 'site');
$short = $long(['startedUtc' => gmdate('Y-m-d\TH:i:s\Z', time() - 10)]);
$ok('short job: no push', co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P3'] + $preq, $short, $send) === 'skip: short');
$c5 = $chat('⏰ Daily sweep');
$job('JOB-D-P4', 'site');
$ok('short job of a schedule chat: push', str_starts_with(co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P4', 'chatId' => $c5] + $preq, $short, $send), 'sent')
    && count($sent) === 2);
$job('JOB-D-P5', 'site');
$ok('cancelled: no push', co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P5'] + $preq, $long(['status' => 'cancelled']), $send) === 'skip: cancelled');
$job('JOB-D-P6', 'site');
co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P6'] + $preq,
    ['status' => 'failed', 'error' => 'Claude exited with code 128', 'agentFinalResponse' => null] + $long(), $send);
$ok('failed: ✗ title and the error as body', ($sent[2]['payload']['title'] ?? '') === '✗ Freedom F2'
    && ($sent[2]['payload']['body'] ?? '') === 'Claude exited with code 128');
$job('JOB-D-P7', 'site');
$silent = co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P7'] + $preq, $long(['agentFinalResponse' => '[[silent]] nothing new']), $send);
$ok('[[silent]] answer: nothing sent', str_contains($silent, '"silent":true') && count($sent) === 3);
$job('JOB-D-P8', 'site');
$ok('old result (drive-sync late): no push', co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P8'] + $preq,
    $long(['completedUtc' => gmdate('Y-m-d\TH:i:s\Z', time() - 7200), 'startedUtc' => gmdate('Y-m-d\TH:i:s\Z', time() - 7300)]), $send) === 'skip: old');

// co_job_finished (db.php job_result, drive-sync): answer + push marker, in the CLI at once
$job('JOB-D-F', 'site');
$ok('co_job_finished: answer exists already -> null, push marker set once',
    co_job_finished('site', $req, $long()) === null
    && $count("SELECT COUNT(*) FROM jobs WHERE job_id = 'JOB-D-F' AND pushed_utc IS NOT NULL") === 1);

// a push no device took (push service down) may be sent again
$job('JOB-D-P9', 'site');
$down = static fn(callable $f, array $p, string $t): array => ['sent' => 0, 'removed' => 0, 'failed' => 2, 'endpoints' => []];
co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P9'] + $preq, $long(), $down);
$ok('push failed everywhere: marker cleared', $count("SELECT COUNT(*) FROM jobs WHERE job_id = 'JOB-D-P9' AND pushed_utc IS NULL") === 1);
$boom = static function (): array { throw new RuntimeException('curl'); };
co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P9'] + $preq, $long(), $boom);
$ok('push threw: marker cleared', $count("SELECT COUNT(*) FROM jobs WHERE job_id = 'JOB-D-P9' AND pushed_utc IS NULL") === 1);
$ok('retry later is sent', str_starts_with(co_push_job_finished($db, 'site', ['jobId' => 'JOB-D-P9'] + $preq, $long(), $send), 'sent')
    && $count("SELECT COUNT(*) FROM jobs WHERE job_id = 'JOB-D-P9' AND pushed_utc IS NOT NULL") === 1);

// ---------- schema 25 cleanup of an existing database (the DELETE of schema.sql, run before the unique key) ----------
$sql = (string)file_get_contents(__DIR__ . '/../db/schema.sql');
preg_match('/^DELETE e2 FROM events e2.*?;/ms', $sql, $m);
$ok('schema.sql has the cleanup', isset($m[0]));
$db->query('ALTER TABLE events DROP INDEX ux_events_answer');
$c6 = $chat();
$raw = static function (string $id, string $chatId, string $text, string $synced, array $extra = []) use ($db): void {
    // key order differs between writers (CO Web, finalize-lib.php): the comparison must not depend on it
    $d = ['eventId' => $id, 'sequence' => (int)substr($id, -1), 'createdUtc' => $synced, 'type' => 'assistant_message',
        'status' => 'completed', 'text' => $text, 'artifacts' => [], 'completedUtc' => $synced];
    $d = array_replace($d, $extra);
    $data = json_encode(substr($id, -1) === '3' ? array_reverse($d, true) : $d);
    $st = $db->prepare("INSERT INTO events (event_id, chat_id, branch_id, seq, type, job_id, text, data, sha, synced_utc)
                        VALUES (?, ?, 'BR-root', 2, 'assistant_message', 'JOB-D-C', ?, ?, 'x', ?)");
    $st->execute([$id, $chatId, $text, $data, $synced]);
};
$raw('EVT-D-C2', $c6, 'pong', '2026-11-12 08:00:02.000');
$raw('EVT-D-C1', $c6, 'pong', '2026-11-12 08:00:01.000');
$raw('EVT-D-C3', $c6, 'pong', '2026-11-12 08:00:03.000');
$raw('EVT-D-C4', $c6, 'other text', '2026-11-12 08:00:04.000');
$raw('EVT-D-C5', $c6, 'Pong', '2026-11-12 08:00:05.000');
$raw('EVT-D-C6', $c6, 'pong', '2026-11-12 08:00:06.000', ['error' => 'x']);
$raw('EVT-D-C7', $c6, 'pong', '2026-11-12 08:00:07.000', ['artifacts' => ['a.docx']]);
$db->query($m[0] ?? 'SELECT 1');
$left = $db->query("SELECT GROUP_CONCAT(event_id ORDER BY event_id) FROM events WHERE job_id = 'JOB-D-C'")->fetch_row()[0];
$ok('cleanup deletes exact copies only (text case, error, artifacts differ = kept)', $left === 'EVT-D-C1,EVT-D-C4,EVT-D-C5,EVT-D-C6,EVT-D-C7');
$db->query("DELETE FROM events WHERE event_id IN ('EVT-D-C4', 'EVT-D-C5', 'EVT-D-C6', 'EVT-D-C7')");
$db->query('ALTER TABLE events ADD UNIQUE INDEX ux_events_answer (chat_id, answer_job)');
$ok('unique key back after the cleanup', $count("SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE()
    AND table_name = 'events' AND index_name = 'ux_events_answer'") > 0);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
