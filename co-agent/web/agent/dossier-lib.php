<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);

/*
 * Schema 19 (2026-11-07): the dossier of a person (sections me / people / state) and the secretary hints.
 * - co_related(): for every message CO Web asks for protocol entries of OTHER chats (and dossier rows) that match its
 *   text (MariaDB full-text, no model); CO Web adds them to the job as a hidden "secretary hints" block.
 * - co_dossier_*: rows written by the protocol secretary (auto-accepted), the person (CO Web) and agents (aiwb-dossier,
 *   host key). co_dossier_context() is the block new agent sessions get; shared projects/chats get only people.
 */

const CO_DOSSIER_SECTIONS = ['me', 'people', 'state'];
const CO_DOSSIER_STATUSES = ['active', 'replaced', 'closed', 'deleted'];
const CO_DOSSIER_MAX_TEXT = 500;
const CO_DOSSIER_MAX_SUBJECT = 120;
const CO_DOSSIER_SECRETARY_MAX = 8; // rows a single secretary call may add or change
const CO_DOSSIER_CONTEXT_MAX = 20000; // characters of the session block (~7k tokens), shared by sections
const CO_RELATED_MAX_QUERY = 1500;

function co_dossier_q(mysqli $db, string $sql, array $p = []): array
{
    $st = $db->prepare($sql);
    $st->execute($p);
    $r = $st->get_result();
    $rows = $r ? $r->fetch_all(MYSQLI_ASSOC) : [];
    $st->close();
    return $rows;
}

function co_dossier_clean(mixed $s, int $max): string
{
    if (!is_string($s)) {
        return '';
    }
    $s = trim((string)preg_replace('/\s+/u', ' ', $s));
    return mb_strlen($s, 'UTF-8') > $max ? rtrim(mb_substr($s, 0, $max - 1, 'UTF-8')) . '…' : $s;
}

function co_dossier_row(array $r): array
{
    return [
        'id' => (int)$r['id'],
        'section' => (string)$r['section'],
        'subject' => (string)$r['subject'],
        'text' => (string)$r['text'],
        'status' => (string)$r['status'],
        'replacedBy' => $r['replaced_by'] === null ? null : (int)$r['replaced_by'],
        'chatId' => $r['source_chat'] === null ? null : (string)$r['source_chat'],
        'msg' => $r['source_msg'] === null ? null : (int)$r['source_msg'],
        'date' => $r['source_date'] === null ? null : (string)$r['source_date'],
        'author' => (string)$r['author'],
        'updatedUtc' => (string)$r['updated_utc'],
    ] + (isset($r['score']) ? ['score' => round((float)$r['score'], 2)] : []);
}

/** Active rows of an env (all statuses with $all), people only with $peopleOnly. */
function co_dossier_list(mysqli $db, string $env, bool $all = false, bool $peopleOnly = false): array
{
    $sql = 'SELECT * FROM dossier WHERE env = ?' . ($all ? '' : " AND status = 'active'")
        . ($peopleOnly ? " AND section = 'people'" : '')
        . " ORDER BY FIELD(section, 'me', 'state', 'people'), subject, id";
    return array_map('co_dossier_row', co_dossier_q($db, $sql, [$env]));
}

/** Full-text search over active rows (subject and text), best first. */
function co_dossier_search(mysqli $db, string $env, string $q, int $limit = 20, bool $peopleOnly = false): array
{
    $q = co_dossier_clean($q, CO_RELATED_MAX_QUERY);
    if (mb_strlen($q, 'UTF-8') < 2) {
        return [];
    }
    $ftq = co_fts_query($q);
    $rows = $ftq === '' ? [] : co_dossier_q($db,
        "SELECT d.*, MATCH(d.subject, d.text) AGAINST(? IN BOOLEAN MODE) AS score FROM dossier d
          WHERE d.env = ? AND d.status = 'active'" . ($peopleOnly ? " AND d.section = 'people'" : '') . "
            AND MATCH(d.subject, d.text) AGAINST(? IN BOOLEAN MODE)
          ORDER BY score DESC LIMIT " . max(1, min(100, $limit)),
        [$ftq, $env, $ftq]);
    // short names are below the full-text token size: a plain substring match on the subject as well
    $seen = array_fill_keys(array_map(static fn($r) => (int)$r['id'], $rows), true);
    if (mb_strlen($q, 'UTF-8') <= 40) {
        foreach (co_dossier_q($db, "SELECT d.*, 0 AS score FROM dossier d WHERE d.env = ? AND d.status = 'active'"
            . ($peopleOnly ? " AND d.section = 'people'" : '') . " AND (d.subject LIKE ? OR d.text LIKE ?) LIMIT 50",
            [$env, '%' . $q . '%', '%' . $q . '%']) as $r) {
            if (!isset($seen[(int)$r['id']])) {
                $rows[] = $r;
            }
        }
    }
    return array_map('co_dossier_row', array_slice($rows, 0, max(1, min(100, $limit))));
}

