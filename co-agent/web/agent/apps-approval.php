<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// The site stores approval metadata only. Private proposals/results stay on the worker.
declare(strict_types=1);

function co_app_approval_envelope(array $in): ?array
{
    if (array_key_exists('args', $in) || array_key_exists('dispatch', $in)) return null;
    $e = $in['envelope'] ?? null;
    if (!is_array($e) || array_diff(array_keys($e), ['id', 'pk', 'iv', 'ct'])) return null;
    foreach (['id', 'pk', 'iv', 'ct'] as $k) if (!is_string($e[$k] ?? null)) return null;
    return $e;
}

function co_app_approval_transport(array $c, array $q, array $e, string $phase, bool $person = false): array
{
    $d = ['kind' => 'app-approval-v1', 'actor' => $person || !in_array($phase, ['stage', 'result'], true) ? 'person' : 'agent',
        'phase' => $phase, 'env' => $c['ctx']['myEnv'], 'module' => $q['app'], 'action' => $q['action'],
        'version' => $q['version'], 'outward' => true, 'requestId' => $q['id'], 'revision' => $q['revision'],
        'expires' => $q['expires'], 'schema' => $c['a']['args'],
        'envelopeHash' => hash('sha256', implode("\n", array_map(static fn($k) => $e[$k], ['id', 'pk', 'iv', 'ct'])))];
    if ($phase === 'run') $d['grantExpires'] = time() + 45;
    return ($c['lib']['appCall'])($c['ctx'], $q['app'], $e, $d);
}

function co_app_approval_person_context(mysqli $db, string $env, bool $owner, string $email, array $q): array
{
    [$m, $actions] = co_app_modules($db, ['env' => $env])[$q['app']] ?? [null, []];
    $a = $actions[$q['action']] ?? null;
    if (!$m || !$a || !$a['outward'] || ($a['via'] ?? '') !== $q['via'] || $m['version'] !== $q['version']) return ['error' => 'app_changed'];
    $relay = co_modules($db)[$q['via']] ?? null;
    if (!$relay || !$relay['enabled'] || !$relay['standard'] || !co_module_for_env($relay, $env)
        || co_module_person_mode($db, $relay, $env) === 'off') return ['error' => 'relay_disabled'];
    $lib = co_module_load($relay)['relay'] ?? [];
    if (!is_callable($lib['status'] ?? null) || !is_callable($lib['appCall'] ?? null)) return ['error' => 'relay_upgrade_required'];
    $ctx = co_module_ctx($db, $relay, $env, $owner, $email);
    return compact('m', 'a', 'lib', 'ctx');
}

function co_app_relay_propose(mysqli $db, array $in): array
{
    $c = co_app_relay_context($db, $in, true);
    if (isset($c['error'])) return ['ok' => false] + $c;
    if (!$c['a']['outward'] || ($in['version'] ?? null) !== $c['m']['version']) return ['ok' => false, 'error' => 'app_changed'];
    $e = co_app_approval_envelope($in);
    if (!$e) return ['ok' => false, 'error' => 'bad_envelope'];
    $status = ($c['lib']['status'])($c['ctx'], $c['m']['id']);
    if (($status['status']['relayApprovalProtocol'] ?? null) !== 1) return ['ok' => false, 'error' => 'relay_upgrade_required'];
    $job = $c['job']; $m = $c['m']; $a = $c['a']; $face = co_app_face($m);
    $rows = share_q($db, 'SELECT title, project_id FROM chats WHERE chat_id = ?', [$job['chatId']]);
    $chat = $rows[0] ?? [];
    $q = ['id' => bin2hex(random_bytes(8)), 'revision' => bin2hex(random_bytes(16)), 'state' => 'staging',
        'app' => $m['id'], 'action' => $a['op'], 'version' => $m['version'], 'via' => $a['via'],
        'title' => $face['title'], 'icon' => $face['icon'], 'description' => $a['description'],
        'jobId' => $job['jobId'], 'chatId' => $job['chatId'], 'chatTitle' => (string)($chat['title'] ?? ''), 'projectId' => (string)($chat['project_id'] ?? ''),
        'created' => time(), 'expires' => time() + CO_APP_REQ_TTL_MIN * 60];
    $q['etag'] = hash('sha256', json_encode($q, JSON_THROW_ON_ERROR));
    $saved = co_app_requests_locked($db, $job['env'], static function (array &$list) use ($q): bool {
        if (count(array_filter($list, static fn($x) => in_array($x['state'] ?? '', ['pending', 'staging'], true))) >= CO_APP_REQ_PENDING_MAX) return false;
        $list[] = $q; return true;
    });
    if (!$saved) return ['ok' => false, 'error' => 'too_many_pending'];
    $r = co_app_approval_transport($c, $q, $e, 'stage');
    co_app_requests_locked($db, $job['env'], static function (array &$list) use ($q, $r): void {
        foreach ($list as &$x) if ($x['id'] === $q['id']) $x['state'] = isset($r['error']) ? 'failed' : 'pending';
    });
    if (isset($r['error'])) return ['ok' => false, 'error' => $r['error']];
    co_app_request_push($job['env'], $q['id'], $q['title'], $q['description'], $q['chatTitle']);
    // Transport success is not worker acceptance: tools decrypt the acknowledgement, and Run always requires preview.
    return ['ok' => true, 'pending' => true, 'envelope' => $r, 'requestId' => $q['id'], 'revision' => $q['revision']];
}

