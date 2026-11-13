<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server entry of the module canvas (Freedom F10, SPEC §3.6): the handlers modules-lib.php calls. The code itself is
// canvas-lib.php of the core (CO Web 0.63.0-0.68.0), moved here almost unchanged (SPEC: transitional reuse).
//   user: canvas_ticket, canvas_share_draft - the module pages ui/editor.html (/canvas) and ui/share.html (/canvas-share)
//         through the ext/1 bridge of CO Web. A module op always answers HTTP 200, so the errors the core used to
//         send as 400/403/404/409/401/502 are in the body ({error: forbidden|not_found|need_gmail|busy|uncertain|...})
//         and the pages read them there.
//   host: canvas_job, canvas_job_get, canvas_share_put - canvas-bridge on the home PC (host key aiwb/co-push, gz body),
//         same wire format as before; a bad request is {error: <message>} (was HTTP 400 with the same body).
// No install hook: the data of the module (shared files, ~/co-agent/state/canvas-share, one hour) and the ticket key
// (~/co-agent/secrets/canvas-ticket-key) stay where the core kept them.
declare(strict_types=1);

require_once __DIR__ . '/canvas-lib.php';

$coCanvasSafe = static function (callable $f): array {
    try {
        return $f();
    } catch (InvalidArgumentException $e) {
        return ['error' => $e->getMessage()];
    }
};
$coCanvasAcc = static fn(array $c): array => ['myEnv' => (string)$c['myEnv'], 'admin' => (bool)$c['owner'],
    'email' => (string)$c['email'], 'config' => is_array($c['config'] ?? null) ? $c['config'] : []];

return [
    'user' => [
        'canvas_ticket' => static fn(array $c, array $in): array => $coCanvasSafe(static fn(): array => co_canvas_ticket($c['db'], $in, $coCanvasAcc($c))),
        'canvas_share_draft' => static fn(array $c, array $in): array => $coCanvasSafe(static fn(): array => co_canvas_share_draft($in, $coCanvasAcc($c))),
    ],
    'push' => [],
    'host' => [
        'canvas_job' => static fn(array $c, array $in): array => $coCanvasSafe(static fn(): array => co_canvas_job($c['db'], $in)),
        'canvas_job_get' => static fn(array $c, array $in): array => $coCanvasSafe(static fn(): array => co_canvas_job_get($c['db'], $in)),
        'canvas_share_put' => static fn(array $c, array $in): array => $coCanvasSafe(static fn(): array => co_canvas_share_put($in)),
    ],
];
