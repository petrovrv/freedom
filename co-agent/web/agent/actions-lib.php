<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// actions-lib.php: confirmation cards (schema 18, CO Web 0.81.0). read-guard 0.2.0 holds risky actions of agents in all
// chats (send, purchase, delete, publish, git push; in a chat that read a suspected prompt injection also secrets and
// uploads) and asks the owner for "<code> да". Its hook event reaches job_events through Bridge (source guard, type
// read_guard, content = the event JSON); co_actions_from_batch files risky_action_held events into action_requests and
// pushes the chat owner once per request. "Waiting for you" shows pending requests as cards; Yes / No send the code with
// да / нет into the chat (the normal message path, so read-guard grant finds the owner's own message there).
declare(strict_types=1);

const CO_ACTION_KINDS = ['send', 'purchase', 'delete', 'publish', 'push', 'secret', 'submit', 'other'];
const CO_ACTION_TTL_H = 24;
const CO_ACTION_TITLE = [
    'send' => 'Send', 'purchase' => 'Buy / pay', 'delete' => 'Delete', 'publish' => 'Publish',
    'push' => 'git push', 'secret' => 'Use a secret', 'submit' => 'Submit in the browser', 'other' => 'Risky action',
];

/** Cut a string to $len characters (one line). */
function co_action_cut(mixed $s, int $len): string
{
    $t = trim((string)preg_replace('/\s+/u', ' ', is_string($s) ? $s : ''));
    return mb_strlen($t) > $len ? mb_substr($t, 0, $len - 1) . '…' : $t;
}

/** Called by db_job_post_events for each stored batch; never throws (a broken event must not lose the batch). */
function co_actions_from_batch(mysqli $db, string $jobId, array $batch): void
{
    foreach ($batch['events'] ?? [] as $e) {
        if (!is_array($e) || ($e['source'] ?? '') !== 'guard' || ($e['type'] ?? '') !== 'read_guard') continue;
        $ev = is_string($e['content'] ?? null) ? json_decode($e['content'], true) : null;
        if (!is_array($ev) || ($ev['type'] ?? '') !== 'risky_action_held') continue;
        try {
            co_action_file($db, $jobId, $ev);
        } catch (Throwable $ex) {
            error_log('co_actions_from_batch: ' . $ex->getMessage());
        }
    }
}

