<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6 principle 1): the server core knows no module by name. Employer words may appear in
// web/agent/*.php only in gateway-lib.php, as the legacy wire tokens that deployed Bridges, devices, CO Web builds and
// old database rows still send or read; everything else about a model gateway comes from the installed module.
// No database needed. Driver: php co-agent/tests/core-words-test.php (or tests/actions-test.py --test core-words-test.php)
declare(strict_types=1);

const CORE_WORDS_RE = '/corporate|engie|tractebel/i';
// the one file that holds the legacy wire tokens (names deployed Bridges, devices, CO Web builds and old rows still send)
const CORE_WORDS_KEEP_FILE = 'gateway-lib.php';

$fail = 0;
$files = glob(__DIR__ . '/../web/agent/*.php') ?: [];
foreach ($files as $f) {
    if (basename($f) === CORE_WORDS_KEEP_FILE) continue;
    foreach (file($f) ?: [] as $n => $line) {
        if (preg_match(CORE_WORDS_RE, $line)) {
            $fail++;
            echo 'FAIL ' . basename($f) . ':' . ($n + 1) . ': ' . trim($line) . "\n";
        }
    }
}
echo $fail === 0 ? 'ok   ' . count($files) . " core files without module names (gateway-lib.php holds the legacy wire tokens)\nall ok\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
