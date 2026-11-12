<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// relay-lib.php (schema 22, S1 of chat CHAT-20261111085044-HV3A): the site agent calls tools of home devices through the
// server. A Bridge publishes the devices it serves (relay_devices_put: CO Desktop, CO Resident Macs, the Bridge itself as
// "h-<host>"), the site agent lists them for the job's environment (relay_devices), queues calls (relay_call_put) and
// waits for the answer (relay_call_get); the Bridge long-polls relay_claim, runs each call over its own signed device
// channel (the device keeps its rights, confirmations and trust) and stores the reply (relay_result). Host key only.
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';
require_once __DIR__ . '/share-lib.php';
require_once __DIR__ . '/sandbox-lib.php';

const RELAY_FRESH_SEC = 90;          // a Bridge that did not publish its devices for this long is offline
const RELAY_MAX_DEVICES = 20;
const RELAY_MAX_TOOLS = 1048576;
const RELAY_MAX_ARGS = 1048576;
const RELAY_MAX_RESULT = 4194304;
const RELAY_MAX_WAIT = 20;           // long poll of relay_claim / relay_call_get, seconds
const RELAY_PICKUP_SEC = 60;         // a call the Bridge has not claimed by then never runs (the agent was told it failed)
const RELAY_KINDS = ['desktop', 'mac', 'home'];

function relay_str(array $in, string $key, string $re, string $what): string
{
    $v = $in[$key] ?? null;
    if (!is_string($v) || preg_match($re, $v) !== 1) {
        throw new InvalidArgumentException("invalid $what");
    }
    return $v;
}

function relay_host(array $in): string
{
    return relay_str($in, 'host', '/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/', 'host');
}

function relay_env(array $in, string $key = 'env'): string
{
    return relay_str($in, $key, '/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', $key);
}

function relay_device_id(mixed $v): string
{
    if (!is_string($v) || preg_match('/^(d-[0-9a-f]{16}|h-[A-Za-z0-9_-]{1,38})$/', $v) !== 1) {
        throw new InvalidArgumentException('invalid deviceId');
    }
    return $v;
}

function relay_call_id(mixed $v): string
{
    if (!is_string($v) || preg_match('/^rc-[0-9a-f]{32}$/', $v) !== 1) {
        throw new InvalidArgumentException('invalid callId');
    }
    return $v;
}

// Tool names a chat turned off (deviceToolsDeny of the job), at most 32
function relay_deny(mixed $v): array
{
    if ($v === null) {
        return [];
    }
    if (!is_array($v) || !array_is_list($v) || count($v) > 32) {
        throw new InvalidArgumentException('invalid deny');
    }
    foreach ($v as $n) {
        if (!is_string($n) || preg_match('/^[A-Za-z0-9_-]{1,64}$/', $n) !== 1) {
            throw new InvalidArgumentException('invalid deny');
        }
    }
    return array_values(array_unique($v));
}

// Job ids the Bridge acknowledges as cancelled, at most 64
function relay_job_ids(mixed $v): array
{
    if (!is_array($v) || !array_is_list($v)) {
        return [];
    }
    $out = [];
    foreach (array_slice($v, 0, 64) as $j) {
        if (is_string($j) && preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', $j) === 1) {
            $out[] = $j;
        }
    }
    return $out;
}

function relay_wait(array $in): float
{
    $w = $in['waitSec'] ?? 0;
    return is_int($w) || is_float($w) ? max(0.0, min((float)RELAY_MAX_WAIT, (float)$w)) : 0.0;
}

