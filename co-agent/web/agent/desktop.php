<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// desktop.php: latest CO Desktop installer for signed-in users — release info (POST) and download (GET ?op=download).
// The installer and latest.json live outside public_html in ~/co-desktop; co-desktop/tools/publish_site.py puts them there.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/push-lib.php';

// latest.json with the installer next to it, or null.
function co_desktop_latest(): ?array
{
    $dir = co_agent_home() . '/co-desktop';
    $json = @file_get_contents($dir . '/latest.json');
    if ($json === false) return null;
    $data = json_decode($json, true);
    if (!is_array($data)) return null;
    $fileName = $data['fileName'] ?? null;
    if (!is_string($fileName) || !preg_match('/^CO-Desktop-Setup-\d+\.\d+\.\d+\.exe$/', $fileName)) return null;
    $path = $dir . '/' . $fileName;
    clearstatcache(true, $path);
    if (!is_file($path) || is_link($path)) return null;
    $size = filesize($path);
    if ($size === false) return null;
    return [
        'version' => is_string($data['version'] ?? null) ? $data['version'] : '',
        'fileName' => $fileName,
        'size' => $size,
        'sha256' => is_string($data['sha256'] ?? null) ? strtolower($data['sha256']) : '',
        'publishedUtc' => is_string($data['publishedUtc'] ?? null) ? $data['publishedUtc'] : '',
        'path' => $path,
    ];
}

// Any of the CO users (cookie session or CO Desktop bearer).
function co_desktop_signed_in(): bool
{
    $cookie = $_COOKIE[CO_SESSION_COOKIE] ?? null;
    $email = co_agent_email(co_open(is_string($cookie) ? $cookie : null));
    return $email !== '' && array_key_exists($email, co_push_users());
}

$method = $_SERVER['REQUEST_METHOD'] ?? '';

if ($method === 'POST') {
    co_require_api_call();
    if (!co_desktop_signed_in()) co_json(403, ['error' => 'forbidden']);
    $op = 'latest';
    $raw = file_get_contents('php://input');
    if ($raw !== false && trim($raw) !== '') {
        $body = json_decode($raw, true);
        if (!is_array($body) || (array_key_exists('op', $body) && !is_string($body['op']))) co_json(400, ['error' => 'bad_request']);
        $op = $body['op'] ?? 'latest';
    }
    if ($op !== 'latest') co_json(400, ['error' => 'bad_request']);
    $rel = co_desktop_latest();
    if ($rel === null) co_json(404, ['error' => 'no_release']);
    unset($rel['path']);
    co_json(200, $rel);
}

// A top-level link navigation carries only the cookie; refuse cross-site requests when the browser tells.
if ($method === 'GET' && ($_GET['op'] ?? null) === 'download') {
    $site = $_SERVER['HTTP_SEC_FETCH_SITE'] ?? null;
    if ($site !== null && $site !== 'same-origin' && $site !== 'none') co_json(403, ['error' => 'forbidden']);
    if (!co_desktop_signed_in()) {
        header('Cache-Control: no-store');
        header('Location: /', true, 302);
        exit;
    }
    $rel = co_desktop_latest();
    if ($rel === null) co_json(404, ['error' => 'no_release']);
    set_time_limit(0);
    @ini_set('zlib.output_compression', '0');
    while (ob_get_level() > 0) ob_end_clean();
    header('Content-Type: application/vnd.microsoft.portable-executable');
    header('Content-Disposition: attachment; filename="' . $rel['fileName'] . '"');
    header('Content-Length: ' . $rel['size']);
    header('X-Content-Type-Options: nosniff');
    header('Cache-Control: private, no-store');
    readfile($rel['path']);
    exit;
}

co_json(400, ['error' => 'bad_request']);
