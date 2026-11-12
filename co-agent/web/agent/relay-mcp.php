<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// relay-mcp.php (schema 24, subtask A of chat CHAT-20261112071412-CJZ0): MCP over HTTP (Streamable HTTP, stateless)
// for the tools of one home device, served by the server itself. Replaces relay-mcp.mjs, a node process per device and
// job that used up the threads of the hosting account (CONNECTION_CLOSED, exit 128 under load).
//
// URL relay-mcp.php?d=<short> = MCP server dev_<short> of a site job; header Authorization: Bearer <key of the job>
// (relay_token, written by the site agent into the job's mcp.json). The key gives the environment, job, chat, deny list
// and taint; the R2 policy (restricted job, other env/chat, deviceToolsDeny) stays in relay_call_put.
//
// Long calls: LiteSpeed kills a PHP request after 120 s (LSAPI_MAX_PROCESS_TIME), and a confirmation on the device may
// take 40 min. tools/call answers as SSE: a priming event with an id and retry, comments as heartbeat, and after
// RMCP_HOLD_SEC the stream ends without the answer; the client (MCP SDK, Claude Code 2.1.283+) reconnects with GET and
// Last-Event-ID after retry ms and this script waits again. An lsphp process is held only while a call is waiting,
// none between the calls of a job.
declare(strict_types=1);

require_once __DIR__ . '/relay-lib.php';

const RMCP_PROTOCOLS = ['2025-11-25', '2025-06-18', '2025-03-26'];
if (!defined('RMCP_HOLD_SEC')) {
    define('RMCP_HOLD_SEC', 25);     // one SSE segment, far below the 120 s of LiteSpeed (tests define a shorter one)
}
const RMCP_BEAT_SEC = 8;             // heartbeat comment; also how often a closed connection is noticed
const RMCP_CALL_TIMEOUT_SEC = 2400;  // a person may have to confirm on the device (Bridge ask-user budget 30 min)
const RMCP_MAX_BODY = 2 * 1048576;

header('Cache-Control: no-store');

function rmcp_json(int $status, ?array $body): never
{
    http_response_code($status);
    if ($body !== null) {
        header('Content-Type: application/json');
        echo json_encode($body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE);
    }
    exit;
}

function rmcp_error(int $status, mixed $id, int $code, string $message): never
{
    rmcp_json($status, ['jsonrpc' => '2.0', 'id' => $id, 'error' => ['code' => $code, 'message' => $message]]);
}

function rmcp_text(mixed $id, string $text, bool $isError): array
{
    return ['jsonrpc' => '2.0', 'id' => $id, 'result' => ['content' => [['type' => 'text', 'text' => $text]], 'isError' => $isError]];
}

// The MCP result of a relay answer ({ok, content, error, untrusted}), as relay-mcp.mjs gave it
function rmcp_result_text(?array $r): array
{
    if (!is_array($r)) {
        return ['Error bad_result: Empty answer from the home Bridge.', true];
    }
    if (!empty($r['ok'])) {
        return [is_string($r['content'] ?? null) ? $r['content'] : (string)json_encode($r['content'] ?? ''), false];
    }
    $e = is_array($r['error'] ?? null) ? $r['error'] : [];
    return ['Error ' . (string)($e['code'] ?? 'failed') . ': ' . (string)($e['message'] ?? ''), true];
}

// ---- pc_upload / pc_download with local_path (Bridge 1.15.0 pc_* tools, subtask B): files of the site job itself.
// The server reads the chunk of the job's file into data_base64 (upload) or writes the chunk the Bridge returned into it
// (download), so the bytes never pass through the model. Only inside the job's workspace (the chat scratch of the site
// agent, work/<project>/chats/<chat>/scratch), checked after realpath; one chunk per call, the answer says where the
// next one starts.
const RMCP_UP_CHUNK = 700000;        // base64 of it stays below RELAY_MAX_ARGS (1 MiB)

