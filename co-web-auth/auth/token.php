<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Returns a fresh access token for the signed-in browser; renews the session cookie (sliding 400 days).
declare(strict_types=1);
require_once __DIR__ . '/lib.php';
require_once dirname(__DIR__) . '/agent/members-lib.php';

co_require_api_call();
$cfg = co_config();
$session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
if (!$session || empty($session['rt'])) co_json(401, ['error' => 'signed_out']);

[$status, $tok] = co_post_form('https://oauth2.googleapis.com/token', [
    'client_id' => $cfg['client_id'], 'client_secret' => $cfg['client_secret'],
    'refresh_token' => $session['rt'], 'grant_type' => 'refresh_token',
]);
if ($status === 400 || $status === 401) {
    // invalid_grant: revoked by the user, password change, or Testing-mode expiry.
    co_set_cookie(CO_SESSION_COOKIE, '', 0, 'Strict');
    co_json(401, ['error' => 'signed_out', 'reason' => $tok['error'] ?? 'invalid_grant']);
}
if ($status !== 200 || empty($tok['access_token'])) co_json(502, ['error' => 'google_unavailable']);

co_set_cookie(CO_SESSION_COOKIE, co_seal($session), CO_SESSION_MAX_AGE, 'Strict');
// 2026-10-16: the site's own jobs (cron/drive-sync.php, the server agent) read Drive with the owner's refresh token;
// keep their copy current on every token refresh of the owner, not only while the Site host is selected (ping.php).
if (strtolower((string)($session['email'] ?? '')) === co_owner_email() && co_owner_email() !== '' && !empty($session['rt'])) {
    $gs = dirname(__DIR__, 4) . '/co-agent/secrets/google-session.json';
    $cur = is_file($gs) ? json_decode((string)file_get_contents($gs), true) : null;
    if (is_dir(dirname($gs)) && ($cur['rt'] ?? '') !== $session['rt']) {
        $old = umask(077);
        file_put_contents($gs . '.tmp', json_encode(['rt' => $session['rt'], 'email' => $session['email'], 'savedUtc' => gmdate('c')]));
        rename($gs . '.tmp', $gs);
        umask($old);
    }
}
co_json(200, [
    'access_token' => $tok['access_token'],
    'expires_in' => (int)($tok['expires_in'] ?? 3599),
    'scope' => $tok['scope'] ?? '',
    'email' => $session['email'],
]);
