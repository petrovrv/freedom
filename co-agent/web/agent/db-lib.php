<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// db-lib.php: MariaDB access for the AI Workbench state (step 1 of the Drive → DB migration, 2026-10-15).
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/push-lib.php';
require_once __DIR__ . '/fts-lib.php';

/** Shared mysqli connection (singleton). */
function co_db(bool $fresh = false): mysqli
{
    static $db = null;
    if ($db !== null && !$fresh) {
        return $db;
    }
    if ($db !== null) {
        // 2026-10-16: a new connection after a long call (the host closes idle connections: "server has gone away")
        try {
            $db->close();
        } catch (Throwable) {
        }
        $db = null;
    }

    $path = co_agent_home() . '/co-agent/secrets/db.json';
    $raw = @file_get_contents($path);
    if ($raw === false) {
        throw new RuntimeException('db config not readable');
    }
    $cfg = json_decode($raw, true, 8, JSON_THROW_ON_ERROR);
    foreach (['host', 'db', 'user', 'password'] as $k) {
        if (!is_array($cfg) || !isset($cfg[$k]) || !is_string($cfg[$k])) {
            throw new RuntimeException("db config: missing '$k'");
        }
    }

    mysqli_report(MYSQLI_REPORT_ERROR | MYSQLI_REPORT_STRICT);
    // 2026-11-13: the hosting refuses a connect now and then while CO Web loads a chat (a burst of parallel calls):
    // "Operation not permitted" (2002), dozens an hour; a short retry gets through instead of answering 500
    for ($try = 1; ; $try++) {
        try {
            $m = new mysqli($cfg['host'], $cfg['user'], $cfg['password'], $cfg['db']);
            break;
        } catch (mysqli_sql_exception $e) {
            if ($try >= 4 || str_contains($e->getMessage(), 'timed out')
                || ($e->getCode() !== 2002 && !str_contains($e->getMessage(), 'Operation not permitted'))) {
                throw $e;
            }
            usleep(150000 * $try + random_int(0, 100000));   // jitter: the calls of one burst do not retry together
        }
    }
    $m->set_charset('utf8mb4');
    $m->query("SET time_zone='+00:00'");

    return $db = $m;
}

