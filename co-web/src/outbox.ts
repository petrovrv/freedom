// SPDX-License-Identifier: AGPL-3.0-or-later
// Offline outbox (CO Web 0.22.0, PWA): a text message written without network waits on this device and is sent when
// the connection is back and its chat is open. Text only; files and new chats need a connection.
import type { AgentId } from './config';
import type { Mode } from './types';

const KEY = 'co.outbox';

export interface OutboxItem {
  id: string;
  chatId: string;
  createdAt: number;
  text: string;
  sel: { agent: AgentId; mode: Mode; model: string | null; effort: string | null };
  returnFiles: string[];
  forceNew: boolean;
}

const listeners = new Set<() => void>();

function readAll(): OutboxItem[] {
  try {
    const v = JSON.parse(localStorage.getItem(KEY) || '[]');
    return Array.isArray(v) ? (v as OutboxItem[]) : [];
  } catch {
    return [];
  }
}

function writeAll(items: OutboxItem[]): void {
  localStorage.setItem(KEY, JSON.stringify(items));
  listeners.forEach((l) => l());
}

export function outboxList(chatId: string): OutboxItem[] {
  return readAll().filter((i) => i.chatId === chatId).sort((a, b) => a.createdAt - b.createdAt);
}

export function outboxAdd(item: Omit<OutboxItem, 'id' | 'createdAt'>): OutboxItem {
  const full: OutboxItem = { ...item, id: `OUT-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, createdAt: Date.now() };
  writeAll([...readAll(), full]);
  return full;
}

export function outboxRemove(id: string): void {
  writeAll(readAll().filter((i) => i.id !== id));
}

// Changes in this tab and in other tabs of the same browser.
export function outboxSubscribe(l: () => void): () => void {
  listeners.add(l);
  const onStorage = (e: StorageEvent) => {
    if (e.key === KEY) l();
  };
  window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(l);
    window.removeEventListener('storage', onStorage);
  };
}

export function watchOnline(setOnline: (v: boolean) => void): () => void {
  const on = () => setOnline(true);
  const off = () => setOnline(false);
  window.addEventListener('online', on);
  window.addEventListener('offline', off);
  return () => {
    window.removeEventListener('online', on);
    window.removeEventListener('offline', off);
  };
}

// Takes an item out before sending it, so a second tab cannot send it too; false = already taken.
export function outboxTake(id: string): boolean {
  const all = readAll();
  if (!all.some((i) => i.id === id)) return false;
  writeAll(all.filter((i) => i.id !== id));
  return true;
}

// Puts an item back after a failed send (keeps its place by createdAt).
export function outboxPut(item: OutboxItem): void {
  writeAll([...readAll().filter((i) => i.id !== item.id), item]);
}
