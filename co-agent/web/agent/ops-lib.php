<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Job queue observability (2026-10-18): the Ops report for CO Web (db.php op "ops", owner only) and the
// "Jobs failing" push of cron/watch-cron.php. Callers load db-lib.php (and push-lib.php for the push).
declare(strict_types=1);

// SQL CASE expression that normalizes job state/status into a single outcome label.
function co_ops_outcome_sql(): string
{
    return "CASE"
        . " WHEN state = 'cancelled' OR status = 'cancelled'"
        . " OR (cancel_requested = 1 AND COALESCE(status, '') NOT IN ('completed', 'failed')) THEN 'cancelled'"
        . " WHEN status = 'failed' OR state IN ('failed', 'rejected') THEN 'failed'"
        . " WHEN status = 'completed' OR state = 'completed' THEN 'completed'"
        . " WHEN state = 'claimed' THEN 'running'"
        . " WHEN state = 'inbox' THEN 'queued'"
        . " ELSE 'other'"
        . " END";
}

// SQL expression picking the first available timestamp to anchor a job in time.
function co_ops_anchor_sql(): string
{
    return "COALESCE(queued_utc, started_utc, claimed_utc, completed_utc, synced_utc)";
}

// SQL expression extracting the most relevant error text for a job.
function co_ops_error_sql(): string
{
    return "COALESCE(JSON_VALUE(result,'$.error'), rejection, JSON_VALUE(result,'$.agentErrorMessages[0]'))";
}

// Normalizes an error message into a grouping key by masking variable parts.
function co_ops_error_key(?string $s): string
{
    $s = mb_strtolower(trim((string)$s), 'UTF-8');
    $s = preg_split('/\R/u', $s, 2)[0] ?? '';
    $s = mb_substr($s, 0, 160, 'UTF-8');

    $s = preg_replace('/\b(?:job|chat)-[a-z0-9_-]+/iu', '<id>', $s);
    $s = preg_replace('/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/iu', '<id>', $s);
    $s = preg_replace('/\b[0-9a-f]{8,}\b/iu', '<id>', $s);
    $s = preg_replace('/\d{4}-\d{2}-\d{2}[t ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:z|[+-]\d{2}:?\d{2})?/iu', '<t>', $s);
    $s = preg_replace('/\b[a-z]:\\\\[^\s"\'<>|]*/iu', '<path>', $s);
    $s = preg_replace('/(?<![\w.])(?:\/[^\s\/"\'<>]+){2,}\/?/u', '<path>', $s);
    $s = preg_replace('/\d+/u', '<n>', $s);
    $s = preg_replace('/\s+/u', ' ', $s);
    $s = trim((string)$s);

    return $s === '' ? '(no error text)' : $s;
}

// Nearest-rank percentile (p in 0-100) of an ascending-sorted array, null if empty.
function co_ops_pct(array $sorted, float $p): ?float
{
    $values = array_values($sorted);
    $n = count($values);
    if ($n === 0) {
        return null;
    }
    $rank = max(1, min($n, (int)ceil(($p / 100) * $n)));
    return (float)$values[$rank - 1];
}

// Parse 'Y-m-d H:i:s.v' UTC anchor to float epoch seconds, null if invalid.
function co_ops_anchor_ts(?string $a): ?float
{
    if ($a === null || $a === '') return null;
    $d = DateTimeImmutable::createFromFormat('!Y-m-d H:i:s.v', $a, new DateTimeZone('UTC'))
        ?: DateTimeImmutable::createFromFormat('!Y-m-d H:i:s', $a, new DateTimeZone('UTC'));
    return $d === false ? null : (float)$d->format('U.u');
}

