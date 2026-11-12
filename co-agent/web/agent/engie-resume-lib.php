<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Legacy name (Freedom F1 5b): the code is corporate-resume-lib.php; the old function names stay for scripts that still
// require this file.
declare(strict_types=1);
require_once __DIR__ . '/corporate-resume-lib.php';

function co_engie_waiting(mysqli $db, string $env): array { return co_corporate_waiting($db, $env); }
function co_engie_resume(mysqli $db, array $in, array $acc, callable $envAllowed): array { return co_corporate_resume($db, $in, $acc, $envAllowed); }
