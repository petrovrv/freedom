// SPDX-License-Identifier: AGPL-3.0-or-later
// Import of chats from claude.ai or ChatGPT: the ui.page of the module chat-import (Freedom F10; was
// co-web/src/ImportView.tsx up to CO Web 0.102.0, same steps). The export is read in this frame and never leaves the
// browser; only the chats the user selects go to the core, one conversation per call of the ext/1 import point:
//   core.import.context -> {env, projects, imported}   projects of the environment, conversations imported before
//   core.import.write   -> {skipped, projectId, chatId} one conversation into a new or existing project (the label of
//                          its source comes from this module's manifest, provides.import)
//   core.import.finish  -> {created}                    saves the import index, CO Web refreshes and opens a project
import { type ImportBundle, type ImportedConversation, type ImportedProject } from './parse';
import { readExport } from './zip';

declare const coExt: {
  ready: Promise<Record<string, unknown>>;
  call: (op: string, args?: Record<string, unknown>, opts?: { timeoutMs?: number }) => Promise<any>;
};

const NO_PROJECT = '__none__';
const SAFE_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/;
const PARALLEL = 3;
const WRITE_TIMEOUT_MS = 5 * 60 * 1000;

interface TargetChoice { mode: 'new' | 'existing'; newId: string; newTitle: string; existingId: string }
interface Ctx { env: string; projects: { projectId: string; title: string }[]; imported: Record<string, { projectId: string; chatId: string }> }

const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', ґ: 'g', д: 'd', е: 'e', ё: 'e', є: 'ie', ж: 'zh', з: 'z', и: 'i', і: 'i', ї: 'i',
  й: 'i', к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts',
  ч: 'ch', ш: 'sh', щ: 'shch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'iu', я: 'ia',
};

/** Bridge-safe project id from a display name: ^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$ */
export function projectIdFrom(name: string, prefix: string): string {
  const latin = name.toLowerCase().split('').map((c) => TRANSLIT[c] ?? c).join('')
    .normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const id = `${prefix}-${latin}`.slice(0, 40).replace(/-+$/g, '');
  return SAFE_ID.test(id) ? id : `${prefix}-${Date.now().toString(36)}`;
}

const sourceLabel = (s: string) => (s === 'claude' ? 'claude.ai' : 'ChatGPT');
const indexKey = (source: string, id: string) => `${source}:${id}`;

function formatLocal(utc?: string | null): string {
  if (!utc) return '';
  const d = new Date(utc);
  if (isNaN(d.getTime())) return utc;
  return d.toLocaleString(undefined, { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
}

type Kid = Node | string | null | undefined | false;
function h(tag: string, attrs: Record<string, unknown> = {}, kids: Kid[] = []): HTMLElement {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'text') e.textContent = String(v);
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v as EventListener);
    else if (k === 'value') (e as HTMLInputElement).value = String(v);
    else if (k === 'checked') (e as HTMLInputElement).checked = !!v;
    else if (k === 'disabled') (e as HTMLButtonElement).disabled = !!v;
    else e.setAttribute(k, v === true ? '' : String(v));
  }
  for (const c of kids) if (c) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
  return e;
}

const root = document.getElementById('app')!;
let ctx: Ctx | null = null;
let bundle: ImportBundle | null = null;
let parsing: number | null = null;
let err = '';
const selected = new Set<string>();
let focus: string | null = null;
let q = '';
let projFilter = '';
let hideImported = true;
let targets: Record<string, TargetChoice> = {};
let includeContent = true;
let running: { done: number; total: number; label: string } | null = null;
let result: { imported: number; skipped: number; failed: { title: string; error: string }[]; projects: string[] } | null = null;

const isImported = (c: ImportedConversation) => !!ctx?.imported[indexKey(c.source, c.id)];
const projectsByKey = () => new Map((bundle?.projects || []).map((p) => [p.key, p]));
const prefix = () => (bundle?.source === 'chatgpt' ? 'gpt' : 'cl');

