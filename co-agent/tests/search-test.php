<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Associative chat search (search-lib.php, schema 20) against a throwaway MariaDB (never production).
// Driver: python co-agent/tests/actions-test.py --test search-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';
require __DIR__ . '/../web/agent/dossier-lib.php';
require __DIR__ . '/../web/agent/search-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static fn(string $sql, array $p = []): array => co_search_q($db, $sql, $p);
$throws = static function (callable $f): bool {
    try { $f(); } catch (InvalidArgumentException) { return true; }
    return false;
};
// int8 vector with one hot axis (plus a small tail so that the norm is not trivial)
$hot = static function (int $axis, int $val = 100): string {
    $v = array_fill(0, CO_VEC_DIM, 0);
    $v[$axis] = $val;
    $v[255] = 5;
    return pack('c' . CO_VEC_DIM, ...$v);
};

foreach (['events', 'protocol', 'chats', 'projects', 'shares', 'vec_words', 'vec_protocol', 'aliases'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP()),
    ('P2', 'sam', 'Sam', '{}', 'x', UTC_TIMESTAMP()), ('P3', 'owner', 'Old', '{}', 'x', UTC_TIMESTAMP())");
$q("UPDATE projects SET archived = 1 WHERE project_id = 'P3'");
foreach ([['CHAT-A', 'P1'], ['CHAT-B', 'P1'], ['CHAT-C', 'P1'], ['CHAT-D', 'P1'], ['CHAT-S', 'P2'], ['CHAT-X', 'P3']] as [$c, $p]) {
    $q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES (?, ?, ?, '{}', 'x', UTC_TIMESTAMP())", [$c, $p, "title $c"]);
}
$ev = [
    ['CHAT-A', 'ONLYOFFICE не открывается снаружи, туннель отвечает ошибкой DNS'],
    ['CHAT-B', 'Обсуждали голосовой ввод на телефоне и повторы слов'],
    ['CHAT-C', 'Разное про погоду и планы на выходные'],
    ['CHAT-D', 'Тут ничего про поиск'],
    ['CHAT-S', 'ONLYOFFICE у Софии тоже не открывается'],
    ['CHAT-X', 'ONLYOFFICE в архивном проекте'],
];
foreach ($ev as $i => [$c, $t]) {
    $q("INSERT INTO events (event_id, chat_id, seq, type, text, data, sha, synced_utc) VALUES (?, ?, 1, 'user_message', ?, '{}', 'x', UTC_TIMESTAMP())", ["E$i", $c, $t]);
}
$pr = [
    ['CHAT-B', 1, 'Голосовой ввод Android дублирует слова при диктовке'],
    ['CHAT-C', 1, 'Ночная резервная копия базы растёт каждый день'],
    ['CHAT-D', 1, 'Протокольная запись без совпадений'],
    ['CHAT-D', 2, 'Старая запись про резервную копию'],
];
foreach ($pr as [$c, $n, $t]) {
    $q("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, created_utc, updated_utc) VALUES (?, ?, 'fact', ?, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP())", [$c, $n, $t]);
}
$q("UPDATE protocol SET status = 'superseded' WHERE chat_id = 'CHAT-D' AND entry_no = 2");
$scope = ['sql' => 'p.env = ? AND p.archived = 0', 'params' => ['owner']];

// ---- full-text over messages and protocol, merged
$r = co_search_chats($db, 'owner', $scope, 'ONLYOFFICE');
$ok('messages: ONLYOFFICE finds CHAT-A, not other envs or archived projects', array_keys($r['chats']) === ['CHAT-A']);
$ok('no vectors yet: vector=false', $r['vector'] === false);
$r = co_search_chats($db, 'owner', $scope, 'диктовка android');
$ok('protocol only: диктовка android finds CHAT-B via the protocol entry', isset($r['chats']['CHAT-B']) && $r['chats']['CHAT-B']['ranks']['protocol'] === 1);
$ok('protocol match carries the message number', str_starts_with($r['chats']['CHAT-B']['matches'][0] ?? '', '#1 (protocol): '));
$r = co_search_chats($db, 'owner', $scope, 'голосовой ввод');
$ok('in both lists: messages and protocol rank 1, scores add up', ($r['chats']['CHAT-B']['ranks'] ?? null) === ['messages' => 1, 'protocol' => 1, 'vector' => null]
    && abs($r['chats']['CHAT-B']['score'] - 2 / 61) < 1e-5);
$r = co_search_chats($db, 'owner', $scope, 'резервную копию');
$ok('superseded protocol entries are not searched', !isset($r['chats']['CHAT-D']) && isset($r['chats']['CHAT-C']));
$r = co_search_chats($db, 'owner', $scope, 'ONLYOFFICE', [], 'CHAT-A');
$ok('exclude: the current chat is left out', !isset($r['chats']['CHAT-A']));

// ---- aliases
$ok('alias_put: one term alone is not stored', co_alias_op($db, 'alias_put', ['groups' => [['env' => '', 'grp' => 'Q1', 'terms' => ['solo']]]])['inserted'] === 0);
$ok('alias_put: bad env refused', $throws(fn() => co_alias_op($db, 'alias_put', ['groups' => [['env' => 'Bad Env', 'grp' => 'x', 'terms' => ['a1', 'b2']]]])));
$n = co_alias_op($db, 'alias_put', ['groups' => [
    ['env' => 'owner', 'grp' => 'own:onlyoffice', 'source' => 'model', 'terms' => ['ONLYOFFICE', 'офисный редактор', 'Офисный  редактор']],
    ['env' => 'sam', 'grp' => 'own:voice', 'source' => 'model', 'terms' => ['погода', 'голосовой ввод']],
]])['inserted'];
$ok('alias_put: terms normalised and deduplicated', $n === 4);
$ok('alias expand: phrase in the query -> other names of the group', co_alias_expand($db, 'owner', 'Почему офисный редактор недоступен') === ['onlyoffice']);
co_alias_op($db, 'alias_put', ['groups' => [['env' => 'owner', 'grp' => 'g1', 'terms' => ['саша', 'александра']], ['env' => 'owner', 'grp' => 'g2', 'terms' => ['саша', 'александр']],
    ['env' => 'owner', 'grp' => 'g3', 'terms' => ['саша', 'дети']]]]);
$ok('alias expand: a term in 3+ groups expands nothing', co_alias_expand($db, 'owner', 'саша') === [] && co_alias_expand($db, 'owner', 'александра') === ['саша']);
co_alias_op($db, 'alias_delete', ['groups' => [['env' => 'owner', 'grp' => 'g1'], ['env' => 'owner', 'grp' => 'g2'], ['env' => 'owner', 'grp' => 'g3']]]);
$ok('alias expand: rows of another env are not used', co_alias_expand($db, 'owner', 'погода') === []);
$r = co_search_chats($db, 'owner', $scope, 'офисный редактор недоступен');
$ok('alias: "офисный редактор" finds the ONLYOFFICE chat', array_key_first($r['chats']) === 'CHAT-A' && $r['aliases'] === ['onlyoffice']);
$fq = co_search_fts_query(['офисный редактор', 'редактор onlyoffice'], ['onlyoffice', 'резервная копия базы', 'a "b']);
$ok('alias query: user words as they are, aliases with <, phrase quoted, no stray operators', str_contains($fq, 'onlyoffice onlyoffic*') && str_contains($fq, '<"резервная копия базы"') && !str_contains($fq, '"b')
    && str_contains(co_search_fts_query(['офис'], ['onlyoffice']), '<onlyoffice <onlyoffic*')
    && substr_count($fq, 'onlyoffic*') === 1);
co_alias_op($db, 'alias_put', ['groups' => [['env' => 'owner', 'grp' => 'own:onlyoffice', 'terms' => ['onlyoffice', 'only office']]]]);
co_alias_op($db, 'alias_put', ['groups' => [['env' => 'owner', 'grp' => 'own:onlyoffice', 'terms' => ['typo']]]]);
$ok('alias_put: one term leaves the stored group alone', count($q("SELECT * FROM aliases WHERE grp = 'own:onlyoffice'")) === 2);
$ok('alias_put replaces the group', count($q("SELECT * FROM aliases WHERE grp = 'own:onlyoffice'")) === 2);
$ok('alias_delete', co_alias_op($db, 'alias_delete', ['groups' => [['env' => 'sam', 'grp' => 'own:voice']]])['deleted'] === 2);
$ok('alias_list for an env includes general rows', count(co_alias_op($db, 'alias_list', ['env' => 'owner'])['aliases']) === 2);

$long = str_repeat('для что как почему где когда зачем ', 6) . 'вопрос';
$r = co_search_chats($db, 'owner', $scope, $long, ['ONLYOFFICE']);
$ok('a long question does not push the expanded terms out', isset($r['chats']['CHAT-A']));

// ---- vectors
$todo = co_vec_todo($db, []);
$ok('vec_todo: active entries without vectors', count($todo['rows']) === 3 && $todo['left'] === 3);
$byChat = [];
foreach ($todo['rows'] as $row) $byChat[$row['chatId']] = $row;
$ok('vec_put: bad vector refused', $throws(fn() => co_vec_put($db, ['protocol' => [['id' => 1, 'chatId' => 'CHAT-C', 'sha' => str_repeat('a', 40), 'scale' => 1, 'v' => base64_encode('short')]]])));
$put = [];
foreach (['CHAT-B' => 1, 'CHAT-C' => 2, 'CHAT-D' => 3] as $c => $axis) {
    $v = $hot($axis);
    $put[] = ['id' => $byChat[$c]['id'], 'chatId' => $c, 'sha' => sha1($byChat[$c]['text']), 'scale' => 1 / sqrt(100 * 100 + 25), 'v' => base64_encode($v)];
}
$ok('vec_put: protocol vectors and words', co_vec_put($db, ['protocol' => $put, 'words' => [
    ['w' => 'backup', 'n' => 2, 'scale' => 0.01, 'v' => base64_encode($hot(2)), 'freq' => 5],
    ['w' => 'резервная', 'n' => 3, 'scale' => 0.01, 'v' => base64_encode($hot(2)), 'freq' => 9],
    ['w' => 'резервуар', 'n' => 3, 'scale' => 0.01, 'v' => base64_encode($hot(3)), 'freq' => 1],
    ['w' => 'voice', 'n' => 1, 'scale' => 0.01, 'v' => base64_encode($hot(1)), 'freq' => 3],
]]) === ['protocol' => 3, 'words' => 4, 'skipped' => 0]);
$ok('vec_put: upper-case word skipped, not stored', co_vec_put($db, ['words' => [['w' => 'Backup', 'n' => 1, 'scale' => 1, 'v' => base64_encode($hot(1))]]])['skipped'] === 1
    && !$q("SELECT * FROM vec_words WHERE word = 'Backup'"));
$ok('vec_todo: nothing left', co_vec_todo($db, [])['left'] === 0);
$r = co_search_chats($db, 'owner', $scope, 'nightly backup');
$ok('vector: English "backup" finds the Russian protocol entry of CHAT-C', $r['vector'] === true && array_key_first($r['chats']) === 'CHAT-C'
    && $r['chats']['CHAT-C']['ranks']['vector'] === 1 && $r['chats']['CHAT-C']['ranks']['messages'] === null);
$q("UPDATE vec_protocol SET chat_id = 'CHAT-B' WHERE chat_id = 'CHAT-C'");
$r = co_search_chats($db, 'owner', $scope, 'nightly backup');
$ok('vector: a row whose chat_id disagrees with its protocol entry is ignored', ($r['chats']['CHAT-C']['ranks']['vector'] ?? null) === null
    && !str_contains(implode(' ', $r['chats']['CHAT-B']['matches'] ?? []), 'резервная'));
$q("UPDATE vec_protocol x JOIN protocol p ON p.id = x.protocol_id SET x.chat_id = p.chat_id");
$qv = co_vec_query($db, ['резервной']);
$ok('vector: unknown form by the most frequent word with the longest prefix', $qv !== null && $qv[2] > 0.9);
$ok('vector: no known word -> null', co_vec_query($db, ['zzzzqq', '2026']) === null);
$q("UPDATE protocol SET text = 'Голосовой ввод: исправлено' WHERE chat_id = 'CHAT-B'");
$ok('vec_todo: changed text needs a new vector', co_vec_todo($db, [])['left'] === 1);
$q("DELETE FROM protocol WHERE chat_id = 'CHAT-D'");
$t = co_vec_todo($db, []);
$ok('vec_todo: vectors of deleted entries are removed', $t['removed'] === 1 && count($q('SELECT * FROM vec_protocol')) === 2);
$w = co_vec_words($db, ['after' => 'резервная']);
$ok('vec_words pages by word', $w['words'] === [['резервуар', 1]]);
$tx = co_vec_texts($db, ['limit' => 2]);
$ok('vec_texts pages by event id', count($tx['rows']) === 2 && $tx['rows'][0][0] === 'E0');

$pt = co_vec_protocol_texts($db, ['env' => 'owner']);
$ok('protocol_texts: active entries of the env', array_column($pt['rows'], 1) === ['CHAT-B', 'CHAT-C'] && co_vec_protocol_texts($db, ['env' => 'sam'])['rows'] === []);

// ---- find_chats (host op of aiwb-chat find)
$f = co_search_find($db, ['q' => 'ONLYOFFICE', 'env' => 'owner']);
$ok('find: own env only', array_column($f['chats'], 'chatId') === ['CHAT-A'] && $f['chats'][0]['title'] === 'title CHAT-A');
$q("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('chat', 'CHAT-S', 'owner', 'viewer', UTC_TIMESTAMP())");
$f = co_search_find($db, ['q' => 'ONLYOFFICE', 'chatId' => 'CHAT-B']);
$ok('find: env from chatId, chats shared with it count', $f['env'] === 'owner' && array_column($f['chats'], 'chatId') === ['CHAT-A', 'CHAT-S']
    || $f['env'] === 'owner' && array_column($f['chats'], 'chatId') === ['CHAT-S', 'CHAT-A']);
$f = co_search_find($db, ['q' => 'ONLYOFFICE', 'chatId' => 'CHAT-A']);
$ok('find: the asking chat is left out unless includeSelf', !in_array('CHAT-A', array_column($f['chats'], 'chatId'), true)
    && in_array('CHAT-A', array_column(co_search_find($db, ['q' => 'ONLYOFFICE', 'chatId' => 'CHAT-A', 'includeSelf' => true])['chats'], 'chatId'), true));
$ok('find: bad input refused', $throws(fn() => co_search_find($db, ['q' => 'x', 'env' => 'owner'])) && $throws(fn() => co_search_find($db, ['q' => 'abc']))
    && $throws(fn() => co_search_find($db, ['q' => 'abc', 'chatId' => 'CHAT-NONE'])));
$ok('find: no internal entry ids in the answer', !array_key_exists('entryIds', $f['chats'][0] ?? []));

// ---- secretary hints on the chat search (dossier-lib co_related, 09.11)
$r = co_search_chats($db, 'owner', $scope, 'голосовой ввод', [], null, false);
$ok('messages=false: the message list is skipped', isset($r['chats']['CHAT-B']) && $r['chats']['CHAT-B']['ranks']['messages'] === null && $r['chats']['CHAT-B']['entryIds'] !== []);
$q("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, created_utc, updated_utc) VALUES ('CHAT-C', 5, 'fact', 'Расчёт: лифт elevator здания', 3, UTC_TIMESTAMP(), UTC_TIMESTAMP())");
co_alias_op($db, 'alias_put', ['groups' => [['env' => 'owner', 'grp' => 'own:лифт', 'terms' => ['лифт', 'elevator']]]]);
$ok('alias groups keep the query word that called them', co_alias_groups($db, 'owner', 'космический лифт') === ['лифт' => ['elevator']]);
$hint = static fn(string $text): array => array_map(static fn($h) => $h['chatId'] . ':' . $h['no'], co_related($db, 'owner', $text, 'CHAT-A', false)['hits']);
$ok('hints: a word and its alias name are one word, not two', !in_array('CHAT-C:5', $hint('Как спустить космический лифт с орбиты'), true));
$ok('hints: two words of the message (one through its alias) give a hint', in_array('CHAT-C:5', $hint('Как спустить космический лифт для здания'), true));
$ok('hints: entries carry no internal fields', !array_key_exists('rare', co_related($db, 'owner', 'Как спустить космический лифт для здания', 'CHAT-A', false)['hits'][0] ?? ['rare' => 1]));

$ok('hints: a stem of digits (230000V) does not break the hints', is_array(co_related($db, 'owner', 'Кабель 230000V и 100000kWh для здания лифт', 'CHAT-A', false)['hits']));

echo $fail ? "\n$fail FAILED\n" : "\nall ok\n";
exit($fail ? 1 : 0);
