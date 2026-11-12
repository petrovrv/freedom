<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// "Suggest title" for a chat in CO Web 0.12.1 (src/search.ts suggestChatTitle). Claude Haiku through the server's CLI.
// Input: {messages: [{role: "user"|"assistant", text}]}, an excerpt the browser cut from the chat.
declare(strict_types=1);
require __DIR__ . '/lib.php';
require_once __DIR__ . '/members-lib.php';


co_require_api_call();
set_time_limit(80);
$session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
if (!isset(co_push_users()[co_agent_email($session)])) co_json(403, ['error' => 'not_allowed']);

$in = json_decode((string)file_get_contents('php://input'), true);
$parts = [];
$budget = 9000;
foreach (array_slice((array)($in['messages'] ?? []), 0, 12) as $m) {
    if (!is_array($m) || $budget <= 0) continue;
    $who = ($m['role'] ?? '') === 'assistant' ? 'Agent' : 'User';
    $text = mb_substr(trim((string)($m['text'] ?? '')), 0, min(2000, $budget));
    $budget -= mb_strlen($text);
    if ($text !== '') $parts[] = "$who: $text";
}
if (!$parts) co_json(400, ['error' => 'empty_chat']);

$prompt = "Here is an excerpt of a chat between a person and an AI agent.\n\n" . implode("\n\n---\n\n", $parts) . "\n\n"
    . "Name this chat: 2 to 6 words in the language of the person's messages, a noun phrase describing the topic "
    . "(no question, no quotes, no final period), specific enough to tell it apart from other chats on similar topics.\n"
    . "Answer with JSON only: {\"title\": \"...\"}";
try {
    $j = co_agent_json_from(co_agent_claude($prompt, 'haiku', 70));
    // Byte-wise trim() would cut UTF-8 sequences (« and » share bytes with Cyrillic letters).
    $title = preg_replace(['/\s+/u', '/^[\s"\'«»“”]+|[\s"\'«»“”.]+$/u'], [' ', ''], (string)($j['title'] ?? ''));
    if ($title === '') throw new RuntimeException('empty title');
    co_json(200, ['title' => mb_substr($title, 0, 80)]);
} catch (Throwable $e) {
    co_json(502, ['error' => 'model_failed', 'detail' => substr($e->getMessage(), 0, 300)]);
}
