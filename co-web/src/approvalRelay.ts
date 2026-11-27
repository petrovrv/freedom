// SPDX-License-Identifier: AGPL-3.0-or-later
// Generic encrypted approval transport. No private preview/result is sent to dbapi or persisted in the browser.
type Call = (phase: string, envelope?: Record<string, string>) => Promise<any>;
const bytes = (s: string) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
const b64 = (b: ArrayBuffer | Uint8Array) => { let s = ''; for (const c of new Uint8Array(b)) s += String.fromCharCode(c); return btoa(s); };
const text = new TextEncoder();
export async function approvalRelay(call: Call, expectedRequestId: string) {
  const s = await call('status');
  if (!s.ok) throw new Error(s.error || 'relay_unavailable');
  if (s.requestId !== expectedRequestId || !/^[0-9a-f]{32}$/.test(s.revision)
      || ['env', 'app', 'action', 'version', 'pub'].some(k => typeof s[k] !== 'string' || !s[k])) throw new Error('approval_mismatch');
  const crypto = globalThis.crypto.subtle;
  const mine = await crypto.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const pk = b64(await crypto.exportKey('raw', mine.publicKey));
  const peer = await crypto.importKey('raw', bytes(s.pub), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await crypto.deriveBits({ name: 'ECDH', public: peer }, mine.privateKey, 256);
  const ikm = await crypto.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  let previewToken = '';
  const perform = async (phase: 'preview' | 'run' | 'result') => {
    if (phase === 'run' && !previewToken) throw new Error('preview_required');
    const id = Array.from(globalThis.crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
    const key = (direction: string) => crypto.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: text.encode(`${s.env}|${s.app}|${id}`),
      info: text.encode(`freedom-home-relay/1 ${direction}`) }, ikm, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
    const marker = { phase, requestId: s.requestId, revision: s.revision, ...(phase === 'run' ? { previewToken } : {}) };
    if (phase === 'run') previewToken = ''; // consume locally before any ambiguous network failure
    const plain = { action: s.action, args: {}, ts: Date.now(), _app: { version: s.version }, _approval: marker };
    const ct = await crypto.encrypt({ name: 'AES-GCM', iv, additionalData: text.encode(id) }, await key('req'), text.encode(JSON.stringify(plain)));
    // No retry: after Run a lost response is ambiguous, and must be reconciled by encrypted result polling.
    const r = await call(phase, { id, pk, iv: b64(iv), ct: b64(ct) });
    if (!r.ok) throw new Error(r.error || 'relay_failed');
    const decrypted = await crypto.decrypt({ name: 'AES-GCM', iv: bytes(r.envelope.iv), additionalData: text.encode(id) }, await key('res'), bytes(r.envelope.ct));
    const result = JSON.parse(new TextDecoder().decode(decrypted));
    if (!result.ok) throw new Error(result.error || 'approval_refused');
    const value = result.result;
    if (value?.requestId !== s.requestId || value?.revision !== s.revision) throw new Error('approval_mismatch');
    if (phase === 'preview') {
      if (value.action !== s.action || value.version !== s.version || typeof value.previewToken !== 'string' || !value.args || typeof value.args !== 'object' || Array.isArray(value.args)) throw new Error('approval_mismatch');
      previewToken = value.previewToken;
    } else if (!['pending', 'executing', 'done', 'failed', 'expired'].includes(value.state)) throw new Error('approval_mismatch');
    return value;
  };
  return { preview: () => perform('preview'), run: () => perform('run'), result: () => perform('result') };
}