function visible(): ImportedConversation[] {
  if (!bundle) return [];
  const needle = q.trim().toLowerCase();
  return bundle.conversations.filter((c) => {
    if (hideImported && isImported(c)) return false;
    if (projFilter === NO_PROJECT && c.projectKey) return false;
    if (projFilter && projFilter !== NO_PROJECT && c.projectKey !== projFilter) return false;
    if (!needle) return true;
    return c.title.toLowerCase().includes(needle) || c.messages.some((m) => m.text.toLowerCase().includes(needle));
  });
}
const selectedConvs = () => (bundle?.conversations || []).filter((c) => selected.has(c.id));
const groupKeys = () => [...new Set(selectedConvs().map((c) => c.projectKey || NO_PROJECT))];

function defaultTarget(key: string): TargetChoice {
  const src = projectsByKey().get(key);
  const title = key === NO_PROJECT ? `Imported from ${sourceLabel(bundle?.source || '')}` : src?.name || key;
  return { mode: 'new', newId: key === NO_PROJECT ? `${prefix()}-imported` : projectIdFrom(title, prefix()), newTitle: title, existingId: ctx?.projects[0]?.projectId || '' };
}
const targetOf = (key: string) => targets[key] || defaultTarget(key);
function targetErrors(): string[] {
  return groupKeys().map((k) => {
    const t = targetOf(k);
    if (t.mode === 'existing') return t.existingId ? '' : 'Choose an existing project.';
    return SAFE_ID.test(t.newId) ? '' : `Invalid project ID "${t.newId}".`;
  }).filter(Boolean);
}

async function loadContext() {
  try {
    ctx = await coExt.call('core.import.context') as Ctx;
  } catch (e) {
    err = (e as Error).message;
  }
  render();
}

async function parse(files: File[]) {
  if (!files.length) return;
  err = '';
  bundle = null;
  result = null;
  parsing = 0;
  render();
  let last = 0;
  try {
    const b = await readExport(files, (p) => {
      parsing = p;
      if (p - last > 0.02) {
        last = p;
        renderPick();
      }
    });
    b.conversations.sort((a, c) => (c.updatedUtc || '').localeCompare(a.updatedUtc || ''));
    bundle = b;
    selected.clear();
    targets = {};
    focus = null;
  } catch (e) {
    err = (e as Error).message || 'The export could not be read.';
  }
  parsing = null;
  render();
}

async function start() {
  if (!bundle || !ctx) return;
  err = '';
  result = null;
  const byKey = new Map<string, Record<string, unknown>>();
  const pk = projectsByKey();
  for (const k of groupKeys()) {
    const t = targetOf(k);
    if (t.mode === 'existing') {
      if (!ctx.projects.some((p) => p.projectId === t.existingId)) {
        err = `Project ${t.existingId} not found.`;
        render();
        return;
      }
      byKey.set(k, { kind: 'existing', projectId: t.existingId });
    } else {
      const src = pk.get(k) as ImportedProject | undefined;
      const withContent = includeContent && !!src;
      byKey.set(k, {
        kind: 'new', projectId: t.newId, title: t.newTitle || t.newId,
        description: src?.description || '', instructions: withContent ? src!.instructions || '' : '', docs: withContent ? src!.docs : [],
      });
    }
  }
  const list = selectedConvs();
  const res = { imported: 0, skipped: 0, failed: [] as { title: string; error: string }[], projects: [] as string[] };
  running = { done: 0, total: list.length, label: '' };
  render();
  let next = 0;
  const worker = async () => {
    while (next < list.length) {
      const c = list[next++];
      try {
        const { projectKey: _k, ...conversation } = c;
        const r = await coExt.call('core.import.write', { conversation, target: byKey.get(c.projectKey || NO_PROJECT)! }, { timeoutMs: WRITE_TIMEOUT_MS });
        if (r.skipped) res.skipped++;
        else res.imported++;
        ctx!.imported[indexKey(c.source, c.id)] = { projectId: r.projectId, chatId: r.chatId };
      } catch (e) {
        res.failed.push({ title: c.title, error: (e as Error).message });
      }
      running = { done: running!.done + 1, total: list.length, label: c.title };
      renderProgress();
    }
  };
  await Promise.all(Array.from({ length: Math.min(PARALLEL, list.length) }, worker));
  try {
    const f = await coExt.call('core.import.finish', {}, { timeoutMs: WRITE_TIMEOUT_MS });
    res.projects = (f?.created as string[]) || [];
  } catch (e) {
    err = (e as Error).message;
  }
  running = null;
  result = res;
  selected.clear();
  render();
}