function rmcp_workspace(mysqli $db, array $tok): ?string
{
    if ($tok['chatId'] === '' || preg_match('/^[A-Za-z0-9_-]{1,80}$/', $tok['chatId']) !== 1) {
        return null;
    }
    $rows = share_q($db, 'SELECT project_id FROM chats WHERE chat_id = ?', [$tok['chatId']]);
    $project = $rows ? (string)$rows[0]['project_id'] : '';
    if (preg_match('/^[A-Za-z0-9_.-]{1,120}$/', $project) !== 1 || str_contains($project, '..')) {
        return null;
    }
    $ws = realpath(co_agent_home() . '/co-agent/work/' . $project . '/chats/' . $tok['chatId'] . '/scratch');
    return $ws === false ? null : $ws;
}

function rmcp_inside(string $ws, string $real): bool
{
    return $real === $ws || str_starts_with($real, $ws . DIRECTORY_SEPARATOR);
}

// Real path of a file of the workspace; $create makes missing parent folders (inside it only).
function rmcp_local_file(string $ws, string $p, bool $create): ?string
{
    if ($p === '' || str_contains($p, "\0")) {
        return null;
    }
    $abs = $p[0] === '/' || preg_match('/^[A-Za-z]:[\\\\\/]/', $p) === 1 ? $p : $ws . '/' . $p;
    if (is_link($abs)) {
        return null;          // a (dangling) link would be followed by fopen (review B-02)
    }
    $real = realpath($abs);
    if ($real !== false) {
        return rmcp_inside($ws, $real) && !is_dir($real) && !is_link($real) ? $real : null;
    }
    if (!$create) {
        return null;
    }
    $dir = dirname($abs);
    $up = $dir;
    while (realpath($up) === false && $up !== dirname($up)) {
        $up = dirname($up);
    }
    $base = realpath($up);
    if ($base === false || !rmcp_inside($ws, $base) || preg_match('#(^|/)\.\.(/|$)#', substr($dir, strlen($up))) === 1) {
        return null;
    }
    if (!is_dir($dir) && !@mkdir($dir, 0700, true)) {
        return null;
    }
    $rd = realpath($dir);
    return $rd !== false && rmcp_inside($ws, $rd) ? $rd . '/' . basename($abs) : null;
}

// Arguments of the call with the job's file attached (upload) or its target noted (download), or [null, error text].
function rmcp_local_args(mysqli $db, array $tok, string $tool, array $args): array
{
    $ws = rmcp_workspace($db, $tok);
    if ($ws === null) {
        return [null, 'Error local_unavailable: This job has no workspace on the site; give data_base64 instead of local_path.'];
    }
    $local = (string)$args['local_path'];
    unset($args['local_path']);
    $off = is_int($args['offset'] ?? null) ? max(0, $args['offset']) : 0;
    if ($tool === 'pc_upload') {
        $f = rmcp_local_file($ws, $local, false);
        if ($f === null || !is_file($f)) {
            return [null, "Error local_not_found: No file $local in the job's workspace ($ws)."];
        }
        $size = filesize($f);
        $h = fopen($f, 'rb');
        if ($h === false || ($off > 0 && fseek($h, $off) !== 0)) {
            return [null, "Error local_read: Cannot read $local."];
        }
        $data = $off < $size ? (string)fread($h, RMCP_UP_CHUNK) : '';
        fclose($h);
        unset($args['offset']);
        $args['data_base64'] = base64_encode($data);
        $args['append'] = $off > 0;
        $args['site'] = ['local_path' => $f, 'offset' => $off, 'length' => strlen($data), 'size' => $size,
            'next' => $off + strlen($data), 'eof' => $off + strlen($data) >= $size];
        return [$args, null];
    }
    $f = rmcp_local_file($ws, $local, true);
    if ($f === null) {
        return [null, "Error local_path: $local is outside the job's workspace ($ws)."];
    }
    $args['site'] = ['local_path' => $f];
    return [$args, null];
}