// Re-runs after a failure (same chat within 30 min) and queue incidents.
function co_ops_retries(array $rows): array
{
    $out = ['retried' => 0, 'fallback' => 0, 'stuckAlerts' => 0, 'rejected' => 0, 'timeouts' => 0];
    $byChat = [];
    foreach ($rows as $r) {
        // mysqlnd returns native ints for the flag columns
        if ((int)($r['fb'] ?? 0) === 1) $out['fallback']++;
        if ((int)($r['al'] ?? 0) === 1) $out['stuckAlerts']++;
        if ((int)($r['rj'] ?? 0) === 1) $out['rejected']++;
        if (in_array((string)($r['tmo'] ?? ''), ['true', '1'], true)) $out['timeouts']++;
        $cid = $r['chat_id'] ?? null;
        $ts = co_ops_anchor_ts($r['anchor'] ?? null);
        if ($cid === null || $cid === '' || $ts === null) continue;
        $byChat[$cid][] = ['a' => (string)$r['anchor'], 't' => $ts, 'o' => $r['outcome'] ?? null];
    }
    foreach ($byChat as $jobs) {
        usort($jobs, fn($x, $y) => strcmp($x['a'], $y['a']));
        $n = count($jobs);
        for ($i = 1; $i < $n; $i++) {
            // Look back for a failed/cancelled job within 30 minutes
            for ($j = $i - 1; $j >= 0; $j--) {
                $diff = $jobs[$i]['t'] - $jobs[$j]['t'];
                if ($diff > 1800) break;
                if ($diff > 0 && in_array($jobs[$j]['o'], ['failed', 'cancelled'], true)) { $out['retried']++; break; }
            }
        }
    }
    return $out;
}

// Top 10 error groups of failed jobs.
function co_ops_errors(array $rows): array
{
    $g = [];
    foreach ($rows as $r) {
        if (($r['outcome'] ?? null) !== 'failed') continue;
        $err = $r['err'] ?? null;
        $k = co_ops_error_key($err);
        $a = (string)($r['anchor'] ?? '');
        $g[$k] ??= ['key' => $k, 'count' => 0, 'a' => null, 'err' => null, 'agents' => [], 'hosts' => []];
        $g[$k]['count']++;
        // Keep error text of the latest job by anchor
        if ($g[$k]['a'] === null || strcmp($a, $g[$k]['a']) > 0) { $g[$k]['a'] = $a; $g[$k]['err'] = $err; }
        $g[$k]['agents'][$r['agent'] ?? 'unknown'] = true;
        $g[$k]['hosts'][$r['host_id'] ?? 'unknown'] = true;
    }
    $list = [];
    foreach ($g as $e) {
        $agents = array_map('strval', array_keys($e['agents'])); sort($agents, SORT_STRING);
        $hosts = array_map('strval', array_keys($e['hosts'])); sort($hosts, SORT_STRING);
        $list[] = [
            'key' => (string)$e['key'],
            'count' => $e['count'],
            'example' => $e['err'] === null ? '' : mb_substr((string)$e['err'], 0, 400),
            'lastUtc' => co_db_iso($e['a'] === '' ? null : $e['a']),
            'agents' => $agents,
            'hosts' => $hosts,
        ];
    }
    usort($list, fn($x, $y) => [$y['count'], (string)$y['lastUtc']] <=> [$x['count'], (string)$x['lastUtc']]);
    return array_slice($list, 0, 10);
}

// Jobs per UTC day of the rolling window, empty days included.
function co_ops_daily(array $rows, int $days): array
{
    if ($days < 1) return [];
    // Buckets cover the whole rolling window: from the UTC date of (now - days) to today
    $now = new DateTimeImmutable('now', new DateTimeZone('UTC'));
    $today = $now->setTime(0, 0);
    $map = [];
    for ($d = $now->modify("-{$days} days")->setTime(0, 0); $d <= $today; $d = $d->modify('+1 day')) {
        $k = $d->format('Y-m-d');
        $map[$k] = ['day' => $k, 'jobs' => 0, 'completed' => 0, 'failed' => 0, 'cancelled' => 0];
    }
    foreach ($rows as $r) {
        $d = substr((string)($r['anchor'] ?? ''), 0, 10);
        if (!isset($map[$d])) continue;
        $map[$d]['jobs']++;
        $o = $r['outcome'] ?? null;
        if (in_array($o, ['completed', 'failed', 'cancelled'], true)) $map[$d][$o]++;
    }
    return array_values($map);
}

