<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Nightly database copy (2026-10-16): cron/drive-sync.php calls co_db_backup() every minute; from 03:00
// Europe/Brussels it starts the copy once a day. mysqldump | gzip -> ~/co-agent/backups/db-YYYY-MM-DD.sql.gz
// (14 days) and Google Drive AI-Workbench/backups/db (30 newest kept).
// 2026-10-27: the dump grew to ~150 MB and the old inline run was cut off by cron every minute (20 orphan dump-*
// files, no copy since 21.10). Now the copy runs in a detached process (setsid) under its own flock, at most
// 3 attempts a day 50+ min apart, limit 400 MB, stale dump-* removed, Drive upload resumable in 8 MB chunks.
// Manual run (inline, counts as an attempt): /usr/bin/php ~/co-agent/cron/db-backup.php --run
declare(strict_types=1);
require_once __DIR__ . '/boot.php';

const CO_DB_BACKUP_MAX_BYTES = 400 * 1024 * 1024;
const CO_DB_BACKUP_TRIES = 3;
const CO_DB_BACKUP_RETRY_SEC = 50 * 60;
const CO_DB_BACKUP_LAUNCH_SEC = 5 * 60;
const CO_DB_BACKUP_CHUNK = 8 * 1024 * 1024; // multiple of 256 KiB (Drive resumable upload)

/** Called every minute by drive-sync: starts the detached copy when due; returns a log line or null. */
function co_db_backup(bool $force = false): ?string
{
    if ($force) {
        return co_db_backup_run();
    }
    $now = new DateTimeImmutable('now', new DateTimeZone('Europe/Brussels'));
    $day = $now->format('Y-m-d');
    if ((int) $now->format('G') < 3 || co_db_backup_meta('db_backup_day') === $day) {
        return null;
    }
    // The run itself counts attempts (db_backup_try = "<day>|<attempts>|<epoch>") once it holds the lock, so a launch
    // that never reached PHP (setsid, php, log file) costs nothing and is retried after CO_DB_BACKUP_LAUNCH_SEC.
    [$tday, $tries, $at] = array_pad(explode('|', (string) co_db_backup_meta('db_backup_try')), 3, '');
    $tries = $tday === $day ? (int) $tries : 0;
    if ($tries >= CO_DB_BACKUP_TRIES || ($tday === $day && time() - (int) $at < CO_DB_BACKUP_RETRY_SEC)
        || time() - (int) co_db_backup_meta('db_backup_launch') < CO_DB_BACKUP_LAUNCH_SEC) {
        return null;
    }
    co_db_backup_meta('db_backup_launch', (string) time());

    $log = co_agent_home() . '/co-agent/state/db-backup.log';
    $cmd = 'setsid nohup /usr/bin/php ' . escapeshellarg(__FILE__) . ' --run >> ' . escapeshellarg($log) . ' 2>&1 < /dev/null &';
    $p = proc_open(['/bin/sh', '-c', $cmd], [], $pipes);
    $rc = is_resource($p) ? proc_close($p) : -1;
    if ($rc !== 0) {
        throw new RuntimeException("db-backup: cannot launch the detached run (rc=$rc)");
    }
    return sprintf('db-backup: launched attempt %d/%d for %s (log %s)', $tries + 1, CO_DB_BACKUP_TRIES, $day, $log);
}

/** Reads ($v null) or writes a meta value; $fresh reconnects (the detached copy runs for minutes, the host drops idle
 *  connections). drive-sync shares co_db() with its own code, so the minute check never reconnects. */
function co_db_backup_meta(string $k, ?string $v = null, bool $fresh = false): ?string
{
    $db = co_db($fresh);
    if ($v === null) {
        $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
        $st->execute([$k]);
        $r = $st->get_result()->fetch_row()[0] ?? null;
        $st->close();
        return $r === null ? null : (string) $r;
    }
    $st = $db->prepare('INSERT INTO meta (k, v) VALUES (?, ?) ON DUPLICATE KEY UPDATE v = VALUES(v)');
    $st->execute([$k, $v]);
    $st->close();
    return $v;
}

/** One attempt: counts it, makes the copy; push to the owner when the last attempt of the day fails. Only one run at
 *  a time (flock); a second one returns at once. */
