<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// modules-lib.php (schema 27, Freedom F10, SPEC §3.6): the server side of modules. The core knows no module by name:
// it reads the rows of `modules`, and a module comes in through extension points only.
//
// Server code of a module (SPEC §3.6 principle 4, decision 0.9.1): only a standard module (signed by the maintainers,
// checked at home by install/freedom_modules.py) may bring PHP. Its files live in ~/co-agent/modules/<id>/ and are
// included only while the module is enabled and every file matches the sha256 registered at install. The entry file
// (manifest server.entry) returns ['user' => [op => fn], 'push' => [...], 'host' => [...], 'routes' => [...],
// 'install' => fn, 'remove' => fn]; a handler gets the context of the call (co_module_ctx) and the request.
//   user  - db.php ops of signed-in members (session or bearer); CO Web calls them for the module's interface
//   push  - push.php ops with the push key (home PC services of a module, e.g. a status put)
//   host  - db.php ops with the host key
//   routes - agent/module.php?m=<id>&r=<route> (GET endpoints such as map tiles; the handler checks access itself)
//   uploads - agent/module.php?m=<id>&u=<name> (POST multipart from CO Web for the module's page, ext/1 coExt.upload;
//            the core admits only signed-in members and hands the handler their env, manifest server.uploads)
//   search - search.provider hooks (search-lib.php): 'expand' (other names of query words), 'chats' (one more ranked
//            list of chats, merged by rank with the core's full-text lists), 'items' (more results such as documents),
//            'text' (indexed text of a project file); manifest provides.search [{id, hooks}]
//   notify - copies of pushes (push-lib.php co_push_mirror): hook 'push' gets {envs, title, body, url, tag} of every push
//            the server sends, e.g. to write it to the person in a messenger; manifest provides.notify [{id, hooks}]
// Core op names always win: a module cannot take over an op of the core.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/db-lib.php';
require_once __DIR__ . '/members-lib.php';
require_once __DIR__ . '/push-lib.php';

const CO_EXT_VERSION = 1;
const CO_MODULE_ID_RE = '/^[a-z0-9][a-z0-9-]{0,62}$/';
const CO_MODULE_OP_RE = '/^[a-z][a-z0-9_]{2,40}$/';
const CO_MODULE_PERSON_MODES = ['auto', 'ask', 'off'];
// ui.composer: a small page in the chat composer row (an input action such as voice input) that may put text into the
// message box (ext/1 coExt.input, permission composer.input); ui.page: a page in the centre with a button in the sidebar;
// ui.hint: a hidden page that reads the composer text (permission composer.read) and answers with a hint
const CO_MODULE_SLOTS = ['ui.header', 'ui.page', 'ui.panel', 'ui.settings', 'ui.composer', 'ui.hint'];
const CO_MODULE_NAME_RE = '/^[a-z][a-z0-9_-]{0,31}$/';

function co_module_home(string $id): string
{
    if (!preg_match(CO_MODULE_ID_RE, $id)) throw new InvalidArgumentException('bad module id');
    return co_agent_home() . '/co-agent/modules/' . $id;
}

/** Data folder of a module on the server (created 0700); kept when the module is disabled. */
function co_module_state_dir(string $id): string
{
    if (!preg_match(CO_MODULE_ID_RE, $id)) throw new InvalidArgumentException('bad module id');
    $d = co_push_state_dir() . '/modules/' . $id;
    if (!is_dir($d)) @mkdir($d, 0700, true);
    return $d;
}

function co_module_row(array $r): array
{
    $man = json_decode((string)$r['manifest_json'], true);
    $files = json_decode((string)$r['files_json'], true);
    return ['id' => (string)$r['id'], 'version' => (string)$r['version'], 'enabled' => (int)$r['enabled'] === 1,
        'standard' => (int)$r['standard'] === 1, 'publisher' => (string)$r['publisher'],
        'manifest' => is_array($man) ? $man : [], 'files' => is_array($files) ? $files : [],
        'installedUtc' => co_db_iso($r['installed_utc'] ?? null), 'updatedUtc' => co_db_iso($r['updated_utc'] ?? null)];
}

/** All installed modules by id (cached per request; $fresh after a change). Empty before schema 27. */
function co_modules(mysqli $db, bool $fresh = false): array
{
    static $cache = null;
    if ($cache !== null && !$fresh) return $cache;
    $cache = [];
    try {
        $res = $db->query('SELECT * FROM modules ORDER BY id');
        while ($r = $res->fetch_assoc()) $cache[(string)$r['id']] = co_module_row($r);
        $res->free();
    } catch (mysqli_sql_exception) {
        $cache = []; // schema < 27
    }
    return $cache;
}

function co_module_log(mysqli $db, string $id, string $action, string $version, string $by, array $detail = []): void
{
    $st = $db->prepare('INSERT INTO module_log (module_id, action, version, by_env, detail_json, utc) VALUES (?, ?, ?, ?, ?, UTC_TIMESTAMP(3))');
    $json = json_encode((object)$detail, JSON_UNESCAPED_UNICODE);
    $st->bind_param('sssss', $id, $action, $version, $by, $json);
    $st->execute();
    $st->close();
}

/** Ops a module declares for one channel (user|push|host), checked against the name rule. */
function co_module_ops(array $m, string $via): array
{
    $ops = $m['manifest']['server']['ops'][$via] ?? [];
    return array_values(array_filter(is_array($ops) ? $ops : [], static fn($o): bool => is_string($o) && preg_match(CO_MODULE_OP_RE, $o) === 1));
}

/** Upload names a module declares (manifest server.uploads), checked against the name rule. */
function co_module_uploads(array $m): array
{
    $u = $m['manifest']['server']['uploads'] ?? [];
    return array_values(array_filter(is_array($u) ? $u : [], static fn($x): bool => is_string($x) && preg_match(CO_MODULE_NAME_RE, $x) === 1));
}

