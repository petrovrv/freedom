<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
/*
 * Project memory file <project folder>/context/memory.md on Drive (schema 9, 2026-10-18): reading for the protocol
 * secretary (cron/memory-candidates.php) and appending accepted candidates (db.php memory_decide). Writes go through
 * the owner's Drive token, serialized per project by a MySQL named lock, so two accepts never overwrite each other.
 * Schema 14: revisions of the nightly consolidation (co_memory_rewrite, memory-archive.md); every write is conditional
 * (Drive v2 If-Match), so edits made meanwhile in CO Web or by aiwb-memory are never overwritten.
 */
declare(strict_types=1);

require_once __DIR__ . '/drive-lib.php';

/** Regex matching an optional leading "- YYYY-MM-DD: [tag]" prefix (each part optional). */
function co_memory_prefix_re(): string
{
    return '/^\s*(?:-\s*)?(?:\d{4}-\d{2}-\d{2}\s*:?\s*)?(?:\[[\p{L}_-]{1,20}\]\s*:?\s*)?/u';
}

/** Normalize a memory text for duplicate detection. */
function co_memory_norm(string $s): string
{
    $s = mb_scrub($s, 'UTF-8');
    $s = mb_strtolower($s, 'UTF-8');
    $s = preg_replace(co_memory_prefix_re(), '', $s, 1) ?? '';
    // Remove punctuation and markdown chars; keep letters, marks, digits of any script.
    $s = preg_replace('/[^\p{L}\p{M}\p{N}\s]+/u', ' ', $s) ?? '';
    $s = preg_replace('/\s+/u', ' ', $s) ?? '';
    return trim($s);
}

/**
 * Entry lines of memory.md (lines starting with "- "), tag and date included.
 *
 * @return list<string>
 */
function co_memory_parse_lines(string $md): array
{
    if (str_starts_with($md, "\xEF\xBB\xBF")) {
        $md = substr($md, 3);
    }
    $md = mb_scrub($md, 'UTF-8');

    $out = [];
    foreach (preg_split('/\R/u', $md) ?: [] as $line) {
        $line = rtrim($line);
        if (!str_starts_with($line, '- ') || trim(substr($line, 2)) === '') {
            continue;
        }
        $out[] = $line;
    }
    return $out;
}

/** Escape a value for use inside a single-quoted Drive query string. */
function co_memory_q_escape(string $s): string
{
    return str_replace(['\\', "'"], ['\\\\', "\\'"], $s);
}

/** First child of $parentId with this name (folder or not), or ''. */
function co_memory_child(string $parentId, string $name, bool $folder): string
{
    $q = sprintf(
        "'%s' in parents and name = '%s' and mimeType %s 'application/vnd.google-apps.folder' and trashed = false",
        co_memory_q_escape($parentId),
        co_memory_q_escape($name),
        $folder ? '=' : '!='
    );
    $resp = co_drive_get('files', [
        'q'                         => $q,
        'fields'                    => 'files(id,name)',
        'pageSize'                  => 5,
        'supportsAllDrives'         => 'true',
        'includeItemsFromAllDrives' => 'true',
    ]);
    return (string)($resp['files'][0]['id'] ?? '');
}

/**
 * Locates the project's memory file. Returns [folderId, contextId|'', fileId|'']; throws when the project folder is
 * unknown or Drive fails.
 *
 * @return array{0:string,1:string,2:string}
 */
