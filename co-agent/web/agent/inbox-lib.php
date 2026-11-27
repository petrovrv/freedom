<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// inbox-lib.php: "Waiting for you" - one list of everything agents wait on (CO Web 0.71.0, db.php ops inbox / inbox_mark).
// Chat items come from the newest message of a chat when it is an answer without a later user message and no job is
// running: questions ([[ask]] / [[options]] or a final '?'), captcha permission requests (browser-agent captcha request),
// browser confirmations, failed jobs; schema 18 adds confirmation cards of held risky actions (actions-lib.php). Memory items are pending memory candidates (schema 16: also rules from corrections);
// tools items are pending proposals of the tool library learning pass (owner only). Only chats and projects the user may
// write to are listed (own env, editor shares). Read and done marks are per user in kv (scope 'inbox/<email>').
declare(strict_types=1);

const CO_INBOX_DAYS = 7;
const CO_INBOX_MAX_CHATS = 300;
const CO_INBOX_MAX_MEMORY = 300;
const CO_INBOX_MEMORY_PER_PROJECT = 30;
const CO_INBOX_KEEP_MARKS = 800;
const CO_INBOX_MARKER_RE = '/\[\[\s*(?:options|choices|ask)\s*:[^\]\n]{1,600}?\]\]/i';
const CO_INBOX_CAPTCHA_RE = '/captcha|капч/iu';
const CO_INBOX_CONFIRM_RE = '/confirm_required|user_action_needed|please confirm|needs? your (?:confirmation|approval|ok)|подтвердите|нужно (?:ваше )?подтверждение|жду (?:вашего )?(?:подтверждения|разрешения)/iu';

/** Visible-with-write condition for projects p / chats c; 3 parameters: env, env, env. */
function co_inbox_write_sql(string $p = 'p', string $c = 'c'): string
{
    return "($p.env = ? OR $p.project_id IN (SELECT target_id FROM shares WHERE kind = 'project' AND env = ? AND role = 'editor')"
        . " OR $c.chat_id IN (SELECT target_id FROM shares WHERE kind = 'chat' AND env = ? AND role = 'editor'))";
}

/** Captcha code the user should answer with ("1234 да"), or null. */
function co_inbox_captcha_code(string $text): ?string
{
    if (preg_match('/(?<!\d)(\d{4})(?!\d)\s*[«"“]?\s*(?:да|yes|ok|ок)\b/iu', $text, $m)) return $m[1];
    if (preg_match('/(?:код|code)\D{0,20}?(?<!\d)(\d{4})(?!\d)/iu', $text, $m)) return $m[1];
    return null;
}

/** The text without quick-reply markers, whitespace collapsed, last $len characters. */
function co_inbox_excerpt(string $text, int $len = 320): string
{
    $t = trim((string)preg_replace('/\s+/u', ' ', (string)preg_replace(CO_INBOX_MARKER_RE, ' ', $text)));
    return mb_strlen($t) > $len ? '…' . ltrim(mb_substr($t, -$len)) : $t;
}

function co_inbox_marks(mysqli $db, string $email): array
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['inbox/' . $email, 'marks']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $d = $row ? json_decode((string)$row[0], true) : null;
    return [
        'read' => is_array($d['read'] ?? null) ? $d['read'] : [],
        'done' => is_array($d['done'] ?? null) ? $d['done'] : [],
    ];
}

/** F10 side jobs of modules (ext/1, modules-lib.php): their answers never wait for the user. */
function co_inbox_side_sql(mysqli $db): string
{
    require_once __DIR__ . '/modules-lib.php';
    $sql = '';
    foreach (co_module_side_keys($db) as $k) $sql .= "            AND JSON_EXTRACT(le.data, '$.$k') IS NULL
";
    return $sql;
}

