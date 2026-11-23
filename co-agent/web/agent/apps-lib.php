<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// apps-lib.php (Freedom F14b-1, SPEC §3.6a item 5): actions of apps for the agent. A module describes some of its user
// ops (server.ops.user) as actions (manifest provides.actions [{op, description, args, outward}]); an agent job calls
// them through the core tool aiwb-apps (db.php host ops app_list / app_action with the job id) with the rights of the
// person of the job's environment, exactly as that person's page would call the op.
//   - who: the env of the job's row in `jobs` (never the caller's word); the job must still run
//   - read-only chat (permissions.files read): app_list only, no action (an action may change the app's data)
//   - where: not for a shared chat, a chat below one or a job queued restricted (relay_job_restricted, the rule of
//     home devices); a module switched off, or off for this person (personal mode off), has no actions
//   - outward (writes to the world outside the workbench: mail, calendars of others, devices of the house): never run by
//     the agent. F14b-2: the call becomes a request, a card in "Waiting for you" of the job's environment (what, which
//     app, the arguments in words, from which chat); only the person of that env runs it (Run / Decline, user op
//     app_request_decide) with their own rights, as their click in the app would. One-time: the card lives
//     CO_APP_REQ_TTL_MIN, the first decision moves it out of 'pending' under a row lock (a second click is refused),
//     and the click carries the card's etag (app, action, module version, arguments): a changed request is refused.
//     The agent gets {pending, requestId} and asks app_action_status for the outcome (also from a later job of the env).
//   - after an action the open pages of the app get the ext/1 event "refresh" (app_events, polled by CO Web)
declare(strict_types=1);

require_once __DIR__ . '/modules-lib.php';
require_once __DIR__ . '/relay-lib.php';

const CO_APP_ARG_TYPES = ['string', 'integer', 'number', 'boolean'];
const CO_APP_EVENTS_KEEP = 30;
const CO_APP_JOB_DONE = ['completed', 'failed', 'cancelled', 'rejected'];
const CO_APP_REQ_TTL_MIN = 30;      // an outward card waits this long for the person's click
const CO_APP_REQ_KEEP = 60;         // requests kept per env (pending and decided, newest)
const CO_APP_REQ_PENDING_MAX = 20;  // pending cards per env: more is refused (an agent in a loop must not flood the inbox)

/** Actions a module declares, each checked against its own server.ops.user; malformed ones are left out. */
function co_app_actions(array $m): array
{
    $decl = $m['manifest']['provides']['actions'] ?? [];
    $ops = co_module_ops($m, 'user');
    $out = [];
    foreach (is_array($decl) ? $decl : [] as $a) {
        if (!is_array($a)) continue;
        $op = (string)($a['op'] ?? '');
        $desc = trim((string)($a['description'] ?? ''));
        if (!in_array($op, $ops, true) || $desc === '' || !is_bool($a['outward'] ?? null)) continue;
        $out[$op] = ['op' => $op, 'description' => mb_substr($desc, 0, 500), 'args' => co_app_schema($a['args'] ?? null),
            'outward' => $a['outward']];
    }
    return $out;
}

/** The argument schema of an action, reduced to what the core checks: flat object of string/integer/number/boolean. */
function co_app_schema(mixed $s): array
{
    $props = [];
    foreach (is_array($s['properties'] ?? null) ? $s['properties'] : [] as $k => $p) {
        if (!is_string($k) || !preg_match('/^[A-Za-z][A-Za-z0-9_]{0,40}$/', $k) || !is_array($p)) continue;
        $t = (string)($p['type'] ?? '');
        if (!in_array($t, CO_APP_ARG_TYPES, true)) continue;
        $props[$k] = ['type' => $t] + (isset($p['description']) ? ['description' => mb_substr((string)$p['description'], 0, 300)] : [])
            + (is_array($p['enum'] ?? null) ? ['enum' => array_values($p['enum'])] : []);
    }
    $req = array_values(array_filter(is_array($s['required'] ?? null) ? $s['required'] : [],
        static fn($k): bool => is_string($k) && isset($props[$k])));
    return ['type' => 'object', 'properties' => (object)$props, 'required' => $req, 'additionalProperties' => false];
}

