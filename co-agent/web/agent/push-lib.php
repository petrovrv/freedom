<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// push-lib.php: Web Push (RFC 8291 aes128gcm + RFC 8292 VAPID) without composer.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/members-lib.php';

const CO_PUSH_MAX_FAILURES = 5;
const CO_PUSH_P256_SPKI_PREFIX = '3059301306072a8648ce3d020106082a8648ce3d030107034200';

// ---------- Paths / filesystem ----------

function co_push_ensure_dir(string $dir): string
{
    if (!is_dir($dir) && !@mkdir($dir, 0700, true) && !is_dir($dir)) {
        throw new RuntimeException('Cannot create directory');
    }
    @chmod($dir, 0700);
    return $dir;
}

function co_push_secrets_dir(): string
{
    return co_push_ensure_dir(co_agent_home() . '/co-agent/secrets');
}

function co_push_state_dir(): string
{
    return co_push_ensure_dir(co_agent_home() . '/co-agent/state');
}

// Write file atomically (temp file in same dir + rename).
function co_push_write_atomic(string $path, string $data, int $mode = 0600): void
{
    $tmp = $path . '.tmp.' . bin2hex(random_bytes(6));
    if (file_put_contents($tmp, $data, LOCK_EX) !== strlen($data)) {
        @unlink($tmp);
        throw new RuntimeException('Cannot write temp file');
    }
    @chmod($tmp, $mode);
    if (!rename($tmp, $path)) {
        @unlink($tmp);
        throw new RuntimeException('Cannot rename temp file');
    }
}

function co_push_now(): string
{
    return gmdate('Y-m-d\TH:i:s\Z');
}

// ---------- Users ----------
// co_push_users(): members-lib.php (table members).

// ---------- Base64url ----------

function co_push_b64url_encode(string $bin): string
{
    return rtrim(strtr(base64_encode($bin), '+/', '-_'), '=');
}

function co_push_b64url_decode(string $s): string
{
    if ($s === '' || preg_match('/^[A-Za-z0-9_-]+={0,2}$/', $s) !== 1) {
        return '';
    }
    $s = strtr(rtrim($s, '='), '-_', '+/');
    $pad = strlen($s) % 4;
    if ($pad === 1) {
        return '';
    }
    if ($pad > 0) {
        $s .= str_repeat('=', 4 - $pad);
    }
    $out = base64_decode($s, true);
    return $out === false ? '' : $out;
}

// ---------- EC helpers ----------

function co_push_ec_new(): OpenSSLAsymmetricKey
{
    $key = openssl_pkey_new([
        'private_key_type' => OPENSSL_KEYTYPE_EC,
        'curve_name'       => 'prime256v1',
    ]);
    if ($key === false) {
        throw new RuntimeException('EC key generation failed');
    }
    return $key;
}

// Uncompressed public point: 0x04 || X(32) || Y(32).
function co_push_ec_raw_public(OpenSSLAsymmetricKey $key): string
{
    $d = openssl_pkey_get_details($key);
    if (!is_array($d) || !isset($d['ec']['x'], $d['ec']['y'])) {
        throw new RuntimeException('EC key details unavailable');
    }
    return "\x04"
        . str_pad($d['ec']['x'], 32, "\0", STR_PAD_LEFT)
        . str_pad($d['ec']['y'], 32, "\0", STR_PAD_LEFT);
}

// Raw P-256 point -> PEM SubjectPublicKeyInfo.
function co_push_raw_public_to_pem(string $raw): string
{
    $der = hex2bin(CO_PUSH_P256_SPKI_PREFIX) . $raw;
    return "-----BEGIN PUBLIC KEY-----\n"
        . chunk_split(base64_encode($der), 64, "\n")
        . "-----END PUBLIC KEY-----\n";
}

