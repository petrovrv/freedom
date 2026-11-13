<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// models-lib.php (schema 23, S5 of chat CHAT-20261111085044-HV3A): one list of model resources - subscriptions on the
// hosts (from their owner.json: agents and models), the site, subscriptions registered by devices (S4) and gateway
// models of modules - each with a card of what it can and cannot do and the flag "slave allowed": a leading agent of any member may
// then call it as a subagent (aiwb-subtask spawn --resource), without limits or confirmations. Own resources are always
// callable; confidential ones (gateway models) never leave their environment. Table model_resources overrides the built-in
// entries (flag, card, title) and holds the registered ones. subtask_spawn creates the child chat and its job here, so a
// leading agent on the site needs no Drive or SSH of its own.
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';
require_once __DIR__ . '/members-lib.php';
require_once __DIR__ . '/share-lib.php';
require_once __DIR__ . '/gateway-lib.php';
require_once __DIR__ . '/executors-lib.php';

const MODELS_KINDS = ['subscription', 'site', CO_GATEWAY_KIND, 'free']; // + CO_GATEWAY_LEGACY_KIND on input (co_gateway_kind)
// Freedom F10: agents of model gateways (and their cards) come from installed modules (gateway-lib.php).
const MODELS_CORE_AGENTS = ['claude', 'codex', 'gemini', 'agy'];
const MODELS_CORE_JOB_AGENTS = ['claude', 'codex']; // agents a job may name today (Bridge, site agent), plus job agents of gateways
const MODELS_MODEL_RE = '/^[A-Za-z0-9][A-Za-z0-9._:\/\[\]-]{0,79}$/';
const MODELS_FRESH_SEC = 900;        // owner.json older than this: the host is offline
const MODELS_MAX_CARD = 4000;
const MODELS_MAX_PROMPT = 200000;
const MODELS_POLL_MAX = 20;

// Cards of the built-in agents; a row of model_resources replaces them field by field.
const MODELS_DEFAULT_CARDS = [
    'claude' => [
        'summary' => 'Claude Code CLI on a Claude subscription: the strongest all-round agent here.',
        'strengths' => 'code, reasoning, long multi-step work, files, web search, the tool library',
        'limits' => 'subscription windows (5 h / week) shared with the owner\'s own chats',
    ],
    'codex' => [
        'summary' => 'Codex CLI on a ChatGPT subscription.',
        'strengths' => 'Word and Excel files (best in the model bench), code review, a second opinion',
        'limits' => 'not on the site host (thread limit); no subagents in chat mode',
    ],
];
const MODELS_SITE_LIMITS = 'runs on the web server: no home devices except through the relay, no WSL, files come back as artifacts';

function models_agents(): array
{
    return array_merge(MODELS_CORE_AGENTS, array_keys(co_gateway_agents()));
}

function models_job_agents(): array
{
    return array_merge(MODELS_CORE_JOB_AGENTS, array_keys(array_filter(co_gateway_agents(), fn($a) => $a['job'])));
}

/**
 * A gateway agent (always confidential): one of an installed module, or flagged so by its host's owner.json
 * (confidential: true since Bridge F10; the legacy wire flag CO_GATEWAY_LEGACY_KIND of older Bridges).
 */
function models_is_gateway(string $agent, ?array $ownerAgent = null): bool
{
    return co_gateway_agent($agent) !== null || ($ownerAgent['confidential'] ?? false) === true
        || ($ownerAgent[CO_GATEWAY_LEGACY_KIND] ?? false) === true;
}

function models_id(mixed $v): string
{
    if (!is_string($v) || preg_match('#^[a-z0-9][a-z0-9._/-]{0,79}$#', $v) !== 1) {
        throw new InvalidArgumentException('invalid resource id');
    }
    return $v;
}

function models_env(mixed $v, string $what = 'env'): string
{
    if (!is_string($v) || preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/', $v) !== 1) {
        throw new InvalidArgumentException("invalid $what");
    }
    return $v;
}

function models_str(mixed $v, int $max): string
{
    return is_string($v) ? mb_substr(trim(preg_replace('/\s+/u', ' ', $v) ?? ''), 0, $max) : '';
}

/** Card: {summary, strengths, limits, dataStaysIn, notes} of plain strings. */
function models_card(mixed $v): array
{
    if ($v === null) {
        return [];
    }
    if (!is_array($v) || array_is_list($v) && $v !== []) {
        throw new InvalidArgumentException('invalid card');
    }
    $out = [];
    foreach (['summary' => 300, 'strengths' => 600, 'limits' => 600, 'dataStaysIn' => 60, 'notes' => 1000] as $k => $max) {
        if (array_key_exists($k, $v)) {
            $out[$k] = models_str($v[$k], $max);
        }
    }
    if (strlen(co_db_json($out)) > MODELS_MAX_CARD) {
        throw new InvalidArgumentException('card too large');
    }
    return $out;
}

