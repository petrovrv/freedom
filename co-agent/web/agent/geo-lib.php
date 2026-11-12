<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// geo-lib.php (CO Web 0.91.0, icon 0.93.0): family presence in the CO Web header. geo-presence on the home PC (OwnTracks) puts
// a short status per member (state, place name, distance, no coordinates) through push.php op=geo_put; CO Web reads it
// with db.php op=geo_state. Frequent places come as suggestions ("remember this place?") shown to the member and to the
// owner; their answers (geo_answer) wait here until the next geo_put picks them up and acknowledges them.
// 0.97.0: privacy per member (geo_privacy, only the member sets it): where I am / only heading home / a saved place /
// a place picked on the map / off, plus manual "I'm home" and "heading home". geo-presence filters on the home PC before
// geo_put (the real place never comes here); this file scrubs again when a member changes the mode, so the header does not
// show the real place until the PC's next put. Icon questions (kind icon): a known place without an icon asks for an emoji.
declare(strict_types=1);

require_once __DIR__ . '/push-lib.php';

const CO_GEO_STATES = ['home', 'left', 'coming', 'walk', 'there', 'moving', 'stale', 'nodata', 'away'];
const CO_GEO_MODES = ['full', 'home', 'saved', 'map', 'off'];

function co_geo_file(string $name): string
{
    return co_push_state_dir() . '/' . $name;
}

/** Runs $fn under an exclusive lock of the geo state files. */
function co_geo_locked(callable $fn): mixed
{
    $h = fopen(co_geo_file('geo.lock'), 'c');
    if ($h === false) throw new RuntimeException('Cannot open geo lock');
    try {
        flock($h, LOCK_EX);
        return $fn();
    } finally {
        flock($h, LOCK_UN);
        fclose($h);
    }
}

function co_geo_read(string $name): array
{
    $f = co_geo_file($name);
    if (!is_file($f)) return [];
    $j = json_decode((string)file_get_contents($f), true);
    return is_array($j) ? $j : [];
}

function co_geo_write(string $name, array $data): void
{
    co_push_write_atomic(co_geo_file($name), json_encode($data, JSON_UNESCAPED_UNICODE));
}

function co_geo_text(mixed $v, int $n): string
{
    if (!is_string($v)) return '';
    $v = trim(preg_replace('/[\x00-\x1f\x7f]+/u', ' ', $v) ?? '');
    return mb_substr($v, 0, $n);
}

function co_geo_num(mixed $v): ?float
{
    return is_int($v) || is_float($v) ? (float)$v : null;
}

/** Cleans one member status from the PC. */
function co_geo_member(array $m): ?array
{
    $user = co_geo_text($m['user'] ?? null, 20);
    $state = co_geo_text($m['state'] ?? null, 10);
    if (!in_array($user, co_member_envs(), true) || !in_array($state, CO_GEO_STATES, true)) return null;
    $out = ['user' => $user, 'name' => co_geo_text($m['name'] ?? null, 40), 'state' => $state,
        'place' => co_geo_text($m['place'] ?? null, 60), 'activity' => co_geo_text($m['activity'] ?? null, 60), 'icon' => co_geo_text($m['icon'] ?? null, 8)];
    $pv = co_geo_text($m['pv'] ?? null, 8);
    if (in_array($pv, CO_GEO_MODES, true)) $out['pv'] = $pv;
    if (is_int($m['pvTs'] ?? null)) $out['pvTs'] = $m['pvTs'];
    foreach (['since', 'updated', 'etaMin', 'km', 'batt'] as $k) {
        $n = co_geo_num($m[$k] ?? null);
        if ($n !== null) $out[$k] = $n;
    }
    return $out;
}

function co_geo_suggestion(array $s): ?array
{
    $id = co_geo_text($s['id'] ?? null, 40);
    $user = co_geo_text($s['user'] ?? null, 20);
    if (!preg_match('/^[a-z0-9-]{4,40}$/', $id) || !in_array($user, co_member_envs(), true)) return null;
    return ['id' => $id, 'user' => $user, 'area' => co_geo_text($s['area'] ?? null, 60),
        'reason' => co_geo_text($s['reason'] ?? null, 120), 'kind' => ($s['kind'] ?? '') === 'icon' ? 'icon' : 'place',
        'place' => co_geo_text($s['place'] ?? null, 60)];
}

