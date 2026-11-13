<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// docs-lib.php of the module docs-search (Freedom F10; until CO Web 0.102 part of the core's db.php, schema 7,
// 2026-10-16): text of project documents (docx, pdf, xlsx, pptx, md, txt, ...) in chunks, written by the aiwb-docs tool
// on the home PC (host ops docs_state / docs_put / docs_delete), searched by CO Web (ext/1 search.provider hook 'items',
// legacy user op search_docs) and agents (host op docs_search). doc_key = sha1 of the path relative to
// AI-Workbench/projects. Table m_docs_search_docs (install hook: the pre-F10 core table docs is renamed).
declare(strict_types=1);

function co_docs_key(mixed $k): string
{
    if (!is_string($k) || !preg_match('/^[0-9a-f]{40}$/', $k)) {
        throw new InvalidArgumentException('bad doc key');
    }
    return $k;
}

function co_docs_state(mysqli $db): array
{
    $docs = [];
    $res = $db->query('SELECT doc_key, MAX(mtime), MAX(size), MAX(path) FROM m_docs_search_docs GROUP BY doc_key');
    while ($r = $res->fetch_row()) {
        $docs[$r[0]] = [(int)$r[1], (int)$r[2], (string)$r[3]];
    }
    return ['docs' => $docs];
}