// A pc_download answer for local_path: its chunk is written into the job's file and left out of the text.
function rmcp_local_save(mysqli $db, array $tok, string $callId, array $res): array
{
    $c = json_decode(is_string($res['content'] ?? null) ? $res['content'] : '', true);
    if (empty($res['ok']) || !is_array($c) || !is_array($c['site'] ?? null) || !is_string($c['site']['local_path'] ?? null) || !is_string($c['data_base64'] ?? null)) {
        return $res;
    }
    $st = $db->prepare('SELECT tool FROM relay_calls WHERE call_id = ?');
    $st->execute([$callId]);
    $tool = (string)($st->get_result()->fetch_row()[0] ?? '');
    $st->close();
    $ws = rmcp_workspace($db, $tok);
    $f = $ws === null ? null : rmcp_local_file($ws, $c['site']['local_path'], true);
    $data = base64_decode($c['data_base64'], true);
    $off = is_int($c['offset'] ?? null) ? max(0, $c['offset']) : 0;
    if ($tool !== 'pc_download' || $f === null || $data === false) {
        return ['ok' => false, 'content' => '', 'error' => ['code' => 'local_write', 'message' => 'The chunk could not be saved in the job\'s workspace.']];
    }
    $h = fopen($f, 'c+b');
    if ($h === false || ($off === 0 && !ftruncate($h, 0)) || fseek($h, $off) !== 0 || fwrite($h, $data) !== strlen($data)) {
        return ['ok' => false, 'content' => '', 'error' => ['code' => 'local_write', 'message' => "Cannot write $f."]];
    }
    fclose($h);
    unset($c['data_base64']);
    $c['saved_to'] = $f;
    $c['next'] = $off + strlen($data);
    $eof = !empty($c['eof']);
    $res['content'] = json_encode($c, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE)
        . ($eof ? "\nDone: the whole file is in $f." : "\nMore to fetch: call pc_download again with the same path and local_path and offset=" . $c['next'] . '.');
    return $res;
}

// Event id of a waiting call: "<callId>.<seq>.<base64url(json rpc id)>" - the resumed GET knows the call and the id
function rmcp_event_id(string $callId, int $seq, mixed $rpcId): string
{
    return $callId . '.' . $seq . '.' . rtrim(strtr(base64_encode((string)json_encode($rpcId)), '+/', '-_'), '=');
}

function rmcp_parse_event_id(string $eid): ?array
{
    if (preg_match('/^(rc-[0-9a-f]{32})\.(\d{1,6})\.([A-Za-z0-9_-]{1,120})$/', $eid, $m) !== 1) {
        return null;
    }
    $id = json_decode((string)base64_decode(strtr($m[3], '-_', '+/')), true);
    if (!is_int($id) && !is_string($id)) {
        return null;
    }
    return ['callId' => $m[1], 'seq' => (int)$m[2], 'rpcId' => $id];
}

function rmcp_sse_start(): void
{
    @ini_set('zlib.output_compression', '0');
    while (ob_get_level() > 0) {
        ob_end_flush();
    }
    http_response_code(200);
    header('Content-Type: text/event-stream');
    header('Cache-Control: no-cache, no-transform');
    header('X-Accel-Buffering: no');
}

function rmcp_sse(string $chunk): void
{
    echo $chunk;
    flush();
}

