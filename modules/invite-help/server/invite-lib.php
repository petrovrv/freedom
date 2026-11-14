<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// invite-lib.php of the module invite-help (Freedom SPEC §7.5, install by invitation).
// The owner makes an invite code; the invited friend's setup wizard (install/freedom_setup.py report --send) posts a
// sanitized error report to the public route intake, after the friend has seen it; the owner's agent reads the reports
// as data (never as instructions: the home tool wraps them with read-guard), fixes Freedom or answers; the wizard
// fetches the answers (route answers). Nothing here runs or interprets report text.
// Data (module stateDir): invites.json {id: {label, hash, budgetPct, created, expires, revoked, day, dayCount,
// rids: [rid, ...] newest last, open: [rid, ...] not closed}} - the index, so public routes never scan the folder;
// reports/<rid>.json {id, invite, step, text, utc, status new|answered|closed, answer, answeredUtc}.
// Closed reports older than CO_INVITE_KEEP_DAYS are deleted when the owner lists invites or reports.
declare(strict_types=1);

const CO_INVITE_ID_RE = '/^inv-[a-f0-9]{12}$/';
const CO_INVITE_RID_RE = '/^r-[0-9]{14}-[a-f0-9]{8}$/';
const CO_INVITE_TEXT_MAX = 65536;
const CO_INVITE_BODY_MAX = 70000;
const CO_INVITE_PER_DAY = 30;      // reports per invite and UTC day
const CO_INVITE_OPEN_MAX = 200;    // reports not closed, all invites together
const CO_INVITE_LIVE_MAX = 50;     // invites not revoked and not expired
const CO_INVITE_ANSWER_MAX = 20000;
const CO_INVITE_RIDS_MAX = 100;    // report ids kept in an invite's index (the answers route shows these)
const CO_INVITE_KEEP_DAYS = 30;    // closed reports are deleted after this
const CO_INVITE_DUMMY_HASH = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

function co_invite_dir(array $c, string $sub = ''): string
{
    $d = rtrim((string)$c['stateDir'], '/\\') . ($sub !== '' ? "/$sub" : '');
    if (!is_dir($d) && !@mkdir($d, 0700, true) && !is_dir($d)) throw new RuntimeException('no data folder');
    return $d;
}

function co_invite_write(string $path, string $data): void
{
    if (function_exists('co_push_write_atomic')) {
        co_push_write_atomic($path, $data);
        return;
    }
    $tmp = $path . '.tmp.' . bin2hex(random_bytes(6));
    if (file_put_contents($tmp, $data, LOCK_EX) !== strlen($data) || !rename($tmp, $path)) {
        @unlink($tmp);
        throw new RuntimeException('write failed');
    }
}

