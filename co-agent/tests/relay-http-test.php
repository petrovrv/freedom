<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// relay-mcp.php (schema 24) over real HTTP against a throwaway MariaDB (never production): the test starts php -S on
// web/agent with a 2 s SSE segment, plays the home Bridge itself (relay_claim / relay_result on the database) and talks
// MCP Streamable HTTP like Claude Code: initialize, tools/list, tools/call with SSE, resumption with Last-Event-ID.
// Driver: python tests/actions-test.py --test relay-http-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/relay-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};

// ---- server: php -S with the same extensions and database port, RMCP_HOLD_SEC = 2 ----
$prepend = tempnam(sys_get_temp_dir(), 'rmcp');
file_put_contents($prepend, "<?php define('RMCP_HOLD_SEC', 2);");
$s = stream_socket_server('tcp://127.0.0.1:0');
$port = (int)substr(strrchr(stream_socket_get_name($s, false), ':'), 1);
fclose($s);
$cmd = [PHP_BINARY, '-d', 'extension_dir=' . ini_get('extension_dir'), '-d', 'extension=mysqli', '-d', 'extension=mbstring', '-d', 'extension=openssl',
    '-d', 'mysqli.default_port=' . ini_get('mysqli.default_port'), '-d', 'auto_prepend_file=' . $prepend,
    '-S', "127.0.0.1:$port", '-t', realpath(__DIR__ . '/../web/agent')];
$srv = proc_open($cmd, [0 => ['pipe', 'r'], 1 => ['file', 'nul', 'w'], 2 => ['file', 'nul', 'w']], $pipes);
for ($i = 0; $i < 50 && !@fsockopen('127.0.0.1', $port); $i++) usleep(100000);

// HTTP/1.1 without keep-alive; returns [status, headers (lowercase), body]
$open = static function (string $method, string $path, ?array $body, array $headers = []) use ($port) {
    $sock = fsockopen('127.0.0.1', $port, $en, $es, 5);
    $data = $body === null ? '' : json_encode($body);
    $h = "$method $path HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nAccept: application/json, text/event-stream\r\n";
    foreach ($headers as $k => $v) $h .= "$k: $v\r\n";
    if ($body !== null) $h .= "Content-Type: application/json\r\nContent-Length: " . strlen($data) . "\r\n";
    fwrite($sock, $h . "\r\n" . $data);
    return $sock;
};
$read = static function ($sock): array {
    stream_set_timeout($sock, 30);
    $raw = stream_get_contents($sock);
    fclose($sock);
    [$head, $body] = array_pad(explode("\r\n\r\n", (string)$raw, 2), 2, '');
    $lines = explode("\r\n", $head);
    $status = (int)(explode(' ', $lines[0])[1] ?? 0);
    $hs = [];
    foreach (array_slice($lines, 1) as $l) {
        [$k, $v] = array_pad(explode(':', $l, 2), 2, '');
        $hs[strtolower(trim($k))] = trim($v);
    }
    if (str_contains($hs['transfer-encoding'] ?? '', 'chunked')) {
        $out = '';
        while ($body !== '' && ($p = strpos($body, "\r\n")) !== false) {
            $n = hexdec(substr($body, 0, $p));
            if ($n === 0) break;
            $out .= substr($body, $p + 2, $n);
            $body = substr($body, $p + 2 + $n + 2);
        }
        $body = $out;
    }
    return [$status, $hs, $body];
};
$http = static fn(string $method, string $path, ?array $body, array $headers = []) => $read($open($method, $path, $body, $headers));
// SSE events of a body: [['id' => ..., 'retry' => ..., 'data' => ...], ...] (comments dropped)
$events = static function (string $body): array {
    $out = [];
    foreach (preg_split("/\n\n/", str_replace("\r\n", "\n", $body)) as $block) {
        $e = [];
        foreach (explode("\n", $block) as $l) {
            if ($l === '' || $l[0] === ':') continue;
            [$k, $v] = array_pad(explode(':', $l, 2), 2, '');
            $e[$k] = ltrim($v, ' ');
        }
        if ($e) $out[] = $e;
    }
    return $out;
};
$answer = static function (array $evs): ?array {
    foreach ($evs as $e) {
        if (($e['data'] ?? '') !== '') return json_decode($e['data'], true);
    }
    return null;
};
$lastId = static function (array $evs): ?string {
    $id = null;
    foreach ($evs as $e) if (isset($e['id'])) $id = $e['id'];
    return $id;
};
// The Bridge: wait for the queued call, claim it and answer
$bridge = static function (array $result, float $waitSec = 5.0) use ($db): ?array {
    $end = microtime(true) + $waitSec;
    do {
        $cl = relay_claim($db, ['host' => 'home-main', 'waitSec' => 0]);
        if ($cl['calls']) {
            relay_result($db, ['host' => 'home-main', 'callId' => $cl['calls'][0]['callId'], 'result' => $result]);
            return $cl['calls'][0];
        }
        usleep(100000);
    } while (microtime(true) < $end);
    return null;
};