function co_memory_locate(mysqli $db, string $projectId): array
{
    $st = $db->prepare('SELECT folder_id FROM projects WHERE project_id = ? LIMIT 1');
    $st->execute([$projectId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    $folderId = is_array($row) ? trim((string)($row['folder_id'] ?? '')) : '';
    if ($folderId === '') {
        throw new RuntimeException('project folder not known');
    }
    $contextId = co_memory_child($folderId, 'context', true);
    $fileId = $contextId === '' ? '' : co_memory_child($contextId, 'memory.md', false);
    return [$folderId, $contextId, $fileId];
}

/** Reads memory.md: '' if the file does not exist, null on any error. Cached per project unless $fresh. */
function co_memory_file_text(mysqli $db, string $projectId, bool $fresh = false): ?string
{
    static $cache = [];
    if (!$fresh && array_key_exists($projectId, $cache)) {
        return $cache[$projectId];
    }
    try {
        [, , $fileId] = co_memory_locate($db, $projectId);
        $result = $fileId === '' ? '' : co_drive_download($fileId, 500000);
    } catch (Throwable $e) {
        error_log('memory ' . $projectId . ': ' . $e->getMessage());
        $result = null;
    }
    $cache[$projectId] = $result;
    return $result;
}

/** True when memory.md text holds an entry equal to $line (ignoring date and tag). */
function co_memory_has_line(string $md, string $line): bool
{
    $target = co_memory_norm($line);
    foreach (co_memory_parse_lines($md) as $l) {
        if (co_memory_norm($l) === $target) {
            return true;
        }
    }
    return false;
}

/**
 * Appends "- $day: [$tag] $text" to memory.md (creates context/ and the file when missing). Returns false when the
 * same entry (ignoring date and tag) is already there. The caller holds the project's memory lock; the CO Web editor
 * and aiwb-memory write without it, so the write is conditional (co_memory_transform, schema 14).
 */
function co_memory_append(mysqli $db, string $projectId, string $day, string $tag, string $text): bool
{
    $line = '- ' . $day . ': [' . $tag . '] ' . $text;
    [$folderId, $contextId, $fileId] = co_memory_locate($db, $projectId);
    $add = static function (string $md) use ($line) {
        if (co_memory_has_line($md, $line)) {
            return true;
        }
        $b = trim($md) === '' ? "# Project memory
" : $md;
        return (str_ends_with($b, "
") ? $b : $b . "
") . $line . "
";
    };
    if ($fileId !== '') {
        return co_memory_transform($fileId, $add);
    }
    if ($contextId === '') {
        $contextId = co_drive_create_folder($folderId, 'context');
    }
    co_drive_create_text($contextId, 'memory.md', (string)$add(''));
    return true;
}

/** memory.md no longer holds the lines a revision replaces (schema 14). */
final class CoMemoryChanged extends RuntimeException
{
}

/**
 * Read-modify-write of a Drive text file with a conditional write (schema 14). Drive v3 has none, so the write goes
 * through Drive v2 with If-Match on the etag read before the content: a concurrent writer (CO Web editor, aiwb-memory,
 * Drive for desktop) makes it fail with 412 and the change is applied again to the new content, never over it.
 * $transform(string $text, int $attempt) returns the new text or true (already applied) and throws when it cannot
 * apply. Returns true when something was written.
 */
function co_memory_transform(string $fileId, callable $transform): bool
{
    for ($attempt = 0; $attempt < 5; $attempt++) {
        $etag = co_memory_etag($fileId);
        $text = co_drive_download($fileId, 2000000);
        $next = $transform($text, $attempt);
        if ($next === true || $next === $text) {
            return false;
        }
        if (co_memory_put_if_match($fileId, (string)$next, $etag)) {
            return true;
        }
        usleep(500000 + random_int(0, 1000000));
    }
    throw new RuntimeException('memory.md keeps changing; the change was not kept');
}

/** Current etag of a file (Drive v2). */
function co_memory_etag(string $fileId): string
{
    $etag = (string)(co_memory_v2('GET', 'https://www.googleapis.com/drive/v2/files/' . rawurlencode($fileId)
        . '?fields=etag&supportsAllDrives=true', null, [])['etag'] ?? '');
    if ($etag === '') {
        throw new RuntimeException('drive v2: no etag for ' . $fileId);
    }
    return $etag;
}

/** Uploads new content only if the file still has $etag; false on 412 (changed meanwhile). */
function co_memory_put_if_match(string $fileId, string $text, string $etag): bool
{
    return co_memory_v2('PUT', 'https://www.googleapis.com/upload/drive/v2/files/' . rawurlencode($fileId)
        . '?uploadType=media&fields=etag&supportsAllDrives=true', $text,
        ['Content-Type: text/markdown', 'If-Match: ' . $etag]) !== null;
}

/** One Drive v2 request; decoded JSON, or null on 412 (only with If-Match). Retries once on 429/5xx/network errors. */
function co_memory_v2(string $method, string $url, ?string $body, array $headers): ?array
{
    for ($attempt = 1; ; $attempt++) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_CUSTOMREQUEST  => $method,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT        => 60,
            CURLOPT_HTTPHEADER     => array_merge(['Authorization: Bearer ' . co_drive_token(), 'Expect:'], $headers),
        ]);
        if ($body !== null) {
            curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
        }
        $resp = curl_exec($ch);
        $errno = curl_errno($ch);
        $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);
        if ($status === 412) {
            return null;
        }
        if (($errno !== 0 || $status === 429 || $status >= 500) && $attempt < 2) {
            sleep(2);
            continue;
        }
        if ($errno !== 0 || $status < 200 || $status >= 300) {
            throw new RuntimeException(sprintf('drive v2 %s: HTTP %d curl %d %s', $method, $status, $errno,
                substr(is_string($resp) ? $resp : '', 0, 200)));
        }
        $j = json_decode(is_string($resp) ? $resp : '', true);
        return is_array($j) ? $j : [];
    }
}

