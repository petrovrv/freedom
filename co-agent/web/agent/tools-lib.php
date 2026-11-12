<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
/*
 * Tool library learning (schema 16, 2026-11-01). cron/tool-learn.php counts calls of library tools in job_events
 * (tool_usage) and proposes changes at night (tool_candidates: new | skill | archive). The owner decides in CO Web
 * ("Waiting for you"; db.php tool_candidates / tool_decide). Accepting never writes the library from the server: it
 * returns the text of an agent task, which CO Web sends in a new chat of the project tool_learn_project (default
 * AI-Workbench); the agent changes the library through toolbox.py.
 */
declare(strict_types=1);

const CO_TOOLS_KINDS = ['new', 'skill', 'archive'];

/** Tools of the library copy on the server: name => [description, version, firstDay]. */
function co_tools_index(): array
{
    $out = [];
    foreach (glob(co_agent_home() . '/co-agent/tools/*/SKILL.md') ?: [] as $f) {
        $name = basename(dirname($f));
        if ($name === '' || $name[0] === '_' || !preg_match('/^[a-z0-9][a-z0-9-]{0,59}$/', $name)) {
            continue;
        }
        $text = (string)@file_get_contents($f);
        $desc = preg_match('/^description:\s*(.+)$/m', $text, $m) ? trim($m[1]) : '';
        $ver = preg_match('/^version:\s*(\S+)/m', $text, $m) ? $m[1] : '';
        // the oldest date in SKILL.md (history lines "- x.y.z — YYYY-MM-DD — ...") = when the tool was made
        preg_match_all('/\b(20\d\d-\d\d-\d\d)\b/', $text, $mm);
        $days = $mm[1] ?? [];
        sort($days);
        $out[$name] = ['description' => $desc, 'version' => $ver, 'firstDay' => $days[0] ?? null];
    }
    ksort($out);
    return $out;
}

function co_tools_project(mysqli $db): string
{
    $st = $db->prepare("SELECT v FROM meta WHERE k = 'tool_learn_project'");
    $st->execute();
    $v = $st->get_result()->fetch_row()[0] ?? null;
    return is_string($v) && $v !== '' ? $v : 'AI-Workbench';
}