/**
 * Adds a row. Returns the new id. Throws InvalidArgumentException on bad input.
 * $src: ['chat' => ?string, 'msg' => ?int, 'date' => ?string 'Y-m-d']
 */
function co_dossier_add(mysqli $db, string $env, string $section, mixed $subject, mixed $text, array $src, string $author, ?string $model = null, ?string $by = null): int
{
    $section = strtolower(trim($section));
    if (!in_array($section, CO_DOSSIER_SECTIONS, true)) {
        throw new InvalidArgumentException('section must be me, people or state');
    }
    $text = co_dossier_clean($text, CO_DOSSIER_MAX_TEXT);
    $subject = co_dossier_clean($subject, CO_DOSSIER_MAX_SUBJECT);
    if (mb_strlen($text, 'UTF-8') < 3) {
        throw new InvalidArgumentException('text is empty');
    }
    if ($section !== 'me' && $subject === '') {
        throw new InvalidArgumentException('subject (person or topic) is required');
    }
    if (co_dossier_secretish($text) || co_dossier_secretish($subject)) {
        throw new InvalidArgumentException('passwords, tokens, keys, card and account numbers are not stored in the dossier');
    }
    $date = isset($src['date']) && is_string($src['date']) && preg_match('/^\d{4}-\d\d-\d\d$/', $src['date']) ? $src['date'] : null;
    $chat = isset($src['chat']) && is_string($src['chat']) && $src['chat'] !== '' ? mb_substr($src['chat'], 0, 80) : null;
    $msg = isset($src['msg']) && is_int($src['msg']) ? $src['msg'] : null;
    $st = $db->prepare('INSERT INTO dossier (env, section, subject, text, source_chat, source_msg, source_date, author, model,
                          created_utc, updated_utc, updated_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), ?)');
    $st->execute([$env, $section, $subject, $text, $chat, $msg, $date, mb_substr($author, 0, 40), $model === null ? null : mb_substr($model, 0, 60), $by]);
    $id = (int)$db->insert_id;
    $st->close();
    return $id;
}

function co_dossier_get(mysqli $db, string $env, int $id): ?array
{
    $rows = co_dossier_q($db, 'SELECT * FROM dossier WHERE id = ? AND env = ?', [$id, $env]);
    return $rows ? $rows[0] : null;
}

/**
 * Changes a row. $mode 'edit' (the person or an agent fixing it: in place) or 'supersede' (the secretary: a new row,
 * the old one gets status replaced). Returns the id of the current row.
 * Optimistic: the change applies only while the row is still active and unchanged since it was read ($f['rev'] = the
 * updatedUtc the caller saw, else the one read here), so a user's edit and a secretary's supersede never overwrite
 * each other (review 07.11); a stale change throws CoDossierStale.
 */
