// SPDX-License-Identifier: AGPL-3.0-or-later
// CO service worker: caches the app shell only. Google APIs and Drive data are never cached.
const CACHE = 'co-shell-v8';

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(['/', '/manifest.webmanifest', '/icon.svg', '/icon-192.png', '/favicon.ico'])));
  self.skipWaiting();
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== self.location.origin) return;
  // Lab pages are never cached and never replace the app shell.
  if (url.pathname.startsWith('/lab')) return;
  // Sign-in endpoints carry cookies and redirects; they must never be cached as the shell.
  if (url.pathname.startsWith('/auth/')) return;
  if (e.request.mode === 'navigate') {
    // Network first for the shell, cached copy when offline.
    e.respondWith(
      fetch(e.request)
        .then((r) => {
          const copy = r.clone();
          caches.open(CACHE).then((c) => c.put('/', copy));
          return r;
        })
        .catch(() => caches.match('/')),
    );
    return;
  }
  if (url.pathname.startsWith('/assets/')) {
    // Hashed, immutable build assets.
    e.respondWith(
      caches.match(e.request).then(
        (hit) => hit || fetch(e.request).then((r) => {
          const copy = r.clone();
          caches.open(CACHE).then((c) => c.put(e.request, copy));
          return r;
        }),
      ),
    );
  }
});

// --- Web Push (CO Web 0.13.0) ---

self.addEventListener('push', (e) => {
  let data;
  try {
    data = e.data?.json();
  } catch (err) {
    data = { title: 'CO', body: e.data?.text() };
  }
  data = data || {};

  e.waitUntil((async () => {
    // Skip if the user is already looking at CO (test pushes and, 0.81.0, confirmations of risky actions always show).
    // 0.101.1: a push of a chat (tag = chat id) is skipped only while that same chat is on screen; 11.11 the answers of
    // three subchats were dropped because another CO chat was open on the phone.
    const chatTag = /^CHAT-/.test(String(data.tag || '')) ? String(data.tag) : '';
    const list = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const active = list.some((c) => {
      try {
        const u = new URL(c.url);
        if (u.origin !== self.location.origin || c.visibilityState !== 'visible' || !c.focused) return false;
        return !chatTag || u.pathname.includes('/c/' + chatTag);
      } catch (err) {
        return false;
      }
    });
    if (active && data.tag !== 'co-test' && !String(data.tag || '').startsWith('action-')) return;

    await self.registration.showNotification(data.title || 'CO', {
      body: data.body,
      tag: data.tag || undefined,
      renotify: !!data.tag,
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      data: { url: data.url || '/' },
    });
  })());
});

// 0.83.1: ask an open CO window to route to the chat in place (App.tsx answers on the port); false if it does not answer in time
function askAppToOpen(client, url) {
  return new Promise((resolve) => {
    const ch = new MessageChannel();
    const timer = setTimeout(() => resolve(false), 1500);
    ch.port1.onmessage = () => {
      clearTimeout(timer);
      resolve(true);
    };
    try {
      const u = new URL(url);
      client.postMessage({ type: 'co-open', path: u.pathname + u.search + u.hash }, [ch.port2]);
    } catch (err) {
      clearTimeout(timer);
      resolve(false);
    }
  });
}

self.addEventListener('notificationclick', (e) => {
  e.notification.close();
  const url = new URL(e.notification.data?.url || '/', self.location.origin).href;

  e.waitUntil((async () => {
    const list = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const same = list.filter((c) => {
      try {
        return new URL(c.url).origin === self.location.origin;
      } catch (err) {
        return false;
      }
    });
    const c = same.find((x) => x.focused) || same.find((x) => x.visibilityState === 'visible') || same[0];
    if (!c) return self.clients.openWindow(url);

    // 0.83.1: focus first, while the click still allows it. Android Chrome (PWA) drops that right during an awaited
    // navigate() of a frozen background window, so the old order brought CO forward on its previous page.
    try {
      await c.focus();
    } catch (err) {
      return self.clients.openWindow(url);
    }
    if (c.url === url) return;
    if (await askAppToOpen(c, url)) return;
    if ('navigate' in c) {
      try {
        if (await c.navigate(url)) return;
      } catch (err) { /* uncontrolled client */ }
    }
    return self.clients.openWindow(url);
  })());
});

self.addEventListener('pushsubscriptionchange', (e) => {
  e.waitUntil((async () => {
    const key = e.oldSubscription?.options.applicationServerKey;
    let sub = e.newSubscription;
    if (!sub) {
      if (!key) return;
      sub = await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
    }
    // No bearer token in the SW; the CO Web session cookie identifies the user
    await fetch('/agent/push.php', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CO-Auth': '1' },
      credentials: 'same-origin',
      body: JSON.stringify({ op: 'subscribe', subscription: sub.toJSON() }),
    });
  })());
});

// --- Web Share Target (CO Web 0.14.0): stash POST /share-target in IndexedDB, then open the app ---
(() => {
  const DB_NAME = 'co-share';
  const STORE = 'shares';

  function openDb() {
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

  function put(db, record) {
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(record);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  }

  async function handleShare(request) {
    try {
      const form = await request.formData();
      const str = (key) => {
        const v = form.get(key);
        return typeof v === 'string' && v.trim() ? v.trim() : undefined;
      };
      const files = form.getAll('files').filter((f) => f instanceof File && f.size > 0);
      const record = {
        id: Date.now().toString(36) + Math.random().toString(36).slice(2, 10),
        createdUtc: new Date().toISOString(),
        title: str('title'),
        text: str('text'),
        url: str('url'),
        files,
      };
      const db = await openDb();
      try {
        await put(db, record);
      } finally {
        db.close();
      }
    } catch (err) {
      // Always land the user in the app
    }
    return Response.redirect(new URL('/', self.location.origin).href, 303);
  }

  self.addEventListener('fetch', (e) => {
    if (e.request.method !== 'POST') return;
    const url = new URL(e.request.url);
    if (url.origin !== self.location.origin || url.pathname !== '/share-target') return;
    e.respondWith(handleShare(e.request));
  });
})();
