<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// finalize-lib.php: writes the assistant_message of a finished job into its chat on the server (CO Web 0.20.0).
// Until now only an open CO Web wrote it (finalizeTurn), so answers of schedule chats and of jobs whose chat was
// closed waited for the chat to be opened. Called by db.php job_result (Bridge 1.3.0, at once) and by
// cron/drive-sync.php for result.json files from Drive (other hosts, older Bridges; within a minute).
// The event equals what CO Web writes; it goes to the database first, then to the chat's events/ folder on Drive.
declare(strict_types=1);

require_once __DIR__ . '/drive-lib.php';
require_once __DIR__ . '/autoswitch-lib.php';
require_once __DIR__ . '/gateway-lib.php';
require_once __DIR__ . '/push-lib.php';
require_once __DIR__ . '/modules-lib.php';

const CO_FIN_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const CO_FIN_FOLDER_MIME = 'application/vnd.google-apps.folder';

/** EVT-yyyymmddHHMMss-XXXXXX, same alphabet as CO Web util.ts. */
function co_fin_event_id(): string
{
    $r = '';
    foreach (str_split(random_bytes(6)) as $b) {
        $r .= CO_FIN_ALPHABET[ord($b) % 32];
    }
    return 'EVT-' . gmdate('YmdHis') . '-' . $r;
}

/** chatTitle.ts extractTitle: strips [[chat-title: …]] markers, returns [text, title|null]. */
function co_fin_extract_title(string $text): array
{
    $title = null;
    $stripped = preg_replace_callback('/\[\[\s*chat-title\s*:\s*([^\]\n]{1,120}?)\s*\]\]/iu', function (array $m) use (&$title) {
        $title = $m[1];
        return '';
    }, $text);
    if ($title === null || !is_string($stripped)) {
        return [$text, null];
    }
    $c = trim((string)preg_replace('/[.:;,\s]+$/u', '', (string)preg_replace('/\s+/u', ' ', (string)preg_replace('/[«»"“”\'`*_#]/u', '', $title))));
    if (mb_strlen($c) < 2) {
        $c = null;
    } else {
        $c = mb_strtoupper(mb_substr($c, 0, 1)) . mb_substr($c, 1);
        if (mb_strlen($c) > 70) {
            $cut = mb_substr($c, 0, 70);
            $sp = mb_strrpos($cut, ' ');
            $c = rtrim($sp !== false && $sp > 35 ? mb_substr($cut, 0, $sp) : $cut, ",.;: -") . '…';
        }
    }
    return [rtrim((string)preg_replace("/\n{3,}/", "\n\n", $stripped)), $c];
}

/** Authenticated Drive POST (JSON or multipart); returns the decoded answer. */
function co_fin_drive_post(string $url, string $body, string $contentType): array
{
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_POST           => true,
        CURLOPT_POSTFIELDS     => $body,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CONNECTTIMEOUT => 10,
        CURLOPT_TIMEOUT        => 30,
        CURLOPT_HTTPHEADER     => ['Authorization: Bearer ' . co_drive_token(), 'Content-Type: ' . $contentType],
    ]);
    $raw = curl_exec($ch);
    $code = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    $d = is_string($raw) ? json_decode($raw, true) : null;
    if ($code < 200 || $code >= 300 || !is_array($d)) {
        throw new RuntimeException("drive POST $code: " . substr((string)$raw, 0, 200));
    }
    return $d;
}

/** Id of the child folder `name` under `parent`, created when missing (CO Web ensurePath). */
function co_fin_folder(string $parent, string $name): string
{
    $q = sprintf("'%s' in parents and name='%s' and mimeType='%s' and trashed=false", $parent, $name, CO_FIN_FOLDER_MIME);
    $list = co_drive_get('files', ['q' => $q, 'fields' => 'files(id)', 'pageSize' => 1]);
    $id = $list['files'][0]['id'] ?? null;
    if (is_string($id) && $id !== '') {
        return $id;
    }
    $d = co_fin_drive_post('https://www.googleapis.com/drive/v3/files?fields=id',
        json_encode(['name' => $name, 'mimeType' => CO_FIN_FOLDER_MIME, 'parents' => [$parent]]), 'application/json');
    return (string)$d['id'];
}

