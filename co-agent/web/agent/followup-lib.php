<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);

/*
 * Follow-ups (schema 13, 2026-10-30; CO Web 0.60.0, Bridge 1.7.0). A message the user sends while a job of the chat runs
 * does not start a parallel job. CO Web stores it here:
 * - deliver = true (Claude on a Bridge with the "followups" capability): job_claim offers it to Bridge, Bridge writes it
 *   into the stdin of the running Claude (--input-format stream-json; Claude reads it after its current step) and acks;
 *   the server then writes it into the chat as a user_message with followupOf = <jobId> (no jobId of its own).
 * - otherwise (Codex, the site agent) or when Bridge answers "late" (the turn already ended): state late; when the job is
 *   over, CO Web takes the late rows (followup_take, once) and sends them as one next message.
 */

const CO_FU_MAX_TEXT = 20000;
const CO_FU_REOFFER_SECONDS = 120;
const CO_FU_RESEND_SECONDS = 120;

function co_fu_now(): string
{
    return gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000);
}

/** Job row with the caller's role on it; exits 404/403. */
function co_fu_job(mysqli $db, string $jobId, callable $envAllowed, callable $chatRole, string $need): array
{
    $st = $db->prepare('SELECT job_id, env, chat_id, host_id, agent, state, result IS NOT NULL AS done FROM jobs WHERE job_id = ?');
    $st->execute([$jobId]);
    $job = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$job) {
        co_json(404, ['error' => 'no_job']);
    }
    $role = $envAllowed($job['env']) ? 'owner' : $chatRole((string)$job['chat_id']);
    if (!share_can($role, $need)) {
        co_json(403, ['error' => 'forbidden']);
    }
    return $job;
}

function co_fu_job_over(array $job): bool
{
    return (int)$job['done'] === 1 || in_array($job['state'], ['cancelled', 'rejected'], true);
}

// ---- user ops ----

function followup_add(mysqli $db, array $in, callable $envAllowed, callable $chatRole, string $author): array
{
    $jobId = db_id($in, 'jobId');
    $text = $in['text'] ?? null;
    if (!is_string($text) || trim($text) === '') {
        throw new InvalidArgumentException('text required');
    }
    if (mb_strlen($text) > CO_FU_MAX_TEXT) {
        throw new InvalidArgumentException('text too long');
    }
    $job = co_fu_job($db, $jobId, $envAllowed, $chatRole, 'write');
    if (co_fu_job_over($job)) {
        co_json(409, ['error' => 'job_done']);
    }
    $state = ($in['deliver'] ?? false) === true ? 'pending' : 'late';
    $st = $db->prepare('INSERT INTO job_followups (job_id, chat_id, host_id, author, text, state, created_utc) VALUES (?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3))');
    $st->execute([$jobId, $job['chat_id'], $job['host_id'], $author, $text, $state]);
    $id = (int)$db->insert_id;
    $st->close();
    return ['id' => $id, 'state' => $state];
}

/** Open follow-ups of a chat. state late also for pending/offered rows of a finished job. */
function followup_list(mysqli $db, array $in, callable $envAllowed, callable $chatRole): array
{
    $chatId = db_id($in, 'chatId');
    co_fu_repair_events($db, null, $chatId);
    $st = $db->prepare(
        "SELECT f.id, f.job_id, f.author, f.text, f.state, f.created_utc, j.env, j.state AS job_state, j.result IS NOT NULL AS done
           FROM job_followups f JOIN jobs j ON j.job_id = f.job_id
          WHERE f.chat_id = ?
            AND (f.state IN ('pending', 'offered', 'late')
                 OR (f.state = 'sending' AND f.offered_utc < UTC_TIMESTAMP(3) - INTERVAL " . CO_FU_RESEND_SECONDS . " SECOND))
          ORDER BY f.id LIMIT 50"
    );
    $st->execute([$chatId]);
    $rows = $st->get_result()->fetch_all(MYSQLI_ASSOC);
    $st->close();
    if (!$rows) {
        return ['followups' => []];
    }
    $role = $envAllowed($rows[0]['env']) ? 'owner' : $chatRole($chatId);
    if (!share_can($role, 'read')) {
        co_json(403, ['error' => 'forbidden']);
    }
    $out = [];
    foreach ($rows as $r) {
        $over = co_fu_job_over(['done' => $r['done'], 'state' => $r['job_state']]) || $r['state'] === 'sending';
        $out[] = [
            'id' => (int)$r['id'],
            'jobId' => $r['job_id'],
            'author' => $r['author'],
            'text' => $r['text'],
            'state' => $over ? 'late' : $r['state'],
            'jobOver' => $over,
            'createdUtc' => $r['created_utc'],
        ];
    }
    return ['followups' => $out];
}