function co_app_request_relay(mysqli $db, string $env, bool $owner, string $email, array $in): array
{
    $id = $in['id'] ?? ''; $phase = $in['phase'] ?? '';
    if (!is_string($id) || !in_array($phase, ['status', 'preview', 'run', 'result'], true)) return ['ok' => false, 'error' => 'bad_request'];
    $q = null;
    foreach (co_app_requests($db, $env) as $x) if ($x['id'] === $id) $q = $x;
    if (!$q || !isset($q['via'])) return ['ok' => false, 'error' => 'not_found'];
    if (!in_array($phase, ['status', 'result'], true) && $q['state'] !== 'pending') return ['ok' => false, 'error' => 'already_decided'];
    if (!is_string($in['etag'] ?? null) || !hash_equals($q['etag'], $in['etag'])) return ['ok' => false, 'error' => 'etag_mismatch'];
    $c = co_app_approval_person_context($db, $env, $owner, $email, $q);
    if (isset($c['error'])) return ['ok' => false] + $c;
    if ($phase === 'status') {
        $s = ($c['lib']['status'])($c['ctx'], $q['app']);
        if (empty($s['online']) || empty($s['served']) || ($s['status']['relayApprovalProtocol'] ?? null) !== 1) return ['ok' => false, 'error' => 'relay_unavailable'];
        return ['ok' => true, 'env' => $env, 'pub' => $s['pub'], 'requestId' => $id, 'revision' => $q['revision'], 'version' => $q['version'], 'app' => $q['app'], 'action' => $q['action']];
    }
    $e = co_app_approval_envelope($in);
    if (!$e) return ['ok' => false, 'error' => 'bad_envelope'];
    if ($phase === 'run') {
        $claimed = co_app_requests_locked($db, $env, static function (array &$list) use ($q, $email): bool {
            foreach ($list as &$x) if ($x['id'] === $q['id'] && $x['state'] === 'pending' && $x['etag'] === $q['etag']) {
                $x['state'] = 'running'; $x['decided'] = time(); $x['by'] = $email; return true;
            }
            return false;
        });
        if (!$claimed) return ['ok' => false, 'error' => 'already_decided'];
    }
    $r = co_app_approval_transport($c, $q, $e, $phase, true);
    // Never label ciphertext transport as execution success or retry a timed out Run.
    if ($phase === 'run') co_app_event_put($db, $env, $q['app'], $q['action']);
    return isset($r['error']) ? ['ok' => false, 'error' => $r['error']] : ['ok' => true, 'envelope' => $r];
}

function co_app_relay_result(mysqli $db, array $in): array
{
    $job = co_app_job($db, $in);
    if ($job['restricted'] || $job['readOnly']) return ['ok' => false, 'error' => 'not_allowed'];
    foreach (co_app_requests($db, $job['env']) as $q) {
        if ($q['id'] !== ($in['requestId'] ?? null) || !isset($q['via'])) continue;
        $c = co_app_approval_person_context($db, $job['env'], $job['owner'], $job['email'], $q);
        if (isset($c['error'])) return ['ok' => false] + $c;
        $e = co_app_approval_envelope($in);
        if (!$e) return ['ok' => false, 'error' => 'bad_envelope'];
        $r = co_app_approval_transport($c, $q, $e, 'result');
        return isset($r['error']) ? ['ok' => false, 'error' => $r['error']] : ['ok' => true, 'envelope' => $r];
    }
    return ['ok' => false, 'error' => 'not_found'];
}