// DER ECDSA signature (SEQUENCE{INTEGER r, INTEGER s}) -> raw r||s (64 bytes).
function co_push_der_sig_to_raw(string $der): ?string
{
    $len = strlen($der);
    $pos = 0;
    $readLen = static function () use ($der, $len, &$pos): ?int {
        if ($pos >= $len) {
            return null;
        }
        $l = ord($der[$pos++]);
        if ($l < 0x80) {
            return $l;
        }
        $n = $l & 0x7f;
        if ($n < 1 || $n > 2 || $pos + $n > $len) {
            return null;
        }
        $l = 0;
        for ($i = 0; $i < $n; $i++) {
            $l = ($l << 8) | ord($der[$pos++]);
        }
        return $l;
    };

    if ($len < 8 || ord($der[$pos++]) !== 0x30 || $readLen() === null) {
        return null;
    }
    $out = '';
    for ($i = 0; $i < 2; $i++) {
        if ($pos >= $len || ord($der[$pos++]) !== 0x02) {
            return null;
        }
        $l = $readLen();
        if ($l === null || $l < 1 || $pos + $l > $len) {
            return null;
        }
        $int = ltrim(substr($der, $pos, $l), "\0");
        $pos += $l;
        if (strlen($int) > 32) {
            return null;
        }
        $out .= str_pad($int, 32, "\0", STR_PAD_LEFT);
    }
    return $out;
}

// ---------- Secrets ----------

