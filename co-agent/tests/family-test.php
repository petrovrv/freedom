<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Family chats (CO Web 0.89.0) against a throwaway MariaDB (never production): who gets the push of a new message or
// of the agent's answer. Driver: python tests/actions-test.py --test family-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';
require __DIR__ . '/../web/agent/family-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static function (string $sql, array $p = []) use ($db): void {
    $st = $db->prepare($sql);
    $st->execute($p);
    $st->close();
};
foreach (['events', 'chats', 'projects', 'shares', 'kv'] as $t) $db->query("DELETE FROM $t");
$fam = 'CHAT-20261109000000-FAMI';
$plain = 'CHAT-20261109000000-PLAI';
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Family', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES (?, 'P1', 'Семья', '{\"family\":true}', 'x', UTC_TIMESTAMP()), (?, 'P1', 'Plain', '{}', 'x', UTC_TIMESTAMP())", [$fam, $plain]);
$q("INSERT INTO shares (kind, target_id, env, role, created_utc, added_by) VALUES ('project', 'P1', 'sam', 'editor', UTC_TIMESTAMP(), 'owner@example.org'), ('chat', ?, 'alex', 'editor', UTC_TIMESTAMP(), 'owner@example.org')", [$fam]);

$calls = [];
$push = static function (callable $filter, array $payload, string $topic) use (&$calls): array {
    foreach (['owner', 'sam', 'alex'] as $env) if ($filter(['env' => $env])) $calls[] = [$env, $payload];
    return ['sent' => 1, 'removed' => 0, 'failed' => 0];
};
$msg = ['eventId' => 'E1', 'chatId' => $fam, 'type' => 'user_message', 'author' => 'sam@example.org', 'text' => 'Привет всем!'];
co_family_notify($db, [$msg], 'sam', $push);
$to = array_column($calls, 0);
sort($to);
$ok('message goes to the others', $to === ['alex', 'owner']);
$ok('title names the writer', $calls[0][1]['title'] === '👪 Сэм' && $calls[0][1]['body'] === 'Привет всем!');
$ok('url opens the Family tab', $calls[0][1]['url'] === '/e/owner?family=1');

$calls = [];
co_family_notify($db, [['chatId' => $plain] + $msg], 'sam', $push);
$ok('plain chat: no push', $calls === []);

$calls = [];
co_family_notify($db, [['eventId' => 'E2', 'text' => '', 'attachments' => [['displayName' => 'IMG_1.jpg'], ['displayName' => 'b.png']]] + $msg], 'sam', $push);
$ok('photos body', $calls[0][1]['body'] === '📷 2 фото');

// the answer: skips the caller (author of the job's message), whoever wrote it
$q("INSERT INTO events (event_id, chat_id, seq, type, job_id, author, created_utc, text, data, sha, synced_utc) VALUES ('E3', ?, 3, 'user_message', 'JOB-9', 'alex@example.org', UTC_TIMESTAMP(3), '@co не работает', '{}', 'x', UTC_TIMESTAMP())", [$fam]);
$calls = [];
co_family_notify($db, [['eventId' => 'E4', 'chatId' => $fam, 'type' => 'assistant_message', 'jobId' => 'JOB-9', 'text' => 'Сейчас посмотрю. [[chat-title: x]]']], 'owner', $push);
$to = array_column($calls, 0);
sort($to);
$ok('answer skips the caller', $to === ['owner', 'sam'] && $calls[0][1]['title'] === '👪 AI Workbench' && $calls[0][1]['body'] === 'Сейчас посмотрю.');
$calls = [];
co_family_notify($db, [['eventId' => 'E5', 'chatId' => $fam, 'type' => 'assistant_message', 'jobId' => 'JOB-9', 'text' => '[[silent]]']], null, $push);
$ok('silent answer: no push', $calls === []);
$calls = [];
co_family_notify($db, [['eventId' => 'E6', 'chatId' => $fam, 'type' => 'reaction', 'author' => 'owner@example.org', 'emoji' => '👍']], 'owner', $push);
$ok('reaction: no push', $calls === []);

$calls = [];
co_family_notify($db, [['eventId' => 'E7', 'coFor' => 'E1'] + $msg], 'sam', $push);
$ok('delayed @co trigger: no push', $calls === []);
$calls = [];
co_family_notify($db, [['eventId' => 'E8', 'author' => 'owner@example.org'] + $msg], 'sam', $push, 'sam@example.org');
$ok('writer named by the session, not the author field', $calls[0][1]['title'] === '👪 Сэм');

// 09.11: an agent of another chat posts into the family chat (co_family_post)
$stored = [];
$append = static function (array $in) use (&$stored): array {
    $stored[] = $in['event'];
    return ['sequence' => 9, 'eventId' => $in['event']['eventId'], 'rejected' => []];
};
$calls = [];
$r = co_family_post($db, ['text' => "  Шаг 1: обновите CO.\nШаг 2: войдите.  ", 'fromChat' => $plain], $append, $push);
$to = array_column($calls, 0);
sort($to);
$ok('post: the only family chat of the owner', $r['chatId'] === $fam && $r['sequence'] === 9 && count($stored) === 1);
$ok('post: assistant message without a job', $stored[0]['type'] === 'assistant_message' && $stored[0]['jobId'] === null
    && $stored[0]['text'] === "Шаг 1: обновите CO.\nШаг 2: войдите." && $stored[0]['postedFrom'] === $plain);
