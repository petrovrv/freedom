// SPDX-License-Identifier: AGPL-3.0-or-later
// AI Workbench extension: page side. Injected on demand into every frame of the tab the agent works in; runs in the
// extension's isolated world, so page scripts cannot reach it. panel.js calls globalThis.__coExt.<fn>(...) through
// chrome.scripting.executeScript. Every function returns plain data; errors come back as { error: { code, message } }.
// Classification and sensitive-field rules are ported from CO Desktop (AgentPageScripts.cs) and browser-agent.
(() => {
  if (globalThis.__coExt) return;

  const refs = new Map(); // ref -> WeakRef(element)
  const refOfEl = new WeakMap();
  let seq = 0;
  const refOf = (el) => {
    let r = refOfEl.get(el);
    if (!r) { r = 'n' + (++seq); refOfEl.set(el, r); }
    refs.set(r, new WeakRef(el));
    return r;
  };
  class PageError extends Error { constructor(code, message) { super(message); this.code = code; } }
  const byRef = (r) => {
    const el = refs.get(String(r))?.deref();
    if (!el || !el.isConnected) throw new PageError('stale_ref', `Element ${r} is gone; call browser_snapshot again.`);
    return el;
  };
  const wrap = (fn) => (...a) => {
    try { return fn(...a); } catch (e) {
      return { error: { code: e instanceof PageError ? e.code : 'page_error', message: String(e?.message || e) } };
    }
  };

  const clip = (s, n) => { s = String(s ?? '').replace(/\s+/g, ' ').trim(); return s.length > n ? s.slice(0, n - 1) + '…' : s; };
  const q = (s) => JSON.stringify(s);

  // ---------- names and roles ----------
  const INTERACTIVE_ROLES = new Set(['button', 'link', 'checkbox', 'radio', 'tab', 'menuitem', 'menuitemcheckbox',
    'menuitemradio', 'option', 'switch', 'combobox', 'textbox', 'searchbox', 'listbox', 'treeitem', 'slider', 'spinbutton']);

  function roleOf(el) {
    const role = (el.getAttribute('role') || '').trim().split(/\s+/)[0].toLowerCase();
    if (role && INTERACTIVE_ROLES.has(role)) return role;
    const tag = el.tagName;
    if (tag === 'A' && el.hasAttribute('href')) return 'link';
    if (tag === 'BUTTON' || tag === 'SUMMARY') return 'button';
    if (tag === 'SELECT') return 'select';
    if (tag === 'TEXTAREA') return 'textbox';
    if (tag === 'INPUT') {
      const t = (el.type || 'text').toLowerCase();
      if (t === 'hidden') return null;
      if (['button', 'submit', 'reset', 'image'].includes(t)) return 'button';
      if (t === 'checkbox' || t === 'radio' || t === 'file' || t === 'range' || t === 'color') return t;
      return 'textbox';
    }
    if (el.isContentEditable && (el.parentElement == null || !el.parentElement.isContentEditable)) return 'textbox';
    if (el.hasAttribute('onclick')) return 'button';
    return null;
  }

  function textOfIds(ids, doc) {
    return ids.split(/\s+/).map((id) => doc.getElementById(id)?.innerText || '').join(' ');
  }

  function nameOf(el) {
    const doc = el.ownerDocument;
    const lb = el.getAttribute('aria-labelledby');
    if (lb) { const t = clip(textOfIds(lb, doc), 120); if (t) return t; }
    const al = el.getAttribute('aria-label');
    if (al && al.trim()) return clip(al, 120);
    if (el.labels && el.labels.length) { const t = clip(el.labels[0].innerText, 120); if (t) return t; }
    const tag = el.tagName;
    if (tag === 'INPUT' && ['button', 'submit', 'reset'].includes((el.type || '').toLowerCase())) return clip(el.value, 120);
    if (tag === 'INPUT' || tag === 'TEXTAREA') {
      return clip(el.placeholder || el.getAttribute('title') || el.name || '', 120);
    }
    if (el.isContentEditable) return clip(el.getAttribute('title') || el.getAttribute('data-placeholder') || '', 120);
    const t = clip(el.innerText, 120);
    if (t) return t;
    const img = el.querySelector?.('img[alt],svg[aria-label],[title]');
    return clip(el.getAttribute('title') || img?.getAttribute('alt') || img?.getAttribute('aria-label') || img?.getAttribute('title') || '', 120);
  }

  // ---------- sensitive fields (never typed into by the agent) ----------
  function sensitive(el) {
    const type = String(el.type || el.getAttribute?.('type') || '').toLowerCase();
    const ac = String(el.getAttribute?.('autocomplete') || '').toLowerCase();
    if (type === 'password') return 'password field';
    if (ac.split(/\s+/).some((t) => t.startsWith('cc-'))) return 'payment field';
    if (ac.split(/\s+/).includes('one-time-code')) return 'one-time code field';
    const re = /card.?num|cvc|cvv|csc|security.?code|iban|password|passcode|wachtwoord|пароль|one.?time|otp|verification.?code|\bpin\b/i;
    const lab = el.labels && el.labels[0] ? el.labels[0].innerText : '';
    for (const v of [el.getAttribute?.('name'), el.id, ac, el.getAttribute?.('aria-label'), el.getAttribute?.('placeholder'), lab]) {
      if (v && re.test(v)) return 'sensitive field (' + clip(v, 40) + ')';
    }
    return null;
  }

  // ---------- classification of clicks ----------
  function collectFields(form) {
    const out = [];
    for (const f of form.querySelectorAll('input,select,textarea')) {
      const type = String(f.type || f.tagName.toLowerCase()).toLowerCase();
      if (['hidden', 'submit', 'button', 'image'].includes(type)) continue;
      if (out.length >= 30) break;
      const label = (f.labels && f.labels[0] && f.labels[0].innerText) || f.getAttribute('aria-label') || f.name || f.placeholder || f.id || '';
      const value = sensitive(f) ? '***'
        : (type === 'checkbox' || type === 'radio') ? f.checked
        : type === 'file' ? Array.from(f.files || []).map((x) => x.name).join(', ')
        : clip(f.value, 200);
      out.push({ label: clip(label, 80), type, value });
    }
    return out;
  }

  const sendRe = /\b(send|post|publish|tweet|reply|share|comment|message)\b|verstuur|verzend|plaats|publiceer|senden|envoyer|отправ|опубликов|ответить|написать/iu;
  const purchaseRe = /\b(pay|buy|order|checkout|purchase|subscribe|book)\b|bestel|betaal|koop|afrekenen|kaufen|bezahlen|купить|оплат|заказ|оформить/iu;
  const deleteRe = /\b(delete|remove|cancel subscription|unsubscribe|close account|discard)\b|verwijder|löschen|supprimer|удалить|удалени/iu;
  const finalRe = /\b(submit|apply|confirm|finish|complete|sign up|register|done)\b|bevestig|registreer|indienen|solliciteer|absenden|bewerben|подтверд|зарегистр|подать|завершить/iu;
  const nextRe = /\b(next|continue|proceed|forward|save and continue|save & continue)\b|volgende|doorgaan|verder|weiter|suivant|continuer|далее|продолжить|следующ|вперёд|вперед/iu;
  const saveRe = /\b(save)\b|opslaan|speichern|сохранить/iu;
  const HARMLESS = new Set(['search', 'find', 'filter', 'sort', 'show more', 'load more', 'see all', 'expand', 'collapse',
    'close', 'dismiss', 'menu', 'back', 'previous', 'accept', 'accept all', 'accept cookies', 'reject cookies', 'accept all cookies',
    'reject all cookies', 'reject all', 'allow all', 'allow all cookies', 'allow cookies', 'agree', 'i agree', 'i accept', 'got it', 'ok',
    'only necessary', 'necessary only', 'essential only', 'play', 'pause', 'mute', 'unmute', 'zoom in', 'zoom out', 'language',
    'zoeken', 'zoek', 'sluiten', 'meer', 'toon meer', 'taal', 'akkoord', 'accepteren', 'alles accepteren', 'alle akzeptieren',
    'akzeptieren', 'tout accepter', 'accepter', 'поиск', 'найти', 'закрыть', 'свернуть', 'развернуть', 'меню', 'язык',
    'показать ещё', 'показать еще', 'ещё', 'еще', 'принять', 'принять все', 'назад']);

  function classify(el) {
    // A label activates its control: classify the control (a <label for> a submit button submits).
    const lab = el.closest('label');
    if (lab && lab.control && lab.control !== el && !lab.control.contains(el)) return classify(lab.control);
    const btn = el.closest('button,input,[role=button],a,[role=link],[role=menuitem],[role=tab]') || el;
    const text = clip(btn.innerText || btn.value || btn.getAttribute('aria-label') || '', 80);
    const form = btn.form || btn.closest('form');
    const tag = btn.tagName;
    const type = String(btn.getAttribute('type') || '').toLowerCase();
    const label = [text, btn.getAttribute('aria-label') || '', btn.getAttribute('title') || '', btn.getAttribute('data-tooltip') || '', tag === 'INPUT' ? btn.value : ''].join(' ');
    const isFormSubmitter = (tag === 'BUTTON' && !!form && (!btn.hasAttribute('type') || type === 'submit')) ||
      (tag === 'INPUT' && (type === 'submit' || type === 'image'));
    let category = null;
    if (sendRe.test(label)) category = 'send';
    else if (purchaseRe.test(label)) category = 'purchase';
    else if (deleteRe.test(label)) category = 'delete';
    else if (finalRe.test(label)) category = 'submit';
    // A step of a multi-page form runs without asking, even when the button submits the step to the server (owner's
    // rule, 29.10). Final words (submit, apply, confirm, send, pay, delete) are checked first and always ask.
    else if (nextRe.test(label)) category = 'next';
    else if (isFormSubmitter || saveRe.test(label)) category = 'submit';
    else {
      const norm = (s) => String(s ?? '').toLowerCase().replace(/\s+/g, ' ').replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '').trim();
      const parts = [...new Set([text, btn.getAttribute('aria-label'), btn.getAttribute('title'), tag === 'INPUT' ? btn.value : ''].map(norm).filter(Boolean))];
      const harmless = parts.length > 0 && parts.every((p) => HARMLESS.has(p));
      const roles = String(btn.getAttribute('role') || '').toLowerCase().split(/\s+/);
      const buttonLike = tag === 'BUTTON' || (tag === 'INPUT' && ['button', 'submit', 'image', 'reset'].includes(type)) || roles.includes('button');
      let actionLike = false;
      if (tag === 'A') {
        const href = (btn.getAttribute('href') || '').trim().toLowerCase();
        actionLike = href === '' || href.startsWith('#') || href.startsWith('java' + 'script:');
      } else if (!['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'LABEL'].includes(tag)) actionLike = true;
      if (tag === 'INPUT' && ['checkbox', 'radio'].includes(type)) actionLike = false;
      if ((buttonLike || actionLike) && !harmless) category = 'click';
    }
    const role = btn.getAttribute('role');
    const description = [tag.toLowerCase(), role ? 'role=' + role : '', text ? q(text) : ''].filter(Boolean).join(' ');
    return { category, description, fields: form ? collectFields(form) : [] };
  }

  // Category of Enter in a field: the form's submit button, or "send" for a message box outside a form.
  function enterCategory(el) {
    const form = el?.form || el?.closest?.('form');
    if (form) {
      const sub = form.querySelector('button[type=submit],input[type=submit],input[type=image],button:not([type])');
      if (sub) { const c = classify(sub).category; return c === 'click' || c === null ? 'submit' : c; }
      return 'submit';
    }
    if (el && (el.tagName === 'TEXTAREA' || el.isContentEditable)) return 'send';
    if (el && el.tagName === 'INPUT' && /search/i.test((el.type || '') + ' ' + (el.name || '') + ' ' + (el.getAttribute('role') || '') + ' ' + (el.getAttribute('aria-label') || ''))) return null;
    return el && el.tagName === 'INPUT' ? 'submit' : null;
  }

  // ---------- challenges ----------
  function challenge() {
    try {
      const vis = (el) => el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0;
      const captchaSrc = /recaptcha|hcaptcha|challenges\.cloudflare\.com|turnstile|arkoselabs|funcaptcha/i;
      for (const f of document.querySelectorAll('iframe')) if (captchaSrc.test(f.src || '') && vis(f)) return 'captcha';
      if (document.querySelector('.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form')) return 'captcha';
      const inputs = [...document.querySelectorAll('input')].filter(vis);
      const otp = /otp|one.?time|2fa|totp|mfa|verification.?code/i;
      if (inputs.some((i) => (i.getAttribute('autocomplete') || '').toLowerCase() === 'one-time-code' || otp.test(i.name || '') || otp.test(i.id || ''))) return 'one-time code (MFA)';
      if (inputs.some((i) => (i.type || '').toLowerCase() === 'password')) return 'password';
      const card = /card.?number|cvc|cvv/i;
      if (inputs.some((i) => (i.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).some((t) => t.startsWith('cc-')) || card.test(i.name || '') || card.test(i.id || ''))) return 'payment';
    } catch { /* ignore */ }
    return null;
  }

  // ---------- snapshot ----------
  const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'HEAD', 'META', 'LINK', 'svg', 'SVG', 'CANVAS', 'VIDEO', 'AUDIO', 'OBJECT', 'EMBED']);
  const BLOCK = new Set(['DIV', 'P', 'LI', 'UL', 'OL', 'DL', 'DT', 'DD', 'TR', 'TABLE', 'TBODY', 'THEAD', 'SECTION', 'ARTICLE',
    'HEADER', 'FOOTER', 'NAV', 'MAIN', 'ASIDE', 'FORM', 'FIELDSET', 'LEGEND', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BR', 'HR',
    'BLOCKQUOTE', 'PRE', 'FIGURE', 'FIGCAPTION', 'DETAILS', 'DIALOG', 'LABEL', 'TD', 'TH', 'CAPTION', 'ADDRESS']);

  function visible(el) {
    if (el.hidden) return false;
    if (el.getAttribute('aria-hidden') === 'true') return false;
    if (el.checkVisibility && el.checkVisibility({ visibilityProperty: true })) return true;
    const cs = getComputedStyle(el); // display:contents has no box of its own, its children may be visible
    if (cs.display === 'contents') return cs.visibility !== 'hidden';
    if (el.checkVisibility) return false;
    return cs.display !== 'none' && cs.visibility !== 'hidden';
  }

  function describe(el, role) {
    const r = refOf(el);
    const name = nameOf(el);
    const bits = ['[' + r + ']', role];
    if (name) bits.push(q(name));
    const tag = el.tagName;
    if (role === 'link') {
      const href = el.getAttribute('href') || '';
      if (href && !href.startsWith('java' + 'script:')) bits.push('-> ' + clip(href, 100));
    } else if (role === 'textbox' || role === 'searchbox' || role === 'combobox' || role === 'spinbutton') {
      if (sensitive(el)) bits.push('(' + sensitive(el) + ', agent may not type here)');
      else {
        const v = el.isContentEditable ? el.innerText : el.value;
        if (v) bits.push('value=' + q(clip(v, el.isContentEditable ? 2000 : 300)));
      }
      if (tag === 'INPUT' && el.type && !['text', 'search'].includes(el.type)) bits.push('type=' + el.type);
    } else if (role === 'select') {
      const opts = Array.from(el.options || []);
      const sel = opts.filter((o) => o.selected).map((o) => clip(o.textContent, 60));
      bits.push('= ' + q(sel.join(', ')));
      bits.push('options: ' + opts.slice(0, 40).map((o) => clip(o.textContent, 40)).join(' | ') + (opts.length > 40 ? ' | …(' + opts.length + ')' : ''));
    } else if (role === 'checkbox' || role === 'radio' || role === 'switch') {
      const checked = tag === 'INPUT' ? el.checked : el.getAttribute('aria-checked') === 'true';
      bits.push(checked ? 'checked' : 'unchecked');
    } else if (role === 'file') {
      const names = Array.from(el.files || []).map((f) => f.name).join(', ');
      bits.push(names ? 'files=' + q(names) : 'no file');
      if (el.accept) bits.push('accept=' + el.accept);
    } else if (role === 'tab' || role === 'option' || role === 'treeitem') {
      if (el.getAttribute('aria-selected') === 'true') bits.push('selected');
    }
    if (el.getAttribute('aria-expanded') === 'true') bits.push('expanded');
    if (el.disabled || el.getAttribute('aria-disabled') === 'true') bits.push('disabled');
    if (el.required || el.getAttribute('aria-required') === 'true') bits.push('required');
    if (el.getAttribute('aria-invalid') === 'true') bits.push('invalid');
    if (el === el.ownerDocument.activeElement) bits.push('focused');
    return bits.join(' ');
  }

  function snapshot(maxChars) {
    // A same-origin child frame is already inside its parent's snapshot.
    if (window.top !== window) {
      try { if (window.parent.document) return null; } catch { /* cross-origin: own section */ }
      if (innerWidth < 30 || innerHeight < 30) return null;
    }
    const lines = [];
    let cur = '';
    let size = 0;
    let truncated = false;
    const limit = Math.max(1000, maxChars || 20000);
    const flush = () => {
      const t = cur.replace(/\s+/g, ' ').trim();
      cur = '';
      if (!t) return;
      if (size + t.length > limit) { truncated = true; return; }
      lines.push(t); size += t.length + 1;
    };
    const push = (line) => { flush(); if (size + line.length > limit) { truncated = true; return; } lines.push(line); size += line.length + 1; };

    const walk = (node, depth) => {
      if (truncated || depth > 200) return;
      if (node.nodeType === 3) { cur += ' ' + node.nodeValue; return; }
      if (node.nodeType !== 1 && node.nodeType !== 11) return;
      const el = node;
      if (node.nodeType === 1) {
        if (SKIP.has(el.tagName) || el.id === '__co_ext_overlay') return;
        if (!visible(el)) {
          // Custom-styled checkboxes/radios/file inputs are often visually hidden but still the real control.
          if (!(el.tagName === 'INPUT' && ['checkbox', 'radio', 'file'].includes((el.type || '').toLowerCase()))) return;
        }
        if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
          let inner = null;
          try { inner = el.contentDocument; } catch { inner = null; }
          if (inner && inner.body) { flush(); walk(inner.body, depth + 1); flush(); }
          else push('[frame ' + clip(el.title || el.getAttribute('name') || el.src || '', 80) + ' (see its own section)]');
          return;
        }
        if (el.tagName === 'IMG') {
          const alt = el.getAttribute('alt');
          if (alt && alt.trim()) cur += ' [image: ' + clip(alt, 80) + ']';
          return;
        }
        const role = roleOf(el);
        if (role) { push(describe(el, role)); return; }
        const h = /^H([1-6])$/.exec(el.tagName);
        if (h) { push('#'.repeat(+h[1]) + ' ' + clip(el.innerText, 200)); return; }
        if (el.getAttribute('role') === 'alert' || el.getAttribute('aria-live') === 'assertive') {
          const t = clip(el.innerText, 300); if (t) push('! ' + t); return;
        }
      }
      const block = node.nodeType === 1 && BLOCK.has(el.tagName);
      if (block) flush();
      if (node.nodeType === 1 && el.shadowRoot) walk(el.shadowRoot, depth + 1);
      else if (node.nodeType === 1 && el.tagName === 'SLOT') for (const c of el.assignedNodes({ flatten: true })) walk(c, depth + 1);
      else for (const c of node.childNodes) walk(c, depth + 1);
      if (block) flush();
    };
    if (document.body) walk(document.body, 0);
    flush();
    return {
      url: location.href, title: document.title, top: window.top === window,
      text: lines.join('\n'), truncated, challenge: challenge(),
      scroll: { y: Math.round(scrollY), height: document.documentElement.scrollHeight, view: innerHeight },
    };
  }

  // ---------- actions ----------
  function center(el) {
    el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
    const r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height };
  }

  // Only text controls take typed text: typing into a button, select or checkbox would change its label or state
  // behind the confirmation rules.
  const TEXT_TYPES = ['text', 'search', 'email', 'url', 'tel', 'number', 'date', 'datetime-local', 'month', 'time', 'week'];
  function textField(el) {
    if (el.readOnly || el.disabled) return false;
    if (el.tagName === 'TEXTAREA') return true;
    if (el.tagName === 'INPUT') return TEXT_TYPES.includes(String(el.type || 'text').toLowerCase());
    return el.isContentEditable === true;
  }

  function inspect(ref) {
    const el = byRef(ref);
    const c = classify(el);
    const role = roleOf(el);
    return { ...c, role, sensitive: sensitive(el), name: nameOf(el), url: location.href, textField: textField(el) };
  }

  function click(ref) {
    const el = byRef(ref);
    const { x, y } = center(el);
    const hit = el.ownerDocument.elementFromPoint(x, y);
    let covered = null;
    if (hit && hit !== el && !el.contains(hit) && !hit.contains(el)) covered = clip(nameOf(hit) || hit.tagName.toLowerCase(), 80);
    const view = el.ownerDocument.defaultView;
    const opts = { bubbles: true, cancelable: true, composed: true, view, clientX: x, clientY: y, button: 0, buttons: 1 };
    el.dispatchEvent(new PointerEvent('pointerover', opts));
    el.dispatchEvent(new MouseEvent('mouseover', opts));
    el.dispatchEvent(new PointerEvent('pointerdown', opts));
    el.dispatchEvent(new MouseEvent('mousedown', opts));
    if (typeof el.focus === 'function') el.focus({ preventScroll: true });
    el.dispatchEvent(new PointerEvent('pointerup', { ...opts, buttons: 0 }));
    el.dispatchEvent(new MouseEvent('mouseup', { ...opts, buttons: 0 }));
    el.click();
    return { ok: true, covered };
  }

  // For a real (trusted) click: the point must hit the element itself, not something laid over it.
  function rectOf(ref) {
    const el = byRef(ref);
    const c = center(el);
    const hit = el.ownerDocument.elementFromPoint(c.x, c.y);
    if (hit && hit !== el && !el.contains(hit) && !hit.contains(el)) {
      throw new PageError('covered', 'Element ' + ref + ' is covered by ' + q(clip(nameOf(hit) || hit.tagName.toLowerCase(), 80)) +
        ' (a banner or dialog?). Close it first or click that element by its own ref.');
    }
    // Same-origin child frames are part of this frame's snapshot: add their offsets up to this window.
    let w = el.ownerDocument.defaultView;
    // At every level the point must land on the frame itself, not on a parent-page overlay.
    while (w && w !== window && w.frameElement) {
      const f = w.frameElement; const r = f.getBoundingClientRect();
      c.x += r.left + f.clientLeft; c.y += r.top + f.clientTop;
      if (f.ownerDocument.elementFromPoint(c.x, c.y) !== f) throw new PageError('covered', 'The frame of element ' + ref + ' is covered at the click point; close the overlay first.');
      w = w.parent;
    }
    return c;
  }

  // Cross-origin chain (panel.js pointOf): the translated point must hit that frame element in this document.
  function frameAt(x, y, src) {
    const hit = document.elementFromPoint(x, y);
    return !!hit && (hit.tagName === 'IFRAME' || hit.tagName === 'FRAME') && hit.src === src;
  }

  function frameRects() {
    return Array.from(document.querySelectorAll('iframe,frame')).map((f) => {
      const r = f.getBoundingClientRect();
      return { src: f.src || '', x: r.left + f.clientLeft, y: r.top + f.clientTop, w: r.width, h: r.height };
    });
  }

  function focusRef(ref) {
    const el = byRef(ref);
    center(el);
    el.focus({ preventScroll: true });
    if (el.isContentEditable) {
      const sel = el.ownerDocument.getSelection();
      const range = el.ownerDocument.createRange();
      range.selectNodeContents(el); range.collapse(false);
      sel.removeAllRanges(); sel.addRange(range);
    }
    return { ok: true };
  }

  function type(ref, text, append) {
    const el = byRef(ref);
    const s = sensitive(el);
    if (s) throw new PageError('sensitive_field', 'This is a ' + s + '; the agent never types there. Ask the user to fill it in the tab.');
    if (!textField(el)) throw new PageError('not_editable', 'Element ' + ref + ' is not an editable text field.');
    center(el);
    el.focus({ preventScroll: true });
    const doc = el.ownerDocument;
    if (el.isContentEditable) {
      const sel = doc.getSelection();
      const range = doc.createRange();
      range.selectNodeContents(el);
      if (append) range.collapse(false);
      sel.removeAllRanges(); sel.addRange(range);
      const lines = String(text).split(/\r?\n/);
      let ok = true;
      lines.forEach((line, i) => {
        if (i > 0) ok = doc.execCommand('insertLineBreak') && ok;
        if (line) ok = doc.execCommand('insertText', false, line) && ok;
      });
      if (!ok) { // editors without execCommand support
        if (!append) el.textContent = '';
        el.append(doc.createTextNode(String(text)));
        el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: String(text) }));
      }
      return { ok: true, value: clip(el.innerText, 200) };
    }
    if (!textField(el)) throw new PageError('not_editable', 'Element ' + ref + ' is not an editable text field.');
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    const next = append ? el.value + String(text) : String(text);
    if (setter) setter.call(el, next); else el.value = next;
    el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: String(text) }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, value: clip(el.value, 200) };
  }

  function select(ref, want) {
    const el = byRef(ref);
    if (el.tagName !== 'SELECT') throw new PageError('not_select', 'Element ' + ref + ' is not a <select>; open it with browser_click and click the option.');
    const norm = (s) => String(s ?? '').replace(/\s+/g, ' ').trim().toLowerCase();
    const options = Array.from(el.options);
    const raw = String(want ?? ''); const t = norm(want);
    const m = options.find((o) => o.value === raw) || options.find((o) => norm(o.textContent) === t) || options.find((o) => norm(o.value) === t)
      || options.find((o) => norm(o.textContent).includes(t) && t.length > 2);
    if (!m || m.disabled) throw new PageError('no_option', 'No option ' + q(raw) + '. Options: ' + options.slice(0, 40).map((o) => clip(o.textContent, 40)).join(' | '));
    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
    if (setter) setter.call(el, m.value); else el.value = m.value;
    m.selected = true;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, value: m.value, text: clip(m.textContent, 80) };
  }

  function upload(ref, files) {
    const el = byRef(ref);
    if (el.tagName !== 'INPUT' || (el.type || '').toLowerCase() !== 'file') throw new PageError('not_file_input', 'Element ' + ref + ' is not a file input.');
    const dt = new DataTransfer();
    for (const f of files) {
      const bin = atob(f.dataBase64);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      dt.items.add(new File([bytes], f.name, { type: f.mime || 'application/octet-stream' }));
    }
    el.files = dt.files;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return { ok: true, files: Array.from(el.files).map((f) => f.name + ' (' + f.size + ' bytes)') };
  }

  function focusInfo() {
    let el = document.activeElement;
    while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
    const hasFocus = document.hasFocus();
    if (!el || el === document.body) return { category: null, description: 'page', fields: [], editable: false, hasFocus };
    // Focus inside a child frame: the key goes to that frame, which this one cannot classify.
    if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') return { category: null, description: 'frame', fields: [], editable: false, hasFocus, inFrame: true };
    const editable = el.isContentEditable || el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && roleOf(el) === 'textbox');
    const c = classify(el);
    const form = el.form || el.closest('form');
    return { category: editable ? null : c.category, enter: enterCategory(el), description: c.description || clip(nameOf(el), 80),
      fields: form ? collectFields(form) : [], editable, sensitive: sensitive(el), hasFocus };
  }

  function scroll(dy, ref) {
    if (ref) { byRef(ref).scrollIntoView({ block: 'center', behavior: 'instant' }); return { ok: true }; }
    const before = scrollY;
    window.scrollBy({ top: dy, behavior: 'instant' });
    if (scrollY !== before) return { ok: true };
    // App layouts (mail, dashboards) scroll an inner container: the scrollable ancestor at the viewport center.
    let el = document.elementFromPoint(innerWidth / 2, innerHeight / 2);
    while (el && el !== document.documentElement) {
      const cs = getComputedStyle(el);
      if (/(auto|scroll)/.test(cs.overflowY) && el.scrollHeight > el.clientHeight + 4) { el.scrollBy({ top: dy, behavior: 'instant' }); return { ok: true }; }
      el = el.parentElement;
    }
    return { ok: true, note: 'nothing scrolled' };
  }

  // ---------- overlay: orange frame while the agent works in this tab ----------
  let host = null; let bar = null; let text = null;
  function overlay(on, paused) {
    if (window.top !== window) return { ok: true };
    if (!on) { host?.remove(); host = null; return { ok: true }; }
    if (!host) {
      host = document.createElement('div');
      host.id = '__co_ext_overlay';
      host.setAttribute('aria-hidden', 'true');
      host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;inset:0;pointer-events:none;';
      const root = host.attachShadow({ mode: 'closed' });
      const frame = document.createElement('div');
      frame.style.cssText = 'position:fixed;inset:0;border:3px solid #e8590c;pointer-events:none;box-sizing:border-box;';
      bar = document.createElement('div');
      bar.style.cssText = 'position:fixed;top:0;left:50%;transform:translateX(-50%);pointer-events:auto;background:#e8590c;color:#fff;' +
        'font:13px system-ui;padding:4px 10px;border-radius:0 0 8px 8px;display:flex;gap:8px;align-items:center;';
      text = document.createElement('span');
      const b = document.createElement('button');
      b.type = 'button';
      b.style.cssText = 'font:inherit;color:#fff;background:transparent;border:1px solid #fff;border-radius:4px;padding:1px 8px;cursor:pointer;';
      b.addEventListener('click', (ev) => {
        if (!ev.isTrusted) return;
        ev.stopPropagation();
        chrome.runtime.sendMessage({ type: 'co-ext-pause-toggle' }).catch(() => {});
      });
      for (const t of ['pointerdown', 'mousedown', 'mouseup', 'pointerup']) bar.addEventListener(t, (ev) => ev.stopPropagation());
      bar.append(text, b);
      root.append(frame, bar);
      host._frame = frame; host._button = b;
      document.documentElement.appendChild(host);
    }
    if (!host.isConnected) document.documentElement.appendChild(host);
    const color = paused ? '#2b8a3e' : '#e8590c';
    host._frame.style.borderColor = color; bar.style.background = color;
    text.textContent = paused ? 'Agent paused - you control this tab' : 'AI Workbench agent works in this tab';
    host._button.textContent = paused ? 'Resume agent' : 'Pause';
    return { ok: true };
  }

  globalThis.__coExt = {
    snapshot: wrap(snapshot), inspect: wrap(inspect), click: wrap(click), rectOf: wrap(rectOf), frameRects: wrap(frameRects), frameAt: wrap(frameAt),
    focusRef: wrap(focusRef), type: wrap(type), select: wrap(select), upload: wrap(upload), focusInfo: wrap(focusInfo),
    scroll: wrap(scroll), overlay: wrap(overlay), challenge: wrap(challenge), ping: () => true,
  };
})();
