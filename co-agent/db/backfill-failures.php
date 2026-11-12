<?php
// One-off backfill of job_failures (schema 17) from jobs and job_events of the last N days (default 14); idempotent.
// Run on the server: php ~/co-agent/db/backfill-failures.php [days]
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';
require CO_AGENT_WEB . '/db-lib.php';
require CO_AGENT_WEB . '/failures-lib.php';

$days = max(1, min(60, (int)($argv[1] ?? 14)));
$total = 0;
do {
    $n = co_job_failures_fill(co_db(), 200, 60.0, $days);
    $total += $n;
} while ($n > 0);
$rows = co_db()->query("SELECT kind, COUNT(*) FROM job_failures WHERE failed_utc >= UTC_TIMESTAMP() - INTERVAL {$days} DAY GROUP BY kind ORDER BY 2 DESC")->fetch_all();
echo "built {$total} rows; last {$days} days by kind:\n";
foreach ($rows as [$k, $c]) {
    echo "  {$c}  {$k}\n";
}
// SPDX-License-Identifier: AGPL-3.0-or-later
