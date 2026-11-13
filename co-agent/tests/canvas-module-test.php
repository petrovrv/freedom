<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// The module canvas (Freedom F10: canvas-lib.php and the canvas ops of db.php moved to modules/canvas) against a
// throwaway MariaDB (never production): install like install/freedom_modules.py with the install config, dispatch of
// its user and host ops, canvas_ticket role checks (owner, a viewer, a member without access, bad paths), the canvas
// side job (canvas_job / canvas_job_get), share put + draft without a Gmail session, and a disabled module.
// Driver: python tests/actions-test.py --test canvas-module-test.php (seeds owner, sam, alex)
declare(strict_types=1);
require __DIR__ . '/../web/agent/modules-lib.php';
require __DIR__ . '/../web/agent/share-lib.php';

// db_append_event lives in db.php (a request handler, not includable here): the events canvas_job appends are kept
$events = [];
function db_append_event(array $in): array
{
    $GLOBALS['events'][] = $in['event'];
    return ['ok' => true];
}

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static function (string $sql, array $p = []) use ($db): array {
    $st = $db->prepare($sql);
    $st->execute($p);
    $r = $st->get_result();
    $rows = $r ? $r->fetch_all(MYSQLI_ASSOC) : [];
    $st->close();
    return $rows;
};
// what install/freedom_modules.py does before op module_install: upload the files, hash them
$upload = static function (string $src, string $id): array {
    $dst = co_module_home($id);
    co_module_rmtree($dst);
    $files = [];
    $it = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS));
    foreach ($it as $f) {
        $rel = str_replace('\\', '/', substr($f->getPathname(), strlen($src) + 1));
        if (str_contains($rel, '__pycache__/') || basename($rel) === 'freedom-manifest.json') continue;
        @mkdir(dirname("$dst/$rel"), 0700, true);
        copy($f->getPathname(), "$dst/$rel");
        if ($rel !== 'module.json' && basename($rel) !== 'README.md') $files[$rel] = hash_file('sha256', "$dst/$rel");
    }
    return ['manifest' => json_decode((string)file_get_contents("$src/module.json"), true), 'files' => $files];
};
$b64d = static fn(string $s): string => (string)base64_decode(strtr($s, '-_', '+/') . str_repeat('=', (4 - strlen($s) % 4) % 4));

foreach (['events', 'jobs', 'chats', 'projects', 'shares'] as $t) $db->query("DELETE FROM $t");
$chat = 'CHAT-20261112000000-CANV';
$q("INSERT INTO projects (project_id, env, title, data, sha, synced_utc) VALUES ('P1', 'owner', 'Proj', '{}', 'x', UTC_TIMESTAMP())");
$q("INSERT INTO chats (chat_id, project_id, title, data, sha, synced_utc) VALUES (?, 'P1', 'Canvas chat', '{}', 'x', UTC_TIMESTAMP())", [$chat]);
$q("INSERT INTO shares (kind, target_id, env, role, created_utc) VALUES ('chat', ?, 'sam', 'viewer', UTC_TIMESTAMP(3))", [$chat]);
// the chat's last own turn: canvas jobs take its agent and mode
$q("INSERT INTO jobs (job_id, env, project_id, chat_id, agent, mode, state, request, sha, synced_utc, queued_utc)
    VALUES ('JOB-OWN', 'owner', 'P1', ?, 'codex', 'cowork', 'completed', ?, 'x', UTC_TIMESTAMP(), UTC_TIMESTAMP(3))",
    [$chat, json_encode(['agent' => 'codex', 'mode' => 'cowork', 'model' => 'gpt-x'])]);
@mkdir(co_agent_home() . '/co-agent/secrets', 0700, true);
$key = str_repeat('k', 40);
file_put_contents(co_agent_home() . '/co-agent/secrets/canvas-ticket-key', $key);
$doc = "projects/P1/chats/$chat/scratch/report.docx";

// ---------- install ----------
$src = realpath(__DIR__ . '/../modules/canvas');
$ok('canvas module folder found', $src !== false && is_file("$src/server/ops.php"));
$ok('no canvas op before install', co_module_for_op($db, 'user', 'canvas_ticket') === null && co_module_for_op($db, 'host', 'canvas_job') === null);
$p = $upload($src, 'canvas');
$r = co_module_install($db, $p + ['standard' => true, 'publisher' => 'ed25519:test',
    'config' => ['publicUrl' => 'http://127.0.0.1:9', 'notDeclared' => 'x']]);
