<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// The module dictation on the server (Freedom F10, ext/1 ui.composer + server.uploads) against a throwaway MariaDB
// (never production): install, the composer page and its upload in modules_list, the status op with and without the
// speech-to-text key, a provider chain with a local server first, a declared upload handler, nothing of it while the module is off.
// Driver: python co-agent/tests/actions-test.py --test dictation-test.php
declare(strict_types=1);
require __DIR__ . '/../web/agent/modules-lib.php';

$db = co_db();
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$db->query('DELETE FROM modules');
co_modules($db, true);

$src = realpath(__DIR__ . '/../modules/dictation');
$dst = co_module_home('dictation');
co_module_rmtree($dst);
$files = [];
$it = new RecursiveIteratorIterator(new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS));
foreach ($it as $f) {
    $rel = str_replace('\\', '/', substr($f->getPathname(), strlen($src) + 1));
    @mkdir(dirname("$dst/$rel"), 0700, true);
    copy($f->getPathname(), "$dst/$rel");
    if (!in_array($rel, ['module.json', 'README.md', 'freedom-manifest.json'], true)) $files[$rel] = hash_file('sha256', "$dst/$rel");
}
$man = json_decode((string)file_get_contents("$src/module.json"), true);
$r = co_module_install($db, ['manifest' => $man, 'files' => $files, 'standard' => true, 'publisher' => 'ed25519:test']);
$ok('installed, off', $r['ok'] && !$r['enabled']);
$ok('off: members see nothing, op not dispatched', co_modules_list($db, 'sam', false)['modules'] === [] && co_module_for_op($db, 'user', 'dictation_status') === null);

co_module_set($db, 'dictation', true, 'owner');
$list = co_modules_list($db, 'sam', false)['modules'];
$ok('member sees the composer page, its upload and permissions', ($list[0]['ui'][0]['slot'] ?? '') === 'ui.composer'
    && $list[0]['uploads'] === ['transcribe'] && in_array('media.audio', $list[0]['permissions'], true) && $list[0]['uiOps'] === ['dictation_status']);

$key = co_agent_home() . '/co-agent/secrets/transcribe.json';
@unlink($key);
$ok('status without a key: not configured', co_module_call($db, 'user', 'dictation_status', [], 'sam', false) === ['configured' => false, 'provider' => null]);
file_put_contents($key, json_encode(['provider' => 'groq', 'key' => 'gsk_test_not_real']));
$ok('status with a key: configured, provider only (no key)', co_module_call($db, 'user', 'dictation_status', [], 'sam', false) === ['configured' => true, 'provider' => 'groq']);
file_put_contents($key, json_encode(['providers' => [
    ['provider' => 'local', 'key' => 'whk-test', 'url' => 'https://127.0.0.1:9', 'prompt' => 'Colruyt, OpenMC'],
    ['provider' => 'local', 'key' => 'whk-test', 'url' => 'http://insecure.example'],
    ['provider' => 'groq', 'key' => 'gsk_test_not_real'],
]]));
$chain = co_transcribe_chain();
$ok('chain: local first, plain-http local dropped, groq as fallback', array_column($chain, 'provider') === ['local', 'groq']);
$ok('local url completed to /v1/audio/transcriptions, short connect', $chain[0]['url'] === 'https://127.0.0.1:9/v1/audio/transcriptions' && $chain[0]['connect'] === 5);
$ok('status with a chain: first provider', co_module_call($db, 'user', 'dictation_status', [], 'sam', false) === ['configured' => true, 'provider' => 'local']);
if (class_exists('CURLFile')) {
    $probe = tempnam(sys_get_temp_dir(), 'dict');
    file_put_contents($probe, 'not audio');
    $r = co_transcribe_post($chain[0], $probe, 'audio/webm', 'a.webm', 'ru', '');
    $ok('unreachable home server: failed, hand over to the next, key not in detail', !$r['ok'] && $r['retry'] && !str_contains($r['detail'], 'whk-test'));
    @unlink($probe);
} else {
    echo "skip unreachable home server (this PHP has no curl)
";
}
file_put_contents($key, json_encode(['provider' => 'local', 'key' => 'whk-test']));
$ok('local without url: not configured', co_module_call($db, 'user', 'dictation_status', [], 'sam', false)['configured'] === false);
file_put_contents($key, json_encode(['provider' => 'nobody', 'key' => 'x']));
$ok('unknown provider: not configured', co_module_call($db, 'user', 'dictation_status', [], 'sam', false)['configured'] === false);
@unlink($key);

$h = co_module_load(co_modules($db)['dictation']);
$ok('upload handler declared and loaded', is_callable($h['uploads']['transcribe'] ?? null));
$ok('upload names checked', co_module_uploads(['manifest' => ['server' => ['uploads' => ['transcribe', '../x', 'A', 5]]]]) === ['transcribe']);
$ok('composer slot accepted', in_array('ui.composer', CO_MODULE_SLOTS, true));

co_module_set($db, 'dictation', false, 'owner');
$ok('off again: op gone', co_module_for_op($db, 'user', 'dictation_status') === null);
$ok('remove', co_module_remove($db, ['id' => 'dictation', 'data' => 'keep'])['ok'] === true);

echo $fail ? "\n$fail FAILED\n" : "\nall ok\n";
exit($fail ? 1 : 0);