// ---- data: a job, its chat, the devices of the home Bridge ----
$db->query("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P-own', 'owner', 'Mine', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES
    ('CHAT-1', 'P-own', 'Plain', '{}', 'x', UTC_TIMESTAMP(3)), ('CHAT-2', 'P-own', 'Other', '{}', 'x', UTC_TIMESTAMP(3))");
$db->query("INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, state, request, sha, synced_utc) VALUES
    ('J-1', 'site', 'owner', 'P-own', 'CHAT-1', 'claimed', '{}', '', UTC_TIMESTAMP(3)),
    ('J-2', 'site', 'owner', 'P-own', 'CHAT-2', 'claimed', '{}', '', UTC_TIMESTAMP(3))");
$homeTools = [
    ['name' => 'tool_help', 'description' => 'help', 'inputSchema' => ['type' => 'object', 'properties' => ['name' => ['type' => 'string']]]],
    ['name' => 'pc_shell', 'description' => 'shell', 'inputSchema' => ['type' => 'object', 'properties' => new stdClass()]],
    ['name' => 'pc_write', 'description' => 'write', 'inputSchema' => ['type' => 'object']],
];
relay_devices_put($db, ['host' => 'home-main', 'devices' => [
    ['deviceId' => 'h-home-main', 'environmentId' => 'owner', 'short' => 'home', 'name' => 'Home PC', 'kind' => 'home', 'online' => true, 'tools' => $homeTools],
    ['deviceId' => 'd-b903ab04eea11ba2', 'environmentId' => 'alex', 'short' => 'macalex', 'name' => 'Mac', 'kind' => 'mac', 'online' => true, 'tools' => $homeTools],
]]);
$t1 = relay_token($db, ['env' => 'owner', 'jobId' => 'J-1', 'chatId' => 'CHAT-1', 'deny' => ['pc_write'], 'ttlSec' => 600])['token'];
$t2 = relay_token($db, ['env' => 'owner', 'jobId' => 'J-2', 'chatId' => 'CHAT-2', 'ttlSec' => 600])['token'];
$auth = ['Authorization' => "Bearer $t1"];
$H = '/relay-mcp.php?d=home';

// ---- authorization ----
[$st] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 1, 'method' => 'initialize', 'params' => []]);
$ok('no key: 403 (not 401, no OAuth)', $st === 403);
[$st] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 1, 'method' => 'initialize', 'params' => []], ['Authorization' => 'Bearer rt-' . str_repeat('a', 48)]);
$ok('unknown key: 403', $st === 403);
[$st] = $http('POST', '/relay-mcp.php?d=Bad!', ['jsonrpc' => '2.0', 'id' => 1, 'method' => 'ping'], $auth);
$ok('bad device name: 400', $st === 400);

// ---- initialize, list ----
[$st, $hs, $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 1, 'method' => 'initialize', 'params' => ['protocolVersion' => '2025-11-25', 'capabilities' => new stdClass(), 'clientInfo' => ['name' => 't', 'version' => '1']]], $auth);
$r = json_decode($b, true);
$ok('initialize', $st === 200 && $r['id'] === 1 && $r['result']['protocolVersion'] === '2025-11-25' && $r['result']['serverInfo']['name'] === 'dev_home'
    && str_contains($r['result']['instructions'], 'home PC'));
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 1, 'method' => 'initialize', 'params' => ['protocolVersion' => '2099-01-01']], $auth);
$ok('unknown protocol: a supported one', json_decode($b, true)['result']['protocolVersion'] === '2025-06-18');
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'method' => 'notifications/initialized'], $auth);
$ok('notification: 202, no body', $st === 202 && $b === '');
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 'p', 'method' => 'ping'], $auth);
$ok('ping', $st === 200 && json_decode($b, true)['id'] === 'p');
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 2, 'method' => 'tools/list'], $auth);
$names = array_column(json_decode($b, true)['result']['tools'], 'name');
$ok('tools/list: every declared tool except the denied one', $names === ['tool_help', 'pc_shell']);
$ok('empty properties stay an object', str_contains($b, '"properties":{}'));
[$st, , $b] = $http('POST', '/relay-mcp.php?d=macalex', ['jsonrpc' => '2.0', 'id' => 2, 'method' => 'tools/list'], $auth);
$ok('device of another env: no tools', json_decode($b, true)['result']['tools'] === []);
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 3, 'method' => 'resources/list'], $auth);
$ok('unknown method: -32601', json_decode($b, true)['error']['code'] === -32601);
[$st] = $http('GET', $H, null, $auth);
$ok('GET without Last-Event-ID: 405', $st === 405);
[$st] = $http('DELETE', $H, null, $auth);
$ok('DELETE: 405', $st === 405);

