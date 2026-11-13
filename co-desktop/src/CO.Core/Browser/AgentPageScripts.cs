// SPDX-License-Identifier: AGPL-3.0-or-later
namespace CO.Core.Browser;

// JavaScript of the agent browser (CO Desktop 0.8.0). Runs in an isolated world of the agent tab, so page scripts
// cannot reach it or patch the prototypes it uses. Ported from the browser-agent tool (security.mjs, overlay.mjs).
internal static class AgentPageScripts
{
    // Runtime.callFunctionOn with this = target element: { isSubmit, category, description, fields }.
    public const string Classify = """
        function() { return ((el) => {
            const collectFields = (form) => {
              const out = [];
              for (const f of form.querySelectorAll('input,select,textarea')) {
                const type = String(f.type || f.tagName.toLowerCase()).toLowerCase();
                if (['hidden', 'submit', 'button', 'image'].includes(type)) continue;
                if (out.length >= 30) break;
                const label = (f.labels && f.labels[0] && f.labels[0].innerText) || f.getAttribute('aria-label') ||
                  f.name || f.placeholder || f.id || '';
                // co-resident 0.5.3: one-time codes are never shown in confirmations either.
                const otpField = (f.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).includes('one-time-code') ||
                  /one.?time|otp|verification.?code|verify.?code|2fa|two.?factor|totp|mfa|sms.?code|auth(?:entication)?.?code|login.?code|sign.?in.?code|confirmation.?code|verificatiecode|bevestigingscode|code.?de.?v[ée]rification|best[äa]tigungscode|код.?подтверждения|код.?из.?смс|одноразов/i.test(
                    [f.name, f.id, f.getAttribute('aria-label'), f.getAttribute('placeholder'), f.labels && f.labels[0] ? f.labels[0].innerText : ''].join(' '));
                const value = (type === 'password' || otpField) ? '***'
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
            const sendRe = /\b(send|post|publish|tweet|reply|share|comment|message)\b|verstuur|verzend|plaats|publiceer|reageer|delen|envoy|publier|répondre|repondre|partager|senden|absenden|veröffentlich|antworten|teilen|enviar|publicar|responder|compartir|inviare|invia|pubblica|rispondi|condividi|wyślij|opublikuj|отправ|опубликов|ответить|написать|поделит/iu;
            const purchaseRe = /\b(pay|buy|order|checkout|purchase|subscribe|book)\b|bestel|betaal|koop|afrekenen|payer|acheter|commander|kaufen|bezahlen|bestellen|kasse|pagar|comprar|paga|acquista|ordina|\bkup|zapła|płać|płatn|zamów|zamaw|купить|оплат|заказ|оформить/iu;
            const deleteRe = /\b(delete|remove|cancel subscription|unsubscribe|close account)\b|verwijder|supprimer|löschen|entfernen|eliminar|borrar|elimina|cancella|usuń|удалить|удалени/iu;
            const submitRe = /\b(submit|confirm|sign up|register|apply|save)\b|bevestig|registreer|подтверд|сохранить|зарегистр/iu;
            // Element that would submit its form natively
            const isFormSubmitter = (tag === 'BUTTON' && !!form && (!btn.hasAttribute('type') || type === 'submit')) ||
              (tag === 'INPUT' && (type === 'submit' || type === 'image'));
            // co-resident 0.5.1: a link with a real address outside a form opens a page ("Apply ->" on a job list, 13.11):
            // it is never the generic 'submit'; send, purchase and delete words still count (an onclick may act).
            const NAV_NOLINK = 'java' + 'script:';
            const linkHref = tag === 'A' ? (btn.getAttribute('href') || '').trim().toLowerCase() : '';
            const realLink = tag === 'A' && linkHref !== '' && !linkHref.startsWith('#') && !linkHref.startsWith(NAV_NOLINK) && !form;
            // Specific categories take precedence over the generic 'submit'
            let category = null;
            if (sendRe.test(label)) category = 'send';
            else if (purchaseRe.test(label)) category = 'purchase';
            else if (deleteRe.test(label)) category = 'delete';
            else if (!realLink && (isFormSubmitter || submitRe.test(label))) category = 'submit';
            else {
              // Any other button may still act (its handler can buy or send): only exact, known harmless UI labels pass.
              const harmlessPhrases = new Set([
                'search', 'find', 'filter', 'sort', 'show more', 'load more', 'see all',
                'expand', 'collapse', 'close', 'dismiss', 'menu',
                'accept cookies', 'reject cookies', 'accept all cookies', 'reject all cookies',
                'allow all cookies', 'allow cookies',
                'play', 'pause', 'mute', 'unmute', 'zoom in', 'zoom out', 'language',
                'zoeken', 'zoek', 'sluiten', 'meer', 'toon meer', 'taal',
                'поиск', 'найти', 'закрыть', 'свернуть', 'развернуть', 'меню', 'язык',
                'показать ещё', 'показать еще', 'ещё', 'еще'
              ]);
              const normLabelPart = s => String(s == null ? '' : s)
                .toLowerCase()
                .replace(/\s+/g, ' ')
                .replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '')
                .trim();
              const labelParts = Array.from(new Set([
                text, btn.getAttribute('aria-label'), btn.getAttribute('title'), 'value' in btn ? btn.value : ''
              ].map(normLabelPart).filter(p => p.length > 0)));
              const isHarmless = labelParts.length > 0 && labelParts.every(p => harmlessPhrases.has(p));
              const roles = String(btn.getAttribute('role') || '').toLowerCase().split(/\s+/);
              const isButtonLike = tag === 'BUTTON' ||
                (tag === 'INPUT' && ['button', 'submit', 'image', 'reset'].includes(type)) ||
                roles.includes('button');
              // Links without a real target (no href, '#', script URL) and other clickable elements act through scripts.
              const NOLINK_PREFIX = 'java' + 'script:';
              const isActionLike = (() => {
                if (tag === 'A') {
                  const href = (btn.getAttribute('href') || '').trim().toLowerCase();
                  return href === '' || href.startsWith('#') || href.startsWith(NOLINK_PREFIX);
                }
                return !['BUTTON', 'INPUT'].includes(tag);
              })();
              if ((isButtonLike || isActionLike) && !isHarmless) category = 'click';
            }
            const isSubmit = category !== null;
            const role = btn.getAttribute('role');
            const description = [tag.toLowerCase(), role ? `role=${role}` : '', text ? `"${text}"` : ''].filter(Boolean).join(' ');
            return { isSubmit, category, description, fields: form ? collectFields(form) : [] };
        })(this); }
        """;