/** @return list<array{id:string,label:string}> */
function models_model_list(mixed $v): array
{
    if ($v === null) {
        return [];
    }
    if (!is_array($v) || !array_is_list($v) || count($v) > 40) {
        throw new InvalidArgumentException('invalid models');
    }
    $out = [];
    foreach ($v as $m) {
        $id = is_array($m) ? ($m['id'] ?? null) : $m;
        if (!is_string($id) || preg_match('/^[A-Za-z0-9][A-Za-z0-9._:\/\[\]-]{0,79}$/', $id) !== 1) {
            throw new InvalidArgumentException('invalid model id');
        }
        $out[] = ['id' => $id, 'label' => is_array($m) ? models_str($m['label'] ?? '', 80) : ''];
    }
    return $out;
}

/** owner.json of every host the database knows (kv host:<id>/owner, written by drive-sync / the hosts). */
function models_host_owners(mysqli $db): array
{
    $hosts = [];
    $res = $db->query("SELECT scope, data FROM kv WHERE name = 'owner' AND scope LIKE 'host:%'");
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $o = json_decode((string)$r['data'], true);
        if (is_array($o['data'] ?? null)) {
            $o = $o['data'];
        }
        $host = substr((string)$r['scope'], 5);
        if (is_array($o) && preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/', $host) === 1) {
            $hosts[$host] = $o;
        }
    }
    ksort($hosts);
    return $hosts;
}

function models_host_online(?array $owner): ?bool
{
    if ($owner === null) {
        return null;
    }
    $t = strtotime((string)($owner['updatedUtc'] ?? ''));
    if ($t === false) {
        return false;
    }
    return time() - $t < MODELS_FRESH_SEC && ($owner['state'] ?? 'running') !== 'paused';
}

function models_host_label(string $host): string
{
    // F12: the title a registered executor gave itself (executors-lib.php), Home / Site for the first two
    return co_exec_title(co_db(), $host);
}

/**
 * Built-in resources: one per agent of every host's owner.json, owned by the owner's environment; F12: by the
 * environment whose machine the executor is (executors.owner_env), when it is a family member's.
 */
function models_builtin(array $owners, string $ownerEnv, array $execEnv = []): array
{
    $out = [];
    foreach ($owners as $host => $o) {
        $env = ($execEnv[$host] ?? '') !== '' ? $execEnv[$host] : $ownerEnv;
        $agents = is_array($o['agents'] ?? null) ? $o['agents'] : [];
        foreach ($agents as $agent => $a) {
            if (!is_string($agent) || !in_array($agent, models_agents(), true) || !is_array($a)) {
                continue;
            }
            $gateway = models_is_gateway($agent, $a);
            $card = MODELS_DEFAULT_CARDS[$agent] ?? (co_gateway_agent($agent)['card'] ?? [])
                ?: ['summary' => (string)($a['displayName'] ?? $agent)];
            if ($host === 'site') {
                $card['limits'] = trim(($card['limits'] ?? '') . '; ' . MODELS_SITE_LIMITS, '; ');
            }
            $models = [];
            foreach (is_array($a['models'] ?? null) ? $a['models'] : [] as $m) {
                if (is_array($m) && is_string($m['id'] ?? null) && $m['id'] !== '') {
                    $models[] = ['id' => $m['id'], 'label' => models_str($m['label'] ?? '', 80)];
                }
            }
            $out[$host . '/' . $agent] = [
                'id' => $host . '/' . $agent,
                'env' => $env,
                'kind' => $gateway ? CO_GATEWAY_KIND : ($host === 'site' ? 'site' : 'subscription'),
                'title' => models_str($a['displayName'] ?? ucfirst($agent), 60) . ' · ' . models_host_label($host),
                'host' => $host,
                'agent' => $agent,
                'models' => $models,
                'defaultModel' => is_string($a['defaultModel'] ?? null) ? $a['defaultModel'] : null,
                'card' => $card,
                'slaveAllowed' => false,
                'confidential' => $gateway,
                'source' => 'auto',
                'builtin' => true,
                'online' => models_host_online($o),
                'updatedUtc' => null,
            ];
        }
    }
    return $out;
}