// ---- tools/call refused before the relay ----
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 4, 'method' => 'tools/call', 'params' => ['name' => 'pc_write', 'arguments' => ['path' => 'x']]], $auth);
$r = json_decode($b, true);
$ok('denied tool: tool_off, nothing queued', $r['result']['isError'] === true && str_contains($r['result']['content'][0]['text'], 'tool_off')
    && (int)$db->query('SELECT COUNT(*) FROM relay_calls')->fetch_row()[0] === 0);
[$st, , $b] = $http('POST', '/relay-mcp.php?d=macalex', ['jsonrpc' => '2.0', 'id' => 4, 'method' => 'tools/call', 'params' => ['name' => 'pc_shell', 'arguments' => []]], $auth);
$ok('device of another env: refused', json_decode($b, true)['result']['isError'] === true);

// ---- a fast call: answer in the first segment ----
$sock = $open('POST', $H, ['jsonrpc' => '2.0', 'id' => 5, 'method' => 'tools/call', 'params' => ['name' => 'pc_shell', 'arguments' => ['command' => "hostname; echo '<?php system(1); ?>'"]]], $auth);
$call = $bridge(['ok' => true, 'content' => 'HOMEPC', 'untrusted' => true]);
[$st, $hs, $b] = $read($sock);
$evs = $events($b);
$a = $answer($evs);
$ok('call reaches the Bridge with job, env, chat and deny of the key', $call !== null && $call['jobId'] === 'J-1' && $call['environmentId'] === 'owner'
    && $call['chatId'] === 'CHAT-1' && $call['deny'] === ['pc_write'] && $call['deviceId'] === 'h-home-main' && $call['tainted'] === false
    && json_decode($call['argsJson'], true)['command'] === "hostname; echo '<?php system(1); ?>'");
$ok('SSE with a priming event (id, retry)', $st === 200 && str_starts_with($hs['content-type'] ?? '', 'text/event-stream')
    && isset($evs[0]['id'], $evs[0]['retry']) && str_starts_with($evs[0]['id'], $call['callId'] . '.'));
$ok('answer with the request id', $a['id'] === 5 && $a['result']['content'][0]['text'] === 'HOMEPC' && $a['result']['isError'] === false);
$ok('untrusted answer taints the job', relay_token_job($db, $t1)['tainted'] === true);

// ---- a long call: the stream ends without the answer, the client resumes with Last-Event-ID ----
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 'call-6', 'method' => 'tools/call', 'params' => ['name' => 'tool_help', 'arguments' => ['name' => 'aiwb-chat']]], $auth);
$evs = $events($b);
$eid = $lastId($evs);
$ok('segment ends without an answer', $st === 200 && $answer($evs) === null && $eid !== null);
$cl = relay_claim($db, ['host' => 'home-main', 'waitSec' => 0]);
$ok('later calls of the job are tainted', $cl['calls'][0]['tainted'] === true);
[$st, , $b] = $http('GET', $H, null, $auth + ['Last-Event-ID' => $eid]);
$evs2 = $events($b);
$ok('resumed stream still waiting, next event id', $st === 200 && $answer($evs2) === null && $lastId($evs2) !== $eid);
relay_result($db, ['host' => 'home-main', 'callId' => $cl['calls'][0]['callId'], 'result' => ['ok' => false, 'error' => ['code' => 'confirm_refused', 'message' => 'no']]]);
[$st, , $b] = $http('GET', $H, null, $auth + ['Last-Event-ID' => $lastId($evs2)]);
$a = $answer($events($b));
$ok('resumed stream gets the answer with the original id', $a['id'] === 'call-6' && $a['result']['isError'] === true
    && str_contains($a['result']['content'][0]['text'], 'confirm_refused'));
[$st] = $http('GET', $H, null, ['Authorization' => "Bearer $t2", 'Last-Event-ID' => $eid]);
$ok('another job cannot follow the call', $st === 404);
[$st] = $http('GET', $H, null, $auth + ['Last-Event-ID' => 'rc-zz.1.MQ']);
$ok('malformed Last-Event-ID: 405', $st === 405);

// ---- a call the Bridge never picks up ----
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 7, 'method' => 'tools/call', 'params' => ['name' => 'pc_shell', 'arguments' => []]], $auth);
$eid = $lastId($events($b));
$db->query('UPDATE relay_calls SET created_utc = UTC_TIMESTAMP(3) - INTERVAL 61 SECOND WHERE state = \'queued\'');
[$st, , $b] = $http('GET', $H, null, $auth + ['Last-Event-ID' => $eid]);
$a = $answer($events($b));
$ok('unclaimed: home_offline, withdrawn', $a['id'] === 7 && str_contains($a['result']['content'][0]['text'], 'home_offline')
    && (int)$db->query("SELECT COUNT(*) FROM relay_calls WHERE state = 'queued'")->fetch_row()[0] === 0);

