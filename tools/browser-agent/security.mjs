// security.mjs - URL and form-safety guards for a Playwright-based agent browser (Node 24, ESM).

// Parse a strict dotted-quad IPv4 literal into 4 octets, or return null.
function parseIPv4(h) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(h);
  if (!m) return null;
  const o = m.slice(1).map(Number);
  return o.some((n) => n > 255) ? null : o;
}

// Private, loopback, "this network" and link-local IPv4 ranges.
function isBlockedIPv4([a, b]) {
  return a === 127 || a === 0 || a === 10 ||
    (a === 172 && b >= 16 && b <= 31) ||
    (a === 192 && b === 168) ||
    (a === 169 && b === 254);
}

// Expand an IPv6 literal (incl. embedded IPv4 tail) into 8 numeric groups, or return null.
function parseIPv6(h) {
  if (!h.includes(':')) return null;
  let s = h.split('%')[0]; // drop zone id
  const last = s.lastIndexOf(':');
  const tail = s.slice(last + 1);
  if (tail.includes('.')) {
    const v4 = parseIPv4(tail);
    if (!v4) return null;
    s = s.slice(0, last + 1) + ((v4[0] << 8) | v4[1]).toString(16) + ':' + ((v4[2] << 8) | v4[3]).toString(16);
  }
  const parts = s.split('::');
  if (parts.length > 2) return null;
  const head = parts[0] ? parts[0].split(':') : [];
  const rest = parts.length === 2 && parts[1] ? parts[1].split(':') : [];
  const fill = 8 - head.length - rest.length;
  if (fill < 0 || (parts.length === 1 && fill !== 0)) return null;
  const groups = [...head, ...Array(parts.length === 2 ? fill : 0).fill('0'), ...rest];
  if (groups.length !== 8 || groups.some((g) => !/^[0-9a-f]{1,4}$/.test(g))) return null;
  return groups.map((g) => parseInt(g, 16));
}

function isBlockedIPv6(g) {
  const zeros = (n) => g.slice(0, n).every((x) => x === 0);
  if (zeros(8)) return true;                                   // ::
  if (zeros(7) && g[7] === 1) return true;                     // ::1
  if ((g[0] & 0xfe00) === 0xfc00) return true;                 // fc00::/7
  if ((g[0] & 0xffc0) === 0xfe80) return true;                 // fe80::/10
  if (zeros(5) && g[5] === 0xffff) {                           // ::ffff:a.b.c.d
    return isBlockedIPv4([g[6] >> 8, g[6] & 0xff, g[7] >> 8, g[7] & 0xff]);
  }
  return false;
}

function normalizeHost(hostname) {
  return String(hostname ?? '').trim().toLowerCase().replace(/^\[|\]$/g, '').replace(/\.+$/, '');
}

// True for loopback, private, link-local and CGNAT addresses (checked against the address a response came from).
export function isPrivateAddress(ip) {
  const h = normalizeHost(ip);
  const v4 = parseIPv4(h);
  if (v4) {
    const [a, b] = v4;
    // includes CGNAT 100.64.0.0/10
    return !!(isBlockedIPv4(v4) || (a === 100 && b >= 64 && b <= 127));
  }
  const v6 = parseIPv6(h);
  if (v6) return !!isBlockedIPv6(v6);
  return false;
}

// True for hosts that must never be reached, regardless of allowlist. No DNS lookups.
export function isAlwaysBlockedHost(hostname) {
  const h = normalizeHost(hostname);
  if (!h) return true;
  if (h === 'localhost' || h.endsWith('.localhost') || h.endsWith('.local')) return true;
  const v4 = parseIPv4(h);
  if (v4) return isBlockedIPv4(v4);
  const v6 = parseIPv6(h);
  if (v6) return isBlockedIPv6(v6);
  return false;
}

// '*' matches any host; 'example.com' matches itself and any subdomain.
export function hostAllowed(hostname, allow) {
  if (!Array.isArray(allow)) return false;
  const h = normalizeHost(hostname);
  if (!h) return false;
  return allow.some((raw) => {
    const p = String(raw ?? '').trim().toLowerCase().replace(/^\*\./, '').replace(/^\.+|\.+$/g, '');
    if (p === '*') return true;
    return !!p && (h === p || h.endsWith('.' + p));
  });
}

