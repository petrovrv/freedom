// SPDX-License-Identifier: AGPL-3.0-or-later
// Small helpers: IDs, hashing, filenames, time.

const ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

function randomPart(len: number): string {
  const bytes = crypto.getRandomValues(new Uint8Array(len));
  let s = '';
  for (const b of bytes) s += ALPHABET[b % 32];
  return s;
}

function stamp(): string {
  // yyyymmddHHMMss in UTC, sortable
  return new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
}

// All IDs satisfy the Bridge rule: ^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$
export function newJobId(): string {
  return `JOB-${stamp()}-${randomPart(6)}`;
}
export function newChatId(): string {
  return `CHAT-${stamp()}-${randomPart(4)}`;
}
export function newEventId(): string {
  return `EVT-${stamp()}-${randomPart(6)}`;
}

export const SAFE_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/;

export async function sha256Hex(data: ArrayBuffer | Uint8Array): Promise<string> {
  const buf = data instanceof Uint8Array ? data : new Uint8Array(data);
  const digest = await crypto.subtle.digest('SHA-256', buf as BufferSource);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

const RESERVED = /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$/i;

// Windows-safe file name that keeps Unicode (Cyrillic round trip is a test case).
export function safeFileName(name: string): string {
  let s = name.normalize('NFC').replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_');
  s = s.replace(/[. ]+$/g, '');
  if (!s) s = 'file';
  if (RESERVED.test(s)) s = '_' + s;
  if (s.length > 120) {
    const dot = s.lastIndexOf('.');
    const ext = dot > 0 && s.length - dot <= 10 ? s.slice(dot) : '';
    s = s.slice(0, 120 - ext.length) + ext;
  }
  return s;
}

export function uniqueNames(names: string[]): string[] {
  const seen = new Map<string, number>();
  return names.map((n) => {
    const key = n.toLowerCase();
    const count = seen.get(key) || 0;
    seen.set(key, count + 1);
    if (count === 0) return n;
    const dot = n.lastIndexOf('.');
    return dot > 0 ? `${n.slice(0, dot)} (${count})${n.slice(dot)}` : `${n} (${count})`;
  });
}

export function nowUtc(): string {
  return new Date().toISOString();
}

export function formatLocal(utc?: string | null): string {
  if (!utc) return '';
  const d = new Date(utc);
  if (isNaN(d.getTime())) return utc;
  return d.toLocaleString(undefined, {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

export function formatBytes(n?: number | null): string {
  if (n == null) return '';
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

// Run async tasks with bounded concurrency to stay polite with the Drive API.
export async function mapLimit<T, R>(items: T[], limit: number, fn: (t: T) => Promise<R>): Promise<R[]> {
  const out: R[] = new Array(items.length);
  let i = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (i < items.length) {
      const idx = i++;
      out[idx] = await fn(items[idx]);
    }
  });
  await Promise.all(workers);
  return out;
}