    // Runtime.callFunctionOn with this = target element: { sensitive, reason, attrs }.
    // co-resident 0.5.3: attrs (type, autocomplete, name, id, ariaLabel, placeholder, label) for AgentOtpRules.Classify;
    // 'sensitive' keeps the pre-0.5.3 rule, applied unchanged while one-time codes from chat are off.
    public const string Field = """
        function() { return ((el) => {
            const type = String(el.type || el.getAttribute('type') || '').toLowerCase();
            const ac = String(el.getAttribute('autocomplete') || '').toLowerCase();
            const fieldLabel = el.labels && el.labels[0] ? el.labels[0].innerText : '';
            const attrs = { type, autocomplete: ac, name: String(el.getAttribute('name') || ''), id: String(el.id || ''),
              ariaLabel: String(el.getAttribute('aria-label') || ''), placeholder: String(el.getAttribute('placeholder') || ''),
              label: String(fieldLabel || '').slice(0, 4000) };
            const out = (sensitive, reason) => ({ sensitive, reason, attrs });
            if (type === 'password') return out(true, 'password field');
            if (ac.split(/\s+/).some((t) => t.startsWith('cc-'))) return out(true, 'payment autocomplete');
            if (ac.split(/\s+/).includes('one-time-code')) return out(true, 'one-time-code autocomplete');
            const re = /card.?(?:num|no\b|nr\b|holder)|(?:credit|debit).?card|kaartnummer|cvc|cvv|csc|security.?code|iban|password|passcode|wachtwoord|пароль|one.?time|otp|verification.?code|\bpin\b/i;
            for (const [k, v] of [
              ['name', el.getAttribute('name')], ['id', el.id], ['autocomplete', ac],
              ['aria-label', el.getAttribute('aria-label')], ['placeholder', el.getAttribute('placeholder')], ['label', fieldLabel],
            ]) {
              if (v && re.test(v)) return out(true, `${k} matches sensitive pattern`);
            }
            return out(false, '');
        })(this); }
        """;

