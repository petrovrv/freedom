<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// home-relay server part: the site is only the postman between the pages of a person (any module: mail, calendar, ...)
// and the relay worker of that person on the home server. Requests and answers are end-to-end encrypted (ECDH P-256 +
// AES-GCM between the page's session key and the worker's key, ui/relay.js and home/relayd.py); the site sees the
// module name, ids, sizes and ciphertext, keeps each item only until it is taken (at most CO_RELAY_TTL seconds) and never
// a request or an answer in clear. Which person, account and files a request reaches is decided by the home server
// (its own binding environment -> person -> module handler), never by the page or the site.
//   lib   relay.status($c, $module) / relay.call($c, $module, $in) - for the user ops of other modules ($c: the context of
//         this module for the caller's environment, see co_home_relay()); a page calls its own module's op, which
//         delegates here, so every module keeps its own op names and the core needs no cross-module call
//   push  relay_worker_token - {env[, revoke]}: the owner's home PC pairs (or unpairs) the worker of an environment; the
//                      token is shown once and only its sha256 is kept
//   route worker       - POST {env, token, status: {pub, modules: {<module>: {...}}}, replies}: the worker hands in answers
//                      and long-polls for requests of its own environment only
declare(strict_types=1);

const CO_RELAY_ENV_RE = '/^[a-z0-9][a-z0-9_-]{0,39}$/';
const CO_RELAY_MODULE_RE = '/^[a-z][a-z0-9-]{1,39}$/';
const CO_RELAY_ID_RE = '/^[a-f0-9]{32}$/';
const CO_RELAY_B64_RE = '/^[A-Za-z0-9+\/]+={0,2}$/';
const CO_RELAY_WAIT = 10;          // seconds a page call waits for its answer
const CO_RELAY_PENDING = 6;        // requests of one environment waiting at once, all modules (each holds a PHP worker)
const CO_RELAY_POLL = 15;          // seconds a worker call waits for requests
const CO_RELAY_TTL = 60;           // seconds a request or answer may stay on the site
const CO_RELAY_REQ_MAX = 65536;    // bytes of ciphertext in a request (base64)
const CO_RELAY_RES_MAX = 8388608;  // bytes of ciphertext in one answer (base64; an attachment of ~6 MB)
const CO_RELAY_BODY_MAX = 16777216;
const CO_RELAY_ONLINE = 45;        // seconds since the worker's last call
const CO_RELAY_STATUS_KEYS = 20;   // keys of one module's status (small numbers and labels for trays, never content)

function co_relay_dir(array $c, string $env = ''): string
{
    $d = $c['stateDir'] . ($env === '' ? '' : '/q/' . $env);
    if (!is_dir($d) && !@mkdir($d, 0700, true) && !is_dir($d)) throw new RuntimeException('relay_state');
    return $d;
}

function co_relay_json_file(string $path): array
{
    $v = json_decode((string)@file_get_contents($path), true);
    return is_array($v) ? $v : [];
}

function co_relay_workers(array $c): array
{
    return co_relay_json_file(co_relay_dir($c) . '/workers.json');
}

function co_relay_status_file(array $c, string $env): string
{
    return co_relay_dir($c, $env) . '/status.json';
}

// items older than the TTL go, whoever left them (a page that gave up, a worker that died after taking a request)
function co_relay_sweep(string $dir): void
{
    foreach (glob($dir . '/*.{req,taken,res}', GLOB_BRACE) ?: [] as $f) {
        if (@filemtime($f) < time() - CO_RELAY_TTL) @unlink($f);
    }
    foreach (glob($dir . '/.[0-9a-f]*') ?: [] as $f) {  // temp files of a call that died between write and link
        if (@filemtime($f) < time() - CO_RELAY_TTL) @unlink($f);
    }
}

function co_relay_b64(mixed $v, int $max): ?string
{
    return is_string($v) && $v !== '' && strlen($v) <= $max && preg_match(CO_RELAY_B64_RE, $v) ? $v : null;
}

/** The status a worker reports for each module it serves: flat scalars only, bounded. */
function co_relay_module_status(mixed $mods): array
{
    $out = [];
    foreach (is_array($mods) ? array_slice($mods, 0, 20, true) : [] as $m => $s) {
        if (!is_string($m) || !preg_match(CO_RELAY_MODULE_RE, $m) || !is_array($s)) continue;
        $one = [];
        foreach (array_slice($s, 0, CO_RELAY_STATUS_KEYS, true) as $k => $v) {
            if (!is_string($k) || !preg_match('/^[A-Za-z][A-Za-z0-9_]{0,31}$/', $k)) continue;
            if (is_bool($v)) $one[$k] = $v;
            elseif (is_int($v)) $one[$k] = max(-1000000000, min(1000000000, $v));
            elseif (is_string($v)) $one[$k] = mb_substr($v, 0, 200);
        }
        $out[$m] = $one;
    }
    return $out;
}

