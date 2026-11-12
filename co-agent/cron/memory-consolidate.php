<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
/*
 * Memory consolidation (schema 14, 2026-10-31): a "sleep-time" pass over <project>/context/memory.md. A model reads
 * the numbered entries and proposes revisions (merge duplicates, replace entries superseded by later ones, shorten very
 * long entries, drop obsolete ones). Nothing is applied here: every revision becomes a memory_candidates row (kind
 * revise|drop, replaces = JSON list of the exact original lines) that the owner accepts or rejects in CO Web
 * (Project → Memory; db.php memory_decide rewrites memory.md and archives the removed lines in memory-archive.md).
 *
 * Called from watch-cron.php every 5 minutes; works only 01:00-05:59 Europe/Brussels. Per run it checks the projects
 * not checked today (one memory.md download each) and makes at most one model call: for a project whose memory.md
 * changed since its last pass, or whose last pass is older than 7 days. State per project: meta mc_<sha1> =
 * {"sha":…, "pass":"Y-m-d H:i:s", "check":"Y-m-d"}.
 * Manual run: php memory-consolidate.php <projectId> [--dry]   (ignores the window and the state)
 */
declare(strict_types=1);

require_once __DIR__ . '/boot.php';
require_once CO_AGENT_WEB . '/lib.php';
require_once CO_AGENT_WEB . '/db-lib.php';
require_once CO_AGENT_WEB . '/memory-lib.php';

const CO_MC_MIN_ENTRIES   = 8;      // smaller memories are not worth a pass
const CO_MC_MAX_REVISIONS = 15;     // per pass
const CO_MC_MAX_LINES     = 8;      // lines one revision may merge
const CO_MC_MAX_PENDING   = 25;     // pending revisions: wait for the owner before proposing more
const CO_MC_WEEK_SEC      = 7 * 86400;
const CO_MC_TAGS          = ['decision', 'fact', 'todo'];

function co_mc_key(string $projectId): string
{
    return 'mc_' . substr(sha1($projectId), 0, 30);
}

/** @return array{sha?:string,pass?:string,check?:string} */
function co_mc_state(mysqli $db, string $projectId): array
{
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([co_mc_key($projectId)]);
    $v = $st->get_result()->fetch_row()[0] ?? null;
    $j = is_string($v) ? json_decode($v, true) : null;
    return is_array($j) ? $j : [];
}

function co_mc_state_set(mysqli $db, string $projectId, array $state): void
{
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute([co_mc_key($projectId), json_encode($state, JSON_UNESCAPED_SLASHES)]);
}

/** Cron entry: at most one model call per run, only in the night window. Returns a log line or null. */
function co_memory_consolidate_cron(int $budgetSec = 280): ?string
{
    $now = new DateTimeImmutable('now', new DateTimeZone('Europe/Brussels'));
    $hour = (int)$now->format('G');
    if ($hour < 1 || $hour > 5) {
        return null;
    }
    $lock = fopen(co_agent_home() . '/co-agent/state/memory-consolidate.lock', 'c');
    if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) {
        return null;
    }
    $t0 = time();
    $today = $now->format('Y-m-d');
    $db = co_db();
    $rows = $db->query("SELECT project_id FROM projects WHERE archived = 0 AND folder_id IS NOT NULL AND folder_id <> ''
                         ORDER BY updated_utc DESC")->fetch_all(MYSQLI_ASSOC);
    foreach ($rows as $r) {
        if (time() - $t0 > 60) {
            break; // the checks are cheap; the next run continues
        }
        $projectId = (string)$r['project_id'];
        $state = co_mc_state($db, $projectId);
        if (($state['check'] ?? '') === $today) {
            continue;
        }
        $md = co_memory_file_text($db, $projectId, true);
        if ($md === null) {
            continue; // Drive error: retried by the next run
        }
        $sha = hash('sha256', $md);
        $state['check'] = $today;
        $due = ($state['sha'] ?? '') !== $sha
            || strtotime((string)($state['pass'] ?? '1970-01-01') . ' UTC') < time() - CO_MC_WEEK_SEC;
        if (!$due || count(co_memory_parse_lines($md)) < CO_MC_MIN_ENTRIES) {
            co_mc_state_set($db, $projectId, $state);
            continue;
        }
        try {
            $res = co_memory_consolidate($db, $projectId, $md, false, $budgetSec);
            $db = co_db(true);
            $state['sha'] = $sha;
            $state['pass'] = gmdate('Y-m-d H:i:s');
            co_mc_state_set($db, $projectId, $state);
            return 'memory consolidate ' . $projectId . ': ' . json_encode($res, JSON_UNESCAPED_UNICODE);
        } catch (Throwable $e) {
            // check stays unset for today only after 3 failures (meta mcf_), so a broken answer is not retried forever
            $db = co_db(true);
            $fk = 'mcf_' . substr(sha1($projectId . $today), 0, 30);
            $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
            $st->execute([$fk]);
            $fails = (int)($st->get_result()->fetch_row()[0] ?? 0) + 1;
            $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
            $st->execute([$fk, (string)$fails]);
            if ($fails >= 3) {
                co_mc_state_set($db, $projectId, $state);
            }
            return 'memory consolidate ' . $projectId . ' failed (' . $fails . '): ' . mb_substr($e->getMessage(), 0, 300);
        }
    }
    return null;
}

