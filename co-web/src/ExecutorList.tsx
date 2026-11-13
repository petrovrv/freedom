// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F12 (SPEC §3.1a): Host and devices → Executors. Every executor the server knows for this environment (Bridges
// on any machine, the site agent) with its health, OS, whose machine, agents and whether it has a sandbox for you; the
// owner switches a registered one off (it takes no new jobs) or forgets it.
import { useEffect, useState } from 'react';
import { dbExecutors, dbExecutorSet, type ExecutorInfo } from './dbapi';
import { applyExecutors } from './executors';
import { getAuth } from './auth';
import { formatLocal } from './util';

const OS: Record<string, string> = { windows: 'Windows', macos: 'macOS', linux: 'Linux' };

export function ExecutorsSection({ env }: { env: string }) {
  const [list, setList] = useState<ExecutorInfo[] | null | 'loading'>('loading');
  const [busy, setBusy] = useState('');
  const auth = getAuth();
  const owner = auth.status === 'signed_in' && auth.owner;
  const load = () => dbExecutors(env).then((l) => { if (l) applyExecutors(l); setList(l); });
  useEffect(() => { void load(); }, [env]); // eslint-disable-line react-hooks/exhaustive-deps

  if (list === 'loading') return <p className="small muted">Reading executors…</p>;
  if (!list) return <p className="small muted">The server does not list executors yet (schema 28).</p>;
  const set = async (id: string, change: { enabled?: boolean; forget?: boolean }) => {
    setBusy(id);
    await dbExecutorSet(id, change);
    await load();
    setBusy('');
  };
  return (
    <ul className="plain exec-list">
      {list.map((e) => (
        <li key={e.id} className="small">
          <span className={`hl-dot ${e.takesJobs ? 'green' : e.state === 'paused' || e.state === 'updating' ? 'amber' : e.seenUtc ? 'red' : 'grey'}`} />{' '}
          <b>{e.kind === 'site' ? 'Site' : e.title || e.id}</b>
          <span className="muted">
            {' '}· {e.id}{OS[e.os] ? ` · ${OS[e.os]}` : ''}{e.version ? ` · v${e.version}` : ''}{e.ownerEnv ? ` · ${e.ownerEnv}'s` : ''}
            {e.agents.length ? ` · ${e.agents.join(', ')}` : ''}{e.sandboxForMe ? ' · your sandbox' : ''}
            {!e.enabled ? ' · switched off' : e.takesJobs ? ' · takes jobs' : e.state !== 'running' ? ` · ${e.state}` : ' · silent'}
            {e.seenUtc ? ` · heard ${formatLocal(e.seenUtc)}` : ''}
          </span>
          {owner && e.registered && e.id !== 'home-main' && e.id !== 'site' && (
            <>
              {' '}
              <button type="button" className="btn tiny ghost" disabled={busy === e.id}
                onClick={() => void set(e.id, { enabled: !e.enabled })}>{e.enabled ? 'Switch off' : 'Switch on'}</button>
              {!e.takesJobs && (
                <button type="button" className="btn tiny ghost" disabled={busy === e.id} title="Remove it from the list; it comes back when its Bridge beats again"
                  onClick={() => void set(e.id, { forget: true })}>Forget</button>
              )}
            </>
          )}
        </li>
      ))}
    </ul>
  );
}