/**
 * Takes late follow-ups of finished jobs for sending as the next message (state sending; one CO Web at a time). CO Web
 * confirms with followup_done after the message is queued; without it the rows are offered again after 120 s, so a tab
 * closed in between loses nothing (at worst the text is sent twice).
 */
function followup_take(mysqli $db, array $in, callable $envAllowed, callable $chatRole): array
{
    $chatId = db_id($in, 'chatId');
    $ids = $in['ids'] ?? null;
    if (!is_array($ids) || !array_is_list($ids) || count($ids) > 50) {
        throw new InvalidArgumentException('ids must be a list');
    }
    $taken = [];
    foreach ($ids as $id) {
        if (!is_int($id)) {
            continue;
        }
        $st = $db->prepare('SELECT f.job_id FROM job_followups f WHERE f.id = ? AND f.chat_id = ?');
        $st->execute([$id, $chatId]);
        $row = $st->get_result()->fetch_assoc();
        $st->close();
        if (!$row) {
            continue;
        }
        co_fu_job($db, (string)$row['job_id'], $envAllowed, $chatRole, 'write');
        $st = $db->prepare(
            "UPDATE job_followups f JOIN jobs j ON j.job_id = f.job_id
                SET f.state = 'sending', f.offered_utc = UTC_TIMESTAMP(3)
              WHERE f.id = ? AND f.chat_id = ?
                AND (j.result IS NOT NULL OR j.state IN ('cancelled', 'rejected') OR f.state = 'sending')
                AND (f.state = 'late' OR f.state = 'pending'
                     OR (f.state = 'offered' AND f.offered_utc < UTC_TIMESTAMP(3) - INTERVAL 30 SECOND)
                     OR (f.state = 'sending' AND f.offered_utc < UTC_TIMESTAMP(3) - INTERVAL " . CO_FU_RESEND_SECONDS . " SECOND))"
        );
        $st->execute([$id, $chatId]);
        if ($st->affected_rows === 1) {
            $taken[] = $id;
        }
        $st->close();
    }
    return ['taken' => $taken];
}

/** The taken follow-ups went out as a message. */
function followup_done(mysqli $db, array $in, callable $envAllowed, callable $chatRole): array
{
    $chatId = db_id($in, 'chatId');
    $ids = $in['ids'] ?? null;
    if (!is_array($ids) || !array_is_list($ids) || count($ids) > 50) {
        throw new InvalidArgumentException('ids must be a list');
    }
    $n = 0;
    foreach ($ids as $id) {
        if (!is_int($id)) {
            continue;
        }
        $st = $db->prepare('SELECT job_id FROM job_followups WHERE id = ? AND chat_id = ?');
        $st->execute([$id, $chatId]);
        $row = $st->get_result()->fetch_assoc();
        $st->close();
        if (!$row) {
            continue;
        }
        co_fu_job($db, (string)$row['job_id'], $envAllowed, $chatRole, 'write');
        $st = $db->prepare("UPDATE job_followups SET state = 'requeued', done_utc = UTC_TIMESTAMP(3) WHERE id = ? AND state = 'sending'");
        $st->execute([$id]);
        $n += $st->affected_rows;
        $st->close();
    }
    return ['done' => $n];
}

