<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// family-lib.php (CO Web 0.89.0): family chats - a shared chat with "family": true in its chat.json, where people
// write to each other and the agent (@co) answers when called. A new message or answer goes as push to the other
// members (project owner + project/chat shares); the writer's own environment gets none.
declare(strict_types=1);


function co_family_chat(mysqli $db, string $chatId): ?array
{
    $st = $db->prepare('SELECT c.project_id, c.title, c.data, p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
    $st->execute([$chatId]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$r) return null;
    $data = json_decode((string)$r['data'], true);
    if (!is_array($data) || ($data['family'] ?? false) !== true) return null;
    return ['projectId' => (string)$r['project_id'], 'title' => (string)$r['title'], 'env' => (string)$r['env']];
}

/** Environments that see the chat: the project owner and every project or chat share. */
function co_family_members(mysqli $db, string $chatId, array $chat): array
{
    $envs = [$chat['env']];
    $st = $db->prepare("SELECT env FROM shares WHERE (kind = 'project' AND target_id = ?) OR (kind = 'chat' AND target_id = ?)");
    $st->execute([$chat['projectId'], $chatId]);
    foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as $row) $envs[] = (string)$row[0];
    $st->close();
    return array_values(array_unique($envs));
}

function co_family_cut(string $s, int $n): string
{
    $s = trim(preg_replace('/\s+/u', ' ', $s) ?? $s);
    return mb_strlen($s) > $n ? mb_substr($s, 0, $n - 1) . '…' : $s;
}

/** Push body of a message: its text, or the attachment kind. */
function co_family_body(array $ev): string
{
    $text = (string)($ev['text'] ?? '');
    if (($ev['type'] ?? '') === 'assistant_message') {
        $text = preg_replace('/\[\[(?:chat-title|options|ask):[^\]]*\]\]/u', '', $text) ?? $text;
    }
    $text = co_family_cut($text, 160);
    if ($text !== '') return $text;
    $att = is_array($ev['attachments'] ?? null) ? $ev['attachments'] : [];
    if (!$att) return '…';
    $img = 0;
    foreach ($att as $a) {
        if (is_array($a) && preg_match('/\.(jpe?g|png|gif|webp|heic)$/i', (string)($a['displayName'] ?? ''))) $img++;
    }
    return $img === count($att) ? ($img > 1 ? "📷 $img фото" : '📷 Фото') : '📎 ' . count($att) . ' файл(ов)';
}

/** True when the agent chose to stay out ([[silent]] only). */
function co_family_silent(array $ev): bool
{
    return ($ev['type'] ?? '') === 'assistant_message' && trim((string)($ev['text'] ?? '')) === '[[silent]]';
}

/**
 * Push new messages of family chats to the other members. $events - events just written for the first time;
 * $writerEnv - environment of the person who wrote them; an agent's answer skips the env of the person who called
 * it instead (they see it in the open chat and get Bridge's own push).
 */
function co_family_notify(mysqli $db, array $events, ?string $writerEnv, ?callable $push = null, ?string $writerEmail = null): int
{
    $push ??= function_exists('co_push_broadcast') ? 'co_push_broadcast' : null;
    if ($push === null) return 0;
    $sent = 0;
    $chats = [];
    foreach ($events as $ev) {
        if (!is_array($ev)) continue;
        $type = (string)($ev['type'] ?? '');
        if (($type !== 'user_message' && $type !== 'assistant_message') || co_family_silent($ev)) continue;
        // follow-ups and the trigger of a delayed @co turn repeat a message the members already got
        if (!empty($ev['followupOf']) || !empty($ev['coFor'])) continue;
        $cid = (string)($ev['chatId'] ?? '');
        if (!array_key_exists($cid, $chats)) $chats[$cid] = co_family_chat($db, $cid);
        $chat = $chats[$cid];
        if ($chat === null) continue;
        // the session's account names the writer, not the author field the client sent
        $from = $type === 'assistant_message' ? 'AI Workbench' : co_family_author_name($writerEmail ?? (string)($ev['author'] ?? ''), $writerEnv);
        // an answer is written by whoever finalizes it (server or an open CO Web): skip the caller's env instead
        $skip = $type === 'assistant_message' ? co_family_author_env($db, $cid, (string)($ev['jobId'] ?? '')) : $writerEnv;
        foreach (co_family_members($db, $cid, $chat) as $env) {
            if ($env === $skip) continue;
            try {
                $push(
                    static fn($sub): bool => is_array($sub) && ($sub['env'] ?? '') === $env,
                    ['title' => '👪 ' . $from, 'body' => co_family_body($ev),
                        'url' => '/e/' . rawurlencode($env) . '?family=1', // 0.90.0: opens the Family tab of the right panel
                        'tag' => 'family-' . $cid],
                    'family-' . substr(hash('sha256', $cid), 0, 16)
                );
                $sent++;
            } catch (Throwable $e) {
                error_log('co_family_notify: ' . $e->getMessage());
            }
        }
    }
    return $sent;
}

