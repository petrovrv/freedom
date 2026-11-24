<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);

require_once __DIR__ . '/gateway-lib.php';

// Error of a job or subtask a shared (restricted) chat may not run: devices, context folders or a confidential model.
// The value is legacy wire (gateway-lib.php): CO Web shows its own text for it.
const SHARE_REFUSED = CO_GATEWAY_SHARE_REFUSED;

// Sharing of projects and chats between environments (schema 10, CO Web 0.45.0).

/** Run a SELECT and return all rows (assoc). Always closes the statement. */
function share_q(mysqli $db, string $sql, array $params): array
{
    $st = $db->prepare($sql);
    try {
        $st->execute($params);
        $res = $st->get_result();
        $rows = $res->fetch_all(MYSQLI_ASSOC);
        $res->free();
        return $rows;
    } finally {
        $st->close();
    }
}

/** Run a write statement. Always closes the statement. */
function share_exec(mysqli $db, string $sql, array $params): void
{
    $st = $db->prepare($sql);
    try {
        $st->execute($params);
    } finally {
        $st->close();
    }
}

/** DATETIME(3) UTC string -> 'Y-m-d\TH:i:s.v\Z' or null. */
function share_iso_utc(?string $dt): ?string
{
    if ($dt === null || $dt === '') {
        return null;
    }
    $tz = new DateTimeZone('UTC');
    $d = DateTimeImmutable::createFromFormat('Y-m-d H:i:s.v', $dt, $tz)
        ?: DateTimeImmutable::createFromFormat('Y-m-d H:i:s', $dt, $tz);
    return $d ? $d->format('Y-m-d\TH:i:s.v\Z') : null;
}

/** Unique environments from the push user map, as a list. */
function share_known_envs(): array
{
    $envs = [];
    foreach (co_push_users() as $env) {
        if (is_string($env) && $env !== '') {
            $envs[$env] = true;
        }
    }
    return array_keys($envs);
}

function share_rank(?string $role): int
{
    return match ($role) {
        'viewer' => 1,
        'editor' => 2,
        'owner'  => 3,
        default  => 0,
    };
}

function share_can(?string $role, string $need): bool
{
    $r = share_rank($role);
    return match ($need) {
        'read'   => $r >= 1,
        'write'  => $r >= 2,
        'manage' => $r >= 3,
        default  => false,
    };
}

/** Role stored in shares for one target/env, or null. */
function share_role_for(mysqli $db, string $kind, string $id, string $env): ?string
{
    $rows = share_q($db,
        'SELECT role FROM shares WHERE kind = ? AND target_id = ? AND env = ?',
        [$kind, $id, $env]);
    return $rows ? (string)$rows[0]['role'] : null;
}

function share_project_env(mysqli $db, string $projectId): ?string
{
    $rows = share_q($db, 'SELECT env FROM projects WHERE project_id = ?', [$projectId]);
    return ($rows && $rows[0]['env'] !== null) ? (string)$rows[0]['env'] : null;
}

function share_project_role(mysqli $db, string $myEnv, bool $admin, string $projectId): ?string
{
    $rows = share_q($db, 'SELECT env FROM projects WHERE project_id = ?', [$projectId]);
    if (!$rows) {
        return null; // project missing
    }
    if ($admin || $rows[0]['env'] === $myEnv) {
        return 'owner';
    }
    return share_role_for($db, 'project', $projectId, $myEnv);
}

function share_chat_project(mysqli $db, string $chatId): ?string
{
    $rows = share_q($db, 'SELECT project_id FROM chats WHERE chat_id = ?', [$chatId]);
    return ($rows && $rows[0]['project_id'] !== null) ? (string)$rows[0]['project_id'] : null;
}

/**
 * F15.2: trashedUtc of a chat in the CO trash ('' = not trashed, null = no row). $lock (inside a transaction) holds the chat
 * row FOR UPDATE, so a job queued under it and the trash clean-up (delete_chat ifTrashedUtc) are serialized on that row.
 */
function share_chat_trashed(mysqli $db, string $chatId, bool $lock = false): ?string
{
    $rows = share_q($db, 'SELECT data FROM chats WHERE chat_id = ?' . ($lock ? ' FOR UPDATE' : ''), [$chatId]);
    if (!$rows) {
        return null;
    }
    $data = json_decode((string)$rows[0]['data'], true);
    $t = is_array($data) ? ($data['trashedUtc'] ?? '') : '';
    return is_string($t) ? $t : '';
}

