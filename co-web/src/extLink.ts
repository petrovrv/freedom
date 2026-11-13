// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.62.0: link to the AI Workbench browser extension panel that frames CO (see extDevice.ts). No imports:
// auth.ts uses it too.

// Freedom F10 (ext/1 provides.client): the extensions come from the modules of the tree at build time (vite.config.ts);
// an extension's unpacked and store builds share one id (manifest "key"). The panel that frames this page is one of them.
declare const __CO_EXT_ORIGINS__: string[] | undefined;
const EXT_ORIGINS: string[] = typeof __CO_EXT_ORIGINS__ !== 'undefined' ? __CO_EXT_ORIGINS__ : [];

function parentOrigin(): string {
  if (window.parent === window) return '';
  const a = (location as Location & { ancestorOrigins?: DOMStringList }).ancestorOrigins;
  const o = a && a.length > 0 ? a[0] : '';
  return EXT_ORIGINS.includes(o) ? o : EXT_ORIGINS.length === 1 ? EXT_ORIGINS[0] : '';
}

// Origin of the extension panel framing CO ('' = none known: messages to it are not sent).
export const EXT_ORIGIN: string = parentOrigin();

// The panel loads /?ext=1; the flag survives CO's own navigation inside the frame.
export function inExtension(): boolean {
  if (window.parent === window || !EXT_ORIGIN) return false;
  try {
    if (new URLSearchParams(location.search).get('ext') === '1') sessionStorage.setItem('co.ext', '1');
    return sessionStorage.getItem('co.ext') === '1';
  } catch {
    return false;
  }
}

// Sign-in cannot run inside a frame (Google refuses to be framed): the panel opens it in a tab.
export function openInTab(path: string): void {
  if (EXT_ORIGIN) window.parent.postMessage({ type: 'co-ext-open', path }, EXT_ORIGIN);
}

inExtension(); // record the ?ext=1 flag before the router rewrites the address
