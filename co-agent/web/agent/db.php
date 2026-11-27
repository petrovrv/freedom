<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// db.php: JSON endpoint of the AI Workbench database. Host ops (put/stats/shas) with the home PC key,
// user ops (projects/chats/events/search) with the CO Web session or bearer token.
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';
require_once __DIR__ . '/finalize-lib.php';
require_once __DIR__ . '/ops-lib.php';
require_once __DIR__ . '/share-lib.php';
require_once __DIR__ . '/msgedit-lib.php';
require_once __DIR__ . '/webdev-lib.php';
require_once __DIR__ . '/relay-lib.php';
require_once __DIR__ . '/gateway-lib.php';
require_once __DIR__ . '/trace-lib.php';
require_once __DIR__ . '/jobstats-lib.php';
require_once __DIR__ . '/files-lib.php';
require_once __DIR__ . '/followup-lib.php';
require_once __DIR__ . '/inbox-lib.php';
require_once __DIR__ . '/failures-lib.php';
require_once __DIR__ . '/actions-lib.php';
require_once __DIR__ . '/dossier-lib.php';
require_once __DIR__ . '/browser-lib.php';
require_once __DIR__ . '/search-lib.php';
require_once __DIR__ . '/family-lib.php';
require_once __DIR__ . '/modules-lib.php';
require_once __DIR__ . '/apps-lib.php';
require_once __DIR__ . '/models-lib.php';
require_once __DIR__ . '/subs-lib.php';
require_once __DIR__ . '/executors-lib.php';

header('Cache-Control: no-store');

const DB_MAX_BODY = 8 * 1024 * 1024;

// Read an optional int field, clamped to [min, max]; 400 on wrong type.
function db_int(array $in, string $key, int $def, int $min, int $max): int
{
    if (!array_key_exists($key, $in) || $in[$key] === null) {
        return $def;
    }
    $v = $in[$key];
    if (is_string($v) && preg_match('/^-?\d+$/', $v)) {
        $v = (int)$v;
    }
    if (!is_int($v)) {
        throw new InvalidArgumentException("$key must be an integer");
    }
    return max($min, min($max, $v));
}

// Read a required valid id field.
function db_id(array $in, string $key): string
{
    $v = $in[$key] ?? null;
    if (!is_string($v) || !co_db_id_ok($v)) {
        throw new InvalidArgumentException("invalid $key");
    }
    return $v;
}

// Decode the 'data' column of every row, skipping undecodable rows.
function db_decode_rows(mysqli_result $res): array
{
    $out = [];
    while ($row = $res->fetch_assoc()) {
        $d = is_string($row['data'] ?? null) ? json_decode($row['data'], true) : null;
        if (is_array($d)) {
            if (is_string($row['folder_id'] ?? null) && $row['folder_id'] !== '') {
                $d['folderId'] = $row['folder_id']; // schema 3: lets CO Web skip the Drive listing
            }
            if (is_string($row['summary'] ?? null) && $row['summary'] !== '') {
                $d['summary'] = $row['summary']; // schema 6: automatic chat summary
            }
            $out[] = $d;
        }
    }
    $res->free();
    return $out;
}

// CO Web 0.18.0 write-through: projects/chats/events the browser just wrote to Drive, so that database-first reads
// see them at once. Client-only keys are dropped (stored JSON equals the Drive file); folder ids go to folder_id.
function db_put_user(array $in, callable $envAllowed, array $acc): array
{
    $db = co_db();
    $idKey = ['projects' => 'projectId', 'chats' => 'chatId', 'events' => 'eventId'];
    $put = [];
    $folders = ['projects' => [], 'chats' => []];
    foreach ($idKey as $k => $ik) {
        $list = $in[$k] ?? [];
        if (!is_array($list) || !array_is_list($list) || count($list) > 200) {
            throw new InvalidArgumentException("invalid $k");
        }
        foreach ($list as $o) {
            if (!is_array($o) || !co_db_id_ok($o[$ik] ?? null)) {
                throw new InvalidArgumentException("invalid item in $k");
            }
            $f = $o['folderId'] ?? null;
            if ($k !== 'events' && is_string($f) && preg_match('/^[A-Za-z0-9_-]{10,80}$/', $f)) {
                $folders[$k][(string)$o[$ik]] = $f;
            }
            unset($o['folderId'], $o['legacy'], $o['draft']);
            $put[$k][] = $o;
        }
    }

    $one = function (string $sql, string $id) use ($db): ?string {
        $st = $db->prepare($sql);
        $st->execute([$id]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        return $row === null ? null : (string)$row[0];
    };
    $cache = ['env' => [], 'chat' => [], 'event' => []];
    $lookup = function (string $type, string $sql, string $id) use ($one, &$cache): ?string {
        if (!array_key_exists($id, $cache[$type])) {
            $cache[$type][$id] = $one($sql, $id);
        }
        return $cache[$type][$id];
    };
    $dbEnv = fn(string $pid): ?string => $lookup('env', 'SELECT env FROM projects WHERE project_id=?', $pid);
    $dbChat = fn(string $cid): ?string => $lookup('chat', 'SELECT project_id FROM chats WHERE chat_id=?', $cid);
    $dbEvent = fn(string $eid): ?string => $lookup('event', 'SELECT chat_id FROM events WHERE event_id=?', $eid);
    // 0.45.0 (schema 10): roles instead of the env check; shared projects/chats are writable by editors.
    $db = $acc['db'];
    $myEnv = (string)$acc['myEnv'];
    $admin = (bool)$acc['admin'];

    $allow = fn(?string $env): bool => $env !== null && $env !== '' && (bool)$envAllowed($env);
    $deny = function (): never { throw new RuntimeException('forbidden'); };

    // cached role lookups
    $projRoles = [];
    $chatRoles = [];
    $projRole = function (string $pid) use (&$projRoles, $db, $myEnv, $admin): ?string {
        if (!array_key_exists($pid, $projRoles)) {
            $projRoles[$pid] = share_project_role($db, $myEnv, $admin, $pid);
        }
        return $projRoles[$pid];
    };
    $chatRole = function (string $cid) use (&$chatRoles, $db, $myEnv, $admin): ?string {
        if (!array_key_exists($cid, $chatRoles)) {
            $chatRoles[$cid] = share_chat_role($db, $myEnv, $admin, $cid);
        }
        return $chatRoles[$cid];
    };

    // projects
    $callEnv = [];
    $transfers = [];
    $known = share_known_envs();
    foreach ($put['projects'] ?? [] as $p) {
        $pid = (string)$p['projectId'];
        $env = $p['environmentId'] ?? null;
        if (!is_string($env) || $env === '') $deny();
        $old = $dbEnv($pid);
        if ($old === null) {
            // new project: own env (admin any)
            if (!$allow($env)) $deny();
        } elseif ($env === $old) {
            // update in place
            if (!share_can($projRole($pid), 'write')) $deny();
        } else {
            // transfer: manage on old, known target env
            if (!share_can($projRole($pid), 'manage')) $deny();
            if (!in_array($env, $known, true)) $deny();
            // share changes wait until the project row is written (after co_db_put)
            $transfers[$pid] = ['from' => $old, 'to' => $env];
        }
        $callEnv[$pid] = $env;
    }

    // projects from this call count as owner
    $canWriteProject = fn(string $pid): bool =>
        array_key_exists($pid, $callEnv) || share_can($projRole($pid), 'write');

    // chats
    $callChat = [];
    foreach ($put['chats'] ?? [] as $c) {
        $cid = (string)$c['chatId'];
        $pid = $c['projectId'] ?? null;
        if (!co_db_id_ok($pid)) $deny();
        $pid = (string)$pid;
        $old = $dbChat($cid);
        $ok = $canWriteProject($pid)
            || ($old === $pid && share_can($chatRole($cid), 'write'));
        if (!$ok) $deny();
        // moving chat: need write on old project too
        if ($old !== null && $old !== $pid && !$canWriteProject($old)) $deny();
        $callChat[$cid] = $pid;
    }

    // chats from this call already checked
    $canWriteChat = fn(string $cid): bool =>
        array_key_exists($cid, $callChat) || share_can($chatRole($cid), 'write');

    // events
    foreach ($put['events'] ?? [] as $e) {
        $cid = $e['chatId'] ?? null;
        if (!co_db_id_ok($cid)) $deny();
        $cid = (string)$cid;
        // chat must exist in DB or in this call
        if (!array_key_exists($cid, $callChat) && $dbChat($cid) === null) $deny();
        if (!$canWriteChat($cid)) $deny();
        // moving event: need write on old chat too
        $old = $dbEvent((string)$e['eventId']);
        if ($old !== null && $old !== $cid && !$canWriteChat($old)) $deny();
    }

    // 0.45.0: grant the previous owner's editor share before the write (ignored while still owner),
    // so a failure after co_db_put or a host sync importing the new env cannot lock them out
    foreach ($transfers as $pid => $t) {
        share_set($db, 'project', (string)$pid, $t['from'], 'editor', 'transfer');
    }
    // schema 18: shas of the events before the write; new or changed ones get the caller as writer afterwards
    $evIds = array_values(array_unique(array_map(static fn(array $e): string => (string)$e['eventId'], $put['events'] ?? [])));
    $shaOf = static function (array $ids) use ($db): array {
        if (!$ids) return [];
        $st = $db->prepare('SELECT event_id, sha FROM events WHERE event_id IN (' . implode(',', array_fill(0, count($ids), '?')) . ')');
        $st->execute($ids);
        $m = [];
        foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as $r) $m[(string)$r[0]] = (string)$r[1];
        $st->close();
        return $m;
    };
    $before = $shaOf($evIds);
    $res = co_db_put($put);
    $after = $shaOf($evIds);
    $changed = array_values(array_filter($evIds, static fn(string $id): bool =>
        isset($after[$id]) && ($before[$id] ?? null) !== $after[$id]));
    if ($changed) {
        $st = $db->prepare('UPDATE events SET writer = ? WHERE event_id IN (' . implode(',', array_fill(0, count($changed), '?')) . ')');
        $st->execute([strtolower((string)($acc['email'] ?? '')), ...$changed]);
        $st->close();
    }
    // 0.89.0: first writes of messages in family chats go as push to the other members (family-lib.php)
    $fresh = array_values(array_filter($put['events'] ?? [], static fn(array $e): bool =>
        !isset($before[(string)$e['eventId']]) && isset($after[(string)$e['eventId']])));
    if ($fresh) {
        // after the response is sent: slow push endpoints must not hold the sender's request
        $wEmail = strtolower((string)($acc['email'] ?? ''));
        $wEnv = co_push_users()[$wEmail] ?? $myEnv;
        register_shutdown_function(static function () use ($fresh, $wEnv, $wEmail): void {
            if (function_exists('litespeed_finish_request')) litespeed_finish_request();
            elseif (function_exists('fastcgi_finish_request')) fastcgi_finish_request();
            try {
                co_family_notify(co_db(), $fresh, $wEnv, null, $wEmail !== '' ? $wEmail : null);
            } catch (Throwable $e) {
                error_log('family notify: ' . $e->getMessage());
            }
        });
    }
    // apply recorded transfers only for projects whose env was actually written
    foreach ($transfers as $pid => $t) {
        if (share_project_env($db, (string)$pid) === $t['to']) {
            // drop shares pointing at the new env
            share_exec($db, "DELETE FROM shares WHERE kind = 'project' AND target_id = ? AND env = ?", [(string)$pid, $t['to']]);
        }
    }

    $upd = [
        'projects' => 'UPDATE projects SET folder_id=? WHERE project_id=?',
        'chats' => 'UPDATE chats SET folder_id=? WHERE chat_id=?',
    ];
    foreach ($upd as $k => $sql) {
        if (!$folders[$k]) continue;
        $st = $db->prepare($sql);
        foreach ($folders[$k] as $id => $fid) {
            $st->execute([$fid, (string)$id]);
        }
        $st->close();
    }
    return $res;
}

// ---------- Job queue (schema 4, Bridge 1.3.0, CO Web 0.19.0) ----------

// Hosts that take jobs from the database: S7 'site' too (site agent 1.6.0, job_claim); F12 (schema 28, executors-lib.php)
// every registered executor besides the two compatible ids 'home-main' and 'site' (co_exec_known).
// A claimed job without any event batch after this many seconds is offered again (Bridge died before starting it).
const DB_CLAIM_LEASE_SECONDS = 120;

function db_job_view(array $row): array
{
    $res = is_string($row['result'] ?? null) && $row['result'] !== '' ? json_decode($row['result'], true) : null;
    return [
        'state'           => $row['state'],
        'status'          => $row['status'],
        'origin'          => $row['origin'],
        'result'          => is_array($res) ? $res : null,
        'rejection'       => $row['rejection'],
        'cancelRequested' => (int)$row['cancel_requested'] === 1,
    ];
}

