<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// search-lib.php: chat search of the core (schema 20, 2026-11-09; ext/1 search.provider since Freedom F10).
// Ranked lists of chats are merged by reciprocal rank (RRF): the core's full-text over messages (events) and over
// protocol entries, plus one list per search provider of an enabled module (manifest provides.search, hook 'chats').
// A provider's 'expand' hook adds other names of the things a query mentions as extra full-text words (lower weight),
// 'items' adds results that are not chats (documents), 'text' gives the indexed text of a project file. The core knows
// no provider by name: it calls the hooks of modules-lib.php co_module_hooks('search') and merges what comes back.
declare(strict_types=1);

require_once __DIR__ . '/fts-lib.php';
require_once __DIR__ . '/modules-lib.php';

const CO_SEARCH_RRF_K = 60;
const CO_SEARCH_DEPTH = 50;          // chats taken from each list
const CO_SEARCH_MAX_EXPAND = 24;     // extra full-text terms from the providers' expand hooks

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

/** Lower-case term: letters, digits and _ words joined by one space ('' when nothing is left). */
function co_search_norm(string $t): string
{
    preg_match_all('/[\p{L}\p{N}_]+/u', mb_strtolower($t, 'UTF-8'), $m);
    return mb_substr(implode(' ', $m[0]), 0, 120, 'UTF-8');
}

/**
 * Calls hook $hook of every search provider; a provider that throws is logged and skipped (the core search still
 * answers). Returns [[provider id, result], ...] in the order of the module list.
 */
function co_search_call(mysqli $db, string $hook, array $in): array
{
    $out = [];
    foreach (co_module_hooks($db, 'search') as $p) {
        $fn = $p['hooks'][$hook] ?? null;
        if ($fn === null) continue;
        try {
            $out[] = [$p['id'], $fn(co_module_ctx($db, $p['module']), $in)];
        } catch (Throwable $e) {
            error_log("search provider {$p['module']['id']}/{$p['id']} $hook: " . $e->getMessage());
        }
    }
    return $out;
}

/**
 * Other names of the things a query mentions, by the query words that called them: [query words => [other names]]
 * from the providers' expand hooks (at most CO_SEARCH_MAX_EXPAND names in all).
 */
function co_search_expand(mysqli $db, string $env, string $q): array
{
    if (trim($q) === '') return [];
    $out = [];
    $count = 0;
    foreach (co_search_call($db, 'expand', ['env' => $env, 'q' => $q]) as [, $groups]) {
        foreach (is_array($groups) ? $groups : [] as $via => $names) {
            foreach (is_array($names) ? $names : [] as $t) {
                if (!is_string($t) || $t === '' || $count >= CO_SEARCH_MAX_EXPAND) continue;
                $out[(string)$via][] = $t;
                $count++;
            }
        }
    }
    return $out;
}

/**
 * BOOLEAN MODE query: each text (the user's query, the expanded terms) through co_fts_query on its own, so a long
 * question does not push the terms out of the 30-word cap; plus expansion names with the lower weight '<' - a one-word
 * name as word and stem prefix, a longer one as a quoted phrase of its 3+ letter words. Duplicate terms are dropped
 * (the user's own term wins over the same expansion term).
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
        $ws = array_values(array_filter(explode(' ', co_search_norm($a)), static fn($w) => mb_strlen($w, 'UTF-8') >= 3));
        $terms = count($ws) === 1 ? explode(' ', co_fts_query($ws[0])) : (count($ws) > 1 ? ['"' . implode(' ', $ws) . '"'] : []);
        foreach ($terms as $t) {
            if ($t !== '' && !isset($out[$t])) {
                $out[$t] = '<' . $t;
            }
        }
    }
    return implode(' ', array_values($out));
}

/**
 * Chats ranked by the core's lists and the providers' lists merged by RRF. $scope: ['sql' => condition over p
 * (projects) and c (chats), 'params' => its parameters]. $q is the text for full-text and the providers, $extraTerms are
 * words that only widen the full-text search (CO Web AI search terms); $messages false skips the message list
 * (secretary hints: protocol and providers only).
 * Returns ['chats' => [chatId => [...]], 'aliases' => [...], 'aliasGroups' => [query words => [...]], 'lists' => [ids
 * of provider lists that ranked]].
 *
 * Hook 'chats' of a provider (ext/1 search.provider): in {env, q, text, where, params, depth} - where/params are the
 * SQL condition over p and c (join `chats c` and `projects p` yourself); out: null (the provider had nothing to rank
 * with) or a list of chats best first [{chatId, match?: string, entryId?: protocol entry id behind the match}].
 */
