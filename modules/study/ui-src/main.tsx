// SPDX-License-Identifier: AGPL-3.0-or-later
// Module study: entry of the viewer panel (ui/viewer.html, slot ui.panel of the mode "study"). The core opens it next to
// a chat with ?study=<path>; the page gets the path in ctx.params.study, the chat's events in 'chat' messages and does
// everything else through ext/1 requests (coExt.ts). This script runs before /ext/ext1.js (see coExt.ts).
//
// The pdf.js worker module is imported first: it defines globalThis.pdfjsWorker.WorkerMessageHandler, and pdf.js then
// runs its "fake worker" on the main thread instead of new Worker(url). A sandboxed frame (opaque origin) cannot load a
// module worker from this site without CORS, and the CSP (default-src 'none') forbids blob: workers.
import 'pdfjs-dist/build/pdf.worker.mjs';
import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import StudyViewer from './StudyViewer';
import { chatAsk, chatReveal, onChat, panelClose, ready, type ChatState } from './coExt';

function App({ path }: { path: string }) {
  const [chat, setChat] = useState<ChatState | null>(null);
  useEffect(() => onChat(setChat), []);
  return (
    <StudyViewer
      path={path}
      events={chat ? chat.events : null}
      canAsk={!!chat?.canAsk}
      onAsk={(text, files) => chatAsk(text, files)}
      onOpenAnswer={(id) => { void chatReveal(id).catch(() => undefined); }}
      onClose={() => { void panelClose().catch(() => undefined); }}
    />
  );
}

ready().then((ctx) => {
  const el = document.getElementById('root')!;
  const path = String(ctx.params?.study || '');
  if (!path) {
    el.textContent = 'No document: the viewer opens with ?study=<path of a file>.';
    return;
  }
  createRoot(el).render(<App path={path} />);
}).catch((e: Error) => { document.body.textContent = e.message; });
