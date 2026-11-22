<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module whatsapp: the handlers modules-lib.php calls.
//   host: wa_ask, wa_answer - the WhatsApp service on the home PC (host key) puts a contact's message into the chosen
//         chat as an isolated job and reads the answer it sends back
//   user: wa_status - the tray chip: is the service alive (heartbeat of wa_pushes), are my pushes on
//   host: wa_pushes - the same service fetches copies of the pushes of the envs it writes to on WhatsApp
//   notify: push - copies of the server's pushes for those envs (state/modules/whatsapp/pushes.json);
//           status - whether the service writes pushes to an env now (core push.php op=channels)
declare(strict_types=1);

require_once __DIR__ . '/whatsapp-lib.php';

$coWaSafe = static function (callable $f): array {
    try {
        return $f();
    } catch (InvalidArgumentException $e) {
        return ['error' => $e->getMessage()];
    }
};

return [
    'user' => [
        'wa_status' => static fn(array $c, array $in): array => co_wa_status($c),
    ],
    'push' => [],
    'host' => [
        'wa_ask' => static fn(array $c, array $in): array => $coWaSafe(static fn(): array => co_wa_ask($c['db'], $in)),
        'wa_answer' => static fn(array $c, array $in): array => $coWaSafe(static fn(): array => co_wa_answer($c['db'], $in)),
        'wa_pushes' => static fn(array $c, array $in): array => co_wa_pushes($c, $in),
    ],
    'notify' => [
        'whatsapp' => ['push' => static fn(array $c, array $in) => co_wa_push_hook($c, $in),
            'status' => static fn(array $c, array $in): array => co_wa_push_status($c, $in)],
    ],
];
