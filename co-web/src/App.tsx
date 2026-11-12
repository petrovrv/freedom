// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { onAuth, signIn, type AuthState } from './auth';
import { APP_VERSION } from './config';
import Shell from './Shell';
import Canvas from './Canvas';
import CanvasShare from './CanvasShare';

function Login({ error }: { error?: string }) {
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | undefined>(error);
  useEffect(() => setErr(error), [error]);
  return (
    <div className="login">
      <div className="login-card">
        <div className="brand">CO</div>
        <p className="muted">AI Workbench · personal access</p>
        <button
          className="btn primary"
          disabled={busy}
          onClick={async () => {
            setBusy(true);
            setErr(undefined);
            try {
              await signIn();
            } catch (e) {
              setErr((e as Error).message);
            } finally {
              setBusy(false);
            }
          }}
        >
          {busy ? 'Waiting for Google…' : 'Sign in with Google'}
        </button>
        {err && <p className="error-text">{err}</p>}
        <p className="tiny muted">v{APP_VERSION}</p>
      </div>
    </div>
  );
}

export default function App() {
  const [auth, setAuth] = useState<AuthState>({ status: 'signed_out' });
  useEffect(() => onAuth(setAuth), []);

  // 0.83.1: a tapped push asks the open window to show its chat (sw.js notificationclick); route in place, no reload
  const navigate = useNavigate();
  useEffect(() => {
    const sw = navigator.serviceWorker;
    if (!sw) return;
    const onMessage = (e: MessageEvent) => {
      const d = e.data as { type?: string; path?: string } | null;
      if (d?.type !== 'co-open' || typeof d.path !== 'string' || !d.path.startsWith('/') || d.path.startsWith('//')) return;
      navigate(d.path);
      e.ports[0]?.postMessage('ok');
    };
    sw.addEventListener('message', onMessage);
    sw.startMessages?.();
    return () => sw.removeEventListener('message', onMessage);
  }, [navigate]);

  if (auth.status === 'signed_out' && auth.checking) return <div className="login"><p className="muted">Signing in…</p></div>;
  if (auth.status === 'signed_out') return <Login error={auth.error} />;

  return (
    <Routes>
      <Route path="/canvas" element={<Canvas />} />
      <Route path="/canvas-share" element={<CanvasShare />} />
      <Route path="/e/:env" element={<Shell auth={auth} />} />
      <Route path="/e/:env/import" element={<Shell auth={auth} />} />
      <Route path="/e/:env/inbox" element={<Shell auth={auth} />} />
      <Route path="/e/:env/p/:projectId" element={<Shell auth={auth} />} />
      <Route path="/e/:env/p/:projectId/c/:chatId" element={<Shell auth={auth} />} />
      <Route path="*" element={<Navigate to={`/e/${auth.homeEnv}`} replace />} />
    </Routes>
  );
}
