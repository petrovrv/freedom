<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// concierge-mail server part: the Mail page reaches the person's mailbox on the home server through the module
// home-relay (the site as postman, end-to-end encrypted; see modules/home-relay/server/ops.php). These ops only name
// this module; which mailbox answers is decided on the home server.
//   user  mail_status  - is the worker online and serving mail, its public key, my unread count (a number for the tray)
//         mail_call    - {id, pk, iv, ct}: one sealed request to my mail handler; waits for the sealed answer
//         mail_chats   - {q}: chats of my environment to put a thread into (0.12.0: id, project, titles; never mail)
declare(strict_types=1);

const CO_MAIL_MODULE = 'concierge-mail';

/** [relay lib, its context for my environment], or null when the module home-relay is not enabled. */
function co_mail_relay(array $c): ?array
{
    if (isset($c['relay'])) return $c['relay'];  // tests
    $m = co_modules($c['db'])['home-relay'] ?? null;
    if ($m === null || !$m['enabled']) return null;
    $lib = co_module_load($m)['relay'] ?? null;
    if (!is_array($lib)) return null;
    $rc = co_module_ctx($c['db'], $m, (string)$c['myEnv'], (bool)($c['owner'] ?? false), (string)($c['email'] ?? ''));
    // a page's call answers right after the relay (db.php: co_json), so its database connection may close while it waits
    // (the shared host limits connections); an agent's app action writes its log after the handler and keeps it
    if (!isset($c['actor'])) $rc['release'] = static function () use ($c): void { try { $c['db']?->close(); } catch (Throwable) {} };
    return [$lib, $rc];
}

function co_mail_status(array $c): array
{
    $r = co_mail_relay($c);
    if ($r === null) return ['online' => false, 'paired' => false];  // home-relay switched off: "not connected"
    [$lib, $rc] = $r;
    $st = $lib['status']($rc, CO_MAIL_MODULE);
    $mine = is_array($st['status'] ?? null) ? $st['status'] : [];
    // the page and the tray read a flat status: paired = my worker serves mail for me; online = it answers from a mailbox
    // of my environment right now (a handler that is down or bound elsewhere reports no "bound")
    $served = !empty($st['paired']) && !empty($st['served']);
    return ['env' => (string)($st['env'] ?? ''), 'module' => CO_MAIL_MODULE, 'paired' => $served,
        'online' => $served && !empty($st['online']) && ($mine['bound'] ?? false) === true, 'seen' => (int)($st['seen'] ?? 0), 'pub' => (string)($st['pub'] ?? ''),
        'unread' => max(0, (int)($mine['unread'] ?? 0)), 'synced' => (string)($mine['synced'] ?? ''), 'account' => (string)($mine['account'] ?? '')]
        + (isset($st['error']) ? ['error' => (string)$st['error']] : []);
}

/**
 * Chats of my environment whose title or project title has every word (rows chat_id, project_id, title, data, ptitle,
 * pdata), newest first; at most 600 rows, the filter below drops archived, trashed and agent chats.
 */
function co_mail_chat_rows(array $c, array $words): array
{
    if (isset($c['chatRows'])) return $c['chatRows'];  // tests
    $db = $c['db'] ?? null;
    if (!$db instanceof mysqli || (string)($c['myEnv'] ?? '') === '') return [];
    $sql = 'SELECT c.chat_id, c.project_id, c.title, c.data, p.title AS ptitle, p.data AS pdata FROM chats c'
        . ' JOIN projects p ON p.project_id = c.project_id WHERE p.env = ? AND p.archived = 0';
    $types = 's';
    $params = [(string)$c['myEnv']];
    foreach (array_slice($words, 0, 6) as $w) {
        $like = '%' . addcslashes($w, '%_\\') . '%';
        $sql .= ' AND (LOWER(COALESCE(c.title, \'\')) LIKE ? OR LOWER(COALESCE(p.title, \'\')) LIKE ?)';
        $types .= 'ss';
        array_push($params, $like, $like);
    }
    $st = $db->prepare($sql . ' ORDER BY c.updated_utc DESC LIMIT 600');
    $st->bind_param($types, ...$params);
    $st->execute();
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    return $rows;
}

/**
 * 0.12.0: chats a thread can be put into - the person's own live chats (no family room, archive, trash or chats an
 * agent started for another chat, title "⤷ ..."), the words of q in the title or the project's title, at most 20.
 */
function co_mail_chats(array $c, array $in): array
{
    $words = preg_split('/\s+/u', mb_strtolower(trim(mb_substr((string)($in['q'] ?? ''), 0, 80))), -1, PREG_SPLIT_NO_EMPTY) ?: [];
    $out = [];
    foreach (co_mail_chat_rows($c, $words) as $r) {
        $title = (string)($r['title'] ?? '');
        if ($title === '' || str_starts_with($title, "\u{2937} ")) continue;
        $d = json_decode((string)($r['data'] ?? ''), true) ?: [];
        $pd = json_decode((string)($r['pdata'] ?? ''), true) ?: [];
        if (!empty($d['archived']) || !empty($d['draft']) || !empty($d['trashedUtc']) || !empty($pd['family'])) continue;
        $ptitle = (string)($r['ptitle'] ?? '') ?: (string)$r['project_id'];
        $hay = mb_strtolower($title . ' ' . $ptitle);
        foreach ($words as $w) if (!str_contains($hay, $w)) continue 2;
        $out[] = ['chat' => (string)$r['chat_id'], 'project' => (string)$r['project_id'], 'title' => mb_substr($title, 0, 200),
            'projectTitle' => mb_substr($ptitle, 0, 200)];
        if (count($out) >= 20) break;
    }
    return ['chats' => $out];
}

function co_mail_call(array $c, array $in): array
{
    $r = co_mail_relay($c);
    if ($r === null) return ['error' => 'not_paired'];
    [$lib, $rc] = $r;
    return $lib['call']($rc, CO_MAIL_MODULE, $in);
}

return [
    'user' => [
        'mail_status' => static fn(array $c, array $in): array => co_mail_status($c),
        'mail_call' => static fn(array $c, array $in): array => co_mail_call($c, $in),
        'mail_chats' => static fn(array $c, array $in): array => co_mail_chats($c, $in),
    ],
    'push' => [],
    'host' => [],
];
