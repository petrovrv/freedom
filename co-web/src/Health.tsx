// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.77.0: "Health" section of the Host tab — causes of failed jobs over 24 h / 7 days (server diagnosis, job_failures)
// and the last events of the Bridge supervisor (launcher watchdog, restart ladder; host_events). db.php op "health", owner only.
import { useEffect, useState } from 'react';
import { dbHealth, type HealthReport } from './dbapi';
import { corporate } from './corporate';

// Short labels of the diagnosis kinds (co-web/src/diagnose.ts and co-agent/web/agent/failures-lib.php).
const KIND_LABEL: Record<string, string> = {
  codex_limit: 'ChatGPT usage limit',
  claude_limit: 'Claude usage limit',
  claude_auth: 'Claude signed out',
  codex_auth: 'Codex signed out',
  model_not_found: 'Model not available',
  bridge_stuck_job: 'Hung job closed by Bridge',
  corporate: `${corporate().title} unavailable`,
  engie: `${corporate().title} unavailable`, // kind of job_failures rows before Freedom F1 5b
  context_too_long: 'Conversation too long',
  session_missing: 'Session missing',
  timeout: 'Job time limit',
  network: 'No network / DNS',
  overloaded: 'Provider unavailable',
  bridge_restart: 'Bridge restarted',
  cancelled: 'Cancelled',
  busy: 'Previous reply running',
  site_codex_no_tools: 'GPT tools on the server',
  codex_crash: 'Codex crashed',
  thread_limit: 'Thread limit',
  native_crash: 'Agent process crashed',
  git_128: 'Exit code 128',
  bad_request: 'Settings rejected',
  bad_permissions: 'Bad Files permission',
  folder_missing: 'Folder missing',
  git_missing: 'Git missing',
  missing_artifacts: 'Expected file missing',
  agent_exit_1: 'Agent exit 1',
  no_result: 'No result',
  fallback: 'Other',
};

const tableStyle = { width: '100%', borderCollapse: 'collapse' as const, marginTop: 6 };
const num = { textAlign: 'right' as const };
const left = { textAlign: 'left' as const };
const danger = 'var(--danger, #c33)';

const when = (utc: string | null) => (utc ? new Date(utc).toLocaleString() : '—');

export function HealthSection() {
  const [tick, setTick] = useState(0);
  const [loading, setLoading] = useState(true);
  const [data, setData] = useState<HealthReport | 'forbidden' | null>(null);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    dbHealth()
      .then((r) => { if (alive) setData(r); })
      .catch(() => { if (alive) setData(null); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [tick]);

  const render = (r: HealthReport) => (
    <>
      <div className="small" style={{ marginTop: 6, fontWeight: 600 }}>Failed jobs by cause</div>
      {r.failures.length === 0 ? (
        <div className="small muted">No failed jobs in 7 days.</div>
      ) : (
        <table className="usage-table small" style={tableStyle}>
          <thead>
            <tr>
              <th style={left}>Cause</th>
              <th style={num}>24 h</th>
              <th style={num}>7 d</th>
              <th style={left}>Last</th>
            </tr>
          </thead>
          <tbody>
            {r.failures.map((f) => (
              <tr key={f.kind} title={f.lastDetail || undefined}>
                <td>{KIND_LABEL[f.kind] || f.kind}</td>
                <td style={f.n24 ? { ...num, color: danger } : num}>{f.n24}</td>
                <td style={num}>{f.n7}</td>
                <td>
                  {f.lastEnv && f.lastProjectId && f.lastChatId ? (
                    <a href={`/e/${f.lastEnv}/p/${f.lastProjectId}/c/${f.lastChatId}`}>{when(f.lastUtc)}</a>
                  ) : (
                    when(f.lastUtc)
                  )}
                  {f.lastHost && <span className="muted"> · {f.lastHost}{f.lastAgent ? ` · ${f.lastAgent}` : ''}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="small" style={{ marginTop: 10, fontWeight: 600 }}>Bridge supervisor</div>
      {r.events.length === 0 ? (
        <div className="small muted">No events.</div>
      ) : (
        <ul className="small" style={{ margin: '4px 0', paddingLeft: 18 }}>
          {r.events.slice(0, 10).map((e, i) => (
            <li key={`${e.utc}-${i}`}>
              {when(e.utc)} · {e.host} · <b>{e.kind}</b>
              {e.detail && <span className="muted"> — {e.detail}</span>}
            </li>
          ))}
        </ul>
      )}
      <div className="small muted" style={{ marginTop: 6 }}>Hover a cause to see the last error. Generated {when(r.generatedUtc)}.</div>
    </>
  );

  return (
    <div>
      <button className="btn small" onClick={() => setTick((n) => n + 1)} disabled={loading}>
        Refresh
      </button>
      {loading ? (
        <div className="small muted" style={{ marginTop: 6 }}>Loading…</div>
      ) : data === null ? (
        <div className="small muted" style={{ marginTop: 6 }}>Not available (database not reachable or not updated yet).</div>
      ) : data === 'forbidden' ? (
        <div className="small muted" style={{ marginTop: 6 }}>Only the owner sees host health.</div>
      ) : (
        render(data)
      )}
    </div>
  );
}