/** Canonical JSON encoding (also the input of every sha). */
function co_db_json(mixed $v): string
{
    return json_encode($v, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
}

/** ISO-8601 (Z or offset) -> 'Y-m-d H:i:s.v' UTC; invalid -> null. */
function co_db_dt(mixed $iso): ?string
{
    if (!is_string($iso) || $iso === '') {
        return null;
    }
    if (!preg_match('/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/', $iso)) {
        return null;
    }
    try {
        $d = new DateTimeImmutable($iso);
    } catch (Exception) {
        return null;
    }
    // Reject rolled-over dates such as 2026-02-30
    $err = DateTimeImmutable::getLastErrors();
    if (is_array($err) && ($err['warning_count'] > 0 || $err['error_count'] > 0)) {
        return null;
    }
    return $d->setTimezone(new DateTimeZone('UTC'))->format('Y-m-d H:i:s.v');
}

/** DB DATETIME(3) -> ISO-8601 UTC string. */
function co_db_iso(?string $dt): ?string
{
    if ($dt === null || $dt === '') {
        return null;
    }
    $d = DateTimeImmutable::createFromFormat('Y-m-d H:i:s.v', $dt, new DateTimeZone('UTC'))
        ?: DateTimeImmutable::createFromFormat('Y-m-d H:i:s', $dt, new DateTimeZone('UTC'));
    return $d ? $d->format('Y-m-d\TH:i:s.v\Z') : $dt;
}

/** Valid record id. */
function co_db_id_ok(mixed $id): bool
{
    return is_string($id) && preg_match('/^[A-Za-z0-9._@-]{1,100}$/', $id) === 1;
}

/** Valid kv scope. */
function co_db_scope_ok(mixed $s): bool
{
    return is_string($s) && preg_match('/^[A-Za-z0-9:._@-]{1,160}$/', $s) === 1;
}

/** Scalar field as string, else null. */
function co_db_s(array $r, string $k): ?string
{
    $v = $r[$k] ?? null;
    if (is_string($v)) {
        return $v;
    }
    return (is_int($v) || is_float($v)) ? (string)$v : null;
}

/**
 * Compare sha and upsert one row if changed.
 * $pk / $cols: column => value. $tsCol is set to UTC_TIMESTAMP(3).
 */
function co_db_upsert(mysqli $db, string $table, array $pk, array $cols, string $sha, string $tsCol, array &$res, bool $newOnly = false): bool
{
    static $stmts = []; // prepared statement cache (single connection)

    $where = implode(' AND ', array_map(fn($c) => "`$c`=?", array_keys($pk)));
    $sel = "SELECT sha FROM `$table` WHERE $where";
    $st = $stmts[$sel] ??= $db->prepare($sel);
    $st->execute(array_values($pk));
    $row = $st->get_result()->fetch_row();

    if ($row !== null && $row[0] === $sha) {
        $res['same']++;
        return true;
    }

    $all = $pk + $cols + ['sha' => $sha];
    $names = array_keys($all);
    $upd = array_diff($names, array_keys($pk));
    $sql = "INSERT INTO `$table` (" . implode(',', array_map(fn($c) => "`$c`", $names)) . ",`$tsCol`)"
        . ' VALUES (' . implode(',', array_fill(0, count($names), '?')) . ',UTC_TIMESTAMP(3))';
    // schema 25: a new row that may hit another unique key (one answer per job) is a plain INSERT; ON DUPLICATE KEY
    // UPDATE would overwrite the row that won instead. Duplicate key: false, nothing written (the statement alone fails).
    if (!($newOnly && $row === null)) {
        $sql .= ' ON DUPLICATE KEY UPDATE ' . implode(',', array_map(fn($c) => "`$c`=VALUES(`$c`)", $upd))
            . ",`$tsCol`=UTC_TIMESTAMP(3)";
    }

    $ins = $stmts[$sql] ??= $db->prepare($sql);
    try {
        $ins->execute(array_map(fn($v) => $v === null ? null : (string)$v, array_values($all)));
    } catch (mysqli_sql_exception $e) {
        if ($newOnly && $row === null && $e->getCode() === 1062) {
            return false;
        }
        throw $e;
    }

    $row === null ? $res['inserted']++ : $res['updated']++;
    return true;
}

/**
 * Schema 25: event id of the answer (assistant_message) a chat already has for $jobId, other than $eventId, or null.
 * A plain read (a locking one takes gap locks, and two writers of one answer would deadlock): an answer committed by a
 * concurrent writer after this transaction's snapshot is caught by the unique key ux_events_answer instead (null here).
 */
function co_db_answer_of(mysqli $db, string $chatId, string $jobId, string $eventId): ?string
{
    $st = $db->prepare("SELECT event_id FROM events WHERE chat_id = ? AND job_id = ? AND type = 'assistant_message' AND event_id <> ?
                        LIMIT 1");
    $st->execute([$chatId, $jobId, $eventId]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    return $row ? (string)$row[0] : null;
}

/** HOST put operation: upsert projects/chats/events/jobs/kv in one transaction. */
function co_db_put(array $in): array
{
    $list = function (string $k) use ($in): array {
        if (!isset($in[$k])) {
            return [];
        }
        if (!is_array($in[$k]) || !array_is_list($in[$k])) {
            throw new InvalidArgumentException("$k must be a list");
        }
        return $in[$k];
    };

    $projects = $list('projects');
    $chats    = $list('chats');
    $events   = $list('events');
    $jobs     = $list('jobs');
    $kv       = $list('kv');

    if (count($events) > 500) {
        throw new InvalidArgumentException('too many events (max 500)');
    }

    $res = ['inserted' => 0, 'updated' => 0, 'same' => 0, 'rejected' => [], 'duplicates' => []];
    $reject = function (string $kind, mixed $id, string $reason) use (&$res): void {
        $res['rejected'][] = ['kind' => $kind, 'id' => is_scalar($id) ? (string)$id : null, 'reason' => $reason];
    };

    $db = co_db();
    $db->begin_transaction();
    try {
        foreach ($projects as $r) {
            if (!is_array($r)) { $reject('project', null, 'not an object'); continue; }
            $id = $r['projectId'] ?? null;
            if (!co_db_id_ok($id)) { $reject('project', $id, 'invalid projectId'); continue; }

            $data = co_db_json($r);
            co_db_upsert($db, 'projects', ['project_id' => $id], [
                'env'           => co_db_s($r, 'environmentId'),
                'title'         => co_db_s($r, 'title'),
                'description'   => co_db_s($r, 'description'),
                'default_agent' => co_db_s($r, 'defaultAgentId'),
                'archived'      => ($r['archived'] ?? false) === true ? 1 : 0,
                'created_utc'   => co_db_dt($r['createdUtc'] ?? null),
                'updated_utc'   => co_db_dt($r['updatedUtc'] ?? null),
                'data'          => $data,
            ], hash('sha256', $data), 'synced_utc', $res);
        }

        foreach ($chats as $r) {
            if (!is_array($r)) { $reject('chat', null, 'not an object'); continue; }
            $id = $r['chatId'] ?? null;
            if (!co_db_id_ok($id)) { $reject('chat', $id, 'invalid chatId'); continue; }
            if (!co_db_id_ok($r['projectId'] ?? null)) { $reject('chat', $id, 'invalid projectId'); continue; }

            $data = co_db_json($r);
            co_db_upsert($db, 'chats', ['chat_id' => $id], [
                'project_id'     => $r['projectId'],
                'title'          => co_db_s($r, 'title'),
                'root_branch_id' => co_db_s($r, 'rootBranchId'),
                'default_mode'   => co_db_s($r, 'defaultMode'),
                'created_utc'    => co_db_dt($r['createdUtc'] ?? null),
                'updated_utc'    => co_db_dt($r['updatedUtc'] ?? null),
                'data'           => $data,
            ], hash('sha256', $data), 'synced_utc', $res);
        }

        // CO Web 0.133.0 (msgedit-lib.php): data.deleted of a hidden message stays when a client writes an older copy
        $evIds = array_values(array_unique(array_filter(array_map(static fn($r) => is_array($r) && co_db_id_ok($r['eventId'] ?? null)
            ? (string)$r['eventId'] : null, $events))));
        $hiddenOf = [];
        if ($evIds) {
            $st = $db->prepare("SELECT event_id, JSON_EXTRACT(data, '$.deleted') FROM events WHERE event_id IN ("
                . implode(',', array_fill(0, count($evIds), '?')) . ") AND JSON_EXTRACT(data, '$.deleted') IS NOT NULL");
            $st->execute($evIds);
            foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as $h) {
                $d = json_decode((string)$h[1], true);
                if (is_array($d)) $hiddenOf[(string)$h[0]] = $d;
            }
            $st->close();
        }

        foreach ($events as $r) {
            if (!is_array($r)) { $reject('event', null, 'not an object'); continue; }
            // data.deleted is written only by msgedit-lib.php: a client's value is replaced by the stored one (none = none)
            unset($r['deleted']);
            if (isset($r['eventId']) && is_string($r['eventId']) && isset($hiddenOf[$r['eventId']])) {
                $r['deleted'] = $hiddenOf[$r['eventId']];
            } elseif (is_string($r['jobId'] ?? null) && $r['jobId'] !== '' && ($r['type'] ?? '') !== 'user_message'
                && is_string($r['chatId'] ?? null)) {
                // a late answer (or other event) of a job whose message was deleted or forgotten stays hidden with it
                $st = $db->prepare("SELECT JSON_EXTRACT(data, '$.deleted') FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message'
                                    AND JSON_EXTRACT(data, '$.deleted') IS NOT NULL LIMIT 1");
                $st->execute([$r['chatId'], $r['jobId']]);
                $h = $st->get_result()->fetch_row();
                $st->close();
                $d = $h ? json_decode((string)$h[0], true) : null;
                if (is_array($d)) {
                    unset($d['protocol']);
                    $r['deleted'] = $d;
                }
            }
            $id = $r['eventId'] ?? null;
            if (!co_db_id_ok($id)) { $reject('event', $id, 'invalid eventId'); continue; }
            if (!co_db_id_ok($r['chatId'] ?? null)) { $reject('event', $id, 'invalid chatId'); continue; }
            if (!co_db_id_ok($r['branchId'] ?? null)) { $reject('event', $id, 'invalid branchId'); continue; }

            $seq = $r['sequence'] ?? null;
            if (is_string($seq) && preg_match('/^-?\d+$/', $seq)) {
                $seq = (int)$seq;
            }
            if (!is_int($seq)) { $reject('event', $id, 'missing sequence'); continue; }

            $type = co_db_s($r, 'type');
            if ($type === null || $type === '') { $reject('event', $id, 'missing type'); continue; }

            $jobId = $r['jobId'] ?? null;
            if ($jobId !== null && !co_db_id_ok($jobId)) { $reject('event', $id, 'invalid jobId'); continue; }

            // schema 25: one answer per job and chat; a later copy (another CO Web tab, the server's finalize, a retry)
            // is dropped and named in 'duplicates' with the answer that stays
            $answer = $type === 'assistant_message' && $jobId !== null;
            if ($answer && ($first = co_db_answer_of($db, (string)$r['chatId'], (string)$jobId, (string)$id)) !== null) {
                $res['duplicates'][] = ['id' => (string)$id, 'jobId' => (string)$jobId, 'existing' => $first];
                continue;
            }

            $data = co_db_json($r);
            $written = co_db_upsert($db, 'events', ['event_id' => $id], [
                'chat_id'     => $r['chatId'],
                'branch_id'   => $r['branchId'],
                'seq'         => $seq,
                'type'        => $type,
                'job_id'      => $jobId,
                'agent'       => co_db_s($r, 'agent'),
                'author'      => co_db_s($r, 'author'),
                'created_utc' => co_db_dt($r['createdUtc'] ?? null),
                'text'        => co_db_s($r, 'text'),
                'data'        => $data,
            ], hash('sha256', $data), 'synced_utc', $res, $answer);
            if (!$written) {
                $res['duplicates'][] = ['id' => (string)$id, 'jobId' => (string)$jobId,
                    'existing' => co_db_answer_of($db, (string)$r['chatId'], (string)$jobId, (string)$id)];
            }
        }

        foreach ($jobs as $r) {
            if (!is_array($r)) { $reject('job', null, 'not an object'); continue; }
            $req = $r['request'] ?? null;
            if (!is_array($req)) { $reject('job', null, 'missing request'); continue; }

            $id = $req['jobId'] ?? null;
            if (!co_db_id_ok($id)) { $reject('job', $id, 'invalid jobId'); continue; }

            $host = $r['hostId'] ?? $req['hostId'] ?? null;
            if (!co_db_id_ok($host)) { $reject('job', $id, 'invalid hostId'); continue; }

            $state = $r['state'] ?? null;
            if (!is_string($state) || $state === '') { $reject('job', $id, 'missing state'); continue; }

            $result = $r['result'] ?? null;
            if ($result !== null && !is_array($result)) { $reject('job', $id, 'invalid result'); continue; }

            // Schema 4: the queue (job_claim / job_result) may be ahead of the Drive copy; never move a job back.
            [$state, $result] = co_db_job_merge($db, $id, $state, $result);

            // sha covers request + result; result NULL stored as SQL NULL
            $data = co_db_json(['request' => $req, 'result' => $result]);
            co_db_upsert($db, 'jobs', ['job_id' => $id], [
                'host_id'       => $host,
                'env'           => co_db_s($req, 'environmentId'),
                'project_id'    => co_db_s($req, 'projectId'),
                'chat_id'       => co_db_s($req, 'chatId'),
                'agent'         => co_db_s($req, 'agent'),
                'mode'          => co_db_s($req, 'mode'),
                'model'         => co_db_s($req, 'model'),
                'state'         => $state,
                'status'        => $result !== null ? co_db_s($result, 'status') : null,
                'started_utc'   => co_db_dt($result['startedUtc'] ?? null),
                'completed_utc' => co_db_dt($result['completedUtc'] ?? null),
                'request'       => co_db_json($req),
                'result'        => $result !== null ? co_db_json($result) : null,
            ], hash('sha256', $data), 'synced_utc', $res);
        }

        foreach ($kv as $r) {
            if (!is_array($r)) { $reject('kv', null, 'not an object'); continue; }
            $scope = $r['scope'] ?? null;
            $name  = $r['name'] ?? null;
            $kid   = (is_scalar($scope) ? (string)$scope : '') . '/' . (is_scalar($name) ? (string)$name : '');
            if (!co_db_scope_ok($scope)) { $reject('kv', $kid, 'invalid scope'); continue; }
            if (!co_db_id_ok($name)) { $reject('kv', $kid, 'invalid name'); continue; }
            if (!array_key_exists('data', $r)) { $reject('kv', $kid, 'missing data'); continue; }

            $data = co_db_json($r);
            co_db_upsert($db, 'kv', ['scope' => $scope, 'name' => $name], [
                'data' => $data,
            ], hash('sha256', $data), 'updated_utc', $res);
        }

        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }

    return $res;
}

/** Order of job states; a later state never goes back to an earlier one. */
function co_db_job_rank(?string $state): int
{
    return match ($state) {
        null, '', 'inbox' => 0,
        'claimed' => 1,
        'cancelled' => 3, // 2026-11-01: a withdrawn job stays cancelled even if a stray copy reports a result
        default => 2, // completed, failed, rejected
    };
}

/** Merge an incoming job state/result with the stored row: keep the later state and any known result. */
function co_db_job_merge(mysqli $db, string $jobId, string $state, ?array $result): array
{
    $st = $db->prepare('SELECT state, result FROM jobs WHERE job_id=?');
    $st->execute([$jobId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row) {
        return [$state, $result];
    }
    if (co_db_job_rank($row['state']) > co_db_job_rank($state)) {
        $state = (string)$row['state'];
    }
    if ($result === null && is_string($row['result']) && $row['result'] !== '') {
        $old = json_decode($row['result'], true);
        $result = is_array($old) ? $old : null;
    }
    return [$state, $result];
}

/** Row count and last sync time per table. */
function co_db_stats(): array
{
    $tables = [
        'projects' => 'synced_utc',
        'chats'    => 'synced_utc',
        'events'   => 'synced_utc',
        'jobs'     => 'synced_utc',
        'kv'       => 'updated_utc',
    ];
    $db = co_db();
    $out = [];
    foreach ($tables as $t => $col) {
        $st = $db->prepare("SELECT COUNT(*), MAX(`$col`) FROM `$t`");
        $st->execute();
        $row = $st->get_result()->fetch_row();
        $out[$t] = ['count' => (int)$row[0], 'max' => co_db_iso($row[1])];
        $st->close();
    }
    return $out;
}

/** Return stored shas by ids or (events only) by chatId. */
function co_db_shas(array $in): array
{
    $pkMap = [
        'projects' => 'project_id',
        'chats'    => 'chat_id',
        'events'   => 'event_id',
        'jobs'     => 'job_id',
    ];
    $kind = $in['kind'] ?? null;
    if (!is_string($kind) || !isset($pkMap[$kind])) {
        throw new InvalidArgumentException('invalid kind');
    }
    $pk = $pkMap[$kind];
    $db = co_db();
    $shas = [];

    if (isset($in['chatId'])) {
        if ($kind !== 'events') {
            throw new InvalidArgumentException('chatId only supported for events');
        }
        if (!co_db_id_ok($in['chatId'])) {
            throw new InvalidArgumentException('invalid chatId');
        }
        $st = $db->prepare('SELECT event_id, sha FROM events WHERE chat_id=?');
        $st->execute([$in['chatId']]);
    } else {
        $ids = $in['ids'] ?? null;
        if (!is_array($ids)) {
            throw new InvalidArgumentException('ids or chatId required');
        }
        if (count($ids) > 2000) {
            throw new InvalidArgumentException('too many ids (max 2000)');
        }
        $ids = array_values(array_unique(array_filter($ids, 'co_db_id_ok')));
        if ($ids === []) {
            return ['shas' => $shas];
        }
        $ph = implode(',', array_fill(0, count($ids), '?'));
        $st = $db->prepare("SELECT `$pk`, sha FROM `$kind` WHERE `$pk` IN ($ph)");
        $st->execute($ids);
    }

    $rs = $st->get_result();
    while ($row = $rs->fetch_row()) {
        $shas[(string)$row[0]] = $row[1];
    }
    $st->close();

    return ['shas' => $shas];
}

/** Require POST with valid X-CO-Push-Key header (the home PC's shared key). */
function co_db_host_auth(): void
{
    $method   = $_SERVER['REQUEST_METHOD'] ?? '';
    $given    = $_SERVER['HTTP_X_CO_PUSH_KEY'] ?? '';
    $expected = co_push_notify_key();

    if ($method !== 'POST'
        || !is_string($given) || $given === ''
        || $expected === ''
        || !hash_equals($expected, $given)) {
        co_json(403, ['error' => 'forbidden']);
    }
}

// 2026-10-17 (live protocol): messages of a chat by number, search or the latest ones, for agents working from the
// chat protocol (tool aiwb-chat, home PC key). Op chat_read in db.php.
function db_chat_read(array $in): array
{
    $toInt = static function (mixed $v, string $name, int $min, int $max): int {
        if (is_int($v)) {
            $i = $v;
        } elseif (is_string($v) && preg_match('/^\s*-?\d{1,18}\s*$/', $v)) {
            $i = (int) trim($v);
        } else {
            throw new InvalidArgumentException("Invalid '$name': integer expected");
        }
        if ($i < $min || $i > $max) {
            throw new InvalidArgumentException("Invalid '$name': must be between $min and $max");
        }
        return $i;
    };
    $present = static fn(string $k): bool => array_key_exists($k, $in) && $in[$k] !== null && $in[$k] !== '';

    $chatId = $in['chatId'] ?? null;
    if (!is_string($chatId) || !preg_match('/^[A-Za-z0-9_.-]{1,60}$/', $chatId)) {
        throw new InvalidArgumentException("Invalid 'chatId'");
    }

    $hasSeq = $present('seq');
    $hasQ   = $present('q');
    if ($hasSeq && $hasQ) {
        throw new InvalidArgumentException("Provide either 'seq' or 'q', not both");
    }

    $db   = co_db();
    $cols = 'seq, type, agent, writer, created_utc, text';
    // schema 18: byOwner - the signed-in account that wrote the message (events.writer, set by put_user from the session)
    // is the person of the chat's env; read-guard grant only counts those. Without a writer: unknown (null) in an
    // unshared chat, false in a shared one.
    $st = $db->prepare("SELECT p.env, (SELECT COUNT(*) FROM shares s WHERE (s.kind = 'chat' AND s.target_id = c.chat_id)
                          OR (s.kind = 'project' AND s.target_id = c.project_id)) FROM chats c
                          JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?");
    $st->execute([$chatId]);
    $prow = $st->get_result()->fetch_row();
    $st->close();
    $penv = $prow[0] ?? null;
    $shared = (int)($prow[1] ?? 0) > 0;
    $users = co_push_users();
    $base = "chat_id = ? AND type IN ('user_message','assistant_message') AND JSON_EXTRACT(data, '$.deleted') IS NULL";

    $fetch = static function (string $sql, array $params) use ($db): array {
        $st = $db->prepare($sql);
        $st->execute($params);
        $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
        $st->close();
        return $rows;
    };
    $row = static fn(array $r, string $text): array => [
        'seq'        => (int) $r['seq'],
        'type'       => (string) $r['type'],
        'agent'      => $r['agent'] !== null ? (string) $r['agent'] : null,
        'createdUtc' => (string) $r['created_utc'],
        'text'       => $text,
        'byOwner'    => $r['type'] !== 'user_message' || $penv === null ? null
            : ($r['writer'] === null ? ($shared ? false : null) : ($users[strtolower((string) $r['writer'])] ?? null) === $penv),
    ];

    // Mode 1: seq (+ around)
    if ($hasSeq) {
        $seq    = $toInt($in['seq'], 'seq', 0, PHP_INT_MAX);
        $around = $present('around') ? $toInt($in['around'], 'around', 0, 5) : 0;
        $from   = max(0, $seq - $around);
        $to     = $seq + $around;

        $rows = $fetch(
            "SELECT $cols FROM events WHERE $base AND seq BETWEEN ? AND ? ORDER BY seq",
            [$chatId, $from, $to]
        );

        $out = [];
        foreach ($rows as $r) {
            $full  = (string) ($r['text'] ?? '');
            $len   = mb_strlen($full, 'UTF-8');
            $text  = $len > 60000 ? mb_substr($full, 0, 60000, 'UTF-8') . '…[cut]' : $full;
            $m     = $row($r, $text);
            $m['chars'] = $len;
            $out[] = $m;
        }
        return ['messages' => $out];
    }

    // Mode 2: q (+ limit)
    if ($hasQ) {
        if (!is_string($in['q'])) {
            throw new InvalidArgumentException("Invalid 'q': string expected");
        }
        $q    = trim($in['q']);
        $qLen = mb_strlen($q, 'UTF-8');
        if ($qLen < 2 || $qLen > 200) {
            throw new InvalidArgumentException("Invalid 'q': length must be between 2 and 200");
        }
        $limit = $present('limit') ? $toInt($in['limit'], 'limit', 1, 20) : 8;

        $words = array_values(array_unique(array_filter(
            preg_split('/\s+/u', $q) ?: [],
            static fn($w) => $w !== ''
        )));

        // 2026-11-07: word stems (fts-lib.php) - "капчу" finds "капча", "tests" finds "testing"; 2026-11-08: long rows damped
        $ftq  = co_fts_query($q);
        $rows = $ftq === '' ? [] : $fetch(
            "SELECT $cols, " . co_fts_score_sql('MATCH(text) AGAINST(? IN BOOLEAN MODE)', 'text') . " AS relevance
             FROM events
             WHERE $base AND MATCH(text) AGAINST(? IN BOOLEAN MODE)
             ORDER BY relevance DESC
             LIMIT $limit",
            [$ftq, $chatId, $ftq]
        );

        if (!$rows) {
            $likeWords = array_slice(
                array_values(array_filter($words, static fn($w) => mb_strlen($w, 'UTF-8') >= 3)),
                0,
                5
            );
            if ($likeWords) {
                $conds  = [];
                $params = [$chatId];
                foreach ($likeWords as $w) {
                    $conds[]  = 'text LIKE ?';
                    $params[] = '%' . str_replace(['\\', '%', '_'], ['\\\\', '\\%', '\\_'], $w) . '%';
                }
                $rows = $fetch(
                    "SELECT $cols FROM events WHERE $base AND " . implode(' AND ', $conds)
                    . " ORDER BY seq DESC LIMIT $limit",
                    $params
                );
            }
        }

        $needles = co_fts_needles($q);
        $snippet = static function (string $text) use ($needles): string {
            $max = 300;
            $len = mb_strlen($text, 'UTF-8');
            if ($len <= $max) {
                return $text;
            }
            $pos = null;
            foreach ($needles as $w) {
                $p = mb_stripos($text, $w, 0, 'UTF-8');
                if ($p !== false && ($pos === null || $p < $pos)) {
                    $pos = $p;
                }
            }
            if ($pos === null) {
                return mb_substr($text, 0, $max, 'UTF-8') . '…';
            }
            $start = max(0, $pos - 100);
            if ($start + $max > $len) {
                $start = max(0, $len - $max);
            }
            $cut = mb_substr($text, $start, $max, 'UTF-8');
            return ($start > 0 ? '…' : '') . $cut . ($start + $max < $len ? '…' : '');
        };

        $out = [];
        foreach ($rows as $r) {
            $out[] = $row($r, $snippet((string) ($r['text'] ?? '')));
        }
        return ['messages' => $out];
    }

    // Mode 3: last N messages
    $limit = $present('limit') ? $toInt($in['limit'], 'limit', 1, 50) : 20;
    $rows  = $fetch(
        "SELECT $cols FROM events WHERE $base ORDER BY seq DESC LIMIT $limit",
        [$chatId]
    );

    $out = [];
    foreach (array_reverse($rows) as $r) {
        $out[] = $row($r, mb_substr((string) ($r['text'] ?? ''), 0, 120, 'UTF-8'));
    }
    return ['messages' => $out];
}
