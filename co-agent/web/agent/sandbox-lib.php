<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// sandbox-lib.php (schema 26, subtask B of chat CHAT-20261112071412-CJZ0, owner 12.11 #19-#23): sandbox providers.
// A sandbox is a machine part where the pc_* tools of an environment that does not own the computer run (now: the
// children on the home PC, WSL distro aiwb-family under their own Linux user). Providers publish their sandboxes here
// like model resources (sandbox_put, host key, every 30 s): who owns it, its kind (wsl | mac-vm | microvm ...), a card
// (OS, tools, CPU/RAM, limits), who may use it and the quota of each, and the seat of each recipient = the relay device
// its jobs call (relay_devices). For an environment the server picks one sandbox: its own (owner_env = env) first, then
// one of a family member that allows it, else none (sandbox_pick); relay_devices shows only the seat of the picked one
// and relay_call_put refuses the others. The owner of the PC works on the whole PC (device h-<host>), not in a sandbox.
// Future providers (a Mac resident's Linux VM, Freedom peer microVMs after mutual pairing) put rows of their own kind
// and seats on their own relay devices; pc_* and the pick do not change.
declare(strict_types=1);

const SANDBOX_FRESH_SEC = 90;
const SANDBOX_KINDS = ['wsl', 'mac-vm', 'microvm', 'container', 'vm'];
const SANDBOX_MAX_PER_HOST = 8;

