// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.13.1: every open CO page (browser or CO Desktop) triggers the site's "Bridge offline" check
// (/agent/push.php?op=watch, throttled to once a minute on the server) every 5 minutes while visible.
import { useEffect, useRef } from 'react';

const WATCH_URL = '/agent/push.php?op=watch';
const INTERVAL_MS = 5 * 60 * 1000;

export function useBridgeWatch(): void {
  const lastRef = useRef(0);

  useEffect(() => {
    let disposed = false;

    const ping = (): void => {
      if (disposed) return;
      lastRef.current = Date.now();
      fetch(WATCH_URL, { cache: 'no-store', credentials: 'same-origin' }).catch(() => undefined);
    };

    const isVisible = (): boolean => document.visibilityState === 'visible';

    // Interval tick: only when visible; small tolerance avoids double pings after a visibility ping.
    const onTick = (): void => {
      if (isVisible() && Date.now() - lastRef.current >= INTERVAL_MS - 5000) ping();
    };

    // Became visible: ping if the last call is older than the interval.
    const onVisibility = (): void => {
      if (isVisible() && Date.now() - lastRef.current > INTERVAL_MS) ping();
    };

    if (isVisible()) ping();
    const id = window.setInterval(onTick, INTERVAL_MS);
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      disposed = true;
      window.clearInterval(id);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);
}