// ---------- rendering ----------

function renderPick() {
  const p = document.getElementById('parsing');
  if (p) p.textContent = parsing === null ? '' : `Reading export… ${Math.round(parsing * 100)}%`;
}

function renderProgress() {
  const box = document.getElementById('progress');
  if (!box) return;
  box.replaceChildren(...(running ? [h('progress', { max: running.total, value: running.done }), ` ${running.done}/${running.total} `, h('span', { class: 'muted small', text: running.label })] : []));
}

function render() {
  if (!bundle) {
    const input = h('input', { type: 'file', accept: '.zip,.json', multiple: true, hidden: true,
      onchange: (e: Event) => void parse(Array.from((e.target as HTMLInputElement).files || [])) }) as HTMLInputElement;
    root.replaceChildren(h('div', { class: 'import pad' }, [
      h('h2', { text: 'Import chats from claude.ai or ChatGPT' }),
      h('ol', { class: 'small' }, [
        h('li', {}, [h('b', { text: 'claude.ai:' }), ' Settings → Privacy → Export data. The download link arrives by e-mail.']),
        h('li', {}, [h('b', { text: 'ChatGPT:' }), ' Settings → Data controls → Export data. The download link arrives by e-mail.']),
        h('li', {}, ['Select the downloaded ', h('code', { text: '.zip' }), ' (or ', h('code', { text: 'conversations.json' }), ' and ',
          h('code', { text: 'projects.json' }), '). It is read in this browser; only the chats you select are written to Drive.']),
      ]),
      h('div', { class: 'dropzone', ondragover: (e: Event) => e.preventDefault(),
        ondrop: (e: DragEvent) => { e.preventDefault(); void parse(Array.from(e.dataTransfer?.files || [])); } }, [
        h('button', { class: 'btn primary', disabled: parsing !== null || !ctx, onclick: () => input.click(), text: 'Choose export file…' }),
        input,
        h('span', { class: 'muted small', text: 'or drop it here' }),
      ]),
      h('p', { class: 'muted', id: 'parsing' }),
      err ? h('p', { class: 'error-text', text: err }) : null,
    ]));
    renderPick();
    return;
  }

  const b = bundle;
  const importedCount = b.conversations.filter(isImported).length;
  const search = h('input', { class: 'search', placeholder: 'Search titles and messages', value: q, 'aria-label': 'Search',
    oninput: (e: Event) => { q = (e.target as HTMLInputElement).value; renderList(); } });
  const filter = h('select', { 'aria-label': 'Project filter', onchange: (e: Event) => { projFilter = (e.target as HTMLSelectElement).value; renderList(); } }, [
    h('option', { value: '', text: 'All chats' }),
    h('option', { value: NO_PROJECT, text: 'Without project' }),
    ...b.projects.map((p) => h('option', { value: p.key, text: `${p.name} (${b.conversations.filter((c) => c.projectKey === p.key).length})` })),
  ]) as HTMLSelectElement;
  filter.value = projFilter;
  root.replaceChildren(h('div', { class: 'import' }, [
    h('div', { class: 'import-head' }, [
      h('div', {}, [
        h('h2', { text: `Import from ${sourceLabel(b.source)}` }),
        h('div', { class: 'muted small' }, [`${b.conversations.length} chats · ${b.projects.length} projects · ${importedCount} already imported · target environment `, h('b', { text: ctx?.env || '' })]),
        ...b.warnings.map((w) => h('div', { class: 'warn small', text: w })),
      ]),
      h('button', { class: 'btn ghost small', text: 'Other file', onclick: () => { bundle = null; render(); } }),
    ]),
    h('div', { class: 'import-toolbar' }, [
      search, filter,
      h('label', { class: 'small check' }, [h('input', { type: 'checkbox', checked: hideImported,
        onchange: (e: Event) => { hideImported = (e.target as HTMLInputElement).checked; renderList(); } }), ' Hide imported']),
      h('button', { class: 'btn tiny', id: 'select-shown', onclick: () => { for (const c of visible()) if (!isImported(c)) selected.add(c.id); renderList(); renderTargets(); } }),
      h('button', { class: 'btn tiny', text: 'Clear', onclick: () => { selected.clear(); renderList(); renderTargets(); } }),
    ]),
    h('div', { class: 'import-body' }, [
      h('ul', { class: 'import-list', id: 'list', role: 'listbox', 'aria-multiselectable': 'true' }),
      h('div', { class: 'import-preview', id: 'preview' }),
    ]),
    h('div', { id: 'targets' }),
    h('div', { class: 'import-progress', id: 'progress' }),
    err ? h('p', { class: 'error-text pad', text: err }) : null,
    result ? h('div', { class: 'import-result' }, [
      `Imported ${result.imported}, skipped ${result.skipped} (already imported)${result.failed.length ? `, failed ${result.failed.length}` : ''}.`,
      ...result.failed.map((f) => h('div', { class: 'error-text small', text: `${f.title}: ${f.error}` })),
      h('div', {}, [h('button', { class: 'btn small', text: 'Open projects',
        onclick: () => void coExt.call('core.import.finish', { open: result?.projects[0] || '' }).catch(() => undefined) })]),
    ]) : null,
  ]));
  renderList();
  renderPreview();
  renderTargets();
  renderProgress();
}

