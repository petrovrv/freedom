<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// members-lib.php: the people of this workbench (schema 21, Freedom F1). One row per environment: e-mail, name,
// color, role (one owner) and the right to start the server agent. Everything that used to be an e-mail or
// environment constant in the sources reads it from here. The last good list is kept in ~/co-agent/state/members.json,
// so a database outage does not lock everybody out of push and sign-in checks.
declare(strict_types=1);

require_once __DIR__ . '/lib.php';
require_once __DIR__ . '/db-lib.php';

const CO_MEMBERS_ROLES = ['owner', 'member'];

/** All members ordered by sort, env: [env, email, name, color, role, siteAgent]. */
function co_members(bool $fresh = false): array
{
    static $list = null;
    if ($list !== null && !$fresh) return $list;
    $cache = co_agent_home() . '/co-agent/state/members.json';
    try {
        $rows = co_db()->query("SELECT env, email, name, color, role, site_agent FROM members ORDER BY sort, env")->fetch_all(MYSQLI_NUM);
        $list = array_map(static fn(array $r): array => ['env' => (string)$r[0], 'email' => strtolower((string)$r[1]),
            'name' => (string)$r[2], 'color' => (string)$r[3], 'role' => (string)$r[4], 'siteAgent' => (int)$r[5] === 1], $rows);
        $json = json_encode($list, JSON_UNESCAPED_UNICODE);
        if ($list && @file_get_contents($cache) !== $json) {
            if (!is_dir(dirname($cache))) @mkdir(dirname($cache), 0700, true);
            $tmp = $cache . '.tmp.' . getmypid();
            if (@file_put_contents($tmp, $json) !== false) {
                @chmod($tmp, 0600); // e-mails of the family
                @rename($tmp, $cache);
            }
        }
    } catch (Throwable) {
        $old = json_decode((string)@file_get_contents($cache), true);
        $list = is_array($old) ? $old : [];
    }
    return $list;
}

/** email => env of every member (sign-in to the API, push, authors of messages). */
function co_push_users(): array
{
    $m = [];
    foreach (co_members() as $r) $m[$r['email']] = $r['env'];
    return $m;
}

function co_member(string $env): ?array
{
    foreach (co_members() as $r) if ($r['env'] === $env) return $r;
    return null;
}

/** Environment of the owner ('' before the installer has added one). */
function co_owner_env(): string
{
    foreach (co_members() as $r) if ($r['role'] === 'owner') return $r['env'];
    return '';
}

function co_owner_email(): string
{
    foreach (co_members() as $r) if ($r['role'] === 'owner') return $r['email'];
    return '';
}

function co_is_owner_env(?string $env): bool
{
    return $env !== null && $env !== '' && $env === co_owner_env();
}

/** env => display name. */
function co_member_names(): array
{
    return array_column(co_members(), 'name', 'env');
}

function co_member_envs(): array
{
    return array_column(co_members(), 'env');
}

/** E-mails allowed to start the server agent: the owner and members with site_agent. */
function co_site_agent_emails(): array
{
    return array_values(array_map(static fn(array $r): string => $r['email'],
        array_filter(co_members(), static fn(array $r): bool => $r['role'] === 'owner' || $r['siteAgent'])));
}

/**
 * Add or change a member (installer, db/members.php). Fields: env, email, name, color?, role?, siteAgent?, sort?.
 * Without role an existing member keeps theirs (new ones become members). Making someone owner turns the previous
 * owner into a member; the owner cannot be demoted directly (make another member owner).
 */
function co_member_put(mysqli $db, array $in): array
{
    $env = is_string($in['env'] ?? null) ? strtolower(trim($in['env'])) : '';
    $email = is_string($in['email'] ?? null) ? strtolower(trim($in['email'])) : '';
    $name = is_string($in['name'] ?? null) ? trim($in['name']) : '';
    $cur = preg_match('/^[a-z0-9-]{1,40}$/', $env) ? co_member($env) : null;
    $role = is_string($in['role'] ?? null) && $in['role'] !== '' ? $in['role'] : ($cur['role'] ?? 'member');
    if (!preg_match('/^[a-z0-9-]{1,40}$/', $env)) throw new InvalidArgumentException('env must match [a-z0-9-]{1,40}');
    if (!filter_var($email, FILTER_VALIDATE_EMAIL)) throw new InvalidArgumentException('bad email');
    if ($name === '' || mb_strlen($name) > 80) throw new InvalidArgumentException('name must be 1..80 chars');
    if (!in_array($role, CO_MEMBERS_ROLES, true)) throw new InvalidArgumentException('role must be owner or member');
    if (($cur['role'] ?? '') === 'owner' && $role !== 'owner') {
        throw new InvalidArgumentException('the owner cannot be demoted; make another member owner first');
    }
    $color = is_string($in['color'] ?? null) ? trim($in['color']) : '';
    if ($color !== '' && !preg_match('/^#[0-9a-fA-F]{3,8}$|^[a-z]{3,16}$/', $color)) throw new InvalidArgumentException('bad color');
    $site = !empty($in['siteAgent']) ? 1 : 0;
    $sort = (int)($in['sort'] ?? 0);
    $db->begin_transaction();
    try {
        if ($role === 'owner') {
            $st = $db->prepare("UPDATE members SET role = 'member', updated_utc = UTC_TIMESTAMP(3) WHERE role = 'owner' AND env <> ?");
            $st->execute([$env]);
            $st->close();
        }
        $st = $db->prepare("INSERT INTO members (env, email, name, color, role, site_agent, sort, updated_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3)) ON DUPLICATE KEY UPDATE email = VALUES(email), name = VALUES(name),
            color = VALUES(color), role = VALUES(role), site_agent = VALUES(site_agent), sort = VALUES(sort), updated_utc = VALUES(updated_utc)");
        $st->execute([$env, $email, $name, $color, $role, $site, $sort]);
        $st->close();
        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }
    co_members(true);
    return co_member($env) ?? [];
}

function co_member_delete(mysqli $db, string $env): bool
{
    if ($env === co_owner_env()) throw new InvalidArgumentException('the owner cannot be deleted; make another member owner first');
    $st = $db->prepare("DELETE FROM members WHERE env = ?");
    $st->execute([$env]);
    $n = $st->affected_rows;
    $st->close();
    co_members(true);
    return $n > 0;
}
