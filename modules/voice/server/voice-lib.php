<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server side of the module voice (SPEC §3.8). The voice work itself happens at home: the hub (tools/voice, `voice.py
// hub`) on the family server does speech to text, Home Assistant and text to speech. This file only
//   - forwards a member's recording or text from CO Web to the hub (upload turn, op voice_speak);
//   - lets the hub (host key) put a spoken question into a chat for its agent and read the answer (host ops voice_ask,
//     voice_answer) - the voice points without a screen use that.
// The hub address and key: ~/co-agent/secrets/voice.json {"hub": "https://...", "key": "..."} (0600, never in git).
declare(strict_types=1);

const CO_VOICE_MAX_BYTES = 12 * 1024 * 1024;
const CO_VOICE_LANGS = ['ru', 'uk', 'en', 'nl', 'fr', 'de'];
const CO_VOICE_AGENTS = ['claude', 'codex'];
const CO_VOICE_RESUME_S = 1800;       // a voice session of the chat is resumed within 30 min of its last answer
const CO_VOICE_PROTOCOL_MAX = 12000;  // chars of the chat protocol given to a new voice session
const CO_VOICE_TEXT_MAX = 2000;

/** {hub, key} from ~/co-agent/secrets/voice.json, or null. */
function co_voice_config(): ?array
{
    $path = co_agent_home() . '/co-agent/secrets/voice.json';
    if (!is_file($path) || !is_readable($path)) return null;
    $cfg = json_decode((string)@file_get_contents($path), true);
    if (!is_array($cfg)) return null;
    $hub = is_string($cfg['hub'] ?? null) ? rtrim(trim($cfg['hub']), '/') : '';
    $key = is_string($cfg['key'] ?? null) ? trim($cfg['key']) : '';
    if (!preg_match('~^https://[^\s/]+(/\S*)?$~i', $hub) || $key === '') return null;
    return ['hub' => $hub, 'key' => $key];
}

/** User op voice_status: is the hub configured and alive (the composer page shows its voice button only then). */
function co_voice_status(): array
{
    $cfg = co_voice_config();
    if ($cfg === null) return ['configured' => false];
    [$code, $body] = co_voice_http('GET', $cfg['hub'] . '/v1/voice/health', null, [], 4);
    $j = json_decode($body, true);
    return ['configured' => true, 'online' => $code === 200 && is_array($j) && ($j['ok'] ?? false) === true,
            'version' => is_array($j) ? ($j['version'] ?? null) : null];
}

/** @return array{0:int,1:string} */
function co_voice_http(string $method, string $url, mixed $body, array $headers, int $timeout): array
{
    if (!function_exists('curl_init')) return [0, ''];
    $ch = curl_init($url);
    $opts = [CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => $timeout, CURLOPT_CONNECTTIMEOUT => 5,
             CURLOPT_CUSTOMREQUEST => $method, CURLOPT_HTTPHEADER => $headers, CURLOPT_USERAGENT => 'freedom-voice-server'];
    if ($body !== null) $opts[CURLOPT_POSTFIELDS] = $body;
    curl_setopt_array($ch, $opts);
    $out = curl_exec($ch);
    $code = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    return [$code, is_string($out) ? $out : ''];
}

function co_voice_lang(mixed $v): string
{
    $c = is_string($v) ? strtolower(substr(trim($v), 0, 2)) : '';
    return in_array($c, CO_VOICE_LANGS, true) ? $c : '';
}

/**
 * Upload turn (agent/module.php?m=voice&u=turn): a member's recording -> the hub: speech to text, then Home Assistant
 * when the phrase is a home command (its answer comes back as audio), else {route: agent, text} and CO Web sends the
 * text into its chat itself (route=client). Fields: file, lang?, route? (auto|home|client), speak? (0|1).
 */
