<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// executors-lib.php (schema 28, Freedom F12, SPEC §3.1a): any number of executors — Bridges on Windows PCs, Macs and Linux
// servers, and the site agent. Each Bridge registers itself and beats every 30 s (executor_put, host key): one row of
// executors plus a copy of its owner.json in kv host:<id>/owner, so everything that reads owner.json from the database
// (models, autoswitch, handover) sees it without Google Drive. 'home-main' and 'site' are the ids of the first two
// executors and are known even without a row (an older Bridge and the site agent live by their owner.json on Drive).
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';
require_once __DIR__ . '/members-lib.php';

const EXEC_HOME = 'home-main';
const EXEC_SITE = 'site';
const EXEC_COMPAT = [EXEC_HOME => 'bridge', EXEC_SITE => 'site'];
// 32: the Bridge device "h-<host>" and a sandbox seat "h-<host>-<env>" stay within the 40 characters of a device id
const EXEC_ID_RE = '/^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/';
const EXEC_FRESH_SEC = 90;      // a registered executor silent for this long is not alive (it beats every 30 s)
const EXEC_TAKEN_SEC = 600;     // another machine may take over an id after the first one was silent this long
const EXEC_MAX_OWNER = 262144;  // bytes of the owner.json copy
const EXEC_OS = ['windows', 'macos', 'linux', ''];
const EXEC_STATES = ['running', 'paused', 'updating'];

function co_exec_id_ok(mixed $id): bool
{
    return is_string($id) && preg_match(EXEC_ID_RE, $id) === 1;
}

/** Rows of executors by host id; [] before schema 28. */
function co_exec_rows(mysqli $db): array
{
    try {
        $res = $db->query('SELECT *, seen_utc > UTC_TIMESTAMP(3) - INTERVAL ' . EXEC_FRESH_SEC . ' SECOND AS fresh FROM executors ORDER BY host_id');
    } catch (mysqli_sql_exception) {
        return [];
    }
    $out = [];
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $labels = json_decode((string)$r['labels_json'], true);
        $r['labels'] = is_array($labels) ? $labels : [];
        $out[(string)$r['host_id']] = $r;
    }
    return $out;
}

function co_exec_row(mysqli $db, string $host): ?array
{
    return co_exec_rows($db)[$host] ?? null;
}

/** True when $machine is another machine than the one that owns the executor row and beat in the last 10 min. */
function co_exec_foreign(mysqli $db, array $row, mixed $machine): bool
{
    if (!is_string($machine) || $machine === '' || (string)$row['machine'] === '' || strcasecmp((string)$row['machine'], $machine) === 0) {
        return false;
    }
    $st = $db->prepare('SELECT seen_utc > UTC_TIMESTAMP(3) - INTERVAL ' . EXEC_TAKEN_SEC . ' SECOND FROM executors WHERE host_id = ?');
    $st->execute([(string)$row['host_id']]);
    $recent = (int)($st->get_result()->fetch_row()[0] ?? 0) === 1;
    $st->close();
    return $recent;
}

/** owner.json of a host from the database (kv host:<host>/owner: drive-sync or executor_put), else null. */
function co_exec_owner(mysqli $db, string $host): ?array
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['host:' . $host, 'owner']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $o = $row ? json_decode((string)$row[0], true) : null;
    if (is_array($o['data'] ?? null)) $o = $o['data']; // stored as the put item {scope, name, data}
    return is_array($o) ? $o : null;
}

/** A host that may get jobs: the two compatible ids always, others once registered and not switched off. */
function co_exec_known(mysqli $db, string $host): bool
{
    if (!co_exec_id_ok($host)) return false;
    if (isset(EXEC_COMPAT[$host])) return true;
    $r = co_exec_row($db, $host);
    return $r !== null && (int)$r['enabled'] === 1;
}

/** Environments an executor takes jobs of: null = all (the owner's machines and the two compatible ids). */
function co_exec_serves_list(?array $row): ?array
{
    if ($row === null) return null;
    $env = (string)$row['owner_env'];
    $serves = $row['labels']['serves'] ?? null;
    if (is_array($serves)) {
        $list = array_values(array_filter($serves, static fn($e): bool => is_string($e) && $e !== ''));
        if (in_array('*', $list, true)) return null;
        return $env !== '' && !in_array($env, $list, true) ? array_merge([$env], $list) : $list;
    }
    return $env === '' || co_is_owner_env($env) ? null : [$env];
}