function co_db_backup_run(): string
{
    $home = co_agent_home();
    $lock = fopen($home . '/co-agent/state/db-backup.lock', 'c');
    if ($lock === false) {
        throw new RuntimeException("db-backup: cannot open $home/co-agent/state/db-backup.lock");
    }
    if (!flock($lock, LOCK_EX | LOCK_NB)) {
        return 'db-backup: another run is in progress';
    }

    $now = new DateTimeImmutable('now', new DateTimeZone('Europe/Brussels'));
    $day = $now->format('Y-m-d');
    [$tday, $tries] = array_pad(explode('|', (string) co_db_backup_meta('db_backup_try', null, true)), 2, '');
    $tries = ($tday === $day ? (int) $tries : 0) + 1;
    co_db_backup_meta('db_backup_try', "$day|$tries|" . time());
    try {
        return co_db_backup_make($home, $now);
    } catch (Throwable $e) {
        if ($tries >= CO_DB_BACKUP_TRIES) {
            try {
                co_push_broadcast(
                    static fn($sub): bool => is_array($sub) && ($sub['env'] ?? '') === co_owner_env(),
                    ['title' => 'DB backup failed', 'body' => "$day, attempt $tries: " . substr($e->getMessage(), 0, 160), 'url' => '/', 'tag' => 'db-backup'],
                    'db-backup'
                );
            } catch (Throwable $pe) {
                error_log('db-backup push: ' . $pe->getMessage());
            }
        }
        throw $e;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

/** mysqldump | gzip into ~/co-agent/backups, then the Drive upload and rotation. */
function co_db_backup_make(string $home, DateTimeImmutable $now): string
{
    $day = $now->format('Y-m-d');
    $dir = $home . '/co-agent/backups';
    if (!is_dir($dir) && !mkdir($dir, 0700, true) && !is_dir($dir)) {
        throw new RuntimeException("db-backup: cannot create $dir");
    }
    // We hold the lock, so every dump-* left here is from a run that was cut off.
    foreach (glob("$dir/dump-*") ?: [] as $stale) {
        @unlink($stale);
    }

    $name = "db-$day.sql.gz";
    $file = "$dir/$name";
    $tmp = tempnam($dir, 'dump-');
    if ($tmp === false) {
        throw new RuntimeException('db-backup: cannot create temp file');
    }

    $dbName = (string)(json_decode((string)@file_get_contents($home . '/co-agent/secrets/db.json'), true)['db'] ?? '');
    if (!preg_match('/^[A-Za-z0-9_]+$/', $dbName)) {
        throw new RuntimeException('db-backup: no database name in secrets/db.json');
    }
    try {
        $args = ['/usr/bin/mysqldump', '--defaults-extra-file=' . $home . '/co-agent/secrets/my.cnf', '--single-transaction',
            '--quick', '--routines', '--default-character-set=utf8mb4',
            // docs is the aiwb-docs search index (~60% of the dump): only its structure is kept, the indexer
            // on the home PC rebuilds the rows from the files on Drive after a restore.
            '--ignore-table-data=' . $dbName . '.docs', $dbName];
        // head caps the file on disk at the limit + 1 byte, so an oversized dump never fills the disk.
        $cmd = '{ ' . implode(' ', array_map('escapeshellarg', $args)) . ' || echo "__DUMP_FAILED__ $?" >&2; } | gzip -6 | head -c '
            . (CO_DB_BACKUP_MAX_BYTES + 1) . ' > ' . escapeshellarg($tmp);
        $proc = proc_open(['/bin/sh', '-c', $cmd], [0 => ['file', '/dev/null', 'r'], 1 => ['file', '/dev/null', 'w'], 2 => ['pipe', 'w']], $pipes);
        if (!is_resource($proc)) {
            throw new RuntimeException('db-backup: proc_open failed');
        }
        $stderr = (string) stream_get_contents($pipes[2]);
        fclose($pipes[2]);
        $rc = proc_close($proc);
        clearstatcache(true, $tmp);
        $size = (int) filesize($tmp);
        if ($size > CO_DB_BACKUP_MAX_BYTES) {
            throw new RuntimeException('db-backup: dump too large (over ' . CO_DB_BACKUP_MAX_BYTES . ' bytes compressed)');
        }
        if ($rc !== 0 || str_contains($stderr, '__DUMP_FAILED__') || $size <= 1000) {
            throw new RuntimeException("db-backup: dump failed (rc=$rc, size=$size): " . substr(trim($stderr), 0, 500));
        }
        // The pipeline status is head's: accept the file only if the archive is whole and mysqldump finished.
        $chk = proc_open(['/bin/sh', '-c', 'gzip -t ' . escapeshellarg($tmp) . ' && gzip -cd ' . escapeshellarg($tmp) . ' | tail -c 300'],
            [0 => ['file', '/dev/null', 'r'], 1 => ['pipe', 'w'], 2 => ['file', '/dev/null', 'w']], $cp);
        $tail = is_resource($chk) ? (string) stream_get_contents($cp[1]) : '';
        if (is_resource($chk)) {
            fclose($cp[1]);
            proc_close($chk);
        }
        if (!str_contains($tail, '-- Dump completed')) {
            throw new RuntimeException("db-backup: dump archive is broken or incomplete (size=$size)");
        }
        if (!rename($tmp, $file)) {
            throw new RuntimeException("db-backup: cannot move dump to $file");
        }
        chmod($file, 0600);
    } finally {
        if (is_file($tmp)) {
            @unlink($tmp);
        }
    }

    $cutoff = $now->modify('-14 days')->format('Y-m-d');
    foreach (glob("$dir/db-*.sql.gz") ?: [] as $old) {
        if (preg_match('/db-(\d{4}-\d{2}-\d{2})\.sql\.gz$/', $old, $m) && $m[1] < $cutoff) {
            @unlink($old);
        }
    }

    $api = function (string $method, string $url, string $body, string $type = 'application/json; charset=UTF-8'): array {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 120,
            CURLOPT_HTTPHEADER => ['Authorization: Bearer ' . co_drive_token(), 'Content-Type: ' . $type],
        ]);
        $res = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        $err = curl_error($ch);
        curl_close($ch);
        if ($res === false || $code < 200 || $code >= 300) {
            throw new RuntimeException("db-backup: Drive $method HTTP $code: " . substr($res === false ? $err : (string) $res, 0, 200));
        }
        return json_decode((string) $res, true) ?: [];
    };
    $json = fn(string $method, string $url, array $data): array => $api($method, $url, json_encode($data, JSON_THROW_ON_ERROR));

    $child = function (string $parent, string $n) use ($json): string {
        $q = sprintf("'%s' in parents and name='%s' and mimeType='application/vnd.google-apps.folder' and trashed=false", $parent, addcslashes($n, "\\'"));
        return co_drive_get('files', ['q' => $q, 'fields' => 'files(id)', 'pageSize' => 1])['files'][0]['id']
            ?? $json('POST', 'https://www.googleapis.com/drive/v3/files?fields=id', ['name' => $n, 'mimeType' => 'application/vnd.google-apps.folder', 'parents' => [$parent]])['id']
            ?? throw new RuntimeException("db-backup: cannot create folder $n");
    };

    // AI-Workbench root = grandparent of any project folder (projects/<id>)
    $pf = co_db(true)->query('SELECT folder_id FROM projects WHERE folder_id IS NOT NULL LIMIT 1')->fetch_row()[0] ?? null;
    $projects = $pf ? (co_drive_meta((string) $pf)['parents'][0] ?? null) : null;
    $root = $projects ? (co_drive_meta($projects)['parents'][0] ?? null) : null;
    if (!$root) {
        throw new RuntimeException('db-backup: cannot resolve AI-Workbench root folder');
    }
    $dbFolder = $child($child($root, 'backups'), 'db');

    // A copy of the same day uploaded by an earlier attempt or by hand is replaced, not duplicated.
    $same = co_drive_get('files', [
        'q' => sprintf("'%s' in parents and name='%s' and trashed=false", $dbFolder, $name),
        'fields' => 'files(id)',
        'pageSize' => 10,
    ])['files'] ?? [];
    $id = co_db_backup_upload($file, $name, $dbFolder);
    foreach ($same as $f) {
        if ($f['id'] !== $id) {
            $json('PATCH', 'https://www.googleapis.com/drive/v3/files/' . rawurlencode($f['id']), ['trashed' => true]);
        }
    }

    $files = co_drive_get('files', [
        'q' => "'$dbFolder' in parents and trashed=false",
        'orderBy' => 'name desc',
        'fields' => 'files(id,name)',
        'pageSize' => 1000,
    ])['files'] ?? [];
    foreach (array_slice($files, 30) as $f) {
        $json('PATCH', 'https://www.googleapis.com/drive/v3/files/' . rawurlencode($f['id']), ['trashed' => true]);
    }

    co_db_backup_meta('db_backup_day', $day, true);

    return sprintf('db-backup: %s %d KB uploaded, kept %d on Drive', $name, (int) round($size / 1024), min(count($files), 30));
}

