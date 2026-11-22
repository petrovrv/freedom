<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// concierge-mail server part: the site is only the postman between the Mail page of a person and the mail worker of
// that person on the home server (stage 2). Requests and answers are end-to-end encrypted (ECDH P-256 + AES-GCM between
// the page's session key and the worker's key, ui/relay.js and home/relay.py): the site sees ids, sizes and ciphertext,
// keeps each item only until it is taken (at most CO_MAIL_TTL seconds) and never a message in clear.
//   user  mail_status  - is my worker online, its public key, my unread count (a number for the tray)
//         mail_call    - {id, pk, iv, ct}: one sealed request to my worker; waits up to CO_MAIL_WAIT s for the sealed answer
//   push  mail_worker_token - {env[, revoke]}: the owner's home PC pairs (or unpairs) the worker of an environment; the
//                      token is shown once and only its sha256 is kept
//   route worker       - POST {env, token, status, replies}: the worker hands in answers and long-polls for requests of
//                      its own environment only
declare(strict_types=1);

const CO_MAIL_ENV_RE = '/^[a-z0-9][a-z0-9_-]{0,39}$/';
const CO_MAIL_ID_RE = '/^[a-f0-9]{32}$/';
const CO_MAIL_B64_RE = '/^[A-Za-z0-9+\/]+={0,2}$/';
const CO_MAIL_WAIT = 10;          // seconds a page call waits for its answer
const CO_MAIL_PENDING = 4;        // requests of one environment waiting at once (each holds a PHP worker)
const CO_MAIL_POLL = 15;          // seconds a worker call waits for requests
const CO_MAIL_TTL = 60;           // seconds a request or answer may stay on the site
const CO_MAIL_REQ_MAX = 65536;    // bytes of ciphertext in a request (base64)
const CO_MAIL_RES_MAX = 8388608;  // bytes of ciphertext in one answer (base64; an attachment of ~6 MB)
const CO_MAIL_BODY_MAX = 16777216;
const CO_MAIL_ONLINE = 45;        // seconds since the worker's last call

function co_mail_dir(array $c, string $env = ''): string
{
    $d = $c['stateDir'] . ($env === '' ? '' : '/q/' . $env);
    if (!is_dir($d) && !@mkdir($d, 0700, true) && !is_dir($d)) throw new RuntimeException('mail_state');
    return $d;
}

function co_mail_json_file(string $path): array
{
    $v = json_decode((string)@file_get_contents($path), true);
    return is_array($v) ? $v : [];
}

function co_mail_workers(array $c): array
{
    return co_mail_json_file(co_mail_dir($c) . '/workers.json');
}

function co_mail_status_file(array $c, string $env): string
{
    return co_mail_dir($c, $env) . '/status.json';
}

// items older than the TTL go, whoever left them (a page that gave up, a worker that died after taking a request)
function co_mail_sweep(string $dir): void
{
    foreach (glob($dir . '/*.{req,taken,res}', GLOB_BRACE) ?: [] as $f) {
        if (@filemtime($f) < time() - CO_MAIL_TTL) @unlink($f);
    }
    foreach (glob($dir . '/.[0-9a-f]*') ?: [] as $f) {  // temp files of a call that died between write and link
        if (@filemtime($f) < time() - CO_MAIL_TTL) @unlink($f);
    }
}

function co_mail_status(array $c): array
{
    $env = (string)$c['myEnv'];
    if (!preg_match(CO_MAIL_ENV_RE, $env)) return ['online' => false, 'error' => 'no_env'];
    if (!isset(co_mail_workers($c)[$env])) return ['env' => $env, 'online' => false, 'paired' => false];
    $s = co_mail_json_file(co_mail_status_file($c, $env));
    $seen = (int)($s['seen'] ?? 0);
    // env: the page seals with it (HKDF salt), the worker opens with its own; a mismatch only fails, never leaks
    return ['env' => $env, 'paired' => true, 'online' => time() - $seen < CO_MAIL_ONLINE, 'seen' => $seen,
        'pub' => (string)($s['pub'] ?? ''), 'unread' => (int)($s['unread'] ?? 0),
        'synced' => (string)($s['synced'] ?? ''), 'account' => (string)($s['account'] ?? '')];
}

