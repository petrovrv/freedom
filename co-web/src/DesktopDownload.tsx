// SPDX-License-Identifier: AGPL-3.0-or-later
// Host tab → CO Desktop (0.46.0): latest installer from /agent/desktop.php; inside CO Desktop the installed version.
import { useEffect, useState } from 'react';
import { getToken } from './auth';
import { formatBytes, formatLocal } from './util';

type Release = { version: string; fileName: string; size: number; sha256: string; publishedUtc: string };
type State = Release | 'loading' | 'none' | 'error';

export default function DesktopDownloadSection() {
  const [state, setState] = useState<State>('loading');

  useEffect(() => {
    let active = true;
    const headers: Record<string, string> = { 'X-CO-Auth': '1', 'Content-Type': 'application/json' };
    const token = getToken();
    if (token != null) headers.Authorization = `Bearer ${token}`;
    fetch('/agent/desktop.php', {
      method: 'POST', headers, credentials: 'same-origin', cache: 'no-store', body: JSON.stringify({ op: 'latest' }),
    })
      .then(async (res) => {
        if (res.status === 200) {
          const data = (await res.json()) as Release;
          if (active) setState(data);
        } else if (active) {
          setState(res.status === 404 ? 'none' : 'error');
        }
      })
      .catch(() => {
        if (active) setState('error');
      });
    return () => {
      active = false;
    };
  }, []);

  if (state === 'loading') return <p className="muted small">Loading…</p>;
  if (state === 'none') return <p className="muted small">No installer published yet.</p>;
  if (state === 'error') return <p className="muted small">Could not load the latest version.</p>;

  const { version, fileName, size, sha256, publishedUtc } = state;
  const installed = (window as unknown as { coDesktop?: { version?: string } }).coDesktop?.version;

  return (
    <>
      <dl className="kv">
        {installed && (<><dt>Installed</dt><dd>v{installed}</dd></>)}
        <dt>Latest</dt><dd>v{version}</dd>
        <dt>Size</dt><dd>{formatBytes(size)}</dd>
        <dt>Published</dt><dd>{formatLocal(publishedUtc)}</dd>
      </dl>
      {installed ? (
        <p className="muted small">
          {installed === version
            ? 'Up to date. Updates install automatically.'
            : 'A newer version installs automatically when no calls are running.'}
        </p>
      ) : (
        <>
          <p>
            <a className="btn small" href="/agent/desktop.php?op=download" download={fileName}>
              Download CO Desktop v{version}
            </a>
          </p>
          <p className="muted small">
            Windows 10/11, installs for the current user without admin rights. After installing, sign in with your
            Google account; updates then install automatically.
          </p>
        </>
      )}
      <details>
        <summary className="muted small">SHA-256</summary>
        <code style={{ wordBreak: 'break-all', fontSize: 11 }}>{sha256}</code>
      </details>
    </>
  );
}
