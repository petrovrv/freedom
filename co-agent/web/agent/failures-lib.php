<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// failures-lib.php: causes of failed jobs and Bridge supervisor events (schema 17, 2026-11-03).
// job_failures: one row per failed/timed_out job - exit code, diagnosis kind (the rules of co-web/src/diagnose.ts,
// same order; examples shared in co-web/src/diagnose.cases.json, checked by co-agent/tests/failures-test.php), the
// first meaningful error line, the stderr tail and the last event of job_events (deleted after 14 days, so the row is
// built while they exist). Filled by cron/drive-sync.php (co_job_failures_fill) next to job_stats; one-off backfill:
// php co-agent/db/backfill-failures.php [days]. host_events: Bridge supervisor events (db.php op host_event), 90 days.
// db.php op health (owner) and aiwb-smoke status (SQL) read the summary.
declare(strict_types=1);
require_once __DIR__ . '/corporate-lib.php';

const CO_FAIL_DIAG_VER = 1;
const CO_FAIL_EVENTS_DAYS = 90;

/** Diagnosis rules: [kind, agent family ('' | codex | claude), scope (error | all), regex | callable(ctx): bool]. */
function co_fail_rules(): array
{
    return [
        ['codex_limit', 'codex', 'all', '/usage limit|hit your usage limit|rate limit reached/i'],
        ['claude_limit', 'claude', 'all', '/hit your (?:weekly |session |5-hour )?limit|usage limit reached|Claude AI usage limit/i'],
        ['claude_auth', 'claude', 'all', '/Invalid API key|Please run \/login|OAuth token (?:has )?expired|OAuth session expired|Failed to authenticate|authentication_error|Not logged in|\b401\b/i'],
        ['codex_auth', 'codex', 'all', '/\b401\b|Unauthorized|refresh token|codex login|not logged in|token_expired/i'],
        ['model_not_found', '', 'all', "/model_not_found|model is not supported|model (?:\\S+ )?(?:does not exist|not found)|There's an issue with the selected model|not_found_error[^\\n]*model/i"],
        ['bridge_stuck_job', '', 'error', '/bridge_stuck_job/i'],
        // Freedom F1 5b: was kind 'engie' (older rows keep it); the connector's own messages come from its manifest
        ['corporate', '', 'error', static fn(array $c): bool => (bool)preg_match(co_corporate_error_re(), $c['error'])],
        ['context_too_long', '', 'all', '/prompt is too long|context_length_exceeded|context window|maximum context/i'],
        ['session_missing', '', 'error', static fn(array $c): bool => (bool)preg_match('/No conversation found/i', $c['all'])
            || (preg_match('/session/i', $c['error']) && preg_match('/not found|no conversation|missing/i', $c['error']))],
        ['timeout', '', 'error', '/Agent exceeded timeout of \d+ minutes|timed? ?out/i'],
        ['network', '', 'all', "/ENOTFOUND|EAI_AGAIN|getaddrinfo|Can't reach the API server|remote name could not be resolved/i"],
        ['overloaded', '', 'error', '/overloaded|\b(?:529|503|502)\b|500 Internal|stream disconnected|Service Unavailable|Reconnecting|ECONNRESET|network error/i'],
        ['bridge_update_pause', '', 'error', '/Paused for the Bridge update/i'], // Bridge 1.16.0
        ['bridge_restart', '', 'error', '/BridgeAgent restarted/i'],
        ['cancelled', '', 'error', '/Cancellation requested/i'],
        ['busy', '', 'error', '/previous message in this chat is still running/i'],
        ['site_codex_no_tools', '', 'error', '/Site host cannot run Codex tools|code-mode host|creating threadpool failed|failed to spawn thread/i'],
        ['codex_crash', 'codex', 'error', '/panicked at|exitCode=128\b|non-unwinding panic/i'],
        ['thread_limit', '', 'error', '/\bEAGAIN\b|os error 11\b|Resource temporarily unavailable/i'],
        ['native_crash', '', 'error', '/exitCode=-1073740791\b|exitCode=-1073741819\b|0xC0000409|0xC0000005/i'],
        ['git_128', '', 'error', '/exitCode=128\b/i'],
        ['bad_permissions', '', 'error', '/permissions\.files must/i'],
        ['bad_request', '', 'error', '/not in the catalog of agent|is not allowed for (?:agent|project)|Unknown agent profile|Unknown project profile|mode must be/i'],
        ['folder_missing', '', 'error', '/Workspace does not exist|Context folder not found|Unknown context root|excluded folder name/i'],
        ['git_missing', '', 'error', '/requires a Git work tree|git\.exe was not found/i'],
        ['missing_artifacts', '', 'error', '/missing artifacts:\s*([^;\n]*)/i'],
    ];
}

