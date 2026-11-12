// overlay.mjs - "agent controls this tab" overlay rendered from a CDP isolated world.
// The page shares the DOM but cannot reach the isolated world's globals or the binding.

const WORLD = 'co-agent-ui';
const BINDING = 'coAgentCtl';

// Runs inside the isolated world. Serialized into OVERLAY_SOURCE below.
function overlayMain() {
  if (window.__coAgentUi) return; // guard against double init
  if (window.top !== window) return; // main frame only, no overlay inside iframes

  const ACTIVE = '#e8590c';
  const PAUSED = '#2b8a3e';
  const state = { paused: false, reason: '' };
  let host = null, frame = null, bar = null, text = null, button = null;

  function render() {
    if (!bar) return;
    const color = state.paused ? PAUSED : ACTIVE;
    frame.style.borderColor = color;
    bar.style.background = color;
    text.textContent = state.paused
      ? 'You control this tab' + (state.reason ? ' - ' + state.reason : '')
      : 'AI agent controls this tab';
    button.textContent = state.paused ? 'Return to agent' : 'Take over';
  }

  function build() {
    host = document.createElement('div');
    host.setAttribute('aria-hidden', 'true'); // keeps it out of the agent's accessibility snapshot
    host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;inset:0;pointer-events:none;';
    const root = host.attachShadow({ mode: 'closed' }); // page cannot reach host.shadowRoot

    frame = document.createElement('div');
    frame.style.cssText = 'position:fixed;inset:0;border:3px solid ' + ACTIVE +
      ';pointer-events:none;box-sizing:border-box;';

    bar = document.createElement('div');
    bar.style.cssText = 'position:fixed;top:0;left:50%;transform:translateX(-50%);pointer-events:auto;' +
      'background:' + ACTIVE + ';color:#fff;font:13px system-ui;padding:4px 10px;' +
      'border-radius:0 0 8px 8px;display:flex;gap:8px;align-items:center;';

    text = document.createElement('span');
    button = document.createElement('button');
    button.type = 'button';
    button.style.cssText = 'font:inherit;color:#fff;background:transparent;border:1px solid #fff;' +
      'border-radius:4px;padding:1px 8px;cursor:pointer;';

    button.addEventListener('click', (ev) => {
      if (!ev.isTrusted) return; // synthetic page clicks are ignored
      ev.stopPropagation();
      if (typeof window.coAgentCtl === 'function') window.coAgentCtl(state.paused ? 'resume' : 'pause');
      state.paused = !state.paused;
      state.reason = '';
      render();
    });
    // Do not leak pointer interaction on the bar to page listeners.
    for (const type of ['pointerdown', 'mousedown', 'mouseup', 'pointerup']) {
      bar.addEventListener(type, (ev) => ev.stopPropagation());
    }

    bar.append(text, button);
    root.append(frame, bar);
    render();
  }

  function ensureAttached() {
    const docEl = document.documentElement;
    if (docEl && !host.isConnected) docEl.appendChild(host); // documentElement, not body
  }

  function mount() {
    build();
    ensureAttached();
    let observedRoot = document.documentElement;
    const rootObserver = new MutationObserver(ensureAttached);
    rootObserver.observe(observedRoot, { childList: true });
    // Handle documentElement replacement (document.open/write, root swaps).
    new MutationObserver(() => {
      if (document.documentElement && document.documentElement !== observedRoot) {
        observedRoot = document.documentElement;
        rootObserver.disconnect();
        rootObserver.observe(observedRoot, { childList: true });
      }
      ensureAttached();
    }).observe(document, { childList: true });
  }

  window.__coAgentUi = {
    set(paused, reason) {
      state.paused = !!paused;
      state.reason = reason ? String(reason) : '';
      render();
    },
  };

  if (document.documentElement) {
    mount();
  } else {
    const waiter = new MutationObserver(() => {
      if (!document.documentElement) return;
      waiter.disconnect();
      mount();
    });
    waiter.observe(document, { childList: true });
  }
}

const OVERLAY_SOURCE = `(${overlayMain.toString()})();`;

export async function attachOverlay(page, ctl) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Runtime.enable');
  await cdp.send('Page.enable');
  // Binding is exposed only in contexts named WORLD, never in the page main world.
  await cdp.send('Runtime.addBinding', { name: BINDING, executionContextName: WORLD });

  const state = { paused: !!ctl.isPaused?.(), reason: '' };

  cdp.on('Runtime.bindingCalled', (e) => {
    if (e.name !== BINDING) return;
    try {
      if (e.payload === 'pause') {
        state.paused = true;
        state.reason = '';
        ctl.onPause('user took over');
      } else if (e.payload === 'resume') {
        state.paused = false;
        state.reason = '';
        ctl.onResume();
      }
    } catch { /* controller errors must not break the CDP session */ }
  });

  // Evaluate an expression in the isolated world of the current main-frame document.
  async function evalInUiWorld(expression) {
    const { frameTree } = await cdp.send('Page.getFrameTree');
    const { executionContextId } = await cdp.send('Page.createIsolatedWorld', {
      frameId: frameTree.frame.id,
      worldName: WORLD,
    });
    await cdp.send('Runtime.evaluate', { expression, contextId: executionContextId });
  }

  const stateExpr = () =>
    `window.__coAgentUi && window.__coAgentUi.set(${JSON.stringify(state.paused)}, ${JSON.stringify(state.reason || '')})`;

  await cdp.send('Page.addScriptToEvaluateOnNewDocument', {
    source: OVERLAY_SOURCE,
    worldName: WORLD,
    runImmediately: true,
  });
  try { await evalInUiWorld(OVERLAY_SOURCE + ';' + stateExpr()); } catch { /* about:blank etc. */ }

  async function refresh() {
    try { await evalInUiWorld(OVERLAY_SOURCE + ';' + stateExpr()); } catch { /* ignore */ }
  }

  // New documents start in the default (active) state; re-apply after main-frame navigation.
  cdp.on('Page.frameNavigated', (e) => { if (!e.frame.parentId) refresh(); });

  return {
    async setPaused(paused, reason) {
      state.paused = !!paused;
      state.reason = reason || '';
      try { await evalInUiWorld(stateExpr()); } catch { /* ignore */ }
    },
    refresh,
  };
}

