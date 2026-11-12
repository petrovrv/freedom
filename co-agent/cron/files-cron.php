<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// files-cron.php (schema 12, 2026-10-28): annotations and the daily cleanup of chat files (web/agent/files-lib.php).
// Called from watch-cron.php every 5 minutes. Manual run: php files-cron.php gc-report | gc-apply | annotate [N]
declare(strict_types=1);

require_once __DIR__ . '/boot.php';
require_once CO_AGENT_WEB . '/lib.php'; // co_agent_claude (also via db-lib.php)
require_once CO_AGENT_WEB . '/db-lib.php';
require_once CO_AGENT_WEB . '/drive-lib.php';
require_once CO_AGENT_WEB . '/files-lib.php';

const CO_FILES_TEXT_EXT = ['md', 'txt', 'csv', 'json', 'html', 'htm', 'xml', 'yaml', 'yml', 'tex', 'svg', 'ini', 'toml'];

/**
 * One-line annotations ("what is this document about") for up to $limit live files whose head changed, from the text
 * aiwb-docs indexed (docs) or, for small text files, the first 6 KB downloaded from Drive. One Haiku call per run.
 */
function co_files_annotate(int $limit = 6): int
{
    $db = co_db();
    $rows = $db->query(
        "SELECT f.id, f.chat_id, f.path, f.name, f.kind, c.folder_id, c.title FROM chat_files f JOIN chats c ON c.chat_id = f.chat_id
          WHERE f.state = 'live' AND f.head_sha256 IS NOT NULL AND (f.annotation_sha IS NULL OR f.annotation_sha <> f.head_sha256)
            AND f.head_utc < UTC_TIMESTAMP(3) - INTERVAL 3 MINUTE
            AND (f.annotation_utc IS NULL OR f.annotation_utc < UTC_TIMESTAMP(3) - INTERVAL 30 MINUTE)
            AND f.head_utc > UTC_TIMESTAMP(3) - INTERVAL 30 DAY
          ORDER BY f.head_utc DESC LIMIT " . max(1, min(20, $limit))
    )->fetch_all(MYSQLI_ASSOC);
    if (!$rows) {
        return 0;
    }
    $items = [];
    foreach ($rows as $r) {
        $text = '';
        $docPath = preg_replace('#^projects/#', '', (string)$r['path']);
        $st = $db->prepare('SELECT text FROM docs WHERE doc_key = ? ORDER BY chunk LIMIT 2');
        $key = sha1($docPath);
        $st->bind_param('s', $key);
        $st->execute();
        foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as [$t]) {
            $text .= $t . "\n";
        }
        $st->close();
        $ext = strtolower(pathinfo((string)$r['name'], PATHINFO_EXTENSION));
        if ($text === '' && $r['folder_id'] && (in_array($ext, CO_FILES_TEXT_EXT, true) || $r['kind'] === 'temp')) {
            try {
                $meta = co_files_drive_find((string)$r['folder_id'], (string)$r['path'], (string)$r['chat_id']);
                if ($meta && (int)($meta['size'] ?? 0) <= 2_000_000) {
                    $text = substr(co_drive_download((string)$meta['id'], 2_000_000), 0, 6000);
                }
            } catch (Throwable $e) {
                fwrite(STDERR, 'annotate ' . $r['path'] . ': ' . $e->getMessage() . "\n");
            }
        }
        $text = trim((string)preg_replace('/\s+/u', ' ', mb_convert_encoding($text, 'UTF-8', 'UTF-8')));
        $items[] = ['id' => (int)$r['id'], 'name' => (string)$r['name'], 'chat' => (string)$r['title'], 'text' => mb_substr($text, 0, 2500)];
    }
    $prompt = "For every file below write one very short annotation (max 120 characters): what the document is about or what "
        . "the code does, with the key subject (names, numbers). Use the language of the file's text; Russian if there is no "
        . "text. No intro, no file name repeated, no markdown. Answer with JSON only: {\"files\":[{\"id\":1,\"note\":\"...\"}]}\n\n"
        . json_encode(['files' => $items], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    $done = 0;
    try {
        $j = co_agent_json_from(co_agent_claude($prompt, 'haiku', 120));
    } catch (Throwable $e) {
        fwrite(STDERR, 'annotate: ' . $e->getMessage() . "\n");
        $j = [];
    }
    $db = co_db(true); // the call takes ~20 s; the idle connection may be gone
    $notes = [];
    foreach ($j['files'] ?? [] as $f) {
        if (is_array($f) && is_int($f['id'] ?? null) && is_string($f['note'] ?? null) && trim($f['note']) !== '') {
            $notes[$f['id']] = mb_substr(trim($f['note']), 0, 200);
        }
    }
    foreach ($rows as $r) {
        $id = (int)$r['id'];
        $note = $notes[$id] ?? null;
        // A file without an answer is retried after 30 minutes (annotation_utc), with a new head at once.
        $st = $db->prepare('UPDATE chat_files SET annotation = COALESCE(?, annotation), annotation_sha = IF(? IS NULL, annotation_sha, head_sha256),
                                   annotation_utc = UTC_TIMESTAMP(3) WHERE id = ?');
        $st->bind_param('ssi', $note, $note, $id);
        $st->execute();
        $st->close();
        $done += $note !== null ? 1 : 0;
    }
    return $done;
}

/** Daily at 04:xx Brussels: files-gc in the mode of meta.files_gc_mode (apply | report, default report). */
function co_files_gc_daily(): ?string
{
    $db = co_db();
    $now = new DateTimeImmutable('now', new DateTimeZone('Europe/Brussels'));
    if ((int)$now->format('G') < 4) {
        return null;
    }
    $day = $now->format('Y-m-d');
    $get = static function (string $k) use ($db): ?string {
        $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
        $st->execute([$k]);
        return $st->get_result()->fetch_row()[0] ?? null;
    };
    if ($get('files_gc_day') === $day) {
        return null;
    }
    // Up to 3 tries a day: a run that throws is retried by the next cron call (5 min).
    $try = $get('files_gc_try');
    $n = $try !== null && str_starts_with($try, $day . ':') ? (int)substr($try, 11) : 0;
    $mark = static function (string $k, string $v) use ($db): void {
        $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
        $st->execute([$k, $v]);
        $st->close();
    };
    if ($n >= 3) {
        $mark('files_gc_day', $day);
        return 'files-gc: 3 failed tries today';
    }
    $mark('files_gc_try', $day . ':' . ($n + 1));
    $apply = $get('files_gc_mode') === 'apply';
    $r = co_files_gc($apply);
    $mark('files_gc_day', $day);
    $n = count($r['trashed']);
    $msg = sprintf('files-gc %s: %d file(s), %.1f MB%s, %d error(s)', $apply ? 'trashed' : 'would trash', $n, $r['bytes'] / 1048576,
        '', count($r['errors']));
    @file_put_contents(co_agent_home() . '/co-agent/state/files-gc-' . $day . '.json', json_encode($r, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
    if ($n > 0 || $r['errors']) {
        $by = [];
        foreach ($r['trashed'] as $t) {
            $by[$t['reason']] = ($by[$t['reason']] ?? 0) + 1;
        }
        $parts = [];
        foreach ($by as $k => $v) {
            $parts[] = "$k $v";
        }
        if (function_exists('co_watch_notify')) {
            co_watch_notify('Chat files cleanup', sprintf('%s %d old file(s) (%s), %.1f MB, to the Drive trash%s.',
                $apply ? 'Moved' : 'Would move', $n, implode(', ', $parts), $r['bytes'] / 1048576,
                $r['errors'] ? '; ' . count($r['errors']) . ' error(s)' : ''));
        }
    }
    return $msg;
}

if (PHP_SAPI === 'cli' && realpath($argv[0] ?? '') === realpath(__FILE__)) {
    $cmd = $argv[1] ?? '';
    if ($cmd === 'gc-report' || $cmd === 'gc-apply') {
        $r = co_files_gc($cmd === 'gc-apply', (int)($argv[2] ?? 300));
        echo json_encode($r, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), "\n";
    } elseif ($cmd === 'annotate') {
        echo co_files_annotate((int)($argv[2] ?? 6)), " annotated\n";
    } elseif ($cmd === 'sync') {
        echo co_files_sync((int)($argv[2] ?? 500)), " records\n";
    } else {
        fwrite(STDERR, "usage: php files-cron.php gc-report|gc-apply [max] | annotate [n] | sync [n]\n");
        exit(1);
    }
}