const WEB_SCHEMES = new Set(['http:', 'https:', 'ws:', 'wss:']);

// Returns null if the URL may be loaded, otherwise a short reason string.
// block (0.7.0): owner's blocked sites (agent-browser.json Block), applied to documents and subresources.
export function checkUrl(urlString, allow, isDocument, block = []) {
  let u;
  try { u = new URL(urlString); } catch { return 'invalid url'; }
  if (isDocument) {
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'scheme not allowed';
    if (isAlwaysBlockedHost(u.hostname)) return 'private or local address';
    if (block.length && hostAllowed(u.hostname, block)) return 'site is in the blocked sites list';
    return hostAllowed(u.hostname, allow) ? null : 'domain not in allowlist';
  }
  if (!WEB_SCHEMES.has(u.protocol)) return null; // data:, blob:, etc. for subresources
  if (isAlwaysBlockedHost(u.hostname)) return 'private or local address';
  return block.length && hostAllowed(u.hostname, block) ? 'site is in the blocked sites list' : null;
}

// Route every request in the context through checkUrl; fail closed on internal errors.
// isHostBlocked: hosts that were seen resolving to a private address (DNS rebinding) stay blocked.
// target: a Page (0.7.0, own tabs only) or a BrowserContext; getBlock: blocked sites list.
export async function installGuard(target, getAllow, onBlock, isHostBlocked, getBlock = () => []) {
  await target.route('**/*', async (route, req) => {
    let reason;
    const isDocument = req.resourceType() === 'document';
    let isMainFrame = isDocument;
    try { const f = req.frame(); isMainFrame = isDocument && f === f.page().mainFrame(); } catch { /* worker or detached frame */ }
    try {
      let hostname = '';
      try { hostname = new URL(req.url()).hostname; } catch { /* ignore bad url */ }
      if (hostname && isHostBlocked?.(hostname)) {
        reason = 'host resolved to a private address';
      } else {
        reason = checkUrl(req.url(), getAllow(), isDocument, getBlock());
      }
    } catch {
      reason = 'guard error';
    }
    try {
      if (reason) {
        try { onBlock?.({ url: req.url(), reason, isDocument, isMainFrame }); } catch { /* ignore callback errors */ }
        await route.abort('blockedbyclient');
      } else {
        await route.continue();
      }
    } catch { /* page or context closed */ }
  });
}