/**
 * Handlers of one hook group ('search', ...) of every enabled standard module that declares it (manifest
 * provides.<group>: [{id, hooks: [...]}]), in the order of the module list: [['module' => row, 'id' => provider id,
 * 'hooks' => [name => callable]]]. A module whose files fail the check is left out (logged), never breaks the core.
 */
function co_module_hooks(mysqli $db, string $group): array
{
    $out = [];
    foreach (co_modules($db) as $m) {
        $decl = $m['manifest']['provides'][$group] ?? null;
        if (!$m['enabled'] || !$m['standard'] || !is_array($decl) || !$decl) continue;
        try {
            $h = co_module_load($m)[$group] ?? null;
        } catch (Throwable $e) {
            error_log("module {$m['id']} $group: " . $e->getMessage());
            continue;
        }
        if (!is_array($h)) continue;
        foreach ($decl as $d) {
            $pid = is_array($d) ? (string)($d['id'] ?? '') : '';
            if (!preg_match(CO_MODULE_NAME_RE, $pid)) continue;
            $hooks = [];
            foreach (is_array($d['hooks'] ?? null) ? $d['hooks'] : [] as $name) {
                $fn = is_string($name) ? ($h[$pid][$name] ?? null) : null;
                if (is_callable($fn)) $hooks[$name] = $fn;
            }
            if ($hooks) $out[] = ['module' => $m, 'id' => $pid, 'hooks' => $hooks];
        }
    }
    return $out;
}

/** The enabled module that declares $op on channel $via, or null. */
function co_module_for_op(mysqli $db, string $via, string $op): ?array
{
    foreach (co_modules($db) as $m) {
        if ($m['enabled'] && in_array($op, co_module_ops($m, $via), true)) return $m;
    }
    return null;
}

/** Includes the server entry of an enabled standard module after checking every registered file; returns its handlers. */
function co_module_load(array $m): array
{
    static $loaded = [];
    $id = $m['id'];
    if (isset($loaded[$id])) return $loaded[$id];
    if (!$m['enabled']) throw new RuntimeException("module $id is disabled");
    if (!$m['standard']) throw new RuntimeException("module $id is not signed by the maintainers: no server code");
    $entry = (string)($m['manifest']['server']['entry'] ?? '');
    if (!preg_match('#^server/[A-Za-z0-9._/-]+\.php$#', $entry) || str_contains($entry, '..') || !isset($m['files'][$entry])) {
        throw new RuntimeException("module $id has no valid server entry");
    }
    $home = co_module_home($id);
    foreach ($m['files'] as $rel => $sha) {
        if (!is_string($rel) || !preg_match('#^server/[A-Za-z0-9._/-]+\.php$#', $rel) || str_contains($rel, '..')) continue;
        $f = "$home/$rel";
        if (!is_file($f) || !hash_equals((string)$sha, (string)hash_file('sha256', $f))) {
            throw new RuntimeException("module $id: $rel does not match its install");
        }
    }
    $h = (static fn(string $file): mixed => include $file)("$home/$entry");
    if (!is_array($h)) throw new RuntimeException("module $id: entry returned no handlers");
    return $loaded[$id] = $h;
}

/** Context handed to a module handler. */
function co_module_ctx(mysqli $db, array $m, string $myEnv = '', bool $owner = false, string $email = ''): array
{
    $id = $m['id'];
    return ['db' => $db, 'module' => $id, 'version' => $m['version'], 'myEnv' => $myEnv, 'owner' => $owner, 'email' => $email,
        'stateDir' => co_module_state_dir($id), 'home' => co_module_home($id), 'config' => co_module_config($id),
        'person' => static fn(string $env): string => co_module_person_mode($db, $m, $env)];
}

/**
 * Settings of a module from the owner's install.json (manifest installConfig {key: [install.json paths]}); the installer
 * sends the values with op module_install and they live in the module's data folder (config.json). Never secrets.
 */
function co_module_config(string $id): array
{
    $c = json_decode((string)@file_get_contents(co_module_state_dir($id) . '/config.json'), true);
    return is_array($c) ? $c : [];
}

