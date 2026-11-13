// SPDX-License-Identifier: AGPL-3.0-or-later
// Typed access to window.coExt (/ext/ext1.js, modules/EXT1.md): the only way this page reaches files and the chat.
//
// Load order: viewer.html loads viewer.js BEFORE /ext/ext1.js. ext1.js assigns window.coExt and says hello in the same
// script, and CO Web answers with the init and right after it the first 'chat' message. ext1.js does not keep a
// 'chat' message for handlers registered later, so a handler must exist before the hello: the setter below registers
// it the moment ext1.js assigns window.coExt. (With viewer.js after ext1.js, the first 'chat' message arrived while the
// big bundle was still loading and was lost: no markers and Ask disabled until the chat changed.)

export interface ExtContext {
  ext: number;
  module: string;
  params?: Record<string, string>;
  chat?: { id: string | null; title: string } | null;
  theme?: Record<string, string>;
}

// The events of the viewer's chat as the core sends them ('chat' messages, permission chat.read).
export interface ChatEvent {
  eventId: string;
  type: string; // user_message | assistant_message
  text?: string;
  status?: string;
  createdUtc?: string;
  jobId?: string;
}
export interface ChatState { events: ChatEvent[]; canAsk: boolean }

export interface FileGot {
  name: string;
  mimeType: string;
  id: string;
  md5?: string;
  modifiedTime?: string;
  size?: number;
  bytes: ArrayBuffer;
}

export interface Sibling {
  id: string;
  name: string;
  appProperties?: Record<string, string>;
  bytes: ArrayBuffer | null; // null when asked withBytes: false
}

export interface AskFile {
  name: string;
  type: string;
  bytes: ArrayBuffer;
}

interface CoExt {
  ready: Promise<ExtContext>;
  req: (name: string, args?: unknown, transfer?: Transferable[]) => Promise<unknown>;
  on: (type: string, fn: (data: unknown) => void) => void;
}

let api: CoExt | null = null;
let lastChat: ChatState | null = null;
const chatFns = new Set<(d: ChatState) => void>();
let gotApi: (c: CoExt) => void = () => undefined;
const apiReady = new Promise<CoExt>((res) => { gotApi = res; });

function hook(c: CoExt) {
  if (api) return;
  api = c;
  c.on('chat', (d) => {
    lastChat = d as ChatState;
    chatFns.forEach((fn) => fn(lastChat!));
  });
  gotApi(c);
}

{
  const w = window as unknown as { coExt?: CoExt };
  if (w.coExt) hook(w.coExt); // loaded after ext1.js: works, but may miss the first 'chat' message
  else {
    let held: CoExt | undefined;
    Object.defineProperty(window, 'coExt', {
      configurable: true,
      get: () => held,
      set: (v: CoExt) => { held = v; hook(v); },
    });
  }
}

function ext(): CoExt {
  if (!api) throw new Error('The ext/1 client (/ext/ext1.js) is not loaded.');
  return api;
}

export const ready = (): Promise<ExtContext> => apiReady.then((c) => c.ready);
// fn gets the latest state at once when there is one, then every change
export function onChat(fn: (d: ChatState) => void): () => void {
  chatFns.add(fn);
  if (lastChat) fn(lastChat);
  return () => { chatFns.delete(fn); };
}

export const fileGet = (path: string) => ext().req('file.get', { path }) as Promise<FileGot>;
export const filePdf = (path: string) => ext().req('file.pdf', { path }) as Promise<{ bytes: ArrayBuffer; note?: string }>;
export const fileSibling = (path: string, name: string, withBytes: boolean) =>
  ext().req('file.sibling', { path, name, withBytes }) as Promise<Sibling | null>;
// appProperties: the core replaces an existing sibling only when the FIRST key has the same value (studyOf first)
export const filePutSibling = (path: string, name: string, bytes: ArrayBuffer, mimeType: string, appProperties: Record<string, string>) =>
  ext().req('file.putSibling', { path, name, bytes, mimeType, appProperties }, [bytes]) as Promise<{ id: string }>;
// files travel as ArrayBuffers in the transfer list (the page does not keep them)
export const chatAsk = (text: string, files: AskFile[]) =>
  ext().req('chat.ask', { text, files }, files.map((f) => f.bytes)) as Promise<{ ok: boolean }>;
export const chatReveal = (eventId: string) => ext().req('chat.reveal', { eventId });
export const panelClose = () => ext().req('panel.close', {});