// Returns 'captcha' | 'one-time code' | 'payment' | null.
// Checks every frame: ATS such as iCIMS run the form, and its hCaptcha challenge, inside a nested iframe.
// 0.11.0: frames the agent can evaluate. Captcha widget frames are left unattached by the CDP proxy (cobrowser.mjs);
// Playwright shows them with an empty URL and every evaluate there would only wait for its timeout.
export function liveFrames(page) {
  if (typeof page.frames !== 'function') return [page];
  const main = page.mainFrame();
  return page.frames().filter((f) => f === main || f.url() !== '');
}

export async function detectChallenge(page) {
  const frames = liveFrames(page);
  const found = await Promise.all(frames.map((f) => Promise.race([
    detectInFrame(f), new Promise((r) => setTimeout(() => r(null), 3000))])));
  return found.includes('captcha') ? 'captcha' : (found.find(Boolean) || null);
}

async function detectInFrame(page) {
  try {
    return await page.evaluate(() => {
      const visible = (el) => el.offsetParent !== null || el.getClientRects().length > 0;
      const captchaSrc = /recaptcha|hcaptcha|challenges\.cloudflare\.com|turnstile|arkoselabs|funcaptcha|friendlycaptcha|frcapi\.com/i;
      // Only a visible widget counts: sites like LinkedIn embed an invisible reCAPTCHA iframe on every page
      // (the invisible-mode badge is 256x60; real widgets are at least ~300x65).
      const shown = (el) => {
        const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
        return r.width >= 150 && r.height >= 64 && cs.visibility !== 'hidden' && cs.display !== 'none' &&
          Number(cs.opacity) > 0.1 && r.bottom > 0 && r.right > 0 &&
          r.top < innerHeight && r.left < innerWidth;
      };
      for (const f of document.querySelectorAll('iframe')) {
        if (captchaSrc.test(f.src || '') && shown(f)) return 'captcha';
      }
      if ([...document.querySelectorAll('.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form')].some(shown)) return 'captcha';
      // 0.15.1: Friendly Captcha ("Ik ben een mens", VDAB guest form) is a low div widget (~60 px), unsolved while its
      // solution field is empty or a '.UNSTARTED'/'.UNFINISHED' placeholder.
      const frcSolved = [...document.querySelectorAll('input[name="frc-captcha-solution"], input[name="frc-captcha-response"]')]
        .some((e) => String(e.value || '').length > 20 && !String(e.value).startsWith('.'));
      const low = (el) => { const r = el.getBoundingClientRect(); return r.width >= 150 && r.height >= 40 && visible(el); };
      if (!frcSolved && [...document.querySelectorAll('.frc-captcha, frc-widget, [data-sitekey][class*="frc"]')].some(low)) return 'captcha';

      const inputs = [...document.querySelectorAll('input')].filter(visible);
      const otp = /otp|one.?time|2fa|totp|verification.?code/i;
      // autocomplete=one-time-code alone is not enough: some ATS (Worley/Phenom) set it on name/phone/address
      // fields only to stop browser autofill. Such a field counts only if its own hints do not say it is ordinary.
      const plain = /name|phone|tel|mobile|address|street|city|town|zip|post|country|e.?mail|company|employer|job.?title/i;
      const codeWord = /code|otp|pin|token|verif/i;
      const ordinary = (i) => {
        const lab = i.labels && i.labels[0] ? i.labels[0].innerText : '';
        const hint = [i.name, i.id, i.getAttribute('aria-label'), i.getAttribute('placeholder'), lab].join(' ');
        return plain.test(hint) && !codeWord.test(hint.replace(/zip.?code|post.?code|country.?code/ig, ''));
      };
      if (inputs.some((i) =>
        ((i.getAttribute('autocomplete') || '').toLowerCase() === 'one-time-code' && !ordinary(i)) ||
        otp.test(i.name || '') || otp.test(i.id || ''))) return 'one-time code';

      const card = /card.?number|cvc|cvv/i;
      if (inputs.some((i) =>
        (i.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).some((t) => t.startsWith('cc-')) ||
        card.test(i.name || '') || card.test(i.id || ''))) return 'payment';

      return null;
    });
  } catch {
    return null;
  }
}
