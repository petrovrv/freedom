<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10: gateway-lib.php (the core's generic model gateway, described by installed modules) and the server part of
// the module corporate (modules/corporate/server: user ops corporate_waiting / corporate_resume) against a throwaway
// MariaDB (never production). Installed + disabled = the old paused connector: agents known, no ops; enabled: ops
// dispatched and named in the description; chats stopped by gatewayBlocked or the legacy wire corporateBlocked /
// engieBlocked are found. Driver: python tests/actions-test.py --test gateway-test.php
declare(strict_types=1);
require_once __DIR__ . '/../web/agent/db-lib.php';
require_once __DIR__ . '/../web/agent/share-lib.php';
require_once __DIR__ . '/../web/agent/finalize-lib.php';
require_once __DIR__ . '/../web/agent/modules-lib.php';
require_once __DIR__ . '/../web/agent/gateway-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static function (string $sql, array $p = []) use ($db): void {
    $st = $db->prepare($sql);
    $st->execute($p);
    $st->close();
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (InvalidArgumentException) { return true; }
};
// what install/freedom_modules.py does before op module_install: upload the files, hash them
$upload = static function (string $src, string $id): array {
    $dst = co_module_home($id);
    co_module_rmtree($dst);
    $files = [];
    $it = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS));
    foreach ($it as $f) {
        $rel = str_replace('\\', '/', substr($f->getPathname(), strlen($src) + 1));
        @mkdir(dirname("$dst/$rel"), 0700, true);
        copy($f->getPathname(), "$dst/$rel");
        if ($rel !== 'module.json' && $rel !== 'README.md') $files[$rel] = hash_file('sha256', "$dst/$rel");
    }
    return ['manifest' => json_decode((string)file_get_contents("$src/module.json"), true), 'files' => $files];
};

// ---- nothing installed: neutral ----
$ok('no module: no gateways', co_gateways(null, true) === [] && co_gateway_agents() === [] && co_gateway_tools() === []);
$ok('no module: neutral texts', co_gateway()['title'] === 'Employer' && co_gateway()['agents'] === [] && co_gateway()['ops'] === []);
$ok('no module: core error pattern only', preg_match(co_gateway_error_re(), 'sign_in_needed') === 1
    && preg_match(co_gateway_error_re(), 'gateway call failed: 502') === 1 && preg_match(co_gateway_error_re(), 'Acme call failed') === 0);

// ---- installed, disabled (the owner's state): the old paused manifest ----
$src = realpath(__DIR__ . '/../modules/corporate');
$r = co_module_install($db, $upload($src, 'corporate') + ['standard' => true, 'publisher' => 'ed25519:test']);
$ok('install ok, stays off', $r['ok'] && !$r['enabled']);
$gws = co_gateways(null, true);
$g = $gws[0] ?? [];
$ok('one gateway, paused', count($gws) === 1 && $g['module'] === 'corporate' && $g['enabled'] === false && $g['paused'] === true);
$ok('agents known while paused', isset($g['agents']['dispatcher'], $g['agents']['engie'])
    && co_gateway_agent('dispatcher')['kind'] === 'gateway-dispatcher' && co_gateway_agent('dispatcher')['needsDevice'] === true
    && co_gateway_agent('dispatcher')['family'] === 'claude' && co_gateway_agent('engie')['kind'] === 'gateway-llm');
$ok('tools with kinds', co_gateway_tools('gateway-llm') === ['engie_chat'] && co_gateway_tools('gateway-fetch') === ['engie_fetch']
    && in_array('outlook_search', co_gateway_tools(''), true));
$ok('texts from the module', $g['title'] !== 'Employer' && $g['resumeText'] !== '' && $g['blockedMarkers'] !== [] && $g['domains'] !== []);
$ok('module error pattern', preg_match(co_gateway_error_re(), $g['title'] . ' call failed: 502') === 1);
$ok('disabled: no ops in the description', $g['ops'] === []);
$ok('disabled: op not dispatched', co_module_call($db, 'user', 'corporate_waiting', [], 'owner', true) === null);
$w = co_gateway_wire();
$ok('wire: agents as a list, ops as an object', array_is_list($w[0]['agents']) && $w[0]['agents'][0]['id'] !== ''
    && json_encode($w[0]['ops']) === '{}');

// ---- enabled ----
co_module_set($db, 'corporate', true, 'owner');
$g = co_gateways(null, true)[0];
$ok('enabled: ops named', $g['enabled'] === true && $g['paused'] === false
    && $g['ops'] === ['resume' => 'corporate_resume', 'waiting' => 'corporate_waiting']);

