// SPDX-License-Identifier: AGPL-3.0-or-later
import { RETIRED_AGENTS } from './config';
import { corporate } from './corporate';
import type { AgentLimits, LimitsFile, LimitWindow } from './types';

const AGENT_LABEL: Record<string, string> = {
  claude: 'Claude', codex: 'GPT',
  ...Object.fromEntries(corporate().agents.map((a) => [a.id, `${a.title} saved`])),
};
const WINDOW_LABEL: Record<string, string> = { session: 'Session', week: 'Week' };

function fmtTime(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  const sameDay = d.toDateString() === new Date().toDateString();
  return sameDay
    ? d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
}

// A window whose reset time has passed is shown as reset: the provider reports the new
// value only with the next request.
export function windowState(w: LimitWindow): { pct: number; reset: boolean } {
  const reset = !!w.resetsAtUtc && new Date(w.resetsAtUtc).getTime() < Date.now();
  return { pct: reset ? 0 : w.usedPercent, reset };
}

const level = (pct: number) => (pct >= 90 ? 'bad' : pct >= 75 ? 'warn' : 'ok');

// Corporate dispatcher (Bridge 1.1.6): share of generated tokens written by the corporate models instead of the Claude
// subscription (limits source = usageSource of the connector manifest).
// More is better, so the colour runs the other way. Before 1.1.6 Bridge wrote a copy of the Claude limits here.
const isSavings = (a: AgentLimits) => !!corporate().usageSource && a.source === corporate().usageSource;
const corpTok = (w: LimitWindow) => w.corpTokens ?? w.engieTokens; // engieTokens: legacy wire (Bridge before 5b)
const savedLevel = (pct: number) => (pct >= 50 ? 'ok' : pct >= 20 ? 'warn' : 'low');
const kTok = (n?: number | null) => (n ?? 0) >= 10000 ? `${Math.round((n ?? 0) / 1000)}k` : `${n ?? 0}`;

function savingsTip(w: LimitWindow): string {
  const span = WINDOW_LABEL[w.id] || w.key;
  const t = corporate().title;
  return `Dispatcher · ${span}: ${Math.round(w.usedPercent)}% of generated tokens came from ${t} models\n` +
    `${t} ${kTok(corpTok(w))} · Claude ${kTok(w.claudeTokens)} output tokens · ${w.jobs ?? 0} job(s)\n` +
    `window ends ${fmtTime(w.resetsAtUtc)} (same as the Claude subscription)`;
}

// Agents shown: everything except the agents of a paused corporate connector (0.66.0: its savings row is hidden).
const shown = (limits: LimitsFile | null) =>
  Object.entries(limits?.agents || {}).filter(([agent, a]) => a.windows?.length && !RETIRED_AGENTS.has(agent));

function tip(agent: string, a: AgentLimits, w: LimitWindow): string {
  const { reset } = windowState(w);
  const name = `${AGENT_LABEL[agent] || agent} · ${WINDOW_LABEL[w.id] || w.key}${w.windowMinutes ? ` (${w.windowMinutes >= 1440 ? w.windowMinutes / 1440 + ' d' : w.windowMinutes / 60 + ' h'})` : ''}`;
  const state = reset ? `reset at ${fmtTime(w.resetsAtUtc)}` : `${w.usedPercent}% used, resets ${fmtTime(w.resetsAtUtc)}`;
  return `${name}: ${state}\nobserved ${fmtTime(a.observedUtc)}${a.plan ? ` · plan ${a.plan}` : ''}`;
}

const ordered = (a: AgentLimits) =>
  [...a.windows].sort((x, y) => (x.id === 'session' ? -1 : y.id === 'session' ? 1 : x.id === 'week' ? -1 : y.id === 'week' ? 1 : 0));

// Compact bars for the top bar.
export function LimitsBar({ limits }: { limits: LimitsFile | null }) {
  const entries = shown(limits);
  if (!entries.length) return null;
  return (
    <div className="limits" aria-label="Usage limits">
      {entries.map(([agent, a]) => (
        <div key={agent} className="lim-agent">
          <span className="lim-name">{agent === 'codex' ? 'GPT' : AGENT_LABEL[agent] || agent}</span>
          {ordered(a)
            .filter((w) => w.id === 'session' || w.id === 'week')
            .map((w) => {
              const { pct, reset } = windowState(w);
              const saved = isSavings(a);
              return (
                <span key={w.key} className={`lim-bar ${saved ? 'saved ' + savedLevel(pct) : level(pct)} ${reset ? 'reset' : ''}`} title={saved ? savingsTip(w) : tip(agent, a, w)}>
                  <span className="lim-letter">{w.id === 'session' ? 'S' : 'W'}</span>
                  <span className="lim-track">
                    <span className="lim-fill" style={{ width: `${pct}%` }} />
                  </span>
                  <span className="lim-pct">{Math.round(pct)}%</span>
                </span>
              );
            })}
        </div>
      ))}
    </div>
  );
}

// Detailed view for the Host tab.
export function LimitsDetail({ limits }: { limits: LimitsFile | null }) {
  const entries = shown(limits);
  if (!entries.length) return <p className="muted small">No limit data yet. It appears after the first Claude job; Codex data comes from its session logs.</p>;
  return (
    <div className="lim-detail">
      {entries.map(([agent, a]) => (
        <div key={agent}>
          <div className="small">
            <b>{AGENT_LABEL[agent] || agent}</b>{isSavings(a) ? <span className="muted tiny"> · {corporate().title} share of generated tokens</span> : null}
            <span className="muted tiny"> · observed {fmtTime(a.observedUtc)}{a.plan ? ` · plan ${a.plan}` : ''}</span>
          </div>
          {ordered(a).map((w) => {
            const { pct, reset } = windowState(w);
            return (
              <div key={w.key} className="lim-row">
                <span className="lim-row-label">{WINDOW_LABEL[w.id] || w.key}</span>
                <span className={`lim-track wide ${isSavings(a) ? 'saved ' + savedLevel(pct) : level(pct)}`} title={isSavings(a) ? savingsTip(w) : undefined}>
                  <span className="lim-fill" style={{ width: `${pct}%` }} />
                </span>
                <span className="tiny">{reset ? `reset ${fmtTime(w.resetsAtUtc)}` : isSavings(a)
                  ? `${w.usedPercent}% saved · ${corporate().title} ${kTok(corpTok(w))} / Claude ${kTok(w.claudeTokens)}`
                  : `${w.usedPercent}% · ${fmtTime(w.resetsAtUtc)}`}</span>
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}
