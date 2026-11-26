<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Schema 19 dossier and secretary hints against a throwaway MariaDB (never production).
// Driver: python co-agent/tests/actions-test.py --test dossier-test.php
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

foreach (['dossier', 'protocol', 'chats', 'projects', 'shares'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP()), ('P2', 'owner', 'Shared', '{}', 'x', UTC_TIMESTAMP()), ('P3', 'alex', 'Hers', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('project', 'P2', 'sam', 'editor', UTC_TIMESTAMP())");
$chats = ['CHAT-A' => 'P1', 'CHAT-B' => 'P1', 'CHAT-C' => 'P2', 'CHAT-D' => 'P3'];
foreach ($chats as $c => $p) {
    $q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES (?, ?, ?, '{}', 'x', UTC_TIMESTAMP())", [$c, $p, "Title $c"]);
}
$prot = [
    ['CHAT-A', 'Ночная копия базы данных растёт: 18, 23, 41 МБ за три дня', 'fact'],
    ['CHAT-B', 'Капча Cloudflare Turnstile не проходит даже при ручном клике в агентском браузере', 'fact'],
    ['CHAT-B', 'Решили проходить капчу только по разрешению пользователя с одноразовым кодом', 'decision'],
    ['CHAT-C', 'Турнир по шахматам у детей в субботу', 'fact'],
    ['CHAT-D', 'Капча Cloudflare Turnstile в браузере Алекс', 'fact'],
];
// filler rows so that natural-language scores are not degenerate on a tiny table
for ($i = 0; $i < 40; $i++) $prot[] = ['CHAT-A', "Служебная запись номер $i про выпуск версии и тесты", 'artifact'];
$n = 0;
foreach ($prot as [$c, $text, $sec]) {
    $n++;
    $q("INSERT INTO protocol (chat_id, entry_no, section, text, msg_seq, created_utc, updated_utc) VALUES (?, ?, ?, ?, 3, UTC_TIMESTAMP(), UTC_TIMESTAMP())", [$c, $n, $sec, $text]);
}

// ---- hints
$r = co_related($db, 'owner', 'Почему капча Cloudflare Turnstile не проходит в агентском браузере?', 'CHAT-A', false);
$ids = array_map(static fn($h) => $h['chatId'], $r['hits']);
$ok('related finds the captcha chat', in_array('CHAT-B', $ids, true));
$ok('related never shows another env\'s private chat', !in_array('CHAT-D', $ids, true));
$r2 = co_related($db, 'owner', 'Почему капча Cloudflare Turnstile не проходит в агентском браузере?', 'CHAT-B', false);
$ok('related skips the current chat', !in_array('CHAT-B', array_map(static fn($h) => $h['chatId'], $r2['hits']), true));
$ok('short text gives no hints', co_related($db, 'owner', 'да', 'CHAT-A', false)['hits'] === []);
$rs = co_related($db, 'sam', 'Турнир по шахматам у детей', 'CHAT-X', true);
$ok('shared project visible to sam', in_array('CHAT-C', array_map(static fn($h) => $h['chatId'], $rs['hits']), true));
$ok('sam does not see the owner\'s own chats', !array_intersect(['CHAT-A', 'CHAT-B'], array_map(static fn($h) => $h['chatId'], $rs['hits'])));

// ---- dossier rows
$me = co_dossier_add($db, 'owner', 'me', 'Работа', 'С 2026-10-27 не работает в Acme, ищет работу', ['chat' => 'CHAT-A', 'msg' => 3, 'date' => '2026-10-27'], 'secretary');
$p1 = co_dossier_add($db, 'owner', 'people', 'Анна Иванова', 'дочь, ищет работу в Бельгии', [], 'secretary');
$st = co_dossier_add($db, 'owner', 'state', 'Поиск работы', 'Отклики SOW Sanitair, Vigo; интервью Denys', ['date' => '2026-11-03'], 'secretary');
co_dossier_add($db, 'sam', 'me', '', 'Живёт в Генте', [], 'secretary');
$ok('section checked', (function () use ($db) { try { co_dossier_add($db, 'owner', 'bogus', 'x', 'text', [], 'user'); return false; } catch (InvalidArgumentException $e) { return true; } })());
$ok('people need a subject', (function () use ($db) { try { co_dossier_add($db, 'owner', 'people', '', 'someone', [], 'user'); return false; } catch (InvalidArgumentException $e) { return true; } })());

$ctx = co_dossier_context($db, 'owner', false);
$ok('context has all sections', str_contains($ctx, 'About the user') && str_contains($ctx, 'Current affairs') && str_contains($ctx, 'Анна Иванова'));
$ok('context has row ids and dates', str_contains($ctx, '[d' . $me . ']') && str_contains($ctx, '(2026-10-27)'));
$ok('context is per env', !str_contains($ctx, 'Генте'));
$pc = co_dossier_context($db, 'owner', true);
$ok('shared context has people only', str_contains($pc, 'Анна Иванова') && !str_contains($pc, 'Acme') && !str_contains($pc, 'Sanitair'));
$ok('shared detection', co_dossier_shared($db, 'P2', 'CHAT-C') && !co_dossier_shared($db, 'P1', 'CHAT-A'));
$ok('empty dossier gives empty block', co_dossier_context($db, 'alex', false) === '');
$small = co_dossier_context($db, 'owner', false, 60);
$ok('long context is cut with a note', str_contains($small, 'more rows'));
// C0J6: with a topic the rows on it come first in their section and survive a small budget
$me2 = co_dossier_add($db, 'owner', 'me', 'Пианино', 'Продаёт Kawai, выбирает цифровое', [], 'secretary');
$other = co_dossier_context($db, 'owner', false, 20000, 'что писать в Acme про работу');
$before = static fn(string $s, string $a, string $b): bool => ($i = strpos($s, $a)) !== false && ($j = strpos($s, $b)) !== false && $i < $j;
$ok('another topic, another order', $before($other, 'Acme', 'Kawai'));
$topical = co_dossier_context($db, 'owner', false, 20000, 'какое цифровое пианино взять вместо Kawai');
$ok('topic rows first', $before($topical, 'Kawai', 'Acme'));
co_dossier_add($db, 'owner', 'people', 'Борис Петров', 'сосед', [], 'secretary');
$named = co_dossier_context($db, 'owner', false, 20000, 'что ответить Борису Петрову');
$ok('named person first', $before($named, 'Борис Петров', 'Анна Иванова'));

$s = co_dossier_search($db, 'owner', 'Анна', 10);
$ok('search by a name', $s && $s[0]['id'] === $p1);

// ---- secretary ops
[$block, $known] = co_dossier_prompt_block($db, 'owner', 'Анна нашла работу? Поиск работы продолжается');
$ok('prompt block lists known rows', str_contains($block, 'd' . $p1) && isset($known[$st]));
$dates = [5 => '2026-11-07', 6 => '2026-11-07'];
$applied = co_dossier_apply($db, 'owner', 'CHAT-B', $dates, [
    ['op' => 'add', 'section' => 'people', 'subject' => 'Бориса Иванова', 'text' => 'жена', 'msg' => 5],
    ['op' => 'update', 'id' => 'd' . $p1, 'text' => 'дочь, с ноября 2026 работает в Генте', 'msg' => 6],
    ['op' => 'close', 'id' => 'd' . $st, 'msg' => 6],
    ['op' => 'add', 'section' => 'me', 'subject' => 'Gmail', 'text' => 'пароль от почты qwerty123', 'msg' => 5],
    ['op' => 'add', 'section' => 'me', 'subject' => 'x', 'text' => 'сообщение не из пачки', 'msg' => 99],
    ['op' => 'update', 'id' => 'd99999', 'text' => 'unknown id', 'msg' => 5],
    ['op' => 'close', 'id' => 'd' . $me, 'msg' => 5],
], 'haiku', $known);
$ok('three ops applied', $applied === 3);
$rows = co_dossier_list($db, 'owner', true);
$byId = [];
foreach ($rows as $row) $byId[$row['id']] = $row;
$ok('update supersedes', $byId[$p1]['status'] === 'replaced' && $byId[$byId[$p1]['replacedBy']]['text'] === 'дочь, с ноября 2026 работает в Генте');
$ok('new row carries the source', $byId[$byId[$p1]['replacedBy']]['chatId'] === 'CHAT-B' && $byId[$byId[$p1]['replacedBy']]['msg'] === 6 && $byId[$byId[$p1]['replacedBy']]['date'] === '2026-11-07');
$ok('close finishes an affair', $byId[$st]['status'] === 'closed');
$ok('no password row', !array_filter($rows, static fn($r) => str_contains($r['text'], 'qwerty')));
$ok('me row not closed (only state rows close)', $byId[$me]['status'] === 'active');
$ok('secret-like text detected', co_dossier_secretish('IBAN BE68 5390 0754 7034') && co_dossier_secretish('карта 4111 1111 1111 1111') && !co_dossier_secretish('родилась 12.03.2010, телефон не нужен') && !co_dossier_secretish('работала секретарём, secretary of the board'));

// ---- host entry point
$h = co_dossier_host($db, ['env' => 'owner', 'action' => 'import', 'rows' => [
    ['section' => 'people', 'subject' => 'Иван', 'text' => 'друг из Антверпена', 'chatId' => 'CGPT-1', 'date' => '2025-05-01'],
    ['section' => 'me', 'subject' => '', 'text' => 'token abc'],
    ['section' => 'zzz', 'subject' => 'a', 'text' => 'bad section'],
]]);
$ok('import adds and skips', $h['added'] === 1 && $h['skipped'] === 2);
$e = co_dossier_host($db, ['env' => 'owner', 'action' => 'edit', 'id' => 'd' . $me, 'text' => 'С 27.10.2026 без работы, ищет', 'author' => 'user', 'by' => 'owner@example.org']);
$ok('edit keeps the id', $e['id'] === $me && co_dossier_get($db, 'owner', $me)['text'] === 'С 27.10.2026 без работы, ищет');
$ok('delete', co_dossier_host($db, ['env' => 'owner', 'action' => 'delete', 'id' => $me])['ok'] === true && co_dossier_get($db, 'owner', $me)['status'] === 'deleted');
$ok('restore', co_dossier_host($db, ['env' => 'owner', 'action' => 'restore', 'id' => $me])['ok'] === true);
$cur = co_dossier_get($db, 'owner', $me);
$ok('edit with a stale revision refused', (function () use ($db, $me) { try { co_dossier_host($db, ['env' => 'owner', 'action' => 'edit', 'id' => $me, 'text' => 'old view', 'rev' => '2000-01-01 00:00:00.000']); return false; } catch (CoDossierStale $e) { return true; } })());
$ok('edit with the current revision works', co_dossier_host($db, ['env' => 'owner', 'action' => 'edit', 'id' => $me, 'text' => 'current view', 'rev' => $cur['updated_utc']])['id'] === $me);
$ok('supersede of a changed row is dropped', (function () use ($db, $me) { try { co_dossier_update($db, 'owner', $me, ['text' => 'secretary', 'rev' => '2000-01-01 00:00:00.000'], 'supersede', 'secretary'); return false; } catch (CoDossierStale $e) { return co_dossier_get($db, 'owner', $me)['status'] === 'active'; } })());
$ok('invalid supersede keeps the row', (function () use ($db, $me) { try { co_dossier_update($db, 'owner', $me, ['text' => ' '], 'supersede', 'secretary'); return false; } catch (InvalidArgumentException $e) { return co_dossier_get($db, 'owner', $me)['status'] === 'active'; } })());
$ok('add refuses a secret in the subject', (function () use ($db) { try { co_dossier_host($db, ['env' => 'owner', 'action' => 'add', 'section' => 'people', 'subject' => 'token sk-123', 'text' => 'кто-то']); return false; } catch (InvalidArgumentException $e) { return true; } })());
$ok('edit refuses a secret', (function () use ($db, $me) { try { $r = co_dossier_get($db, 'owner', $me); co_dossier_host($db, ['env' => 'owner', 'action' => 'edit', 'id' => $me, 'text' => 'пароль 1234', 'rev' => $r['updated_utc']]); return false; } catch (InvalidArgumentException $e) { return true; } })());
$ok('edit of another env refused', (function () use ($db, $me) { try { co_dossier_host($db, ['env' => 'sam', 'action' => 'edit', 'id' => $me, 'text' => 'x y z']); return false; } catch (InvalidArgumentException $e) { return true; } })());
$ok('bad env refused', (function () use ($db) { try { co_dossier_host($db, ['env' => '../x', 'action' => 'list']); return false; } catch (InvalidArgumentException $e) { return true; } })());
for ($i = 0; $i < 60; $i++) co_dossier_add($db, 'owner', 'people', "Знакомый $i", "коллега по проекту номер $i", [], 'import');
$rh = co_related($db, 'owner', 'Что нового у Бориса Иванова и жены, когда день рождения?', 'CHAT-A', false);
$ok('hints include dossier rows', (bool)array_filter($rh['dossier'], static fn($d) => $d['subject'] === 'Бориса Иванова'));
$ok('a shared surname alone is not a hint', !array_filter($rh['dossier'], static fn($d) => $d['subject'] === 'Анна Иванова'));
$cap = 'Почему капча Cloudflare Turnstile не проходит в агентском браузере?';
$ok('scope to the shared project hides other projects', co_related($db, 'owner', $cap, 'CHAT-C', true, 5, 'P2')['hits'] === []);
$ok('scope to the same project keeps its chats', in_array('CHAT-B', array_map(static fn($h) => $h['chatId'], co_related($db, 'owner', $cap, 'CHAT-A', false, 5, 'P1')['hits']), true));
$ok('chat share gets no protocol hints', co_related($db, 'owner', $cap, 'CHAT-C', true, 5, '')['hits'] === []);

echo $fail ? "\n$fail FAILED\n" : "\nall ok\n";
exit($fail ? 1 : 0);
