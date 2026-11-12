<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// corporate-lib.php (Freedom F1 step 5b): what the server knows about the corporate connector comes from its manifest
// (connectors/corporate/manifest.json of the repository: id, titles, agents with kind, tools with kind, error pattern),
// never from employer names in the code. Where: config key corporate_manifest of ~/co-agent/config.php, else
// ~/co-agent/connectors/corporate/manifest.json (deployed only where the connector is installed), else the repository
// copy (tests). No manifest = no corporate agents or tools; neutral texts.
// Legacy wire: Bridge and devices still send engieBlocked / engie; both the new and the old names are read.
declare(strict_types=1);

const CO_CORP_NEUTRAL = [
    'id' => 'corporate', 'title' => 'Corporate', 'employerTitle' => 'Corporate', 'portalTitle' => 'the corporate portal',
    'signinTitle' => '', 'paused' => false, 'agents' => [], 'tools' => [], 'errorPattern' => '', 'usageSource' => '',
    'resumeText' => '',
];
// Core part of the "corporate model unavailable" diagnosis; the manifest adds its own messages (errorPattern).
const CO_CORP_CORE_ERRORS = 'corporate call failed|sign_in_needed|device_unreachable';

function co_corporate_manifest_path(): ?string
{
    $cands = [];
    $env = getenv('AIWB_CORPORATE_MANIFEST');
    if (is_string($env) && $env !== '') $cands[] = $env;
    if (function_exists('co_site_config')) {
        $p = co_site_config('corporate_manifest', null);
        if (is_string($p) && $p !== '') $cands[] = $p;
    }
    if (function_exists('co_agent_home')) $cands[] = co_agent_home() . '/co-agent/connectors/corporate/manifest.json';
    $cands[] = dirname(__DIR__, 3) . '/connectors/corporate/manifest.json';
    foreach ($cands as $p) {
        if (is_file($p)) return $p;
    }
    return null;
}

/** The manifest with every field present (neutral defaults); invalid entries are dropped. */
function co_corporate(?array $set = null): array
{
    static $m = null;
    if ($set !== null) {
        $m = co_corporate_normalize($set);
    }
    if ($m === null) {
        $p = co_corporate_manifest_path();
        $j = $p !== null ? json_decode((string)file_get_contents($p), true) : null;
        $m = co_corporate_normalize(is_array($j) ? $j : []);
    }
    return $m;
}

function co_corporate_normalize(array $j): array
{
    $m = CO_CORP_NEUTRAL;
    foreach (['id', 'title', 'employerTitle', 'portalTitle', 'signinTitle', 'errorPattern', 'usageSource', 'resumeText'] as $k) {
        if (is_string($j[$k] ?? null) && $j[$k] !== '') $m[$k] = $j[$k];
    }
    if (!isset($j['employerTitle']) && isset($j['title'])) $m['employerTitle'] = $m['title'];
    $m['paused'] = ($j['paused'] ?? false) === true;
    foreach (is_array($j['agents'] ?? null) ? $j['agents'] : [] as $a) {
        if (is_array($a) && is_string($a['id'] ?? null) && preg_match('/^[a-z][a-z0-9-]{0,15}$/', $a['id'])) {
            $m['agents'][$a['id']] = [
                'id' => $a['id'],
                'kind' => is_string($a['kind'] ?? null) ? $a['kind'] : 'corporate-llm',
                'title' => is_string($a['title'] ?? null) ? $a['title'] : $a['id'],
                'needsDevice' => ($a['needsDevice'] ?? false) === true,
                'defaultModel' => is_string($a['defaultModel'] ?? null) ? $a['defaultModel'] : null,
                'job' => ($a['job'] ?? false) === true,
                'family' => is_string($a['family'] ?? null) ? $a['family'] : '', // CLI it runs on (claude) for diagnosis rules
                'card' => is_array($a['card'] ?? null) ? array_filter($a['card'], 'is_string') : [],
            ];
        }
    }
    foreach (is_array($j['tools'] ?? null) ? $j['tools'] : [] as $t) {
        if (is_array($t) && is_string($t['name'] ?? null) && preg_match('/^[a-z][a-z0-9_]{0,63}$/', $t['name'])) {
            $m['tools'][] = ['name' => $t['name'], 'kind' => is_string($t['kind'] ?? null) ? $t['kind'] : ''];
        }
    }
    if ($m['errorPattern'] !== '' && @preg_match('/' . str_replace('/', '\/', $m['errorPattern']) . '/i', '') === false) {
        $m['errorPattern'] = '';
    }
    return $m;
}

/** Agents of the corporate connector (agent value of jobs and owner.json), e.g. its LLM gateway and its dispatcher. */
function co_corporate_agents(): array
{
    return co_corporate()['agents'];
}

function co_corporate_agent(string $agent): ?array
{
    return co_corporate()['agents'][$agent] ?? null;
}

/** Tool names of the connector, optionally of one kind (corporate-llm, corporate-fetch). */
function co_corporate_tools(?string $kind = null): array
{
    $out = [];
    foreach (co_corporate()['tools'] as $t) {
        if ($kind === null || $t['kind'] === $kind) $out[] = $t['name'];
    }
    return $out;
}

/** Regex (with delimiters) of errors that mean "the corporate model was unreachable". */
function co_corporate_error_re(): string
{
    $p = CO_CORP_CORE_ERRORS;
    $extra = co_corporate()['errorPattern'];
    if ($extra !== '') $p .= '|' . $extra;
    return '/' . str_replace('/', '\/', $p) . '/i';
}

/** Why the job could not reach the corporate model: result/event field corporateBlocked, legacy wire engieBlocked. */
function co_corporate_blocked(array $x): string
{
    foreach (['corporateBlocked', 'engieBlocked'] as $k) {
        if (is_string($x[$k] ?? null) && $x[$k] !== '') return $x[$k];
    }
    return '';
}