/** Rows of model_resources (schema 23); [] before the migration. */
function models_rows(mysqli $db): array
{
    try {
        $res = $db->query('SELECT * FROM model_resources ORDER BY id');
    } catch (mysqli_sql_exception) {
        return [];
    }
    $out = [];
    foreach ($res->fetch_all(MYSQLI_ASSOC) as $r) {
        $out[(string)$r['id']] = $r;
    }
    return $out;
}

/** All resources (built-in merged with stored rows), unfiltered. */
function models_all(mysqli $db): array
{
    $owners = models_host_owners($db);
    $list = models_builtin($owners, co_owner_env(),
        array_map(static fn(array $r): string => (string)$r['owner_env'], co_exec_rows($db)));
    foreach (models_rows($db) as $id => $r) {
        $card = json_decode((string)$r['card_json'], true);
        $card = is_array($card) ? $card : [];
        $models = json_decode((string)$r['models_json'], true);
        $models = is_array($models) ? $models : [];
        if (isset($list[$id])) {
            if ((string)$r['source'] !== 'auto' || (string)$r['env'] !== $list[$id]['env']) {
                continue; // only the owner's own override row may change a built-in entry
            }
            // the stored row overrides the flag, the card (field by field) and the title of a built-in entry
            $b = $list[$id];
            $b['card'] = array_merge($b['card'], array_filter($card, static fn($v): bool => $v !== ''));
            $b['title'] = (string)$r['title'] !== '' ? (string)$r['title'] : $b['title'];
            $b['slaveAllowed'] = (int)$r['slave_allowed'] === 1;
            // gateway entries stay confidential whatever an older stored row says (Freedom F1 5b)
            $b['confidential'] = $b['kind'] === CO_GATEWAY_KIND || (int)$r['confidential'] === 1;
            $b['updatedUtc'] = co_db_iso((string)$r['updated_utc']);
            $list[$id] = $b;
            continue;
        }
        if ((string)$r['source'] === 'auto') {
            continue; // override of a built-in entry whose host no longer announces the agent
        }
        $host = (string)$r['host_id'];
        $list[$id] = [
            'id' => $id,
            'env' => (string)$r['env'],
            'kind' => co_gateway_kind((string)$r['kind']),
            'title' => (string)$r['title'],
            'host' => $host,
            'agent' => (string)$r['agent'],
            'models' => $models,
            'defaultModel' => $r['default_model'] !== null ? (string)$r['default_model'] : null,
            'card' => $card,
            'slaveAllowed' => (int)$r['slave_allowed'] === 1,
            'confidential' => (int)$r['confidential'] === 1 || co_gateway_kind((string)$r['kind']) === CO_GATEWAY_KIND
                || models_is_gateway((string)$r['agent']),
            'source' => (string)$r['source'],
            'builtin' => false,
            'online' => $host !== '' ? models_host_online($owners[$host] ?? null) : null,
            'updatedUtc' => co_db_iso((string)$r['updated_utc']),
        ];
    }
    return $list;
}

/** May a leading agent of $env call $r as a slave? Own resources yes; others only flagged and not confidential. */
function models_visible(array $r, string $env): bool
{
    return $r['env'] === $env || ($r['slaveAllowed'] && !$r['confidential']);
}