/** @return array{private_pem:string, public_b64url:string} */
function co_push_vapid(): array
{
    static $cache = null;
    if ($cache !== null) {
        return $cache;
    }
    $file = co_push_secrets_dir() . '/vapid.json';

    $read = static function () use ($file): ?array {
        if (!is_file($file)) {
            return null;
        }
        $j = json_decode((string)file_get_contents($file), true);
        if (is_array($j) && is_string($j['private_pem'] ?? null) && is_string($j['public_b64url'] ?? null)) {
            return ['private_pem' => $j['private_pem'], 'public_b64url' => $j['public_b64url']];
        }
        return null;
    };

    if (($v = $read()) !== null) {
        return $cache = $v;
    }

    // Create once, serialized by a lock file to avoid concurrent generation.
    $lock = fopen($file . '.lock', 'c');
    if ($lock === false) {
        throw new RuntimeException('Cannot open VAPID lock');
    }
    try {
        flock($lock, LOCK_EX);
        if (($v = $read()) !== null) {
            return $cache = $v;
        }
        $key = co_push_ec_new();
        if (!openssl_pkey_export($key, $pem)) {
            throw new RuntimeException('VAPID key export failed');
        }
        $v = [
            'private_pem'   => $pem,
            'public_b64url' => co_push_b64url_encode(co_push_ec_raw_public($key)),
        ];
        co_push_write_atomic($file, json_encode($v, JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT | JSON_THROW_ON_ERROR), 0600);
        return $cache = $v;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

function co_push_notify_key(): string
{
    $file = co_push_secrets_dir() . '/push-notify-key';
    if (!is_file($file)) {
        return '';
    }
    $k = file_get_contents($file);
    return $k === false ? '' : trim($k);
}

// ---------- Subscriptions store ----------

function co_push_store_file(): string
{
    return co_push_state_dir() . '/push-subscriptions.json';
}

function co_push_read_store(string $file): array
{
    if (!is_file($file)) {
        return [];
    }
    $j = json_decode((string)file_get_contents($file), true);
    return is_array($j) ? $j : [];
}

/** Subscriptions keyed by sha256(endpoint). */
function co_push_load(): array
{
    return co_push_read_store(co_push_store_file());
}

/**
 * Mutate subscriptions under exclusive lock. $fn receives array &$subs.
 * Returns whatever $fn returns.
 */
function co_push_update(callable $fn): mixed
{
    $file = co_push_store_file();
    $lock = fopen($file . '.lock', 'c');
    if ($lock === false) {
        throw new RuntimeException('Cannot open store lock');
    }
    try {
        flock($lock, LOCK_EX);
        $subs = co_push_read_store($file);
        $result = $fn($subs);
        co_push_write_atomic($file, json_encode($subs, JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT | JSON_THROW_ON_ERROR), 0600);
        return $result;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

// ---------- Encryption (RFC 8291) ----------

// Returns aes128gcm body, or null on failure.
function co_push_encrypt(string $plainJson, string $uaPublic, string $authSecret): ?string
{
    $eph = co_push_ec_new();
    $asPublic = co_push_ec_raw_public($eph);

    $uaKey = openssl_pkey_get_public(co_push_raw_public_to_pem($uaPublic));
    if ($uaKey === false) {
        return null;
    }
    $ecdh = openssl_pkey_derive($uaKey, $eph, 32);
    if (!is_string($ecdh) || strlen($ecdh) !== 32) {
        return null;
    }

    // IKM derivation bound to both public keys.
    $prkKey = hash_hmac('sha256', $ecdh, $authSecret, true);
    $ikm = hash_hmac('sha256', "WebPush: info\x00" . $uaPublic . $asPublic . "\x01", $prkKey, true);

    // Content encryption key and nonce.
    $salt = random_bytes(16);
    $prk = hash_hmac('sha256', $ikm, $salt, true);
    $cek = substr(hash_hmac('sha256', "Content-Encoding: aes128gcm\x00\x01", $prk, true), 0, 16);
    $nonce = substr(hash_hmac('sha256', "Content-Encoding: nonce\x00\x01", $prk, true), 0, 12);

    // Single record with last-record delimiter.
    $plain = $plainJson . "\x02";
    if (strlen($plain) + 16 > 4096 - 86) {
        return null;
    }
    $tag = '';
    $ct = openssl_encrypt($plain, 'aes-128-gcm', $cek, OPENSSL_RAW_DATA, $nonce, $tag, '', 16);
    if ($ct === false || strlen($tag) !== 16) {
        return null;
    }

    return $salt . pack('N', 4096) . chr(65) . $asPublic . $ct . $tag;
}

// ---------- VAPID (RFC 8292) ----------

function co_push_vapid_jwt(string $endpoint, string $privatePem): ?string
{
    $u = parse_url($endpoint);
    if (!is_array($u) || empty($u['scheme']) || empty($u['host'])) {
        return null;
    }
    $aud = $u['scheme'] . '://' . $u['host'] . (isset($u['port']) ? ':' . $u['port'] : '');

    $header = co_push_b64url_encode('{"typ":"JWT","alg":"ES256"}');
    $claims = co_push_b64url_encode(json_encode(
        ['aud' => $aud, 'exp' => time() + 43200, 'sub' => 'mailto:' . (co_owner_email() ?: 'push@localhost')],
        JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR
    ));
    $input = $header . '.' . $claims;

    $priv = openssl_pkey_get_private($privatePem);
    if ($priv === false || !openssl_sign($input, $der, $priv, OPENSSL_ALGO_SHA256)) {
        return null;
    }
    $raw = co_push_der_sig_to_raw($der);
    return $raw === null ? null : $input . '.' . co_push_b64url_encode($raw);
}

// ---------- Send ----------

/** Returns HTTP status, 0 on local/curl error. */
function co_push_send(array $sub, array $payload, int $ttl = 86400, string $topic = ''): int
{
    try {
        $endpoint = is_string($sub['endpoint'] ?? null) ? $sub['endpoint'] : '';
        $uaPublic = co_push_b64url_decode(is_string($sub['p256dh'] ?? null) ? $sub['p256dh'] : '');
        $authSecret = co_push_b64url_decode(is_string($sub['auth'] ?? null) ? $sub['auth'] : '');
        if (!str_starts_with($endpoint, 'https://') || strlen($uaPublic) !== 65 || $uaPublic[0] !== "\x04" || strlen($authSecret) !== 16) {
            return 0;
        }

        $json = json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE | JSON_THROW_ON_ERROR);
        $body = co_push_encrypt($json, $uaPublic, $authSecret);
        if ($body === null) {
            return 0;
        }

        $vapid = co_push_vapid();
        $jwt = co_push_vapid_jwt($endpoint, $vapid['private_pem']);
        if ($jwt === null) {
            return 0;
        }

        $headers = [
            'Content-Encoding: aes128gcm',
            'Content-Type: application/octet-stream',
            'Content-Length: ' . strlen($body),
            'TTL: ' . max(0, $ttl),
            'Urgency: normal',
            'Authorization: vapid t=' . $jwt . ', k=' . $vapid['public_b64url'],
            'Expect:',
        ];
        $topic = substr((string)preg_replace('/[^A-Za-z0-9_-]/', '', $topic), 0, 32);
        if ($topic !== '') {
            $headers[] = 'Topic: ' . $topic;
        }

        $ch = curl_init($endpoint);
        if ($ch === false) {
            return 0;
        }
        curl_setopt_array($ch, [
            CURLOPT_POST           => true,
            CURLOPT_POSTFIELDS     => $body,
            CURLOPT_HTTPHEADER     => $headers,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT        => 15,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_PROTOCOLS      => CURLPROTO_HTTPS,
            CURLOPT_SSL_VERIFYPEER => true,
        ]);
        $res = curl_exec($ch);
        $status = $res === false ? 0 : (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);
        return $status;
    } catch (Throwable) {
        return 0;
    }
}

// ---------- Broadcast ----------

/**
 * Copies of a push for the notify hooks of enabled modules (ext/1 provides.notify, e.g. a messenger account that writes
 * the push to its person): the members whose devices $filter takes (filters look at the subscription's env), and the
 * payload. Runs whether or not the env has a subscribed device. Never throws, never blocks the push.
 */
function co_push_mirror(callable $filter, array $payload, ?array $channels = null): void
{
    if (!function_exists('co_module_hooks') || !function_exists('co_members') || !function_exists('co_db')) return;
    try {
        $envs = [];
        foreach (co_members() as $r) {
            $env = (string)($r['env'] ?? '');
            try {
                if ($env !== '' && $filter(['env' => $env, 'endpoint' => ''])) $envs[] = $env;
            } catch (Throwable) {
            }
        }
        if ($envs === []) return;
        $db = co_db();
        foreach (co_module_hooks($db, 'notify') as $p) {
            $fn = $p['hooks']['push'] ?? null;
            if ($fn === null || ($channels !== null && !in_array($p['module']['id'] . '/' . $p['id'], $channels, true))) continue;
            try {
                $fn(co_module_ctx($db, $p['module']), ['envs' => $envs] + $payload);
            } catch (Throwable $e) {
                error_log("notify hook {$p['module']['id']}/{$p['id']}: " . $e->getMessage());
            }
        }
    } catch (Throwable $e) {
        error_log('co_push_mirror: ' . $e->getMessage());
    }
}

/**
 * Delivery channels of a push (ext/1 notify): CO_PUSH_DEVICES = the subscribed devices (Web Push), "<module>/<id>" = a
 * notify hook of an enabled module. $channels null = all of them (every push of the core goes everywhere).
 * @return array{sent:int, removed:int, failed:int}
 */
function co_push_broadcast(callable $filter, array $payload, string $topic, ?array $channels = null): array
{
    co_push_mirror($filter, $payload, $channels);
    $results = [];
    foreach ($channels === null || in_array(CO_PUSH_DEVICES, $channels, true) ? co_push_load() : [] as $id => $sub) {
        if (is_array($sub) && $filter($sub)) {
            $results[(string)$id] = co_push_send($sub, $payload, 86400, $topic);
        }
    }

    $sent = 0;
    $removed = 0;
    $failed = 0;
    if ($results === []) {
        return ['sent' => 0, 'removed' => 0, 'failed' => 0];
    }

    $now = co_push_now();
    co_push_update(static function (array &$all) use ($results, $now, &$sent, &$removed, &$failed): void {
        foreach ($results as $id => $status) {
            if ($status >= 200 && $status < 300) {
                $sent++;
                if (isset($all[$id])) {
                    $all[$id]['lastOkUtc'] = $now;
                    $all[$id]['failures'] = 0;
                }
                continue;
            }
            if ($status === 404 || $status === 410) {
                unset($all[$id]);
                $removed++;
                continue;
            }
            $failed++;
            if (isset($all[$id])) {
                $f = (int)($all[$id]['failures'] ?? 0) + 1;
                if ($f >= CO_PUSH_MAX_FAILURES) {
                    unset($all[$id]);
                    $removed++;
                } else {
                    $all[$id]['failures'] = $f;
                }
            }
        }
    });

    // the push service of each device and its answer (fcm.googleapis.com 201 = accepted), for push-jobs.log
    $by = [];
    $all = co_push_load();
    foreach ($results as $id => $status) {
        $h = (string)parse_url((string)($all[$id]['endpoint'] ?? ''), PHP_URL_HOST);
        $by[] = ($h !== '' ? $h : '?') . ' ' . $status;
    }
    return ['sent' => $sent, 'removed' => $removed, 'failed' => $failed, 'endpoints' => $by];
}

/**
 * One push to the devices of $env ('*' = all): what push.php op=notify does for Bridge. A body starting with [[silent]]
 * (A1 observer, 0.82.1) sends nothing. Returns the counters of co_push_broadcast, or ['sent' => 0, 'silent' => true].
 */
function co_push_notify(string $env, string $title, string $body, string $url, string $tag, ?callable $send = null, ?array $channels = null): array
{
    $title = mb_substr($title, 0, 120);
    $body = mb_substr($body, 0, 400);
    $url = mb_substr($url, 0, 1000);
    // Same-origin path only (reject protocol-relative "//").
    if (!str_starts_with($url, '/') || str_starts_with($url, '//')) {
        $url = '/';
    }
    $tag = mb_substr($tag, 0, 64);
    if (preg_match('/^\s*\[\[\s*silent\s*\]\]/i', $body)) {
        return ['sent' => 0, 'silent' => true];
    }
    $send ??= 'co_push_broadcast';
    $filter = static fn(array $s): bool => $env === '*' || ($s['env'] ?? null) === $env;
    $payload = ['title' => $title, 'body' => $body, 'url' => $url, 'tag' => $tag];
    if ($channels === null) return $send($filter, $payload, $tag);
    return $send($filter, $payload, $tag, array_values(array_unique(array_filter($channels, 'co_push_channel_ok'))));
}

const CO_PUSH_DEVICES = 'devices';

function co_push_channel_ok(mixed $id): bool
{
    if (!is_string($id)) return false;
    if ($id === CO_PUSH_DEVICES) return true;
    // "<module id>/<notify id>": the same rules as the manifests (modules-lib.php)
    $p = explode('/', $id);
    return count($p) === 2 && defined('CO_MODULE_ID_RE') && preg_match(CO_MODULE_ID_RE, $p[0]) === 1 && preg_match(CO_MODULE_NAME_RE, $p[1]) === 1;
}

/**
 * Where a push to $env can go (push.php op=channels, e.g. a module that lets its person choose where its reports go):
 * [{id, kind: core|module, title, ready: bool|null, devices?, module?, note?}]. ready of a module channel comes from its
 * optional notify hook 'status' (ctx, {env}) -> {ready, note?}; null when the module does not say.
 */
function co_push_channels(string $env): array
{
    $n = 0;
    foreach (co_push_load() as $sub) if (is_array($sub) && ($sub['env'] ?? null) === $env) $n++;
    $out = [['id' => CO_PUSH_DEVICES, 'kind' => 'core', 'title' => 'CO push (subscribed devices)', 'ready' => $n > 0, 'devices' => $n]];
    if (!function_exists('co_module_hooks') || !function_exists('co_db')) return $out;
    try {
        $db = co_db();
        foreach (co_module_hooks($db, 'notify') as $p) {
            if (!isset($p['hooks']['push'])) continue;
            $c = ['id' => $p['module']['id'] . '/' . $p['id'], 'kind' => 'module', 'module' => $p['module']['id'],
                'title' => (string)($p['module']['manifest']['title'] ?? $p['module']['id']), 'ready' => null];
            if (isset($p['hooks']['status'])) {
                try {
                    $st = ($p['hooks']['status'])(co_module_ctx($db, $p['module']), ['env' => $env]);
                    if (is_array($st)) {
                        $c['ready'] = !empty($st['ready']);
                        if (isset($st['note'])) $c['note'] = mb_substr((string)$st['note'], 0, 200);
                    }
                } catch (Throwable $e) {
                    error_log("notify status {$c['id']}: " . $e->getMessage());
                }
            }
            $out[] = $c;
        }
    } catch (Throwable $e) {
        error_log('co_push_channels: ' . $e->getMessage());
    }
    return $out;
}

// ---------- Job finished (D 12.11.2026: the server's copy of Bridge's NotifyJobFinished) ----------
// Bridge (BridgePush.cs NotifyJobFinished) pushes the jobs it ran; the site agent has no push of its own, so the server
// pushes the jobs of these hosts when their result arrives (db.php job_result, cron/drive-sync.php). A job runs on one
// host only (co_queue_handover moves it before anyone ran it), so a Home<->Site move still gives one push.
const CO_PUSH_SERVER_HOSTS = ['site'];
const CO_PUSH_MIN_JOB_SECONDS = 45; // Bridge push.minJobSeconds default

/** Bridge PushPlain: rough markdown -> plain text, one line, at most 180 chars. */
function co_push_plain(?string $text): string
{
    if ($text === null || $text === '') {
        return '';
    }
    $s = (string)preg_replace('/```[\s\S]*?(```|$)/u', ' ', $text);
    $s = (string)preg_replace('/!?\[([^\]]*)\]\([^)]*\)/u', '$1', $s);
    $s = (string)preg_replace('/^[ \t]*[#>]+[ \t]*/mu', '', $s);
    $s = str_replace(['*', '`'], '', $s);
    $s = (string)preg_replace('/(?<!\w)_+|_+(?!\w)/u', '', $s);
    $s = str_replace(['#', '>'], '', $s);
    $s = trim((string)preg_replace('/\s+/u', ' ', $s));
    if (mb_strlen($s) > 180) {
        $s = rtrim(mb_substr($s, 0, 179)) . '…';
    }
    return $s;
}

/** A line of state/push-jobs.log (what the push services answered, e.g. fcm.googleapis.com 201). Never throws. */
function co_push_job_log(string $line): void
{
    try {
        $file = co_push_state_dir() . '/push-jobs.log';
        if (is_file($file) && filesize($file) > 1048576) {
            @rename($file, $file . '.1');
        }
        @file_put_contents($file, co_push_now() . ' ' . $line . "\n", FILE_APPEND | LOCK_EX);
    } catch (Throwable) {
    }
}

/**
 * Why the server sends no "job finished" push for this result ('' = it sends one). Bridge's rules: none for cancelled
 * jobs or without an environment, none for a job shorter than 45 s unless its chat is a schedule chat (title ⏰).
 * Also none for a result older than 30 min (drive-sync seeing a missed job_result late).
 */
function co_push_job_skip(string $host, array $req, array $res, string $chatTitle): string
{
    if (!in_array($host, CO_PUSH_SERVER_HOSTS, true)) return 'bridge host';
    if (($res['status'] ?? '') === 'cancelled') return 'cancelled';
    if (!is_string($req['environmentId'] ?? null) || $req['environmentId'] === '') return 'no env';
    if (!co_db_id_ok($req['jobId'] ?? null) || !co_db_id_ok($req['chatId'] ?? null)) return 'no job';
    $done = is_string($res['completedUtc'] ?? null) ? strtotime($res['completedUtc']) : false;
    if ($done !== false && $done < time() - 1800) return 'old';
    $started = is_string($res['startedUtc'] ?? null) ? strtotime($res['startedUtc']) : false;
    if ($started !== false && !str_starts_with($chatTitle, '⏰')
        && ($done !== false ? $done : time()) - $started < CO_PUSH_MIN_JOB_SECONDS) return 'short';
    return '';
}

/**
 * "✓ <chat title>" / "✗ <chat title>" push of a finished job of a server host, tag = chatId (CO Web's sw.js keeps it
 * quiet while that chat is on screen). Once per job: jobs.pushed_utc is set before the send, so a retried job_result or
 * drive-sync seeing the same result sends no second one; it is cleared again when no device took the push and some
 * failed (a later call retries). Returns what happened (logs, tests). Never throws.
 */
function co_push_job_finished(mysqli $db, string $host, array $req, array $res, ?callable $send = null): string
{
    try {
        $jobId = (string)($req['jobId'] ?? '');
        $chatId = (string)($req['chatId'] ?? '');
        $title = $chatId;
        if (co_db_id_ok($chatId)) {
            $st = $db->prepare('SELECT title FROM chats WHERE chat_id = ?');
            $st->execute([$chatId]);
            $t = trim((string)($st->get_result()->fetch_row()[0] ?? ''));
            $st->close();
            if ($t !== '') $title = $t;
        }
        $skip = co_push_job_skip($host, $req, $res, $title);
        if ($skip !== '') {
            return 'skip: ' . $skip;
        }
        $st = $db->prepare('UPDATE jobs SET pushed_utc = UTC_TIMESTAMP(3) WHERE job_id = ? AND pushed_utc IS NULL');
        $st->execute([$jobId]);
        $mine = $st->affected_rows === 1;
        $st->close();
        if (!$mine) {
            return 'skip: pushed';
        }
        $env = (string)$req['environmentId'];
        $completed = ($res['status'] ?? '') === 'completed';
        if ($completed) {
            $body = co_push_plain(is_string($res['agentFinalResponse'] ?? null) ? $res['agentFinalResponse'] : null);
        } else {
            $err = is_string($res['error'] ?? null) && $res['error'] !== '' ? $res['error'] : null;
            foreach (is_array($res['agentErrorMessages'] ?? null) ? $res['agentErrorMessages'] : [] as $m) {
                if ($err === null && is_string($m) && trim($m) !== '') $err = $m;
            }
            $body = co_push_plain($err);
        }
        if ($body === '') {
            $body = $completed ? 'Done' : 'Failed';
        }
        $url = '/e/' . rawurlencode($env) . '/p/' . rawurlencode((string)($req['projectId'] ?? '')) . '/c/' . rawurlencode($chatId);
        try {
            $r = co_push_notify($env, ($completed ? '✓ ' : '✗ ') . $title, $body, $url, $chatId, $send);
        } catch (Throwable $e) {
            $r = ['sent' => 0, 'failed' => 1, 'error' => $e->getMessage()];
        }
        // no device took it (push service down): the job may be pushed again (drive-sync sees the result a minute later)
        if (($r['sent'] ?? 0) === 0 && ($r['failed'] ?? 0) > 0) {
            $st = $db->prepare('UPDATE jobs SET pushed_utc = NULL WHERE job_id = ?');
            $st->execute([$jobId]);
            $st->close();
        }
        $out = 'sent ' . json_encode($r, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
        co_push_job_log("$jobId $host env=$env tag=$chatId $out");
        return $out;
    } catch (Throwable $e) {
        error_log('co_push_job_finished: ' . $e->getMessage());
        return 'error: ' . $e->getMessage();
    }
}

// ---------- Bridge offline watchdog (v1.2.8) ----------
// Bridge posts op=heartbeat every 5 min; any request that runs co_watch_check() (CO Web pages call op=watch every
// 5 min while visible, every notify) alerts the owner's env once per offline episode when the last heartbeat is > 15 min old.

function co_watch_file(): string
{
    return co_push_state_dir() . '/bridge-heartbeat.json';
}

/** Parse 'Y-m-d\TH:i:s\Z' (UTC) to a Unix timestamp, or null. */
function co_watch_ts(mixed $utc): ?int
{
    if (!is_string($utc) || $utc === '') {
        return null;
    }
    $d = DateTimeImmutable::createFromFormat('!Y-m-d\TH:i:s\Z', $utc, new DateTimeZone('UTC'));
    return $d === false ? null : $d->getTimestamp();
}

/** Read state without locking (writes are atomic). Always returns a normalized structure. */
function co_watch_read(string $file): array
{
    $state = ['hosts' => [], 'lastCheckUtc' => null];
    $raw = is_file($file) ? @file_get_contents($file) : false;
    if (!is_string($raw) || $raw === '') {
        return $state;
    }
    $d = json_decode($raw, true);
    if (!is_array($d)) {
        return $state;
    }
    if (isset($d['hosts']) && is_array($d['hosts'])) {
        foreach ($d['hosts'] as $h => $v) {
            if (is_array($v)) {
                $state['hosts'][(string)$h] = $v;
            }
        }
    }
    if (isset($d['lastCheckUtc']) && is_string($d['lastCheckUtc'])) {
        $state['lastCheckUtc'] = $d['lastCheckUtc'];
    }
    return $state;
}

function co_watch_write(string $file, array $state): void
{
    // Cast hosts to object so an empty map stays a JSON object.
    $out = ['hosts' => (object)$state['hosts'], 'lastCheckUtc' => $state['lastCheckUtc']];
    co_push_write_atomic(
        $file,
        json_encode($out, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR) . "\n"
    );
}

/** Locked read-modify-write: $fn(array &$state): mixed. Written only if modified. */
function co_watch_update(callable $fn): mixed
{
    $file = co_watch_file();
    $lock = @fopen($file . '.lock', 'c');
    if ($lock === false) {
        throw new RuntimeException('co_watch: cannot open lock file');
    }
    try {
        if (!flock($lock, LOCK_EX)) {
            throw new RuntimeException('co_watch: flock failed');
        }
        $state  = co_watch_read($file);
        $before = $state;
        $result = $fn($state);
        if ($state !== $before) {
            co_watch_write($file, $state);
        }
        return $result;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

/** Send a 'bridge-down' tagged push to the owner's env. Never throws. */
function co_watch_notify(string $title, string $body): void
{
    try {
        co_push_broadcast(
            static fn($sub): bool => is_array($sub) && co_owner_env() !== '' && ($sub['env'] ?? '') === co_owner_env(),
            ['title' => $title, 'body' => $body, 'url' => '/', 'tag' => 'bridge-down'],
            'bridge-down'
        );
    } catch (Throwable $e) {
        error_log('co_watch_notify: ' . $e->getMessage());
    }
}

/** Record a heartbeat; notifies recovery if the host was flagged offline. */
function co_watch_heartbeat(string $host, string $version): array
{
    $now = co_push_now();

    $recovered = co_watch_update(static function (array &$st) use ($host, $version, $now): ?array {
        $prev       = $st['hosts'][$host] ?? null;
        $wasAlerted = is_array($prev) && !empty($prev['alerted']);

        $st['hosts'][$host] = [
            'lastUtc'    => $now,
            'version'    => $version,
            'alerted'    => false,
            'alertedUtc' => null,
        ];

        return $wasAlerted ? ['prevLastUtc' => $prev['lastUtc'] ?? null] : null;
    });

    if ($recovered === null) {
        return ['ok' => true, 'recovered' => false];
    }

    $nowTs  = co_watch_ts($now) ?? time();
    $prevTs = co_watch_ts($recovered['prevLastUtc']);
    $mins   = $prevTs === null ? '?' : (string)max(0, intdiv($nowTs - $prevTs, 60));

    co_watch_notify('Bridge back online', $host . ' is back (v' . $version . ') after ' . $mins . ' min offline.');

    return ['ok' => true, 'recovered' => true];
}

/** Throttled check for stale hosts; alerts once per offline episode. Never throws. */
function co_watch_check(int $thresholdMin = 15, int $minIntervalSec = 60): array
{
    try {
        $now   = co_push_now();
        $nowTs = co_watch_ts($now) ?? time();
        $limit = $thresholdMin * 60;

        $res = co_watch_update(static function (array &$st) use ($now, $nowTs, $limit, $minIntervalSec): ?array {
            $lastCheck = co_watch_ts($st['lastCheckUtc']);
            // Throttle (ignore a lastCheck in the future, e.g. clock change).
            if ($lastCheck !== null && $lastCheck <= $nowTs && ($nowTs - $lastCheck) < $minIntervalSec) {
                return null;
            }
            $st['lastCheckUtc'] = $now;

            $hosts = [];
            $toAlert = [];
            foreach ($st['hosts'] as $host => $h) {
                $host = (string)$host;
                $last = co_watch_ts($h['lastUtc'] ?? null);
                if ($last === null) {
                    continue;
                }
                $age = max(0, $nowTs - $last);
                $hosts[$host] = intdiv($age, 60);

                if ($age > $limit && empty($h['alerted'])) {
                    $st['hosts'][$host]['alerted']    = true;
                    $st['hosts'][$host]['alertedUtc'] = $now;
                    $toAlert[] = ['host' => $host, 'last' => $last, 'min' => intdiv($age, 60)];
                }
            }
            return ['hosts' => $hosts, 'toAlert' => $toAlert];
        });

        if ($res === null) {
            return ['checked' => false];
        }

        $tz = new DateTimeZone('Europe/Brussels');
        $alerted = [];
        foreach ($res['toAlert'] as $a) {
            $hhmm = (new DateTimeImmutable('@' . $a['last']))->setTimezone($tz)->format('H:i');
            co_watch_notify(
                'Bridge offline',
                'No heartbeat from ' . $a['host'] . ' since ' . $hhmm . ' (' . $a['min'] . ' min). '
                . 'The PC may be asleep, off the network, or Bridge stopped.'
            );
            $alerted[] = $a['host'];
        }

        return ['checked' => true, 'alerted' => $alerted, 'hosts' => $res['hosts']];
    } catch (Throwable $e) {
        error_log('co_watch_check: ' . $e->getMessage());
        return ['checked' => false, 'error' => $e->getMessage()];
    }
}

/** Number of hosts currently over the threshold (lock-free read). Never throws. */
function co_watch_offline_count(int $thresholdMin = 15): int
{
    try {
        $st    = co_watch_read(co_watch_file());
        $nowTs = co_watch_ts(co_push_now()) ?? time();
        $n = 0;
        foreach ($st['hosts'] as $h) {
            $last = co_watch_ts($h['lastUtc'] ?? null);
            if ($last !== null && ($nowTs - $last) > $thresholdMin * 60) {
                $n++;
            }
        }
        return $n;
    } catch (Throwable $e) {
        error_log('co_watch_offline_count: ' . $e->getMessage());
        return 0;
    }
}