function co_dossier_update(mysqli $db, string $env, int $id, array $f, string $mode, string $author, ?string $model = null, ?string $by = null): int
{
    $old = co_dossier_get($db, $env, $id);
    if ($old === null || $old['status'] !== 'active') {
        throw new InvalidArgumentException('no active dossier row ' . $id);
    }
    $section = isset($f['section']) && is_string($f['section']) && $f['section'] !== '' ? $f['section'] : (string)$old['section'];
    $subject = array_key_exists('subject', $f) && is_string($f['subject']) ? $f['subject'] : (string)$old['subject'];
    $text = isset($f['text']) && is_string($f['text']) ? $f['text'] : (string)$old['text'];
    $rev = isset($f['rev']) && is_string($f['rev']) && $f['rev'] !== '' ? $f['rev'] : (string)$old['updated_utc'];
    // validate before anything changes: an invalid replacement must not leave the old row replaced (review 07.11)
    $section = strtolower(trim($section));
    if (!in_array($section, CO_DOSSIER_SECTIONS, true)) {
        throw new InvalidArgumentException('section must be me, people or state');
    }
    $text = co_dossier_clean($text, CO_DOSSIER_MAX_TEXT);
    $subject = co_dossier_clean($subject, CO_DOSSIER_MAX_SUBJECT);
    if (mb_strlen($text, 'UTF-8') < 3 || ($section !== 'me' && $subject === '')) {
        throw new InvalidArgumentException('text and subject are required');
    }
    if (co_dossier_secretish($text) || co_dossier_secretish($subject)) {
        throw new InvalidArgumentException('passwords, tokens, keys, card and account numbers are not stored in the dossier');
    }
    if ($mode === 'supersede') {
        // claim the old row first: a concurrent edit or supersede makes this a no-op and the change is dropped
        $st = $db->prepare("UPDATE dossier SET status = 'replaced', updated_utc = UTC_TIMESTAMP(3) WHERE id = ? AND env = ? AND status = 'active' AND updated_utc = ?");
        $st->execute([$id, $env, $rev]);
        $claimed = $st->affected_rows;
        $st->close();
        if ($claimed !== 1) {
            throw new CoDossierStale('dossier row ' . $id . ' changed meanwhile');
        }
        $new = co_dossier_add($db, $env, $section, $subject, $text, $f['src'] ?? [], $author, $model, $by);
        $st = $db->prepare('UPDATE dossier SET replaced_by = ? WHERE id = ?');
        $st->execute([$new, $id]);
        $st->close();
        return $new;
    }
    $st = $db->prepare("UPDATE dossier SET section = ?, subject = ?, text = ?, author = ?, updated_utc = UTC_TIMESTAMP(3), updated_by = ?
                          WHERE id = ? AND env = ? AND status = 'active' AND updated_utc = ?");
    $st->execute([$section, $subject, $text, mb_substr($author, 0, 40), $by, $id, $env, $rev]);
    $n = $st->affected_rows;
    $st->close();
    if ($n !== 1) {
        throw new CoDossierStale('dossier row ' . $id . ' changed meanwhile: reload and edit again');
    }
    return $id;
}

/** A row changed between reading and writing it (stale revision). */
final class CoDossierStale extends InvalidArgumentException
{
}

/** status closed (a finished affair) or deleted (wrong row). */
function co_dossier_set_status(mysqli $db, string $env, int $id, string $status, ?string $by = null, ?string $rev = null): bool
{
    if (!in_array($status, ['closed', 'deleted', 'active'], true)) {
        throw new InvalidArgumentException('bad status');
    }
    // $rev: only while the row is unchanged since it was read (the secretary closing an affair the person just edited)
    $st = $db->prepare('UPDATE dossier SET status = ?, updated_utc = UTC_TIMESTAMP(3), updated_by = ? WHERE id = ? AND env = ?'
        . ($rev !== null ? ' AND updated_utc = ?' : ''));
    $st->execute($rev !== null ? [$status, $by, $id, $env, $rev] : [$status, $by, $id, $env]);
    $n = $st->affected_rows;
    $st->close();
    return $n > 0;
}

/** Is the project (or the chat) shared with anybody: then sessions get only the people section. */
function co_dossier_shared(mysqli $db, string $projectId, string $chatId): bool
{
    $rows = co_dossier_q($db, "SELECT 1 FROM shares WHERE (kind = 'project' AND target_id = ?) OR (kind = 'chat' AND target_id = ?) LIMIT 1",
        [$projectId, $chatId]);
    return $rows !== [];
}

/**
 * The block for a new agent session; '' without rows. Each section has its share of $max (about me 40 %, current
 * affairs 35 %, people 25 %; an unused share passes on; shared chats: people only), lines are cut to 170 characters
 * (full rows: aiwb-dossier search, secretary hints), and a section that does not fit says how many rows it left out.
 */
function co_dossier_context(mysqli $db, string $env, bool $peopleOnly, int $max = CO_DOSSIER_CONTEXT_MAX): string
{
    $rows = co_dossier_list($db, $env, false, $peopleOnly);
    if ($rows === []) {
        return '';
    }
    $line = static function (array $r): string {
        $d = $r['date'] ? ' (' . $r['date'] . ')' : '';
        return '- ' . ($r['subject'] !== '' ? $r['subject'] . ': ' : '') . co_dossier_clean($r['text'], 170) . $d . ' [d' . $r['id'] . ']';
    };
    $me = array_values(array_filter($rows, static fn($r) => $r['section'] === 'me'));
    $state = array_values(array_filter($rows, static fn($r) => $r['section'] === 'state'));
    // newest affairs first
    usort($state, static fn($a, $b) => strcmp((string)$b['date'], (string)$a['date']));
    $by = [];
    foreach (array_filter($rows, static fn($r) => $r['section'] === 'people') as $r) {
        $by[$r['subject']][] = $r;
    }
    // people with more rows (the closest) first, one line each: the first fact; the rest come as hints or by search
    uksort($by, static fn($a, $b) => [count($by[$b]), $a] <=> [count($by[$a]), $b]);
    $peopleLines = [];
    foreach ($by as $name => $list) {
        $more = count($list) > 1 ? ' (+' . (count($list) - 1) . ')' : '';
        $peopleLines[] = '- ' . $name . ': ' . co_dossier_clean($list[0]['text'], 150) . ' [d' . $list[0]['id'] . ']' . $more;
    }
    $sections = $peopleOnly
        ? [['People around the user (one line each)', $peopleLines, 1.0]]
        : [
            ['About the user', array_map($line, $me), 0.40],
            ['Current affairs', array_map($line, $state), 0.35],
            ['People around the user (one line each)', $peopleLines, 0.25],
        ];
    $parts = [];
    $carry = 0;
    foreach ($sections as [$title, $lines, $share]) {
        if (!$lines) {
            $carry += (int)($max * $share);
            continue;
        }
        $budget = (int)($max * $share) + $carry;
        $kept = [];
        $used = 0;
        foreach ($lines as $l) {
            $len = mb_strlen($l, 'UTF-8') + 1;
            if ($used + $len > $budget) {
                break;
            }
            $kept[] = $l;
            $used += $len;
        }
        $carry = max(0, $budget - $used);
        $left = count($lines) - count($kept);
        $parts[] = $title . ":\n" . implode("\n", $kept) . ($left ? "\n[… {$left} more rows: search the dossier]" : '');
    }
    $head = $peopleOnly
        ? "[Dossier of {$env}: people only (this project or chat is shared)]"
        : "[Dossier of {$env}: facts about the user kept by the protocol secretary; dated, may be outdated]";
    $tool = 'AI-Workbench/tools/aiwb-dossier/aiwb-dossier.py';   // co_drive_path: lib.php (not loaded by the tests)
    $tail = '[End of dossier. Search or fix it: python "' . (function_exists('co_drive_path') ? co_drive_path($tool) : $tool)
        . '" search <words> | edit <id> ...]';
    return $head . "\n" . implode("\n\n", $parts) . "\n" . $tail;
}

/**
 * Secretary prompt block: dossier rows relevant to the batch (full-text on the batch text) plus the list of open
 * affairs, so that the model updates rows instead of repeating them. Returns [text, ids known to the model].
 *
 * @return array{0:string,1:array<int,bool>}
 */
function co_dossier_prompt_block(mysqli $db, string $env, string $batchText): array
{
    $rows = co_dossier_search($db, $env, mb_substr($batchText, 0, 3000, 'UTF-8'), 40);
    foreach (co_dossier_q($db, "SELECT * FROM dossier WHERE env = ? AND status = 'active' AND section IN ('me', 'state') ORDER BY updated_utc DESC LIMIT 60", [$env]) as $r) {
        $rows[] = co_dossier_row($r);
    }
    $known = [];
    $lines = [];
    foreach ($rows as $r) {
        if (isset($known[$r['id']])) {
            continue;
        }
        $known[$r['id']] = $r['updatedUtc']; // the revision the model saw
        $lines[] = 'd' . $r['id'] . ' ' . $r['section'] . ($r['subject'] !== '' ? ' [' . $r['subject'] . ']' : '') . ': ' . $r['text'];
    }
    return [$lines ? implode("\n", $lines) : '(none yet)', $known];
}

/**
 * Applies the secretary's "dossier" ops for $env. Ops:
 *  {"op":"add","section":"me|people|state","subject":"…","text":"…","msg":<seq>}
 *  {"op":"update","id":<id>,"text":"…","subject":"…","msg":<seq>}   (supersede: the old row stays as replaced)
 *  {"op":"close","id":<id>,"msg":<seq>}                              (a finished affair)
 * Only ids shown to the model ($known) may be changed. Returns the number of applied ops.
 */
function co_dossier_apply(mysqli $db, string $env, string $chatId, array $dates, mixed $ops, string $model, array $known): int
{
    if (!is_array($ops) || $ops === []) {
        return 0;
    }
    $n = 0;
    foreach ($ops as $op) {
        if ($n >= CO_DOSSIER_SECRETARY_MAX) {
            break;
        }
        if (!is_array($op)) {
            continue;
        }
        $msg = $op['msg'] ?? null;
        $msg = is_int($msg) ? $msg : (is_string($msg) && ctype_digit($msg) ? (int)$msg : null);
        if ($msg === null || !isset($dates[$msg])) {
            continue;
        }
        $src = ['chat' => $chatId, 'msg' => $msg, 'date' => $dates[$msg] ?: null];
        $id = $op['id'] ?? null;
        $id = is_int($id) ? $id : (is_string($id) && preg_match('/^d?(\d+)$/', $id, $m) ? (int)$m[1] : null);
        try {
            switch ($op['op'] ?? '') {
                case 'add':
                    if (co_dossier_secretish((string)($op['text'] ?? ''))) {
                        continue 2;
                    }
                    co_dossier_add($db, $env, (string)($op['section'] ?? ''), $op['subject'] ?? '', $op['text'] ?? '', $src, 'secretary', $model);
                    $n++;
                    break;
                case 'update':
                    if ($id === null || !isset($known[$id]) || co_dossier_secretish((string)($op['text'] ?? ''))) {
                        continue 2;
                    }
                    co_dossier_update($db, $env, $id, ['text' => $op['text'] ?? null, 'subject' => $op['subject'] ?? null, 'src' => $src, 'rev' => $known[$id]], 'supersede', 'secretary', $model);
                    $n++;
                    break;
                case 'close':
                    if ($id === null || !isset($known[$id])) {
                        continue 2;
                    }
                    $row = co_dossier_get($db, $env, $id);
                    if ($row && $row['section'] === 'state' && co_dossier_set_status($db, $env, $id, 'closed', 'secretary', $known[$id])) {
                        $n++;
                    }
                    break;
            }
        } catch (InvalidArgumentException $e) {
            continue;
        }
    }
    return $n;
}

/** Passwords, tokens, keys, card and account numbers never go into the dossier. */
function co_dossier_secretish(string $text): bool
{
    // whole words: "secretary" / "секретарь" are fine
    return (bool)preg_match('/(парол[ьяеюи]|password|passwd|\btokens?\b|токен|api[ _-]?key|\bsecrets?\b|секрет(?!ар)|\bPIN\b|\bCVV\b|\b(?:\d[ -]?){13,19}\b|\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}\b)/iu', $text);
}

const CO_RELATED_STOP = ['это', 'этот', 'эта', 'эти', 'того', 'тому', 'там', 'тут', 'как', 'так', 'что', 'чтобы', 'чтоб', 'когда', 'если',
    'или', 'либо', 'уже', 'еще', 'ещё', 'есть', 'было', 'были', 'будет', 'быть', 'надо', 'нужно', 'можно', 'может', 'могу', 'очень',
    'просто', 'только', 'тоже', 'также', 'потом', 'сейчас', 'теперь', 'здесь', 'вот', 'всё', 'все', 'всех', 'него', 'неё', 'них',
    'она', 'они', 'оно', 'мне', 'меня', 'мной', 'тебе', 'тебя', 'себя', 'свой', 'своя', 'свои', 'наш', 'наша', 'который', 'которая',
    'которые', 'которых', 'для', 'про', 'при', 'без', 'над', 'под', 'через', 'после', 'перед', 'между', 'около', 'вместо', 'кроме',
    'опять', 'снова', 'давай', 'сделай', 'сделать', 'посмотри', 'смотри', 'with', 'that', 'this', 'from', 'have', 'what', 'which',
    'there', 'their', 'about', 'would', 'could', 'should', 'into', 'your', 'they', 'them', 'then', 'than', 'when', 'where', 'also',
    'just', 'like', 'only', 'some', 'more', 'please'];

// A word stem counts as rare at this idf (or ln(N/3) on small tables): in fewer than ~1 of 650 protocol entries.
const CO_RELATED_RARE_IDF = 6.5;

/** Significant word stems of a text: words of 4+ letters (acronyms of 3+), no stop words, first 6 letters. */
function co_related_stems(string $text, int $max = 60): array
{
    static $stop = null;
    $stop ??= array_fill_keys(CO_RELATED_STOP, true);
    preg_match_all('/[\p{L}\p{N}][\p{L}\p{N}_-]*/u', $text, $m);
    $out = [];
    foreach ($m[0] as $w) {
        $acro = preg_match('/^[A-Z0-9]{3,}$/', $w) === 1 && preg_match('/[A-Z]/', $w) === 1;
        $w = mb_strtolower($w, 'UTF-8');
        if ((mb_strlen($w, 'UTF-8') < 4 && !$acro) || isset($stop[$w]) || ctype_digit($w)) {
            continue;
        }
        $out[mb_substr($w, 0, 6, 'UTF-8')] = true;
        if (count($out) >= $max) {
            break;
        }
    }
    return array_map('strval', array_keys($out)); // a digits-only stem ("230000" of 230000V) would come back as int
}

/**
 * Secretary hints: protocol entries of other chats visible to $env (own projects, shared ones) and dossier rows that
 * match $text. Mechanical: candidates are the best full-text entries for the message plus (schema 20, 09.11) the entries
 * behind the protocol and vector matches of the best CO_RELATED_CHATS chats of the chat search (search-lib.php, without
 * the message list); an entry counts only when it shares at least two RARE word stems with the message - names, terms,
 * ids. Another name from the aliases counts as the word of the message that called it (Клод finds Claude, but лифт and
 * its alias elevator are one word, not two). Calibrated 07.11 on the copy of the database (10k entries): plain full-text
 * scores of whole messages were mostly noise (one common word such as "reader" or "why"), so the secretary speaks
 * rarely and only when it is sure. 09.11, 40 messages of the owner: the chat search alone found half as many hints at
 * 1 s instead of 30 ms, so it only adds candidates.
 */
function co_related(mysqli $db, string $env, string $text, string $excludeChat, bool $peopleOnly, int $limit = 5, ?string $scopeProject = null): array
{
    $q = co_dossier_clean($text, CO_RELATED_MAX_QUERY);
    if (mb_strlen($q, 'UTF-8') < 12) {
        return ['hits' => [], 'dossier' => []];
    }
    $dossier = co_related_dossier($db, $env, $q, $peopleOnly);
    if (count(co_related_stems($q)) < 2 || $scopeProject === '') {
        return ['hits' => [], 'dossier' => $dossier];
    }
    $scored = co_related_candidates($db, $env, $q, $excludeChat, $scopeProject);
    $hits = [];
    $perChat = [];
    foreach ($scored as $c) {
        if ($c['rare'] < 2 || ($perChat[$c['chatId']] ?? 0) >= 2) {
            continue;
        }
        $perChat[$c['chatId']] = ($perChat[$c['chatId']] ?? 0) + 1;
        unset($c['rare'], $c['via']);
        $hits[] = $c;
        if (count($hits) >= $limit) {
            break;
        }
    }
    return ['hits' => $hits, 'dossier' => $dossier];
}

const CO_RELATED_CHATS = 8; // best chats of the chat search whose protocol entries join the candidates

/**
 * Candidate protocol entries for $q, best first: 'rare' - words of $q (a word with its alias names counts once) whose
 * rare stem the entry has, 'score' - the idf sum of those stems, 'via' - fts or search.
 */
function co_related_candidates(mysqli $db, string $env, string $q, string $excludeChat, ?string $scopeProject): array
{
    require_once __DIR__ . '/search-lib.php';
    $vis = share_visible_sql('p', 'c') . " AND p.archived = 0 AND pr.status <> 'superseded' AND pr.section <> 'question'"
        . ($scopeProject !== null ? ' AND c.project_id = ?' : '');
    $visP = $scopeProject !== null ? [$env, $env, $env, $scopeProject] : [$env, $env, $env];
    $cols = 'pr.id, pr.chat_id, c.project_id, p.env, c.title, pr.entry_no, pr.section, pr.text, pr.msg_seq, pr.created_utc';
    $from = 'FROM protocol pr JOIN chats c ON c.chat_id = pr.chat_id JOIN projects p ON p.project_id = c.project_id';
    $rows = [];
    foreach (co_dossier_q($db, "SELECT $cols $from WHERE $vis AND pr.chat_id <> ? AND MATCH(pr.text) AGAINST(? IN NATURAL LANGUAGE MODE)
          ORDER BY MATCH(pr.text) AGAINST(? IN NATURAL LANGUAGE MODE) DESC LIMIT 100", [...$visP, $excludeChat, $q, $q]) as $r) {
        $rows[(int)$r['id']] = $r + ['via' => 'fts'];
    }
    $scope = ['sql' => share_visible_sql('p', 'c') . ' AND p.archived = 0' . ($scopeProject !== null ? ' AND c.project_id = ?' : ''), 'params' => $visP];
    $found = co_search_chats($db, $env, $scope, $q, [], $excludeChat, false);
    $ids = [];
    foreach (array_slice($found['chats'], 0, CO_RELATED_CHATS, true) as $c) {
        foreach ($c['entryIds'] as $id) {
            if (!isset($rows[(int)$id])) {
                $ids[] = (int)$id;
            }
        }
    }
    if ($ids) {
        $in = implode(',', array_fill(0, count($ids), '?'));
        foreach (co_dossier_q($db, "SELECT $cols $from WHERE $vis AND pr.id IN ($in)", [...$visP, ...$ids]) as $r) {
            $rows[(int)$r['id']] = $r + ['via' => 'search'];
        }
    }
    if (!$rows) {
        return [];
    }
    // words of the message: each own stem is one word; the stems of its alias names belong to the word that called them
    $word = [];
    foreach (co_related_stems($q, 200) as $st) {
        $word[$st] = $st;
    }
    foreach ($found['aliasGroups'] as $via => $names) {
        $key = co_related_stems((string)$via, 10)[0] ?? (string)$via;
        foreach (co_related_stems(implode(' ', $names), 100) as $st) {
            $word[$st] ??= $key;
        }
    }
    $n = max(1, (int)(co_dossier_q($db, 'SELECT COUNT(*) AS n FROM protocol')[0]['n'] ?? 1));
    $rareIdf = min(CO_RELATED_RARE_IDF, log($n / 3));
    $idf = [];
    $out = [];
    foreach ($rows as $r) {
        $sum = 0.0;
        $words = [];
        foreach (co_related_stems((string)$r['text'], 200) as $st) {
            if (!isset($word[$st])) {
                continue;
            }
            $idf[$st] ??= log($n / (1 + co_related_df($db, $st)));
            if ($idf[$st] >= $rareIdf) {
                $words[$word[$st]] = true;
                $sum += $idf[$st];
            }
        }
        $out[] = [
            'chatId' => (string)$r['chat_id'],
            'projectId' => (string)$r['project_id'],
            'env' => (string)$r['env'],
            'title' => (string)($r['title'] ?? ''),
            'no' => (int)$r['entry_no'],
            'msg' => (int)$r['msg_seq'],
            'section' => (string)$r['section'],
            'text' => co_dossier_clean((string)$r['text'], 260),
            'date' => substr((string)$r['created_utc'], 0, 10),
            'score' => round($sum, 2),
            'rare' => count($words),
            'via' => $r['via'],
        ];
    }
    usort($out, static fn($a, $b) => $b['score'] <=> $a['score']);
    return $out;
}

/** Protocol entries containing a word that starts with $stem (full-text index, boolean prefix search). */
function co_related_df(mysqli $db, string $stem): int
{
    $t = (string)preg_replace('/[^\p{L}\p{N}]+/u', '', $stem);
    if (mb_strlen($t, 'UTF-8') < 3) {
        return PHP_INT_MAX >> 8; // below the index token size: treat as common
    }
    return (int)(co_dossier_q($db, 'SELECT COUNT(*) AS n FROM protocol WHERE MATCH(text) AGAINST(? IN BOOLEAN MODE)', [$t . '*'])[0]['n'] ?? 0);
}

/**
 * Dossier rows for the hints: people whose name (a word of 4+ letters of the subject) is in the message, and current
 * affairs whose topic words are all in it; up to 3 rows, one per subject.
 */
function co_related_dossier(mysqli $db, string $env, string $q, bool $peopleOnly): array
{
    $qset = array_fill_keys(co_related_stems($q, 200), true);
    if (!$qset) {
        return [];
    }
    $out = [];
    $seen = [];
    $rows = co_dossier_list($db, $env, false, $peopleOnly);
    // a name word shared by several people (the family surname) does not point at anybody
    $shared = [];
    foreach (array_unique(array_map(static fn($r) => $r['subject'], array_filter($rows, static fn($r) => $r['section'] === 'people'))) as $subj) {
        foreach (co_related_stems($subj, 10) as $x) {
            $shared[$x] = ($shared[$x] ?? 0) + 1;
        }
    }
    foreach ($rows as $r) {
        if ($r['section'] === 'me' || $r['subject'] === '' || isset($seen[$r['subject']])) {
            continue;
        }
        $st = co_related_stems($r['subject'], 10);
        if (!$st) {
            continue;
        }
        $hit = array_filter($st, static fn($x) => isset($qset[$x]));
        $ok = $r['section'] === 'people'
            ? count($hit) === count($st) || (bool)array_filter($hit, static fn($x) => ($shared[$x] ?? 0) < 2)
            : count($hit) === count($st);
        if (!$ok) {
            continue;
        }
        $seen[$r['subject']] = true;
        $out[] = ['id' => $r['id'], 'section' => $r['section'], 'subject' => $r['subject'], 'text' => co_dossier_clean($r['text'], 260), 'date' => $r['date']];
        if (count($out) >= 3) {
            break;
        }
    }
    return $out;
}

/**
 * One entry point for the host key (aiwb-dossier, first pass) and the person's edits (db.php dossier_put).
 * $in: env, action list|search|context|add|edit|close|delete|restore|import, plus the action's fields.
 */
function co_dossier_host(mysqli $db, array $in): array
{
    $env = $in['env'] ?? null;
    if (!is_string($env) || !preg_match('/^[a-z][a-z0-9_-]{0,39}$/', $env)) {
        throw new InvalidArgumentException('env is required');
    }
    $author = is_string($in['author'] ?? null) && in_array($in['author'], ['user', 'agent', 'import'], true) ? $in['author'] : 'agent';
    $by = is_string($in['by'] ?? null) ? mb_substr($in['by'], 0, 200) : null;
    $id = isset($in['id']) && (is_int($in['id']) || (is_string($in['id']) && preg_match('/^d?\d+$/', $in['id'])))
        ? (int)ltrim((string)$in['id'], 'd') : null;
    $src = [
        'chat' => is_string($in['chatId'] ?? null) ? $in['chatId'] : null,
        'msg' => is_int($in['msg'] ?? null) ? $in['msg'] : null,
        'date' => is_string($in['date'] ?? null) ? $in['date'] : gmdate('Y-m-d'),
    ];
    switch ($in['action'] ?? '') {
        case 'list':
            return ['rows' => co_dossier_list($db, $env, !empty($in['all']), !empty($in['peopleOnly']))];
        case 'search':
            return ['rows' => co_dossier_search($db, $env, is_string($in['q'] ?? null) ? $in['q'] : '', (int)($in['limit'] ?? 30))];
        case 'context':
            return ['text' => co_dossier_context($db, $env, !empty($in['peopleOnly']))];
        case 'add':
            return ['id' => co_dossier_add($db, $env, (string)($in['section'] ?? ''), $in['subject'] ?? '', $in['text'] ?? '', $src, $author, null, $by)];
        case 'edit':
            if ($id === null) {
                throw new InvalidArgumentException('id is required');
            }
            return ['id' => co_dossier_update($db, $env, $id, ['section' => $in['section'] ?? null, 'subject' => $in['subject'] ?? null,
                'text' => $in['text'] ?? null, 'rev' => $in['rev'] ?? null], 'edit', $author, null, $by)];
        case 'close':
        case 'delete':
        case 'restore':
            if ($id === null) {
                throw new InvalidArgumentException('id is required');
            }
            $status = ['close' => 'closed', 'delete' => 'deleted', 'restore' => 'active'][$in['action']];
            return ['ok' => co_dossier_set_status($db, $env, $id, $status, $by)];
        case 'import':
            // first pass: rows [{section, subject, text, chatId?, msg?, date?}], author import, no secret-like text
            $rows = $in['rows'] ?? null;
            if (!is_array($rows) || !array_is_list($rows) || count($rows) > 500) {
                throw new InvalidArgumentException('rows must be a list of up to 500 items');
            }
            $added = 0;
            $skipped = 0;
            foreach ($rows as $r) {
                if (!is_array($r) || co_dossier_secretish((string)($r['text'] ?? ''))) {
                    $skipped++;
                    continue;
                }
                try {
                    co_dossier_add($db, $env, (string)($r['section'] ?? ''), $r['subject'] ?? '', $r['text'] ?? '', [
                        'chat' => is_string($r['chatId'] ?? null) ? $r['chatId'] : null,
                        'msg' => is_int($r['msg'] ?? null) ? $r['msg'] : null,
                        'date' => is_string($r['date'] ?? null) ? $r['date'] : null,
                    ], 'import', is_string($in['model'] ?? null) ? $in['model'] : null, $by);
                    $added++;
                } catch (InvalidArgumentException $e) {
                    $skipped++;
                }
            }
            return ['added' => $added, 'skipped' => $skipped];
    }
    throw new InvalidArgumentException('action must be list, search, context, add, edit, close, delete, restore or import');
}
