<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// jobstats-lib.php: per-job timeline and cost summary (schema 15, 2026-11-01).
// job_events (the raw Bridge batches) are deleted after 14 days; job_stats keeps for good what the job page and the
// usage report need: tokens, reported and API-equivalent cost, tool calls and errors, and a compact timeline (steps,
// cleared after 180 days). Rows are built by cron/drive-sync.php (co_job_stats_fill) and read by db.php op job_stats.
declare(strict_types=1);

const CO_JS_MAX_STEPS = 300;
const CO_JS_LABEL_MAX = 60;
const CO_JS_STEPS_DAYS = 180;

// API list prices in USD per 1M tokens: [input, cached input, output]. meta.api_prices (same shape) overrides entries.
// Keys match the model id by prefix, longest first; "<agent>:default" is used when the job names no model.
// Codex reports no cost, Claude Code does (its costUsd is taken as is). OpenAI figures as of 2026-11-01 from third-party
// price lists (pricepertoken.com, nops.io), not checked against developers.openai.com: set meta.api_prices to correct them.
const CO_JS_PRICES = [
    'gpt-6-sol' => [2.0, 0.2, 10.0],
    'gpt-6-luna' => [0.1, 0.01, 0.5],
    'gpt-5.6-sol' => [4.0, 0.4, 20.0],
    'gpt-5.6-luna' => [0.2, 0.02, 1.2],
    'codex:default' => [2.0, 0.2, 10.0],
];

function co_js_prices(mysqli $db): array
{
    static $p = null;
    if ($p !== null) {
        return $p;
    }
    $p = CO_JS_PRICES;
    try {
        $r = $db->query("SELECT v FROM meta WHERE k = 'api_prices'")->fetch_row();
        $o = $r ? json_decode((string)$r[0], true) : null;
        if (is_array($o)) {
            foreach ($o as $k => $v) {
                if (is_string($k) && is_array($v) && count($v) === 3) {
                    $p[$k] = array_map('floatval', array_values($v));
                }
            }
        }
    } catch (Throwable) {
    }
    uksort($p, fn($a, $b) => strlen($b) <=> strlen($a));
    return $p;
}

/** API-equivalent USD of a usage block by the price table; null = model unknown. */
function co_js_price_usage(mysqli $db, ?string $agent, ?string $model, array $u): ?float
{
    $prices = co_js_prices($db);
    $row = null;
    $m = strtolower((string)$model);
    if ($m !== '') {
        foreach ($prices as $k => $v) {
            if (!str_contains($k, ':') && str_starts_with($m, $k)) {
                $row = $v;
                break;
            }
        }
    } elseif ($agent !== null && isset($prices[$agent . ':default'])) {
        $row = $prices[$agent . ':default'];
    }
    if ($row === null) {
        return null;
    }
    $in = (float)($u['inputTokens'] ?? 0);
    $cached = (float)($u['cachedInputTokens'] ?? 0);
    $out = (float)($u['outputTokens'] ?? 0);
    if ($agent === 'codex') {
        $in = max(0.0, $in - $cached); // Codex counts cached tokens inside inputTokens
    } else {
        $in += (float)($u['cacheCreationInputTokens'] ?? 0);
    }
    return round(($in * $row[0] + $cached * $row[1] + $out * $row[2]) / 1e6, 4);
}

function co_js_short(string $s, int $n = CO_JS_LABEL_MAX): string
{
    $t = trim((string)preg_replace('/\s+/u', ' ', $s));
    return mb_strlen($t) > $n ? mb_substr($t, 0, $n - 1) . '…' : $t;
}

function co_js_ms(?string $utc): ?int
{
    if ($utc === null || $utc === '') {
        return null;
    }
    try {
        $d = new DateTimeImmutable($utc, new DateTimeZone('UTC'));
    } catch (Throwable) {
        return null;
    }
    return (int)floor((float)$d->format('U.u') * 1000);
}

