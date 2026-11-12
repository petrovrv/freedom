<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
require __DIR__ . '/lib.php';

co_require_api_call();
$session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
if ($session && !empty($session['rt'])) {
    co_post_form('https://oauth2.googleapis.com/revoke', ['token' => $session['rt']]);
}
co_set_cookie(CO_SESSION_COOKIE, '', 0, 'Strict');
co_json(200, ['ok' => true]);
