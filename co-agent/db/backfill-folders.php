<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// One-off (schema 3): fill projects.folder_id and chats.folder_id for records synced before drive-sync stored them.
// Chats: from drive_map 'chatdir' rows (db/backfill-map.php). Projects: parents of every project.json on Drive whose
// folder name equals its projectId; also records them in drive_map as 'projectdir'.
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';
require CO_AGENT_WEB . '/drive-lib.php';

$db = co_db();
$db->query("UPDATE chats c JOIN drive_map m ON m.kind = 'chatdir' AND m.rec_id = c.chat_id SET c.folder_id = m.file_id");
$chats = $db->affected_rows;

$known = [];
$res = $db->query('SELECT project_id FROM projects');
while ($r = $res->fetch_assoc()) {
    $known[$r['project_id']] = true;
}
$projects = 0;
$page = co_drive_get('files', ['q' => "name='project.json' and trashed=false", 'fields' => 'files(id,parents)', 'pageSize' => 1000]);
foreach ($page['files'] ?? [] as $f) {
    $parent = $f['parents'][0] ?? null;
    if (!is_string($parent)) {
        continue;
    }
    $meta = co_drive_meta($parent);
    $name = (string)($meta['name'] ?? '');
    if (!isset($known[$name])) {
        continue;
    }
    $db->prepare('UPDATE projects SET folder_id=? WHERE project_id=?')->execute([$parent, $name]);
    $projects += $db->affected_rows;
    $db->prepare("INSERT IGNORE INTO drive_map (file_id, kind, rec_id, updated_utc) VALUES (?, 'projectdir', ?, UTC_TIMESTAMP(3))")
        ->execute([$parent, $name]);
}
$r = $db->query('SELECT (SELECT COUNT(*) FROM chats WHERE folder_id IS NULL) c, (SELECT COUNT(*) FROM projects WHERE folder_id IS NULL) p')->fetch_assoc();
echo "chats updated: $chats, projects updated: $projects, still without folder: chats {$r['c']}, projects {$r['p']}\n";
