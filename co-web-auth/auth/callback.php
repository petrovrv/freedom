<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
require __DIR__ . '/lib.php';

$cfg = co_config();
$flow = co_open($_COOKIE[CO_FLOW_COOKIE] ?? null);
co_set_cookie(CO_FLOW_COOKIE, '', 0, 'Lax');

if (isset($_GET['error'])) co_page('Sign-in cancelled', 'Google returned: ' . (string)$_GET['error']);
if (!$flow || !isset($_GET['state'], $_GET['code']) || !hash_equals($flow['s'], (string)$_GET['state']) || time() - (int)$flow['t'] > 600) {
    co_page('Sign-in failed', 'The sign-in link expired or was opened in another browser. Start again.');
}

[$status, $tok] = co_post_form('https://oauth2.googleapis.com/token', [
    'client_id' => $cfg['client_id'], 'client_secret' => $cfg['client_secret'],
    'code' => (string)$_GET['code'], 'code_verifier' => $flow['v'],
    'grant_type' => 'authorization_code', 'redirect_uri' => $cfg['origin'] . '/auth/callback.php',
]);
if ($status !== 200 || empty($tok['id_token'])) co_page('Sign-in failed', 'Token exchange: ' . ($tok['error_description'] ?? $tok['error'] ?? "HTTP $status"));

// The ID token comes straight from Google over TLS in this exchange, so its payload is trusted as is.
$parts = explode('.', $tok['id_token']);
$claims = json_decode(co_unb64url($parts[1] ?? ''), true) ?: [];
$email = strtolower((string)($claims['email'] ?? ''));
if (($claims['aud'] ?? '') !== $cfg['client_id'] || empty($claims['email_verified'])) co_page('Sign-in failed', 'Unexpected identity token.');
// Freedom F1: members of the workbench (table members) may sign in; config allowlist is the bootstrap list for the
// installer before the table has rows.
$memberLib = dirname(__DIR__) . '/agent/members-lib.php';
if (is_file($memberLib)) require_once $memberLib;
$isMember = function_exists('co_push_users') && isset(co_push_users()[$email]);
if (!$isMember && !in_array($email, $cfg['allowlist'] ?? [], true)) {
    co_post_form('https://oauth2.googleapis.com/revoke', ['token' => $tok['refresh_token'] ?? $tok['access_token']]);
    co_page('Not allowed', $email . ' is not allowed to use CO.');
}
if (empty($tok['refresh_token'])) co_page('Sign-in incomplete', 'Google did not issue a long-lived token. Remove CO in myaccount.google.com → Security → Third-party access and sign in again.');

co_set_cookie(CO_SESSION_COOKIE, co_seal(['rt' => $tok['refresh_token'], 'email' => $email, 'iat' => time()]), CO_SESSION_MAX_AGE, 'Strict');
header('Cache-Control: no-store');
$back = (string)($flow['b'] ?? '/');
header('Location: ' . (preg_match('#^/canvas-share\?id=[0-9a-f]{32}$#', $back) ? $back : '/'));
