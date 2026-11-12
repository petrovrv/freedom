// SPDX-License-Identifier: AGPL-3.0-or-later
// Web Push for CO Web (0.13.0): the device subscribes at /agent/push.php; Bridge sends through the same endpoint.
import { getToken } from './auth';

const ENDPOINT = '/agent/push.php';

export type PushState = 'unsupported' | 'denied' | 'off' | 'on';
export type TestResult = { sent: number; removed: number; failed: number };

export function pushSupported(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

// True on iOS when not running as a Home Screen app (push unavailable there)
export function isStandaloneIOS(): boolean {
  return /iPhone|iPad/.test(navigator.userAgent) && !window.matchMedia('(display-mode: standalone)').matches;
}

export async function pushState(): Promise<PushState> {
  if (!pushSupported()) return 'unsupported';
  if (Notification.permission === 'denied') return 'denied';
  if (Notification.permission !== 'granted') return 'off';

  const reg = await navigator.serviceWorker.getRegistration();
  const sub = await reg?.pushManager.getSubscription();
  if (!sub) return 'off';

  const res = await call<{ subscribed: boolean }>({ op: 'status', endpoint: sub.endpoint });
  return res.subscribed ? 'on' : 'off';
}

export async function enablePush(): Promise<void> {
  if (!pushSupported()) throw new Error('Push notifications are not supported in this browser.');

  const perm = await Notification.requestPermission();
  if (perm !== 'granted') throw new Error('Notifications are blocked in the browser settings.');

  const reg = await navigator.serviceWorker.ready;
  const { publicKey } = await call<{ publicKey: string }>({ op: 'key' });
  const appKey = urlBase64ToUint8Array(publicKey);

  let sub = await reg.pushManager.getSubscription();
  if (sub && !sameKey(sub.options.applicationServerKey, appKey)) {
    // Server key rotated: drop the stale subscription
    const oldEndpoint = sub.endpoint;
    await sub.unsubscribe();
    try {
      await call({ op: 'unsubscribe', endpoint: oldEndpoint });
    } catch {
      /* best effort */
    }
    sub = null;
  }
  if (!sub) {
    sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: appKey });
  }

  await call<{ ok: boolean; count: number }>({ op: 'subscribe', subscription: sub.toJSON() });
}

export async function disablePush(): Promise<void> {
  if (!pushSupported()) return;
  const reg = await navigator.serviceWorker.getRegistration();
  const sub = await reg?.pushManager.getSubscription();
  if (!sub) return;

  try {
    await call<{ ok: boolean; removed: number }>({ op: 'unsubscribe', endpoint: sub.endpoint });
  } finally {
    await sub.unsubscribe();
  }
}

export async function testPush(): Promise<TestResult> {
  return call<TestResult>({ op: 'test' });
}

async function call<T>(body: Record<string, unknown>): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json', 'X-CO-Auth': '1' };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  const res = await fetch(ENDPOINT, {
    method: 'POST',
    headers,
    credentials: 'same-origin',
    cache: 'no-store',
    body: JSON.stringify(body),
  });

  let json: unknown = null;
  try {
    json = await res.json();
  } catch {
    /* non-JSON response */
  }

  if (!res.ok) {
    const msg = (json as { error?: string } | null)?.error || `Request failed (HTTP ${res.status})`;
    throw new Error(msg);
  }
  return json as T;
}

function urlBase64ToUint8Array(base64url: string) {
  const padding = '='.repeat((4 - (base64url.length % 4)) % 4);
  const base64 = (base64url + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(base64);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

function sameKey(a: ArrayBuffer | null, b: Uint8Array): boolean {
  if (!a) return false;
  const x = new Uint8Array(a);
  if (x.length !== b.length) return false;
  for (let i = 0; i < x.length; i++) {
    if (x[i] !== b[i]) return false;
  }
  return true;
}
