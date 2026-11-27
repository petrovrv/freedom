<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.133.0 (plan step 10i): delete a chat message and "rewrite from here". Soft: the hidden events keep their rows and
// get data.deleted = {utc, by, batch, mode, at[, protocol]}; CO Web leaves them out of the chat, of the agent's context and
// starts a new agent session after the newest deletion (no resume of a CLI session that saw them). Restore within
// CO_MSG_RESTORE_DAYS days puts them back (with their protocol entries). co_db_put keeps data.deleted when a client writes
// an older copy of the event without it.
declare(strict_types=1);

const CO_MSG_RESTORE_DAYS = 30;

/** Seq and type of the event at $seq of a chat (the first one when an old chat has two), or null. */
function msgedit_event_at(mysqli $db, string $chatId, int $seq): ?array
{
    $rows = share_q($db, "SELECT event_id, seq, type, job_id, data FROM events WHERE chat_id = ? AND seq = ?
                          AND JSON_EXTRACT(data, '$.deleted') IS NULL ORDER BY event_id LIMIT 1", [$chatId, $seq]);
    return $rows[0] ?? null;
}

/**
 * Events a delete or a rewrite hides (not yet hidden ones): 'pair' = the user message at $seq with every event of its
 * job (its answer), 'answer' = the answer at $seq (assistant_message of its job), 'rewrite' = every event with seq >= $seq.
 */
function msgedit_targets(mysqli $db, string $chatId, int $seq, string $mode): array
{
    $live = "JSON_EXTRACT(data, '$.deleted') IS NULL";
    if ($mode === 'rewrite') {
        return share_q($db, "SELECT event_id, seq, type, job_id, data FROM events WHERE chat_id = ? AND seq >= ? AND $live
                             ORDER BY seq, event_id", [$chatId, $seq]);
    }
    $at = msgedit_event_at($db, $chatId, $seq);
    if (!$at) return [];
    $want = $mode === 'pair' ? 'user_message' : 'assistant_message';
    if ($at['type'] !== $want) return [];
    if ($at['job_id'] === null || $at['job_id'] === '') return [$at];
    $types = $mode === 'pair' ? '' : " AND type = 'assistant_message'";
    return share_q($db, "SELECT event_id, seq, type, job_id, data FROM events WHERE chat_id = ? AND job_id = ? AND $live$types
                         ORDER BY seq, event_id", [$chatId, (string)$at['job_id']]);
}

/**
 * Rule 4: the chat's owner hides anything; an editor (shared chat, family member in another's chat) only their own
 * messages and the answers to them - a user message whose writer is the signed-in account or whose job they submitted.
 * Name of the first event that is not theirs, or null when every target is allowed.
 */
function msgedit_foreign(mysqli $db, array $targets, string $chatId, string $role, string $email, string $myEnv): ?string
{
    if ($role === 'owner') return null;
    $email = strtolower($email);
    $mine = static function (?string $writer, ?string $jobId) use ($db, $email, $myEnv): bool {
        if ($writer !== null && $writer !== '' && strtolower($writer) === $email) return true;
        if ($jobId === null || $jobId === '') return false;
        $j = share_q($db, 'SELECT actor_env FROM jobs WHERE job_id = ?', [$jobId]);
        return $j && (string)$j[0]['actor_env'] === $myEnv;
    };
    foreach ($targets as $t) {
        $jobId = $t['job_id'] === null ? null : (string)$t['job_id'];
        if ($t['type'] === 'user_message') {
            $w = share_q($db, 'SELECT writer FROM events WHERE event_id = ?', [(string)$t['event_id']]);
            if (!$mine($w[0]['writer'] ?? null, $jobId)) return '#' . $t['seq'];
        } elseif ($t['type'] === 'assistant_message') {
            if ($jobId === null) return '#' . $t['seq'];
            $u = share_q($db, "SELECT writer FROM events WHERE chat_id = ? AND job_id = ? AND type = 'user_message' LIMIT 1", [$chatId, $jobId]);
            if (!$mine($u[0]['writer'] ?? null, $jobId)) return '#' . $t['seq'];
        }
    }
    return null;
}

/**
 * Rule 4 (who may delete in a chat at all): the workbench owner anywhere (with the role checks of msgedit_hide); another
 * person only in chats of their own environment and in the family room (there msgedit_foreign limits them to their own
 * messages), never in someone else's chat shared with them.
 */
function msgedit_env_allows(mysqli $db, string $chatId, bool $admin, string $myEnv): bool
{
    if ($admin) return true;
    $r = share_q($db, 'SELECT p.env, c.data FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?', [$chatId]);
    if (!$r) return false;
    if ((string)$r[0]['env'] === $myEnv) return true;
    $data = json_decode((string)$r[0]['data'], true);
    return is_array($data) && ($data['family'] ?? false) === true;
}

/** Stops jobs of the hidden messages: a queued one never starts, a running one is stopped by its executor. Ids stopped. */
function msgedit_stop_jobs(mysqli $db, string $chatId, array $jobIds): array
{
    $stopped = [];
    foreach (array_unique($jobIds) as $jobId) {
        $j = share_q($db, "SELECT state, host_id, origin, fallback_utc FROM jobs WHERE job_id = ? AND chat_id = ?
                           AND state IN ('inbox', 'claimed') AND result IS NULL", [$jobId, $chatId]);
        if (!$j) continue;
        share_exec($db, "UPDATE jobs SET cancel_requested = 1, state = IF(state = 'inbox', 'cancelled', state) WHERE job_id = ?", [$jobId]);
        $stopped[] = ['jobId' => $jobId, 'host' => (string)$j[0]['host_id'], 'drive' => $j[0]['state'] === 'inbox'
            && ($j[0]['origin'] !== 'db' || $j[0]['fallback_utc'] !== null)];
    }
    return $stopped;
}

/** Rule 3: memory candidates and dossier rows the secretary made from the hidden messages (shown to the person). */
function msgedit_traces(mysqli $db, string $chatId, array $seqs): array
{
    if (!$seqs) return ['memory' => [], 'dossier' => []];
    $in = implode(',', array_fill(0, count($seqs), '?'));
    $mem = share_q($db, "SELECT id, project_id, msg_seq, tag, text, final_text, status FROM memory_candidates
                         WHERE chat_id = ? AND msg_seq IN ($in) AND status IN ('pending', 'accepted') ORDER BY id", [$chatId, ...$seqs]);
    $dos = share_q($db, "SELECT id, env, section, subject, text, source_msg FROM dossier
                         WHERE source_chat = ? AND source_msg IN ($in) AND status = 'active' ORDER BY id", [$chatId, ...$seqs]);
    return [
        'memory' => array_map(static fn(array $r): array => ['id' => (int)$r['id'], 'projectId' => (string)$r['project_id'],
            'msg' => (int)$r['msg_seq'], 'tag' => (string)$r['tag'], 'text' => (string)($r['final_text'] ?? $r['text']),
            'status' => (string)$r['status']], $mem),
        'dossier' => array_map(static fn(array $r): array => ['id' => (int)$r['id'], 'env' => (string)$r['env'],
            'section' => (string)$r['section'], 'subject' => (string)$r['subject'], 'text' => (string)$r['text'],
            'msg' => (int)$r['source_msg']], $dos),
    ];
}

/**
 * msg_delete / msg_rewrite: hide the targets under the chat row lock (the lock share_job_insert_untrashed takes, so no
 * job of the chat is queued meanwhile), stop their jobs, take their protocol entries out (kept in the hidden event for a
 * restore), and for a rewrite reopen entries the hidden messages closed and move the secretary back to $seq - 1.
 * Errors: not_found | forbidden | not_yours (with 'event').
 */
function msgedit_hide(mysqli $db, string $chatId, int $seq, string $mode, string $role, string $email, string $myEnv): array
{
    if (!in_array($mode, ['pair', 'answer', 'rewrite'], true) || $seq < 1) throw new InvalidArgumentException('mode/seq');
    if (!share_can($role, 'write')) return ['error' => 'forbidden'];
    $db->begin_transaction();
    try {
        if (share_chat_trashed($db, $chatId, true) === null) {
            $db->rollback();
            return ['error' => 'not_found'];
        }
        if ($mode === 'rewrite') {
            $at = msgedit_event_at($db, $chatId, $seq);
            if (!$at || $at['type'] !== 'user_message') {
                $db->rollback();
                return ['error' => 'not_found'];
            }
        }
        $targets = msgedit_targets($db, $chatId, $seq, $mode);
        if (!$targets) {
            $db->rollback();
            return ['error' => 'not_found'];
        }
        if (($who = msgedit_foreign($db, $targets, $chatId, $role, $email, $myEnv)) !== null) {
            $db->rollback();
            return ['error' => 'not_yours', 'event' => $who];
        }
        $seqs = array_values(array_unique(array_map(static fn(array $t): int => (int)$t['seq'], $targets)));
        $in = implode(',', array_fill(0, count($seqs), '?'));
        $proto = share_q($db, "SELECT entry_no, section, text, quote, msg_seq, status, replaced_by, closed_seq, author, model, created_utc
                               FROM protocol WHERE chat_id = ? AND msg_seq IN ($in) ORDER BY entry_no", [$chatId, ...$seqs]);
        $bySeq = [];
        foreach ($proto as $p) $bySeq[(int)$p['msg_seq']][] = $p;
        $utc = gmdate('Y-m-d\TH:i:s.v\Z');
        $batch = 'DEL-' . bin2hex(random_bytes(6));
        $hidden = [];
        $answers = [];
        foreach ($targets as $t) {
            $data = json_decode((string)$t['data'], true);
            if (!is_array($data)) continue;
            $mark = ['utc' => $utc, 'by' => strtolower($email), 'batch' => $batch, 'mode' => $mode, 'at' => $seq];
            if (isset($bySeq[(int)$t['seq']])) {
                $mark['protocol'] = $bySeq[(int)$t['seq']];
                unset($bySeq[(int)$t['seq']]); // one copy per seq
            }
            $data['deleted'] = $mark;
            $json = co_db_json($data);
            share_exec($db, 'UPDATE events SET data = ?, sha = ?, synced_utc = UTC_TIMESTAMP(3) WHERE event_id = ?',
                [$json, hash('sha256', $json), (string)$t['event_id']]);
            $hidden[] = (string)$t['event_id'];
            if ($t['type'] === 'assistant_message' && $t['job_id'] !== null) $answers[] = (string)$t['job_id'];
        }
        if ($proto) share_exec($db, "DELETE FROM protocol WHERE chat_id = ? AND msg_seq IN ($in)", [$chatId, ...$seqs]);
        if ($mode === 'rewrite') {
            // entries the forgotten messages closed or superseded are open again; the secretary reads on from $seq - 1
            share_exec($db, "UPDATE protocol SET status = 'active', replaced_by = NULL, closed_seq = NULL
                             WHERE chat_id = ? AND closed_seq >= ?", [$chatId, $seq]);
            share_exec($db, 'UPDATE chats SET protocol_seq = LEAST(COALESCE(protocol_seq, 0), ?) WHERE chat_id = ?', [$seq - 1, $chatId]);
        }
        $jobIds = array_values(array_filter(array_map(static fn(array $t): ?string => $t['job_id'] === null ? null : (string)$t['job_id'], $targets)));
        $stopped = msgedit_stop_jobs($db, $chatId, $jobIds);
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    foreach ($stopped as $s) {
        if ($s['drive'] && function_exists('co_queue_drop_drive_copy')) {
            try {
                co_queue_drop_drive_copy($s['host'], $s['jobId']);
            } catch (Throwable $e) {
                error_log('msgedit: Drive copy of ' . $s['jobId'] . ' not trashed: ' . $e->getMessage());
            }
        }
    }
    return ['batch' => $batch, 'utc' => $utc, 'hidden' => $hidden, 'seqs' => $seqs, 'protocolRemoved' => count($proto),
        'stopped' => array_column($stopped, 'jobId'), 'answers' => array_values(array_unique($answers)),
        'traces' => msgedit_traces($db, $chatId, $seqs)];
}

/**
 * msg_restore: the events of one deletion batch come back with their protocol entries (an entry number taken meanwhile
 * gets the next free one), within CO_MSG_RESTORE_DAYS days; the chat's owner or the person who deleted them.
 */
function msgedit_restore(mysqli $db, string $chatId, string $batch, string $role, string $email): array
{
    if (!preg_match('/^DEL-[0-9a-f]{12}$/', $batch)) throw new InvalidArgumentException('batch');
    if (!share_can($role, 'write')) return ['error' => 'forbidden'];
    $db->begin_transaction();
    try {
        if (share_chat_trashed($db, $chatId, true) === null) {
            $db->rollback();
            return ['error' => 'not_found'];
        }
        $rows = share_q($db, "SELECT event_id, data FROM events WHERE chat_id = ? AND JSON_UNQUOTE(JSON_EXTRACT(data, '$.deleted.batch')) = ?",
            [$chatId, $batch]);
        if (!$rows) {
            $db->rollback();
            return ['error' => 'not_found'];
        }
        $first = json_decode((string)$rows[0]['data'], true)['deleted'] ?? [];
        if ($role !== 'owner' && strtolower((string)($first['by'] ?? '')) !== strtolower($email)) {
            $db->rollback();
            return ['error' => 'forbidden'];
        }
        if (strtotime((string)($first['utc'] ?? '')) < time() - CO_MSG_RESTORE_DAYS * 86400) {
            $db->rollback();
            return ['error' => 'expired'];
        }
        $restored = 0;
        foreach ($rows as $r) {
            $data = json_decode((string)$r['data'], true);
            $proto = is_array($data['deleted']['protocol'] ?? null) ? $data['deleted']['protocol'] : [];
            unset($data['deleted']);
            $json = co_db_json($data);
            share_exec($db, 'UPDATE events SET data = ?, sha = ?, synced_utc = UTC_TIMESTAMP(3) WHERE event_id = ?',
                [$json, hash('sha256', $json), (string)$r['event_id']]);
            foreach ($proto as $p) {
                $no = (int)$p['entry_no'];
                if (share_q($db, 'SELECT 1 FROM protocol WHERE chat_id = ? AND entry_no = ?', [$chatId, $no])) {
                    $no = (int)share_q($db, 'SELECT COALESCE(MAX(entry_no), 0) + 1 AS n FROM protocol WHERE chat_id = ?', [$chatId])[0]['n'];
                }
                share_exec($db, 'INSERT INTO protocol (chat_id, entry_no, section, text, quote, msg_seq, status, replaced_by, closed_seq, author, model, created_utc, updated_utc)
                                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3))',
                    [$chatId, $no, $p['section'], $p['text'], $p['quote'], (int)$p['msg_seq'], $p['status'], $p['replaced_by'],
                        $p['closed_seq'], $p['author'], $p['model'], $p['created_utc']]);
            }
            $restored++;
        }
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return ['restored' => $restored];
}
