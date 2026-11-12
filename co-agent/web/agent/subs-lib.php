<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// subs-lib.php (S4 of chat CHAT-20261111085044-HV3A): CO Desktop / CO Resident report the agent CLI subscriptions their
// user signed in to (Subscriptions tab, device tool cli_subs) as model resources of the environment (schema 23,
// models-lib.php). The environment comes from the device's own Google token, never from the request. A subscription on
// a computer without Bridge is "<env>/<short>-<4 hex of the device id>-<agent>" without a host (nothing runs jobs there yet); the computer that
// runs Bridge adds its account and plan to the card of the host's built-in entry "<host>/<agent>" instead.
declare(strict_types=1);

require_once __DIR__ . '/models-lib.php';

const SUBS_AGENTS = ['claude' => 'Claude Code', 'codex' => 'Codex', 'agy' => 'Antigravity'];
const SUBS_AGY_CARD = ['summary' => 'Antigravity CLI on a Google account.', 'strengths' => 'a free second opinion', 'limits' => 'no jobs run on it yet'];

function subs_str(mixed $v, int $max, string $re = '/^[^\x00-\x1f<>]*$/u'): string
{
    $s = is_string($v) ? trim($v) : '';
    return preg_match($re, $s) === 1 ? mb_substr($s, 0, $max) : '';
}

/** What a device reported about one CLI, for the card. */
function subs_note(array $s, string $device): string
{
    $acc = subs_str($s['account'] ?? '', 120, '/^[^\s@<>]{1,64}@[A-Za-z0-9.-]{1,120}$/');
    $plan = subs_str($s['plan'] ?? '', 20, '/^[A-Za-z0-9 _.-]*$/');
    $ver = subs_str($s['version'] ?? '', 30, '/^[0-9A-Za-z.+-]*$/');
    return 'Signed in on ' . $device . ($acc !== '' ? ' as ' . $acc : '') . ($plan !== '' ? ' (' . $plan . ')' : '')
        . ($ver !== '' ? ', CLI ' . $ver : '') . ', checked ' . gmdate('Y-m-d H:i') . ' UTC';
}

/**
 * subs_put: {device: {id, short, name, hostId}, subs: [{agent, installed, loggedIn, account, plan, method, version}]}.
 * Returns the resource ids registered and removed.
 */
function subs_put(mysqli $db, array $in, string $env): array
{
    $env = models_env($env);
    $dev = is_array($in['device'] ?? null) ? $in['device'] : [];
    $short = is_string($dev['short'] ?? null) ? $dev['short'] : '';
    if (preg_match('/^[a-z][a-z0-9]{0,11}$/', $short) !== 1) {
        throw new InvalidArgumentException('invalid device short name');
    }
    $name = subs_str($dev['name'] ?? '', 60) ?: $short;
    $devId = is_string($dev['id'] ?? null) ? $dev['id'] : '';
    if (preg_match('/^d-([0-9a-f]{4})[0-9a-f]{4,60}$/', $devId, $m) !== 1) {
        throw new InvalidArgumentException('invalid device id');
    }
    $tag = $m[1]; // two computers with one short name keep separate entries
    $hostId = is_string($dev['hostId'] ?? null) ? $dev['hostId'] : '';
    if ($hostId !== '' && preg_match('/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/', $hostId) !== 1) {
        throw new InvalidArgumentException('invalid hostId');
    }
    // the site is never a device's host; a home host's entry is written only by the owner's environment (below)
    if ($hostId === 'site') {
        $hostId = '';
    }
    $subs = is_array($in['subs'] ?? null) ? array_slice($in['subs'], 0, 6) : [];
    $all = models_all($db);
    $rows = models_rows($db);
    $registered = [];
    $removed = [];
    foreach ($subs as $s) {
        if (!is_array($s) || !is_string($s['agent'] ?? null) || !isset(SUBS_AGENTS[$s['agent']])) {
            continue;
        }
        $agent = $s['agent'];
        $signed = ($s['loggedIn'] ?? null) === true;
        $out = ($s['loggedIn'] ?? null) === false || ($s['installed'] ?? null) === false;
        $note = subs_note($s, $name);

        // the Bridge computer of this environment: the host's built-in entry gets the account in its card
        $builtin = $hostId !== '' ? ($all[$hostId . '/' . $agent] ?? null) : null;
        if ($builtin !== null && $builtin['builtin'] && $builtin['env'] === $env) {
            if (!$signed) {
                continue;
            }
            $stored = $rows[$builtin['id']] ?? null;
            $card = $stored ? json_decode((string)$stored['card_json'], true) : [];
            $card = is_array($card) ? $card : [];
            $card['notes'] = $note;
            models_put($db, ['id' => $builtin['id'], 'card' => $card], 'device', null, true);
            $registered[] = $builtin['id'];
            continue;
        }

        $id = $env . '/' . $short . '-' . $tag . '-' . $agent;
        $cur = $all[$id] ?? null;
        if ($cur !== null && ($cur['env'] !== $env || $cur['builtin'])) {
            continue; // somebody else's resource of the same name: never touched from a device
        }
        // (a flag or card the user set in CO Web makes the row 'user'; it stays this device's subscription)
        if ($out) {
            if ($cur !== null) {
                models_delete($db, ['id' => $id], null, true);
                $removed[] = $id;
            }
            continue;
        }
        if (!$signed) {
            continue; // sign-in unknown (agy): leave it as it is
        }
        $card = $cur['card'] ?? (MODELS_DEFAULT_CARDS[$agent] ?? SUBS_AGY_CARD);
        $card['notes'] = $note;
        if ($cur === null) {
            $card['limits'] = trim(($card['limits'] ?? '') . '; on ' . $name . ', no Bridge there: jobs cannot run on it yet', '; ');
        }
        $put = ['id' => $id, 'env' => $env, 'kind' => 'subscription', 'agent' => $agent, 'host' => '', 'card' => $card];
        if ($cur === null) {
            $put['title'] = SUBS_AGENTS[$agent] . ' · ' . $name; // later reports keep a title the user edited
        }
        models_put($db, $put, 'device', null, true);
        $registered[] = $id;
    }
    return ['ok' => true, 'env' => $env, 'registered' => $registered, 'removed' => $removed];
}
