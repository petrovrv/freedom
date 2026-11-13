<?php
// Checks failures-lib.php diagnosis against the cases shared with CO Web (co-web/src/diagnose.cases.json).
// Run: php co-agent/tests/failures-test.php   (exit 0 = all cases pass)
declare(strict_types=1);
require __DIR__ . '/../web/agent/failures-lib.php';
// F10: the gateway (its error messages, its dispatcher on the claude CLI) comes from an installed module; fixture: the
// corporate module of the tree, installed and disabled (no database here: set directly)
co_gateways([['id' => 'corporate', 'enabled' => false,
    'manifest' => json_decode((string)file_get_contents(__DIR__ . '/../../modules/corporate/module.json'), true)]]);

$data = json_decode((string)file_get_contents(__DIR__ . '/../../co-web/src/diagnose.cases.json'), true, 512, JSON_THROW_ON_ERROR);
$fail = 0;
foreach ($data['cases'] as $c) {
    $want = $c['serverKind'] ?? $c['kind'];
    [$kind, $detail] = co_fail_diagnose($c['error'], $c['text'] ?? '', $c['agent'], $c['exitCode'] ?? null, !empty($c['timedOut']));
    if ($kind !== $want) {
        $fail++;
        echo "FAIL {$c['name']}: {$kind} (want {$want})\n";
    } elseif ($detail === null || $detail === '') {
        $fail++;
        echo "FAIL {$c['name']}: empty detail\n";
    }
}
// Merge of agent messages and the detail of a bare exit 1 with an agent message.
$err = co_fail_merge_error(['error' => 'exitCode=1; structured result failed', 'agentErrorMessages' => ['boom happened', 'boom happened', '']]);
if ($err !== "exitCode=1; structured result failed\nboom happened") { $fail++; echo "FAIL merge: " . json_encode($err) . "\n"; }
[$k, $d] = co_fail_diagnose($err, '', 'claude', 1, false);
if ($k !== 'agent_exit_1' || $d !== 'boom happened') { $fail++; echo "FAIL exit1 detail: {$k} / {$d}\n"; }
[$k, $d] = co_fail_diagnose('exitCode=1; structured result failed; Agent process exited with code 1.', "First line\nsecond", 'claude', 1, false);
if ($d !== 'First line') { $fail++; echo "FAIL text detail: {$d}\n"; }
[$k, $d] = co_fail_diagnose('', '', 'claude', null, false);
if ($k !== 'no_result' || !$d) { $fail++; echo "FAIL no_result: {$k}\n"; }
// The gateway's own error messages come from its module; its dispatcher runs on the claude CLI.
[$k] = co_fail_diagnose('ENGIE call failed: 502', '', 'dispatcher', null, false);
if ($k !== 'gateway') { $fail++; echo "FAIL module error pattern: {$k}
"; }
[$k] = co_fail_diagnose('hit your weekly limit', '', 'dispatcher', null, false);
if ($k !== 'claude_limit') { $fail++; echo "FAIL dispatcher family: {$k}
"; }
co_gateways([]); // no gateway module
[$k] = co_fail_diagnose('ENGIE call failed: 502', '', 'claude', null, false);
if ($k === 'gateway') { $fail++; echo "FAIL pattern without module
"; }
[$k] = co_fail_diagnose('sign_in_needed', '', 'claude', null, false);
if ($k !== 'gateway') { $fail++; echo "FAIL core pattern: {$k}
"; }
echo $fail ? "{$fail} failed\n" : count($data['cases']) . " cases OK\n";
exit($fail ? 1 : 0);
// SPDX-License-Identifier: AGPL-3.0-or-later