/**
 * One pass for a project: marks pending revisions whose lines left memory.md obsolete, asks the model, validates and
 * stores the proposals. $dry: no writes, the validated proposals are returned.
 *
 * @return array{obsolete:int,proposed:int,stored:int,proposals?:list<array>}
 */
function co_memory_consolidate(mysqli $db, string $projectId, string $md, bool $dry = false, int $timeoutSec = 280): array
{
    $lines = co_memory_parse_lines($md);
    $present = array_flip($lines);

    // Pending revisions: drop those whose lines are gone, keep the rest out of this pass.
    $obsolete = 0;
    $busy = [];
    $pendingText = [];
    $st = $db->prepare("SELECT id, kind, replaces, text FROM memory_candidates
                         WHERE project_id = ? AND status = 'pending' AND kind IN ('revise', 'drop')");
    $st->execute([$projectId]);
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $p) {
        $rep = json_decode((string)$p['replaces'], true);
        $rep = is_array($rep) ? $rep : [];
        $gone = $rep === [] || array_filter($rep, static fn($l) => !isset($present[$l])) !== [];
        if ($gone) {
            if (!$dry) {
                $u = $db->prepare("UPDATE memory_candidates SET status = 'obsolete', decided_utc = UTC_TIMESTAMP(3),
                                   decided_by = 'consolidation' WHERE id = ? AND status = 'pending'");
                $u->execute([(int)$p['id']]);
            }
            $obsolete++;
            continue;
        }
        foreach ($rep as $l) {
            $busy[$l] = true;
        }
        $pendingText[] = '- ' . $p['kind'] . ' of ' . count($rep) . ' line(s)' . ($p['text'] !== '' ? ': ' . $p['text'] : '');
    }
    if (count($pendingText) >= CO_MC_MAX_PENDING) {
        return ['obsolete' => $obsolete, 'proposed' => 0, 'stored' => 0, 'skipped' => 'too many pending revisions'];
    }

    // Line sets proposed before (rejected, accepted or pending) must not come back.
    $rejected = [];
    $st = $db->prepare("SELECT norm_sha FROM memory_candidates WHERE project_id = ? AND kind IN ('revise', 'drop')");
    $st->execute([$projectId]);
    foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as $x) {
        $rejected[(string)$x[0]] = true;
    }

    $numbered = [];
    foreach ($lines as $i => $l) {
        $numbered[] = '[' . ($i + 1) . ']' . (isset($busy[$l]) ? ' (has a pending proposal, do not use)' : '') . ' ' . $l;
    }
    $list = implode("\n", $numbered);
    $today = gmdate('Y-m-d');
    $max = CO_MC_MAX_REVISIONS;
    $maxLines = CO_MC_MAX_LINES;
    $pend = $pendingText === [] ? '(none)' : implode("\n", $pendingText);
    $prompt = <<<PROMPT
You maintain the project memory of a software/work project. Every new AI agent session of the project receives this memory, so it must be short, current and free of repetition. Today is {$today}.
Entries have the form "- YYYY-MM-DD: [tag] text"; later entries are newer.

Propose up to {$max} revisions. Kinds:
- "revise": the listed entries are replaced by ONE new entry. Use it to merge duplicates or near-duplicates, to replace an entry by the later entry that supersedes it (merge them into the current state), or to shorten a very long entry (over ~250 characters) to its essence.
- "drop": the listed entries are removed. Only for entries that are clearly obsolete or fully superseded/contained in another entry that stays.
Rules:
- Keep every fact that is still true and useful: exact names, paths, numbers, versions that still matter. Never invent facts. When unsure, propose nothing for that entry.
- "lines" are entry numbers from the list (at most {$maxLines} per revision; one entry in at most one revision). Never use entries marked "has a pending proposal".
- "text" (revise only): one line, at most 280 characters, in the language of the entries, without date and tag prefix. "tag": decision, fact or todo.
- "reason": one short English sentence, e.g. "duplicate of [12]", "superseded by [40]: project paused", "shortened".
- An entry that says a project/service is paused or stopped supersedes older operational details of it only if those details are no longer useful.
- Usually few revisions are needed; an empty list is a valid answer.

Already pending proposals (the owner has not decided yet):
{$pend}

Memory entries:
{$list}

Answer with JSON only:
{"revisions":[{"kind":"revise","lines":[3,17],"tag":"fact","text":"…","reason":"…"},{"kind":"drop","lines":[5],"reason":"…"}]}
PROMPT;

    $model = 'opus';
    $st = $db->prepare("SELECT v FROM meta WHERE k = 'memory_consolidate_model'");
    $st->execute();
    $m = $st->get_result()->fetch_row()[0] ?? null;
    if (is_string($m) && preg_match('/^(claude-[a-z0-9-]+|opus|sonnet|haiku)$/', $m)) {
        $model = $m;
    }
    $answer = co_agent_claude($prompt, $model, $timeoutSec);
    $j = co_agent_json_from($answer);
    if (!isset($j['revisions']) || !is_array($j['revisions'])) {
        throw new RuntimeException('model answer has no revisions array');
    }

    $props = co_memory_consolidate_validate($lines, $busy, $j['revisions'], $rejected);
    $db = co_db(true);
    $stored = 0;
    if (!$dry) {
        $ins = $db->prepare(
            "INSERT IGNORE INTO memory_candidates
               (project_id, chat_id, msg_seq, tag, text, norm_sha, day, status, model, created_utc, kind, replaces, reason)
             VALUES (?, '', 0, ?, ?, ?, ?, 'pending', ?, UTC_TIMESTAMP(3), ?, ?, ?)"
        );
        foreach ($props as $p) {
            $ins->execute([$projectId, $p['tag'], $p['text'], $p['sha'], $p['day'], mb_substr($model, 0, 60, 'UTF-8'),
                $p['kind'], json_encode($p['replaces'], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), $p['reason']]);
            $stored += $ins->affected_rows === 1 ? 1 : 0;
        }
    }
    $out = ['obsolete' => $obsolete, 'proposed' => count($props), 'stored' => $stored];
    if ($dry) {
        $out['proposals'] = $props;
    }
    return $out;
}