function co_fin_drive_create(string $parent, string $name, string $content): string
{
    $boundary = 'co' . bin2hex(random_bytes(8));
    $body = "--$boundary\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n"
        . json_encode(['name' => $name, 'parents' => [$parent], 'mimeType' => 'application/json']) . "\r\n"
        . "--$boundary\r\nContent-Type: application/json\r\n\r\n" . $content . "\r\n--$boundary--";
    $d = co_fin_drive_post('https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id',
        $body, "multipart/related; boundary=$boundary");
    return (string)$d['id'];
}

/**
 * Writes the assistant_message for a finished job unless the chat already has one for it.
 * Needs the job's user_message in the database (it carries branch, host and agent). Returns the eventId or null.
 */
function co_finalize_job(array $req, array $res): ?string
{
    $jobId = $req['jobId'] ?? null;
    $chatId = $req['chatId'] ?? null;
    if (!co_db_id_ok($jobId) || !co_db_id_ok($chatId) || !is_string($res['status'] ?? null)) {
        return null;
    }
    $db = co_db();
    $one = function (string $sql, array $p) use ($db): ?array {
        $st = $db->prepare($sql);
        $st->execute($p);
        $row = $st->get_result()->fetch_assoc();
        $st->close();
        return $row ?: null;
    };
    if ($one("SELECT 1 AS x FROM events WHERE chat_id = ? AND job_id = ? AND type = 'assistant_message' LIMIT 1", [$chatId, $jobId])) {
        return null;
    }
    // 2026-11-01: a job withdrawn from the queue (state cancelled) gets no answer, even if a stray copy ran it.
    if ($one("SELECT 1 AS x FROM jobs WHERE job_id = ? AND state = 'cancelled'", [$jobId])) {
        return null;
    }
    $userRow = $one("SELECT data FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message' LIMIT 1", [$chatId, $jobId]);
    $u = $userRow ? json_decode((string)$userRow['data'], true) : null;
    $chat = $one('SELECT folder_id FROM chats WHERE chat_id = ?', [$chatId]);
    if (!is_array($u) || !$chat) {
        return null;
    }

    $status = $res['status'];
    $phase = $status === 'completed' ? 'completed' : ($status === 'cancelled' ? 'cancelled' : 'failed');
    [$text, $title] = co_fin_extract_title(is_string($res['agentFinalResponse'] ?? null) ? $res['agentFinalResponse'] : '');
    $errs = is_array($res['agentErrorMessages'] ?? null) ? $res['agentErrorMessages'] : [];
    // 0.53.0: the agent's own messages follow the Bridge summary so CO Web can diagnose the failure (mergeAgentErrors)
    $baseErr = is_string($res['error'] ?? null) && $res['error'] !== '' ? $res['error'] : null;
    if ($baseErr !== null) {
        $extra = [];
        foreach ($errs as $m) {
            if (!is_string($m)) continue;
            $m = trim($m);
            if ($m === '' || str_contains($baseErr, $m)) continue;
            $m = mb_substr($m, 0, 1000);
            if (in_array($m, $extra, true)) continue;
            $extra[] = $m;
            if (count($extra) >= 5) break;
        }
        $error = $extra ? $baseErr . "\n" . implode("\n", $extra) : $baseErr;
    } else {
        $error = $errs ? implode("\n", $errs) : null;
    }
    $seqRow = $one('SELECT COALESCE(MAX(seq), 0) + 1 AS s FROM events WHERE chat_id = ?', [$chatId]);
    $now = gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000);

    // Key order of CO Web finalizeTurn (workbench.ts); absent optional keys are left out like JSON.stringify does.
    $ev = [
        'schemaVersion' => 1,
        'eventId'       => co_fin_event_id(),
        'sequence'      => (int)$seqRow['s'],
        'createdUtc'    => $now,
        'chatId'        => $chatId,
        'branchId'      => is_string($u['branchId'] ?? null) ? $u['branchId'] : 'BR-root',
        'type'          => 'assistant_message',
        'jobId'         => $jobId,
    ];
    // S8: the host that ran the job (co_queue_handover may have moved it), so CO Web resumes the session there
    if (is_string($req['hostId'] ?? null) && $req['hostId'] !== '') $ev['hostId'] = $req['hostId'];
    elseif (isset($u['hostId'])) $ev['hostId'] = $u['hostId'];
    if (isset($u['agent'])) $ev['agent'] = $u['agent'];
    // F10 side jobs (ext/1): the answer to a job a module queued is a row of its turn under the same key (CO Web
    // collapses both)
    foreach (co_module_side_keys($db) as $k) if (isset($u[$k])) $ev[$k] = $u[$k];
    $ev['status'] = $phase;
    $ev['text'] = $text;
    if ($title !== null) $ev['autoTitle'] = $title;
    $ev += [
        'sessionId'        => $res['sessionId'] ?? null,
        'artifacts'        => $res['artifacts'] ?? [],
        'missingArtifacts' => $res['missingArtifacts'] ?? [],
        'error'            => $error,
        'completedUtc'     => is_string($res['completedUtc'] ?? null) ? $res['completedUtc'] : $now,
        'mode'             => $res['mode'] ?? $u['mode'] ?? 'cowork',
        'model'            => $res['model'] ?? $u['model'] ?? null,
        'effort'           => $res['effort'] ?? $u['effort'] ?? null,
        'modelReported'    => $res['modelReported'] ?? null,
        'usage'            => $res['usage'] ?? null,
        'exportSkipped'    => $res['exportSkipped'] ?? [],
    ];
    // Bridge 1.5.1: the job could not reach the gateway model (the gateway's module lists the chat for a resume when it
    // is back); read under the new and the legacy wire names, written under all of them for older CO Web
    $blocked = co_gateway_blocked($res);
    if ($blocked !== '') foreach (CO_GATEWAY_BLOCKED_FIELDS as $k) $ev[$k] = $blocked;

    // A concurrent writer (an open CO Web) may have answered meanwhile: check again right before the insert.
    if ($one("SELECT 1 AS x FROM events WHERE chat_id = ? AND job_id = ? AND type = 'assistant_message' LIMIT 1", [$chatId, $jobId])) {
        return null;
    }
    // 2026-10-16: chat messages live only in the database (no Drive copy).
    // Schema 25: another writer's copy may have won the unique answer key meanwhile; then this one was dropped.
    if (!empty(co_db_put(['events' => [$ev]])['duplicates'])) {
        return null;
    }
    // 0.89.0: the answer in a family chat goes as push to the other members
    if (function_exists('co_family_notify')) {
        try {
            co_family_notify(co_db(), [$ev], null);
        } catch (Throwable $e) {
            error_log('finalize family: ' . $e->getMessage());
        }
    }
    // 0.71.0: a limit, sign-in or model error is retried once on the other agent (autoswitch-lib.php); never breaks the answer
    if ($phase === 'failed') {
        try {
            co_autoswitch_try($req, $res, $u, $error, $text);
        } catch (Throwable $e) {
            error_log('finalize autoswitch: ' . $e->getMessage());
        }
    }
    return $ev['eventId'];
}