    // Runtime.evaluate: { inForm, fields } for the focused element.
    public const string ActiveForm = """
        (() => {
            const collectFields = (form) => {
              const out = [];
              for (const f of form.querySelectorAll('input,select,textarea')) {
                const type = String(f.type || f.tagName.toLowerCase()).toLowerCase();
                if (['hidden', 'submit', 'button', 'image'].includes(type)) continue;
                if (out.length >= 30) break;
                const label = (f.labels && f.labels[0] && f.labels[0].innerText) || f.getAttribute('aria-label') ||
                  f.name || f.placeholder || f.id || '';
                // co-resident 0.5.3: one-time codes are never shown in confirmations either.
                const otpField = (f.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).includes('one-time-code') ||
                  /one.?time|otp|verification.?code|verify.?code|2fa|two.?factor|totp|mfa|sms.?code|auth(?:entication)?.?code|login.?code|sign.?in.?code|confirmation.?code|verificatiecode|bevestigingscode|code.?de.?v[ée]rification|best[äa]tigungscode|код.?подтверждения|код.?из.?смс|одноразов/i.test(
                    [f.name, f.id, f.getAttribute('aria-label'), f.getAttribute('placeholder'), f.labels && f.labels[0] ? f.labels[0].innerText : ''].join(' '));
                const value = (type === 'password' || otpField) ? '***'
                  : (type === 'checkbox' || type === 'radio') ? f.checked
                  : String(f.value ?? '').slice(0, 200);
                out.push({ label: String(label).trim(), type, value });
              }
              return out;
            };
            const ae = document.activeElement;
            const form = ae && typeof ae.closest === 'function' ? ae.closest('form') : null;
            return { inForm: !!form, fields: form ? collectFields(form) : [] };
        })()
        """;

    // Runtime.evaluate: comma-separated set of 'one-time code', 'payment', 'captcha', or null (AgentOtpRules.Decide).
    // 0.9.9: one-time code and payment pause the tab; a captcha only marks it captcha-pending.
    // co-resident 0.5.3: every kind found is returned, so with one-time codes from chat on a payment page still pauses.
    public const string Challenge = """
        (() => { try { return (() => {
              const visible = (el) => el.offsetParent !== null || el.getClientRects().length > 0;
              const inputs = [...document.querySelectorAll('input')].filter(visible);
              const found = [];
              const otp = /otp|one.?time|2fa|totp|verification.?code/i;
              if (inputs.some((i) =>
                (i.getAttribute('autocomplete') || '').toLowerCase() === 'one-time-code' ||
                otp.test(i.name || '') || otp.test(i.id || ''))) found.push('one-time code');

              const card = /card.?number|cvc|cvv/i;
              if (inputs.some((i) =>
                (i.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).some((t) => t.startsWith('cc-')) ||
                card.test(i.name || '') || card.test(i.id || ''))) found.push('payment');
              // co-resident 0.5.3: a code page of a card or bank payment confirmation (3-D Secure) is a payment page.
              if (found.length === 1 && found[0] === 'one-time code') {
                const payText = /3-?d.?secure|3ds|verified by visa|visa secure|identity.?check|securecode|safekey|card.?holder auth|cardinal|arcot|authori[sz]e (?:the |this |your )?(?:payment|purchase|transaction)|confirm (?:the |this |your )?(?:payment|purchase|transaction)|approve (?:the |this |your )?(?:payment|purchase|transaction)|betaling (?:bevestigen|goedkeuren)|bevestig (?:de |uw |je )?betaling|confirmer (?:le |votre )?paiement|paiement s[ée]curis|zahlung (?:best[äa]tigen|freigeben)|подтвер\S* (?:оплат|плат)/i;
                const page = location.hostname + ' ' + document.title + ' ' + String((document.body && document.body.innerText) || '').slice(0, 8000);
                if (payText.test(page)) found.push('payment');
              }

              const captchaSrc = /recaptcha|hcaptcha|challenges\.cloudflare\.com|turnstile|arkoselabs|funcaptcha/i;
              // 0.9.9: only a visible widget counts; the invisible reCAPTCHA v3 badge (size=invisible, tiny or hidden
              // frame) would otherwise keep the tab captcha-pending, and with captchaKeep it would never close.
              const shown = (el) => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width >= 60 && r.height >= 40 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
              };
              const captcha = () => {
                for (const f of document.querySelectorAll('iframe')) {
                  const src = f.src || '';
                  if (captchaSrc.test(src) && !/[?&]size=invisible/i.test(src) && shown(f)) return true;
                }
                for (const el of document.querySelectorAll('.g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form')) {
                  if (el.getAttribute('data-size') !== 'invisible' && shown(el)) return true;
                }
                return false;
              };
              if (captcha()) found.push('captcha');

              return found.length ? found.join(',') : null;
        })(); } catch (e) { return null; } })()
        """;

