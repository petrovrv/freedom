<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// apps-lib.php (Freedom F14b-1, SPEC §3.6a item 5): actions of apps for an agent job against a throwaway MariaDB (never
// production) with the module smart-home: the job decides the env (owner, member, nobody), finished / unknown /
// restricted jobs get nothing, outward actions are refused, arguments are checked against the schema, the handler sees
// the person's rights and actor=agent, and the env's open app pages get a refresh event.
// Driver: python tests/actions-test.py --test apps-test.php (seeds owner, sam, alex)
declare(strict_types=1);
require __DIR__ . '/../web/agent/apps-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f, string $like = ''): bool {
    try { $f(); return false; } catch (InvalidArgumentException $e) { return $like === '' || str_contains($e->getMessage(), $like); }
};
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
$job = static function (string $id, string $env, string $state = 'claimed', array $req = [], ?string $result = null) use ($db): void {
    $st = $db->prepare("INSERT INTO jobs (job_id, env, project_id, chat_id, state, request, result, sha, synced_utc)
                        VALUES (?, ?, 'PRJ-T', ?, ?, ?, ?, '', UTC_TIMESTAMP(3))");
    $chat = 'CHAT-' . $id;
    $r = json_encode((object)$req);
    $st->bind_param('ssssss', $id, $env, $chat, $state, $r, $result);
    $st->execute();
    $st->close();
};

$src = realpath(__DIR__ . '/../modules/smart-home');
$p = $upload($src, 'smart-home');
co_module_install($db, $p + ['standard' => true, 'publisher' => 'ed25519:test']);
co_module_config_put('smart-home', $p['manifest'], ['members' => 'sam']);
$job('J-OWNER', 'owner');
$job('J-SAM', 'sam');
$job('J-ALEX', 'alex');
$job('J-DONE', 'owner', 'completed', [], '{}');
$job('J-RESTR', 'owner', 'claimed', ['restricted' => true]);
$job('J-RO', 'owner', 'claimed', ['permissions' => ['files' => 'read']]);

$ok('module off: no apps', co_app_list($db, ['jobId' => 'J-OWNER'])['apps'] === []);
co_module_set($db, 'smart-home', true, 'owner');

$l = co_app_list($db, ['jobId' => 'J-OWNER']);
$home = $l['apps'][0] ?? [];
$ok('owner job: the Home app with its face', ($home['app'] ?? '') === 'smart-home' && ($home['icon'] ?? '') === '🏠' && $l['env'] === 'owner');
$acts = array_column($home['actions'] ?? [], null, 'op');
$ok('two actions, outward marked', ($acts['home_state']['outward'] ?? null) === false && ($acts['home_control']['outward'] ?? null) === true
    && isset($acts['home_control']['note']));
$ok('schema reduced and closed', ($acts['home_control']['args']['required'] ?? []) === ['entity', 'action', 'requestKey']
    && ($acts['home_control']['args']['additionalProperties'] ?? true) === false);
$ok('unknown job refused', $throws(fn() => co_app_list($db, ['jobId' => 'J-NOPE']), 'unknown job'));
$ok('missing job id refused', $throws(fn() => co_app_list($db, []), 'jobId'));
$ok('finished job refused', $throws(fn() => co_app_action($db, ['jobId' => 'J-DONE', 'app' => 'smart-home', 'action' => 'home_state']), 'finished'));
$ok('restricted job: no apps', co_app_list($db, ['jobId' => 'J-RESTR'])['restricted'] === true
    && co_app_action($db, ['jobId' => 'J-RESTR', 'app' => 'smart-home', 'action' => 'home_state'])['error'] === 'not_allowed');

$ok('read-only chat: list yes, action no', count(co_app_list($db, ['jobId' => 'J-RO'])['apps']) === 1
    && co_app_action($db, ['jobId' => 'J-RO', 'app' => 'smart-home', 'action' => 'home_state'])['error'] === 'read_only');
$ev0 = co_app_events($db, 'owner', -1);
$r = co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('owner reads the house', $r['ok'] === true && ($r['result']['online'] ?? null) === false && is_array($r['result']['entities'] ?? null));
$ev = co_app_events($db, 'owner', $ev0['seq']);
$ok('refresh event for the owner\'s pages', $ev['seq'] === $ev0['seq'] + 1 && ($ev['events'][count($ev['events']) - 1]['app'] ?? '') === 'smart-home');
$ok('since -1 gives only the cursor', co_app_events($db, 'owner', -1)['events'] === [] && co_app_events($db, 'owner', 0)['events'] !== []);
$ok('no event for another env', co_app_events($db, 'sam', -1)['seq'] === 0);

$r = co_app_action($db, ['jobId' => 'J-SAM', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('member listed in the module config reads it too', $r['ok'] === true);
$r = co_app_action($db, ['jobId' => 'J-ALEX', 'app' => 'smart-home', 'action' => 'home_state']);
$ok('member without access gets the module\'s own refusal', $r['ok'] === false && ($r['result']['error'] ?? '') === 'home_forbidden');
$ok('a refused action refreshes nothing', co_app_events($db, 'alex', -1)['seq'] === 0);

$r = co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_control',
    'args' => ['entity' => 'light.x', 'action' => 'turn_on', 'requestKey' => str_repeat('a', 20)]]);
$ok('outward action refused for the agent', $r['ok'] === false && $r['error'] === 'outward');
$ok('an op that is not an action is unknown', co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_pair_start'])['error'] === 'unknown_action');
$ok('another app is unknown', co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'nope', 'action' => 'home_state'])['error'] === 'unknown_action');
$ok('extra argument refused', $throws(fn() => co_app_action($db, ['jobId' => 'J-OWNER', 'app' => 'smart-home', 'action' => 'home_state', 'args' => ['x' => 1]]), 'args.x'));

// the argument checker on its own
$s = co_app_schema(['properties' => ['n' => ['type' => 'integer'], 'k' => ['type' => 'string', 'enum' => ['a', 'b']], 'bad' => ['type' => 'array']], 'required' => ['n', 'bad']]);
$ok('unsupported types dropped from the schema', !isset(((array)$s['properties'])['bad']) && $s['required'] === ['n']);
$ok('required checked', $throws(fn() => co_app_args($s, []), 'args.n'));
$ok('type checked', $throws(fn() => co_app_args($s, ['n' => '1']), 'integer'));
$ok('enum checked', $throws(fn() => co_app_args($s, ['n' => 1, 'k' => 'c']), 'one of'));
$ok('a list is not an object', $throws(fn() => co_app_args($s, [1]), 'object'));
$ok('good args pass', co_app_args($s, ['n' => 2, 'k' => 'b']) === ['n' => 2, 'k' => 'b']);

// a manifest action whose op is not a user op of the module is ignored by the server
$bad = $p['manifest'];
$bad['provides']['actions'][] = ['op' => 'home_exchange', 'description' => 'host op', 'outward' => false];
$bad['provides']['actions'][] = ['op' => 'home_pair_state', 'description' => 'no outward flag'];
$ok('actions outside server.ops.user or without outward are dropped', array_keys(co_app_actions(['manifest' => $bad])) === ['home_state', 'home_control']);

co_module_remove($db, ['id' => 'smart-home', 'data' => 'erase']);
echo $fail ? "$fail FAILED\n" : "apps-test OK\n";
exit($fail ? 1 : 0);