function renderList() {
  const ul = document.getElementById('list');
  if (!ul || !bundle) return;
  const pk = projectsByKey();
  const vis = visible();
  const sel = document.getElementById('select-shown');
  if (sel) sel.textContent = `Select shown (${vis.length})`;
  ul.replaceChildren(...[
    ...vis.slice(0, 1000).map((c) => {
      const done = isImported(c);
      return h('li', { class: `${focus === c.id ? 'focus' : ''} ${done ? 'done' : ''}` }, [
        h('input', { type: 'checkbox', checked: selected.has(c.id), disabled: done, 'aria-label': `Select ${c.title}`,
          onchange: () => { if (selected.has(c.id)) selected.delete(c.id); else selected.add(c.id); renderTargets(); } }),
        h('button', { class: 'import-item', onclick: () => { focus = c.id; renderList(); renderPreview(); } }, [
          h('span', { class: 'nav-title', text: c.title }),
          h('span', { class: 'muted tiny', text: `${formatLocal(c.updatedUtc)} · ${c.messages.length} msg${c.projectKey ? ` · ${pk.get(c.projectKey)?.name || 'project'}` : ''}${done ? ' · imported' : ''}` }),
        ]),
      ]);
    }),
    vis.length > 1000 ? h('li', { class: 'muted small pad', text: `Showing 1000 of ${vis.length}; refine the search.` }) : null,
    vis.length === 0 ? h('li', { class: 'muted small pad', text: 'Nothing matches.' }) : null,
  ].filter(Boolean) as Node[]);
}

function renderPreview() {
  const box = document.getElementById('preview');
  if (!box || !bundle) return;
  const f = bundle.conversations.find((c) => c.id === focus);
  if (!f) {
    box.replaceChildren(h('p', { class: 'muted', text: 'Click a chat to preview it.' }));
    return;
  }
  const who = bundle.source === 'chatgpt' ? 'ChatGPT' : 'Claude';
  box.replaceChildren(
    h('h3', { text: f.title }),
    h('div', { class: 'muted small', text: `${formatLocal(f.createdUtc)} – ${formatLocal(f.updatedUtc)} · ${f.messages.length} messages${f.model ? ` · ${f.model}` : ''}` }),
    ...f.messages.slice(0, 60).map((m) => h('div', { class: `pv-msg ${m.role}` }, [
      h('div', { class: 'muted tiny', text: `${m.role === 'user' ? 'You' : who} ${m.createdUtc ? '· ' + formatLocal(m.createdUtc) : ''}` }),
      h('div', { class: 'pre', text: m.text.length > 1500 ? m.text.slice(0, 1500) + ' …' : m.text }),
      m.attachments?.length ? h('div', { class: 'muted tiny', text: `📎 ${m.attachments.map((a) => a.name).join(', ')}` }) : null,
    ])),
    f.messages.length > 60 ? h('div', { class: 'muted small', text: `… ${f.messages.length - 60} more messages` }) : document.createTextNode(''),
  );
}

