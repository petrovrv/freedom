<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// push.php: Web Push API endpoint (JSON in/out). CO Web subscribes devices; the home PC (Bridge) sends notifications.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/push-lib.php';
require_once __DIR__ . '/geo-lib.php';

header('Cache-Control: no-store');

// Safe string extraction from decoded JSON.
function co_push_str(mixed $v): string
{
    return is_string($v) ? $v : '';
}

try {
    $method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
    $in = [];

    if ($method === 'POST') {
        $raw = file_get_contents('php://input', false, null, 0, 65536);
        $in = json_decode($raw === false ? '' : $raw, true);
        if (!is_array($in)) {
            co_json(400, ['error' => 'bad_json']);
        }
        $op = co_push_str($in['op'] ?? null);
    } elseif ($method === 'GET') {
        $op = co_push_str($_GET['op'] ?? null);
        if ($op !== 'key' && $op !== 'watch') {
            co_json(405, ['error' => 'method_not_allowed']);
        }
    } else {
        co_json(405, ['error' => 'method_not_allowed']);
    }

    // ---------- notify (home PC, shared key, no cookie) ----------
    if ($op === 'notify') {
        $key = co_push_notify_key();
        $given = co_push_str($_SERVER['HTTP_X_CO_PUSH_KEY'] ?? null);
        if ($key === '' || !hash_equals($key, $given)) {
            co_json(403, ['error' => 'forbidden']);
        }

        $env = co_push_str($in['env'] ?? null);
        if ($env === '') {
            co_json(400, ['error' => 'env_required']);
        }
        // co_push_notify (push-lib.php): limits, same-origin url, [[silent]] (0.82.1 A1 observer) sends nothing;
        // the server's own "job finished" push of site jobs goes the same way (co_push_job_finished)
        $counters = co_push_notify($env, co_push_str($in['title'] ?? null), co_push_str($in['body'] ?? null),
            co_push_str($in['url'] ?? null), co_push_str($in['tag'] ?? null));
        if (empty($counters['silent'])) {
            co_watch_check(); // opportunistic watchdog run; never throws
        }
        co_json(200, $counters);
    }

    // ---------- heartbeat (home PC, same key as notify) ----------
    if ($op === 'heartbeat') {
        $expected = co_push_notify_key();
        $given = co_push_str($_SERVER['HTTP_X_CO_PUSH_KEY'] ?? null);
        if ($expected === '' || !hash_equals($expected, $given)) {
            co_json(403, ['error' => 'forbidden']);
        }
        $host = co_push_str($in['host'] ?? null);
        $version = trim(co_push_str($in['version'] ?? null));
        if (!preg_match('/^[a-z0-9-]{1,40}$/', $host)) {
            co_json(400, ['error' => 'invalid_host']);
        }
        if (strlen($version) > 20 || preg_match('/[\x00-\x1f\x7f]/', $version)) {
            co_json(400, ['error' => 'invalid_version']);
        }
        co_json(200, co_watch_heartbeat($host, $version));
    }

    // ---------- geo_put (home PC geo-presence, same key as notify): family presence for the CO Web header ----------
    if ($op === 'geo_put') {
        $expected = co_push_notify_key();
        $given = co_push_str($_SERVER['HTTP_X_CO_PUSH_KEY'] ?? null);
        if ($expected === '' || !hash_equals($expected, $given)) {
            co_json(403, ['error' => 'forbidden']);
        }
        co_json(200, co_geo_put($in));
    }

    // ---------- watch (anyone; only triggers the throttled check, reveals no host details) ----------
    if ($op === 'watch') {
        $r = co_watch_check();
        co_json(200, ['checked' => !empty($r['checked']), 'offline' => co_watch_offline_count()]);
    }

    // ---------- Signed-in family members ----------
    if (!($method === 'GET' && $op === 'key')) {
        co_require_api_call();
    }

    $users = co_push_users();
    $cookie = $_COOKIE[CO_SESSION_COOKIE] ?? null;
    $session = co_open(is_string($cookie) ? $cookie : null);
    $email = strtolower(co_agent_email($session));
    if ($email === '' || !isset($users[$email])) {
        co_json(403, ['error' => 'forbidden']);
    }

    switch ($op) {
        case 'key':
            co_json(200, ['publicKey' => co_push_vapid()['public_b64url']]);

        case 'subscribe':
            $s = is_array($in['subscription'] ?? null) ? $in['subscription'] : [];
            $keys = is_array($s['keys'] ?? null) ? $s['keys'] : [];
            $endpoint = co_push_str($s['endpoint'] ?? null);
            $p256dh = co_push_str($keys['p256dh'] ?? null);
            $auth = co_push_str($keys['auth'] ?? null);

            if (
                $endpoint === '' || strlen($endpoint) > 1000 || !str_starts_with($endpoint, 'https://')
                || filter_var($endpoint, FILTER_VALIDATE_URL) === false
                || strlen(co_push_b64url_decode($p256dh)) !== 65
                || strlen(co_push_b64url_decode($auth)) !== 16
            ) {
                co_json(400, ['error' => 'bad_subscription']);
            }

            $id = hash('sha256', $endpoint);
            $env = $users[$email];
            $ua = mb_substr(co_push_str($_SERVER['HTTP_USER_AGENT'] ?? null), 0, 200);
            $now = co_push_now();

            $count = co_push_update(static function (array &$subs) use ($id, $endpoint, $p256dh, $auth, $email, $env, $ua, $now): int {
                $prev = is_array($subs[$id] ?? null) ? $subs[$id] : null;
                $same = $prev !== null && ($prev['email'] ?? null) === $email;
                $subs[$id] = [
                    'endpoint'   => $endpoint,
                    'p256dh'     => $p256dh,
                    'auth'       => $auth,
                    'email'      => $email,
                    'env'        => $env,
                    'ua'         => $ua,
                    'createdUtc' => $same ? ($prev['createdUtc'] ?? $now) : $now,
                    'lastOkUtc'  => $same ? ($prev['lastOkUtc'] ?? null) : null,
                    'failures'   => 0,
                ];
                return count(array_filter($subs, static fn($r): bool => is_array($r) && ($r['email'] ?? null) === $email));
            });
            co_json(200, ['ok' => true, 'count' => $count]);

        case 'unsubscribe':
            $endpoint = co_push_str($in['endpoint'] ?? null);
            if ($endpoint === '') {
                co_json(400, ['error' => 'endpoint_required']);
            }
            $id = hash('sha256', $endpoint);
            $removed = co_push_update(static function (array &$subs) use ($id, $email): bool {
                if (is_array($subs[$id] ?? null) && ($subs[$id]['email'] ?? null) === $email) {
                    unset($subs[$id]);
                    return true;
                }
                return false;
            });
            co_json(200, ['ok' => true, 'removed' => $removed]);

        case 'status':
            $endpoint = co_push_str($in['endpoint'] ?? null);
            $subs = co_push_load();
            $id = hash('sha256', $endpoint);
            $subscribed = $endpoint !== ''
                && is_array($subs[$id] ?? null)
                && ($subs[$id]['email'] ?? null) === $email;
            co_json(200, ['subscribed' => $subscribed]);

        case 'test':
            $payload = ['title' => 'CO', 'body' => 'Test notification', 'url' => '/', 'tag' => 'co-test'];
            $counters = co_push_broadcast(
                static fn(array $s): bool => ($s['email'] ?? null) === $email,
                $payload,
                'co-test'
            );
            co_json(200, $counters);

        default:
            co_json(400, ['error' => 'unknown_op']);
    }
} catch (Throwable $e) {
    error_log('push.php: ' . $e->getMessage());
    co_json(500, ['error' => 'server_error']);
}
