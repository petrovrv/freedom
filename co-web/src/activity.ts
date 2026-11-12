// SPDX-License-Identifier: AGPL-3.0-or-later
// Normalizes Bridge events and provider JSONL (Codex exec --json, Claude stream-json)
// into activity cards (TZ section 13, "Activity rendering").

import type { BridgeEvent } from './types';
import { corporate } from './corporate';

export type ActivityKind = 'bridge' | 'command' | 'read' | 'write' | 'search' | 'tool' | 'reasoning' | 'error' | 'other';

export interface ActivityItem {
  key: string;
  kind: ActivityKind;
  label: string;
  detail?: string;
  status?: 'running' | 'ok' | 'error';
  depth?: number; // 1 = step of a subagent
  background?: boolean; // backgrounded subagent, finished by task_notification
  raw: string[];
}

export interface ActivityState {
  items: ActivityItem[];
  draft: string[]; // assistant text seen so far (live)
  providerSessionId?: string;
  lastSequence: number;
  tasks?: Record<string, string>; // subagent task_id -> Agent tool_use_id
}

export function emptyActivity(): ActivityState {
  return { items: [], draft: [], lastSequence: 0 };
}

const BRIDGE_LABELS: Record<string, string> = {
  claimed: 'Job claimed by home agent',
  started: 'Agent started',
  attachment_staged: 'Attachment staged',
  artifact_exported: 'Artifact exported',
  artifact_missing: 'Declared output file not found',
  cancel_requested: 'Cancellation requested',
  cancelled: 'Cancelled',
  completed: 'Completed',
  failed: 'Failed',
  timeout: 'Timed out',
  policy_warning: `${corporate().title} policy: content written without the ${corporate().title} model`,
};

function short(s: string, n = 160): string {
  const t = s.replace(/\s+/g, ' ').trim();
  return t.length > n ? t.slice(0, n - 1) + '…' : t;
}

function upsert(st: ActivityState, item: ActivityItem) {
  const i = st.items.findIndex((x) => x.key === item.key);
  if (i < 0) st.items.push(item);
  else st.items[i] = { ...st.items[i], ...item, raw: [...st.items[i].raw, ...item.raw] };
}

function codexItem(st: ActivityState, ev: Record<string, any>, raw: string) {
  const it = ev.item || {};
  const done = ev.type === 'item.completed';
  const key = `codex:${it.id ?? Math.random()}`;
  const status: ActivityItem['status'] = done ? (it.status === 'failed' || (it.exit_code ?? 0) !== 0 ? 'error' : 'ok') : 'running';
  switch (it.type) {
    case 'agent_message':
      if (done && it.text) st.draft.push(it.text);
      return;
    case 'reasoning':
      if (done && it.text) upsert(st, { key, kind: 'reasoning', label: 'Thinking', detail: it.text, status: 'ok', raw: [raw] });
      return;
    case 'command_execution':
      upsert(st, {
        key, kind: 'command', label: `Running ${short(String(it.command || ''), 120)}`,
        detail: it.aggregated_output ? String(it.aggregated_output) : undefined, status, raw: [raw],
      });
      return;
    case 'file_change': {
      const paths = (it.changes || []).map((c: any) => `${c.kind || 'edit'} ${c.path}`).join(', ');
      upsert(st, { key, kind: 'write', label: `Editing files: ${short(paths, 140)}`, status, raw: [raw] });
      return;
    }
    case 'web_search':
      upsert(st, { key, kind: 'search', label: `Searching web: ${short(String(it.query || ''))}`, status, raw: [raw] });
      return;
    case 'mcp_tool_call':
      upsert(st, { key, kind: 'tool', label: `Tool ${it.server || ''}.${it.tool || ''}`, status, raw: [raw] });
      return;
    case 'todo_list':
      upsert(st, {
        key, kind: 'other', label: 'Plan updated',
        detail: (it.items || []).map((t: any) => `${t.completed ? '[x]' : '[ ]'} ${t.text}`).join('\n'), status: 'ok', raw: [raw],
      });
      return;
    case 'error':
      upsert(st, { key, kind: 'error', label: short(String(it.message || 'Error')), status: 'error', raw: [raw] });
      return;
    default:
      upsert(st, { key, kind: 'other', label: `${it.type || 'item'}`, status, raw: [raw] });
  }
}