function co_module_config_put(string $id, array $man, mixed $in): void
{
    if (!is_array($in)) return;
    $decl = is_array($man['installConfig'] ?? null) ? $man['installConfig'] : [];
    $cfg = co_module_config($id);
    foreach ($in as $k => $v) {
        if (!is_string($k) || !array_key_exists($k, $decl) || !preg_match('/^[A-Za-z][A-Za-z0-9_]{0,40}$/', $k)) continue;
        if (is_string($v) && mb_strlen($v) <= 500 && !preg_match('/[\x00-\x1f]/', $v)) $cfg[$k] = $v;
    }
    co_push_write_atomic(co_module_state_dir($id) . '/config.json', json_encode((object)$cfg, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
}

/** Runs op $op of channel $via; null when no enabled module declares it. */
function co_module_call(mysqli $db, string $via, string $op, array $in, string $myEnv = '', bool $owner = false, string $email = ''): ?array
{
    $m = co_module_for_op($db, $via, $op);
    if ($m === null) return null;
    $h = co_module_load($m)[$via][$op] ?? null;
    if (!is_callable($h)) throw new RuntimeException("module {$m['id']} declares $op but has no handler");
    $r = $h(co_module_ctx($db, $m, $myEnv, $owner, $email), $in);
    return is_array($r) ? $r : ['ok' => true];
}

// ---------- the person's own switch (SPEC §3.6 personal, decision 0.9.1) ----------

function co_module_personal(array $m): bool
{
    return is_array($m['manifest']['personal'] ?? null);
}

/** auto | ask | off for $env: the own row, else the owner's default row, else the manifest default (ask). */
function co_module_person_mode(mysqli $db, array $m, string $env): string
{
    if (!co_module_personal($m)) return 'auto';
    $st = $db->prepare('SELECT mode FROM module_people WHERE module_id = ? AND env = ?');
    $st->bind_param('ss', $m['id'], $env);
    $st->execute();
    $mode = (string)($st->get_result()->fetch_row()[0] ?? '');
    $st->close();
    if (in_array($mode, CO_MODULE_PERSON_MODES, true)) return $mode;
    $def = (string)($m['manifest']['personal']['default'] ?? 'ask');
    return in_array($def, CO_MODULE_PERSON_MODES, true) ? $def : 'ask';
}

/** Rows of a module: [env => [mode, own]] (own = the person chose it). */
function co_module_people(mysqli $db, string $id): array
{
    $st = $db->prepare('SELECT env, mode, set_by FROM module_people WHERE module_id = ?');
    $st->bind_param('s', $id);
    $st->execute();
    $out = [];
    foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as $r) $out[(string)$r[0]] = ['mode' => (string)$r[1], 'own' => $r[0] === $r[2]];
    $st->close();
    return $out;
}

/**
 * Sets the switch of $env. The person sets the own one any time; the owner (or the installer, $by = '') sets only a
 * default, and never over a choice the person made.
 */
function co_module_person_set(mysqli $db, string $id, string $env, string $mode, string $by): array
{
    $m = co_modules($db)[$id] ?? null;
    if ($m === null) return ['ok' => false, 'error' => 'not_found'];
    if (!co_module_personal($m)) return ['ok' => false, 'error' => 'not_personal'];
    if (!in_array($mode, CO_MODULE_PERSON_MODES, true)) return ['ok' => false, 'error' => 'bad_mode'];
    if (!in_array($env, co_member_envs(), true)) return ['ok' => false, 'error' => 'bad_env'];
    $people = co_module_people($db, $id);
    if ($by !== $env && !empty($people[$env]['own'])) return ['ok' => false, 'error' => 'person_decides'];
    $setBy = $by === '' ? 'installer' : $by;
    if ($by === $env) {
        $st = $db->prepare('INSERT INTO module_people (module_id, env, mode, set_by, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE mode = VALUES(mode), set_by = VALUES(set_by), updated_utc = VALUES(updated_utc)');
    } else {
        // a default never overwrites the person's own choice, also one saved a moment ago (the check is in the update;
        // the assignments see the old set_by, as mode and set_by come before updated_utc)
        $st = $db->prepare('INSERT INTO module_people (module_id, env, mode, set_by, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE mode = IF(set_by = env, mode, VALUES(mode)),
            updated_utc = IF(set_by = env, updated_utc, VALUES(updated_utc)), set_by = IF(set_by = env, set_by, VALUES(set_by))');
    }
    $st->bind_param('ssss', $id, $env, $mode, $setBy);
    $st->execute();
    $st->close();
    if ($by !== $env && !empty(co_module_people($db, $id)[$env]['own'])) return ['ok' => false, 'error' => 'person_decides'];
    co_module_log($db, $id, 'person', $m['version'], $setBy, ['env' => $env, 'mode' => $mode]);
    return ['ok' => true, 'mode' => $mode];
}

// ---------- what CO Web sees ----------

/** Interface entries of a module for CO Web (only declared slots, paths inside ui/). */
function co_module_ui(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['ui'] ?? null) ? $m['manifest']['provides']['ui'] : [] as $u) {
        if (!is_array($u)) continue;
        $slot = (string)($u['slot'] ?? '');
        $entry = (string)($u['entry'] ?? '');
        if (!in_array($slot, CO_MODULE_SLOTS, true) || !preg_match('#^ui/[A-Za-z0-9._/-]+\.html$#', $entry) || str_contains($entry, '..')) continue;
        $row = ['slot' => $slot, 'id' => (string)($u['id'] ?? $slot), 'entry' => $entry, 'title' => mb_substr((string)($u['title'] ?? ''), 0, 120)];
        if (is_string($u['label'] ?? null) && $u['label'] !== '') $row['label'] = mb_substr($u['label'], 0, 40);
        // ui.page: an extra top-level address of CO Web (e.g. a link that existed before the feature became a module);
        // CO Web never lets it shadow one of its own routes
        $path = (string)($u['path'] ?? '');
        if ($slot === 'ui.page' && preg_match(CO_MODULE_PATH_RE, $path) && !in_array(explode('/', $path)[1], CO_MODULE_CORE_PATHS, true)) $row['path'] = $path;
        // ext/1 ui.window: a desktop app (CO Desktop, CO Resident) opens this page in its own window when CO Web opens it
        // in a new tab; windowKey = the query parameter that names one window (one window per value, e.g. per file)
        if ($slot === 'ui.page' && ($u['window'] ?? false) === true) {
            $row['window'] = true;
            if (is_string($u['windowKey'] ?? null) && preg_match('#^[A-Za-z][A-Za-z0-9_-]{0,30}$#', $u['windowKey'])) $row['windowKey'] = $u['windowKey'];
        }
        $out[] = $row;
    }
    return $out;
}

/**
 * ext/1 provides.client {kind: "browser-extension", id}: origins of the browser extensions of enabled modules. Their
 * panel frames CO Web, so module pages (agent/module.php) allow them as frame ancestors; CO Web's own page gets the same
 * list at build time from the modules of its tree (co-web/vite.config.ts, .htaccess).
 */
function co_module_ext_origins(mysqli $db): array
{
    $out = [];
    foreach (co_modules($db) as $m) {
        $c = $m['manifest']['provides']['client'] ?? null;
        if ($m['enabled'] && is_array($c) && ($c['kind'] ?? '') === 'browser-extension' && preg_match('#^[a-p]{32}$#', (string)($c['id'] ?? ''))) {
            $out[] = 'chrome-extension://' . $c['id'];
        }
    }
    return array_values(array_unique($out));
}

/** Outside services a module imports from (manifest provides.import): CO Web labels imported chats with them. */
function co_module_imports(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['import'] ?? null) ? $m['manifest']['provides']['import'] : [] as $x) {
        if (!is_array($x) || !preg_match('/^[a-z0-9][a-z0-9-]{0,31}$/', (string)($x['source'] ?? ''))) continue;
        $out[] = ['source' => (string)$x['source'], 'label' => mb_substr((string)($x['label'] ?? $x['source']), 0, 40),
            'speaker' => mb_substr((string)($x['speaker'] ?? $x['label'] ?? $x['source']), 0, 60)];
    }
    return $out;
}

