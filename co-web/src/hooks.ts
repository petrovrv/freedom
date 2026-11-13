// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef, useState } from 'react';
import type { OwnerInfo } from './types';
import { hostAlive, hostHealth } from './routing';
import { gatewayState, type GatewayState } from './gateway';

// setInterval that always calls the latest callback, pauses when disabled or tab hidden.
export function useInterval(fn: () => void, ms: number, enabled = true) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    if (!enabled) return;
    const id = window.setInterval(() => {
      if (document.visibilityState === 'visible') ref.current();
    }, ms);
    const onFocus = () => ref.current();
    window.addEventListener('focus', onFocus);
    return () => {
      window.clearInterval(id);
      window.removeEventListener('focus', onFocus);
    };
  }, [ms, enabled]);
}

// 0.37.0: session of the gateway on a device as CO Desktop 0.7.11 reports it ('off' = disabled there).
export interface GatewayStatus {
  state: GatewayState;
  detail: string;
  checkedUtc: string | null;
}

export interface LocalDevice {
  deviceId: string;
  name: string;
  short: string;
  paired: boolean;
  gateway?: GatewayStatus; // remote devices (owner.json); CO Desktop's own status() is not read for it
  remote?: boolean; // 0.42.0: device reported by Bridge in owner.json, not this computer
}

// 0.42.0: device of the home PC that Bridge 1.4.1 reports in owner.json, for CO Web without CO Desktop (a phone).
export function remoteDevice(owner: OwnerInfo | null | undefined, env: string): LocalDevice | null {
  if (!owner) return null;
  const health = hostHealth(owner);
  if (!hostAlive(health)) return null;
  const candidates = (owner.devices || []).filter((d) => d && d.environmentId === env && d.online && d.deviceId);
  if (candidates.length === 0) return null;
  // 0.42.0: prefer the device whose gateway session is online
  const d = candidates.find((c) => gatewayState(c) === 'online') ?? candidates[0];
  const state = gatewayState(d);
  const gw: GatewayStatus | undefined = state ? { state, detail: '', checkedUtc: d.lastHeartbeatUtc ?? null } : undefined;
  return { deviceId: d.deviceId, name: d.name || d.shortName || d.deviceId, short: d.shortName || '', paired: true, gateway: gw, remote: true };
}

const DESKTOP_REFRESH = 'co-desktop-refresh';

// The CO Desktop this page runs in (window.coDesktop), refreshed every 10 s; null in a plain browser.
export function useLocalDevice(): LocalDevice | null {
  const [dev, setDev] = useState<LocalDevice | null>(null);
  useEffect(() => {
    const d = (window as unknown as { coDesktop?: { status?: () => Promise<LocalDevice> } }).coDesktop;
    if (!d?.status) return;
    let alive = true;
    const tick = () => d.status!().then((s) => alive && setDev(s)).catch(() => alive && setDev(null));
    tick();
    const id = window.setInterval(tick, 10000);
    window.addEventListener(DESKTOP_REFRESH, tick);
    return () => {
      alive = false;
      window.clearInterval(id);
      window.removeEventListener(DESKTOP_REFRESH, tick);
    };
  }, []);
  return dev;
}

// Whether messages from this CO Desktop attach its device tools; remembered per computer.
export function useDeviceToggle(): [boolean, (v: boolean) => void] {
  const key = 'co.useDevice';
  const [on, setOn] = useState<boolean>(() => {
    try { return localStorage.getItem(key) !== 'off'; } catch { return true; }
  });
  const set = (v: boolean) => {
    setOn(v);
    try { localStorage.setItem(key, v ? 'on' : 'off'); } catch { /* storage unavailable */ }
  };
  return [on, set];
}
