<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// models-lib.php (schema 23, S5) against a throwaway MariaDB (never production): built-in resources from owner.json,
// overrides, registered subscriptions, visibility (own / slave allowed / confidential), subtask_spawn and subtask_poll.
// Driver: python tests/actions-test.py --test models-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/models-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$throws = static function (callable $f): bool {
    try { $f(); return false; } catch (InvalidArgumentException) { return true; }
};
$q = static function (string $sql, array $args = []) use ($db): void {
    $st = $db->prepare($sql);
    $st->execute($args);
    $st->close();
};

foreach (['kv', 'model_resources', 'jobs', 'chats', 'projects', 'events'] as $t) $db->query("DELETE FROM $t");
$now = gmdate('Y-m-d\TH:i:s\Z');
$home = ['hostId' => 'home-main', 'updatedUtc' => $now, 'state' => 'running', 'agents' => [
    'claude' => ['displayName' => 'Claude', 'models' => [['id' => 'claude-opus-5-5', 'label' => 'Opus 5.5'], ['id' => 'sonnet']], 'defaultModel' => null],
    'codex' => ['displayName' => 'GPT', 'models' => [['id' => 'gpt-6-sol']]],
    'dispatcher' => ['displayName' => 'Dispatcher'],
]];
$site = ['hostId' => 'site', 'updatedUtc' => gmdate('Y-m-d\TH:i:s\Z', time() - 3600), 'agents' => ['claude' => ['displayName' => 'Claude']]];
$q("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES ('host:home-main', 'owner', ?, 'x', UTC_TIMESTAMP(3))", [json_encode(['scope' => 'host:home-main', 'name' => 'owner', 'data' => $home])]);
$q("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES ('host:site', 'owner', ?, 'x', UTC_TIMESTAMP(3))", [json_encode($site)]);

$all = models_all($db);
$ok('built-in per host agent', isset($all['home-main/claude'], $all['home-main/codex'], $all['home-main/dispatcher'], $all['site/claude']));
$ok('owner env owns built-ins', $all['home-main/claude']['env'] === 'owner');
$ok('kinds', $all['home-main/claude']['kind'] === 'subscription' && $all['site/claude']['kind'] === 'site' && $all['home-main/dispatcher']['kind'] === 'corporate');
$ok('corporate is confidential', $all['home-main/dispatcher']['confidential'] === true && $all['home-main/claude']['confidential'] === false);
$ok('models from owner.json', array_column($all['home-main/claude']['models'], 'id') === ['claude-opus-5-5', 'sonnet']);
$ok('online from updatedUtc', $all['home-main/claude']['online'] === true && $all['site/claude']['online'] === false);
$ok('default card', str_contains($all['home-main/codex']['card']['strengths'], 'Excel'));

$own = array_column(models_list($db, 'owner')['resources'], null, 'id');
$ok('owner sees own four', count($own) === 4 && $own['home-main/claude']['own'] && $own['home-main/claude']['callable']);
$ok('sam sees nothing yet', models_list($db, 'sam')['resources'] === []);
$ok('admin view lists all', count(models_list($db, 'sam', true)['resources']) === 4);

// owner allows Claude at home as a slave and edits its card
$r = models_put($db, ['id' => 'home-main/claude', 'slaveAllowed' => true, 'card' => ['strengths' => 'everything']], 'user', 'owner', false);
$ok('set flag', $r['ok'] && $r['resource']['slaveAllowed'] === true && $r['resource']['card']['strengths'] === 'everything');
$ok('card keeps other fields', str_contains($r['resource']['card']['summary'], 'Claude Code'));
$ok('title stays built-in', $r['resource']['title'] === 'Claude · Home');
$r = models_put($db, ['id' => 'home-main/claude', 'title' => 'Claude of Oleg'], 'user', 'owner', false);
$ok('title change keeps flag and card', $r['resource']['slaveAllowed'] === true && $r['resource']['card']['strengths'] === 'everything' && $r['resource']['title'] === 'Claude of Oleg');
$ok('other env cannot change it', models_put($db, ['id' => 'home-main/claude', 'slaveAllowed' => false], 'user', 'sam', false)['error'] === 'not_owner');
$sam = array_column(models_list($db, 'sam')['resources'], null, 'id');
$ok('sam sees the allowed one only', array_keys($sam) === ['home-main/claude'] && !$sam['home-main/claude']['own'] && $sam['home-main/claude']['callable']);
$r = models_put($db, ['id' => 'home-main/dispatcher', 'slaveAllowed' => true], 'user', 'owner', false);
$ok('confidential stays hidden even when allowed', !isset(array_column(models_list($db, 'sam')['resources'], null, 'id')['home-main/dispatcher']));