/** Environment of a host call: env, else the environment of the project of chatId (aiwb-subtask models in a chat). */
function models_env_of(mysqli $db, array $in): string
{
    if (isset($in['env'])) {
        return models_env($in['env']);
    }
    $chat = $in['chatId'] ?? null;
    if (!is_string($chat) || !co_db_id_ok($chat)) {
        throw new InvalidArgumentException('env or chatId required');
    }
    $st = $db->prepare('SELECT p.env FROM chats c JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
    $st->execute([$chat]);
    $env = $st->get_result()->fetch_row()[0] ?? null;
    $st->close();
    if (!is_string($env)) {
        throw new InvalidArgumentException('unknown chat');
    }
    return $env;
}

/** models_list: resources the environment may call (all for the owner's admin view when $all). */
function models_list(mysqli $db, string $env, bool $all = false): array
{
    $out = [];
    foreach (models_all($db) as $r) {
        if ($all || models_visible($r, $env)) {
            $r['own'] = $r['env'] === $env;
            $r['callable'] = models_visible($r, $env) && in_array($r['agent'], models_job_agents(), true) && $r['host'] !== '';
            $out[] = $r;
        }
    }
    usort($out, static fn(array $a, array $b): int => [$a['own'] ? 0 : 1, $a['env'], $a['id']] <=> [$b['own'] ? 0 : 1, $b['env'], $b['id']]);
    return ['resources' => $out, 'env' => $env];
}

/**
 * Upsert of a resource. $by: 'user' (CO Web: flag, card, title, confidential of an existing entry; or a new entry of
 * the caller's environment) or 'device' (host key: a subscription a device registered, S4).
 */
function models_put(mysqli $db, array $in, string $by, ?string $callerEnv, bool $admin): array
{
    $id = models_id($in['id'] ?? null);
    $all = models_all($db);
    $cur = $all[$id] ?? null;
    if ($cur !== null && $by === 'user' && !$admin && $cur['env'] !== $callerEnv) {
        return ['ok' => false, 'error' => 'not_owner'];
    }
    if ($cur !== null && $cur['builtin']) {
        // built-in: only the overridable fields
        $row = ['env' => $cur['env'], 'kind' => $cur['kind'], 'host' => $cur['host'], 'agent' => $cur['agent'],
            'models' => [], 'defaultModel' => null, 'source' => 'auto'];
    } else {
        $env = $by === 'user' && !$admin ? (string)$callerEnv : models_env($in['env'] ?? $cur['env'] ?? null);
        // registered resources are named "<env>/...": they never collide with the built-in "<host>/<agent>" entries
        if (!str_starts_with($id, $env . '/')) {
            throw new InvalidArgumentException('id must start with "' . $env . '/"');
        }
        $kind = $in['kind'] ?? $cur['kind'] ?? 'subscription';
        $kind = is_string($kind) ? co_gateway_kind($kind) : $kind;
        if (!in_array($kind, MODELS_KINDS, true)) {
            throw new InvalidArgumentException('invalid kind');
        }
        $agent = $in['agent'] ?? $cur['agent'] ?? null;
        if (!is_string($agent) || !in_array($agent, models_agents(), true)) {
            throw new InvalidArgumentException('invalid agent');
        }
        $host = $in['host'] ?? $cur['host'] ?? '';
        if ($by === 'user' && !$admin && $host !== '' && $host !== ($cur['host'] ?? '')) {
            // a member cannot point a resource at a host (and subscription) of somebody else; devices register hosts
            throw new InvalidArgumentException('only a device registration or the owner sets the host');
        }
        if (!is_string($host) || ($host !== '' && preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/', $host) !== 1)) {
            throw new InvalidArgumentException('invalid host');
        }
        $models = array_key_exists('models', $in) ? models_model_list($in['models']) : ($cur['models'] ?? []);
        $dm = array_key_exists('defaultModel', $in) ? $in['defaultModel'] : ($cur['defaultModel'] ?? null);
        if ($dm !== null && $dm !== '' && (!is_string($dm) || preg_match(MODELS_MODEL_RE, $dm) !== 1)) {
            throw new InvalidArgumentException('invalid defaultModel');
        }
        $row = ['env' => $env, 'kind' => $kind, 'host' => $host, 'agent' => $agent, 'models' => $models,
            'defaultModel' => is_string($dm) && $dm !== '' ? mb_substr($dm, 0, 80) : null,
            'source' => $by === 'device' ? 'device' : 'user'];
    }
    // fields the caller leaves out keep their stored value (for a built-in entry: its override row, if any)
    $stored = models_rows($db)[$id] ?? null;
    $storedCard = $stored ? json_decode((string)$stored['card_json'], true) : null;
    $title = array_key_exists('title', $in) ? models_str($in['title'], 60)
        : ($cur !== null && !$cur['builtin'] ? $cur['title'] : (string)($stored['title'] ?? ''));
    if ($cur === null && $title === '') {
        throw new InvalidArgumentException('title required');
    }
    $card = array_key_exists('card', $in) ? models_card($in['card'])
        : ($cur !== null && !$cur['builtin'] ? $cur['card'] : (is_array($storedCard) ? $storedCard : []));
    $slave = array_key_exists('slaveAllowed', $in) ? $in['slaveAllowed'] === true : (bool)($cur['slaveAllowed'] ?? false);
    $conf = array_key_exists('confidential', $in) ? $in['confidential'] === true
        : (bool)($cur['confidential'] ?? ($row['kind'] === CO_GATEWAY_KIND));
    if ($row['kind'] === CO_GATEWAY_KIND || models_is_gateway((string)$row['agent'])) {
        $conf = true; // data of a gateway never leaves its environment
    }
    $st = $db->prepare(
        'INSERT INTO model_resources (id, env, kind, title, host_id, agent, models_json, default_model, card_json,
                                      slave_allowed, confidential, source, updated_utc, updated_by)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), ?)
         ON DUPLICATE KEY UPDATE env = VALUES(env), kind = VALUES(kind), title = VALUES(title), host_id = VALUES(host_id),
             agent = VALUES(agent), models_json = VALUES(models_json), default_model = VALUES(default_model),
             card_json = VALUES(card_json), slave_allowed = VALUES(slave_allowed), confidential = VALUES(confidential),
             source = VALUES(source), updated_utc = VALUES(updated_utc), updated_by = VALUES(updated_by)'
    );
    $st->execute([$id, $row['env'], $row['kind'], $title, $row['host'], $row['agent'], co_db_json($row['models']),
        $row['defaultModel'], co_db_json((object)$card), $slave ? 1 : 0, $conf ? 1 : 0, $row['source'],
        mb_substr($by === 'user' ? (string)$callerEnv : 'device', 0, 40)]);
    $st->close();
    return ['ok' => true, 'resource' => models_all($db)[$id] ?? null];
}

