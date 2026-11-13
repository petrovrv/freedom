<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// gateway-lib.php (Freedom F10, SPEC §3.6 ext/1 point `models` + `device.tools`): a model gateway = a confidential model
// reached through a device tool, plus optional agents that run on it (an LLM agent, a dispatcher that delegates to it).
// The core knows no gateway by name: the description comes from installed modules (table `modules`, co_modules()) whose
// manifest has provides.gateway; provides.models gives the agents, provides.deviceTools the tools, server.gatewayOps the
// user ops the module serves for CO Web (resume, waiting). Installed but disabled = paused: the agents stay known (old
// chats and model resources keep their names and stay confidential), the module's ops and tools are off.
// db.php op modules_list sends every description to CO Web (`gateways`); nothing secret is in it.
// Every legacy wire token of the core lives in this file (constants below): names that deployed Bridges, devices, CO Web
// builds and old database rows still send; they are read next to the new generic ones.
// No includes at load time (failures-lib.php runs without the site's libs in tests); modules-lib.php is loaded when the
// table is read.
declare(strict_types=1);

// Model kind of gateway resources; the legacy wire kind (older rows of model_resources, registrations of older devices,
// owner.json agent flag of Bridge before F10) is read as the same and written as the new one
const CO_GATEWAY_KIND = 'gateway';
const CO_GATEWAY_LEGACY_KIND = 'corporate';
// Result/event fields "the job could not reach the gateway model": the new name first, then the legacy wire names
const CO_GATEWAY_BLOCKED_FIELDS = ['gatewayBlocked', 'corporateBlocked', 'engieBlocked'];
// Core part of the "gateway model unavailable" diagnosis; each gateway adds its own messages (errorPattern).
// The third message is the one of a Bridge before F10 without a manifest (legacy wire).
const CO_GATEWAY_CORE_ERRORS = 'gateway call failed|employer call failed|corporate call failed|sign_in_needed|device_unreachable';
// Error of a job or subtask a shared (restricted) chat may not run (share-lib.php SHARE_REFUSED; CO Web reads it)
const CO_GATEWAY_SHARE_REFUSED = 'shared_chat_no_corporate';
// Template flags "use the gateway" stored in templates rows (db.php templates_put; CO Web)
const CO_GATEWAY_TEMPLATE_FLAGS = ['useCorporate', 'useEngie'];
// Bridge event types of a gateway model call (autoswitch-lib.php): the new one and the legacy wire names
const CO_GATEWAY_EVENT_TYPES = ['gateway', 'corporate', 'engie'];
const CO_GATEWAY_NEUTRAL = [
    'module' => '', 'enabled' => false, 'paused' => true,
    'title' => 'Employer', 'employerTitle' => 'Employer', 'portalTitle' => 'the employer portal', 'signinTitle' => '',
    'domains' => [], 'errorPattern' => '', 'usageSource' => '', 'resumeText' => '', 'blockedMarkers' => [],
    'agents' => [], 'tools' => [], 'ops' => [],
];
const CO_GATEWAY_AGENT_KINDS = ['llm' => 'gateway-llm', 'dispatcher' => 'gateway-dispatcher'];
const CO_GATEWAY_TOOL_KINDS = ['llm' => 'gateway-llm', 'fetch' => 'gateway-fetch'];
const CO_GATEWAY_OP_ROLES = ['resume', 'waiting'];

/**
 * Normalized description of one installed module with provides.gateway (null when it has none). Same shape in PHP,
 * CO Web and Bridge; agents keyed by id here (co_gateway_wire() sends them as a list).
 */
