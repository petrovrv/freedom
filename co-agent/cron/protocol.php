<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);

/*
 * Chat protocol (schema 8, 2026-10-17). Called from cron/watch-cron.php every 5 minutes.
 * The secretary — the same model as the chat's agent — reads new messages and keeps a protocol
 * (decisions, constraints, facts, open questions, artifacts, answer pointers) linked to the
 * source messages. Only shown in CO Web for now; agents do not read it yet.
 * Schema 9 (2026-10-18): the same call proposes project memory candidates (cron/memory-candidates.php).
 * Schema 16 (2026-11-01): and rules from a person's corrections of the agent (memory_candidates kind 'rule').
 * Schema 19 (2026-11-07): and the dossier of the person who writes (dossier-lib.php: me / people / state), applied
 * at once without review (decision of the owner 07.11); only batches with a message written by a person.
 * CO Web 0.129.0 (SPEC §3.6b): section "agreement" (who does what by when; protocol-apply.php keeps "who" and "due" in
 * the text) for the Events of the chat folder; new messages carry their date for the due dates.
 * Chats are taken up once they get a message after meta.protocol_since (first run: 24 h back); older chats are
 * not backfilled. A failing batch is retried every run, after 3 failures in a row only once an hour; the cursor
 * (chats.protocol_seq) never moves past an unprocessed batch.
 * GPT (Codex) chats are not kept yet: the server's Codex CLI runs without a sandbox and cannot be started without
 * command tools, so untrusted chat text could make it read server secrets. Claude runs with --tools ''.
 */

require_once __DIR__ . '/protocol-apply.php';
require_once __DIR__ . '/memory-candidates.php';
require_once __DIR__ . '/boot.php';
require_once CO_AGENT_WEB . '/dossier-lib.php';

