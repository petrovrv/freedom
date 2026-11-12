<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// members.php (Freedom F1, schema 21): list and change the members of this workbench on the server.
//   php ~/co-agent/db/members.php list
//   php ~/co-agent/db/members.php set <env> <email> <name> [--owner|--member] [--site-agent] [--color=#hex] [--sort=N]
//   php ~/co-agent/db/members.php delete <env>
// The installer adds the owner first; CO Web reads names and colors from the same table.
declare(strict_types=1);
require_once __DIR__ . '/../cron/boot.php';
require_once CO_AGENT_WEB . '/members-lib.php';

$args = array_slice($argv, 1);
$flags = [];
$pos = [];
foreach ($args as $a) {
    if (str_starts_with($a, '--')) {
        [$k, $v] = array_pad(explode('=', substr($a, 2), 2), 2, true);
        $flags[$k] = $v;
    } else {
        $pos[] = $a;
    }
}
$cmd = $pos[0] ?? 'list';
try {
    if ($cmd === 'set' && count($pos) === 4) {
        co_member_put(co_db(), ['env' => $pos[1], 'email' => $pos[2], 'name' => $pos[3],
            'role' => isset($flags['owner']) ? 'owner' : (isset($flags['member']) ? 'member' : ''), 'siteAgent' => isset($flags['site-agent']),
            'color' => is_string($flags['color'] ?? null) ? $flags['color'] : '', 'sort' => (int)($flags['sort'] ?? 0)]);
    } elseif ($cmd === 'delete' && count($pos) === 2) {
        if (!co_member_delete(co_db(), $pos[1])) throw new InvalidArgumentException("no member {$pos[1]}");
    } elseif ($cmd !== 'list') {
        fwrite(STDERR, "usage: members.php list | set <env> <email> <name> [--owner|--member] [--site-agent] [--color=#hex] [--sort=N] | delete <env>\n");
        exit(1);
    }
    echo json_encode(co_members(true), JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES), "\n";
} catch (Throwable $e) {
    fwrite(STDERR, $e->getMessage() . "\n");
    exit(1);
}
