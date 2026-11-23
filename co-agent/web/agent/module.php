<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// module.php (schema 27, Freedom F10, SPEC §3.6): files and routes of enabled modules.
//   GET ?m=<id>&f=ui/<file>   a file of the module's interface (html, js, css, svg, png, json) - only files registered at
//                             install whose sha256 still matches; CO Web shows the html in a sandboxed iframe (ext/1)
//                             F14c: a module of some environments (manifest envs) also needs &k=<file key of a person
//                             it is for> (modules_list fileKey); its html, css and js get the key into their ?m=<id>&f= links
//   GET ?m=<id>&f=device/<file> device code of the module (ext/1 device.tools), as a download; the device checks sha256
//   GET ?m=<id>&r=<route>     a route of a standard module's server code (manifest server.routes); it checks access itself
//   POST ?m=<id>&u=<name>     an upload of a standard module (manifest server.uploads): CO Web posts a file for the
//                             module's page (ext/1 coExt.upload); only signed-in members, the handler gets their env
// A disabled or unknown module answers 404, so its interface disappears with the switch.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/modules-lib.php';
require_once __DIR__ . '/members-lib.php';

const CO_MODULE_MIME = ['html' => 'text/html; charset=utf-8', 'js' => 'text/javascript; charset=utf-8', 'css' => 'text/css; charset=utf-8',
    'svg' => 'image/svg+xml', 'png' => 'image/png', 'json' => 'application/json; charset=utf-8'];

function co_module_404(): never
{
    http_response_code(404);
    header('Content-Type: text/plain');
    echo 'not found';
    exit;
}

try {
    $id = is_string($_GET['m'] ?? null) ? $_GET['m'] : '';
    if (!preg_match(CO_MODULE_ID_RE, $id)) co_module_404();
    $db = co_db();
    $m = co_modules($db)[$id] ?? null;
    if ($m === null || !$m['enabled']) co_module_404();

    // F14c: the files, routes and uploads of a module of some environments only for its people (an unknown key: 404)
    $key = is_string($_GET['k'] ?? null) ? $_GET['k'] : '';
    $scoped = co_module_envs($m) !== [];
    if ($scoped && !co_module_file_key_ok($m, $key)) co_module_404();

    $f = is_string($_GET['f'] ?? null) ? $_GET['f'] : '';
    if ($f !== '' && str_starts_with($f, 'device/')) {
        // device code of the module (ext/1 device.tools): CO Desktop / CO Resident download it and check its sha256
        // against db.php modules_list; never rendered here
        if (!preg_match(CO_MODULE_DEVICE_FILE_RE, $f) || str_contains($f, '..') || !isset($m['files'][$f])) co_module_404();
        $path = co_module_home($id) . "/$f";
        $sha = (string)$m['files'][$f];
        if (!is_file($path) || !hash_equals($sha, (string)hash_file('sha256', $path))) co_module_404();
        header('Content-Type: application/octet-stream');
        header('Content-Disposition: attachment; filename="' . basename($f) . '"');
        header('X-Content-Type-Options: nosniff');
        header("Content-Security-Policy: sandbox; default-src 'none'");
        header('Cache-Control: no-cache');
        header('ETag: "' . substr($sha, 0, 32) . '"');
        readfile($path);
        exit;
    }
    if ($f !== '') {
        $ext = strtolower(pathinfo($f, PATHINFO_EXTENSION));
        if (!preg_match('#^ui/[A-Za-z0-9._/-]+$#', $f) || str_contains($f, '..') || !isset(CO_MODULE_MIME[$ext]) || !isset($m['files'][$f])) co_module_404();
        $path = co_module_home($id) . "/$f";
        $sha = (string)$m['files'][$f];
        if (!is_file($path) || !hash_equals($sha, (string)hash_file('sha256', $path))) co_module_404();
        // a page loads /ext/ext1.js, which the site and its CDN cache for a week (13.11 the CDN kept serving the ext/1
        // of CO Web 0.102.0 after 0.103.0 went out): the page names it with the hash of the current file
        $extV = '';
        if ($ext === 'html' && is_file($ep = dirname(__DIR__) . '/ext/ext1.js')) $extV = substr((string)hash_file('sha256', $ep), 0, 12);
        $etag = substr($sha, 0, 32) . ($extV !== '' ? "-$extV" : '') . ($scoped ? '-' . substr($key, 0, 8) : '');
        header('Content-Type: ' . CO_MODULE_MIME[$ext]);
        header('X-Content-Type-Options: nosniff');
        // a scoped module's files are never kept by a shared cache (the key is in the address)
        header('Cache-Control: ' . ($scoped ? 'private, no-cache' : 'no-cache'));
        header('ETag: "' . $etag . '"');
        // the page of a module: own scripts and styles only, no network of its own (data comes over postMessage from
        // CO Web), images from this site (routes such as map tiles), framed only by CO Web (and the browser extensions
        // of enabled modules, whose panel frames CO Web: ext/1 provides.client)
        // sandbox: opened as a normal page (a link, a new tab) it still runs with an opaque origin, never as CO Web
        $framers = implode('', array_map(static fn(string $o): string => " $o", co_module_ext_origins($db)));
        header("Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
            . "img-src 'self' data:; connect-src 'none'; frame-ancestors 'self'$framers; base-uri 'none'; form-action 'none'");
        header('Referrer-Policy: no-referrer');
        if (trim((string)($_SERVER['HTTP_IF_NONE_MATCH'] ?? ''), '"') === $etag) {
            http_response_code(304);
            exit;
        }
        if ($extV === '' && !$scoped) {
            readfile($path);
            exit;
        }
        $body = (string)file_get_contents($path);
        if ($extV !== '') $body = str_replace(['src="/ext/ext1.js"', "src='/ext/ext1.js'"], 'src="/ext/ext1.js?v=' . $extV . '"', $body);
        // the module's own files (?m=<id>&f=... in its html, css and js, as the template writes them) carry the key
        if ($scoped && in_array($ext, ['html', 'css', 'js'], true)) {
            $body = str_replace(["?m=$id&amp;f=", "?m=$id&f="], ["?m=$id&amp;k=$key&amp;f=", "?m=$id&k=$key&f="], $body);
        }
        echo $body;
        exit;
    }

    $u = is_string($_GET['u'] ?? null) ? $_GET['u'] : '';
    if ($u !== '') {
        if (!in_array($u, co_module_uploads($m), true)) co_module_404();
        co_require_api_call();
        $email = co_agent_email(co_open($_COOKIE[CO_SESSION_COOKIE] ?? null));
        $users = co_push_users();
        if (!isset($users[$email])) co_json(403, ['error' => 'forbidden']);
        $h = co_module_load($m)['uploads'][$u] ?? null;
        if (!is_callable($h)) co_module_404();
        $myEnv = $users[$email];
        $res = $h(co_module_ctx($db, $m, $myEnv, co_is_owner_env($myEnv), $email));
        co_json(200, is_array($res) ? $res : ['ok' => true]);
    }

    $r = is_string($_GET['r'] ?? null) ? $_GET['r'] : '';
    if (!preg_match('/^[a-z][a-z0-9_-]{0,31}$/', $r) || !in_array($r, (array)($m['manifest']['server']['routes'] ?? []), true)) co_module_404();
    $h = co_module_load($m)['routes'][$r] ?? null;
    if (!is_callable($h)) co_module_404();
    $h(co_module_ctx($db, $m));
} catch (Throwable $e) {
    error_log('module.php: ' . $e->getMessage());
    http_response_code(500);
}