const CO_MODULE_TOOL_RE = '/^[a-z][a-z0-9_]{2,63}$/';
const CO_MODULE_DEVICE_FILE_RE = '#^device/[A-Za-z0-9._/-]+$#';

/**
 * What a device (CO Desktop, CO Resident) needs of an enabled module (ext/1 device.tools): the tools it declares with
 * their risk class (manifest provides.deviceTools [{name, class (or risk), description, input, run, platforms, untrusted}]), the
 * device ops its settings page may call on the device it is shown on (provides.deviceOps; handled by the module's
 * native part in the app or by the device/ file manifest device.ops), the sha256 of its device/ files (the device
 * downloads them from module.php and checks them) and the per-device default (manifest device {default: on|off,
 * warning, ops}). The device decides what it loads; the server only lists.
 */
function co_module_device(array $m): array
{
    $man = $m['manifest'];
    $tools = [];
    foreach (is_array($man['provides']['deviceTools'] ?? null) ? $man['provides']['deviceTools'] : [] as $t) {
        if (!is_array($t)) continue;
        $name = (string)($t['name'] ?? '');
        $class = (string)($t['class'] ?? $t['risk'] ?? '');   // "risk" is accepted as another name of the class
        if (!preg_match(CO_MODULE_TOOL_RE, $name) || !in_array($class, ['R', 'W', 'X', 'N', 'E', 'D'], true)) continue;
        $run = (string)($t['run'] ?? '');
        if ($run !== '' && (!preg_match(CO_MODULE_DEVICE_FILE_RE, $run) || str_contains($run, '..') || !isset($m['files'][$run]))) continue;
        $tools[] = ['name' => $name, 'class' => $class, 'description' => mb_substr((string)($t['description'] ?? ''), 0, 2000),
            'input' => is_array($t['input'] ?? null) ? $t['input'] : null, 'run' => $run,
            'platforms' => array_values(array_filter(is_array($t['platforms'] ?? null) ? $t['platforms'] : [], 'is_string')),
            'untrusted' => ($t['untrusted'] ?? true) !== false];
    }
    $ops = array_values(array_filter(is_array($man['provides']['deviceOps'] ?? null) ? $man['provides']['deviceOps'] : [],
        static fn($o): bool => is_string($o) && preg_match(CO_MODULE_OP_RE, $o) === 1));
    $files = [];
    foreach ($m['files'] as $rel => $sha) {
        if (is_string($rel) && preg_match(CO_MODULE_DEVICE_FILE_RE, $rel) && !str_contains($rel, '..')) $files[$rel] = (string)$sha;
    }
    // the owner saw the permission device.tools when the module was enabled; without it a device gets nothing
    if (!in_array('device.tools', is_array($man['permissions'] ?? null) ? $man['permissions'] : [], true)) {
        $tools = $ops = $files = [];
    }
    $dev = is_array($man['device'] ?? null) ? $man['device'] : [];
    $opsRun = (string)($dev['ops'] ?? '');
    if (!isset($files[$opsRun])) $opsRun = '';
    return ['tools' => $tools, 'ops' => $ops, 'opsRun' => $opsRun, 'files' => (object)$files,
        'default' => ($dev['default'] ?? 'on') === 'off' ? 'off' : 'on', 'warning' => mb_substr((string)($dev['warning'] ?? ''), 0, 1000)];
}

// ---------- ext/1 declarations read by CO Web (chat.mode, context, file actions, message views, side jobs) ----------

const CO_MODULE_PATH_RE = '#^/[a-z0-9][a-z0-9-]{1,40}$#';
const CO_MODULE_CORE_PATHS = ['e', 'm', 'agent', 'auth', 'ext', 'assets', 'api', 'desktop', 'mac'];
const CO_MODULE_KEY_RE = '/^[a-z][A-Za-z0-9]{2,30}$/';
// fields of chats, projects and events of the core: a mode or a side job of a module cannot take them
const CO_MODULE_CORE_KEYS = ['schemaVersion', 'eventId', 'sequence', 'createdUtc', 'chatId', 'branchId', 'jobId', 'hostId', 'agent',
    'author', 'text', 'type', 'status', 'error', 'attachments', 'returnFiles', 'conversation', 'mode', 'model', 'effort', 'title',
    'titleAuto', 'projectId', 'environmentId', 'archived', 'parent', 'permissions', 'contextMode', 'autoSwitch', 'browser',
    'family', 'folderId', 'description', 'defaultAgentId', 'defaultMode', 'updatedUtc', 'movedUtc', 'draft', 'legacy', 'shareRole',
    'artifacts', 'sessionId', 'usage', 'imported', 'followupOf', 'request', 'result', 'instruction', 'output',
    'rootBranchId', 'createdUtc', 'importedFrom', 'summary', 'shareVia', 'sharedChatIds', 'kind', 'contextFolders',
    'returnFiles', 'modelReported', 'exportSkipped', 'workbenchNote',
    'contextPack', 'contextReason', 'queue', 'autoSwitchFrom', 'missingArtifacts', 'completedUtc', 'hints',
    'autoTitle', 'emoji', 'target', 'remove', 'replyTo', 'coPending', 'coFor', 'chatId', 'schemaVersion'];

/** Text of a registered file of the module (context/*.md|txt, up to 32 KB) whose sha256 still matches; '' otherwise. */
function co_module_text(array $m, string $rel): string
{
    if (!preg_match('#^context/[A-Za-z0-9._/-]+\.(md|txt)$#', $rel) || str_contains($rel, '..') || !isset($m['files'][$rel])) return '';
    $f = co_module_home($m['id']) . "/$rel";
    if (!is_file($f) || filesize($f) > 32768) return '';
    $t = (string)file_get_contents($f);
    if (!hash_equals((string)$m['files'][$rel], hash('sha256', $t))) return '';
    return rtrim(str_replace("\r\n", "\n", $t));
}

