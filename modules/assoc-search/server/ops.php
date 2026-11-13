<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module assoc-search (Freedom F10, SPEC §3.6, §3.7): the handlers modules-lib.php calls.
//   host: vec_todo, vec_put, vec_words, vec_texts, protocol_texts, alias_put, alias_list, alias_delete - the indexer
//         aiwb-assoc on the home PC (host key), unchanged wire format of schema 20
//   search.vector: expand (aliases) and chats (protocol vectors) for the core's chat search (ext/1 search.provider)
//   install: creates the tables m_assoc_search_*; a server that had them in the core (schema 20) gets them renamed, so
//            vectors, the word table and the aliases stay as they were
declare(strict_types=1);

require_once __DIR__ . '/assoc-lib.php';

const CO_ASSOC_TABLES = ['vec_words' => 'm_assoc_search_vec_words', 'vec_protocol' => 'm_assoc_search_vec_protocol',
    'aliases' => 'm_assoc_search_aliases'];

function co_assoc_table_exists(mysqli $db, string $t): bool
{
    $st = $db->prepare('SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?');
    $st->bind_param('s', $t);
    $st->execute();
    $n = (int)($st->get_result()->fetch_row()[0] ?? 0);
    $st->close();
    return $n > 0;
}

/** Tables of the module: the pre-F10 core tables renamed (one atomic RENAME), else created. Additive only. */
function co_assoc_migrate(mysqli $db): array
{
    $renamed = [];
    $pairs = [];
    foreach (CO_ASSOC_TABLES as $old => $new) {
        if (co_assoc_table_exists($db, $old) && !co_assoc_table_exists($db, $new)) {
            $pairs[] = "`$old` TO `$new`";
            $renamed[] = $old;
        }
    }
    if ($pairs) {
        $db->query('RENAME TABLE ' . implode(', ', $pairs));
    }
    $db->query("CREATE TABLE IF NOT EXISTS m_assoc_search_vec_words (
      word        VARCHAR(60)   CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
      n           SMALLINT      NOT NULL,
      scale       FLOAT         NOT NULL,
      v           VARBINARY(256) NOT NULL,
      freq        INT           NOT NULL DEFAULT 0,
      PRIMARY KEY (word)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
    $db->query("CREATE TABLE IF NOT EXISTS m_assoc_search_vec_protocol (
      protocol_id BIGINT        NOT NULL,
      chat_id     VARCHAR(60)   NOT NULL,
      sha         CHAR(40)      NOT NULL,
      scale       FLOAT         NOT NULL,
      v           VARBINARY(256) NOT NULL,
      PRIMARY KEY (protocol_id),
      KEY ix_vec_protocol_chat (chat_id)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
    $db->query("CREATE TABLE IF NOT EXISTS m_assoc_search_aliases (
      id          BIGINT        NOT NULL AUTO_INCREMENT,
      env         VARCHAR(40)   NOT NULL DEFAULT '',
      grp         VARCHAR(80)   NOT NULL,
      term        VARCHAR(120)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
      source      VARCHAR(12)   NOT NULL,
      created_utc DATETIME(3)   NOT NULL,
      PRIMARY KEY (id),
      UNIQUE KEY ux_aliases (env, grp, term),
      KEY ix_aliases_term (term)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
    return ['renamed' => $renamed];
}

return [
    'host' => [
        'vec_todo'       => static fn(array $c, array $in): array => co_vec_todo($c['db'], $in),
        'vec_put'        => static fn(array $c, array $in): array => co_vec_put($c['db'], $in),
        'vec_words'      => static fn(array $c, array $in): array => co_vec_words($c['db'], $in),
        'vec_texts'      => static fn(array $c, array $in): array => co_vec_texts($c['db'], $in),
        'protocol_texts' => static fn(array $c, array $in): array => co_vec_protocol_texts($c['db'], $in),
        'alias_put'      => static fn(array $c, array $in): array => co_alias_op($c['db'], 'alias_put', $in),
        'alias_list'     => static fn(array $c, array $in): array => co_alias_op($c['db'], 'alias_list', $in),
        'alias_delete'   => static fn(array $c, array $in): array => co_alias_op($c['db'], 'alias_delete', $in),
    ],
    'search' => [
        'vector' => [
            'expand' => static fn(array $c, array $in): array => co_alias_groups($c['db'], (string)$in['env'], (string)$in['q']),
            'chats'  => static fn(array $c, array $in): ?array => co_assoc_chats($c['db'], $in),
        ],
    ],
    'install' => static fn(array $c, array $info): array => co_assoc_migrate($c['db']),
    // remove with data erase: the tables go too (keep: they stay for a later install)
    'remove' => static function (array $c, array $info): void {
        if (($info['data'] ?? 'keep') === 'erase') {
            foreach (CO_ASSOC_TABLES as $t) $c['db']->query("DROP TABLE IF EXISTS `$t`");
        }
    },
];