function sandbox_json_obj(mixed $v, int $max, string $what): string
{
    if ($v === null) {
        $v = [];
    }
    if (!is_array($v) || ($v !== [] && array_is_list($v))) {
        throw new InvalidArgumentException("invalid $what");
    }
    $j = (string)json_encode($v === [] ? new stdClass() : $v, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    if (strlen($j) > $max) {
        throw new InvalidArgumentException("$what too large");
    }
    return $j;
}

function sandbox_env_list(mixed $v): array
{
    if ($v === null) {
        return [];
    }
    if (!is_array($v) || !array_is_list($v) || count($v) > 32) {
        throw new InvalidArgumentException('invalid allowed');
    }
    foreach ($v as $e) {
        if (!is_string($e) || preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/', $e) !== 1) {
            throw new InvalidArgumentException('invalid allowed');
        }
    }
    return array_values(array_unique($v));
}

// Provider (a Bridge): replace the set of sandboxes it publishes
function sandbox_put(mysqli $db, array $in): array
{
    $host = relay_host($in);
    $list = $in['sandboxes'] ?? null;
    if (!is_array($list) || !array_is_list($list) || count($list) > SANDBOX_MAX_PER_HOST) {
        throw new InvalidArgumentException('invalid sandboxes');
    }
    $rows = [];
    foreach ($list as $s) {
        if (!is_array($s)) {
            throw new InvalidArgumentException('invalid sandbox');
        }
        $id = relay_str($s, 'id', '/^[A-Za-z0-9][A-Za-z0-9._\/-]{0,79}$/', 'id');
        if (!str_starts_with($id, $host . '/')) {
            throw new InvalidArgumentException('sandbox id must start with the host');
        }
        $device = isset($s['deviceId']) && $s['deviceId'] !== '' ? relay_device_id($s['deviceId']) : '';
        $owner = relay_env($s, 'ownerEnv');
        $kind = $s['kind'] ?? null;
        if (!in_array($kind, SANDBOX_KINDS, true)) {
            throw new InvalidArgumentException('invalid kind');
        }
        $allowed = sandbox_env_list($s['allowed'] ?? []);
        $seats = $s['seats'] ?? [];
        if (!is_array($seats) || ($seats !== [] && array_is_list($seats))) {
            throw new InvalidArgumentException('invalid seats');
        }
        foreach ($seats as $env => $dev) {
            if (!is_string($env) || !in_array($env, $allowed, true) && $env !== $owner) {
                throw new InvalidArgumentException('seat of an environment that is not allowed');
            }
            // a seat is a device of this host published for that environment (review B-06)
            if (!is_string($dev) || !str_starts_with(relay_device_id($dev), 'h-' . $host . '-')) {
                throw new InvalidArgumentException('seat must be a device of this host');
            }
        }
        $rows[$id] = [$id, $host, $device, $owner, $kind, sandbox_json_obj($s['card'] ?? [], 16384, 'card'),
            (string)json_encode($allowed), sandbox_json_obj($s['quotas'] ?? [], 16384, 'quotas'), sandbox_json_obj($seats, 4096, 'seats'),
            !empty($s['online']) ? 1 : 0];
    }
    $db->begin_transaction();
    try {
        $st = $db->prepare('DELETE FROM sandbox_resources WHERE host_id = ?');
        $st->execute([$host]);
        $st->close();
        if ($rows) {
            $st = $db->prepare('INSERT INTO sandbox_resources (id, host_id, device_id, owner_env, kind, card_json, allowed_json, quotas_json, seats_json, online, updated_utc)
                                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3))');
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
    return ['ok' => true, 'sandboxes' => count($rows)];
}

function sandbox_row(array $r): array
{
    $dec = static fn(string $j) => is_array($x = json_decode($j, true)) ? $x : [];
    return ['id' => (string)$r['id'], 'host' => (string)$r['host_id'], 'deviceId' => (string)$r['device_id'], 'ownerEnv' => (string)$r['owner_env'],
        'kind' => (string)$r['kind'], 'card' => $dec((string)$r['card_json']), 'allowed' => $dec((string)$r['allowed_json']),
        'quotas' => $dec((string)$r['quotas_json']), 'seats' => $dec((string)$r['seats_json']),
        'online' => (bool)(int)$r['online'] && (bool)(int)$r['fresh'], 'updatedUtc' => co_db_iso((string)$r['updated_utc'])];
}

// Sandboxes an environment may use (own or allowed), or all of them without env; fresh ones are online
function sandbox_list(mysqli $db, array $in): array
{
    $sql = 'SELECT *, updated_utc > UTC_TIMESTAMP(3) - INTERVAL ' . SANDBOX_FRESH_SEC . ' SECOND AS fresh FROM sandbox_resources ORDER BY id';
    $rows = $db->query($sql)->fetch_all(MYSQLI_ASSOC);
    $env = isset($in['env']) ? relay_env($in) : null;
    $out = [];
    foreach ($rows as $r) {
        $s = sandbox_row($r);
        if ($env === null || $s['ownerEnv'] === $env || in_array($env, $s['allowed'], true)) {
            $out[] = $s;
        }
    }
    $pick = $env === null ? null : sandbox_pick($db, $env);
    return ['sandboxes' => $out, 'pick' => $pick['id'] ?? null];
}

// The sandbox an environment's pc_* run in: its own first, then one of a family member that allows it (and seats it),
// online and fresh; the most recently heard one of a rank; null = no sandbox.
function sandbox_pick(mysqli $db, string $env): ?array
{
    try {
        $rows = $db->query('SELECT *, updated_utc > UTC_TIMESTAMP(3) - INTERVAL ' . SANDBOX_FRESH_SEC . ' SECOND AS fresh
                            FROM sandbox_resources WHERE online = 1 ORDER BY updated_utc DESC')->fetch_all(MYSQLI_ASSOC);
    } catch (mysqli_sql_exception) {
        return null;   // schema < 26
    }
    $own = null;
    $family = null;
    foreach ($rows as $r) {
        $s = sandbox_row($r);
        if (!$s['online'] || !is_string($s['seats'][$env] ?? null)) {
            continue;
        }
        if ($s['ownerEnv'] === $env) {
            $own ??= $s;
        } elseif (in_array($env, $s['allowed'], true)) {
            $family ??= $s;
        }
    }
    return $own ?? $family;
}

// Relay devices of an environment that are sandbox seats, and the one seat of the picked sandbox
function sandbox_seats(mysqli $db, string $env): array
{
    try {
        $rows = $db->query('SELECT seats_json FROM sandbox_resources')->fetch_all(MYSQLI_ASSOC);
    } catch (mysqli_sql_exception) {
        return [[], null];
    }
    $seats = [];
    foreach ($rows as $r) {
        $s = json_decode((string)$r['seats_json'], true);
        if (is_array($s) && is_string($s[$env] ?? null)) {
            $seats[$s[$env]] = true;
        }
    }
    $pick = sandbox_pick($db, $env);
    return [array_keys($seats), $pick === null ? null : (string)$pick['seats'][$env]];
}