/** Items waiting for the user of $env ($email keeps the read marks). */
function co_inbox_list(mysqli $db, string $env, string $email): array
{
    $items = [];
    $st = $db->prepare(
        "SELECT c.chat_id, c.project_id, c.title, p.title AS ptitle, le.event_id, le.agent, le.job_id, le.created_utc, le.text,
                JSON_VALUE(le.data, '$.status') AS status, LEFT(JSON_VALUE(le.data, '$.error'), 4000) AS error
           FROM chats c
           JOIN projects p ON p.project_id = c.project_id
           JOIN (SELECT e.chat_id, MAX(e.seq) AS max_seq
                   FROM events e
                   JOIN chats c2 ON c2.chat_id = e.chat_id
                   JOIN projects p2 ON p2.project_id = c2.project_id
                  WHERE " . co_inbox_write_sql('p2', 'c2') . "
                    AND e.type IN ('user_message', 'assistant_message') AND JSON_EXTRACT(e.data, '$.deleted') IS NULL
                    -- events, not chats.updated_utc: answers finalized by the server do not touch the chat row
                    AND e.created_utc > UTC_TIMESTAMP() - INTERVAL " . CO_INBOX_DAYS . " DAY
                  GROUP BY e.chat_id) m ON m.chat_id = c.chat_id
           JOIN events le ON le.chat_id = m.chat_id AND le.seq = m.max_seq AND le.type = 'assistant_message'
          WHERE " . co_inbox_write_sql('p', 'c') . " AND p.archived = 0
            AND COALESCE(JSON_VALUE(c.data, '$.archived'), 'false') <> 'true'
" . co_inbox_side_sql($db) . "
            -- 0.82.1: a [[silent]] answer (observer: nothing important) does not wait for the user
            AND LEFT(TRIM(le.text), 10) <> '[[silent]]'
            -- only answers that may need the user (classified exactly below), so routine replies do not fill the limit
            AND (JSON_VALUE(le.data, '$.status') = 'failed' OR RIGHT(TRIM(le.text), 1) = '?' OR le.text LIKE '%[[%'
                 OR le.text REGEXP 'captcha|капч|confirm|подтвер|user_action_needed|approval|разрешени')
            AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.chat_id = c.chat_id AND j.state IN ('inbox', 'claimed')
                              AND j.result IS NULL AND j.cancel_requested = 0
                              AND COALESCE(j.queued_utc, j.claimed_utc, j.synced_utc) > UTC_TIMESTAMP() - INTERVAL 1 DAY)
          ORDER BY le.created_utc DESC
          LIMIT " . CO_INBOX_MAX_CHATS
    );
    $st->execute([$env, $env, $env, $env, $env, $env]);
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        $text = (string)($r['text'] ?? '');
        $status = (string)($r['status'] ?? '');
        $item = [
            'id'           => 'ev:' . $r['event_id'],
            'chatId'       => (string)$r['chat_id'],
            'projectId'    => (string)$r['project_id'],
            'chatTitle'    => (string)($r['title'] ?? ''),
            'projectTitle' => (string)($r['ptitle'] ?? ''),
            'eventId'      => (string)$r['event_id'],
            'agent'        => $r['agent'] === null ? null : (string)$r['agent'],
            'utc'          => co_db_iso($r['created_utc']),
        ];
        if ($status === 'failed') {
            $item['kind'] = 'failed';
            $item['error'] = (string)($r['error'] ?? '');
            $item['excerpt'] = co_inbox_excerpt($text, 200);
        } elseif ($status === 'completed') {
            preg_match_all(CO_INBOX_MARKER_RE, $text, $mm);
            $markers = implode("\n", $mm[0]);
            // offered choices win: an answer that only talks about a captcha code still has its own buttons
            $code = $markers === '' && preg_match(CO_INBOX_CAPTCHA_RE, $text) ? co_inbox_captcha_code($text) : null;
            if ($code !== null) {
                $item['kind'] = 'captcha';
                $item['code'] = $code;
            } elseif (preg_match(CO_INBOX_CONFIRM_RE, $text)) {
                $item['kind'] = 'confirm';
            } elseif ($markers !== '' || str_ends_with(rtrim($text), '?')) {
                $item['kind'] = 'ask';
            } else {
                continue;
            }
            $item['excerpt'] = co_inbox_excerpt($text);
            $item['markers'] = $markers;
        } else {
            continue;
        }
        $items[] = $item;
    }
    $st->close();

    // pending memory candidates of projects the user may decide on (memory_decide needs write on the project);
    // one item per project, its id follows the newest candidate, so new candidates make it unread again
    $st = $db->prepare(
        "SELECT m.id, m.project_id, p.title AS ptitle, m.chat_id, c.title, m.tag, m.text, m.created_utc, m.kind, m.msg_seq,
                m.reason, m.target_env
           FROM memory_candidates m
           JOIN projects p ON p.project_id = m.project_id
           LEFT JOIN chats c ON c.chat_id = m.chat_id
          WHERE m.status = 'pending' AND p.archived = 0
            AND (p.env = ? OR p.project_id IN (SELECT target_id FROM shares WHERE kind = 'project' AND env = ? AND role = 'editor'))
          ORDER BY m.id DESC
          LIMIT " . CO_INBOX_MAX_MEMORY
    );
    $st->execute([$env, $env]);
    $byProject = [];
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        $pid = (string)$r['project_id'];
        if (!isset($byProject[$pid])) {
            $byProject[$pid] = [
                'id'           => 'mem:' . $r['id'],
                'kind'         => 'memory',
                'projectId'    => $pid,
                'projectTitle' => (string)($r['ptitle'] ?? ''),
                'utc'          => co_db_iso($r['created_utc']),
                'count'        => 0,
                'candidates'   => [],
            ];
        }
        $p = &$byProject[$pid];
        $p['count']++;
        if (count($p['candidates']) < CO_INBOX_MEMORY_PER_PROJECT) {
            $p['candidates'][] = [
                'id'        => (int)$r['id'],
                'tag'       => (string)$r['tag'],
                'text'      => (string)$r['text'],
                'chatId'    => (string)$r['chat_id'],
                'chatTitle' => (string)($r['title'] ?? ''),
                // schema 16: rules from corrections (tag env|project) show their message and the verbatim correction
                'kind'      => (string)($r['kind'] ?? 'add'),
                'msg'       => (int)$r['msg_seq'],
                'reason'    => $r['reason'] === null ? null : (string)$r['reason'],
                'targetEnv' => $r['target_env'] === null ? null : (string)$r['target_env'],
            ];
        }
        unset($p);
    }
    $st->close();
    foreach ($byProject as $p) {
        $items[] = $p;
    }

    // schema 16: proposals of the tool library learning pass - the owner's own library, so only in the owner's env
    if (co_is_owner_env($env) && (co_push_users()[strtolower($email)] ?? null) === $env) {
        $rows = $db->query("SELECT id, kind, tool, title, text, evidence, created_utc, offered_utc FROM tool_candidates
                             WHERE status = 'pending' ORDER BY id DESC LIMIT 30")->fetch_all(MYSQLI_ASSOC);
        if ($rows !== []) {
            $c = [];
            foreach ($rows as $r) {
                $ev = json_decode((string)($r['evidence'] ?? ''), true);
                $c[] = ['id' => (int)$r['id'], 'kind' => (string)$r['kind'], 'tool' => (string)$r['tool'],
                    'title' => (string)$r['title'], 'text' => (string)$r['text'],
                    'evidence' => is_array($ev) ? array_values(array_filter($ev, 'is_array')) : [],
                    'offeredUtc' => co_db_iso($r['offered_utc'])];
            }
            require_once __DIR__ . '/tools-lib.php';
            $items[] = [
                'id'           => 'tool:' . $rows[0]['id'],
                'kind'         => 'tools',
                'projectId'    => co_tools_project($db),
                'projectTitle' => 'Tool library',
                'utc'          => co_db_iso($rows[0]['created_utc']),
                'count'        => count($rows),
                'tools'        => $c,
            ];
        }
    }

    // schema 18: confirmation cards of held risky actions, only for the person of the chat's env (actions-lib.php);
    // the agent's answer asking for the same code is not listed a second time
    $myEnv = co_push_users()[strtolower($email)] ?? null;
    if ($myEnv === $env) {
        $acts = co_actions_list($db, $env);
        $cards = [];
        foreach ($acts as $a) $cards[$a['chatId']] = true;
        if ($cards) {
            $items = array_values(array_filter($items, static fn(array $it): bool =>
                !isset($it['chatId'], $cards[$it['chatId']]) || ($it['kind'] ?? '') === 'failed'));
        }
        array_push($items, ...$acts);
        // F14b-2 (apps-lib.php): outward actions of apps an agent asked for, Run / Decline in place
        if (function_exists('co_app_requests_inbox')) array_push($items, ...co_app_requests_inbox($db, $env));
    }

    $marks = co_inbox_marks($db, $email);
    $out = [];
    $unread = 0;
    foreach ($items as $it) {
        if (isset($marks['done'][$it['id']])) continue;
        $it['unread'] = !isset($marks['read'][$it['id']]);
        if ($it['unread']) $unread++;
        $out[] = $it;
    }
    usort($out, static fn(array $a, array $b): int => strcmp((string)$b['utc'], (string)$a['utc']));
    return ['items' => $out, 'unread' => $unread];
}