/**
 * F15.2: run $insert (the INSERT of a job) unless the chat is in the CO trash - check and INSERT in one transaction holding
 * the chat row, so the trash clean-up (delete_chat ifTrashedUtc) sees the job or the job sees the trash. False = trashed.
 * Inside a caller's open transaction it runs in that one (a new begin_transaction would commit it silently).
 * Modules that queue jobs themselves (voice, whatsapp, canvas) call it too.
 */
function share_job_insert_untrashed(mysqli $db, string $chatId, callable $insert): bool
{
    $outer = (int)(share_q($db, 'SELECT @@in_transaction AS t', [])[0]['t'] ?? 0) === 1;
    if (!$outer) {
        $db->begin_transaction();
    }
    try {
        if ((string)share_chat_trashed($db, $chatId, true) !== '') {
            if (!$outer) {
                $db->rollback();
            }
            return false;
        }
        $insert();
        if (!$outer) {
            $db->commit();
        }
        return true;
    } catch (Throwable $e) {
        if (!$outer) {
            $db->rollback();
        }
        throw $e;
    }
}

/**
 * F15.2: jobs of a chat still queued or running (the trash clean-up keeps such a chat; a job stuck in claimed keeps it
 * until the job is ended - the safe side). A locking read: the latest committed rows whatever was read before.
 */
