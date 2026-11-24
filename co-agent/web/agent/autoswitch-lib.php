<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// autoswitch-lib.php: a job that failed on its subscription limit, sign-in or model is sent once to the other agent
// (Claude <-> Codex) with a note in the chat (CO Web 0.71.0). Called by co_finalize_job right after the failed
// assistant_message is written, so it also works for scheduled and night jobs with no open CO Web.
// The project must allow the other agent (owner.json allowedAgents). Safety: the first run must provably have done nothing. Its whole trace (job_events) has to be present from event 1
// up to the agent's own final record, without dropped events, follow-ups or artifacts, and contain no tool call.
// Switches: meta 'autoswitch_off' = '1' (all chats, owner in CO Web) and chat.json autoSwitch = false (one chat).
// One attempt: the retry's user_message carries autoSwitchFrom, and a retry is never switched again.
declare(strict_types=1);

require_once __DIR__ . '/gateway-lib.php';
require_once __DIR__ . '/executors-lib.php';
require_once __DIR__ . '/share-lib.php';

/** Errors where a repeat on the same agent is pointless; matched on the error and on a short reply. */
const CO_AUTOSWITCH_PATTERNS = [
    'limit' => '/usage limit|hit your (?:weekly |session |5-hour |daily )?limit|rate limit reached|Claude AI usage limit/i',
    'auth'  => '/Please run \/login|Not logged in|authentication_error|OAuth token (?:has )?expired|Invalid API key|codex login|token_expired|refresh token (?:was|has been|is) (?:revoked|expired|invalid)/i',
    'model' => '/model_not_found|model (?:is )?not supported|not supported when using|does not exist or you do not have access|unknown model/i',
];
// A reply longer than this is real work, not an error notice.
const CO_AUTOSWITCH_MAX_REPLY = 600;
// Claude assistant content blocks and Codex items that are not actions.
const CO_AUTOSWITCH_SAFE_BLOCKS = ['text', 'thinking', 'redacted_thinking'];
const CO_AUTOSWITCH_SAFE_ITEMS = ['agent_message', 'reasoning', 'todo_list', 'error'];
// Bridge events that mean something reached the outside or the run went on after its first turn, and the call of a
// gateway model (its event types, legacy wire names included, in gateway-lib.php).
const CO_AUTOSWITCH_BAD_BRIDGE = ['events_dropped', 'followup', 'artifact', ...CO_GATEWAY_EVENT_TYPES];
// F12: every Bridge executor ('home-main' and the registered ones), not the site agent
const CO_AUTOSWITCH_HOSTS = ['home-main'];

function co_autoswitch_host_ok(mixed $host): bool
{
    return is_string($host) && (in_array($host, CO_AUTOSWITCH_HOSTS, true)
        || ($host !== 'site' && ($r = co_exec_row(co_db(), $host)) !== null && (string)$r['kind'] === 'bridge'));
}

/** Which rule an error matches: [kind, matched text] or null. */
function co_autoswitch_reason(string $s): ?array
{
    foreach (CO_AUTOSWITCH_PATTERNS as $kind => $re) {
        if (preg_match($re, $s, $m)) {
            return [$kind, $m[0]];
        }
    }
    return null;
}

/**
 * Agents the project allows on the host, from its owner.json (kv host:<host>/owner, drive-sync): projects[<id>] first,
 * then the policy of the project's environment for chat projects. Null = unknown (no switch).
 */
function co_autoswitch_allowed_agents(mysqli $db, string $host, string $projectId, string $env): ?array
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['host:' . $host, 'owner']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $o = $row ? json_decode((string)$row[0], true) : null;
    if (is_array($o['data'] ?? null)) $o = $o['data']; // stored as the put item {scope, name, data}
    if (!is_array($o)) return null;
    $list = $o['projects'][$projectId]['allowedAgents'] ?? $o['chatProjectEnvironments'][$env]['allowedAgents'] ?? null;
    if (!is_array($list)) return null;
    $list = array_values(array_filter($list, 'is_string'));
    // Only agents the host runs (owner.json agents): the site host stopped offering GPT in site agent 1.2.1.
    if (is_array($o['agents'] ?? null)) $list = array_values(array_filter($list, fn($a) => array_key_exists($a, $o['agents'])));
    return $list;
}

