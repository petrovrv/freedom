<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// tile.php of the module geo-presence (Freedom F10; was web/agent/geotile.php of CO Web 0.97.0): OpenStreetMap tiles for
// the "place on the map" privacy picker. Route agent/module.php?m=geo-presence&r=tile&z=&x=&y=&t=. The module page runs in
// a sandboxed iframe (opaque origin, no cookies), so access is the signed token of geo_state; a CO Web cookie also works.
// Zoom 3-17, cached on disk 30 days, an identifying User-Agent (OSM tile usage policy).
declare(strict_types=1);

function co_geo_tile_route(): void
{
    $z = filter_input(INPUT_GET, 'z', FILTER_VALIDATE_INT);
    $x = filter_input(INPUT_GET, 'x', FILTER_VALIDATE_INT);
    $y = filter_input(INPUT_GET, 'y', FILTER_VALIDATE_INT);
    if (!is_int($z) || !is_int($x) || !is_int($y) || $z < 3 || $z > 17 || $x < 0 || $y < 0 || $x >= (1 << $z) || $y >= (1 << $z)) {
        http_response_code(400);
        exit;
    }
    $tok = $_GET['t'] ?? '';
    $cookie = $_COOKIE[CO_SESSION_COOKIE] ?? null;
    try {
        $tokOk = is_string($tok) && co_geo_tile_ok($tok);
    } catch (RuntimeException) {
        $tokOk = false;
    }
    if (!$tokOk && !isset(co_push_users()[co_agent_email(co_open(is_string($cookie) ? $cookie : null))])) {
        http_response_code(403);
        exit;
    }

    $dir = co_geo_file('geotiles');
    $file = "$dir/$z-$x-$y.png";
    if (!is_file($file) || filemtime($file) < time() - 30 * 86400) {
        $ctx = stream_context_create(['http' => ['timeout' => 10, 'header' => "User-Agent: Freedom-Workbench-CO/1 (family presence map; https://"
            . ($_SERVER['HTTP_HOST'] ?? 'localhost') . ")\r\n"]]);   // OSM tile policy: a contact of the site
        $png = @file_get_contents("https://tile.openstreetmap.org/$z/$x/$y.png", false, $ctx);
        if ($png === false || strncmp($png, "\x89PNG", 4) !== 0) {
            if (!is_file($file)) {
                http_response_code(502);
                exit;
            }
        } else {
            if (!is_dir($dir)) @mkdir($dir, 0700, true);
            co_push_write_atomic($file, $png, 0600);
        }
    }
    header('Content-Type: image/png');
    header('Cache-Control: private, max-age=604800');
    readfile($file);
    exit;
}