function co_voice_upload_turn(array $c): array
{
    $cfg = co_voice_config();
    if ($cfg === null) return ['error' => 'not_configured'];
    $file = $_FILES['file'] ?? null;
    if (!is_array($file) || (int)($file['error'] ?? UPLOAD_ERR_NO_FILE) !== UPLOAD_ERR_OK) {
        return ['error' => (int)($_SERVER['CONTENT_LENGTH'] ?? 0) > CO_VOICE_MAX_BYTES ? 'too_large' : 'missing_audio'];
    }
    $tmp = (string)($file['tmp_name'] ?? '');
    if ($tmp === '' || !is_uploaded_file($tmp)) return ['error' => 'upload_failed'];
    $size = (int)@filesize($tmp);
    if ($size <= 0) return ['error' => 'empty_audio'];
    if ($size > CO_VOICE_MAX_BYTES) return ['error' => 'too_large'];
    $mime = (string)preg_replace('/[^\w\/\-+.;=]/', '', is_string($file['type'] ?? null) ? $file['type'] : '');
    $mime = $mime !== '' ? $mime : 'audio/webm';
    $ext = str_contains($mime, 'ogg') ? 'ogg' : (str_contains($mime, 'mp4') ? 'm4a' : (str_contains($mime, 'wav') ? 'wav' : 'webm'));
    $route = in_array($_POST['route'] ?? null, ['auto', 'home', 'client'], true) ? $_POST['route'] : 'client';
    if ($route === 'auto') $route = 'client';  // CO Web never makes the hub wait for an agent: it sends the text itself
    $fields = ['file' => new CURLFile($tmp, $mime, "phrase.$ext"), 'route' => $route,
               'speak' => ($_POST['speak'] ?? '1') === '0' ? '0' : '1', 'format' => 'mp3',
               'point' => 'web-' . substr((string)$c['myEnv'], 0, 30)];
    $lang = co_voice_lang($_POST['lang'] ?? null);
    if ($lang !== '') $fields['lang'] = $lang;
    set_time_limit(150);
    [$code, $body] = co_voice_http('POST', $cfg['hub'] . '/v1/voice/turn', $fields, ['Authorization: Bearer ' . $cfg['key']], 140);
    $j = json_decode($body, true);
    if ($code !== 200 || !is_array($j)) return ['error' => 'hub_failed', 'status' => $code];
    return co_voice_pick($j);
}

/** Only what the page needs from the hub's answer. */
function co_voice_pick(array $j): array
{
    $out = ['route' => is_string($j['route'] ?? null) ? $j['route'] : 'none',
            'text' => is_string($j['text'] ?? null) ? $j['text'] : '',
            'lang' => co_voice_lang($j['lang'] ?? null),
            'reply' => is_string($j['reply'] ?? null) ? $j['reply'] : '',
            'timings' => is_array($j['timings'] ?? null) ? $j['timings'] : (object)[]];
    if (is_array($j['audio'] ?? null) && is_string($j['audio']['b64'] ?? null)) {
        $out['audio'] = ['type' => (string)($j['audio']['type'] ?? 'audio/mpeg'), 'b64' => $j['audio']['b64']];
    }
    return $out;
}

/** User op voice_speak {text, lang}: the hub reads the text aloud -> {audio: {type, b64}} (the agent's answer in CO Web). */
function co_voice_speak(array $in): array
{
    $cfg = co_voice_config();
    if ($cfg === null) return ['error' => 'not_configured'];
    $text = is_string($in['text'] ?? null) ? trim($in['text']) : '';
    if ($text === '') return ['error' => 'text required'];
    $lang = co_voice_lang($in['lang'] ?? null);
    $payload = json_encode(['text' => mb_substr(co_voice_plain($text), 0, 1500), 'lang' => $lang ?: 'ru', 'format' => 'mp3'],
        JSON_UNESCAPED_UNICODE);
    [$code, $body] = co_voice_http('POST', $cfg['hub'] . '/v1/voice/speak', $payload,
        ['Authorization: Bearer ' . $cfg['key'], 'Content-Type: application/json'], 60);
    if ($code !== 200 || $body === '') return ['error' => 'hub_failed', 'status' => $code];
    return ['audio' => ['type' => 'audio/mpeg', 'b64' => base64_encode($body)]];
}

/** Markdown of an agent's answer -> plain sentences for the speaker. */
function co_voice_plain(string $t): string
{
    $t = (string)preg_replace('/```.*?```/s', ' ', $t);
    $t = (string)preg_replace('/`([^`]*)`/', '$1', $t);
    $t = (string)preg_replace('/!?\[([^\]]*)\]\([^)]*\)/', '$1', $t);
    $t = (string)preg_replace('~https?://\S+~', '', $t);
    $t = (string)preg_replace('/^[#>*\-\s|]+/m', '', $t);
    $t = (string)preg_replace('/[*_~|]+/', '', $t);
    return trim((string)preg_replace('/\s+/u', ' ', $t));
}

function co_voice_rand(int $n): string
{
    $abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    $s = '';
    for ($i = 0; $i < $n; $i++) $s .= $abc[random_int(0, 35)];
    return $s;
}