// Wait for a call for one segment. Ends with the answer, or without it (the client resumes after retry ms).
function rmcp_wait(mysqli $db, array $tok, string $callId, int $seq, mixed $rpcId, string $deviceName): never
{
    ignore_user_abort(true);
    set_time_limit(RMCP_HOLD_SEC + 60);
    $st = $db->prepare('SELECT TIMESTAMPDIFF(SECOND, created_utc, UTC_TIMESTAMP(3)) FROM relay_calls WHERE call_id = ?');
    $st->execute([$callId]);
    $age = (int)($st->get_result()->fetch_row()[0] ?? 0);
    $st->close();
    // Fast answers come within the first segment; a call waiting long (a confirmation) is looked at less often
    $retry = $age < 60 ? 500 : ($age < 300 ? 3000 : 10000);
    rmcp_sse_start();
    rmcp_sse('id: ' . rmcp_event_id($callId, $seq, $rpcId) . "\nretry: $retry\ndata:\n\n");
    $end = microtime(true) + RMCP_HOLD_SEC;
    for (;;) {
        $left = $end - microtime(true);
        $g = relay_call_get($db, ['callId' => $callId, 'waitSec' => max(0.0, min((float)RMCP_BEAT_SEC, $left))]);
        $state = $g['state'] ?? '';
        $final = null;
        if ($state === 'done') {
            $res = is_array($g['result'] ?? null) ? $g['result'] : null;
            if (!empty($res['untrusted']) && !$tok['tainted']) {
                relay_token_taint($db, $tok['hash']);
            }
            if (is_array($res) && str_contains((string)($res['content'] ?? ''), '"local_path"')) {
                $res = rmcp_local_save($db, $tok, $callId, $res);
            }
            $final = rmcp_result_text($res);
        } elseif ($state === 'expired' || $state === 'missing' || $age + (int)RMCP_HOLD_SEC > RMCP_CALL_TIMEOUT_SEC + 120) {
            $final = ["Error device_timeout: No answer from $deviceName in time.", true];
        } elseif ($state === 'unclaimed') {
            $final = ['Error home_offline: The home Bridge did not pick up the call within 60 s (the home PC may be asleep or Bridge restarting); '
                . 'the call was withdrawn and will not run. Do not retry at once; tell the user.', true];
        }
        if ($final !== null) {
            $msg = rmcp_text($rpcId, $final[0], $final[1]);
            rmcp_sse("event: message\ndata: " . json_encode($msg, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE) . "\n\n");
            exit;
        }
        if (microtime(true) >= $end - 0.2) {
            exit; // the client comes back with Last-Event-ID
        }
        rmcp_sse(": waiting\n\n");
        if (connection_aborted()) {
            exit; // the job ended or Claude gave up; relay_cancel of the job end withdraws the call
        }
    }
}

$method = $_SERVER['REQUEST_METHOD'] ?? '';
if ($method !== 'POST' && $method !== 'GET') {
    rmcp_error(405, null, -32000, 'Method not allowed.');
}
if (!preg_match('/^Bearer\s+(\S+)$/', (string)($_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? ''), $m)) {
    rmcp_error(403, null, -32001, 'Missing relay key.');
}
$short = $_GET['d'] ?? '';
if (!is_string($short) || preg_match('/^[a-z0-9]{1,12}$/', $short) !== 1) {
    rmcp_error(400, null, -32602, 'Invalid device.');
}