/** Delete a registered resource, or the override of a built-in one (back to the defaults). */
function models_delete(mysqli $db, array $in, ?string $callerEnv, bool $admin): array
{
    $id = models_id($in['id'] ?? null);
    $cur = models_all($db)[$id] ?? null;
    if ($cur === null) {
        return ['ok' => false, 'error' => 'not_found'];
    }
    if ($callerEnv !== null && !$admin && $cur['env'] !== $callerEnv) {
        return ['ok' => false, 'error' => 'not_owner'];
    }
    $st = $db->prepare('DELETE FROM model_resources WHERE id = ?');
    $st->execute([$id]);
    $n = $st->affected_rows;
    $st->close();
    return ['ok' => true, 'deleted' => $n];
}

// ---------------------------------------------------------------- subtasks on a resource

function models_rand(int $n): string
{
    $abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    $s = '';
    for ($i = 0; $i < $n; $i++) {
        $s .= $abc[random_int(0, 35)];
    }
    return $s;
}

/**
 * subtask_spawn (host key): a child chat "⤷ title" under the parent chat and a job of the resource on its host.
 * $mkChat(projectFolderId, chatId, chatDoc) creates the Drive folder with chat.json and returns its id;
 * $appendEvent(event) stores the user_message (db.php db_append_event). Injected so the test runs without Drive.
 */
