<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Shared helpers of the /agent endpoints.
declare(strict_types=1);
require_once dirname(__DIR__) . '/auth/lib.php';

// The user's home: the folder above the web root that holds co-agent/ (Freedom F2: hPanel ~/domains/<site>/public_html,
// cPanel and DirectAdmin-like ~/public_html); env CO_AGENT_HOME wins.
function co_agent_home(): string {
    static $home = null;
    if ($home === null) {
        $home = (string)getenv('CO_AGENT_HOME');
        if ($home === '') {
            $home = dirname(__DIR__, 4);
            foreach ([4, 2, 3, 5] as $up) {
                if (@is_dir(dirname(__DIR__, $up) . '/co-agent')) {
                    $home = dirname(__DIR__, $up);
                    break;
                }
            }
        }
        $home = rtrim($home, '/');
    }
    return $home;
}

// Server part of install.json (Freedom F1): ~/co-agent/config.php returns an array (canvas_url, site_name, ...).
// Missing file or key = default.
function co_site_config(string $key, mixed $default = null): mixed {
    static $cfg = null;
    if ($cfg === null) {
        $f = co_agent_home() . '/co-agent/config.php';
        $c = is_file($f) ? require $f : [];
        $cfg = is_array($c) ? $c : [];
    }
    return $cfg[$key] ?? $default;
}

// Where Google Drive is mounted on the home host (install.json drive.root); the default is the mount of Google Drive
// for desktop. co_drive_path('AI-Workbench/tools') -> G:\My Drive\AI-Workbench\tools, for texts given to agents.
function co_drive_root(): string {
    return rtrim((string)co_site_config('drive_root', 'G:\\My Drive'), '\\/');
}

function co_drive_path(string $rel = ''): string {
    $rel = ltrim(str_replace('/', '\\', $rel), '\\');
    return $rel === '' ? co_drive_root() : co_drive_root() . '\\' . $rel;
}

// S7: the site agent (~/co-agent/executor/agent.mjs) is alive when the pid of agent.pid runs agent.mjs.
function co_site_agent_running(): bool {
    $dir = co_agent_home() . '/co-agent/executor';
    $pid = is_file($dir . '/agent.pid') ? (int)file_get_contents($dir . '/agent.pid') : 0;
    return $pid > 0 && is_dir('/proc/' . $pid) && str_contains((string)@file_get_contents('/proc/' . $pid . '/cmdline'), 'agent.mjs');
}

