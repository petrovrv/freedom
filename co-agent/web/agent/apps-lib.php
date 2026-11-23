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
//   - F14c: an app with the permission data.env keeps small data per person through the core (no server code of its
//     own: apps made on the fly are signed by the owner, not the maintainers) - ext/1 coExt.data, user op app_data; the
//     agent gets the core actions data_list / data_get / data_set / data_delete on it
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
            + (is_array($p['enum'] ?? null) ? ['enum' => array_values($p['enum'])] : [])
            // a longer string than the default 2000 characters (F14c: a value of the app's data), at most 65536
            + ($t === 'string' && is_int($p['maxLength'] ?? null) && $p['maxLength'] > 0 ? ['maxLength' => min(65536, $p['maxLength'])] : []);
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
            'string' => is_string($v) && mb_strlen($v) <= ($p['maxLength'] ?? 2000),
            'integer' => is_int($v),
            'number' => is_int($v) || is_float($v),
            'boolean' => is_bool($v),
        };
        if (!$ok) throw new InvalidArgumentException("args.$k must be a {$p['type']}" . ($p['type'] === 'string' ? ' (up to ' . ($p['maxLength'] ?? 2000) . ' characters)' : ''));
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

/** Every action of a module: the core's data actions (data.env) first, then the module's own (standard modules only). */
function co_app_all_actions(array $m): array
{
    return co_app_data_actions($m) + ($m['standard'] ? co_app_actions($m) : []);
}

