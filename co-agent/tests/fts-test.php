<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Word stems in the full-text search (fts-lib.php) against a throwaway MariaDB (never production).
// Driver: python co-agent/tests/actions-test.py --test fts-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/db-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';
require __DIR__ . '/../web/agent/dossier-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static fn(string $sql, array $p = []): array => co_dossier_q($db, $sql, $p);

// ---- stemmer
$same = [
    ['капча', 'капчу', 'капчи', 'капчей'], ['браузер', 'браузере', 'браузерами'], ['релевантность', 'релевантности'],
    ['подтверждение', 'подтверждения', 'подтверждений'], ['проходит', 'проходить'], ['ночная', 'ночной', 'ночные'],
    ['test', 'tests', 'testing', 'tested'], ['policy', 'policies'], ['release', 'releases', 'released'], ['browser', 'browsers'], ['class', 'classes'],
    ['cache', 'caching', 'cached'],
];
foreach ($same as $set) {
    $p = array_unique(array_map('co_fts_prefix', $set));
    $ok('one prefix for ' . implode('/', $set) . ' -> ' . implode(',', $p), count($p) === 1 && $p[0] !== '');
}
$ok('ё = е', co_stem('прошёл') === co_stem('прошел'));
$ok('status and analysis keep their s', co_stem('status') === 'status' && co_stem('analysis') === 'analysis');
$ok('short stem falls back to 4 letters (копии/копия)', co_fts_prefix('копии') === 'копи' && co_fts_prefix('копия') === 'копи');
$ok('ids and digits stay exact', co_fts_prefix('t0na') === '' && co_fts_prefix('20261107') === '');
$ok('short words: no prefix', co_fts_prefix('дом') === '' && co_fts_prefix('run') === '');
$fq = co_fts_query('Почему +капча -Turnstile "x" (CHAT-20261107201900-T0NA)* @3 ~a <b');
$ok('query: no boolean operators from the user', preg_match('/[+\-"()~<>@]/', $fq) === 0);
$ok('query: word plus stem prefix', str_contains($fq, 'капча капч*') && str_contains($fq, 'turnstile turnstil*'));
$ok('query: 2-letter words left out, empty query -> ""', co_fts_query('a b co') === '' && co_fts_query('!!') === '');
$ok('function words left out', !str_contains(co_fts_query('резидент для мака'), 'для') && co_fts_query('для что') === 'для что');
$ok('needles are strings for numbers too', co_fts_needles('20261107 копия') === ['20261107', 'копи', 'копия']);
$ok('needles: stem first', co_fts_needles('капчу')[0] === 'капч');

// ---- database
foreach (['events', 'dossier', 'chats', 'projects', 'shares'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES ('CHAT-A', 'P1', 'A', '{}', 'x', UTC_TIMESTAMP())");
$msgs = [
    'Капчу Cloudflare Turnstile агент проходит только по разрешению пользователя',
    'Ночные копии базы данных растут каждый день',
    'The release tests failed after caching was enabled',
];
for ($i = 0; $i < 30; $i++) $msgs[] = "Служебная запись номер $i про выпуск версии";
// a long tool dump that names the word more often: the length damping keeps the short message first (seq 34)
$msgs[] = str_repeat('Выгрузка инструмента, строка лога браузера без особого смысла. ', 400) . str_repeat(' капчу', 3);
foreach ($msgs as $i => $t) {
    $q("INSERT INTO events (event_id, chat_id, seq, type, text, data, sha, synced_utc) VALUES (?, 'CHAT-A', ?, 'user_message', ?, '{}', 'x', UTC_TIMESTAMP())",
        ["E$i", $i + 1, $t]);
}
$hit = static function (string $query): array {
    return array_map(static fn($m) => (int)$m['seq'], db_chat_read(['chatId' => 'CHAT-A', 'q' => $query])['messages']);
};
$ok('chat search: "капча" finds "Капчу"', ($hit('капча')[0] ?? 0) === 1);
$ok('chat search: "ночная копия" finds "Ночные копии"', ($hit('ночная копия')[0] ?? 0) === 2);
$ok('chat search: "released test cache" finds "release tests caching"', ($hit('released test cache')[0] ?? 0) === 3);
$ok('chat search: short message above a long dump with more hits', ($hit('капча')[0] ?? 0) === 1 && in_array(34, $hit('капча'), true));
$ok('chat search: exact word still first', ($hit('выпуск капчу')[0] ?? 0) === 1);
$m = db_chat_read(['chatId' => 'CHAT-A', 'q' => 'копия'])['messages'];
$ok('chat search: snippet returned', isset($m[0]['text']) && str_contains($m[0]['text'], 'копии'));
$ok('chat search: numbers in the query do not break it', is_array($hit('номер 20261107')));
$ok('chat search: operators in the query do not break it', is_array($hit('+капча -"cloudflare (')));

$now = gmdate('Y-m-d H:i:s');
foreach ([['people', 'Ann Clauws', 'Рекрутер Randstad RiseSmart, встреча в Генте'], ['state', 'Собеседования', 'Интервью с компаниями на следующей неделе']] as [$sec, $subj, $text]) {
    $q("INSERT INTO dossier (env, section, subject, text, created_utc, updated_utc) VALUES ('owner', ?, ?, ?, ?, ?)", [$sec, $subj, $text, $now, $now]);
}
$d = co_dossier_search($db, 'owner', 'собеседование');
$ok('dossier search: "собеседование" finds "Собеседования"', ($d[0]['subject'] ?? '') === 'Собеседования');
$d = co_dossier_search($db, 'owner', 'рекрутеры');
$ok('dossier search: "рекрутеры" finds "Рекрутер"', ($d[0]['subject'] ?? '') === 'Ann Clauws');

echo $fail ? "\n$fail FAILED\n" : "\nall ok\n";
exit($fail ? 1 : 0);