// a device registers sam's own subscription (S4)
$r = models_put($db, ['id' => 'sam/claude-mac', 'env' => 'sam', 'kind' => 'subscription', 'title' => 'Claude of Sam', 'host' => 'mac-sam',
    'agent' => 'claude', 'models' => ['sonnet'], 'card' => ['summary' => 'Pro plan']], 'device', null, true);
$ok('device registers', $r['ok'] && $r['resource']['source'] === 'device' && $r['resource']['online'] === null);
$ok('owner does not see it until allowed', !isset(array_column(models_list($db, 'owner')['resources'], null, 'id')['sam/claude-mac']));
models_put($db, ['id' => 'sam/claude-mac', 'slaveAllowed' => true], 'user', 'sam', false);
$o = array_column(models_list($db, 'owner')['resources'], null, 'id');
$ok('owner sees it once sam allows', isset($o['sam/claude-mac']) && $o['sam/claude-mac']['slaveAllowed'] && $o['sam/claude-mac']['models'][0]['id'] === 'sonnet');
$ok('re-register keeps the flag', models_put($db, ['id' => 'sam/claude-mac', 'env' => 'sam', 'agent' => 'claude', 'title' => 'Claude of Sam'], 'device', null, true)['resource']['slaveAllowed'] === true);
$ok('user creates in own env only', models_put($db, ['id' => 'alex/x', 'env' => 'owner', 'agent' => 'engie', 'kind' => 'corporate', 'title' => 'Corp'], 'user', 'alex', false)['resource']['env'] === 'alex');
$ok('corporate defaults confidential', models_all($db)['alex/x']['confidential'] === true);
$ok('bad id refused', $throws(fn() => models_put($db, ['id' => 'A B', 'title' => 'x', 'agent' => 'claude'], 'user', 'owner', false)));
$ok('bad agent refused', $throws(fn() => models_put($db, ['id' => 'x/y', 'title' => 'x', 'agent' => 'rm'], 'user', 'owner', false)));
$ok('new without title refused', $throws(fn() => models_put($db, ['id' => 'x/z', 'agent' => 'claude'], 'user', 'owner', false)));
$ok('bad card refused', $throws(fn() => models_put($db, ['id' => 'home-main/claude', 'card' => ['a', 'b']], 'user', 'owner', false)));

// review fixes: no hijack of built-ins, no foreign hosts, corporate always confidential
$ok('member cannot name a resource outside own prefix', $throws(fn() => models_put($db, ['id' => 'home-main/engie', 'title' => 'x', 'agent' => 'engie', 'slaveAllowed' => true], 'user', 'sam', false)));
$ok('member cannot point at a host', $throws(fn() => models_put($db, ['id' => 'sam/claude', 'title' => 'x', 'agent' => 'claude', 'host' => 'home-main'], 'user', 'sam', false)));
$ok('device id needs the env prefix', $throws(fn() => models_put($db, ['id' => 'other/x', 'env' => 'sam', 'agent' => 'claude', 'title' => 'x'], 'device', null, true)));
$q("INSERT INTO model_resources (id, env, kind, title, host_id, agent, models_json, card_json, slave_allowed, confidential, source, updated_utc) VALUES ('home-main/codex', 'sam', 'subscription', 'Hijack', 'home-main', 'codex', '[]', '{}', 1, 0, 'user', UTC_TIMESTAMP(3))");
$ok('foreign row does not override a built-in', models_all($db)['home-main/codex']['slaveAllowed'] === false && models_all($db)['home-main/codex']['title'] === 'GPT · Home');
$db->query("DELETE FROM model_resources WHERE id = 'home-main/codex'");
$ok('corporate agent forced confidential', models_put($db, ['id' => 'home-main/dispatcher', 'confidential' => false], 'user', 'owner', false)['resource']['confidential'] === true);
$ok('string false is not true', models_put($db, ['id' => 'sam/claude-mac', 'slaveAllowed' => 'false'], 'user', 'sam', false)['resource']['slaveAllowed'] === false);
models_put($db, ['id' => 'sam/claude-mac', 'slaveAllowed' => true], 'user', 'sam', false);
$ok('bad defaultModel refused', $throws(fn() => models_put($db, ['id' => 'sam/claude-mac', 'defaultModel' => 'a b'], 'device', null, true)));