function renderErrors() {
  const box = document.getElementById('target-errors');
  const go = document.getElementById('go') as HTMLButtonElement | null;
  const errs = targetErrors();
  box?.replaceChildren(...errs.map((x) => h('div', { class: 'error-text small', text: x })));
  if (go) go.disabled = !!running || errs.length > 0;
}

function renderTargets() {
  const box = document.getElementById('targets');
  if (!box || !bundle) return;
  const convs = selectedConvs();
  if (!convs.length) {
    box.replaceChildren();
    box.className = '';
    return;
  }
  box.className = 'import-targets';
  const pk = projectsByKey();
  const projects = ctx?.projects || [];
  box.replaceChildren(
    h('h4', { text: `Where to put ${convs.length} selected chat(s)` }),
    ...groupKeys().map((k) => {
      const t = targetOf(k);
      const src = pk.get(k);
      const count = convs.filter((c) => (c.projectKey || NO_PROJECT) === k).length;
      const set = (patch: Partial<TargetChoice>, redraw: boolean) => {
        targets = { ...targets, [k]: { ...targetOf(k), ...patch } };
        if (redraw) renderTargets();
        else renderErrors();
      };
      const mode = h('select', { 'aria-label': 'Target type', onchange: (e: Event) => set({ mode: (e.target as HTMLSelectElement).value as TargetChoice['mode'] }, true) }, [
        h('option', { value: 'new', text: 'New CO project' }),
        h('option', { value: 'existing', text: 'Existing CO project', disabled: !projects.length }),
      ]) as HTMLSelectElement;
      mode.value = t.mode;
      let fields: HTMLElement[];
      if (t.mode === 'new') {
        const id = h('input', { value: t.newId, 'aria-label': 'Project ID', class: SAFE_ID.test(t.newId) ? '' : 'invalid',
          oninput: (e: Event) => { const v = (e.target as HTMLInputElement).value; id.className = SAFE_ID.test(v) ? '' : 'invalid'; set({ newId: v }, false); } }) as HTMLInputElement;
        const title = h('input', { value: t.newTitle, 'aria-label': 'Project title', oninput: (e: Event) => {
          // keep the ID in step with the title until the ID is edited by hand
          const cur = targetOf(k);
          const v = (e.target as HTMLInputElement).value;
          const auto = cur.newId === projectIdFrom(cur.newTitle, prefix());
          if (auto) {
            id.value = projectIdFrom(v, prefix());
            id.className = SAFE_ID.test(id.value) ? '' : 'invalid';
          }
          set({ newTitle: v, ...(auto ? { newId: id.value } : {}) }, false);
        } });
        fields = [id, title];
      } else {
        const ex = h('select', { 'aria-label': 'Existing project', onchange: (e: Event) => set({ existingId: (e.target as HTMLSelectElement).value }, false) },
          projects.map((p) => h('option', { value: p.projectId, text: p.title }))) as HTMLSelectElement;
        ex.value = t.existingId;
        fields = [ex];
      }
      return h('div', { class: 'target-row' }, [
        h('span', { class: 'target-src' }, [k === NO_PROJECT ? 'Chats without project' : src?.name || k, ' ', h('span', { class: 'muted tiny', text: `(${count})` })]),
        mode, ...fields,
      ]);
    }),
    bundle.source === 'claude' && bundle.projects.some((p) => p.instructions || p.docs.length)
      ? h('label', { class: 'small check' }, [h('input', { type: 'checkbox', checked: includeContent,
        onchange: (e: Event) => { includeContent = (e.target as HTMLInputElement).checked; } }), ' Import project instructions and knowledge files into new projects'])
      : document.createTextNode(''),
    h('p', { class: 'muted tiny' }, ['Imported projects exist in Drive only and run in Chat mode. Add a project to the Bridge ', h('code', { text: 'config.json' }), ' to use Cowork with a workspace.']),
    h('div', { id: 'target-errors' }),
    h('button', { class: 'btn primary', id: 'go', text: `Import ${convs.length} chat(s)`, onclick: () => void start() }),
  );
  renderErrors();
}

render();
void coExt.ready.then(loadContext);