    // Runtime.evaluate (by reference): the first visible one-time code input, same detection as Challenge, or null.
    // co-resident 0.5.3: its ref goes into the hint for the agent ("browser_type it into ref n123").
    public const string OtpField = """
        (() => { try {
              const visible = (el) => el.offsetParent !== null || el.getClientRects().length > 0;
              const otp = /otp|one.?time|2fa|totp|verification.?code/i;
              return [...document.querySelectorAll('input')].filter(visible).find((i) =>
                (i.getAttribute('autocomplete') || '').toLowerCase() === 'one-time-code' ||
                otp.test(i.name || '') || otp.test(i.id || '')) || null;
        } catch (e) { return null; } })()
        """;

    // Runtime.callFunctionOn with this = select element, argument = wanted value or visible text.
    // 0.9.8: browser_select on a radio button or a radio group (native or role=radio): picks the radio whose label,
    // aria-label or value matches; returns { ok, radios: true } or { ok: false, radios: false } when there are none.
    public const string PickRadio = """
        function(want) {
          const norm = (s) => String(s ?? '').replace(/\s+/g, ' ').trim().toLowerCase();
          const target = norm(want);
          const el = this;
          const isRadio = (e) => (e.tagName === 'INPUT' && (e.type || '').toLowerCase() === 'radio') || e.getAttribute('role') === 'radio';
          let radios = [];
          if (isRadio(el) && el.tagName === 'INPUT' && el.name) {
            const scope = el.form || el.getRootNode();
            radios = Array.from(scope.querySelectorAll('input[type=radio]')).filter((r) => r.name === el.name);
          }
          if (radios.length === 0) {
            const box = isRadio(el) ? (el.closest('[role=radiogroup], fieldset') || el.parentElement?.parentElement || el) : el;
            radios = Array.from(box.querySelectorAll('input[type=radio], [role=radio]'));
            if (radios.length === 0 && isRadio(el)) radios = [el];
          }
          if (radios.length === 0) return { ok: false, radios: false };
          const labelOf = (r) => {
            const parts = [r.getAttribute('aria-label'), r.textContent];
            if (r.id) { const l = r.getRootNode().querySelector('label[for="' + CSS.escape(r.id) + '"]'); if (l) parts.push(l.textContent); }
            const by = r.getAttribute('aria-labelledby');
            if (by) by.split(/\s+/).forEach((id) => { const n = r.getRootNode().getElementById?.(id) || document.getElementById(id); if (n) parts.push(n.textContent); });
            const wrap = r.closest('label'); if (wrap) parts.push(wrap.textContent);
            if (r.parentElement) parts.push(r.parentElement.textContent); parts.push(r.value);
            return parts.map(norm).filter((x) => x.length > 0);
          };
          const all = radios.map((r) => ({ r, labels: labelOf(r) }));
          const pick = all.find((x) => x.labels.includes(target))
            || all.find((x) => x.labels.some((l) => l.startsWith(target)))
            || all.find((x) => x.labels.some((l) => l.includes(target)));
          if (!pick) return { ok: false, radios: true, options: all.map((x) => x.labels[0] || '').slice(0, 30) };
          const r = pick.r;
          if (r.disabled || r.getAttribute('aria-disabled') === 'true') return { ok: false, radios: true, options: ['(disabled) ' + (pick.labels[0] || '')] };
          const label = r.tagName === 'INPUT' && r.id ? r.getRootNode().querySelector('label[for="' + CSS.escape(r.id) + '"]') : null;
          const hit = label || r;
          for (const t of ['pointerdown', 'mousedown', 'pointerup', 'mouseup']) hit.dispatchEvent(new MouseEvent(t, { bubbles: true, cancelable: true, view: window }));
          hit.click();
          if (r.tagName === 'INPUT' && !r.checked) { r.click(); }
          const checked = r.tagName === 'INPUT' ? r.checked : r.getAttribute('aria-checked') === 'true';
          return { ok: true, radios: true, text: pick.labels[0] || '', checked };
        }
        """;

