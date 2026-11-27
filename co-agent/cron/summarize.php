<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Automatic chat summaries (schema 6, 2026-10-16). Called from cron/watch-cron.php every 5 minutes: up to N chats whose
// newest message is older than 10 minutes and newer than their summary get 2-4 sentences from Claude Haiku (the
// server's CLI). CO Web shows them in the chat list and gives the 5 newest of a project to new agent sessions.
// Backfill: php -r "require '<agent>/drive-lib.php'; require 'summarize.php'; print_r(co_summarize_chats(100));"
declare(strict_types=1);

function co_summarize_chats(int $limit = 3): array
{
    $db = co_db();
    $sql = "SELECT c.chat_id, c.title, x.last FROM chats c
              JOIN (SELECT chat_id, MAX(created_utc) AS last, COUNT(*) AS n FROM events GROUP BY chat_id) x
                ON x.chat_id = c.chat_id
             WHERE x.n >= 2 AND x.last < UTC_TIMESTAMP(3) - INTERVAL 10 MINUTE
               AND (c.summary_utc IS NULL OR c.summary_utc < x.last)
             ORDER BY x.last DESC LIMIT " . max(1, min(200, $limit));
    $done = [];
    $todo = $db->query($sql)->fetch_all(MYSQLI_ASSOC);
    foreach ($todo as $c) {
        $db = co_db(true);
        $st = $db->prepare("SELECT type, text FROM events WHERE chat_id = ? AND type IN ('user_message','assistant_message') AND JSON_EXTRACT(data, '$.deleted') IS NULL
                             AND text IS NOT NULL ORDER BY seq, event_id");
        $st->execute([$c['chat_id']]);
        $msgs = $st->get_result()->fetch_all(MYSQLI_ASSOC);
        if (count($msgs) < 2) {
            continue;
        }
        $line = static function (array $m, int $max): string {
            $who = $m['type'] === 'user_message' ? 'User' : 'Agent';
            $t = trim((string)preg_replace('/\s+/u', ' ', (string)$m['text']));
            return $who . ': ' . (mb_strlen($t) > $max ? mb_substr($t, 0, $max) . '…' : $t);
        };
        // The first request and the last eight messages carry goal and outcome.
        $parts = [$line($msgs[0], 1500)];
        $tail = array_slice($msgs, max(1, count($msgs) - 8));
        if (count($msgs) > 9) {
            $parts[] = '[… ' . (count($msgs) - 9) . ' messages omitted …]';
        }
        foreach ($tail as $m) {
            $parts[] = $line($m, 900);
        }
        $prompt = "Chat title: " . $c['title'] . "\n\n" . implode("\n\n", $parts) . "\n\n"
            . "Summarize this chat between a person and an AI agent in 2-4 short sentences, in the language of the "
            . "person's messages: the goal, what was done or decided (with key numbers, names, versions), and what is "
            . "still open. No intro, no markdown. Answer with JSON only: {\"summary\": \"...\"}";
        try {
            $j = co_agent_json_from(co_agent_claude($prompt, 'haiku', 90));
            $db = co_db(true); // the Haiku call takes ~20 s; the idle connection may be gone
            $summary = trim((string)($j['summary'] ?? ''));
            if ($summary === '') {
                throw new RuntimeException('empty summary');
            }
            $st = $db->prepare('UPDATE chats SET summary = ?, summary_utc = UTC_TIMESTAMP(3) WHERE chat_id = ?');
            $st->execute([mb_substr($summary, 0, 1200), $c['chat_id']]);
            $done[] = $c['chat_id'];
        } catch (Throwable $e) {
            fwrite(STDERR, 'summarize ' . $c['chat_id'] . ': ' . $e->getMessage() . "\n");
        }
    }
    return $done;
}