function claudeToolLabel(name: string, input: Record<string, any>): { kind: ActivityKind; label: string } {
  const p = input?.file_path || input?.path || input?.notebook_path || '';
  switch (name) {
    case 'Read': return { kind: 'read', label: `Reading ${p}` };
    case 'Write': return { kind: 'write', label: `Writing ${p}` };
    case 'Edit': case 'MultiEdit': case 'NotebookEdit': return { kind: 'write', label: `Editing ${p}` };
    case 'Bash': case 'PowerShell': return { kind: 'command', label: `Running ${short(String(input?.command || ''), 120)}` };
    case 'Grep': case 'Glob': return { kind: 'search', label: `Searching files: ${short(String(input?.pattern || ''))}` };
    case 'WebSearch': return { kind: 'search', label: `Searching web: ${short(String(input?.query || ''))}` };
    case 'WebFetch': return { kind: 'search', label: `Fetching ${short(String(input?.url || ''))}` };
    case 'Task': case 'Agent': return { kind: 'tool', label: `Agent delegated task: ${short(String(input?.description || ''))}` };
    default: return { kind: 'tool', label: `Tool ${name}` };
  }
}

const TASK_DONE = ['completed', 'failed', 'killed', 'stopped'];

// Resolves the Agent tool_use id of a subagent task event and remembers the mapping.
function claudeTaskId(st: ActivityState, ev: Record<string, any>): string | undefined {
  if (!st.tasks) st.tasks = {};
  if (ev.task_id && ev.tool_use_id) st.tasks[ev.task_id] = ev.tool_use_id;
  return ev.tool_use_id || (ev.task_id ? st.tasks[ev.task_id] : undefined);
}

// Finishes the parent item of a subagent.
function claudeTaskEnd(st: ActivityState, id: string, ok: boolean, detail: string | undefined, raw: string) {
  const key = `claude:${id}`;
  const status = ok ? 'ok' : 'error';
  const it = st.items.find((x) => x.key === key);
  if (it) {
    it.status = status;
    if (detail) it.detail = detail;
    it.raw = [...it.raw, raw];
  } else upsert(st, { key, kind: 'tool', label: 'Subagent', status, detail, raw: [raw] });
}

