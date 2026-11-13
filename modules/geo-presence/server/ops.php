<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module geo-presence (Freedom F10, SPEC §3.6): the handlers modules-lib.php calls.
//   user: geo_state, geo_answer, geo_privacy - the header page (ui/header.html) through the ext/1 bridge of CO Web
//   push: geo_put - the geo-presence service on the home PC (push key), unchanged wire format
//   routes: tile - map tiles for the privacy picker
//   install: moves the data the core kept before F10 (~/co-agent/state/geo-*.json, geotile.key, geotiles/) into the
//            module's data folder, once; remove: nothing to do (the core keeps or erases the folder)
declare(strict_types=1);

require_once __DIR__ . '/geo-lib.php';
require_once __DIR__ . '/tile.php';

return [
    'user' => [
        'geo_state'   => static fn(array $c, array $in): array => co_geo_state($c['myEnv'], $c['owner'], $c['person']),
        'geo_answer'  => static fn(array $c, array $in): array => co_geo_answer($c['myEnv'], $c['owner'], $in),
        'geo_privacy' => static fn(array $c, array $in): array => co_geo_privacy($c['myEnv'], $in),
    ],
    'push' => [
        'geo_put' => static fn(array $c, array $in): array => co_geo_put($in, $c['person']),
    ],
    'routes' => [
        'tile' => static fn(array $c): mixed => co_geo_tile_route(),
    ],
    'install' => static function (array $c, array $info): array {
        $old = co_push_state_dir();
        $moved = [];
        // statuses, privacy choices, answers and the tile key must arrive (else the module would run with defaults over
        // people's choices: the hook throws and the core keeps the old registration); geotiles is only a cache
        foreach (['geo-status.json', 'geo-privacy.json', 'geo-answers.json', 'geotile.key', 'geotiles'] as $f) {
            if (!file_exists("$old/$f") || file_exists($c['stateDir'] . "/$f")) continue;
            if (@rename("$old/$f", $c['stateDir'] . "/$f")) $moved[] = $f;
            elseif ($f !== 'geotiles') throw new RuntimeException("cannot move $f into the module data folder");
        }
        @unlink("$old/geo.lock");
        return ['moved' => $moved];
    },
];
