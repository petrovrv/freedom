<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.129.0 (SPEC §3.6b): the stored text of a protocol "agreement" - who in front, a real due date as
// " [due YYYY-MM-DD]" at the end (chatFolder.ts agreementOf reads that form). No database needed.
// Driver: php co-agent/tests/protocol-agreement-test.php
declare(strict_types=1);

require __DIR__ . '/../cron/protocol-apply.php';

$fail = 0;
$n = 0;
$ok = static function (string $name, bool $cond) use (&$fail, &$n): void {
    $n++;
    if (!$cond) {
        $fail++;
        echo "FAIL $name\n";
    }
};

$ok('who and due', co_protocol_agreement_text('send the report', 'Sam', '2026-12-01') === 'Sam: send the report [due 2026-12-01]');
$ok('no due', co_protocol_agreement_text('send the report', 'Sam', null) === 'Sam: send the report');
$ok('who already leads', co_protocol_agreement_text('Sam sends the report', 'Sam', '2026-12-01') === 'Sam sends the report [due 2026-12-01]');
$ok('impossible date dropped', co_protocol_agreement_text('pay', 'Alex', '2026-02-30') === 'Alex: pay');
$ok('bad form dropped', co_protocol_agreement_text('pay', 'Alex', 'Friday') === 'Alex: pay');
$ok('marker of the model replaced', co_protocol_agreement_text('pay [due 2020-01-01]', '', '2026-12-05') === 'pay [due 2026-12-05]');
$ok('who cleaned of brackets and colons', co_protocol_agreement_text('pay', "[Alex]:\nX", null) === 'Alex X: pay');
$ok('empty text refused', co_protocol_agreement_text('  [due 2026-12-01] ', 'Sam', '2026-12-01') === null);
$ok('non-string who ignored', co_protocol_agreement_text('pay', 5, null) === 'pay');
$ok('long text keeps the marker', str_ends_with((string)co_protocol_agreement_text(str_repeat('a', 700), 'Sam', '2026-12-01'), ' [due 2026-12-01]'));
$ok('unicode', co_protocol_agreement_text('прислать отчёт', 'Агент', '2026-11-28') === 'Агент: прислать отчёт [due 2026-11-28]');

echo $fail === 0 ? "ok   $n agreement checks\nall ok\n" : "$fail of $n FAILED\n";
exit($fail === 0 ? 0 : 1);
