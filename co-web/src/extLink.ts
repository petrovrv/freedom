// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.62.0: link to the AI Workbench browser extension panel that frames CO (see extDevice.ts). No imports:
// auth.ts uses it too.

// Unpacked and store builds share this id: manifest "key" in the extension's manifest.json.
export const EXT_ORIGIN = 'chrome-extension://eobijkacekfneefkngaejdcdaehfijaf';

// The panel loads /?ext=1; the flag survives CO's own navigation inside the frame.
export function inExtension(): boolean {
  if (window.parent === window) return false;
  try {
    if (new URLSearchParams(location.search).get('ext') === '1') sessionStorage.setItem('co.ext', '1');
    return sessionStorage.getItem('co.ext') === '1';
  } catch {
    return false;
  }
}

// Sign-in cannot run inside a frame (Google refuses to be framed): the panel opens it in a tab.
export function openInTab(path: string): void {
  window.parent.postMessage({ type: 'co-ext-open', path }, EXT_ORIGIN);
}

inExtension(); // record the ?ext=1 flag before the router rewrites the address