/**
 * Applies a revision (schema 14): the exact lines $replaces are removed from memory.md, $newLine (revise; null = drop)
 * takes the place of the first of them. The removed lines are first archived in context/memory-archive.md with $note
 * ("superseded 2026-10-31 by: …" / "dropped 2026-10-31: …"); when the change fails, the entries this call added for
 * lines memory.md still holds are taken out again. The caller holds the
 * project's memory lock. Throws CoMemoryChanged when an original line is no longer in memory.md (the file changed
 * since the proposal). Already applied (an accept repeated after a failure): the archive is completed and it succeeds.
 *
 * @param list<string> $replaces
 */
function co_memory_rewrite(mysqli $db, string $projectId, array $replaces, ?string $newLine, string $note): void
{
    if ($replaces === []) {
        throw new InvalidArgumentException('nothing to replace');
    }
    [, $contextId, $fileId] = co_memory_locate($db, $projectId);
    if ($fileId === '') {
        throw new CoMemoryChanged('memory.md is gone');
    }
    $md = co_drive_download($fileId, 2000000);
    if (co_memory_rewrite_plan($md, $replaces, $newLine) === null) {
        if ($newLine === null && co_memory_drop_done($contextId, $md, $replaces)) {
            return; // an accept repeated after the drop was written (e.g. the status update failed)
        }
        throw co_memory_changed($md, $replaces);
    }
    $added = co_memory_archive($contextId, $replaces, $note);
    try {
        co_memory_transform($fileId, static function (string $md) use ($replaces, $newLine) {
            $plan = co_memory_rewrite_plan($md, $replaces, $newLine);
            if ($plan === null) {
                throw co_memory_changed($md, $replaces);
            }
            return $plan;
        });
    } catch (Throwable $e) {
        // Undo the archive entries of the lines memory.md still holds (their removal did not land); when that cannot
        // be read, extra history is kept rather than risking the only copy of removed lines.
        try {
            $now = co_drive_download($fileId, 2000000);
            $undo = [];
            foreach ($replaces as $r) {
                $entry = rtrim($r) . ' — ' . trim(preg_replace('/\s+/u', ' ', $note) ?? '');
                if (in_array($entry, $added, true) && co_memory_line_in($now, $r)) {
                    $undo[] = $entry; // this line is still in memory.md: its archive entry did not happen
                }
            }
            co_memory_unarchive($contextId, $undo);
        } catch (Throwable $e2) {
            error_log('memory unarchive: ' . $e2->getMessage());
        }
        throw $e;
    }
}

/**
 * True when a drop was already carried out: none of its lines is in memory.md and every one is archived as dropped.
 *
 * @param list<string> $replaces
 */
function co_memory_drop_done(string $contextId, string $md, array $replaces): bool
{
    foreach ($replaces as $r) {
        if (co_memory_line_in($md, $r)) {
            return false;
        }
    }
    $aid = $contextId === '' ? '' : co_memory_child($contextId, 'memory-archive.md', false);
    if ($aid === '') {
        return false;
    }
    $archive = co_drive_download($aid, 2000000);
    foreach ($replaces as $r) {
        if (!str_contains($archive, rtrim($r) . ' — dropped ')) {
            return false;
        }
    }
    return true;
}

