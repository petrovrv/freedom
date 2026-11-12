<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Keeps the server agent (~/co-agent/executor) running and hands it the owner's Google session.
// Called by CO Web every minute while the "Site" agent is selected. The owner and members with site_agent (table
// members) may start the agent (it runs their jobs on the owner's subscriptions); only the owner's session is handed to it.
declare(strict_types=1);
require __DIR__ . '/lib.php';
require_once __DIR__ . '/members-lib.php';

co_require_api_call();
$home = co_agent_home();
$agentDir = $home . '/co-agent';
$session = co_open($_COOKIE[CO_SESSION_COOKIE] ?? null);
$email = co_agent_email($session);
if ($email === '' || !in_array($email, co_site_agent_emails(), true)) co_json(403, ['error' => 'owner_only']);

// The agent reads Drive with the owner's refresh token; refresh it when the browser has a newer one.
if ($email === co_owner_email() && $session && !empty($session['rt'])) {
    $file = $agentDir . '/secrets/google-session.json';
    $cur = is_file($file) ? json_decode((string)file_get_contents($file), true) : null;
    if (($cur['rt'] ?? '') !== $session['rt']) {
        $old = umask(077);
        file_put_contents($file . '.tmp', json_encode(['rt' => $session['rt'], 'email' => $email, 'savedUtc' => gmdate('c')]));
        rename($file . '.tmp', $file);
        umask($old);
    }
}

// S7: the start itself is shared with db.php (job_submit / subtask_spawn for the site) and cron drive-sync.php.
$running = co_site_agent_running();
$started = !$running && co_site_agent_kick(false);
co_json(200, ['running' => $running, 'started' => $started]);
