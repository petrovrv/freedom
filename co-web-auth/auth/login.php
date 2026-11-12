<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
require __DIR__ . '/lib.php';

$cfg = co_config();
$verifier = co_b64url(random_bytes(32));
$state = co_b64url(random_bytes(16));
// 0.68.0 (canvas M5): ?add=gmail asks for gmail.compose on top of the granted scopes (Share makes a Gmail draft);
// ?back=/canvas-share?id=... returns there after sign-in.
$scopes = $cfg['scopes'];
if (($_GET['add'] ?? '') === 'gmail') $scopes .= ' https://www.googleapis.com/auth/gmail.compose';
$back = $_GET['back'] ?? '';
if (!is_string($back) || !preg_match('#^/canvas-share\?id=[0-9a-f]{32}$#', $back)) $back = '/';
co_set_cookie(CO_FLOW_COOKIE, co_seal(['s' => $state, 'v' => $verifier, 't' => time(), 'b' => $back]), 600, 'Lax');

$params = [
    'response_type' => 'code',
    'client_id' => $cfg['client_id'],
    'redirect_uri' => $cfg['origin'] . '/auth/callback.php',
    'scope' => $scopes,
    'access_type' => 'offline',
    'prompt' => 'consent',
    'include_granted_scopes' => 'true',
    'state' => $state,
    'code_challenge' => co_b64url(hash('sha256', $verifier, true)),
    'code_challenge_method' => 'S256',
];
$hint = $_GET['hint'] ?? '';
if (is_string($hint) && filter_var($hint, FILTER_VALIDATE_EMAIL)) $params['login_hint'] = $hint;
header('Cache-Control: no-store');
header('Location: https://accounts.google.com/o/oauth2/v2/auth?' . http_build_query($params));
