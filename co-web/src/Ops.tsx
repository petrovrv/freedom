// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.39.0: "Jobs" section of the Host tab — outcomes per host and agent, durations, re-runs, daily trend and top errors
// of the job queue (db.php op "ops", owner only).
import { useEffect, useState } from 'react';
import { clearDbFailureLog, dbFailureLog, dbOps, type DbFailure, type OpsReport } from './dbapi';

// Format seconds as s / min / h
function fmtSec(s: number | null): string {
  if (s == null) return '—';
  if (s < 60) return `${s.toFixed(1)} s`;
  if (s < 3600) return `${(s / 60).toFixed(1)} min`;
  return `${(s / 3600).toFixed(1)} h`;
}

// Percentage, '—' when denominator is 0
function pct(a: number, b: number): string {
  return b ? `${Math.round((a / b) * 100)}%` : '—';
}

const tableStyle = { width: '100%', borderCollapse: 'collapse' as const, marginTop: 6 };
const num = { textAlign: 'right' as const };
const left = { textAlign: 'left' as const };
const danger = 'var(--danger, #c33)';

export function OpsSection() {
  const [days, setDays] = useState(7);
  const [tick, setTick] = useState(0);
  const [loading, setLoading] = useState(true);
  const [data, setData] = useState<OpsReport | 'forbidden' | null>(null);

  // Load report on period change or refresh; ignore stale responses
  useEffect(() => {
    let alive = true;
    setLoading(true);
    dbOps(days)
      .then((r) => { if (alive) setData(r); })
      .catch(() => { if (alive) setData(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [days, tick]);

  const renderReport = (r: OpsReport) => {
    const t = r.totals;
    const d = r.durations;
    const rt = r.retries;
    const failHigh = t.jobs > 0 && t.failed / t.jobs > 0.2;
    const max = Math.max(1, ...r.daily.map((x) => x.jobs));
    const h = (v: number) => (v / max) * 60;

    return (
      <>
        <div className="small" style={{ marginTop: 6 }}>
          {t.jobs} jobs · {t.completed} completed ({pct(t.completed, t.jobs)}) ·{' '}
          <span style={failHigh ? { color: danger } : undefined}>
            {t.failed} failed ({pct(t.failed, t.jobs)})
          </span>{' '}
          · {t.cancelled} cancelled
          {t.running > 0 && ` · ${t.running} running`}
          {t.queued > 0 && ` · ${t.queued} queued`}
        </div>

        <div className="small muted" style={{ marginTop: 4 }}>
          Queue → claim p50 {fmtSec(d.waitP50)} / p95 {fmtSec(d.waitP95)} ({d.waitN}) · Run p50{' '}
          {fmtSec(d.runP50)} / p95 {fmtSec(d.runP95)} ({d.runN}) · Total p50 {fmtSec(d.totalP50)} / p95{' '}
          {fmtSec(d.totalP95)}
        </div>

        <div className="small" style={{ marginTop: 10, fontWeight: 600 }}>By host and agent</div>
        <table className="usage-table small" style={tableStyle}>
          <thead>
            <tr>
              <th style={left}>Host · Agent</th>
              <th style={num}>Jobs</th>
              <th style={num} title="Completed share of jobs">OK %</th>
              <th style={num}>Failed</th>
              <th style={num}>Cancelled</th>
              <th style={num} title="Run = claim to result, median">Run p50</th>
              <th style={num} title="Run = claim to result, 95th percentile">Run p95</th>
            </tr>
          </thead>
          <tbody>
            {r.groups.map((g) => (
              <tr key={`${g.hostId}|${g.agent}`}>
                <td>{g.hostId} · {g.agent}</td>
                <td style={num}>{g.jobs}</td>
                <td style={num}>{pct(g.completed, g.jobs)}</td>
                <td style={g.failed ? { ...num, color: danger } : num}>{g.failed}</td>
                <td style={num}>{g.cancelled}</td>
                <td style={num}>{fmtSec(g.runP50)}</td>
                <td style={num}>{fmtSec(g.runP95)}</td>
              </tr>
            ))}
          </tbody>
        </table>

        <div className="small muted" style={{ marginTop: 8 }}>
          Re-runs after a failure (same chat within 30 min): {rt.retried} · Drive fallback: {rt.fallback} ·
          Stuck alerts: {rt.stuckAlerts} · Timeouts: {rt.timeouts} · Rejected: {rt.rejected}
        </div>

        {r.daily.length > 0 && (
          <>
            <div className="small" style={{ marginTop: 10, fontWeight: 600 }}>Daily trend</div>
            <div style={{ display: 'flex', alignItems: 'flex-end', height: 60, gap: 2, marginTop: 6 }}>
              {r.daily.map((x) => {
                const rest = Math.max(0, x.jobs - x.completed - x.failed - x.cancelled);
                return (
                  <div
                    key={x.day}
                    title={`${x.day}: ${x.jobs} jobs, ${x.failed} failed, ${x.cancelled} cancelled`}
                    style={{ flex: 1, minWidth: 2, height: '100%', display: 'flex', flexDirection: 'column-reverse' }}
                  >
                    <div style={{ height: h(x.completed), background: 'var(--ok, #4a4)' }} />
                    <div style={{ height: h(x.failed), background: danger }} />
                    <div style={{ height: h(x.cancelled), background: '#999' }} />
                    <div style={{ height: h(rest), background: '#ccc' }} />
                  </div>
                );
              })}
            </div>
            {r.daily.length > 1 && (
              <div className="small muted" style={{ display: 'flex', justifyContent: 'space-between' }}>
                <span>{r.daily[0].day.slice(5)}</span>
                <span>{r.daily[r.daily.length - 1].day.slice(5)}</span>
              </div>
            )}
          </>
        )}

        <div className="small" style={{ marginTop: 10, fontWeight: 600 }}>Top errors</div>
        {r.errors.length === 0 ? (
          <div className="small muted">No failed jobs.</div>
        ) : (
          <ol className="small" style={{ margin: '4px 0', paddingLeft: 18 }}>
            {r.errors.map((e) => (
              <li key={e.key} title={e.example} style={{ marginBottom: 4 }}>
                {e.count}× {e.key}
                <div className="muted">
                  {e.agents.join(', ')} · {e.hosts.join(', ')}
                  {e.lastUtc && ` · last ${new Date(e.lastUtc).toLocaleString()}`}
                </div>
              </li>
            ))}
          </ol>
        )}

        <div className="small muted" style={{ marginTop: 8 }}>
          Generated {new Date(r.generatedUtc).toLocaleString()}. Counts by queue time (UTC days).
        </div>
      </>
    );
  };

  return (
    <div>
      <div className="small" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
          <option value={1}>24 hours</option>
          <option value={7}>7 days</option>
          <option value={30}>30 days</option>
        </select>
        <button className="btn small" onClick={() => setTick((n) => n + 1)} disabled={loading}>
          Refresh
        </button>
      </div>

      {loading ? (
        <div className="small muted" style={{ marginTop: 6 }}>Loading…</div>
      ) : data === null ? (
        <div className="small muted" style={{ marginTop: 6 }}>Database not reachable.</div>
      ) : data === 'forbidden' ? (
        <div className="small muted" style={{ marginTop: 6 }}>Only the owner sees job statistics.</div>
      ) : (
        renderReport(data)
      )}
      <BrowserDbFailures tick={tick} />
    </div>
  );
}

// 0.106.0: why the database did not answer this browser (kept in this browser only, last 100 failed calls)
function BrowserDbFailures({ tick }: { tick: number }) {
  const [list, setList] = useState<DbFailure[]>(() => dbFailureLog());
  useEffect(() => setList(dbFailureLog()), [tick]);
  const day = list.filter((f) => Date.now() - Date.parse(f.utc) < 86400000);
  const by = new Map<string, number>();
  for (const f of day) {
    const k = f.reason.startsWith('network') ? 'network' : f.reason;
    by.set(k, (by.get(k) || 0) + 1);
  }
  return (
    <>
      <div className="small" style={{ marginTop: 10, fontWeight: 600 }}>Database failures seen by this browser (24 h)</div>
      {day.length === 0 ? (
        <div className="small muted" style={{ marginTop: 4 }}>None.</div>
      ) : (
        <>
          <div className="small" style={{ marginTop: 4 }}>
            {[...by.entries()].sort((a, b) => b[1] - a[1]).map(([k, n]) => `${k}: ${n}`).join(' · ')}{' '}
            <button className="btn small" onClick={() => { clearDbFailureLog(); setList([]); }}>Clear</button>
          </div>
          <ol className="small muted" style={{ marginTop: 4 }}>
            {day.slice(-10).reverse().map((f, i) => (
              <li key={f.utc + i}>
                {new Date(f.utc).toLocaleTimeString()} · {f.op} · {f.reason} · {(f.ms / 1000).toFixed(1)} s
              </li>
            ))}
          </ol>
        </>
      )}
      <div className="small muted">timeout — no answer in 8 s (slow network or busy hosting); http 5xx/429/508 — the hosting refused; offline/network — this device had no connection.</div>
    </>
  );
}
