<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Cron entry for the "Bridge offline" watchdog: runs the same check as push.php?op=watch, without HTTP.
// Deployed to ~/co-agent/cron/watch-cron.php; hPanel → Advanced → Cron Jobs, every 5 minutes:
//   /usr/bin/php ~/co-agent/cron/watch-cron.php
declare(strict_types=1);

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}

require_once __DIR__ . '/boot.php';
require CO_AGENT_WEB . '/push-lib.php';

// The cron runs every 5 minutes; the 60 s throttle shared with op=watch keeps both paths from double-checking.
$r = co_watch_check();
if (!empty($r['alerted']) || !empty($r['error'])) {
    fwrite(STDOUT, gmdate('c') . ' ' . json_encode($r) . "\n");
}

// 2026-10-16: Drive -> database sync health. drive-sync.php marks meta.drive_sync_ok after every successful run;
// silence for 30 min sends one push (usually the site's Google sign-in expired), recovery sends another.
require_once CO_AGENT_WEB . '/db-lib.php';
try {
    $db = co_db();
    $get = static function (string $k) use ($db): ?string {
        $st = $db->prepare('SELECT v FROM meta WHERE k = ?');
        $st->execute([$k]);
        return $st->get_result()->fetch_row()[0] ?? null;
    };
    $ok = $get('drive_sync_ok');
    $alerted = $get('drive_sync_alerted') === '1';
    // No mark yet (first run) counts as healthy: only a mark that stopped moving raises the alert.
    $age = $ok ? time() - strtotime($ok) : 0;
    if ($age > 1800 && !$alerted) {
        $since = $ok ? (new DateTimeImmutable($ok))->setTimezone(new DateTimeZone('Europe/Brussels'))->format('d.m H:i') : 'never';
        co_watch_notify('Database sync stopped', "No successful Drive -> database sync since $since. Usually the site's Google sign-in expired: sign in to CO Web in a browser once.");
        $db->query("REPLACE INTO meta VALUES ('drive_sync_alerted', '1')");
    } elseif ($age <= 1800 && $alerted) {
        co_watch_notify('Database sync is back', 'Drive -> database sync works again.');
        $db->query("REPLACE INTO meta VALUES ('drive_sync_alerted', '0')");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' sync health: ' . $e->getMessage() . "\n");
}

// 2026-10-16: job queue watchdog (schema 5). One push per job: a database job still waiting after 15 min (no Bridge
// takes jobs, even through the Drive fallback) or a claimed job without a result 250 min after the claim (the longest
// job timeout is 240 min since 2026-10-19).
try {
    $db = co_db();
    $res = $db->query(
        "SELECT job_id, state, project_id, TIMESTAMPDIFF(MINUTE, COALESCE(claimed_utc, queued_utc), UTC_TIMESTAMP()) AS mins
           FROM jobs
          WHERE origin = 'db' AND result IS NULL AND cancel_requested = 0
            AND ((state = 'inbox' AND queued_utc < UTC_TIMESTAMP() - INTERVAL 15 MINUTE)
              OR (state = 'claimed' AND claimed_utc < UTC_TIMESTAMP() - INTERVAL 250 MINUTE))
            AND alerted_utc IS NULL AND COALESCE(queued_utc, claimed_utc) > UTC_TIMESTAMP() - INTERVAL 2 DAY
          LIMIT 20"
    );
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $j) {
        $st = $db->prepare('UPDATE jobs SET alerted_utc = UTC_TIMESTAMP(3) WHERE job_id = ? AND alerted_utc IS NULL');
        $st->execute([$j['job_id']]);
        if ($st->affected_rows !== 1) {
            continue;
        }
        $what = $j['state'] === 'inbox' ? "waiting in the queue for {$j['mins']} min" : "running for {$j['mins']} min without a result";
        co_watch_notify('Job stuck: ' . $j['project_id'], "Job {$j['job_id']} is $what. Check Bridge on the home PC.");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' queue watch: ' . $e->getMessage() . "\n");
}

// 2026-10-18: failing jobs — one push when >= 3 jobs failed in the last hour or > 20 % of >= 5 finished jobs failed
// in 24 h; the next push only after 6 h and a newer failure (meta.jobs_fail_alert).
require_once CO_AGENT_WEB . '/ops-lib.php';
try {
    $r = co_ops_alert_check(co_db());
    if ($r && $r['sent']) {
        fwrite(STDOUT, gmdate('c') . ' jobs alert: ' . json_encode($r) . "
");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' jobs alert: ' . $e->getMessage() . "
");
}

// 2026-10-16: automatic chat summaries (project memory), up to 3 chats per run.
require_once __DIR__ . '/summarize.php';
try {
    co_summarize_chats(3);
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' summaries: ' . $e->getMessage() . "\n");
}

// 2026-10-17: chat protocol (schema 8) — the secretary, the same model as the chat's agent, up to 2 chats per run.
require_once __DIR__ . '/protocol.php';
try {
    co_protocol_run(2, 200);
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' protocol: ' . $e->getMessage() . "\n");
}

// 2026-11-01: memory consolidation (schema 14) — at night (01-05 Brussels) one model pass per run over a project's
// memory.md; revise/drop proposals wait for the owner in CO Web (Project → Memory).
require_once __DIR__ . '/memory-consolidate.php';
try {
    $msg = co_memory_consolidate_cron();
    if ($msg !== null) {
        fwrite(STDOUT, gmdate('c') . ' ' . $msg . "
");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' memory consolidate: ' . $e->getMessage() . "
");
}

// 2026-11-01: tool library learning (schema 16) — tool usage from job_events every run; at night (02-05 Brussels) one
// model pass proposes new tools, SKILL.md fixes and archives; the owner decides in CO Web (Waiting for you).
require_once __DIR__ . '/tool-learn.php';
try {
    $msg = co_tool_learn_cron();
    if ($msg !== null) {
        fwrite(STDOUT, gmdate('c') . ' ' . $msg . "
");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' tool learn: ' . $e->getMessage() . "
");
}

// 2026-10-19: web device (schema 11) — expire calls past issuedUtc + timeoutMs, delete finished calls older than 10 min.
require_once CO_AGENT_WEB . '/webdev-lib.php';
try {
    $r = webdev_cleanup(co_db());
    if ($r['expired'] > 0) {
        fwrite(STDOUT, gmdate('c') . ' webdev: ' . json_encode($r) . "\n");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' webdev: ' . $e->getMessage() . "\n");
}

// 2026-11-11: device relay (schema 22) — expire overdue calls of the site agent to home devices, delete finished ones.
require_once CO_AGENT_WEB . '/relay-lib.php';
try {
    $r = relay_cleanup(co_db());
    if ($r['expired'] > 0) {
        fwrite(STDOUT, gmdate('c') . ' relay: ' . json_encode($r) . "\n");
    }
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' relay: ' . $e->getMessage() . "\n");
}

// 2026-10-28: chat files (schema 12) — one-line annotations (Haiku, up to 10 files) and the daily cleanup at 04:xx.
require_once __DIR__ . '/files-cron.php';
try {
    $msg = co_files_gc_daily();
    if ($msg !== null) {
        fwrite(STDOUT, gmdate('c') . ' ' . $msg . "\n");
    }
    co_files_annotate(10);
} catch (Throwable $e) {
    fwrite(STDOUT, gmdate('c') . ' files: ' . $e->getMessage() . "\n");
}
