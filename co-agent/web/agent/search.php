<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// AI search over chats and projects for CO Web 0.12.0 (src/search.ts). Claude Haiku through the server's CLI.
// step=expand: request -> search terms for Drive full-text search (synonyms, translations).
// step=rank:   request + chat and project titles with their full-text matches -> relevant ones, best first.
declare(strict_types=1);
require __DIR__ . '/lib.php';
require_once __DIR__ . '/members-lib.php';

const CO_SEARCH_MODEL = 'haiku';

co_require_api_call();
set_time_limit(100);
$session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
if (!isset(co_push_users()[co_agent_email($session)])) co_json(403, ['error' => 'not_allowed']);

$in = json_decode((string)file_get_contents('php://input'), true);
if (!is_array($in)) co_json(400, ['error' => 'bad_request']);
$query = trim((string)($in['query'] ?? ''));
if ($query === '' || mb_strlen($query) > 500) co_json(400, ['error' => 'bad_query']);
$clean = fn($s, int $n) => mb_substr(str_replace(["\r", "\n", '|'], ' ', (string)$s), 0, $n);

try {
    if (($in['step'] ?? '') === 'expand') {
        $prompt = "You help to search a personal archive of chats with AI agents. Chats are in Russian, English, French and Dutch.\n"
            . "Request: " . $clean($query, 500) . "\n\n"
            . "Give 4 to 10 short search terms (one word, or a name of two words) that are likely to appear literally in the "
            . "messages of the chats the person is looking for: key words of the request, their synonyms, translations into "
            . "the other languages, specific technical terms and names it implies. Names of places, companies and people written in Cyrillic "
            . "also go in their original Latin spelling (Шерталь -> Chertal). Prefer distinctive words over common ones "
            . "like 'site' or 'file'.\n"
            . "Answer with JSON only: {\"terms\": [\"...\"]}";
        $j = co_agent_json_from(co_agent_claude($prompt, CO_SEARCH_MODEL, 60));
        $terms = array_values(array_filter(array_map(fn($t) => $clean($t, 40), (array)($j['terms'] ?? [])), fn($t) => trim($t) !== ''));
        co_json(200, ['terms' => array_slice($terms, 0, 10)]);
    }
    if (($in['step'] ?? '') === 'rank') {
        $lines = [];
        foreach (array_slice((array)($in['projects'] ?? []), 0, 200) as $p) {
            $lines[] = $clean($p['id'] ?? '', 8) . ' | ' . $clean($p['title'] ?? '', 120) . ' | ' . $clean($p['description'] ?? '', 200);
        }
        $projects = implode("\n", $lines);
        $lines = [];
        foreach (array_slice((array)($in['chats'] ?? []), 0, 300) as $c) {
            $m = array_map(fn($t) => $clean($t, 40), array_slice((array)($c['matches'] ?? []), 0, 10));
            $lines[] = $clean($c['id'] ?? '', 8) . ' | ' . $clean($c['project'] ?? '', 80) . ' | ' . $clean($c['title'] ?? '', 160)
                . ' | ' . $clean($c['date'] ?? '', 10) . ' | ' . ($m ? implode(', ', $m) : '-');
        }
        $chats = implode("\n", $lines);
        $prompt = "You help to search a personal archive of chats with AI agents. Today is " . gmdate('Y-m-d') . ".\n"
            . "Request: " . $clean($query, 500) . "\n\n"
            . "Projects (id | title | description):\n$projects\n\n"
            . "Chats (id | project | title | last activity | search terms found in the chat's messages):\n$chats\n\n"
            . "Choose the chats and projects the person is most likely looking for. Judge by meaning: titles, projects and the "
            . "terms found in the messages; a chat whose messages contain several terms is a strong candidate. If the request "
            . "mentions time (\"yesterday\", \"last week\"), use the dates. At most 15 chats and 3 projects, best first; "
            . "leave out weak matches. For each give a reason of at most 12 words in the language of the request.\n"
            . "Answer with JSON only: {\"projects\": [{\"id\": \"P0\", \"why\": \"...\"}], \"results\": [{\"id\": \"C0\", \"why\": \"...\"}]}";
        $j = co_agent_json_from(co_agent_claude($prompt, CO_SEARCH_MODEL, 80));
        $pick = fn($list) => array_values(array_map(
            fn($r) => ['id' => $clean($r['id'] ?? '', 8), 'why' => $clean($r['why'] ?? '', 200)],
            array_filter((array)$list, 'is_array')));
        co_json(200, ['projects' => $pick($j['projects'] ?? []), 'results' => $pick($j['results'] ?? [])]);
    }
    co_json(400, ['error' => 'bad_step']);
} catch (Throwable $e) {
    co_json(502, ['error' => 'model_failed', 'detail' => substr($e->getMessage(), 0, 300)]);
}