/** Kind and short label of a Claude tool call (same groups as CO Web activity.ts). */
function co_js_claude_tool(string $name, array $in): array
{
    $p = (string)($in['file_path'] ?? $in['path'] ?? $in['notebook_path'] ?? '');
    $base = $p !== '' ? basename(str_replace('\\', '/', $p)) : '';
    switch ($name) {
        case 'Read':
            return ['read', 'Read ' . $base];
        case 'Write':
        case 'Edit':
        case 'MultiEdit':
        case 'NotebookEdit':
            return ['write', $name . ' ' . $base];
        case 'Bash':
        case 'PowerShell':
            return ['command', $name . ': ' . (string)($in['command'] ?? '')];
        case 'Grep':
        case 'Glob':
            return ['search', $name . ' ' . (string)($in['pattern'] ?? '')];
        case 'WebSearch':
            return ['search', 'WebSearch ' . (string)($in['query'] ?? '')];
        case 'WebFetch':
            return ['search', 'WebFetch ' . (string)($in['url'] ?? '')];
        case 'Task':
        case 'Agent':
            return ['agent', 'Agent: ' . (string)($in['description'] ?? '')];
    }
    return ['tool', $name];
}

/** Tool name used in the per-tool counters (MCP tools without the server prefix noise). */
function co_js_tool_key(string $name): string
{
    if (preg_match('/^mcp__(.+?)__(.+)$/', $name, $m)) {
        return $m[1] . '.' . $m[2];
    }
    return $name;
}

/**
 * Timeline of one job from its event batches.
 * Returns [steps, tools, toolCalls, toolErrors, subagents, firstMs].
 * Step: [offsetMs, durMs|null, kind, label, err 0|1, depth, outTokens so far|null].
 */