function co_family_author_name(string $email, ?string $env): string
{
    $users = function_exists('co_push_users') ? co_push_users() : [];
    $e = $users[strtolower($email)] ?? $env ?? '';
    return co_member_names()[$e] ?? ($email !== '' ? $email : 'CO');
}

/** Environment of the person whose message started the job (their own device shows the answer already). */
function co_family_author_env(mysqli $db, string $chatId, string $jobId): ?string
{
    if ($jobId === '') return null;
    $st = $db->prepare("SELECT author FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message' LIMIT 1");
    $st->execute([$chatId, $jobId]);
    $r = $st->get_result()->fetch_row();
    $st->close();
    if (!$r || !is_string($r[0])) return null;
    $users = function_exists('co_push_users') ? co_push_users() : [];
    return $users[strtolower($r[0])] ?? null;
}

/** Family chats an environment sees (its own projects or shares), newest first; archived ones are left out. */
function co_family_list(mysqli $db, string $env): array
{
    $st = $db->prepare("SELECT c.chat_id, c.project_id, c.title, c.data, c.updated_utc, p.env FROM chats c JOIN projects p ON p.project_id = c.project_id
        WHERE c.data LIKE '%\"family\":true%' AND (p.env = ? OR EXISTS (SELECT 1 FROM shares s WHERE s.env = ?
            AND ((s.kind = 'project' AND s.target_id = c.project_id) OR (s.kind = 'chat' AND s.target_id = c.chat_id))))
        ORDER BY c.updated_utc DESC LIMIT 20");
    $st->execute([$env, $env]);
    $out = [];
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        $d = json_decode((string)$r['data'], true);
        if (!is_array($d) || ($d['family'] ?? false) !== true || ($d['archived'] ?? false) === true) continue;
        $out[] = ['chatId' => (string)$r['chat_id'], 'projectId' => (string)$r['project_id'], 'title' => (string)$r['title'],
            'owner' => (string)$r['env'], 'updatedUtc' => (string)$r['updated_utc']];
    }
    $st->close();
    return $out;
}

/**
 * 09.11 (server only): an agent of another chat posts into a family chat as AI Workbench (aiwb-chat post, host key): instructions
 * for the family, answers. No chatId - the only family chat of the environment. The message is an assistant_message
 * without a job; every member gets the push. $append numbers and stores the event (db_append_event).
 */
function co_family_post(mysqli $db, array $in, ?callable $append = null, ?callable $push = null): array
{
    $append ??= 'db_append_event';
    $text = is_string($in['text'] ?? null) ? trim($in['text']) : '';
    if ($text === '' || mb_strlen($text) > 20000) throw new InvalidArgumentException('text must be 1..20000 chars');
    $chatId = is_string($in['chatId'] ?? null) && $in['chatId'] !== '' ? $in['chatId'] : null;
    if ($chatId === null) {
        $env = is_string($in['env'] ?? null) && $in['env'] !== '' ? $in['env'] : co_owner_env();
        $list = co_family_list($db, $env);
        if (count($list) !== 1) {
            throw new InvalidArgumentException(count($list) === 0 ? "no family chat of $env"
                : 'several family chats, pass chatId: ' . implode(', ', array_map(static fn($c) => $c['chatId'] . ' ' . $c['title'], $list)));
        }
        $chatId = $list[0]['chatId'];
    }
    if (!co_db_id_ok($chatId) || co_family_chat($db, $chatId) === null) throw new InvalidArgumentException('not a family chat');
    $now = gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000);
    $ev = ['schemaVersion' => 1, 'eventId' => 'EVT-' . gmdate('YmdHis') . '-' . strtoupper(bin2hex(random_bytes(3))), 'sequence' => 0,
        'createdUtc' => $now, 'chatId' => $chatId, 'branchId' => 'BR-root', 'type' => 'assistant_message', 'jobId' => null,
        'agent' => is_string($in['agent'] ?? null) && preg_match('/^[a-z]{2,20}$/', $in['agent']) ? $in['agent'] : 'claude',
        'author' => 'co', 'status' => 'completed', 'text' => $text, 'completedUtc' => $now, 'mode' => 'chat'];
    $from = $in['fromChat'] ?? null;
    if (is_string($from) && co_db_id_ok($from) && $from !== $chatId) $ev['postedFrom'] = $from;
    $reply = $in['replyTo'] ?? null;
    if (is_string($reply) && $reply !== '') {
        $st = $db->prepare('SELECT 1 FROM events WHERE chat_id = ? AND event_id = ?');
        $st->execute([$chatId, $reply]);
        $found = $st->get_result()->fetch_row() !== null;
        $st->close();
        if (!$found) throw new InvalidArgumentException('replyTo is not a message of this chat');
        $ev['replyTo'] = $reply;
    }
    $res = $append(['event' => $ev]);
    if (!empty($res['rejected'])) throw new RuntimeException('event rejected: ' . json_encode($res['rejected']));
    $ev['sequence'] = (int)$res['sequence'];
    $pushed = 0;
    try {
        $pushed = co_family_notify($db, [$ev], null, $push);
    } catch (Throwable $e) {
        error_log('co_family_post push: ' . $e->getMessage());
    }
    return ['chatId' => $chatId, 'sequence' => $ev['sequence'], 'eventId' => $ev['eventId'], 'pushed' => $pushed];
}