// CO Web: queue a job (the same request.json it would write to the Drive inbox). 409 if the jobId exists.
function db_job_submit(array $in, callable $envAllowed, array $acc): array
{
    $db = $acc['db'];
    $myEnv = (string)$acc['myEnv'];
    $admin = (bool)$acc['admin'];

    $req = $in['request'] ?? null;
    if (!is_array($req) || array_is_list($req)) {
        throw new InvalidArgumentException('invalid request');
    }
    foreach (['jobId', 'projectId', 'chatId', 'agent'] as $k) {
        if (!co_db_id_ok($req[$k] ?? null)) {
            throw new InvalidArgumentException("invalid request.$k");
        }
    }
    $host = $req['hostId'] ?? null;
    if (!is_string($host) || !co_exec_known($db, $host)) {
        throw new InvalidArgumentException('host without database queue');
    }
    // F12: an executor of a family member takes only the jobs of the environments it serves
    if (!co_exec_serves($db, $host, (string)($req['environmentId'] ?? ''))) {
        co_json(403, ['error' => 'host_not_serving', 'detail' => "Executor $host does not take jobs of this environment."]);
    }

    // 0.45.0 (schema 10): the job runs in the project's env; the sender needs write on the chat or project.
    $pid = (string)$req['projectId'];
    $cid = (string)$req['chatId'];
    $penv = share_project_env($db, $pid);
    if ($penv === null) {
        // project not in DB yet: legacy env rule
        if (!$envAllowed($req['environmentId'] ?? null)) {
            co_json(403, ['error' => 'forbidden']);
        }
    } else {
        if (($req['environmentId'] ?? null) !== $penv) {
            throw new InvalidArgumentException('environmentId must equal the project environment');
        }
        // the chat must belong to the named project; chat role if chat exists, else project role
        $chatPid = share_chat_project($db, $cid);
        if ($chatPid !== null && $chatPid !== $pid) {
            throw new InvalidArgumentException('chatId does not belong to projectId');
        }
        $role = $chatPid !== null
            ? share_chat_role($db, $myEnv, $admin, $cid)
            : share_project_role($db, $myEnv, $admin, $pid);
        if (!share_can($role, 'write')) {
            co_json(403, ['error' => 'forbidden']);
        }
        // shared chats: no devices, context folders or gateway agents (the owner's confidential data stays out). R2: also
        // a chat below a shared one (subtasks), and the job carries restricted so the site agent and relay refuse devices.
        $refused = share_submit_policy($db, $pid, $cid, $req, co_gateway_agent((string)$req['agent']) !== null);
        if ($refused !== null) {
            co_json(403, [
                'error' => $refused,
                'detail' => 'A shared chat cannot use devices, confidential models or context folders.',
            ]);
        }
    }

    // schema 11: web devices ("w-...") must belong to the submitter
    webdev_check_job_devices($db, $req['devices'] ?? null, (string)$acc['email']);

    $json = co_db_json($req);
    if (strlen($json) > 2 * 1024 * 1024) {
        throw new InvalidArgumentException('request too large');
    }
    $sha = hash('sha256', co_db_json(['request' => $req, 'result' => null]));
    // F15.2: no job for a chat in the CO trash; the check and the INSERT hold the chat row, like the trash clean-up
    try {
        // CO Web 0.134.0 (msgedit-lib.php): a window that has not seen a deletion yet must not resume the old session
        if (msgedit_resume_stale($db, $cid, $req)) {
            co_json(403, ['error' => 'session_reset',
                'detail' => 'Messages of this chat were deleted or rewritten in another window. Reload the chat and send again: a new agent session starts.']);
        }
        $queued = share_job_insert_untrashed($db, $cid, static function () use ($db, $req, $host, $pid, $cid, $json, $sha, $myEnv): void {
            $st = $db->prepare(
                "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc,
                                   request, sha, synced_utc, actor_env)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'inbox', 'db', UTC_TIMESTAMP(3), ?, ?, UTC_TIMESTAMP(3), ?)"
            );
            $st->execute([$req['jobId'], $host, $req['environmentId'], $pid, $cid, $req['agent'],
                co_db_s($req, 'mode'), co_db_s($req, 'model'), $json, $sha, $myEnv]);
            $st->close();
        });
    } catch (mysqli_sql_exception $e) {
        if ($e->getCode() === 1062) {
            co_json(409, ['error' => 'exists']);
        }
        throw $e;
    }
    if (!$queued) {
        co_json(403, ['error' => 'chat_trashed', 'detail' => 'The chat is in the trash; restore it first.']);
    }
    return ['queued' => true];
}