// S7: starts the site agent when it is not running (ping.php of CO Web, db.php job_submit / subtask_spawn for the site,
// cron drive-sync.php with queued site jobs). One start per 20 s under a lock: the agent itself exits when another
// instance runs, but two starts in one second could both pass that check. Returns true when a start was issued.
function co_site_agent_kick(bool $needSession = true): bool {
    $home = co_agent_home();
    $dir = $home . '/co-agent/executor';
    if (co_site_agent_running() || !is_file($dir . '/run.sh')) return false;
    if ($needSession && !is_file($home . '/co-agent/secrets/google-session.json')) return false;
    $state = $home . '/co-agent/state';
    if (!is_dir($state)) @mkdir($state, 0700, true);
    $lock = @fopen($state . '/agent-kick.lock', 'c+');
    if ($lock === false || !flock($lock, LOCK_EX | LOCK_NB)) return false;
    try {
        $last = (int)stream_get_contents($lock);
        if ($last > time() - 20 || co_site_agent_running()) return false;
        ftruncate($lock, 0);
        rewind($lock);
        fwrite($lock, (string)time());
        fflush($lock);
        // Freedom F1: the agent calls db.php of this site (S1 device relay); site_url of ~/co-agent/config.php
        // (install.json site.url), else the name of this virtual host, else (cron) the domain of the web dir.
        $site = (string)co_site_config('site_url', '');
        if ($site === '' && ($_SERVER['SERVER_NAME'] ?? '') !== '') $site = 'https://' . $_SERVER['SERVER_NAME'];
        if ($site === '' && preg_match('#/domains/([^/]+)/public_html/#', __DIR__ . '/', $m)) $site = 'https://' . $m[1];
        // The agent must not inherit this PHP's descriptors (the lock, a MySQL connection, the caller's pipes): it would
        // keep them open for its whole life (a cron run or an SSH session would not end, a database connection leaks).
        $cmd = 'for f in /proc/$$/fd/*; do n=${f##*/}; [ "$n" -gt 2 ] 2>/dev/null && eval "exec $n>&-"; done; '
            . 'cd ' . escapeshellarg($dir) . ' && CO_DB_URL=' . escapeshellarg(rtrim($site, '/') . '/agent/db.php')
            . ' setsid nohup ./run.sh >> logs/daemon.out 2>&1 < /dev/null &';
        $p = @proc_open(['/bin/sh', '-c', $cmd], [], $pipes);
        if (!is_resource($p)) return false;
        proc_close($p);
        return true;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

// Signed-in Google account: the CO Web cookie session, or the bearer token CO Desktop sends (it has no cookie).
function co_agent_email(?array $session): string {
    if ($session && !empty($session['rt'])) return strtolower((string)($session['email'] ?? ''));
    if (!preg_match('/^Bearer\s+(\S+)$/', $_SERVER['HTTP_AUTHORIZATION'] ?? '', $m)) return '';
    // 2026-10-16: the answer is cached for 10 min per token (db.php is called on every list), and a token without the
    // email scope (Bridge, possibly CO Desktop) is resolved through Drive about.user, which any Drive token may read.
    $cache = co_agent_home() . '/co-agent/state/token-email-' . hash('sha256', $m[1]);
    if (is_file($cache) && filemtime($cache) > time() - 600) return (string)file_get_contents($cache);
    $get = static function (string $url, array $headers = []): array {
        $ch = curl_init($url);
        curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => 10, CURLOPT_HTTPHEADER => $headers]);
        $j = json_decode((string)curl_exec($ch), true) ?: [];
        curl_close($ch);
        return $j;
    };
    $info = $get('https://oauth2.googleapis.com/tokeninfo?access_token=' . urlencode($m[1]));
    $email = ($info['email_verified'] ?? '') === 'true' ? strtolower((string)($info['email'] ?? '')) : '';
    if ($email === '' && isset($info['scope']) && str_contains((string)$info['scope'], '/auth/drive')) {
        $about = $get('https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)', ['Authorization: Bearer ' . $m[1]]);
        $email = strtolower((string)($about['user']['emailAddress'] ?? ''));
    }
    if ($email !== '') {
        if (!is_dir(dirname($cache))) mkdir(dirname($cache), 0700, true);
        file_put_contents($cache, $email);
        // Drop cache files older than a day.
        foreach (glob(dirname($cache) . '/token-email-*') ?: [] as $f) if (filemtime($f) < time() - 86400) @unlink($f);
    }
    return $email;
}

// One prompt through the server's Claude Code CLI without tools; returns the model's text.
function co_agent_claude(string $prompt, string $model, int $timeoutSec): string {
    $home = co_agent_home();
    $work = $home . '/co-agent/state/search';
    if (!is_dir($work)) mkdir($work, 0700, true);
    $env = [
        'HOME' => $home,
        'PATH' => '/opt/alt/alt-nodejs24/root/usr/bin:' . $home . '/co-agent/cli/node_modules/.bin:/usr/bin:/bin',
        // PHP-spawned processes have a 4 GB virtual memory limit (see run.sh).
        'NODE_OPTIONS' => '--disable-wasm-trap-handler',
        'CLAUDE_CODE_OAUTH_TOKEN' => trim((string)@file_get_contents($home . '/co-agent/secrets/claude_oauth_token')),
    ];
    $cmd = ['/usr/bin/timeout', (string)$timeoutSec, $home . '/co-agent/cli/node_modules/.bin/claude',
        '-p', '--model', $model, '--output-format', 'json', '--tools', '', '--no-session-persistence', '--setting-sources', '',
        '--system-prompt', 'You are a helper inside the CO web app. Do exactly what the message asks and answer in the format it asks for.'];
    $p = proc_open($cmd, [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes, $work, $env);
    if (!is_resource($p)) throw new RuntimeException('cannot start claude');
    fwrite($pipes[0], $prompt);
    fclose($pipes[0]);
    $out = (string)stream_get_contents($pipes[1]);
    $err = (string)stream_get_contents($pipes[2]);
    fclose($pipes[1]);
    fclose($pipes[2]);
    $rc = proc_close($p);
    $j = json_decode($out, true);
    if (!is_array($j) || !empty($j['is_error']) || !isset($j['result'])) {
        throw new RuntimeException('claude rc=' . $rc . ': ' . substr(trim($err !== '' ? $err : $out), 0, 300));
    }
    return (string)$j['result'];
}

// First JSON object in a model answer (it may wrap the object in a code fence).
function co_agent_json_from(string $text): array {
    $a = strpos($text, '{');
    $b = strrpos($text, '}');
    $j = ($a !== false && $b > $a) ? json_decode(substr($text, $a, $b - $a + 1), true) : null;
    if (!is_array($j)) throw new RuntimeException('model answer is not JSON');
    return $j;
}
