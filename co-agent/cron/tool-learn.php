<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
/*
 * Tool library learning (schema 16, 2026-11-01). Called from watch-cron.php every 5 minutes.
 * 1. co_tool_usage_scan(): counts calls of library tools in job_events into tool_usage, incrementally (cursor
 *    meta.tool_usage_cursor = "created_utc|job_id|first_seq"); the first runs catch up with the whole history.
 *    A call is an aiwb-tools tool_run (via mcp) or a shell command naming ...AI-Workbench\tools\<name>\ or
 *    ...AIWorkbench\tools\<name>\ (via cli; Claude Bash/PowerShell, Codex command_execution). An error is a tool
 *    result with is_error (Claude) or a non-zero exit code / failed status (Codex). Calls outside agent jobs (Windows
 *    scheduled tasks) are not seen: the agent that carries out an archive proposal checks them.
 * 2. co_tool_learn_cron(): once a day 02:00-05:59 Europe/Brussels, after the scan has caught up, one model call (opus,
 *    no tools) over a digest of the last 24 h of jobs proposes new tools (repeated one-off code) and SKILL.md fixes
 *    (agents erred or got confused on a tool); archive proposals are computed without a model (no call in 60 days,
 *    tool older than 60 days, usage known for 60 days). Proposals wait in tool_candidates for the owner.
 * Manual run: php tool-learn.php scan | learn [--dry] | digest
 */
declare(strict_types=1);

require_once __DIR__ . '/boot.php';
require_once CO_AGENT_WEB . '/lib.php';
require_once CO_AGENT_WEB . '/db-lib.php';
require_once CO_AGENT_WEB . '/memory-lib.php';
require_once CO_AGENT_WEB . '/tools-lib.php';

const CO_TL_SCAN_ROWS     = 4000;   // job_events rows per scan run
const CO_TL_SCAN_SEC      = 40;
const CO_TL_PENDING_IDS   = 300;    // tool_use ids kept across runs to match later results
const CO_TL_MAX_PROPOSALS = 5;      // model proposals per night
const CO_TL_MAX_PENDING   = 15;     // pending new/skill proposals: wait for the owner before proposing more
const CO_TL_DIGEST_CHARS  = 90000;
const CO_TL_JOB_CHARS     = 3000;
const CO_TL_DIGEST_SEC    = 60;     // reading time of the digest
const CO_TL_IDLE_DAYS     = 60;
const CO_TL_KEEP_DEFAULT  = 'toolbox,devtree,aiwb-docs,aiwb-backup-check,aiwb-e2e,aiwb-evals,aiwb-smoke';

function co_tl_meta(mysqli $db, string $k): ?string
{
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([$k]);
    $v = $st->get_result()->fetch_row()[0] ?? null;
    return $v === null ? null : (string)$v;
}

function co_tl_meta_set(mysqli $db, string $k, string $v): void
{
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute([$k, $v]);
}

/** Library tool names in a shell command (each once). */
function co_tl_cli_tools(string $cmd, array $known): array
{
    if (!preg_match_all('~AI-?Workbench[\\\\/]+tools[\\\\/]+([a-z0-9][a-z0-9-]{0,59})[\\\\/]~i', $cmd, $m)) {
        return [];
    }
    $out = [];
    foreach ($m[1] as $n) {
        $n = strtolower($n);
        if (isset($known[$n])) {
            $out[$n] = true;
        }
    }
    return array_keys($out);
}

/**
 * Tool calls and results in one stdout line of an agent (Claude stream-json or Codex --json).
 *
 * @return list<array{t:string,id?:string,tool?:string,via?:string,err?:bool,input?:string,cmd?:string,file?:string,
 *                    content?:string,out?:string}>  t = call | result | code
 */