function co_mail_b64(mixed $v, int $max): ?string
{
    return is_string($v) && $v !== '' && strlen($v) <= $max && preg_match(CO_MAIL_B64_RE, $v) ? $v : null;
}

function co_mail_call(array $c, array $in): array
{
    $st = co_mail_status($c);
    if (empty($st['online'])) return ['error' => empty($st['paired']) ? 'not_paired' : 'offline'];
    $id = is_string($in['id'] ?? null) && preg_match(CO_MAIL_ID_RE, $in['id']) ? $in['id'] : null;
    $pk = co_mail_b64($in['pk'] ?? null, 200);
    $iv = co_mail_b64($in['iv'] ?? null, 40);
    $ct = co_mail_b64($in['ct'] ?? null, CO_MAIL_REQ_MAX);
    if (!$id || !$pk || !$iv || !$ct) return ['error' => 'bad_request'];
    $dir = co_mail_dir($c, (string)$c['myEnv']);
    co_mail_sweep($dir);
    // a person (or a runaway tab) cannot pin more than a few PHP workers of the shared host: counting and creating
    // happen under one admission lock of the environment, released before the wait
    $adm = fopen("$dir/admit.lock", 'c');
    if (!$adm || !flock($adm, LOCK_EX)) throw new RuntimeException('mail_lock');
    try {
        if (count(glob("$dir/*.{req,taken}", GLOB_BRACE) ?: []) >= CO_MAIL_PENDING) return ['error' => 'busy'];
        foreach (['taken', 'res'] as $ext) if (file_exists("$dir/$id.$ext")) return ['error' => 'duplicate_id'];
        // exclusive creation: link() never replaces an existing request of the same id
        $tmp = "$dir/.$id." . bin2hex(random_bytes(4));
        file_put_contents($tmp, json_encode(['id' => $id, 'pk' => $pk, 'iv' => $iv, 'ct' => $ct], JSON_THROW_ON_ERROR));
        $made = @link($tmp, "$dir/$id.req");
        @unlink($tmp);
        if (!$made) return ['error' => 'duplicate_id'];
    } finally {
        flock($adm, LOCK_UN);
        fclose($adm);
    }
    $until = microtime(true) + CO_MAIL_WAIT;
    do {
        clearstatcache();
        if (is_file("$dir/$id.res")) {
            $res = co_mail_json_file("$dir/$id.res");
            @unlink("$dir/$id.res");
            return $res ?: ['error' => 'bad_answer'];
        }
        usleep(120000);
    } while (microtime(true) < $until);
    @unlink("$dir/$id.req");
    return ['error' => 'timeout'];
}