/** A short single-line string of a declaration ('' when missing or too long). */
function co_module_decl_str(mixed $v, int $max = 300): string
{
    return is_string($v) && mb_strlen($v) <= $max && !preg_match('/[\x00-\x08\x0b-\x1f\x7f]/', $v) ? $v : '';
}

/**
 * ext/1 chat.mode: modes a chat can switch on (the agent gets the module's rules while it is on, a short note once it
 * was switched off). State lives in the chat document under the mode key: <key> (bool), <key>Log (switches), and the
 * project default <key>Default; a viewer (ui.panel) opens with ?<param>=<path> next to the chat (<key>Doc of the chat).
 */
function co_module_modes(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['modes'] ?? null) ? $m['manifest']['provides']['modes'] : [] as $d) {
        if (!is_array($d)) continue;
        $key = (string)($d['key'] ?? '');
        if (!preg_match(CO_MODULE_KEY_RE, $key) || in_array($key, CO_MODULE_CORE_KEYS, true)) continue;
        $rules = co_module_text($m, (string)($d['rules'] ?? ''));
        if ($rules === '') continue;
        $row = ['key' => $key, 'label' => co_module_decl_str($d['label'] ?? $key, 60), 'short' => co_module_decl_str($d['short'] ?? '', 30),
            'icon' => co_module_decl_str($d['icon'] ?? '', 8), 'rules' => $rules, 'offNote' => co_module_text($m, (string)($d['offNote'] ?? '')),
            'hintOn' => co_module_decl_str($d['hintOn'] ?? ''), 'hintOff' => co_module_decl_str($d['hintOff'] ?? ''),
            'projectLabel' => co_module_decl_str($d['projectLabel'] ?? '', 80), 'newProjectHint' => co_module_decl_str($d['newProjectHint'] ?? ''),
            'inherit' => !empty($d['inherit'])];
        $v = $d['viewer'] ?? null;
        if (is_array($v) && preg_match('/^[a-z][a-z0-9]{1,20}$/', (string)($v['param'] ?? '')) && is_string($v['panel'] ?? null)) {
            $row['viewer'] = ['panel' => (string)$v['panel'], 'param' => (string)$v['param'], 'titlePrefix' => co_module_decl_str($v['titlePrefix'] ?? '', 12)];
        }
        $out[] = $row;
    }
    return $out;
}

/** ext/1 context: fragments of instructions every job of the environment gets while the module is enabled. */
function co_module_context(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['context'] ?? null) ? $m['manifest']['provides']['context'] : [] as $c) {
        $rel = is_string($c) ? $c : (string)($c['entry'] ?? '');
        $t = co_module_text($m, $rel);
        if ($t !== '') $out[] = $t;
    }
    return $out;
}

/** ext/1 ui.command for files: items of the file menu (open a module page or a mode viewer for some extensions). */
function co_module_file_actions(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['fileActions'] ?? null) ? $m['manifest']['provides']['fileActions'] : [] as $a) {
        if (!is_array($a) || !is_array($a['exts'] ?? null) || !is_array($a['open'] ?? null)) continue;
        $exts = array_values(array_filter($a['exts'], static fn($e): bool => is_string($e) && preg_match('/^[a-z0-9]{1,10}$/', $e) === 1));
        $open = $a['open'];
        $o = isset($open['page']) ? ['page' => co_module_decl_str($open['page'], 40), 'param' => co_module_decl_str($open['param'] ?? 'path', 20),
            'target' => ($open['target'] ?? '') === 'blank' ? 'blank' : 'self'] : (isset($open['mode']) ? ['mode' => co_module_decl_str($open['mode'], 31)] : null);
        if (!$exts || $o === null) continue;
        $out[] = ['id' => co_module_decl_str($a['id'] ?? '', 40), 'label' => co_module_decl_str($a['label'] ?? '', 60),
            'title' => co_module_decl_str($a['title'] ?? ''), 'exts' => $exts, 'exclude' => co_module_decl_str($a['exclude'] ?? '', 120), 'open' => $o];
    }
    return $out;
}

/** ext/1 message views: how a person sees messages a module page wrote (a short label instead of a technical tag). */
function co_module_message_views(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['messageViews'] ?? null) ? $m['manifest']['provides']['messageViews'] : [] as $v) {
        if (!is_array($v) || co_module_decl_str($v['match'] ?? '', 400) === '') continue;
        $out[] = ['match' => $v['match'], 'label' => co_module_decl_str($v['label'] ?? '', 120),
            'strip' => array_values(array_filter((array)($v['strip'] ?? []), static fn($s): bool => co_module_decl_str($s, 400) !== '')),
            'replace' => array_values(array_filter((array)($v['replace'] ?? []), static fn($p): bool => is_array($p) && count($p) === 2
                && co_module_decl_str($p[0], 1000) !== '' && is_string($p[1]) && mb_strlen($p[1]) <= 400))];
    }
    return $out;
}

/** ext/1 side jobs: jobs a module queues into a chat under its own event key; CO Web collapses each into one row. */
function co_module_side_jobs(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['sideJobs'] ?? null) ? $m['manifest']['provides']['sideJobs'] : [] as $s) {
        $key = is_array($s) ? (string)($s['key'] ?? '') : '';
        if (!preg_match(CO_MODULE_KEY_RE, $key) || in_array($key, CO_MODULE_CORE_KEYS, true)) continue;
        $out[] = ['key' => $key, 'label' => co_module_decl_str($s['label'] ?? $key, 40), 'list' => co_module_decl_str($s['list'] ?? '', 30),
            'item' => co_module_decl_str($s['item'] ?? '', 30), 'count' => co_module_decl_str($s['count'] ?? '', 30),
            'countNoun' => co_module_decl_str($s['countNoun'] ?? '', 30)];
    }
    return $out;
}