function co_tl_parse_line(string $content, array $known): array
{
    $j = json_decode($content, true);
    if (!is_array($j)) {
        return [];
    }
    $out = [];
    $type = (string)($j['type'] ?? '');
    if ($type === 'assistant' && is_array($j['message']['content'] ?? null)) {
        foreach ($j['message']['content'] as $c) {
            if (!is_array($c) || ($c['type'] ?? '') !== 'tool_use') {
                continue;
            }
            $name = (string)($c['name'] ?? '');
            $in = is_array($c['input'] ?? null) ? $c['input'] : [];
            $id = (string)($c['id'] ?? '');
            if ($name === 'mcp__aiwb-tools__tool_run') {
                $t = strtolower((string)($in['name'] ?? ''));
                if (isset($known[$t])) {
                    $args = is_array($in['args'] ?? null) ? implode(' ', array_map('strval', $in['args'])) : '';
                    $out[] = ['t' => 'call', 'id' => $id, 'tool' => $t, 'via' => 'mcp', 'input' => $args];
                }
            } elseif (in_array($name, ['Bash', 'PowerShell'], true) && is_string($in['command'] ?? null)) {
                $tools = co_tl_cli_tools($in['command'], $known);
                foreach ($tools as $t) {
                    $out[] = ['t' => 'call', 'id' => $id, 'tool' => $t, 'via' => 'cli', 'input' => $in['command']];
                }
                if ($tools === []) {
                    $out[] = ['t' => 'code', 'id' => $id, 'cmd' => $in['command']];
                }
            } elseif (in_array($name, ['Write'], true) && is_string($in['file_path'] ?? null)) {
                $out[] = ['t' => 'code', 'id' => $id, 'file' => $in['file_path'], 'content' => (string)($in['content'] ?? '')];
            }
        }
    } elseif ($type === 'user' && is_array($j['message']['content'] ?? null)) {
        foreach ($j['message']['content'] as $c) {
            if (is_array($c) && ($c['type'] ?? '') === 'tool_result') {
                $txt = $c['content'] ?? '';
                if (is_array($txt)) {
                    $txt = implode(' ', array_map(static fn($x) => is_array($x) ? (string)($x['text'] ?? '') : '', $txt));
                }
                $out[] = ['t' => 'result', 'id' => (string)($c['tool_use_id'] ?? ''), 'err' => ($c['is_error'] ?? false) === true,
                    'out' => (string)$txt];
            }
        }
    } elseif ($type === 'item.completed' && is_array($j['item'] ?? null)) {
        $it = $j['item'];
        $itype = (string)($it['type'] ?? '');
        $id = (string)($it['id'] ?? '');
        if ($itype === 'command_execution' && is_string($it['command'] ?? null)) {
            $err = (isset($it['exit_code']) && $it['exit_code'] !== null && (int)$it['exit_code'] !== 0)
                || in_array((string)($it['status'] ?? ''), ['failed', 'declined'], true);
            $tools = co_tl_cli_tools($it['command'], $known);
            foreach ($tools as $t) {
                $out[] = ['t' => 'call', 'id' => '', 'tool' => $t, 'via' => 'cli', 'err' => $err, 'input' => $it['command'],
                    'out' => (string)($it['aggregated_output'] ?? '')];
            }
            if ($tools === []) {
                $out[] = ['t' => 'code', 'id' => $id, 'cmd' => $it['command']];
            }
        } elseif ($itype === 'mcp_tool_call' && ($it['tool'] ?? '') === 'tool_run') {
            $a = $it['arguments'] ?? null;
            $a = is_string($a) ? json_decode($a, true) : $a;
            $t = strtolower(is_array($a) ? (string)($a['name'] ?? '') : '');
            if (isset($known[$t])) {
                $err = ($it['status'] ?? '') === 'failed' || !empty($it['error']);
                $args = is_array($a['args'] ?? null) ? implode(' ', array_map('strval', $a['args'])) : '';
                $out[] = ['t' => 'call', 'id' => '', 'tool' => $t, 'via' => 'mcp', 'err' => $err, 'input' => $args,
                    'out' => is_string($it['error'] ?? null) ? $it['error'] : ''];
            }
        }
    }
    return $out;
}

/** Agent stdout lines of a job_events batch. @return list<array{0:string,1:string}> [createdUtc, content] */
function co_tl_batch_lines(string $data): array
{
    $b = json_decode($data, true);
    $out = [];
    foreach (is_array($b['events'] ?? null) ? $b['events'] : [] as $e) {
        if (is_array($e) && ($e['type'] ?? '') === 'stdout' && is_string($e['content'] ?? null)) {
            $out[] = [(string)($e['createdUtc'] ?? ''), $e['content']];
        }
    }
    return $out;
}

