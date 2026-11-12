// SPDX-License-Identifier: AGPL-3.0-or-later
// S2 (CO Web 0.98.0): one traffic light in the header instead of the Home · Site switch. Green — Site and the home Bridge
// are alive; blue — one of them (usually Site only, that is normal); red — none. A click shows a short diagnosis (what is
// alive, when the Bridge last answered, the queue, home devices for the Site AI) without settings; the manual executor
// choice lives in Host and devices.
import { useEffect, useState } from 'react';
import type { OwnerInfo } from './types';
import { HOME, SITE, hostHealth, hostLight, hostStateText, label, type HostPref } from './routing';
import { dbHostLight, type HostLightReport } from './dbapi';
import { formatLocal } from './util';

const LIGHT_TEXT = {
  green: 'Site and home Bridge are working',
  blue: 'One of Site and home Bridge is working',
  red: 'Neither Site nor home Bridge answers',
  grey: 'Checking Site and home Bridge…',
} as const;

function ago(utc?: string | null): string {
  if (!utc) return 'never';
  const s = Math.max(0, Math.round((Date.now() - new Date(utc).getTime()) / 1000));
  if (s < 90) return `${s} s ago`;
  if (s < 90 * 60) return `${Math.round(s / 60)} min ago`;
  if (s < 36 * 3600) return `${Math.round(s / 3600)} h ago`;
  return formatLocal(utc);
}

export function HostLight({ owners, env, pref, effective, onAdvanced }: {
  owners: Record<string, OwnerInfo | null>;
  env: string;
  pref: HostPref;
  effective: string;
  onAdvanced: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [report, setReport] = useState<HostLightReport | null | 'loading'>(null);
  const light = hostLight(owners);

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
            {row(HOME, 'Home Bridge')}
            {report === 'loading' && <div className="small muted">Reading the queue…</div>}
            {relay && (
              <div className="small muted">
                Home devices for the Site AI: {relay.devices ? `${relay.devices} online` : 'none'}
                {relay.seenUtc ? ` (Bridge heard ${ago(relay.seenUtc)})` : ''}
              </div>
            )}
            <div className="small">
              New jobs go to <b>{label(effective)}</b>
              {pref === 'auto' ? ' (automatic)' : ' (chosen by hand)'}; device tools and home-only agents go to Home.
            </div>
            <button type="button" className="btn tiny ghost" onClick={() => { setOpen(false); onAdvanced(); }}>Advanced…</button>
          </div>
        </>
      )}
    </div>
  );
}
