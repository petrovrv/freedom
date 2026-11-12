<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// One-off: fill drive_map with chat folders (kind 'chatdir') for chats imported before drive-sync existed,
// so that trashing such a chat folder on Drive also deletes it from the database.
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';
require CO_AGENT_WEB . '/drive-lib.php';

$db = co_db();
$known = [];
$res = $db->query('SELECT chat_id, project_id FROM chats');
while ($r = $res->fetch_assoc()) {
    $known[$r['chat_id']] = $r['project_id'];
}

$pageToken = null;
$added = 0;
$seen = 0;
do {
    $q = ['q' => "mimeType='application/vnd.google-apps.folder' and name contains 'CHAT-' and trashed=false",
          'fields' => 'nextPageToken,files(id,name,parents)', 'pageSize' => 1000];
    if ($pageToken) {
        $q['pageToken'] = $pageToken;
    }
    $page = co_drive_get('files', $q);
    foreach ($page['files'] ?? [] as $f) {
        $name = (string)$f['name'];
        if (!isset($known[$name])) {
            continue;
        }
        // The parent must be a 'chats' folder of the chat's project
        $anc = co_drive_ancestor_names((string)$f['id'], 2);
        if (($anc[0] ?? '') !== 'chats' || ($anc[1] ?? '') !== $known[$name]) {
            continue;
        }
        $seen++;
        $st = $db->prepare("INSERT IGNORE INTO drive_map (file_id, kind, rec_id, updated_utc) VALUES (?, 'chatdir', ?, UTC_TIMESTAMP(3))");
        $st->execute([(string)$f['id'], $name]);
        $added += $st->affected_rows;
    }
    $pageToken = $page['nextPageToken'] ?? null;
} while ($pageToken);

echo "chats in db: " . count($known) . ", chat folders matched: $seen, drive_map rows added: $added\n";