/**
 * A finished job reported by its host (db.php job_result, cron/drive-sync.php): its answer into the chat, then the
 * "job finished" push when the host has no push of its own (site agent; Bridge pushes itself). The push does not
 * depend on who wrote the answer: for a site job an open CO Web often writes it first.
 */
function co_job_finished(string $host, array $req, array $res): ?string
{
    try {
        $eid = co_finalize_job($req, $res);
    } finally {
        if (in_array($host, CO_PUSH_SERVER_HOSTS, true)) {
            $push = static function () use ($host, $req, $res): void {
                co_push_job_finished(co_db(), $host, $req, $res);
            };
            if (PHP_SAPI === 'cli') {
                $push();
            } else {
                // after the response: slow push endpoints must not hold the site agent's job_result
                register_shutdown_function(static function () use ($push): void {
                    if (function_exists('litespeed_finish_request')) litespeed_finish_request();
                    elseif (function_exists('fastcgi_finish_request')) fastcgi_finish_request();
                    $push();
                });
            }
        }
    }
    return $eid;
}

/** Drive folder id of transport/hosts/<host>/inbox (cached in meta). */
function co_fin_inbox_folder(string $host): ?string
{
    $db = co_db();
    $key = 'drive_inbox_' . $host;
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([$key]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    if ($row && is_string($row[0]) && $row[0] !== '') {
        return $row[0];
    }
    $list = co_drive_get('files', ['q' => "name='inbox' and mimeType='" . CO_FIN_FOLDER_MIME . "' and trashed=false",
        'fields' => 'files(id)', 'pageSize' => 50]);
    foreach ($list['files'] ?? [] as $f) {
        $a = co_drive_ancestor_names((string)$f['id'], 3);
        if (($a[0] ?? null) === $host && ($a[1] ?? null) === 'hosts' && ($a[2] ?? null) === 'transport') {
            $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
            $st->execute([$key, (string)$f['id']]);
            return (string)$f['id'];
        }
    }
    return null;
}

/**
 * Safety net of the database queue: jobs no Bridge has claimed for 90 s get a Drive inbox copy with the same jobId
 * (request.json, then request.ready). Returns the job ids copied.
 * 2026-11-01: not for a host whose Bridge called job_claim within 60 s (meta claim_poll_<host>): it is alive and only
 * busy, takes the job from the database when a slot frees, and a Drive copy would only escape job_cancel.
 */
function co_queue_fallback(): array
{
    $db = co_db();
    $res = $db->query("SELECT job_id, host_id, request FROM jobs
                        WHERE origin = 'db' AND state = 'inbox' AND cancel_requested = 0 AND fallback_utc IS NULL
                          AND queued_utc < UTC_TIMESTAMP(3) - INTERVAL 90 SECOND
                          AND NOT EXISTS (SELECT 1 FROM meta m
                                           WHERE m.k = IF(CHAR_LENGTH(jobs.host_id) <= 29, CONCAT('claim_poll_', jobs.host_id),
                                                          CONCAT('claim_poll_#', LEFT(MD5(jobs.host_id), 28)))
                                             AND m.v > DATE_FORMAT(UTC_TIMESTAMP() - INTERVAL 60 SECOND, '%Y-%m-%d %H:%i:%s'))
                        ORDER BY queued_utc LIMIT 10");
    $done = [];
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $j) {
        $inbox = co_fin_inbox_folder((string)$j['host_id']);
        if ($inbox === null) {
            break;
        }
        $req = json_decode((string)$j['request'], true);
        if (!is_array($req)) {
            continue;
        }
        // Mark first: a second cron run never writes a second copy; a job claimed or cancelled meanwhile gets none.
        $st = $db->prepare("UPDATE jobs SET fallback_utc = UTC_TIMESTAMP(3)
                             WHERE job_id = ? AND fallback_utc IS NULL AND state = 'inbox' AND cancel_requested = 0");
        $st->execute([$j['job_id']]);
        $mine = $st->affected_rows === 1;
        $st->close();
        if (!$mine) {
            continue;
        }
        $bytes = (string)json_encode($req, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        $pkg = co_fin_folder($inbox, (string)$j['job_id']);
        // Bridge 1.8.1+ never runs a copy marked db-fallback without the database's consent (job_drive_claim).
        co_fin_drive_create($pkg, 'db-fallback', '{}');
        co_fin_drive_create($pkg, 'request.json', $bytes);
        co_fin_drive_create($pkg, 'request.ready', (string)json_encode([
            'schemaVersion' => 2, 'payload' => 'request.json', 'sizeBytes' => strlen($bytes), 'sha256' => hash('sha256', $bytes),
        ]));
        // A job_cancel between the mark and the upload may have looked for the copy before it existed: drop it here.
        $st = $db->prepare('SELECT state, cancel_requested FROM jobs WHERE job_id = ?');
        $st->execute([$j['job_id']]);
        $now = $st->get_result()->fetch_assoc();
        $st->close();
        if ($now && ($now['state'] === 'cancelled' || (int)$now['cancel_requested'] === 1)) {
            co_queue_drop_drive_copy((string)$j['host_id'], (string)$j['job_id']);
            continue;
        }
        $done[] = (string)$j['job_id'];
    }
    return $done;
}

/**
 * S8 (owner's decision 11.11: the nearest live executor): a database job CO Web sent with the 'auto' preference
 * (request.hostAuto) that its executor has not taken within 60 s goes once to the other executor when that one is alive
 * and fits the job. 11.11 a first message of a new project waited 34 min in the Home queue while the Bridge drained for
 * an update. Home -> Site and Site -> Home. No double run: the move is one UPDATE guarded by host, state 'inbox',
 * no cancel, no result and no Drive copy (fallback_utc) on the row job_claim locks; job_drive_claim runs nothing of
 * another host. The target must take its jobs from the database (job_claim within 60 s: Bridge, site agent 1.6.0+), so
 * Stop reaches it through job_claim; the chat's user_message gets the new hostId, so CO Web follows the job (events,
 * state, Stop) on the new host. Not moved: resumed provider sessions (they live on the first host), gateway agents,
 * files and context folders, browser devices, devices for an agent other than Claude (the relay serves Claude only),
 * agents the target lacks. Returns "jobId from>to" lines.
 */
function co_queue_handover(): array
{
    $db = co_db();
    $done = [];
    foreach (['home-main' => 'site', 'site' => 'home-main'] as $from => $to) {
        $owner = co_handover_target($db, $to);
        if ($owner === null) continue;
        // the exclusions are in SQL (agents of the target without the gateway ones, auto, never moved, a new session,
        // no context folders or files), so jobs that cannot move do not fill the window; PHP checks the devices
        $agents = array_values(array_filter(is_array($owner['agents'] ?? null) ? array_map('strval', array_keys($owner['agents'])) : ['claude'],
            static fn(string $a): bool => co_gateway_agent($a) === null && co_db_id_ok($a)));
        if (!$agents) continue;
        $ph = implode(',', array_fill(0, count($agents), '?'));
        $st = $db->prepare("SELECT job_id, agent, request FROM jobs
                             WHERE host_id = ? AND origin = 'db' AND state = 'inbox' AND cancel_requested = 0 AND result IS NULL
                               AND fallback_utc IS NULL AND agent IN ($ph)
                               AND queued_utc < UTC_TIMESTAMP(3) - INTERVAL 60 SECOND
                               AND queued_utc > UTC_TIMESTAMP(3) - INTERVAL 6 HOUR
                               AND request LIKE '%\"hostAuto\":true%' AND request NOT LIKE '%\"handover\":%'
                               AND request LIKE '%\"operation\":\"new\"%' AND request NOT LIKE '%\"contextFolders\":%'
                               AND request NOT LIKE '%\"attachments\":[{%'
                             ORDER BY queued_utc LIMIT 500");
        $st->execute(array_merge([$from], $agents));
        $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
        $st->close();
        foreach ($rows as $j) {
            $req = json_decode((string)$j['request'], true);
            if (!is_array($req) || co_handover_blocked($req, $to) !== null) continue;
            $req['hostId'] = $to;
            $req['handover'] = ['from' => $from, 'utc' => gmdate('Y-m-d\TH:i:s\Z')];
            $req['instruction'] = co_handover_note($from, $to) . (string)($req['instruction'] ?? '');
            $json = co_db_json($req);
            $sha = hash('sha256', co_db_json(['request' => $req, 'result' => null]));
            $st = $db->prepare("UPDATE jobs SET host_id = ?, request = ?, sha = ?
                                 WHERE job_id = ? AND host_id = ? AND origin = 'db' AND state = 'inbox' AND cancel_requested = 0
                                   AND result IS NULL AND fallback_utc IS NULL");
            $st->execute([$to, $json, $sha, $j['job_id'], $from]);
            $moved = $st->affected_rows === 1;
            $st->close();
            if (!$moved) continue;
            co_handover_user_event($db, (string)$j['job_id'], (string)($req['chatId'] ?? ''), $to);
            $done[] = "{$j['job_id']} {$from}>{$to}";
        }
    }
    return $done;
}

/** The chat's user_message of a moved job names the new host (CO Web reads events, state and Stop there). */
function co_handover_user_event(mysqli $db, string $jobId, string $chatId, string $to): void
{
    $st = $db->prepare("SELECT event_id, data FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message'");
    $st->execute([$chatId, $jobId]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    foreach ($rows as $r) {
        $ev = json_decode((string)$r['data'], true);
        if (!is_array($ev)) continue;
        $ev['hostId'] = $to;
        $data = co_db_json($ev);
        $st = $db->prepare('UPDATE events SET data = ?, sha = ?, synced_utc = UTC_TIMESTAMP(3) WHERE event_id = ?');
        $st->execute([$data, hash('sha256', $data), $r['event_id']]);
        $st->close();
    }
}

/** Why a job may not leave its host for $to (null = it may). */
function co_handover_blocked(array $req, string $to): ?string
{
    if (($req['hostAuto'] ?? false) !== true) return 'manual';
    if (isset($req['handover'])) return 'moved once';
    if (($req['conversation']['operation'] ?? 'new') !== 'new') return 'resume';
    if (!empty($req['attachments']) || !empty($req['contextFolders'])) return 'files';
    $agent = (string)($req['agent'] ?? '');
    if (co_gateway_agent($agent) !== null) return 'gateway';
    $devices = is_array($req['devices'] ?? null) ? $req['devices'] : [];
    foreach ($devices as $d) {
        if (is_string($d) && str_starts_with($d, 'w-')) return 'browser devices';
    }
    if ($devices && $to === 'site' && $agent !== 'claude') return 'devices';
    return null;
}

/**
 * owner.json of $host (kv host:<host>/owner) when it is alive, takes jobs and takes them from the database (a job_claim
 * within 60 s, db.php meta claim_poll_<host>: Bridge, site agent 1.6.0+), else null.
 */
function co_handover_target(mysqli $db, string $host): ?array
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['host:' . $host, 'owner']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $o = $row ? json_decode((string)$row[0], true) : null;
    if (is_array($o['data'] ?? null)) $o = $o['data'];
    if (!is_array($o)) return null;
    if (in_array($o['state'] ?? 'running', ['updating', 'paused'], true)) return null;
    $t = strtotime((string)($o['updatedUtc'] ?? ''));
    // the site's owner.json reaches the database through Drive (slower); its job_claim is the proof of life
    if ($t === false || time() - $t >= ($host === 'site' ? 900 : 240)) return null;
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([co_queue_poll_key($host)]);
    $poll = $st->get_result()->fetch_row()[0] ?? null;
    $st->close();
    $pt = $poll !== null ? strtotime($poll . ' UTC') : false;
    return $pt !== false && time() - $pt < 60 ? $o : null;
}

function co_handover_has_agent(array $owner, string $agent): bool
{
    if (is_array($owner['agents'] ?? null)) return array_key_exists($agent, $owner['agents']);
    return $agent === 'claude';
}

/** First lines of a moved instruction: the environment note below it names the first executor. */
function co_handover_note(string $from, string $to): string
{
    $where = $to === 'site'
        ? 'the server agent (shared hosting, Linux; home devices of the environment, if any, come as MCP servers dev_<name>)'
        : 'the home computer (Windows, BridgeAgent, Google Drive mounted)';
    $fromName = $from === 'site' ? 'Site' : 'Home';
    return "[CO: this job was queued for {$fromName}, which did not take it within a minute, so it runs on {$where}. "
        . "Where the AI Workbench environment note below names the executor, this line wins.]\n\n";
}

/** meta key with the time of the last job_claim of a host (written by db.php job_claim); same as in co_queue_fallback. */
function co_queue_poll_key(string $host): string
{
    return strlen($host) <= 29 ? 'claim_poll_' . $host : 'claim_poll_#' . substr(md5($host), 0, 28);
}

/** Trashes inbox/<jobId> of the host on Drive (the fallback or attachment copy of a withdrawn job). */
function co_queue_drop_drive_copy(string $host, string $jobId): bool
{
    $inbox = co_fin_inbox_folder($host);
    if ($inbox === null) {
        return false;
    }
    $q = sprintf("'%s' in parents and name='%s' and mimeType='%s' and trashed=false", $inbox, $jobId, CO_FIN_FOLDER_MIME);
    $list = co_drive_get('files', ['q' => $q, 'fields' => 'files(id)', 'pageSize' => 5]);
    $n = 0;
    foreach ($list['files'] ?? [] as $f) {
        co_drive_send('PATCH', 'https://www.googleapis.com/drive/v3/files/' . rawurlencode((string)$f['id']) . '?fields=id',
            '{"trashed":true}', 'application/json');
        $n++;
    }
    return $n > 0;
}
