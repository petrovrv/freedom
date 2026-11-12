<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// files-lib.php: chat files as version streams (schema 12, 2026-10-28; docs/SPEC-chat-files.md).
// A stream is one live Drive file, keyed by its path relative to AI-Workbench ("projects/<p>/chats/<c>/scratch/x.docx");
// its versions are the Drive revisions of that file, linked to messages by md5. chat_files keeps the registry and what
// Drive cannot: kind (doc/temp), merges, branches, restores, the one-line annotation. Drive actions on behalf of a user
// (merge, branch, make main, delete a revision) are done by CO Web with the user's token; db.php records them here.
declare(strict_types=1);

const CO_FILES_TEMP_EXT = ['py', 'ps1', 'psm1', 'sh', 'bash', 'bat', 'cmd', 'js', 'mjs', 'cjs', 'ts', 'tsx', 'jsx', 'cs',
    'java', 'go', 'rs', 'rb', 'php', 'pl', 'lua', 'sql', 'r', 'vbs', 'ipynb', 'log'];
const CO_FILES_TEMP_DIRS = ['tmp', 'temp', '_tmp', 'scratch-tmp'];
const CO_FILES_EXPIRE_DAYS = 30;
// Service files (kind temp) expire sooner: nobody reads them after the job (owner, 2026-10-28).
const CO_FILES_TEMP_DAYS = 7;

/** 'temp' for code, logs and files under tmp/ folders (they expire CO_FILES_TEMP_DAYS after their last change), else 'doc'. */
function co_files_kind(string $name): string
{
    $parts = explode('/', strtolower(str_replace('\\', '/', $name)));
    $base = array_pop($parts);
    foreach ($parts as $p) {
        if (in_array($p, CO_FILES_TEMP_DIRS, true)) {
            return 'temp';
        }
    }
    $dot = strrpos($base, '.');
    $ext = $dot === false ? '' : substr($base, $dot + 1);
    return in_array($ext, CO_FILES_TEMP_EXT, true) ? 'temp' : 'doc';
}

/**
 * Stream key and display name of a message file record.
 *  - artifact with ref (Bridge 1.6, site agent 1.1): the live file itself;
 *  - legacy artifact (a copy in .../artifacts/<job>/): the scratch file it was copied from;
 *  - attachment with streamPath (CO Web 0.55): the scratch file it became;
 *  - legacy attachment: its persistent copy (branches/<b>/attachments/<job>/<name>).
 * Returns null for records without a usable path.
 */
function co_files_key(array $rec, string $kind, string $projectId, string $chatId, ?string $jobId, ?string $branchId): ?array
{
    $chatRoot = 'projects/' . $projectId . '/chats/' . $chatId;
    if ($kind === 'artifact') {
        // Codex on Windows may report "dir\file": one form for the key
        $p = str_replace('\\', '/', (string)($rec['driveRelativePath'] ?? ''));
        $src = str_replace('\\', '/', (string)($rec['sourceRelativePath'] ?? ''));
        if ($p === '' || str_contains($p, ':') || str_starts_with($p, '/')) {
            return null; // a workspace outside Drive (absolute path)
        }
        if (empty($rec['ref']) && preg_match('#/artifacts/[^/]+/(.+)$#', $p, $m)) {
            $src = $src !== '' ? $src : $m[1];
            return ['key' => $chatRoot . '/scratch/' . $src, 'name' => $src, 'legacy' => true];
        }
        $name = $src !== '' ? $src : basename($p);
        return ['key' => $p, 'name' => $name, 'legacy' => false];
    }
    $sp = (string)($rec['streamPath'] ?? '');
    if ($sp !== '') {
        return ['key' => $sp, 'name' => (string)($rec['target'] ?? basename($sp)), 'legacy' => false];
    }
    $path = (string)($rec['path'] ?? '');
    if ($path === '' || $jobId === null) {
        return null;
    }
    $name = (string)($rec['displayName'] ?? basename($path));
    return [
        'key' => $chatRoot . '/branches/' . ($branchId ?: 'BR-root') . '/attachments/' . $jobId . '/' . $path,
        'name' => $name, 'legacy' => true,
    ];
}