function co_voice_one(mysqli $db, string $sql, array $p): ?array
{
    $st = $db->prepare($sql);
    $st->execute($p);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    return $row ?: null;
}

/**
 * Host op voice_ask (the hub, host key): a spoken question of a voice point goes into a chat as a user message of
 * `env` (who must be allowed to write there) and a job for the chat's agent. Voice turns keep their own agent session
 * (request.voice), resumed within 30 min, so a long chat session is not reloaded for every phrase; a new voice session
 * gets the chat protocol. {chatId, env, text, lang, point} -> {jobId} | {busy: jobId} | {error}.
 */
function co_voice_ask(mysqli $db, array $in): array
{
    $chatId = $in['chatId'] ?? null;
    $env = $in['env'] ?? null;
    $text = is_string($in['text'] ?? null) ? trim($in['text']) : '';
    if (!co_db_id_ok($chatId) || !co_db_id_ok($env) || $text === '') throw new InvalidArgumentException('chatId, env and text required');
    $text = mb_substr($text, 0, CO_VOICE_TEXT_MAX);
    $lang = co_voice_lang($in['lang'] ?? null) ?: 'ru';
    $point = is_string($in['point'] ?? null) ? mb_substr((string)preg_replace('/[^\w .-]/u', '', $in['point']), 0, 40) : '';
    $chat = co_voice_one($db, 'SELECT c.project_id, p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?', [$chatId]);
    if (!$chat) return ['error' => 'not_found'];
    if (!share_can(share_chat_role($db, (string)$env, co_is_owner_env((string)$env), (string)$chatId), 'write')) return ['error' => 'forbidden'];
    $busy = co_voice_one($db, "SELECT job_id FROM jobs WHERE chat_id = ? AND state IN ('inbox', 'claimed', 'running') AND result IS NULL
                                 AND JSON_EXISTS(request, '$.voice') LIMIT 1", [$chatId]);
    if ($busy) return ['busy' => $busy['job_id']];

    // agent, model and host of the chat's last turn
    $last = co_voice_one($db, "SELECT request, host_id FROM jobs WHERE chat_id = ? AND state = 'completed' ORDER BY queued_utc DESC LIMIT 1", [$chatId]);
    $prev = $last ? (json_decode((string)$last['request'], true) ?: []) : [];
    $agent = in_array($prev['agent'] ?? null, CO_VOICE_AGENTS, true) ? $prev['agent'] : 'claude';
    $model = $agent === ($prev['agent'] ?? null) && is_string($prev['model'] ?? null) ? $prev['model'] : null;
    $host = $last && co_exec_known($db, (string)$last['host_id']) ? (string)$last['host_id'] : 'home-main';

    $sess = co_voice_one($db, "SELECT JSON_VALUE(result, '$.sessionId') AS sid, JSON_VALUE(request, '$.agent') AS agent FROM jobs
                                WHERE chat_id = ? AND state = 'completed' AND JSON_EXISTS(request, '$.voice')
                                  AND completed_utc > UTC_TIMESTAMP() - INTERVAL " . CO_VOICE_RESUME_S . " SECOND
                                ORDER BY queued_utc DESC LIMIT 1", [$chatId]);
    $resume = $sess && is_string($sess['sid']) && $sess['sid'] !== '' && $sess['agent'] === $agent;
    $conversation = $resume ? ['operation' => 'resume', 'sessionId' => $sess['sid']] : ['operation' => 'new', 'sessionId' => null];
    $rules = "[Voice: the user said this to a voice point" . ($point !== '' ? " ($point)" : '') . ". Your answer is read aloud: "
        . "answer in the language of the question ($lang), briefly (1-3 sentences), plain text without markdown, lists, links or code.]\n\n";
    $instruction = ($resume ? '' : co_voice_preamble($db, (string)$chatId)) . $rules . $text;

    $stamp = gmdate('YmdHis');
    $jobId = 'JOB-' . $stamp . '-' . co_voice_rand(6);
    $now = gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000);
    $voice = ['lang' => $lang, 'point' => $point];
    db_append_event(['event' => [
        'schemaVersion' => 1, 'eventId' => 'EVT-' . $stamp . '-' . co_voice_rand(6), 'type' => 'user_message',
        'createdUtc' => $now, 'chatId' => $chatId, 'branchId' => 'BR-root', 'jobId' => $jobId, 'hostId' => $host,
        'agent' => $agent, 'author' => $env, 'text' => '🎙 ' . $text, 'attachments' => [], 'returnFiles' => [],
        'conversation' => $conversation, 'mode' => 'chat', 'model' => $model, 'effort' => null, 'voice' => $voice,
    ]]);
    $req = [
        'schemaVersion' => 3, 'protocolMinor' => 1, 'hostId' => $host, 'jobId' => $jobId, 'environmentId' => $chat['env'],
        'projectId' => $chat['project_id'], 'chatId' => $chatId, 'branchId' => 'BR-root', 'agent' => $agent,
        'instruction' => $instruction, 'conversation' => $conversation, 'attachments' => [], 'artifactPaths' => [],
        'timeoutMinutes' => 10, 'mode' => 'chat', 'model' => $model, 'effort' => null, 'output' => ['autoExport' => false],
        // a voice point has no screen to confirm anything on: no devices or context folders
        'permissions' => ['files' => 'read', 'shell' => true, 'web' => true], 'contextFolders' => [], 'voice' => $voice,
    ];
    $json = co_db_json($req);
    $sha = hash('sha256', co_db_json(['request' => $req, 'result' => null]));
    $st = $db->prepare(
        "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc,
                           request, sha, synced_utc, actor_env)
         VALUES (?, ?, ?, ?, ?, ?, 'chat', ?, 'inbox', 'db', UTC_TIMESTAMP(3), ?, ?, UTC_TIMESTAMP(3), ?)"
    );
    $st->execute([$jobId, $host, $chat['env'], $chat['project_id'], $chatId, $agent, $model, $json, $sha, $env]);
    $st->close();
    return ['jobId' => $jobId, 'resume' => $resume, 'agent' => $agent, 'host' => $host];
}