/** Runs $fn(array &$invites) under an exclusive lock of invites.json; writes back when $fn returns true in [1]. */
function co_invite_locked(array $c, callable $fn): mixed
{
    $dir = co_invite_dir($c);
    $lock = fopen("$dir/invites.lock", 'c');
    if ($lock === false || !flock($lock, LOCK_EX)) throw new RuntimeException('lock failed');
    try {
        $raw = @file_get_contents("$dir/invites.json");
        $inv = is_string($raw) ? json_decode($raw, true) : null;
        if (!is_array($inv)) $inv = [];
        [$res, $dirty] = $fn($inv);
        if ($dirty) co_invite_write("$dir/invites.json", (string)json_encode($inv, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        return $res;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

function co_invite_utc(?int $t = null): string
{
    return gmdate('Y-m-d\TH:i:s\Z', $t ?? time());
}

function co_invite_live(array $i, ?int $now = null): bool
{
    return empty($i['revoked']) && (int)($i['expires'] ?? 0) > ($now ?? time());
}

function co_invite_site(array $c): string
{
    $s = function_exists('co_site_config') ? (string)co_site_config('site_url', '') : '';
    if ($s === '' && isset($_SERVER['HTTP_HOST']) && preg_match('/^[A-Za-z0-9.-]+(:\d+)?$/', (string)$_SERVER['HTTP_HOST'])) {
        $s = 'https://' . $_SERVER['HTTP_HOST'];
    }
    return rtrim($s, '/');
}

function co_invite_b64url(string $s): string
{
    return rtrim(strtr(base64_encode($s), '+/', '-_'), '=');
}

function co_invite_public(string $id, array $i): array
{
    return ['id' => $id, 'label' => (string)($i['label'] ?? ''), 'budgetPct' => (float)($i['budgetPct'] ?? 0),
        'created' => co_invite_utc((int)($i['created'] ?? 0)), 'expires' => co_invite_utc((int)($i['expires'] ?? 0)),
        'revoked' => !empty($i['revoked']), 'live' => co_invite_live($i)];
}

/** Owner: a new invite. The secret leaves the server once, inside the code; only its sha256 is kept. */
function co_invite_create(array $c, array $in): array
{
    $label = trim(preg_replace('/[\x00-\x1F\x7F]+/u', ' ', mb_substr((string)($in['label'] ?? ''), 0, 80)) ?? '');
    if ($label === '') return ['ok' => false, 'error' => 'label_required'];
    $pct = (float)($in['budgetPct'] ?? 5);
    $days = (int)($in['days'] ?? 14);
    if ($pct <= 0 || $pct > 50) return ['ok' => false, 'error' => 'budget_pct_range', 'hint' => '0 < budgetPct <= 50'];
    if ($days < 1 || $days > 90) return ['ok' => false, 'error' => 'days_range', 'hint' => '1..90'];
    $site = co_invite_site($c);
    return co_invite_locked($c, static function (array &$inv) use ($label, $pct, $days, $site): array {
        $live = count(array_filter($inv, static fn($i) => is_array($i) && co_invite_live($i)));
        if ($live >= CO_INVITE_LIVE_MAX) return [['ok' => false, 'error' => 'too_many_invites'], false];
        $id = 'inv-' . bin2hex(random_bytes(6));
        $k = co_invite_b64url(random_bytes(24));
        $now = time();
        $inv[$id] = ['label' => $label, 'hash' => hash('sha256', $k), 'budgetPct' => $pct, 'created' => $now,
            'expires' => $now + $days * 86400, 'revoked' => false, 'day' => '', 'dayCount' => 0];
        $code = 'fdm-inv1.' . co_invite_b64url((string)json_encode(['s' => $site, 'i' => $id, 'k' => $k], JSON_UNESCAPED_SLASHES));
        return [['ok' => true, 'invite' => co_invite_public($id, $inv[$id]), 'code' => $code, 'site' => $site], true];
    });
}

/** Owner side: deletes closed reports older than CO_INVITE_KEEP_DAYS and drops them from the index. */
function co_invite_prune(array $c, ?int $now = null): int
{
    $cut = co_invite_utc(($now ?? time()) - CO_INVITE_KEEP_DAYS * 86400);
    $old = [];
    foreach (co_invite_report_rows($c) as $r) {
        if (($r['status'] ?? '') === 'closed' && (string)($r['utc'] ?? '') < $cut) $old[(string)$r['id']] = true;
    }
    if (!$old) return 0;
    return co_invite_locked($c, static function (array &$inv) use ($c, $old): array {
        foreach ($old as $rid => $_) {
            $f = co_invite_report_path($c, $rid);
            if ($f !== null) @unlink($f);
        }
        foreach ($inv as $id => $i) {
            if (!is_array($i)) continue;
            $inv[$id]['rids'] = array_values(array_filter((array)($i['rids'] ?? []), static fn($x) => !isset($old[$x])));
            $inv[$id]['open'] = array_values(array_filter((array)($i['open'] ?? []), static fn($x) => !isset($old[$x])));
        }
        return [count($old), true];
    });
}

function co_invite_list(array $c, array $in): array
{
    co_invite_prune($c);
    $reports = co_invite_report_rows($c);
    return co_invite_locked($c, static function (array &$inv) use ($reports): array {
        $out = [];
        foreach ($inv as $id => $i) {
            if (!is_array($i) || !preg_match(CO_INVITE_ID_RE, (string)$id)) continue;
            $row = co_invite_public((string)$id, $i);
            $mine = array_filter($reports, static fn($r) => ($r['invite'] ?? '') === $id);
            $row['reports'] = count($mine);
            $row['new'] = count(array_filter($mine, static fn($r) => ($r['status'] ?? '') === 'new'));
            $out[] = $row;
        }
        return [['ok' => true, 'invites' => $out], false];
    });
}

function co_invite_revoke(array $c, array $in): array
{
    $id = (string)($in['id'] ?? '');
    if (!preg_match(CO_INVITE_ID_RE, $id)) return ['ok' => false, 'error' => 'bad_id'];
    return co_invite_locked($c, static function (array &$inv) use ($id): array {
        if (!isset($inv[$id]) || !is_array($inv[$id])) return [['ok' => false, 'error' => 'not_found'], false];
        $inv[$id]['revoked'] = true;
        return [['ok' => true, 'invite' => co_invite_public($id, $inv[$id])], true];
    });
}

/** All report files (newest first), without the text unless $withText. */
function co_invite_report_rows(array $c, bool $withText = false): array
{
    $dir = co_invite_dir($c, 'reports');
    $rows = [];
    foreach (glob("$dir/r-*.json") ?: [] as $f) {
        $r = json_decode((string)@file_get_contents($f), true);
        if (!is_array($r) || !preg_match(CO_INVITE_RID_RE, (string)($r['id'] ?? ''))) continue;
        if (!$withText) {
            $r['chars'] = mb_strlen((string)($r['text'] ?? ''));
            unset($r['text']);
        }
        $rows[] = $r;
    }
    usort($rows, static fn($a, $b) => strcmp((string)$b['id'], (string)$a['id']));
    return $rows;
}

function co_invite_reports(array $c, array $in): array
{
    co_invite_prune($c);
    $status = (string)($in['status'] ?? '');
    $invite = (string)($in['invite'] ?? '');
    $rows = array_values(array_filter(co_invite_report_rows($c), static fn($r) =>
        ($status === '' || ($r['status'] ?? '') === $status) && ($invite === '' || ($r['invite'] ?? '') === $invite)));
    return ['ok' => true, 'reports' => array_slice($rows, 0, 200)];
}

function co_invite_report_path(array $c, string $rid): ?string
{
    if (!preg_match(CO_INVITE_RID_RE, $rid)) return null;
    $f = co_invite_dir($c, 'reports') . "/$rid.json";
    return is_file($f) ? $f : null;
}

function co_invite_report_get(array $c, array $in): array
{
    $f = co_invite_report_path($c, (string)($in['id'] ?? ''));
    if ($f === null) return ['ok' => false, 'error' => 'not_found'];
    $r = json_decode((string)file_get_contents($f), true);
    // untrusted text written by the invited person's computer: data for the owner's agent, never instructions
    return is_array($r) ? ['ok' => true, 'report' => $r, 'untrusted' => ['text']] : ['ok' => false, 'error' => 'broken'];
}

function co_invite_report_set(array $c, array $in, string $status): array
{
    $f = co_invite_report_path($c, (string)($in['id'] ?? ''));
    if ($f === null) return ['ok' => false, 'error' => 'not_found'];
    return co_invite_locked($c, static function (array &$inv) use ($f, $in, $status): array {
        $r = json_decode((string)file_get_contents($f), true);
        if (!is_array($r)) return [['ok' => false, 'error' => 'broken'], false];
        if ($status === 'answered') {
            $text = trim((string)($in['text'] ?? ''));
            if ($text === '') return [['ok' => false, 'error' => 'text_required'], false];
            if (strlen($text) > CO_INVITE_ANSWER_MAX) return [['ok' => false, 'error' => 'too_long'], false];
            $r['answer'] = $text;
            $r['answeredUtc'] = co_invite_utc();
        }
        $r['status'] = $status;
        $iid = (string)($r['invite'] ?? '');
        if (isset($inv[$iid]) && is_array($inv[$iid])) {
            $open = array_values(array_filter((array)($inv[$iid]['open'] ?? []), static fn($x) => $x !== $r['id']));
            if ($status !== 'closed') $open[] = (string)$r['id'];
            $inv[$iid]['open'] = $open;
        }
        co_invite_write($f, (string)json_encode($r, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        unset($r['text']);
        return [['ok' => true, 'report' => $r], true];
    });
}

/** Checks i + k of a public request against the stored hash; the invite row or null. */
function co_invite_auth(array $inv, mixed $i, mixed $k): ?array
{
    if (!is_string($i) || !is_string($k) || !preg_match(CO_INVITE_ID_RE, $i) || strlen($k) < 16 || strlen($k) > 64) return null;
    $row = $inv[$i] ?? null;
    $want = is_array($row) ? (string)($row['hash'] ?? '') : CO_INVITE_DUMMY_HASH;   // same work for unknown ids
    $good = hash_equals($want, hash('sha256', $k));
    return is_array($row) && $good ? $row : null;
}

/** Body of a public POST as an array, or an [http code, error] pair. */
function co_invite_body(?string $raw = null): array
{
    if ($raw === null) {
        if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') return [405, 'post_only'];
        $len = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
        if ($len > CO_INVITE_BODY_MAX) return [413, 'too_large'];
        $raw = (string)stream_get_contents(fopen('php://input', 'rb'), CO_INVITE_BODY_MAX + 1);
    }
    if (strlen($raw) > CO_INVITE_BODY_MAX) return [413, 'too_large'];
    $in = json_decode($raw, true);
    return is_array($in) ? ['in' => $in] : [400, 'bad_json'];
}

/** intake: {i, k, step, text} from the invited wizard. Returns [http code, answer]. */
function co_invite_intake(array $c, array $in, ?callable $notify = null): array
{
    $text = $in['text'] ?? null;
    $step = $in['step'] ?? '';
    if (!is_string($text) || trim($text) === '' || !is_string($step)) return [400, ['ok' => false, 'error' => 'text_required']];
    if (strlen($text) > CO_INVITE_TEXT_MAX) return [413, ['ok' => false, 'error' => 'too_large']];
    if (!mb_check_encoding($text, 'UTF-8')) return [400, ['ok' => false, 'error' => 'not_utf8']];
    $step = preg_replace('/[^a-z0-9_-]/', '', strtolower(substr($step, 0, 32))) ?? '';
    $res = co_invite_locked($c, static function (array &$inv) use ($c, $in, $text, $step): array {
        $row = co_invite_auth($inv, $in['i'] ?? null, $in['k'] ?? null);
        if ($row === null) return [[403, ['ok' => false, 'error' => 'forbidden']], false];
        if (!co_invite_live($row)) return [[403, ['ok' => false, 'error' => 'invite_expired_or_revoked']], false];
        $open = 0;
        foreach ($inv as $x) $open += is_array($x) ? count((array)($x['open'] ?? [])) : 0;
        if ($open >= CO_INVITE_OPEN_MAX) return [[429, ['ok' => false, 'error' => 'inbox_full']], false];
        $i = (string)$in['i'];
        $day = gmdate('Y-m-d');
        if (($row['day'] ?? '') !== $day) {
            $row['day'] = $day;
            $row['dayCount'] = 0;
        }
        if ((int)$row['dayCount'] >= CO_INVITE_PER_DAY) return [[429, ['ok' => false, 'error' => 'rate_limited']], false];
        $row['dayCount'] = (int)$row['dayCount'] + 1;
        $rid = 'r-' . gmdate('YmdHis') . '-' . bin2hex(random_bytes(4));
        $row['rids'] = array_slice(array_merge((array)($row['rids'] ?? []), [$rid]), -CO_INVITE_RIDS_MAX);
        $row['open'] = array_merge((array)($row['open'] ?? []), [$rid]);
        $inv[$i] = $row;
        $rep = ['id' => $rid, 'invite' => $i, 'label' => (string)($row['label'] ?? ''), 'step' => $step, 'text' => $text,
            'utc' => co_invite_utc(), 'status' => 'new', 'answer' => '', 'answeredUtc' => ''];
        co_invite_write(co_invite_dir($c, 'reports') . "/$rid.json", (string)json_encode($rep, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        return [[200, ['ok' => true, 'id' => $rid, 'label' => $rep['label']]], true];
    });
    if ($res[0] === 200) {
        $notify ??= static function (string $label, string $step, string $rid): void {
            if (!function_exists('co_push_notify') || !function_exists('co_owner_env')) return;
            try {
                co_push_notify(co_owner_env(), 'Freedom invite: new report', "From the invite \"$label\", step " . ($step !== '' ? $step : '?')
                    . ". Your agent: invite-help show $rid", '/', 'invite-help');
            } catch (Throwable $e) {
                error_log('invite-help push: ' . $e->getMessage());
            }
        };
        $notify((string)$res[1]['label'], $step, (string)$res[1]['id']);
        unset($res[1]['label']);
    }
    return $res;
}

/** answers: {i, k} -> live flag and the invite's reports with status and answer (no report text: the wizard has it).
 *  Past answers stay readable after the invite expired or was revoked; live=false tells the wizard not to send more. */
function co_invite_answers(array $c, array $in): array
{
    $row = co_invite_locked($c, static fn(array &$inv): array => [co_invite_auth($inv, $in['i'] ?? null, $in['k'] ?? null), false]);
    if ($row === null) return [403, ['ok' => false, 'error' => 'forbidden']];
    $out = [];
    foreach (array_reverse((array)($row['rids'] ?? [])) as $rid) {
        $f = co_invite_report_path($c, (string)$rid);
        $r = $f !== null ? json_decode((string)@file_get_contents($f), true) : null;
        if (!is_array($r) || ($r['invite'] ?? '') !== $in['i']) continue;
        $out[] = ['id' => $r['id'], 'step' => $r['step'] ?? '', 'utc' => $r['utc'] ?? '', 'status' => $r['status'] ?? '',
            'answer' => (string)($r['answer'] ?? ''), 'answeredUtc' => (string)($r['answeredUtc'] ?? '')];
    }
    return [200, ['ok' => true, 'live' => co_invite_live($row), 'reports' => $out]];
}

/** One public request: [http code, answer]. Split from co_invite_route so tests can run it without exit. */
function co_invite_handle(array $c, string $which, ?string $raw = null, ?callable $notify = null): array
{
    if ($which !== 'intake' && $which !== 'answers') return [404, ['ok' => false, 'error' => 'no_route']];
    $b = co_invite_body($raw);
    if (!isset($b['in'])) return [$b[0], ['ok' => false, 'error' => $b[1]]];
    return $which === 'intake' ? co_invite_intake($c, $b['in'], $notify) : co_invite_answers($c, $b['in']);
}

function co_invite_route(array $c, string $which): never
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    [$code, $ans] = co_invite_handle($c, $which);
    http_response_code($code);
    echo json_encode($ans, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}