/** Modules whose actions this job may see: enabled, for the job's env, with actions, not switched off by the person. */
function co_app_modules(mysqli $db, array $job): array
{
    $out = [];
    foreach (co_modules($db) as $m) {
        if (!$m['enabled'] || !co_module_for_env($m, $job['env'])) continue;
        $acts = co_app_all_actions($m);
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
    if (isset(co_app_data_actions($m)[$op])) {
        // F14c: a core data action (the module has no handler of its own for it)
        $r = co_app_data($db, $m['id'], $env, substr($op, 5), $args);
        if (empty($r['ok'])) $r += ['error' => 'failed'];
    } else {
        $h = co_module_load($m)['user'][$op] ?? null;
        if (!is_callable($h)) throw new RuntimeException("module {$m['id']} declares $op but has no handler");
        $r = $h(co_module_ctx($db, $m, $env, $owner, $email) + $ctx, $args);
        $r = is_array($r) ? $r : ['ok' => true];
    }
    $failed = isset($r['error']);
    co_module_log($db, $m['id'], 'action', $m['version'], $env, ['op' => $op, 'ok' => !$failed] + $log);
    // reads of the core's data actions change nothing: no refresh
    if (!$failed && !in_array($op, ['data_list', 'data_get'], true)) co_app_event_put($db, $env, $m['id'], $op);
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

// ---------- F14b-3: notifications of apps through the core's push (kv scope env/<env>, name app-notify) ----------
// A module with the permission notify.push sends its person a push: from a page (ext/1 coExt.notify -> user op
// app_notify, the session's env), from its server code ($ctx['notify'], user ops: only the caller's env) or from a home
// service (push.php op app_notify with the push key, any member env). Limits per module and env, the person's own switch
// (user op app_notify_set, Settings -> the module), `tag` collapses repeats; every push is a `notify` row of the journal.
// Copies go to the notify hooks of the other modules like any push of the core, never to the sender's own hooks.
const CO_APP_NOTIFY_PERMISSION = 'notify.push';
const CO_APP_NOTIFY_HOUR = 10;      // pushes per module and env in any hour
const CO_APP_NOTIFY_DAY = 50;       // ... and in any 24 hours
const CO_APP_NOTIFY_SAME_SEC = 600; // the same tag with the same text again within this time is not sent

/** Whether a module may notify at all (enabled and its manifest declares notify.push). */
function co_app_notify_allowed(array $m): bool
{
    return $m['enabled'] && in_array(CO_APP_NOTIFY_PERMISSION, (array)($m['manifest']['permissions'] ?? []), true);
}

/**
 * The env's notify state under a row lock; $f gets it by reference and returns the answer. The insert takes the row's
 * exclusive lock at once (a no-op update; INSERT IGNORE would take a shared one, and two of them deadlock on the
 * upgrade); a deadlock all the same is retried (review F14b-3).
 */
function co_app_notify_locked(mysqli $db, string $env, callable $f): mixed
{
    for ($try = 1; ; $try++) {
        try {
            return co_app_notify_locked_once($db, $env, $f);
        } catch (mysqli_sql_exception $e) {
            if ($try >= 3 || !in_array($e->getCode(), [1213, 1205], true)) throw $e;
            usleep(random_int(20000, 80000) * $try);
        }
    }
}

function co_app_notify_locked_once(mysqli $db, string $env, callable $f): mixed
{
    $scope = 'env/' . $env;
    $db->begin_transaction();
    try {
        $st = $db->prepare("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, 'app-notify', '{}', '', UTC_TIMESTAMP(3))
                            ON DUPLICATE KEY UPDATE name = name");
        $st->execute([$scope]);
        $st->close();
        $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-notify' FOR UPDATE");
        $st->execute([$scope]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $d = $row ? json_decode((string)$row[0], true) : null;
        $d = is_array($d) ? $d : [];
        foreach (['off', 'sent', 'last'] as $k) $d[$k] = is_array($d[$k] ?? null) ? $d[$k] : [];
        $out = $f($d);
        $data = json_encode(['off' => (object)$d['off'], 'sent' => (object)$d['sent'], 'last' => (object)$d['last']],
            JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
        $st = $db->prepare("UPDATE kv SET data = ?, sha = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = 'app-notify'");
        $st->execute([$data, hash('sha256', $data), $scope]);
        $st->close();
        $db->commit();
        return $out;
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
}

/** Modules whose notifications the person of $env switched off: [module id => true]. */
function co_app_notify_off(mysqli $db, string $env): array
{
    $st = $db->prepare("SELECT data FROM kv WHERE scope = ? AND name = 'app-notify'");
    $st->execute(['env/' . $env]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $d = $row ? json_decode((string)$row[0], true) : null;
    return is_array($d['off'] ?? null) ? array_filter($d['off']) : [];
}

/** db.php user op app_notify_set {id, on}: the person's own switch of a module's notifications. */
function co_app_notify_set(mysqli $db, string $env, string $id, bool $on): array
{
    $m = co_modules($db)[$id] ?? null;
    if ($m === null || !in_array(CO_APP_NOTIFY_PERMISSION, (array)($m['manifest']['permissions'] ?? []), true)) {
        return ['ok' => false, 'error' => 'not_found'];
    }
    co_app_notify_locked($db, $env, static function (array &$d) use ($id, $on): void {
        if ($on) unset($d['off'][$id]);
        else $d['off'][$id] = true;
    });
    co_module_log($db, $id, 'notify-switch', $m['version'], $env, ['on' => $on]);
    return ['ok' => true, 'on' => $on];
}

/** Where a click on the push goes: the module's page (default its app page) in the shell; with params the bare page. */
function co_app_notify_url(array $m, string $env, mixed $open): string
{
    $open = is_array($open) ? $open : [];
    $pages = [];
    $app = '';
    foreach (co_module_ui($m) as $u) {
        if ($u['slot'] !== 'ui.page') continue;
        $pages[] = $u['id'];
        if ($app === '' && !empty($u['app'])) $app = $u['id'];
    }
    $given = array_key_exists('page', $open);
    $page = $given ? $open['page'] : ($app !== '' ? $app : ($pages[0] ?? ''));
    if (!is_string($page) || !in_array($page, $pages, true) || !preg_match('/^[A-Za-z0-9._-]{1,40}$/', $page)) {
        if ($given) throw new InvalidArgumentException('open.page is not a ui.page of the module');
        return '/';
    }
    $params = $open['params'] ?? [];
    if (!is_array($params) || ($params !== [] && array_is_list($params)) || count($params) > 8) {
        throw new InvalidArgumentException('open.params must be an object of at most 8 strings');
    }
    foreach ($params as $k => $v) {
        if (!is_string($k) || !preg_match('/^[A-Za-z][A-Za-z0-9_-]{0,30}$/', $k) || !is_string($v) || mb_strlen($v) > 300
            || preg_match('/[\x00-\x1f]/', $v)) {
            throw new InvalidArgumentException('open.params: names [A-Za-z][A-Za-z0-9_-]*, values short strings');
        }
    }
    // the shell address has no query; a page that needs its params opens bare at /x/ (ctx.params), as fileActions do
    if ($params === []) return '/e/' . rawurlencode($env) . '/m/' . $m['id'] . '/' . $page;
    $url = '/x/' . $m['id'] . '/' . $page . '?' . http_build_query($params, '', '&', PHP_QUERY_RFC3986);
    // co_push_notify keeps 1000 characters: a longer address would arrive cut, so it is refused before any quota is used
    if (strlen($url) > 1000) throw new InvalidArgumentException('open.params: the address is longer than 1000 characters');
    return $url;
}

/** A text field of a notification: one line, trimmed, cut to $max with an ellipsis. */
function co_app_notify_text(mixed $v, int $max, string $name, bool $need): string
{
    if ($v === null) $v = '';
    if (!is_string($v)) throw new InvalidArgumentException("$name must be a string");
    $v = trim((string)preg_replace('/[\s\x00-\x1f\x7f]+/u', ' ', $v));
    if ($need && $v === '') throw new InvalidArgumentException("$name must not be empty");
    return mb_strlen($v) > $max ? rtrim(mb_substr($v, 0, $max - 1)) . '…' : $v;
}

/**
 * One notification of module $id to the person of $env ($by: page | server | service, for the journal).
 * $n = {title, body?, tag?, open?: {page?, params?}}. Returns {ok: true, sent, modules} (sent = devices reached),
 * {ok: true, sent: 0, duplicate: true}, or {ok: false, error, message}. Throws InvalidArgumentException for bad fields.
 */
function co_app_notify(mysqli $db, string $id, string $env, array $n, string $by): array
{
    $m = co_modules($db)[$id] ?? null;
    if ($m === null || !$m['enabled']) return ['ok' => false, 'error' => 'module_disabled', 'message' => "Module $id is not enabled."];
    if (!co_app_notify_allowed($m)) {
        return ['ok' => false, 'error' => 'permission_not_declared', 'message' => "Module $id does not declare the permission " . CO_APP_NOTIFY_PERMISSION . '.'];
    }
    if (!in_array($env, co_member_envs(), true)) return ['ok' => false, 'error' => 'bad_env', 'message' => 'Not a member environment.'];
    if (co_module_person_mode($db, $m, $env) === 'off') {
        return ['ok' => false, 'error' => 'person_off', 'message' => 'The person switched this module off for themselves.'];
    }
    $title = co_app_notify_text($n['title'] ?? null, 80, 'title', true);
    $body = co_app_notify_text($n['body'] ?? null, 300, 'body', false);
    $tag = $n['tag'] ?? '';
    if (!is_string($tag) || ($tag !== '' && !preg_match('/^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$/', $tag))) {
        throw new InvalidArgumentException('tag must match [A-Za-z0-9][A-Za-z0-9._-]{0,40}');
    }
    $url = co_app_notify_url($m, $env, $n['open'] ?? null);
    $face = co_app_face($m);
    // the app's name in front: a module cannot pass its push off as one of the core ("Confirm: ...")
    $head = trim($face['icon'] . ' ' . $face['title']) . ' · ' . $title;
    // the push tag (and Web Push topic): per module and tag, so modules never replace each other's or the core's
    $ptag = 'app-' . substr(hash('sha256', $id . '/' . ($tag !== '' ? $tag : bin2hex(random_bytes(8)))), 0, 20);
    $same = hash('sha256', co_db_json([$title, $body, $url]));
    $now = time();
    $gate = co_app_notify_locked($db, $env, static function (array &$d) use ($id, $tag, $same, $now): array {
        if (!empty($d['off'][$id])) return ['ok' => false, 'error' => 'notify_off', 'message' => 'The person switched off the notifications of this app.'];
        $last = array_filter(is_array($d['last'][$id] ?? null) ? $d['last'][$id] : [],
            static fn($x): bool => is_array($x) && (int)($x[1] ?? 0) > $now - CO_APP_NOTIFY_SAME_SEC);
        $d['last'][$id] = (object)$last;
        if ($tag !== '' && isset($last[$tag]) && hash_equals((string)$last[$tag][0], $same)) {
            return ['ok' => true, 'sent' => 0, 'duplicate' => true];
        }
        $sent = array_values(array_filter(is_array($d['sent'][$id] ?? null) ? $d['sent'][$id] : [],
            static fn($t): bool => is_int($t) && $t > $now - 86400));
        $hour = array_values(array_filter($sent, static fn(int $t): bool => $t > $now - 3600));
        $d['sent'][$id] = $sent;
        if (count($hour) >= CO_APP_NOTIFY_HOUR || count($sent) >= CO_APP_NOTIFY_DAY) {
            // the later of the two: both limits may hold at once
            $retry = max(count($sent) >= CO_APP_NOTIFY_DAY ? $sent[count($sent) - CO_APP_NOTIFY_DAY] + 86400 : 0,
                count($hour) >= CO_APP_NOTIFY_HOUR ? $hour[count($hour) - CO_APP_NOTIFY_HOUR] + 3600 : 0);
            return ['ok' => false, 'error' => 'rate_limited', 'retryAfter' => max(1, $retry - $now),
                'message' => 'At most ' . CO_APP_NOTIFY_HOUR . ' notifications an hour and ' . CO_APP_NOTIFY_DAY . ' a day per app.'];
        }
        $d['sent'][$id][] = $now;
        if ($tag !== '') {
            $last[$tag] = [$same, $now];
            $d['last'][$id] = (object)$last;
        }
        return ['ok' => true];
    });
    if (!$gate['ok'] || isset($gate['duplicate'])) return $gate;
    // every channel of the core except the sender's own notify hooks (a messenger module notifying must not loop)
    $channels = [CO_PUSH_DEVICES];
    foreach (co_module_hooks($db, 'notify') as $p) {
        if ($p['module']['id'] !== $id && isset($p['hooks']['push'])) $channels[] = $p['module']['id'] . '/' . $p['id'];
    }
    $r = co_push_notify($env, $head, $body, $url, $ptag, null, $channels);
    $mods = array_keys(array_filter((array)($r['modules'] ?? [])));
    co_module_log($db, $id, 'notify', $m['version'], $env, ['by' => $by, 'tag' => $tag, 'sent' => (int)($r['sent'] ?? 0), 'modules' => $mods]);
    return ['ok' => true, 'sent' => (int)($r['sent'] ?? 0), 'modules' => $mods];
}

/** $ctx['notify'] of a module handler: a user op (myEnv set) notifies only its caller; push/host ops name the env. */
function co_app_notify_ctx(mysqli $db, string $id, string $myEnv): Closure
{
    return static function (array $n) use ($db, $id, $myEnv): array {
        $env = is_string($n['env'] ?? null) ? $n['env'] : $myEnv;
        if ($env === '') return ['ok' => false, 'error' => 'env_required', 'message' => 'A push or host op names the env of the person.'];
        if ($myEnv !== '' && $env !== $myEnv) return ['ok' => false, 'error' => 'not_your_env', 'message' => 'A user op notifies only its caller.'];
        try {
            return co_app_notify($db, $id, $env, $n, 'server');
        } catch (InvalidArgumentException $e) {
            return ['ok' => false, 'error' => 'bad_request', 'message' => $e->getMessage()];
        }
    };
}

// ---------- F14c: data of an app kept by the core (kv scope env/<env>, name app-data.<module>) ----------
// An app made on the fly has no server code (only standard modules run PHP, SPEC §3.6 principle 4), so the core keeps
// its data: string values under short keys, per module and person, only for a module that declares data.env and is for
// that person's environment. The page uses coExt.data (user op app_data, the session's env); the agent the actions
// data_list / data_get / data_set / data_delete (co_app_run: journal, and a refresh of the open app after a write).
// The data stays when the module is switched off; remove --data erase deletes it (modules-lib.php co_module_remove).
const CO_APP_DATA_PERMISSION = 'data.env';
const CO_APP_DATA_KEY_RE = '/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/';
const CO_APP_DATA_VALUE_MAX = 65536;     // bytes of one value
const CO_APP_DATA_KEYS_MAX = 200;        // keys per module and person
const CO_APP_DATA_TOTAL_MAX = 1048576;   // bytes of all values of a module and person

/** The core's data actions of a module with data.env (none without the permission). */
function co_app_data_actions(array $m): array
{
    if (!in_array(CO_APP_DATA_PERMISSION, (array)($m['manifest']['permissions'] ?? []), true)) return [];
    $key = ['type' => 'string', 'description' => 'Key: letters, digits, . _ - (up to 80 characters)'];
    $acts = [
        'data_list' => ['List the keys this app keeps for the person (key, size in bytes, last change).',
            ['prefix' => ['type' => 'string', 'description' => 'Only keys that start with this']], []],
        'data_get' => ['Read the value of one key (text; often JSON the app page wrote).', ['key' => $key], ['key']],
        'data_set' => ['Write the value of one key (text up to 64 KB; keep the format the app page uses); the open app reloads.',
            ['key' => $key, 'value' => ['type' => 'string', 'description' => 'The new value', 'maxLength' => CO_APP_DATA_VALUE_MAX]], ['key', 'value']],
        'data_delete' => ['Delete one key; the open app reloads.', ['key' => $key], ['key']],
    ];
    $out = [];
    foreach ($acts as $op => [$desc, $props, $req]) {
        $out[$op] = ['op' => $op, 'description' => $desc, 'args' => co_app_schema(['properties' => $props, 'required' => $req]),
            'outward' => false];
    }
    return $out;
}

/** The data of module $id for $env under a row lock; $f gets the items by reference and returns the answer. */
function co_app_data_locked(mysqli $db, string $id, string $env, callable $f): mixed
{
    $scope = 'env/' . $env;
    $name = 'app-data.' . $id;
    for ($try = 1; ; $try++) {
        $db->begin_transaction();
        try {
            // a no-op update takes the row's exclusive lock at once (as co_app_notify_locked_once)
            $st = $db->prepare("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, '{}', '', UTC_TIMESTAMP(3))
                                ON DUPLICATE KEY UPDATE name = name");
            $st->execute([$scope, $name]);
            $st->close();
            $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ? FOR UPDATE');
            $st->execute([$scope, $name]);
            $row = $st->get_result()->fetch_row();
            $st->close();
            $items = $row ? json_decode((string)$row[0], true) : null;
            $items = is_array($items) ? $items : [];
            $before = $items;
            $out = $f($items);
            if ($items !== $before) {
                $data = json_encode((object)$items, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
                $st = $db->prepare('UPDATE kv SET data = ?, sha = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = ?');
                $st->execute([$data, hash('sha256', $data), $scope, $name]);
                $st->close();
            }
            $db->commit();
            return $out;
        } catch (mysqli_sql_exception $e) {
            $db->rollback();
            if ($try >= 3 || !in_array($e->getCode(), [1213, 1205], true)) throw $e;
            usleep(random_int(20000, 80000) * $try);
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
    }
}

/** A key of app data, or InvalidArgumentException. */
function co_app_data_key(mixed $k): string
{
    if (!is_string($k) || !preg_match(CO_APP_DATA_KEY_RE, $k)) throw new InvalidArgumentException('key must match [A-Za-z0-9][A-Za-z0-9._-]{0,79}');
    return $k;
}

/**
 * The app data of module $id for the person of $env: $action get {key} | set {key, value} | delete {key} | list {prefix?}.
 * Returns {ok: true, ...} or {ok: false, error, message}; throws InvalidArgumentException for a malformed request.
 */
function co_app_data(mysqli $db, string $id, string $env, string $action, array $in): array
{
    $m = co_modules($db)[$id] ?? null;
    if ($m === null || !$m['enabled']) return ['ok' => false, 'error' => 'module_disabled', 'message' => "Module $id is not enabled."];
    if (!in_array(CO_APP_DATA_PERMISSION, (array)($m['manifest']['permissions'] ?? []), true)) {
        return ['ok' => false, 'error' => 'permission_not_declared', 'message' => "Module $id does not declare the permission " . CO_APP_DATA_PERMISSION . '.'];
    }
    if (!in_array($env, co_member_envs(), true)) return ['ok' => false, 'error' => 'bad_env', 'message' => 'Not a member environment.'];
    if (!co_module_for_env($m, $env)) return ['ok' => false, 'error' => 'not_your_app', 'message' => 'This app belongs to another environment.'];
    if (co_module_person_mode($db, $m, $env) === 'off') {
        return ['ok' => false, 'error' => 'person_off', 'message' => 'The person switched this module off for themselves.'];
    }
    if ($action === 'get' || $action === 'list') {
        // reads take no lock
        $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
        $st->execute(['env/' . $env, 'app-data.' . $id]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $items = $row ? json_decode((string)$row[0], true) : null;
        $items = is_array($items) ? $items : [];
        if ($action === 'get') {
            $it = $items[co_app_data_key($in['key'] ?? null)] ?? null;
            return ['ok' => true, 'value' => is_array($it) ? (string)($it['v'] ?? '') : null, 'utc' => is_array($it) ? (string)($it['t'] ?? '') : null];
        }
        $prefix = $in['prefix'] ?? '';
        if (!is_string($prefix) || strlen($prefix) > 80) throw new InvalidArgumentException('prefix must be a string of up to 80 characters');
        $keys = [];
        foreach ($items as $k => $it) {
            if (is_array($it) && ($prefix === '' || str_starts_with((string)$k, $prefix))) {
                $keys[] = ['key' => (string)$k, 'size' => strlen((string)($it['v'] ?? '')), 'utc' => (string)($it['t'] ?? '')];
            }
        }
        usort($keys, static fn(array $a, array $b): int => strcmp($a['key'], $b['key']));
        return ['ok' => true, 'keys' => $keys];
    }
    if ($action !== 'set' && $action !== 'delete') throw new InvalidArgumentException('action must be get, set, delete or list');
    $key = co_app_data_key($in['key'] ?? null);
    $value = $in['value'] ?? null;
    if ($action === 'set' && $value === null) $action = 'delete';
    if ($action === 'set') {
        if (!is_string($value) || !mb_check_encoding($value, 'UTF-8')) throw new InvalidArgumentException('value must be a string (or null to delete)');
        if (strlen($value) > CO_APP_DATA_VALUE_MAX) {
            return ['ok' => false, 'error' => 'too_large', 'message' => 'A value holds at most ' . CO_APP_DATA_VALUE_MAX . ' bytes.'];
        }
    }
    $utc = gmdate('Y-m-d\TH:i:s\Z');
    return co_app_data_locked($db, $id, $env, static function (array &$items) use ($action, $key, $value, $utc): array {
        if ($action === 'delete') {
            $had = isset($items[$key]);
            unset($items[$key]);
            return ['ok' => true, 'deleted' => $had];
        }
        $next = $items;
        $next[$key] = ['v' => $value, 't' => $utc];
        if (count($next) > CO_APP_DATA_KEYS_MAX) {
            return ['ok' => false, 'error' => 'too_many_keys', 'message' => 'An app keeps at most ' . CO_APP_DATA_KEYS_MAX . ' keys per person.'];
        }
        $total = 0;
        foreach ($next as $it) $total += is_array($it) ? strlen((string)($it['v'] ?? '')) : 0;
        if ($total > CO_APP_DATA_TOTAL_MAX) {
            return ['ok' => false, 'error' => 'quota', 'message' => 'An app keeps at most ' . CO_APP_DATA_TOTAL_MAX . ' bytes per person.'];
        }
        $items = $next;
        return ['ok' => true, 'utc' => $utc];
    });
}