function co_relay_status(array $c, string $module): array
{
    $env = (string)$c['myEnv'];
    if (!preg_match(CO_RELAY_ENV_RE, $env)) return ['online' => false, 'error' => 'no_env'];
    if (!preg_match(CO_RELAY_MODULE_RE, $module)) return ['online' => false, 'error' => 'bad_module'];
    if (!isset(co_relay_workers($c)[$env])) return ['env' => $env, 'module' => $module, 'online' => false, 'paired' => false];
    $s = co_relay_json_file(co_relay_status_file($c, $env));
    $seen = (int)($s['seen'] ?? 0);
    $mods = is_array($s['modules'] ?? null) ? $s['modules'] : [];
    // env and module: the page seals with them (HKDF salt), the worker opens with its own; a mismatch only fails, never leaks
    return ['env' => $env, 'module' => $module, 'paired' => true, 'online' => time() - $seen < CO_RELAY_ONLINE, 'seen' => $seen,
        'pub' => (string)($s['pub'] ?? ''), 'served' => isset($mods[$module]), 'status' => is_array($mods[$module] ?? null) ? $mods[$module] : (object)[]];
}

function co_relay_call(array $c, string $module, array $in): array
{
    $st = co_relay_status($c, $module);
    if (empty($st['online'])) return ['error' => $st['error'] ?? (empty($st['paired']) ? 'not_paired' : 'offline')];
    if (empty($st['served'])) return ['error' => 'not_served'];
    $id = is_string($in['id'] ?? null) && preg_match(CO_RELAY_ID_RE, $in['id']) ? $in['id'] : null;
    $pk = co_relay_b64($in['pk'] ?? null, 200);
    $iv = co_relay_b64($in['iv'] ?? null, 40);
    $ct = co_relay_b64($in['ct'] ?? null, CO_RELAY_REQ_MAX);
    if (!$id || !$pk || !$iv || !$ct) return ['error' => 'bad_request'];
    $dir = co_relay_dir($c, (string)$c['myEnv']);
    co_relay_sweep($dir);
    // a person (or a runaway tab) cannot pin more than a few PHP workers of the shared host: counting and creating
    // happen under one admission lock of the environment, released before the wait
    $adm = fopen("$dir/admit.lock", 'c');
    if (!$adm || !flock($adm, LOCK_EX)) throw new RuntimeException('relay_lock');
    try {
        if (count(glob("$dir/*.{req,taken}", GLOB_BRACE) ?: []) >= CO_RELAY_PENDING) return ['error' => 'busy'];
        foreach (['taken', 'res'] as $ext) if (file_exists("$dir/$id.$ext")) return ['error' => 'duplicate_id'];
        // exclusive creation: link() never replaces an existing request of the same id
        $tmp = "$dir/.$id." . bin2hex(random_bytes(4));
        file_put_contents($tmp, json_encode(['id' => $id, 'module' => $module, 'pk' => $pk, 'iv' => $iv, 'ct' => $ct], JSON_THROW_ON_ERROR));
        $made = @link($tmp, "$dir/$id.req");
        @unlink($tmp);
        if (!$made) return ['error' => 'duplicate_id'];
    } finally {
        flock($adm, LOCK_UN);
        fclose($adm);
    }
    // the caller's database connection is not needed while the page call waits (the shared host limits connections)
    if (isset($c['release']) && is_callable($c['release'])) ($c['release'])();
    $until = microtime(true) + CO_RELAY_WAIT;
    do {
        clearstatcache();
        if (is_file("$dir/$id.res")) {
            $res = co_relay_json_file("$dir/$id.res");
            @unlink("$dir/$id.res");
            return $res ?: ['error' => 'bad_answer'];
        }
        usleep(120000);
    } while (microtime(true) < $until);
    @unlink("$dir/$id.req");
    return ['error' => 'timeout'];
}

