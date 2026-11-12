<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom B/C cron sidecar: runs the cron lines of the hosting panel (freedom_setup.py CRON_LINES, written by
// freedom_docker.py deploy into ~/co-agent/crontab) inside the app image, as the user co, with the same PHP scripts.
// Five-field lines: minute hour day month weekday (*, */n, a-b, a,b). A line still running is not started again.
// Every finished run goes to the container log and ~/co-agent/state/freedom-cron.json (freedom_docker.py verify).
declare(strict_types=1);

const CRONTAB = '/home/co/co-agent/crontab';
const STATE = '/home/co/co-agent/state/freedom-cron.json';

function field_ok(string $f, int $v, int $min, int $max): bool {
    foreach (explode(',', $f) as $part) {
        $step = 1;
        if (str_contains($part, '/')) {
            [$part, $s] = explode('/', $part, 2);
            $step = max(1, (int)$s);
        }
        if ($part === '*') {
            [$a, $b] = [$min, $max];
        } elseif (str_contains($part, '-')) {
            [$a, $b] = array_map('intval', explode('-', $part, 2));
        } else {
            $a = $b = (int)$part;
        }
        if ($v >= $a && $v <= $b && ($v - $a) % $step === 0) return true;
    }
    return false;
}

function line_due(array $f, int $t): bool {
    return field_ok($f[0], (int)gmdate('i', $t), 0, 59) && field_ok($f[1], (int)gmdate('G', $t), 0, 23)
        && field_ok($f[2], (int)gmdate('j', $t), 1, 31) && field_ok($f[3], (int)gmdate('n', $t), 1, 12)
        && field_ok($f[4], (int)gmdate('w', $t), 0, 6);
}

function read_lines(): array {
    $out = [];
    foreach (@file(CRONTAB, FILE_IGNORE_NEW_LINES) ?: [] as $l) {
        $l = trim($l);
        if ($l === '' || $l[0] === '#') continue;
        $p = preg_split('/\s+/', $l, 6);
        if (count($p) === 6) $out[] = ['fields' => array_slice($p, 0, 5), 'cmd' => $p[5]];
    }
    return $out;
}

// The last $n bytes of $s without cutting a UTF-8 character in two (script output may be Cyrillic).
function tail_bytes(string $s, int $n): string {
    if (strlen($s) <= $n) return $s;
    $start = strlen($s) - $n;
    while ($start < strlen($s) && (ord($s[$start]) & 0xC0) === 0x80) $start++;
    return substr($s, $start);
}

function say(string $s): void {
    fwrite(STDOUT, gmdate('Y-m-d\TH:i:s\Z') . ' ' . $s . "\n");
}

$running = [];   // cmd => [proc, pipes, start, out]
$state = is_file(STATE) ? (json_decode((string)file_get_contents(STATE), true) ?: []) : [];
$lastMinute = 0;
say('freedom-cron started, crontab ' . CRONTAB);
for (;;) {
    $now = time();
    $minute = intdiv($now, 60);
    if ($minute !== $lastMinute) {
        $lastMinute = $minute;
        $lines = read_lines();
        if (!$lines) say('no lines in ' . CRONTAB . ' yet (freedom_docker.py deploy writes it)');
        foreach ($lines as $l) {
            if (!line_due($l['fields'], $now) || isset($running[$l['cmd']])) continue;
            $p = proc_open(['/bin/sh', '-c', $l['cmd']], [0 => ['file', '/dev/null', 'r'], 1 => ['pipe', 'w'], 2 => ['redirect', 1]],
                $pipes, '/home/co');
            if (!is_resource($p)) {
                say('cannot start: ' . $l['cmd']);
                continue;
            }
            stream_set_blocking($pipes[1], false);
            $running[$l['cmd']] = ['p' => $p, 'out' => $pipes[1], 'start' => microtime(true), 'text' => ''];
        }
    }
    foreach ($running as $cmd => &$r) {
        $r['text'] .= (string)stream_get_contents($r['out']);
        if (strlen($r['text']) > 20000) $r['text'] = tail_bytes($r['text'], 20000);
        $st = proc_get_status($r['p']);
        if ($st['running']) continue;
        $r['text'] .= (string)stream_get_contents($r['out']);
        fclose($r['out']);
        proc_close($r['p']);
        $rc = $st['exitcode'];
        $secs = round(microtime(true) - $r['start'], 1);
        $tail = trim(tail_bytes($r['text'], 600));
        say("rc={$rc} {$secs}s {$cmd}" . ($tail !== '' ? "\n  " . str_replace("\n", "\n  ", $tail) : ''));
        $s = $state[$cmd] ?? ['runs' => 0, 'ok' => 0];
        $s['runs']++;
        $s['ok'] += $rc === 0 ? 1 : 0;
        $s['lastRc'] = $rc;
        $s['lastUtc'] = gmdate('c');
        $s['lastOutput'] = tail_bytes($tail, 300);
        $state[$cmd] = $s;
        // a broken byte (output that is not UTF-8) must not empty the state file: substitute it, never write false
        $json = json_encode($state, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
        if ($json !== false && @file_put_contents(STATE . '.tmp', $json) !== false) @rename(STATE . '.tmp', STATE);
        unset($running[$cmd]);
    }
    unset($r);
    usleep(500000);
}
