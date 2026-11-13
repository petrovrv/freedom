<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Module corporate (Freedom F10): chats whose last answer stopped because the employer's gateway model was unavailable
// (assistant_message with gatewayBlocked, legacy wire corporateBlocked / engieBlocked of Bridge before F10) - listed by
// user op corporate_waiting and resumed in bulk by user op corporate_resume (CO Web finds both names in
// modules_list.gateways[].ops). Before F10 this was the core's corporate-resume-lib.php (CO Web 0.52.0, Bridge 1.5.1):
// op activity (corporateWaiting) and op corporate_resume / engie_resume of db.php. Loaded only while the module is
// enabled; it runs inside db.php (db_job_submit, co_db_put, co_finalize_job, share_*).
declare(strict_types=1);

const CO_CORP_RESUME_TEXT = 'The model of the employer is back online. Continue the interrupted task from where you stopped. If it was already finished, reply briefly that nothing is left to do.';
// Event fields of a blocked answer, the new name first (SQL of co_corp_waiting)
const CO_CORP_BLOCKED_SQL = "COALESCE(NULLIF(JSON_VALUE(le.data, '$.gatewayBlocked'), ''), NULLIF(JSON_VALUE(le.data, '$.corporateBlocked'), ''), JSON_VALUE(le.data, '$.engieBlocked'))";

/** Resume text of the module's gateway description (manifest provides.gateway.resumeText), else a neutral one. */
function co_corp_resume_text(string $module): string
{
    $t = co_gateway($module)['resumeText'];
    return $t !== '' ? $t : CO_CORP_RESUME_TEXT;
}

// New job id: JOB-YYYYmmddHHMMSS-xxxxxx
function co_corp_job_id(): string
{
    $r = '';
    foreach (str_split(random_bytes(6)) as $b) $r .= CO_FIN_ALPHABET[ord($b) % 32];
    return 'JOB-' . gmdate('YmdHis') . '-' . $r;
}

// Last user/assistant event if it is a blocked assistant answer, else null
function co_corp_last_blocked(mysqli $db, string $chatId): ?array
{
    $st = $db->prepare("SELECT type, data FROM events
        WHERE chat_id = ? AND type IN ('user_message','assistant_message')
        ORDER BY seq DESC LIMIT 1");
    $st->execute([$chatId]);
    $row = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$row || $row['type'] !== 'assistant_message') return null;

    $data = is_string($row['data'] ?? null) ? json_decode($row['data'], true) : null;
    if (!is_array($data)) return null;
    if (co_gateway_blocked($data) === '') return null;

    // Max seq over all events of the chat
    $st = $db->prepare('SELECT MAX(seq) AS max_seq FROM events WHERE chat_id = ?');
    $st->execute([$chatId]);
    $m = $st->get_result()->fetch_assoc();
    $st->close();

    return ['event' => $data, 'seq' => (int)($m['max_seq'] ?? 0)];
}

// Chats of the env's own projects waiting for the gateway model (last 7 days, no running job)
function co_corp_waiting(mysqli $db, string $env): array
{
    $sql = "SELECT c.chat_id, c.project_id, c.title,
            JSON_VALUE(le.data, '$.eventId') AS event_id,
            " . CO_CORP_BLOCKED_SQL . " AS reason,
            le.created_utc
        FROM chats c
        JOIN projects p ON p.project_id = c.project_id
        JOIN (SELECT e.chat_id, MAX(e.seq) AS max_seq
              FROM events e
              JOIN chats c2 ON c2.chat_id = e.chat_id
              JOIN projects p2 ON p2.project_id = c2.project_id
              WHERE p2.env = ?
                AND c2.updated_utc > UTC_TIMESTAMP() - INTERVAL 7 DAY
                AND e.type IN ('user_message','assistant_message')
              GROUP BY e.chat_id) m ON m.chat_id = c.chat_id
        JOIN events le ON le.chat_id = m.chat_id AND le.seq = m.max_seq
            AND le.type IN ('user_message','assistant_message')
        WHERE p.env = ?
          AND le.type = 'assistant_message'
          AND " . CO_CORP_BLOCKED_SQL . " <> ''
          AND NOT EXISTS (SELECT 1 FROM jobs j
              WHERE j.chat_id = c.chat_id
                AND j.state IN ('inbox','claimed')
                AND j.result IS NULL
                AND j.cancel_requested = 0)
        ORDER BY le.created_utc DESC
        LIMIT 50";
    $st = $db->prepare($sql);
    $st->execute([$env, $env]);
    $res = $st->get_result();
    $out = [];
    while ($r = $res->fetch_assoc()) {
        $out[] = [
            'chatId' => (string)$r['chat_id'],
            'projectId' => (string)$r['project_id'],
            'title' => $r['title'],
            'eventId' => $r['event_id'],
            'reason' => $r['reason'],
            'since' => co_db_iso($r['created_utc']),
        ];
    }
    $st->close();
    return $out;
}