/** Incremental usage scan. Returns ['rows' => n, 'calls' => n, 'caughtUp' => bool]. */
function co_tool_usage_scan(mysqli $db): array
{
    $known = co_tools_index();
    if ($known === []) {
        return ['rows' => 0, 'calls' => 0, 'caughtUp' => false, 'error' => 'no tool library copy on the server'];
    }
    $cur = explode('|', co_tl_meta($db, 'tool_usage_cursor') ?? '');
    if (count($cur) !== 3) {
        $first = $db->query('SELECT MIN(created_utc) FROM job_events')->fetch_row()[0] ?? null;
        if ($first === null) {
            return ['rows' => 0, 'calls' => 0, 'caughtUp' => true];
        }
        co_tl_meta_set($db, 'tool_usage_since', substr((string)$first, 0, 10));
        $cur = ['1970-01-01 00:00:00.000', '', '0'];
    }
    // tool_use ids waiting for their result; kv (LONGTEXT), meta.v holds only 200 characters (Codex review)
    $st = $db->prepare("SELECT data FROM kv WHERE scope = 'tool-learn' AND name = 'usage-pending'");
    $st->execute();
    $pending = json_decode((string)($st->get_result()->fetch_row()[0] ?? ''), true);
    $st->close();
    $pending = is_array($pending) ? $pending : [];

    $t0 = microtime(true);
    $st = $db->prepare('SELECT job_id, first_seq, created_utc, data FROM job_events
                         WHERE created_utc >= ? AND (created_utc, job_id, first_seq) > (?, ?, ?)
                         ORDER BY created_utc, job_id, first_seq LIMIT ' . CO_TL_SCAN_ROWS);
    $st->execute([$cur[0], $cur[0], $cur[1], (int)$cur[2]]);
    $res = $st->get_result();
    $acc = [];   // key tool|day|job|via => [calls, errors]
    $rows = 0;
    $calls = 0;
    $last = null;
    while ($r = $res->fetch_assoc()) {
        $rows++;
        $last = $r;
        $job = (string)$r['job_id'];
        $day = substr((string)$r['created_utc'], 0, 10);
        foreach (co_tl_batch_lines((string)$r['data']) as [, $line]) {
            foreach (co_tl_parse_line($line, $known) as $x) {
                if ($x['t'] === 'call') {
                    $key = $x['tool'] . '|' . $day . '|' . $job . '|' . $x['via'];
                    $acc[$key] ??= [0, 0];
                    $acc[$key][0]++;
                    $calls++;
                    if (!empty($x['err'])) {
                        $acc[$key][1]++;
                    } elseif (($x['id'] ?? '') !== '') {
                        $pending[$x['id']] = $key;
                    }
                } elseif ($x['t'] === 'result' && isset($pending[$x['id']])) {
                    $key = $pending[$x['id']];
                    unset($pending[$x['id']]);
                    if ($x['err']) {
                        $acc[$key] ??= [0, 0];
                        $acc[$key][1]++;
                    }
                }
            }
        }
        if (microtime(true) - $t0 > CO_TL_SCAN_SEC) {
            break;
        }
    }
    $st->close();
    if ($last === null) {
        return ['rows' => 0, 'calls' => 0, 'caughtUp' => true];
    }
    if (count($pending) > CO_TL_PENDING_IDS) {
        $pending = array_slice($pending, -CO_TL_PENDING_IDS, null, true);
    }
    $db->begin_transaction();
    try {
        $up = $db->prepare('INSERT INTO tool_usage (tool, day, job_id, via, calls, errors) VALUES (?, ?, ?, ?, ?, ?)
                            ON DUPLICATE KEY UPDATE calls = calls + VALUES(calls), errors = errors + VALUES(errors)');
        foreach ($acc as $key => [$c, $e]) {
            [$tool, $day, $job, $via] = explode('|', $key);
            $up->execute([$tool, $day, $job, $via, $c, $e]);
        }
        co_tl_meta_set($db, 'tool_usage_cursor', $last['created_utc'] . '|' . $last['job_id'] . '|' . $last['first_seq']);
        $pj = json_encode((object)$pending, JSON_UNESCAPED_SLASHES);
        $kv = $db->prepare("REPLACE INTO kv (scope, name, data, sha, updated_utc) VALUES ('tool-learn', 'usage-pending', ?, ?, UTC_TIMESTAMP(3))");
        $kv->execute([$pj, hash('sha256', $pj)]);
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    $caughtUp = strtotime((string)$last['created_utc'] . ' UTC') > time() - 3600 && $rows < CO_TL_SCAN_ROWS;
    return ['rows' => $rows, 'calls' => $calls, 'caughtUp' => $caughtUp];
}

/**
 * Masks credentials in job trace text before it goes into the model prompt (Codex review): key=value / key: value of
 * secret-like names (a quoted value whole), Authorization headers, user:password@ in URLs, private key blocks, known token formats (OpenAI /
 * Anthropic sk-, GitHub, Slack, AWS, Google), JWTs and any other run of 40+ token characters.
 */
function co_tl_redact(string $s): string
{
    $re = [
        '/-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)/s' => '[private key]',
        '/\b(authorization|proxy-authorization)\s*[:=]\s*(?:bearer|basic|token)?\s*[^\s"\',;]+/i' => '$1: [redacted]',
        '/\bbearer\s+[A-Za-z0-9._~+\/=-]{8,}/i' => 'Bearer [redacted]',
        '~(\b[a-z][a-z0-9+.-]*://)[^\s:/@"\']+:[^\s@/"\']+@~i' => '$1[redacted]@',
        '/((?:^|[^A-Za-z0-9])[A-Za-z0-9_.-]*(?:pass(?:word|wd)?|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|cookie|session[_-]?id|auth)[A-Za-z0-9_.-]*["\']?\s*(?:=|:|\s)\s*)(["\'])(?:(?!\2)[^\r\n]){1,400}\2/i' => '$1$2[redacted]$2',
        '/((?:^|[^A-Za-z0-9])[A-Za-z0-9_.-]*(?:pass(?:word|wd)?|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|cookie|session[_-]?id|auth)[A-Za-z0-9_.-]*["\']?\s*(?:=|:|\s)\s*["\']?)(?!\[redacted)[^\s"\',;&)}]{4,}/i' => '$1[redacted]',
        '/\b(?:sk|pk|rk)-[A-Za-z0-9_-]{16,}/' => '[token]',
        '/\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}/' => '[token]',
        '/\bxox[abprs]-[A-Za-z0-9-]{10,}/' => '[token]',
        '/\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/' => '[token]',
        '/\bAIza[0-9A-Za-z_-]{30,}|\bya29\.[0-9A-Za-z_-]{20,}/' => '[token]',
        '/\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/' => '[jwt]',
        '/(?<![A-Za-z0-9+\/_=-])[A-Za-z0-9+\/_=-]{40,}(?![A-Za-z0-9+\/_=-])/' => '[long token]',
    ];
    foreach ($re as $pattern => $to) {
        $s = preg_replace($pattern, $to, $s) ?? '';
    }
    return $s;
}

function co_tl_cut(string $s, int $n): string
{
    $s = co_tl_redact($s);
    $s = trim(preg_replace('/\s+/u', ' ', mb_scrub($s, 'UTF-8')) ?? '');
    return mb_strlen($s, 'UTF-8') > $n ? mb_substr($s, 0, $n, 'UTF-8') . '…' : $s;
}

/** True for ad-hoc code worth showing: inline scripts, heredocs, scripts written to files. */
function co_tl_is_code(array $x): bool
{
    if (isset($x['file'])) {
        return (bool)preg_match('/\.(py|ps1|mjs|js|ts|sh|php|cmd|bat)$/i', $x['file']);
    }
    $c = (string)($x['cmd'] ?? '');
    return mb_strlen($c) > 120 && (bool)preg_match('/<<\s*[\'"]?\w+|python[0-9.]*(\.exe)?\s+-[cI]?\s*(-|<<|"|\')|node\s+-e|@\'\s*\R|\|\s*python|import\s+\w+/i', $c);
}

/**
 * Digest of the jobs with agent events in the last 24 h. Returns [text, jobs] where jobs = jobId => [chatId, projectId].
 *
 * @return array{0:string,1:array<string,array{chatId:string,projectId:string}>}
 */
function co_tool_learn_digest(mysqli $db, array $known): array
{
    $jobs = $db->query("SELECT j.job_id, j.chat_id, j.project_id, j.agent, JSON_VALUE(j.request, '$.instruction') AS instr,
                               COALESCE(JSON_VALUE(j.result, '$.status'), j.state) AS status
                          FROM jobs j
                         WHERE j.job_id IN (SELECT DISTINCT job_id FROM job_events WHERE created_utc > UTC_TIMESTAMP() - INTERVAL 1 DAY)
                         ORDER BY j.queued_utc")->fetch_all(MYSQLI_ASSOC);
    $blocks = [];
    $meta = [];
    // only the batches of the window: a long job is not read from its start (Codex review); time-boxed as well
    $ev = $db->prepare('SELECT data FROM job_events WHERE job_id = ? AND created_utc > UTC_TIMESTAMP() - INTERVAL 1 DAY
                         ORDER BY first_seq');
    $t0 = microtime(true);
    foreach ($jobs as $j) {
        if (microtime(true) - $t0 > CO_TL_DIGEST_SEC) {
            break;
        }
        $jobId = (string)$j['job_id'];
        $ev->execute([$jobId]);
        $calls = [];
        $byId = [];
        $code = [];
        $errors = 0;
        foreach ($ev->get_result()->fetch_all(MYSQLI_NUM) as [$data]) {
            foreach (co_tl_batch_lines((string)$data) as [, $line]) {
                foreach (co_tl_parse_line($line, $known) as $x) {
                    if ($x['t'] === 'call') {
                        $k = count($calls);
                        $calls[$k] = ['tool' => $x['tool'], 'via' => $x['via'], 'input' => co_tl_cut((string)($x['input'] ?? ''), 200),
                            'err' => !empty($x['err']), 'out' => !empty($x['err']) ? co_tl_cut((string)($x['out'] ?? ''), 300) : ''];
                        if (($x['id'] ?? '') !== '') {
                            $byId[$x['id']][] = $k;
                        }
                    } elseif ($x['t'] === 'result' && isset($byId[$x['id']]) && $x['err']) {
                        foreach ($byId[$x['id']] as $k) {
                            $calls[$k]['err'] = true;
                            $calls[$k]['out'] = co_tl_cut($x['out'], 300);
                        }
                    } elseif ($x['t'] === 'code' && co_tl_is_code($x) && count($code) < 6) {
                        $code[] = isset($x['file'])
                            ? 'script ' . basename(str_replace('\\', '/', $x['file'])) . ': ' . co_tl_cut((string)$x['content'], 350)
                            : 'inline: ' . co_tl_cut((string)$x['cmd'], 350);
                    }
                }
            }
        }
        if ($calls === [] && $code === []) {
            continue;
        }
        $lines = [];
        foreach ($calls as $c) {
            $errors += $c['err'] ? 1 : 0;
            $lines[] = '  TOOL ' . $c['tool'] . ' (' . $c['via'] . '): ' . $c['input'] . ($c['err'] ? "\n    -> ERROR: " . $c['out'] : '');
        }
        foreach ($code as $c) {
            $lines[] = '  CODE ' . $c;
        }
        $head = 'JOB ' . $jobId . ' (agent ' . $j['agent'] . ', ' . $j['status'] . ', project ' . $j['project_id'] . '): '
            . co_tl_cut((string)($j['instr'] ?? ''), 300);
        $block = $head . "\n" . implode("\n", $lines);
        if (mb_strlen($block, 'UTF-8') > CO_TL_JOB_CHARS) {
            $block = mb_substr($block, 0, CO_TL_JOB_CHARS, 'UTF-8') . "\n  …";
        }
        // jobs with errors or ad-hoc code first: they are what the pass looks for
        $blocks[] = [($errors > 0 ? 2 : 0) + ($code !== [] ? 1 : 0), $block];
        $meta[$jobId] = ['chatId' => (string)$j['chat_id'], 'projectId' => (string)$j['project_id']];
    }
    $ev->close();
    usort($blocks, static fn($a, $b) => $b[0] <=> $a[0]);
    $text = '';
    foreach ($blocks as [, $b]) {
        if (mb_strlen($text, 'UTF-8') + mb_strlen($b, 'UTF-8') > CO_TL_DIGEST_CHARS) {
            break;
        }
        $text .= $b . "\n\n";
    }
    return [$text, $meta];
}

/** Archive proposals without a model. @return list<array> */
function co_tool_learn_archive(mysqli $db, array $known): array
{
    $since = co_tl_meta($db, 'tool_usage_since');
    $limit = gmdate('Y-m-d', time() - CO_TL_IDLE_DAYS * 86400);
    if ($since === null || $since > $limit) {
        return []; // usage not known long enough
    }
    $keep = array_flip(array_map('trim', explode(',', co_tl_meta($db, 'tool_learn_keep') ?? CO_TL_KEEP_DEFAULT)));
    $used = [];
    $st = $db->prepare('SELECT tool, MAX(day) FROM tool_usage GROUP BY tool');
    $st->execute();
    foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as [$t, $d]) {
        $used[(string)$t] = (string)$d;
    }
    $out = [];
    foreach ($known as $name => $info) {
        if (isset($keep[$name]) || ($info['firstDay'] ?? null) === null || $info['firstDay'] > $limit) {
            continue;
        }
        $lastDay = $used[$name] ?? null;
        if ($lastDay !== null && $lastDay > $limit) {
            continue;
        }
        $out[] = [
            'kind'  => 'archive',
            'tool'  => $name,
            'title' => 'Archive ' . $name . ' ' . $info['version'] . ': no agent job used it ' . ($lastDay === null ? 'since ' . $since : 'after ' . $lastDay),
            'text'  => 'Инструмент `' . $name . '` (' . $info['version'] . ', с ' . $info['firstDay'] . ') не вызывался в заданиях агентов '
                . ($lastDay === null ? 'ни разу с начала учёта (' . $since . ')' : 'с ' . $lastDay) . ' — больше ' . CO_TL_IDLE_DAYS
                . ' дней. Описание: ' . co_tl_cut($info['description'], 300),
            'evidence' => [],
            'sha'   => sha1('archive|' . $name . '|' . $info['version']),
        ];
    }
    return $out;
}

/**
 * Validates the model proposals. @param array<string,array> $jobs digest jobs
 * @return list<array>
 */
function co_tool_learn_validate(array $props, array $known, array $jobs): array
{
    $out = [];
    $seen = [];
    foreach ($props as $p) {
        if (count($out) >= CO_TL_MAX_PROPOSALS) {
            break;
        }
        if (!is_array($p)) {
            continue;
        }
        $kind = strtolower(trim((string)($p['kind'] ?? '')));
        $tool = strtolower(trim((string)($p['tool'] ?? '')));
        if (!in_array($kind, ['new', 'skill'], true) || !preg_match('/^[a-z0-9][a-z0-9-]{1,59}$/', $tool)) {
            continue;
        }
        if ($kind === 'skill' && !isset($known[$tool])) {
            continue;
        }
        $title = co_tl_cut((string)($p['title'] ?? ''), 280);
        $text = trim(mb_scrub((string)($p['text'] ?? ''), 'UTF-8'));
        if (mb_strlen($title, 'UTF-8') < 10 || mb_strlen($text, 'UTF-8') < 20) {
            continue;
        }
        $text = mb_substr($text, 0, 2000, 'UTF-8');
        $ev = [];
        foreach (is_array($p['jobs'] ?? null) ? $p['jobs'] : [] as $jid) {
            $jid = is_string($jid) ? trim($jid) : '';
            if (isset($jobs[$jid]) && !isset($ev[$jid])) {
                $ev[$jid] = ['jobId' => $jid, 'chatId' => $jobs[$jid]['chatId'], 'projectId' => $jobs[$jid]['projectId']];
            }
        }
        // a new tool needs code repeated in at least two jobs; a SKILL.md fix needs one job where the agent erred
        if (count($ev) < ($kind === 'new' ? 2 : 1)) {
            continue;
        }
        $sha = sha1($kind . '|' . $tool . '|' . co_memory_norm($title));
        if (isset($seen[$kind . '|' . $tool])) {
            continue;
        }
        $seen[$kind . '|' . $tool] = true;
        $out[] = ['kind' => $kind, 'tool' => $tool, 'title' => $title, 'text' => $text, 'evidence' => array_values($ev), 'sha' => $sha];
    }
    return $out;
}

/** One learning pass. $dry: nothing stored, the proposals are returned. */
function co_tool_learn(mysqli $db, bool $dry = false, int $timeoutSec = 280): array
{
    $known = co_tools_index();
    $props = co_tool_learn_archive($db, $known);
    $pending = (int)($db->query("SELECT COUNT(*) FROM tool_candidates WHERE status = 'pending' AND kind IN ('new', 'skill')")
        ->fetch_row()[0] ?? 0);
    $digestJobs = 0;
    $model = 'opus';
    if ($pending < CO_TL_MAX_PENDING) {
        [$digest, $jobs] = co_tool_learn_digest($db, $known);
        $digestJobs = count($jobs);
        if ($digest !== '') {
            // open proposals of the same tool are listed so that the model does not repeat them
            $open = [];
            foreach ($db->query("SELECT kind, tool, title, status FROM tool_candidates
                                  WHERE kind IN ('new', 'skill') ORDER BY id DESC LIMIT 60")->fetch_all(MYSQLI_ASSOC) as $o) {
                $open[] = '- ' . $o['kind'] . ' ' . $o['tool'] . ' (' . $o['status'] . '): ' . $o['title'];
            }
            $list = [];
            foreach ($known as $n => $i) {
                $list[] = '- ' . $n . ' ' . $i['version'] . ': ' . co_tl_cut($i['description'], 260);
            }
            $max = CO_TL_MAX_PROPOSALS;
            $openText = $open === [] ? '(none)' : implode("\n", $open);
            $toolText = implode("\n", $list);
            $prompt = <<<PROMPT
You improve the tool library of AI agents (a folder of small CLI tools, each with a SKILL.md that tells agents how to use it). Below is a digest of the agent jobs of the last 24 hours: library tool calls (TOOL, with ERROR when the call failed) and ad-hoc code the agents wrote (CODE).

Propose up to {$max} changes. Kinds:
- "new": the same kind of ad-hoc code (not a library tool) appears in at least TWO different jobs and would be reusable: propose a new tool, or an extension of an existing tool when one fits ("tool" = that existing tool's name). Not for one-off code tied to specific files.
- "skill": an agent erred or got confused on a library tool (wrong arguments, misread output, repeated retries, an error the SKILL.md could have prevented): propose a concrete fix of that tool's SKILL.md (or code). "tool" = the existing tool.
Rules:
- Only with clear evidence in the digest; "jobs" lists the JOB ids that show it. Never invent. Errors caused by the network, the site, permissions or a user's cancel are not SKILL.md problems.
- "tool": lowercase latin, digits, hyphens. "title": one line in English, under 150 characters. "text": in Russian, 2-6 sentences: what exactly to add or change and why, naming the observed errors or the repeated code.
- Never copy credentials, tokens, passwords or personal data into a proposal (the digest masks them as [redacted]).
- Do not repeat proposals listed as open or rejected below. An empty list is a valid and frequent answer.

Library tools:
{$toolText}

Proposals made before:
{$openText}

Digest:
{$digest}

Answer with JSON only:
{"proposals":[{"kind":"skill","tool":"…","title":"…","text":"…","jobs":["JOB-…"]}]}
PROMPT;
            $m = co_tl_meta($db, 'tool_learn_model');
            if (is_string($m) && preg_match('/^(claude-[a-z0-9-]+|opus|sonnet|haiku)$/', $m)) {
                $model = $m;
            }
            $answer = co_agent_claude($prompt, $model, $timeoutSec);
            $db = co_db(true);
            $j = co_agent_json_from($answer);
            if (!isset($j['proposals']) || !is_array($j['proposals'])) {
                throw new RuntimeException('model answer has no proposals array');
            }
            $props = array_merge($props, co_tool_learn_validate($j['proposals'], $known, $jobs));
        }
    }
    $stored = 0;
    if (!$dry) {
        $ins = $db->prepare("INSERT IGNORE INTO tool_candidates (kind, tool, title, text, evidence, norm_sha, status, model, created_utc)
                             VALUES (?, ?, ?, ?, ?, ?, 'pending', ?, UTC_TIMESTAMP(3))");
        foreach ($props as $p) {
            $ins->execute([$p['kind'], $p['tool'], $p['title'], $p['text'],
                json_encode($p['evidence'], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), $p['sha'],
                $p['kind'] === 'archive' ? null : $model]);
            $stored += $ins->affected_rows === 1 ? 1 : 0;
        }
    }
    $out = ['jobs' => $digestJobs, 'pending' => $pending, 'proposed' => count($props), 'stored' => $stored];
    if ($dry) {
        $out['proposals'] = $props;
    }
    return $out;
}

/** Cron entry: the usage scan every run, the learning pass once a night. Returns a log line or null. */
function co_tool_learn_cron(): ?string
{
    $lock = fopen(co_agent_home() . '/co-agent/state/tool-learn.lock', 'c');
    if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) {
        return null;
    }
    $db = co_db();
    $scan = co_tool_usage_scan($db);
    $log = isset($scan['error']) || ($scan['rows'] > 0 && !$scan['caughtUp']) ? 'tool usage scan: ' . json_encode($scan) : null;
    if (isset($scan['error'])) {
        return $log; // e.g. no library copy on the server (toolbox.py site-sync): logged every run until fixed
    }

    $now = new DateTimeImmutable('now', new DateTimeZone('Europe/Brussels'));
    $hour = (int)$now->format('G');
    $today = $now->format('Y-m-d');
    if ($hour < 2 || $hour > 5 || !$scan['caughtUp'] || co_tl_meta($db, 'tool_learn_day') === $today) {
        return $log;
    }
    $fk = 'tool_learn_fail_' . $today;
    $fails = (int)(co_tl_meta($db, substr($fk, 0, 40)) ?? '0');
    try {
        $res = co_tool_learn($db);
        $db = co_db(true);
        co_tl_meta_set($db, 'tool_learn_day', $today);
        return 'tool learn: ' . json_encode($res, JSON_UNESCAPED_UNICODE);
    } catch (Throwable $e) {
        // after 3 failures the night is given up, so a broken answer is not retried every 5 minutes until 06:00
        $db = co_db(true);
        co_tl_meta_set($db, substr($fk, 0, 40), (string)($fails + 1));
        if ($fails + 1 >= 3) {
            co_tl_meta_set($db, 'tool_learn_day', $today);
        }
        return 'tool learn failed (' . ($fails + 1) . '): ' . mb_substr($e->getMessage(), 0, 300);
    }
}

if (PHP_SAPI === 'cli' && realpath($_SERVER['SCRIPT_FILENAME'] ?? '') === __FILE__) {
    $cmd = $argv[1] ?? '';
    $db = co_db();
    if ($cmd === 'scan') {
        echo json_encode(co_tool_usage_scan($db)) . "\n";
    } elseif ($cmd === 'learn') {
        echo json_encode(co_tool_learn($db, in_array('--dry', $argv, true)), JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT) . "\n";
    } elseif ($cmd === 'digest') {
        [$text, $jobs] = co_tool_learn_digest($db, co_tools_index());
        echo $text . "\n(" . count($jobs) . " jobs, " . mb_strlen($text, 'UTF-8') . " chars)\n";
    } else {
        fwrite(STDERR, "usage: php tool-learn.php scan | learn [--dry] | digest\n");
        exit(1);
    }
}