/** Parts of a Bridge error summary that say nothing about the cause (diagnose.ts GENERIC_PARTS). */
function co_fail_generic(string $part): bool
{
    foreach (['/^timeout$/i', '/^exitCode=-?\d+$/i', '/^output not drained$/i', '/^structured final event missing$/i',
                 '/^structured result failed$/i', '/^agent error events:\s*\d+$/i', '/^Agent process exited with code -?\d+\.?$/i',
                 '/^Claude result subtype:\s*\S+$/i'] as $re) {
        if (preg_match($re, $part)) {
            return true;
        }
    }
    return false;
}

/** First non-generic line of the error (diagnose.ts firstAgentLine), or null. */
function co_fail_first_line(string $error, int $max = 500): ?string
{
    foreach (preg_split('/\r?\n/', $error) as $line) {
        $kept = array_filter(array_map('trim', preg_split('/;\s+/', $line)), static fn($p) => $p !== '' && !co_fail_generic($p));
        $joined = trim((string)preg_replace('/^(?:error|fatal)\s*:\s*/i', '', implode('; ', $kept)));
        if (mb_strlen($joined) >= 3) {
            return mb_substr($joined, 0, $max);
        }
    }
    return null;
}

/** The error as CO Web shows it: Bridge summary + up to 5 agent messages not already in it (finalize-lib, mergeAgentErrors). */
function co_fail_merge_error(array $res, ?string $rejection = null): string
{
    $errs = is_array($res['agentErrorMessages'] ?? null) ? $res['agentErrorMessages'] : [];
    $base = is_string($res['error'] ?? null) && $res['error'] !== '' ? $res['error'] : ($rejection ?: null);
    if ($base === null) {
        return implode("\n", array_filter($errs, 'is_string'));
    }
    $extra = [];
    foreach ($errs as $m) {
        if (!is_string($m)) {
            continue;
        }
        $m = trim($m);
        if ($m === '' || str_contains($base, $m)) {
            continue;
        }
        $m = mb_substr($m, 0, 1000);
        if (!in_array($m, $extra, true)) {
            $extra[] = $m;
        }
        if (count($extra) >= 5) {
            break;
        }
    }
    return $extra ? $base . "\n" . implode("\n", $extra) : $base;
}

/**
 * Diagnosis kind of a failure: the first matching rule (diagnose.ts order), else no_result (no error and no text: the
 * job failed without a result), timeout for a timed-out run, agent_exit_1 for exit code 1 (the cause is in the agent's
 * own messages), fallback otherwise.
 * Returns [kind, detail].
 */
function co_fail_diagnose(string $error, string $text, string $agent, ?int $exitCode, bool $timedOut): array
{
    $error = trim($error);
    $text = trim($text);
    $agent = strtolower($agent);
    $c = ['error' => $error, 'text' => $text, 'all' => $text !== '' ? $error . "\n" . $text : $error];
    $isCodex = $agent === 'codex';
    $isClaude = $agent === 'claude' || (co_corporate_agent($agent)['family'] ?? '') === 'claude';
    $kind = 'fallback';
    foreach (co_fail_rules() as [$k, $fam, $scope, $test]) {
        if (($fam === 'codex' && !$isCodex) || ($fam === 'claude' && !$isClaude)) {
            continue;
        }
        $hit = $test instanceof Closure ? $test($c) : (bool)preg_match($test . 'u', $scope === 'all' ? $c['all'] : $c['error']);
        if ($hit) {
            $kind = $k;
            break;
        }
    }
    if ($kind === 'fallback' && $error === '' && $text === '') {
        return ['no_result', 'failed without a result (exit code ' . ($exitCode ?? 'none') . ')'];
    }
    if ($kind === 'fallback' && $timedOut) {
        $kind = 'timeout';
    } elseif ($kind === 'fallback' && $exitCode === 1) {
        $kind = 'agent_exit_1';
    }
    $firstText = $text === '' ? null : mb_substr(trim(preg_split('/\r?\n/', $text)[0]), 0, 500);
    $detail = co_fail_first_line($error) ?? $firstText ?? ($error !== '' ? mb_substr($error, 0, 500) : null);
    return [$kind, $detail];
}