// subtasks
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc, folder_id) VALUES ('P1', 'sam', 'Hers', '{}', 'x', UTC_TIMESTAMP(), 'FOLDERP1xxxx'), ('P2', 'owner', 'Mine', '{}', 'x', UTC_TIMESTAMP(), 'FOLDERP2xxxx')");
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES ('CHAT-20261111000000-AAAA', 'P1', 'Lead chat', '{}', 'x', UTC_TIMESTAMP()), ('CHAT-20261111000000-BBBB', 'P2', 'Owner chat', '{}', 'x', UTC_TIMESTAMP())");
$made = [];
$events = [];
$mk = static function (string $folder, string $chatId, array $doc) use (&$made): string {
    $made[] = [$folder, $chatId, $doc];
    return 'FOLDER' . substr($chatId, -4) . 'xxxxxx';
};
$ap = static function (array $ev) use (&$events): array {
    $events[] = $ev;
    return ['sequence' => 1];
};
$sp = static fn(array $x) => models_subtask_spawn($db, $x + ['parentChatId' => 'CHAT-20261111000000-AAAA', 'prompt' => 'Summarise the file'], $mk, $ap);

$r = $sp(['resourceId' => 'home-main/claude', 'title' => 'Summary']);
$ok('sam spawns on the allowed slave', $r['ok'] === true && $r['host'] === 'home-main' && $r['agent'] === 'claude' && preg_match('/^JOB-\d{14}-[A-Z0-9]{6}$/', $r['jobId']) === 1);
$ok('chat folder under the project', $made[0][0] === 'FOLDERP1xxxx' && $made[0][2]['parent']['kind'] === 'subtask' && $made[0][2]['title'] === '⤷ Summary');
$ok('user_message appended', $events[0]['chatId'] === $r['chatId'] && $events[0]['author'] === 'subtask' && $events[0]['text'] === 'Summarise the file');
$st = $db->prepare('SELECT host_id, env, agent, state, request, actor_env FROM jobs WHERE job_id = ?');
$st->execute([$r['jobId']]);
$job = $st->get_result()->fetch_assoc();
$st->close();
$req = json_decode($job['request'], true);
$ok('job queued in the leader env', $job['state'] === 'inbox' && $job['env'] === 'sam' && $job['host_id'] === 'home-main' && $job['actor_env'] === 'sam');
$ok('request marks the slave', $req['slave']['resourceId'] === 'home-main/claude' && $req['slave']['resourceEnv'] === 'owner' && str_contains($req['instruction'], 'as a slave of environment sam'));
$st = $db->prepare('SELECT folder_id FROM chats WHERE chat_id = ?');
$st->execute([$r['chatId']]);
$ok('chat row with folder', ($st->get_result()->fetch_row()[0] ?? '') === 'FOLDER' . substr($r['chatId'], -4) . 'xxxxxx');
$st->close();