function co_gateway_describe(array $m): ?array
{
    $man = is_array($m['manifest'] ?? null) ? $m['manifest'] : [];
    $p = is_array($man['provides'] ?? null) ? $man['provides'] : [];
    $g = $p['gateway'] ?? null;
    if (!is_array($g)) return null;
    $d = CO_GATEWAY_NEUTRAL;
    $d['module'] = (string)($m['id'] ?? ($man['id'] ?? ''));
    $d['enabled'] = ($m['enabled'] ?? false) === true;
    $d['paused'] = !$d['enabled'];
    foreach (['title', 'employerTitle', 'portalTitle', 'signinTitle', 'errorPattern', 'usageSource', 'resumeText'] as $k) {
        if (is_string($g[$k] ?? null) && $g[$k] !== '') $d[$k] = $g[$k];
    }
    if (!isset($g['employerTitle']) && is_string($g['title'] ?? null) && $g['title'] !== '') $d['employerTitle'] = $d['title'];
    foreach (['domains', 'blockedMarkers'] as $k) {
        $d[$k] = array_values(array_filter(is_array($g[$k] ?? null) ? $g[$k] : [], static fn($v): bool => is_string($v) && $v !== ''));
    }
    if ($d['errorPattern'] !== '' && @preg_match('/' . str_replace('/', '\/', $d['errorPattern']) . '/i', '') === false) {
        $d['errorPattern'] = '';
    }
    foreach (is_array($p['models'] ?? null) ? $p['models'] : [] as $a) {
        if (!is_array($a) || !is_string($a['id'] ?? null) || !preg_match('/^[a-z][a-z0-9-]{0,15}$/', $a['id'])) continue;
        $kind = is_string($a['kind'] ?? null) ? $a['kind'] : 'llm';
        $d['agents'][$a['id']] = [
            'id' => $a['id'],
            'kind' => CO_GATEWAY_AGENT_KINDS[$kind] ?? CO_GATEWAY_AGENT_KINDS['llm'],
            'title' => is_string($a['title'] ?? null) ? $a['title'] : $a['id'],
            'family' => is_string($a['family'] ?? null) ? $a['family'] : '', // CLI it runs on (claude) for diagnosis rules
            'needsDevice' => ($a['needsDevice'] ?? false) === true,
            'defaultModel' => is_string($a['defaultModel'] ?? null) ? $a['defaultModel'] : null,
            'job' => ($a['job'] ?? false) === true,
            'card' => is_array($a['card'] ?? null) ? array_filter($a['card'], 'is_string') : [],
        ];
    }
    foreach (is_array($p['deviceTools'] ?? null) ? $p['deviceTools'] : [] as $t) {
        if (!is_array($t) || !is_string($t['name'] ?? null) || !preg_match('/^[a-z][a-z0-9_]{0,63}$/', $t['name'])) continue;
        $d['tools'][] = ['name' => $t['name'], 'kind' => CO_GATEWAY_TOOL_KINDS[(string)($t['kind'] ?? '')] ?? ''];
    }
    // ops only while enabled and standard-signed (only then the core loads its server code)
    if ($d['enabled'] && ($m['standard'] ?? false) === true) {
        $declared = $man['server']['ops']['user'] ?? [];
        $declared = is_array($declared) ? $declared : [];
        $roles = $man['server']['gatewayOps'] ?? [];
        foreach (CO_GATEWAY_OP_ROLES as $role) {
            $op = is_array($roles) ? ($roles[$role] ?? null) : null;
            if (is_string($op) && preg_match('/^[a-z][a-z0-9_]{2,40}$/', $op) && in_array($op, $declared, true)) $d['ops'][$role] = $op;
        }
    }
    return $d;
}

/**
 * Descriptions of every installed module with provides.gateway (enabled or not), ordered by module id. Cached per
 * request; $set replaces them (tests, or a caller that already has fresh rows), $fresh reads the table again.
 * No schema 27 = no gateways; a database error gives none for this call only (not cached).
 */
function co_gateways(?array $set = null, bool $fresh = false): array
{
    static $list = null;
    if ($set !== null) {
        $list = [];
        foreach ($set as $m) {
            $d = is_array($m) ? co_gateway_describe($m) : null;
            if ($d !== null) $list[] = $d;
        }
    }
    if ($list === null || $fresh) {
        $list = [];
        try {
            require_once __DIR__ . '/modules-lib.php';
            foreach (co_modules(co_db(), $fresh) as $m) {
                $d = co_gateway_describe($m);
                if ($d !== null) $list[] = $d;
            }
        } catch (Throwable) {
            $list = null; // not cached: a transient error must not hide the gateway agents for the rest of the request
            return [];
        }
    }
    return $list;
}

/** One description: of module $module, else the first installed, else the neutral one. */
function co_gateway(?string $module = null): array
{
    foreach (co_gateways() as $d) {
        if ($module === null || $d['module'] === $module) return $d;
    }
    return CO_GATEWAY_NEUTRAL;
}

/** For modules_list: the descriptions with agents as a list. */
function co_gateway_wire(): array
{
    return array_map(static fn(array $d): array => array_merge($d, ['agents' => array_values($d['agents']), 'ops' => (object)$d['ops']]),
        co_gateways());
}

/** Agents of all gateways by id (agent value of jobs and owner.json); the first module wins on a clash. */
function co_gateway_agents(): array
{
    $out = [];
    foreach (co_gateways() as $d) $out += $d['agents'];
    return $out;
}

function co_gateway_agent(string $agent): ?array
{
    return co_gateway_agents()[$agent] ?? null;
}

/** Tool names of all gateways, optionally of one kind (gateway-llm, gateway-fetch; '' = tools without a kind). */
function co_gateway_tools(?string $kind = null, ?string $module = null): array
{
    $out = [];
    foreach (co_gateways() as $d) {
        if ($module !== null && $d['module'] !== $module) continue;
        foreach ($d['tools'] as $t) {
            if ($kind === null || $t['kind'] === $kind) $out[] = $t['name'];
        }
    }
    return array_values(array_unique($out));
}

/** Regex (with delimiters) of errors that mean "the gateway model was unreachable". */
function co_gateway_error_re(): string
{
    $p = CO_GATEWAY_CORE_ERRORS;
    foreach (co_gateways() as $d) {
        if ($d['errorPattern'] !== '') $p .= '|' . $d['errorPattern'];
    }
    return '/' . str_replace('/', '\/', $p) . '/i';
}

/** The model kind as written today: the legacy gateway kind becomes CO_GATEWAY_KIND. */
function co_gateway_kind(string $kind): string
{
    return $kind === CO_GATEWAY_LEGACY_KIND ? CO_GATEWAY_KIND : $kind;
}

/** Why the job could not reach the gateway model: gatewayBlocked, or the legacy wire corporateBlocked / engieBlocked. */
function co_gateway_blocked(array $x): string
{
    foreach (CO_GATEWAY_BLOCKED_FIELDS as $k) {
        if (is_string($x[$k] ?? null) && $x[$k] !== '') return $x[$k];
    }
    return '';
}