// Ops report for the last $days days: outcomes per host and agent, durations, re-runs, top errors, daily trend.
function co_ops_report(mysqli $db, int $days): array
{
    $days = max(1, min(90, $days));
    $o = co_ops_outcome_sql(); $a = co_ops_anchor_sql(); $e = co_ops_error_sql();
    $st = $db->prepare("SELECT chat_id, host_id, agent, ($o) AS outcome, ($a) AS anchor,
        fallback_utc IS NOT NULL AS fb, alerted_utc IS NOT NULL AS al, rejection IS NOT NULL AS rj,
        JSON_VALUE(result,'\$.timedOut') AS tmo, CASE WHEN ($o)='failed' THEN ($e) END AS err,
        TIMESTAMPDIFF(MICROSECOND, queued_utc, claimed_utc)/1e6 AS wait_s,
        TIMESTAMPDIFF(MICROSECOND, COALESCE(started_utc, claimed_utc), completed_utc)/1e6 AS run_s,
        TIMESTAMPDIFF(MICROSECOND, COALESCE(queued_utc, claimed_utc, started_utc), completed_utc)/1e6 AS total_s
        FROM jobs WHERE ($a) >= UTC_TIMESTAMP() - INTERVAL ? DAY");
    $st->execute([$days]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();

    $keys = ['completed', 'failed', 'cancelled', 'running', 'queued'];
    $blank = ['jobs' => 0, 'completed' => 0, 'failed' => 0, 'cancelled' => 0, 'running' => 0, 'queued' => 0, 'other' => 0];
    $tally = function (array &$c, $out) use ($keys) { $c['jobs']++; $c[in_array($out, $keys, true) ? $out : 'other']++; };
    $clean = function (array $x): array {
        $x = array_values(array_filter(array_map(fn($v) => $v === null ? null : (float)$v, $x), fn($v) => $v !== null && $v >= 0));
        sort($x);
        return $x;
    };
    $stats = function (array $w, array $r, array $t) use ($clean): array {
        $res = [];
        foreach (['wait' => $w, 'run' => $r, 'total' => $t] as $k => $x) {
            $x = $clean($x);
            foreach ([50, 95] as $p) { $v = co_ops_pct($x, $p); $res[$k . 'P' . $p] = $v === null ? null : round($v, 1); }
        }
        return $res;
    };

    $totals = $blank; $W = $R = $T = []; $g = [];
    foreach ($rows as $row) {
        $tally($totals, $row['outcome']);
        $W[] = $row['wait_s']; $R[] = $row['run_s']; $T[] = $row['total_s'];
        $h = $row['host_id'] ?? 'unknown'; $ag = $row['agent'] ?? 'unknown'; $k = "$h|$ag";
        $g[$k] ??= ['hostId' => $h, 'agent' => $ag, 'c' => $blank, 'w' => [], 'r' => [], 't' => []];
        $tally($g[$k]['c'], $row['outcome']);
        $g[$k]['w'][] = $row['wait_s']; $g[$k]['r'][] = $row['run_s']; $g[$k]['t'][] = $row['total_s'];
    }

    $groups = array_map(fn($x) => ['hostId' => $x['hostId'], 'agent' => $x['agent']] + $x['c'] + $stats($x['w'], $x['r'], $x['t']), array_values($g));
    usort($groups, fn($p, $q) => $q['jobs'] <=> $p['jobs']);

    return [
        'days' => $days,
        'generatedUtc' => gmdate('Y-m-d\TH:i:s\Z'),
        'totals' => $totals,
        'durations' => $stats($W, $R, $T) + ['waitN' => count($clean($W)), 'runN' => count($clean($R))],
        'groups' => $groups,
        'retries' => co_ops_retries($rows),
        'errors' => co_ops_errors($rows),
        'daily' => co_ops_daily($rows, $days),
    ];
}

// Cron (every 5 min): one "Jobs failing" push to the owner when >= 3 jobs failed in the last hour or more than 20 %
// of >= 5 finished jobs failed in 24 h. Anti-spam: meta.jobs_fail_alert; the next push only after 6 h and a newer failure.
// $push replaces co_push_broadcast in tests.
function co_ops_alert_check(mysqli $db, ?callable $push = null): ?array
{
    $q = static function (string $sql, array $p = []) use ($db): mysqli_result {
        $st = $db->prepare($sql);
        $p ? $st->execute($p) : $st->execute();
        return $st->get_result();
    };

    $o = '(' . co_ops_outcome_sql() . ')';
    $t = 'COALESCE(completed_utc, ' . co_ops_anchor_sql() . ')';
    $since24 = "$t >= UTC_TIMESTAMP() - INTERVAL 24 HOUR";

    // One aggregate pass over the last 24 h
    $r = $q("SELECT
            COALESCE(SUM($o = 'failed' AND $t >= UTC_TIMESTAMP() - INTERVAL 1 HOUR), 0) AS h1,
            COALESCE(SUM($o IN ('completed', 'failed')), 0) AS n24,
            COALESCE(SUM($o = 'failed'), 0) AS f24,
            LEFT(DATE_FORMAT(MAX(CASE WHEN $o = 'failed' THEN $t END), '%Y-%m-%d %H:%i:%s.%f'), 23) AS last_failed
        FROM jobs
        WHERE $since24")->fetch_assoc() ?? [];

    $h1 = (int)($r['h1'] ?? 0);
    $n24 = (int)($r['n24'] ?? 0);
    $f24 = (int)($r['f24'] ?? 0);
    $last = $r['last_failed'] ?? null;

    if (!($h1 >= 3 || ($n24 >= 5 && $f24 / $n24 > 0.2))) return null;
    $reason = $h1 >= 3 ? 'h1' : 'share';

    // Anti-spam: cooldown of 6 h and require a newer failure
    $row = $q('SELECT v FROM meta WHERE k = ?', ['jobs_fail_alert'])->fetch_assoc();
    $prev = $row ? json_decode((string)$row['v'], true) : null;
    if (is_array($prev)) {
        $prevTs = strtotime(($prev['utc'] ?? '') . ' UTC');
        $cooledDown = $prevTs === false || $prevTs < time() - 6 * 3600;
        $newer = strcmp((string)$last, (string)($prev['lastFailedUtc'] ?? '')) > 0;
        if (!($cooledDown && $newer)) {
            return ['h1' => $h1, 'n24' => $n24, 'f24' => $f24, 'sent' => false, 'reason' => 'cooldown'];
        }
    }

    // Most frequent error key among failed jobs in 24 h
    $counts = [];
    foreach ($q('SELECT ' . co_ops_error_sql() . " AS err FROM jobs WHERE $o = 'failed' AND $since24")->fetch_all(MYSQLI_ASSOC) as $e) {
        $k = co_ops_error_key($e['err'] ?? null);
        $counts[$k] = ($counts[$k] ?? 0) + 1;
    }
    $top = '';
    $best = 0;
    foreach ($counts as $k => $c) {
        if ($c > $best) { $best = $c; $top = (string)$k; }
    }
    $top = mb_substr($top, 0, 120);

    $pct = $n24 > 0 ? (int)round($f24 / $n24 * 100) : 0;
    $body = sprintf('%d failed in the last hour; 24 h: %d of %d failed (%d%%).', $h1, $f24, $n24, $pct)
        . ($top !== '' ? " Top error: $top" : '');

    // Save the cooldown only after a delivered push (or when nobody is subscribed): a failed push retries in 5 min.
    $out = ['h1' => $h1, 'n24' => $n24, 'f24' => $f24, 'body' => $body];
    $push ??= function_exists('co_push_broadcast') ? 'co_push_broadcast' : null;
    if ($push === null) {
        return $out + ['sent' => false, 'reason' => 'no_push'];
    }
    try {
        $res = $push(
            static fn($s): bool => is_array($s) && co_owner_env() !== '' && ($s['env'] ?? '') === co_owner_env(),
            ['title' => 'Jobs failing', 'body' => $body, 'url' => '/', 'tag' => 'jobs-failed'],
            'jobs-failed'
        );
    } catch (Throwable $e) {
        return $out + ['sent' => false, 'reason' => 'push_failed'];
    }
    if ((int)($res['sent'] ?? 0) === 0 && (int)($res['failed'] ?? 0) > 0) {
        return $out + ['sent' => false, 'reason' => 'push_failed', 'push' => $res];
    }

    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute(['jobs_fail_alert', json_encode(['utc' => gmdate('Y-m-d H:i:s'), 'lastFailedUtc' => $last], JSON_UNESCAPED_SLASHES)]);
    $st->close();

    return $out + ['sent' => true, 'reason' => $reason, 'push' => $res];
}
