<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// concierge-mail server part: the Mail page reaches the person's mailbox on the home server through the module
// home-relay (the site as postman, end-to-end encrypted; see modules/home-relay/server/ops.php). These ops only name
// this module; which mailbox answers is decided on the home server.
//   user  mail_status  - is the worker online and serving mail, its public key, my unread count (a number for the tray)
//         mail_call    - {id, pk, iv, ct}: one sealed request to my mail handler; waits for the sealed answer
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
    return [$lib, co_module_ctx($c['db'], $m, (string)$c['myEnv'], (bool)($c['owner'] ?? false), (string)($c['email'] ?? ''))];
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
    ],
    'push' => [],
    'host' => [],
];