/** Checks the agent's arguments against the schema; throws InvalidArgumentException with a message for the agent. */
function co_app_args(array $schema, mixed $args): array
{
    if ($args === null) $args = [];
    if (!is_array($args) || ($args !== [] && array_is_list($args))) throw new InvalidArgumentException('args must be an object');
    $props = (array)$schema['properties'];
    foreach ($schema['required'] as $k) {
        if (!array_key_exists($k, $args)) throw new InvalidArgumentException("args.$k is required");
    }
    foreach ($args as $k => $v) {
        $p = $props[$k] ?? null;
        if ($p === null) throw new InvalidArgumentException("args.$k is not an argument of this action");
        $ok = match ($p['type']) {
            'string' => is_string($v) && mb_strlen($v) <= 2000,
            'integer' => is_int($v),
            'number' => is_int($v) || is_float($v),
            'boolean' => is_bool($v),
        };
        if (!$ok) throw new InvalidArgumentException("args.$k must be a {$p['type']}" . ($p['type'] === 'string' ? ' (up to 2000 characters)' : ''));
        if (isset($p['enum']) && !in_array($v, $p['enum'], true)) throw new InvalidArgumentException("args.$k must be one of " . json_encode($p['enum']));
    }
    return $args;
}

/** The app entry (ui.page app: true) of a module: title and icon for the agent; null when it has none. */
function co_app_face(array $m): array
{
    foreach (is_array($m['manifest']['provides']['ui'] ?? null) ? $m['manifest']['provides']['ui'] : [] as $u) {
        if (is_array($u) && ($u['slot'] ?? '') === 'ui.page' && ($u['app'] ?? false) === true) {
            return ['title' => (string)($u['label'] ?? $u['title'] ?? $m['id']), 'icon' => (string)($u['icon'] ?? '')];
        }
    }
    return ['title' => (string)($m['manifest']['title'] ?? $m['id']), 'icon' => ''];
}

/**
 * The job an agent calls for: its env, chat and whether apps are closed to it. Throws for an unknown or finished job.
 * Returns ['jobId', 'env', 'chatId', 'owner', 'email', 'restricted', 'readOnly'].
 */
