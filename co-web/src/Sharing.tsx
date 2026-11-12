// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { ENVIRONMENTS, type EnvironmentId } from './config';
import { dbSharesGet, dbShareSet, type SharesInfo, type ShareRole } from './dbapi';
import { saveProjectMeta } from './workbench';
import { categorize } from './errors';
import type { Project } from './types';

// 0.45.0: sharing of a project or a chat with other environments
export interface SharingSectionProps {
  kind: 'project' | 'chat';
  id: string;
  project: Project;
  transferable: boolean;
  onProjectChanged: (p: Project) => void;
  onError: (m: string) => void;
}

function name(env: string): string {
  return ENVIRONMENTS.find((e) => e.id === env)?.displayName ?? env;
}

function roleLabel(role: ShareRole): string {
  return role === 'editor' ? 'Can edit' : 'Can view';
}

export function SharingSection({ kind, id, project, transferable, onProjectChanged, onError }: SharingSectionProps) {
  const [info, setInfo] = useState<SharesInfo | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [target, setTarget] = useState('');
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setInfo(null);
    dbSharesGet(kind, id)
      .then((r) => {
        if (alive) setInfo(r);
      })
      .catch((e: unknown) => {
        if (alive) onError(categorize(e).message);
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
    // onError intentionally not a dependency (avoids reload loops on inline callbacks)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind, id, reloadKey]);

  if (loading) return <p className="muted tiny">Loading...</p>;
  if (!info) return null;

  const isOwner = info.role === 'owner';
  const others = info.envs.filter((e) => e !== info.ownerEnv);
  const roleOf = (env: string): ShareRole | '' => info.shares.find((s) => s.env === env)?.role ?? '';
  const transferTo = target && others.includes(target) ? target : others[0] || '';

  async function setRole(env: string, value: string) {
    const role: ShareRole | null = value === 'viewer' || value === 'editor' ? value : null;
    setBusy(true);
    try {
      const shares = await dbShareSet(kind, id, env, role);
      setInfo((cur) => (cur ? { ...cur, shares } : cur));
    } catch (e) {
      onError(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function transfer() {
    if (!transferTo) return;
    if (!window.confirm(`Transfer project "${project.title}" to ${name(transferTo)}? The new owner manages it; you stay an editor.`)) return;
    setBusy(true);
    try {
      onProjectChanged(await saveProjectMeta(project, { environmentId: transferTo as EnvironmentId }));
      setTarget('');
      setReloadKey((k) => k + 1);
    } catch (e) {
      onError(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      {kind === 'project' ? (
        <p className="muted tiny">
          Owner: {name(info.ownerEnv)}. Shared chats cannot use devices or context folders.
        </p>
      ) : (
        <p className="muted tiny">
          Shared chat: devices and context folders are off for everyone in it.
          {info.projectShares.length > 0 &&
            ' The whole project is shared with: ' +
              info.projectShares.map((s) => `${name(s.env)} (${roleLabel(s.role)})`).join(', ') +
              '.'}
        </p>
      )}

      {others.map((env) => {
        const r = roleOf(env);
        return (
          <div key={env} className="share-row small">
            <span>{name(env)}</span>{' '}
            {isOwner ? (
              <select value={r} disabled={busy} onChange={(e) => void setRole(env, e.target.value)}>
                <option value="">No access</option>
                <option value="viewer">Can view</option>
                <option value="editor">Can edit</option>
              </select>
            ) : (
              <span>{r ? roleLabel(r) : 'No access'}</span>
            )}
          </div>
        );
      })}

      {kind === 'project' && isOwner && others.length > 0 &&
        (transferable ? (
          <div className="share-row small">
            <select value={transferTo} disabled={busy} onChange={(e) => setTarget(e.target.value)}>
              {others.map((env) => (
                <option key={env} value={env}>
                  {name(env)}
                </option>
              ))}
            </select>{' '}
            <button type="button" className="btn tiny ghost" disabled={busy || !transferTo} onClick={() => void transfer()}>
              Transfer...
            </button>
          </div>
        ) : (
          <p className="muted tiny">Projects registered on the home agent (Bridge config) cannot be transferred.</p>
        ))}
    </div>
  );
}
