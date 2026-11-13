<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module docs-search (Freedom F10, SPEC §3.6, §3.7): the handlers modules-lib.php calls.
//   host: docs_state, docs_put, docs_delete - the indexer aiwb-docs on the home PC (host key); docs_search - agents
//         (aiwb-docs search); unchanged wire format of schema 7
//   user: search_docs - CO Web before Freedom F10 (the AI search of CO Web 0.27-0.102); newer CO Web asks the core's
//         search_items, which calls the provider below
//   search.docs: items (documents for the AI search), text (indexed text of a project file for the file annotations)
//   install: creates m_docs_search_docs; a server that had the table in the core (schema 7) gets it renamed, so the
//            index stays as it was (no re-indexing)
declare(strict_types=1);

require_once __DIR__ . '/docs-lib.php';

function co_docs_table_exists(mysqli $db, string $t): bool
{
    $st = $db->prepare('SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?');
    $st->bind_param('s', $t);
    $st->execute();
    $n = (int)($st->get_result()->fetch_row()[0] ?? 0);
    $st->close();
    return $n > 0;
}

/** Table of the module: the pre-F10 core table renamed, else created. Additive only. */
function co_docs_migrate(mysqli $db): array
{
    $renamed = false;
    if (co_docs_table_exists($db, 'docs') && !co_docs_table_exists($db, 'm_docs_search_docs')) {
        $db->query('RENAME TABLE `docs` TO `m_docs_search_docs`');
        $renamed = true;
    }
    $db->query("CREATE TABLE IF NOT EXISTS m_docs_search_docs (
      doc_key     CHAR(40)      NOT NULL,
      chunk       INT           NOT NULL,
      project_id  VARCHAR(100)  NOT NULL,
      path        VARCHAR(1000) NOT NULL,
      name        VARCHAR(255)  NOT NULL,
      mtime       BIGINT        NOT NULL,
      size        BIGINT        NOT NULL,
      indexed_utc DATETIME(3)   NOT NULL,
      text        MEDIUMTEXT    NOT NULL,
      PRIMARY KEY (doc_key, chunk),
      KEY ix_docs_project (project_id),
      FULLTEXT KEY ft_docs (name, text)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
    return ['renamed' => $renamed ? ['docs'] : []];
}

return [
    'host' => [
        'docs_state'  => static fn(array $c, array $in): array => co_docs_state($c['db']),
        'docs_put'    => static fn(array $c, array $in): array => co_docs_put($c['db'], $in),
        'docs_delete' => static fn(array $c, array $in): array => co_docs_delete($c['db'], $in),
        'docs_search' => static fn(array $c, array $in): array => co_docs_search($c['db'], $in, is_string($in['env'] ?? null) ? $in['env'] : null),
    ],
    'user' => [
        'search_docs' => static function (array $c, array $in): array {
            $env = $in['env'] ?? null;
            if (!is_string($env) || $env === '' || (!$c['owner'] && $env !== $c['myEnv'])) {
                throw new InvalidArgumentException('forbidden');
            }
            return co_docs_search($c['db'], $in, $env);
        },
    ],
    'search' => [
        'docs' => [
            'items' => static fn(array $c, array $in): array => co_docs_items($c['db'], $in),
            'text'  => static fn(array $c, array $in): string => co_docs_text($c['db'], $in),
        ],
    ],
    'install' => static fn(array $c, array $info): array => co_docs_migrate($c['db']),
    // remove with data erase: the table goes too (keep: it stays for a later install)
    'remove' => static function (array $c, array $info): void {
        if (($info['data'] ?? 'keep') === 'erase') $c['db']->query('DROP TABLE IF EXISTS `m_docs_search_docs`');
    },
];
