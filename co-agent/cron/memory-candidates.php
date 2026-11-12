<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
/*
 * Project memory candidates (schema 9, 2026-10-18). The secretary (cron/protocol.php) proposes durable project-level
 * decisions and facts in the same model call as the protocol; they are validated and stored here inside the protocol
 * transaction. The owner accepts or rejects them in CO Web (db.php memory_candidates / memory_decide); accepting
 * appends "- YYYY-MM-DD: [tag] text" to <project>/context/memory.md on Drive (web/agent/memory-lib.php).
 */
declare(strict_types=1);

require_once __DIR__ . '/boot.php';
require_once CO_AGENT_WEB . '/memory-lib.php';

const CO_MEMORY_TAGS              = ['decision', 'fact'];
const CO_MEMORY_PROMPT_LINES      = 120;
const CO_MEMORY_PROMPT_LINE_CHARS = 300;
const CO_MEMORY_PROMPT_PENDING    = 60;
const CO_MEMORY_MAX_PER_CALL      = 3;
const CO_MEMORY_MIN_CHARS         = 10;
const CO_MEMORY_MAX_CHARS         = 300;

/**
 * Prompt block with the existing memory and pending candidates.
 *
 * @return array{0:string,1:array<string,bool>} [promptText, existingNormSet]
 */
function co_memory_prompt_block(mysqli $db, string $projectId): array
{
    $existing = [];

    $md = co_memory_file_text($db, $projectId);
    $lines = $md === null ? [] : co_memory_parse_lines($md);
    foreach ($lines as $line) {
        $n = co_memory_norm($line);
        if ($n !== '') {
            $existing[$n] = true;
        }
    }

    // Candidates of the project in any status: rejected ones must not come back.
    $pending = [];
    $st = $db->prepare('SELECT tag, text, final_text, status, kind FROM memory_candidates WHERE project_id = ? ORDER BY id');
    $st->execute([$projectId]);
    $res = $st->get_result();
    while ($row = $res->fetch_assoc()) {
        // schema 14: a revision's text counts only once accepted (a rejected one never reached memory.md)
        $isRevision = ($row['kind'] ?? 'add') !== 'add';
        foreach ($isRevision && $row['status'] !== 'accepted' ? [] : [$row['text'], $row['final_text']] as $t) {
            if ($t === null || $t === '') {
                continue;
            }
            $n = co_memory_norm((string)$t);
            if ($n !== '') {
                $existing[$n] = true;
            }
        }
        if ($row['status'] === 'pending' && ($row['kind'] ?? 'add') === 'add') { // schema 14: not the revisions
            $pending[] = '- [' . $row['tag'] . '] ' . (string)$row['text'];
        }
    }
    $st->close();

    $out = "Project memory (context/memory.md):\n";
    if ($md === null) {
        $out .= "(could not be read)\n";
    } elseif ($lines === []) {
        $out .= "(none)\n";
    } else {
        foreach (array_slice($lines, -CO_MEMORY_PROMPT_LINES) as $line) {
            $out .= mb_substr($line, 0, CO_MEMORY_PROMPT_LINE_CHARS, 'UTF-8') . "\n";
        }
    }

    $out .= "\nPending memory candidates:\n";
    if ($pending === []) {
        $out .= "(none)\n";
    } else {
        foreach (array_slice($pending, -CO_MEMORY_PROMPT_PENDING) as $line) {
            $out .= mb_substr($line, 0, CO_MEMORY_PROMPT_LINE_CHARS, 'UTF-8') . "\n";
        }
    }

    return [$out, $existing];
}

/**
 * Inserts validated memory candidates. Called inside the open transaction of co_protocol_apply (no begin/commit).
 *
 * @param array<int,array{type:string,text:string}> $batch
 * @param array<int,string> $dates seq => 'Y-m-d'
 * @param array<string,bool> $existing norm => true (updated in place)
 */