function share_chat_active_jobs(mysqli $db, string $chatId): int
{
    $rows = share_q($db, "SELECT COUNT(*) AS n FROM jobs WHERE chat_id = ? AND state IN ('inbox', 'claimed') AND result IS NULL
                          LOCK IN SHARE MODE", [$chatId]);
    return (int)($rows[0]['n'] ?? 0);
}

function share_chat_role(mysqli $db, string $myEnv, bool $admin, string $chatId): ?string
{
    $rows = share_q($db, 'SELECT project_id FROM chats WHERE chat_id = ?', [$chatId]);
    if (!$rows) {
        return null; // chat missing
    }
    $pid = $rows[0]['project_id'];
    $projRole = $pid !== null
        ? share_project_role($db, $myEnv, $admin, (string)$pid)
        : ($admin ? 'owner' : null);
    $chatRole = share_role_for($db, 'chat', $chatId, $myEnv);
    return share_rank($chatRole) > share_rank($projRole) ? $chatRole : $projRole;
}

function share_is_shared(mysqli $db, string $projectId, ?string $chatId): bool
{
    if ($chatId === null) {
        $rows = share_q($db,
            "SELECT 1 FROM shares WHERE kind = 'project' AND target_id = ? LIMIT 1",
            [$projectId]);
    } else {
        $rows = share_q($db,
            "SELECT 1 FROM shares
              WHERE (kind = 'project' AND target_id = ?) OR (kind = 'chat' AND target_id = ?)
              LIMIT 1",
            [$projectId, $chatId]);
    }
    return (bool)$rows;
}

/**
 * R2 (review S1-02, S5-01): the security context of a chat. Restricted = the chat, its project or any ancestor chat
 * (parent links in chat.json, up to 16 levels; a loop ends the walk) is shared, or one of their jobs was queued
 * restricted (request.restricted, written by the server only). A restricted job gets no devices (relay included), no
 * confidential (gateway) agent and no context folders, and its subtasks inherit that, however deep.
 */
function share_restricted(mysqli $db, string $projectId, string $chatId): bool
{
    $pid = $projectId;
    $cid = $chatId;
    $seen = [];
    for ($depth = 0; $cid !== '' && !isset($seen[$cid]); $depth++) {
        if ($depth >= 16) {
            return true;                      // deeper than any real chain: refuse rather than guess
        }
        $seen[$cid] = true;
        if (share_is_shared($db, $pid, $cid)) {
            return true;
        }
        if (share_q($db, "SELECT 1 FROM jobs WHERE chat_id = ? AND JSON_EXTRACT(request, '$.restricted') = 'true' LIMIT 1", [$cid])) {
            return true;
        }
        $rows = share_q($db, "SELECT JSON_VALUE(data, '$.parent.chatId') AS parent FROM chats WHERE chat_id = ?", [$cid]);
        $parent = $rows ? (string)($rows[0]['parent'] ?? '') : '';
        if ($parent === '') {
            return false;
        }
        $rows = share_q($db, 'SELECT project_id FROM chats WHERE chat_id = ?', [$parent]);
        if (!$rows) {
            return false;                     // the parent is gone: nothing left to inherit from
        }
        $pid = (string)$rows[0]['project_id'];
        $cid = $parent;
    }
    return false;
}

/**
 * job_submit of CO Web: restricted is written by the server only. A restricted chat gets restricted = true in its
 * request; devices, context folders or a confidential agent there give SHARE_REFUSED (else null).
 */
function share_submit_policy(mysqli $db, string $projectId, string $chatId, array &$req, bool $confidential): ?string
{
    unset($req['restricted']);
    if (!share_restricted($db, $projectId, $chatId)) {
        return null;
    }
    $req['restricted'] = true;
    $devices = $req['devices'] ?? null;
    $folders = $req['contextFolders'] ?? null;
    if ((is_array($devices) && $devices !== []) || (is_array($folders) && $folders !== []) || $confidential) {
        return SHARE_REFUSED;
    }
    return null;
}

function share_list(mysqli $db, string $kind, string $id): array
{
    $rows = share_q($db,
        'SELECT env, role, added_by, created_utc FROM shares
          WHERE kind = ? AND target_id = ? ORDER BY env',
        [$kind, $id]);
    $out = [];
    foreach ($rows as $r) {
        $out[] = [
            'env'        => (string)$r['env'],
            'role'       => (string)$r['role'],
            'addedBy'    => $r['added_by'] !== null ? (string)$r['added_by'] : null,
            'createdUtc' => share_iso_utc($r['created_utc'] !== null ? (string)$r['created_utc'] : null),
        ];
    }
    return $out;
}

/** Grant/update a share; $role null removes it. */
function share_set(mysqli $db, string $kind, string $id, string $env, ?string $role, string $byEmail): void
{
    if ($kind !== 'project' && $kind !== 'chat') {
        throw new InvalidArgumentException("Invalid share kind: $kind");
    }
    if ($role !== null && $role !== 'editor' && $role !== 'viewer') {
        throw new InvalidArgumentException("Invalid share role: $role");
    }
    if ($role === null) {
        share_exec($db,
            'DELETE FROM shares WHERE kind = ? AND target_id = ? AND env = ?',
            [$kind, $id, $env]);
        return;
    }
    share_exec($db,
        'INSERT INTO shares (kind, target_id, env, role, added_by, created_utc)
         VALUES (?, ?, ?, ?, ?, UTC_TIMESTAMP(3))
         ON DUPLICATE KEY UPDATE role = VALUES(role), added_by = VALUES(added_by)',
        [$kind, $id, $env, $role, $byEmail !== '' ? $byEmail : null]);
}

function share_delete_target(mysqli $db, string $kind, string $id): void
{
    share_exec($db, 'DELETE FROM shares WHERE kind = ? AND target_id = ?', [$kind, $id]);
}

/** Foreign projects visible to $env, via a project share or via shared chats. */
function share_shared_projects(mysqli $db, string $env): array
{
    $out = [];

    // 1) Project shares (these win)
    $rows = share_q($db,
        "SELECT s.target_id, s.role
           FROM shares s
           JOIN projects p ON p.project_id = s.target_id
          WHERE s.kind = 'project' AND s.env = ? AND p.env <> ?",
        [$env, $env]);
    foreach ($rows as $r) {
        $pid = (string)$r['target_id'];
        $out[$pid] = ['projectId' => $pid, 'role' => (string)$r['role'], 'via' => 'project', 'chatIds' => []];
    }

    // 2) Chat shares, grouped per project
    $rows = share_q($db,
        "SELECT c.project_id, c.chat_id, s.role
           FROM shares s
           JOIN chats c ON c.chat_id = s.target_id
           JOIN projects p ON p.project_id = c.project_id
          WHERE s.kind = 'chat' AND s.env = ? AND p.env <> ?
          ORDER BY c.project_id, c.chat_id",
        [$env, $env]);
    foreach ($rows as $r) {
        $pid = (string)$r['project_id'];
        $role = (string)$r['role'];
        if (isset($out[$pid]) && $out[$pid]['via'] === 'project') {
            continue;
        }
        if (!isset($out[$pid])) {
            $out[$pid] = ['projectId' => $pid, 'role' => $role, 'via' => 'chat', 'chatIds' => []];
        } elseif (share_rank($role) > share_rank($out[$pid]['role'])) {
            $out[$pid]['role'] = $role;
        }
        $out[$pid]['chatIds'][] = (string)$r['chat_id'];
    }

    ksort($out, SORT_STRING);
    return array_values($out);
}

/** Visibility fragment; bind env three times. */
function share_visible_sql(string $p = 'p', string $c = 'c'): string
{
    return "($p.env = ? OR $p.project_id IN (SELECT target_id FROM shares WHERE kind = 'project' AND env = ?)"
        . " OR $c.chat_id IN (SELECT target_id FROM shares WHERE kind = 'chat' AND env = ?))";
}