/** A saved place the privacy picker offers (name + icon; no coordinates). */
function co_geo_place(array $p): ?array
{
    $name = co_geo_text($p['name'] ?? null, 60);
    return $name === '' ? null : ['name' => $name, 'icon' => co_geo_text($p['icon'] ?? null, 8)];
}

/** The privacy setting of one member, or null when it has expired or is the default. */
function co_geo_pv(array $all, string $env, int $now): ?array
{
    $p = $all[$env] ?? null;
    if (!is_array($p)) return null;
    if (!empty($p['until']) && (int)$p['until'] <= $now) $p = array_intersect_key($p, ['manual' => 1]);
    $man = is_array($p['manual'] ?? null) ? $p['manual'] : null;
    // manual "I'm home" holds 12 h, "heading home" 3 h (geo-presence ends them earlier on real data)
    if ($man && $now - (int)($man['ts'] ?? 0) > (($man['state'] ?? '') === 'home' ? 12 : 3) * 3600) unset($p['manual']);
    if (($p['mode'] ?? 'full') === 'full') return null;
    return $p;
}

/** Server-side scrub of a status the PC sent before it knew the current mode (map mode: neutral "away"). */
function co_geo_scrub(array $m, ?array $pv, array $places): array
{
    if ($pv === null) return $m;
    $mode = (string)($pv['mode'] ?? 'full');
    $man = is_array($pv['manual'] ?? null) ? $pv['manual'] : null;
    // "man" marks a state set by a manual button, so a later button replaces it (a real "home" is never overridden)
    if ($man && ($m['state'] !== 'home' || !empty($m['man'])) && in_array($man['state'] ?? '', ['home', 'coming'], true)) {
        $m['state'] = $man['state'];
        $m['since'] = (int)($man['ts'] ?? 0);
        $m['man'] = 1;
        unset($m['etaMin']);
    }
    // trust a status the PC already filtered only for this very setting (mode and the time it was set)
    if (($m['pv'] ?? '') === $mode && (int)($m['pvTs'] ?? 0) === (int)($pv['ts'] ?? 0)) return $m;
    unset($m['km'], $m['activity']);
    $m['pv'] = $mode;
    $m['pvTs'] = (int)($pv['ts'] ?? 0);
    if ($m['state'] === 'home') {
        $m['place'] = '';
        $m['icon'] = '';
        return $m;
    }
    $fake = null;
    if ($mode === 'saved') {
        foreach ($places as $p) {
            if (($p['name'] ?? '') === ($pv['place'] ?? null)) $fake = $p;
        }
    }
    if ($m['state'] === 'coming') {
        $m['place'] = $fake['name'] ?? '';
        $m['icon'] = $fake['icon'] ?? '';
        return $m;
    }
    if ($fake !== null) {
        return array_merge($m, ['state' => 'there', 'place' => $fake['name'], 'icon' => $fake['icon'], 'since' => (int)($pv['ts'] ?? 0)]);
    }
    unset($m['since'], $m['batt']);
    return array_merge($m, ['state' => 'away', 'place' => '', 'icon' => '']);
}

