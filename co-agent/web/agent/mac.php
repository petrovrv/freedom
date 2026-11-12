<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// mac.php: CO Resident for macOS (Apple Silicon) — the install offer in CO Web and the one-time install link.
//   POST {op:'state'}                 -> offer state of the signed-in user (eligible, release, dismissed, installed)
//   POST {op:'dismiss', mode}         -> 'later' (7 days) | 'never'
//   POST {op:'token'}                 -> one-time install command (link valid 30 min, one download)
//   GET  ?op=install&t=TOKEN          -> install.sh for Terminal (curl … | bash), bound to the user's environment
//   GET  ?op=bin&t=TOKEN              -> the release archive; uses up the token
//   GET  ?op=update (Bearer)          -> the newest archive for the resident's self-update (Bridge signs the manifest)
//   GET  ?op=check&v=X.Y.Z (Bearer)   -> the Mac check test bundle of release X.Y.Z (the resident pins its sha256)
// Files live outside public_html in ~/co-desktop/mac: latest.json + archive (co-desktop/tools/publish_mac.py),
// tokens/ and state/<env>.json.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/push-lib.php';

const CO_MAC_TOKEN_TTL = 1800;
const CO_MAC_LATER_DAYS = 7;

function co_mac_dir(string $sub = ''): string
{
    $d = co_agent_home() . '/co-desktop/mac' . ($sub !== '' ? '/' . $sub : '');
    if (!is_dir($d)) @mkdir($d, 0700, true);
    return $d;
}

// The release in latest.json, or the one pinned in a token ($pinned: the record written at token time).
function co_mac_release(?array $pinned = null): ?array
{
    if ($pinned !== null) {
        $d = $pinned;
    } else {
        $json = @file_get_contents(co_mac_dir() . '/latest.json');
        if ($json === false) return null;
        $d = json_decode($json, true);
    }
    if (!is_array($d)) return null;
    $name = $d['fileName'] ?? null;
    if (!is_string($name) || !preg_match('/^co-resident-\d+\.\d+\.\d+-osx-arm64\.tar\.gz$/', $name)) return null;
    $path = co_mac_dir() . '/' . $name;
    clearstatcache(true, $path);
    if (!is_file($path) || is_link($path)) return null;
    $sha = is_string($d['sha256'] ?? null) ? strtolower($d['sha256']) : '';
    if (!preg_match('/^[0-9a-f]{64}$/', $sha)) return null;
    return [
        'version' => is_string($d['version'] ?? null) ? $d['version'] : '',
        'fileName' => $name, 'size' => (int)filesize($path), 'sha256' => $sha,
        'publishedUtc' => is_string($d['publishedUtc'] ?? null) ? $d['publishedUtc'] : '',
        'path' => $path,
    ];
}

// [email, env] of the signed-in CO user (cookie session or bearer), or null.
function co_mac_user(): ?array
{
    $cookie = $_COOKIE[CO_SESSION_COOKIE] ?? null;
    $email = co_agent_email(co_open(is_string($cookie) ? $cookie : null));
    $users = co_push_users();
    if ($email === '' || !array_key_exists($email, $users)) return null;
    return [$email, $users[$email]];
}

function co_mac_state_file(string $env): string
{
    return co_mac_dir('state') . '/' . preg_replace('/[^a-z0-9_-]/', '', $env) . '.json';
}

function co_mac_state(string $env): array
{
    $j = @file_get_contents(co_mac_state_file($env));
    $d = $j === false ? null : json_decode($j, true);
    return is_array($d) ? $d : [];
}

// Atomic write through a unique temporary file; false when it did not persist.
function co_mac_write(string $file, array $data): bool
{
    $tmp = $file . '.' . bin2hex(random_bytes(6)) . '.tmp';
    if (file_put_contents($tmp, json_encode($data, JSON_PRETTY_PRINT)) === false) return false;
    if (!rename($tmp, $file)) { @unlink($tmp); return false; }
    return true;
}