/** Pending proposals and the usage of every tool (calls/errors in 30 days, last use) for CO Web. */
function co_tools_candidates(mysqli $db): array
{
    $rows = $db->query("SELECT id, kind, tool, title, text, evidence, model, created_utc, offered_utc FROM tool_candidates
                         WHERE status = 'pending' ORDER BY id DESC LIMIT 100")->fetch_all(MYSQLI_ASSOC);
    $cands = [];
    foreach ($rows as $r) {
        $ev = json_decode((string)($r['evidence'] ?? ''), true);
        $cands[] = [
            'id'         => (int)$r['id'],
            'kind'       => (string)$r['kind'],
            'tool'       => (string)$r['tool'],
            'title'      => (string)$r['title'],
            'text'       => (string)$r['text'],
            'evidence'   => is_array($ev) ? array_values(array_filter($ev, 'is_array')) : [],
            'model'      => $r['model'] === null ? null : (string)$r['model'],
            'createdUtc' => co_db_iso($r['created_utc']),
            'offeredUtc' => co_db_iso($r['offered_utc']),
        ];
    }
    $usage = [];
    $res = $db->query("SELECT tool, MAX(day) AS last_day,
                              SUM(IF(day > UTC_DATE() - INTERVAL 30 DAY, calls, 0)) AS calls30,
                              SUM(IF(day > UTC_DATE() - INTERVAL 30 DAY, errors, 0)) AS errors30,
                              COUNT(DISTINCT IF(day > UTC_DATE() - INTERVAL 30 DAY, job_id, NULL)) AS jobs30
                         FROM tool_usage GROUP BY tool");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $u) {
        $usage[(string)$u['tool']] = [
            'lastDay' => (string)$u['last_day'],
            'calls30' => (int)$u['calls30'],
            'errors30' => (int)$u['errors30'],
            'jobs30' => (int)$u['jobs30'],
        ];
    }
    $st = $db->prepare("SELECT v FROM meta WHERE k = 'tool_usage_since'");
    $st->execute();
    $since = $st->get_result()->fetch_row()[0] ?? null;
    return ['candidates' => $cands, 'usage' => (object)$usage, 'usageSince' => $since, 'projectId' => co_tools_project($db)];
}

/** Agent task for an accepted proposal (Russian: the owner's language; the agent answers in it). */
function co_tools_task_text(array $c): string
{
    $tool = (string)$c['tool'];
    $lines = [];
    foreach (is_array($c['evidence'] ?? null) ? $c['evidence'] : [] as $e) {
        if (!is_array($e)) {
            continue;
        }
        $s = '- ' . (string)($e['jobId'] ?? '');
        if (($e['chatId'] ?? '') !== '') {
            $s .= ', чат ' . $e['chatId'];
        }
        if (($e['note'] ?? '') !== '') {
            $s .= ': ' . $e['note'];
        }
        $lines[] = $s;
    }
    $evidence = $lines === [] ? '' : "\n\nОснования (задания; ход задания — CO Web, текст чата — aiwb-chat):\n" . implode("\n", array_slice($lines, 0, 12));
    $head = 'Библиотека инструментов: владелец принял предложение ночного прохода #' . (int)$c['id'] . ' (' . $c['kind'] . ', `' . $tool . '`).';
    $body = "\n\n**" . $c['title'] . "**\n\n" . $c['text'] . $evidence . "\n\n";
    $rules = 'Правила: библиотеку менять только через toolbox.py в C:\\AIWorkbench\\tools (не в зеркале G:); bump version, строка в «История» SKILL.md, `toolbox.py check`, `toolbox.py commit <имя> -m "<имя> <версия>: <что>"`. '
        . 'Если по проверке предложение неверно или уже сделано — ничего не меняй и коротко объясни. В конце — коротко: что изменено, версия, коммит.';
    $what = match ((string)$c['kind']) {
        'new' => 'Сделай: сначала `toolbox.py search <слова>` — если подходящий инструмент есть, расширь его (обратно совместимо); иначе `toolbox.py new <имя>` и перенеси туда повторяющийся код из заданий-оснований, заполни SKILL.md.',
        'skill' => 'Сделай: прочитай SKILL.md и код `' . $tool . '`, по заданиям-основаниям найди, где агент ошибся или запутался, и исправь SKILL.md (и код, если причина в нём).',
        'archive' => 'Сделай: перед архивом проверь, что `' . $tool . '` не используется вне заданий агентов — задачи планировщика Windows (Get-ScheduledTask, действия со ссылкой на tools\\' . $tool . '), schedules.json проектов, другие инструменты и скрипты (поиск имени в C:\\AIWorkbench\\tools и C:\\AIWorkbench\\source). Если используется — не архивируй и напиши где. Иначе `toolbox.py archive ' . $tool . ' -m "<причина>"`.',
        default => '',
    };
    return $head . $body . $what . "\n" . $rules;
}

/**
 * Decide on a pending proposal. accept: returns the agent task and the project for the new chat, the proposal stays
 * pending (a lost answer or a closed page loses nothing, Codex review); started: CO Web created the chat with the task
 * (chatId), the proposal becomes accepted; reject: rejected.
 */
function co_tools_decide(mysqli $db, int $id, string $action, string $email, string $chatId = ''): array
{
    if (!in_array($action, ['accept', 'started', 'reject'], true)) {
        throw new InvalidArgumentException('bad_action');
    }
    $st = $db->prepare("SELECT id, kind, tool, title, text, evidence, status FROM tool_candidates WHERE id = ?");
    $st->execute([$id]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row) {
        return ['ok' => false, 'status' => null];
    }
    if ($row['status'] !== 'pending') {
        return ['ok' => false, 'status' => (string)$row['status']];
    }
    if ($action === 'accept') {
        $st = $db->prepare('UPDATE tool_candidates SET offered_utc = UTC_TIMESTAMP(3) WHERE id = ?');
        $st->execute([$id]);
        $st->close();
        $row['evidence'] = json_decode((string)($row['evidence'] ?? ''), true);
        return ['ok' => true, 'status' => 'pending', 'task' => co_tools_task_text($row), 'projectId' => co_tools_project($db)];
    }
    if ($action === 'started' && !preg_match('/^CHAT-[A-Za-z0-9-]{1,60}$/', $chatId)) {
        throw new InvalidArgumentException('bad_chat');
    }
    $status = $action === 'started' ? 'accepted' : 'rejected';
    $st = $db->prepare("UPDATE tool_candidates SET status = ?, decided_utc = UTC_TIMESTAMP(3), decided_by = ?, chat_id = ?
                         WHERE id = ? AND status = 'pending'");
    $st->execute([$status, $email, $action === 'started' ? $chatId : null, $id]);
    $ok = $st->affected_rows === 1;
    $st->close();
    if (!$ok) {
        $st = $db->prepare('SELECT status FROM tool_candidates WHERE id = ?');
        $st->execute([$id]);
        return ['ok' => false, 'status' => (string)($st->get_result()->fetch_row()[0] ?? '')];
    }
    return ['ok' => true, 'status' => $status];
}