/**
 * ext/1 navigate: where a page of the module may send the whole window (an https origin, a path of this site, or the
 * origin of a value of the module's install config). CO Web refuses every other target.
 */
function co_module_navigate(array $m): array
{
    $out = [];
    foreach (is_array($m['manifest']['provides']['navigate'] ?? null) ? $m['manifest']['provides']['navigate'] : [] as $n) {
        if (is_array($n) && is_string($n['config'] ?? null)) $n = (string)(co_module_config($m['id'])[$n['config']] ?? '');
        if (!is_string($n)) continue;
        if (preg_match('#^/[A-Za-z0-9._/-]{1,80}$#', $n) && !str_contains($n, '..') && !str_starts_with($n, '//')) {
            $out[] = $n;
        } elseif (preg_match('#^(https://[a-z0-9.-]+(?::\d{1,5})?)(?:/.*)?$#i', $n, $mm)) {
            $out[] = strtolower($mm[1]);
        }
    }
    return array_values(array_unique($out));
}

/** Event keys of side jobs of every installed module (also disabled ones: their old rows stay out of the inbox). */
function co_module_side_keys(mysqli $db): array
{
    $keys = [];
    foreach (co_modules($db) as $m) foreach (co_module_side_jobs($m) as $s) $keys[$s['key']] = true;
    return array_keys($keys);
}

/**
 * db.php op modules_list: enabled modules for everybody (interface + own switch); the owner also sees disabled ones and
 * every switch. uiOps never contains an op of the core ($coreOps): CO Web lets a module page call only these, so a
 * manifest cannot hand its page the core's chats or dossier.
 */
function co_modules_list(mysqli $db, string $myEnv, bool $owner, array $coreOps = []): array
{
    $out = [];
    $labels = [];
    foreach (co_modules($db) as $m) {
        // names of import sources of every installed module: chats imported earlier keep their label while it is off
        foreach (co_module_imports($m) as $x) $labels[$x['source']] ??= $x;
        if (!$m['enabled'] && !$owner) continue;
        $man = $m['manifest'];
        $row = ['id' => $m['id'], 'version' => $m['version'], 'title' => (string)($man['title'] ?? $m['id']),
            'description' => (string)($man['description'] ?? ''), 'enabled' => $m['enabled'], 'standard' => $m['standard'],
            'permissions' => array_values(array_filter(is_array($man['permissions'] ?? null) ? $man['permissions'] : [], 'is_string')),
            'ui' => $m['enabled'] ? co_module_ui($m) : [], 'imports' => $m['enabled'] ? co_module_imports($m) : [], 'uiOps' => array_values(array_diff(co_module_ops($m, 'user'), $coreOps)),
            'uploads' => co_module_uploads($m),
            'device' => $m['enabled'] ? co_module_device($m) : null,
            'personal' => co_module_personal($m) ? ['about' => (string)($man['personal']['about'] ?? 'members'), 'mode' => co_module_person_mode($db, $m, $myEnv),
                'question' => (string)($man['personal']['question'] ?? '')] : null];
        // what CO Web weaves into its own screens (only while enabled: a disabled module leaves no trace there)
        $on = $m['enabled'];
        $row += ['modes' => $on ? co_module_modes($m) : [], 'context' => $on ? co_module_context($m) : [],
            'fileActions' => $on ? co_module_file_actions($m) : [], 'messageViews' => $on ? co_module_message_views($m) : [],
            // side-job rows of a switched-off module stay collapsed (old turns), so these come always
            'sideJobs' => co_module_side_jobs($m), 'navigate' => $on ? co_module_navigate($m) : []];
        if ($owner) {
            $people = co_module_people($db, $m['id']);
            $row['people'] = co_module_personal($m) ? array_map(static fn(string $env): array => ['env' => $env,
                'mode' => co_module_person_mode($db, $m, $env), 'own' => !empty($people[$env]['own'])], co_member_envs()) : [];
            $row['installedUtc'] = $m['installedUtc'];
            $row['updatedUtc'] = $m['updatedUtc'];
            $row['publisher'] = $m['publisher'];
        }
        $out[] = $row;
    }
    return ['ext' => CO_EXT_VERSION, 'modules' => $out, 'importLabels' => array_values($labels)];
}