/** Drive resumable upload from the file in CO_DB_BACKUP_CHUNK pieces (memory stays small); returns the file id. */
function co_db_backup_upload(string $file, string $name, string $folder): string
{
    $total = (int) filesize($file);
    $req = function (string $method, string $url, array $headers, ?string $body) {
        $ch = curl_init($url);
        $hdrs = [];
        curl_setopt_array($ch, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_POSTFIELDS => $body ?? '',
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 300,
            CURLOPT_HTTPHEADER => array_merge(['Authorization: Bearer ' . co_drive_token()], $headers),
            CURLOPT_HEADERFUNCTION => function ($c, string $h) use (&$hdrs) {
                $p = explode(':', $h, 2);
                if (count($p) === 2) {
                    $hdrs[strtolower(trim($p[0]))] = trim($p[1]);
                }
                return strlen($h);
            },
        ]);
        $res = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        $err = curl_error($ch);
        curl_close($ch);
        return [$code, $res === false ? $err : (string) $res, $hdrs];
    };

    $meta = json_encode(['name' => $name, 'parents' => [$folder], 'mimeType' => 'application/gzip'], JSON_THROW_ON_ERROR);
    [$code, $res, $h] = $req('POST', 'https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id',
        ['Content-Type: application/json; charset=UTF-8', 'X-Upload-Content-Type: application/gzip', "X-Upload-Content-Length: $total"], $meta);
    $session = $h['location'] ?? '';
    if ($code !== 200 || $session === '') {
        throw new RuntimeException("db-backup: Drive resumable start HTTP $code: " . substr($res, 0, 200));
    }

    $fh = fopen($file, 'rb');
    if ($fh === false) {
        throw new RuntimeException("db-backup: cannot read $file");
    }
    try {
        $offset = 0;
        $fails = 0;
        while (true) {
            $want = max(0, min(CO_DB_BACKUP_CHUNK, $total - $offset));
            $chunk = $want > 0 && fseek($fh, $offset) === 0 ? fread($fh, $want) : '';
            if ($chunk === false || strlen($chunk) !== $want) {
                throw new RuntimeException("db-backup: cannot read $file at $offset");
            }
            $end = $offset + strlen($chunk) - 1;
            $range = $chunk === '' ? "bytes */$total" : "bytes $offset-$end/$total";
            [$code, $res, $h] = $req('PUT', $session, ['Content-Type: application/gzip', "Content-Range: $range"], $chunk);
            if ($code === 200 || $code === 201) {
                $id = json_decode($res, true)['id'] ?? '';
                if ($id === '') {
                    throw new RuntimeException('db-backup: upload returned no file id');
                }
                return $id;
            }
            if ($code === 308) {
                // Drive tells how much it has: "Range: bytes=0-N" (absent = nothing yet)
                $offset = preg_match('/bytes=0-(\d+)/', $h['range'] ?? '', $m) ? (int) $m[1] + 1 : 0;
                $fails = 0;
                continue;
            }
            if (++$fails > 3 || ($code >= 400 && $code < 500 && $code !== 408 && $code !== 429)) {
                throw new RuntimeException("db-backup: Drive chunk at $offset HTTP $code: " . substr($res, 0, 200));
            }
            sleep(5 * $fails);
            // Ask Drive where to continue (empty body with "bytes */total"), the next loop resends from there.
            [$code, $res, $h] = $req('PUT', $session, ["Content-Range: bytes */$total"], '');
            if (($code === 200 || $code === 201) && ($id = json_decode($res, true)['id'] ?? '') !== '') {
                return $id;
            }
            if ($code === 308) {
                $offset = preg_match('/bytes=0-(\d+)/', $h['range'] ?? '', $m) ? (int) $m[1] + 1 : 0;
            }
        }
    } finally {
        fclose($fh);
    }
}

// Detached run started by co_db_backup() (or by hand): php db-backup.php --run
if (PHP_SAPI === 'cli' && realpath($_SERVER['argv'][0] ?? '') === __FILE__ && in_array('--run', $_SERVER['argv'], true)) {
    require_once CO_AGENT_WEB . '/drive-lib.php';
    require_once CO_AGENT_WEB . '/finalize-lib.php';
    $t = microtime(true);
    try {
        echo date('c') . ' ' . co_db_backup_run() . sprintf(' (%.0f s)', microtime(true) - $t) . "\n";
    } catch (Throwable $e) {
        echo date('c') . ' ' . $e->getMessage() . "\n";
        exit(1);
    }
}