// ---- R2 after the key was issued: the chat became shared ----
$db->query("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('chat', 'CHAT-2', 'sam', 'editor', UTC_TIMESTAMP())");
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 8, 'method' => 'tools/call', 'params' => ['name' => 'pc_shell', 'arguments' => []]], ['Authorization' => "Bearer $t2"]);
$a = json_decode($b, true);
$ok('shared since: not_allowed', $a['result']['isError'] === true && str_contains($a['result']['content'][0]['text'], 'not_allowed'));

// ---- pc_upload / pc_download with local_path: the server moves the bytes between the job's workspace and the call ----
$ws = co_agent_home() . '/co-agent/work/P-own/chats/CHAT-1/scratch';
@mkdir($ws . '/out', 0700, true);
file_put_contents($ws . '/out/a.bin', "\x00\x01hello");
$sock = $open('POST', $H, ['jsonrpc' => '2.0', 'id' => 20, 'method' => 'tools/call', 'params' => ['name' => 'pc_upload', 'arguments' => ['path' => 'C:\\x\\a.bin', 'local_path' => 'out/a.bin']]], $auth);
$call = $bridge(['ok' => true, 'content' => '{"path":"C:\\\\x\\\\a.bin","site":{"local_path":"' . realpath($ws) . '/out/a.bin"}}', 'untrusted' => true]);
[, , $b] = $read($sock);
$ua = $call === null ? [] : json_decode($call['argsJson'], true);
$ok('upload: the file goes as data_base64', ($ua['data_base64'] ?? '') === base64_encode("\x00\x01hello") && !isset($ua['local_path'])
    && $ua['site']['size'] === 7 && $ua['site']['eof'] === true && $ua['append'] === false);
[$st, , $b] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 21, 'method' => 'tools/call', 'params' => ['name' => 'pc_upload', 'arguments' => ['path' => 'C:\\x', 'local_path' => '../../../../secrets/db.json']]], $auth);
$ok('upload: outside the workspace refused', str_contains(json_decode($b, true)['result']['content'][0]['text'] ?? '', 'local_not_found'));
$sock = $open('POST', $H, ['jsonrpc' => '2.0', 'id' => 22, 'method' => 'tools/call', 'params' => ['name' => 'pc_download', 'arguments' => ['path' => 'C:\\x\\b.bin', 'local_path' => 'in/b.bin']]], $auth);
$cl = null;
for ($i = 0; $i < 50 && !$cl; $i++) { $c = relay_claim($db, ['host' => 'home-main', 'waitSec' => 0]); $cl = $c['calls'][0] ?? null; if (!$cl) usleep(100000); }
$site = json_decode($cl['argsJson'] ?? '{}', true)['site'] ?? [];
relay_result($db, ['host' => 'home-main', 'callId' => $cl['callId'], 'result' => ['ok' => true, 'untrusted' => true,
    'content' => json_encode(['path' => 'C:\\x\\b.bin', 'size' => 4, 'offset' => 0, 'length' => 4, 'eof' => true, 'data_base64' => base64_encode('WXYZ'), 'site' => $site])]]);
$a = $answer($events($read($sock)[2]));
$ok('download: written into the workspace, bytes left out', @file_get_contents($ws . '/in/b.bin') === 'WXYZ'
    && !str_contains($a['result']['content'][0]['text'] ?? '', 'data_base64') && str_contains($a['result']['content'][0]['text'] ?? '', 'Done'));
$sock = $open('POST', $H, ['jsonrpc' => '2.0', 'id' => 23, 'method' => 'tools/call', 'params' => ['name' => 'pc_download', 'arguments' => ['path' => 'C:\\x\\b.bin', 'local_path' => 'in/c.bin']]], $auth);
$bridge(['ok' => true, 'untrusted' => true, 'content' => json_encode(['offset' => 0, 'eof' => true, 'data_base64' => base64_encode('evil'), 'site' => ['local_path' => '/tmp/evil.txt']])]);
$a = $answer($events($read($sock)[2]));
$ok('download: a target the Bridge changed is refused', str_contains($a['result']['content'][0]['text'] ?? '', 'local_write') && !file_exists('/tmp/evil.txt'));

// ---- the job ends: its key is gone ----
relay_cancel($db, ['jobId' => 'J-1']);
[$st] = $http('POST', $H, ['jsonrpc' => '2.0', 'id' => 9, 'method' => 'tools/list'], $auth);
$ok('after the job: 403', $st === 403);

proc_terminate($srv);
proc_close($srv);
@unlink($prepend);
echo $fail ? "FAILED: $fail\n" : "all ok\n";
exit($fail ? 1 : 0);
