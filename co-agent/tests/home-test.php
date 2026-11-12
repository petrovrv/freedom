<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F2: co_agent_home() and cron/boot.php find the home on the hPanel layout (~/domains/<site>/public_html) and on
// the cPanel layout (~/public_html). Copies lib.php into throwaway layouts; no database.  php tests/home-test.php
declare(strict_types=1);

$src = dirname(__DIR__);
$auth = dirname(__DIR__, 2) . '/co-web-auth/auth/lib.php';
$fail = 0;
$ok = static function (string $name, bool $cond) use (&$fail): void {
    if (!$cond) $fail++;
    echo ($cond ? 'ok   ' : 'FAIL ') . $name . "\n";
};
$q = static function (string $code): string {
    $file = tempnam(sys_get_temp_dir(), 'co-home-test');
    file_put_contents($file, "<?php". PHP_EOL . $code);
    $out = trim((string)shell_exec(escapeshellarg(PHP_BINARY) . ' ' . escapeshellarg($file)));
    unlink($file);
    return str_replace(chr(92), '/', $out); // Windows test runs print backslashes
};
$rm = static function (string $d) use (&$rm): void {
    foreach (glob($d . '/{,.}[!.]*', GLOB_BRACE) ?: [] as $f) is_dir($f) ? $rm($f) : unlink($f);
    rmdir($d);
};

foreach (['hpanel' => 'domains/example.org/public_html', 'cpanel' => 'public_html'] as $kind => $web) {
    $home = str_replace(chr(92), '/', sys_get_temp_dir()) . '/co-home-test-' . $kind . '-' . bin2hex(random_bytes(4));
    @mkdir("$home/$web/agent", 0777, true);
    @mkdir("$home/$web/auth", 0777, true);
    @mkdir("$home/co-agent/cron", 0777, true);
    copy("$src/web/agent/lib.php", "$home/$web/agent/lib.php");
    copy("$src/web/agent/db-lib.php", "$home/$web/agent/db-lib.php");
    copy($auth, "$home/$web/auth/lib.php");
    copy("$src/cron/boot.php", "$home/co-agent/cron/boot.php");
    $got = $q('require ' . var_export("$home/$web/agent/lib.php", true) . '; echo co_agent_home();');
    $ok("$kind: co_agent_home", $got === $home);
    @mkdir("$home/co-auth", 0777, true);
    file_put_contents("$home/co-auth/config.php", "<?php return ['origin' => 'https://$kind.example.org'];");
    $got = $q('require ' . var_export("$home/$web/auth/lib.php", true) . '; echo co_config()["origin"];');
    $ok("$kind: sign-in finds ~/co-auth/config.php", $got === "https://$kind.example.org");
    $got = $q('require ' . var_export("$home/co-agent/cron/boot.php", true) . '; echo CO_AGENT_WEB;');
    $ok("$kind: boot.php CO_AGENT_WEB", $got === "$home/$web/agent");
    putenv('CO_AGENT_HOME=/srv/other');
    $got = $q('require ' . var_export("$home/$web/agent/lib.php", true) . '; echo co_agent_home();');
    putenv('CO_AGENT_HOME');
    $ok("$kind: env CO_AGENT_HOME wins", $got === '/srv/other');
    $rm($home);
}
echo $fail ? "FAILED $fail\n" : "all ok\n";
exit($fail ? 1 : 0);