function models_subtask_spawn(mysqli $db, array $in, callable $mkChat, callable $appendEvent): array
{
    $parent = $in['parentChatId'] ?? null;
    if (!is_string($parent) || preg_match('/^CHAT-\d{14}-[A-Z0-9]{4}$/', $parent) !== 1) {
        throw new InvalidArgumentException('invalid parentChatId');
    }
    $prompt = is_string($in['prompt'] ?? null) ? trim($in['prompt']) : '';
    if ($prompt === '' || strlen($prompt) > MODELS_MAX_PROMPT) {
        throw new InvalidArgumentException('prompt must be 1..200000 bytes');
    }
    $mode = $in['mode'] ?? 'cowork';
    if (!in_array($mode, ['chat', 'cowork'], true)) {
        throw new InvalidArgumentException('invalid mode');
    }
    $timeout = (int)($in['timeoutMinutes'] ?? 120);
    $timeout = max(0, min(240, $timeout));
    $devices = $in['devices'] ?? [];
    if (!is_array($devices) || !array_is_list($devices) || count($devices) > 8) {
        throw new InvalidArgumentException('invalid devices');
    }
    foreach ($devices as $d) {
        if (!is_string($d) || preg_match('/^d-[0-9a-f]{16}$/', $d) !== 1) {
            throw new InvalidArgumentException('invalid device id');
        }
    }

    $st = $db->prepare('SELECT c.project_id, p.env, p.folder_id, c.title FROM chats c
                          JOIN projects p ON p.project_id = c.project_id WHERE c.chat_id = ?');
    $st->execute([$parent]);
    $p = $st->get_result()->fetch_assoc();
    $st->close();
    if (!$p) {
        return ['ok' => false, 'error' => 'parent_not_found'];
    }
    $env = (string)$p['env'];
    $folder = (string)($p['folder_id'] ?? '');
    if ($folder === '') {
        return ['ok' => false, 'error' => 'project_without_folder'];
    }

    // as db_job_submit: a shared chat or project gets no devices and no gateway agent (the owner's confidential data stays out).
    // R2 (review S5-01): so does any chat below one - the restriction is inherited along the parent chain and the job
    // carries restricted, which its own subtasks find again however deep.
    $shared = share_restricted($db, (string)$p['project_id'], $parent);
    $rid = models_id($in['resourceId'] ?? null);
    $r = models_all($db)[$rid] ?? null;
    if ($r === null) {
        return ['ok' => false, 'error' => 'unknown_resource'];
    }
    if (!models_visible($r, $env)) {
        return ['ok' => false, 'error' => $r['confidential'] ? 'confidential' : 'slave_not_allowed',
            'detail' => $r['confidential'] ? 'Confidential resource: only its own environment may use it.'
                : 'The owner of this resource has not allowed it as a slave.'];
    }
    if (!in_array($r['agent'], models_job_agents(), true) || $r['host'] === '') {
        return ['ok' => false, 'error' => 'not_runnable', 'detail' => 'No host runs jobs of this agent yet.'];
    }
    if (!in_array($r['host'], array_keys(models_host_owners($db)), true)) {
        return ['ok' => false, 'error' => 'host_unknown', 'detail' => "Host {$r['host']} does not take jobs (no owner.json)."];
    }
    $gw = co_gateway_agent($r['agent']);
    if ($shared && ($devices !== [] || $gw !== null || $r['confidential'])) {
        return ['ok' => false, 'error' => SHARE_REFUSED, 'detail' => 'A shared chat cannot use devices or confidential models.'];
    }
    if (($gw['needsDevice'] ?? false) && $devices === []) {
        return ['ok' => false, 'error' => 'needs_device', 'detail' => "{$r['agent']} needs a device with the gateway tools (devices)."];
    }
    $model = $in['model'] ?? $r['defaultModel'];
    if ($model !== null && (!is_string($model) || preg_match(MODELS_MODEL_RE, $model) !== 1)) {
        throw new InvalidArgumentException('invalid model');
    }
    if ($model === null && ($gw['defaultModel'] ?? null) !== null) {
        $model = $gw['defaultModel'];
    }

    // R2 (review S5-02): one subtask per requestKey of the parent - a repeat after a lost answer gets the same chat and job
    $key = $in['requestKey'] ?? null;
    if ($key !== null && (!is_string($key) || preg_match('/^[A-Za-z0-9_-]{16,64}$/', $key) !== 1)) {
        throw new InvalidArgumentException('invalid requestKey');
    }
    $now = new DateTimeImmutable('now', new DateTimeZone('UTC'));
    $stamp = $now->format('YmdHis');
    $iso = $now->format('Y-m-d\TH:i:s.v\Z');
    $chatId = "CHAT-$stamp-" . models_rand(4);
    $jobId = "JOB-$stamp-" . models_rand(6);
    if ($key !== null) {
        $hit = models_spawn_reserve($db, $parent, $key, $chatId, $jobId, $r['online']);
        if ($hit !== null) {
            return $hit;
        }
    }
    try {
        $out = models_spawn_create($db, $mkChat, $appendEvent, $p, $parent, $prompt, $mode, $timeout, $devices, $rid, $r, $model,
            $shared, $in, $stamp, $iso, $chatId, $jobId, $key);
    } catch (Throwable $e) {
        if ($key !== null) {
            models_spawn_release($db, $parent, $key, $jobId);
        }
        throw $e;
    }
    return $out;
}

/** The chat, its user_message and the job of a checked subtask_spawn. */
function models_spawn_create(mysqli $db, callable $mkChat, callable $appendEvent, array $p, string $parent, string $prompt,
                             string $mode, int $timeout, array $devices, string $rid, array $r, ?string $model, bool $shared,
                             array $in, string $stamp, string $iso, string $chatId, string $jobId, ?string $key = null): array
{
    $folder = (string)$p['folder_id'];
    $env = (string)$p['env'];
    $parentTitle = (string)$p['title'];
    $titleIn = models_str($in['title'] ?? '', 80);
    $title = "\u{2937} " . ($titleIn !== '' ? $titleIn : mb_substr(models_str($prompt, 200), 0, 60));
    $chatDoc = [
        'chatId' => $chatId, 'projectId' => (string)$p['project_id'], 'title' => $title, 'rootBranchId' => 'BR-root',
        'createdUtc' => $iso, 'updatedUtc' => $iso, 'defaultMode' => $mode,
        'parent' => ['chatId' => $parent, 'seq' => 0, 'kind' => 'subtask', 'title' => $parentTitle],
    ];
    $chatFolder = $mkChat($folder, $chatId, $chatDoc);
    co_db_put(['chats' => [$chatDoc + ['folderId' => $chatFolder]]]);
    $st = $db->prepare('UPDATE chats SET folder_id = ? WHERE chat_id = ?');
    $st->execute([$chatFolder, $chatId]);
    $st->close();
    $appendEvent([
        'schemaVersion' => 1, 'eventId' => "EVT-$stamp-" . models_rand(6), 'type' => 'user_message', 'createdUtc' => $iso,
        'chatId' => $chatId, 'branchId' => 'BR-root', 'jobId' => $jobId, 'hostId' => $r['host'], 'agent' => $r['agent'],
        'author' => 'subtask', 'text' => $prompt, 'attachments' => [], 'returnFiles' => [],
        'conversation' => ['operation' => 'new', 'sessionId' => null], 'mode' => $mode, 'model' => $model, 'effort' => null,
    ]);
    $who = $r['env'] === $env ? '' : ' as a slave of environment ' . $env;
    $request = [
        'schemaVersion' => 3, 'protocolMinor' => 2, 'hostId' => $r['host'], 'jobId' => $jobId, 'environmentId' => $env,
        'projectId' => (string)$p['project_id'], 'chatId' => $chatId, 'branchId' => 'BR-root', 'agent' => $r['agent'],
        'instruction' => "[Subtask of chat \"$parentTitle\" ($parent) on resource {$r['title']}$who: work on it on your own and end with a concise result]\n\n" . $prompt,
        'conversation' => ['operation' => 'new', 'sessionId' => null], 'attachments' => [], 'artifactPaths' => [],
        'timeoutMinutes' => $timeout, 'mode' => $mode, 'model' => $model, 'effort' => null,
        'output' => ['autoExport' => true], 'permissions' => ['files' => 'write', 'shell' => true, 'web' => true],
        'contextFolders' => [],
        'slave' => ['resourceId' => $rid, 'resourceEnv' => $r['env'], 'leaderEnv' => $env, 'parentChatId' => $parent],
    ];
    if ($devices !== []) {
        $request['devices'] = $devices;
    }
    if ($shared) {
        $request['restricted'] = true;
    }
    $json = co_db_json($request);
    $sha = hash('sha256', co_db_json(['request' => $request, 'result' => null]));
    $out = ['ok' => true, 'chatId' => $chatId, 'jobId' => $jobId, 'title' => $title, 'host' => $r['host'],
        'agent' => $r['agent'], 'model' => $model, 'online' => $r['online']];
    $insert = static function () use ($db, $jobId, $r, $env, $p, $chatId, $mode, $model, $json, $sha): void {
        $st = $db->prepare(
            "INSERT INTO jobs (job_id, host_id, env, project_id, chat_id, agent, mode, model, state, origin, queued_utc,
                               request, sha, synced_utc, actor_env)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'inbox', 'db', UTC_TIMESTAMP(3), ?, ?, UTC_TIMESTAMP(3), ?)"
        );
        $st->execute([$jobId, $r['host'], $env, (string)$p['project_id'], $chatId, $r['agent'], $mode, $model, $json, $sha, $env]);
        $st->close();
    };
    if ($key === null) {
        $insert();
        return $out;
    }
    // fencing: ownership of the key, the INSERT and the done mark under the lock of the reservation row - a takeover
    // (models_spawn_reserve) takes the same lock, so a creator that lost the key queues nothing
    $scope = 'subtask-key:' . $parent;
    $db->begin_transaction();
    try {
        $rows = share_q($db, "SELECT JSON_VALUE(data, '$.jobId') AS j FROM kv WHERE scope = ? AND name = ? FOR UPDATE", [$scope, $key]);
        if (!$rows || (string)$rows[0]['j'] !== $jobId) {
            $db->rollback();
            return ['ok' => false, 'error' => 'in_progress', 'detail' => 'The same subtask (requestKey) was taken over by a repeat; ask its status there.'];
        }
        $insert();
        $st = $db->prepare('UPDATE kv SET data = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = ?');
        $st->execute([co_db_json(['state' => 'done', 'jobId' => $jobId, 'result' => $out]), $scope, $key]);
        $st->close();
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    return $out;
}

