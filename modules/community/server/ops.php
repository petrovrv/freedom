<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module community (Freedom F4-F6 packaged in F10, SPEC §3.6/§3.7). The forum archive stays on the
// home PC; the server only keeps the short view the home PC builds (python -m community page --push) for the owner's
// page ui/forum.html.
//   push: community_page_put - the home PC (push key) puts the view
//   user: community_page     - the owner reads it (members get nothing: the forum is the owner's)
declare(strict_types=1);

const CO_COMMUNITY_PAGE_MAX = 200000;

return [
    'push' => [
        'community_page_put' => static function (array $c, array $in): array {
            $view = $in['view'] ?? null;
            if (!is_array($view) || (int)($view['v'] ?? 0) !== 1) return ['ok' => false, 'error' => 'bad_view'];
            $json = json_encode($view, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            if (!is_string($json) || strlen($json) > CO_COMMUNITY_PAGE_MAX) return ['ok' => false, 'error' => 'too_large'];
            co_push_write_atomic($c['stateDir'] . '/page.json', $json);
            return ['ok' => true, 'bytes' => strlen($json)];
        },
    ],
    'user' => [
        'community_page' => static function (array $c, array $in): array {
            if (!$c['owner']) return ['ok' => false, 'error' => 'owner_only'];
            $raw = @file_get_contents($c['stateDir'] . '/page.json');
            $view = is_string($raw) ? json_decode($raw, true) : null;
            return ['ok' => true, 'view' => is_array($view) ? $view : null];
        },
    ],
];
