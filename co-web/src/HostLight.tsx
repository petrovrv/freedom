// SPDX-License-Identifier: AGPL-3.0-or-later
// S2 (CO Web 0.98.0): one traffic light in the header instead of the Home · Site switch. Green — Site and the home Bridge
// are alive; blue — one of them (usually Site only, that is normal); red — none. A click shows a short diagnosis (what is
// alive, when the Bridge last answered, the queue, home devices for the Site AI) without settings; the manual executor
// choice lives in Host and devices. Freedom F12: one row per executor of the server's list (any number of Bridges).
import { useEffect, useState } from 'react';
import type { OwnerInfo } from './types';
import { HOME, SITE, hostHealth, hostLight, hostStateText, label, type HostPref } from './routing';
import { dbHostLight, type HostLightReport } from './dbapi';
import { formatLocal } from './util';
import { executorOf } from './executors';

const LIGHT_TEXT = {
  green: 'Site and a Bridge are working',
  blue: 'Only Site or only a Bridge is working',
  red: 'Neither Site nor any Bridge answers',
  grey: 'Checking Site and Bridges…',
} as const;
const OS: Record<string, string> = { windows: 'Windows', macos: 'macOS', linux: 'Linux' };

function ago(utc?: string | null): string {
  if (!utc) return 'never';
  const s = Math.max(0, Math.round((Date.now() - new Date(utc).getTime()) / 1000));
  if (s < 90) return `${s} s ago`;
  if (s < 90 * 60) return `${Math.round(s / 60)} min ago`;
  if (s < 36 * 3600) return `${Math.round(s / 3600)} h ago`;
  return formatLocal(utc);
}

export function HostLight({ owners, ids, env, pref, effective, onAdvanced }: {
  owners: Record<string, OwnerInfo | null>;
  ids?: string[];
  env: string;
  pref: HostPref;
  effective: string;
  onAdvanced: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [report, setReport] = useState<HostLightReport | null | 'loading'>(null);
  const list = ids && ids.length ? ids : [SITE, HOME];
  const light = hostLight(owners, list);

  useEffect(() => {
    if (!open) return;
    let alive = true;
    setReport('loading');
    void dbHostLight(env).then((r) => { if (alive) setReport(r); });
    return () => { alive = false; };
  }, [open, env]);

  const row = (hostId: string, name: string) => {
    const o = owners[hostId] ?? null;
    const h = hostHealth(o);
    const q = report && report !== 'loading' ? report.hosts[hostId] : undefined;
    return (
      <div className="hl-row">
        <span className={`hl-dot ${h === 'online' ? 'green' : h === 'offline' ? 'red' : h === 'unknown' ? 'grey' : 'amber'}`} />
        <div>
          <b>{name}</b>: {h === 'updating' || h === 'paused' ? hostStateText(o, h) : h}
          {executorOf(hostId)?.enabled === false ? <span className="muted"> · switched off</span> : null}
          {o?.bridgeVersion ? <span className="muted"> · v{o.bridgeVersion}</span> : null}
          <div className="small muted">
            {o?.updatedUtc ? `answered ${ago(o.updatedUtc)}` : 'not announced'}
            {q ? ` · ${q.running} running, ${q.queued} queued` : ''}
            {q?.lastDoneUtc ? ` · last reply ${ago(q.lastDoneUtc)}` : ''}
          </div>
        </div>
      </div>
    );
  };

  const relay = report && report !== 'loading' ? report.relay : null;
  return (
    <div className="host-light-wrap">
      <button
        type="button"
        className={`host-light ${light}`}
        aria-label={LIGHT_TEXT[light]}
        aria-expanded={open}
        title={`${LIGHT_TEXT[light]}. Click for details.`}
        onClick={() => setOpen((v) => !v)}
      >
        <span className={`hl-dot ${light}`} />
      </button>
      {open && (
        <>
          <div className="app-menu-scrim" onClick={() => setOpen(false)} />
          <div className="geo-pop hl-pop" role="dialog" aria-label="Agents">
            <div><b>{LIGHT_TEXT[light]}</b></div>
            {row(SITE, 'Site')}
            {list.filter((id) => id !== SITE).map((id) => {
              const e = executorOf(id);
              const os = e && OS[e.os] ? ` · ${OS[e.os]}` : '';
              return <div key={id}>{row(id, id === HOME ? 'Home Bridge' : `${label(id)}${os}${e?.ownerEnv ? ` (${e.ownerEnv})` : ''}`)}</div>;
            })}
            {report === 'loading' && <div className="small muted">Reading the queue…</div>}
            {relay && (
              <div className="small muted">
                Home devices for the Site AI: {relay.devices ? `${relay.devices} online` : 'none'}
                {relay.seenUtc ? ` (Bridge heard ${ago(relay.seenUtc)})` : ''}
              </div>
            )}
            <div className="small">
              New jobs go to <b>{label(effective)}</b>
              {pref === 'auto' ? ' (automatic)' : ' (chosen by hand)'}; device tools and home-only agents stay with their Bridge.
            </div>
            <button type="button" className="btn tiny ghost" onClick={() => { setOpen(false); onAdvanced(); }}>Advanced…</button>
          </div>
        </>
      )}
    </div>
  );
}