/** Adds one version (a message file record) to its stream; idempotent by sha256. */
function co_files_note(mysqli $db, string $projectId, string $chatId, array $k, string $origin, array $rec, ?string $utc, int $seq): void
{
    $sha = strtolower((string)($rec['sha256'] ?? ''));
    $md5 = strtolower((string)($rec['md5'] ?? ''));
    $size = is_numeric($rec['sizeBytes'] ?? null) ? (int)$rec['sizeBytes'] : null;
    $pathSha = sha1($k['key']);
    $st = $db->prepare('SELECT id, extra, head_utc, head_seq FROM chat_files WHERE chat_id = ? AND path_sha = ?');
    $st->execute([$chatId, $pathSha]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    $extra = $row ? (json_decode((string)$row['extra'], true) ?: []) : [];
    $shas = $extra['shas'] ?? [];
    $newer = !$row || $row['head_seq'] === null || $seq > (int)$row['head_seq'];
    if ($sha !== '' && in_array($sha, $shas, true)) {
        // Known content: a later message with it (a file back to an earlier version) only moves the head.
        if ($row && $newer) {
            $st = $db->prepare("UPDATE chat_files SET head_md5 = ?, head_sha256 = ?, head_size = ?, head_utc = ?, head_origin = ?,
                                       head_seq = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?");
            $md5v = $md5 ?: null;
            $dt = co_db_dt($utc) ?? gmdate('Y-m-d H:i:s.000');
            $id = (int)$row['id'];
            $st->bind_param('ssissii', $md5v, $sha, $size, $dt, $origin, $seq, $id);
            $st->execute();
            $st->close();
        }
        return;
    }
    if ($sha !== '') {
        $shas[] = $sha;
        $extra['shas'] = array_slice($shas, -300);
    }
    if (!empty($rec['fileId']) && $origin === 'user' && $k['legacy']) {
        $extra['fileId'] = (string)$rec['fileId'];
    }
    $dt = co_db_dt($utc) ?? gmdate('Y-m-d H:i:s.000');
    $extraJson = co_db_json($extra);
    if (!$row) {
        $st = $db->prepare(
            'INSERT INTO chat_files (chat_id, project_id, path, path_sha, name, kind, legacy, extra, versions,
                head_md5, head_sha256, head_size, head_utc, head_origin, head_seq, first_utc, updated_utc)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3))'
        );
        $legacy = $k['legacy'] ? 1 : 0;
        $kind = co_files_kind($k['name']);
        $md5v = $md5 ?: null;
        $shav = $sha ?: null;
        $st->bind_param('ssssssisssissis', $chatId, $projectId, $k['key'], $pathSha, $k['name'], $kind, $legacy, $extraJson,
            $md5v, $shav, $size, $dt, $origin, $seq, $dt);
        $st->execute();
        $st->close();
        return;
    }
    if ($newer) {
        $st = $db->prepare(
            "UPDATE chat_files SET extra = ?, versions = versions + 1, head_md5 = ?, head_sha256 = ?, head_size = ?, head_utc = ?,
                    head_origin = ?, head_seq = ?, legacy = IF(?, legacy, 0),
                    state = IF(state = 'gone', 'live', state), updated_utc = UTC_TIMESTAMP(3) WHERE id = ?"
        );
        $md5v = $md5 ?: null;
        $shav = $sha ?: null;
        $legacy = $k['legacy'] ? 1 : 0;
        $id = (int)$row['id'];
        $st->bind_param('sssissiii', $extraJson, $md5v, $shav, $size, $dt, $origin, $seq, $legacy, $id);
    } else {
        $st = $db->prepare('UPDATE chat_files SET extra = ?, versions = versions + 1, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
        $id = (int)$row['id'];
        $st->bind_param('si', $extraJson, $id);
    }
    $st->execute();
    $st->close();
}

