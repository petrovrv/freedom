<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// trace-lib.php: what of an agent's raw output (Bridge event batches, job_events) is worth keeping (2026-10-28).
// CO Web's Trace window (activity.ts) reads tool calls, tool results, text, thinking text and the subagent task events;
// everything else in the Claude stream-json / Codex JSONL lines is dropped or shortened before the insert.
declare(strict_types=1);

const CO_TRACE_RESULT_MAX = 4096;   // tool results of the main agent; tool input of subagents
const CO_TRACE_SUB_RESULT_MAX = 2048; // tool results of subagents
const CO_TRACE_INPUT_MAX = 16384;   // strings inside tool_use input (Write content, long commands)
const CO_TRACE_TEXT_MAX = 65536;    // assistant text and thinking text
const CO_TRACE_ANY_MAX = 8192;      // any other string (Codex aggregated_output, unknown events)

// Claude system subtypes that only drive live progress and are never read afterwards.
const CO_TRACE_DROP_SUBTYPES = ['thinking_tokens', 'tool_progress', 'status', 'hook_started', 'hook_response', 'compact_boundary_progress',
    'background_tasks_changed', 'commands_changed'];
// Subagent task events: the fields activity.ts reads.
const CO_TRACE_TASK_KEEP = ['type', 'subtype', 'task_id', 'tool_use_id', 'parent_tool_use_id', 'description', 'subagent_type',
    'is_backgrounded', 'status', 'summary', 'patch'];

function co_trace_cut(string $s, int $max): string
{
    if (strlen($s) <= $max) {
        return $s;
    }
    // The marker fits inside $max, so trimming a trimmed string changes nothing (compact-job-events.php reruns).
    $cut = mb_strcut($s, 0, max(0, $max - 40), 'UTF-8');
    return $cut . "\n…[" . (strlen($s) - strlen($cut)) . ' bytes trimmed]';
}

/** Shortens every string of a decoded JSON value to $max bytes. */
function co_trace_cut_all(mixed $v, int $max): mixed
{
    if (is_string($v)) {
        return co_trace_cut($v, $max);
    }
    if (is_array($v)) {
        foreach ($v as $k => $x) {
            $v[$k] = co_trace_cut_all($x, $max);
        }
    }
    return $v;
}

/** Text of a tool_result content (string or [{type:text,text}], images dropped), shortened. */
function co_trace_result_content(mixed $c, int $max): mixed
{
    if (is_string($c)) {
        return co_trace_cut($c, $max);
    }
    if (!is_array($c)) {
        return $c;
    }
    $out = [];
    $left = $max;
    foreach ($c as $part) {
        if (!is_array($part)) {
            continue;
        }
        if (($part['type'] ?? '') === 'text' && is_string($part['text'] ?? null)) {
            if ($left <= 0) {
                continue;
            }
            $t = co_trace_cut($part['text'], $left);
            $left -= strlen($t);
            $out[] = ['type' => 'text', 'text' => $t];
        } elseif (($part['type'] ?? '') === 'image') {
            $out[] = ['type' => 'text', 'text' => '[image]'];
        } else {
            $out[] = co_trace_cut_all($part, 512);
        }
    }
    return $out;
}

/**
 * One decoded stdout line of an agent; null = drop the line.
 * Claude stream-json: system / assistant / user / result / rate_limit_event / stream_event.
 * Codex JSONL: thread.* / turn.* / item.* (only long strings are shortened).
 */
