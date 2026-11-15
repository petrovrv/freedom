<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module whatsapp: the handlers modules-lib.php calls.
//   host: wa_ask, wa_answer - the WhatsApp service on the home PC (host key) puts a contact's message into the chosen
//         chat as an isolated job and reads the answer it sends back
// No data of its own.
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
    'user' => [],
    'push' => [],
    'host' => [
        'wa_ask' => static fn(array $c, array $in): array => $coWaSafe(static fn(): array => co_wa_ask($c['db'], $in)),
        'wa_answer' => static fn(array $c, array $in): array => $coWaSafe(static fn(): array => co_wa_answer($c['db'], $in)),
    ],
];