function co_exec_serves(mysqli $db, string $host, string $env): bool
{
    $list = co_exec_serves_list(co_exec_row($db, $host));
    return $list === null || in_array($env, $list, true);
}

/** Last job_claim of a host (meta claim_poll_<host>, written by db.php job_claim), as a Unix time or null. */
function co_exec_poll_time(mysqli $db, string $host): ?int
{
    $key = strlen($host) <= 29 ? 'claim_poll_' . $host : 'claim_poll_#' . substr(md5($host), 0, 28);
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([$key]);
    $poll = $st->get_result()->fetch_row()[0] ?? null;
    $st->close();
    $t = $poll !== null ? strtotime($poll . ' UTC') : false;
    return $t === false ? null : $t;
}

/**
 * owner.json of $host when it is alive, takes jobs and takes them from the database (a job_claim within 60 s), else null.
 * A registered executor is alive by its heartbeat (90 s); 'home-main'/'site' without a row by their owner.json
 * (240 s; the site's reaches the database through Drive, so 900 s and its job_claim is the proof of life).
 */
function co_exec_alive(mysqli $db, string $host, ?array $row = null): ?array
{
    $row ??= co_exec_row($db, $host);
    if ($row !== null && (int)$row['enabled'] !== 1) return null;
    $o = co_exec_owner($db, $host);
    if ($row !== null && (int)$row['fresh'] === 1) {
        $o ??= ['hostId' => $host];
        $o['state'] = (string)$row['state'];
    } else {
        // a registered executor other than the first two lives by its heartbeat only
        if ($o === null || ($row !== null && !isset(EXEC_COMPAT[$host]))) return null;
        $t = strtotime((string)($o['updatedUtc'] ?? ''));
        if ($t === false || time() - $t >= ($host === EXEC_SITE ? 900 : 240)) return null;
    }
    if (in_array($o['state'] ?? 'running', ['updating', 'paused'], true)) return null;
    $pt = co_exec_poll_time($db, $host);
    return $pt !== null && time() - $pt < 60 ? $o : null;
}

/**
 * Hosts in the order a job of $env should try them (SPEC §3.1a p. 5-6): executors of the environment itself, 'home-main'
 * (owner 12.11: Home first), the site, then the others by id. Only hosts that serve $env.
 */
function co_exec_order(mysqli $db, string $env): array
{
    $rows = co_exec_rows($db);
    $ids = array_values(array_unique(array_merge(array_keys(EXEC_COMPAT), array_keys($rows))));
    $rank = static function (string $h) use ($rows, $env): int {
        $r = $rows[$h] ?? null;
        if ($r !== null && $env !== '' && (string)$r['owner_env'] === $env && !co_is_owner_env($env)) return 0;
        return match ($h) { EXEC_HOME => 1, EXEC_SITE => 2, default => 3 };
    };
    usort($ids, static fn(string $a, string $b): int => [$rank($a), $a] <=> [$rank($b), $b]);
    return array_values(array_filter($ids, static function (string $h) use ($rows, $env): bool {
        $r = $rows[$h] ?? null;
        if ($r !== null && (int)$r['enabled'] !== 1) return false;
        $list = co_exec_serves_list($r);
        return $list === null || in_array($env, $list, true);
    }));
}

function co_exec_kind(?array $row, string $host): string
{
    return $row !== null ? (string)$row['kind'] : (EXEC_COMPAT[$host] ?? 'bridge');
}

/** Short name of an executor for notes and labels. */
function co_exec_title(mysqli $db, string $host): string
{
    $r = co_exec_row($db, $host);
    if ($r !== null && (string)$r['title'] !== '') return (string)$r['title'];
    return match ($host) { EXEC_HOME => 'Home', EXEC_SITE => 'Site', default => $host };
}