function claudeLine(st: ActivityState, ev: Record<string, any>, raw: string) {
  // Subagent events carry the id of their parent Agent tool_use.
  const parent: string | undefined = ev.parent_tool_use_id || undefined;
  const sub = parent ? { depth: 1 } : {};

  if (ev.type === 'system') {
    if (ev.session_id) st.providerSessionId = ev.session_id;
    if (ev.subtype === 'task_started') {
      const id = claudeTaskId(st, ev);
      if (!id) return;
      const bg = !!ev.is_backgrounded;
      const label = `Subagent (${ev.subagent_type || 'agent'}): ${short(String(ev.description || ''), 120)}${bg ? ' · background' : ''}`;
      upsert(st, { key: `claude:${id}`, kind: 'tool', label, status: 'running', background: bg, raw: [raw] });
    } else if (ev.subtype === 'task_progress') {
      const id = claudeTaskId(st, ev);
      if (!id) return;
      const key = `claude:${id}`;
      const it = st.items.find((x) => x.key === key);
      // Replace the previous progress suffix.
      const base = it ? it.label.split(' — ')[0].replace(/ \(\d+ tools\)$/, '') : 'Subagent';
      let label = base;
      if (ev.description) label += ` — ${short(String(ev.description), 80)}`;
      if (ev.usage?.tool_uses != null) label += ` (${ev.usage.tool_uses} tools)`;
      if (it) {
        it.label = label;
        it.raw = [...it.raw, raw];
      } else upsert(st, { key, kind: 'tool', label, status: 'running', raw: [raw] });
    } else if (ev.subtype === 'task_updated') {
      const s = ev.patch?.status;
      if (!TASK_DONE.includes(s)) return;
      const id = claudeTaskId(st, ev);
      if (id) claudeTaskEnd(st, id, s === 'completed', undefined, raw);
    } else if (ev.subtype === 'task_notification') {
      const id = claudeTaskId(st, ev);
      if (id) claudeTaskEnd(st, id, ev.status === 'completed', ev.summary ? String(ev.summary) : undefined, raw);
    }
  } else if (ev.type === 'assistant') {
    for (const c of ev.message?.content || []) {
      if (c.type === 'text' && c.text) {
        if (!parent) st.draft.push(c.text);
        else upsert(st, { key: `claude:subtext:${parent}:${st.items.length}`, kind: 'other', label: short(c.text), detail: c.text, status: 'ok', depth: 1, raw: [raw] });
      } else if (c.type === 'thinking' && c.thinking)
        upsert(st, { key: `claude:think:${st.items.length}`, kind: 'reasoning', label: 'Thinking', detail: c.thinking, status: 'ok', ...sub, raw: [raw] });
      else if (c.type === 'tool_use') {
        const { kind, label } = claudeToolLabel(c.name, c.input || {});
        upsert(st, { key: `claude:${c.id}`, kind, label, status: 'running', detail: undefined, ...sub, raw: [raw] });
      }
    }
  } else if (ev.type === 'user') {
    // The subagent's prompt text is skipped; only tool results matter.
    for (const c of ev.message?.content || []) {
      if (c.type === 'tool_result') {
        const text = Array.isArray(c.content) ? c.content.map((x: any) => x.text || '').join('\n') : String(c.content ?? '');
        const status = c.is_error ? 'error' : 'ok';
        const it = st.items.find((x) => x.key === `claude:${c.tool_use_id}`);
        if (it) {
          // A background subagent is finished later by task_notification / task_updated.
          if (!(it.background && it.status === 'running')) it.status = status;
          it.detail = text;
          it.raw = [...it.raw, raw];
        } else upsert(st, { key: `claude:${c.tool_use_id}`, kind: 'tool', label: 'Tool result', status, detail: text, ...sub, raw: [raw] });
      }
    }
  } else if (ev.type === 'result') {
    if (ev.session_id) st.providerSessionId = ev.session_id;
    if (ev.is_error) upsert(st, { key: 'claude:result', kind: 'error', label: short(String(ev.result || 'Agent error')), status: 'error', raw: [raw] });
  }
}

export function applyBridgeEvents(st: ActivityState, events: BridgeEvent[]): ActivityState {
  const next: ActivityState = { ...st, items: st.items.map((i) => ({ ...i })), draft: [...st.draft], tasks: { ...(st.tasks || {}) } };
  for (const e of events) {
    if (e.sequence <= next.lastSequence) continue;
    next.lastSequence = e.sequence;
    if (e.source === 'agent' && e.type === 'stdout') {
      let ev: Record<string, any>;
      try {
        ev = JSON.parse(e.content);
      } catch {
        upsert(next, { key: `out:${e.sequence}`, kind: 'other', label: short(e.content), raw: [e.content] });
        continue;
      }
      if (typeof ev.type === 'string' && ev.type.startsWith('item.')) codexItem(next, ev, e.content);
      else if (ev.type === 'thread.started') next.providerSessionId = ev.thread_id;
      else if (ev.type === 'turn.failed' || ev.type === 'error')
        upsert(next, { key: `err:${e.sequence}`, kind: 'error', label: short(String(ev.error?.message || ev.message || 'Error')), status: 'error', raw: [e.content] });
      else if (ev.type === 'turn.started' || ev.type === 'turn.completed') continue;
      else claudeLine(next, ev, e.content);
      continue;
    }
    if (e.source === 'agent' && e.type === 'stderr') {
      if (e.content.trim()) upsert(next, { key: `stderr:${e.sequence}`, kind: 'other', label: `stderr: ${short(e.content)}`, raw: [e.content] });
      continue;
    }
    const label = BRIDGE_LABELS[e.type] || e.type;
    const detail = e.content && e.content !== label ? e.content : undefined;
    const kind: ActivityKind = /fail|error|missing|reject|warning/.test(e.type) ? 'error' : 'bridge';
    upsert(next, { key: `bridge:${e.sequence}`, kind, label, detail, status: kind === 'error' ? 'error' : 'ok', raw: [JSON.stringify(e)] });
  }
  return next;
}