function co_protocol_meta(mysqli $db, string $k): ?string
{
    $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
    $st->execute([$k]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    if (!$rows || $rows[0]['v'] === null) {
        return null;
    }
    return (string)$rows[0]['v'];
}

function co_protocol_meta_set(mysqli $db, string $k, string $v): void
{
    $st = $db->prepare('REPLACE INTO meta (k, v) VALUES (?, ?)');
    $st->execute([$k, $v]);
}

function co_protocol_meta_delete(mysqli $db, string $k): void
{
    $st = $db->prepare('DELETE FROM meta WHERE k = ?');
    $st->execute([$k]);
}

// meta.k is VARCHAR(40): the failure counter key uses a hash of the chat id.
function co_protocol_fail_key(string $chatId): string
{
    return 'pf_' . substr(sha1($chatId), 0, 30);
}

function co_protocol_cut(string $text, int $max): string
{
    if (mb_strlen($text) <= $max) {
        return $text;
    }
    return mb_substr($text, 0, $max) . '…[cut]';
}

function co_protocol_run(int $limit = 2, int $budgetSec = 200): array
{
    $t0 = time();
    $processed = [];
    // Two overlapping cron runs must not process the same chat twice.
    $lock = fopen(co_agent_home() . '/co-agent/state/protocol.lock', 'c');
    if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) {
        return [];
    }
    $db = co_db();

    $since = co_protocol_meta($db, 'protocol_since');
    if ($since === null) {
        $since = gmdate('Y-m-d H:i:s', time() - 86400);
        co_protocol_meta_set($db, 'protocol_since', $since);
    }

    $limit = max(1, $limit);
    $sql = "SELECT c.chat_id, c.title, c.project_id, c.protocol_seq, e.last
            FROM chats c
            JOIN (
                SELECT chat_id,
                       MAX(created_utc) AS last,
                       MAX(CASE WHEN type IN ('user_message','assistant_message') THEN created_utc END) AS last_msg,
                       MAX(CASE WHEN type = 'assistant_message' THEN seq END) AS max_a
                FROM events
                GROUP BY chat_id
            ) e ON e.chat_id = c.chat_id
            LEFT JOIN meta f ON f.k = CONCAT('pf_', LEFT(SHA1(c.chat_id), 30))
            WHERE e.max_a > COALESCE(c.protocol_seq, 0)
              AND NOT (f.v IS NOT NULL AND CAST(SUBSTRING_INDEX(f.v, '|', 1) AS UNSIGNED) >= 3
                       AND CAST(SUBSTRING_INDEX(f.v, '|', -1) AS UNSIGNED) > UNIX_TIMESTAMP() - 3600)
              AND e.last < UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE
              AND e.last_msg >= ?
            ORDER BY e.last DESC
            LIMIT " . ($limit + 10);
    $st = $db->prepare($sql);
    $st->execute([$since]);
    $candidates = $st->get_result()->fetch_all(MYSQLI_ASSOC);

    foreach ($candidates as $cand) {
        if (time() - $t0 >= $budgetSec || count($processed) >= $limit) {
            break;
        }

        $chatId = (string)$cand['chat_id'];
        $title = (string)($cand['title'] ?? '');
        $projectId = (string)($cand['project_id'] ?? '');
        $batchEnd = null;

        try {
            $db = co_db(true);
            // Back-off: after 3 failures in a row a chat is retried at most once an hour.
            $fail = explode('|', co_protocol_meta($db, co_protocol_fail_key($chatId)) ?? '0|0');
            if ((int)$fail[0] >= 3 && time() - (int)($fail[1] ?? 0) < 3600) {
                continue;
            }
            $from = (int)$cand['protocol_seq'];

            $st = $db->prepare("SELECT seq, type, text, data, created_utc, author, writer FROM events
                                WHERE chat_id = ? AND type IN ('user_message','assistant_message') AND seq > ? AND JSON_EXTRACT(data, '$.deleted') IS NULL
                                ORDER BY seq");
            $st->execute([$chatId, $from]);
            $msgs = $st->get_result()->fetch_all(MYSQLI_ASSOC);

            // Build batch within the size budget.
            $picked = [];
            $sum = 0;
            $hasAnswer = false;
            foreach ($msgs as $m) {
                $len = mb_strlen((string)($m['text'] ?? ''));
                // Over the budget only once the batch already holds an answer (long messages must not stall a chat).
                if ($hasAnswer && $sum + $len > 60000) {
                    break;
                }
                $hasAnswer = $hasAnswer || $m['type'] === 'assistant_message';
                $picked[] = $m;
                $sum += $len;
            }

            // Cut back to the last assistant message.
            $lastA = null;
            for ($i = count($picked) - 1; $i >= 0; $i--) {
                if ($picked[$i]['type'] === 'assistant_message') {
                    $lastA = $i;
                    break;
                }
            }
            if ($lastA === null) {
                continue;
            }
            $picked = array_slice($picked, 0, $lastA + 1);
            $lastMsg = $picked[$lastA];
            $batchEnd = (int)$lastMsg['seq'];

            $batch = [];
            $dates = [];
            foreach ($picked as $m) {
                $batch[(int)$m['seq']] = ['type' => (string)$m['type'], 'text' => (string)($m['text'] ?? ''),
                    'author' => (string)($m['author'] ?? ''), 'writer' => (string)($m['writer'] ?? '')];
                $dates[(int)$m['seq']] = substr((string)($m['created_utc'] ?? ''), 0, 10);
            }

            // Agent and model.
            $data = json_decode((string)($lastMsg['data'] ?? ''), true);
            if (!is_array($data)) {
                $data = [];
            }
            $agent = (isset($data['agent']) && is_string($data['agent']) && $data['agent'] !== '') ? $data['agent'] : 'claude';

            $skipRaw = co_protocol_meta($db, 'protocol_skip_agents') ?? '';
            $skip = array_filter(array_map('trim', explode(',', $skipRaw)), static fn($s) => $s !== '');
            if (in_array($agent, $skip, true)) {
                $st = $db->prepare('UPDATE chats SET protocol_seq = ?, protocol_utc = UTC_TIMESTAMP(3), protocol_error = ? WHERE chat_id = ?');
                $st->execute([$batchEnd, 'skipped: agent ' . $agent, $chatId]);
                $processed[] = $chatId;
                continue;
            }

            if ($agent === 'codex') {
                $st = $db->prepare('UPDATE chats SET protocol_seq = ?, protocol_utc = UTC_TIMESTAMP(3), protocol_model = NULL, protocol_error = ? WHERE chat_id = ?');
                $st->execute([$batchEnd, 'GPT chats are not kept yet (no tool-free Codex on the server)', $chatId]);
                $processed[] = $chatId;
                continue;
            } else {
                $mr = $data['modelReported'] ?? null;
                $md = $data['model'] ?? null;
                if (is_string($mr) && preg_match('/^claude-[a-z0-9-]+$/', $mr)) {
                    $model = $mr;
                } elseif (is_string($md) && preg_match('/^(claude-[a-z]+-\d+(-\d+)?|opus|sonnet|haiku)$/', $md)) {
                    $model = $md;
                } else {
                    $model = 'opus';
                }
            }

            // Existing active entries.
            $st = $db->prepare("SELECT entry_no, section, text, msg_seq FROM protocol
                                WHERE chat_id = ? AND status = 'active' ORDER BY entry_no");
            $st->execute([$chatId]);
            $entries = $st->get_result()->fetch_all(MYSQLI_ASSOC);
            $entryLines = [];
            foreach ($entries as $e) {
                $entryLines[] = '#' . $e['entry_no'] . ' ' . $e['section'] . ' (msg ' . $e['msg_seq'] . '): ' . $e['text'];
            }
            $entriesText = $entryLines ? implode("\n", $entryLines) : '(none yet)';

            // Previous context messages.
            $prevText = '(none)';
            if ($from > 0) {
                $st = $db->prepare("SELECT seq, type, text FROM events
                                    WHERE chat_id = ? AND type IN ('user_message','assistant_message') AND seq <= ? AND JSON_EXTRACT(data, '$.deleted') IS NULL
                                    ORDER BY seq DESC LIMIT 2");
                $st->execute([$chatId, $from]);
                $prev = array_reverse($st->get_result()->fetch_all(MYSQLI_ASSOC));
                $parts = [];
                foreach ($prev as $p) {
                    $role = $p['type'] === 'user_message' ? 'USER' : 'AGENT';
                    $parts[] = '[#' . $p['seq'] . ' ' . $role . "]\n" . co_protocol_cut((string)($p['text'] ?? ''), 1500);
                }
                if ($parts) {
                    $prevText = implode("\n\n", $parts);
                }
            }

            // New messages.
            $newParts = [];
            foreach ($batch as $seq => $m) {
                $isUser = $m['type'] === 'user_message';
                // the date (UTC) lets the secretary convert "tomorrow" or "by Friday" of an agreement into a due date
                $newParts[] = '[#' . $seq . ' ' . ($isUser ? 'USER' : 'AGENT') . (($dates[$seq] ?? '') !== '' ? ' ' . $dates[$seq] : '') . "]\n"
                    . co_protocol_cut($m['text'], $isUser ? 8000 : 12000);
            }
            $newText = implode("\n\n", $newParts);
            $seqList = implode(', ', array_keys($batch));

            // Schema 9: project memory and its pending candidates, so that the model does not repeat them.
            $memExisting = [];
            $memoryBlock = '';
            if ($projectId !== '') {
                [$memoryBlock, $memExisting] = co_memory_prompt_block($db, $projectId);
            }
            // Schema 16: rules from corrections, only for messages a person wrote (not agents, schedules, imports)
            $humanSeqs = $projectId === '' ? [] : co_rule_human_seqs($batch);
            $ruleExisting = [];
            $rulesBlock = '';
            if ($humanSeqs !== []) {
                [$rulesKnown, $ruleExisting] = co_rule_prompt_block($db, $projectId, $humanSeqs);
                $humanList = implode(', ', array_map(static fn(int $s): string => '#' . $s, array_keys($humanSeqs)));
                $rulesBlock = <<<RULES

AGENT RULES FROM CORRECTIONS.
Additionally, in a top-level "rules" array, propose 0-2 rules for future agents when the person CORRECTS the agent in one of the messages {$humanList}: says the agent did something wrong or unwanted, repeats an instruction the agent ignored, points out that the agent missed something ("I already sent it", "you did not check X"), asks for a different style ("shorter", "in Russian"), or forbids something ("do not use X").
Rules for each item:
- "text": one imperative line for any future agent, under 200 characters, in the language of the person's messages, self-contained and general enough to apply next time (a lasting lesson, not "fix this paragraph");
- "scope": "env" when it is about how agents should work for this person in any project (style and length of answers, checking sent mail before proposing to send, services or tools not to use, asking versus acting); "project" only when it concerns the subject of this project (its code, paths, release steps, data);
- "quote": the exact words of the correction, copied verbatim from that message (at most 200 characters);
- "msg": the number of that message.
Never propose a rule for a one-off request without a lasting lesson, for a mistake the person did not point out, or with secrets. No duplicates or paraphrases of the known rules below. Usually "rules":[].

{$rulesKnown}
RULES;
            }

            // Schema 19: the dossier of the person who wrote the human messages of the batch, by the signed-in account
            // that stored them (events.writer, set by the server), not the client's author field (review 07.11); a batch
            // with messages of several people, or with a message without a matching writer, is skipped
            // every user message of the batch counts, whatever its author field says (review 07.11)
            $pushUsers = co_push_users();
            $writers = [];
            foreach ($batch as $bm) {
                if ($bm['type'] === 'user_message') {
                    $writers[] = $pushUsers[strtolower(trim((string)($bm['writer'] ?? '')))] ?? '?';
                }
            }
            $writers = array_values(array_unique($writers));
            $dossierEnv = $projectId !== '' && count($writers) === 1 && $writers[0] !== '?' ? (string)$writers[0] : null;
            $dossierKnown = [];
            $dossierBlock = '';
            if ($dossierEnv !== null) {
                [$dossierRows, $dossierKnown] = co_dossier_prompt_block($db, $dossierEnv, $newText);
                $dossierBlock = <<<DOSSIERTXT

DOSSIER OF THE PERSON.
Additionally, in a top-level "dossier" array, keep the person's dossier: what any future agent should know about the person to help them well. Sections:
- "me": facts about the person: biography, family status, where they live, education, work and employment status, health, documents and permits, finances in general terms, important dates, lasting preferences and goals;
- "people": people around the person, one fact per item, "subject" = the person's full name as known (e.g. "Анна Иванова"); who they are to the person (relation, role), their important dates (birthday), work, where they live, notable facts;
- "state": current affairs of the person, "subject" = a short topic ("Job search", "Rent Koolstraat"): status, deadlines, next steps.
Health, psychology, legal matters and children are allowed. Never: passwords, tokens, keys, PINs, card or account numbers.
Ops: {"op":"add","section":"…","subject":"…","text":"…","msg":<seq>}; {"op":"update","id":"d<id>","text":"…","msg":<seq>} when a known row is outdated or wrong (the old one is kept as history); {"op":"close","id":"d<id>","msg":<seq>} when an affair in "state" is finished.
Rules: "text" one self-contained line under 300 characters in the language of the person's messages, with the date when it matters ("since 2026-10-27"); only facts stated or clearly established in the new messages (the person's words, documents and emails the agent reports), no guesses, no facts about the AI system itself, no duplicates of the known rows below. Usually 0-3 items, often "dossier":[].

Known dossier rows (d<id>):
{$dossierRows}
DOSSIERTXT;
            }

            $prompt = <<<PROMPT
You are the secretary of a chat between a person and an AI agent. Chat title: "{$title}".
Keep a protocol of this chat so that a later agent can work from it instead of the full chat, and open a source message by its number when details are needed.

Rules:
- Write entries in the language of the person's messages. One entry = one short line.
- Sections:
  - decision: choices of the person, including accepting or rejecting the agent's proposals.
  - constraint: requirements, prohibitions, preferences, exact numbers, deadlines set by the person.
  - fact: facts established: measurements, versions, paths, ids, results the agent reports, contents of emails/documents found.
  - question: open questions, unfinished work, agreed next steps.
  - artifact: files, commits, releases, links produced.
  - answer: the agent's explanation/opinion/overview. ONLY a pointer: the question and the headings of the answer's points in one line; the full text stays in the message.
  - agreement: a promise or arrangement about who does what, usually by a date ("I'll send it by Friday", "the agent checks again tomorrow", "Anna pays on the 5th"). Add "who" (the person, the agent or a named third party, as written) and, when a date is said or clearly meant, "due" as YYYY-MM-DD (a weekday or "tomorrow" counted from the message date). "text" is what is to be done. Close it when it is done or called off.
- decision, constraint, fact and agreement entries need "quote": an exact verbatim fragment (at most 300 characters) copied from that message. Other sections may omit it.
- Nothing for small talk, greetings, sign-in problems, "ok", tool noise. Do not duplicate existing active entries. Few, precise entries.
- If a message changes an existing active entry, use "supersede" (with the replacement). If an open question is resolved or the work is done, use "close".

Existing active protocol entries:
{$entriesText}

Previous messages (already processed, context only):
{$prevText}

New messages (numbers: {$seqList}):
{$newText}

PROJECT MEMORY CANDIDATES.
Additionally, propose 0-3 project memory candidates in a top-level "memory" array. The owner of the project will later accept or reject them.
Only propose:
- durable project-level decisions made by the person;
- facts that a future agent working on ANY chat of this project must know (architecture choices, policies, accepted conventions, verified constraints of the environment).
Never propose:
- chat-local tasks, progress or status, versions or details of a single release, questions, opinions, guesses;
- secrets of any kind (passwords, tokens, keys, private personal data).
Rules for each candidate:
- "tag" is "decision" or "fact";
- "text" is one line, under 200 characters, in the language of the person's messages, self-contained (no "this chat", "above", "as discussed"), without date or tag prefix;
- "msg" is the number of the new message the candidate is based on;
- no duplicates or paraphrases of the existing project memory or of the pending candidates listed below.
Usually the answer is an empty array: "memory":[].

{$memoryBlock}{$rulesBlock}{$dossierBlock}
Answer with JSON only:
{"ops":[{"op":"add","section":"…","text":"…","quote":"…","msg":<seq>},{"op":"add","section":"agreement","who":"…","text":"…","due":"YYYY-MM-DD","quote":"…","msg":<seq>},{"op":"supersede","no":<entry>,"section":"…","text":"…","quote":"…","msg":<seq>},{"op":"close","no":<entry>,"msg":<seq>}],"memory":[{"tag":"decision","text":"…","msg":<seq>}],"rules":[{"scope":"env","text":"…","quote":"…","msg":<seq>}],"dossier":[{"op":"add","section":"people","subject":"…","text":"…","msg":<seq>}]}
"msg" must be one of the new message numbers. Answer {"ops":[],"memory":[],"rules":[],"dossier":[]} if there is nothing new.
PROMPT;
            if ($projectId === '') {
                $prompt = str_replace("PROJECT MEMORY CANDIDATES.", "PROJECT MEMORY CANDIDATES (this chat has no project: always \"memory\":[]).", $prompt);
            }

            $answer = co_agent_claude($prompt, $model, 240);

            $db = co_db(true);
            $j = co_agent_json_from($answer);
            if (!isset($j['ops']) || !is_array($j['ops'])) {
                throw new RuntimeException('model answer has no ops array');
            }
            $ops = $j['ops'];
            $memOps = $j['memory'] ?? [];
            $ruleOps = $j['rules'] ?? [];
            $dossierOps = $dossierEnv !== null ? ($j['dossier'] ?? []) : [];
            co_protocol_apply($db, $chatId, $batch, $ops, $model, $batchEnd,
                ($projectId === '' && $dossierEnv === null) ? null : static function (mysqli $db) use ($projectId, $chatId, $batch, $dates, $memOps, $ruleOps, $humanSeqs, $model, &$memExisting, &$ruleExisting, $dossierEnv, $dossierOps, $dossierKnown): void {
                    if ($projectId !== '') {
                        co_memory_insert($db, $projectId, $chatId, $batch, $dates, $memOps, $model, $memExisting);
                        co_rule_insert($db, $projectId, $chatId, $batch, $dates, $humanSeqs, $ruleOps, $model, $ruleExisting);
                    }
                    if ($dossierEnv !== null) {
                        co_dossier_apply($db, $dossierEnv, $chatId, $dates, $dossierOps, $model, $dossierKnown);
                    }
                });
            co_protocol_meta_delete($db, co_protocol_fail_key($chatId));

            $processed[] = $chatId;
        } catch (Throwable $ex) {
            $msg = $ex->getMessage();
            try {
                $db = co_db(true);
                $failKey = co_protocol_fail_key($chatId);
                $fails = (int)explode('|', co_protocol_meta($db, $failKey) ?? '0')[0] + 1;
                co_protocol_meta_set($db, $failKey, $fails . '|' . time());

                $error = gmdate('H:i') . ' ' . mb_substr($msg, 0, 440);
                if ($fails >= 3) {
                    $error .= ' (failed ' . $fails . 'x, retry hourly)';
                }
                $st = $db->prepare('UPDATE chats SET protocol_error = ? WHERE chat_id = ?');
                $st->execute([$error, $chatId]);
            } catch (Throwable $ex2) {
                fwrite(STDERR, 'protocol ' . $chatId . ': ' . $ex2->getMessage() . "\n");
            }
            fwrite(STDERR, 'protocol ' . $chatId . ': ' . $msg . "\n");
        }
    }

    return $processed;
}