function co_mail_worker_token(array $c, array $in): array
{
    $env = (string)($in['env'] ?? '');
    if (!preg_match(CO_MAIL_ENV_RE, $env)) return ['error' => 'bad_env'];
    $path = co_mail_dir($c) . '/workers.json';
    $lock = fopen(co_mail_dir($c) . '/workers.lock', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) throw new RuntimeException('mail_lock');
    try {
        $w = co_mail_json_file($path);
        if (!empty($in['revoke'])) {
            unset($w[$env]);
            $token = null;
        } else {
            $token = bin2hex(random_bytes(32));
            $w[$env] = ['hash' => hash('sha256', $token), 'created' => gmdate('c')];
        }
        co_push_write_atomic($path, json_encode($w, JSON_THROW_ON_ERROR));
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
    return $token === null ? ['ok' => true, 'revoked' => $env] : ['ok' => true, 'env' => $env, 'token' => $token];
}

function co_mail_worker(array $c, array $in): array
{
    $env = (string)($in['env'] ?? '');
    $token = (string)($in['token'] ?? '');
    $w = preg_match(CO_MAIL_ENV_RE, $env) ? (co_mail_workers($c)[$env] ?? null) : null;
    if (!$w || $token === '' || !hash_equals((string)$w['hash'], hash('sha256', $token))) return [403, ['error' => 'forbidden']];
    $dir = co_mail_dir($c, $env);
    $s = is_array($in['status'] ?? null) ? $in['status'] : [];
    $pub = co_mail_b64($s['pub'] ?? null, 200);
    co_push_write_atomic(co_mail_status_file($c, $env), json_encode([
        'seen' => time(), 'pub' => $pub ?? '', 'unread' => max(0, min(1000000, (int)($s['unread'] ?? 0))),
        'synced' => mb_substr((string)($s['synced'] ?? ''), 0, 40), 'account' => mb_substr((string)($s['account'] ?? ''), 0, 200),
    ], JSON_THROW_ON_ERROR));
    // answers only for requests this worker took
    foreach (is_array($in['replies'] ?? null) ? array_slice($in['replies'], 0, 50) : [] as $r) {
        $id = is_array($r) && is_string($r['id'] ?? null) && preg_match(CO_MAIL_ID_RE, $r['id']) ? $r['id'] : null;
        if (!$id || !is_file("$dir/$id.taken")) continue;
        $ans = isset($r['error']) ? ['error' => preg_replace('/[^a-z_]/', '', (string)$r['error']) ?: 'worker_error']
            : (($iv = co_mail_b64($r['iv'] ?? null, 40)) && ($ct = co_mail_b64($r['ct'] ?? null, CO_MAIL_RES_MAX))
                ? ['iv' => $iv, 'ct' => $ct] : ['error' => 'bad_answer']);
        co_push_write_atomic("$dir/$id.res", json_encode($ans, JSON_THROW_ON_ERROR));
        @unlink("$dir/$id.taken");
    }
    co_mail_sweep($dir);
    // one waiting poll per environment: a second worker process (or a replayed call) gets an immediate empty answer
    $lock = fopen("$dir/poll.lock", 'c');
    $wait = empty($in['nowait']) && $lock && flock($lock, LOCK_EX | LOCK_NB);
    if ($wait && isset($c['release']) && is_callable($c['release'])) ($c['release'])();
    $until = microtime(true) + ($wait ? CO_MAIL_POLL : 0);
    try {
        do {
            clearstatcache();
            $out = [];
            foreach (glob("$dir/*.req") ?: [] as $f) {
                $id = basename($f, '.req');
                // claim without replacing: link() fails when the .taken of this id already exists
                if (!preg_match(CO_MAIL_ID_RE, $id) || !@link($f, "$dir/$id.taken")) continue;
                @unlink($f);
                $req = co_mail_json_file("$dir/$id.taken");
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

function co_mail_route_worker(array $c): never
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
        http_response_code(405);
        exit(json_encode(['error' => 'post_only']));
    }
    $raw = (string)stream_get_contents(fopen('php://input', 'rb'), CO_MAIL_BODY_MAX + 1);
    $in = strlen($raw) <= CO_MAIL_BODY_MAX ? json_decode($raw, true) : null;
    unset($raw);
    // the route is the last thing of this request: the database connection is not needed while the poll waits
    $c['release'] = static function () use ($c): void { try { $c['db']?->close(); } catch (Throwable) {} };
    [$code, $ans] = is_array($in) ? co_mail_worker($c, $in) : [400, ['error' => 'bad_json']];
    http_response_code($code);
    exit(json_encode($ans, JSON_UNESCAPED_SLASHES));
}

return [
    'user' => [
        'mail_status' => static fn(array $c, array $in): array => co_mail_status($c),
        'mail_call' => static fn(array $c, array $in): array => co_mail_call($c, $in),
    ],
    'push' => [
        'mail_worker_token' => static fn(array $c, array $in): array => co_mail_worker_token($c, $in),
    ],
    'host' => [],
    'routes' => [
        'worker' => static fn(array $c): never => co_mail_route_worker($c),
    ],
];