foreach (['events', 'jobs', 'chats', 'projects'] as $t) $db->query("DELETE FROM $t");
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP())");
$chats = ['CHAT-20261111000000-AAAA' => '{"engieBlocked":"signin"}', 'CHAT-20261111000000-BBBB' => '{"corporateBlocked":"offline"}',
    'CHAT-20261111000000-CCCC' => '{"status":"completed"}', 'CHAT-20261111000000-DDDD' => '{"corporateBlocked":"","engieBlocked":"off"}',
    'CHAT-20261111000000-EEEE' => '{"gatewayBlocked":"new","corporateBlocked":"new"}'];
$i = 0;
foreach ($chats as $cid => $data) {
    $i++;
    $q("INSERT INTO chats (chat_id, project_id, title, data, sha, updated_utc, synced_utc) VALUES (?, 'P1', ?, '{}', 'x', UTC_TIMESTAMP(3), UTC_TIMESTAMP())", [$cid, "c$i"]);
    $ev = json_encode(['eventId' => "EV-$i"] + json_decode($data, true));
    $q("INSERT INTO events (event_id, chat_id, seq, type, writer, created_utc, text, data, sha, synced_utc) VALUES
        (?, ?, 1, 'user_message', 'owner@example.org', UTC_TIMESTAMP(3) - INTERVAL 1 MINUTE, 'go', '{}', 'x', UTC_TIMESTAMP()),
        (?, ?, 2, 'assistant_message', NULL, UTC_TIMESTAMP(3), 'stopped', ?, 'x', UTC_TIMESTAMP())", ["U-$i", $cid, "EV-$i", $cid, $ev]);
}
$res = co_module_call($db, 'user', 'corporate_waiting', [], 'owner', true);
$w = array_column($res['waiting'] ?? [], 'reason', 'chatId');
$ok('legacy engieBlocked found', ($w['CHAT-20261111000000-AAAA'] ?? null) === 'signin');
$ok('legacy corporateBlocked found', ($w['CHAT-20261111000000-BBBB'] ?? null) === 'offline');
$ok('new gatewayBlocked found', ($w['CHAT-20261111000000-EEEE'] ?? null) === 'new');
$ok('empty newer name falls back to the older one', ($w['CHAT-20261111000000-DDDD'] ?? null) === 'off');
$ok('finished chat not listed', !isset($w['CHAT-20261111000000-CCCC']) && count($w) === 4);
$ok('member sees only the own env', (co_module_call($db, 'user', 'corporate_waiting', ['env' => 'sam'], 'sam', false)['waiting'] ?? null) === []);
$ok('member cannot list another env', $throws(fn() => co_module_call($db, 'user', 'corporate_waiting', ['env' => 'owner'], 'sam', false)));
$ok('last blocked', co_corp_last_blocked($db, 'CHAT-20261111000000-BBBB') !== null && co_corp_last_blocked($db, 'CHAT-20261111000000-CCCC') === null);
$ok('resume text from the module', co_corp_resume_text('corporate') === $g['resumeText'] && co_corp_resume_text('none') === CO_CORP_RESUME_TEXT);
$r = co_module_call($db, 'user', 'corporate_resume', ['chatIds' => ['CHAT-20261111000000-CCCC', 'bad id', 'CHAT-20261111000000-CCCC']], 'owner', true);
$ok('resume: a chat that is not waiting, a bad id, a duplicate skipped', count($r['results']) === 2
    && $r['results'][0]['error'] === 'not waiting for the corporate model' && $r['results'][1]['error'] === 'invalid chatId');
$r = co_module_call($db, 'user', 'corporate_resume', ['chatIds' => ['CHAT-20261111000000-BBBB']], 'sam', false);
$ok('resume: chat of another env forbidden', ($r['results'][0]['error'] ?? '') === 'forbidden');
$ok('resume: empty list refused', $throws(fn() => co_module_call($db, 'user', 'corporate_resume', ['chatIds' => []], 'owner', true)));

$ok('unsigned module: no ops advertised', co_gateway_describe(['id' => 'x', 'enabled' => true, 'standard' => false,
    'manifest' => json_decode((string)file_get_contents($src . '/module.json'), true)])['ops'] === []);
// ---- disabled again: ops gone, agents stay ----
co_module_set($db, 'corporate', false, 'owner');
$ok('disabled again: op gone, agents stay', co_module_for_op($db, 'user', 'corporate_resume') === null
    && co_gateways(null, true)[0]['ops'] === [] && co_gateway_agent('dispatcher') !== null);
// ---- removed: neutral ----
co_module_remove($db, ['id' => 'corporate', 'data' => 'erase']);
$ok('removed: no gateway', co_gateways(null, true) === [] && co_gateway_agent('dispatcher') === null);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