function co_app_job(mysqli $db, array $in): array
{
    $job = is_string($in['jobId'] ?? null) ? $in['jobId'] : '';
    if (!preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$/', $job)) throw new InvalidArgumentException('jobId missing (AIWB_JOB_ID of the job)');
    $rows = share_q($db, "SELECT env, chat_id, state, result IS NOT NULL AS done, JSON_UNQUOTE(JSON_EXTRACT(request, '$.permissions.files')) AS files
                          FROM jobs WHERE job_id = ?", [$job]);
    if (!$rows) throw new InvalidArgumentException('unknown job');
    $r = $rows[0];
    if ((int)$r['done'] === 1 || in_array((string)$r['state'], CO_APP_JOB_DONE, true)) throw new InvalidArgumentException('the job has finished');
    $env = (string)$r['env'];
    if ($env === '') throw new InvalidArgumentException('the job has no environment');
    $email = (string)(array_search($env, co_push_users(), true) ?: '');
    $chat = (string)$r['chat_id'];
    return ['jobId' => $job, 'env' => $env, 'chatId' => $chat, 'owner' => co_is_owner_env($env), 'email' => $email,
        'restricted' => relay_job_restricted($db, $env, $job, $chat), 'readOnly' => (string)$r['files'] === 'read'];
}

/** Modules whose actions this job may see: enabled, with actions, not switched off by the person. */
function co_app_modules(mysqli $db, array $job): array
{
    $out = [];
    foreach (co_modules($db) as $m) {
        if (!$m['enabled'] || !$m['standard']) continue;
        $acts = co_app_actions($m);
        if (!$acts || co_module_person_mode($db, $m, $job['env']) === 'off') continue;
        $out[$m['id']] = [$m, $acts];
    }
    return $out;
}

/** db.php host op app_list {jobId}: the apps and actions of the job's environment. */
function co_app_list(mysqli $db, array $in): array
{
    $job = co_app_job($db, $in);
    if ($job['restricted']) {
        return ['apps' => [], 'restricted' => true,
            'note' => 'This job belongs to a shared chat (or a subtask of one, or was queued restricted): app actions are not available to it.'];
    }
    $apps = [];
    foreach (co_app_modules($db, $job) as $id => [$m, $acts]) {
        $apps[] = ['app' => $id, 'version' => $m['version']] + co_app_face($m) + ['actions' => array_values(array_map(
            static fn(array $a): array => $a + ($a['outward'] ? ['note' => 'outward: app_action makes a card in "Waiting for you"; it runs only when the person presses Run (see app_action_status)'] : []),
            $acts))];
    }
    return ['env' => $job['env'], 'apps' => $apps];
}

/** db.php host op app_action {jobId, app, action, args}: runs one action as the person of the job's environment. */
function co_app_action(mysqli $db, array $in): array
{
    $job = co_app_job($db, $in);
    if ($job['restricted']) {
        return ['ok' => false, 'error' => 'not_allowed', 'message' => 'App actions are not available in a shared chat, below one or in a restricted job.'];
    }
    if ($job['readOnly']) {
        return ['ok' => false, 'error' => 'read_only', 'message' => 'This chat is read-only for the agent: app actions are not available in it.'];
    }
    $app = is_string($in['app'] ?? null) ? $in['app'] : '';
    $op = is_string($in['action'] ?? null) ? $in['action'] : '';
    [$m, $acts] = co_app_modules($db, $job)[$app] ?? [null, []];
    $a = $acts[$op] ?? null;
    if ($m === null || $a === null) {
        return ['ok' => false, 'error' => 'unknown_action', 'message' => "No action $op of app $app in this environment (see app_list)."];
    }
    $args = co_app_args($a['args'], $in['args'] ?? []);
    if ($a['outward']) return co_app_request_put($db, $job, $m, $a, $args);
    $r = co_app_run($db, $m, $op, $args, $job['env'], $job['owner'], $job['email'], ['actor' => 'agent', 'jobId' => $job['jobId']],
        ['job' => $job['jobId']]);
    return ['ok' => !isset($r['error']), 'app' => $app, 'action' => $op, 'result' => $r];
}

/** Runs the module's handler of user op $op as the person of $env; logs it and refreshes the env's pages on success. */
function co_app_run(mysqli $db, array $m, string $op, array $args, string $env, bool $owner, string $email, array $ctx, array $log): array
{
    $h = co_module_load($m)['user'][$op] ?? null;
    if (!is_callable($h)) throw new RuntimeException("module {$m['id']} declares $op but has no handler");
    $r = $h(co_module_ctx($db, $m, $env, $owner, $email) + $ctx, $args);
    $r = is_array($r) ? $r : ['ok' => true];
    $failed = isset($r['error']);
    co_module_log($db, $m['id'], 'action', $m['version'], $env, ['op' => $op, 'ok' => !$failed] + $log);
    if (!$failed) co_app_event_put($db, $env, $m['id'], $op);
    return $r;
}

// ---------- F14b-2: outward actions wait for the person (kv scope env/<env>, name app-requests) ----------

/** The etag of a request: what the person sees on the card is what runs (app, action, module version, arguments). */
function co_app_request_etag(string $app, string $op, string $version, array $args): string
{
    ksort($args);
    return substr(hash('sha256', co_db_json([$app, $op, $version, (object)$args])), 0, 24);
}

/** Pending requests past their time read as expired. */
function co_app_requests_expire(array $list): array
{
    $list = array_values(array_filter($list, 'is_array'));
    $now = time();
    foreach ($list as &$q) {
        if (($q['state'] ?? '') === 'pending' && (int)($q['expires'] ?? 0) <= $now) $q['state'] = 'expired';
    }
    unset($q);
    return $list;
}

/** Keeps every pending or running request and the newest decided ones, CO_APP_REQ_KEEP in all (review F14b-2). */
function co_app_requests_prune(array $list): array
{
    $live = static fn(array $q): bool => in_array($q['state'] ?? '', ['pending', 'running'], true);
    $drop = max(0, count($list) - CO_APP_REQ_KEEP);
    $out = [];
    foreach (array_values($list) as $q) {
        if ($drop > 0 && !$live($q)) {
            $drop--;
            continue;
        }
        $out[] = $q;
    }
    return $out;
}

/** Reads and changes the env's request list under a row lock; $f gets the list by reference and returns the answer. */
function co_app_requests_locked(mysqli $db, string $env, callable $f): mixed
{
    $scope = 'env/' . $env;
    $db->begin_transaction();
    try {
        $st = $db->prepare("INSERT IGNORE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, 'app-requests', '[]', '', UTC_TIMESTAMP(3))");
        $st->execute([$scope]);
        $st->close();
        $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-requests' FOR UPDATE");
        $st->execute([$scope]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $list = $row ? json_decode((string)$row[0], true) : null;
        $list = co_app_requests_expire(is_array($list) && array_is_list($list) ? $list : []);
        $out = $f($list);
        $data = co_db_json(co_app_requests_prune($list));
        $st = $db->prepare("UPDATE kv SET data = ?, sha = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = 'app-requests'");
        $st->execute([$data, hash('sha256', $data), $scope]);
        $st->close();
        $db->commit();
        return $out;
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
}

/** The env's requests without a lock. */
function co_app_requests(mysqli $db, string $env): array
{
    $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-requests'");
    $st->execute(['env/' . $env]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $list = $row ? json_decode((string)$row[0], true) : null;
    return co_app_requests_expire(is_array($list) && array_is_list($list) ? $list : []);
}

/** The arguments in words for the card: the schema's description of each argument next to its value. */
function co_app_args_view(array $schema, array $args): array
{
    $props = (array)$schema['properties'];
    $out = [];
    foreach ($args as $k => $v) {
        $out[] = ['name' => (string)$k, 'about' => (string)($props[$k]['description'] ?? ''),
            'value' => is_bool($v) ? ($v ? 'yes' : 'no') : (string)$v];
    }
    return $out;
}

/** An agent's outward action: files a request (the same pending one again for a repeated call) and pushes the person. */
function co_app_request_put(mysqli $db, array $job, array $m, array $a, array $args): array
{
    $app = $m['id'];
    $op = $a['op'];
    $etag = co_app_request_etag($app, $op, $m['version'], $args);
    $st = $db->prepare('SELECT title, project_id FROM chats WHERE chat_id = ?');
    $st->execute([$job['chatId']]);
    $chat = $st->get_result()->fetch_assoc() ?: [];
    $st->close();
    $face = co_app_face($m);
    $r = co_app_requests_locked($db, $job['env'], static function (array &$list) use ($job, $m, $a, $args, $app, $op, $etag, $chat, $face): array {
        foreach ($list as $q) {
            if (($q['state'] ?? '') === 'pending' && ($q['chatId'] ?? '') === $job['chatId'] && ($q['etag'] ?? '') === $etag) {
                return ['id' => $q['id'], 'expires' => $q['expires'], 'again' => true];
            }
        }
        if (count(array_filter($list, static fn(array $q): bool => ($q['state'] ?? '') === 'pending')) >= CO_APP_REQ_PENDING_MAX) {
            return ['error' => 'too_many_pending'];
        }
        $id = bin2hex(random_bytes(8));
        $exp = time() + CO_APP_REQ_TTL_MIN * 60;
        $list[] = ['id' => $id, 'state' => 'pending', 'app' => $app, 'action' => $op, 'version' => $m['version'], 'args' => (object)$args,
            'etag' => $etag, 'title' => $face['title'], 'icon' => $face['icon'], 'description' => $a['description'],
            'argsView' => co_app_args_view($a['args'], $args), 'jobId' => $job['jobId'], 'chatId' => $job['chatId'],
            'chatTitle' => (string)($chat['title'] ?? ''), 'projectId' => (string)($chat['project_id'] ?? ''),
            'created' => time(), 'expires' => $exp];
        return ['id' => $id, 'expires' => $exp, 'again' => false];
    });
    if (isset($r['error'])) {
        return ['ok' => false, 'error' => 'too_many_pending', 'message' => 'Too many actions already wait for the person in "Waiting for you"; wait for their decisions.'];
    }
    if (!$r['again']) {
        co_module_log($db, $app, 'request', $m['version'], $job['env'], ['op' => $op, 'job' => $job['jobId'], 'request' => $r['id']]);
        co_app_request_push($job['env'], $r['id'], $face['title'], $a['description'], (string)($chat['title'] ?? ''));
    }
    return ['ok' => false, 'pending' => true, 'status' => 'pending', 'app' => $app, 'action' => $op, 'requestId' => $r['id'],
        'expiresUtc' => gmdate('Y-m-d\TH:i:s\Z', (int)$r['expires']),
        'message' => "$app/$op acts outside the workbench, so it waits for the person: a card in \"Waiting for you\" (Run / Decline, "
            . CO_APP_REQ_TTL_MIN . ' min). Nothing has happened yet. Tell the person, then check app_action_status with this requestId.'];
}

/** Push to the devices of the env's person (no quiet hours: the card expires). */
function co_app_request_push(string $env, string $id, string $title, string $desc, string $chatTitle): void
{
    if (!function_exists('co_push_broadcast')) return;
    try {
        co_push_broadcast(
            static fn($sub): bool => is_array($sub) && ($sub['env'] ?? '') === $env,
            ['title' => 'Confirm: ' . mb_substr($title, 0, 60), 'body' => mb_substr($desc, 0, 140) . ($chatTitle !== '' ? ' · ' . mb_substr($chatTitle, 0, 60) : ''),
                'url' => '/e/' . rawurlencode($env) . '/inbox#app-' . $id, 'tag' => 'app-' . $id],
            'app-' . $id
        );
    } catch (Throwable $e) {
        error_log('co_app_request_push: ' . $e->getMessage());
    }
}

/** What the agent may know of a request (not who decided). */
function co_app_request_public(array $q): array
{
    $out = ['requestId' => (string)$q['id'], 'status' => (string)$q['state'], 'app' => (string)$q['app'], 'action' => (string)$q['action'],
        'expiresUtc' => gmdate('Y-m-d\TH:i:s\Z', (int)($q['expires'] ?? 0))];
    if (isset($q['decided'])) $out['decidedUtc'] = gmdate('Y-m-d\TH:i:s\Z', (int)$q['decided']);
    if (array_key_exists('result', $q)) $out['result'] = $q['result'];
    return $out;
}

/** db.php host op app_action_status {jobId, requestId}: the outcome of a request of the job's environment. */
function co_app_action_status(mysqli $db, array $in): array
{
    $job = co_app_job($db, $in);
    if ($job['restricted']) {
        return ['ok' => false, 'error' => 'not_allowed', 'message' => 'App actions are not available in a shared chat, below one or in a restricted job.'];
    }
    $id = is_string($in['requestId'] ?? null) ? $in['requestId'] : '';
    foreach (co_app_requests($db, $job['env']) as $q) {
        if ($id !== '' && hash_equals((string)($q['id'] ?? ''), $id)) return ['ok' => true] + co_app_request_public($q);
    }
    return ['ok' => false, 'error' => 'unknown_request', 'message' => 'No such request in this environment (only the newest ' . CO_APP_REQ_KEEP . ' are kept).'];
}

/** Cards of pending requests for "Waiting for you" of $env (inbox-lib.php; only the person of $env gets them). */
function co_app_requests_inbox(mysqli $db, string $env): array
{
    $out = [];
    foreach (co_app_requests($db, $env) as $q) {
        if (($q['state'] ?? '') !== 'pending') continue;
        $out[] = ['id' => 'app:' . $q['id'], 'kind' => 'app', 'chatId' => (string)$q['chatId'], 'projectId' => (string)$q['projectId'],
            'chatTitle' => (string)$q['chatTitle'], 'projectTitle' => (string)$q['title'], 'eventId' => '', 'agent' => null,
            'utc' => gmdate('Y-m-d\TH:i:s\Z', (int)$q['created']), 'excerpt' => (string)$q['description'],
            'app' => ['requestId' => (string)$q['id'], 'app' => (string)$q['app'], 'action' => (string)$q['action'],
                'title' => (string)$q['title'], 'icon' => (string)$q['icon'], 'description' => (string)$q['description'],
                'args' => is_array($q['argsView'] ?? null) ? $q['argsView'] : [], 'etag' => (string)$q['etag'],
                'expiresUtc' => gmdate('Y-m-d\TH:i:s\Z', (int)$q['expires'])]];
    }
    return $out;
}

/**
 * db.php user op app_request_decide {id, decision run|deny, etag}: the person of $myEnv decides a card of their own env.
 * The first decision takes the request out of 'pending' under the row lock, so a repeated or concurrent click is refused;
 * run checks the etag of the card and the module as it is now (enabled, same version, still an outward action, the
 * person not switched off), then runs the handler with the person's own rights (actor person, confirmed: the request).
 */
function co_app_request_decide(mysqli $db, string $myEnv, bool $owner, string $email, array $in): array
{
    $id = is_string($in['id'] ?? null) ? $in['id'] : '';
    $decision = $in['decision'] ?? null;
    if (!preg_match('/^[0-9a-f]{16}$/', $id) || !in_array($decision, ['run', 'deny'], true)) {
        throw new InvalidArgumentException('id and decision run|deny required');
    }
    $etag = is_string($in['etag'] ?? null) ? $in['etag'] : '';
    $took = co_app_requests_locked($db, $myEnv, static function (array &$list) use ($id, $decision, $etag, $email): array {
        foreach ($list as &$q) {
            if (($q['id'] ?? '') !== $id) continue;
            if ($q['state'] !== 'pending') return ['error' => $q['state'] === 'expired' ? 'expired' : 'already_decided', 'status' => $q['state']];
            if ($decision === 'run' && !hash_equals((string)$q['etag'], $etag)) return ['error' => 'etag_mismatch'];
            $q['state'] = $decision === 'run' ? 'running' : 'denied';
            $q['decided'] = time();
            $q['by'] = $email;
            return ['q' => $q];
        }
        unset($q);
        return ['error' => 'not_found'];
    });
    if (isset($took['error'])) return ['ok' => false] + $took;
    $q = $took['q'];
    if ($decision === 'deny') {
        co_module_log($db, (string)$q['app'], 'request', (string)$q['version'], $myEnv, ['op' => $q['action'], 'request' => $id, 'decision' => 'deny']);
        return ['ok' => true, 'status' => 'denied'];
    }
    $args = (array)$q['args'];
    try {
        $m = co_app_modules($db, ['env' => $myEnv])[$q['app']][0] ?? null;
        $a = $m ? (co_app_actions($m)[$q['action']] ?? null) : null;
        if ($m === null || $a === null || !$a['outward']) {
            $r = ['error' => 'app_changed', 'message' => 'The app or this action is no longer available.'];
        } elseif (!hash_equals(co_app_request_etag($m['id'], $a['op'], $m['version'], $args), (string)$q['etag'])) {
            $r = ['error' => 'app_changed', 'message' => 'The app was updated after the card was made: ask the agent again.'];
        } else {
            $r = co_app_run($db, $m, $a['op'], co_app_args($a['args'], $args), $myEnv, $owner, $email,
                ['actor' => 'person', 'confirmed' => ['request' => $id, 'jobId' => (string)$q['jobId']]],
                ['request' => $id, 'job' => (string)$q['jobId'], 'confirmed' => true]);
        }
    } catch (Throwable $e) {
        error_log('co_app_request_decide: ' . $e->getMessage());
        $r = ['error' => 'failed', 'message' => mb_substr($e->getMessage(), 0, 300)];
    }
    $state = isset($r['error']) ? 'failed' : 'done';
    $result = strlen(co_db_json($r)) > 4000 ? ['truncated' => true] + (isset($r['error']) ? ['error' => $r['error']] : []) : $r;
    co_app_requests_locked($db, $myEnv, static function (array &$list) use ($id, $state, $result): void {
        foreach ($list as &$x) {
            if (($x['id'] ?? '') === $id) {
                $x['state'] = $state;
                $x['result'] = $result;
            }
        }
        unset($x);
    });
    return ['ok' => $state === 'done', 'status' => $state, 'result' => $result];
}

// ---------- "refresh" for open app pages (kv scope env/<env>, name app-events) ----------

/** Records that an action of $app ran for $env; the pages of that app in CO Web of $env refresh. */
function co_app_event_put(mysqli $db, string $env, string $app, string $op): void
{
    $db->begin_transaction();
    try {
        $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-events' FOR UPDATE");
        $st->execute(['env/' . $env]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $d = $row ? json_decode((string)$row[0], true) : null;
        $d = is_array($d) ? $d : ['seq' => 0, 'events' => []];
        $seq = (int)($d['seq'] ?? 0) + 1;
        $events = array_slice(array_merge(is_array($d['events'] ?? null) ? $d['events'] : [],
            [['seq' => $seq, 'app' => $app, 'action' => $op, 'utc' => gmdate('Y-m-d\TH:i:s\Z')]]), -CO_APP_EVENTS_KEEP);
        $data = json_encode(['seq' => $seq, 'events' => $events], JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
        $st = $db->prepare('INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
                            ON DUPLICATE KEY UPDATE data = VALUES(data), sha = VALUES(sha), updated_utc = VALUES(updated_utc)');
        $st->execute(['env/' . $env, 'app-events', $data, hash('sha256', $data)]);
        $st->close();
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
}

/** db.php user op app_events {since}: actions after seq $since for the caller's env (since -1 or a seq from before a reset: only the current seq). */
function co_app_events(mysqli $db, string $env, int $since): array
{
    $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-events'");
    $st->execute(['env/' . $env]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $d = $row ? json_decode((string)$row[0], true) : null;
    $seq = is_array($d) ? (int)($d['seq'] ?? 0) : 0;
    if ($since < 0 || $since > $seq) return ['seq' => $seq, 'events' => []];
    $ev = array_values(array_filter(is_array($d['events'] ?? null) ? $d['events'] : [],
        static fn($e): bool => is_array($e) && (int)($e['seq'] ?? 0) > $since));
    return ['seq' => $seq, 'events' => $ev];
}