/** Registers the files of one chat event (assistant artifacts, user attachments). */
function co_files_from_event(mysqli $db, array $ev, string $projectId): int
{
    $chatId = (string)($ev['chatId'] ?? '');
    $type = (string)($ev['type'] ?? '');
    $seq = (int)($ev['sequence'] ?? 0);
    $utc = is_string($ev['createdUtc'] ?? null) ? $ev['createdUtc'] : null;
    $jobId = is_string($ev['jobId'] ?? null) ? $ev['jobId'] : null;
    $branch = is_string($ev['branchId'] ?? null) ? $ev['branchId'] : null;
    $n = 0;
    $list = $type === 'assistant_message' ? ($ev['artifacts'] ?? []) : ($type === 'user_message' ? ($ev['attachments'] ?? []) : []);
    if (!is_array($list)) {
        return 0;
    }
    foreach ($list as $rec) {
        if (!is_array($rec)) {
            continue;
        }
        $k = co_files_key($rec, $type === 'assistant_message' ? 'artifact' : 'attachment', $projectId, $chatId, $jobId, $branch);
        if ($k === null) {
            continue;
        }
        co_files_note($db, $projectId, $chatId, $k, $type === 'assistant_message' ? 'agent' : 'user', $rec, $utc, $seq);
        $n++;
    }
    return $n;
}

/** Cron (drive-sync.php, every minute): new chat messages -> chat_files. Cursor meta.files_sync_utc on events.synced_utc. */
function co_files_sync(int $limit = 500): int
{
    $db = co_db();
    $st = $db->prepare("SELECT v FROM meta WHERE k = 'files_sync_utc'");
    $st->execute();
    $cursor = $st->get_result()->fetch_row()[0] ?? '1970-01-01 00:00:00.000';
    $st->close();
    // 5 s overlap: rows committed slightly out of order are seen again; co_files_note is idempotent by sha256.
    $st = $db->prepare(
        "SELECT e.data, e.synced_utc, c.project_id FROM events e JOIN chats c ON c.chat_id = e.chat_id
          WHERE e.synced_utc > ? - INTERVAL 5 SECOND AND e.type IN ('assistant_message', 'user_message')
            AND (e.data LIKE '%\"artifacts\":[{%' OR e.data LIKE '%\"attachments\":[{%')
          ORDER BY e.synced_utc LIMIT ?"
    );
    $st->bind_param('si', $cursor, $limit);
    $st->execute();
    $rows = $st->get_result()->fetch_all(MYSQLI_NUM);
    $st->close();
    $n = 0;
    $last = $cursor;
    foreach ($rows as [$data, $synced, $projectId]) {
        $ev = json_decode((string)$data, true);
        if (is_array($ev)) {
            $n += co_files_from_event($db, $ev, (string)$projectId);
        }
        $last = max($last, (string)$synced);
    }
    if ($rows === [] || count($rows) < $limit) {
        // Caught up: move the cursor to now minus the overlap, so the next scan is short.
        $st = $db->query('SELECT UTC_TIMESTAMP(3) - INTERVAL 5 SECOND');
        $last = max($last, (string)$st->fetch_row()[0]);
    }
    $st = $db->prepare("REPLACE INTO meta (k, v) VALUES ('files_sync_utc', ?)");
    $st->execute([$last]);
    $st->close();
    return $n;
}

/** Row as CO Web sees it. */
function co_files_view(array $r): array
{
    $extra = json_decode((string)($r['extra'] ?? ''), true) ?: [];
    unset($extra['shas']);
    return [
        'id' => (int)$r['id'],
        'path' => $r['path'],
        'name' => $r['name'],
        'kind' => $r['kind'],
        'kindByUser' => (bool)$r['kind_user'],
        'state' => $r['state'],
        'mergedInto' => $r['merged_into'] !== null ? (int)$r['merged_into'] : null,
        'legacy' => (bool)$r['legacy'],
        'versions' => (int)$r['versions'],
        'head' => [
            'md5' => $r['head_md5'], 'sha256' => $r['head_sha256'], 'size' => $r['head_size'] !== null ? (int)$r['head_size'] : null,
            'utc' => co_db_iso($r['head_utc']), 'origin' => $r['head_origin'], 'seq' => $r['head_seq'] !== null ? (int)$r['head_seq'] : null,
        ],
        'firstUtc' => co_db_iso($r['first_utc']),
        'annotation' => $r['annotation'],
        'goneUtc' => co_db_iso($r['gone_utc']),
        'extra' => $extra ?: new stdClass(),
    ];
}