    // 0.9.8: opens a combobox-like control (custom dropdown, Avature/Phenom/select2 lists) by a synthetic click;
    // returns { input: true } when the element itself takes text (search field of the list).
    public const string OpenCombo = """
        function() {
          const el = this;
          const t = (el.type || '').toLowerCase();
          const input = el.isContentEditable || (el.tagName === 'INPUT' && !['button','submit','image','checkbox','radio','file','reset'].includes(t));
          el.scrollIntoView({ block: 'center' });
          for (const ty of ['pointerdown', 'mousedown', 'pointerup', 'mouseup']) el.dispatchEvent(new MouseEvent(ty, { bubbles: true, cancelable: true, view: window }));
          el.click();
          if (input) el.focus();
          return { input };
        }
        """;

    // 0.9.8: picks the visible option that matches the wanted text in the list a combobox opened (called on the
    // combobox). Options are looked for only inside the list it names (aria-controls / aria-owns) or, without one, inside
    // visible list containers (role=listbox, select2/chosen results); an option inside or around a button, link or
    // form control is never clicked (it may send, submit or delete).
    public const string PickListOption = """
        function(want) {
          const norm = (s) => String(s ?? '').replace(/\s+/g, ' ').trim().toLowerCase();
          const target = norm(want);
          const visible = (e) => { const b = e.getBoundingClientRect(); const st = getComputedStyle(e); return b.width > 0 && b.height > 0 && st.visibility !== 'hidden' && st.display !== 'none'; };
          const ids = ((this.getAttribute('aria-controls') || '') + ' ' + (this.getAttribute('aria-owns') || '')).split(/\s+/).filter((x) => x);
          let roots = ids.map((id) => document.getElementById(id)).filter((e) => e);
          if (roots.length === 0)
            roots = Array.from(document.querySelectorAll('[role=listbox], .select2-results, .chosen-results')).filter(visible);
          const sel = '[role=option], li, .select2-results__option, [id*=option], [class*=option]';
          const danger = 'a, button, input, textarea, select, form [type=submit], [role=button], [role=link], [role=menuitem]';
          const opts = [];
          for (const r of roots) for (const e of r.querySelectorAll(sel))
            if (visible(e) && norm(e.textContent).length > 0 && norm(e.textContent).length < 300 && !e.closest(danger) && !e.querySelector(danger)) opts.push(e);
          const leaf = opts.filter((e) => !opts.some((o) => o !== e && e.contains(o)));
          const pick = leaf.find((e) => norm(e.textContent) === target)
            || leaf.find((e) => norm(e.textContent).startsWith(target))
            || leaf.find((e) => norm(e.textContent).includes(target));
          if (!pick) return { ok: false, options: leaf.slice(0, 30).map((e) => norm(e.textContent)) };
          if (pick.getAttribute('aria-disabled') === 'true') return { ok: false, options: ['(disabled) ' + norm(pick.textContent)] };
          pick.scrollIntoView({ block: 'nearest' });
          for (const ty of ['pointerover', 'mouseover', 'pointerdown', 'mousedown', 'pointerup', 'mouseup']) pick.dispatchEvent(new MouseEvent(ty, { bubbles: true, cancelable: true, view: window }));
          pick.click();
          return { ok: true, text: norm(pick.textContent) };
        }
        """;

    // 0.9.8: after a dropdown opened: true when the newly focused element is the dropdown's own search field
    // (a combobox / autocomplete input, or an input inside the list's search box), never an unrelated field.
    public const string DropdownSearchFocused = """
        function() {
          const e = document.activeElement;
          if (!e || e === this || e.tagName !== 'INPUT' || ['button','submit','checkbox','radio','file'].includes((e.type || '').toLowerCase())) return false;
          return e.getAttribute('role') === 'combobox' || !!e.getAttribute('aria-autocomplete')
            || !!e.closest('.select2-search, .select2-dropdown, .chosen-search, .chosen-drop, [role=listbox], [role=combobox]');
        }
        """;

