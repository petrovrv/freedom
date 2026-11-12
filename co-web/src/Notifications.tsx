// SPDX-License-Identifier: AGPL-3.0-or-later
// "Notifications" section of the Host tab: Web Push on/off and test for this device (CO Web 0.13.0).
import { useEffect, useState } from 'react';
import { disablePush, enablePush, isStandaloneIOS, pushState, testPush, type PushState } from './push';

const LABELS: Record<PushState, string> = {
  unsupported: 'Not supported in this browser',
  denied: 'Blocked in browser settings',
  off: 'Off on this device',
  on: 'On for this device',
};

function errMsg(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function safeState(): Promise<PushState> {
  return pushState().catch((): PushState => 'off');
}

export function NotificationsSection() {
  const [state, setState] = useState<PushState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState<number | null>(null);
  const iosHint = isStandaloneIOS();

  useEffect(() => {
    let alive = true;
    pushState()
      .then((s) => {
        if (alive) setState(s);
      })
      .catch((e) => {
        if (!alive) return;
        setState('off');
        setError(errMsg(e));
      });
    return () => {
      alive = false;
    };
  }, []);

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(errMsg(e));
    } finally {
      setBusy(false);
    }
  };

  const onEnable = () =>
    run(async () => {
      setSent(null);
      try {
        await enablePush();
      } finally {
        setState(await safeState());
      }
    });

  const onDisable = () =>
    run(async () => {
      setSent(null);
      try {
        await disablePush();
      } finally {
        setState(await safeState());
      }
    });

  const onTest = () =>
    run(async () => {
      setSent(null);
      const res = await testPush();
      setSent(res.sent);
    });

  return (
    <div className="notif">
      <div className="muted small">Job results, schedule summaries and Bridge sign-in problems arrive as notifications.</div>
      {iosHint && <div className="muted small">On iPhone/iPad add CO to the Home Screen first.</div>}
      <div className="small">{state ? LABELS[state] : 'Checking…'}</div>
      <div>
        {state === 'off' && (
          <button type="button" className="btn small" disabled={busy} onClick={onEnable}>
            Enable on this device
          </button>
        )}
        {state === 'on' && (
          <>
            <button type="button" className="btn ghost small" disabled={busy} onClick={onDisable}>
              Disable
            </button>{' '}
            <button type="button" className="btn small" disabled={busy} onClick={onTest}>
              Send test
            </button>
          </>
        )}
        {sent !== null && (
          <>
            {' '}
            <span className="muted small">Sent: {sent}</span>
          </>
        )}
      </div>
      {error && <div className="error-text small">{error}</div>}
    </div>
  );
}