function co_trace_trim_line(array $ev): ?array
{
    $type = $ev['type'] ?? null;
    if (!is_string($type)) {
        return co_trace_cut_all($ev, CO_TRACE_ANY_MAX);
    }
    if ($type === 'rate_limit_event' || $type === 'stream_event' || $type === 'tool_progress') {
        return null;
    }
    unset($ev['tool_use_result'], $ev['uuid'], $ev['timestamp'], $ev['agent_id']);
    if ($type === 'user' || $type === 'assistant') {
        unset($ev['subagent_type'], $ev['task_description']);
    }
    $sub = !empty($ev['parent_tool_use_id']);
    if ($type !== 'result' && !($type === 'system' && ($ev['subtype'] ?? '') === 'init')) {
        unset($ev['session_id']);
    }
    if ($type === 'system') {
        $st = (string)($ev['subtype'] ?? '');
        if (in_array($st, CO_TRACE_DROP_SUBTYPES, true)) {
            return null;
        }
        if ($st === 'init') {
            $keep = ['type', 'subtype', 'session_id', 'model', 'cwd', 'permissionMode', 'claude_code_version', 'apiKeySource', 'output_style'];
            $ev = array_intersect_key($ev, array_flip($keep));
            return $ev;
        }
        if (str_starts_with($st, 'task_')) {
            $tools = $ev['usage']['tool_uses'] ?? null;
            $ev = array_intersect_key($ev, array_flip(CO_TRACE_TASK_KEEP));
            if (is_int($tools)) {
                $ev['usage'] = ['tool_uses' => $tools];
            }
            return co_trace_cut_all($ev, 2048);
        }
        return co_trace_cut_all($ev, CO_TRACE_ANY_MAX);
    }
    if ($type === 'assistant' || $type === 'user') {
        $msg = $ev['message'] ?? null;
        if (is_array($msg)) {
            // 2026-11-01: id and the token counts stay for job_stats (jobstats-lib.php: output tokens along the timeline).
            $usage = is_array($msg['usage'] ?? null) ? array_intersect_key($msg['usage'], array_flip(['input_tokens', 'cache_read_input_tokens',
                'cache_creation_input_tokens', 'output_tokens'])) : null;
            $msg = array_intersect_key($msg, array_flip(['id', 'role', 'content', 'model', 'stop_reason']));
            if ($type === 'assistant' && $usage) {
                $msg['usage'] = $usage;
            }
            if (is_array($msg['content'] ?? null)) {
                foreach ($msg['content'] as $i => $c) {
                    if (!is_array($c)) {
                        continue;
                    }
                    $t = $c['type'] ?? '';
                    if ($t === 'thinking') {
                        unset($c['signature']);
                        if (is_string($c['thinking'] ?? null)) {
                            $c['thinking'] = co_trace_cut($c['thinking'], CO_TRACE_TEXT_MAX);
                        }
                    } elseif ($t === 'redacted_thinking') {
                        $c = ['type' => 'redacted_thinking'];
                    } elseif ($t === 'text') {
                        if (is_string($c['text'] ?? null)) {
                            $c['text'] = co_trace_cut($c['text'], CO_TRACE_TEXT_MAX);
                        }
                    } elseif ($t === 'tool_use') {
                        $c['input'] = co_trace_cut_all($c['input'] ?? null, $sub ? CO_TRACE_RESULT_MAX : CO_TRACE_INPUT_MAX);
                    } elseif ($t === 'tool_result') {
                        $c['content'] = co_trace_result_content($c['content'] ?? null, $sub ? CO_TRACE_SUB_RESULT_MAX : CO_TRACE_RESULT_MAX);
                    } elseif ($t === 'image') {
                        $c = ['type' => 'text', 'text' => '[image]'];
                    } else {
                        $c = co_trace_cut_all($c, CO_TRACE_ANY_MAX);
                    }
                    $msg['content'][$i] = $c;
                }
            } elseif (is_string($msg['content'] ?? null)) {
                $msg['content'] = co_trace_cut($msg['content'], CO_TRACE_TEXT_MAX);
            }
            $ev['message'] = $msg;
        }
        return $ev;
    }
    if ($type === 'result') {
        // The final line: keep it, minus the per-model usage breakdown and long denial lists.
        unset($ev['modelUsage'], $ev['permission_denials']);
        if (is_string($ev['result'] ?? null)) {
            $ev['result'] = co_trace_cut($ev['result'], CO_TRACE_TEXT_MAX);
        }
        return co_trace_cut_all($ev, CO_TRACE_TEXT_MAX);
    }
    return co_trace_cut_all($ev, CO_TRACE_ANY_MAX);
}

/** An EventBatch as posted by Bridge / the site agent, trimmed. Bridge and stderr events keep their text (shortened). */
function co_trace_trim_batch(array $batch): array
{
    $out = [];
    foreach ($batch['events'] ?? [] as $e) {
        if (!is_array($e)) {
            continue;
        }
        $content = $e['content'] ?? null;
        if (($e['source'] ?? '') === 'agent' && ($e['type'] ?? '') === 'stdout' && is_string($content)) {
            $line = json_decode($content, true);
            if (is_array($line)) {
                $line = co_trace_trim_line($line);
                if ($line === null) {
                    continue;
                }
                $e['content'] = json_encode($line, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
            } else {
                $e['content'] = co_trace_cut($content, CO_TRACE_ANY_MAX);
            }
        } elseif (is_string($content)) {
            $e['content'] = co_trace_cut($content, CO_TRACE_ANY_MAX);
        }
        $out[] = $e;
    }
    $batch['events'] = $out;
    return $batch;
}