function co_js_timeline(array $batches, ?int $t0): array
{
    $events = [];
    foreach ($batches as $b) {
        foreach ($b['events'] ?? [] as $e) {
            if (is_array($e) && isset($e['sequence'])) {
                $events[(int)$e['sequence']] = $e;
            }
        }
    }
    ksort($events);
    $steps = [];
    $open = [];      // tool id -> index in $steps
    $tools = [];     // key -> [calls, errors, ms]
    $calls = $errors = $subagents = 0;
    $out = null;     // output tokens so far (Claude assistant usage / Codex turn usage)
    $seenMsg = [];
    $codexOut = 0;
    foreach ($events as $e) {
        $ms = co_js_ms($e['createdUtc'] ?? null);
        if ($ms === null) {
            continue;
        }
        if ($t0 === null) {
            $t0 = $ms;
        }
        $off = max(0, $ms - $t0);
        $src = (string)($e['source'] ?? '');
        $type = (string)($e['type'] ?? '');
        if ($src !== 'agent') {
            if (preg_match('/fail|error|timeout|missing|reject|warning/', $type)) {
                $steps[] = [$off, null, 'bridge', co_js_short($type . ': ' . (string)($e['content'] ?? '')), 1, 0, $out];
            }
            continue;
        }
        if ($type !== 'stdout') {
            continue;
        }
        $ev = json_decode((string)($e['content'] ?? ''), true);
        if (!is_array($ev)) {
            continue;
        }
        $t = (string)($ev['type'] ?? '');
        $depth = empty($ev['parent_tool_use_id']) ? 0 : 1;
        if ($t === 'assistant') {
            $msg = is_array($ev['message'] ?? null) ? $ev['message'] : [];
            $mid = (string)($msg['id'] ?? '');
            if (is_array($msg['usage'] ?? null) && $mid !== '' && !isset($seenMsg[$mid])) {
                $seenMsg[$mid] = true;
                $out = (int)$out + (int)($msg['usage']['output_tokens'] ?? 0);
            }
            foreach (is_array($msg['content'] ?? null) ? $msg['content'] : [] as $c) {
                if (!is_array($c) || ($c['type'] ?? '') !== 'tool_use') {
                    continue;
                }
                $name = (string)($c['name'] ?? 'tool');
                [$kind, $label] = co_js_claude_tool($name, is_array($c['input'] ?? null) ? $c['input'] : []);
                if ($kind === 'agent') {
                    $subagents++;
                }
                $calls++;
                $k = co_js_tool_key($name);
                $tools[$k] ??= [0, 0, 0];
                $tools[$k][0]++;
                $open[(string)($c['id'] ?? '')] = count($steps);
                $steps[] = [$off, null, $kind, co_js_short($label), 0, $depth, $out, $k];
            }
        } elseif ($t === 'user') {
            $msg = is_array($ev['message'] ?? null) ? $ev['message'] : [];
            foreach (is_array($msg['content'] ?? null) ? $msg['content'] : [] as $c) {
                if (!is_array($c) || ($c['type'] ?? '') !== 'tool_result') {
                    continue;
                }
                $i = $open[(string)($c['tool_use_id'] ?? '')] ?? null;
                if ($i === null) {
                    continue;
                }
                unset($open[(string)$c['tool_use_id']]);
                $steps[$i][1] = max(0, $off - $steps[$i][0]);
                $k = $steps[$i][7];
                $tools[$k][2] += $steps[$i][1];
                if (!empty($c['is_error'])) {
                    $steps[$i][4] = 1;
                    $tools[$k][1]++;
                    $errors++;
                }
            }
        } elseif ($t === 'result') {
            if (!empty($ev['is_error'])) {
                $steps[] = [$off, null, 'error', co_js_short('Agent error: ' . (string)($ev['result'] ?? '')), 1, 0, $out];
            }
        } elseif ($t === 'item.started' || $t === 'item.completed') {
            $it = is_array($ev['item'] ?? null) ? $ev['item'] : [];
            $it_type = (string)($it['type'] ?? '');
            if (in_array($it_type, ['agent_message', 'reasoning', 'todo_list'], true)) {
                continue;
            }
            $id = (string)($it['id'] ?? '');
            if ($t === 'item.started' || !isset($open[$id])) {
                [$kind, $label, $k] = match ($it_type) {
                    'command_execution' => ['command', 'Shell: ' . (string)($it['command'] ?? ''), 'shell'],
                    'file_change' => ['write', 'Edit ' . implode(', ', array_map(fn($x) => basename(str_replace('\\', '/', (string)($x['path'] ?? ''))), is_array($it['changes'] ?? null) ? $it['changes'] : [])), 'file_change'],
                    'web_search' => ['search', 'WebSearch ' . (string)($it['query'] ?? ''), 'web_search'],
                    'mcp_tool_call' => ['tool', (string)($it['server'] ?? '') . '.' . (string)($it['tool'] ?? ''), (string)($it['server'] ?? '') . '.' . (string)($it['tool'] ?? '')],
                    'error' => ['error', 'Error: ' . (string)($it['message'] ?? ''), 'error'],
                    default => ['tool', $it_type, $it_type ?: 'item'],
                };
                if ($kind !== 'error') {
                    $calls++;
                    $tools[$k] ??= [0, 0, 0];
                    $tools[$k][0]++;
                }
                $open[$id] = count($steps);
                $steps[] = [$off, $kind === 'error' ? null : 0, $kind, co_js_short($label), $kind === 'error' ? 1 : 0, 0, $out, $k];
                if ($kind === 'error') {
                    $errors++;
                    unset($open[$id]);
                    continue;
                }
            }
            if ($t === 'item.completed' && isset($open[$id])) {
                $i = $open[$id];
                unset($open[$id]);
                $steps[$i][1] = max(0, $off - $steps[$i][0]);
                $k = $steps[$i][7];
                $tools[$k][2] += $steps[$i][1];
                if (($it['status'] ?? '') === 'failed' || (int)($it['exit_code'] ?? 0) !== 0) {
                    $steps[$i][4] = 1;
                    $tools[$k][1]++;
                    $errors++;
                }
            }
        } elseif ($t === 'turn.completed') {
            $codexOut += (int)($ev['usage']['output_tokens'] ?? 0);
            $out = $codexOut;
        } elseif ($t === 'turn.failed' || $t === 'error') {
            $steps[] = [$off, null, 'error', co_js_short('Error: ' . (string)($ev['error']['message'] ?? $ev['message'] ?? '')), 1, 0, $out];
        }
    }
    // Calls still open at the end ran until the last event (killed, timed out) and stay without a duration.
    foreach ($steps as $i => $s) {
        unset($steps[$i][7]);
    }
    if (count($steps) > CO_JS_MAX_STEPS) {
        // Keep every error, then the longest steps, in time order.
        $idx = array_keys($steps);
        usort($idx, fn($a, $b) => [$steps[$b][4], $steps[$b][1] ?? 0] <=> [$steps[$a][4], $steps[$a][1] ?? 0]);
        $idx = array_slice($idx, 0, CO_JS_MAX_STEPS);
        sort($idx);
        $steps = array_map(fn($i) => $steps[$i], $idx);
    }
    uasort($tools, fn($a, $b) => $b[0] <=> $a[0]);
    return [array_values($steps), array_slice($tools, 0, 30, true), $calls, $errors, $subagents, $t0];
}

