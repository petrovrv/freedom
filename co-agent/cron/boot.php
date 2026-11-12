<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// boot.php (Freedom F1): where the web part of the site lives, for scripts in ~/co-agent/cron and ~/co-agent/db.
// CO_AGENT_WEB = env CO_AGENT_WEB, else web_dir of ~/co-agent/config.php (install.json), else the only
// ~/domains/*/public_html/agent with db-lib.php (Hostinger layout), else ~/public_html/agent (cPanel).
declare(strict_types=1);

if (!defined('CO_AGENT_WEB')) {
    (static function (): void {
        $home = dirname(__DIR__, 2);
        $dir = (string)getenv('CO_AGENT_WEB');
        if ($dir === '' && is_file($home . '/co-agent/config.php')) {
            $cfg = require $home . '/co-agent/config.php';
            $dir = is_array($cfg) ? (string)($cfg['web_dir'] ?? '') : '';
        }
        if ($dir === '') {
            $found = glob($home . '/domains/*/public_html/agent/db-lib.php') ?: [];
            if (!$found && is_file($home . '/public_html/agent/db-lib.php')) $found = [$home . '/public_html/agent/db-lib.php'];
            if (count($found) !== 1) {
                fwrite(STDERR, "boot.php: set web_dir in ~/co-agent/config.php (found " . count($found) . " agent dirs)\n");
                exit(2);
            }
            $dir = dirname($found[0]);
        }
        define('CO_AGENT_WEB', rtrim($dir, '/'));
    })();
}
