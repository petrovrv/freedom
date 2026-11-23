<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// calendar server part: the Calendar page and tray reach the person's calendar copy on the home server through the module
// home-relay (the site as postman, end-to-end encrypted; see modules/home-relay/server/ops.php). These ops only name
// this module; which calendar answers is decided on the home server.
//   user  cal_status  - is the worker online and serving the calendar, its public key, the flat tray status
//                       (start of the next meeting, number of conflicts; never titles)
//         cal_call    - {id, pk, iv, ct}: one sealed request to my calendar handler; waits for the sealed answer
declare(strict_types=1);

const CO_CAL_MODULE = 'calendar';

/** [relay lib, its context for my environment], or null when the module home-relay is not enabled. */
function co_cal_relay(array $c): ?array
{
    if (isset($c['relay'])) return $c['relay'];  // tests
    $m = co_modules($c['db'])['home-relay'] ?? null;
    if ($m === null || !$m['enabled']) return null;
    $lib = co_module_load($m)['relay'] ?? null;
    if (!is_array($lib)) return null;
    $rc = co_module_ctx($c['db'], $m, (string)$c['myEnv'], (bool)($c['owner'] ?? false), (string)($c['email'] ?? ''));
    // a page's call answers right after the relay, so its database connection may close while it waits (shared host)
    if (!isset($c['actor'])) $rc['release'] = static function () use ($c): void { try { $c['db']?->close(); } catch (Throwable) {} };
    return [$lib, $rc];
}

function co_cal_status(array $c): array
{
    $r = co_cal_relay($c);
    if ($r === null) return ['online' => false, 'paired' => false];
    [$lib, $rc] = $r;
    $st = $lib['status']($rc, CO_CAL_MODULE);
    $mine = is_array($st['status'] ?? null) ? $st['status'] : [];
    $served = !empty($st['paired']) && !empty($st['served']);
    $time = static fn($v): string => is_string($v) && preg_match('/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/', $v) ? $v : '';
    return ['env' => (string)($st['env'] ?? ''), 'module' => CO_CAL_MODULE, 'paired' => $served,
        'online' => $served && !empty($st['online']) && ($mine['bound'] ?? false) === true, 'seen' => (int)($st['seen'] ?? 0),
        'pub' => (string)($st['pub'] ?? ''), 'auth' => in_array($mine['auth'] ?? '', ['ok', 'never', 'needs_consent'], true) ? $mine['auth'] : '',
        'nextStart' => $time($mine['next_start'] ?? ''), 'nextEnd' => $time($mine['next_end'] ?? ''),
        'conflicts' => max(0, (int)($mine['conflicts'] ?? 0)), 'synced' => $time($mine['synced'] ?? ''),
        'account' => (string)($mine['account'] ?? '')]
        + (isset($st['error']) ? ['error' => (string)$st['error']] : []);
}

function co_cal_call(array $c, array $in): array
{
    $r = co_cal_relay($c);
    if ($r === null) return ['error' => 'not_paired'];
    [$lib, $rc] = $r;
    return $lib['call']($rc, CO_CAL_MODULE, $in);
}

return [
    'user' => [
        'cal_status' => static fn(array $c, array $in): array => co_cal_status($c),
        'cal_call' => static fn(array $c, array $in): array => co_cal_call($c, $in),
    ],
    'push' => [],
    'host' => [],
];
