// SPDX-License-Identifier: AGPL-3.0-or-later
// The Mail page's end of the sealed channel to the person's mail worker (home/relay.py is the other end).
// ECDH P-256 (session key of this page, the worker's key from mail_status) -> HKDF-SHA256 (salt "<env>|<id>", info
// "freedom-mail-relay/1 req|res") -> AES-256-GCM, 12-byte nonce, AAD = id. The site carries ciphertext only.
//   const relay = MailRelay(callOp, env); await relay.call('list', {label: 'INBOX'})  -> result or throws Error(code)
(() => {
'use strict';
const subtle = globalThis.crypto && globalThis.crypto.subtle;
const enc = new TextEncoder(), dec = new TextDecoder();
const b64 = buf => { const b = new Uint8Array(buf); let s = ''; for (let i = 0; i < b.length; i += 0x8000) s += String.fromCharCode.apply(null, b.subarray(i, i + 0x8000)); return btoa(s); };
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
const hex = n => Array.from(globalThis.crypto.getRandomValues(new Uint8Array(n)), x => x.toString(16).padStart(2, '0')).join('');

function MailRelay(callOp, env) {
  if (!subtle) throw new Error('no_webcrypto');
  let mine = null, pkB64 = '', peer = null, peerB64 = '';
  async function session() {
    if (!mine) {
      mine = await subtle.generateKey({name: 'ECDH', namedCurve: 'P-256'}, false, ['deriveBits']);
      pkB64 = b64(await subtle.exportKey('raw', mine.publicKey));
    }
  }
  async function worker(pub) {
    if (pub !== peerB64) {
      peer = await subtle.importKey('raw', unb64(pub), {name: 'ECDH', namedCurve: 'P-256'}, false, []);
      peerB64 = pub;
    }
  }
  async function key(id, dir) {
    const shared = await subtle.deriveBits({name: 'ECDH', public: peer}, mine.privateKey, 256);
    const ikm = await subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
    return subtle.deriveKey({name: 'HKDF', hash: 'SHA-256', salt: enc.encode(env + '|' + id), info: enc.encode('freedom-mail-relay/1 ' + dir)},
      ikm, {name: 'AES-GCM', length: 256}, false, ['encrypt', 'decrypt']);
  }
  async function seal(id, value) {
    const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
    const ct = await subtle.encrypt({name: 'AES-GCM', iv, additionalData: enc.encode(id)}, await key(id, 'req'), enc.encode(JSON.stringify(value)));
    return {id, pk: pkB64, iv: b64(iv), ct: b64(ct)};
  }
  async function open(id, ans) {
    const plain = await subtle.decrypt({name: 'AES-GCM', iv: unb64(ans.iv), additionalData: enc.encode(id)}, await key(id, 'res'), unb64(ans.ct));
    return JSON.parse(dec.decode(plain));
  }
  // pub: the worker's public key (mail_status.pub); refreshed by the caller when the worker restarts with a new key
  async function call(action, args, pub, timeoutMs) {
    await session();
    await worker(pub);
    const id = hex(16);
    const ans = await callOp('mail_call', await seal(id, {action, args: args || {}, ts: Date.now()}), {timeoutMs: timeoutMs || 20000});
    if (!ans || ans.error) throw new Error((ans && ans.error) || 'no_answer');
    const out = await open(id, ans);
    if (!out.ok) throw new Error(out.error || 'worker_error');
    return out.result;
  }
  return {call, _seal: async (id, v, pub) => { await session(); await worker(pub); return seal(id, v); }, _open: open};
}
if (typeof module !== 'undefined' && module.exports) module.exports = MailRelay; else globalThis.MailRelay = MailRelay;
})();
