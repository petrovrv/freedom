<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// compact-job-events.php: one-off (2026-10-28) - applies the trace-lib.php rules to the job_events rows written before
// db.php trimmed on insert. Idempotent: a trimmed row trims to itself. Full logs stay in the pre-files snapshot
// (backups/db/pre-files). Run on the server:
//   php ~/co-agent/db/compact-job-events.php [--dry-run] [--optimize]
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';

$agentDir = CO_AGENT_WEB;
require_once $agentDir . '/db-lib.php';
require_once (getenv('CO_TRACE_LIB') ?: $agentDir . '/trace-lib.php');

$dry = in_array('--dry-run', $argv, true);
$optimize = in_array('--optimize', $argv, true);
$db = co_db();

$before = 0;
$after = 0;
$rows = 0;
$changed = 0;
$lastJob = '';
$lastSeq = -1;
$t0 = microtime(true);
while (true) {
    $st = $db->prepare(
        'SELECT job_id, first_seq, data FROM job_events
          WHERE (job_id > ?) OR (job_id = ? AND first_seq > ?)
          ORDER BY job_id, first_seq LIMIT 200'
    );
    $st->bind_param('ssi', $lastJob, $lastJob, $lastSeq);
    $st->execute();
    $list = $st->get_result()->fetch_all(MYSQLI_NUM);
    $st->close();
    if (!$list) {
        break;
    }
    $up = $db->prepare('UPDATE job_events SET data = ? WHERE job_id = ? AND first_seq = ?');
    foreach ($list as [$jobId, $seq, $data]) {
        $lastJob = (string)$jobId;
        $lastSeq = (int)$seq;
        $rows++;
        $before += strlen((string)$data);
        $batch = json_decode((string)$data, true);
        if (!is_array($batch)) {
            $after += strlen((string)$data);
            continue;
        }
        $new = co_db_json(co_trace_trim_batch($batch));
        $after += strlen($new);
        if ($new !== $data) {
            $changed++;
            if (!$dry) {
                $up->bind_param('ssi', $new, $lastJob, $lastSeq);
                $up->execute();
            }
        }
    }
    $up->close();
}
printf(
    "rows %d, changed %d, data %.1f MB -> %.1f MB%s, %.0f s\n",
    $rows, $changed, $before / 1048576, $after / 1048576, $dry ? ' (dry run)' : '', microtime(true) - $t0
);
if ($optimize && !$dry) {
    // InnoDB rebuilds the table; the file shrinks only after this.
    $db->query('OPTIMIZE TABLE job_events');
    echo "optimized\n";
}