function co_docs_put(mysqli $db, array $in): array
{
    $list = $in['docs'] ?? null;
    if (!is_array($list) || !array_is_list($list) || count($list) > 200) {
        throw new InvalidArgumentException('docs must be a list of up to 200');
    }
    $n = 0;
    foreach ($list as $d) {
        $key = co_docs_key($d['key'] ?? null);
        $chunks = $d['chunks'] ?? null;
        $path = (string)($d['path'] ?? '');
        $project = (string)($d['projectId'] ?? '');
        if (!is_array($chunks) || count($chunks) > 400 || $path === '' || mb_strlen($path) > 1000 || $project === '') {
            throw new InvalidArgumentException('bad doc ' . $key);
        }
        $db->begin_transaction();
        $db->prepare('DELETE FROM m_docs_search_docs WHERE doc_key = ?')->execute([$key]);
        $st = $db->prepare('INSERT INTO m_docs_search_docs (doc_key, chunk, project_id, path, name, mtime, size, indexed_utc, text)
                            VALUES (?, ?, ?, ?, ?, ?, ?, UTC_TIMESTAMP(3), ?)');
        foreach (array_values($chunks) as $i => $text) {
            // An empty document still gets one row: its state (mtime, size) must be known.
            $st->execute([$key, $i, mb_substr($project, 0, 100), $path, mb_substr((string)($d['name'] ?? ''), 0, 255),
                (int)($d['mtime'] ?? 0), (int)($d['size'] ?? 0), mb_substr((string)$text, 0, 60000)]);
        }
        $db->commit();
        $n++;
    }
    return ['put' => $n];
}

function co_docs_delete(mysqli $db, array $in): array
{
    $keys = $in['keys'] ?? null;
    if (!is_array($keys) || !array_is_list($keys) || count($keys) > 1000) {
        throw new InvalidArgumentException('keys must be a list of up to 1000');
    }
    $st = $db->prepare('DELETE FROM m_docs_search_docs WHERE doc_key = ?');
    $n = 0;
    foreach ($keys as $k) {
        $st->execute([co_docs_key($k)]);
        $n += $st->affected_rows;
    }
    return ['deleted' => $n];
}

/** Best documents for a query: one row per document with up to 2 snippets. $env null = all (host). */
function co_docs_search(mysqli $db, array $in, ?string $env): array
{
    $q = is_string($in['q'] ?? null) ? trim($in['q']) : '';
    $qLen = mb_strlen($q);
    if ($qLen < 2 || $qLen > 300) {
        throw new InvalidArgumentException('q must be 2..300 characters');
    }
    $limit = max(1, min(100, (int)($in['limit'] ?? 20)));
    $project = is_string($in['projectId'] ?? null) && $in['projectId'] !== '' ? $in['projectId'] : null;
    $ftq = co_fts_query($q);
    if ($ftq === '') {
        return ['docs' => []];
    }
    $sql = 'SELECT d.doc_key, d.project_id, p.title AS ptitle, d.path, d.name, d.mtime, d.text,
                   ' . co_fts_score_sql('MATCH(d.name, d.text) AGAINST(? IN BOOLEAN MODE)', 'd.text') . ' AS score
              FROM m_docs_search_docs d JOIN projects p ON p.project_id = d.project_id
             WHERE MATCH(d.name, d.text) AGAINST(? IN BOOLEAN MODE)'
        . ($env !== null ? ' AND p.env = ?' : '') . ($project !== null ? ' AND d.project_id = ?' : '')
        . ' ORDER BY score DESC LIMIT 500';
    $args = [$ftq, $ftq];
    if ($env !== null) {
        $args[] = $env;
    }
    if ($project !== null) {
        $args[] = $project;
    }
    $st = $db->prepare($sql);
    $st->execute($args);
    $res = $st->get_result();
    $docs = [];
    while ($r = $res->fetch_assoc()) {
        $k = $r['doc_key'];
        if (!isset($docs[$k])) {
            $docs[$k] = ['projectId' => $r['project_id'], 'projectTitle' => (string)$r['ptitle'], 'path' => $r['path'],
                'name' => $r['name'], 'modified' => gmdate('Y-m-d\TH:i:s\Z', (int)$r['mtime']), 'score' => 0.0,
                'snippets' => []];
        }
        $docs[$k]['score'] += (float)$r['score'];
        if (count($docs[$k]['snippets']) < 2) {
            $docs[$k]['snippets'][] = db_snippet((string)$r['text'], $q, 220);
        }
    }
    $res->free();
    $docs = array_values($docs);
    usort($docs, static fn(array $a, array $b): int => $b['score'] <=> $a['score']);
    return ['docs' => array_slice($docs, 0, $limit)];
}

/** Search provider hook 'items' (ext/1 search.provider): the best documents as items of the core's AI search. */
function co_docs_items(mysqli $db, array $in): array
{
    $q = (string)($in['q'] ?? '');
    if (mb_strlen(trim($q), 'UTF-8') < 2) {
        return [];
    }
    $args = ['q' => $q, 'limit' => (int)($in['limit'] ?? 8)];
    if (is_string($in['projectId'] ?? null) && $in['projectId'] !== '') {
        $args['projectId'] = $in['projectId'];
    }
    $out = [];
    foreach (co_docs_search($db, $args, (string)($in['env'] ?? ''))['docs'] as $d) {
        $out[] = ['kind' => 'doc', 'title' => (string)$d['name'], 'projectId' => (string)$d['projectId'],
            'projectTitle' => (string)$d['projectTitle'], 'path' => (string)$d['path'], 'modified' => (string)$d['modified'],
            'snippets' => $d['snippets']];
    }
    return $out;
}

/** Search provider hook 'text': the indexed text of one document (path relative to projects/), first chunks. */
function co_docs_text(mysqli $db, array $in): string
{
    $path = (string)($in['path'] ?? '');
    if ($path === '') {
        return '';
    }
    $max = max(1, min(200000, (int)($in['max'] ?? 20000)));
    $st = $db->prepare('SELECT text FROM m_docs_search_docs WHERE doc_key = ? ORDER BY chunk LIMIT 2');
    $key = sha1($path);
    $st->bind_param('s', $key);
    $st->execute();
    $text = '';
    foreach ($st->get_result()->fetch_all(MYSQLI_NUM) as [$t]) {
        $text .= $t . "\n";
    }
    $st->close();
    return mb_substr($text, 0, $max, 'UTF-8');
}