/** One line about where a job runs (handover note, environment note). */
function co_exec_where(mysqli $db, string $host): string
{
    if ($host === EXEC_SITE) {
        return 'the server agent (shared hosting, Linux; home devices of the environment, if any, come as MCP servers dev_<name>)';
    }
    $r = co_exec_row($db, $host);
    if ($r === null) {
        return 'the home computer (Windows, BridgeAgent, Google Drive mounted)';
    }
    $os = ['windows' => 'Windows', 'macos' => 'macOS', 'linux' => 'Linux'][(string)$r['os']] ?? 'unknown OS';
    $title = (string)$r['title'] !== '' ? (string)$r['title'] : $host;
    return "the computer \"{$title}\" ({$os}, BridgeAgent, executor {$host})";
}

function co_exec_str(mixed $v, int $max, string $re = '/^[^\x00-\x1f<>]*$/u'): string
{
    $s = is_string($v) ? trim($v) : '';
    return preg_match($re, $s) === 1 ? mb_substr($s, 0, $max) : '';
}

function co_exec_ids(mixed $v, int $max): array
{
    $out = [];
    foreach (is_array($v) ? $v : [] as $x) {
        if (is_string($x) && preg_match('/^[A-Za-z0-9*][A-Za-z0-9_.:-]{0,63}$/', $x) === 1 && !in_array($x, $out, true)) $out[] = $x;
        if (count($out) >= $max) break;
    }
    return $out;
}

/**
 * executor_put (host key, Bridge every 30 s): {host, title, os, machine, version, ownerEnv, state,
 * labels: {agents, capabilities, sandboxes, devices, serves}, owner: owner.json}. 409 host_taken when another machine
 * beat with this id in the last 10 min.
 */