function co_memory_insert(
    mysqli $db,
    string $projectId,
    string $chatId,
    array $batch,
    array $dates,
    mixed $memOps,
    string $model,
    array &$existing
): int {
    if (!is_array($memOps) || $memOps === []) {
        return 0;
    }

    $st = null;
    $accepted = 0;
    $inserted = 0;
    $modelCut = mb_substr($model, 0, 60, 'UTF-8');

    try {
        foreach ($memOps as $item) {
            if ($accepted >= CO_MEMORY_MAX_PER_CALL) {
                break;
            }
            if (!is_array($item)) {
                continue;
            }

            $tag = $item['tag'] ?? null;
            if (!is_string($tag)) {
                continue;
            }
            $tag = strtolower(trim($tag));
            if (!in_array($tag, CO_MEMORY_TAGS, true)) {
                continue;
            }

            $msg = $item['msg'] ?? null;
            if (is_int($msg)) {
                $seq = $msg;
            } elseif (is_string($msg) && $msg !== '' && ctype_digit($msg)) {
                $seq = (int)$msg;
            } else {
                continue;
            }
            if (!array_key_exists($seq, $batch)) {
                continue;
            }

            $text = $item['text'] ?? null;
            if (!is_string($text)) {
                continue;
            }
            $text = mb_scrub($text, 'UTF-8');
            $text = trim(preg_replace('/[\r\n]+/u', ' ', $text) ?? '');
            $text = trim(preg_replace(co_memory_prefix_re(), '', $text, 1) ?? '');
            $len = mb_strlen($text, 'UTF-8');
            if ($len < CO_MEMORY_MIN_CHARS || $len > CO_MEMORY_MAX_CHARS) {
                continue;
            }

            $norm = co_memory_norm($text);
            if ($norm === '' || isset($existing[$norm])) {
                continue;
            }

            $day = $dates[$seq] ?? '';
            if (!is_string($day) || !preg_match('/^\d{4}-\d{2}-\d{2}$/', $day)) {
                $day = gmdate('Y-m-d');
            }

            $accepted++;
            $existing[$norm] = true;

            $st ??= $db->prepare(
                "INSERT IGNORE INTO memory_candidates
                   (project_id, chat_id, msg_seq, tag, text, norm_sha, day, status, model, created_utc)
                 VALUES (?, ?, ?, ?, ?, ?, ?, 'pending', ?, UTC_TIMESTAMP(3))"
            );
            $st->execute([$projectId, $chatId, $seq, $tag, $text, sha1($norm), $day, $modelCut]);
            if ($st->affected_rows === 1) {
                $inserted++;
            }
        }
    } finally {
        $st?->close();
    }

    return $inserted;
}

// ---- Schema 16: rules from corrections. A person corrects the agent ("not like this", "I already sent it", "shorter",
// "do not use X"); the secretary turns it into a rule for future agents (memory_candidates kind 'rule'). Target (tag):
// env = Reflexes, the environment profile of the person (level 2: every project, above project memory) - the default,
// since corrections are mostly about how agents work for this person; project = memory.md, when the correction only
// concerns the project's subject. The owner accepts in CO Web (Memory / Waiting for you; db.php memory_decide).

require_once CO_AGENT_WEB . '/push-lib.php';

const CO_RULE_MAX_PER_CALL = 2;
const CO_RULE_PROMPT_LINES = 40;

/** seq => env of messages written by a person (an email author known to CO), not agents, schedules or imports. */
function co_rule_human_seqs(array $batch): array
{
    $users = co_push_users();
    $out = [];
    foreach ($batch as $seq => $m) {
        $a = strtolower(trim((string)($m['author'] ?? '')));
        if ($m['type'] === 'user_message' && isset($users[$a])) {
            $out[$seq] = $users[$a];
        }
    }
    return $out;
}



/**
 * Prompt block with the rules known for these envs (profile lines and rule candidates) and the normalized set.
 *
 * @param array<int,string> $humanSeqs
 * @return array{0:string,1:array<string,bool>}
 */