$ok('not allowed resource refused', $sp(['resourceId' => 'home-main/codex'])['error'] === 'slave_not_allowed');
$ok('confidential refused', $sp(['resourceId' => 'home-main/dispatcher'])['error'] === 'confidential');
$ok('unknown resource', $sp(['resourceId' => 'nope/x'])['error'] === 'unknown_resource');
$ok('confidential of another env refused', models_subtask_spawn($db, ['parentChatId' => 'CHAT-20261111000000-AAAA', 'prompt' => 'x', 'resourceId' => 'alex/x'], $mk, $ap)['error'] === 'confidential');
$ok('missing parent', models_subtask_spawn($db, ['parentChatId' => 'CHAT-20261111000000-ZZZZ', 'prompt' => 'x', 'resourceId' => 'home-main/claude'], $mk, $ap)['error'] === 'parent_not_found');
$ok('empty prompt refused', $throws(fn() => $sp(['resourceId' => 'home-main/claude', 'prompt' => ' '])));
$ok('bad mode refused', $throws(fn() => $sp(['resourceId' => 'home-main/claude', 'mode' => 'x'])));
$ok('bad model refused', $throws(fn() => $sp(['resourceId' => 'home-main/claude', 'model' => 'a b'])));
$own = static fn(array $x) => models_subtask_spawn($db, $x + ['parentChatId' => 'CHAT-20261111000000-BBBB', 'prompt' => 'Check'], $mk, $ap);
$ok('owner uses own codex without the flag', $own(['resourceId' => 'home-main/codex', 'model' => 'gpt-6-sol'])['ok'] === true);
$ok('dispatcher needs a device', $own(['resourceId' => 'home-main/dispatcher'])['error'] === 'needs_device');
$r2 = $own(['resourceId' => 'home-main/dispatcher', 'devices' => ['d-b903ab04eea11ba2']]);
$ok('dispatcher with a device, default model', $r2['ok'] === true && $r2['model'] === 'claude-opus-5.5');
$ok('resource on a host without a queue refused', models_subtask_spawn($db, ['parentChatId' => 'CHAT-20261111000000-AAAA', 'prompt' => 'x',
    'resourceId' => 'sam/claude-mac'], $mk, $ap)['error'] === 'host_unknown');

$ok('env of a chat', models_env_of($db, ['chatId' => 'CHAT-20261111000000-AAAA']) === 'sam' && models_env_of($db, ['env' => 'owner']) === 'owner');
$q("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('project', 'P2', 'sam', 'editor', UTC_TIMESTAMP())");
$ok('shared chat: no devices', $own(['resourceId' => 'home-main/dispatcher', 'devices' => ['d-b903ab04eea11ba2']])['error'] === 'shared_chat_no_corporate');
$ok('shared chat: plain resource still fine', $own(['resourceId' => 'home-main/claude'])['ok'] === true);
$db->query('DELETE FROM shares');