/** CoMemoryChanged naming how many of the original lines left memory.md. */
function co_memory_changed(string $md, array $replaces): CoMemoryChanged
{
    $missing = count(array_filter($replaces, static fn($r) => !co_memory_line_in($md, $r)));
    return new CoMemoryChanged('memory.md changed since this proposal: ' . $missing . ' of ' . count($replaces)
        . ' original lines are no longer there. Reject it; the next consolidation pass proposes again.');
}

/**
 * Removes archive entries added by a co_memory_archive call whose memory.md change failed.
 *
 * @param list<string> $entries
 */
function co_memory_unarchive(string $contextId, array $entries): void
{
    if ($entries === []) {
        return;
    }
    $fileId = co_memory_child($contextId, 'memory-archive.md', false);
    if ($fileId === '') {
        return;
    }
    co_memory_transform($fileId, static function (string $text) use ($entries) {
        $drop = array_flip($entries);
        $out = [];
        foreach (preg_split('/\r\n|\n/', $text) ?: [] as $l) {
            if (isset($drop[$l])) {
                unset($drop[$l]);
                continue;
            }
            $out[] = $l;
        }
        return implode("\n", $out);
    });
}

/** True when memory.md text has this exact line (trailing spaces ignored). */
function co_memory_line_in(string $md, string $line): bool
{
    $line = rtrim($line);
    foreach (preg_split('/\r\n|\n/', $md) ?: [] as $l) {
        if (rtrim($l) === $line) {
            return true;
        }
    }
    return false;
}

/**
 * New memory.md text for a revision; true when it is already applied (no original line left, except one equal to
 * $newLine, and $newLine present; or a drop); null when some original line is missing.
 *
 * @param list<string> $replaces
 */
function co_memory_rewrite_plan(string $md, array $replaces, ?string $newLine): string|bool|null
{
    $bom = str_starts_with($md, "\xEF\xBB\xBF") ? "\xEF\xBB\xBF" : '';
    $body = $bom === '' ? $md : substr($md, 3);
    $nl = str_contains($body, "\r\n") ? "\r\n" : "\n";
    $lines = preg_split('/\r\n|\n/', $body) ?: [];
    $keep = $newLine === null ? null : rtrim($newLine);

    $idx = [];
    foreach ($replaces as $r) {
        $r = rtrim($r);
        $found = null;
        foreach ($lines as $i => $l) {
            if (!isset($idx[$i]) && rtrim($l) === $r) {
                $found = $i;
                break;
            }
        }
        if ($found === null) {
            // Applied already? No original line remains (one equal to the new line does not count), the new line is there.
            foreach ($replaces as $r2) {
                if (rtrim($r2) !== $keep && co_memory_line_in($body, $r2)) {
                    return null;
                }
            }
            return $keep !== null && co_memory_line_in($body, $keep) ? true : null; // a drop of lines already gone: changed
        }
        $idx[$found] = true;
    }

    $first = min(array_keys($idx));
    $out = [];
    foreach ($lines as $i => $l) {
        if ($i === $first && $newLine !== null) {
            $out[] = $newLine;
        } elseif (!isset($idx[$i])) {
            $out[] = $l;
        }
    }
    return $bom . implode($nl, $out);
}


/**
 * Appends removed memory lines to context/memory-archive.md ("<line> — <note>"); creates the file when missing. Lines
 * already archived with the same text are not repeated. Returns the entries this call added.
 *
 * @param list<string> $lines
 * @return list<string>
 */
