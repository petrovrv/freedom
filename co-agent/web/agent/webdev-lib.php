<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// webdev-lib.php: web device (schema 11, lab/web-device/SPEC.md) — a CO Web tab acting as a file device for agent
// runs. Browser ops (session) register/heartbeat/claim/result/revoke/mine; Bridge ops (push key) list/call_put/call_get.
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';

const WEBDEV_ONLINE_SEC = 60;
const WEBDEV_MAX_ARGS = 1048576;
const WEBDEV_MAX_RESULT = 2097152;
const WEBDEV_MAX_TOOLS = 1048576;

// Select list incl. computed online flag
const WEBDEV_COLS = 'device_id, user_email, environment_id, short_name, name, folders_json, tools_json, last_seen_utc, revoked, '
    . 'TIMESTAMPDIFF(MICROSECOND, last_seen_utc, UTC_TIMESTAMP(3)) < ' . (WEBDEV_ONLINE_SEC * 1000000) . ' AS online';

// Validate device id from input
function webdev_device_id(array $in, string $key = 'deviceId'): string
{
    $v = $in[$key] ?? null;
    if (!is_string($v) || preg_match('/^w-[0-9a-f]{16}$/', $v) !== 1) {
        throw new InvalidArgumentException("invalid $key");
    }
    return $v;
}

// Short display name
function webdev_short(string $id): string
{
    return 'web' . substr($id, 2, 6);
}

// Validate and normalize folder list
function webdev_folders(mixed $v): array
{
    if (!is_array($v) || !array_is_list($v) || count($v) > 50) {
        throw new InvalidArgumentException('invalid folders');
    }
    $out = [];
    $seen = [];
    foreach ($v as $f) {
        if (!is_array($f)) {
            throw new InvalidArgumentException('invalid folder');
        }
        $id = $f['id'] ?? null;
        $name = $f['name'] ?? null;
        $access = $f['access'] ?? null;
        if (!is_string($id) || preg_match('/^[a-z0-9-]{1,64}$/', $id) !== 1 || isset($seen[$id])) {
            throw new InvalidArgumentException('invalid folder id');
        }
        if (!is_string($name) || mb_strlen($name) < 1 || mb_strlen($name) > 200) {
            throw new InvalidArgumentException('invalid folder name');
        }
        if ($access !== 'read' && $access !== 'write') {
            throw new InvalidArgumentException('invalid folder access');
        }
        $seen[$id] = true;
        $out[] = ['id' => $id, 'name' => $name, 'access' => $access];
    }
    if (strlen(co_db_json($out)) > 60000) {
        throw new InvalidArgumentException('folders too large');
    }
    return $out;
}

// Validate tool list (MCP tools/list entries)
function webdev_tools(mixed $v): array
{
    if (!is_array($v) || !array_is_list($v) || count($v) > 100) {
        throw new InvalidArgumentException('invalid tools');
    }
    foreach ($v as $t) {
        $name = is_array($t) ? ($t['name'] ?? null) : null;
        if (!is_string($name) || preg_match('/^[a-z0-9_]{1,64}$/', $name) !== 1) {
            throw new InvalidArgumentException('invalid tool name');
        }
    }
    if (strlen(co_db_json($v)) > WEBDEV_MAX_TOOLS) {
        throw new InvalidArgumentException('tools too large');
    }
    return $v;
}

// Row -> API view
function webdev_view(array $row): array
{
    $folders = is_string($row['folders_json'] ?? null) ? json_decode($row['folders_json'], true) : null;
    $tools = is_string($row['tools_json'] ?? null) ? json_decode($row['tools_json'], true) : null;
    return [
        'deviceId'      => (string)$row['device_id'],
        'short'         => (string)$row['short_name'],
        'name'          => (string)$row['name'],
        'userEmail'     => (string)$row['user_email'],
        'environmentId' => (string)$row['environment_id'],
        'folders'       => is_array($folders) ? $folders : [],
        'tools'         => is_array($tools) ? $tools : [],
        'lastSeenUtc'   => co_db_iso($row['last_seen_utc']),
        'online'        => (bool)(int)($row['online'] ?? 0),
    ];
}

