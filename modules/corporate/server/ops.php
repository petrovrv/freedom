<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server part of the corporate module (loaded by modules-lib.php only while the module is enabled and standard-signed).
//   user: corporate_waiting - chats of an environment whose last answer stopped because the employer's gateway model was
//                             unavailable ({env?} -> {waiting: [{chatId, projectId, title, eventId, reason, since}]})
//         corporate_resume  - resume such chats in bulk ({chatIds: [1..20], deviceId?, text?} -> {results: [...]})
//   CO Web finds both names in modules_list.gateways[].ops (manifest server.gatewayOps: waiting, resume).
// The handlers run inside db.php (session or bearer of a member already checked); the core's rules for job_submit
// (shared chats, roles, devices) apply to every resumed job.
declare(strict_types=1);

require_once __DIR__ . '/resume-lib.php';

$corpEnv = static function (array $c, array $in): string {
    $env = $in['env'] ?? $c['myEnv'];
    if (!is_string($env) || $env === '' || (!$c['owner'] && $env !== $c['myEnv'])) {
        throw new InvalidArgumentException('forbidden env');
    }
    return $env;
};

return [
    'user' => [
        'corporate_waiting' => static fn(array $c, array $in): array => ['waiting' => co_corp_waiting($c['db'], $corpEnv($c, $in))],
        'corporate_resume' => static fn(array $c, array $in): array => co_corp_resume($c['db'], $in,
            ['db' => $c['db'], 'myEnv' => $c['myEnv'], 'admin' => $c['owner'], 'email' => $c['email']],
            static fn(mixed $env): bool => is_string($env) && $env !== '' && ($c['owner'] || $env === $c['myEnv']),
            $c['module']),
    ],
    'push' => [],
    'host' => [],
];
