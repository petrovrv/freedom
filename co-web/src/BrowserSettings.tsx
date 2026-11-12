// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.87.0: agent browser settings in the Chat tab (browser-lib.php). The chat overrides single keys; "Defaults" edits the
// environment defaults every chat without an override uses. Bridge 1.10.0 reads the result per job and passes it to
// CO Desktop 0.9.9 (devices) and browser-agent 0.15.0 (home PC); the tab chamberlain applies it.
import { useEffect, useState } from 'react';
import { dbBrowserDefaults } from './dbapi';
import type { BrowserSettings } from './types';

type Key = keyof BrowserSettings;

const BUILTIN: Required<BrowserSettings> = { tabs: 0, idleMin: 120, formIdleMin: 360, captchaKeep: true };

const ROWS: { key: Key; label: string; hint: string; options: (number | boolean)[] }[] = [
  { key: 'tabs', label: 'Tabs per chat', hint: 'How many agent browser tabs this chat may hold. With 1 every site opens in the same tab.',
    options: [0, 1, 2, 3, 5, 10] },
  { key: 'idleMin', label: 'Idle tab closes after', hint: 'An agent tab without actions is closed after this time.',
    options: [30, 60, 120, 360, 720, 1440] },
  { key: 'formIdleMin', label: 'With typed data', hint: 'A tab where the agent typed into a form waits this long for you or the next message.',
    options: [120, 360, 720, 1440, 2880] },
  { key: 'captchaKeep', label: 'Captcha tab never closes', hint: 'A tab with an unsolved captcha stays open until you solve it.',
    options: [true, false] },
];

function show(key: Key, v: number | boolean): string {
  if (key === 'captchaKeep') return v ? 'yes' : 'no';
  if (key === 'tabs') return v === 0 ? 'unlimited' : String(v);
  const m = v as number;
  return m % 60 === 0 ? `${m / 60} h` : `${m} min`;
}

export function BrowserSettingsPanel({ env, value, onChange, readOnly }: {
  env: string;
  value?: BrowserSettings;
  onChange: (next: BrowserSettings | undefined) => void;
  readOnly?: boolean;
}) {
  const [defaults, setDefaults] = useState<BrowserSettings | null>(null);
  const [builtin, setBuiltin] = useState<Required<BrowserSettings>>(BUILTIN);
  const [editDefaults, setEditDefaults] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => {
    let alive = true;
    dbBrowserDefaults(env).then((r) => {
      if (!alive) return;
      setBuiltin({ ...BUILTIN, ...r.builtin });
      setDefaults(r.defaults);
    }, (e: unknown) => alive && setErr(e instanceof Error ? e.message : String(e)));
    return () => { alive = false; };
  }, [env]);

  const base = { ...builtin, ...(defaults || {}) };

  function select(key: Key, current: number | boolean | undefined, fallback: number | boolean, fallbackLabel: string,
    set: (v: number | boolean | undefined) => void, disabled?: boolean) {
    const row = ROWS.find((r) => r.key === key)!;
    const options = current === undefined || row.options.includes(current) ? row.options : [...row.options, current];
    return (
      <select className="btn tiny ghost" disabled={disabled} title={row.hint}
        value={current === undefined ? '' : String(current)}
        onChange={(ev) => {
          const s = ev.target.value;
          set(s === '' ? undefined : key === 'captchaKeep' ? s === 'true' : Number(s));
        }}>
        <option value="">{fallbackLabel} ({show(key, fallback)})</option>
        {options.map((o) => <option key={String(o)} value={String(o)}>{show(key, o)}</option>)}
      </select>
    );
  }

  function setChat(key: Key, v: number | boolean | undefined) {
    const next: BrowserSettings = { ...(value || {}) };
    if (v === undefined) delete next[key];
    else (next as Record<Key, number | boolean>)[key] = v;
    onChange(Object.keys(next).length ? next : undefined);
  }

  function setDefault(key: Key, v: number | boolean | undefined) {
    const next: BrowserSettings = { ...(defaults || {}) };
    if (v === undefined) delete next[key];
    else (next as Record<Key, number | boolean>)[key] = v;
    setDefaults(next);
    dbBrowserDefaults(env, next).then((r) => setDefaults(r.defaults), (e: unknown) => setErr(e instanceof Error ? e.message : String(e)));
  }

  return (
    <div className="browser-settings">
      {ROWS.map((r) => (
        <label key={r.key} className="perm-bar" title={r.hint}>
          <span className="browser-settings-label">{r.label}</span>
          {select(r.key, value?.[r.key], base[r.key], 'Default', (v) => setChat(r.key, v), readOnly || defaults === null)}
        </label>
      ))}
      {!readOnly && (
        <button type="button" className="btn tiny ghost" aria-expanded={editDefaults} onClick={() => setEditDefaults(!editDefaults)}
          title={`Defaults of the environment ${env}: every chat without its own value uses them`}>
          {editDefaults ? '▾' : '▸'} Defaults for all chats of {env}
        </button>
      )}
      {editDefaults && defaults !== null && ROWS.map((r) => (
        <label key={`d-${r.key}`} className="perm-bar" title={r.hint}>
          <span className="browser-settings-label">{r.label}</span>
          {select(r.key, defaults[r.key], builtin[r.key], 'Built-in', (v) => setDefault(r.key, v))}
        </label>
      ))}
      {err && <div className="err small">{err}</div>}
    </div>
  );
}
