// SPDX-License-Identifier: AGPL-3.0-or-later
// Web Share Target (CO Web 0.14.0): items the service worker stored from POST /share-target.
const DB_NAME = 'co-share';
const STORE = 'shares';
const DAY_MS = 24 * 60 * 60 * 1000;

export interface SharedItem {
  id: string;
  createdUtc: string;
  title?: string;
  text?: string;
  url?: string;
  files: File[];
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: 'id' });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

// Run fn in a transaction; resolves with fn's return value once the transaction completes
async function withStore<T>(mode: IDBTransactionMode, fn: (store: IDBObjectStore) => T): Promise<T> {
  const db = await openDb();
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const result = fn(tx.objectStore(STORE));
      tx.oncomplete = () => resolve(result);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

function ageMs(item: SharedItem, now: number): number {
  const t = Date.parse(item.createdUtc);
  return Number.isNaN(t) ? Infinity : now - t;
}

function normalize(item: SharedItem): SharedItem {
  return { ...item, files: Array.isArray(item.files) ? item.files : [] };
}

export async function latestShare(maxAgeMin = 30): Promise<SharedItem | null> {
  if (typeof indexedDB === 'undefined') return null;
  const req = await withStore('readonly', (s) => s.getAll() as IDBRequest<SharedItem[]>);
  const items = req.result ?? [];
  const now = Date.now();

  const stale = items.filter((i) => ageMs(i, now) > DAY_MS).map((i) => i.id);
  if (stale.length) {
    await withStore('readwrite', (s) => stale.forEach((id) => s.delete(id))).catch(() => undefined);
  }

  const fresh = items
    .filter((i) => ageMs(i, now) <= maxAgeMin * 60 * 1000)
    .sort((a, b) => ageMs(a, now) - ageMs(b, now));
  return fresh.length ? normalize(fresh[0]) : null;
}

export async function takeShare(id: string): Promise<SharedItem | null> {
  const req = await withStore('readwrite', (s) => {
    const get = s.get(id) as IDBRequest<SharedItem | undefined>;
    s.delete(id);
    return get;
  });
  return req.result ? normalize(req.result) : null;
}

export async function dropShare(id: string): Promise<void> {
  await withStore('readwrite', (s) => {
    s.delete(id);
  });
}

export function shareText(s: SharedItem): string {
  const title = s.title?.trim();
  const text = s.text?.trim();
  const url = s.url?.trim();
  const parts: string[] = [];
  if (title) parts.push(title);
  if (text) parts.push(text);
  if (url && !(text && text.includes(url))) parts.push(url);
  return parts.join('\n');
}