try {
    $db = co_db();
    // 403, not 401: a 401 makes the MCP client start an OAuth sign-in
    $tok = relay_token_job($db, $m[1]);
    if ($tok === null) {
        rmcp_error(403, null, -32001, 'The relay key of this job is unknown or expired (the job ended).');
    }
    $dev = relay_device_by_short($db, $tok['env'], $short);
    $devName = $dev['name'] ?? $short;

    if ($method === 'GET') {
        // Only the resumption of a waiting call; no standalone stream (the server never sends requests)
        $eid = rmcp_parse_event_id((string)($_SERVER['HTTP_LAST_EVENT_ID'] ?? ''));
        if ($eid === null) {
            rmcp_error(405, null, -32000, 'No standalone stream.');
        }
        if (relay_call_job($db, $eid['callId']) !== $tok['jobId']) {
            rmcp_json(404, null);
        }
        rmcp_wait($db, $tok, $eid['callId'], $eid['seq'] + 1, $eid['rpcId'], $devName);
    }

    if ((int)($_SERVER['CONTENT_LENGTH'] ?? 0) > RMCP_MAX_BODY) {
        rmcp_error(413, null, -32600, 'Request too large.');
    }
    $raw = file_get_contents('php://input', false, null, 0, RMCP_MAX_BODY + 1);
    $req = is_string($raw) && strlen($raw) <= RMCP_MAX_BODY ? json_decode($raw, true) : null;
    if (!is_array($req) || array_is_list($req)) {
        rmcp_error(400, null, -32700, 'Parse error (one JSON-RPC message per request).');
    }
    $id = $req['id'] ?? null;
    $rpc = is_string($req['method'] ?? null) ? $req['method'] : '';
    if ($rpc === '' || $id === null) {
        rmcp_json(202, null); // notifications (initialized, cancelled) and responses
    }
    if (!is_int($id) && !is_string($id)) {
        rmcp_error(400, null, -32600, 'Invalid id.');
    }
    $p = is_array($req['params'] ?? null) ? $req['params'] : [];
    $tools = array_values(array_filter($dev['tools'] ?? [], static fn($t) => is_object($t) && !in_array($t->name ?? '', $tok['deny'], true)));

    if ($rpc === 'initialize') {
        $want = is_string($p['protocolVersion'] ?? null) ? $p['protocolVersion'] : '';
        $kind = $dev['kind'] ?? '';
        $what = $kind === 'home' ? 'the home PC itself (its tool library and commands)'
            : ($kind === 'mac' ? "the Mac \"$devName\"" : "the computer \"$devName\" (CO Desktop, its CO browser and folders)");
        rmcp_json(200, ['jsonrpc' => '2.0', 'id' => $id, 'result' => [
            'protocolVersion' => in_array($want, RMCP_PROTOCOLS, true) ? $want : RMCP_PROTOCOLS[1],
            'capabilities' => ['tools' => new stdClass()],
            'serverInfo' => ['name' => "dev_$short", 'version' => '2.0.0'],
            'instructions' => "Tools of $what, reached from the site through the home Bridge. They act on that device, "
                . 'with its own rights and confirmations (a person there may have to approve). Sending messages is never allowed.',
        ]]);
    }
    if ($rpc === 'ping') {
        rmcp_json(200, ['jsonrpc' => '2.0', 'id' => $id, 'result' => new stdClass()]);
    }
    if ($rpc === 'tools/list') {
        rmcp_json(200, ['jsonrpc' => '2.0', 'id' => $id, 'result' => ['tools' => $tools]]);
    }
    if ($rpc !== 'tools/call') {
        rmcp_error(200, $id, -32601, "Method not found: $rpc");
    }

    $tool = is_string($p['name'] ?? null) ? $p['name'] : '';
    if (in_array($tool, $tok['deny'], true)) {
        rmcp_json(200, rmcp_text($id, 'Error tool_off: This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.', true));
    }
    if ($dev === null) {
        rmcp_json(200, rmcp_text($id, "Error home_offline: The device dev_$short is not published by the home Bridge now.", true));
    }
    $args = $p['arguments'] ?? [];
    if (($tool === 'pc_upload' || $tool === 'pc_download') && is_array($args) && is_string($args['local_path'] ?? null)) {
        [$args, $why] = rmcp_local_args($db, $tok, $tool, $args);
        if ($why !== null) {
            rmcp_json(200, rmcp_text($id, $why, true));
        }
    }
    try {
        $put = relay_call_put($db, ['env' => $tok['env'], 'jobId' => $tok['jobId'], 'chatId' => $tok['chatId'], 'deviceId' => $dev['deviceId'],
            'tool' => $tool, 'args' => is_array($args) ? $args : [], 'tainted' => $tok['tainted'], 'deny' => $tok['deny'],
            'timeoutSec' => RMCP_CALL_TIMEOUT_SEC]);
    } catch (InvalidArgumentException $e) {
        rmcp_json(200, rmcp_text($id, 'Error bad_request: ' . $e->getMessage(), true));
    }
    if (empty($put['ok'])) {
        $e = is_array($put['error'] ?? null) ? $put['error'] : [];
        rmcp_json(200, rmcp_text($id, 'Error ' . (string)($e['code'] ?? 'relay_refused') . ': ' . (string)($e['message'] ?? 'The server refused the call.'), true));
    }
    rmcp_wait($db, $tok, (string)$put['callId'], 0, $id, $devName);
} catch (Throwable $e) {
    error_log('relay-mcp: ' . $e->getMessage());
    if (!headers_sent()) {
        rmcp_error(500, null, -32603, 'Server error.');
    }
}
