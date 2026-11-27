<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
require_once __DIR__ . '/modules-lib.php';

/** Serialized per environment, including Drive creation. Reservation survives a failed Drive request. */
function co_module_chat_open(mysqli $db, string $env, string $id, ?callable $materialize = null): array
{
    $m = co_modules($db)[$id] ?? null;
    if (!$m || !$m['enabled'] || !co_module_for_env($m, $env) || !($decl = co_module_chat_decl($m))) {
        return ['error' => 'module_chat_unavailable'];
    }
    $lock = 'module-chat:' . substr(hash('sha256', $env), 0, 40);
    $st = $db->prepare('SELECT GET_LOCK(?, 30)'); $st->execute([$lock]);
    if ((int)$st->get_result()->fetch_row()[0] !== 1) return ['error' => 'module_chat_busy'];
    try {
        $scope = 'module-chat:' . $env;
        $st = $db->prepare('SELECT data FROM kv WHERE scope=? AND name=?'); $st->execute([$scope, $id]);
        $row = $st->get_result()->fetch_row();
        $saved = $row ? json_decode($row[0], true) : null;
        if ($saved) {
            $st = $db->prepare('SELECT c.data, c.folder_id, p.env FROM chats c JOIN projects p ON p.project_id=c.project_id WHERE c.chat_id=?');
            $st->execute([$saved['chat']['chatId']]);
            if ($r = $st->get_result()->fetch_assoc()) {
                if ($r['env'] !== $env) return ['error' => 'module_chat_moved'];
                $chat = json_decode($r['data'], true);
                if (!empty($chat['trashedUtc'])) return ['error' => 'module_chat_in_trash', 'message' => 'Restore the module chat from Trash first.'];
                if (!empty($r['folder_id'])) return ['ok' => true, 'projectId' => $chat['projectId'], 'chatId' => $chat['chatId']];
                // A failed folder-id write is retried without replacing already synced chat metadata.
                $saved['chat'] = $chat;
            }
            if (!$r && !empty($saved['complete'])) $saved = null;
        }
        if (!$saved) {
            $now = gmdate('Y-m-d\TH:i:s\Z');
            $pid = 'Modules-' . substr(hash('sha256', $env), 0, 20);
            do {
                $cid = 'CHAT-' . gmdate('YmdHis') . '-' . strtoupper(bin2hex(random_bytes(2)));
                $st = $db->prepare('SELECT 1 FROM chats WHERE chat_id=?'); $st->execute([$cid]);
            } while ($st->get_result()->fetch_row());
            $saved = ['project' => ['projectId' => $pid, 'environmentId' => $env, 'title' => 'Applications',
                'description' => '', 'defaultAgentId' => 'codex', 'archived' => false, 'createdUtc' => $now, 'updatedUtc' => $now],
                'chat' => ['chatId' => $cid, 'projectId' => $pid, 'title' => trim($decl['icon'] . ' ' . $decl['label']),
                    'titleAuto' => false, 'rootBranchId' => 'BR-root', 'defaultMode' => 'cowork',
                    'createdUtc' => $now, 'updatedUtc' => $now, 'moduleChat' => $id, $decl['mode'] => true]];
            $json = co_db_json($saved);
            $st = $db->prepare('INSERT INTO kv (scope,name,data,sha,updated_utc) VALUES (?,?,?,?,UTC_TIMESTAMP(3)) ON DUPLICATE KEY UPDATE data=VALUES(data),sha=VALUES(sha),updated_utc=VALUES(updated_utc)');
            $st->execute([$scope, $id, $json, hash('sha256', $json)]);
        }
        // Preserve an existing project, including its preferences.
        $st = $db->prepare('SELECT data,folder_id,env FROM projects WHERE project_id=?');
        $st->execute([$saved['project']['projectId']]);
        $projectRow = $st->get_result()->fetch_assoc();
        if ($projectRow && $projectRow['env'] !== $env) return ['error' => 'module_chat_project_moved'];
        if ($projectRow) $saved['project'] = json_decode($projectRow['data'], true);
        $materialize ??= 'co_module_chat_drive';
        [$pf, $cf] = $materialize($saved['project'], $saved['chat']);
        $result = co_db_put(['projects' => [$saved['project']], 'chats' => [$saved['chat']]]);
        if ($result['rejected']) throw new RuntimeException('module chat write failed');
        $db->prepare('UPDATE projects SET folder_id=? WHERE project_id=?')->execute([$pf, $saved['project']['projectId']]);
        $db->prepare('UPDATE chats SET folder_id=? WHERE chat_id=?')->execute([$cf, $saved['chat']['chatId']]);
        $saved['complete'] = true;
        $json = co_db_json($saved);
        $db->prepare('UPDATE kv SET data=?,sha=?,updated_utc=UTC_TIMESTAMP(3) WHERE scope=? AND name=?')
            ->execute([$json, hash('sha256', $json), $scope, $id]);
        return ['ok' => true, 'projectId' => $saved['project']['projectId'], 'chatId' => $saved['chat']['chatId']];
    } finally {
        $st = $db->prepare('SELECT RELEASE_LOCK(?)'); $st->execute([$lock]);
    }
}

/** Idempotent Drive materialization under the server's workbench, no caller-supplied folder authority. */
function co_module_chat_drive(array $project, array $chat): array
{
    require_once __DIR__ . '/drive-lib.php';
    $find = static function (string $parent, string $name, bool $folder): ?string {
        $q = "'" . addcslashes($parent, "'\\") . "' in parents and name='" . addcslashes($name, "'\\") . "' and trashed=false";
        if ($folder) $q .= " and mimeType='application/vnd.google-apps.folder'";
        $r = co_drive_get('files', ['q' => $q, 'fields' => 'files(id)', 'supportsAllDrives' => 'true', 'includeItemsFromAllDrives' => 'true']);
        return $r['files'][0]['id'] ?? null;
    };
    $roots = co_drive_get('files', ['q' => "name='AI-Workbench' and mimeType='application/vnd.google-apps.folder' and trashed=false", 'fields' => 'files(id)']);
    $root = null;
    foreach ($roots['files'] ?? [] as $f) if ($find($f['id'], 'transport', true)) { $root = $f['id']; break; }
    if (!$root) throw new RuntimeException('Workbench folder unavailable');
    $folder = static fn($parent, $name) => $find($parent, $name, true) ?? co_drive_create_folder($parent, $name);
    $write = static function ($parent, $name, $doc) use ($find): void {
        if (!$find($parent, $name, false)) co_drive_create_text($parent, $name, co_db_json($doc), 'application/json');
    };
    $pf = $folder($folder($root, 'projects'), $project['projectId']);
    $write($pf, 'project.json', $project);
    $folder($pf, 'context'); $folder($pf, 'files');
    $cf = $folder($folder($pf, 'chats'), $chat['chatId']);
    $write($cf, 'chat.json', $chat);
    $folder($cf, 'events'); $folder($cf, 'scratch');
    return [$pf, $cf];
}
