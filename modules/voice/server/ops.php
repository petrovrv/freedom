<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module voice (SPEC §3.8): the handlers modules-lib.php calls.
//   user: voice_status - is the voice hub configured and online; voice_speak - the hub reads a text aloud (mp3);
//         voice_ask_user, voice_answer_user - a member's own voice client (co-android) asks the agent of a chat
//   uploads: turn - a member's recording to the hub (speech to text, Home Assistant, answer as audio)
//   host: voice_ask, voice_answer - the hub (host key) puts a phrase of a voice point into a chat for its agent and
//         reads the answer
// No data of its own: the hub address and key stay in ~/co-agent/secrets/voice.json.
declare(strict_types=1);

require_once __DIR__ . '/voice-lib.php';

$coVoiceSafe = static function (callable $f): array {
    try {
        return $f();
    } catch (InvalidArgumentException $e) {
        return ['error' => $e->getMessage()];
    }
};

return [
    'user' => [
        'voice_status' => static fn(array $c, array $in): array => co_voice_status(),
        'voice_speak' => static fn(array $c, array $in): array => $coVoiceSafe(static fn(): array => co_voice_speak($in)),
        'voice_ask_user' => static fn(array $c, array $in): array => $coVoiceSafe(static fn(): array => co_voice_ask_user($c['db'], $in, (string)$c['myEnv'])),
        'voice_answer_user' => static fn(array $c, array $in): array => $coVoiceSafe(static fn(): array => co_voice_answer_user($c['db'], $in, (string)$c['myEnv'])),
    ],
    'push' => [],
    'host' => [
        'voice_ask' => static fn(array $c, array $in): array => $coVoiceSafe(static fn(): array => co_voice_ask($c['db'], $in)),
        'voice_answer' => static fn(array $c, array $in): array => $coVoiceSafe(static fn(): array => co_voice_answer($c['db'], $in)),
    ],
    'uploads' => [
        'turn' => static fn(array $c): array => co_voice_upload_turn($c),
    ],
];