// db.php decodes bodies as arrays, so an empty schema {} of a device tool (often "properties": {} of a tool without
// arguments) arrives as []; Claude Code drops a whole MCP server whose tool has "properties": [] (dev_<desktop> was never
// listed on the site). Only schema positions are walked: maps of schemas (properties, $defs, ...), single schemas
// (items, not, ...) and lists of schemas (anyOf, ...); names of properties and literal values (enum, const, default)
// are kept as they came.
const RELAY_SCHEMA_MAPS = ['properties', 'patternProperties', 'definitions', '$defs', 'dependentSchemas'];
const RELAY_SCHEMA_ONE = ['items', 'additionalProperties', 'additionalItems', 'not', 'if', 'then', 'else', 'contains',
    'propertyNames', 'unevaluatedProperties', 'unevaluatedItems', 'contentSchema'];
const RELAY_SCHEMA_LISTS = ['allOf', 'anyOf', 'oneOf', 'prefixItems'];

function relay_schema_fix(mixed $schema): mixed
{
    if ($schema === []) {
        return new stdClass();
    }
    if (!is_array($schema) || array_is_list($schema)) {
        return $schema;
    }
    foreach ($schema as $k => $v) {
        if (!is_array($v)) {
            continue;
        }
        if (in_array($k, RELAY_SCHEMA_MAPS, true)) {
            if ($v === []) {
                $schema[$k] = new stdClass();
            } elseif (!array_is_list($v)) {
                foreach ($v as $name => $sub) {
                    $v[$name] = relay_schema_fix($sub);
                }
                $schema[$k] = $v;
            }
        } elseif (in_array($k, RELAY_SCHEMA_ONE, true)) {
            $schema[$k] = $v !== [] && array_is_list($v) ? array_map('relay_schema_fix', $v) : relay_schema_fix($v); // items as a tuple
        } elseif (in_array($k, RELAY_SCHEMA_LISTS, true) && array_is_list($v)) {
            $schema[$k] = array_map('relay_schema_fix', $v);
        }
    }
    return $schema;
}

// The tools of a device: the root inputSchema / outputSchema of each tool
function relay_tools_fix(array $tools): array
{
    foreach ($tools as $i => $t) {
        if (!is_array($t)) {
            continue;
        }
        foreach (['inputSchema', 'outputSchema'] as $k) {
            if (isset($t[$k]) && is_array($t[$k])) {
                $t[$k] = $t[$k] === [] ? ($k === 'inputSchema' ? ['type' => 'object'] : new stdClass()) : relay_schema_fix($t[$k]);
            }
        }
        $tools[$i] = $t;
    }
    return $tools;
}

