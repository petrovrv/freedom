// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useState } from 'react';
import { categorize } from './errors';
import { findChild, getJson, updateContent } from './drive';
import type { Project } from './types';

// projects/<id>/schedules.json (Bridge 1.2.0): Bridge runs each enabled schedule at its cron time as an ordinary job in
// the schedule's own chat "⏰ <title>". Here the user switches schedules on and off and starts one now; new schedules
// are written by an agent on request ("make a schedule that …").
export interface ScheduleEntry {
  id: string;
  title?: string;
  enabled: boolean;
  cron: string;
  timezone?: string;
  agent?: string;
  devices?: string[];
  prompt: string;
  chatId?: string | null;
  runNow?: boolean;
  [k: string]: unknown;
}

interface ScheduleFile {
  schemaVersion: number;
  schedules: ScheduleEntry[];
}

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

function days(f: string): string {
  if (f === '*') return 'every day';
  if (f === '1-5') return 'Mon–Fri';
  if (f === '0,6' || f === '6,0' || f === '6-7') return 'weekends';
  return f.split(',').map((p) => p.split('-').map((d) => DAYS[Number(d)] ?? d).join('–')).join(', ');
}

const two = (n: string) => n.padStart(2, '0');

// Common cron shapes in words; anything else is shown as written.
export function describeCron(cron: string): string {
  const f = cron.trim().split(/\s+/);
  if (f.length !== 5) return cron;
  const [min, hour, dom, mon, dow] = f;
  if (dom !== '*' || mon !== '*') return cron;
  const d = days(dow);
  if (/^\d+$/.test(min) && /^\d+$/.test(hour)) return `${d}, ${two(hour)}:${two(min)}`;
  if (/^\d+(,\d+)*$/.test(min) && /^\d+(,\d+)*$/.test(hour)) return `${d}, at ${hour.split(',').map((h) => min.split(',').map((m) => `${two(h)}:${two(m)}`).join(', ')).join(', ')}`;
  const step = /^\*\/(\d+)$/.exec(min);
  const range = /^(\d+)-(\d+)$/.exec(hour);
  if (step && range) return `${d}, every ${step[1]} min ${two(range[1])}:00–${two(range[2])}:59`;
  if (step && hour === '*') return `${d}, every ${step[1]} min`;
  return cron;
}

export function SchedulesSection({ project }: { project: Project }) {
  const [file, setFile] = useState<{ id: string; doc: ScheduleFile } | null | undefined>(undefined);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  async function load() {
    try {
      const f = await findChild(project.folderId, 'schedules.json');
      setFile(f ? { id: f.id, doc: await getJson<ScheduleFile>(f.id) } : null);
    } catch (e) {
      setMsg(categorize(e).message);
    }
  }

  useEffect(() => {
    void load();
  }, [project.folderId]);

  // Reads the file again before writing: Bridge also writes it (chatId on the first run, runNow cleared).
  async function change(id: string, patch: Partial<ScheduleEntry>, note: string) {
    if (!file) return;
    setBusy(id);
    setMsg(null);
    try {
      const fresh = await getJson<ScheduleFile>(file.id);
      const s = fresh.schedules.find((x) => x.id === id);
      if (!s) throw new Error(`Schedule ${id} is gone from schedules.json.`);
      Object.assign(s, patch);
      await updateContent(file.id, JSON.stringify(fresh, null, 2), 'application/json');
      setFile({ id: file.id, doc: fresh });
      setMsg(note);
    } catch (e) {
      setMsg(categorize(e).message);
    } finally {
      setBusy(null);
    }
  }

  if (file === undefined) return <div className="muted small">Loading…</div>;
  return (
    <div className="schedules">
      {file === null || !file.doc.schedules?.length ? (
        <p className="muted tiny">
          No schedules. Ask an agent in a chat of this project, e.g. "every weekday at 9:00 check my Outlook and Teams and
          prepare replies"; it writes <code>schedules.json</code> here.
        </p>
      ) : (
        file.doc.schedules.map((s) => (
          <div key={s.id} className={`sched ${s.enabled ? '' : 'off'}`}>
            <label className="sched-head" title={s.prompt}>
              <input
                type="checkbox"
                checked={!!s.enabled}
                disabled={busy === s.id}
                onChange={(e) => void change(s.id, { enabled: e.target.checked }, e.target.checked ? `${s.title || s.id}: on` : `${s.title || s.id}: off`)}
              />
              <b>{s.title || s.id}</b>
            </label>
            <div className="tiny muted">
              {describeCron(s.cron)}
              {s.timezone && s.timezone !== 'Europe/Brussels' ? ` (${s.timezone})` : ''} · {s.agent || 'claude'}
              {s.devices?.length ? ` · ${s.devices.length} device(s)` : ''}
            </div>
            <div className="tiny muted">Results: chat “⏰ {s.title || s.id}”{s.chatId ? '' : ' (created on the first run)'}</div>
            <button
              className="btn small"
              disabled={busy === s.id || !!s.runNow}
              onClick={() => void change(s.id, { runNow: true }, `${s.title || s.id}: Bridge starts it within a minute.`)}
            >
              {s.runNow ? 'Starting…' : 'Run now'}
            </button>
          </div>
        ))
      )}
      <button className="btn small" onClick={() => void load()}>Refresh</button>
      {msg && <p className="tiny">{msg}</p>}
    </div>
  );
}