// Bridge: take up to `max` queued jobs atomically; also lists claimed jobs whose cancellation was requested.
function db_job_claim(array $in): array
{
    $host = db_id($in, 'host');
    $max = db_int($in, 'max', 1, 0, 10);
    $db = co_db();
    // F12: an executor the owner switched off takes nothing new (cancellations still reach it), nor does a second machine
    // with the id of a registered one (Bridge 1.19.0+ sends its machine; executor_put answered it host_taken)
    $exec = co_exec_row($db, $host);
    if ($exec !== null && co_exec_foreign($db, $exec, $in['machine'] ?? null)) {
        // not even the proof of life (claim_poll): the real owner of the id may be dead, and the Drive fallback and the
        // handover must see that
        return ['jobs' => [], 'cancel' => [], 'hostTaken' => true];
    }
    if ($exec !== null && (int)$exec['enabled'] !== 1) {
        $max = 0;
    }
    $jobs = [];
    if ($max > 0) {
        $db->begin_transaction();
        try {
            $st = $db->prepare(
                "SELECT job_id, request FROM jobs
                  WHERE host_id = ? AND origin = 'db' AND result IS NULL AND cancel_requested = 0
                    AND (state = 'inbox'
                         OR (state = 'claimed' AND claimed_utc < UTC_TIMESTAMP(3) - INTERVAL " . DB_CLAIM_LEASE_SECONDS . " SECOND
                             AND NOT EXISTS (SELECT 1 FROM job_events e WHERE e.job_id = jobs.job_id)))
                  ORDER BY queued_utc
                  LIMIT ?
                  FOR UPDATE SKIP LOCKED"
            );
            $st->bind_param('si', $host, $max);
            $st->execute();
            $res = $st->get_result();
            while ($r = $res->fetch_assoc()) {
                $jobs[] = ['jobId' => $r['job_id'], 'request' => $r['request']];
            }
            $res->free();
            $st->close();
            if ($jobs) {
                $up = $db->prepare("UPDATE jobs SET state = 'claimed', claimed_utc = UTC_TIMESTAMP(3) WHERE job_id = ?");
                foreach ($jobs as $j) {
                    $up->execute([$j['jobId']]);
                }
                $up->close();
            }
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
    }
    $st = $db->prepare(
        "SELECT job_id FROM jobs
          WHERE host_id = ? AND cancel_requested = 1 AND result IS NULL AND state IN ('inbox', 'claimed')
          LIMIT 50"
    );
    $st->execute([$host]);
    $cancel = array_map(static fn(array $r): string => (string)$r[0], $st->get_result()->fetch_all(MYSQLI_NUM));
    $st->close();
    // 2026-11-01: proof of a live Bridge (written only after a successful claim); co_queue_fallback makes no Drive copy
    // for a host polled within 60 s.
    $st = $db->prepare("REPLACE INTO meta (k, v) VALUES (?, DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%d %H:%i:%s'))");
    $st->execute([co_queue_poll_key($host)]);
    $st->close();
    // Schema 13: Bridge 1.7.0+ asks for follow-ups of its running jobs (followup-lib.php)
    if (($in['followups'] ?? false) === true) {
        return ['jobs' => $jobs, 'cancel' => $cancel, 'followups' => followup_offer($db, $host)];
    }
    return ['jobs' => $jobs, 'cancel' => $cancel];
}

// Bridge: event batches of running jobs (any origin), idempotent by (jobId, firstSequence).
function db_job_post_events(array $in): array
{
    $list = $in['batches'] ?? null;
    if (!is_array($list) || !array_is_list($list) || count($list) > 200) {
        throw new InvalidArgumentException('invalid batches');
    }
    $st = co_db()->prepare(
        'INSERT IGNORE INTO job_events (job_id, first_seq, last_seq, data, created_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))'
    );
    $n = 0;
    foreach ($list as $b) {
        $jobId = $b['jobId'] ?? null;
        $batch = $b['batch'] ?? null;
        if (!co_db_id_ok($jobId) || !is_array($batch)
            || !is_int($batch['firstSequence'] ?? null) || !is_int($batch['lastSequence'] ?? null)
            || !is_array($batch['events'] ?? null)) {
            throw new InvalidArgumentException('invalid batch');
        }
        // 2026-10-28: only what the Trace window reads is kept (trace-lib.php).
        $st->execute([$jobId, $batch['firstSequence'], $batch['lastSequence'], co_db_json(co_trace_trim_batch($batch))]);
        $added = max(0, $st->affected_rows);
        $n += $added;
        // schema 18: risky actions read-guard held become confirmation cards (actions-lib.php); a resent batch is skipped
        if ($added > 0) co_actions_from_batch(co_db(), $jobId, $batch);
    }
    $st->close();
    return ['inserted' => $n];
}

// Bridge: the committed result.json of a job (any origin), with its request for jobs the sync has not seen yet.
function db_job_result(array $in): array
{
    $req = $in['request'] ?? null;
    $res = $in['result'] ?? null;
    if (!is_array($req) || !is_array($res)) {
        throw new InvalidArgumentException('request and result required');
    }
    $state = ($res['status'] ?? null) === 'completed' ? 'completed' : 'failed';
    $out = co_db_put(['jobs' => [['hostId' => db_id($in, 'host'), 'state' => $state, 'request' => $req, 'result' => $res]]]);
    // CO Web 0.20.0: the answer goes into the chat now, whether or not a CO Web has the chat open.
    // D 12.11: and the "job finished" push for a host without its own (site agent), once per job
    try {
        $out['answer'] = co_job_finished(db_id($in, 'host'), $req, $res);
    } catch (Throwable $e) {
        error_log('db.php finalize: ' . $e->getMessage());
        $out['answer'] = null;
    }
    return $out;
}

// Bridge 1.8.1+: before it runs a job found in the Drive inbox (queue fallback, attachments). A job the database knows
// as cancelled or finished is not run ('skip'); a queued one becomes claimed here, so that a later job_cancel reaches
// the running job through job_claim. 'run' also for a job the database does not know yet.
function db_job_drive_claim(array $in): array
{
    $jobId = db_id($in, 'jobId');
    $host = db_id($in, 'host');
    $db = co_db();
    $st = $db->prepare("UPDATE jobs SET state = 'claimed', claimed_utc = UTC_TIMESTAMP(3)
                         WHERE job_id = ? AND host_id = ? AND state = 'inbox' AND cancel_requested = 0 AND result IS NULL");
    $st->execute([$jobId, $host]);
    $claimed = $st->affected_rows === 1;
    $st->close();
    if ($claimed) {
        return ['action' => 'run', 'state' => 'claimed'];
    }
    $st = $db->prepare('SELECT state, host_id, cancel_requested, result IS NOT NULL AS has_result FROM jobs WHERE job_id = ?');
    $st->execute([$jobId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row) {
        return ['action' => 'run', 'state' => null];
    }
    $state = (string)$row['state'];
    // S8: a job the server moved to another host (co_queue_handover) runs there only
    if ((string)$row['host_id'] !== $host) {
        return ['action' => 'skip', 'state' => $state];
    }
    $skip = (int)$row['cancel_requested'] === 1 || (int)$row['has_result'] === 1
        || in_array($state, ['cancelled', 'completed', 'failed', 'rejected'], true);
    return ['action' => $skip ? 'skip' : 'run', 'state' => $state];
}

// Bridge: a job it will not run (invalid, duplicate); CO Web shows the reason.
function db_job_reject(array $in): array
{
    $jobId = db_id($in, 'jobId');
    $reason = is_string($in['reason'] ?? null) ? mb_substr($in['reason'], 0, 1000) : 'Rejected by Bridge.';
    $st = co_db()->prepare("UPDATE jobs SET state = 'rejected', rejection = ? WHERE job_id = ? AND result IS NULL");
    $st->execute([$reason, $jobId]);
    $n = $st->affected_rows;
    $st->close();
    return ['updated' => $n];
}

// 2026-10-16 (chat messages live only in the database): the home PC appends an event (schedule runs) and the
// database assigns the next sequence of the chat under a row lock, so numbering stays right without Drive files.
function db_append_event(array $in): array
{
    $ev = $in['event'] ?? null;
    if (!is_array($ev) || !co_db_id_ok($ev['chatId'] ?? null) || !co_db_id_ok($ev['eventId'] ?? null)) {
        throw new InvalidArgumentException('event with chatId and eventId required');
    }
    $db = co_db();
    // Named lock per chat (co_db_put runs its own transaction, so a row lock would not survive it)
    $lock = 'co-append-' . $ev['chatId'];
    $st = $db->prepare('SELECT GET_LOCK(?, 10)');
    $st->execute([$lock]);
    if ((int)$st->get_result()->fetch_row()[0] !== 1) {
        throw new RuntimeException('append lock busy');
    }
    try {
        $st = $db->prepare('SELECT data FROM chats WHERE chat_id = ?');
        $st->execute([$ev['chatId']]);
        $chatRow = $st->get_result()->fetch_assoc();
        if (!$chatRow) {
            throw new InvalidArgumentException('unknown chat');
        }
        $st = $db->prepare('SELECT COALESCE(MAX(seq), 0) + 1 FROM events WHERE chat_id = ?');
        $st->execute([$ev['chatId']]);
        $ev['sequence'] = (int)$st->get_result()->fetch_row()[0];
        $res = co_db_put(['events' => [$ev]]);
        // The chat moves to the top of the list
        $chat = json_decode((string)$chatRow['data'], true);
        if (is_array($chat)) {
            $chat['updatedUtc'] = gmdate('Y-m-d\TH:i:s.v\Z');
            co_db_put(['chats' => [$chat]]);
        }
    } finally {
        $db->prepare('SELECT RELEASE_LOCK(?)')->execute([$lock]);
    }
    return ['sequence' => $ev['sequence'], 'eventId' => $ev['eventId'], 'rejected' => $res['rejected'] ?? []];
}

// ---- Schema 9: project memory candidates (cron/memory-candidates.php proposes, CO Web accepts/rejects) ----

function db_memory_candidates(mysqli $db, string $projectId, int $limit): array
{
    $candidates = [];
    $counts = ['accepted' => 0, 'rejected' => 0];

    $st = $db->prepare(
        "SELECT m.id, m.chat_id, c.title, m.msg_seq, m.tag, m.text, m.day, m.model, m.created_utc, m.kind, m.replaces, m.reason,
                m.target_env
         FROM memory_candidates m
         LEFT JOIN chats c ON c.chat_id = m.chat_id
         WHERE m.project_id = ? AND m.status = 'pending'
         ORDER BY m.id DESC
         LIMIT ?"
    );
    $st->bind_param('si', $projectId, $limit);
    $st->execute();
    $res = $st->get_result();
    while ($row = $res->fetch_assoc()) {
        $candidates[] = [
            'id'         => (int)$row['id'],
            'chatId'     => (string)$row['chat_id'],
            'chatTitle'  => $row['title'] !== null ? (string)$row['title'] : null,
            'msg'        => (int)$row['msg_seq'],
            'tag'        => (string)$row['tag'],
            'text'       => (string)$row['text'],
            'day'        => (string)$row['day'],
            'model'      => $row['model'] !== null ? (string)$row['model'] : null,
            'createdUtc' => (string)$row['created_utc'],
            // schema 14: consolidation proposals (revise/drop) carry the exact original lines
            'kind'       => (string)($row['kind'] ?? 'add'),
            'replaces'   => db_memory_replaces($row['replaces'] ?? null),
            'reason'     => $row['reason'] !== null ? (string)$row['reason'] : null,
            // schema 16: kind rule - tag env|project, targetEnv = whose profile, reason = the verbatim correction
            'targetEnv'  => $row['target_env'] !== null ? (string)$row['target_env'] : null,
        ];
    }
    $st->close();

    $st = $db->prepare(
        "SELECT status, COUNT(*) AS cnt FROM memory_candidates
         WHERE project_id = ? AND status IN ('accepted', 'rejected') GROUP BY status"
    );
    $st->execute([$projectId]);
    $res = $st->get_result();
    while ($row = $res->fetch_assoc()) {
        $counts[(string)$row['status']] = (int)$row['cnt'];
    }
    $st->close();

    return ['candidates' => $candidates, 'counts' => $counts];
}

// Accept or reject a pending candidate. Accept appends "- day: [tag] text" (tag and text possibly edited) to the
// project's memory.md on Drive first, under a per-project named lock, and marks the candidate accepted only after the
// write succeeded; a candidate decided meanwhile in another session is never written.
function db_memory_decide(mysqli $db, int $id, string $action, ?string $text, ?string $tag, string $email): array
{
    if ($action !== 'accept' && $action !== 'reject') {
        throw new InvalidArgumentException('bad_action');
    }
    $st = $db->prepare('SELECT project_id, chat_id, msg_seq, day, status, kind, replaces, text, tag, reason, target_env
                          FROM memory_candidates WHERE id = ?');
    $st->execute([$id]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    $kind = $row ? (string)($row['kind'] ?? 'add') : 'add';
    $clean = '';
    if ($action === 'accept' && $kind === 'drop') {
        $tag = (string)$row['tag'];
    } elseif ($action === 'accept') {
        $clean = $text === null ? null : preg_replace('/\R/u', ' ', $text);
        $clean = $clean === null ? '' : trim(preg_replace('/\s+/u', ' ', $clean) ?? '');
        $len = mb_strlen($clean, 'UTF-8');
        if ($len < 10 || $len > 300) {
            throw new InvalidArgumentException('bad_text');
        }
        // revisions keep the tag of the lines they merge (e.g. todo); new candidates are decision|fact; rules env|project
        $tags = match ($kind) {
            'revise' => ['decision', 'fact', 'todo', (string)($row['tag'] ?? 'fact')],
            'rule'   => ['env', 'project'],
            default  => ['decision', 'fact'],
        };
        if (!in_array($tag, $tags, true)) {
            throw new InvalidArgumentException('bad_tag');
        }
    }

    if (!$row || $row['status'] !== 'pending') {
        return ['ok' => false, 'status' => $row ? (string)$row['status'] : null];
    }
    $projectId = (string)$row['project_id'];

    $lockName = 'aiwb_memory_' . substr(sha1($projectId), 0, 40);
    $st = $db->prepare('SELECT GET_LOCK(?, 30)');
    $st->execute([$lockName]);
    $got = (int)($st->get_result()->fetch_row()[0] ?? 0) === 1;
    $st->close();
    if (!$got) {
        throw new RuntimeException('memory lock busy');
    }
    try {
        // Re-check under the lock (accept and reject both take it): another session may have decided meanwhile.
        $cur = db_memory_status($db, $id);
        if ($cur !== 'pending') {
            return ['ok' => false, 'status' => $cur];
        }
        if ($action === 'reject') {
            $st = $db->prepare(
                "UPDATE memory_candidates SET status = 'rejected', decided_utc = UTC_TIMESTAMP(3), decided_by = ?
                  WHERE id = ? AND status = 'pending'"
            );
            $st->bind_param('si', $email, $id);
            $st->execute();
            $ok = $st->affected_rows === 1;
            $st->close();
            return $ok ? ['ok' => true, 'status' => 'rejected'] : ['ok' => false, 'status' => db_memory_status($db, $id)];
        }
        require_once __DIR__ . '/memory-lib.php';
        if ($kind === 'rule' && $tag === 'env') {
            // schema 16: Reflexes - the environment profile of the person who corrected the agent
            $written = co_rule_profile_append($db, (string)$row['target_env'],
                '- ' . $row['day'] . ': ' . $clean . ' (' . $row['chat_id'] . ' #' . (int)$row['msg_seq'] . ')', $email);
        } elseif ($kind === 'rule') {
            $written = co_memory_append($db, $projectId, (string)$row['day'], 'decision', $clean);
        } elseif ($kind === 'add') {
            $written = co_memory_append($db, $projectId, (string)$row['day'], $tag, $clean);
        } else {
            // schema 14: revise/drop rewrites memory.md; the removed lines go to memory-archive.md
            $replaces = db_memory_replaces($row['replaces'] ?? null);
            $today = gmdate('Y-m-d');
            $newLine = $kind === 'revise' ? '- ' . $row['day'] . ': [' . $tag . '] ' . $clean : null;
            $note = $newLine !== null
                ? 'superseded ' . $today . ' by: ' . mb_substr($newLine, 2, 200, 'UTF-8')
                : 'dropped ' . $today . (($row['reason'] ?? '') !== '' ? ': ' . mb_substr((string)$row['reason'], 0, 200, 'UTF-8') : '');
            co_memory_rewrite($db, $projectId, $replaces, $newLine, $note);
            $written = true;
        }

        $st = $db->prepare(
            "UPDATE memory_candidates SET status = 'accepted', tag = ?, final_text = ?, decided_utc = UTC_TIMESTAMP(3), decided_by = ?
              WHERE id = ? AND status = 'pending'"
        );
        $st->bind_param('sssi', $tag, $clean, $email, $id);
        $st->execute();
        $ok = $st->affected_rows === 1;
        $st->close();
        if (!$ok) {
            return ['ok' => false, 'status' => db_memory_status($db, $id)];
        }
        return ['ok' => true, 'status' => 'accepted', 'text' => $clean, 'written' => $written];
    } finally {
        $st = $db->prepare('SELECT RELEASE_LOCK(?)');
        $st->execute([$lockName]);
        $st->close();
    }
}

/** @return list<string> exact memory.md lines of a revise/drop candidate (schema 14) */
function db_memory_replaces(mixed $json): array
{
    $a = is_string($json) && $json !== '' ? json_decode($json, true) : null;
    return is_array($a) ? array_values(array_filter($a, 'is_string')) : [];
}

function db_memory_status(mysqli $db, int $id): ?string
{
    $st = $db->prepare('SELECT status FROM memory_candidates WHERE id = ?');
    $st->execute([$id]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    return $row ? (string)$row['status'] : null;
}


/** S5 subtask_spawn: the chat folder under <project>/chats with its chat.json (owner's Drive token of the server). */
function db_subtask_drive_chat(string $projectFolder, string $chatId, array $chatDoc): string
{
    require_once __DIR__ . '/drive-lib.php';
    if (preg_match('/^[A-Za-z0-9_-]{10,100}$/', $projectFolder) !== 1) {
        throw new RuntimeException('invalid project folder id');
    }
    $q = "'$projectFolder' in parents and name = 'chats' and trashed = false";
    $found = co_drive_get('files', ['q' => $q, 'fields' => 'files(id)', 'supportsAllDrives' => 'true',
        'includeItemsFromAllDrives' => 'true']);
    $chats = (string)($found['files'][0]['id'] ?? '');
    if ($chats === '') {
        $chats = co_drive_create_folder($projectFolder, 'chats');
    }
    $folder = co_drive_create_folder($chats, $chatId);
    co_drive_create_text($folder, 'chat.json', json_encode($chatDoc, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES
        | JSON_PRETTY_PRINT | JSON_THROW_ON_ERROR), 'application/json');
    return $folder;
}

/** S5: a subtask queued for the site starts the site agent at once (as ping.php does), not when CO Web pings next. */
function db_subtask_kick(array $res): array
{
    if (($res['ok'] ?? false) !== true || ($res['host'] ?? '') !== 'site') {
        return $res;
    }
    if (co_site_agent_kick()) {
        $res['siteAgentStarted'] = true;
    }
    return $res;
}

try {
    // Body: reject oversize early, then read at most limit + 1 bytes.
    if ((int)($_SERVER['CONTENT_LENGTH'] ?? 0) > DB_MAX_BODY) {
        co_json(413, ['error' => 'too_large']);
    }
    $raw = file_get_contents('php://input', false, null, 0, DB_MAX_BODY + 1);
    if ($raw === false) {
        $raw = '';
    }
    if (strlen($raw) > DB_MAX_BODY) {
        co_json(413, ['error' => 'too_large']);
    }
    $in = json_decode($raw, true);
    if (!is_array($in)) {
        co_json(400, ['error' => 'bad_json']);
    }
    // Host calls may wrap the body as {"gz": base64(gzip(json))}: the hosting WAF blocks plain bodies whose chat text
    // contains code, SQL or PHP snippets (403 before PHP runs).
    if (is_string($in['gz'] ?? null)) {
        $bin = base64_decode($in['gz'], true);
        $json = $bin === false ? false : @gzdecode($bin, 4 * DB_MAX_BODY);
        $in = $json === false ? null : json_decode($json, true);
        if (!is_array($in)) {
            co_json(400, ['error' => 'bad_gz']);
        }
        if (!in_array($in['op'] ?? null, ['put', 'stats', 'shas', 'put_user', 'delete_chat',
                'job_submit', 'job_post_events', 'job_result', 'append_event', 'profile_put', 'templates_put', 'chat_read', 'memory_decide', 'tool_decide',
                'webdev_register', 'webdev_heartbeat', 'webdev_result', 'webdev_call_put',
                'relay_devices_put', 'relay_call_put', 'relay_result', 'sandbox_put', 'executor_put', 'executors_host',
                'model_register', 'model_unregister', 'models_host', 'modules_host', 'subtask_spawn', 'subtask_poll', 'model_set',
                'files_list', 'files_set', 'files_merge', 'files_branch', 'files_restored', 'files_undone',
            'followup_add', 'followup_list', 'followup_take', 'followup_done', 'followup_cancel', 'host_event',
                'related', 'dossier_put', 'dossier_host', 'find_chats', 'family_chats', 'family_post', 'app_list', 'app_action', 'app_action_status'], true)
            // Freedom F10: host-key ops of enabled modules may come gzipped too (an indexer posting document text)
            && !(is_string($in['op'] ?? null) && preg_match(CO_MODULE_OP_RE, $in['op']) && co_module_for_op(co_db(), 'host', $in['op']) !== null)) {
            co_json(400, ['error' => 'gz_ops_only']);
        }
    }
    $op = is_string($in['op'] ?? null) ? $in['op'] : '';

    // ---- Host ops (push key) ----
    if (in_array($op, ['put', 'stats', 'shas', 'append_event'], true)) {
        co_db_host_auth();
        match ($op) {
            'put'          => co_json(200, co_db_put($in)),
            'stats'        => co_json(200, co_db_stats()),
            'shas'         => co_json(200, co_db_shas($in)),
            'append_event' => co_json(200, db_append_event($in)),
        };
    }
    if ($op === 'chat_read') {
        co_db_host_auth();
        co_json(200, db_chat_read($in));
    }
    // Schema 20 (search-lib.php): chat search for aiwb-chat find. Freedom F10: the indexers of search provider modules
    // write through their own host ops (manifest server.ops.host)
    if ($op === 'find_chats') {
        co_db_host_auth();
        co_json(200, co_search_find(co_db(), $in));
    }
    // 09.11 (family-lib.php): agents post into family chats as AI Workbench (aiwb-chat family / post)
    if ($op === 'family_chats' || $op === 'family_post') {
        co_db_host_auth();
        co_json(200, $op === 'family_post' ? co_family_post(co_db(), $in)
            : ['chats' => co_family_list(co_db(), is_string($in['env'] ?? null) && $in['env'] !== '' ? $in['env'] : co_owner_env())]);
    }
    if (in_array($op, ['job_claim', 'job_drive_claim', 'job_post_events', 'job_result', 'job_reject', 'followup_ack'], true)) {
        co_db_host_auth();
        match ($op) {
            'job_drive_claim' => co_json(200, db_job_drive_claim($in)),
            'followup_ack'    => co_json(200, followup_ack(co_db(), $in)),
            'job_claim'       => co_json(200, db_job_claim($in)),
            'job_post_events' => co_json(200, db_job_post_events($in)),
            'job_result'      => co_json(200, db_job_result($in)),
            'job_reject'      => co_json(200, db_job_reject($in)),
        };
    }
    // Schema 28 (executors-lib.php, Freedom F12): a Bridge registers itself and beats every 30 s
    if ($op === 'executor_put' || $op === 'executors_host') {
        co_db_host_auth();
        try {
            if ($op === 'executors_host') {
                co_json(200, co_exec_list(co_db(), is_string($in['env'] ?? null) && $in['env'] !== '' ? $in['env'] : co_owner_env(), true));
            }
            $res = co_exec_put(co_db(), $in);
            co_json(($res['error'] ?? '') === 'host_taken' ? 409 : 200, $res);
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }
    // Schema 17 (failures-lib.php): events of the Bridge supervisor (launcher watchdog, restart ladder)
    if ($op === 'host_event') {
        co_db_host_auth();
        try {
            co_json(200, co_host_event_put(co_db(), $in));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }
    // Schema 19 (dossier-lib.php): agents (aiwb-dossier) and the first pass read and change the dossier with the host key
    if ($op === 'dossier_host') {
        co_db_host_auth();
        try {
            co_json(200, co_dossier_host(co_db(), $in));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }
    // CO Web 0.87.0 (browser-lib.php): Bridge reads the agent browser settings of a job (tabs per chat, tab lifetimes)
    if ($op === 'browser_settings') {
        co_db_host_auth();
        try {
            co_json(200, co_browser_host(co_db(), $in));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }
    // Schema 11 (web device, lab/web-device/SPEC.md): Bridge side of the CO Web file device.
    if (in_array($op, ['webdev_list', 'webdev_call_put', 'webdev_call_get'], true)) {
        co_db_host_auth();
        match ($op) {
            'webdev_list'     => co_json(200, webdev_list(co_db(), $in)),
            'webdev_call_put' => co_json(200, webdev_call_put(co_db(), $in)),
            'webdev_call_get' => co_json(200, webdev_call_get(co_db(), $in)),
        };
    }

    // Schema 22 (relay-lib.php, S1): the site agent calls tools of home devices; the Bridge serves the calls.
    // Schema 24: relay_token - the one-time key of a site job for relay-mcp.php (MCP over HTTP instead of relay-mcp.mjs)
    if (in_array($op, ['relay_devices_put', 'relay_devices', 'relay_call_put', 'relay_call_get', 'relay_claim', 'relay_result', 'relay_cancel', 'relay_token', 'sandbox_put', 'sandbox_list'], true)) {
        co_db_host_auth();
        try {
            $db = co_db();
            co_json(200, match ($op) {
                'relay_devices_put' => relay_devices_put($db, $in),
                'relay_devices'     => relay_devices($db, $in),
                'relay_call_put'    => relay_call_put($db, $in),
                'relay_call_get'    => relay_call_get($db, $in),
                'relay_claim'       => relay_claim($db, $in),
                'relay_result'      => relay_result($db, $in),
                'relay_cancel'      => relay_cancel($db, $in),
                'relay_token'       => relay_token($db, $in),
                'sandbox_put'       => sandbox_put($db, $in),     // schema 26 (sandbox-lib.php)
                'sandbox_list'      => sandbox_list($db, $in),
            });
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }

    // Schema 23 (models-lib.php, S5): model resources and subtasks on them (aiwb-subtask on the home PC and the site,
    // devices registering a subscription). The resource's owner allows slaves in CO Web (models_list / model_set).
    // Schema 27 (modules-lib.php, Freedom F10): install/freedom_modules.py registers, updates, switches and removes modules
    if ($op === 'modules_host') {
        co_db_host_auth();
        try {
            co_json(200, co_modules_host(co_db(), $in));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }

    // F14b-1 (apps-lib.php, SPEC §3.6a item 5): actions of apps for an agent job (core tool aiwb-apps): the job's own env
    // F14b-2: an outward action becomes a card for the person; app_action_status tells the agent its outcome
    if ($op === 'app_list' || $op === 'app_action' || $op === 'app_action_status') {
        co_db_host_auth();
        try {
            co_json(200, match ($op) {
                'app_list' => co_app_list(co_db(), $in),
                'app_action' => co_app_action(co_db(), $in),
                'app_action_status' => co_app_action_status(co_db(), $in),
            });
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }

    if (in_array($op, ['models_host', 'model_register', 'model_unregister', 'subtask_spawn', 'subtask_poll'], true)) {
        co_db_host_auth();
        try {
            $db = co_db();
            co_json(200, match ($op) {
                'models_host'      => models_list($db, models_env_of($db, $in)),
                'model_register'   => models_put($db, $in, 'device', null, true),
                'model_unregister' => models_delete($db, $in, null, true),
                'subtask_poll'     => models_subtask_poll($db, $in),
                'subtask_spawn'    => db_subtask_kick(models_subtask_spawn($db, $in, 'db_subtask_drive_chat',
                    static fn(array $ev): array => db_append_event(['event' => $ev]))),
            });
        } catch (InvalidArgumentException $e) {
            co_json(400, ['error' => $e->getMessage()]);
        }
    }

    $coreOps = ['projects', 'chats', 'events', 'search', 'search_chats', 'put_user', 'delete_chat',
            'job_submit', 'job_get', 'job_events', 'job_cancel', 'msg_delete', 'msg_rewrite', 'msg_restore', 'job_stats', 'usage', 'search_items', 'profile_get', 'profile_put', 'templates_get', 'templates_put', 'activity', 'protocol',
            'memory_candidates', 'memory_decide', 'ops', 'health', 'host_light', 'shares_get', 'share_set',
            'webdev_register', 'webdev_heartbeat', 'webdev_claim', 'webdev_result', 'webdev_revoke', 'webdev_mine',
            'files_list', 'files_set', 'files_merge', 'files_branch', 'files_restored', 'files_undone',
            'followup_add', 'followup_list', 'followup_take', 'followup_done', 'followup_cancel',
            'inbox', 'inbox_mark', 'action_decide', 'autoswitch', 'tool_candidates', 'tool_decide', 'executors', 'executor_set',
            'related', 'dossier_context', 'dossier_get', 'dossier_put', 'browser_defaults', 'family_state', 'family_read', 'members',
            'models_list', 'model_set', 'model_delete', 'subs_put', 'modules_list', 'module_set', 'module_person',
            'app_list', 'app_action', 'app_action_status', 'app_events', 'app_request_decide', 'app_notify', 'app_notify_set', 'app_data', 'module_chat'];
    // Schema 27 (modules-lib.php, Freedom F10): an op the core does not know may belong to an enabled module (manifest
    // server.ops.user); core names always win
    $moduleOp = !in_array($op, $coreOps, true);
    // host-key ops of enabled modules (manifest server.ops.host): after every host op of the core, never a core user op
    if ($moduleOp && preg_match(CO_MODULE_OP_RE, $op) && co_module_for_op(co_db(), 'host', $op) !== null) {
        co_db_host_auth();
        co_json(200, co_module_call(co_db(), 'host', $op, $in) ?? ['error' => 'unknown_op']);
    }
    if ($moduleOp && co_module_for_op(co_db(), 'user', $op) === null) {
        co_json(400, ['error' => 'unknown_op']);
    }

    // ---- User ops (session / bearer) ----
    co_require_api_call();
    $email = co_agent_email(co_open($_COOKIE[CO_SESSION_COOKIE] ?? null));
    $users = co_push_users();
    if (!isset($users[$email])) {
        co_json(403, ['error' => 'forbidden']);
    }
    $myEnv = $users[$email];
    $owner = co_is_owner_env($myEnv);
    $envAllowed = static fn(mixed $env): bool =>
        is_string($env) && $env !== '' && ($owner || $env === $myEnv);

    $db = co_db();
    // 0.45.0 (schema 10): roles on shared projects and chats
    $acc = ['db' => $db, 'myEnv' => $myEnv, 'admin' => $owner, 'email' => $email];
    $projRole = fn(string $pid): ?string => share_project_role($db, $myEnv, $owner, $pid);
    $chatRole = fn(string $cid): ?string => share_chat_role($db, $myEnv, $owner, $cid);

    // Schema 19: the chat and project a dossier/hints call is about. An existing chat decides the project (its row, not
    // the client's projectId) and needs read access; a chat not in the database yet (first message of a new chat) needs
    // read access to the project. Returns [chatId, projectId, project env].
    $dossierTarget = function (array $in) use ($db, $projRole, $chatRole): array {
        $chatId = db_id($in, 'chatId');
        $st = $db->prepare('SELECT c.project_id, p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
        $st->execute([$chatId]);
        $row = $st->get_result()->fetch_assoc();
        if ($row) {
            if (!share_can($chatRole($chatId), 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }
            return [$chatId, (string)$row['project_id'], (string)$row['env']];
        }
        $projectId = db_id($in, 'projectId');
        if (!share_can($projRole($projectId), 'read')) {
            co_json(403, ['error' => 'forbidden']);
        }
        $st = $db->prepare('SELECT env FROM projects WHERE project_id = ?');
        $st->execute([$projectId]);
        $pEnv = (string)($st->get_result()->fetch_row()[0] ?? '');
        if ($pEnv === '') {
            co_json(404, ['error' => 'not_found']);
        }
        return [$chatId, $projectId, $pEnv];
    };

    // 0.90.0 (family-lib.php): the family room of the right-panel tab and its unread count, per environment
    if ($op === 'family_state') {
        co_json(200, co_family_state($db, $myEnv, $email));
    }
    // Schema 27 (modules-lib.php, Freedom F10, SPEC §3.6): modules - list, switch on/off (owner), the person's own switch,
    // and the user ops of enabled modules (their interface in CO Web calls them through the ext/1 bridge)
    if ($op === 'modules_list') {
        // gateways (gateway-lib.php): what installed modules with provides.gateway describe, enabled or not
        co_json(200, co_modules_list($db, $myEnv, $owner, $coreOps) + ['gateways' => co_gateway_wire()]);
    }
    if ($op === 'module_set') {
        if (!$owner) co_json(403, ['error' => 'forbidden']);
        co_json(200, co_module_set($db, co_module_in_str($in, 'id', 64), !empty($in['enabled']), $myEnv));
    }
    if ($op === 'module_person') {
        $env = isset($in['env']) ? co_module_in_str($in, 'env', 64) : $myEnv;
        if ($env !== $myEnv && !$owner) co_json(403, ['error' => 'forbidden']);
        co_json(200, co_module_person_set($db, co_module_in_str($in, 'id', 64), $env, co_module_in_str($in, 'mode', 8), $myEnv));
    }
    // F14b-1: pages of apps refresh after an agent's action (CO Web polls while an app is open)
    if ($op === 'app_events') {
        co_json(200, co_app_events($db, $myEnv, db_int($in, 'since', -1, -1, PHP_INT_MAX)));
    }
    // F14b-2: Run / Decline of an outward card in "Waiting for you"; only cards of the caller's own env exist for them
    if ($op === 'app_request_decide') {
        co_json(200, co_app_request_decide($db, $myEnv, $owner, $email, $in));
    }
    // F14b-3: a module page notifies its own person (ext/1 coExt.notify; CO Web checks notify.push, so does the server);
    // the person switches a module's notifications on and off (Settings -> the module)
    if ($op === 'app_notify') {
        try {
            co_json(200, co_app_notify($db, co_module_in_str($in, 'module', 64), $myEnv,
                ['title' => $in['title'] ?? null, 'body' => $in['body'] ?? null, 'tag' => $in['tag'] ?? '', 'open' => $in['open'] ?? null], 'page'));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['ok' => false, 'error' => 'bad_request', 'message' => $e->getMessage()]);
        }
    }
    // F14c: data an app keeps for its person through the core (ext/1 coExt.data, permission data.env; apps-lib.php)
    if ($op === 'module_chat') {
        require_once __DIR__ . '/module-chat-lib.php';
        // A core user operation: the environment comes from authentication, never from the request.
        co_json(200, co_module_chat_open($db, $myEnv, co_module_in_str($in, 'module', 64)));
    }
    if ($op === 'app_data') {
        try {
            co_json(200, co_app_data($db, co_module_in_str($in, 'module', 64), $myEnv, co_module_in_str($in, 'action', 8),
                ['key' => $in['key'] ?? null, 'value' => $in['value'] ?? null, 'prefix' => $in['prefix'] ?? '']));
        } catch (InvalidArgumentException $e) {
            co_json(400, ['ok' => false, 'error' => 'bad_request', 'message' => $e->getMessage()]);
        }
    }
    if ($op === 'app_notify_set') {
        co_json(200, co_app_notify_set($db, $myEnv, co_module_in_str($in, 'id', 64), !empty($in['on'])));
    }
    if ($moduleOp) {
        co_json(200, co_module_call($db, 'user', $op, $in, $myEnv, $owner, $email) ?? ['error' => 'unknown_op']);
    }
    if ($op === 'family_read') {
        co_json(200, co_family_mark_read($db, $myEnv, db_id($in, 'chatId'), db_int($in, 'seq', 0, 0, 100000000)));
    }

    switch ($op) {
        case 'projects':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $st = $db->prepare(
                'SELECT project_id, title, archived, updated_utc, data, folder_id
                 FROM projects WHERE env = ? ORDER BY updated_utc DESC'
            );
            $st->execute([$env]);
            $projects = db_decode_rows($st->get_result());

            // projects shared to this env (the owner sees them in another env's tab too)
            $shared = [];
            if ($envAllowed($env)) {
                $byId = [];
                foreach (share_shared_projects($db, $env) as $s) {
                    $byId[(string)$s['projectId']] = $s;
                }
                if ($byId) {
                    $ids = array_map('strval', array_keys($byId));
                    $ph = implode(',', array_fill(0, count($ids), '?'));
                    $st = $db->prepare("SELECT data, folder_id FROM projects WHERE project_id IN ($ph)");
                    $st->execute($ids);
                    foreach (db_decode_rows($st->get_result()) as $doc) {
                        $s = $byId[(string)($doc['projectId'] ?? '')] ?? null;
                        if ($s === null) {
                            continue;
                        }
                        $doc['shareRole'] = $s['role'];
                        $doc['shareVia'] = $s['via'];
                        $doc['sharedChatIds'] = $s['chatIds'];
                        $shared[] = $doc;
                    }
                }
            }
            co_json(200, ['projects' => $projects, 'shared' => $shared]);

        case 'chats':
            $projectId = db_id($in, 'projectId');
            $limit = db_int($in, 'limit', 300, 1, 500);

            if (share_project_env($db, $projectId) === null) {
                co_json(404, ['error' => 'not_found']);
            }

            // 0.48.0: admin preview of another env computes everything as that env (non-admin)
            $viewEnv = $myEnv;
            $viewAdmin = $owner;
            $reqEnv = $in['env'] ?? null;
            if ($owner && is_string($reqEnv) && $reqEnv !== '' && $reqEnv !== $myEnv) {
                $viewEnv = $reqEnv;
                $viewAdmin = false;
            }

            // full project access
            $role = share_project_role($db, $viewEnv, $viewAdmin, $projectId);
            if (share_can($role, 'read')) {
                $st = $db->prepare(
                    'SELECT data, folder_id, summary FROM chats WHERE project_id = ?
                     ORDER BY updated_utc DESC LIMIT ?'
                );
                $st->bind_param('si', $projectId, $limit);
                $st->execute();
                $chats = db_decode_rows($st->get_result());
                // 0.45.0: project viewers keep chat-level editor shares
                if ($role === 'viewer') {
                    $ed = [];
                    foreach (share_q($db, "SELECT target_id FROM shares WHERE kind = 'chat' AND env = ? AND role = 'editor'", [$viewEnv]) as $r) {
                        $ed[(string)$r['target_id']] = true;
                    }
                    foreach ($chats as &$doc) {
                        if (is_array($doc) && isset($ed[(string)($doc['chatId'] ?? '')])) $doc['shareRole'] = 'editor';
                    }
                    unset($doc);
                }
                co_json(200, ['chats' => $chats, 'role' => $role]);
            }

            // best role among chats shared to my env, plus role per chat
            $st = $db->prepare(
                "SELECT c.chat_id, s.role FROM chats c
                 JOIN shares s ON s.kind = 'chat' AND s.target_id = c.chat_id AND s.env = ?
                 WHERE c.project_id = ?"
            );
            $st->bind_param('ss', $viewEnv, $projectId);
            $st->execute();
            $best = null;
            $roleByChat = [];
            foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
                $cidKey = (string)$r['chat_id'];
                if (share_rank($r['role']) > share_rank($roleByChat[$cidKey] ?? null)) {
                    $roleByChat[$cidKey] = $r['role'];
                }
                if (share_rank($r['role']) > share_rank($best)) {
                    $best = $r['role'];
                }
            }
            if (!share_can($best, 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }

            // shared chats only
            $st = $db->prepare(
                "SELECT c.data, c.folder_id, c.summary FROM chats c
                 JOIN shares s ON s.kind = 'chat' AND s.target_id = c.chat_id AND s.env = ?
                 WHERE c.project_id = ?
                 ORDER BY c.updated_utc DESC LIMIT ?"
            );
            $st->bind_param('ssi', $viewEnv, $projectId, $limit);
            $st->execute();
            // each chat carries its own share role
            $chats = [];
            foreach (db_decode_rows($st->get_result()) as $doc) {
                if (is_array($doc)) {
                    $doc['shareRole'] = $roleByChat[(string)($doc['chatId'] ?? '')] ?? null;
                }
                $chats[] = $doc;
            }
            co_json(200, ['chats' => $chats, 'role' => $best]);

        case 'events':
            $chatId = db_id($in, 'chatId');
            $afterSeq = db_int($in, 'afterSeq', 0, PHP_INT_MIN, PHP_INT_MAX);

            $st = $db->prepare('SELECT 1 FROM chats WHERE chat_id = ?');
            $st->execute([$chatId]);
            if (!$st->get_result()->fetch_row()) {
                co_json(404, ['error' => 'not_found']);
            }
            if (!share_can($chatRole($chatId), 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }

            $st = $db->prepare(
                'SELECT data FROM events WHERE chat_id = ? AND seq > ?
                 ORDER BY seq, event_id'
            );
            $st->bind_param('si', $chatId, $afterSeq);
            $st->execute();
            co_json(200, ['events' => db_decode_rows($st->get_result())]);

        // 2026-10-17 (schema 8): the chat protocol kept by cron/protocol.php, shown next to the messages.
        case 'protocol':
            $chatId = db_id($in, 'chatId');
            $st = $db->prepare('SELECT p.env, c.protocol_seq, c.protocol_utc, c.protocol_error, c.protocol_model FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
            $st->execute([$chatId]);
            $row = $st->get_result()->fetch_assoc();
            if (!$row) {
                co_json(404, ['error' => 'not_found']);
            }
            if (!share_can($chatRole($chatId), 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }

            $st = $db->prepare('SELECT entry_no, section, text, quote, msg_seq, status, replaced_by, closed_seq, author, model, created_utc FROM protocol WHERE chat_id = ? ORDER BY entry_no');
            $st->execute([$chatId]);
            $res = $st->get_result();
            $entries = [];
            while ($r = $res->fetch_assoc()) {
                $entries[] = [
                    'no' => (int)$r['entry_no'],
                    'section' => (string)$r['section'],
                    'text' => (string)$r['text'],
                    'quote' => $r['quote'] === null ? null : (string)$r['quote'],
                    'msg' => (int)$r['msg_seq'],
                    'status' => (string)$r['status'],
                    'replacedBy' => $r['replaced_by'] === null ? null : (int)$r['replaced_by'],
                    'closedSeq' => $r['closed_seq'] === null ? null : (int)$r['closed_seq'],
                    'author' => (string)$r['author'],
                    'model' => $r['model'] === null ? null : (string)$r['model'],
                    'createdUtc' => (string)$r['created_utc'],
                ];
            }

            co_json(200, [
                'entries' => $entries,
                'state' => [
                    'seq' => $row['protocol_seq'] === null ? null : (int)$row['protocol_seq'],
                    'utc' => $row['protocol_utc'] === null ? null : (string)$row['protocol_utc'],
                    'error' => $row['protocol_error'] === null ? null : (string)$row['protocol_error'],
                    'model' => $row['protocol_model'] === null ? null : (string)$row['protocol_model'],
                ],
            ]);

        // 2026-10-18 (schema 9): project memory candidates proposed by the protocol secretary.
        case 'memory_candidates':
            $projectId = db_id($in, 'projectId');
            $st = $db->prepare('SELECT env FROM projects WHERE project_id = ?');
            $st->execute([$projectId]);
            $row = $st->get_result()->fetch_assoc();
            if (!$row) {
                co_json(404, ['error' => 'not_found']);
            }
            if (!share_can($projRole($projectId), 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, db_memory_candidates($db, $projectId, db_int($in, 'limit', 100, 1, 300)));

        case 'memory_decide':
            $id = db_int($in, 'id', 0, 1, PHP_INT_MAX);
            $st = $db->prepare('SELECT p.env, m.project_id FROM memory_candidates m JOIN projects p ON p.project_id = m.project_id WHERE m.id = ?');
            $st->execute([$id]);
            $row = $st->get_result()->fetch_assoc();
            if (!$row) {
                co_json(404, ['error' => 'not_found']);
            }
            // deciding needs write (editors and owners)
            if (!share_can($projRole((string)$row['project_id']), 'write')) {
                co_json(403, ['error' => 'forbidden']);
            }
            // schema 16: a rule goes into an environment profile only by that environment (or the owner)
            if (($in['action'] ?? '') === 'accept' && ($in['tag'] ?? '') === 'env') {
                $st = $db->prepare("SELECT target_env FROM memory_candidates WHERE id = ? AND kind = 'rule'");
                $st->execute([$id]);
                $te = $st->get_result()->fetch_row()[0] ?? null;
                if ($te !== null && !$envAllowed((string)$te)) {
                    co_json(403, ['error' => 'forbidden', 'detail' => 'only ' . $te . ' can add a rule to its profile']);
                }
            }
            try {
                $r = db_memory_decide($db, $id, is_string($in['action'] ?? null) ? $in['action'] : '',
                    is_string($in['text'] ?? null) ? $in['text'] : null, is_string($in['tag'] ?? null) ? $in['tag'] : null, $email);
            } catch (CoMemoryChanged $e) {
                co_json(409, ['error' => 'memory_changed', 'detail' => mb_substr($e->getMessage(), 0, 300)]);
            } catch (RuntimeException $e) {
                error_log('memory_decide ' . $id . ': ' . $e->getMessage());
                co_json(502, ['error' => 'memory_write_failed', 'detail' => mb_substr($e->getMessage(), 0, 200)]);
            }
            if (!$r['ok']) {
                co_json(409, ['error' => 'already_decided', 'status' => $r['status']]);
            }
            co_json(200, $r);

        case 'search':
            $q = is_string($in['q'] ?? null) ? trim($in['q']) : '';
            $qLen = mb_strlen($q);
            if ($qLen < 2 || $qLen > 200) {
                throw new InvalidArgumentException('q must be 2..200 characters');
            }
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $limit = db_int($in, 'limit', 20, 1, 50);
            $ftq = co_fts_query($q);
            if ($ftq === '') {
                co_json(200, ['hits' => []]);
            }

            $st = $db->prepare(
                'SELECT e.chat_id, c.project_id, c.title, e.seq, e.text,
                        ' . co_fts_score_sql('MATCH(e.text) AGAINST(? IN BOOLEAN MODE)', 'e.text') . ' AS score
                 FROM events e
                 JOIN chats c ON c.chat_id = e.chat_id
                 JOIN projects p ON p.project_id = c.project_id
                 WHERE ' . share_visible_sql('p', 'c') . " AND MATCH(e.text) AGAINST(? IN BOOLEAN MODE) AND JSON_EXTRACT(e.data, '$.deleted') IS NULL
                 ORDER BY score DESC LIMIT ?"
            );
            $st->bind_param('sssssi', $ftq, $env, $env, $env, $ftq, $limit);
            $st->execute();
            $res = $st->get_result();

            $hits = [];
            while ($r = $res->fetch_assoc()) {
                $hits[] = [
                    'chatId'    => $r['chat_id'],
                    'projectId' => $r['project_id'],
                    'title'     => $r['title'],
                    'seq'       => (int)$r['seq'],
                    'snippet'   => db_snippet((string)($r['text'] ?? ''), $q),
                ];
            }
            $res->free();
            co_json(200, ['hits' => $hits]);

        // Schema 19 (dossier-lib.php): secretary hints for a message - protocol entries of other chats the sender can see
        // and rows of the project owner's dossier (people only when the project or chat is shared)
        case 'related':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            [$chatId, $projectId, $pEnv] = $dossierTarget($in);
            $text = is_string($in['text'] ?? null) ? $in['text'] : '';
            $peopleOnly = $pEnv !== $myEnv || co_dossier_shared($db, $projectId, $chatId);
            // review: the agent reads the hints and may quote them, so a shared destination (any project or chat share,
            // or another env's project) gets no protocol hints at all, only the people rows of the owner's dossier
            // C0J6 (26.11): only entries of chats of the same project - other projects (personal, the workbench's own
            // development) leaked into unrelated chats and fed guesses
            $r = co_related($db, (string)$env, $text, $chatId, $peopleOnly, 5, $peopleOnly ? '' : $projectId);
            if ($pEnv !== (string)$env) {
                // the dossier part belongs to the project owner
                $r['dossier'] = $pEnv !== '' ? co_related_dossier($db, $pEnv, $text, true) : [];
            }
            co_json(200, $r);

        // Freedom F1 (schema 21): who is in this workbench - CO Web fills its environment list and colors from it
        case 'members':
            co_json(200, ['members' => array_map(static fn(array $m): array => ['env' => $m['env'], 'email' => $m['email'],
                'name' => $m['name'], 'color' => $m['color'], 'role' => $m['role']], co_members()),
                'install' => ['driveRoot' => co_drive_root(), 'assistantName' => co_assistant_name(), 'assistantCalls' => co_assistant_calls()]]);

        case 'dossier_context':
            [$chatId, $projectId, $pEnv] = $dossierTarget($in);
            $peopleOnly = $pEnv !== $myEnv || co_dossier_shared($db, $projectId, $chatId);
            // C0J6 (26.11): with the first message of the session the block is smaller and its rows on that topic come first
            $topic = is_string($in['text'] ?? null) ? mb_substr($in['text'], 0, 1500, 'UTF-8') : '';
            co_json(200, ['text' => co_dossier_context($db, $pEnv, $peopleOnly, $topic !== '' ? CO_DOSSIER_TOPIC_MAX : CO_DOSSIER_CONTEXT_MAX, $topic),
                'peopleOnly' => $peopleOnly]);

        case 'dossier_get':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $q = is_string($in['q'] ?? null) ? trim($in['q']) : '';
            co_json(200, ['rows' => $q !== '' ? co_dossier_search($db, (string)$env, $q, 100)
                : co_dossier_list($db, (string)$env, !empty($in['all']))]);

        case 'dossier_put':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            try {
                co_json(200, co_dossier_host($db, ['env' => $env, 'author' => 'user', 'by' => $email] + $in));
            } catch (InvalidArgumentException $e) {
                co_json(400, ['error' => $e->getMessage()]);
            }

        case 'put_user':
            try {
                co_json(200, db_put_user($in, $envAllowed, $acc));
            } catch (RuntimeException $e) {
                if ($e->getMessage() !== 'forbidden') {
                    throw $e;
                }
                co_json(403, ['error' => 'forbidden']);
            }

        case 'delete_chat':
            // CO Web 0.18.0: a chat moved to the Drive trash leaves the database at once.
            $chatId = db_id($in, 'chatId');
            $st = $db->prepare('SELECT p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
            $st->execute([$chatId]);
            $row = $st->get_result()->fetch_assoc();
            if (!$row) {
                co_json(200, ['deleted' => 0]);
            }
            // 0.45.0: deleting needs manage on the chat's project
            if (!share_can($projRole((string)share_chat_project($db, $chatId)), 'manage')) {
                co_json(403, ['error' => 'forbidden']);
            }
            $db->begin_transaction();
            // F15.2: the 30-day trash clean-up deletes only a chat still trashed at the same moment (not restored since)
            // and with no job still queued or running (job_submit refuses a trashed chat under the same row lock)
            if (array_key_exists('ifTrashedUtc', $in)) {
                $want = (string)$in['ifTrashedUtc'];
                $now = share_chat_trashed($db, $chatId, true);
                if ($now === null) {
                    // gone meanwhile (another tab's clean-up or Delete forever): 'missing' for the client
                    $db->rollback();
                    co_json(200, ['deleted' => 0]);
                }
                if ($want === '' || $now !== $want || share_chat_active_jobs($db, $chatId) > 0) {
                    $db->rollback();
                    co_json(200, ['deleted' => 0, 'kept' => 1]);
                }
            }
            $db->prepare("DELETE FROM shares WHERE kind = 'chat' AND target_id = ?")->execute([$chatId]);
            $db->prepare('DELETE FROM events WHERE chat_id = ?')->execute([$chatId]);
            $db->prepare('DELETE FROM protocol WHERE chat_id = ?')->execute([$chatId]);
            $db->prepare('DELETE FROM chats WHERE chat_id = ?')->execute([$chatId]);
            $db->commit();
            co_json(200, ['deleted' => 1]);

        // CO Web 0.134.0 (msgedit-lib.php): delete a message (with its answer, or the answer only), rewrite from a message
        // (everything from it on is forgotten), restore a deletion
        case 'msg_delete':
        case 'msg_rewrite':
        case 'msg_restore':
            $chatId = db_id($in, 'chatId');
            $role = $chatRole($chatId);
            if ($role === null) {
                co_json(404, ['error' => 'not_found']);
            }
            if ($op === 'msg_restore') {
                $r = msgedit_restore($db, $chatId, is_string($in['batch'] ?? null) ? $in['batch'] : '', $role, $email);
            } else {
                $mode = $op === 'msg_rewrite' ? 'rewrite' : (($in['mode'] ?? '') === 'answer' ? 'answer' : 'pair');
                $r = msgedit_hide($db, $chatId, db_int($in, 'seq', 0, 0, PHP_INT_MAX), $mode, $role, $email, $myEnv);
            }
            co_json(isset($r['error']) ? ($r['error'] === 'forbidden' || $r['error'] === 'not_yours' ? 403 : ($r['error'] === 'expired' ? 410 : 404)) : 200, $r);

        case 'usage':
            // CO Web 0.23.0 Host tab: finished jobs per project and agent (tokens and cost from result.usage).
            // Schema 15: from job_stats with the API-equivalent (apiUsd, Codex priced by meta.api_prices); group 'chat'
            // with projectId = the chats of one project. The old query stays as the fallback until job_stats exists.
            $days = db_int($in, 'days', 30, 1, 365);
            $byChat = ($in['group'] ?? '') === 'chat';
            try {
                $args = [$days];
                $sql = 'SELECT s.env, s.project_id, COALESCE(p.title, s.project_id) AS title, s.agent'
                    . ($byChat ? ', s.chat_id, c.title AS chat_title' : '') . ",
                           COUNT(*) AS jobs, SUM(s.outcome <> 'completed') AS failed, SUM(s.input_tokens) AS input,
                           SUM(s.cached_tokens) AS cached, SUM(s.output_tokens) AS output, SUM(s.cost_usd) AS cost,
                           SUM(s.api_usd) AS api, SUM(s.tool_calls) AS calls, SUM(s.tool_errors) AS errs, SUM(s.duration_ms) AS ms
                      FROM job_stats s LEFT JOIN projects p ON p.project_id = s.project_id"
                    . ($byChat ? ' LEFT JOIN chats c ON c.chat_id = s.chat_id' : '')
                    . ' WHERE s.completed_utc >= UTC_TIMESTAMP() - INTERVAL ? DAY';
                if ($byChat) {
                    $sql .= ' AND s.project_id = ?';
                    $args[] = db_id($in, 'projectId');
                }
                if (!$owner) {
                    $sql .= ' AND s.env = ?';
                    $args[] = $myEnv;
                }
                $sql .= ' GROUP BY s.env, s.project_id, title, s.agent' . ($byChat ? ', s.chat_id, chat_title' : '') . ' ORDER BY output DESC LIMIT 300';
                $st = $db->prepare($sql);
                $st->execute($args);
                $rows = [];
                foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
                    $rows[] = [
                        'env' => $r['env'], 'projectId' => $r['project_id'], 'title' => $r['title'], 'agent' => $r['agent'],
                        'jobs' => (int)$r['jobs'], 'failed' => (int)$r['failed'], 'inputTokens' => (int)$r['input'],
                        'cachedInputTokens' => (int)$r['cached'], 'outputTokens' => (int)$r['output'],
                        'costUsd' => $r['cost'] === null ? null : (float)$r['cost'], 'apiUsd' => $r['api'] === null ? null : (float)$r['api'],
                        'toolCalls' => (int)$r['calls'], 'toolErrors' => (int)$r['errs'], 'durationMs' => (int)$r['ms'],
                    ] + ($byChat ? ['chatId' => $r['chat_id'], 'chatTitle' => $r['chat_title']] : []);
                }
                co_json(200, ['rows' => $rows, 'source' => 'job_stats']);
            } catch (mysqli_sql_exception $e) {
                if ($byChat || stripos($e->getMessage(), 'job_stats') === false) {
                    throw $e;
                }
            }
            $sql = "SELECT j.env, j.project_id, COALESCE(p.title, j.project_id) AS title, j.agent,
                           COUNT(*) AS jobs, SUM(j.status <> 'completed') AS failed,
                           SUM(CAST(JSON_VALUE(j.result, '$.usage.inputTokens') AS UNSIGNED)) AS input,
                           SUM(CAST(JSON_VALUE(j.result, '$.usage.cachedInputTokens') AS UNSIGNED)) AS cached,
                           SUM(CAST(JSON_VALUE(j.result, '$.usage.outputTokens') AS UNSIGNED)) AS output,
                           SUM(CAST(JSON_VALUE(j.result, '$.usage.costUsd') AS DECIMAL(14, 4))) AS cost
                      FROM jobs j LEFT JOIN projects p ON p.project_id = j.project_id
                     WHERE j.result IS NOT NULL AND j.completed_utc >= UTC_TIMESTAMP() - INTERVAL ? DAY"
                . ($owner ? '' : ' AND j.env = ?')
                . ' GROUP BY j.env, j.project_id, title, j.agent ORDER BY output DESC LIMIT 300';
            $st = $db->prepare($sql);
            $st->execute($owner ? [$days] : [$days, $myEnv]);
            $rows = [];
            foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
                $rows[] = [
                    'env' => $r['env'], 'projectId' => $r['project_id'], 'title' => $r['title'], 'agent' => $r['agent'],
                    'jobs' => (int)$r['jobs'], 'failed' => (int)$r['failed'], 'inputTokens' => (int)$r['input'],
                    'cachedInputTokens' => (int)$r['cached'], 'outputTokens' => (int)$r['output'],
                    'costUsd' => $r['cost'] === null ? null : (float)$r['cost'],
                ];
            }
            co_json(200, ['rows' => $rows]);

        case 'ops':
            // 2026-10-18 Ops panel (Host tab): outcomes, durations, re-runs, top errors and daily trend of all jobs; owner only.
            if (!$owner) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, ['ops' => co_ops_report($db, db_int($in, 'days', 7, 1, 90))]);

        case 'health':
            // Schema 17 (CO Web 0.77.0, Host tab Health): failure causes 24 h / 7 d and Bridge supervisor events; owner only.
            if (!$owner) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, ['health' => co_health_report($db, 20)]);

        case 'models_list':
        case 'model_set':
        case 'model_delete':
            // Schema 23 (CO Web 0.99.0, Host and devices → Models): the owner of a resource sets its card and the flag
            // "slave allowed"; everybody sees own resources and the allowed ones of others, the owner sees all.
            try {
                co_json(200, match ($op) {
                    'models_list'  => models_list($db, $myEnv, $owner),
                    'model_set'    => models_put($db, $in, 'user', $myEnv, $owner),
                    'model_delete' => models_delete($db, $in, $myEnv, $owner),
                });
            } catch (InvalidArgumentException $e) {
                co_json(400, ['error' => $e->getMessage()]);
            }

        case 'subs_put':
            // S4 (CO Desktop 0.11.0 / co-resident): the device's signed-in CLI subscriptions become model resources of
            // the environment of its own Google token (subs-lib.php)
            try {
                co_json(200, subs_put($db, $in, $myEnv));
            } catch (InvalidArgumentException $e) {
                co_json(400, ['error' => $e->getMessage()]);
            }

        case 'executors':
            // Schema 28 (CO Web 0.104.0, Freedom F12): the executors that serve the environment, with their health
            $env = (string)($in['env'] ?? $myEnv);
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, co_exec_list($db, $env, $owner));
        case 'executor_set':
            try {
                co_json(200, co_exec_set($db, $in, $owner));
            } catch (InvalidArgumentException $e) {
                co_json(400, ['error' => $e->getMessage()]);
            }

        case 'host_light':
            // S2 (CO Web 0.98.0): diagnosis behind the header traffic light (relay-lib.php co_host_light)
            $env = (string)($in['env'] ?? $myEnv);
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, co_host_light($db, $env));

        // Schema 13 (CO Web 0.60.0): messages sent while a job of the chat runs (followup-lib.php)
        case 'followup_add':
            co_json(200, followup_add($db, $in, $envAllowed, $chatRole, $email));
        case 'followup_list':
            co_json(200, followup_list($db, $in, $envAllowed, $chatRole));
        case 'followup_take':
            co_json(200, followup_take($db, $in, $envAllowed, $chatRole));
        case 'followup_done':
            co_json(200, followup_done($db, $in, $envAllowed, $chatRole));
        case 'followup_cancel':
            co_json(200, followup_cancel($db, $in, $envAllowed, $chatRole));

        case 'job_submit':
            // S7: a job for the site starts the site agent at once, whether or not a CO Web pings it
            $queued = db_job_submit($in, $envAllowed, $acc);
            if (($in['request']['hostId'] ?? '') === 'site' && co_site_agent_kick()) {
                $queued['siteAgentStarted'] = true;
            }
            co_json(200, $queued);

        // Schema 12 (2026-10-28): chat files as version streams (files-lib.php). Drive work is done by CO Web.
        case 'files_list':
        case 'files_set':
        case 'files_merge':
        case 'files_branch':
        case 'files_restored':
        case 'files_undone':
            $chatId = db_id($in, 'chatId');
            $role = $chatRole($chatId);
            if (!share_can($role, $op === 'files_list' ? 'read' : 'write')) {
                co_json(403, ['error' => 'forbidden']);
            }
            try {
                if ($op === 'files_list') {
                    co_json(200, ['files' => co_files_list($db, $chatId)]);
                }
                co_json(200, ['files' => co_files_op($db, $op, $chatId, $in)]);
            } catch (InvalidArgumentException $e) {
                co_json(400, ['error' => $e->getMessage()]);
            }


        // Schema 11: the browser side of the web device; every op checks the device belongs to the session user.
        case 'webdev_register':
            co_json(200, webdev_register($db, $in, $email, $myEnv));
        case 'webdev_heartbeat':
            co_json(200, webdev_heartbeat($db, $in, $email));
        case 'webdev_claim':
            co_json(200, webdev_claim($db, $in, $email));
        case 'webdev_result':
            co_json(200, webdev_result($db, $in, $email));
        case 'webdev_revoke':
            co_json(200, webdev_revoke($db, $in, $email));
        case 'webdev_mine':
            co_json(200, webdev_mine($db, $email));

        case 'job_stats':
            // Schema 15: timeline, tokens, cost and subtask tree of one job (jobstats-lib.php); same access as job_get.
        case 'job_get':
        case 'job_events':
        case 'job_cancel':
            $jobId = db_id($in, 'jobId');
            $st = $db->prepare('SELECT env, chat_id, state, status, origin, result, rejection, cancel_requested FROM jobs WHERE job_id = ?');
            $st->execute([$jobId]);
            $job = $st->get_result()->fetch_assoc();
            $st->close();
            if ($job) {
                // own env = owner, else the chat share role (0.45.0)
                $jobRole = $envAllowed($job['env']) ? 'owner' : $chatRole((string)$job['chat_id']);
                if (!share_can($jobRole, 'read')) {
                    co_json(403, ['error' => 'forbidden']);
                }
                if ($op === 'job_cancel' && !share_can($jobRole, 'write')) {
                    co_json(403, ['error' => 'forbidden']);
                }
            }
            if ($op === 'job_stats') {
                co_json(200, ['stats' => $job ? co_job_stats_get($db, $jobId,
                    fn(string $env, string $cid): bool => $envAllowed($env) || share_can($chatRole($cid), 'read')) : null]);
            }
            if ($op === 'job_get') {
                if (!$job) {
                    co_json(200, ['job' => null]);
                }
                // answered: the chat already has the assistant_message of this job (server finalize, CO Web 0.20.0)
                $st = $db->prepare("SELECT 1 FROM events WHERE job_id = ? AND type = 'assistant_message' LIMIT 1");
                $st->execute([$jobId]);
                $answered = $st->get_result()->fetch_row() !== null;
                $st->close();
                co_json(200, ['job' => db_job_view($job) + ['answered' => $answered]]);
            }
            if ($op === 'job_events') {
                // Without a row (a Drive job not yet synced) nothing is shown; CO Web reads Drive then.
                if (!$job) {
                    co_json(200, ['batches' => []]);
                }
                $after = db_int($in, 'afterSeq', -1, -1, PHP_INT_MAX);
                $st = $db->prepare('SELECT data FROM job_events WHERE job_id = ? AND last_seq > ? ORDER BY first_seq LIMIT 500');
                $st->bind_param('si', $jobId, $after);
                $st->execute();
                $res = $st->get_result();
                $batches = [];
                while ($r = $res->fetch_row()) {
                    $b = json_decode((string)$r[0], true);
                    if (is_array($b)) {
                        $batches[] = $b;
                    }
                }
                $res->free();
                co_json(200, ['batches' => $batches]);
            }
            // job_cancel: a queued job never starts; a claimed one is stopped by Bridge (job_claim returns it).
            if (!$job) {
                co_json(200, ['state' => null]);
            }
            $st = $db->prepare("UPDATE jobs SET cancel_requested = 1, state = 'cancelled' WHERE job_id = ? AND state = 'inbox'");
            $st->execute([$jobId]);
            $withdrawn = $st->affected_rows === 1;
            $st->close();
            if ($withdrawn) {
                // 2026-11-01: a Drive inbox copy (queue fallback, attachment job) must not run later; Bridge 1.8.1+
                // also asks job_drive_claim before it runs one.
                $st = $db->prepare('SELECT host_id, origin, fallback_utc FROM jobs WHERE job_id = ?');
                $st->execute([$jobId]);
                $w = $st->get_result()->fetch_assoc();
                $st->close();
                $dropped = false;
                if ($w && ($w['origin'] !== 'db' || $w['fallback_utc'] !== null)) {
                    try {
                        $dropped = co_queue_drop_drive_copy((string)$w['host_id'], $jobId);
                    } catch (Throwable $e) {
                        error_log('db.php job_cancel: Drive copy of ' . $jobId . ' not trashed: ' . $e->getMessage());
                    }
                }
                co_json(200, ['state' => 'cancelled', 'driveCopyTrashed' => $dropped]);
            }
            $st = $db->prepare('UPDATE jobs SET cancel_requested = 1 WHERE job_id = ?');
            $st->execute([$jobId]);
            $st->close();
            co_json(200, ['state' => $job['state']]);

        case 'profile_get':
            // Environment profile (preferences, style, contacts), CO Web 0.28.0
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $st = $db->prepare('SELECT data, sha, updated_utc FROM kv WHERE scope = ? AND name = ?');
            $st->execute(['env/' . (string)$env, 'profile']);
            $row = $st->get_result()->fetch_assoc();
            $st->close();
            $d = $row ? json_decode((string)$row['data'], true) : null;
            co_json(200, ['profile' => [
                // 0.75.0: revision for profile_put (accepted correction rules also write the profile)
                'rev'        => $row ? (string)$row['sha'] : '',
                'text'       => is_array($d) && is_string($d['text'] ?? null) ? $d['text'] : '',
                'updatedUtc' => $row ? co_db_iso($row['updated_utc'] ?? null) : null,
                'updatedBy'  => is_array($d) && is_string($d['updatedBy'] ?? null) ? $d['updatedBy'] : null,
            ]]);

        case 'profile_put':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $text = $in['text'] ?? null;
            if (!is_string($text) || mb_strlen($text, 'UTF-8') > 20000) {
                throw new InvalidArgumentException('text must be a string of up to 20000 characters');
            }
            $data = json_encode(['text' => $text, 'updatedBy' => $email],
                JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
            // 0.75.0 (schema 16): under the profile lock of accepted correction rules; rev (the sha profile_get
            // returned, '' for no profile) is required, a profile changed since then is not overwritten (409 conflict).
            // CO Web before 0.75.0 sends no rev: its save fails and asks for a reload.
            if (!is_string($in['rev'] ?? null)) {
                co_json(409, ['error' => 'conflict', 'detail' => 'reload the page: this version cannot save the profile']);
            }
            require_once __DIR__ . '/memory-lib.php';
            co_profile_lock($db, (string)$env);
            $conflict = false;
            try {
                $st = $db->prepare('SELECT sha FROM kv WHERE scope = ? AND name = ?');
                $st->execute(['env/' . (string)$env, 'profile']);
                $conflict = (string)($st->get_result()->fetch_row()[0] ?? '') !== $in['rev'];
                $st->close();
                if (!$conflict) {
                    $st = $db->prepare('INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
                                        ON DUPLICATE KEY UPDATE data = VALUES(data), sha = VALUES(sha), updated_utc = VALUES(updated_utc)');
                    $st->execute(['env/' . (string)$env, 'profile', $data, hash('sha256', $data)]);
                    $st->close();
                }
            } finally {
                co_profile_unlock($db, (string)$env);
            }
            if ($conflict) {
                co_json(409, ['error' => 'conflict', 'detail' => 'the profile changed since it was loaded']);
            }
            co_json(200, ['ok' => true]);

        // CO Web 0.37.0: environment templates (kv scope 'env/<env>', name 'templates').
        case 'templates_get':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $st = $db->prepare('SELECT data, sha, updated_utc FROM kv WHERE scope = ? AND name = ?');
            $st->execute(['env/' . (string)$env, 'templates']);
            $row = $st->get_result()->fetch_assoc();
            $st->close();
            $d = $row ? json_decode((string)$row['data'], true) : null;
            // missing or broken data yields an empty list
            $tplList = [];
            if (is_array($d) && is_array($d['templates'] ?? null) && array_is_list($d['templates'])) {
                $tplList = array_values(array_filter($d['templates'], 'is_array'));
            }
            co_json(200, ['templates' => $tplList, 'rev' => $row ? (string)$row['sha'] : '']);

        case 'templates_put':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            // optimistic concurrency: the client sends the rev (sha) it loaded
            $rev = $in['rev'] ?? null;
            if (!is_string($rev)) {
                throw new InvalidArgumentException('rev must be a string');
            }
            $tplScope = 'env/' . (string)$env;
            $tplReadCur = static function () use ($db, $tplScope): array {
                $st = $db->prepare('SELECT data, sha FROM kv WHERE scope = ? AND name = ?');
                $st->execute([$tplScope, 'templates']);
                $row = $st->get_result()->fetch_assoc();
                $st->close();
                $d = $row ? json_decode((string)$row['data'], true) : null;
                $l = is_array($d) && is_array($d['templates'] ?? null) && array_is_list($d['templates'])
                    ? array_values(array_filter($d['templates'], 'is_array')) : [];
                return ['exists' => (bool)$row, 'rev' => $row ? (string)$row['sha'] : '', 'templates' => $l];
            };
            $tplConflict = static function (array $cur): void {
                co_json(409, ['error' => 'templates_changed', 'templates' => $cur['templates'], 'rev' => $cur['rev']]);
            };
            $tplCur = $tplReadCur();
            if ($rev !== $tplCur['rev']) {
                $tplConflict($tplCur);
            }
            $tplRaw = $in['templates'] ?? null;
            if (!is_array($tplRaw) || !array_is_list($tplRaw)) {
                throw new InvalidArgumentException('templates must be a list');
            }
            if (count($tplRaw) > 30) {
                throw new InvalidArgumentException('templates: at most 30 entries allowed');
            }
            // one template; unknown keys are dropped
            $tplClean = static function (mixed $t, int $i): array {
                $at = 'templates[' . $i . ']';
                if (!is_array($t)) {
                    throw new InvalidArgumentException($at . ' must be an object');
                }
                $name = $t['name'] ?? null;
                if (!is_string($name) || ($name = trim($name)) === '' || mb_strlen($name, 'UTF-8') > 40) {
                    throw new InvalidArgumentException($at . '.name must be a non-empty string of at most 40 characters');
                }
                $agent = $t['agent'] ?? null;
                if (!is_string($agent) || !preg_match('/^[a-z0-9-]{1,40}$/D', $agent)) {
                    throw new InvalidArgumentException($at . '.agent must match [a-z0-9-]{1,40}');
                }
                $mode = $t['mode'] ?? null;
                if (!in_array($mode, ['chat', 'cowork'], true)) {
                    throw new InvalidArgumentException($at . ".mode must be 'chat' or 'cowork'");
                }
                $model = $t['model'] ?? null;
                if ($model !== null && (!is_string($model) || mb_strlen($model, 'UTF-8') > 80)) {
                    throw new InvalidArgumentException($at . '.model must be null or a string of at most 80 characters');
                }
                $effort = $t['effort'] ?? null;
                if ($effort !== null && (!is_string($effort) || mb_strlen($effort, 'UTF-8') > 20)) {
                    throw new InvalidArgumentException($at . '.effort must be null or a string of at most 20 characters');
                }
                $out = ['name' => $name, 'agent' => $agent, 'mode' => $mode, 'model' => $model, 'effort' => $effort];
                if (($t['hostId'] ?? null) !== null) {
                    if (!is_string($t['hostId']) || !co_exec_known(co_db(), $t['hostId'])) {
                        throw new InvalidArgumentException($at . '.hostId must be a known executor (home-main, site or a registered one)');
                    }
                    $out['hostId'] = $t['hostId'];
                }
                if (($t['permissions'] ?? null) !== null) {
                    $p = $t['permissions'];
                    if (!is_array($p)
                        || !in_array($p['files'] ?? null, ['read', 'write'], true)
                        || !is_bool($p['shell'] ?? null)
                        || !is_bool($p['web'] ?? null)) {
                        throw new InvalidArgumentException($at . ".permissions requires files ('read'|'write'), shell (bool) and web (bool)");
                    }
                    $out['permissions'] = ['files' => $p['files'], 'shell' => $p['shell'], 'web' => $p['web']];
                }
                foreach (['useDevice', ...CO_GATEWAY_TEMPLATE_FLAGS] as $flag) { // gateway use: legacy wire names (gateway-lib.php)
                    if (($t[$flag] ?? null) !== null) {
                        if (!is_bool($t[$flag])) {
                            throw new InvalidArgumentException($at . '.' . $flag . ' must be a boolean');
                        }
                        $out[$flag] = $t[$flag];
                    }
                }
                return $out;
            };
            // duplicate names: last wins, position of the first appearance kept
            $tplByName = [];
            foreach ($tplRaw as $i => $t) {
                $c = $tplClean($t, $i);
                $tplByName[$c['name']] = $c;
            }
            $data = json_encode(['templates' => array_values($tplByName), 'updatedBy' => $email],
                JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
            $newSha = hash('sha256', $data);
            // unchanged data: UPDATE would report 0 affected rows
            if ($tplCur['exists'] && $newSha === $rev) {
                co_json(200, ['ok' => true, 'rev' => $newSha]);
            }
            if ($tplCur['exists']) {
                $st = $db->prepare('UPDATE kv SET data = ?, sha = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = ? AND sha = ?');
                $st->execute([$data, $newSha, $tplScope, 'templates', $rev]);
            } else {
                $st = $db->prepare('INSERT IGNORE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
                $st->execute([$tplScope, 'templates', $data, $newSha]);
            }
            $tplAffected = $st->affected_rows;
            $st->close();
            if ($tplAffected !== 1) {
                $tplConflict($tplReadCur());
            }
            co_json(200, ['ok' => true, 'rev' => $newSha]);

        // 0.45.0 (schema 10): who a project or chat is shared with; the project owner (or admin) changes it.
        case 'shares_get':
            $id = (string)db_id($in, 'id');
            $kind = $in['kind'] ?? null;
            if ($kind !== 'project' && $kind !== 'chat') {
                throw new InvalidArgumentException('invalid kind');
            }
            $pid = $kind === 'project' ? $id : share_chat_project($db, $id);
            $penv = $pid === null ? null : share_project_env($db, $pid);
            if ($penv === null) {
                co_json(404, ['error' => 'not_found']);
            }
            $role = $kind === 'project' ? $projRole($id) : $chatRole($id);
            if (!share_can($role, 'read')) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, [
                'shares' => share_list($db, $kind, $id),
                'role' => $role,
                'ownerEnv' => $penv,
                'envs' => share_known_envs(),
                'projectShares' => $kind === 'chat' ? share_list($db, 'project', $pid) : [],
            ]);

        case 'share_set':
            $id = (string)db_id($in, 'id');
            $kind = $in['kind'] ?? null;
            if ($kind !== 'project' && $kind !== 'chat') {
                throw new InvalidArgumentException('invalid kind');
            }
            $pid = $kind === 'project' ? $id : share_chat_project($db, $id);
            $penv = $pid === null ? null : share_project_env($db, $pid);
            if ($penv === null) {
                co_json(404, ['error' => 'not_found']);
            }
            // only project managers may change shares
            if (!share_can($projRole($pid), 'manage')) {
                co_json(403, ['error' => 'forbidden']);
            }
            $env = $in['env'] ?? null;
            if (!is_string($env) || $env === $penv || !in_array($env, share_known_envs(), true)) {
                throw new InvalidArgumentException('invalid env');
            }
            $role = $in['role'] ?? null; // null = revoke
            if ($role !== null && !is_string($role)) {
                throw new InvalidArgumentException('invalid role');
            }
            share_set($db, $kind, $id, $env, $role, $email);
            co_json(200, ['ok' => true, 'shares' => share_list($db, $kind, $id)]);

        // CO Web 0.71.0: "Waiting for you" - questions, captcha and browser confirmations, failed jobs, memory candidates
        case 'inbox':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            co_json(200, co_inbox_list($db, (string)$env, $email));

        case 'inbox_mark':
            co_json(200, co_inbox_mark($db, $email, $in));

        // Schema 18: Yes / No of a confirmation card; only the person of the chat's env (project owner) decides
        case 'action_decide':
            $r = co_action_decide($db, $myEnv, $email, db_int($in, 'id', 0, 1, PHP_INT_MAX),
                is_string($in['decision'] ?? null) ? $in['decision'] : '');
            if (!$r['ok']) {
                co_json($r['status'], ['error' => $r['error']]);
            }
            co_json(200, $r);

        // Schema 16: proposals of the tool library learning pass (cron/tool-learn.php); the library is the owner's
        case 'tool_candidates':
        case 'tool_decide':
            if (!$owner) {
                co_json(403, ['error' => 'forbidden']);
            }
            require_once __DIR__ . '/tools-lib.php';
            if ($op === 'tool_candidates') {
                co_json(200, co_tools_candidates($db));
            }
            $r = co_tools_decide($db, db_int($in, 'id', 0, 1, PHP_INT_MAX), is_string($in['action'] ?? null) ? $in['action'] : '', $email,
                is_string($in['chatId'] ?? null) ? $in['chatId'] : '');
            if (!$r['ok']) {
                co_json($r['status'] === null ? 404 : 409, ['error' => $r['status'] === null ? 'not_found' : 'already_decided', 'status' => $r['status']]);
            }
            co_json(200, $r);

        // CO Web 0.71.0: global switch of the automatic Claude <-> Codex retry (autoswitch-lib.php); the owner sets it
        case 'autoswitch':
            if (array_key_exists('enabled', $in)) {
                if (!$owner) {
                    co_json(403, ['error' => 'forbidden']);
                }
                if (!is_bool($in['enabled'])) {
                    throw new InvalidArgumentException('enabled must be a boolean');
                }
                co_autoswitch_set_enabled($db, $in['enabled']);
            }
            co_json(200, ['enabled' => co_autoswitch_enabled($db), 'canSet' => $owner]);

        // CO Web 0.87.0 (browser-lib.php): agent browser defaults of an environment; with 'defaults' saves them
        case 'browser_defaults':
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            if (array_key_exists('defaults', $in)) {
                co_browser_env_defaults_put($db, (string)$env, $in['defaults']);
            }
            co_json(200, ['builtin' => CO_BROWSER_BUILTIN, 'defaults' => (object)co_browser_env_defaults($db, (string)$env)]);

        case 'activity':
            // CO Web 0.31.0 status icons: chats with a queued/running job, chats whose last message is a question.
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $st = $db->prepare(
                "SELECT j.chat_id, MAX(j.project_id) AS project_id, MAX(j.state = 'claimed') AS is_running,
                        MIN(COALESCE(j.queued_utc, j.claimed_utc, j.synced_utc)) AS since
                   FROM jobs j
                  WHERE (j.env = ? OR j.chat_id IN (SELECT c.chat_id FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE " . share_visible_sql('p', 'c') . ")) AND j.state IN ('inbox', 'claimed') AND j.result IS NULL
                    AND j.cancel_requested = 0
                    AND COALESCE(j.queued_utc, j.claimed_utc, j.synced_utc) > UTC_TIMESTAMP() - INTERVAL 1 DAY
                  GROUP BY j.chat_id"
            );
            $st->execute([$env, $env, $env, $env]);
            $running = [];
            foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
                $running[] = [
                    'chatId'    => (string)$r['chat_id'],
                    'projectId' => (string)$r['project_id'],
                    'state'     => ((int)$r['is_running'] === 1) ? 'running' : 'queued',
                    'since'     => co_db_iso($r['since']),
                ];
            }
            $st->close();
            $st = $db->prepare(
                "SELECT c.chat_id
                   FROM chats c
                   JOIN projects p ON p.project_id = c.project_id
                   JOIN (SELECT e.chat_id, MAX(e.seq) AS max_seq
                           FROM events e
                           JOIN chats c2 ON c2.chat_id = e.chat_id
                           JOIN projects p2 ON p2.project_id = c2.project_id
                          WHERE " . share_visible_sql('p2', 'c2') . " AND c2.updated_utc > UTC_TIMESTAMP() - INTERVAL 14 DAY
                            AND e.type IN ('user_message', 'assistant_message') AND JSON_EXTRACT(e.data, '$.deleted') IS NULL
                          GROUP BY e.chat_id) m ON m.chat_id = c.chat_id
                   JOIN events le ON le.chat_id = m.chat_id AND le.seq = m.max_seq
                                 AND le.type IN ('user_message', 'assistant_message')
                  WHERE " . share_visible_sql('p', 'c') . " AND c.updated_utc > UTC_TIMESTAMP() - INTERVAL 14 DAY
                    AND le.type = 'assistant_message' AND RIGHT(TRIM(le.text), 1) = '?'
                  ORDER BY c.updated_utc DESC
                  LIMIT 500"
            );
            $st->execute([$env, $env, $env, $env, $env, $env]);
            $asking = array_map(static fn(array $r): string => (string)$r['chat_id'],
                $st->get_result()->fetch_all(MYSQLI_ASSOC));
            $st->close();
            // F10: chats stopped while a gateway model was unavailable come from the gateway's module (gateways[].ops.waiting)
            co_json(200, ['running' => $running, 'asking' => $asking]);

        case 'search_items':
            // Freedom F10 (search-lib.php, ext/1 search.provider): results of the search providers that are not chats
            // (project documents of a document index module); CO Web AI search shows them next to the chats
            $env = $in['env'] ?? null;
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $project = is_string($in['projectId'] ?? null) && $in['projectId'] !== '' ? $in['projectId'] : null;
            co_json(200, ['items' => co_search_items($db, $env, is_string($in['q'] ?? null) ? $in['q'] : '', db_int($in, 'limit', 8, 1, 50), $project)]);

        case 'search_chats':
            // CO Web 0.16.0 AI search: every chat of the env, the ones found for the query and expanded terms first.
            $env = is_string($in['env'] ?? null) ? $in['env'] : '';
            if (!$envAllowed($env)) {
                co_json(403, ['error' => 'forbidden']);
            }
            $raw = $in['terms'] ?? null;
            if (!is_array($raw) || !array_is_list($raw) || count($raw) < 1 || count($raw) > 10) {
                throw new InvalidArgumentException('terms must contain 1..10 items');
            }
            $terms = [];
            foreach ($raw as $t) {
                if (!is_string($t)) {
                    throw new InvalidArgumentException('terms must be strings');
                }
                $t = trim($t);
                $len = mb_strlen($t, 'UTF-8');
                if ($len < 1 || $len > 60) {
                    throw new InvalidArgumentException('each term must be 1..60 chars');
                }
                $terms[] = $t;
            }
            $limit = db_int($in, 'limit', 300, 1, 300);
            // Schema 20 (search-lib.php): messages, protocol entries and the lists of search providers merged by rank; the
            // user's own words (query, CO Web 0.88.0+) go to the providers, the expanded terms widen the full-text lists
            $query = is_string($in['query'] ?? null) ? trim(mb_substr($in['query'], 0, 300, 'UTF-8')) : '';
            $found = co_search_chats($db, $env, ['sql' => 'p.env = ? AND p.archived = 0', 'params' => [$env]], $query, $terms);
            $hits = [];
            foreach ($found['chats'] as $id => $h) {
                $hits[$id] = ['score' => $h['score'], 'matches' => $h['matches'], 'ranks' => $h['ranks']];
            }

            // All chats of the env (titles count for the ranking too)
            $st = $db->prepare(
                'SELECT c.chat_id, c.project_id, c.title, c.updated_utc, c.summary
                   FROM chats c
                   JOIN projects p ON p.project_id = c.project_id
                  WHERE p.env = ? AND p.archived = 0'
            );
            $st->execute([$env]);
            $res = $st->get_result();
            $rows = [];
            while ($r = $res->fetch_assoc()) {
                $id = (string)$r['chat_id'];
                $h = $hits[$id] ?? null;
                $rows[] = [
                    '_u'         => (string)($r['updated_utc'] ?? ''),
                    'chatId'     => $id,
                    'projectId'  => (string)$r['project_id'],
                    'title'      => (string)($r['title'] ?? ''),
                    'summary'    => (string)($r['summary'] ?? ''),
                    'updatedUtc' => co_db_iso($r['updated_utc'] ?? null),
                    'score'      => $h ? (float)$h['score'] : 0.0,
                    'matches'    => $h ? $h['matches'] : [],
                    'ranks'      => $h ? $h['ranks'] : null,
                ];
            }
            $res->free();
            usort($rows, static fn(array $a, array $b): int => [$b['score'], $b['_u']] <=> [$a['score'], $a['_u']]);
            $rows = array_map(static function (array $r): array {
                unset($r['_u']);
                return $r;
            }, array_slice($rows, 0, $limit));
            co_json(200, ['chats' => $rows]);
    }

    co_json(400, ['error' => 'unknown_op']);
} catch (InvalidArgumentException $e) {
    co_json(400, ['error' => 'bad_request', 'detail' => $e->getMessage()]);
} catch (Throwable $e) {
    error_log('db.php: ' . $e->getMessage());
    co_json(500, ['error' => 'server_error']);
}
