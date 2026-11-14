<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module invite-help (Freedom SPEC §7.5, install by invitation).
//   user: invite_* - the owner in CO Web (members get owner_only)
//   push: invite_* - the owner's home tool invite-help (push key of the owner's PC)
//   routes: intake  - POST {i, k, step, text}: a sanitized report the invited friend saw and confirmed
//           answers - POST {i, k}: status and answers of that invite's reports
declare(strict_types=1);

require_once __DIR__ . '/invite-lib.php';

$owner = static fn(callable $fn): callable => static fn(array $c, array $in): array =>
    !empty($c['owner']) ? $fn($c, $in) : ['ok' => false, 'error' => 'owner_only'];
$ops = [
    'invite_create'        => static fn(array $c, array $in): array => co_invite_create($c, $in),
    'invite_list'          => static fn(array $c, array $in): array => co_invite_list($c, $in),
    'invite_revoke'        => static fn(array $c, array $in): array => co_invite_revoke($c, $in),
    'invite_reports'       => static fn(array $c, array $in): array => co_invite_reports($c, $in),
    'invite_report_get'    => static fn(array $c, array $in): array => co_invite_report_get($c, $in),
    'invite_report_answer' => static fn(array $c, array $in): array => co_invite_report_set($c, $in, 'answered'),
    'invite_report_close'  => static fn(array $c, array $in): array => co_invite_report_set($c, $in, 'closed'),
];

return [
    'user' => array_map($owner, $ops),
    // the push key belongs to the owner's PC (push.php checks it before the module is called)
    'push' => $ops,
    'routes' => [
        'intake'  => static fn(array $c): mixed => co_invite_route($c, 'intake'),
        'answers' => static fn(array $c): mixed => co_invite_route($c, 'answers'),
    ],
];