/** Insert or extend the request of (chat, code); push on a new row. */
function co_action_file(mysqli $db, string $jobId, array $ev): void
{
    $code = (string)($ev['code'] ?? '');
    if (!preg_match('/^\d{4}$/', $code)) return;
    $st = $db->prepare('SELECT j.chat_id, p.env, c.title FROM jobs j JOIN chats c ON c.chat_id = j.chat_id
                         JOIN projects p ON p.project_id = c.project_id WHERE j.job_id = ?');
    $st->execute([$jobId]);
    $job = $st->get_result()->fetch_assoc();
    $st->close();
    $scope = (string)($ev['scope'] ?? '');
    if (!$job && preg_match('/^CHAT-\d{14}-[0-9A-Z]{4}$/', $scope)) {
        // a job Bridge took from Drive (schedules) may reach the database only with its result
        $st = $db->prepare('SELECT c.chat_id, p.env, c.title FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
        $st->execute([$scope]);
        $job = $st->get_result()->fetch_assoc();
        $st->close();
    }
    // the hook knows its chat too (AIWB_CHAT_ID); the job row wins, the event may only name the same chat
    if (!$job || (str_starts_with($scope, 'CHAT-') && $scope !== $job['chat_id'])) return;
    $kind = in_array($ev['kind'] ?? null, CO_ACTION_KINDS, true) ? (string)$ev['kind'] : 'other';
    $summary = co_action_cut($ev['summary'] ?? $ev['action'] ?? '', 500);
    if ($summary === '') $summary = CO_ACTION_TITLE[$kind];
    $command = co_action_cut($ev['command'] ?? '', 500);
    $one = ['kind' => $kind, 'summary' => mb_substr($summary, 0, 300)];

    $st = $db->prepare('SELECT id, actions, state FROM action_requests WHERE chat_id = ? AND code = ?');
    $st->execute([$job['chat_id'], $code]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if ($row) {
        $list = json_decode((string)($row['actions'] ?? ''), true);
        $list = is_array($list) ? $list : [];
        if (!in_array($one, $list, true)) $list[] = $one;
        $list = array_slice($list, -8);
        // a code read-guard hands out again after a decision opens the request again (new pending, same row)
        $st = $db->prepare("UPDATE action_requests SET actions = ?, job_id = ?, kind = ?, summary = ?, command = ?,
                              tainted = GREATEST(tainted, ?),
                              state = IF(state = 'pending' AND expires_utc > UTC_TIMESTAMP(3), state, 'pending'),
                              created_utc = IF(state = 'pending' AND expires_utc > UTC_TIMESTAMP(3), created_utc, UTC_TIMESTAMP(3)),
                              expires_utc = UTC_TIMESTAMP(3) + INTERVAL " . CO_ACTION_TTL_H . " HOUR
                            WHERE id = ?");
        $st->execute([co_db_json($list), $jobId, $kind, $summary, $command ?: null, empty($ev['tainted']) ? 0 : 1, (int)$row['id']]);
        $st->close();
        return;
    }
    $st = $db->prepare("INSERT IGNORE INTO action_requests (chat_id, job_id, env, code, kind, summary, command, actions, tainted, state,
                          created_utc, expires_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending', UTC_TIMESTAMP(3),
                          UTC_TIMESTAMP(3) + INTERVAL " . CO_ACTION_TTL_H . " HOUR)");
    $st->execute([$job['chat_id'], $jobId, $job['env'], $code, $kind, $summary, $command ?: null, co_db_json([$one]),
        empty($ev['tainted']) ? 0 : 1]);
    $id = $st->affected_rows > 0 ? (int)$db->insert_id : 0;
    $st->close();
    if ($id > 0) co_action_push($db, $id, (string)$job['env'], $kind, $summary, (string)($job['title'] ?? ''));
}

/** Push to the chat owner's devices; no quiet hours (the job waits for the answer). */
function co_action_push(mysqli $db, int $id, string $env, string $kind, string $summary, string $chatTitle): void
{
    if (!function_exists('co_push_broadcast')) return;
    try {
        co_push_broadcast(
            static fn($sub): bool => is_array($sub) && ($sub['env'] ?? '') === $env,
            ['title' => 'Confirm: ' . CO_ACTION_TITLE[$kind], 'body' => co_action_cut($summary, 160) . ($chatTitle !== '' ? ' · ' . co_action_cut($chatTitle, 60) : ''),
                'url' => '/e/' . rawurlencode($env) . '/inbox#act-' . $id, 'tag' => 'action-' . $id],
            'action-' . $id
        );
        $db->query('UPDATE action_requests SET pushed_utc = UTC_TIMESTAMP(3) WHERE id = ' . $id);
    } catch (Throwable $e) {
        error_log('co_action_push: ' . $e->getMessage());
    }
}

/**
 * Pending requests of chats in $env (only the env's own person decides: project owner of a shared chat). A request
 * leaves the list once a later user message of the chat names its code (answered by card or by typing), or after 24 h.
 */
function co_actions_list(mysqli $db, string $env): array
{
    $emails = array_keys(array_filter(co_push_users(), static fn(string $e): bool => $e === $env));
    if (!$emails) return [];
    $ph = implode(',', array_fill(0, count($emails), '?'));
    $st = $db->prepare(
        "SELECT a.id, a.chat_id, a.code, a.kind, a.summary, a.command, a.actions, a.tainted, a.created_utc, a.expires_utc,
                c.project_id, c.title, p.title AS ptitle,
                (SELECT e.event_id FROM events e WHERE e.chat_id = a.chat_id ORDER BY e.seq DESC LIMIT 1) AS last_event,
                (SELECT e.type FROM events e WHERE e.chat_id = a.chat_id ORDER BY e.seq DESC LIMIT 1) AS last_type
           FROM action_requests a
           JOIN chats c ON c.chat_id = a.chat_id
           JOIN projects p ON p.project_id = c.project_id
          WHERE a.env = ? AND p.env = ? AND a.state <> 'expired' AND a.expires_utc > UTC_TIMESTAMP(3)
            -- the card goes when the owner's answer with the code is in the chat (Yes/No only record the decision first,
            -- so a failed send keeps the card); a shared editor's message with the code does not count (review)
            AND NOT EXISTS (SELECT 1 FROM events u WHERE u.chat_id = a.chat_id AND u.type = 'user_message'
                              AND u.created_utc > a.created_utc AND u.text REGEXP CONCAT('(^|[^0-9])', a.code, '([^0-9]|$)')
                              AND (u.writer IN ($ph) OR (u.writer IS NULL AND NOT EXISTS (SELECT 1 FROM shares s
                                   WHERE (s.kind = 'chat' AND s.target_id = a.chat_id) OR (s.kind = 'project' AND s.target_id = c.project_id)))))
          ORDER BY a.id DESC LIMIT 50"
    );
    $st->execute([$env, $env, ...$emails]);
    $out = [];
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        $acts = json_decode((string)($r['actions'] ?? ''), true);
        $out[] = [
            'id'           => 'act:' . $r['id'],
            'kind'         => 'action',
            'chatId'       => (string)$r['chat_id'],
            'projectId'    => (string)$r['project_id'],
            'chatTitle'    => (string)($r['title'] ?? ''),
            'projectTitle' => (string)($r['ptitle'] ?? ''),
            // quick send only when the agent's answer (asking for the code) is the newest event of the chat
            'eventId'      => ($r['last_type'] ?? '') === 'assistant_message' ? (string)$r['last_event'] : '',
            'agent'        => null,
            'utc'          => co_db_iso($r['created_utc']),
            'code'         => (string)$r['code'],
            'excerpt'      => (string)$r['summary'],
            'action'       => [
                'requestId' => (int)$r['id'],
                'kind'      => (string)$r['kind'],
                'title'     => CO_ACTION_TITLE[$r['kind']] ?? CO_ACTION_TITLE['other'],
                'summary'   => (string)$r['summary'],
                'command'   => (string)($r['command'] ?? ''),
                'actions'   => is_array($acts) ? array_values(array_filter($acts, 'is_array')) : [],
                'tainted'   => (int)$r['tainted'] === 1,
                'expiresUtc' => co_db_iso($r['expires_utc']),
            ],
        ];
    }
    $st->close();
    return $out;
}

/** Records the owner's Yes / No from a card (the chat message with the code is what read-guard checks). */
function co_action_decide(mysqli $db, string $myEnv, string $email, int $id, string $decision): array
{
    if (!in_array($decision, ['approved', 'denied'], true)) throw new InvalidArgumentException('decision approved|denied');
    $st = $db->prepare("SELECT a.state, a.env, p.env AS penv, a.chat_id, a.code FROM action_requests a JOIN chats c ON c.chat_id = a.chat_id
                         JOIN projects p ON p.project_id = c.project_id WHERE a.id = ?");
    $st->execute([$id]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$r) return ['ok' => false, 'status' => 404, 'error' => 'not_found'];
    if ($r['env'] !== $myEnv || $r['penv'] !== $myEnv) return ['ok' => false, 'status' => 403, 'error' => 'forbidden'];
    $st = $db->prepare("UPDATE action_requests SET state = ?, decided_utc = UTC_TIMESTAMP(3), decided_by = ? WHERE id = ? AND state <> 'expired'");
    $st->execute([$decision, $email, $id]);
    $n = $st->affected_rows;
    $st->close();
    return ['ok' => true, 'changed' => $n > 0, 'chatId' => (string)$r['chat_id'], 'code' => (string)$r['code']];
}