function co_rule_prompt_block(mysqli $db, string $projectId, array $humanSeqs): array
{
    $existing = [];
    $shown = [];
    // project rules of other projects do not count: their agents never see this project's memory
    $envs = array_values(array_unique(array_values($humanSeqs)));
    foreach ($envs as $env) {
        foreach (preg_split('/\R/u', co_rule_profile_text($db, $env)) ?: [] as $line) {
            $n = co_rule_norm($line);
            if (mb_strlen($n, 'UTF-8') >= 10) {
                $existing[$n] = true;
                $shown[] = '- ' . trim(ltrim(trim($line), '- '));
            }
        }
    }
    $ph = implode(',', array_fill(0, max(1, count($envs)), '?'));
    $st = $db->prepare("SELECT text, final_text, status FROM memory_candidates
                         WHERE kind = 'rule' AND (project_id = ? OR (tag = 'env' AND target_env IN ($ph))) ORDER BY id DESC LIMIT 300");
    $st->execute(array_merge([$projectId], $envs === [] ? [''] : $envs));
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        foreach ([$r['text'], $r['final_text']] as $t) {
            $n = $t === null ? '' : co_rule_norm((string)$t);
            if ($n !== '') {
                $existing[$n] = true;
            }
        }
        if ($r['status'] !== 'accepted') {
            $shown[] = '- (' . $r['status'] . ') ' . (string)$r['text'];
        }
    }
    $st->close();
    $shown = array_slice($shown, 0, CO_RULE_PROMPT_LINES);
    $text = "Rules already known (environment profile, proposed before):\n" . ($shown === [] ? "(none)\n" : implode("\n", array_map(
        static fn(string $l): string => mb_substr($l, 0, CO_MEMORY_PROMPT_LINE_CHARS, 'UTF-8'), $shown)) . "\n");
    return [$text, $existing];
}

/**
 * Inserts validated rule candidates. Called inside the protocol transaction (no begin/commit).
 *
 * @param array<int,array{type:string,text:string}> $batch
 * @param array<int,string> $humanSeqs seq => env
 * @param array<string,bool> $existing
 */
function co_rule_insert(
    mysqli $db,
    string $projectId,
    string $chatId,
    array $batch,
    array $dates,
    array $humanSeqs,
    mixed $ruleOps,
    string $model,
    array &$existing
): int {
    if (!is_array($ruleOps) || $ruleOps === []) {
        return 0;
    }
    $inserted = 0;
    $accepted = 0;
    $st = null;
    try {
        foreach ($ruleOps as $item) {
            if ($accepted >= CO_RULE_MAX_PER_CALL || !is_array($item)) {
                continue;
            }
            $msg = $item['msg'] ?? null;
            $seq = is_int($msg) ? $msg : (is_string($msg) && ctype_digit($msg) ? (int)$msg : -1);
            if (!isset($humanSeqs[$seq])) {
                continue;
            }
            $scope = strtolower(trim((string)($item['scope'] ?? 'env'))) === 'project' ? 'project' : 'env';
            $text = trim(preg_replace('/\s+/u', ' ', mb_scrub((string)($item['text'] ?? ''), 'UTF-8')) ?? '');
            $text = trim(preg_replace(co_memory_prefix_re(), '', $text, 1) ?? '');
            $len = mb_strlen($text, 'UTF-8');
            if ($len < CO_MEMORY_MIN_CHARS || $len > CO_MEMORY_MAX_CHARS) {
                continue;
            }
            // the correction must be quoted verbatim from the person's message
            $quote = trim(preg_replace('/\s+/u', ' ', mb_scrub((string)($item['quote'] ?? ''), 'UTF-8')) ?? '');
            $src = preg_replace('/\s+/u', ' ', (string)$batch[$seq]['text']) ?? '';
            if (mb_strlen($quote, 'UTF-8') < 2 || mb_stripos($src, $quote, 0, 'UTF-8') === false) {
                continue;
            }
            $norm = co_rule_norm($text);
            if ($norm === '' || isset($existing[$norm])) {
                continue;
            }
            $day = $dates[$seq] ?? '';
            if (!is_string($day) || !preg_match('/^\d{4}-\d{2}-\d{2}$/', $day)) {
                $day = gmdate('Y-m-d');
            }
            $accepted++;
            $existing[$norm] = true;
            $st ??= $db->prepare(
                "INSERT IGNORE INTO memory_candidates
                   (project_id, chat_id, msg_seq, tag, text, norm_sha, day, status, model, created_utc, kind, reason, target_env)
                 VALUES (?, ?, ?, ?, ?, ?, ?, 'pending', ?, UTC_TIMESTAMP(3), 'rule', ?, ?)"
            );
            $st->execute([$projectId, $chatId, $seq, $scope, $text, sha1('rule|' . $norm), $day, mb_substr($model, 0, 60, 'UTF-8'),
                mb_substr($quote, 0, 380, 'UTF-8'), $humanSeqs[$seq]]);
            if ($st->affected_rows === 1) {
                $inserted++;
            }
        }
    } finally {
        $st?->close();
    }
    return $inserted;
}