function co_memory_archive(string $contextId, array $lines, string $note): array
{
    if ($contextId === '') {
        throw new RuntimeException('project has no context folder');
    }
    $note = trim(preg_replace('/\s+/u', ' ', $note) ?? '');
    $added = [];
    $add = static function (string $current) use ($lines, $note, &$added) {
        $added = [];
        $body = trim($current) === ''
            ? "# Project memory archive\n\nEntries removed from memory.md by accepted consolidation proposals (CO Web, Project → Memory).\n\n"
            : $current;
        if (!str_ends_with($body, "\n")) {
            $body .= "\n";
        }
        foreach ($lines as $l) {
            $entry = rtrim($l) . ' — ' . $note;
            if (!str_contains($body, $entry)) {
                $body .= $entry . "\n";
                $added[] = $entry;
            }
        }
        return $added === [] ? true : $body;
    };
    $fileId = co_memory_child($contextId, 'memory-archive.md', false);
    if ($fileId !== '') {
        co_memory_transform($fileId, $add);
    } else {
        co_drive_create_text($contextId, 'memory-archive.md', (string)$add(''));
    }
    return $added;
}

// ---- Schema 16: accepted rules from corrections go to the environment profile (Reflexes; cron/memory-candidates.php) ----

const CO_RULE_PROFILE_HEAD = 'Corrections (accepted rules for agents):';
const CO_RULE_SOURCE_RE    = '/\s*\((?:CHAT-[A-Za-z0-9-]+\s*)?#\d+\)\s*$/u';

/** Named lock of an environment profile: accepted rules and profile_put (with its rev check) write under it. */
function co_profile_lock(mysqli $db, string $env): void
{
    $st = $db->prepare('SELECT GET_LOCK(?, 20)');
    $st->execute(['aiwb_profile_' . substr(sha1($env), 0, 40)]);
    if ((int)($st->get_result()->fetch_row()[0] ?? 0) !== 1) {
        throw new RuntimeException('profile lock busy');
    }
}

function co_profile_unlock(mysqli $db, string $env): void
{
    $st = $db->prepare('SELECT RELEASE_LOCK(?)');
    $st->execute(['aiwb_profile_' . substr(sha1($env), 0, 40)]);
}

function co_rule_profile_text(mysqli $db, string $env): string
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['env/' . $env, 'profile']);
    $d = json_decode((string)($st->get_result()->fetch_row()[0] ?? ''), true);
    return is_array($d) && is_string($d['text'] ?? null) ? $d['text'] : '';
}

/** Normalized rule text of a profile line or candidate (the "(CHAT-… #N)" source suffix ignored). */
function co_rule_norm(string $line): string
{
    return co_memory_norm(preg_replace(CO_RULE_SOURCE_RE, '', $line) ?? $line);
}

/**
 * Appends an accepted rule line to the environment profile (kv env/<env> profile) under CO_RULE_PROFILE_HEAD,
 * serialized by a named lock. Returns false when the same rule is already there.
 */
function co_rule_profile_append(mysqli $db, string $env, string $line, string $email): bool
{
    co_profile_lock($db, $env);
    try {
        $text = co_rule_profile_text($db, $env);
        $norm = co_rule_norm($line);
        foreach (preg_split('/\R/u', $text) ?: [] as $l) {
            if (co_rule_norm($l) === $norm) {
                return false;
            }
        }
        $lines = preg_split('/\R/u', rtrim($text)) ?: [];
        if ($lines === [''] ) {
            $lines = [];
        }
        $head = null;
        foreach ($lines as $i => $l) {
            if (trim($l) === CO_RULE_PROFILE_HEAD) {
                $head = $i;
                break;
            }
        }
        if ($head === null) {
            if ($lines !== []) {
                $lines[] = '';
            }
            $lines[] = CO_RULE_PROFILE_HEAD;
            $head = count($lines) - 1;
        }
        // the new line goes after the last rule line of the section
        $at = $head + 1;
        while ($at < count($lines) && str_starts_with(ltrim($lines[$at]), '- ')) {
            $at++;
        }
        array_splice($lines, $at, 0, [$line]);
        $text = implode("\n", $lines);
        if (mb_strlen($text, 'UTF-8') > 20000) {
            throw new RuntimeException('environment profile would exceed 20000 characters');
        }
        $data = json_encode(['text' => $text, 'updatedBy' => $email], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
        $st = $db->prepare('INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
                            ON DUPLICATE KEY UPDATE data = VALUES(data), sha = VALUES(sha), updated_utc = VALUES(updated_utc)');
        $st->execute(['env/' . $env, 'profile', $data, hash('sha256', $data)]);
        return true;
    } finally {
        co_profile_unlock($db, $env);
    }
}