/** push.php op=geo_put (home PC key): store statuses and suggestions, drop acknowledged answers, return the rest. */
function co_geo_put(array $in): array
{
    $members = [];
    foreach (is_array($in['members'] ?? null) ? $in['members'] : [] as $m) {
        if (is_array($m) && ($c = co_geo_member($m)) !== null) $members[] = $c;
    }
    $sugg = [];
    foreach (is_array($in['suggestions'] ?? null) ? $in['suggestions'] : [] as $s) {
        if (is_array($s) && ($c = co_geo_suggestion($s)) !== null) $sugg[] = $c;
    }
    $places = [];
    foreach (is_array($in['places'] ?? null) ? array_slice($in['places'], 0, 100) : [] as $p) {
        if (is_array($p) && ($c = co_geo_place($p)) !== null) $places[] = $c;
    }
    $c = is_array($in['center'] ?? null) ? $in['center'] : [];
    $center = count($c) === 2 && co_geo_num($c[0]) !== null && co_geo_num($c[1]) !== null
        ? [round((float)$c[0], 2), round((float)$c[1], 2)] : null;
    $ack = array_values(array_filter(is_array($in['ack'] ?? null) ? $in['ack'] : [], 'is_string'));
    return co_geo_locked(static function () use ($members, $sugg, $ack, $places, $center): array {
        $now = time();
        $pvAll = co_geo_read('geo-privacy.json');
        $pvOut = [];
        foreach (co_member_envs() as $env) {
            if (($pv = co_geo_pv($pvAll, $env, $now)) !== null) $pvOut[$env] = $pv;
        }
        foreach ($members as $i => $m) $members[$i] = co_geo_scrub($m, $pvOut[$m['user']] ?? null, $places);
        // a member who hides the place gets no "remember this place?" questions in the header
        $sugg = array_values(array_filter($sugg, static fn(array $s): bool => !isset($pvOut[$s['user']])));
        $answers = array_values(array_filter(co_geo_read('geo-answers.json'),
            static fn($a): bool => is_array($a) && !in_array($a['id'] ?? '', $ack, true)));
        $answered = array_column($answers, 'id');
        // a suggestion already answered on the web stays hidden until the PC has taken the answer
        $sugg = array_values(array_filter($sugg, static fn(array $s): bool => !in_array($s['id'], $answered, true)));
        co_geo_write('geo-status.json', ['updated' => $now, 'members' => $members, 'suggestions' => $sugg, 'places' => $places, 'center' => $center]);
        co_geo_write('geo-answers.json', $answers);
        return ['ok' => true, 'answers' => $answers, 'privacy' => (object)$pvOut];
    });
}

/** db.php op=geo_state: statuses for every family member, suggestions of the signed-in member (owner sees all). */
function co_geo_state(string $myEnv, bool $owner): array
{
    $s = co_geo_read('geo-status.json');
    $sugg = array_values(array_filter(is_array($s['suggestions'] ?? null) ? $s['suggestions'] : [],
        static fn($x): bool => is_array($x) && ($owner || ($x['user'] ?? '') === $myEnv)));
    $now = time();
    $mine = co_geo_pv(co_geo_read('geo-privacy.json'), $myEnv, $now);
    $members = array_map(static function ($m) {
        if (is_array($m)) unset($m['pv'], $m['pvTs'], $m['man']);
        return $m;
    }, is_array($s['members'] ?? null) ? $s['members'] : []);
    return ['updated' => (int)($s['updated'] ?? 0), 'now' => $now, 'members' => $members, 'suggestions' => $sugg,
        'places' => is_array($s['places'] ?? null) ? $s['places'] : [], 'center' => $s['center'] ?? null,
        'privacy' => $mine ?? ['mode' => 'full'], 'tileToken' => co_geo_tile_token_safe()];
}

/** Signed token for geotile.php (CO Desktop and other bearer clients send no cookie with <img>): "<exp>.<hmac>", 1 day. */
function co_geo_tile_token(?int $exp = null): string
{
    $exp ??= (intdiv(time(), 3600) + 25) * 3600;
    return $exp . '.' . substr(hash_hmac('sha256', 'geotile.' . $exp, co_geo_tile_key()), 0, 32);
}

function co_geo_tile_token_safe(): string
{
    try {
        return co_geo_tile_token();
    } catch (RuntimeException) {
        return '';  // the map then shows no tiles; the header itself keeps working
    }
}

function co_geo_tile_key(): string
{
    $f = co_geo_file('geotile.key');
    if (!is_file($f)) co_push_write_atomic($f, bin2hex(random_bytes(32)), 0600);
    $k = @file_get_contents($f);
    // never sign with an empty or short key (an unreadable file would make tokens forgeable)
    if (!is_string($k) || strlen($k) < 32) throw new RuntimeException('geotile key unreadable');
    return $k;
}

function co_geo_tile_ok(string $t): bool
{
    if (!preg_match('/^(\d{10})\.[0-9a-f]{32}$/', $t, $m) || (int)$m[1] < time()) return false;
    return hash_equals(co_geo_tile_token((int)$m[1]), $t);
}

