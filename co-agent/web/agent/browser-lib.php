<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);

// CO Web 0.87.0 (09.11): settings of the agent browser (tabs per chat, tab lifetimes, captcha tabs).
// Built-in defaults < environment defaults (kv env/<env> 'browser') < chat override (chats.data.browser, only the keys set).
// Bridge 1.10.0 reads the effective settings once per job (host op browser_settings) and passes them to device calls
// (CO Desktop 0.9.9) and to browser-agent (env AIWB_BROWSER_*).

const CO_BROWSER_BUILTIN = ['tabs' => 0, 'idleMin' => 120, 'formIdleMin' => 360, 'captchaKeep' => true];

/** Keeps the known keys with valid values; clamps numbers to the contract ranges. Unknown or broken keys are dropped. */
function co_browser_clean(mixed $in): array
{
    if (!is_array($in)) return [];
    $out = [];
    $int = static fn(mixed $v, int $lo, int $hi): ?int =>
        is_int($v) || (is_float($v) && floor($v) === $v) ? max($lo, min($hi, (int)$v)) : null;
    if (($v = $int($in['tabs'] ?? null, 0, 20)) !== null) $out['tabs'] = $v;
    if (($v = $int($in['idleMin'] ?? null, 5, 1440)) !== null) $out['idleMin'] = $v;
    if (($v = $int($in['formIdleMin'] ?? null, 5, 2880)) !== null) $out['formIdleMin'] = $v;
    if (is_bool($in['captchaKeep'] ?? null)) $out['captchaKeep'] = $in['captchaKeep'];
    return $out;
}

/** Environment defaults as stored (only the keys set). */
function co_browser_env_defaults(mysqli $db, string $env): array
{
    $st = $db->prepare('SELECT data FROM kv WHERE scope = ? AND name = ?');
    $st->execute(['env/' . $env, 'browser']);
    $row = $st->get_result()->fetch_row();
    $st->close();
    return co_browser_clean($row ? json_decode((string)$row[0], true) : null);
}

function co_browser_env_defaults_put(mysqli $db, string $env, mixed $defaults): array
{
    $clean = co_browser_clean($defaults);
    $data = json_encode((object)$clean, JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    $st = $db->prepare('INSERT INTO kv (scope, name, data, sha, updated_utc) VALUES (?, ?, ?, ?, UTC_TIMESTAMP(3))
                        ON DUPLICATE KEY UPDATE data = VALUES(data), sha = VALUES(sha), updated_utc = VALUES(updated_utc)');
    $st->execute(['env/' . $env, 'browser', $data, hash('sha256', $data)]);
    $st->close();
    return $clean;
}

/** The chat's override (chats.data.browser), [] when the chat or the override does not exist. */
function co_browser_chat_override(mysqli $db, ?string $chatId): array
{
    if ($chatId === null || $chatId === '') return [];
    $st = $db->prepare('SELECT data FROM chats WHERE chat_id = ?');
    $st->execute([$chatId]);
    $row = $st->get_result()->fetch_row();
    $st->close();
    $chat = $row ? json_decode((string)$row[0], true) : null;
    return is_array($chat) ? co_browser_clean($chat['browser'] ?? null) : [];
}

/** Effective settings; formIdleMin is never below idleMin. */
function co_browser_effective(array $envDefaults, array $chat): array
{
    $e = array_merge(CO_BROWSER_BUILTIN, $envDefaults, $chat);
    $e['formIdleMin'] = max($e['idleMin'], $e['formIdleMin']);
    return $e;
}

/**
 * Host op browser_settings {env, chatId?}: what Bridge passes to devices and browser-agent for one job.
 * With 'defaults' (agents on the home PC, e.g. a member's tabs = 1) the environment defaults are saved first.
 */
function co_browser_host(mysqli $db, array $in): array
{
    $env = $in['env'] ?? null;
    if (!is_string($env) || !preg_match('/^[a-z0-9-]{1,40}$/D', $env)) {
        throw new InvalidArgumentException('invalid env');
    }
    if (array_key_exists('defaults', $in)) {
        co_browser_env_defaults_put($db, $env, $in['defaults']);
    }
    $chatId = is_string($in['chatId'] ?? null) && co_db_id_ok($in['chatId']) ? $in['chatId'] : null;
    return ['browser' => co_browser_effective(co_browser_env_defaults($db, $env), co_browser_chat_override($db, $chatId))];
}