    // 0.9.8: key press in a hidden or occluded tab, where CDP key events are not delivered: synthetic keyboard
    // events on the focused element. Synthetic events have no default action, so only keys whose effect is emulated
    // or handled by page scripts are accepted: Enter/Space click a button-like element (already classified and
    // confirmed by the caller), Enter in a dropdown search field and Escape/arrows go to the page's own handlers.
    // Nothing is submitted from script. Returns { ok: false } for everything else (Enter in an ordinary field, Tab,
    // Backspace, Delete, paging): the caller reports tab_hidden. KEY/CODE/VK replaced.
    public const string SyntheticKey = """
        (function(key, code, vk) {
          if (!['Enter', ' ', 'Escape', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(key)) return { ok: false };
          const el = document.activeElement || document.body;
          const t0 = (el.type || '').toLowerCase();
          const buttonLike0 = el.tagName === 'BUTTON' || el.tagName === 'A' || ['checkbox','radio','submit','button'].includes(t0)
            || ['button','option','radio','checkbox','menuitem','tab'].includes(el.getAttribute('role') || '');
          const listField = el.tagName === 'INPUT' && (((el.getAttribute('role') === 'combobox' || ['list','both'].includes((el.getAttribute('aria-autocomplete') || '').toLowerCase())) && el.getAttribute('aria-expanded') === 'true') || !!el.closest('.select2-search, .select2-dropdown, .chosen-search, .chosen-drop'));
          if ((key === 'Enter' || key === ' ') && !buttonLike0 && !listField) return { ok: false };
          const init = { key, code, keyCode: vk, which: vk, bubbles: true, cancelable: true, composed: true };
          const down = el.dispatchEvent(new KeyboardEvent('keydown', init));
          if (down && key.length === 1) el.dispatchEvent(new KeyboardEvent('keypress', init));
          el.dispatchEvent(new KeyboardEvent('keyup', init));
          const t = (el.type || '').toLowerCase();
          const buttonLike = el.tagName === 'BUTTON' || el.tagName === 'A' || ['checkbox','radio','submit','button'].includes(t)
            || ['button','option','radio','checkbox','menuitem','tab'].includes(el.getAttribute('role') || '');
          if (down && (key === 'Enter' || key === ' ') && buttonLike) el.click();
          return { ok: true };
        })(KEY, CODE, VK)
        """;

    public const string SelectOption = """
        function(want) { return (function selectOption(selectEl, want) {
          const norm = (s) => String(s ?? '').replace(/\s+/g, ' ').trim().toLowerCase();
          const options = selectEl && selectEl.options ? Array.from(selectEl.options) : [];
          const listTexts = () => options.slice(0, 30).map((o) => (o.textContent || '').replace(/\s+/g, ' ').trim());

          if (!selectEl || selectEl.tagName !== 'SELECT') {
            return { ok: false, notSelect: true, options: [] };
          }

          const target = norm(want);
          const raw = String(want ?? '');

          const match =
            options.find((o) => o.value === raw) ||
            options.find((o) => norm(o.value) === target) ||
            options.find((o) => norm(o.textContent) === target) ||
            options.find((o) => norm(o.label) === target);

          if (!match || match.disabled) {
            return { ok: false, options: listTexts() };
          }

          // Use the native setter so frameworks that track value (e.g. React) detect the change
          const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
          if (setter) setter.call(selectEl, match.value);
          else selectEl.value = match.value;

          match.selected = true;
          selectEl.selectedIndex = options.indexOf(match);

          selectEl.dispatchEvent(new Event('input', { bubbles: true }));
          selectEl.dispatchEvent(new Event('change', { bubbles: true }));

          return {
            ok: true,
            value: match.value,
            text: (match.textContent || '').replace(/\s+/g, ' ').trim(),
          };
        })(this, want); }
        """;

    // Runtime.callFunctionOn with this = element the agent picked for an upload: the file input itself, the control of
    // its label, the file input inside it or its nearest ancestors (8 levels; of several the nearest after it), else the only one
    // on the page.
    // own = true: only the element itself, its label's control or the one input inside it (0.3.5).
    public const string FileInputNear = """
        function(own) {
          const isFile = e => !!e && e.tagName === 'INPUT' && (e.type || '').toLowerCase() === 'file';
          let el = this.nodeType === 1 ? this : this.parentElement;
          if (!el) return null;
          if (isFile(el)) return el;
          const label = el.closest('label');
          if (label && isFile(label.control)) return label.control;
          // co-resident 0.3.5: several inputs in one form (CV + letter on itds.nl, Gravity Forms) - the first one after the
          // element (a heading or a drop zone comes before its input), else the last one before it.
          const near = list => {
            const after = list.filter(f => el.compareDocumentPosition(f) & (Node.DOCUMENT_POSITION_FOLLOWING | Node.DOCUMENT_POSITION_CONTAINED_BY));
            return after.length ? after[0] : list[list.length - 1];
          };
          for (let a = el, i = 0; a && i < 8; a = a.parentElement, i++) {
            const found = [...a.querySelectorAll('input[type=file i]')];
            if (found.length === 1) return found[0];
            if (found.length > 1) return i === 0 ? null : near(found);
            if (own) return null;
          }
          const all = el.ownerDocument.querySelectorAll('input[type=file i]');
          return all.length === 1 ? all[0] : null;
        }
        """;