/** db.php op module_set (owner): enable or disable. Data stays either way. */
function co_module_set(mysqli $db, string $id, bool $enabled, string $by): array
{
    $m = co_modules($db)[$id] ?? null;
    if ($m === null) return ['ok' => false, 'error' => 'not_found'];
    $v = $enabled ? 1 : 0;
    $st = $db->prepare('UPDATE modules SET enabled = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
    $st->bind_param('is', $v, $id);
    $st->execute();
    $st->close();
    co_module_log($db, $id, $enabled ? 'enable' : 'disable', $m['version'], $by);
    co_modules($db, true);
    return ['ok' => true, 'enabled' => $enabled];
}

// ---------- installer side (db.php op modules_host, host key) ----------

/**
 * Copies the data folder before a migration; keeps the 2 newest snapshots (SPEC §3.6 step 4, §11.22). The copy goes to
 * a .part folder first; any failure (disk full, permissions) throws before older snapshots are pruned or data migrated.
 */
function co_module_snapshot(string $id, string $version): string
{
    $src = co_module_state_dir($id);
    $base = dirname($src) . '/.snap';
    if (!is_dir($base) && !@mkdir($base, 0700, true) && !is_dir($base)) throw new RuntimeException('snapshot: cannot create ' . basename($base));
    $dst = "$base/$id@" . preg_replace('/[^0-9A-Za-z.-]/', '_', $version) . '@' . gmdate('YmdHis');
    $part = "$dst.part";
    co_module_rmtree($part);
    try {
        if (is_dir($src)) co_module_copy($src, $part);
        elseif (!@mkdir($part, 0700, true)) throw new RuntimeException('snapshot: cannot create the folder');
    } catch (Throwable $e) {
        co_module_rmtree($part);
        throw $e;
    }
    co_module_rmtree($dst);
    if (!@rename($part, $dst)) {
        co_module_rmtree($part);
        throw new RuntimeException('snapshot: cannot finish');
    }
    $old = array_filter(glob("$base/$id@*") ?: [], static fn(string $o): bool => !str_ends_with($o, '.part'));
    usort($old, static fn(string $a, string $b): int => strcmp(substr($a, strrpos($a, '@')), substr($b, strrpos($b, '@'))));
    foreach (array_slice($old, 0, max(0, count($old) - 2)) as $o) co_module_rmtree($o);
    return $dst;
}

/** Recursive copy; throws on the first folder, listing or file that fails. */
function co_module_copy(string $src, string $dst): void
{
    if (!is_dir($dst) && !@mkdir($dst, 0700, true) && !is_dir($dst)) throw new RuntimeException('snapshot: mkdir failed');
    $list = @scandir($src);
    if ($list === false) throw new RuntimeException('snapshot: cannot list ' . basename($src));
    foreach ($list as $f) {
        if ($f === '.' || $f === '..') continue;
        if (is_dir("$src/$f")) co_module_copy("$src/$f", "$dst/$f");
        elseif (!@copy("$src/$f", "$dst/$f")) throw new RuntimeException("snapshot: cannot copy $f");
    }
}

function co_module_rmtree(string $d): void
{
    if (!is_dir($d)) {
        @unlink($d);
        return;
    }
    foreach (scandir($d) ?: [] as $f) if ($f !== '.' && $f !== '..') co_module_rmtree("$d/$f");
    @rmdir($d);
}

/**
 * op module_install {manifest, files: {rel: sha256}, standard, publisher}: registers or updates a module whose files the
 * installer has just uploaded. A new module stays disabled; an update keeps the switch. Before the module's own install
 * hook (data migration) the data folder is copied. Permissions that grew disable the module until the owner agrees.
 */
function co_module_install(mysqli $db, array $in): array
{
    $man = is_array($in['manifest'] ?? null) ? $in['manifest'] : null;
    $id = (string)($man['id'] ?? '');
    $version = (string)($man['version'] ?? '');
    if ($man === null || !preg_match(CO_MODULE_ID_RE, $id) || !preg_match('/^[0-9][0-9A-Za-z.+-]{0,31}$/', $version)) {
        throw new InvalidArgumentException('bad manifest');
    }
    if (!co_module_ext_ok((string)($man['ext'] ?? ''))) return ['ok' => false, 'error' => 'ext_incompatible', 'ext' => CO_EXT_VERSION];
    $files = is_array($in['files'] ?? null) ? $in['files'] : [];
    foreach ($files as $rel => $sha) {
        if (!is_string($rel) || !is_string($sha) || !preg_match('/^[0-9a-f]{64}$/', $sha) || str_contains($rel, '..')) throw new InvalidArgumentException('bad files');
        $f = co_module_home($id) . "/$rel";
        if (!is_file($f) || !hash_equals($sha, (string)hash_file('sha256', $f))) return ['ok' => false, 'error' => 'file_mismatch', 'file' => $rel];
    }
    // an op belongs to one module: another module's page could otherwise call it, or shadow it in co_module_for_op
    $mine = [];
    foreach (['user', 'push', 'host'] as $via) $mine = array_merge($mine, co_module_ops(['manifest' => $man], $via));
    foreach (co_modules($db) as $other) {
        if ($other['id'] === $id) continue;
        foreach (['user', 'push', 'host'] as $via) {
            $taken = array_intersect($mine, co_module_ops($other, $via));
            if ($taken) return ['ok' => false, 'error' => 'op_taken', 'ops' => array_values($taken), 'by' => $other['id']];
        }
    }
    $standard = !empty($in['standard']) ? 1 : 0;
    $publisher = mb_substr((string)($in['publisher'] ?? ''), 0, 160);
    $old = co_modules($db)[$id] ?? null;
    $enabled = $old ? (int)$old['enabled'] : 0;
    $grew = $old ? array_values(array_diff((array)($man['permissions'] ?? []), (array)($old['manifest']['permissions'] ?? []))) : [];
    if ($grew) $enabled = 0;
    try {
        $snap = $old ? co_module_snapshot($id, $old['version']) : '';
    } catch (Throwable $e) {
        // no complete copy of the data: no migration, the old registration and files stay
        return ['ok' => false, 'error' => 'snapshot_failed', 'detail' => mb_substr($e->getMessage(), 0, 300)];
    }
    $mj = json_encode($man, JSON_UNESCAPED_UNICODE);
    $fj = json_encode((object)$files);
    $st = $db->prepare('INSERT INTO modules (id, version, enabled, standard, publisher, manifest_json, files_json, installed_utc, updated_utc)
        VALUES (?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))
        ON DUPLICATE KEY UPDATE version = VALUES(version), enabled = VALUES(enabled), standard = VALUES(standard), publisher = VALUES(publisher),
        manifest_json = VALUES(manifest_json), files_json = VALUES(files_json), updated_utc = VALUES(updated_utc)');
    $st->bind_param('ssiisss', $id, $version, $enabled, $standard, $publisher, $mj, $fj);
    $st->execute();
    $st->close();
    co_module_log($db, $id, $old ? 'update' : 'install', $version, 'installer', ['from' => $old['version'] ?? '', 'standard' => $standard, 'grew' => $grew]);
    $m = co_modules($db, true)[$id];
    if (isset($in['config'])) co_module_config_put($id, $man, $in['config']);
    // the module's own migration runs with the module loaded as enabled, even while it is still switched off
    $hook = null;
    if ($standard && isset($man['server']['entry'])) {
        try {
            $h = co_module_load(array_merge($m, ['enabled' => true]));
            $hook = is_callable($h['install'] ?? null) ? $h['install'](co_module_ctx($db, $m), ['from' => $old['version'] ?? '']) : null;
        } catch (Throwable $e) {
            co_module_unregister($db, $id, $old);
            co_module_log($db, $id, 'failed', $version, 'installer', ['error' => mb_substr($e->getMessage(), 0, 300)]);
            return ['ok' => false, 'error' => 'install_hook_failed', 'detail' => mb_substr($e->getMessage(), 0, 300), 'restored' => $old['version'] ?? null];
        }
    }
    // initial switches the installer asked for (defaults only: never over a person's own choice)
    $people = [];
    foreach (is_array($in['people'] ?? null) ? $in['people'] : [] as $env => $mode) {
        if (is_string($env) && is_string($mode)) $people[$env] = co_module_person_set($db, $id, $env, $mode, '')['ok'] ?? false;
    }
    return ['ok' => true, 'id' => $id, 'version' => $version, 'enabled' => $enabled === 1, 'updated' => $old !== null,
        'grew' => $grew, 'snapshot' => $snap !== '', 'hook' => $hook, 'people' => $people];
}

/** Puts back the row a failed install replaced (or drops a new one). The installer then puts back the old files. */
function co_module_unregister(mysqli $db, string $id, ?array $old): void
{
    if ($old === null) {
        $st = $db->prepare('DELETE FROM modules WHERE id = ?');
        $st->bind_param('s', $id);
    } else {
        $en = $old['enabled'] ? 1 : 0;
        $std = $old['standard'] ? 1 : 0;
        $mj = json_encode($old['manifest'], JSON_UNESCAPED_UNICODE);
        $fj = json_encode((object)$old['files']);
        $st = $db->prepare('UPDATE modules SET version = ?, enabled = ?, standard = ?, publisher = ?, manifest_json = ?, files_json = ?, updated_utc = UTC_TIMESTAMP(3) WHERE id = ?');
        $st->bind_param('siissss', $old['version'], $en, $std, $old['publisher'], $mj, $fj, $id);
    }
    $st->execute();
    $st->close();
    co_modules($db, true);
}

/** "ext": ">=1 <2" style range (or a bare number); empty = 1. */
function co_module_ext_ok(string $range): bool
{
    $range = trim($range);
    if ($range === '') return CO_EXT_VERSION === 1;
    if (preg_match('/^\d+$/', $range)) return (int)$range === CO_EXT_VERSION;
    foreach (preg_split('/\s+/', $range) as $part) {
        if (!preg_match('/^(>=|<=|>|<|=)?(\d+)$/', $part, $p)) return false;
        $n = (int)$p[2];
        $ok = match ($p[1]) { '>=' => CO_EXT_VERSION >= $n, '<=' => CO_EXT_VERSION <= $n, '>' => CO_EXT_VERSION > $n, '<' => CO_EXT_VERSION < $n, default => CO_EXT_VERSION === $n };
        if (!$ok) return false;
    }
    return true;
}

/** op module_remove {id, data: keep|erase}: drops the registration; erase also deletes the data folder and the switches. */
function co_module_remove(mysqli $db, array $in): array
{
    $id = (string)($in['id'] ?? '');
    $m = co_modules($db)[$id] ?? null;
    if ($m === null) return ['ok' => false, 'error' => 'not_found'];
    $data = (string)($in['data'] ?? 'keep');
    if (!in_array($data, ['keep', 'erase'], true)) throw new InvalidArgumentException('data must be keep or erase');
    if ($m['standard'] && isset($m['manifest']['server']['entry'])) {
        try {
            $h = co_module_load(array_merge($m, ['enabled' => true]));
            if (is_callable($h['remove'] ?? null)) $h['remove'](co_module_ctx($db, $m), ['data' => $data]);
        } catch (Throwable $e) {
            error_log("module $id remove hook: " . $e->getMessage());
        }
    }
    $st = $db->prepare('DELETE FROM modules WHERE id = ?');
    $st->bind_param('s', $id);
    $st->execute();
    $st->close();
    if ($data === 'erase') {
        co_module_rmtree(co_module_state_dir($id));
        // the snapshots hold the same personal data
        foreach (glob(dirname(co_module_state_dir($id)) . "/.snap/$id@*") ?: [] as $o) co_module_rmtree($o);
        $st = $db->prepare('DELETE FROM module_people WHERE module_id = ?');
        $st->bind_param('s', $id);
        $st->execute();
        $st->close();
    }
    co_module_log($db, $id, 'remove', $m['version'], 'installer', ['data' => $data]);
    co_modules($db, true);
    return ['ok' => true, 'data' => $data];
}

/** op modules_host {action: list|install|remove|set|person}: the installer's channel. */
function co_modules_host(mysqli $db, array $in): array
{
    $action = (string)($in['action'] ?? 'list');
    return match ($action) {
        'list'    => ['ok' => true, 'ext' => CO_EXT_VERSION, 'modules' => array_values(array_map(static fn(array $m): array => [
            'id' => $m['id'], 'version' => $m['version'], 'enabled' => $m['enabled'], 'standard' => $m['standard'],
            'publisher' => $m['publisher'], 'files' => $m['files'],
            // Bridge's module loader (F10): whose switch lets the module work for them (personal modules only)
            'people' => co_module_personal($m) ? (object)array_combine(co_member_envs(), array_map(
                static fn(string $env): string => co_module_person_mode($db, $m, $env), co_member_envs())) : null],
            co_modules($db)))],
        'install' => co_module_install($db, $in),
        'remove'  => co_module_remove($db, $in),
        'set'     => co_module_set($db, (string)($in['id'] ?? ''), !empty($in['enabled']), 'installer'),
        'person'  => co_module_person_set($db, (string)($in['id'] ?? ''), (string)($in['env'] ?? ''), (string)($in['mode'] ?? ''), ''),
        default   => throw new InvalidArgumentException('unknown action'),
    };
}

/** A short plain string field of a request ('' when missing). */
function co_module_in_str(array $in, string $key, int $max): string
{
    $v = $in[$key] ?? '';
    if (!is_string($v) || mb_strlen($v) > $max || preg_match('/[\x00-\x1f\x7f]/', $v)) throw new InvalidArgumentException("bad $key");
    return $v;
}
