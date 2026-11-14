// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.106.0: the last messages of recently opened chats, kept in this browser (IndexedDB), so a chat stays
// readable while the CO database does not answer. Per signed-in account, the 30 most recently opened chats, wiped on
// sign-out. Never a source of truth: shown only with a "copy from <time>" notice until the database answers again.
import { getAuth, onAuth } from './auth';
import type { ChatEvent } from './types';

const DB_NAME = 'co-cache';
const STORE = 'events';
const KEEP = 30;
const MAX_CHARS = 4_000_000; // larger chats are not cached

interface Entry { key: string; email: string; chatId: string; savedUtc: string; at: number; events: ChatEvent[] }

let dbP: Promise<IDBDatabase | null> | null = null;

function open(): Promise<IDBDatabase | null> {
  if (typeof indexedDB === 'undefined') return Promise.resolve(null);
  dbP ??= new Promise((resolve) => {
    try {
      const req = indexedDB.open(DB_NAME, 1);
      req.onupgradeneeded = () => {
        if (!req.result.objectStoreNames.contains(STORE)) req.result.createObjectStore(STORE, { keyPath: 'key' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
      req.onblocked = () => resolve(null);
    } catch {
      resolve(null);
    }
  });
  return dbP;
}

function done<T>(req: IDBRequest<T>): Promise<T | null> {
  return new Promise((resolve) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => resolve(null);
  });
}

function account(): string | null {
  const a = getAuth();
  return a.status === 'signed_out' ? null : a.email;
}

// Remember the messages of a chat just read from the database. Never throws.
export async function saveChatEvents(chatId: string, events: ChatEvent[]): Promise<void> {
  const email = account();
  if (!email) return;
  try {
    if (JSON.stringify(events).length > MAX_CHARS) return;
    const db = await open();
    if (!db) return;
    const st = db.transaction(STORE, 'readwrite').objectStore(STORE);
    const entry: Entry = { key: email + '|' + chatId, email, chatId, savedUtc: new Date().toISOString(), at: Date.now(), events };
    await done(st.put(entry));
    const all = ((await done(db.transaction(STORE, 'readonly').objectStore(STORE).getAll())) || []) as Entry[];
    const mine = all.filter((e) => e.email === email).sort((a, b) => b.at - a.at);
    if (mine.length > KEEP) {
      const del = db.transaction(STORE, 'readwrite').objectStore(STORE);
      for (const e of mine.slice(KEEP)) del.delete(e.key);
    }
  } catch {
    /* cache only */
  }
}

// The cached copy of a chat for the signed-in account, or null.
export async function cachedChatEvents(chatId: string): Promise<{ events: ChatEvent[]; savedUtc: string } | null> {
  const email = account();
  if (!email) return null;
  try {
    const db = await open();
    if (!db) return null;
    const e = (await done(db.transaction(STORE, 'readonly').objectStore(STORE).get(email + '|' + chatId))) as Entry | null;
    return e && Array.isArray(e.events) ? { events: e.events, savedUtc: e.savedUtc } : null;
  } catch {
    return null;
  }
}

async function clearAll(): Promise<void> {
  try {
    const db = await open();
    if (db) await done(db.transaction(STORE, 'readwrite').objectStore(STORE).clear());
  } catch {
    /* ignore */
  }
}

// Sign-out of a signed-in account wipes the copies: another person may sign in in this browser next. A failed start-up
// check (no network) is not a sign-out.
let was: string = getAuth().status;
onAuth((s) => {
  if (s.status === 'signed_out' && !s.error && (was === 'signed_in' || was === 'expired')) void clearAll();
  was = s.status;
});