/** Host op voice_answer {jobId}: state and answer of a voice job. */
function co_voice_answer(mysqli $db, array $in): array
{
    $jobId = $in['jobId'] ?? null;
    if (!co_db_id_ok($jobId)) throw new InvalidArgumentException('jobId required');
    $r = co_voice_one($db, "SELECT state, JSON_VALUE(result, '$.agentFinalResponse') AS answer, JSON_VALUE(result, '$.error') AS error
                              FROM jobs WHERE job_id = ? AND JSON_EXISTS(request, '$.voice')", [$jobId]);
    if (!$r) return ['error' => 'not_found'];
    return ['state' => $r['state'], 'answer' => $r['answer'], 'failure' => $r['error']];
}

/** Start of a new voice session: the chat's title and protocol. */
function co_voice_preamble(mysqli $db, string $chatId): string
{
    $title = (string)(co_voice_one($db, 'SELECT title FROM chats WHERE chat_id = ?', [$chatId])['title'] ?? '');
    $st = $db->prepare('SELECT section, text FROM protocol WHERE chat_id = ? AND replaced_by IS NULL ORDER BY entry_no');
    $st->execute([$chatId]);
    $res = $st->get_result();
    $lines = [];
    $n = 0;
    while ($r = $res->fetch_assoc()) {
        $line = '- [' . $r['section'] . '] ' . $r['text'];
        $n += strlen($line) + 1;
        if ($n > CO_VOICE_PROTOCOL_MAX) break;
        $lines[] = $line;
    }
    $st->close();
    $out = "[CO voice: you are the agent of the AI Workbench chat \"$title\" ($chatId), asked by voice.]\n\n";
    return $lines ? $out . "[Protocol of the chat]\n" . implode("\n", $lines) . "\n[End of protocol]\n\n" : $out;
}

/** User op voice_ask_user {chatId, text, lang, point?}: a member's own voice client (co-android) asks the agent of a chat. */
function co_voice_ask_user(mysqli $db, array $in, string $myEnv): array
{
    if (!co_db_id_ok($myEnv)) return ['error' => 'forbidden'];
    return co_voice_ask($db, ['chatId' => $in['chatId'] ?? null, 'env' => $myEnv, 'text' => $in['text'] ?? null,
        'lang' => $in['lang'] ?? null, 'point' => $in['point'] ?? 'phone']);
}

/** User op voice_answer_user {jobId}: the answer of a voice job this member asked. */
function co_voice_answer_user(mysqli $db, array $in, string $myEnv): array
{
    $jobId = $in['jobId'] ?? null;
    if (!co_db_id_ok($jobId)) throw new InvalidArgumentException('jobId required');
    $own = co_voice_one($db, 'SELECT 1 AS x FROM jobs WHERE job_id = ? AND actor_env = ?', [$jobId, $myEnv]);
    return $own ? co_voice_answer($db, $in) : ['error' => 'not_found'];
}