/** db.php op=geo_privacy: the signed-in member sets the own mode (nobody else, the owner neither) or a manual state. */
function co_geo_privacy(string $myEnv, array $in): array
{
    if (!in_array($myEnv, co_member_envs(), true)) return ['ok' => false, 'error' => 'forbidden'];
    return co_geo_locked(static function () use ($myEnv, $in): array {
        $now = time();
        $all = co_geo_read('geo-privacy.json');
        $s = co_geo_read('geo-status.json');
        $places = is_array($s['places'] ?? null) ? $s['places'] : [];
        $p = is_array($all[$myEnv] ?? null) ? $all[$myEnv] : [];
        if (array_key_exists('mode', $in)) {
            $mode = co_geo_text($in['mode'], 8);
            if (!in_array($mode, CO_GEO_MODES, true)) return ['ok' => false, 'error' => 'bad_mode'];
            $p = array_intersect_key($p, ['manual' => 1]);
            $p['mode'] = $mode;
            $p['ts'] = $now;
            if ($mode === 'saved') {
                $name = co_geo_text($in['place'] ?? null, 60);
                if (!in_array($name, array_column($places, 'name'), true)) return ['ok' => false, 'error' => 'bad_place'];
                $p['place'] = $name;
            }
            if ($mode === 'map') {
                $lat = co_geo_num($in['lat'] ?? null);
                $lon = co_geo_num($in['lon'] ?? null);
                if ($lat === null || $lon === null || abs($lat) > 85 || abs($lon) > 180) return ['ok' => false, 'error' => 'bad_point'];
                $p['lat'] = round($lat, 5);
                $p['lon'] = round($lon, 5);
            }
            $until = co_geo_num($in['until'] ?? null);
            if ($until !== null && $until > $now) $p['until'] = (int)min($until, $now + 30 * 86400);
            if ($mode === 'full') unset($p['manual']);
        }
        if (array_key_exists('manual', $in)) {
            $st = co_geo_text($in['manual'], 8);
            if ($st === '') unset($p['manual']);
            elseif (in_array($st, ['home', 'coming'], true)) $p['manual'] = ['state' => $st, 'ts' => $now];
            else return ['ok' => false, 'error' => 'bad_manual'];
        }
        $all[$myEnv] = $p;
        co_geo_write('geo-privacy.json', $all);
        // scrub the stored status now; the PC takes the setting on its next put (within 5 min) and filters itself
        $pv = co_geo_pv($all, $myEnv, $now);
        if ($s) {
            $members = is_array($s['members'] ?? null) ? $s['members'] : [];
            foreach ($members as $i => $m) {
                if (is_array($m) && ($m['user'] ?? '') === $myEnv) {
                    unset($m['pv']);
                    $members[$i] = co_geo_scrub($m, $pv, $places);
                }
            }
            if ($pv !== null && is_array($s['suggestions'] ?? null)) {
                $s['suggestions'] = array_values(array_filter($s['suggestions'], static fn($x): bool => is_array($x) && ($x['user'] ?? '') !== $myEnv));
            }
            $s['members'] = $members;
            $s['pvChanged'] = $now;
            co_geo_write('geo-status.json', $s);
        }
        return ['ok' => true, 'privacy' => $pv ?? ['mode' => 'full']];
    });
}

/** db.php op=geo_answer: name a suggested place (name) or decline it (empty name). */
function co_geo_answer(string $myEnv, bool $owner, array $in): array
{
    $id = co_geo_text($in['id'] ?? null, 40);
    $name = co_geo_text($in['name'] ?? null, 60);
    return co_geo_locked(static function () use ($id, $name, $myEnv, $owner): array {
        $s = co_geo_read('geo-status.json');
        $list = is_array($s['suggestions'] ?? null) ? $s['suggestions'] : [];
        $hit = null;
        foreach ($list as $x) {
            if (is_array($x) && ($x['id'] ?? '') === $id) $hit = $x;
        }
        if ($hit === null) return ['ok' => false, 'error' => 'not_found'];
        if (!$owner && ($hit['user'] ?? '') !== $myEnv) return ['ok' => false, 'error' => 'forbidden'];
        $answers = co_geo_read('geo-answers.json');
        $answers[] = ['id' => $id, 'user' => $hit['user'], 'name' => $name, 'by' => $myEnv, 'ts' => time()];
        co_geo_write('geo-answers.json', array_slice($answers, -50));
        $s['suggestions'] = array_values(array_filter($list, static fn($x): bool => is_array($x) && ($x['id'] ?? '') !== $id));
        co_geo_write('geo-status.json', $s);
        return ['ok' => true];
    });
}