$ok('install ok, stays off', ($r['ok'] ?? false) && !$r['enabled']);
$ok('install config stored (declared keys only)', co_module_config('canvas') === ['publicUrl' => 'http://127.0.0.1:9']);
$ok('disabled: ops gone', co_module_for_op($db, 'user', 'canvas_ticket') === null && co_module_for_op($db, 'host', 'canvas_share_put') === null);
$ok('disabled: side job key still known (old rows stay out of the inbox)', in_array('canvas', co_module_side_keys($db), true));

co_module_set($db, 'canvas', true, 'owner');
foreach (['canvas_ticket', 'canvas_share_draft'] as $op) {
    $ok("user op $op", (co_module_for_op($db, 'user', $op)['id'] ?? '') === 'canvas' && co_module_for_op($db, 'host', $op) === null);
}
foreach (['canvas_job', 'canvas_job_get', 'canvas_share_put'] as $op) {
    $ok("host op $op", (co_module_for_op($db, 'host', $op)['id'] ?? '') === 'canvas' && co_module_for_op($db, 'user', $op) === null);
}
$row = array_values(array_filter(co_modules_list($db, 'sam', false)['modules'], fn($x) => $x['id'] === 'canvas'))[0] ?? [];
$ok('pages keep the old addresses', array_column($row['ui'] ?? [], 'path') === ['/canvas', '/canvas-share']);
$ok('uiOps are the user ops', ($row['uiOps'] ?? []) === ['canvas_ticket', 'canvas_share_draft']);
$fa = $row['fileActions'] ?? [];
$ok('file actions: office and notebook', count($fa) === 2 && $fa[0]['exts'] === ['docx', 'xlsx', 'pptx'] && $fa[0]['title'] === ''
    && $fa[1]['exts'] === ['md', 'markdown', 'txt'] && $fa[1]['title'] !== '' && $fa[0]['open'] === ['page' => 'editor', 'param' => 'path', 'target' => 'blank']);
$sj = $row['sideJobs'][0] ?? [];
$ok('side job canvas', ($sj['key'] ?? '') === 'canvas' && $sj['list'] === 'threads' && $sj['item'] === 'reply' && $sj['count'] === 'ops');

// ---------- canvas_ticket ----------
$ticket = static fn(string $env, bool $owner, string $email, array $in) => co_module_call($db, 'user', 'canvas_ticket', $in, $env, $owner, $email);
$t = $ticket('owner', true, 'owner@example.org', ['path' => $doc]);
$parts = explode('.', (string)($t['ticket'] ?? ''));
$claims = count($parts) === 3 ? json_decode($b64d($parts[1]), true) : null;
$ok('owner gets a ticket', isset($t['ticket']) && $t['edit'] === true);
$ok('ticket signed with the key', count($parts) === 3
    && hash_equals(rtrim(strtr(base64_encode(hash_hmac('sha256', "$parts[0].$parts[1]", $key, true)), '+/', '-_'), '='), $parts[2]));
$ok('ticket claims', ($claims['path'] ?? '') === $doc && $claims['chatId'] === $chat && $claims['projectId'] === 'P1'
    && $claims['env'] === 'owner' && $claims['email'] === 'owner@example.org' && $claims['exp'] - $claims['iat'] === 120);
$ok('edit URL from the install config', ($t['editUrl'] ?? '') === 'http://127.0.0.1:9/cb/edit' && $t['online'] === false);
$t = $ticket('sam', false, 'sam@example.org', ['path' => $doc]);
$ok('viewer of the chat: read-only ticket', isset($t['ticket']) && $t['edit'] === false);
$ok('member without access: forbidden', $ticket('alex', false, 'alex@example.org', ['path' => $doc]) === ['error' => 'forbidden']);
$ok('project file needs a project role', $ticket('sam', false, 'sam@example.org', ['path' => 'projects/P1/notes/a.md']) === ['error' => 'forbidden']
    && isset($ticket('owner', true, 'owner@example.org', ['path' => 'projects/P1/notes/a.md'])['ticket']));
$ok('chat of another project: not found', $ticket('owner', true, '', ['path' => "projects/P2/chats/$chat/scratch/a.docx"]) === ['error' => 'not_found']);
$ok('bad path: error in the body', $ticket('owner', true, '', ['path' => 'projects/P1/../x.docx']) === ['error' => 'invalid path']);
$ok('not an office file', $ticket('owner', true, '', ['path' => 'projects/P1/a.exe']) === ['error' => 'not an office file']);
$ok('outside projects', $ticket('owner', true, '', ['path' => 'tools/a.docx']) === ['error' => 'path outside projects']);
$ok('no path', $ticket('owner', true, '', []) === ['error' => 'invalid path']);