function co_search_chats(mysqli $db, string $env, array $scope, string $q, array $extraTerms = [], ?string $exclude = null, bool $messages = true): array
{
    $terms = implode(' ', $extraTerms);
    $text = trim($q . ' ' . $terms);
    $aliasGroups = co_search_expand($db, $env, $q);
    $aliases = array_values(array_unique([...array_merge([], ...array_values($aliasGroups)), ...array_merge([], ...array_values(co_search_expand($db, $env, $terms)))]));
    $aliases = array_slice($aliases, 0, CO_SEARCH_MAX_EXPAND);
    $ftq = co_search_fts_query(array_values(array_filter([$q, $terms], static fn($t) => trim($t) !== '')), $aliases);
    $snipQ = trim($text . ' ' . implode(' ', $aliases));
    $where = $scope['sql'] . ($exclude !== null ? ' AND c.chat_id <> ?' : '');
    $params = $exclude !== null ? [...$scope['params'], $exclude] : $scope['params'];
    $join = 'JOIN chats c ON c.chat_id = x.chat_id JOIN projects p ON p.project_id = c.project_id';

    $msg = $proto = [];
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
    // lists of the search providers (modules)
    $lists = ['messages' => $msg, 'protocol' => $proto];
    $active = [];
    foreach (co_search_call($db, 'chats', ['env' => $env, 'q' => $q, 'text' => $text, 'where' => $where, 'params' => $params,
        'depth' => CO_SEARCH_DEPTH]) as [$pid, $rows]) {
        if (in_array($pid, ['messages', 'protocol'], true)) continue;
        if (!is_array($rows)) {
            $lists[$pid] ??= []; // the provider had nothing to rank with: its rank is null for every chat
            continue;
        }
        // the scope is access control: a chat the provider returns outside it is dropped
        $ids = array_values(array_unique(array_filter(array_map(static fn($r) => is_array($r) && is_string($r['chatId'] ?? null) ? $r['chatId'] : '',
            array_slice($rows, 0, 4 * CO_SEARCH_DEPTH)))));
        $inScope = [];
        if ($ids) {
            $in = implode(',', array_fill(0, count($ids), '?'));
            foreach (co_search_q($db, "SELECT c.chat_id FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE $where AND c.chat_id IN ($in)",
                [...$params, ...$ids]) as $r) {
                $inScope[(string)$r['chat_id']] = true;
            }
        }
        $list = [];
        foreach ($rows as $r) {
            $id = is_array($r) && is_string($r['chatId'] ?? null) ? $r['chatId'] : '';
            if ($id === '' || !isset($inScope[$id]) || isset($list[$id]) || count($list) >= CO_SEARCH_DEPTH) continue;
            $list[$id] = count($list);
            if (is_string($r['match'] ?? null)) $info[$id]['x'][] = $r['match'];
            if (is_int($r['entryId'] ?? null)) $info[$id]['xid'][] = $r['entryId'];
        }
        $lists[$pid] = $list;
        $active[] = $pid;
    }

    $out = [];
    foreach ($lists as $list) {
        foreach ($list as $id => $rank) {
            $out[(string)$id] = ($out[(string)$id] ?? 0.0) + 1.0 / (CO_SEARCH_RRF_K + $rank + 1);
        }
    }
    arsort($out);
    $chats = [];
    foreach ($out as $id => $score) {
        $i = $info[$id] ?? [];
        $matches = [];
        foreach ([$i['p'] ?? null, ...($i['x'] ?? [])] as $m) {
            if ($m !== null && $m !== '' && !in_array($m, $matches, true)) {
                $matches[] = $m;
            }
        }
        $matches = array_slice([...array_slice($matches, 0, 1), ...($i['m'] ?? []), ...array_slice($matches, 1)], 0, 4);
        $ranks = [];
        foreach ($lists as $name => $list) {
            $ranks[$name] = isset($list[$id]) ? $list[$id] + 1 : null;
        }
        $chats[(string)$id] = [
            'score'   => round($score, 6),
            'ranks'   => $ranks,
            'matches' => $matches,
            // protocol entries behind the protocol and provider matches (secretary hints quote them)
            'entryIds' => array_values(array_unique(array_filter([$i['pid'] ?? null, ...($i['xid'] ?? [])]))),
        ];
    }
    return ['chats' => $chats, 'aliases' => $aliases, 'aliasGroups' => $aliasGroups, 'lists' => $active];
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
    // vector: legacy wire of aiwb-chat (schema 20) - a provider list ranked
    return ['env' => $env, 'aliases' => $res['aliases'], 'vector' => $res['lists'] !== [], 'lists' => $res['lists'], 'chats' => $chats];
}