/** Builds (or rebuilds) the job_failures row of one job; false if the job is gone or did not fail. */
function co_job_failure_build(mysqli $db, string $jobId): bool
{
    $st = $db->prepare('SELECT job_id, env, project_id, chat_id, host_id, agent, model, state, status, rejection,
                               COALESCE(completed_utc, synced_utc) AS done_utc, result, sha FROM jobs WHERE job_id = ?');
    $st->execute([$jobId]);
    $j = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$j) {
        return false;
    }
    $res = json_decode((string)($j['result'] ?? ''), true);
    $res = is_array($res) ? $res : [];
    $status = (string)($j['status'] ?: ($res['status'] ?? ''));
    if (!in_array($status, ['failed', 'timed_out'], true) && $j['state'] !== 'failed') {
        return false;
    }
    $exit = isset($res['exitCode']) && is_numeric($res['exitCode']) ? (int)$res['exitCode'] : null;
    $timedOut = !empty($res['timedOut']) || $status === 'timed_out';
    $error = co_fail_merge_error($res, is_string($j['rejection'] ?? null) ? $j['rejection'] : null);
    $text = is_string($res['agentFinalResponse'] ?? null) ? $res['agentFinalResponse'] : '';
    [$kind, $detail] = co_fail_diagnose($error, $text, (string)$j['agent'], $exit, $timedOut);

    // Last events: stderr tail (agent stderr lines) and the very last event of the job.
    $st = $db->prepare('SELECT data FROM job_events WHERE job_id = ? ORDER BY first_seq DESC LIMIT 20');
    $st->execute([$jobId]);
    $r = $st->get_result();
    $events = [];
    while ($row = $r->fetch_row()) {
        $b = json_decode((string)$row[0], true);
        foreach (is_array($b) ? ($b['events'] ?? []) : [] as $e) {
            if (is_array($e) && isset($e['sequence'])) {
                $events[(int)$e['sequence']] = $e;
            }
        }
    }
    $r->free();
    $st->close();
    ksort($events);
    $stderr = '';
    $last = null;
    foreach ($events as $e) {
        $last = $e;
        if (($e['type'] ?? '') === 'stderr') {
            $stderr .= rtrim((string)($e['content'] ?? '')) . "\n";
        }
    }
    $stderr = $stderr === '' ? null : mb_substr(trim($stderr), -2000);
    $lastEvent = $last === null ? null : mb_substr(trim((string)($last['source'] ?? '') . '/' . (string)($last['type'] ?? '')
        . ': ' . (string)($last['content'] ?? '')), 0, 500);

    $doneUtc = $j['done_utc'] ?: gmdate('Y-m-d H:i:s');
    $st = $db->prepare('INSERT INTO job_failures (job_id, env, host_id, agent, model, project_id, chat_id, failed_utc, status, exit_code,
            kind, detail, stderr_tail, last_event, diag_ver, job_sha) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        ON DUPLICATE KEY UPDATE env = VALUES(env), host_id = VALUES(host_id), agent = VALUES(agent), model = VALUES(model),
            project_id = VALUES(project_id), chat_id = VALUES(chat_id), failed_utc = VALUES(failed_utc), status = VALUES(status),
            exit_code = VALUES(exit_code), kind = VALUES(kind), detail = VALUES(detail),
            stderr_tail = COALESCE(VALUES(stderr_tail), stderr_tail), last_event = COALESCE(VALUES(last_event), last_event),
            diag_ver = VALUES(diag_ver), job_sha = VALUES(job_sha)');
    $st->execute([
        $j['job_id'], $j['env'], $j['host_id'], $j['agent'], ($res['modelReported'] ?? null) ?: $j['model'], $j['project_id'], $j['chat_id'],
        $doneUtc, $status !== '' ? $status : 'failed', $exit, $kind, $detail, $stderr, $lastEvent, CO_FAIL_DIAG_VER, $j['sha'],
    ]);
    $st->close();
    return true;
}

/**
 * Cron step (drive-sync.php, every minute): rows of failed jobs of the last $days days that have none, whose jobs row
 * changed or that were built by older rules; at most $limit jobs and $seconds. Prunes host_events once an hour.
 */