// Read-modify-write of state/<env>.json under an exclusive lock; the callback changes the array in place.
function co_mac_update_state(string $env, callable $change): bool
{
    $f = co_mac_state_file($env);
    $lock = fopen($f . '.lock', 'c');
    if ($lock === false || !flock($lock, LOCK_EX)) return false;
    try {
        $s = co_mac_state($env);
        $change($s);
        return co_mac_write($f, $s);
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

function co_mac_token_file(string $token): ?string
{
    if (!preg_match('/^[0-9a-f]{64}$/', $token)) return null;
    return co_mac_dir('tokens') . '/' . hash('sha256', $token) . '.json';
}

// Token record if not expired (and, unless $allowUsed, not used yet), else null.
function co_mac_token(string $token, bool $allowUsed = false): ?array
{
    $f = co_mac_token_file($token);
    if ($f === null || !is_file($f)) return null;
    $d = json_decode((string)file_get_contents($f), true);
    if (!is_array($d) || (int)($d['expires'] ?? 0) < time()) return null;
    if (($d['used'] ?? false) && !$allowUsed) return null;
    return $d;
}

// Marks the token used under a lock; false if another request used it first or the write failed.
function co_mac_use_token(string $token): bool
{
    $f = co_mac_token_file($token);
    if ($f === null) return false;
    $lock = fopen($f . '.lock', 'c');
    if ($lock === false || !flock($lock, LOCK_EX)) return false;
    try {
        $rec = co_mac_token($token);
        if ($rec === null) return false;
        $rec['used'] = true;
        $rec['usedUtc'] = gmdate('c');
        return co_mac_write($f, $rec);
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
        @unlink($f . '.lock');
    }
}

function co_mac_cleanup_tokens(): void
{
    foreach (glob(co_mac_dir('tokens') . '/*.json') ?: [] as $f) {
        if (filemtime($f) < time() - 2 * 86400) @unlink($f);
    }
}

function co_mac_text(int $status, string $text): never
{
    http_response_code($status);
    header('Content-Type: text/plain; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    echo $text;
    exit;
}

$method = $_SERVER['REQUEST_METHOD'] ?? '';

if ($method === 'POST') {
    co_require_api_call();
    $user = co_mac_user();
    if ($user === null) co_json(403, ['error' => 'forbidden']);
    [$email, $env] = $user;
    $raw = file_get_contents('php://input');
    $body = json_decode($raw === false ? '' : $raw, true);
    if (!is_array($body) || !is_string($body['op'] ?? null)) co_json(400, ['error' => 'bad_request']);
    $op = $body['op'];
    $rel = co_mac_release();
    $st = co_mac_state($env);

    if ($op === 'state') {
        co_json(200, [
            'env' => $env,
            'eligible' => !co_is_owner_env($env), // shown by itself to members; the owner opens it from the Host tab
            'release' => $rel === null ? null : ['version' => $rel['version'], 'size' => $rel['size'], 'publishedUtc' => $rel['publishedUtc']],
            'never' => (bool)($st['never'] ?? false),
            'laterUntil' => (int)($st['laterUntil'] ?? 0),
            'installedUtc' => is_string($st['installedUtc'] ?? null) ? $st['installedUtc'] : '',
            'installedVersion' => is_string($st['installedVersion'] ?? null) ? $st['installedVersion'] : '',
            'now' => time(),
        ]);
    }
    if ($op === 'dismiss') {
        $mode = $body['mode'] ?? '';
        if ($mode !== 'never' && $mode !== 'later') co_json(400, ['error' => 'bad_request']);
        $ok = co_mac_update_state($env, function (array &$s) use ($mode): void {
            if ($mode === 'never') $s['never'] = true;
            else $s['laterUntil'] = time() + CO_MAC_LATER_DAYS * 86400;
        });
        if (!$ok) co_json(500, ['error' => 'not_saved']);
        co_json(200, ['ok' => true]);
    }
    if ($op === 'token') {
        if ($rel === null) co_json(404, ['error' => 'no_release']);
        co_mac_cleanup_tokens();
        $token = bin2hex(random_bytes(32));
        // The release is pinned: install.sh and the archive come from the same release even if a new one is published.
        $rec = ['env' => $env, 'email' => $email, 'expires' => time() + CO_MAC_TOKEN_TTL, 'used' => false, 'createdUtc' => gmdate('c'),
            'release' => ['version' => $rel['version'], 'fileName' => $rel['fileName'], 'sha256' => $rel['sha256'], 'publishedUtc' => $rel['publishedUtc']]];
        if (!co_mac_write((string)co_mac_token_file($token), $rec)) co_json(500, ['error' => 'not_saved']);
        // The offer's "I understand and allow" box was ticked.
        co_mac_update_state($env, function (array &$s): void { $s['consentUtc'] = gmdate('c'); });
        $url = co_config()['origin'] . '/agent/mac.php?op=install&t=' . $token;
        co_json(200, ['command' => "curl -fsSL '" . $url . "' | bash", 'expiresIn' => CO_MAC_TOKEN_TTL, 'version' => $rel['version']]);
    }
    co_json(400, ['error' => 'bad_request']);
}

// op=update: the newest archive for a paired resident's self-update (SPEC R2), with the device's Google token (Bearer).
// The resident checks it against the Bridge-signed manifest (size, sha256) before it unpacks anything.
if ($method === 'GET' && ($_GET['op'] ?? '') === 'update') {
    $user = co_mac_user();
    if ($user === null) co_json(403, ['error' => 'forbidden']);
    $rel = co_mac_release();
    if ($rel === null) co_json(404, ['error' => 'no_release']);
    co_mac_update_state($user[1], function (array &$s) use ($rel): void {
        $s['updateUtc'] = gmdate('c');
        $s['updateVersion'] = $rel['version'];
    });
    set_time_limit(0);
    @ini_set('zlib.output_compression', '0');
    while (ob_get_level() > 0) ob_end_clean();
    header('Content-Type: application/gzip');
    header('Content-Disposition: attachment; filename="' . $rel['fileName'] . '"');
    header('Content-Length: ' . $rel['size']);
    header('X-Content-Type-Options: nosniff');
    header('Cache-Control: private, no-store');
    readfile($rel['path']);
    exit;
}

// op=check: the test bundle of one release for the resident's mac_check tool (SPEC R2 Mac check). The resident compares
// it with the sha256 compiled into its own binary, so the site only stores it.
if ($method === 'GET' && ($_GET['op'] ?? '') === 'check') {
    if (co_mac_user() === null) co_json(403, ['error' => 'forbidden']);
    $ver = is_string($_GET['v'] ?? null) ? $_GET['v'] : '';
    if (!preg_match('/^\d+\.\d+\.\d+$/', $ver)) co_json(400, ['error' => 'bad_version']);
    $name = 'co-mac-check-' . $ver . '-osx-arm64.tar.gz';
    $path = co_mac_dir() . '/' . $name;
    clearstatcache(true, $path);
    if (!is_file($path) || is_link($path)) co_json(404, ['error' => 'no_bundle']);
    set_time_limit(0);
    @ini_set('zlib.output_compression', '0');
    while (ob_get_level() > 0) ob_end_clean();
    header('Content-Type: application/gzip');
    header('Content-Disposition: attachment; filename="' . $name . '"');
    header('Content-Length: ' . filesize($path));
    header('X-Content-Type-Options: nosniff');
    header('Cache-Control: private, no-store');
    readfile($path);
    exit;
}

// op=done: install.sh reports a finished setup (the token was used by op=bin and is still within its 30 minutes).
if ($method === 'GET' && ($_GET['op'] ?? '') === 'done') {
    $token = is_string($_GET['t'] ?? null) ? $_GET['t'] : '';
    $rec = co_mac_token($token, true);
    if ($rec === null || !($rec['used'] ?? false) || !preg_match('/^[a-z]+$/', (string)$rec['env'])) co_mac_text(403, "expired\n");
    $ver = is_string($_GET['v'] ?? null) && preg_match('/^\d+\.\d+\.\d+$/', $_GET['v']) ? $_GET['v'] : '';
    $ok = co_mac_update_state((string)$rec['env'], function (array &$s) use ($ver): void {
        $s['installedUtc'] = gmdate('c');
        $s['installedVersion'] = $ver;
    });
    co_mac_text($ok ? 200 : 500, $ok ? "ok\n" : "not saved\n");
}

if ($method === 'GET' && in_array($_GET['op'] ?? '', ['install', 'bin'], true)) {
    $token = is_string($_GET['t'] ?? null) ? $_GET['t'] : '';
    $rec = co_mac_token($token);
    if ($rec === null) co_mac_text(403, "echo 'This install link has expired or was already used. Open CO and get a new one.' >&2; exit 1\n");
    $rel = is_array($rec['release'] ?? null) ? co_mac_release($rec['release']) : null;
    if ($rel === null) co_mac_text(404, "echo 'This CO Resident release is no longer on the server; get a new link in CO.' >&2; exit 1\n");
    $env = (string)$rec['env'];
    if (!preg_match('/^[a-z]+$/', $env)) co_mac_text(400, "exit 1\n");

    if ($_GET['op'] === 'install') {
        $script = file_get_contents(__DIR__ . '/mac-install.sh');
        if ($script === false) co_mac_text(500, "echo 'install script missing on the server' >&2; exit 1\n");
        $script = str_replace("\r\n", "\n", $script); // bash on the Mac stops on CRLF ("pipefail\r: invalid option name")
        $base = co_config()['origin'] . '/agent/mac.php?t=' . $token;
        co_mac_text(200, strtr($script, [
            '@@BIN_URL@@' => $base . '&op=bin', '@@DONE_URL@@' => $base . '&op=done&v=' . $rel['version'],
            '@@SHA256@@' => $rel['sha256'], '@@ENV@@' => $env, '@@VERSION@@' => $rel['version'],
        ]));
    }

    // op=bin: one download per token; "installed" is recorded only by op=done after a finished setup.
    if (!co_mac_use_token($token)) co_mac_text(403, "This install link was already used.\n");
    set_time_limit(0);
    @ini_set('zlib.output_compression', '0');
    while (ob_get_level() > 0) ob_end_clean();
    header('Content-Type: application/gzip');
    header('Content-Disposition: attachment; filename="' . $rel['fileName'] . '"');
    header('Content-Length: ' . $rel['size']);
    header('X-Content-Type-Options: nosniff');
    header('Cache-Control: private, no-store');
    readfile($rel['path']);
    exit;
}

co_json(400, ['error' => 'bad_request']);
