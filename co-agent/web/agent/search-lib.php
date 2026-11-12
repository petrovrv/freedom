<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// search-lib.php: associative chat search (schema 20, 2026-11-09).
// Three ranked lists of chats are merged by reciprocal rank (RRF): full-text over messages (events), full-text over
// protocol entries, and cosine over protocol vectors (potion-multilingual-128M, written by the aiwb-assoc tool from the
// home PC). The query vector is built here from vec_words (sum of raw token embeddings per word / token count), because
// PHP cannot run the model's tokenizer. Aliases (Wikidata, model, own names) add the other names of a thing the query
// mentions as extra full-text words. Measured 08.11 on 22 golden questions (all chats of the owner): keywords MRR 0.66 ->
// 0.87, plain questions 0.47 -> 0.69, English questions 0.29 -> 0.48.
declare(strict_types=1);

require_once __DIR__ . '/fts-lib.php';

const CO_SEARCH_RRF_K = 60;
const CO_SEARCH_DEPTH = 50;          // chats taken from each list
const CO_VEC_DIM = 256;
const CO_ALIAS_MAX_TERMS = 24;       // extra full-text terms from aliases
const CO_ALIAS_MAX_GROUPS = 2;       // a query term in more groups than this expands nothing

/** Rows of a prepared statement as associative arrays. */
function co_search_q(mysqli $db, string $sql, array $params = []): array
{
    $st = $db->prepare($sql);
    $st->execute($params);
    $res = $st->get_result();
    $rows = $res === false ? [] : $res->fetch_all(MYSQLI_ASSOC);
    $st->close();
    return $rows;
}

/** Lower-case alias term: letters, digits and _ words joined by one space ('' when nothing is left). */
function co_alias_norm(string $t): string
{
    preg_match_all('/[\p{L}\p{N}_]+/u', mb_strtolower($t, 'UTF-8'), $m);
    return mb_substr(implode(' ', $m[0]), 0, 120, 'UTF-8');
}

/**
 * Other names of the things a query mentions: every 1..3-word sequence of the query is looked up in aliases (general
 * rows and rows of $env); the remaining terms of the matched groups come back (at most CO_ALIAS_MAX_TERMS).
 */
function co_alias_expand(mysqli $db, string $env, string $q): array
{
    return array_merge([], ...array_values(co_alias_groups($db, $env, $q)));
}

/** co_alias_expand by the query sequence that called them: [query words => [other names]]. */
function co_alias_groups(mysqli $db, string $env, string $q): array
{
    $words = co_fts_words($q);
    $grams = [];
    $n = count($words);
    for ($i = 0; $i < $n; $i++) {
        for ($k = 1; $k <= 3 && $i + $k <= $n; $k++) {
            $grams[implode(' ', array_slice($words, $i, $k))] = true;
        }
    }
    if (!$grams) {
        return [];
    }
    $grams = array_map('strval', array_keys($grams));
    $in = implode(',', array_fill(0, count($grams), '?'));
    // a term that names 3+ groups (a first name shared by several people, a word in many descriptions) is too vague
    $groups = co_search_q($db, "SELECT a.env, a.grp, MIN(t.term) AS via FROM aliases a JOIN (SELECT term FROM aliases WHERE term IN ($in)
                                  AND env IN ('', ?) GROUP BY term HAVING COUNT(*) <= " . CO_ALIAS_MAX_GROUPS . ") t ON t.term = a.term
                                 WHERE a.env IN ('', ?) GROUP BY a.env, a.grp LIMIT 20", [...$grams, $env, $env]);
    if (!$groups) {
        return [];
    }
    $cond = implode(' OR ', array_fill(0, count($groups), '(env = ? AND grp = ?)'));
    $params = [];
    $via = [];
    foreach ($groups as $g) {
        array_push($params, $g['env'], $g['grp']);
        $via[$g['env'] . '|' . $g['grp']] = (string)$g['via'];
    }
    $have = array_fill_keys($grams, true);
    $out = [];
    $count = 0;
    foreach (co_search_q($db, "SELECT env, grp, term FROM aliases WHERE $cond ORDER BY CHAR_LENGTH(term) LIMIT 200", $params) as $r) {
        $t = (string)$r['term'];
        if (!isset($have[$t]) && $count < CO_ALIAS_MAX_TERMS) {
            $have[$t] = true;
            $out[$via[$r['env'] . '|' . $r['grp']]][] = $t;
            $count++;
        }
    }
    return $out;
}