function co_job_failures_fill(mysqli $db, int $limit = 60, float $seconds = 5.0, int $days = 2): int
{
    $until = microtime(true) + $seconds;
    $ids = $db->query("SELECT j.job_id FROM jobs j LEFT JOIN job_failures f ON f.job_id = j.job_id
                        WHERE (j.status IN ('failed', 'timed_out') OR j.state = 'failed')
                          AND COALESCE(j.completed_utc, j.synced_utc) >= UTC_TIMESTAMP(3) - INTERVAL " . max(1, $days) . " DAY
                          AND (f.job_id IS NULL OR NOT (f.job_sha <=> j.sha) OR f.diag_ver < " . CO_FAIL_DIAG_VER . ")
                        ORDER BY COALESCE(j.completed_utc, j.synced_utc) DESC LIMIT " . max(1, $limit))->fetch_all();
    $n = 0;
    foreach (array_column($ids, 0) as $id) {
        if (microtime(true) > $until) {
            break;
        }
        try {
            if (co_job_failure_build($db, (string)$id)) {
                $n++;
            }
        } catch (Throwable $e) {
            fwrite(STDERR, "job_failures {$id}: " . $e->getMessage() . "\n");
        }
    }
    if ((int)gmdate('i') === 11) {
        $db->query('DELETE FROM host_events WHERE utc < UTC_TIMESTAMP(3) - INTERVAL ' . CO_FAIL_EVENTS_DAYS . ' DAY');
    }
    return $n;
}

/** db.php op host_event (push key): one Bridge supervisor event. */
function co_host_event_put(mysqli $db, array $in): array
{
    $host = is_string($in['host'] ?? null) ? $in['host'] : '';
    $kind = is_string($in['kind'] ?? null) ? $in['kind'] : '';
    $detail = is_string($in['detail'] ?? null) ? trim($in['detail']) : '';
    if (!preg_match('/^[A-Za-z0-9_-]{1,40}$/', $host) || !preg_match('/^[a-z0-9_]{1,40}$/', $kind)) {
        throw new InvalidArgumentException('host and kind required');
    }
    $detail = (string)preg_replace('/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/', ' ', mb_substr($detail, 0, 500));
    $st = $db->prepare('INSERT INTO host_events (host_id, utc, kind, detail) VALUES (?, UTC_TIMESTAMP(3), ?, ?)');
    $st->execute([$host, $kind, $detail !== '' ? $detail : null]);
    $st->close();
    return ['ok' => true];
}

/** db.php op health (owner): failure causes of 24 h / 7 days with the newest example, and the last supervisor events. */
function co_health_report(mysqli $db, int $events = 20): array
{
    $rows = $db->query("SELECT f.kind,
                               SUM(f.failed_utc >= UTC_TIMESTAMP(3) - INTERVAL 1 DAY) AS n24, COUNT(*) AS n7,
                               MAX(f.failed_utc) AS last_utc
                          FROM job_failures f WHERE f.failed_utc >= UTC_TIMESTAMP(3) - INTERVAL 7 DAY
                         GROUP BY f.kind ORDER BY n24 DESC, n7 DESC, last_utc DESC")->fetch_all(MYSQLI_ASSOC);
    $last = $db->prepare('SELECT job_id, env, project_id, chat_id, host_id, agent, detail FROM job_failures
                           WHERE kind = ? AND failed_utc >= UTC_TIMESTAMP(3) - INTERVAL 7 DAY ORDER BY failed_utc DESC LIMIT 1');
    $failures = [];
    foreach ($rows as $r) {
        $last->execute([$r['kind']]);
        $x = $last->get_result()->fetch_assoc() ?: [];
        $failures[] = [
            'kind' => $r['kind'], 'n24' => (int)$r['n24'], 'n7' => (int)$r['n7'],
            'lastUtc' => co_fail_iso($r['last_utc']), 'lastJobId' => $x['job_id'] ?? null, 'lastEnv' => $x['env'] ?? null,
            'lastProjectId' => $x['project_id'] ?? null, 'lastChatId' => $x['chat_id'] ?? null,
            'lastHost' => $x['host_id'] ?? null, 'lastAgent' => $x['agent'] ?? null, 'lastDetail' => $x['detail'] ?? null,
        ];
    }
    $last->close();
    $ev = $db->query('SELECT utc, host_id, kind, detail FROM host_events ORDER BY utc DESC, id DESC LIMIT ' . max(1, min(100, $events)))
        ->fetch_all(MYSQLI_ASSOC);
    return [
        'generatedUtc' => gmdate('Y-m-d\TH:i:s\Z'),
        'failures' => $failures,
        'events' => array_map(static fn($e) => ['utc' => co_fail_iso($e['utc']), 'host' => $e['host_id'], 'kind' => $e['kind'],
            'detail' => $e['detail']], $ev),
    ];
}

function co_fail_iso(?string $sql): ?string
{
    return $sql ? str_replace(' ', 'T', substr($sql, 0, 19)) . 'Z' : null;
}
