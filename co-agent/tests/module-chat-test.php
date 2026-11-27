<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Run via tests/actions-test.py; throwaway database, fake Drive materializer.
declare(strict_types=1);
require __DIR__ . '/../web/agent/module-chat-lib.php';
$db = co_db(); $fail = 0;
$ok = static function ($name, $value) use (&$fail) { echo ($value ? 'ok   ' : 'FAIL ') . $name . "\n"; if (!$value) $fail++; };
$dir = co_module_home('planner'); @mkdir($dir . '/context', 0700, true);
file_put_contents($dir . '/context/rules.md', 'Help with planning.');
$man = ['title' => 'Planner', 'provides' => ['moduleChat' => ['mode' => 'planning', 'panel' => 'view', 'icon' => 'P'],
    'modes' => [['key' => 'planning', 'rules' => 'context/rules.md']],
    'ui' => [['slot' => 'ui.page', 'id' => 'main', 'entry' => 'ui/main.html', 'app' => true],
             ['slot' => 'ui.panel', 'id' => 'view', 'entry' => 'ui/view.html']]]];
$files = ['context/rules.md' => hash('sha256', 'Help with planning.')];
$db->prepare("INSERT INTO modules (id,version,enabled,standard,manifest_json,files_json,installed_utc,updated_utc) VALUES ('planner','1.0.0',1,1,?,?,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))")
    ->execute([co_db_json($man), co_db_json($files)]);
co_modules($db, true);
$count = 0;
$drive = static function ($p, $c) use (&$count) { $count++; return ['project-folder', 'chat-folder']; };
$a = co_module_chat_open($db, 'owner', 'planner', $drive);
$b = co_module_chat_open($db, 'owner', 'planner', $drive);
$ok('repeat opens same chat, Drive called once', $a === $b && $count === 1);
$row = $db->query("SELECT data FROM chats")->fetch_row(); $chat = json_decode($row[0], true);
$ok('created on first open with mode and title', $chat['planning'] === true && $chat['title'] === 'P Planner' && $chat['titleAuto'] === false);
$c = co_module_chat_open($db, 'sam', 'planner', $drive);
$ok('environments isolated', $c['chatId'] !== $a['chatId'] && $c['projectId'] !== $a['projectId']);
$ok('unknown module refused', isset(co_module_chat_open($db, 'owner', 'missing', $drive)['error']));
$bad = co_modules($db)['planner']; $bad['manifest']['provides']['moduleChat']['mode'] = 'missing';
$ok('undeclared mode refused', co_module_chat_decl($bad) === null);
$bad['manifest']['provides']['moduleChat']['mode'] = 'planning'; $bad['manifest']['provides']['moduleChat']['panel'] = 'main';
$ok('page cannot stand in for panel', co_module_chat_decl($bad) === null);
$db->query("UPDATE modules SET enabled=0"); co_modules($db, true);
$ok('disabled module refused', isset(co_module_chat_open($db, 'owner', 'planner', $drive)['error']));
$db->query("UPDATE modules SET enabled=1");
$man['envs'] = ['owner']; $db->prepare('UPDATE modules SET manifest_json=?')->execute([co_db_json($man)]); co_modules($db, true);
$ok('foreign private module refused', isset(co_module_chat_open($db, 'sam', 'planner', $drive)['error']));
unset($man['envs']); $db->prepare('UPDATE modules SET manifest_json=?')->execute([co_db_json($man)]); co_modules($db, true);
$chat['trashedUtc'] = gmdate('c'); co_db_put(['chats' => [$chat]]);
$ok('trash not silently restored', co_module_chat_open($db, 'owner', 'planner', $drive)['error'] === 'module_chat_in_trash');
unset($chat['trashedUtc']); $chat['title'] = 'My planner'; co_db_put(['chats' => [$chat]]);
$ok('restore reopens original', co_module_chat_open($db, 'owner', 'planner', $drive) === $a);
try { co_module_chat_open($db, 'alex', 'planner', static function () { throw new RuntimeException('offline'); }); } catch (RuntimeException) {}
$st = $db->prepare('SELECT data FROM kv WHERE scope=?'); $st->execute(['module-chat:alex']);
$reserved = json_decode($st->get_result()->fetch_row()[0], true);
$d = co_module_chat_open($db, 'alex', 'planner', $drive);
$ok('failed creation retries reserved identity', $d['chatId'] === $reserved['chat']['chatId']);
$db->prepare('DELETE FROM chats WHERE chat_id=?')->execute([$a['chatId']]);
$e = co_module_chat_open($db, 'owner', 'planner', $drive);
$ok('permanent deletion creates a fresh chat', $e['chatId'] !== $a['chatId']);
$movedProject = ['projectId' => 'moved-project', 'environmentId' => 'owner', 'title' => 'Other', 'createdUtc' => gmdate('c'), 'updatedUtc' => gmdate('c')];
co_db_put(['projects' => [$movedProject]]);
$st = $db->prepare('SELECT data FROM chats WHERE chat_id=?'); $st->execute([$e['chatId']]);
$movedChat = json_decode($st->get_result()->fetch_row()[0], true); $movedChat['projectId'] = 'moved-project';
co_db_put(['chats' => [$movedChat]]);
$ok('move within environment preserves chat', co_module_chat_open($db, 'owner', 'planner', $drive)['projectId'] === 'moved-project');
$db->query("UPDATE projects SET env='sam' WHERE project_id='moved-project'");
$ok('move outside environment refused', co_module_chat_open($db, 'owner', 'planner', $drive)['error'] === 'module_chat_moved');
$db->prepare('DELETE FROM chats WHERE chat_id=?')->execute([$d['chatId']]);
$db->prepare('UPDATE projects SET env=? WHERE project_id=?')->execute(['sam', $d['projectId']]);
$ok('moved Applications project refused', co_module_chat_open($db, 'alex', 'planner', $drive)['error'] === 'module_chat_project_moved');
exit($fail ? 1 : 0);