const MODELS_SPAWN_STALE_SEC = 120;   // a reservation not finished by then belongs to a request that died half way

/**
 * Reserves requestKey for this request (null: go on and create) or answers for an earlier one: its result, in_progress
 * while it is still being created, or - if it died after its job was queued - the result rebuilt from that job.
 * Rows live in kv (scope subtask-key:<parent>) for a day.
 */
function models_spawn_reserve(mysqli $db, string $parent, string $key, string $chatId, string $jobId, ?bool $online): ?array
{
    $scope = 'subtask-key:' . $parent;
    $busy = ['ok' => false, 'error' => 'in_progress', 'detail' => 'The same subtask (requestKey) is being created; repeat in a few seconds.'];
    $db->query("DELETE FROM kv WHERE scope LIKE 'subtask-key:%' AND updated_utc < UTC_TIMESTAMP(3) - INTERVAL 1 DAY");
    $pending = co_db_json(['state' => 'pending', 'chatId' => $chatId, 'jobId' => $jobId]);
    for ($try = 0; $try < 3; $try++) {
        $st = $db->prepare("INSERT IGNORE INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, '', UTC_TIMESTAMP(3))");
        $st->execute([$scope, $key, $pending]);
        $n = $st->affected_rows;
        $st->close();
        if ($n === 1) {
            return null;
        }
        // under the lock of the reservation row: a creator holds it from its ownership check to its INSERT and done mark
        $db->begin_transaction();
        try {
            $rows = share_q($db, 'SELECT data, updated_utc < UTC_TIMESTAMP(3) - INTERVAL ' . MODELS_SPAWN_STALE_SEC . ' SECOND AS stale
                                  FROM kv WHERE scope = ? AND name = ? FOR UPDATE', [$scope, $key]);
            if (!$rows) {
                $db->commit();
                continue;                               // released meanwhile: reserve again
            }
            $d = json_decode((string)$rows[0]['data'], true) ?: [];
            if (($d['state'] ?? '') === 'done' && is_array($d['result'] ?? null)) {
                $db->commit();
                return $d['result'] + ['repeated' => true];
            }
            $old = is_string($d['jobId'] ?? null) ? $d['jobId'] : '';
            $job = $old === '' ? [] : share_q($db, 'SELECT j.chat_id, j.host_id, j.agent, j.model, c.title FROM jobs j
                                                    LEFT JOIN chats c ON c.chat_id = j.chat_id WHERE j.job_id = ?', [$old]);
            if ($job) {
                // a job queued without its done mark (an older server, a crash between them): answer from the job
                $out = ['ok' => true, 'chatId' => (string)$job[0]['chat_id'], 'jobId' => $old, 'title' => (string)($job[0]['title'] ?? ''),
                    'host' => (string)$job[0]['host_id'], 'agent' => (string)$job[0]['agent'],
                    'model' => $job[0]['model'] !== null ? (string)$job[0]['model'] : null, 'online' => $online];
                $done = co_db_json(['state' => 'done', 'jobId' => $old, 'result' => $out]);
            } elseif (!(int)$rows[0]['stale']) {
                $db->commit();
                return $busy;
            } else {
                $out = null;                            // it died before queueing its job: this request takes the key over
                $done = $pending;
            }
            $st = $db->prepare('UPDATE kv SET data = ?, updated_utc = UTC_TIMESTAMP(3) WHERE scope = ? AND name = ?');
            $st->execute([$done, $scope, $key]);
            $st->close();
            $db->commit();
        } catch (Throwable $e) {
            $db->rollback();
            throw $e;
        }
        return $out === null ? null : $out + ['repeated' => true];
    }
    return $busy;
}

/** A failed creation frees its key (unless its job got queued: then a repeat rebuilds the answer from it). */
function models_spawn_release(mysqli $db, string $parent, string $key, string $jobId): void
{
    if (share_q($db, 'SELECT 1 FROM jobs WHERE job_id = ?', [$jobId])) {
        return;
    }
    $st = $db->prepare("DELETE FROM kv WHERE scope = ? AND name = ? AND JSON_VALUE(data, '$.jobId') = ?");
    $st->execute(['subtask-key:' . $parent, $key, $jobId]);
    $st->close();
}

/** subtask_poll (host key): state and answer of up to 20 jobs. */
function models_subtask_poll(mysqli $db, array $in): array
{
    $ids = $in['jobIds'] ?? null;
    if (!is_array($ids) || !array_is_list($ids) || $ids === [] || count($ids) > MODELS_POLL_MAX) {
        throw new InvalidArgumentException('jobIds: 1..20 job ids');
    }
    $out = [];
    $st = $db->prepare("SELECT job_id, state, IFNULL(status, '') AS status, chat_id,
                               JSON_VALUE(result, '$.agentFinalResponse') AS answer FROM jobs WHERE job_id = ?");
    foreach ($ids as $id) {
        if (!is_string($id) || preg_match('/^JOB-\d{14}-[A-Z0-9]{6}$/', $id) !== 1) {
            throw new InvalidArgumentException('invalid job id');
        }
        $st->execute([$id]);
        $r = $st->get_result()->fetch_assoc();
        $out[] = $r ? ['jobId' => $id, 'state' => (string)$r['state'], 'status' => (string)$r['status'],
            'chatId' => (string)$r['chat_id'], 'answer' => $r['answer'] !== null ? (string)$r['answer'] : '']
            : ['jobId' => $id, 'state' => 'missing', 'status' => '', 'chatId' => '', 'answer' => ''];
    }
    $st->close();
    return ['jobs' => $out];
}
