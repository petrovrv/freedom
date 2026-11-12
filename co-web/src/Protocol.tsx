// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.32.0: chat protocol kept by the server's secretary (cron/protocol.php, schema 8), shown as a mirror
// column next to the messages. Read-only for now; agents do not use it yet.
import { useCallback, useEffect, useRef, useState } from 'react';
import type { MouseEvent } from 'react';
import { dbProtocol } from './dbapi';
import type { ChatProtocol, ProtocolEntry, ProtocolSection } from './dbapi';

const PANE_KEY = 'co.protocolPane';

const SECTION_LABEL: Record<ProtocolSection, string> = {
  decision: 'Решение',
  constraint: 'Ограничение',
  fact: 'Факт',
  question: 'Вопрос',
  artifact: 'Артефакт',
  answer: 'Ответ',
};

export function protocolPaneOn(): boolean {
  try {
    return localStorage.getItem(PANE_KEY) === '1';
  } catch {
    return false;
  }
}

export function setProtocolPaneOn(on: boolean): void {
  try {
    if (on) localStorage.setItem(PANE_KEY, '1');
    else localStorage.removeItem(PANE_KEY);
  } catch {
    /* ignore */
  }
}

export function useProtocol(
  chatId: string | null,
  enabled: boolean,
  pollMs: number,
): { data: ChatProtocol | null; reload: () => void } {
  const [data, setData] = useState<ChatProtocol | null>(null);
  const chatRef = useRef<string | null>(chatId);
  const busyRef = useRef(false);

  // Reset on chat switch; must run before the polling effect
  useEffect(() => {
    chatRef.current = chatId;
    busyRef.current = false;
    setData(null);
  }, [chatId]);

  const load = useCallback(async () => {
    if (!enabled || !chatId || busyRef.current) return;
    busyRef.current = true;
    try {
      const res = await dbProtocol(chatId);
      // Drop responses for a chat that is no longer current
      if (chatRef.current !== chatId) return;
      if (res) setData(res);
    } finally {
      if (chatRef.current === chatId) busyRef.current = false;
    }
  }, [chatId, enabled]);

  useEffect(() => {
    if (!enabled || !chatId) return;
    void load();
    if (!(pollMs > 0)) return;
    const t = window.setInterval(() => {
      if (typeof document !== 'undefined' && document.hidden) return;
      void load();
    }, Math.max(pollMs, 1000));
    return () => window.clearInterval(t);
  }, [chatId, enabled, pollMs, load]);

  const reload = useCallback(() => {
    void load();
  }, [load]);

  return { data, reload };
}

function utcToLocalHM(utc: string | null): string | null {
  if (!utc) return null;
  const d = new Date(utc.replace(' ', 'T') + 'Z');
  if (Number.isNaN(d.getTime())) return null;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

export function ProtocolHeader({ data }: { data: ChatProtocol | null }) {
  if (!data) {
    return <div className="proto-header">Протокол: загрузка…</div>;
  }
  const { entries, state } = data;
  const err = state.error ? <span className="warn"> · ошибка: {state.error}</span> : null;

  if (state.seq == null && entries.length === 0) {
    return (
      <div className="proto-header">
        Протокол: секретарь ещё не обработал этот чат
        {err}
      </div>
    );
  }

  const total = entries.length;
  const active = entries.filter((e) => e.status === 'active').length;
  const hm = utcToLocalHM(state.utc);
  const parts: string[] = [
    'Протокол',
    `${total} ${plural(total, 'запись', 'записи', 'записей')} (${active} ${
      plural(active, 'действующая', 'действующих', 'действующих')
    })`,
  ];
  if (hm) parts.push(`обновлён ${hm}`);
  if (state.model) parts.push(`модель ${state.model}`);

  return (
    <div className="proto-header">
      {parts.join(' · ')}
      {err}
    </div>
  );
}

function ProtocolCard({ e }: { e: ProtocolEntry }) {
  const cls = ['proto-card'];
  if (e.status === 'superseded') cls.push('superseded');
  if (e.status === 'closed') cls.push('closed');
  const title = e.model ? `${e.author} · ${e.model}` : e.author;

  const copy = (ev: MouseEvent) => {
    ev.stopPropagation();
    navigator.clipboard?.writeText(`#${e.no}`).catch(() => {});
  };

  return (
    <div className={cls.join(' ')} title={title}>
      <span className={`proto-badge proto-sec-${e.section}`}>
        {SECTION_LABEL[e.section] ?? e.section}
      </span>{' '}
      <span
        className="proto-no"
        role="button"
        tabIndex={0}
        title={`Скопировать #${e.no}`}
        onClick={copy}
        onKeyDown={(ev) => {
          if (ev.key === 'Enter' || ev.key === ' ') {
            ev.preventDefault();
            navigator.clipboard?.writeText(`#${e.no}`).catch(() => {});
          }
        }}
      >
        #{e.no}
      </span>{' '}
      <span className="proto-text">{e.text}</span>
      {e.status === 'superseded' && e.replacedBy != null && (
        <span className="proto-status"> → #{e.replacedBy}</span>
      )}
      {e.status === 'closed' && <span className="proto-status"> ✓ закрыто</span>}
      {e.quote && <div className="proto-quote">«{e.quote}»</div>}
    </div>
  );
}

export function ProtocolCell({ seq, data }: { seq: number; data: ChatProtocol | null }) {
  const entries = data?.entries.filter((e) => e.msg === seq) ?? [];
  const notes = data?.entries.filter((e) => e.closedSeq === seq) ?? [];

  if (entries.length === 0 && notes.length === 0) {
    const processed = data?.state.seq != null && seq <= data.state.seq;
    return processed ? (
      <div className="proto-empty" title="не в протоколе">—</div>
    ) : (
      <div className="proto-empty" title="секретарь ещё не обработал">…</div>
    );
  }

  return (
    <>
      {entries.map((e) => (
        <ProtocolCard key={e.no} e={e} />
      ))}
      {notes.map((e) => (
        <div key={`n${e.no}`} className="proto-note">
          {e.status === 'superseded' && e.replacedBy != null
            ? `заменена #${e.no} → #${e.replacedBy}`
            : `закрыта #${e.no}`}
        </div>
      ))}
    </>
  );
}