/** Withdraws a follow-up that has not reached the agent yet. */
function followup_cancel(mysqli $db, array $in, callable $envAllowed, callable $chatRole): array
{
    $chatId = db_id($in, 'chatId');
    $id = db_int($in, 'id', 0, 1, PHP_INT_MAX);
    $st = $db->prepare('SELECT job_id FROM job_followups WHERE id = ? AND chat_id = ?');
    $st->execute([$id, $chatId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row) {
        return ['cancelled' => false];
    }
    co_fu_job($db, (string)$row['job_id'], $envAllowed, $chatRole, 'write');
    $st = $db->prepare("UPDATE job_followups SET state = 'cancelled', done_utc = UTC_TIMESTAMP(3) WHERE id = ? AND state IN ('pending', 'late')");
    $st->execute([$id]);
    $ok = $st->affected_rows === 1;
    $st->close();
    return ['cancelled' => $ok];
}

// ---- host ops ----

/** job_claim (Bridge sends followups: true): pending follow-ups of this host's unfinished jobs, marked offered. */
function followup_offer(mysqli $db, string $host): array
{
    co_fu_repair_events($db, $host);
    $out = [];
    $db->begin_transaction();
    try {
        $st = $db->prepare(
            "SELECT f.id, f.job_id, f.text FROM job_followups f JOIN jobs j ON j.job_id = f.job_id
              WHERE j.host_id = ? AND j.result IS NULL
                AND (f.state = 'pending' OR (f.state = 'offered' AND f.offered_utc < UTC_TIMESTAMP(3) - INTERVAL " . CO_FU_REOFFER_SECONDS . " SECOND))
              ORDER BY f.id LIMIT 20
              FOR UPDATE SKIP LOCKED"
        );
        $st->execute([$host]);
        $res = $st->get_result();
        while ($r = $res->fetch_assoc()) {
            $out[] = ['id' => (int)$r['id'], 'jobId' => $r['job_id'], 'text' => $r['text']];
        }
        $res->free();
        $st->close();
        if ($out) {
            $up = $db->prepare("UPDATE job_followups SET state = 'offered', offered_utc = UTC_TIMESTAMP(3) WHERE id = ?");
            foreach ($out as $f) {
                $up->execute([$f['id']]);
            }
            $up->close();
        }
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return $out;
}

/** Bridge: outcome per offered follow-up — delivered | late | retry. */
function followup_ack(mysqli $db, array $in): array
{
    $host = db_id($in, 'host');
    $results = $in['results'] ?? null;
    if (!is_array($results) || !array_is_list($results) || count($results) > 50) {
        throw new InvalidArgumentException('results must be a list');
    }
    $done = 0;
    foreach ($results as $r) {
        if (!is_array($r) || !is_int($r['id'] ?? null) || !in_array($r['outcome'] ?? null, ['delivered', 'late', 'retry'], true)) {
            continue;
        }
        $state = ['delivered' => 'delivered', 'late' => 'late', 'retry' => 'pending'][$r['outcome']];
        $st = $db->prepare(
            "UPDATE job_followups f JOIN jobs j ON j.job_id = f.job_id
                SET f.state = ?, f.done_utc = IF(? = 'pending', NULL, UTC_TIMESTAMP(3))
              WHERE f.id = ? AND f.state = 'offered' AND j.host_id = ?"
        );
        $st->execute([$state, $state, $r['id'], $host]);
        $changed = $st->affected_rows === 1;
        $st->close();
        if ($changed) {
            $done++;
            if ($state === 'delivered') {
                co_fu_write_event($db, (int)$r['id']);
            }
        }
    }
    return ['ok' => true, 'updated' => $done];
}

/**
 * Delivered follow-ups whose chat message is not written yet. Tried again by every job_claim of the host and every
 * followup_list of the chat, without an age limit, until the event exists (event_id set).
 */
function co_fu_repair_events(mysqli $db, ?string $host, ?string $chatId = null): void
{
    $st = $db->prepare(
        "SELECT f.id FROM job_followups f JOIN jobs j ON j.job_id = f.job_id
          WHERE f.state = 'delivered' AND f.event_id IS NULL AND (? IS NULL OR j.host_id = ?) AND (? IS NULL OR f.chat_id = ?)
          ORDER BY f.id LIMIT 5"
    );
    $st->execute([$host, $host, $chatId, $chatId]);
    $ids = array_map(static fn(array $r): int => (int)$r[0], $st->get_result()->fetch_all(MYSQLI_NUM));
    $st->close();
    foreach ($ids as $id) {
        co_fu_write_event($db, $id);
    }
}

/**
 * The delivered follow-up as a chat message (user_message, followupOf = job; no jobId: it is not a job of its own).
 * eventId EVT-FU-<id>: a repeated write after a lost event_id update finds the event and only records it.
 */
function co_fu_write_event(mysqli $db, int $id): void
{
    try {
        $eventId = 'EVT-FU-' . $id;
        $st = $db->prepare('SELECT 1 FROM events WHERE event_id = ?');
        $st->execute([$eventId]);
        $exists = $st->get_result()->fetch_row() !== null;
        $st->close();
        if ($exists) {
            $st = $db->prepare('UPDATE job_followups SET event_id = ? WHERE id = ?');
            $st->execute([$eventId, $id]);
            $st->close();
            return;
        }
        $st = $db->prepare('SELECT job_id, chat_id, author, text FROM job_followups WHERE id = ?');
        $st->execute([$id]);
        $f = $st->get_result()->fetch_assoc();
        $st->close();
        if (!$f) {
            return;
        }
        $st = $db->prepare("SELECT data FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message' LIMIT 1");
        $st->execute([$f['chat_id'], $f['job_id']]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $u = $row ? json_decode((string)$row[0], true) : null;
        $u = is_array($u) ? $u : [];
        $st = $db->prepare('SELECT COALESCE(MAX(seq), 0) + 1 FROM events WHERE chat_id = ?');
        $st->execute([$f['chat_id']]);
        $seq = (int)$st->get_result()->fetch_row()[0];
        $st->close();
        $ev = [
            'schemaVersion' => 1,
            'eventId' => $eventId,
            'sequence' => $seq,
            'createdUtc' => co_fu_now(),
            'chatId' => $f['chat_id'],
            'branchId' => is_string($u['branchId'] ?? null) ? $u['branchId'] : 'BR-root',
            'type' => 'user_message',
            'followupOf' => $f['job_id'],
        ];
        if (isset($u['hostId'])) $ev['hostId'] = $u['hostId'];
        if (isset($u['agent'])) $ev['agent'] = $u['agent'];
        $ev['text'] = $f['text'];
        $author = $f['author'] ?? ($u['author'] ?? null);
        if ($author !== null) $ev['author'] = $author;
        $ev['attachments'] = [];
        $ev['returnFiles'] = [];
        $ev['contextMode'] = $u['contextMode'] ?? 'full';
        $ev['mode'] = $u['mode'] ?? 'cowork';
        $ev['model'] = $u['model'] ?? null;
        $ev['effort'] = $u['effort'] ?? null;
        $res = co_db_put(['events' => [$ev]]);
        if (empty($res['rejected'])) {
            $st = $db->prepare('UPDATE job_followups SET event_id = ? WHERE id = ?');
            $st->execute([$ev['eventId'], $id]);
            $st->close();
        }
    } catch (Throwable $e) {
        // The agent has the text already; a missing chat copy must not fail the ack (co_fu_repair_events retries).
        error_log('followup event ' . $id . ': ' . $e->getMessage());
    }
}