/** Outcome as ops-lib.php counts it. */
function co_js_outcome(array $j, array $res): string
{
    $state = (string)($j['state'] ?? '');
    $status = (string)($j['status'] ?? ($res['status'] ?? ''));
    if ($state === 'cancelled' || $status === 'cancelled') {
        return 'cancelled';
    }
    if ($status === 'failed' || $state === 'failed' || $state === 'rejected' || ($status !== '' && $status !== 'completed')) {
        return 'failed';
    }
    return 'completed';
}

/** Builds and stores the job_stats row of one job. */
function co_job_stats_build(mysqli $db, string $jobId): bool
{
    $st = $db->prepare('SELECT j.job_id, j.env, j.project_id, j.chat_id, j.host_id, j.agent, j.model, j.state, j.status,
                               j.queued_utc, j.claimed_utc, j.started_utc, j.completed_utc, j.result, j.sha,
                               JSON_VALUE(c.data, \'$.parent.chatId\') AS parent_chat, JSON_VALUE(c.data, \'$.parent.kind\') AS parent_kind
                          FROM jobs j LEFT JOIN chats c ON c.chat_id = j.chat_id WHERE j.job_id = ?');
    $st->execute([$jobId]);
    $j = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$j) {
        return false;
    }
    $res = json_decode((string)($j['result'] ?? ''), true);
    $res = is_array($res) ? $res : [];
    $u = is_array($res['usage'] ?? null) ? $res['usage'] : [];
    $model = (string)($res['modelReported'] ?? '') ?: (string)($j['model'] ?? '');
    $startUtc = $j['started_utc'] ?: ($res['startedUtc'] ?? null) ?: $j['claimed_utc'];
    $doneUtc = $j['completed_utc'] ?: ($res['completedUtc'] ?? null);
    $t0 = co_js_ms($j['claimed_utc'] ?: $startUtc);

    $st = $db->prepare('SELECT data FROM job_events WHERE job_id = ? ORDER BY first_seq LIMIT 2000');
    $st->execute([$jobId]);
    $r = $st->get_result();
    $batches = [];
    while ($row = $r->fetch_row()) {
        $b = json_decode((string)$row[0], true);
        if (is_array($b)) {
            $batches[] = $b;
        }
    }
    $r->free();
    $st->close();
    [$steps, $tools, $calls, $errors, $subagents] = co_js_timeline($batches, $t0);

    $ms0 = co_js_ms($startUtc);
    $ms1 = co_js_ms($doneUtc);
    // Wall clock; Claude Code's durationMs leaves out waits (Monitor, background tasks) and is only the fallback.
    $dur = $ms0 !== null && $ms1 !== null && $ms1 >= $ms0 ? $ms1 - $ms0
        : (isset($u['durationMs']) && is_numeric($u['durationMs']) ? (int)$u['durationMs'] : null);
    $cost = isset($u['costUsd']) && is_numeric($u['costUsd']) ? round((float)$u['costUsd'], 4) : null;
    $api = $cost ?? ($u ? co_js_price_usage($db, $j['agent'], $model, $u) : null);
    $int = fn($k) => isset($u[$k]) && is_numeric($u[$k]) ? (int)$u[$k] : null;
    $parent = ($j['parent_kind'] ?? '') === 'subtask' ? $j['parent_chat'] : null;

    $row = [
        $j['job_id'], $j['env'], $j['project_id'], $j['chat_id'], $parent, $j['host_id'], $j['agent'], $model !== '' ? $model : null,
        co_js_outcome($j, $res), $j['queued_utc'], $startUtc ? co_js_sql_utc($startUtc) : null, $doneUtc ? co_js_sql_utc($doneUtc) : null, $dur,
        $int('inputTokens'), $int('cachedInputTokens'), $int('cacheCreationInputTokens'), $int('outputTokens'), $int('reasoningOutputTokens'),
        $cost, $api, $int('numTurns'), $calls, $errors, $subagents, $batches ? 1 : 0,
        $tools ? json_encode($tools, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) : null,
        $steps ? json_encode($steps, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE) : null,
        $j['sha'],
    ];
    // A row built from events is not replaced by one built after the events were deleted.
    $st = $db->prepare('INSERT INTO job_stats (job_id, env, project_id, chat_id, parent_chat_id, host_id, agent, model, outcome, queued_utc,
            started_utc, completed_utc, duration_ms, input_tokens, cached_tokens, cache_write_tokens, output_tokens, reasoning_tokens,
            cost_usd, api_usd, turns, tool_calls, tool_errors, subagents, has_trace, tools, steps, job_sha, built_utc)
        VALUES (' . implode(',', array_fill(0, 28, '?')) . ', UTC_TIMESTAMP(3))
        ON DUPLICATE KEY UPDATE env = VALUES(env), project_id = VALUES(project_id), chat_id = VALUES(chat_id),
            parent_chat_id = VALUES(parent_chat_id), host_id = VALUES(host_id), agent = VALUES(agent), model = VALUES(model),
            outcome = VALUES(outcome), queued_utc = VALUES(queued_utc), started_utc = VALUES(started_utc), completed_utc = VALUES(completed_utc),
            duration_ms = VALUES(duration_ms), input_tokens = VALUES(input_tokens), cached_tokens = VALUES(cached_tokens),
            cache_write_tokens = VALUES(cache_write_tokens), output_tokens = VALUES(output_tokens), reasoning_tokens = VALUES(reasoning_tokens),
            cost_usd = VALUES(cost_usd), api_usd = VALUES(api_usd), turns = VALUES(turns),
            tool_calls = IF(VALUES(has_trace) OR NOT has_trace, VALUES(tool_calls), tool_calls),
            tool_errors = IF(VALUES(has_trace) OR NOT has_trace, VALUES(tool_errors), tool_errors),
            subagents = IF(VALUES(has_trace) OR NOT has_trace, VALUES(subagents), subagents),
            tools = IF(VALUES(has_trace) OR NOT has_trace, VALUES(tools), tools),
            steps = IF(VALUES(has_trace) OR NOT has_trace, VALUES(steps), steps),
            has_trace = GREATEST(has_trace, VALUES(has_trace)),
            job_sha = VALUES(job_sha), built_utc = VALUES(built_utc)');
    $st->execute($row);
    $st->close();
    return true;
}

function co_js_sql_utc(string $utc): ?string
{
    $ms = co_js_ms($utc);
    if ($ms === null) {
        return null;
    }
    return gmdate('Y-m-d H:i:s', intdiv($ms, 1000)) . sprintf('.%03d', $ms % 1000);
}

/**
 * Cron step (drive-sync.php, every minute): builds rows of finished jobs that have none or whose jobs row changed,
 * newest first (their events are still there); at most $limit jobs and $seconds. Clears old steps once an hour.
 */
function co_job_stats_fill(mysqli $db, int $limit = 60, float $seconds = 8.0): int
{
    $until = microtime(true) + $seconds;
    $ids = $db->query("SELECT j.job_id FROM jobs j LEFT JOIN job_stats s ON s.job_id = j.job_id
                        WHERE (j.completed_utc IS NOT NULL OR j.result IS NOT NULL OR j.state IN ('cancelled', 'failed', 'rejected'))
                          AND (s.job_id IS NULL OR s.job_sha <> j.sha)
                        ORDER BY COALESCE(j.completed_utc, j.synced_utc) DESC LIMIT " . max(1, $limit))->fetch_all();
    // An event batch that arrives after the row was built does not change jobs.sha: rebuild those jobs too.
    $late = $db->query('SELECT DISTINCT e.job_id FROM job_events e JOIN job_stats s ON s.job_id = e.job_id
                         WHERE e.created_utc >= UTC_TIMESTAMP(3) - INTERVAL 1 DAY AND e.created_utc > s.built_utc
                         LIMIT ' . max(1, $limit))->fetch_all();
    $ids = array_unique(array_merge(array_column($ids, 0), array_column($late, 0)));
    $n = 0;
    foreach ($ids as $id) {
        if (microtime(true) > $until) {
            break;
        }
        try {
            if (co_job_stats_build($db, (string)$id)) {
                $n++;
            }
        } catch (Throwable $e) {
            fwrite(STDERR, "job_stats {$id}: " . $e->getMessage() . "\n");
        }
    }
    if ((int)gmdate('i') === 7) {
        $db->query('UPDATE job_stats SET steps = NULL WHERE steps IS NOT NULL AND completed_utc < UTC_TIMESTAMP() - INTERVAL ' . CO_JS_STEPS_DAYS . ' DAY');
    }
    return $n;
}

function co_js_view(array $r, bool $withSteps): array
{
    $num = fn($k) => $r[$k] === null ? null : (int)$r[$k];
    $v = [
        'jobId' => $r['job_id'], 'chatId' => $r['chat_id'], 'projectId' => $r['project_id'], 'hostId' => $r['host_id'],
        'agent' => $r['agent'], 'model' => $r['model'], 'outcome' => $r['outcome'],
        'queuedUtc' => $r['queued_utc'], 'startedUtc' => $r['started_utc'], 'completedUtc' => $r['completed_utc'],
        'durationMs' => $num('duration_ms'), 'inputTokens' => $num('input_tokens'), 'cachedTokens' => $num('cached_tokens'),
        'cacheWriteTokens' => $num('cache_write_tokens'), 'outputTokens' => $num('output_tokens'), 'reasoningTokens' => $num('reasoning_tokens'),
        'costUsd' => $r['cost_usd'] === null ? null : (float)$r['cost_usd'], 'apiUsd' => $r['api_usd'] === null ? null : (float)$r['api_usd'],
        'turns' => $num('turns'), 'toolCalls' => (int)$r['tool_calls'], 'toolErrors' => (int)$r['tool_errors'], 'subagents' => (int)$r['subagents'],
        'hasTrace' => (bool)$r['has_trace'],
    ];
    if (isset($r['title'])) {
        $v['title'] = $r['title'];
    }
    if ($withSteps) {
        $v['tools'] = $r['tools'] ? json_decode((string)$r['tools'], true) : null;
        $v['steps'] = $r['steps'] ? json_decode((string)$r['steps'], true) : null;
    }
    return $v;
}

/**
 * The job's summary with steps, and its subtask tree: jobs of subtask chats of the job's chat queued while it ran,
 * recursively (depth 3, 50 jobs). Builds the row on the spot when the cron has not reached it yet.
 * $canRead(env, chatId) filters the subtasks: a share on the parent chat does not open its child chats.
 */
function co_job_stats_get(mysqli $db, string $jobId, callable $canRead): ?array
{
    $q = function (string $where, array $args) use ($db): array {
        $st = $db->prepare('SELECT s.*, c.title FROM job_stats s LEFT JOIN chats c ON c.chat_id = s.chat_id WHERE ' . $where);
        $st->execute($args);
        $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
        $st->close();
        return $rows;
    };
    $rows = $q('s.job_id = ?', [$jobId]);
    if (!$rows) {
        try {
            co_job_stats_build($db, $jobId);
        } catch (Throwable) {
        }
        $rows = $q('s.job_id = ?', [$jobId]);
        if (!$rows) {
            return null;
        }
    }
    $root = co_js_view($rows[0], true);
    $budget = 50;
    $children = function (array $r, int $depth) use (&$children, $q, &$budget, $canRead): array {
        if ($depth > 3 || $budget <= 0 || !$r['chat_id']) {
            return [];
        }
        $from = $r['queued_utc'] ?: $r['started_utc'];
        $to = $r['completed_utc'];
        if (!$from || !$to) {
            return [];
        }
        $out = [];
        foreach ($q('s.parent_chat_id = ? AND s.queued_utc BETWEEN ? AND ? ORDER BY s.queued_utc LIMIT 20', [$r['chat_id'], $from, $to]) as $c) {
            if (!$canRead((string)$c['env'], (string)$c['chat_id'])) {
                continue;
            }
            if ($budget-- <= 0) {
                break;
            }
            $out[] = co_js_view($c, false) + ['children' => $children($c, $depth + 1)];
        }
        return $out;
    };
    $root['children'] = $children($rows[0], 1);
    return $root;
}
