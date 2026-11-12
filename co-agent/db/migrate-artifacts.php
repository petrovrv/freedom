<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// migrate-artifacts.php: one-off (2026-10-28, docs/SPEC-chat-files.md §7). A legacy artifact copy (artifacts/<job>/<path>)
// whose content equals the live chat file (scratch/<path>, same sha256) is a duplicate: the message record is rewritten
// to point at the live file (ref, md5) and the copy goes to the Drive trash. Other copies stay as older versions until
// the daily cleanup (30 days). Events and jobs.result are rewritten; chat_files heads get the md5.
//   php ~/co-agent/db/migrate-artifacts.php [--apply] [--limit N]
// Ran once on 2026-10-28 (431 copies, 84 messages); a step that fails after an event update is not redone by a rerun.
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';

$agent = CO_AGENT_WEB;
require_once $agent . '/db-lib.php';
require_once $agent . '/drive-lib.php';
require_once $agent . '/files-lib.php';

$apply = in_array('--apply', $argv, true);
$limit = PHP_INT_MAX;
foreach ($argv as $i => $a) {
    if ($a === '--limit') {
        $limit = (int)($argv[$i + 1] ?? 0);
    }
}
$db = co_db();

// Folder listing cache: folder id -> name -> file meta.
$kids = [];
$list = static function (string $folder) use (&$kids): array {
    if (!isset($kids[$folder])) {
        $out = [];
        $token = null;
        do {
            $q = ['q' => sprintf("'%s' in parents and trashed = false", $folder), 'pageSize' => 1000,
                'fields' => 'nextPageToken,files(id,name,mimeType,md5Checksum,sha256Checksum,size)',
                'supportsAllDrives' => 'true', 'includeItemsFromAllDrives' => 'true'];
            if ($token) {
                $q['pageToken'] = $token;
            }
            $r = co_drive_get('files', $q);
            foreach ($r['files'] ?? [] as $f) {
                $out[$f['name']] ??= $f;
            }
            $token = $r['nextPageToken'] ?? null;
        } while ($token);
        $kids[$folder] = $out;
    }
    return $kids[$folder];
};
$find = static function (string $chatFolder, string $chatId, string $path) use ($list): ?array {
    $at = strpos($path, '/chats/' . $chatId . '/');
    if ($at === false) {
        return null;
    }
    $id = $chatFolder;
    $f = null;
    foreach (explode('/', substr($path, $at + strlen('/chats/' . $chatId . '/'))) as $name) {
        $f = $list($id)[$name] ?? null;
        if ($f === null) {
            return null;
        }
        $id = (string)$f['id'];
    }
    return $f;
};

