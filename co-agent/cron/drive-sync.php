<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Drive → database sync (step 2 of the Drive → MariaDB migration, 2026-10-15). Drive stays the source of truth.
// Reads the owner's Drive changes feed (one page token in meta.drive_page_token) and upserts changed project.json,
// chat.json, chat events, job request/result and host/project settings into the database; trashed or removed files
// delete their rows through drive_map. Cron (hPanel), every minute:
//   /usr/bin/php ~/co-agent/cron/drive-sync.php
declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}

require_once __DIR__ . '/boot.php';
require CO_AGENT_WEB . '/drive-lib.php';
require CO_AGENT_WEB . '/finalize-lib.php';

// S7: jobs queued for the site in the database start the site agent even when no CO Web pings it (the agent 1.6.0
// takes them with job_claim within seconds; ping.php, job_submit and subtask_spawn start it too).
try {
    $n = (int)co_db()->query("SELECT COUNT(*) FROM jobs WHERE host_id = 'site' AND origin = 'db' AND state = 'inbox'
                                AND cancel_requested = 0 AND result IS NULL
                                AND queued_utc > UTC_TIMESTAMP(3) - INTERVAL 1 DAY")->fetch_row()[0];
    if ($n > 0 && co_site_agent_kick()) {
        echo "drive-sync: site agent started for {$n} queued job(s)\n";
    }
} catch (Throwable $e) {
    fwrite(STDERR, 'site agent kick: ' . $e->getMessage() . "\n");
}

function co_sync_json(string $raw): ?array
{
    if (str_starts_with($raw, "\xEF\xBB\xBF")) {
        $raw = substr($raw, 3);
    }
    $data = json_decode($raw, true);
    return is_array($data) ? $data : null;
}

// Classifies one changed Drive file and queues its record for co_db_put plus its drive_map row.
function co_sync_classify(array $file, array &$put, array &$maps): void
{
    if (!empty($file['trashed'])) {
        return;
    }
    $id = (string)($file['id'] ?? '');
    $name = (string)($file['name'] ?? '');
    if ($id === '' || $name === '') {
        return;
    }

    $str = static fn(?array $d, string $k): bool => is_string($d[$k] ?? null) && $d[$k] !== '';
    $fetch = static fn(): ?array => co_sync_json(co_drive_download($id));
    $dec = static fn(mixed $v): ?array => is_string($v) && $v !== '' ? co_sync_json($v) : null;
    $query = static function (string $sql, string $param): ?array {
        $st = co_db()->prepare($sql);
        if ($st === false || !$st->execute([$param])) {
            return null;
        }
        $res = $st->get_result();
        $row = $res ? $res->fetch_assoc() : null;
        $st->close();
        return $row ?: null;
    };

    if (preg_match('/^\d{6}-[a-z_]+-EVT-[A-Za-z0-9-]+\.json$/', $name)) {
        $d = $fetch();
        if (!$str($d, 'eventId') || !$str($d, 'chatId')) {
            return;
        }
        $put['events'][] = $d;
        $maps[] = [$id, 'event', $d['eventId']];
        return;
    }

    switch ($name) {
        case 'chat.json':
            $d = $fetch();
            if (!$str($d, 'chatId') || !$str($d, 'projectId')) {
                return;
            }
            $parent = $file['parents'][0] ?? null;
            if (!is_string($parent) || (co_drive_ancestor_names($id, 1)[0] ?? null) !== $d['chatId']) {
                return;
            }
            $put['chats'][] = $d;
            $maps[] = [$id, 'chat', $d['chatId']];
            $maps[] = [$parent, 'chatdir', $d['chatId']];
            return;

        case 'project.json':
            $d = $fetch();
            if (!$str($d, 'projectId') || (co_drive_ancestor_names($id, 1)[0] ?? null) !== $d['projectId']) {
                return;
            }
            $put['projects'][] = $d;
            $maps[] = [$id, 'project', $d['projectId']];
            if (is_string($file['parents'][0] ?? null)) {
                $maps[] = [$file['parents'][0], 'projectdir', $d['projectId']]; // schema 3: projects.folder_id
            }
            return;

        case 'request.json':
        case 'result.json':
            $a = co_drive_ancestor_names($id, 4);
            if (($a[3] ?? null) !== 'hosts') {
                return;
            }
            [$jobId, $state, $host] = [$a[0] ?? null, $a[1] ?? null, $a[2] ?? null];
            if (!is_string($jobId) || $jobId === '' || !is_string($state) || !is_string($host) || $host === '') {
                return;
            }
            $row = $query('SELECT request, result FROM jobs WHERE job_id=?', $jobId);
            $isReq = $name === 'request.json';
            if ($isReq) {
                $req = $fetch();
                if (!$str($req, 'jobId') || $req['jobId'] !== $jobId) {
                    return;
                }
                $res = $dec($row['result'] ?? null);
            } else {
                $res = $fetch();
                $req = $dec($row['request'] ?? null);
                if ($res === null || $req === null) {
                    return; // the request comes with request.json
                }
            }
            $put['jobs'][] = ['hostId' => $host, 'state' => $state, 'request' => $req, 'result' => $res];
            $maps[] = [$id, $isReq ? 'job_request' : 'job_result', $jobId];
            return;

        case 'owner.json':
        case 'limits.json':
        case 'cloud-connectors.json':
            $a = co_drive_ancestor_names($id, 2);
            if (($a[1] ?? null) !== 'hosts' || !is_string($a[0] ?? null) || $a[0] === '') {
                return;
            }
            $scope = 'host:' . $a[0];
            $kvName = substr($name, 0, -5);
            break;

        case 'schedules.json':
            $p = co_drive_ancestor_names($id, 1)[0] ?? null;
            if (!is_string($p) || $p === '' || $query('SELECT 1 FROM projects WHERE project_id=?', $p) === null) {
                return;
            }
            $scope = 'project:' . $p;
            $kvName = 'schedules';
            break;

        default:
            return;
    }

    $d = $fetch();
    if ($d === null) {
        return;
    }
    $put['kv'][] = ['scope' => $scope, 'name' => $kvName, 'data' => $d];
    $maps[] = [$id, 'kv', $scope . '/' . $kvName];
}

// Deletes the rows behind a removed/trashed Drive file (known through drive_map); returns affected rows.
function co_sync_remove(string $fileId): int
{
    $db = co_db();
    $st = $db->prepare('SELECT kind, rec_id FROM drive_map WHERE file_id=?');
    $st->execute([$fileId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row) {
        return 0;
    }
    $id = (string)$row['rec_id'];
    $queries = match ((string)$row['kind']) {
        // 2026-10-16: messages live only in the database; a removed message file (cleanup) deletes nothing.
        'event' => [],
        'chatdir' => [
            ['DELETE FROM events WHERE chat_id=?', [$id]],
            ['DELETE FROM protocol WHERE chat_id=?', [$id]],
            ['DELETE FROM chats WHERE chat_id=?', [$id]],
        ],
        'project', 'projectdir' => [['DELETE FROM projects WHERE project_id=?', [$id]]],
        // Schema 4: jobs are history and queue state; transport cleanup and folder moves on Drive keep the row.
        'job_request', 'job_result' => [],
        'kv' => [['DELETE FROM kv WHERE scope=? AND name=?', array_pad(explode('/', $id, 2), 2, '')]],
        default => [],
    };
    $n = 0;
    foreach ($queries as [$sql, $params]) {
        $st = $db->prepare($sql);
        $st->execute($params);
        $n += max(0, $st->affected_rows);
        $st->close();
    }
    $st = $db->prepare('DELETE FROM drive_map WHERE file_id=?');
    $st->execute([$fileId]);
    $st->close();
    return $n;
}

$lock = fopen(co_agent_home() . '/co-agent/state/drive-sync.lock', 'c');
if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) {
    exit(0);
}

try {
    $db = co_db();
    // 2026-10-29: the host closes connections idle for 20 s; a page of 1000 changes reads Drive files much longer
    $db->query('SET SESSION wait_timeout=1800');
    $res = $db->query("SELECT v FROM meta WHERE k='drive_page_token'")->fetch_assoc();
    if (!$res || (string)$res['v'] === '') {
        $st = $db->prepare("INSERT INTO meta (k, v) VALUES ('drive_page_token', ?)");
        $st->execute([co_drive_start_token()]);
        echo "drive-sync: initialized token\n";
        exit(0);
    }
    $token = (string)$res['v'];
    $mapSt = $db->prepare('INSERT INTO drive_map (file_id, kind, rec_id, updated_utc) VALUES (?,?,?,UTC_TIMESTAMP(3))
        ON DUPLICATE KEY UPDATE kind=VALUES(kind), rec_id=VALUES(rec_id), updated_utc=VALUES(updated_utc)');
    $tot = ['changes' => 0, 'inserted' => 0, 'updated' => 0, 'same' => 0, 'deleted' => 0];

    $tally = static function (array $r) use (&$tot): void {
        foreach (['inserted', 'updated', 'same'] as $k) {
            $tot[$k] += (int)($r[$k] ?? 0);
        }
        foreach ($r['rejected'] ?? [] as $rej) {
            fwrite(STDERR, "drive-sync: rejected {$rej['kind']} {$rej['id']}: {$rej['reason']}\n");
        }
    };

    while (true) {
        $page = co_drive_changes($token);
        $put = ['projects' => [], 'chats' => [], 'events' => [], 'jobs' => [], 'kv' => []];
        $maps = [];
        $deleted = 0;
        foreach ($page['changes'] ?? [] as $change) {
            $tot['changes']++;
            $file = $change['file'] ?? null;
            if (!empty($change['removed']) || !$file || !empty($file['trashed'])) {
                $deleted += co_sync_remove((string)$change['fileId']);
            } elseif (($file['mimeType'] ?? '') === 'application/vnd.google-apps.folder') {
                continue;
            } elseif (($file['name'] ?? '') === 'desktop.ini') {
                continue;
            } else {
                try {
                    co_sync_classify($file, $put, $maps);
                } catch (Throwable $e) {
                    fwrite(STDERR, "drive-sync: {$file['name']} ({$file['id']}): {$e->getMessage()}\n");
                }
            }
        }
        $tot['deleted'] += $deleted;

        $core = array_filter(array_intersect_key($put, array_flip(['projects', 'chats', 'jobs', 'kv'])));
        if ($core) {
            $tally(co_db_put($core));
        }
        foreach (array_chunk($put['events'], 500) as $chunk) {
            $tally(co_db_put(['events' => $chunk]));
        }
        foreach ($maps as [$fid, $kind, $recId]) {
            $mapSt->execute([(string)$fid, (string)$kind, (string)$recId]);
            // Schema 3: Drive folder ids for database-first reads in CO Web
            if ($kind === 'chatdir') {
                $db->prepare('UPDATE chats SET folder_id=? WHERE chat_id=?')->execute([(string)$fid, (string)$recId]);
            } elseif ($kind === 'projectdir') {
                $db->prepare('UPDATE projects SET folder_id=? WHERE project_id=?')->execute([(string)$fid, (string)$recId]);
            }
        }

        // CO Web 0.20.0: a result that reached Drive also gets its answer into the chat (recent jobs only).
        foreach ($put['jobs'] as $j) {
            $res = $j['result'] ?? null;
            $done = is_array($res) ? strtotime((string)($res['completedUtc'] ?? '')) : false;
            if ($done === false || $done < time() - 2 * 86400) {
                continue;
            }
            try {
                $host = (string)($j['hostId'] ?? $res['hostId'] ?? ''); // the transport folder of the host that ran it
                if (($eid = co_job_finished($host, $j['request'], $res)) !== null) {
                    echo "drive-sync: answer {$eid} for {$j['request']['jobId']}\n";
                }
            } catch (Throwable $e) {
                fwrite(STDERR, "drive-sync: finalize {$j['request']['jobId']}: {$e->getMessage()}\n");
            }
        }

        if (!empty($page['nextPageToken'])) {
            $token = (string)$page['nextPageToken'];
            continue;
        }
        if (empty($page['newStartPageToken'])) {
            throw new RuntimeException('no page token returned');
        }
        $st = $db->prepare("REPLACE INTO meta VALUES ('drive_page_token', ?)");
        $st->execute([(string)$page['newStartPageToken']]);
        // Health mark for cron/watch-cron.php ("database sync stopped" push)
        $db->query("REPLACE INTO meta VALUES ('drive_sync_ok', DATE_FORMAT(UTC_TIMESTAMP(), '%Y-%m-%dT%H:%i:%sZ'))");
        break;
    }

    if ($tot['changes'] > 0) {
        echo "drive-sync: {$tot['changes']} changes, inserted {$tot['inserted']}, updated {$tot['updated']}, "
            . "same {$tot['same']}, deleted {$tot['deleted']}\n";
    }
} catch (Throwable $e) {
    fwrite(STDERR, 'drive-sync: fatal: ' . $e->getMessage() . "\n");
    exit(1);
}

// Nightly database copy to Drive (once a day at 03:xx Brussels); its failure does not affect the sync.
require __DIR__ . '/db-backup.php';
try {
    $msg = co_db_backup();
    if ($msg !== null) {
        echo $msg . "\n";
    }
} catch (Throwable $e) {
    fwrite(STDERR, $e->getMessage() . "\n");
}

// S8: an 'auto' job its executor has not taken for 60 s goes once to the other live executor (before the Drive fallback).
try {
    foreach (co_queue_handover() as $line) {
        echo "drive-sync: queue handover {$line}\n";
    }
} catch (Throwable $e) {
    fwrite(STDERR, 'queue handover: ' . $e->getMessage() . "\n");
}

// Schema 5: database jobs that no Bridge has claimed for 90 s also go to the Drive inbox.
try {
    foreach (co_queue_fallback() as $jid) {
        echo "drive-sync: queue fallback {$jid} -> Drive inbox\n";
    }
} catch (Throwable $e) {
    fwrite(STDERR, 'queue fallback: ' . $e->getMessage() . "\n");
}

// Schema 12 (2026-10-28): files of new chat messages -> chat_files (web/agent/files-lib.php).
try {
    require_once CO_AGENT_WEB . '/files-lib.php';
    co_files_sync(500);
} catch (Throwable $e) {
    fwrite(STDERR, 'files sync: ' . $e->getMessage() . "
");
}

// Schema 15 (2026-11-01): per-job timeline and cost (web/agent/jobstats-lib.php), built while job_events still exist.
try {
    require_once CO_AGENT_WEB . '/jobstats-lib.php';
    co_job_stats_fill(co_db(), 60, 8.0);
} catch (Throwable $e) {
    fwrite(STDERR, 'job stats: ' . $e->getMessage() . "\n");
}

// Schema 17 (2026-11-03): causes of failed jobs (web/agent/failures-lib.php), built while job_events still exist.
try {
    require_once CO_AGENT_WEB . '/failures-lib.php';
    co_job_failures_fill(co_db(), 60, 5.0, 2);
} catch (Throwable $e) {
    fwrite(STDERR, 'job failures: ' . $e->getMessage() . "\n");
}

// Schema 4: Bridge event batches are kept 14 days since 2026-10-28, 30 before (jobs keep their request and result for good).
try {
    if (random_int(0, 59) === 0) {
        co_db()->query('DELETE FROM job_events WHERE created_utc < UTC_TIMESTAMP(3) - INTERVAL 14 DAY');
    }
} catch (Throwable $e) {
    fwrite(STDERR, 'job_events cleanup: ' . $e->getMessage() . "\n");
}
