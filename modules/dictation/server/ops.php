<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module dictation (Freedom F10, SPEC §3.6, §3.7): the handlers modules-lib.php calls.
//   user: dictation_status - is a speech-to-text service configured on the server (the composer page then records)
//   uploads: transcribe - audio of a member (agent/module.php?m=dictation&u=transcribe) to the Whisper-compatible API
// No data of its own: the key stays in ~/co-agent/secrets/transcribe.json where the core kept it before F10.
declare(strict_types=1);

require_once __DIR__ . '/transcribe-lib.php';

return [
    'user' => [
        'dictation_status' => static fn(array $c, array $in): array => co_transcribe_status(),
    ],
    'uploads' => [
        'transcribe' => static fn(array $c): array => co_transcribe_upload(),
    ],
];