$rows = $db->query(
    "SELECT e.event_id, e.chat_id, e.job_id, e.data, c.folder_id, c.project_id FROM events e JOIN chats c ON c.chat_id = e.chat_id
      WHERE e.type = 'assistant_message' AND e.data LIKE '%/artifacts/%' ORDER BY e.created_utc"
)->fetch_all(MYSQLI_ASSOC);
$stats = ['events' => 0, 'records' => 0, 'duplicates' => 0, 'bytes' => 0, 'copyMissing' => 0, 'liveMissing' => 0, 'differs' => 0, 'errors' => 0];
$done = 0;
foreach ($rows as $r) {
    if ($done >= $limit) {
        break;
    }
    if (!$r['folder_id']) {
        continue;
    }
    $ev = json_decode((string)$r['data'], true);
    $map = [];
    foreach ($ev['artifacts'] ?? [] as $i => $a) {
        $p = str_replace('\\', '/', (string)($a['driveRelativePath'] ?? ''));
        if (!empty($a['ref']) || !preg_match('#/artifacts/[^/]+/(.+)$#', $p, $m)) {
            continue;
        }
        $stats['records']++;
        $src = str_replace('\\', '/', (string)($a['sourceRelativePath'] ?? $m[1]));
        $live = 'projects/' . $r['project_id'] . '/chats/' . $r['chat_id'] . '/scratch/' . $src;
        try {
            $lf = $find((string)$r['folder_id'], (string)$r['chat_id'], $live);
            if (!$lf) {
                $stats['liveMissing']++;
                continue;
            }
            $want = strtolower((string)($a['sha256'] ?? ''));
            if ($want === '' || strtolower((string)($lf['sha256Checksum'] ?? '')) !== $want) {
                $stats['differs']++;
                continue;
            }
            $cf = $find((string)$r['folder_id'], (string)$r['chat_id'], $p);
            if ($cf && strtolower((string)($cf['sha256Checksum'] ?? '')) !== $want) {
                $stats['differs']++; // the copy itself is not the recorded content: left alone
                continue;
            }
            $stats['duplicates']++;
            if (!$cf) {
                $stats['copyMissing']++;
            } else {
                $stats['bytes'] += (int)($cf['size'] ?? 0);
            }
            $ev['artifacts'][$i]['driveRelativePath'] = $live;
            $ev['artifacts'][$i]['md5'] = (string)$lf['md5Checksum'];
            $ev['artifacts'][$i]['ref'] = true;
            $map[$p] = $ev['artifacts'][$i] + ['_copy' => $cf];
        } catch (Throwable $e) {
            $stats['errors']++;
            fwrite(STDERR, "{$r['chat_id']} $p: " . $e->getMessage() . "\n");
        }
    }
    if (!$map) {
        continue;
    }
    $stats['events']++;
    $done++;
    $clean = array_map(static fn(array $a): array => array_diff_key($a, ['_copy' => 1]), $map);
    if (!$apply) {
        continue;
    }
    // No co_db(true) here: co_db_upsert caches prepared statements of the first connection.
    // References first: the copies are trashed only after the message and the job result point at the live file.
    // Messages live only in the database since 2026-10-16 and drive-sync re-reads a result.json only when Drive reports
    // it changed (completed jobs never change), so no Drive copy brings a legacy path back; CO Web 0.55 also falls back
    // from a missing copy to the live file with the same content.
    $put = co_db_put(['events' => [$ev]]);
    if (!empty($put['rejected'])) {
        $stats['errors']++;
        fwrite(STDERR, "{$r['event_id']}: event update rejected, copies kept
");
        continue;
    }
    // The job result carries the same records.
    if ($r['job_id']) {
        $st = $db->prepare('SELECT result FROM jobs WHERE job_id = ?');
        $st->execute([$r['job_id']]);
        $res = json_decode((string)($st->get_result()->fetch_row()[0] ?? ''), true);
        $st->close();
        if (is_array($res) && is_array($res['artifacts'] ?? null)) {
            foreach ($res['artifacts'] as $i => $a) {
                $p = str_replace('\\', '/', (string)($a['driveRelativePath'] ?? ''));
                if (isset($clean[$p])) {
                    $res['artifacts'][$i] = $clean[$p];
                }
            }
            $st = $db->prepare('UPDATE jobs SET result = ? WHERE job_id = ?');
            $j = co_db_json($res);
            $st->bind_param('ss', $j, $r['job_id']);
            $st->execute();
            $st->close();
        }
    }
    foreach ($map as $p => $a) {
        if (!empty($a['_copy'])) {
            co_files_drive_trash((string)$a['_copy']['id']);
            co_files_trash_logged($db, $p, (string)$a['_copy']['id'], 'duplicate', (int)($a['_copy']['size'] ?? 0));
        }
    }
    // The stream head gets the md5 when this record is its head content.
    foreach ($map as $a) {
        $st = $db->prepare('UPDATE chat_files SET head_md5 = ?, legacy = 0 WHERE chat_id = ? AND path_sha = ? AND head_sha256 = ?');
        $md5 = (string)$a['md5'];
        $ps = sha1((string)$a['driveRelativePath']);
        $sha = strtolower((string)$a['sha256']);
        $st->bind_param('ssss', $md5, $r['chat_id'], $ps, $sha);
        $st->execute();
        $st->close();
    }
}
$stats['mb'] = round($stats['bytes'] / 1048576, 1);
echo json_encode($stats + ['mode' => $apply ? 'apply' : 'dry run']), "\n";