// Bridge: replace the set of devices it serves (every 30 s)
function relay_devices_put(mysqli $db, array $in): array
{
    $host = relay_host($in);
    $list = $in['devices'] ?? null;
    if (!is_array($list) || !array_is_list($list) || count($list) > RELAY_MAX_DEVICES) {
        throw new InvalidArgumentException('invalid devices');
    }
    $rows = [];
    $shorts = [];
    foreach ($list as $d) {
        if (!is_array($d)) {
            throw new InvalidArgumentException('invalid device');
        }
        $id = relay_device_id($d['deviceId'] ?? null);
        $env = relay_env($d, 'environmentId');
        $short = relay_str($d, 'short', '/^[a-z0-9]{1,12}$/', 'short');
        $name = is_string($d['name'] ?? null) ? mb_substr(trim($d['name']), 0, 120) : '';
        $kind = $d['kind'] ?? null;
        if (!in_array($kind, RELAY_KINDS, true)) {
            throw new InvalidArgumentException('invalid kind');
        }
        $tools = $d['tools'] ?? null;
        if (!is_array($tools) || !array_is_list($tools) || count($tools) > 100) {
            throw new InvalidArgumentException('invalid tools');
        }
        foreach ($tools as $t) {
            $tn = is_array($t) ? ($t['name'] ?? null) : null;
            if (!is_string($tn) || preg_match('/^[A-Za-z0-9_-]{1,64}$/', $tn) !== 1) {
                throw new InvalidArgumentException('invalid tool name');
            }
        }
        $toolsJson = co_db_json(relay_tools_fix($tools));
        if (strlen($toolsJson) > RELAY_MAX_TOOLS) {
            throw new InvalidArgumentException('tools too large');
        }
        if (isset($rows[$id]) || isset($shorts[$env . '/' . $short])) {
            throw new InvalidArgumentException('duplicate device');
        }
        $shorts[$env . '/' . $short] = true;
        $rows[$id] = [$host, $id, $env, $short, $name !== '' ? $name : $short, $kind, !empty($d['online']) ? 1 : 0, $toolsJson];
    }

    $db->begin_transaction();
    try {
        $st = $db->prepare('DELETE FROM relay_devices WHERE host_id = ?');
        $st->execute([$host]);
        $st->close();
        if ($rows) {
            $st = $db->prepare('INSERT INTO relay_devices (host_id, device_id, environment_id, short_name, name, kind, online, tools_json, updated_utc)
                                VALUES (?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3))');
            foreach ($rows as $r) {
                $st->execute($r);
            }
            $st->close();
        }
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return ['ok' => true, 'devices' => count($rows)];
}

// R2 (review S1-02): the device policy of a site job is decided here, not by the agent. A job of a shared chat, of a
// chat below one (subtasks) or queued restricted gets no device; a job the server does not know (no jobs row, no chat
// row) neither. The env must be the job's own.
function relay_job_restricted(mysqli $db, string $env, string $jobId, string $chatId): bool
{
    $rows = share_q($db, "SELECT env, project_id, chat_id, JSON_EXTRACT(request, '$.restricted') AS restricted FROM jobs WHERE job_id = ?", [$jobId]);
    if ($rows) {
        $r = $rows[0];
        if ((string)$r['env'] !== $env || ($chatId !== '' && (string)$r['chat_id'] !== $chatId) || $r['restricted'] === 'true') {
            return true;
        }
        return share_restricted($db, (string)$r['project_id'], (string)$r['chat_id']);
    }
    if ($chatId === '') {
        return true;
    }
    $rows = share_q($db, 'SELECT c.project_id, p.env FROM chats c LEFT JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?', [$chatId]);
    if (!$rows || ($rows[0]['env'] !== null && (string)$rows[0]['env'] !== $env)) {
        return true;
    }
    return share_restricted($db, (string)$rows[0]['project_id'], $chatId);
}

const RELAY_RESTRICTED = ['code' => 'not_allowed', 'message' => 'This job belongs to a shared chat (or a subtask of one): the home devices are not available to it.'];

// Site agent: devices of an environment whose Bridge published them in the last RELAY_FRESH_SEC. With jobId (site
// agent 1.7.0+) none for a restricted job.
function relay_devices(mysqli $db, array $in): array
{
    $env = relay_env($in);
    if (isset($in['jobId'])) {
        $job = relay_str($in, 'jobId', '/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', 'jobId');
        $chat = isset($in['chatId']) && $in['chatId'] !== '' ? relay_str($in, 'chatId', '/^[A-Za-z0-9_-]{1,80}$/', 'chatId') : '';
        if (relay_job_restricted($db, $env, $job, $chat)) {
            return ['devices' => [], 'restricted' => true];
        }
    }
    $st = $db->prepare('SELECT host_id, device_id, short_name, name, kind, online, tools_json, updated_utc
                        FROM relay_devices
                        WHERE environment_id = ? AND updated_utc > UTC_TIMESTAMP(3) - INTERVAL ' . RELAY_FRESH_SEC . ' SECOND
                        ORDER BY kind, short_name');
    $st->execute([$env]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    // schema 26: of the sandbox seats of this environment only the picked sandbox's (own, else a family member's)
    [$seats, $picked] = sandbox_seats($db, $env);
    $out = [];
    foreach ($rows as $r) {
        if (in_array((string)$r['device_id'], $seats, true) && (string)$r['device_id'] !== $picked) {
            continue;
        }
        $tools = json_decode((string)$r['tools_json'], true);
        $out[] = [
            'host' => (string)$r['host_id'], 'deviceId' => (string)$r['device_id'], 'short' => (string)$r['short_name'],
            'name' => (string)$r['name'], 'kind' => (string)$r['kind'], 'online' => (bool)(int)$r['online'],
            'tools' => is_array($tools) ? relay_tools_fix($tools) : [], 'updatedUtc' => co_db_iso((string)$r['updated_utc']),
        ];
    }
    return ['devices' => $out];
}

// Site agent: queue a call to a device of the job's environment
function relay_call_put(mysqli $db, array $in): array
{
    $env = relay_env($in);
    $job = relay_str($in, 'jobId', '/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', 'jobId');
    $chat = isset($in['chatId']) && $in['chatId'] !== '' ? relay_str($in, 'chatId', '/^[A-Za-z0-9_-]{1,80}$/', 'chatId') : '';
    $device = relay_device_id($in['deviceId'] ?? null);
    $tool = relay_str($in, 'tool', '/^[A-Za-z0-9_-]{1,64}$/', 'tool');
    $args = $in['args'] ?? [];
    if (!is_array($args) || ($args !== [] && array_is_list($args))) {
        throw new InvalidArgumentException('invalid args');
    }
    $argsJson = $args === [] ? '{}' : co_db_json($args);
    if (strlen($argsJson) > RELAY_MAX_ARGS) {
        throw new InvalidArgumentException('args too large');
    }
    $deny = relay_deny($in['deny'] ?? null);
    if (in_array($tool, $deny, true)) {
        return ['ok' => false, 'error' => ['code' => 'tool_off', 'message' => 'This tool is turned off for this chat (its connector button in the CO composer is off).']];
    }
    $timeout = $in['timeoutSec'] ?? 600;
    $timeout = is_int($timeout) ? max(10, min(2400, $timeout)) : 600;
    if (relay_job_restricted($db, $env, $job, $chat)) {
        return ['ok' => false, 'error' => RELAY_RESTRICTED];
    }

    // The device must belong to this environment and its Bridge must be alive (TZ §8.3: a member's device serves only
    // jobs of its environment; the Bridge checks again before it calls the device).
    $st = $db->prepare('SELECT host_id, online, updated_utc > UTC_TIMESTAMP(3) - INTERVAL ' . RELAY_FRESH_SEC . ' SECOND AS fresh
                        FROM relay_devices WHERE device_id = ? AND environment_id = ? ORDER BY updated_utc DESC LIMIT 1');
    $st->execute([$device, $env]);
    $dev = $st->get_result()->fetch_assoc();
    $st->close();
    if ($dev === null) {
        return ['ok' => false, 'error' => ['code' => 'not_allowed', 'message' => "Device $device is not a device of environment '$env'."]];
    }
    [$seats, $picked] = sandbox_seats($db, $env);
    if (in_array($device, $seats, true) && $device !== $picked) {
        return ['ok' => false, 'error' => ['code' => 'sandbox_not_selected', 'message' => 'This sandbox is not the one picked for the environment now (own sandbox first, then a family member\'s).']];
    }
    if (!(int)$dev['fresh']) {
        return ['ok' => false, 'error' => ['code' => 'home_offline', 'message' => 'The home Bridge has not been heard from for over ' . RELAY_FRESH_SEC . ' s; the device cannot be reached now.']];
    }

    $callId = 'rc-' . bin2hex(random_bytes(16));
    $st = $db->prepare('INSERT INTO relay_calls (call_id, host_id, device_id, job_id, environment_id, chat_id, tool, args_json, tainted, deny_json, created_utc, deadline_utc)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL ? SECOND)');
    $st->execute([$callId, (string)$dev['host_id'], $device, $job, $env, $chat, $tool, $argsJson, !empty($in['tainted']) ? 1 : 0, co_db_json($deny), $timeout]);
    $st->close();
    return ['ok' => true, 'callId' => $callId];
}

// Site agent: state of a call; waits up to waitSec for it to finish
function relay_call_get(mysqli $db, array $in): array
{
    $callId = relay_call_id($in['callId'] ?? null);
    $end = microtime(true) + relay_wait($in);
    for (;;) {
        $st = $db->prepare("SELECT state, result_json, deadline_utc <= UTC_TIMESTAMP(3) AS overdue,
                                   created_utc <= UTC_TIMESTAMP(3) - INTERVAL " . RELAY_PICKUP_SEC . " SECOND AS stale
                            FROM relay_calls WHERE call_id = ?");
        $st->execute([$callId]);
        $r = $st->get_result()->fetch_assoc();
        $st->close();
        if ($r === null) {
            return ['state' => 'missing'];
        }
        if ($r['state'] === 'done') {
            $res = json_decode((string)$r['result_json'], true);
            return ['state' => 'done', 'result' => is_array($res) ? $res : null];
        }
        if ($r['state'] === 'queued' && (int)$r['stale']) {
            relay_cancel($db, ['callId' => $callId]);
            return ['state' => 'unclaimed'];
        }
        if ($r['state'] === 'expired' || (int)$r['overdue']) {
            return ['state' => 'expired'];
        }
        if ($end - microtime(true) < 0.3 || connection_aborted()) {
            return ['state' => (string)$r['state']];
        }
        usleep(300000);
    }
}

// Bridge: claim queued calls of this host and learn which site jobs ended while their calls run (cancelJobs); waits up
// to waitSec for either
function relay_claim(mysqli $db, array $in): array
{
    $host = relay_host($in);
    $max = $in['max'] ?? 8;
    $max = is_int($max) ? max(0, min(16, $max)) : 8;   // 0: all slots busy, only cancellations
    // Cancellations stay pending until the Bridge acknowledges them in a later poll (a lost answer repeats them)
    $ack = relay_job_ids($in['ack'] ?? null);
    if ($ack) {
        $st = $db->prepare("UPDATE relay_calls SET cancel_req = 2 WHERE host_id = ? AND job_id = ? AND cancel_req = 1");
        foreach ($ack as $j) {
            $st->execute([$host, $j]);
        }
        $st->close();
    }
    $end = microtime(true) + relay_wait($in);
    for (;;) {
        $calls = [];
        $db->begin_transaction();
        try {
            $st = $max === 0 ? null : $db->prepare("SELECT call_id, device_id, job_id, environment_id, chat_id, tool, args_json, tainted, deny_json,
                                       TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(3), deadline_utc) AS left_sec
                                FROM relay_calls WHERE host_id = ? AND state = 'queued' AND deadline_utc > UTC_TIMESTAMP(3)
                                  AND created_utc > UTC_TIMESTAMP(3) - INTERVAL " . RELAY_PICKUP_SEC . " SECOND
                                ORDER BY created_utc LIMIT $max FOR UPDATE");
            $rows = [];
            if ($st !== null) {
                $st->execute([$host]);
                $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
                $st->close();
            }
            if ($rows) {
                $up = $db->prepare("UPDATE relay_calls SET state = 'claimed', claimed_utc = UTC_TIMESTAMP(3) WHERE call_id = ? AND state = 'queued'");
                foreach ($rows as $r) {
                    $up->execute([$r['call_id']]);
                    if ($up->affected_rows === 1) {
                        $calls[] = [
                            'callId' => (string)$r['call_id'], 'deviceId' => (string)$r['device_id'], 'jobId' => (string)$r['job_id'],
                            'environmentId' => (string)$r['environment_id'], 'chatId' => (string)$r['chat_id'], 'tool' => (string)$r['tool'],
                            'argsJson' => (string)$r['args_json'], 'tainted' => (bool)(int)$r['tainted'], 'leftSec' => (int)$r['left_sec'],
                            'deny' => json_decode((string)$r['deny_json'], true) ?: [],
                        ];
                    }
                }
                $up->close();
            }
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
        $st = $db->prepare("SELECT DISTINCT job_id FROM relay_calls WHERE host_id = ? AND state = 'claimed' AND cancel_req = 1");
        $st->execute([$host]);
        $cancel = array_column($st->get_result()->fetch_all(MYSQLI_ASSOC), 'job_id');
        $st->close();
        if ($calls || $cancel || $end - microtime(true) < 0.3 || connection_aborted()) {
            return ['calls' => $calls, 'cancelJobs' => $cancel];
        }
        usleep(300000);
    }
}

// Bridge: the reply of a claimed call ({ok, content, error, untrusted})
function relay_result(mysqli $db, array $in): array
{
    $host = relay_host($in);
    $callId = relay_call_id($in['callId'] ?? null);
    $result = $in['result'] ?? null;
    if (!is_array($result) || array_is_list($result)) {
        throw new InvalidArgumentException('invalid result');
    }
    $json = co_db_json($result);
    if (strlen($json) > RELAY_MAX_RESULT) {
        $json = co_db_json(['ok' => false, 'content' => '', 'untrusted' => false,
            'error' => ['code' => 'result_too_large', 'message' => 'The device answer is larger than ' . RELAY_MAX_RESULT . ' bytes; ask for less (a smaller range, a query).']]);
    }
    $st = $db->prepare("UPDATE relay_calls SET state = 'done', result_json = ?, done_utc = UTC_TIMESTAMP(3)
                        WHERE call_id = ? AND host_id = ? AND state = 'claimed'");
    $st->execute([$json, $callId, $host]);
    $n = $st->affected_rows;
    $st->close();
    return ['ok' => $n === 1];
}

// ---------- MCP over HTTP (schema 24, relay-mcp.php) ----------
// The site agent asks for a one-time key of the job (relay_token) and writes it into the job's mcp.json; relay-mcp.php
// takes the job, environment, chat, deny list and taint only from the key's row, never from the request.

const RELAY_TOKEN_MAX_SEC = 15300;   // the longest site job (240 min) plus the agent's margin

function relay_token_hash(string $token): string
{
    return hash('sha256', $token);
}

// Site agent: a key for the MCP servers dev_<short> of one job; none for a restricted job (R2, as relay_devices)
function relay_token(mysqli $db, array $in): array
{
    $env = relay_env($in);
    $job = relay_str($in, 'jobId', '/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', 'jobId');
    $chat = isset($in['chatId']) && $in['chatId'] !== '' ? relay_str($in, 'chatId', '/^[A-Za-z0-9_-]{1,80}$/', 'chatId') : '';
    $deny = relay_deny($in['deny'] ?? null);
    $ttl = $in['ttlSec'] ?? 7200;
    $ttl = is_int($ttl) ? max(60, min(RELAY_TOKEN_MAX_SEC, $ttl)) : 7200;
    if (relay_job_restricted($db, $env, $job, $chat)) {
        return ['ok' => false, 'restricted' => true, 'error' => RELAY_RESTRICTED];
    }
    $token = 'rt-' . bin2hex(random_bytes(24));
    $st = $db->prepare('INSERT INTO relay_tokens (token_hash, job_id, environment_id, chat_id, deny_json, tainted, created_utc, expires_utc)
                        VALUES (?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL ? SECOND)');
    $st->execute([relay_token_hash($token), $job, $env, $chat, co_db_json($deny), !empty($in['tainted']) ? 1 : 0, $ttl]);
    $st->close();
    return ['ok' => true, 'token' => $token, 'ttlSec' => $ttl];
}

// relay-mcp.php: the job behind a key, null when unknown or expired
function relay_token_job(mysqli $db, string $token): ?array
{
    if (preg_match('/^rt-[0-9a-f]{48}$/', $token) !== 1) {
        return null;
    }
    $hash = relay_token_hash($token);
    $st = $db->prepare('SELECT job_id, environment_id, chat_id, deny_json, tainted FROM relay_tokens
                        WHERE token_hash = ? AND expires_utc > UTC_TIMESTAMP(3)');
    $st->execute([$hash]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if ($r === null) {
        return null;
    }
    $deny = json_decode((string)$r['deny_json'], true);
    return ['hash' => $hash, 'jobId' => (string)$r['job_id'], 'env' => (string)$r['environment_id'], 'chatId' => (string)$r['chat_id'],
        'deny' => is_array($deny) ? $deny : [], 'tainted' => (bool)(int)$r['tainted']];
}

// relay-mcp.php: a device returned untrusted content - every later call of the job is tainted (as relay-mcp.mjs did)
function relay_token_taint(mysqli $db, string $hash): void
{
    $st = $db->prepare('UPDATE relay_tokens SET tainted = 1 WHERE token_hash = ?');
    $st->execute([$hash]);
    $st->close();
}

// relay-mcp.php: the device dev_<short> of an environment, its last publication even when the Bridge is silent now
// (a call to it is then refused by relay_call_put as home_offline)
function relay_device_by_short(mysqli $db, string $env, string $short): ?array
{
    $st = $db->prepare('SELECT device_id, short_name, name, kind, online, tools_json FROM relay_devices
                        WHERE environment_id = ? AND short_name = ? ORDER BY updated_utc DESC LIMIT 1');
    $st->execute([$env, $short]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if ($r === null) {
        return null;
    }
    // objects, not arrays: an empty "properties": {} of a schema must stay an object
    $tools = json_decode((string)$r['tools_json']);
    return ['deviceId' => (string)$r['device_id'], 'short' => (string)$r['short_name'], 'name' => (string)$r['name'],
        'kind' => (string)$r['kind'], 'online' => (bool)(int)$r['online'], 'tools' => is_array($tools) ? $tools : []];
}

// relay-mcp.php: the job a call belongs to (a resumed stream may only follow a call of its own job)
function relay_call_job(mysqli $db, string $callId): ?string
{
    $st = $db->prepare('SELECT job_id FROM relay_calls WHERE call_id = ?');
    $st->execute([$callId]);
    $r = $st->get_result()->fetch_row();
    $st->close();
    return $r === null ? null : (string)$r[0];
}

// Site agent: withdraw a call that was not claimed yet, or every call of a job when it ends: queued ones never run,
// claimed ones are cancelled on the Bridge (relay_claim cancelJobs: device tool.cancel, tool process tree killed).
// Schema 24: the job's end also deletes its MCP key.
function relay_cancel(mysqli $db, array $in): array
{
    if (isset($in['callId'])) {
        $st = $db->prepare("UPDATE relay_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3) WHERE call_id = ? AND state = 'queued'");
        $st->execute([relay_call_id($in['callId'])]);
    } else {
        $job = relay_str($in, 'jobId', '/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', 'jobId');
        // One transaction, queued first: an UPDATE waits for a relay_claim holding those rows and then sees them
        // claimed, so the second statement flags them; no call slips between the two.
        $db->begin_transaction();
        try {
            $st = $db->prepare("UPDATE relay_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3) WHERE job_id = ? AND state = 'queued'");
            $st->execute([$job]);
            $n = $st->affected_rows;
            $st->close();
            $st = $db->prepare("UPDATE relay_calls SET cancel_req = 1 WHERE job_id = ? AND state = 'claimed' AND cancel_req = 0");
            $st->execute([$job]);
            $running = $st->affected_rows;
            $st->close();
            $st = $db->prepare('DELETE FROM relay_tokens WHERE job_id = ?');
            $st->execute([$job]);
            $st->close();
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
        return ['ok' => true, 'cancelled' => $n, 'running' => $running];
    }
    $n = $st->affected_rows;
    $st->close();
    return ['ok' => true, 'cancelled' => $n];
}

// watch-cron: expire overdue calls, delete finished ones after 10 min and devices of hosts silent for a day
function relay_cleanup(mysqli $db): array
{
    $db->query("UPDATE relay_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3)
                WHERE (state IN ('queued','claimed') AND deadline_utc <= UTC_TIMESTAMP(3))
                   OR (state = 'queued' AND created_utc <= UTC_TIMESTAMP(3) - INTERVAL " . RELAY_PICKUP_SEC . " SECOND)");
    $expired = $db->affected_rows;
    $db->query("DELETE FROM relay_calls WHERE state IN ('done','expired') AND done_utc < UTC_TIMESTAMP(3) - INTERVAL 10 MINUTE");
    $deleted = $db->affected_rows;
    $db->query('DELETE FROM relay_devices WHERE updated_utc < UTC_TIMESTAMP(3) - INTERVAL 1 DAY');
    try {
        $db->query('DELETE FROM sandbox_resources WHERE updated_utc < UTC_TIMESTAMP(3) - INTERVAL 1 DAY');
    } catch (mysqli_sql_exception) {
        /* schema < 26 */
    }
    $tokens = 0;
    try {
        $db->query('DELETE FROM relay_tokens WHERE expires_utc <= UTC_TIMESTAMP(3)');
        $tokens = $db->affected_rows;
    } catch (mysqli_sql_exception) {
        /* schema < 24 */
    }
    return ['expired' => $expired, 'deleted' => $deleted, 'tokens' => $tokens];
}

// S2 (CO Web 0.98.0): diagnosis behind the header traffic light — the job queue per executor (all environments, counts
// only) and whether the home Bridge serves device calls of the environment (relay_devices).
function co_host_light(mysqli $db, string $env): array
{
    $hosts = [];
    $res = $db->query(
        "SELECT host_id, SUM(state = 'inbox') AS queued, SUM(state = 'claimed') AS running
           FROM jobs
          WHERE state IN ('inbox', 'claimed') AND result IS NULL AND cancel_requested = 0
            AND COALESCE(queued_utc, claimed_utc, synced_utc) > UTC_TIMESTAMP() - INTERVAL 1 DAY
          GROUP BY host_id");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $hosts[(string)$r['host_id']] = ['queued' => (int)$r['queued'], 'running' => (int)$r['running'], 'lastDoneUtc' => null];
    }
    $res = $db->query(
        "SELECT host_id, MAX(completed_utc) AS done FROM jobs
          WHERE state = 'completed' AND completed_utc > UTC_TIMESTAMP() - INTERVAL 7 DAY GROUP BY host_id");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $h = (string)$r['host_id'];
        $hosts[$h] = ($hosts[$h] ?? ['queued' => 0, 'running' => 0, 'lastDoneUtc' => null]);
        $hosts[$h]['lastDoneUtc'] = co_db_iso((string)$r['done']);
    }
    $relay = ['seenUtc' => null, 'devices' => 0];
    try {
        $st = $db->prepare('SELECT MAX(updated_utc) AS seen,
                                   SUM(online = 1 AND updated_utc > UTC_TIMESTAMP(3) - INTERVAL ' . RELAY_FRESH_SEC . ' SECOND) AS n
                              FROM relay_devices WHERE environment_id = ?');
        $st->execute([$env]);
        $r = $st->get_result()->fetch_assoc();
        $st->close();
        $relay = ['seenUtc' => $r && $r['seen'] !== null ? co_db_iso((string)$r['seen']) : null, 'devices' => (int)($r['n'] ?? 0)];
    } catch (mysqli_sql_exception) {
        /* schema < 22: no relay */
    }
    return ['hosts' => (object)$hosts, 'relay' => $relay];
}
