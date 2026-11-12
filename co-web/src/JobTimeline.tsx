// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.74.0 (schema 15): timeline, tokens and cost of one job and its subtask tree (db.php op job_stats, jobstats-lib.php).
// The server keeps this summary after the raw trace (job_events, 14 days) is gone; the steps for 180 days.
import { useEffect, useState } from 'react';
import { dbJobStats, type JobStats, type JobStep } from './dbapi';

const KIND_COLOR: Record<string, string> = {
  command: '#6b7fd7', read: '#4fa3a5', write: '#d79b3a', search: '#8f6bd7', tool: '#7a8a6b', agent: '#c56bb0',
  bridge: 'var(--bad)', error: 'var(--bad)',
};

export function fmtMs(ms: number | null | undefined): string {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${Math.round(s - m * 60)} s`;
  return `${Math.floor(m / 60)} h ${m % 60} min`;
}

export function fmtTok(n: number | null | undefined): string {
  if (n == null) return '—';
  return n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${Math.round(n / 1e3)}k` : String(n);
}

export function fmtUsd(stats: { costUsd: number | null; apiUsd: number | null }): string {
  if (stats.apiUsd == null) return '';
  const v = stats.apiUsd >= 10 ? stats.apiUsd.toFixed(0) : stats.apiUsd.toFixed(2);
  return stats.costUsd != null ? `$${v} API-equivalent` : `≈$${v} API-equivalent (price table)`;
}

function Summary({ s }: { s: JobStats }) {
  const parts = [
    s.outcome !== 'completed' ? s.outcome : null,
    fmtMs(s.durationMs),
    `${s.toolCalls} tool call${s.toolCalls === 1 ? '' : 's'}${s.toolErrors ? `, ${s.toolErrors} failed` : ''}`,
    s.subagents ? `${s.subagents} subagent${s.subagents === 1 ? '' : 's'}` : null,
    s.inputTokens != null ? `${fmtTok((s.inputTokens || 0) + (s.agent === 'codex' ? 0 : (s.cachedTokens || 0) + (s.cacheWriteTokens || 0)))} in · ${fmtTok(s.outputTokens)} out` : null,
    fmtUsd(s) || null,
  ];
  return <div className="small">{parts.filter(Boolean).join(' · ')}</div>;
}

function Bar({ steps, total }: { steps: JobStep[]; total: number }) {
  const lanes = steps.some((x) => x[5] > 0) ? 2 : 1;
  return (
    <div className="jt-bar" style={{ height: lanes * 12 + 4 }} aria-label="Timeline of tool calls">
      {steps.map((x, i) => {
        const [off, dur, kind, label, err, depth, out] = x;
        const left = Math.min(100, (off / total) * 100);
        const width = Math.max(0.4, ((dur ?? 0) / total) * 100);
        return (
          <span
            key={i}
            className="jt-step"
            style={{ left: `${left}%`, width: `${Math.min(width, 100 - left)}%`, top: 2 + depth * 12, background: err ? 'var(--bad)' : KIND_COLOR[kind] || 'var(--muted)' }}
            title={`${label}\n+${fmtMs(off)} · ${dur == null ? 'no end' : fmtMs(dur)}${err ? ' · failed' : ''}${out != null ? ` · ${fmtTok(out)} out so far` : ''}`}
          />
        );
      })}
    </div>
  );
}

function Tree({ list }: { list: JobStats[] }) {
  return (
    <ul className="jt-tree">
      {list.map((c) => (
        <li key={c.jobId}>
          <span className={c.outcome === 'completed' ? '' : 'error-text'}>{c.outcome === 'completed' ? '✓' : c.outcome === 'cancelled' ? '⊘' : '✗'}</span>{' '}
          <span title={`${c.chatId} · ${c.jobId}`}>{c.title || c.chatId}</span>
          <span className="muted"> · {c.agent}{c.model ? ` ${c.model}` : ''} · {fmtMs(c.durationMs)} · {c.toolCalls} calls · {fmtTok(c.outputTokens)} out{c.apiUsd != null ? ` · ${fmtUsd(c)}` : ''}</span>
          {!!c.children?.length && <Tree list={c.children} />}
        </li>
      ))}
    </ul>
  );
}

function sumTree(list: JobStats[]): { jobs: number; out: number; usd: number } {
  return list.reduce((t, c) => {
    const k = sumTree(c.children || []);
    return { jobs: t.jobs + 1 + k.jobs, out: t.out + (c.outputTokens || 0) + k.out, usd: t.usd + (c.apiUsd || 0) + k.usd };
  }, { jobs: 0, out: 0, usd: 0 });
}

export default function JobTimeline({ jobId }: { jobId: string }) {
  const [s, setS] = useState<JobStats | null | undefined>(undefined);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    dbJobStats(jobId).then((r) => live && setS(r), (e) => live && setErr(String(e?.message || e)));
    return () => {
      live = false;
    };
  }, [jobId]);
  if (err) return <div className="muted tiny">Timeline: {err}</div>;
  if (s === undefined) return <div className="muted tiny">Loading timeline…</div>;
  if (!s) return <div className="muted tiny">No timeline yet (the job summary is built within a minute after the job ends).</div>;
  const steps = s.steps || [];
  const total = Math.max(1, s.durationMs || 0, ...steps.map((x) => x[0] + (x[1] || 0)));
  const tools = Object.entries(s.tools || {}).slice(0, 8);
  const sub = s.children?.length ? sumTree(s.children) : null;
  return (
    <div className="jt">
      <Summary s={s} />
      {steps.length > 0 && <Bar steps={steps} total={total} />}
      {steps.length > 0 && (
        <div className="jt-axis muted tiny"><span>0</span><span>{fmtMs(total)}</span></div>
      )}
      {!steps.length && s.toolCalls > 0 && <div className="muted tiny">Step timeline no longer kept for this job (180 days); counters stay.</div>}
      {tools.length > 0 && (
        <div className="muted tiny">
          {tools.map(([name, [n, e, ms]]) => `${name} ×${n}${e ? ` (${e} failed)` : ''} ${fmtMs(ms)}`).join(' · ')}
        </div>
      )}
      {sub && s.children && (
        <div className="small">
          Subtasks: {sub.jobs} job{sub.jobs === 1 ? '' : 's'} · {fmtTok(sub.out)} out{sub.usd ? ` · ≈$${sub.usd.toFixed(2)}` : ''}
          <Tree list={s.children} />
        </div>
      )}
    </div>
  );
}