// ---------- canvas_job / canvas_job_get (host) ----------
$host = static fn(string $op, array $in) => co_module_call($db, 'host', $op, $in);
$job = ['path' => $doc, 'env' => 'owner', 'summary' => 'Comments on report.docx', 'prompt' => 'Answer the threads.'];
$r = $host('canvas_job', $job);
$ok('canvas job queued', is_string($r['jobId'] ?? null) && $r['resume'] === false && $r['agent'] === 'codex');
$jr = $q('SELECT state, agent, mode, model, actor_env, request FROM jobs WHERE job_id = ?', [$r['jobId'] ?? ''])[0] ?? [];
$req = json_decode((string)($jr['request'] ?? ''), true) ?: [];
$ok('job row: chat agent and mode, canvas key', ($jr['state'] ?? '') === 'inbox' && $jr['mode'] === 'cowork' && $jr['model'] === 'gpt-x'
    && $jr['actor_env'] === 'owner' && ($req['canvas'] ?? null) === ['path' => $doc, 'file' => 'report.docx']);
$ok('new session gets the preamble', str_starts_with((string)($req['instruction'] ?? ''), '[CO canvas: you are the agent'));
$ok('event under the side job key', count($events) === 1 && ($events[0]['canvas']['file'] ?? '') === 'report.docx' && $events[0]['jobId'] === $r['jobId']);
$ok('second ask while one runs: busy', ($host('canvas_job', $job)['busy'] ?? '') === $r['jobId']);
$g = $host('canvas_job_get', ['jobId' => $r['jobId']]);
$ok('canvas_job_get state', ($g['state'] ?? '') === 'inbox' && $g['answer'] === null);
$ok('canvas_job_get of a chat job: not found', $host('canvas_job_get', ['jobId' => 'JOB-OWN']) === ['error' => 'not_found']);
$ok('canvas_job_get bad id', $host('canvas_job_get', []) === ['error' => 'jobId required']);
$ok('viewer cannot ask the agent', $host('canvas_job', array_merge($job, ['env' => 'sam'])) === ['error' => 'forbidden']);
$ok('project file is not a chat file', $host('canvas_job', array_merge($job, ['path' => 'projects/P1/a.docx'])) === ['error' => 'not_a_chat_file']);
$ok('missing prompt', $host('canvas_job', array_merge($job, ['prompt' => ''])) === ['error' => 'path, env, summary and prompt required']);

// ---------- share ----------
$data = 'PK fake docx';
$put = ['id' => null, 'seq' => 0, 'total' => 1, 'env' => 'owner', 'email' => 'owner@example.org', 'path' => $doc, 'name' => 'report.docx',
    'mime' => 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'size' => strlen($data),
    'sha256' => hash('sha256', $data), 'data' => base64_encode($data)];
$s = $host('canvas_share_put', $put);
$ok('share put', is_string($s['id'] ?? null) && preg_match('/^[0-9a-f]{32}$/', $s['id']) === 1
    && is_file(co_agent_home() . "/co-agent/state/canvas-share/{$s['id']}/data"));
$ok('share put: bad chunk is an error in the body', $host('canvas_share_put', array_merge($put, ['mime' => 'text/html'])) === ['error' => 'invalid share chunk']);
$draft = static fn(string $env, string $email, array $in) => co_module_call($db, 'user', 'canvas_share_draft', $in, $env, false, $email);
$ok('draft: another account is refused', $draft('sam', 'sam@example.org', ['id' => $s['id']]) === ['error' => 'forbidden']);
$ok('draft: no Gmail session -> need_gmail with the e-mail', $draft('owner', 'owner@example.org', ['id' => $s['id']]) === ['error' => 'need_gmail', 'email' => 'owner@example.org']);
$ok('draft: unknown id', $draft('owner', 'owner@example.org', ['id' => str_repeat('0', 32)]) === ['error' => 'not_found']
    && $draft('owner', 'owner@example.org', ['id' => 'x']) === ['error' => 'not_found']);

// ---------- disabled ----------
co_module_set($db, 'canvas', false, 'owner');
co_modules($db, true);
$gone = true;
foreach (['user' => ['canvas_ticket', 'canvas_share_draft'], 'host' => ['canvas_job', 'canvas_job_get', 'canvas_share_put']] as $via => $ops) {
    foreach ($ops as $op) $gone = $gone && co_module_for_op($db, $via, $op) === null && co_module_call($db, $via, $op, []) === null;
}
$ok('disabled module: every op gone', $gone);
$ok('disabled module: no pages for members', array_filter(co_modules_list($db, 'sam', false)['modules'], fn($x) => $x['id'] === 'canvas') === []);
co_module_remove($db, ['id' => 'canvas', 'data' => 'erase']);

echo $fail ? "$fail FAILED\n" : "canvas-module-test OK\n";
exit($fail ? 1 : 0);
