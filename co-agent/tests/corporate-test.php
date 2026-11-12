<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F1 5b: corporate-resume-lib.php against a throwaway MariaDB (never production) - chats stopped because the
// corporate model was unavailable are found by the new field corporateBlocked and by the legacy wire engieBlocked;
// manifest texts and tools. Driver: python tests/actions-test.py --test corporate-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';
require __DIR__ . '/../web/agent/finalize-lib.php';
require __DIR__ . '/../web/agent/engie-resume-lib.php'; // the legacy wrapper pulls corporate-resume-lib.php

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

foreach (['events', 'jobs', 'chats', 'projects'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP())");
$chats = ['CHAT-20261111000000-AAAA' => '{"engieBlocked":"signin"}', 'CHAT-20261111000000-BBBB' => '{"corporateBlocked":"offline"}',
    'CHAT-20261111000000-CCCC' => '{"status":"completed"}', 'CHAT-20261111000000-DDDD' => '{"corporateBlocked":"","engieBlocked":"off"}'];
$i = 0;
foreach ($chats as $cid => $data) {
    $i++;
    $q("INSERT INTO chats (chat_id, project_id, title, data, sha, updated_utc, synced_utc) VALUES (?, 'P1', ?, '{}', 'x', UTC_TIMESTAMP(3), UTC_TIMESTAMP())", [$cid, "c$i"]);
    $ev = json_encode(['eventId' => "EV-$i"] + json_decode($data, true));
    $q("INSERT INTO events (event_id, chat_id, seq, type, writer, created_utc, text, data, sha, synced_utc) VALUES
        (?, ?, 1, 'user_message', 'owner@example.org', UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE, 'go', '{}', 'x', UTC_TIMESTAMP()),
        (?, ?, 2, 'assistant_message', NULL, UTC_TIMESTAMP(3), 'stopped', ?, 'x', UTC_TIMESTAMP())", ["U-$i", $cid, "EV-$i", $cid, $ev]);
}
$w = array_column(co_corporate_waiting($db, 'owner'), 'reason', 'chatId');
$ok('legacy engieBlocked found', ($w['CHAT-20261111000000-AAAA'] ?? null) === 'signin');
$ok('corporateBlocked found', ($w['CHAT-20261111000000-BBBB'] ?? null) === 'offline');
$ok('empty new name falls back to the legacy one', ($w['CHAT-20261111000000-DDDD'] ?? null) === 'off');
$ok('finished chat not listed', !isset($w['CHAT-20261111000000-CCCC']));
$ok('legacy wrapper', count(co_engie_waiting($db, 'owner')) === 3);
$ok('last blocked', co_corporate_last_blocked($db, 'CHAT-20261111000000-BBBB') !== null && co_corporate_last_blocked($db, 'CHAT-20261111000000-CCCC') === null);
$ok('resume text and tools from the manifest', str_contains(co_corporate_resume_text(), co_corporate()['title'])
    && in_array('engie_chat', co_corporate_tools('corporate-llm'), true) && co_corporate_tools('nope') === []);
co_corporate(['title' => 'Acme']);
$ok('no manifest text: neutral resume text', co_corporate_resume_text() === CO_CORP_RESUME_TEXT && co_corporate_tools() === []);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
