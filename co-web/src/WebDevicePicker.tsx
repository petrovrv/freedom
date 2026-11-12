// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.49.0: composer picker of the user's web devices (webdev_mine); selected ids go into the job request devices.
import { useEffect, useMemo, useState } from 'react';
import { dbWebdevMine, type WebDeviceRow } from './dbapi';
import { useWebDev } from './WebDevice';
import { useExtDevice } from './extDevice';
import { inExtension } from './extLink';

// 0.62.0: the extension panel keeps its own choice (its frame may share storage with CO tabs)
const pickKey = (email: string): string => (inExtension() ? 'co.webdev.pick.ext.' : 'co.webdev.pick.') + email;

// Returns null when nothing (valid) is stored
function readPick(email: string): string[] | null {
  try {
    const raw = localStorage.getItem(pickKey(email));
    if (raw === null) return null;
    const v: unknown = JSON.parse(raw);
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : null;
  } catch {
    return null;
  }
}

function writePick(email: string, ids: string[]): void {
  try {
    localStorage.setItem(pickKey(email), JSON.stringify(ids));
  } catch {
    // storage unavailable or full: ignore
  }
}

export function useWebDevicePick(email: string): { devices: WebDeviceRow[]; selected: string[]; toggle: (id: string) => void } {
  const { deviceId, folders } = useWebDev();
  const ext = useExtDevice();
  const ownCount = folders.length;
  const [devices, setDevices] = useState<WebDeviceRow[]>([]);
  const [stored, setStored] = useState<string[] | null>(() => readPick(email));

  // Re-read stored choice when the account changes
  useEffect(() => {
    setStored(readPick(email));
  }, [email]);

  // Load on mount, every 60 s, and when own device id or folder count changes
  useEffect(() => {
    let alive = true;
    const load = (): void => {
      dbWebdevMine()
        .then((rows) => { if (alive) setDevices(rows.filter((d) => d.folders.length > 0 || d.browser)); }) // no folders and no browser tools: nothing to offer
        .catch(() => { /* keep previous list */ });
    };
    load();
    const timer = window.setInterval(load, 60_000);
    return () => {
      alive = false;
      window.clearInterval(timer);
    };
  }, [deviceId, ownCount, ext.deviceId, ext.online]);

  // Effective selection, always limited to known devices; default = this browser when it has folders
  const selected = useMemo(() => {
    const known = new Set(devices.map((d) => d.deviceId));
    // 0.63.1: no duplicates (in the extension panel this tab's device can be the extension's device; Bridge rejects a repeated id)
    if (stored) return [...new Set(stored)].filter((id) => known.has(id));
    const own = devices.find((d) => d.deviceId === deviceId);
    const pick = own && (ownCount > 0 || own.folders.length > 0) ? [own.deviceId] : [];
    // 0.62.0: inside the extension panel its own browser device is on by default
    if (ext.deviceId && known.has(ext.deviceId) && !pick.includes(ext.deviceId)) pick.push(ext.deviceId);
    return pick;
  }, [devices, stored, deviceId, ownCount, ext.deviceId]);

  const toggle = (id: string): void => {
    const next = selected.includes(id) ? selected.filter((x) => x !== id) : [...selected, id];
    setStored(next);
    writePick(email, next);
  };

  return { devices, selected, toggle };
}

export function WebDevicePicker({ devices, selected, onToggle }: { devices: WebDeviceRow[]; selected: string[]; onToggle: (id: string) => void }) {
  const { deviceId } = useWebDev();
  const ext = useExtDevice();
  if (devices.length === 0) return null;

  return (
    <>
      {devices.map((d) => {
        const on = selected.includes(d.deviceId);
        const own = d.deviceId === deviceId || d.deviceId === ext.deviceId;
        const state = d.online ? 'online' : d.browser ? 'offline: open the AI Workbench panel in that browser' : 'offline: open CO in a browser tab on that computer';
        const list = d.browser ? 'the agent reads and works in the tabs of that browser' : d.folders.length > 0
          ? d.folders.map((f) => f.name + ' (' + (f.access === 'write' ? 'rw' : 'ro') + ')').join(', ')
          : 'no folders';
        const title = [state, list, 'Click to turn ' + (on ? 'off' : 'on') + ' for new messages.'].join('\n');
        return (
          <button
            key={d.deviceId}
            type="button"
            className={'perm-chip ' + (on ? 'on' : 'off')}
            aria-pressed={on}
            title={title}
            style={d.online ? undefined : { opacity: 0.6 }}
            onClick={() => onToggle(d.deviceId)}
          >
            {(d.browser ? '🧭 ' : '🗂 ') + d.name + (own ? (d.browser ? ' (this panel)' : ' (this browser)') : '')}
          </button>
        );
      })}
    </>
  );
}