/**
 * 0.90.0 (user op family_state): the family room of an environment for the right-panel tab "Family" of CO Web - the
 * project (flag family) and the room chat as CO Web reads them (data + folderId + shareRole), the newest sequence and
 * the number of messages of others after this env's read mark (kv env:<env>/family-read).
 */
function co_family_state(mysqli $db, string $env, string $email): array
{
    $rooms = co_family_list($db, $env);
    $room = $rooms[0] ?? null;
    $row = static function (string $sql, array $p) use ($db): ?array {
        $st = $db->prepare($sql);
        $st->execute($p);
        $r = $st->get_result()->fetch_assoc();
        $st->close();
        return $r ?: null;
    };
    $shape = static function (?array $r, ?string $role): ?array {
        if (!$r) return null;
        $d = json_decode((string)$r['data'], true);
        if (!is_array($d)) return null;
        if (is_string($r['folder_id'] ?? null) && $r['folder_id'] !== '') $d['folderId'] = $r['folder_id'];
        if ($role === 'editor' || $role === 'viewer') $d['shareRole'] = $role;
        return $d;
    };
    $projectId = $room['projectId'] ?? null;
    if ($projectId === null) {
        // no room yet: the family project the env sees (its own or shared to it)
        $p = $row("SELECT p.project_id FROM projects p WHERE p.data LIKE '%\"family\":true%' AND (p.env = ? OR EXISTS
                     (SELECT 1 FROM shares s WHERE s.env = ? AND s.kind = 'project' AND s.target_id = p.project_id))
                   ORDER BY p.updated_utc DESC LIMIT 1", [$env, $env]);
        $projectId = $p['project_id'] ?? null;
    }
    $project = null;
    if ($projectId !== null) {
        $project = $shape($row('SELECT data, folder_id FROM projects WHERE project_id = ?', [$projectId]),
            share_project_role($db, $env, false, $projectId));
    }
    if ($room === null) return ['project' => $project, 'chat' => null, 'lastSeq' => 0, 'unread' => 0];
    $cid = $room['chatId'];
    $chat = $shape($row('SELECT data, folder_id FROM chats WHERE chat_id = ?', [$cid]), share_chat_role($db, $env, false, $cid));
    $last = (int)($row('SELECT COALESCE(MAX(seq), 0) AS s FROM events WHERE chat_id = ?', [$cid])['s'] ?? 0);
    $read = co_family_read_marks($db, $env)[$cid] ?? 0;
    $n = $row("SELECT COUNT(*) AS n FROM events WHERE chat_id = ? AND seq > ? AND type IN ('user_message', 'assistant_message')
                 AND COALESCE(author, '') <> ? AND COALESCE(text, '') <> '[[silent]]'
                 AND NOT JSON_EXISTS(data, '$.coFor') AND NOT JSON_EXISTS(data, '$.followupOf')", [$cid, $read, $email]);
    return ['project' => $project, 'chat' => $chat, 'lastSeq' => $last, 'unread' => (int)($n['n'] ?? 0)];
}

function co_family_read_marks(mysqli $db, string $env): array
{
    $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'family-read'");
    $st->execute(['env:' . $env]);
    $r = $st->get_result()->fetch_row();
    $st->close();
    $d = $r ? json_decode((string)$r[0], true) : null;
    return is_array($d) ? $d : [];
}

/** User op family_read {chatId, seq}: everything up to seq is read by this env (the mark never goes back). */
function co_family_mark_read(mysqli $db, string $env, string $chatId, int $seq): array
{
    if (!co_db_id_ok($chatId) || co_family_chat($db, $chatId) === null) throw new InvalidArgumentException('not a family chat');
    $marks = co_family_read_marks($db, $env);
    if (($marks[$chatId] ?? 0) >= $seq) return ['seq' => $marks[$chatId] ?? 0];
    $marks[$chatId] = $seq;
    $data = co_db_json($marks);
    $st = $db->prepare("REPLACE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, 'family-read', ?, ?, UTC_TIMESTAMP(3))");
    $st->execute(['env:' . $env, $data, hash('sha256', $data)]);
    $st->close();
    return ['seq' => $seq];
}
