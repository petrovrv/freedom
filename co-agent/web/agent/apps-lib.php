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
//   - outward (writes to the world outside the workbench: mail, calendars of others, devices of the house): never by
//     the agent; F14b-2 turns such a call into a card in "Waiting for you" that only the person's click runs
//   - after an action the open pages of the app get the ext/1 event "refresh" (app_events, polled by CO Web)
declare(strict_types=1);

require_once __DIR__ . '/modules-lib.php';
require_once __DIR__ . '/relay-lib.php';

const CO_APP_ARG_TYPES = ['string', 'integer', 'number', 'boolean'];
const CO_APP_EVENTS_KEEP = 30;
const CO_APP_JOB_DONE = ['completed', 'failed', 'cancelled', 'rejected'];

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
            static fn(array $a): array => $a + ($a['outward'] ? ['note' => 'outward: only the person runs it (not available to the agent yet)'] : []),
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
    if ($a['outward']) {
        // F14b-2: a card in "Waiting for you" with a one-time permission for the person's click
        return ['ok' => false, 'error' => 'outward', 'message' => "$app/$op acts outside the workbench: only the person runs it, by a click in the app. Ask them to do it (cards in \"Waiting for you\" come with F14b-2)."];
    }
    $args = co_app_args($a['args'], $in['args'] ?? []);
    $h = co_module_load($m)['user'][$op] ?? null;
    if (!is_callable($h)) throw new RuntimeException("module $app declares $op but has no handler");
    $ctx = co_module_ctx($db, $m, $job['env'], $job['owner'], $job['email']) + ['actor' => 'agent', 'jobId' => $job['jobId']];
    $r = $h($ctx, $args);
    $r = is_array($r) ? $r : ['ok' => true];
    $failed = isset($r['error']);
    co_module_log($db, $app, 'action', $m['version'], $job['env'], ['op' => $op, 'job' => $job['jobId'], 'ok' => !$failed]);
    if (!$failed) co_app_event_put($db, $job['env'], $app, $op);
    return ['ok' => !$failed, 'app' => $app, 'action' => $op, 'result' => $r];
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
