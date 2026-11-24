<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Canvas (CO Web 0.63.0, SPEC.md of chat CHAT-20261028152233-8P34, stage M2; since Freedom F10 the server part of the
// module canvas, called through server/ops.php - db.php no longer knows these ops): the site checks the user's role on a
// chat or project file and signs a short ticket; canvas-bridge on the home PC opens the file in ONLYOFFICE only with it.
// The module page ui/editor.html navigates to <ts.net>/cb/edit#<ticket> (the editor page of canvas-bridge).
// The ticket is an HS256 JWT with the key ~/co-agent/secrets/canvas-ticket-key (the same key is in the home PC keyring
// aiwb/canvas / ticket). The user's Google token never leaves the site.
declare(strict_types=1);

const CO_CANVAS_TICKET_TTL = 120;
const CO_CANVAS_EXT = ['docx', 'xlsx', 'pptx', 'md', 'markdown', 'txt']; // md/txt: notebook (M4), canvas-bridge makes a docx next to it

// canvas-bridge on the home PC behind Tailscale Funnel: install.json canvas.publicUrl, sent by the module installer
// (installConfig publicUrl -> $ctx['config']); before F10 it came as ~/co-agent/config.php canvas_url, still the fallback.
function co_canvas_url(array $config = []): string
{
    $u = $config['publicUrl'] ?? '';
    return rtrim(is_string($u) && $u !== '' ? $u : (string)co_site_config('canvas_url', ''), '/');
}

function co_canvas_b64url(string $bin): string
{
    return rtrim(strtr(base64_encode($bin), '+/', '-_'), '=');
}

function co_canvas_key(): string
{
    $k = trim((string)@file_get_contents(co_agent_home() . '/co-agent/secrets/canvas-ticket-key'));
    if (strlen($k) < 32) throw new RuntimeException('canvas ticket key missing');
    return $k;
}

/**
 * Path of a file relative to AI-Workbench: "projects/<p>/chats/<c>/scratch/x.docx" (chat role) or "projects/<p>/..."
 * (project role). Returns [projectId, chatId|null] or throws InvalidArgumentException.
 */
function co_canvas_parse_path(string $path): array
{
    if ($path === '' || strlen($path) > 600 || str_contains($path, '\\') || preg_match('/[\x00-\x1f]/', $path)) {
        throw new InvalidArgumentException('invalid path');
    }
    $parts = explode('/', $path);
    foreach ($parts as $p) {
        if ($p === '' || $p === '.' || $p === '..' || str_starts_with($p, ' ') || str_ends_with($p, ' ')) {
            throw new InvalidArgumentException('invalid path');
        }
    }
    if (count($parts) < 3 || $parts[0] !== 'projects' || !co_db_id_ok($parts[1])) {
        throw new InvalidArgumentException('path outside projects');
    }
    $ext = strtolower(pathinfo((string)end($parts), PATHINFO_EXTENSION));
    if (!in_array($ext, CO_CANVAS_EXT, true)) throw new InvalidArgumentException('not an office file');
    $chatId = null;
    if ($parts[2] === 'chats') {
        if (count($parts) < 5 || !co_db_id_ok($parts[3])) throw new InvalidArgumentException('invalid path');
        $chatId = $parts[3];
    }
    return [$parts[1], $chatId];
}

/**
 * User op canvas_ticket {path}: role check like CO Web (chat role inside a chat folder, project role elsewhere).
 * $acc: myEnv, admin (owner), email, config (the module's install config).
 */
function co_canvas_ticket(mysqli $db, array $in, array $acc): array
{
    $path = $in['path'] ?? null;
    if (!is_string($path)) throw new InvalidArgumentException('invalid path');
    [$projectId, $chatId] = co_canvas_parse_path($path);
    if ($chatId !== null) {
        if (share_chat_project($db, $chatId) !== $projectId) return ['error' => 'not_found'];
        $role = share_chat_role($db, $acc['myEnv'], $acc['admin'], $chatId);
    } else {
        $role = share_project_role($db, $acc['myEnv'], $acc['admin'], $projectId);
    }
    if (!share_can($role, 'read')) return ['error' => 'forbidden'];
    $edit = share_can($role, 'write');
    $now = time();
    $claims = [
        'path' => $path,
        'projectId' => $projectId,
        'chatId' => $chatId,
        'env' => $acc['myEnv'],
        'email' => $acc['email'],
        'edit' => $edit,
        'iat' => $now,
        'exp' => $now + CO_CANVAS_TICKET_TTL,
    ];
    $h = co_canvas_b64url(json_encode(['alg' => 'HS256', 'typ' => 'JWT']));
    $p = co_canvas_b64url(json_encode($claims, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
    $s = co_canvas_b64url(hash_hmac('sha256', "$h.$p", co_canvas_key(), true));
    $url = co_canvas_url($acc['config'] ?? []);
    return ['ticket' => "$h.$p.$s", 'editUrl' => $url . '/cb/edit', 'edit' => $edit, 'online' => co_canvas_online($url)];
}

/** canvas-bridge answers through Funnel (the browser cannot probe it: on tailnet devices ts.net is a local address). */
function co_canvas_online(string $url): bool
{
    if ($url === '' || !function_exists('curl_init')) return false;  // not configured: the page offers "Try anyway"
    $ch = curl_init($url . '/cb/health');
    curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => 5, CURLOPT_CONNECTTIMEOUT => 4]);
    $body = (string)curl_exec($ch);
    $code = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    return $code === 200 && str_contains($body, '"ok": true');
}

// ---- M3: the chat agent answers comment threads of a document open in canvas (CO Agent plugin) ----

const CO_CANVAS_RESUME_S = 7200;      // a canvas session of the chat is resumed within 2 h of its last answer
const CO_CANVAS_PROTOCOL_MAX = 20000; // chars of the chat protocol given to a new canvas session
const CO_CANVAS_AGENTS = ['claude', 'codex'];

/**
 * Host op canvas_job (canvas-bridge, host key aiwb/co-push): queue a job for the chat's agent with comment threads of a chat file.
 * The job runs in its own agent session (request.canvas), so the chat conversation and its follow-ups stay apart;
 * CO Web shows it as one collapsed row. {path, env, summary, prompt} -> {jobId} | {busy: jobId} | {error}.
 */
function co_canvas_job(mysqli $db, array $in): array
{
    $path = $in['path'] ?? null;
    $env = $in['env'] ?? null;
    $summary = $in['summary'] ?? null;
    $prompt = $in['prompt'] ?? null;
    if (!is_string($path) || !co_db_id_ok($env) || !is_string($summary) || !is_string($prompt)
        || $prompt === '' || strlen($prompt) > 1_500_000) {
        throw new InvalidArgumentException('path, env, summary and prompt required');
    }
    [$projectId, $chatId] = co_canvas_parse_path($path);
    if ($chatId === null) return ['error' => 'not_a_chat_file'];
    $one = static function (string $sql, array $p) use ($db): ?array {
        $st = $db->prepare($sql);
        $st->execute($p);
        $row = $st->get_result()->fetch_assoc();
        $st->close();
        return $row ?: null;
    };
    $chat = $one('SELECT c.project_id, p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?', [$chatId]);
    if (!$chat || $chat['project_id'] !== $projectId) return ['error' => 'not_found'];
    // the asker had edit rights when the ticket was signed; checked again (a share may have been revoked since)
    if (!share_can(share_chat_role($db, (string)$env, co_is_owner_env((string)$env), $chatId), 'write')) return ['error' => 'forbidden'];
    $busy = $one("SELECT job_id FROM jobs WHERE chat_id = ? AND state IN ('inbox', 'claimed', 'running') AND result IS NULL
                   AND JSON_EXISTS(request, '$.canvas') LIMIT 1", [$chatId]);
    if ($busy) return ['busy' => $busy['job_id']];

    // agent, model and mode of the chat's last own turn
    $last = $one("SELECT request FROM jobs WHERE chat_id = ? AND state = 'completed' AND NOT JSON_EXISTS(request, '$.canvas')
                   ORDER BY queued_utc DESC LIMIT 1", [$chatId]);
    $prev = $last ? (json_decode((string)$last['request'], true) ?: []) : [];
    $agent = in_array($prev['agent'] ?? null, CO_CANVAS_AGENTS, true) ? $prev['agent'] : 'claude';
    $mode = in_array($prev['mode'] ?? null, ['chat', 'cowork'], true) ? $prev['mode'] : 'chat';
    $model = $agent === ($prev['agent'] ?? null) && is_string($prev['model'] ?? null) ? $prev['model'] : null;

    // the canvas session of this chat, if recent
    $sess = $one("SELECT JSON_VALUE(result, '$.sessionId') AS sid, JSON_VALUE(request, '$.agent') AS agent,
                         JSON_VALUE(request, '$.mode') AS mode FROM jobs
                   WHERE chat_id = ? AND state = 'completed' AND JSON_EXISTS(request, '$.canvas')
                     AND completed_utc > UTC_TIMESTAMP() - INTERVAL " . CO_CANVAS_RESUME_S . " SECOND
                   ORDER BY queued_utc DESC LIMIT 1", [$chatId]);
    $resume = $sess && is_string($sess['sid']) && $sess['sid'] !== '' && $sess['agent'] === $agent && $sess['mode'] === $mode;
    $conversation = $resume ? ['operation' => 'resume', 'sessionId' => $sess['sid']] : ['operation' => 'new', 'sessionId' => null];
    $instruction = $resume ? $prompt : co_canvas_preamble($db, $chatId) . $prompt;

    $stamp = gmdate('YmdHis');
    $jobId = 'JOB-' . $stamp . '-' . co_canvas_rand(6);
    $now = gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000);
    $canvas = ['path' => $path, 'file' => basename($path)];
    $ev = [
        'schemaVersion' => 1,
        'eventId' => 'EVT-' . $stamp . '-' . co_canvas_rand(6),
        'type' => 'user_message',
        'createdUtc' => $now,
        'chatId' => $chatId,
        'branchId' => 'BR-root',
        'jobId' => $jobId,
        'hostId' => 'home-main',
        'agent' => $agent,
        'author' => $env,
        'text' => mb_substr($summary, 0, 2000),
        'attachments' => [],
        'returnFiles' => [],
        'conversation' => $conversation,
        'mode' => $mode,
        'model' => $model,
        'effort' => null,
        'canvas' => $canvas,
    ];
    $req = [
        'schemaVersion' => 3,
        'protocolMinor' => 2,
        'hostId' => 'home-main',
        'jobId' => $jobId,
        'environmentId' => $chat['env'],
        'projectId' => $projectId,
        'chatId' => $chatId,
        'branchId' => 'BR-root',
        'agent' => $agent,
        'instruction' => $instruction,
        'conversation' => $conversation,
        'attachments' => [],
        'artifactPaths' => [],
        'timeoutMinutes' => 30,
        'mode' => $mode,
        'model' => $model,
        'effort' => null,
        'output' => ['autoExport' => false],
        // no devices, context folders or confidential models: the answer goes into a document others may share
        'permissions' => ['files' => 'read', 'shell' => true, 'web' => true],
        'contextFolders' => [],
        'canvas' => $canvas,
    ];
    // core with the CO trash (F15.2): no message or job for a trashed chat
    $guard = function_exists('share_job_insert_untrashed');
    if ($guard && (string)share_chat_trashed($db, $chatId) !== '') return ['error' => 'chat_trashed'];
    db_append_event(['event' => $ev]);
    $json = co_db_json($req);
    $sha = hash('sha256', co_db_json(['request' => $req, 'result' => null]));
    $insert = static function () use ($db, $jobId, $chat, $projectId, $chatId, $agent, $mode, $model, $json, $sha, $env): void {
        $st = $db->prepare(
            "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc,
                               request, sha, synced_utc, actor_env)
             VALUES (?, 'home-main', ?, ?, ?, ?, ?, ?, 'inbox', 'db', UTC_TIMESTAMP(3), ?, ?, UTC_TIMESTAMP(3), ?)"
        );
        $st->execute([$jobId, $chat['env'], $projectId, $chatId, $agent, $mode, $model, $json, $sha, $env]);
        $st->close();
    };
    if (!$guard) {
        $insert();
    } elseif (!share_job_insert_untrashed($db, $chatId, $insert)) {
        // trashed meanwhile: take the message back, as the auto-switch does
        $d = $db->prepare('DELETE FROM events WHERE chat_id = ? AND job_id = ? AND type = ?');
        $d->execute([$chatId, $jobId, 'user_message']);
        $d->close();
        return ['error' => 'chat_trashed'];
    }
    return ['jobId' => $jobId, 'resume' => $resume, 'agent' => $agent];
}

/** Host op canvas_job_get {jobId}: state and answer of a canvas job. */
function co_canvas_job_get(mysqli $db, array $in): array
{
    $jobId = $in['jobId'] ?? null;
    if (!co_db_id_ok($jobId)) throw new InvalidArgumentException('jobId required');
    $st = $db->prepare("SELECT state, status, rejection, JSON_VALUE(result, '$.agentFinalResponse') AS answer,
                               JSON_VALUE(result, '$.error') AS error
                          FROM jobs WHERE job_id = ? AND JSON_EXISTS(request, '$.canvas')");
    $st->execute([$jobId]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$r) return ['error' => 'not_found'];
    return ['state' => $r['state'], 'status' => $r['status'], 'answer' => $r['answer'],
            'error' => $r['error'] ?? $r['rejection']];
}

/** Start of a new canvas session: where it runs and the chat protocol (the chat conversation itself is not resumed). */
function co_canvas_preamble(mysqli $db, string $chatId): string
{
    $st = $db->prepare('SELECT title FROM chats WHERE chat_id = ?');
    $st->execute([$chatId]);
    $title = (string)($st->get_result()->fetch_row()[0] ?? '');
    $st->close();
    $st = $db->prepare('SELECT section, text, msg_seq FROM protocol WHERE chat_id = ? AND replaced_by IS NULL ORDER BY entry_no');
    $st->execute([$chatId]);
    $res = $st->get_result();
    $lines = [];
    $n = 0;
    while ($r = $res->fetch_assoc()) {
        $line = '- [' . $r['section'] . '] ' . $r['text'] . ' (msg ' . $r['msg_seq'] . ')';
        $n += strlen($line) + 1;
        if ($n > CO_CANVAS_PROTOCOL_MAX) break;
        $lines[] = $line;
    }
    $st->close();
    $out = "[CO canvas: you are the agent of the AI Workbench chat \"$title\" ($chatId). A user edits a document of this "
        . "chat in the canvas editor and asks you in its comments. This is a separate session for canvas comments; the "
        . "chat conversation goes on elsewhere.]\n\n";
    if ($lines) {
        $out .= "[Protocol of the chat]\n" . implode("\n", $lines) . "\n[End of protocol]\n\n";
    }
    return $out;
}

function co_canvas_rand(int $n): string
{
    $abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    $s = '';
    for ($i = 0; $i < $n; $i++) $s .= $abc[random_int(0, 35)];
    return $s;
}

// ---- M5: Share - a Gmail draft with the document attached, made with the user's own Google account ----
// canvas-bridge prepares the file (docx without agent threads, or PDF) and puts it here in chunks (host op
// canvas_share_put); the editor page opens <CO Web>/canvas-share?id=..., the module page ui/share.html, which asks
// op canvas_share_draft.
// The draft is made with an access token from the user's own CO session (scope gmail.compose, asked on first use);
// it is never sent. Files live in ~/co-agent/state/canvas-share/<id> for an hour.

const CO_CANVAS_SHARE_MAX = 25 * 1024 * 1024;   // Gmail attachment limit
const CO_CANVAS_SHARE_CHUNK = 4_000_000;
const CO_CANVAS_SHARE_TTL = 3600;
const CO_CANVAS_GMAIL_SCOPE = 'https://www.googleapis.com/auth/gmail.compose';
const CO_CANVAS_SHARE_MIME = [
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'application/vnd.openxmlformats-officedocument.presentationml.presentation',
    'application/pdf',
];

function co_canvas_share_dir(?string $id = null): string
{
    $d = co_agent_home() . '/co-agent/state/canvas-share';
    return $id === null ? $d : $d . '/' . $id;
}

function co_canvas_share_rm(string $dir): void
{
    foreach (glob($dir . '/*') ?: [] as $f) @unlink($f);
    @rmdir($dir);
}

/** Host op canvas_share_put {id|null, seq, total, env, email, path, name, mime, size, sha256, data(base64)} -> {id}. */
function co_canvas_share_put(array $in): array
{
    $seq = $in['seq'] ?? null;
    $total = $in['total'] ?? null;
    $size = $in['size'] ?? null;
    $name = $in['name'] ?? null;
    $sha = $in['sha256'] ?? null;
    if (!is_int($seq) || !is_int($total) || !is_int($size) || $total < 1 || $seq < 0 || $seq >= $total
        || $size < 1 || $size > CO_CANVAS_SHARE_MAX || $total !== intdiv($size + CO_CANVAS_SHARE_CHUNK - 1, CO_CANVAS_SHARE_CHUNK)
        || !is_string($name) || $name === '' || strlen($name) > 240 || preg_match('/[\x00-\x1f\/\\\\]/', $name)
        || !in_array($in['mime'] ?? null, CO_CANVAS_SHARE_MIME, true) || !co_db_id_ok($in['env'] ?? null)
        || !is_string($sha) || !preg_match('/^[0-9a-f]{64}$/', $sha) || !is_string($in['data'] ?? null)
        || !is_string($in['email'] ?? null) || !is_string($in['path'] ?? null)) {
        throw new InvalidArgumentException('invalid share chunk');
    }
    co_canvas_parse_path($in['path']);
    $bin = base64_decode($in['data'], true);
    if ($bin === false || strlen($bin) > CO_CANVAS_SHARE_CHUNK || ($seq < $total - 1 && strlen($bin) !== CO_CANVAS_SHARE_CHUNK)) {
        throw new InvalidArgumentException('invalid share chunk');
    }
    $root = co_canvas_share_dir();
    if (!is_dir($root)) mkdir($root, 0700, true);
    foreach (glob($root . '/*', GLOB_ONLYDIR) ?: [] as $old) {
        if (filemtime($old) < time() - CO_CANVAS_SHARE_TTL) co_canvas_share_rm($old);
    }
    if ($seq === 0) {
        $id = bin2hex(random_bytes(16));
        mkdir(co_canvas_share_dir($id), 0700);
        $meta = ['env' => $in['env'], 'email' => strtolower($in['email']), 'path' => $in['path'], 'name' => $name,
                 'mime' => $in['mime'], 'size' => $size, 'sha256' => $sha, 'total' => $total, 'at' => time()];
        if (file_put_contents(co_canvas_share_dir($id) . '/meta.json', json_encode($meta, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE)) === false) {
            co_canvas_share_rm(co_canvas_share_dir($id));
            return ['error' => 'storage'];
        }
    } else {
        $id = $in['id'] ?? null;
        if (!is_string($id) || !preg_match('/^[0-9a-f]{32}$/', $id)) throw new InvalidArgumentException('invalid share id');
        $meta = json_decode((string)@file_get_contents(co_canvas_share_dir($id) . '/meta.json'), true);
        if (!is_array($meta) || $meta['sha256'] !== $sha || $meta['total'] !== $total || $meta['size'] !== $size
            || !is_file(co_canvas_share_dir($id) . '/part' . ($seq - 1))) {
            return ['error' => 'unknown_share'];
        }
    }
    $dir = co_canvas_share_dir($id);
    if (file_put_contents($dir . '/part' . $seq, $bin) !== strlen($bin)) {
        co_canvas_share_rm($dir);
        return ['error' => 'storage'];
    }
    if ($seq === $total - 1) {
        $out = fopen($dir . '/data', 'wb');
        for ($i = 0; $i < $total; $i++) {
            fwrite($out, (string)file_get_contents($dir . '/part' . $i));
        }
        fclose($out);
        for ($i = 0; $i < $total; $i++) @unlink($dir . '/part' . $i);
        if (filesize($dir . '/data') !== $size || hash_file('sha256', $dir . '/data') !== $sha) {
            co_canvas_share_rm($dir);
            return ['error' => 'checksum'];
        }
    }
    return ['id' => $id];
}

/** RFC 2047 header word (UTF-8, base64), plain ASCII as it is. */
function co_canvas_mime_word(string $s): string
{
    return preg_match('/^[\x20-\x7e]*$/', $s) ? $s : '=?UTF-8?B?' . base64_encode($s) . '?=';
}

function co_canvas_mime_message(string $name, string $mime, string $data): string
{
    $b = 'co-' . bin2hex(random_bytes(12));
    // A non-ASCII file name: filename* (RFC 2231) alone in Content-Disposition (next to a plain fallback some readers
    // pick the fallback), and the de-facto RFC 2047 word in the Content-Type name, as Gmail and Thunderbird write it.
    // (a quote or backslash also goes the encoded way, so the name stays exact)
    $plain = preg_match('/^[\x20-\x7e]*$/', $name) === 1 && strpbrk($name, '"\\') === false;
    $ctName = $plain ? "\"$name\"" : '"=?UTF-8?B?' . base64_encode($name) . '?="';
    $cdName = $plain ? "filename=\"$name\"" : "filename*=UTF-8''" . rawurlencode($name);
    $text = "$name is attached.\r\n";
    return "MIME-Version: 1.0\r\n"
        . 'Subject: ' . co_canvas_mime_word(pathinfo($name, PATHINFO_FILENAME)) . "\r\n"
        . "Content-Type: multipart/mixed; boundary=\"$b\"\r\n\r\n"
        . "--$b\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
        . chunk_split(base64_encode($text)) . "\r\n"
        . "--$b\r\nContent-Type: $mime; name=$ctName\r\n"
        . "Content-Disposition: attachment; $cdName\r\n"
        . "Content-Transfer-Encoding: base64\r\n\r\n"
        . chunk_split(base64_encode($data)) . "\r\n--$b--\r\n";
}

/** Access token of this browser's CO session if it carries gmail.compose; null when the user must allow it first. */
function co_canvas_gmail_token(): ?string
{
    $session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
    if (!$session || empty($session['rt'])) return null;
    $cfg = co_config();
    [$status, $tok] = co_post_form('https://oauth2.googleapis.com/token', [
        'client_id' => $cfg['client_id'], 'client_secret' => $cfg['client_secret'],
        'refresh_token' => $session['rt'], 'grant_type' => 'refresh_token',
    ]);
    if ($status !== 200 || empty($tok['access_token'])) return null;
    $scopes = explode(' ', (string)($tok['scope'] ?? ''));
    return in_array(CO_CANVAS_GMAIL_SCOPE, $scopes, true) ? (string)$tok['access_token'] : null;
}

/** User op canvas_share_draft {id} -> {draftId, messageId, email, name} | {error: need_gmail|not_found|...}. */
function co_canvas_share_draft(array $in, array $acc): array
{
    $id = $in['id'] ?? null;
    if (!is_string($id) || !preg_match('/^[0-9a-f]{32}$/', $id)) return ['error' => 'not_found'];
    $dir = co_canvas_share_dir($id);
    $meta = json_decode((string)@file_get_contents($dir . '/meta.json'), true);
    if (!is_array($meta) || (int)$meta['at'] < time() - CO_CANVAS_SHARE_TTL) return ['error' => 'not_found'];
    // only the user who pressed Share in the editor
    if ($meta['env'] !== $acc['myEnv'] || ($meta['email'] !== '' && $meta['email'] !== $acc['email'])) return ['error' => 'forbidden'];
    $done = json_decode((string)@file_get_contents($dir . '/draft.json'), true);
    if (is_array($done)) return $done;  // the page was reloaded: the same draft, not a second one
    if (!is_file($dir . '/data')) return ['error' => 'not_found'];
    $token = co_canvas_gmail_token();
    if ($token === null) return ['error' => 'need_gmail', 'email' => $acc['email']];
    $lock = fopen($dir . '/lock', 'c');
    if (!$lock || !flock($lock, LOCK_EX | LOCK_NB)) return ['error' => 'busy'];  // a second tab of the same share
    // A Gmail call whose answer was lost may have made the draft: never post a second one, send the user to Drafts.
    if (is_file($dir . '/sending')) return ['error' => 'uncertain', 'email' => $acc['email']];
    if (!touch($dir . '/sending')) return ['error' => 'storage'];
    $msg = co_canvas_mime_message((string)$meta['name'], (string)$meta['mime'], (string)file_get_contents($dir . '/data'));
    $ch = curl_init('https://gmail.googleapis.com/upload/gmail/v1/users/me/drafts?uploadType=media');
    curl_setopt_array($ch, [
        CURLOPT_POST => true, CURLOPT_POSTFIELDS => $msg, CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => 120,
        CURLOPT_HTTPHEADER => ['Authorization: Bearer ' . $token, 'Content-Type: message/rfc822'],
    ]);
    $body = (string)curl_exec($ch);
    $code = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    $res = json_decode($body, true) ?: [];
    if ($code >= 400 && $code < 500) @unlink($dir . '/sending');  // Gmail refused it: no draft, a retry is safe
    if ($code === 403 || $code === 401) return ['error' => 'need_gmail', 'email' => $acc['email']];
    if ($code !== 200 || empty($res['id'])) {
        error_log('canvas_share_draft: gmail ' . $code . ' ' . substr($body, 0, 300));
        return ['error' => 'gmail_failed', 'status' => $code];
    }
    $out = ['draftId' => $res['id'], 'messageId' => $res['message']['id'] ?? '', 'email' => $acc['email'], 'name' => $meta['name']];
    // the file goes first: without it a retry cannot make a second draft, even if draft.json cannot be written
    @unlink($dir . '/data');
    if (file_put_contents($dir . '/draft.json', json_encode($out, JSON_UNESCAPED_UNICODE)) === false) {
        error_log('canvas_share_draft: draft.json not written for ' . $id);
    }
    return $out;
}