/**
 * Results that are not chats (CO Web AI search, user op search_items): every provider's 'items' hook, in {env, q,
 * limit, projectId?} out a list best first of {kind, title, projectId, projectTitle?, path?, modified?, snippets?}
 * (kind 'doc' with path = a project file relative to projects/, opened from Drive). Providers in the order of the module
 * list, each cut to $limit.
 */
function co_search_items(mysqli $db, string $env, string $q, int $limit = 8, ?string $projectId = null): array
{
    $q = trim($q);
    if (mb_strlen($q, 'UTF-8') < 2) return [];
    $q = mb_substr($q, 0, 300, 'UTF-8');
    $out = [];
    // an item must belong to a project of the env (the provider is asked for that env; the core checks it)
    $own = [];
    foreach (co_search_q($db, 'SELECT project_id FROM projects WHERE env = ?', [$env]) as $r) {
        $own[(string)$r['project_id']] = true;
    }
    foreach (co_search_call($db, 'items', ['env' => $env, 'q' => $q, 'limit' => $limit, 'projectId' => $projectId]) as [$pid, $items]) {
        foreach (array_slice(is_array($items) ? array_values($items) : [], 0, $limit) as $it) {
            if (!is_array($it) || !is_string($it['title'] ?? null) || !is_string($it['projectId'] ?? null) || !isset($own[$it['projectId']])) continue;
            $out[] = ['provider' => $pid, 'kind' => is_string($it['kind'] ?? null) ? $it['kind'] : 'item', 'title' => $it['title'],
                'projectId' => $it['projectId'], 'projectTitle' => (string)($it['projectTitle'] ?? ''), 'path' => (string)($it['path'] ?? ''),
                'modified' => (string)($it['modified'] ?? ''),
                'snippets' => array_values(array_map('strval', array_filter(is_array($it['snippets'] ?? null) ? $it['snippets'] : [], 'is_string')))];
        }
    }
    return $out;
}

/** Indexed text of a project file (path relative to projects/), from the first provider that has it; '' = none. */
function co_search_doc_text(mysqli $db, string $path, int $max = 20000): string
{
    foreach (co_module_hooks($db, 'search') as $p) {
        $fn = $p['hooks']['text'] ?? null;
        if ($fn === null) continue;
        try {
            $t = $fn(co_module_ctx($db, $p['module']), ['path' => $path, 'max' => $max]);
        } catch (Throwable $e) {
            error_log("search provider {$p['module']['id']}/{$p['id']} text: " . $e->getMessage());
            continue;
        }
        if (is_string($t) && $t !== '') return mb_substr($t, 0, $max, 'UTF-8');
    }
    return '';
}