// Per-chat advisory lock so concurrent tabs cannot queue duplicate resume jobs.
function co_corp_lock(mysqli $db, string $chatId): bool
{
    $st = $db->prepare('SELECT GET_LOCK(?, 5) AS l');
    $st->execute(['co_corp_resume_' . $chatId]);
    $r = $st->get_result()->fetch_assoc();
    $st->close();
    // GET_LOCK returns 1 (acquired), 0 (timeout) or NULL (error)
    return $r !== null && $r['l'] !== null && (int)$r['l'] === 1;
}

// Releases the per-chat lock; errors are ignored (the lock also ends with the connection).
function co_corp_unlock(mysqli $db, string $chatId): void
{
    try {
        $st = $db->prepare('SELECT RELEASE_LOCK(?) AS l');
        $st->execute(['co_corp_resume_' . $chatId]);
        $st->get_result()->free();
        $st->close();
    } catch (Throwable $e) {
    }
}

// Resume a batch of blocked chats of gateway $module; one failure does not stop the others
function co_corp_resume(mysqli $db, array $in, array $acc, callable $envAllowed, string $module): array
{
    $ids = $in['chatIds'] ?? null;
    if (!is_array($ids) || !array_is_list($ids) || count($ids) < 1 || count($ids) > 20) {
        throw new InvalidArgumentException('chatIds must be a list of 1..20 ids');
    }

    // Optional device
    $deviceId = $in['deviceId'] ?? null;
    if ($deviceId !== null && (!is_string($deviceId) || !preg_match('/^[A-Za-z0-9_-]{1,64}$/', $deviceId))) {
        throw new InvalidArgumentException('invalid deviceId');
    }

    // Optional resume text
    $text = $in['text'] ?? null;
    if ($text !== null) {
        if (!is_string($text) || mb_strlen($text) > 2000) {
            throw new InvalidArgumentException('text must be a string of up to 2000 chars');
        }
        if (trim($text) === '') $text = null;
    }
    $text ??= co_corp_resume_text($module);

    $myEnv = (string)($acc['myEnv'] ?? '');
    $admin = (bool)($acc['admin'] ?? false);

    $results = [];
    $seen = [];
    foreach ($ids as $cid) {
        $key = is_scalar($cid) ? (string)$cid : null;
        if ($key !== null) {
            if (isset($seen[$key])) continue; // skip duplicates
            $seen[$key] = true;
        }
        $row = ['chatId' => $key, 'jobId' => null, 'error' => null];

        try {
            // a. id + permission
            if (!is_string($cid) || !co_db_id_ok($cid)) {
                $row['error'] = 'invalid chatId';
                $results[] = $row;
                continue;
            }
            $chatId = $cid;
            $role = share_chat_role($db, $myEnv, $admin, $chatId);
            if (!share_can($role, 'write')) {
                $row['error'] = 'forbidden';
                $results[] = $row;
                continue;
            }

            // Steps b-h run under a per-chat lock
            if (!co_corp_lock($db, $chatId)) {
                $row['error'] = 'busy, try again';
                $results[] = $row;
                continue;
            }
            try {
                // b. must be waiting for the gateway model
                $blk = co_corp_last_blocked($db, $chatId);
                if ($blk === null) {
                    $row['error'] = 'not waiting for the corporate model';
                    $results[] = $row;
                    continue;
                }

                // c. no running job
                $st = $db->prepare("SELECT 1 AS x FROM jobs
                    WHERE chat_id = ? AND state IN ('inbox','claimed')
                      AND result IS NULL AND cancel_requested = 0 LIMIT 1");
                $st->execute([$chatId]);
                $running = $st->get_result()->fetch_assoc();
                $st->close();
                if ($running) {
                    $row['error'] = 'a job of this chat is running';
                    $results[] = $row;
                    continue;
                }

                // d. original request + original user message
                $a = $blk['event'];
                $origJobId = $a['jobId'] ?? null;
                $orig = null;
                if (is_string($origJobId) && $origJobId !== '') {
                    $st = $db->prepare('SELECT request FROM jobs WHERE job_id = ?');
                    $st->execute([$origJobId]);
                    $j = $st->get_result()->fetch_assoc();
                    $st->close();
                    if ($j && is_string($j['request'] ?? null)) {
                        $orig = json_decode($j['request'], true);
                    }
                }
                if (!is_array($orig)) {
                    $row['error'] = 'original job not found';
                    $results[] = $row;
                    continue;
                }

                $st = $db->prepare("SELECT data FROM events
                    WHERE chat_id = ? AND job_id = ? AND type = 'user_message' LIMIT 1");
                $st->execute([$chatId, $origJobId]);
                $ue = $st->get_result()->fetch_assoc();
                $st->close();
                $u = ($ue && is_string($ue['data'] ?? null)) ? json_decode($ue['data'], true) : null;
                if (!is_array($u)) {
                    $row['error'] = 'original message not found';
                    $results[] = $row;
                    continue;
                }

                // A re-run without a session would lose the original attachments (Drive package): resend by hand
                $sid = $blk['event']['sessionId'] ?? null;
                if ((!is_string($sid) || $sid === '') && !empty($orig['attachments'])) {
                    $row['error'] = 'the message had attachments: resend it by hand';
                    $results[] = $row;
                    continue;
                }

                // e. build the new request
                $req = $orig;
                $req['jobId'] = co_corp_job_id();
                $req['attachments'] = [];
                $sessionId = $a['sessionId'] ?? null;
                $resumeSession = is_string($sessionId) && $sessionId !== '';
                if ($resumeSession) {
                    $req['conversation'] = ['operation' => 'resume', 'sessionId' => $sessionId];
                    $req['instruction'] = $text;
                } else {
                    $origInstr = is_string($orig['instruction'] ?? null) ? $orig['instruction'] : '';
                    $req['instruction'] = $text . "\n\n" . $origInstr;
                }

                // Re-allow the gateway tools (those with a kind: its model and fetch tools, as before F10)
                if (array_key_exists('deviceToolsDeny', $req)) {
                    $corpTools = array_merge(co_gateway_tools('gateway-llm', $module), co_gateway_tools('gateway-fetch', $module));
                    $deny = is_array($req['deviceToolsDeny'])
                        ? array_values(array_filter($req['deviceToolsDeny'], fn($t) => !in_array($t, $corpTools, true)))
                        : [];
                    if ($deny) $req['deviceToolsDeny'] = $deny;
                    else unset($req['deviceToolsDeny']);
                }

                // Device routing (not for shared chats): the online corporate device goes first (the corporate agent uses devices[0])
                if ($deviceId !== null && !share_restricted($db, (string)($req['projectId'] ?? ''), $chatId)) { // R2: subtasks of a shared chat too
                    $devs = $req['devices'] ?? null;
                    $wasEmpty = !is_array($devs) || !$devs;
                    $list = [$deviceId];
                    if (!$wasEmpty) {
                        foreach ($devs as $d) {
                            if (count($list) >= 4) {
                                break;
                            }
                            if (!is_string($d) || $d === '' || in_array($d, $list, true)) {
                                continue;
                            }
                            $list[] = $d;
                        }
                    }
                    $req['devices'] = $list;
                    if ($wasEmpty) {
                        $req['protocolMinor'] = 2;
                    }
                }
                unset($req['task']);

                // f. user_message event (written after the job is queued)
                $ev = [
                    'schemaVersion' => 1,
                    'eventId' => co_fin_event_id(),
                    'sequence' => $blk['seq'] + 1,
                    'createdUtc' => gmdate('Y-m-d\TH:i:s') . sprintf('.%03dZ', (int)(microtime(true) * 1000) % 1000),
                    'chatId' => $chatId,
                    'branchId' => $u['branchId'] ?? 'BR-root',
                    'type' => 'user_message',
                    'jobId' => $req['jobId'],
                    'hostId' => $req['hostId'] ?? null,
                    'agent' => $req['agent'] ?? null,
                    'text' => $text,
                ];
                if (($u['author'] ?? null) !== null) $ev['author'] = $u['author'];
                $ev['attachments'] = [];
                $ev['returnFiles'] = [];
                $ev['conversation'] = $req['conversation'] ?? null;
                $ev['contextMode'] = $u['contextMode'] ?? 'full';
                if ($resumeSession) $ev['contextPack'] = 'resume';
                $ev['mode'] = $u['mode'] ?? $req['mode'] ?? 'cowork';
                $ev['model'] = $u['model'] ?? null;
                $ev['effort'] = $u['effort'] ?? null;
                $ev['queue'] = 'db';
                $ev['gatewayResume'] = true;
                $ev['corporateResume'] = true; // legacy wire: CO Web before F10
                $ev['engieResume'] = true; // legacy wire: CO Web before 5b
                // g. queue the job (a refusal may end the request: nothing is written before it)
                db_job_submit(['request' => $req], $envAllowed, $acc);

                // h. the message; a failed write cancels the queued job (its answer could not be finalized)
                $writeErr = null;
                try {
                    $putRes = co_db_put(['events' => [$ev]]);
                    if (!empty($putRes['rejected'])) {
                        $writeErr = new RuntimeException('user_message event rejected: ' . json_encode($putRes['rejected']));
                    }
                } catch (Throwable $we) {
                    $writeErr = $we;
                }
                if ($writeErr !== null) {
                    try {
                        $st = $db->prepare('UPDATE jobs SET cancel_requested = 1 WHERE job_id = ? AND result IS NULL');
                        $st->execute([$req['jobId']]);
                        $st->close();
                    } catch (Throwable $ce) {
                        // cancellation failure must not mask the original write error
                    }
                    throw new RuntimeException('could not write the message; the job was cancelled', 0, $writeErr);
                }

                // i. a job that finished before its message existed was not finalized: do it now
                $st = $db->prepare('SELECT request, result FROM jobs WHERE job_id = ? AND result IS NOT NULL');
                $st->execute([$req['jobId']]);
                $done = $st->get_result()->fetch_assoc();
                $st->close();
                if ($done) {
                    $dReq = json_decode((string)$done['request'], true);
                    $dRes = json_decode((string)$done['result'], true);
                    if (is_array($dReq) && is_array($dRes)) {
                        co_finalize_job($dReq, $dRes);
                    }
                }

                // j. chat date (chats.data.updatedUtc drives the CO Web lists)
                $st = $db->prepare('SELECT data FROM chats WHERE chat_id = ?');
                $st->execute([$chatId]);
                $cr = $st->get_result()->fetch_assoc();
                $st->close();
                $cd = $cr ? json_decode((string)$cr['data'], true) : null;
                if (is_array($cd)) {
                    $cd['updatedUtc'] = $ev['createdUtc'];
                    co_db_put(['chats' => [$cd]]);
                }

                $row['jobId'] = $req['jobId'];
            } finally {
                co_corp_unlock($db, $chatId);
            }
        } catch (Throwable $e) {
            $row['error'] = $e->getMessage() !== '' ? $e->getMessage() : get_class($e);
        }
        $results[] = $row;
    }

    return ['results' => $results];
}