function co_exec_put(mysqli $db, array $in): array
{
    $host = $in['host'] ?? null;
    if (!co_exec_id_ok($host)) {
        throw new InvalidArgumentException('invalid host (letters, digits, _ and -, at most 32)');
    }
    $kind = $host === EXEC_SITE ? 'site' : 'bridge';
    if (($in['kind'] ?? $kind) !== $kind) {
        throw new InvalidArgumentException("host '$host' is reserved for the site agent");
    }
    $machine = co_exec_str($in['machine'] ?? '', 80);
    $os = is_string($in['os'] ?? null) && in_array($in['os'], EXEC_OS, true) ? $in['os'] : '';
    $state = is_string($in['state'] ?? null) && in_array($in['state'], EXEC_STATES, true) ? $in['state'] : 'running';
    $ownerEnv = $in['ownerEnv'] ?? '';
    if (!is_string($ownerEnv) || ($ownerEnv !== '' && co_member($ownerEnv) === null)) {
        throw new InvalidArgumentException('ownerEnv must be an environment of this workbench');
    }
    $l = is_array($in['labels'] ?? null) ? $in['labels'] : [];
    $labels = [
        'agents' => co_exec_ids($l['agents'] ?? [], 20),
        'capabilities' => co_exec_ids($l['capabilities'] ?? [], 60),
        'sandboxes' => co_exec_ids($l['sandboxes'] ?? [], 20),
        'devices' => is_int($l['devices'] ?? null) ? max(0, min(1000, $l['devices'])) : 0,
    ];
    if (is_array($l['serves'] ?? null)) {
        $labels['serves'] = array_values(array_filter(co_exec_ids($l['serves'], 50),
            static fn(string $e): bool => $e === '*' || co_member($e) !== null));
    }

    $cur = co_exec_row($db, $host);
    if ($cur !== null && co_exec_foreign($db, $cur, $machine)) {
        return ['ok' => false, 'error' => 'host_taken',
            'detail' => "Executor id '$host' belongs to machine '{$cur['machine']}'; give this Bridge its own hostId in config.json."];
    }

    $st = $db->prepare(
        'INSERT INTO executors (host_id, kind, title, owner_env, os, machine, version, state, labels_json, enabled, registered_utc, seen_utc)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 1, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))
         ON DUPLICATE KEY UPDATE kind = VALUES(kind), title = VALUES(title), owner_env = VALUES(owner_env), os = VALUES(os),
           machine = VALUES(machine), version = VALUES(version), state = VALUES(state), labels_json = VALUES(labels_json),
           seen_utc = VALUES(seen_utc)');
    // title: the Bridge's executor.title, else Home / Site for the first two ids, else the machine name
    $title = co_exec_str($in['title'] ?? '', 80) ?: match ($host) { EXEC_HOME => 'Home', EXEC_SITE => 'Site', default => $machine ?: $host };
    $st->execute([$host, $kind, $title, $ownerEnv, $os, $machine,
        co_exec_str($in['version'] ?? '', 32, '/^[0-9A-Za-z.+-]*$/'), $state, co_db_json($labels)]);
    $st->close();

    $owner = $in['owner'] ?? null;
    $ownerSaved = false;
    if (is_array($owner) && !array_is_list($owner)) {
        $owner['hostId'] = $host;
        $owner['updatedUtc'] = is_string($owner['updatedUtc'] ?? null) && strtotime($owner['updatedUtc']) !== false
            ? $owner['updatedUtc'] : gmdate('Y-m-d\TH:i:s\Z');
        $data = co_db_json($owner);
        if (strlen($data) <= EXEC_MAX_OWNER) {
            $st = $db->prepare('REPLACE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
            $st->execute(['host:' . $host, 'owner', $data, hash('sha256', $data)]);
            $st->close();
            $ownerSaved = true;
        }
    }
    $row = co_exec_row($db, $host);
    return ['ok' => true, 'host' => $host, 'enabled' => $row !== null && (int)$row['enabled'] === 1, 'owner' => $ownerSaved,
        'freshSec' => EXEC_FRESH_SEC, 'beatSec' => 30];
}

/** The owner.json fields a member may see of an executor that serves them (no paths, projects or devices of others). */
function co_exec_owner_view(array $o, string $env, bool $ownMachine): array
{
    $keep = ['hostId', 'updatedUtc', 'bridgeVersion', 'capabilities', 'protocolMinor', 'agents', 'state', 'updateTo',
        'runningJobs', 'stateSinceUtc'];
    $out = array_intersect_key($o, array_flip($keep));
    if ($ownMachine && isset($o['machineName'])) $out['machineName'] = $o['machineName'];
    if (is_array($o['chatProjectEnvironments'][$env] ?? null)) {
        $out['chatProjectEnvironments'] = [$env => $o['chatProjectEnvironments'][$env]];
    }
    if (is_array($o['projects'] ?? null)) {
        $p = array_filter($o['projects'], static fn($x): bool => is_array($x) && ($x['environmentId'] ?? null) === $env);
        $out['projects'] = array_map(static fn(array $x): array => array_diff_key($x, ['workspace' => 1]), $p);
    }
    if (is_array($o['devices'] ?? null)) {
        $out['devices'] = array_values(array_filter($o['devices'], static fn($d): bool => is_array($d) && ($d['environmentId'] ?? null) === $env));
    }
    return $out;
}

/**
 * executors (CO Web session): the executors that serve $env with their health — the owner sees all with the whole
 * owner.json, a member those that serve them with co_exec_owner_view.
 */
function co_exec_list(mysqli $db, string $env, bool $admin): array
{
    $rows = co_exec_rows($db);
    $owners = [];
    $res = $db->query("SELECT scope, data FROM kv WHERE name = 'owner' AND scope LIKE 'host:%'");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $o = json_decode((string)$r['data'], true);
        if (is_array($o['data'] ?? null)) $o = $o['data'];
        $h = substr((string)$r['scope'], 5);
        if (is_array($o) && co_exec_id_ok($h)) $owners[$h] = $o;
    }
    $ids = array_values(array_unique(array_merge(array_keys(EXEC_COMPAT), array_keys($rows))));
    $order = array_flip(co_exec_order($db, $env));
    $seats = [];
    try {
        foreach ($db->query('SELECT host_id, seats_json FROM sandbox_resources WHERE online = 1')->fetch_all(MYSQLI_ASSOC) as $s) {
            $j = json_decode((string)$s['seats_json'], true);
            if (is_array($j) && is_string($j[$env] ?? null)) $seats[(string)$s['host_id']] = true;
        }
    } catch (mysqli_sql_exception) {
        /* schema < 26 */
    }
    $out = [];
    foreach ($ids as $h) {
        $r = $rows[$h] ?? null;
        $o = $owners[$h] ?? null;
        if ($r === null && $o === null && $h !== EXEC_HOME && $h !== EXEC_SITE) continue;
        $serves = co_exec_serves_list($r);
        $servesMe = $serves === null || in_array($env, $serves, true);
        if (!$admin && !$servesMe) continue;
        $own = $r !== null && (string)$r['owner_env'] === $env;
        $alive = co_exec_alive($db, $h, $r) !== null;
        $item = [
            'id' => $h,
            'kind' => co_exec_kind($r, $h),
            'title' => $r !== null && (string)$r['title'] !== '' ? (string)$r['title'] : co_exec_title($db, $h),
            'registered' => $r !== null,
            'enabled' => $r === null || (int)$r['enabled'] === 1,
            'ownerEnv' => $r !== null ? (string)$r['owner_env'] : '',
            'os' => $r !== null ? (string)$r['os'] : ($h === EXEC_SITE ? 'linux' : ''),
            'version' => $r !== null ? (string)$r['version'] : (string)($o['bridgeVersion'] ?? ''),
            'state' => $r !== null ? (string)$r['state'] : (string)($o['state'] ?? 'running'),
            'seenUtc' => $r !== null ? co_db_iso((string)$r['seen_utc']) : (is_string($o['updatedUtc'] ?? null) ? $o['updatedUtc'] : null),
            'fresh' => $r !== null ? (int)$r['fresh'] === 1 : null,
            'takesJobs' => $alive,
            'servesMe' => $servesMe,
            'serves' => $serves,
            'agents' => $r !== null ? ($r['labels']['agents'] ?? []) : (is_array($o['agents'] ?? null) ? array_keys($o['agents']) : []),
            'sandboxForMe' => isset($seats[$h]),
            'rank' => $order[$h] ?? 99,
        ];
        if ($admin) {
            $item['machine'] = $r !== null ? (string)$r['machine'] : (string)($o['machineName'] ?? '');
            $item['labels'] = $r !== null ? $r['labels'] : (object)[];
        }
        if ($o !== null) {
            $item['owner'] = $admin ? $o : co_exec_owner_view($o, $env, $own);
            if ($r !== null && (int)$r['fresh'] === 1) {
                // the heartbeat is fresher than an owner.json copy that came through Drive
                $item['owner']['updatedUtc'] = max((string)($o['updatedUtc'] ?? ''), (string)co_db_iso((string)$r['seen_utc']));
                $item['owner']['state'] = (string)$r['state'];
            }
        }
        $out[] = $item;
    }
    usort($out, static fn(array $a, array $b): int => [$a['rank'], $a['id']] <=> [$b['rank'], $b['id']]);
    return ['executors' => $out, 'freshSec' => EXEC_FRESH_SEC];
}

/** executor_set (owner, CO Web): switch an executor off (it gets no jobs, its queue waits) or on, or forget it. */
function co_exec_set(mysqli $db, array $in, bool $admin): array
{
    if (!$admin) {
        return ['ok' => false, 'error' => 'owner_only'];
    }
    $host = $in['host'] ?? null;
    if (!co_exec_id_ok($host) || isset(EXEC_COMPAT[$host])) {
        throw new InvalidArgumentException('invalid host (home-main and site cannot be switched here)');
    }
    if (($in['forget'] ?? false) === true) {
        $st = $db->prepare('DELETE FROM executors WHERE host_id = ?');
        $st->execute([$host]);
        $n = $st->affected_rows;
        $st->close();
        return ['ok' => true, 'deleted' => $n];
    }
    if (!is_bool($in['enabled'] ?? null)) {
        throw new InvalidArgumentException('enabled must be true or false');
    }
    $st = $db->prepare('UPDATE executors SET enabled = ? WHERE host_id = ?');
    $st->execute([$in['enabled'] ? 1 : 0, $host]);
    $n = $st->affected_rows;
    $st->close();
    return ['ok' => true, 'changed' => $n];
}