// R2 (review S5-01): A (only the chat shared) -> B (plain subtask) -> C (corporate/device) is refused at every depth
$q("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('chat', 'CHAT-20261111000000-BBBB', 'sam', 'editor', UTC_TIMESTAMP())");
$jobReq = static function (string $jobId) use ($db): array {
    $st = $db->prepare('SELECT request FROM jobs WHERE job_id = ?');
    $st->execute([$jobId]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    return json_decode((string)($row[0] ?? '{}'), true);
};
$b = $own(['resourceId' => 'home-main/claude']);
$ok('A shared: B spawns, marked restricted', $b['ok'] === true && ($jobReq($b['jobId'])['restricted'] ?? false) === true);
$fromB = static fn(array $x) => models_subtask_spawn($db, $x + ['parentChatId' => $b['chatId'], 'prompt' => 'Deeper'], $mk, $ap);
$ok('B -> C with a device refused', $fromB(['resourceId' => 'home-main/claude', 'devices' => ['d-b903ab04eea11ba2']])['error'] === 'shared_chat_no_corporate');
$ok('B -> C corporate refused', $fromB(['resourceId' => 'home-main/dispatcher', 'devices' => ['d-b903ab04eea11ba2']])['error'] === 'shared_chat_no_corporate');
$c = $fromB(['resourceId' => 'home-main/claude']);
$ok('B -> C plain, restricted too', $c['ok'] === true && ($jobReq($c['jobId'])['restricted'] ?? false) === true);
$ok('C -> D with a device refused', models_subtask_spawn($db, ['parentChatId' => $c['chatId'], 'prompt' => 'x', 'resourceId' => 'home-main/claude',
    'devices' => ['d-b903ab04eea11ba2']], $mk, $ap)['error'] === 'shared_chat_no_corporate');
$db->query('DELETE FROM shares');
// the job row keeps the restriction even after the share is gone from the chain's view (immutable, server written)
$ok('B stays restricted by its job', models_subtask_spawn($db, ['parentChatId' => $b['chatId'], 'prompt' => 'x', 'resourceId' => 'home-main/dispatcher',
    'devices' => ['d-b903ab04eea11ba2']], $mk, $ap)['error'] === 'shared_chat_no_corporate');
$ok('unshared owner chat: corporate fine again', $own(['resourceId' => 'home-main/dispatcher', 'devices' => ['d-b903ab04eea11ba2']])['ok'] === true);

// R2 (review S5-02): the same requestKey gives one subtask, also after a lost answer
$jobsOf = static function () use ($db): int {
    return (int)$db->query('SELECT COUNT(*) FROM jobs')->fetch_row()[0];
};
$key = 'k-' . str_repeat('a', 20);
$n0 = $jobsOf();
$k1 = $own(['resourceId' => 'home-main/claude', 'requestKey' => $key]);
$k2 = $own(['resourceId' => 'home-main/claude', 'requestKey' => $key]);
$ok('repeat with the key: same chat and job', $k1['ok'] === true && $k2['jobId'] === $k1['jobId'] && $k2['chatId'] === $k1['chatId'] && ($k2['repeated'] ?? false) === true);
$ok('repeat with the key: one job queued', $jobsOf() === $n0 + 1);
// answer lost after INSERT job: the reservation still says pending with that job
$q("UPDATE kv SET data = ? WHERE scope = ? AND name = ?", [json_encode(['state' => 'pending', 'chatId' => $k1['chatId'], 'jobId' => $k1['jobId']]),
    'subtask-key:CHAT-20261111000000-BBBB', $key]);
$k3 = $own(['resourceId' => 'home-main/claude', 'requestKey' => $key]);
$ok('lost answer after the job was queued: same job rebuilt', $k3['ok'] === true && $k3['jobId'] === $k1['jobId'] && $k3['chatId'] === $k1['chatId'] && $jobsOf() === $n0 + 1);
// another request with the key is still creating: in_progress, nothing created
$key2 = 'k-' . str_repeat('b', 20);
$q("INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, '', UTC_TIMESTAMP(3))",
    ['subtask-key:CHAT-20261111000000-BBBB', $key2, json_encode(['state' => 'pending', 'jobId' => 'JOB-20261111000000-NOPE00'])]);
$ok('key being created: in_progress', $own(['resourceId' => 'home-main/claude', 'requestKey' => $key2])['error'] === 'in_progress' && $jobsOf() === $n0 + 1);
// that request died before its job: after 2 min the key is taken over, one job
$q("UPDATE kv SET updated_utc = UTC_TIMESTAMP(3) - INTERVAL 5 MINUTE WHERE name = ?", [$key2]);
$k4 = $own(['resourceId' => 'home-main/claude', 'requestKey' => $key2]);
$ok('dead reservation taken over', $k4['ok'] === true && $jobsOf() === $n0 + 2);
$ok('and then repeats answer it', $own(['resourceId' => 'home-main/claude', 'requestKey' => $key2])['jobId'] === $k4['jobId'] && $jobsOf() === $n0 + 2);
// a failure before the job frees the key
$key3 = 'k-' . str_repeat('c', 20);
$boom = static function (): string { throw new RuntimeException('drive down'); };
try {
    models_subtask_spawn($db, ['parentChatId' => 'CHAT-20261111000000-BBBB', 'prompt' => 'x', 'resourceId' => 'home-main/claude', 'requestKey' => $key3], $boom, $ap);
    $threw = false;
} catch (RuntimeException) {
    $threw = true;
}
$k5 = $own(['resourceId' => 'home-main/claude', 'requestKey' => $key3]);
$ok('failure before the job frees the key', $threw && $k5['ok'] === true && !isset($k5['repeated']));
// a creator stalled past the takeover time is fenced off: the repeat took the key, the stalled one queues nothing
$key4 = 'k-' . str_repeat('d', 20);
$stall = static function (string $folder, string $chatId, array $doc) use ($db, $key4): string {
    $st = $db->prepare("UPDATE kv SET data = ? WHERE scope = 'subtask-key:CHAT-20261111000000-BBBB' AND name = ?");
    $st->execute([json_encode(['state' => 'pending', 'jobId' => 'JOB-20261111000000-OTHER0']), $key4]);
    $st->close();
    return 'FOLDERstallxxxxxx';
};
$n1 = $jobsOf();
$k6 = models_subtask_spawn($db, ['parentChatId' => 'CHAT-20261111000000-BBBB', 'prompt' => 'x', 'resourceId' => 'home-main/claude', 'requestKey' => $key4], $stall, $ap);
$ok('stalled creator that lost its key queues no job', $k6['error'] === 'in_progress' && $jobsOf() === $n1);
$ok('bad requestKey refused', $throws(fn() => $own(['resourceId' => 'home-main/claude', 'requestKey' => 'short'])));
$ok('without a key every call is new', $own(['resourceId' => 'home-main/claude'])['jobId'] !== $own(['resourceId' => 'home-main/claude'])['jobId']);

$p = models_subtask_poll($db, ['jobIds' => [$r['jobId'], 'JOB-20261111000000-ZZZZZZ']]);
$ok('poll inbox and missing', $p['jobs'][0]['state'] === 'inbox' && $p['jobs'][1]['state'] === 'missing');
$db->query("UPDATE jobs SET state = 'completed', status = 'completed', result = '{\"agentFinalResponse\":\"Done: 3 points\"}' WHERE job_id = '" . $r['jobId'] . "'");
$p = models_subtask_poll($db, ['jobIds' => [$r['jobId']]]);
$ok('poll answer', $p['jobs'][0]['status'] === 'completed' && $p['jobs'][0]['answer'] === 'Done: 3 points');
$ok('poll bad id refused', $throws(fn() => models_subtask_poll($db, ['jobIds' => ['x']])));

$ok('delete override returns to defaults', models_delete($db, ['id' => 'home-main/claude'], 'owner', false)['deleted'] === 1
    && models_all($db)['home-main/claude']['slaveAllowed'] === false);
$ok('delete of others refused', models_delete($db, ['id' => 'alex/x'], 'sam', false)['error'] === 'not_owner');
$ok('admin deletes any', models_delete($db, ['id' => 'alex/x'], 'owner', true)['deleted'] === 1);

// Freedom F1 5b: corporate agents, their cards and flags come from the connector manifest, not from the code
$ok('manifest loaded', co_corporate_agent('dispatcher') !== null && in_array('dispatcher', models_job_agents(), true));
$ok('card from the manifest', ($all['home-main/dispatcher']['card']['dataStaysIn'] ?? '') === 'corporate');
$ok('corporate blocked reads both names', co_corporate_blocked(['engieBlocked' => 'signin']) === 'signin'
    && co_corporate_blocked(['corporateBlocked' => 'off', 'engieBlocked' => 'signin']) === 'off');
co_corporate(['id' => 'acme', 'title' => 'Acme']); // an install without the employer's agents
$b = models_builtin(['home-main' => $home + ['agents' => $home['agents'] + ['x' => []]]], 'owner');
$ok('no manifest agents: the corporate agent is unknown', !isset($b['home-main/dispatcher']) && !in_array('dispatcher', models_job_agents(), true));
$h2 = $home;
$h2['agents']['gemini'] = ['displayName' => 'Gemini', 'corporate' => true];
$b = models_builtin(['home-main' => $h2], 'owner');
$ok('owner.json flag corporate = confidential', $b['home-main/gemini']['confidential'] === true && $b['home-main/gemini']['kind'] === 'corporate');
// an older override row (slave allowed, not confidential) cannot open a resource its host now calls corporate
$q("UPDATE kv SET data = ? WHERE scope = 'host:home-main' AND name = 'owner'", [json_encode(['scope' => 'host:home-main', 'name' => 'owner', 'data' => $h2])]);
$q("INSERT INTO model_resources (id, env, kind, title, host_id, agent, models_json, card_json, slave_allowed, confidential, source, updated_utc) VALUES ('home-main/gemini', 'owner', 'subscription', '', 'home-main', 'gemini', '[]', '{}', 1, 0, 'auto', UTC_TIMESTAMP(3)) ON DUPLICATE KEY UPDATE confidential = 0, slave_allowed = 1");
$g = models_all($db)['home-main/gemini'] ?? null;
$ok('stored row keeps a corporate entry confidential', $g !== null && $g['confidential'] === true);

echo $fail === 0 ? "ALL OK\n" : "$fail FAILED\n";
exit($fail === 0 ? 0 : 1);