/**
 * Validates the model's revisions against the numbered lines.
 *
 * @param list<string> $lines
 * @param array<string,bool> $busy lines of pending revisions
 * @param array<string,bool> $seen norm_sha of line sets proposed before
 * @return list<array{kind:string,replaces:list<string>,tag:string,text:string,day:string,reason:string,sha:string}>
 */
function co_memory_consolidate_validate(array $lines, array $busy, array $revisions, array $seen = []): array
{
    $used = [];
    $out = [];
    foreach ($revisions as $r) {
        if (count($out) >= CO_MC_MAX_REVISIONS) {
            break;
        }
        if (!is_array($r)) {
            continue;
        }
        $kind = is_string($r['kind'] ?? null) ? strtolower(trim($r['kind'])) : '';
        if ($kind !== 'revise' && $kind !== 'drop') {
            continue;
        }
        $nums = is_array($r['lines'] ?? null) ? $r['lines'] : [];
        $idx = [];
        $ok = $nums !== [] && count($nums) <= CO_MC_MAX_LINES;
        foreach ($nums as $n) {
            $n = is_int($n) ? $n : (is_string($n) && ctype_digit($n) ? (int)$n : 0);
            $i = $n - 1;
            if (!isset($lines[$i]) || isset($used[$i]) || isset($idx[$i]) || isset($busy[$lines[$i]])) {
                $ok = false;
                break;
            }
            $idx[$i] = true;
        }
        if (!$ok) {
            continue;
        }
        ksort($idx);
        $replaces = [];
        $day = '';
        $firstTag = 'fact';
        foreach (array_keys($idx) as $k => $i) {
            $replaces[] = $lines[$i];
            if (preg_match('/^- (\d{4}-\d{2}-\d{2})\s*:\s*(?:\[([\p{L}_-]{1,20})\])?/u', $lines[$i], $mm)) {
                $day = max($day, $mm[1]);
                if ($k === 0 && ($mm[2] ?? '') !== '') {
                    $firstTag = strtolower($mm[2]);
                }
            }
        }
        if ($day === '') {
            $day = gmdate('Y-m-d');
        }
        $reason = is_string($r['reason'] ?? null) ? trim(preg_replace('/\s+/u', ' ', mb_scrub($r['reason'], 'UTF-8')) ?? '') : '';
        $reason = mb_substr($reason, 0, 380, 'UTF-8');
        $text = '';
        $tag = in_array($firstTag, CO_MC_TAGS, true) ? $firstTag : 'fact';
        if ($kind === 'revise') {
            $text = is_string($r['text'] ?? null) ? mb_scrub($r['text'], 'UTF-8') : '';
            $text = trim(preg_replace('/\s+/u', ' ', $text) ?? '');
            $text = trim(preg_replace(co_memory_prefix_re(), '', $text, 1) ?? '');
            $len = mb_strlen($text, 'UTF-8');
            if ($len < 10 || $len > 300) {
                continue;
            }
            // a one-line revise must change something
            if (count($replaces) === 1 && co_memory_norm($text) === co_memory_norm($replaces[0])) {
                continue;
            }
            $t = is_string($r['tag'] ?? null) ? strtolower(trim($r['tag'])) : '';
            if (in_array($t, CO_MC_TAGS, true)) {
                $tag = $t;
            }
        }
        $norms = array_map('co_memory_norm', $replaces);
        sort($norms);
        $sha = sha1('rev|' . implode("\n", $norms));
        if (isset($seen[$sha])) {
            continue; // this line set was proposed before (rejected, accepted or pending): its lines stay free
        }
        foreach (array_keys($idx) as $i) {
            $used[$i] = true;
        }
        $out[] = [
            'kind'     => $kind,
            'replaces' => $replaces,
            'tag'      => $tag,
            'text'     => $text,
            'day'      => $day,
            'reason'   => $reason,
            'sha'      => $sha,
        ];
    }
    return $out;
}

if (PHP_SAPI === 'cli' && realpath($_SERVER['SCRIPT_FILENAME'] ?? '') === __FILE__) {
    $projectId = $argv[1] ?? '';
    if ($projectId === '' || $projectId[0] === '-') {
        fwrite(STDERR, "usage: php memory-consolidate.php <projectId> [--dry] | --cron\n");
        if ($projectId === '--cron') {
            echo (co_memory_consolidate_cron() ?? 'nothing to do') . "\n";
            exit(0);
        }
        exit(1);
    }
    $db = co_db();
    $md = co_memory_file_text($db, $projectId, true);
    if ($md === null) {
        fwrite(STDERR, "cannot read memory.md\n");
        exit(1);
    }
    $dry = in_array('--dry', $argv, true);
    $res = co_memory_consolidate($db, $projectId, $md, $dry);
    if (!$dry) {
        $db = co_db(true);
        $state = co_mc_state($db, $projectId);
        $state['sha'] = hash('sha256', $md);
        $state['pass'] = gmdate('Y-m-d H:i:s');
        co_mc_state_set($db, $projectId, $state);
    }
    echo json_encode($res, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT) . "\n";
}