$ok('post: push to every member', $to === ['alex', 'owner', 'sam'] && $calls[0][1]['title'] === '👪 AI Workbench' && $r['pushed'] === 3);
$r = co_family_post($db, ['text' => 'x', 'env' => 'alex'], $append, $push);
$ok('post: a chat share sees the family chat', $r['chatId'] === $fam);
$r = co_family_post($db, ['text' => 'ответ', 'chatId' => $fam, 'replyTo' => 'E3'], $append, $push);
$ok('post: reply to a message', end($stored)['replyTo'] === 'E3');
$err = static function (array $in) use ($db, $append, $push): string {
    try { co_family_post($db, $in, $append, $push); return ''; } catch (InvalidArgumentException $e) { return $e->getMessage(); }
};
$ok('post: plain chat refused', $err(['text' => 'x', 'chatId' => $plain]) === 'not a family chat');
$ok('post: empty text refused', str_starts_with($err(['text' => '  ']), 'text must'));
$ok('post: unknown replyTo refused', str_starts_with($err(['text' => 'x', 'chatId' => $fam, 'replyTo' => 'E404']), 'replyTo'));
$ok('post: env without family chat', $err(['text' => 'x', 'env' => 'nobody']) === 'no family chat of nobody');
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES ('CHAT-20261109000000-FAM2', 'P1', 'Вторая', '{\"family\":true}', 'x', UTC_TIMESTAMP())");
$ok('post: several family chats need chatId', str_starts_with($err(['text' => 'x']), 'several family chats'));
$ok('list: both family chats, no plain', count(co_family_list($db, 'sam')) === 2);
$q("UPDATE chats SET data = '{\"family\":true,\"archived\":true}' WHERE chat_id = 'CHAT-20261109000000-FAM2'");
$ok('list: archived left out', count(co_family_list($db, 'owner')) === 1);

// 0.90.0: family_state / family_read - the room, unread messages of others after the read mark
$db->query('DELETE FROM events');
$q("UPDATE projects SET data = '{\"projectId\":\"P1\",\"family\":true}' WHERE project_id = 'P1'");
$q("UPDATE chats SET folder_id = 'F1' WHERE chat_id = ?", [$fam]);
$ins = static function (int $seq, string $type, string $author, string $text, string $extra = '') use ($q, $fam): void {
    $q("INSERT INTO events (event_id, chat_id, seq, type, author, created_utc, text, data, sha, synced_utc) VALUES (?, ?, ?, ?, ?, UTC_TIMESTAMP(3), ?, ?, 'x', UTC_TIMESTAMP())",
        ["S$seq", $fam, $seq, $type, $author, $text, '{' . $extra . '}']);
};
$ins(1, 'user_message', 'owner@example.org', 'Привет');
$ins(2, 'user_message', 'sam@example.org', 'Привет, папа');
$ins(3, 'reaction', 'sam@example.org', '');
$ins(4, 'user_message', 'sam@example.org', '@co помоги', '"coFor":"S2"');
$ins(5, 'assistant_message', 'co', 'Готово');
$ins(6, 'assistant_message', 'co', '[[silent]]');
$st = co_family_state($db, 'owner', 'owner@example.org');
$ok('state: room, project and folder', $st['chat']['folderId'] === 'F1' && $st['project']['projectId'] === 'P1' && !isset($st['chat']['shareRole']));
$ok('state: unread = others, no reactions/triggers/silent', $st['unread'] === 2 && $st['lastSeq'] === 6);
$st = co_family_state($db, 'sam', 'sam@example.org');
$ok('state: share role for sam', $st['unread'] === 2 && ($st['chat']['shareRole'] ?? '') === 'editor' && ($st['project']['shareRole'] ?? '') === 'editor');
co_family_mark_read($db, 'owner', $fam, 5);
co_family_mark_read($db, 'owner', $fam, 2);
$ok('read: mark never goes back', co_family_state($db, 'owner', 'owner@example.org')['unread'] === 0 && co_family_read_marks($db, 'owner')[$fam] === 5);
$ok('read: other envs keep their count', co_family_state($db, 'sam', 'sam@example.org')['unread'] === 2);
$q("UPDATE chats SET data = '{\"family\":false}' WHERE chat_id = ?", [$fam]);
$q("UPDATE chats SET data = '{\"family\":false}' WHERE chat_id = 'CHAT-20261109000000-FAM2'");
$st = co_family_state($db, 'alex', 'alex@example.org');
$ok('state: no room yet - project is not visible to a chat-only share', $st['chat'] === null && $st['project'] === null);
$st = co_family_state($db, 'sam', 'sam@example.org');
$ok('state: no room yet - the family project for creating it', $st['chat'] === null && $st['project']['projectId'] === 'P1');

echo $fail ? "$fail FAILED\n" : "all ok\n";
exit($fail ? 1 : 0);