/** Global switch (owner): true unless meta autoswitch_off = '1'. */
function co_autoswitch_enabled(mysqli $db): bool
{
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute(['autoswitch_off']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    return !($row && $row[0] === '1');
}

function co_autoswitch_set_enabled(mysqli $db, bool $on): void
{
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute(['autoswitch_off', $on ? '0' : '1']);
    $st->close();
}

/**
 * Reads the job's trace and decides whether it proves that nothing was done.
 * Returns null when it does, else a short reason (for the log).
 */
function co_autoswitch_trace_blocker(mysqli $db, string $jobId, string $agent): ?string
{
    $st = $db->prepare('SELECT first_seq, last_seq, data FROM job_events WHERE job_id = ? ORDER BY first_seq');
    $st->execute([$jobId]);
    $rows = $st->get_result()->fetch_all(MYSQLI_NUM);
    $st->close();
    if (!$rows) return 'no trace';
    $next = 1;
    $final = false;
    foreach ($rows as [$first, $last, $data]) {
        // batches must follow each other from event 1 on; a missing batch could hide an action
        if ((int)$first !== $next) return 'trace gap before ' . $first;
        $next = (int)$last + 1;
        $batch = json_decode((string)$data, true);
        if (!is_array($batch) || !is_array($batch['events'] ?? null)) return 'unreadable batch';
        foreach ($batch['events'] as $e) {
            if (!is_array($e)) return 'unreadable event';
            $src = $e['source'] ?? '';
            $type = $e['type'] ?? '';
            if ($src === 'bridge') {
                if (in_array($type, CO_AUTOSWITCH_BAD_BRIDGE, true)) return 'bridge ' . $type;
                continue;
            }
            if ($src !== 'agent' || $type !== 'stdout') continue; // stderr: text only
            if ($final) return 'output after the final record';
            // an unreadable line could be an action record: no switch
            $line = json_decode((string)($e['content'] ?? ''), true);
            if (!is_array($line)) return 'unreadable agent output';
            $lt = (string)($line['type'] ?? '');
            if ($agent === 'claude') {
                if ($lt === 'assistant' || $lt === 'user') {
                    foreach ((array)($line['message']['content'] ?? []) as $c) {
                        $ct = is_array($c) ? (string)($c['type'] ?? '') : 'text';
                        if (!in_array($ct, CO_AUTOSWITCH_SAFE_BLOCKS, true)) return 'claude ' . $ct;
                    }
                } elseif ($lt === 'system' && str_starts_with((string)($line['subtype'] ?? ''), 'task_')) {
                    return 'claude subagent';
                } elseif ($lt === 'result') {
                    $final = true;
                }
            } else {
                if (str_starts_with($lt, 'item.')) {
                    $it = (string)($line['item']['type'] ?? '');
                    if (!in_array($it, CO_AUTOSWITCH_SAFE_ITEMS, true)) return 'codex ' . $it;
                } elseif ($lt === 'turn.failed' || $lt === 'turn.completed') {
                    $final = true;
                }
            }
        }
    }
    // without the agent's own last record, later events (and actions) may be missing
    return $final ? null : 'no final record';
}

/**
 * Queues the failed job's message for the other agent. $u is the failed job's user_message, $error its error text,
 * $replyText the agent's reply. Returns the new jobId or null (not eligible or switched off).
 */
function co_autoswitch_try(array $req, array $res, array $u, ?string $error, string $replyText): ?string
{
    $from = $req['agent'] ?? null;
    if (!in_array($from, ['claude', 'codex'], true) || ($res['status'] ?? null) !== 'failed') return null;
    if (!co_autoswitch_host_ok($req['hostId'] ?? null) || !empty($u['autoSwitchFrom'])) return null;
    $reply = trim($replyText);
    // Claude writes its limit notice as the reply; any other reply is work that a repeat could duplicate
    $why = co_autoswitch_reason((string)$error);
    if ($reply !== '') {
        $r = mb_strlen($reply) <= CO_AUTOSWITCH_MAX_REPLY ? co_autoswitch_reason($reply) : null;
        if ($r === null) return null;
        $why ??= $r;
    }
    if ($why === null) return null;
    if (!empty($res['artifacts']) || !empty($req['attachments'])) return null;
    $chatId = (string)($req['chatId'] ?? '');
    $pid = (string)($req['projectId'] ?? '');
    $jobId = (string)($req['jobId'] ?? '');
    if (!co_db_id_ok($chatId) || !co_db_id_ok($pid) || !co_db_id_ok($jobId)) return null;

    $db = co_db();
    if (!co_autoswitch_enabled($db)) return null;
    $st = $db->prepare('SELECT data FROM chats WHERE chat_id = ?');
    $st->execute([$chatId]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $chat = $row ? json_decode((string)$row[0], true) : null;
    if (!is_array($chat) || ($chat['autoSwitch'] ?? true) === false) return null;
    // F15.2: a chat in the CO trash gets no new job
    if ((string)($chat['trashedUtc'] ?? '') !== '') return null;
    // messages waiting for this chat would go to the failing agent after the retry: leave the chat to the user
    $st = $db->prepare("SELECT 1 FROM job_followups WHERE (job_id = ? AND state IN ('offered', 'delivered'))
                           OR (chat_id = ? AND state IN ('pending', 'offered', 'sending')) LIMIT 1");
    $st->execute([$jobId, $chatId]);
    $busy = (bool)$st->get_result()->fetch_row();
    $st->close();
    if ($busy) return null;
    $blocker = co_autoswitch_trace_blocker($db, $jobId, $from);
    if ($blocker !== null) {
        error_log("autoswitch $jobId skipped: $blocker");
        return null;
    }

    $to = $from === 'codex' ? 'claude' : 'codex';
    // the project's agent policy holds: a Claude-only project never sends its chat to Codex (and the other way round)
    $allowed = co_autoswitch_allowed_agents($db, (string)$req['hostId'], $pid, (string)($req['environmentId'] ?? ''));
    if ($allowed === null || !in_array($to, $allowed, true)) {
        error_log("autoswitch $jobId skipped: $to not allowed in $pid");
        return null;
    }
    $names = ['claude' => 'Claude', 'codex' => 'Codex'];
    $label = ['limit' => 'usage limit', 'auth' => 'signed out', 'model' => 'model not available'][$why[0]];
    $detail = mb_substr(trim((string)preg_replace('/\s+/', ' ', $why[1])), 0, 80);
    $note = "[CO: auto-switch from {$names[$from]} to {$names[$to]} ({$label}: {$detail}); once, the chat setting can turn it off]";

    $new = $req;
    $new['jobId'] = co_autoswitch_job_id();
    $new['agent'] = $to;
    $new['model'] = null;
    $new['effort'] = null;
    $new['attachments'] = [];
    unset($new['task']);
    $instruction = is_string($req['instruction'] ?? null) ? $req['instruction'] : '';
    if (($req['conversation']['operation'] ?? 'new') !== 'new') {
        // the other agent has no session: give it the latest messages of the chat
        $st = $db->prepare("SELECT type, data FROM events WHERE chat_id = ? AND type IN ('user_message','assistant_message')
                            AND (job_id IS NULL OR job_id <> ?) ORDER BY seq DESC LIMIT 10");
        $st->execute([$chatId, $jobId]);
        $lines = [];
        foreach (array_reverse($st->get_result()->fetch_all(MYSQLI_ASSOC)) as $r) {
            $d = json_decode((string)$r['data'], true);
            $t = is_array($d) && is_string($d['text'] ?? null) ? trim($d['text']) : '';
            if ($t === '') continue;
            $lines[] = ($r['type'] === 'user_message' ? 'User: ' : 'Assistant: ') . mb_substr($t, 0, 1500);
        }
        $st->close();
        if ($lines) {
            $instruction = "[CO context: earlier conversation in this chat; you are starting a new session]\n\n"
                . implode("\n\n", $lines) . "\n\n[End of CO context]\n\n" . $instruction;
        }
    }
    $new['instruction'] = $note . "\n\n" . $instruction;
    $new['conversation'] = ['operation' => 'new', 'sessionId' => null];

    $json = co_db_json($new);
    if (strlen($json) > 2 * 1024 * 1024) return null;
    $sha = hash('sha256', co_db_json(['request' => $new, 'result' => null]));
    $env = (string)($new['environmentId'] ?? '');

    // The user_message goes first: once the job is queued the Bridge may finish it before we return, and
    // co_finalize_job needs the message to write the answer.
    $st = $db->prepare('SELECT COALESCE(MAX(seq), 0) + 1 AS s FROM events WHERE chat_id = ?');
    $st->execute([$chatId]);
    $seq = (int)$st->get_result()->fetch_assoc()['s'];
    $st->close();
    $ev = [
        'schemaVersion' => 1,
        'eventId'       => co_fin_event_id(),
        'sequence'      => $seq,
        'createdUtc'    => gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000),
        'chatId'        => $chatId,
        'branchId'      => is_string($u['branchId'] ?? null) ? $u['branchId'] : 'BR-root',
        'type'          => 'user_message',
        'jobId'         => $new['jobId'],
        'hostId'        => $new['hostId'],
        'agent'         => $to,
        'text'          => $note . "\n" . (is_string($u['text'] ?? null) ? $u['text'] : ''),
    ];
    if (isset($u['author'])) $ev['author'] = $u['author'];
    $ev += [
        'attachments'    => [],
        'returnFiles'    => [],
        'conversation'   => $new['conversation'],
        'contextMode'    => $u['contextMode'] ?? 'full',
        'mode'           => $u['mode'] ?? $new['mode'] ?? 'cowork',
        'model'          => null,
        'effort'         => null,
        'queue'          => 'db',
        'autoSwitchFrom' => $jobId,
    ];
    $put = co_db_put(['events' => [$ev]]);
    if (!empty($put['rejected'])) throw new RuntimeException('user_message rejected');

    try {
        // F15.2: the trash check and the INSERT hold the chat row (as job_submit and the trash clean-up do)
        $queued = share_job_insert_untrashed($db, $chatId, static function () use ($db, $new, $env, $pid, $chatId, $to, $json, $sha): void {
            $st = $db->prepare(
                "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc,
                                   request, sha, synced_utc, actor_env)
                 VALUES (?, ?, ?, ?, ?, ?, ?, NULL, 'inbox', 'db', UTC_TIMESTAMP(3), ?, ?, UTC_TIMESTAMP(3), ?)"
            );
            $st->execute([$new['jobId'], $new['hostId'], $env, $pid, $chatId, $to, co_db_s($new, 'mode'), $json, $sha, $env]);
            $st->close();
        });
        if (!$queued) {
            throw new RuntimeException('chat_trashed');
        }
    } catch (Throwable $e) {
        // no job: take the message back so the chat does not show a request nobody runs
        $d = $db->prepare('DELETE FROM events WHERE chat_id = ? AND job_id = ? AND type = ?');
        $d->execute([$chatId, $new['jobId'], 'user_message']);
        $d->close();
        throw $e;
    }
    return $new['jobId'];
}

function co_autoswitch_job_id(): string
{
    $r = '';
    foreach (str_split(random_bytes(6)) as $b) $r .= CO_FIN_ALPHABET[ord($b) % 32];
    return 'JOB-' . gmdate('YmdHis') . '-' . $r;
}