function co_files_list(mysqli $db, string $chatId): array
{
    $st = $db->prepare('SELECT * FROM chat_files WHERE chat_id = ? ORDER BY head_utc DESC, id DESC LIMIT 1000');
    $st->execute([$chatId]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    return array_map('co_files_view', $rows);
}

function co_files_row(mysqli $db, string $chatId, int $id): array
{
    $st = $db->prepare('SELECT * FROM chat_files WHERE chat_id = ? AND id = ?');
    $st->execute([$chatId, $id]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$r) {
        throw new InvalidArgumentException('file not found');
    }
    return $r;
}

function co_files_save_extra(mysqli $db, int $id, array $extra): void
{
    $st = $db->prepare('UPDATE chat_files SET extra = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
    $j = co_db_json($extra);
    $st->bind_param('si', $j, $id);
    $st->execute();
    $st->close();
}

function co_files_str(array $in, string $k, int $max, bool $required = true): ?string
{
    $v = $in[$k] ?? null;
    if ($v === null && !$required) {
        return null;
    }
    if (!is_string($v) || $v === '' || strlen($v) > $max) {
        throw new InvalidArgumentException("$k required");
    }
    return $v;
}

/** db.php files_* write ops (the caller checked the chat role 'write'). Returns the changed rows. */
function co_files_op(mysqli $db, string $op, string $chatId, array $in): array
{
    $now = gmdate('Y-m-d\TH:i:s\Z');
    if ($op === 'files_set') {
        $id = (int)($in['id'] ?? 0);
        $r = co_files_row($db, $chatId, $id);
        if (isset($in['kind'])) {
            if (!in_array($in['kind'], ['doc', 'temp'], true)) {
                throw new InvalidArgumentException('kind');
            }
            $st = $db->prepare('UPDATE chat_files SET kind = ?, kind_user = 1, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
            $st->bind_param('si', $in['kind'], $id);
            $st->execute();
            $st->close();
        }
        if (isset($in['state'])) {
            if (!in_array($in['state'], ['live', 'hidden'], true) || !in_array($r['state'], ['live', 'hidden'], true)) {
                throw new InvalidArgumentException('state');
            }
            $st = $db->prepare('UPDATE chat_files SET state = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
            $st->bind_param('si', $in['state'], $id);
            $st->execute();
            $st->close();
        }
        return [co_files_view(co_files_row($db, $chatId, $id))];
    }
    if ($op === 'files_merge') {
        // "from" joins "into": its versions are listed under "into"; its Drive file was moved to scratch/.versions/ by CO Web.
        $from = co_files_row($db, $chatId, (int)($in['from'] ?? 0));
        $into = co_files_row($db, $chatId, (int)($in['into'] ?? 0));
        if ((int)$from['id'] === (int)$into['id'] || $from['state'] === 'merged' || $into['state'] === 'merged') {
            throw new InvalidArgumentException('cannot merge these files');
        }
        $fx = json_decode((string)$from['extra'], true) ?: [];
        $ix = json_decode((string)$into['extra'], true) ?: [];
        $movedId = co_files_str($in, 'movedFileId', 100, false);
        if ($movedId !== null && !co_files_in_versions($movedId, $chatId)) {
            // The cleanup trashes members 30 days later with the owner's token: only a file in this chat's scratch/.versions.
            throw new InvalidArgumentException('movedFileId is not in scratch/.versions of this chat');
        }
        $members = $ix['members'] ?? [];
        $members[] = [
            'id' => (int)$from['id'], 'name' => $from['name'], 'path' => co_files_str($in, 'movedPath', 700, false) ?? $from['path'],
            'fileId' => $movedId, 'utc' => $now, 'legacy' => (bool)$from['legacy'],
        ];
        foreach ($fx['members'] ?? [] as $m) {
            $members[] = $m;
        }
        $ix['members'] = $members;
        $ix['shas'] = array_values(array_unique(array_merge($ix['shas'] ?? [], $fx['shas'] ?? [])));
        $db->begin_transaction();
        try {
            co_files_save_extra($db, (int)$into['id'], $ix);
            $st = $db->prepare('UPDATE chat_files SET versions = versions + ? WHERE id = ?');
            $add = (int)$from['versions'];
            $iid = (int)$into['id'];
            $st->bind_param('ii', $add, $iid);
            $st->execute();
            $st->close();
            if (!empty($in['head'])) {
                co_files_head($db, (int)$into['id'], $in['head'], 'user');
            }
            $st = $db->prepare("UPDATE chat_files SET state = 'merged', merged_into = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?");
            $fid = (int)$from['id'];
            $st->bind_param('ii', $iid, $fid);
            $st->execute();
            $st->close();
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
        return [co_files_view(co_files_row($db, $chatId, (int)$into['id'])), co_files_view(co_files_row($db, $chatId, (int)$from['id']))];
    }
    if ($op === 'files_branch') {
        // A revision of "from" became a new live file (CO Web created it); the new stream remembers its parent.
        $from = co_files_row($db, $chatId, (int)($in['from'] ?? 0));
        $path = co_files_str($in, 'path', 700);
        $name = co_files_str($in, 'name', 300);
        $head = is_array($in['head'] ?? null) ? $in['head'] : [];
        $extra = ['parent' => ['id' => (int)$from['id'], 'name' => $from['name'], 'md5' => $head['fromMd5'] ?? null,
            'revisionId' => $head['revisionId'] ?? null, 'utc' => $now]];
        $sha = strtolower((string)($head['sha256'] ?? ''));
        if ($sha !== '') {
            $extra['shas'] = [$sha];
        }
        $st = $db->prepare(
            "INSERT INTO chat_files (chat_id, project_id, path, path_sha, name, kind, kind_user, extra, versions,
                head_md5, head_sha256, head_size, head_utc, head_origin, first_utc, updated_utc)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, UTC_TIMESTAMP(3), 'user', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))"
        );
        $pathSha = sha1($path);
        $kind = $from['kind'];
        $kindUser = (int)$from['kind_user'];
        $extraJson = co_db_json($extra);
        $md5 = $head['md5'] ?? null;
        $shav = $sha ?: null;
        $size = isset($head['size']) ? (int)$head['size'] : null;
        $st->bind_param('ssssssisssi', $chatId, $from['project_id'], $path, $pathSha, $name, $kind, $kindUser, $extraJson, $md5, $shav, $size);
        $st->execute();
        $id = (int)$db->insert_id;
        $st->close();
        return [co_files_view(co_files_row($db, $chatId, $id))];
    }
    if ($op === 'files_restored') {
        // "Make main": CO Web uploaded the content of an older revision as the newest one.
        $r = co_files_row($db, $chatId, (int)($in['id'] ?? 0));
        $head = is_array($in['head'] ?? null) ? $in['head'] : [];
        $extra = json_decode((string)$r['extra'], true) ?: [];
        $restored = $extra['restored'] ?? [];
        $restored[] = ['md5' => $head['md5'] ?? null, 'fromRevision' => $head['revisionId'] ?? null, 'utc' => $now];
        $extra['restored'] = array_slice($restored, -50);
        co_files_save_extra($db, (int)$r['id'], $extra);
        co_files_head($db, (int)$r['id'], $head, 'user');
        return [co_files_view(co_files_row($db, $chatId, (int)$r['id']))];
    }
    if ($op === 'files_undone') {
        // CO Web 0.72.0 "Undo file changes of this answer": CO Web restored the revision from before the job (restore)
        // or moved the file the job created to the Drive trash (trash). CO Web plans only registered streams.
        $path = co_files_str($in, 'path', 700);
        $action = $in['action'] ?? '';
        if (!in_array($action, ['restore', 'trash'], true)) {
            throw new InvalidArgumentException('action');
        }
        $st = $db->prepare('SELECT id FROM chat_files WHERE chat_id = ? AND path_sha = ?');
        $st->execute([$chatId, sha1($path)]);
        $id = (int)($st->get_result()->fetch_row()[0] ?? 0);
        $st->close();
        if (!$id) {
            throw new InvalidArgumentException('file not registered yet');
        }
        $r = co_files_row($db, $chatId, $id);
        $head = is_array($in['head'] ?? null) ? $in['head'] : [];
        $extra = json_decode((string)$r['extra'], true) ?: [];
        $undo = ['seq' => (int)($in['seq'] ?? 0), 'action' => $action, 'fromRevision' => $head['revisionId'] ?? null, 'utc' => $now];
        $extra['undone'] = array_slice(array_merge($extra['undone'] ?? [], [$undo]), -50);
        if ($action === 'restore') {
            $restored = $extra['restored'] ?? [];
            $restored[] = ['md5' => $head['md5'] ?? null, 'fromRevision' => $head['revisionId'] ?? null, 'utc' => $now, 'undoOf' => $undo['seq']];
            $extra['restored'] = array_slice($restored, -50);
            co_files_save_extra($db, $id, $extra);
            co_files_head($db, $id, $head, 'user');
        } else {
            co_files_save_extra($db, $id, $extra);
            $u = $db->prepare("UPDATE chat_files SET state = 'gone', gone_utc = UTC_TIMESTAMP(3), updated_utc = UTC_TIMESTAMP(3) WHERE id = ?");
            $u->bind_param('i', $id);
            $u->execute();
            $u->close();
        }
        return [co_files_view(co_files_row($db, $chatId, $id))];
    }
    throw new InvalidArgumentException('unknown files op');
}

function co_files_head(mysqli $db, int $id, array $head, string $origin): void
{
    $md5 = is_string($head['md5'] ?? null) ? strtolower($head['md5']) : null;
    $sha = is_string($head['sha256'] ?? null) ? strtolower($head['sha256']) : null;
    $size = isset($head['size']) ? (int)$head['size'] : null;
    $st = $db->prepare(
        'UPDATE chat_files SET head_md5 = ?, head_sha256 = ?, head_size = ?, head_utc = UTC_TIMESTAMP(3), head_origin = ?,
                versions = versions + 1, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?'
    );
    $st->bind_param('ssisi', $md5, $sha, $size, $origin, $id);
    $st->execute();
    $st->close();
}

// ---------- Drive access of the server (owner token, drive-lib.php) ----------

/** Drive id of a file by its path relative to AI-Workbench, starting at the chat folder; null if missing. */
function co_files_drive_find(string $chatFolderId, string $path, string $chatId): ?array
{
    $at = strpos($path, '/chats/' . $chatId . '/');
    if ($at === false) {
        return null;
    }
    $parts = explode('/', substr($path, $at + strlen('/chats/' . $chatId . '/')));
    $id = $chatFolderId;
    $meta = null;
    foreach ($parts as $i => $name) {
        $q = sprintf("'%s' in parents and name = '%s' and trashed = false", $id, str_replace(['\\', "'"], ['\\\\', "\\'"], $name));
        $r = co_drive_get('files', ['q' => $q, 'fields' => 'files(id,name,mimeType,modifiedTime,md5Checksum,sha256Checksum,size)',
            'pageSize' => 2, 'supportsAllDrives' => 'true', 'includeItemsFromAllDrives' => 'true']);
        $f = $r['files'][0] ?? null;
        if (!is_array($f)) {
            return null;
        }
        $id = (string)$f['id'];
        $meta = $f;
    }
    return $meta;
}

/** True when the Drive file lies in <chat folder>/scratch/.versions of this chat (checked with the owner's token). */
function co_files_in_versions(string $fileId, string $chatId): bool
{
    if (!preg_match('/^[A-Za-z0-9_-]{10,100}$/', $fileId)) {
        return false;
    }
    $db = co_db();
    $st = $db->prepare('SELECT folder_id FROM chats WHERE chat_id = ?');
    $st->execute([$chatId]);
    $chatFolder = $st->get_result()->fetch_row()[0] ?? null;
    $st->close();
    $meta = co_drive_meta($fileId);
    $versions = $meta['parents'][0] ?? null;
    $vm = $versions ? co_drive_meta((string)$versions) : null;
    $scratch = $vm['parents'][0] ?? null;
    $sm = $scratch ? co_drive_meta((string)$scratch) : null;
    return $chatFolder && $vm && $sm && ($vm['name'] ?? '') === '.versions' && ($sm['name'] ?? '') === 'scratch'
        && ($sm['parents'][0] ?? null) === $chatFolder;
}

function co_files_drive_trash(string $fileId): void
{
    co_drive_send('PATCH', 'https://www.googleapis.com/drive/v3/files/' . rawurlencode($fileId) . '?supportsAllDrives=true',
        '{"trashed":true}', 'application/json');
}

function co_files_trash_logged(mysqli $db, string $path, string $fileId, string $reason, int $bytes): void
{
    $st = $db->prepare('INSERT IGNORE INTO files_trash (path_sha, path, file_id, reason, bytes, trashed_utc) VALUES (?, ?, ?, ?, ?, UTC_TIMESTAMP(3))');
    $sha = sha1($path);
    $st->bind_param('ssssi', $sha, $path, $fileId, $reason, $bytes);
    $st->execute();
    $st->close();
}

/**
 * Daily cleanup (cron/files-gc.php). Old Drive revisions expire by Drive itself (30 days, not kept forever); here:
 *  1. temp streams (code, logs, tmp/) whose live file did not change for CO_FILES_TEMP_DAYS (7) days -> Drive trash, state gone;
 *  2. files absorbed by a merge (scratch/.versions/...) 30 days after the merge -> Drive trash;
 *  3. legacy copies in artifacts/<job>/ older than 30 days whose stream has a newer version -> Drive trash.
 * $apply false = report only. Returns ['trashed' => [...], 'bytes' => n, 'errors' => [...]].
 */
function co_files_gc(bool $apply, int $max = 300): array
{
    $db = co_db();
    $out = ['trashed' => [], 'bytes' => 0, 'errors' => []];
    $days = CO_FILES_EXPIRE_DAYS;
    $folders = [];
    $folder = static function (string $chatId) use ($db, &$folders): ?string {
        if (!array_key_exists($chatId, $folders)) {
            $st = $db->prepare('SELECT folder_id FROM chats WHERE chat_id = ?');
            $st->execute([$chatId]);
            $folders[$chatId] = $st->get_result()->fetch_row()[0] ?? null;
            $st->close();
        }
        return $folders[$chatId];
    };
    $trash = static function (string $chatId, string $path, string $reason, ?string $fileId = null, ?int $size = null) use ($db, $apply, &$out, $folder, $days): bool {
        try {
            if ($fileId === null) {
                $fid = $folder($chatId);
                $meta = $fid ? co_files_drive_find($fid, $path, $chatId) : null;
                if ($meta === null) {
                    return true; // already gone
                }
                $age = $reason === 'temp' ? CO_FILES_TEMP_DAYS : $days;
                if (strtotime((string)$meta['modifiedTime']) > time() - $age * 86400) {
                    return false; // changed on Drive meanwhile
                }
                $fileId = (string)$meta['id'];
                $size = (int)($meta['size'] ?? 0);
            }
            $out['trashed'][] = ['path' => $path, 'reason' => $reason, 'bytes' => (int)$size];
            $out['bytes'] += (int)$size;
            if ($apply) {
                co_files_drive_trash($fileId);
                co_files_trash_logged($db, $path, $fileId, $reason, (int)$size);
            }
            return true;
        } catch (Throwable $e) {
            $out['errors'][] = $path . ': ' . $e->getMessage();
            return false;
        }
    };

    // 1. temp streams
    $st = $db->prepare("SELECT id, chat_id, path FROM chat_files WHERE state IN ('live', 'hidden') AND kind = 'temp'
                         AND head_utc < UTC_TIMESTAMP(3) - INTERVAL ? DAY ORDER BY head_utc LIMIT ?");
    $tempDays = CO_FILES_TEMP_DAYS;
    $st->bind_param('ii', $tempDays, $max);
    $st->execute();
    foreach ($st->get_result()->fetch_all(MYSQLI_ASSOC) as $r) {
        if (count($out['trashed']) >= $max) {
            break;
        }
        if ($trash((string)$r['chat_id'], (string)$r['path'], 'temp') && $apply) {
            $u = $db->prepare("UPDATE chat_files SET state = 'gone', gone_utc = UTC_TIMESTAMP(3) WHERE id = ?");
            $id = (int)$r['id'];
            $u->bind_param('i', $id);
            $u->execute();
            $u->close();
        }
    }
    $st->close();

    // 2. merged-away files
    $res = $db->query("SELECT id, chat_id, extra FROM chat_files WHERE extra LIKE '%\"members\"%'");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $extra = json_decode((string)$r['extra'], true) ?: [];
        $changed = false;
        foreach ($extra['members'] ?? [] as $i => $m) {
            if (count($out['trashed']) >= $max) {
                break 2;
            }
            if (!empty($m['trashedUtc']) || empty($m['fileId']) || strtotime((string)($m['utc'] ?? 'now')) > time() - $days * 86400) {
                continue;
            }
            if (!co_files_in_versions((string)$m['fileId'], (string)$r['chat_id'])) {
                $out['errors'][] = ($m['path'] ?? '') . ': not in scratch/.versions, left alone';
                continue;
            }
            if ($trash((string)$r['chat_id'], (string)($m['path'] ?? ''), 'merged', (string)$m['fileId'], 0)) {
                $extra['members'][$i]['trashedUtc'] = gmdate('Y-m-d\TH:i:s\Z');
                $changed = true;
            }
        }
        if ($changed && $apply) {
            co_files_save_extra($db, (int)$r['id'], $extra);
        }
    }

    // 3. legacy copies: every artifacts/<job>/ record of a message older than 30 days, unless it is the newest version.
    //    Intended loss (owner's rule of 2026-10-28, chat CHAT-20261028041759-5W87): old versions expire after 30 days,
    //    only the newest version of a file is kept; Drive drops unpinned revisions on the same schedule.
    $st = $db->prepare(
        "SELECT e.chat_id, e.seq, e.data, c.project_id FROM events e JOIN chats c ON c.chat_id = e.chat_id
          WHERE e.type = 'assistant_message' AND e.created_utc < UTC_TIMESTAMP(3) - INTERVAL ? DAY
            AND e.data LIKE '%/artifacts/%' ORDER BY e.created_utc"
    );
    $st->bind_param('i', $days);
    $st->execute();
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    foreach ($rows as $r) {
        $ev = json_decode((string)$r['data'], true);
        foreach (($ev['artifacts'] ?? []) as $a) {
            if (count($out['trashed']) >= $max) {
                break 2;
            }
            $p = (string)($a['driveRelativePath'] ?? '');
            if (!is_array($a) || !empty($a['ref']) || !preg_match('#/artifacts/[^/]+/#', $p)) {
                continue;
            }
            $chk = $db->prepare('SELECT 1 FROM files_trash WHERE path_sha = ?');
            $ps = sha1($p);
            $chk->bind_param('s', $ps);
            $chk->execute();
            $done = $chk->get_result()->fetch_row() !== null;
            $chk->close();
            if ($done) {
                continue;
            }
            $k = co_files_key($a, 'artifact', (string)$r['project_id'], (string)$r['chat_id'], null, null);
            $h = $db->prepare('SELECT head_seq, head_sha256 FROM chat_files WHERE chat_id = ? AND path_sha = ?');
            $ks = sha1((string)$k['key']);
            $h->bind_param('ss', $r['chat_id'], $ks);
            $h->execute();
            $head = $h->get_result()->fetch_assoc();
            $h->close();
            // The newest version stays (the agent may have deleted the live file); a copy equal to the head goes too.
            // Only a copy with a confirmed newer version of its file goes (no row yet = not confirmed).
            // A copy with the head's content stays too: it may be the only copy left if the live file is deleted.
            $isHead = !$head || $head['head_seq'] === null || (int)$head['head_seq'] <= (int)$r['seq']
                || strtolower((string)($a['sha256'] ?? '')) === (string)$head['head_sha256'];
            if ($isHead) {
                continue;
            }
            $trash((string)$r['chat_id'], $p, 'legacy');
        }
    }
    return $out;
}