function co_relay_worker_token(array $c, array $in): array
{
    $env = (string)($in['env'] ?? '');
    if (!preg_match(CO_RELAY_ENV_RE, $env)) return ['error' => 'bad_env'];
    $path = co_relay_dir($c) . '/workers.json';
    $lock = fopen(co_relay_dir($c) . '/workers.lock', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) throw new RuntimeException('relay_lock');
    try {
        $w = co_relay_workers($c);
        if (!empty($in['revoke'])) {
            unset($w[$env]);
            $token = null;
        } else {
            $token = bin2hex(random_bytes(32));
            $w[$env] = ['hash' => hash('sha256', $token), 'created' => gmdate('c')];
        }
        co_push_write_atomic($path, json_encode((object)$w, JSON_THROW_ON_ERROR));
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
    if ($token === null) @unlink(co_relay_status_file($c, $env));
    return $token === null ? ['ok' => true, 'revoked' => $env] : ['ok' => true, 'env' => $env, 'token' => $token];
}

function co_relay_worker(array $c, array $in): array
{
    $env = (string)($in['env'] ?? '');
    $token = (string)($in['token'] ?? '');
    $w = preg_match(CO_RELAY_ENV_RE, $env) ? (co_relay_workers($c)[$env] ?? null) : null;
    if (!$w || $token === '' || !hash_equals((string)$w['hash'], hash('sha256', $token))) return [403, ['error' => 'forbidden']];
    $dir = co_relay_dir($c, $env);
    $s = is_array($in['status'] ?? null) ? $in['status'] : [];
    $pub = co_relay_b64($s['pub'] ?? null, 200);
    co_push_write_atomic(co_relay_status_file($c, $env), json_encode([
        'seen' => time(), 'pub' => $pub ?? '', 'modules' => (object)co_relay_module_status($s['modules'] ?? null),
    ], JSON_THROW_ON_ERROR));
    // answers only for requests this worker took
    foreach (is_array($in['replies'] ?? null) ? array_slice($in['replies'], 0, 50) : [] as $r) {
        $id = is_array($r) && is_string($r['id'] ?? null) && preg_match(CO_RELAY_ID_RE, $r['id']) ? $r['id'] : null;
        if (!$id || !is_file("$dir/$id.taken")) continue;
        $ans = isset($r['error']) ? ['error' => preg_replace('/[^a-z_]/', '', (string)$r['error']) ?: 'worker_error']
            : (($iv = co_relay_b64($r['iv'] ?? null, 40)) && ($ct = co_relay_b64($r['ct'] ?? null, CO_RELAY_RES_MAX))
                ? ['iv' => $iv, 'ct' => $ct] : ['error' => 'bad_answer']);
        co_push_write_atomic("$dir/$id.res", json_encode($ans, JSON_THROW_ON_ERROR));
        @unlink("$dir/$id.taken");
    }
    co_relay_sweep($dir);
    // one waiting poll per environment: a second worker process (or a replayed call) gets an immediate empty answer
    $lock = fopen("$dir/poll.lock", 'c');
    $wait = empty($in['nowait']) && $lock && flock($lock, LOCK_EX | LOCK_NB);
    if ($wait && isset($c['release']) && is_callable($c['release'])) ($c['release'])();
    $until = microtime(true) + ($wait ? CO_RELAY_POLL : 0);
    try {
        do {
            clearstatcache();
            $out = [];
            foreach (glob("$dir/*.req") ?: [] as $f) {
                $id = basename($f, '.req');
                // claim without replacing: link() fails when the .taken of this id already exists
                if (!preg_match(CO_RELAY_ID_RE, $id) || !@link($f, "$dir/$id.taken")) continue;
                @unlink($f);
                $req = co_relay_json_file("$dir/$id.taken");
                if ($req) $out[] = $req;
                if (count($out) >= 20) break;
            }
            if ($out) return [200, ['requests' => $out]];
            usleep(150000);
        } while (microtime(true) < $until);
    } finally {
        if ($lock) {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
    return [200, ['requests' => []]];
}

function co_relay_route_worker(array $c): never
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
        http_response_code(405);
        exit(json_encode(['error' => 'post_only']));
    }
    $raw = (string)stream_get_contents(fopen('php://input', 'rb'), CO_RELAY_BODY_MAX + 1);
    $in = strlen($raw) <= CO_RELAY_BODY_MAX ? json_decode($raw, true) : null;
    unset($raw);
    // the route is the last thing of this request: the database connection is not needed while the poll waits
    $c['release'] = static function () use ($c): void { try { $c['db']?->close(); } catch (Throwable) {} };
    [$code, $ans] = is_array($in) ? co_relay_worker($c, $in) : [400, ['error' => 'bad_json']];
    http_response_code($code);
    exit(json_encode($ans, JSON_UNESCAPED_SLASHES));
}

return [
    'user' => [],
    'push' => [
        'relay_worker_token' => static fn(array $c, array $in): array => co_relay_worker_token($c, $in),
    ],
    'host' => [],
    'routes' => [
        'worker' => static fn(array $c): never => co_relay_route_worker($c),
    ],
    // for the server code of other modules (co_home_relay() in their ops.php): not an op, unreachable from a page
    'relay' => [
        'status' => static fn(array $c, string $module): array => co_relay_status($c, $module),
        'call' => static fn(array $c, string $module, array $in): array => co_relay_call($c, $module, $in),
    ],
];