/** Marks items read / unread / done (hidden until the chat changes: a new answer has a new id). */
function co_inbox_mark(mysqli $db, string $email, array $in): array
{
    $ids = $in['ids'] ?? null;
    $action = $in['action'] ?? null;
    if (!is_array($ids) || !array_is_list($ids) || count($ids) === 0 || count($ids) > 300
        || !in_array($action, ['read', 'unread', 'done'], true)) {
        throw new InvalidArgumentException('ids (1-300) and action read|unread|done required');
    }
    foreach ($ids as $id) {
        if (!is_string($id) || !preg_match('/^(?:ev|mem|tool|act|app):[A-Za-z0-9_-]{1,80}$/', $id)) {
            throw new InvalidArgumentException('invalid id');
        }
    }
    $marks = co_inbox_marks($db, $email);
    $now = gmdate('Y-m-d\TH:i:s\Z');
    foreach ($ids as $id) {
        if ($action === 'unread') {
            unset($marks['read'][$id], $marks['done'][$id]);
        } else {
            $marks['read'][$id] = $now;
            if ($action === 'done') $marks['done'][$id] = $now;
        }
    }
    foreach (['read', 'done'] as $k) {
        if (count($marks[$k]) > CO_INBOX_KEEP_MARKS) {
            arsort($marks[$k]);
            $marks[$k] = array_slice($marks[$k], 0, CO_INBOX_KEEP_MARKS, true);
        }
    }
    $data = co_db_json(['read' => (object)$marks['read'], 'done' => (object)$marks['done']]);
    $st = $db->prepare('INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
                        ON DUPLICATE KEY UPDATE data = VALUES(data), sha = VALUES(sha), updated_utc = VALUES(updated_utc)');
    $st->execute(['inbox/' . $email, 'marks', $data, hash('sha256', $data)]);
    $st->close();
    return ['ok' => true];
}