/**
 * BOOLEAN MODE query: each text (the user's query, the expanded terms) through co_fts_query on its own, so a long
 * question does not push the terms out of the 30-word cap; plus alias terms with the lower weight '<' - a one-word
 * alias as word and stem prefix, a longer one as a quoted phrase of its 3+ letter words. Duplicate terms are dropped
 * (the user's own term wins over the same alias term).
 */
function co_search_fts_query(array $texts, array $aliases): string
{
    $out = [];
    foreach (array_map('co_fts_query', $texts) as $p) {
        foreach (explode(' ', $p) as $t) {
            if ($t !== '') {
                $out[$t] = $t;
            }
        }
    }
    foreach ($aliases as $a) {
        $ws = array_values(array_filter(explode(' ', co_alias_norm($a)), static fn($w) => mb_strlen($w, 'UTF-8') >= 3));
        $terms = count($ws) === 1 ? explode(' ', co_fts_query($ws[0])) : (count($ws) > 1 ? ['"' . implode(' ', $ws) . '"'] : []);
        foreach ($terms as $t) {
            if ($t !== '' && !isset($out[$t])) {
                $out[$t] = '<' . $t;
            }
        }
    }
    return implode(' ', array_values($out));
}

/** int8 vector from VARBINARY(256). */
function co_vec_unpack(string $bin): array
{
    return array_values(unpack('c' . CO_VEC_DIM, $bin));
}

/**
 * Query vector from vec_words: words found as they are, unknown words of 5+ letters by the most frequent word sharing
 * the longest prefix (down to 60% of the word, at least 4 letters). null when no word is known.
 */
function co_vec_query(mysqli $db, array $words): ?array
{
    $words = array_values(array_unique(array_filter(array_map('strval', $words),
        static fn($w) => mb_strlen($w, 'UTF-8') >= 2 && mb_strlen($w, 'UTF-8') <= 60 && !ctype_digit($w))));
    if (!$words) {
        return null;
    }
    $in = implode(',', array_fill(0, count($words), '?'));
    $found = [];
    foreach (co_search_q($db, "SELECT word, n, scale, v FROM vec_words WHERE word IN ($in)", $words) as $r) {
        $found[(string)$r['word']] = $r;
    }
    $rows = [];
    $lookups = 0;
    foreach ($words as $w) {
        if (isset($found[$w])) {
            $rows[] = $found[$w];
            continue;
        }
        $len = mb_strlen($w, 'UTF-8');
        if ($len < 5 || preg_match('/^\p{L}+$/u', $w) !== 1) {
            continue;
        }
        for ($k = $len - 1; $k >= max(4, (int)ceil($len * 0.6)) && $lookups < 15; $k--) {
            $lookups++;
            $p = addcslashes(mb_substr($w, 0, $k, 'UTF-8'), '%_\\');
            $r = co_search_q($db, 'SELECT word, n, scale, v FROM vec_words WHERE word LIKE ? ORDER BY freq DESC LIMIT 1', [$p . '%']);
            if ($r) {
                $rows[] = $r[0];
                break;
            }
        }
    }
    if (!$rows) {
        return null;
    }
    $acc = array_fill(0, CO_VEC_DIM, 0.0);
    $tokens = 0;
    foreach ($rows as $r) {
        $s = (float)$r['scale'];
        foreach (co_vec_unpack((string)$r['v']) as $i => $x) {
            $acc[$i] += $x * $s;
        }
        $tokens += (int)$r['n'];
    }
    $norm = sqrt(array_sum(array_map(static fn($x) => $x * $x, $acc)));
    if ($norm <= 0.0 || $tokens <= 0) {
        return null;
    }
    return array_map(static fn($x) => $x / $norm, $acc);
}

/**
 * Chats ranked by three lists merged by RRF. $scope: ['sql' => condition over p (projects) and c (chats), 'params' =>
 * its parameters]. $q is the text for full-text and vectors, $extraTerms are words that only widen the full-text search
 * (CO Web AI search terms); $messages false skips the message list (secretary hints: protocol and vectors only).
 * Returns ['chats' => [chatId => [...]], 'aliases' => [...], 'aliasGroups' => [query words => [...]], 'vector' => bool].
 */
