<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// assoc-lib.php of the module assoc-search (Freedom F10; until CO Web 0.102 part of the core's search-lib.php, schema
// 20, 2026-11-09). Two search providers for the core's chat search (ext/1 search.provider):
//   expand - aliases: other names of a thing the query mentions (Wikidata, model, own names) as extra full-text words
//   chats  - cosine over protocol vectors (potion-multilingual-128M, written by the aiwb-assoc tool from the home PC).
//            The query vector is built here from the word table (sum of raw token embeddings per word / token count),
//            because PHP cannot run the model's tokenizer.
// Measured 08.11 on 22 golden questions (all chats of the owner): keywords MRR 0.66 -> 0.87, plain questions 0.47 ->
// 0.69, English questions 0.29 -> 0.48. Tables m_assoc_search_* (install hook: the pre-F10 core tables are renamed).
declare(strict_types=1);

const CO_VEC_DIM = 256;
const CO_ALIAS_MAX_TERMS = 24;       // extra full-text terms from aliases
const CO_ALIAS_MAX_GROUPS = 2;       // a query term in more groups than this expands nothing

/** Lower-case alias term: letters, digits and _ words joined by one space ('' when nothing is left). */
function co_assoc_norm(string $t): string
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
    $groups = co_search_q($db, "SELECT a.env, a.grp, MIN(t.term) AS via FROM m_assoc_search_aliases a JOIN (SELECT term FROM m_assoc_search_aliases WHERE term IN ($in)
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
    foreach (co_search_q($db, "SELECT env, grp, term FROM m_assoc_search_aliases WHERE $cond ORDER BY CHAR_LENGTH(term) LIMIT 200", $params) as $r) {
        $t = (string)$r['term'];
        if (!isset($have[$t]) && $count < CO_ALIAS_MAX_TERMS) {
            $have[$t] = true;
            $out[$via[$r['env'] . '|' . $r['grp']]][] = $t;
            $count++;
        }
    }
    return $out;
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
    foreach (co_search_q($db, "SELECT word, n, scale, v FROM m_assoc_search_vec_words WHERE word IN ($in)", $words) as $r) {
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
            $r = co_search_q($db, 'SELECT word, n, scale, v FROM m_assoc_search_vec_words WHERE word LIKE ? ORDER BY freq DESC LIMIT 1', [$p . '%']);
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

// ---- host ops of the indexer (aiwb-assoc, host key)

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
    $db->query("DELETE x FROM m_assoc_search_vec_protocol x LEFT JOIN protocol p ON p.id = x.protocol_id WHERE p.id IS NULL OR p.status <> 'active'");
    $removed = $db->affected_rows;
    $limit = max(1, min(5000, (int)($in['limit'] ?? 2000)));
    $where = "p.status = 'active' AND (x.protocol_id IS NULL OR x.sha <> SHA1(p.text))";
    $rows = co_search_q($db, "SELECT p.id, p.chat_id, p.text FROM protocol p LEFT JOIN m_assoc_search_vec_protocol x ON x.protocol_id = p.id
                               WHERE $where ORDER BY p.id LIMIT ?", [$limit]);
    $left = (int)(co_search_q($db, "SELECT COUNT(*) n FROM protocol p LEFT JOIN m_assoc_search_vec_protocol x ON x.protocol_id = p.id WHERE $where")[0]['n'] ?? 0);
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
        $st = $db->prepare('INSERT INTO m_assoc_search_vec_protocol (protocol_id, chat_id, sha, scale, v) VALUES (?, ?, ?, ?, ?)
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
        $st = $db->prepare('INSERT INTO m_assoc_search_vec_words (word, n, scale, v, freq) VALUES (?, ?, ?, ?, ?)
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

/** Words already in m_assoc_search_vec_words (page by word). */
function co_vec_words(mysqli $db, array $in): array
{
    $after = is_string($in['after'] ?? null) ? $in['after'] : '';
    $limit = max(1, min(50000, (int)($in['limit'] ?? 50000)));
    $rows = co_search_q($db, 'SELECT word, freq FROM m_assoc_search_vec_words WHERE word > ? ORDER BY word LIMIT ?', [$after, $limit]);
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
        $rows = co_search_q($db, 'SELECT env, grp, term, source FROM m_assoc_search_aliases' . ($env !== null ? " WHERE env IN ('', ?)" : '') . ' ORDER BY env, grp, term',
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
        $del = $db->prepare('DELETE FROM m_assoc_search_aliases WHERE env = ? AND grp = ?');
        $ins = $db->prepare('INSERT IGNORE INTO m_assoc_search_aliases (env, grp, term, source, created_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))');
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
                $t = is_string($t) ? co_assoc_norm($t) : '';
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

/**
 * Provider list 'vector' of the core's chat search: protocol entries of the scope by cosine to the query vector, best
 * entry per chat, best chats first. null when no query word is known.
 */
function co_assoc_chats(mysqli $db, array $in): ?array
{
    $q = (string)($in['q'] ?? '');
    $qv = co_vec_query($db, co_fts_words($q !== '' ? $q : (string)($in['text'] ?? '')));
    if ($qv === null) {
        return null;
    }
    $where = (string)$in['where'];
    $params = (array)$in['params'];
    $depth = max(1, (int)($in['depth'] ?? 50));
    $best = [];
    $vjoin = 'JOIN chats c ON c.chat_id = pr.chat_id JOIN projects p ON p.project_id = c.project_id';
    $st = $db->prepare("SELECT pr.chat_id, x.protocol_id, x.scale, x.v
           FROM m_assoc_search_vec_protocol x JOIN protocol pr ON pr.id = x.protocol_id AND pr.chat_id = x.chat_id AND pr.status = 'active' $vjoin
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
    $best = array_slice($best, 0, $depth, true);
    $texts = [];
    if ($best) {
        $ids = array_column($best, 1);
        $in2 = implode(',', array_fill(0, count($ids), '?'));
        foreach (co_search_q($db, "SELECT id, msg_seq, text FROM protocol WHERE id IN ($in2)", $ids) as $r) {
            $texts[(int)$r['id']] = '#' . (int)$r['msg_seq'] . ' (protocol): ' . mb_substr((string)$r['text'], 0, 240, 'UTF-8');
        }
    }
    $out = [];
    foreach ($best as $id => $b) {
        $out[] = ['chatId' => (string)$id, 'match' => $texts[$b[1]] ?? '', 'entryId' => $b[1]];
    }
    return $out;
}