    // co-resident 0.5.1: date, time, datetime-local, month and week inputs ignore typed text in Chromium (two job sites
    // got the year 1000/9999, 13.11): the value is set in the input's own format with input/change events.
    // Accepts ISO (2026-12-01, 2026-12-01T09:30, 2026-12, 2026-W49, 09:30) and day-first dates (01/12/2026, 01.12.2026,
    // 01-12-2026). Returns { handled: false } for other elements, else { handled: true, ok, value | expected }.
    public const string SetDateLike = """
        function(text) {
          const el = this;
          const type = el.tagName === 'INPUT' ? String(el.type || '').toLowerCase() : '';
          if (!['date', 'time', 'datetime-local', 'month', 'week'].includes(type)) return { handled: false };
          const t = String(text ?? '').trim();
          const p2 = (n) => String(n).padStart(2, '0');
          const day = (s) => {
            let m = /^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$/.exec(s);
            if (m) return m[1] + '-' + p2(m[2]) + '-' + p2(m[3]);
            m = /^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})$/.exec(s);
            if (m) return m[3] + '-' + p2(m[2]) + '-' + p2(m[1]);
            return null;
          };
          const time = (s) => {
            const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(s);
            return m ? p2(m[1]) + ':' + m[2] + (m[3] ? ':' + m[3] : '') : null;
          };
          let v = null;
          if (type === 'date') v = day(t);
          else if (type === 'time') v = time(t);
          else if (type === 'datetime-local') {
            const parts = t.split(/[T ]+/);
            const d = day(parts[0] || ''), h = parts.length > 1 ? time(parts[1]) : '00:00';
            v = d && h ? d + 'T' + h : null;
          } else if (type === 'month') {
            let m = /^(\d{4})[-/.](\d{1,2})$/.exec(t);
            if (m) v = m[1] + '-' + p2(m[2]);
            else if ((m = /^(\d{1,2})[-/.](\d{4})$/.exec(t))) v = m[2] + '-' + p2(m[1]);
          } else if (type === 'week') {
            const m = /^(\d{4})-?W(\d{1,2})$/i.exec(t);
            if (m) v = m[1] + '-W' + p2(m[2]);
          }
          const expected = { date: '2026-12-01', time: '09:30', 'datetime-local': '2026-12-01T09:30', month: '2026-12', week: '2026-W49' }[type];
          if (el.readOnly || el.disabled) return { handled: true, ok: false, expected, readOnly: true };
          if (!v) return { handled: true, ok: false, expected };
          el.focus();
          const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
          if (set) set.call(el, v); else el.value = v;
          if (el.value !== v) return { handled: true, ok: false, expected, rejected: true };
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
          el.dispatchEvent(new Event('blur'));
          return { handled: true, ok: true, value: v };
        }
        """;

    // Runtime.callFunctionOn with this = input: empties it before Input.insertText.
    public const string ClearValue = """
        function() { if ('value' in this) { this.value = ''; this.dispatchEvent(new Event('input', { bubbles: true })); } else if (this.isContentEditable) { this.textContent = ''; } return true; }
        """;

    // Runtime.callFunctionOn with this = target, argument = node hit at the click point (same world): the target itself or inside
    // it, also through shadow roots. Walks parents instead of Node.contains, which threw "parameter 1 is not of type 'Node'"
    // for LinkedIn buttons on the Mac (10.11); null when the check itself fails.
    public const string Contains = """
        function(other) {
          try {
            for (let n = other; n; n = n.parentNode || n.host || null) if (n === this || n === this.shadowRoot) return true;
            return false;
          } catch { return null; }
        }
        """;

    // Page.addScriptToEvaluateOnNewDocument in world "co-agent-ui": orange frame with Take over / Return bar.
    public const string Overlay = """
        (function overlayMain() {
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
        })();
        """;

    public const string ReadyState = "document.readyState";
}