// Shared field collector; serialized into the page as source text.
// Detect password / payment / banking fields.
export async function inspectField(locator) {
  return locator.evaluate((el) => {
    const re = /card|cvc|cvv|csc|security.?code|iban|password|passcode|passwd|\bpass\b|\bpwd?\b|wachtwoord|passwort|kennwort|mot.?de.?passe|contrase|\u043f\u0430\u0440\u043e\u043b\u044c|one.?time|otp|verification.?code|\bpin\b/i;
    const type = String(el.getAttribute('type') || el.type || '').toLowerCase();
    if (type === 'password') return { sensitive: true, reason: 'password field' };
    // autocomplete tokens
    const ac = String(el.getAttribute('autocomplete') || '').toLowerCase();
    const tokens = ac.split(/\s+/).filter(Boolean);
    if (tokens.some((t) => t.startsWith('cc-'))) return { sensitive: true, reason: 'payment autocomplete' };
    // attribute / label pattern checks
    const label = el.labels && el.labels[0] ? el.labels[0].innerText : '';
    // Some ATS (Worley/Phenom) put autocomplete=one-time-code on ordinary contact fields only to stop browser
    // autofill; such a field is not a code when its own name/id/label clearly says name, phone, address etc.
    const plain = /name|phone|tel|mobile|address|street|city|town|zip|post|country|e.?mail|company|employer|job.?title/i;
    const hint = [el.getAttribute('name'), el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder'), label].join(' ');
    if (tokens.includes('one-time-code') && !(plain.test(hint) && !/code|otp|pin|token|verif/i.test(hint.replace(/zip.?code|post.?code|country.?code/ig, ''))))
      return { sensitive: true, reason: 'one-time-code autocomplete' };
    const fields = {
      name: el.getAttribute('name'),
      id: el.id,
      autocomplete: tokens.filter((t) => t !== 'one-time-code').join(' '), // decided above
      'aria-label': el.getAttribute('aria-label'),
      placeholder: el.getAttribute('placeholder'),
      label,
    };
    for (const [k, v] of Object.entries(fields)) {
      if (v && re.test(String(v))) return { sensitive: true, reason: `${k} matches sensitive pattern` };
    }
    return { sensitive: false, reason: '' };
  });
}

// Decide whether clicking the element likely submits a form or triggers a consequential action.
export async function inspectSubmit(locator) {
  return locator.evaluate((el) => {
    const collectFields = (form) => {
      const out = [];
      for (const f of form.querySelectorAll('input,select,textarea')) {
        const type = String(f.type || f.tagName.toLowerCase()).toLowerCase();
        if (['hidden', 'submit', 'button', 'image'].includes(type)) continue;
        if (out.length >= 30) break;
        const label = (f.labels && f.labels[0] && f.labels[0].innerText) || f.getAttribute('aria-label') ||
          f.name || f.placeholder || f.id || '';
        const value = type === 'password' ? '***'
          : (type === 'checkbox' || type === 'radio') ? f.checked
          : String(f.value ?? '').slice(0, 200);
        out.push({ label: String(label).trim(), type, value });
      }
      return out;
    };
    const btn = el.closest('button,input,[role=button],a') || el;
    const text = (btn.innerText || btn.value || btn.getAttribute('aria-label') || '').trim().slice(0, 80);
    const form = btn.form || btn.closest('form');
    const tag = btn.tagName;
    const type = String(btn.getAttribute('type') || '').toLowerCase();
    // Combine visible text and accessible attributes for classification
    const label = [text, btn.getAttribute('aria-label') || '', btn.getAttribute('title') || '', btn.value || ''].join(' ');
    // English words use \b; \b does not work for Cyrillic, so Cyrillic/Dutch parts have none
    const sendRe = /\b(send|post|publish|tweet|reply|share|comment|message)\b|verstuur|verzend|plaats|publiceer|отправ|опубликов|ответить|написать/iu;
    const purchaseRe = /\b(pay|buy|order|checkout|purchase|subscribe|book)\b|bestel|betaal|koop|afrekenen|купить|оплат|заказ|оформить/iu;
    const deleteRe = /\b(delete|remove|cancel subscription|unsubscribe|close account)\b|verwijder|удалить|удалени/iu;
    const submitRe = /\b(submit|confirm|sign up|register|apply|save)\b|bevestig|registreer|подтверд|сохранить|зарегистр/iu;
    // Element that would submit its form natively
    const isFormSubmitter = (tag === 'BUTTON' && !!form && (!btn.hasAttribute('type') || type === 'submit')) ||
      (tag === 'INPUT' && (type === 'submit' || type === 'image'));
    // Specific categories take precedence over the generic 'submit'
    // Job-application forms (0.8.4): "Verzend/Verstuur/Send" there submits the owner's application,
    // not a message. Either the label names the application, or the form uploads a file (CV)
    // on a page that is clearly a vacancy/application page. Then it is 'apply' and needs --confirm.
    const applyWordRe = /sollicit|application|candidat|bewerbung|apply|cv|resume|résumé|отклик|резюме|заявк/iu;
    const pageCtx = [location.href, document.title, (document.querySelector('h1') || {}).innerText || ''].join(' ');
    const jobPageRe = /sollicit|vacature|vacanc|job|career|carri|apply|application|candidat|recruit|werving|bewerb/iu;
    const hasFileInput = !!(form || document).querySelector('input[type=file]');
    const isApplication = applyWordRe.test(label) ||
      (jobPageRe.test(pageCtx) && (hasFileInput || applyWordRe.test(pageCtx) ||
        applyWordRe.test(String((form || document.body || {}).innerText || '').slice(0, 20000))));
    let category = null;
    if (sendRe.test(label) && isApplication && !/(message|reply|comment|share|tweet|post|publish)|bericht|publiceer|plaats|сообщен|ответить|опубликов/iu.test(label)) category = 'apply';
    else if (sendRe.test(label)) category = 'send';
    else if (purchaseRe.test(label)) category = 'purchase';
    else if (deleteRe.test(label)) category = 'delete';
    else if (isFormSubmitter || submitRe.test(label)) category = 'submit';
    else {
      // Any other button may still act (its handler can buy or send): only exact, known harmless UI labels pass.
      const t = String(type || '').toLowerCase();
      const roles = String(btn.getAttribute('role') || '').toLowerCase().split(/\s+/);
      const buttonLike = tag === 'BUTTON' ||
        (tag === 'INPUT' && ['button', 'submit', 'image', 'reset'].includes(t)) ||
        roles.includes('button');
      // Links without a real target (no href, '#', script URL) and other clickable elements act through scripts.
      const href = String(btn.getAttribute('href') || '').trim().toLowerCase();
      const actionLike =
        (tag === 'A' && (href === '' || href.startsWith('#') || href.startsWith('java' + 'script:'))) ||
        !['A', 'BUTTON', 'INPUT'].includes(tag);
      if (buttonLike || actionLike) {
        const harmless = new Set([
          'search', 'find', 'filter', 'sort', 'show more', 'load more', 'see all',
          'expand', 'collapse', 'close', 'dismiss', 'menu',
          'accept cookies', 'reject cookies', 'accept all cookies', 'reject all cookies',
          'allow all cookies', 'allow cookies',
          'play', 'pause', 'mute', 'unmute', 'zoom in', 'zoom out', 'language',
          'zoeken', 'zoek', 'sluiten', 'meer', 'toon meer', 'taal',
          '\u043f\u043e\u0438\u0441\u043a', '\u043d\u0430\u0439\u0442\u0438', '\u0437\u0430\u043a\u0440\u044b\u0442\u044c',
          '\u0441\u0432\u0435\u0440\u043d\u0443\u0442\u044c', '\u0440\u0430\u0437\u0432\u0435\u0440\u043d\u0443\u0442\u044c',
          '\u043c\u0435\u043d\u044e', '\u044f\u0437\u044b\u043a',
          '\u043f\u043e\u043a\u0430\u0437\u0430\u0442\u044c \u0435\u0449\u0451', '\u043f\u043e\u043a\u0430\u0437\u0430\u0442\u044c \u0435\u0449\u0435',
          '\u0435\u0449\u0451', '\u0435\u0449\u0435',
        ]);
        const norm = (s) => String(s || '')
          .toLowerCase()
          .replace(/\s+/g, ' ')
          .replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '')
          .trim();
        const parts = [...new Set([
          text,
          btn.getAttribute('aria-label'),
          btn.getAttribute('title'),
          'value' in btn ? btn.value : '',
        ].map(norm).filter(Boolean))];
        const isHarmless = parts.length > 0 && parts.every((p) => harmless.has(p));
        if (!isHarmless) category = 'click';
      }
    }
    const isSubmit = category !== null;
    const role = btn.getAttribute('role');
    const description = [tag.toLowerCase(), role ? `role=${role}` : '', text ? `"${text}"` : ''].filter(Boolean).join(' ');
    return { isSubmit, category, description, fields: form ? collectFields(form) : [] };
  });
}

// Report whether the focused element sits inside a form (e.g. before pressing Enter).
export async function activeElementSubmitInfo(page) {
  return page.evaluate(() => {
    const collectFields = (form) => {
      const out = [];
      for (const f of form.querySelectorAll('input,select,textarea')) {
        const type = String(f.type || f.tagName.toLowerCase()).toLowerCase();
        if (['hidden', 'submit', 'button', 'image'].includes(type)) continue;
        if (out.length >= 30) break;
        const label = (f.labels && f.labels[0] && f.labels[0].innerText) || f.getAttribute('aria-label') ||
          f.name || f.placeholder || f.id || '';
        const value = type === 'password' ? '***'
          : (type === 'checkbox' || type === 'radio') ? f.checked
          : String(f.value ?? '').slice(0, 200);
        out.push({ label: String(label).trim(), type, value });
      }
      return out;
    };
    const ae = document.activeElement;
    const form = ae && typeof ae.closest === 'function' ? ae.closest('form') : null;
    return { inForm: !!form, fields: form ? collectFields(form) : [] };
  });
}
