<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
function home_pair_locked(array $c, callable $f): array {
    $path = $c['stateDir'] . '/pair.json';
    $lock = fopen($c['stateDir'] . '/pair.lock', 'c');
    if (!$lock || !flock($lock, LOCK_EX)) throw new RuntimeException('pair_lock');
    try {
        $s = json_decode((string)@file_get_contents($path), true) ?: [];
        $out = $f($s);
        co_push_write_atomic($path, json_encode($s, JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR));
        return $out;
    } finally { flock($lock, LOCK_UN); fclose($lock); }
}
function home_pair_view(array $s): array {
    $r = $s['report'] ?? ['state' => 'idle'];
    $r['online'] = time() - ($s['seen'] ?? 0) < 20 && ($r['connected'] ?? false);
    $r['serverNow'] = time();
    $cmd = $s['command'] ?? null;
    if ($cmd && ($r['id'] ?? '') !== $cmd['id'] && $cmd['expires'] > time()) {
        $r['state'] = $cmd['action'] === 'start' ? 'starting' : 'stopping';
    }
    if (!$r['online']) $r['state'] = 'offline';
    return $r;
}
function home_pair_user(array $c, string $action): array {
    if (!$c['owner']) return ['error' => 'owner_only'];
    return home_pair_locked($c, static function (&$s) use ($action) {
        if ($action !== 'state') {
            $view = home_pair_view($s);
            if (!$view['online']) return ['error' => 'worker_offline'];
            $pending = ($s['command']['expires'] ?? 0) > time()
                && ($s['report']['id'] ?? '') !== ($s['command']['id'] ?? '');
            // Concurrent tabs and repeated clicks share the same start.
            if ($action === 'start' && ($pending || in_array($view['state'], ['starting','searching','interviewing'], true)))
                return $view;
            $s['command'] = ['id' => bin2hex(random_bytes(12)), 'action' => $action,
                'expires' => time() + 15];
        }
        return home_pair_view($s);
    });
}
return [
 'user' => [
    'home_pair_state' => static fn($c, $i) => home_pair_user($c, 'state'),
    'home_pair_start' => static fn($c, $i) => home_pair_user($c, 'start'),
    'home_pair_stop' => static fn($c, $i) => home_pair_user($c, 'stop'),
 ],
 'host' => [
    'home_pair_exchange' => static function ($c, $in) {
        return home_pair_locked($c, static function (&$s) use ($in) {
            $r = $in['report'] ?? [];
            $states = ['idle','starting','searching','interviewing','success','timeout','stopped','error','stopping'];
            if (!is_array($r) || !in_array($r['state'] ?? '', $states, true))
                return ['error' => 'bad_report'];
            $s['report'] = [
                'id' => substr((string)($r['id'] ?? ''),0,48),
                'state' => $r['state'], 'connected' => ($r['connected'] ?? false) === true,
                'until' => (int)($r['until'] ?? 0),
                'device' => mb_substr((string)($r['device'] ?? ''),0,100),
                'note' => mb_substr((string)($r['note'] ?? ''),0,160),
            ];
            $s['seen'] = time();
            $cmd = $s['command'] ?? null;
            return ['command' => $cmd && $cmd['expires'] > time() ? $cmd : null];
        });
    }
 ],
];