// Fetch one device row
function webdev_row(mysqli $db, string $id, bool $forUpdate = false): ?array
{
    $st = $db->prepare('SELECT ' . WEBDEV_COLS . ' FROM web_devices WHERE device_id = ?' . ($forUpdate ? ' FOR UPDATE' : ''));
    $st->execute([$id]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    return $row ?: null;
}

// Fetch row owned by user, else 403
function webdev_owned(mysqli $db, string $id, string $email, bool $forUpdate = false): array
{
    $row = webdev_row($db, $id, $forUpdate);
    if ($row === null || $row['user_email'] !== $email || (int)$row['revoked'] !== 0) {
        co_json(403, ['error' => 'not_permitted']);
    }
    return $row;
}

// Register or refresh a device
function webdev_register(mysqli $db, array $in, string $email, string $env): array
{
    $id = webdev_device_id($in);
    $name = is_string($in['name'] ?? null) ? trim($in['name']) : '';
    $len = mb_strlen($name);
    if ($len < 1 || $len > 120) {
        throw new InvalidArgumentException('invalid name');
    }
    $folders = co_db_json(webdev_folders($in['folders'] ?? null));
    $tools = co_db_json(webdev_tools($in['tools'] ?? null));

    for ($try = 0; ; $try++) {
        $db->begin_transaction();
        try {
            $row = webdev_row($db, $id, true);
            if ($row !== null) {
                // Existing: must be own and active
                if ($row['user_email'] !== $email || (int)$row['revoked'] !== 0) {
                    $db->rollback();
                    co_json(403, ['error' => 'not_permitted']);
                }
                $st = $db->prepare(
                    'UPDATE web_devices SET name = ?, environment_id = ?, folders_json = ?, tools_json = ?, '
                    . 'last_seen_utc = UTC_TIMESTAMP(3) WHERE device_id = ?'
                );
                $st->execute([$name, $env, $folders, $tools, $id]);
                $st->close();
            } else {
                $st = $db->prepare(
                    'INSERT INTO web_devices (device_id, user_email, environment_id, short_name, name, folders_json, tools_json, '
                    . 'created_utc, last_seen_utc, revoked) VALUES (?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), 0)'
                );
                $st->execute([$id, $email, $env, webdev_short($id), $name, $folders, $tools]);
                $st->close();
            }
            $db->commit();
            break;
        } catch (mysqli_sql_exception $e) {
            $db->rollback();
            // Concurrent insert: retry once
            if ($e->getCode() === 1062 && $try === 0) {
                continue;
            }
            throw $e;
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
    }

    $row = webdev_row($db, $id);
    if ($row === null) {
        throw new RuntimeException('device vanished');
    }
    return ['device' => webdev_view($row)];
}

// Keepalive with folder refresh
function webdev_heartbeat(mysqli $db, array $in, string $email): array
{
    $id = webdev_device_id($in);
    $folders = array_key_exists('folders', $in) ? co_db_json(webdev_folders($in['folders'])) : null;
    webdev_owned($db, $id, $email);
    if ($folders !== null) {
        $st = $db->prepare(
            'UPDATE web_devices SET folders_json = ?, last_seen_utc = UTC_TIMESTAMP(3) '
            . 'WHERE device_id = ? AND user_email = ? AND revoked = 0'
        );
        $st->execute([$folders, $id, $email]);
    } else {
        $st = $db->prepare(
            'UPDATE web_devices SET last_seen_utc = UTC_TIMESTAMP(3) WHERE device_id = ? AND user_email = ? AND revoked = 0'
        );
        $st->execute([$id, $email]);
    }
    $st->close();
    return ['ok' => true];
}

// Current user's active devices
function webdev_mine(mysqli $db, string $email): array
{
    $st = $db->prepare(
        'SELECT ' . WEBDEV_COLS . ' FROM web_devices WHERE user_email = ? AND revoked = 0 ORDER BY last_seen_utc DESC'
    );
    $st->execute([$email]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    return ['devices' => array_map('webdev_view', $rows)];
}

// Active devices, optionally filtered by environment (Bridge)
function webdev_list(mysqli $db, array $in): array
{
    $envs = null;
    if (array_key_exists('environmentIds', $in) && $in['environmentIds'] !== null) {
        $v = $in['environmentIds'];
        if (!is_array($v) || !array_is_list($v) || count($v) > 50) {
            throw new InvalidArgumentException('invalid environmentIds');
        }
        foreach ($v as $e) {
            if (!is_string($e) || !co_db_id_ok($e)) {
                throw new InvalidArgumentException('invalid environmentIds');
            }
        }
        if ($v === []) {
            return ['devices' => []];
        }
        $envs = array_values(array_unique($v));
    }

    $sql = 'SELECT ' . WEBDEV_COLS . ' FROM web_devices WHERE revoked = 0';
    $params = null;
    if ($envs !== null) {
        $sql .= ' AND environment_id IN (' . implode(',', array_fill(0, count($envs), '?')) . ')';
        $params = $envs;
    }
    $sql .= ' ORDER BY last_seen_utc DESC';

    $st = $db->prepare($sql);
    $st->execute($params);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    return ['devices' => array_map('webdev_view', $rows)];
}

// Validate callId
function webdev_call_id(mixed $v): string
{
    if (!is_string($v) || !co_db_id_ok($v) || strlen($v) > 64) {
        throw new InvalidArgumentException('invalid callId');
    }
    return $v;
}

// Call deadline as unix seconds: issuedUtc + timeoutMs, fallback created_utc
function webdev_deadline(string $argsJson, ?string $createdUtc): ?float
{
    $env = json_decode($argsJson, true);
    $timeoutMs = 60000;
    $candidates = [];
    if (is_array($env)) {
        $t = $env['timeoutMs'] ?? null;
        if (is_int($t) && $t >= 1 && $t <= 3600000) {
            $timeoutMs = $t;
        }
        $issued = co_db_dt($env['issuedUtc'] ?? null);
        if ($issued !== null) {
            $candidates[] = $issued;
        }
    }
    if ($createdUtc !== null) {
        $candidates[] = $createdUtc;
    }
    $utc = new DateTimeZone('UTC');
    foreach ($candidates as $s) {
        $dt = DateTimeImmutable::createFromFormat('Y-m-d H:i:s.v', $s, $utc)
            ?: DateTimeImmutable::createFromFormat('Y-m-d H:i:s', $s, $utc);
        if ($dt !== false) {
            return (float)$dt->format('U.u') + $timeoutMs / 1000;
        }
    }
    return null;
}

// Long-poll: claim queued calls for an owned device
function webdev_claim(mysqli $db, array $in, string $email): array
{
    $id = webdev_device_id($in);
    $w = $in['waitMs'] ?? 20000;
    if (is_string($w)) {
        $w = filter_var(trim($w), FILTER_VALIDATE_INT);
    }
    if (!is_int($w)) {
        throw new InvalidArgumentException('invalid waitMs');
    }
    $waitMs = max(0, min(20000, $w));

    if (session_status() === PHP_SESSION_ACTIVE) {
        session_write_close();
    }
    set_time_limit(intdiv($waitMs, 1000) + 30);

    $end = microtime(true) + $waitMs / 1000;
    $lastSeen = null;

    while (true) {
        webdev_owned($db, $id, $email);

        // last_seen writes at most once per 5 s
        $now = microtime(true);
        if ($lastSeen === null || $now - $lastSeen >= 5.0) {
            $st = $db->prepare('UPDATE web_devices SET last_seen_utc = UTC_TIMESTAMP(3) WHERE device_id = ?');
            $st->execute([$id]);
            $st->close();
            $lastSeen = $now;
        }

        $calls = [];
        $db->begin_transaction();
        try {
            $st = $db->prepare(
                "SELECT call_id, args_json, created_utc FROM web_device_calls
                 WHERE device_id = ? AND state = 'queued'
                 ORDER BY created_utc LIMIT 10 FOR UPDATE"
            );
            $st->execute([$id]);
            $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
            $st->close();

            if ($rows) {
                $exp = $db->prepare(
                    "UPDATE web_device_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3)
                     WHERE call_id = ? AND state = 'queued'"
                );
                $clm = $db->prepare(
                    "UPDATE web_device_calls SET state = 'claimed', claimed_utc = UTC_TIMESTAMP(3)
                     WHERE call_id = ? AND state = 'queued'"
                );
                try {
                    foreach ($rows as $r) {
                        $args = (string)$r['args_json'];
                        $deadline = webdev_deadline($args, $r['created_utc'] !== null ? (string)$r['created_utc'] : null);
                        $env = json_decode($args, true);
                        // overdue or undecodable -> expire
                        if (($deadline !== null && $deadline <= microtime(true)) || !is_array($env)) {
                            $exp->execute([$r['call_id']]);
                            continue;
                        }
                        $clm->execute([$r['call_id']]);
                        if ($clm->affected_rows === 1) {
                            $calls[] = $env;
                        }
                    }
                } finally {
                    $exp->close();
                    $clm->close();
                }
            }
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }

        if ($calls) {
            return ['calls' => $calls];
        }
        if ($end - microtime(true) < 0.25 || connection_aborted()) {
            return ['calls' => []];
        }
        usleep(250000);
    }
}

// Store the result of a claimed call
function webdev_result(mysqli $db, array $in, string $email): array
{
    $callId = webdev_call_id($in['callId'] ?? null);
    $result = $in['result'] ?? null;
    if (!is_array($result) || array_is_list($result)) {
        throw new InvalidArgumentException('invalid result');
    }
    $json = co_db_json($result);
    if (strlen($json) > WEBDEV_MAX_RESULT) {
        throw new InvalidArgumentException('result too large');
    }

    $db->begin_transaction();
    try {
        $st = $db->prepare('SELECT state, device_id FROM web_device_calls WHERE call_id = ? FOR UPDATE');
        $st->execute([$callId]);
        $call = $st->get_result()->fetch_assoc();
        $st->close();

        if ($call === null) {
            $db->rollback();
            co_json(403, ['error' => 'not_permitted']);
        }

        // owner check
        $dev = webdev_row($db, (string)$call['device_id']);
        if ($dev === null || $dev['user_email'] !== $email || (int)$dev['revoked'] !== 0) {
            $db->rollback();
            co_json(403, ['error' => 'not_permitted']);
        }

        if ($call['state'] === 'expired') {
            $db->commit();
            return ['ok' => false, 'error' => 'expired'];
        }
        if ($call['state'] !== 'claimed') {
            $db->commit();
            return ['ok' => false, 'error' => 'not_claimed'];
        }

        $st = $db->prepare(
            "UPDATE web_device_calls SET state = 'done', result_json = ?, done_utc = UTC_TIMESTAMP(3)
             WHERE call_id = ?"
        );
        $st->execute([$json, $callId]);
        $st->close();
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return ['ok' => true];
}

// Revoke a device and expire its pending calls
function webdev_revoke(mysqli $db, array $in, string $email): array
{
    $id = webdev_device_id($in);

    $db->begin_transaction();
    try {
        $dev = webdev_row($db, $id, true);
        if ($dev === null || $dev['user_email'] !== $email || (int)$dev['revoked'] !== 0) {
            $db->rollback();
            co_json(403, ['error' => 'not_permitted']);
        }

        $st = $db->prepare('UPDATE web_devices SET revoked = 1 WHERE device_id = ?');
        $st->execute([$id]);
        $st->close();

        $st = $db->prepare(
            "UPDATE web_device_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3)
             WHERE device_id = ? AND state IN ('queued','claimed')"
        );
        $st->execute([$id]);
        $expired = $st->affected_rows;
        $st->close();

        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return ['ok' => true, 'expired' => $expired];
}

// Bridge: enqueue a tool.call envelope (idempotent on callId)
function webdev_call_put(mysqli $db, array $in): array
{
    $call = $in['call'] ?? null;
    if (!is_array($call) || array_is_list($call)) {
        throw new InvalidArgumentException('invalid call');
    }
    $callId = webdev_call_id($call['callId'] ?? null);
    $runId = $call['runId'] ?? null;
    if (!is_string($runId) || !co_db_id_ok($runId) || strlen($runId) > 64) {
        throw new InvalidArgumentException('invalid runId');
    }
    $deviceId = webdev_device_id($call);
    $tool = $call['tool'] ?? null;
    if (!is_string($tool) || !preg_match('/^[a-z0-9_]{1,32}$/', $tool)) {
        throw new InvalidArgumentException('invalid tool');
    }
    $args = co_db_json($call);
    if (strlen($args) > WEBDEV_MAX_ARGS) {
        throw new InvalidArgumentException('call too large');
    }

    $dev = webdev_row($db, $deviceId);
    if ($dev === null || (int)$dev['revoked'] !== 0) {
        co_json(404, ['error' => 'device_not_found']);
    }

    $st = $db->prepare(
        "INSERT INTO web_device_calls (call_id, device_id, job_id, tool, args_json, state, created_utc)
         VALUES (?, ?, ?, ?, ?, 'queued', UTC_TIMESTAMP(3))"
    );
    try {
        $st->execute([$callId, $deviceId, $runId, $tool, $args]);
    } catch (mysqli_sql_exception $e) {
        if ($e->getCode() === 1062) {
            return ['ok' => true, 'duplicate' => true];
        }
        throw $e;
    } finally {
        $st->close();
    }
    return ['ok' => true];
}

// Bridge: finished (done/expired) calls by id
function webdev_call_get(mysqli $db, array $in): array
{
    $raw = $in['callIds'] ?? null;
    if (!is_array($raw) || !array_is_list($raw) || count($raw) < 1 || count($raw) > 50) {
        throw new InvalidArgumentException('invalid callIds');
    }
    $ids = array_values(array_unique(array_map('webdev_call_id', $raw)));
    $ph = implode(',', array_fill(0, count($ids), '?'));

    $st = $db->prepare(
        "SELECT call_id, state, result_json FROM web_device_calls
         WHERE call_id IN ($ph) AND state IN ('done','expired')"
    );
    $st->execute($ids);
    $res = $st->get_result();
    $out = [];
    while ($r = $res->fetch_assoc()) {
        $result = $r['result_json'] !== null ? json_decode((string)$r['result_json'], true) : null;
        $out[] = [
            'callId' => $r['call_id'],
            'state'  => $r['state'],
            'result' => is_array($result) ? $result : null,
        ];
    }
    $st->close();
    return ['results' => $out];
}

// job_submit: every web device ("w-...") named by the job must belong to the submitter
function webdev_check_job_devices(mysqli $db, mixed $devices, string $email): void
{
    if (!is_array($devices)) {
        return;
    }
    $set = [];
    foreach ($devices as $d) {
        if (is_string($d) && str_starts_with($d, 'w-')) {
            $set[$d] = true;
        }
    }
    if (!$set) {
        return;
    }
    $ids = array_map('strval', array_keys($set));
    foreach ($ids as $d) {
        if (!preg_match('/^w-[0-9a-f]{16}$/', $d)) {
            co_json(403, ['error' => 'device_not_owned']);
        }
    }

    $ph = implode(',', array_fill(0, count($ids), '?'));
    $st = $db->prepare(
        "SELECT device_id FROM web_devices
         WHERE device_id IN ($ph) AND user_email = ? AND revoked = 0"
    );
    $st->execute([...$ids, $email]);
    $found = $st->get_result()->num_rows;
    $st->close();

    if ($found !== count($ids)) {
        co_json(403, ['error' => 'device_not_owned']);
    }
}

// Cron: expire overdue calls, purge rows older than 10 min
function webdev_cleanup(mysqli $db): array
{
    $rows = $db->query(
        "SELECT call_id, args_json, created_utc FROM web_device_calls
         WHERE state IN ('queued','claimed') ORDER BY created_utc LIMIT 1000"
    )->fetch_all(MYSQLI_ASSOC);

    $expired = 0;
    if ($rows) {
        $st = $db->prepare(
            "UPDATE web_device_calls SET state = 'expired', done_utc = UTC_TIMESTAMP(3)
             WHERE call_id = ? AND state IN ('queued','claimed')"
        );
        try {
            foreach ($rows as $r) {
                $deadline = webdev_deadline(
                    (string)$r['args_json'],
                    $r['created_utc'] !== null ? (string)$r['created_utc'] : null
                );
                if ($deadline !== null && $deadline <= microtime(true)) {
                    $st->execute([$r['call_id']]);
                    $expired += max(0, $st->affected_rows);
                }
            }
        } finally {
            $st->close();
        }
    }

    // Open calls stay until their deadline (timeoutMs may exceed 10 min); 2 h hard cap (timeoutMs <= 1 h)
    $db->query(
        "DELETE FROM web_device_calls
          WHERE (created_utc < UTC_TIMESTAMP(3) - INTERVAL 10 MINUTE AND state IN ('done','expired'))
             OR created_utc < UTC_TIMESTAMP(3) - INTERVAL 2 HOUR"
    );
    $deleted = $db->affected_rows;

    return ['expired' => $expired, 'deleted' => $deleted];
}