function co_search_chats(mysqli $db, string $env, array $scope, string $q, array $extraTerms = [], ?string $exclude = null, bool $messages = true): array
{
    $terms = implode(' ', $extraTerms);
    $text = trim($q . ' ' . $terms);
    $aliasGroups = co_alias_groups($db, $env, $q);
    $aliases = array_values(array_unique([...array_merge([], ...array_values($aliasGroups)), ...co_alias_expand($db, $env, $terms)]));
    $aliases = array_slice($aliases, 0, CO_ALIAS_MAX_TERMS);
    $ftq = co_search_fts_query(array_values(array_filter([$q, $terms], static fn($t) => trim($t) !== '')), $aliases);
    $snipQ = trim($text . ' ' . implode(' ', $aliases));
    $where = $scope['sql'] . ($exclude !== null ? ' AND c.chat_id <> ?' : '');
    $params = $exclude !== null ? [...$scope['params'], $exclude] : $scope['params'];
    $join = 'JOIN chats c ON c.chat_id = x.chat_id JOIN projects p ON p.project_id = c.project_id';

    $msg = $proto = $vec = [];
    $info = [];
    if ($ftq !== '' && $messages) {
        // messages: the best message of each chat (rows come by score), up to 3 snippets
        foreach (co_search_q($db,
            "SELECT x.chat_id, x.seq, x.text, " . co_fts_score_sql('MATCH(x.text) AGAINST(? IN BOOLEAN MODE)', 'x.text') . " AS score
               FROM events x $join
              WHERE $where AND MATCH(x.text) AGAINST(? IN BOOLEAN MODE)
              ORDER BY score DESC LIMIT 2000", [$ftq, ...$params, $ftq]) as $r) {
            $id = (string)$r['chat_id'];
            if (!isset($msg[$id]) && count($msg) >= CO_SEARCH_DEPTH) {
                continue;
            }
            $msg[$id] ??= count($msg);
            $info[$id]['m'] ??= [];
            if (count($info[$id]['m']) < 3) {
                $info[$id]['m'][] = '#' . (int)$r['seq'] . ': ' . db_snippet((string)$r['text'], $snipQ, 160);
            }
        }
    }
    if ($ftq !== '') {
        // protocol entries
        foreach (co_search_q($db,
            "SELECT x.id, x.chat_id, x.entry_no, x.msg_seq, x.text, MATCH(x.text) AGAINST(? IN BOOLEAN MODE) AS score
               FROM protocol x $join
              WHERE $where AND x.status = 'active' AND MATCH(x.text) AGAINST(? IN BOOLEAN MODE)
              ORDER BY score DESC LIMIT 1000", [$ftq, ...$params, $ftq]) as $r) {
            $id = (string)$r['chat_id'];
            if (isset($proto[$id]) || count($proto) >= CO_SEARCH_DEPTH) {
                continue;
            }
            $proto[$id] = count($proto);
            $info[$id]['p'] = '#' . (int)$r['msg_seq'] . ' (protocol): ' . mb_substr((string)$r['text'], 0, 240, 'UTF-8');
            $info[$id]['pid'] = (int)$r['id'];
        }
    }
    // vectors of protocol entries
    $qv = co_vec_query($db, co_fts_words($q !== '' ? $q : $text));
    if ($qv !== null) {
        $best = [];
        $vjoin = 'JOIN chats c ON c.chat_id = pr.chat_id JOIN projects p ON p.project_id = c.project_id';
        $st = $db->prepare("SELECT pr.chat_id, x.protocol_id, x.scale, x.v
               FROM vec_protocol x JOIN protocol pr ON pr.id = x.protocol_id AND pr.chat_id = x.chat_id AND pr.status = 'active' $vjoin
              WHERE $where");
        $st->execute($params);
        $res = $st->get_result();
        while ($r = $res->fetch_assoc()) {
            $v = co_vec_unpack((string)$r['v']);
            $dot = 0.0;
            for ($i = 0; $i < CO_VEC_DIM; $i++) {
                $dot += $qv[$i] * $v[$i];
            }
            $dot *= (float)$r['scale'];
            $id = (string)$r['chat_id'];
            if (!isset($best[$id]) || $dot > $best[$id][0]) {
                $best[$id] = [$dot, (int)$r['protocol_id']];
            }
        }
        $st->close();
        uasort($best, static fn($a, $b) => $b[0] <=> $a[0]);
        $best = array_slice($best, 0, CO_SEARCH_DEPTH, true);
        $texts = [];
        if ($best) {
            $ids = array_column($best, 1);
            $in = implode(',', array_fill(0, count($ids), '?'));
            foreach (co_search_q($db, "SELECT id, msg_seq, text FROM protocol WHERE id IN ($in)", $ids) as $r) {
                $texts[(int)$r['id']] = '#' . (int)$r['msg_seq'] . ' (protocol): ' . mb_substr((string)$r['text'], 0, 240, 'UTF-8');
            }
        }
        foreach ($best as $id => $b) {
            $vec[(string)$id] = count($vec);
            $info[(string)$id]['v'] = $texts[$b[1]] ?? '';
            $info[(string)$id]['vid'] = $b[1];
        }
    }

    $out = [];
    foreach ([$msg, $proto, $vec] as $list) {
        foreach ($list as $id => $rank) {
            $out[(string)$id] = ($out[(string)$id] ?? 0.0) + 1.0 / (CO_SEARCH_RRF_K + $rank + 1);
        }
    }
    arsort($out);
    $chats = [];
    foreach ($out as $id => $score) {
        $i = $info[$id] ?? [];
        $matches = [];
        foreach ([$i['p'] ?? null, $i['v'] ?? null] as $m) {
            if ($m !== null && $m !== '' && !in_array($m, $matches, true)) {
                $matches[] = $m;
            }
        }
        $matches = array_slice([...array_slice($matches, 0, 1), ...($i['m'] ?? []), ...array_slice($matches, 1)], 0, 4);
        $chats[(string)$id] = [
            'score'   => round($score, 6),
            'ranks'   => ['messages' => isset($msg[$id]) ? $msg[$id] + 1 : null, 'protocol' => isset($proto[$id]) ? $proto[$id] + 1 : null,
                          'vector' => isset($vec[$id]) ? $vec[$id] + 1 : null],
            'matches' => $matches,
            // protocol entries behind the protocol and vector matches (secretary hints quote them)
            'entryIds' => array_values(array_unique(array_filter([$i['pid'] ?? null, $i['vid'] ?? null]))),
        ];
    }
    return ['chats' => $chats, 'aliases' => $aliases, 'aliasGroups' => $aliasGroups, 'vector' => $qv !== null];
}

/** Host op find_chats (aiwb-chat find): chats of an env (own and shared with it) for a query, best first. */
function co_search_find(mysqli $db, array $in): array
{
    $q = is_string($in['q'] ?? null) ? trim($in['q']) : '';
    if (mb_strlen($q, 'UTF-8') < 2 || mb_strlen($q, 'UTF-8') > 300) {
        throw new InvalidArgumentException('q must be 2..300 characters');
    }
    $env = is_string($in['env'] ?? null) ? $in['env'] : '';
    $chatId = is_string($in['chatId'] ?? null) && $in['chatId'] !== '' ? $in['chatId'] : null;
    if ($chatId !== null) {
        $r = co_search_q($db, 'SELECT p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?', [$chatId]);
        if (!$r) {
            throw new InvalidArgumentException('unknown chatId');
        }
        $env = (string)$r[0]['env'];
    }
    if (preg_match('/^[a-z0-9_-]{1,40}$/', $env) !== 1) {
        throw new InvalidArgumentException('env or chatId required');
    }
    $limit = max(1, min(30, (int)($in['limit'] ?? 10)));
    $scope = ['sql' => share_visible_sql('p', 'c') . ' AND p.archived = 0', 'params' => [$env, $env, $env]];
    $res = co_search_chats($db, $env, $scope, $q, [], empty($in['includeSelf']) ? $chatId : null);
    $ids = array_map('strval', array_slice(array_keys($res['chats']), 0, $limit));
    $meta = [];
    if ($ids) {
        $in2 = implode(',', array_fill(0, count($ids), '?'));
        foreach (co_search_q($db, "SELECT c.chat_id, c.project_id, c.title, c.updated_utc, c.summary FROM chats c WHERE c.chat_id IN ($in2)", $ids) as $r) {
            $meta[(string)$r['chat_id']] = $r;
        }
    }
    $chats = [];
    foreach ($ids as $id) {
        $m = $meta[$id] ?? [];
        $c = $res['chats'][$id];
        unset($c['entryIds']);
        $chats[] = ['chatId' => $id, 'projectId' => (string)($m['project_id'] ?? ''), 'title' => (string)($m['title'] ?? ''),
            'updatedUtc' => co_db_iso($m['updated_utc'] ?? null), 'summary' => (string)($m['summary'] ?? '')] + $c;
    }
    return ['env' => $env, 'aliases' => $res['aliases'], 'vector' => $res['vector'], 'chats' => $chats];
}

// ---- host ops of the indexer (aiwb-assoc, push key)

/** Decoded VARBINARY(256) vector from base64, or an exception. */
function co_vec_b64(mixed $v): string
{
    $bin = is_string($v) ? base64_decode($v, true) : false;
    if ($bin === false || strlen($bin) !== CO_VEC_DIM) {
        throw new InvalidArgumentException('v must be base64 of ' . CO_VEC_DIM . ' bytes');
    }
    return $bin;
}

/** Protocol entries without a current vector (removes vectors of gone or inactive entries first). */
function co_vec_todo(mysqli $db, array $in): array
{
    $db->query("DELETE x FROM vec_protocol x LEFT JOIN protocol p ON p.id = x.protocol_id WHERE p.id IS NULL OR p.status <> 'active'");
    $removed = $db->affected_rows;
    $limit = max(1, min(5000, (int)($in['limit'] ?? 2000)));
    $where = "p.status = 'active' AND (x.protocol_id IS NULL OR x.sha <> SHA1(p.text))";
    $rows = co_search_q($db, "SELECT p.id, p.chat_id, p.text FROM protocol p LEFT JOIN vec_protocol x ON x.protocol_id = p.id
                               WHERE $where ORDER BY p.id LIMIT ?", [$limit]);
    $left = (int)(co_search_q($db, "SELECT COUNT(*) n FROM protocol p LEFT JOIN vec_protocol x ON x.protocol_id = p.id WHERE $where")[0]['n'] ?? 0);
    return ['removed' => $removed, 'left' => $left,
        'rows' => array_map(static fn($r) => ['id' => (int)$r['id'], 'chatId' => (string)$r['chat_id'], 'text' => (string)$r['text']], $rows)];
}

/** Upsert protocol vectors and word rows. */
function co_vec_put(mysqli $db, array $in): array
{
    $prot = is_array($in['protocol'] ?? null) ? $in['protocol'] : [];
    $words = is_array($in['words'] ?? null) ? $in['words'] : [];
    if (count($prot) > 5000 || count($words) > 20000) {
        throw new InvalidArgumentException('at most 5000 protocol rows and 20000 words per call');
    }
    $np = $nw = $skipped = 0;
    $db->begin_transaction();
    try {
        $st = $db->prepare('INSERT INTO vec_protocol (protocol_id, chat_id, sha, scale, v) VALUES (?, ?, ?, ?, ?)
                            ON DUPLICATE KEY UPDATE chat_id = VALUES(chat_id), sha = VALUES(sha), scale = VALUES(scale), v = VALUES(v)');
        foreach ($prot as $r) {
            $sha = (string)($r['sha'] ?? '');
            if (!is_array($r) || preg_match('/^[0-9a-f]{40}$/', $sha) !== 1 || !is_int($r['id'] ?? null) || !is_string($r['chatId'] ?? null)) {
                throw new InvalidArgumentException('protocol rows need id, chatId, sha, scale, v');
            }
            $st->execute([$r['id'], $r['chatId'], $sha, (float)($r['scale'] ?? 0), co_vec_b64($r['v'] ?? null)]);
            $np++;
        }
        $st->close();
        $st = $db->prepare('INSERT INTO vec_words (word, n, scale, v, freq) VALUES (?, ?, ?, ?, ?)
                            ON DUPLICATE KEY UPDATE n = VALUES(n), scale = VALUES(scale), v = VALUES(v), freq = VALUES(freq)');
        foreach ($words as $r) {
            $w = is_array($r) && is_string($r['w'] ?? null) ? $r['w'] : '';
            if ($w === '' || mb_strlen($w, 'UTF-8') > 60 || $w !== mb_strtolower($w, 'UTF-8')) {
                $skipped++; // lower case differs between Python and PHP for a few letters: skip, do not fail the batch
                continue;
            }
            $st->execute([$w, max(1, min(30000, (int)($r['n'] ?? 1))), (float)($r['scale'] ?? 0), co_vec_b64($r['v'] ?? null), max(0, (int)($r['freq'] ?? 0))]);
            $nw++;
        }
        $st->close();
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return ['protocol' => $np, 'words' => $nw, 'skipped' => $skipped];
}

/** Words already in vec_words (page by word). */
function co_vec_words(mysqli $db, array $in): array
{
    $after = is_string($in['after'] ?? null) ? $in['after'] : '';
    $limit = max(1, min(50000, (int)($in['limit'] ?? 50000)));
    $rows = co_search_q($db, 'SELECT word, freq FROM vec_words WHERE word > ? ORDER BY word LIMIT ?', [$after, $limit]);
    return ['words' => array_map(static fn($r) => [(string)$r['word'], (int)$r['freq']], $rows)];
}

/** Message texts for the vocabulary (page by event_id, each cut to 20000 characters). */
function co_vec_texts(mysqli $db, array $in): array
{
    $after = is_string($in['after'] ?? null) ? $in['after'] : '';
    $limit = max(1, min(2000, (int)($in['limit'] ?? 1000)));
    $rows = co_search_q($db, "SELECT event_id, LEFT(text, 20000) t FROM events
                               WHERE event_id > ? AND type IN ('user_message', 'assistant_message') ORDER BY event_id LIMIT ?", [$after, $limit]);
    return ['rows' => array_map(static fn($r) => [(string)$r['event_id'], (string)($r['t'] ?? '')], $rows)];
}

/** Active protocol entries of an env's own projects (page by id): the corpus of aiwb-assoc aliases. */
function co_vec_protocol_texts(mysqli $db, array $in): array
{
    $env = is_string($in['env'] ?? null) ? $in['env'] : '';
    if (preg_match('/^[a-z0-9_-]{1,40}$/', $env) !== 1) {
        throw new InvalidArgumentException('env required');
    }
    $after = max(0, (int)($in['after'] ?? 0));
    $limit = max(1, min(5000, (int)($in['limit'] ?? 5000)));
    $rows = co_search_q($db, "SELECT x.id, x.chat_id, x.text FROM protocol x JOIN chats c ON c.chat_id = x.chat_id
                               JOIN projects p ON p.project_id = c.project_id
                               WHERE p.env = ? AND x.status = 'active' AND x.id > ? ORDER BY x.id LIMIT ?", [$env, $after, $limit]);
    return ['rows' => array_map(static fn($r) => [(int)$r['id'], (string)$r['chat_id'], (string)$r['text']], $rows)];
}

/** Aliases: replace the rows of the given groups (or add), list, delete. */
function co_alias_op(mysqli $db, string $op, array $in): array
{
    if ($op === 'alias_list') {
        $env = is_string($in['env'] ?? null) ? $in['env'] : null;
        $rows = co_search_q($db, 'SELECT env, grp, term, source FROM aliases' . ($env !== null ? " WHERE env IN ('', ?)" : '') . ' ORDER BY env, grp, term',
            $env !== null ? [$env] : []);
        return ['aliases' => $rows];
    }
    $groups = is_array($in['groups'] ?? null) ? $in['groups'] : [];
    if (count($groups) > 5000) {
        throw new InvalidArgumentException('at most 5000 groups per call');
    }
    $n = 0;
    $db->begin_transaction();
    try {
        $del = $db->prepare('DELETE FROM aliases WHERE env = ? AND grp = ?');
        $ins = $db->prepare('INSERT IGNORE INTO aliases (env, grp, term, source, created_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
        foreach ($groups as $g) {
            $env = is_array($g) && is_string($g['env'] ?? null) ? $g['env'] : '';
            $grp = is_array($g) && is_string($g['grp'] ?? null) ? $g['grp'] : '';
            if (preg_match('/^[a-z0-9_-]{0,40}$/', $env) !== 1 || $grp === '' || mb_strlen($grp, 'UTF-8') > 80) {
                throw new InvalidArgumentException('groups need env ("" or an env id) and grp');
            }
            if ($op === 'alias_delete') {
                $del->execute([$env, $grp]);
                $n += $del->affected_rows;
                continue;
            }
            $source = in_array($g['source'] ?? '', ['wikidata', 'model', 'user'], true) ? $g['source'] : 'model';
            $terms = [];
            foreach ((array)($g['terms'] ?? []) as $t) {
                $t = is_string($t) ? co_alias_norm($t) : '';
                if (mb_strlen($t, 'UTF-8') >= 2) {
                    $terms[$t] = true;
                }
            }
            if (count($terms) < 2) {
                continue; // one name alone expands nothing; the stored group stays as it is
            }
            $del->execute([$env, $grp]);
            foreach (array_keys($terms) as $t) {
                $ins->execute([$env, $grp, (string)$t, $source]);
                $n += $ins->affected_rows;
            }
        }
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return [$op === 'alias_delete' ? 'deleted' : 'inserted' => $n];
}
