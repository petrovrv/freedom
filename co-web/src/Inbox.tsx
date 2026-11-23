// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.71.0: "Waiting for you" - one list of what agents wait on in all chats the user may write to:
// questions and offered choices, captcha permission requests, browser confirmations, failed jobs, memory candidates.
// The server builds it (db.php inbox, inbox-lib.php); read / done marks are per user. Quick replies open the chat and
// send there (ChatView takes location.state.inboxReply), so they go through the normal send path.
// 0.75.0 (schema 16): memory items also hold rules from corrections (target: environment profile or project memory); tools items
// are proposals of the nightly tool library pass - Accept & run sends the agent task in a new chat (location.state.toolTask).
// 0.81.0 (schema 18): action items are confirmation cards of risky actions read-guard held in any chat (send, purchase,
// delete, publish, git push); Yes / No record the decision (action_decide) and send "<code> да" / "<code> нет" in the chat,
// which is what read-guard grant checks. A push opens /inbox#act-<id>.
// 0.120.0 (F14b-2): app items are outward actions of apps (smart-home home_control, ...) an agent asked for through
// app_action. The card says what, which app, the arguments in words and from which chat; Run executes it here with my
// rights, once (the server refuses a second click, an expired card or a changed request), Decline drops it. A push
// opens /inbox#app-<id>.
import { useCallback, useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { AGENTS } from './config';
import { dbActionDecide, dbAppRequestDecide, dbInbox, dbInboxMark, dbMemoryDecide, dbToolDecide, type InboxAction, type InboxApp, type InboxItem, type InboxKind, type InboxMemory, type ToolCandidate } from './dbapi';
import { diagnose } from './diagnose';
import { categorize } from './errors';
import { useInterval } from './hooks';
import { extractQuickReplies } from './quickReplies';
import { formatLocal } from './util';

const POLL_INBOX_MS = 30000;

const KIND_LABEL: Record<InboxKind, string> = {
  ask: 'Question',
  captcha: 'Captcha',
  confirm: 'Confirmation',
  action: 'Confirm action',
  app: 'Run in app',
  failed: 'Failed',
  memory: 'Memory',
  tools: 'Tool library',
};

const TOOL_KIND_LABEL: Record<ToolCandidate['kind'], string> = { new: 'new tool', skill: 'SKILL.md fix', archive: 'archive' };

const agentLabel = (a: string | null) => AGENTS.find((x) => x.id === a)?.displayName || a || 'Agent';

export interface InboxState {
  items: InboxItem[] | null;
  unread: number;
  error: string | null;
  refresh: () => Promise<void>;
  mark: (ids: string[], action: 'read' | 'unread' | 'done') => Promise<void>;
}

// Polled by Shell for the sidebar badge; the page uses the same state.
export function useInbox(env: string, enabled: boolean): InboxState {
  const [items, setItems] = useState<InboxItem[] | null>(null);
  const [unread, setUnread] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const refresh = useCallback(async () => {
    if (!enabled) return;
    try {
      const r = await dbInbox(env);
      setItems(r.items);
      setUnread(r.unread);
      setError(null);
    } catch (e) {
      setError(categorize(e).message);
    }
  }, [env, enabled]);
  useEffect(() => {
    setItems(null);
    setUnread(0);
    void refresh();
  }, [refresh]);
  useInterval(() => void refresh(), POLL_INBOX_MS, enabled);
  const mark = useCallback(async (ids: string[], action: 'read' | 'unread' | 'done') => {
    if (!ids.length) return;
    // optimistic: the badge follows at once, the next poll corrects it
    setItems((prev) => {
      if (!prev) return prev;
      const set = new Set(ids);
      const next = action === 'done' ? prev.filter((x) => !set.has(x.id)) : prev.map((x) => (set.has(x.id) ? { ...x, unread: action === 'unread' } : x));
      setUnread(next.filter((x) => x.unread).length);
      return next;
    });
    try {
      await dbInboxMark(ids, action);
    } catch (e) {
      setError(categorize(e).message);
      void refresh();
    }
  }, [refresh]);
  return { items, unread, error, refresh, mark };
}

export function InboxView({ env, inbox }: { env: string; inbox: InboxState }) {
  const navigate = useNavigate();
  const location = useLocation();
  const [filter, setFilter] = useState<'all' | 'unread'>('all');
  const { items, error, mark, refresh } = inbox;
  const shown = (items || []).filter((x) => filter === 'all' || x.unread);
  // 0.81.0: a push "Confirm: ..." opens /inbox#act-<id>; the card is scrolled into view once it is loaded
  const [focused, setFocused] = useState<string | null>(null);
  useEffect(() => {
    const h = location.hash.replace(/^#/, '');
    if (!/^(?:act-\d+|app-[0-9a-f]{16})$/.test(h) || !items || focused === h) return;
    const el = document.getElementById(h);
    if (!el) return;
    setFocused(h);
    el.scrollIntoView({ block: 'center' });
  }, [location.hash, items, focused]);

  const open = (it: InboxItem, reply?: string) => {
    void mark([it.id], 'read');
    const base = `/e/${env}/p/${it.projectId}`;
    if (!it.chatId || it.kind === 'memory') {
      navigate(base);
      return;
    }
    navigate(`${base}/c/${it.chatId}`, reply ? { state: { inboxReply: { text: reply, eventId: it.eventId } } } : undefined);
  };

  return (
    <div className="inbox">
      <div className="inbox-head">
        <h2>Waiting for you</h2>
        <div className="inbox-tools">
          <div className="seg" role="tablist">
            <button role="tab" aria-selected={filter === 'all'} className={filter === 'all' ? 'active' : ''} onClick={() => setFilter('all')}>All {items ? `(${items.length})` : ''}</button>
            <button role="tab" aria-selected={filter === 'unread'} className={filter === 'unread' ? 'active' : ''} onClick={() => setFilter('unread')}>Unread ({inbox.unread})</button>
          </div>
          <button className="btn small" disabled={!inbox.unread} onClick={() => void mark((items || []).filter((x) => x.unread).map((x) => x.id), 'read')}>Mark all read</button>
          <button className="btn small ghost" onClick={() => void refresh()} title="Refresh">↻</button>
        </div>
      </div>
      <p className="muted small">
        Questions, confirmations of risky actions, captcha and browser confirmations, failed jobs and memory candidates from chats you can write to.
        An item leaves the list when you answer in its chat; <em>Done</em> hides it until the agent writes again.
      </p>
      {error && <div className="error-text small">{error}</div>}
      {items === null && !error && <div className="muted small">Loading…</div>}
      {items && shown.length === 0 && <div className="muted small pad">{filter === 'unread' ? 'Nothing unread.' : 'No agent is waiting for you.'}</div>}
      <ul className="inbox-list">
        {shown.map((it) => (
          <InboxRow key={it.id} item={it} env={env} onOpen={open} onMark={mark} onChanged={refresh} focused={focused === it.id.replace(':', '-')} />
        ))}
      </ul>
    </div>
  );
}

function InboxRow({ item: it, env, onOpen, onMark, onChanged, focused }: {
  item: InboxItem;
  env: string;
  onOpen: (it: InboxItem, reply?: string) => void;
  onMark: (ids: string[], action: 'read' | 'unread' | 'done') => Promise<void>;
  onChanged: () => Promise<void>;
  focused?: boolean;
}) {
  const [reply, setReply] = useState('');
  const own = it.kind === 'memory' || it.kind === 'tools'; // decided in place, no chat of their own
  const quick = it.markers ? extractQuickReplies(it.markers) : null;
  const diag = it.kind === 'failed' ? diagnose({ error: it.error, text: it.excerpt, agent: it.agent || undefined, status: 'failed' }) : null;
  const options: string[] = it.kind === 'captcha' && it.code
    ? [`${it.code} да`, 'нет']
    : quick?.options.length
      ? quick.options
      : it.kind === 'confirm' ? ['Да', 'Нет'] : [];

  return (
    <li id={it.id.replace(':', '-')} className={`inbox-item kind-${it.kind} ${it.unread ? 'unread' : ''} ${focused ? 'focused' : ''}`}>
      <div className="inbox-line">
        <span className={`inbox-kind kind-${it.kind}`}>{KIND_LABEL[it.kind]}</span>
        <button className="link inbox-where" onClick={() => onOpen(it)} title="Open">
          {it.projectTitle}{own ? '' : ` · ${it.chatTitle || it.chatId}`}
        </button>
        <span className="spacer" />
        {!own && it.agent && <span className="muted tiny">{agentLabel(it.agent)}</span>}
        <span className="muted tiny">{formatLocal(it.utc)}</span>
        <button className="btn tiny ghost" onClick={() => void onMark([it.id], it.unread ? 'read' : 'unread')} title={it.unread ? 'Mark read' : 'Mark unread'}>{it.unread ? '●' : '○'}</button>
        {it.kind !== 'app' && <button className="btn tiny ghost" onClick={() => void onMark([it.id], 'done')} title="Done: hide until the agent writes again">Done</button>}
      </div>
      {diag ? (
        <div className="inbox-text"><strong>{diag.title}.</strong> {diag.todo}</div>
      ) : it.kind === 'memory' ? (
        <MemoryList item={it} env={env} onChanged={onChanged} />
      ) : it.kind === 'tools' ? (
        <ToolList item={it} env={env} onChanged={onChanged} />
      ) : it.kind === 'action' && it.action ? (
        <ActionCard item={it} action={it.action} onOpen={onOpen} />
      ) : it.kind === 'app' && it.app ? (
        <AppCard app={it.app} onChanged={onChanged} />
      ) : (
        it.excerpt && <div className="inbox-text">{it.excerpt}</div>
      )}
      {it.kind === 'captcha' && <div className="muted tiny">The agent asks to solve one captcha itself (screenshot in the chat). Answering “{it.code} да” allows only this one.</div>}
      {quick && quick.asks.length > 0 && (
        <ol className="inbox-asks">{quick.asks.map((a, i) => <li key={i}>{a.question}</li>)}</ol>
      )}
      {!own && (
        <div className="inbox-actions">
          {it.kind !== 'action' && it.kind !== 'app' && options.map((o) => (
            <button key={o} className="btn small" onClick={() => onOpen(it, o)} title="Open the chat and send this as your reply">{o}</button>
          ))}
          {it.kind !== 'failed' && it.kind !== 'app' && (
            <form className="inbox-reply" onSubmit={(e) => { e.preventDefault(); if (reply.trim()) onOpen(it, reply.trim()); }}>
              <input value={reply} onChange={(e) => setReply(e.target.value)} placeholder={quick?.asks.length ? 'Answer (or open the chat for the form)…' : 'Reply…'} aria-label="Reply" />
              <button className="btn small" type="submit" disabled={!reply.trim()}>Send</button>
            </form>
          )}
          <button className="btn small ghost" onClick={() => onOpen(it)}>Open chat</button>
        </div>
      )}
    </li>
  );
}

const ACTION_ICON: Record<string, string> = { send: '✉️', purchase: '💳', delete: '🗑️', publish: '📣', push: '⬆️', secret: '🔑', submit: '☑️', other: '⚠️' };

// 0.81.0: what the agent wants to do, in the words of the hold (who / what / where) and the raw call; Yes / No answer in the chat
function ActionCard({ item: it, action: a, onOpen }: { item: InboxItem; action: InboxAction; onOpen: (it: InboxItem, reply?: string) => void }) {
  const [busy, setBusy] = useState(false);
  const others = a.actions.filter((x) => x.summary !== a.summary);
  // The chat message "<code> да|нет" is the answer (read-guard grant reads it); the record is only bookkeeping, so it is
  // sent without waiting and the card stays in the list until the message is in the chat (server, review of 0.81.0).
  const decide = (yes: boolean) => {
    if (!it.code) return;
    setBusy(true);
    void dbActionDecide(a.requestId, yes ? 'approved' : 'denied').catch(() => undefined);
    onOpen(it, `${it.code} ${yes ? 'да' : 'нет'}`);
  };
  return (
    <div className="action-card">
      <div className="action-title">{ACTION_ICON[a.kind] || ACTION_ICON.other} <strong>{a.title}</strong></div>
      <div className="action-summary">{a.summary}</div>
      {others.length > 0 && (
        <ul className="action-more">{others.map((x, i) => <li key={i}>{(ACTION_ICON[x.kind] || '') + ' ' + x.summary}</li>)}</ul>
      )}
      {a.command && a.command !== a.summary && (
        <details className="action-cmd"><summary className="muted tiny">Command</summary><code>{a.command}</code></details>
      )}
      {a.tainted && <div className="action-warn small">⚠ This chat read text with signs of prompt injection. Say yes only if you asked for this yourself.</div>}
      <div className="muted tiny">
        Code {it.code} · Yes opens this kind of action in the chat for 15 min · waits until {formatLocal(a.expiresUtc)}
      </div>
      <div className="inbox-actions">
        <button className="btn small primary" disabled={busy || !it.code} onClick={() => decide(true)} title={`Send “${it.code} да” in the chat`}>Да</button>
        <button className="btn small" disabled={busy || !it.code} onClick={() => decide(false)} title={`Send “${it.code} нет” in the chat`}>Нет</button>
      </div>
    </div>
  );
}

const APP_ERROR: Record<string, string> = {
  already_decided: 'Already decided (here or on another device).',
  expired: 'The card expired: ask the agent again if it is still needed.',
  etag_mismatch: 'The request changed: reload the list and check it again.',
  not_found: 'This card is gone.',
};

// 0.120.0 (F14b-2): an outward action of an app waiting for me; Run executes it with my rights, as my click in the app would
function AppCard({ app: a, onChanged }: { app: InboxApp; onChanged: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const decide = async (run: boolean) => {
    setBusy(true);
    setErr(null);
    try {
      const r = await dbAppRequestDecide(a.requestId, run ? 'run' : 'deny', a.etag);
      if (r.ok) setDone(run ? 'Done.' : 'Declined.');
      else if (r.status === 'failed') setErr(`The app answered: ${String(r.result?.message || r.result?.error || 'error')}`);
      else setErr(APP_ERROR[r.error || ''] || r.error || 'Refused.');
      if (r.ok || r.status === 'failed' || r.error === 'already_decided' || r.error === 'expired' || r.error === 'not_found') {
        window.setTimeout(() => void onChanged(), 1500);
      }
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="action-card">
      <div className="action-title">{a.icon || '▦'} <strong>{a.title}</strong> <code className="muted tiny">{a.action}</code></div>
      <div className="action-summary">{a.description}</div>
      {a.args.length > 0 && (
        <ul className="action-more">
          {a.args.map((x) => <li key={x.name}><strong>{x.value}</strong> <span className="muted tiny">— {x.about || x.name}</span></li>)}
        </ul>
      )}
      <div className="muted tiny">An agent asks to do this outside the workbench. Run does it once, with your rights · waits until {formatLocal(a.expiresUtc)}</div>
      {err && <div className="error-text small">{err}</div>}
      {done ? <div className="small">{done}</div> : (
        <div className="inbox-actions">
          <button className="btn small primary" disabled={busy} onClick={() => void decide(true)} title="Run this action now, once">Run</button>
          <button className="btn small" disabled={busy} onClick={() => void decide(false)} title="Do not run it; the agent learns that you declined">Decline</button>
        </div>
      )}
    </div>
  );
}

function MemoryList({ item, env, onChanged }: { item: InboxItem; env: string; onChanged: () => Promise<void> }) {
  const navigate = useNavigate();
  const [gone, setGone] = useState<Record<number, 'accept' | 'reject'>>({});
  const [busy, setBusy] = useState<number | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [all, setAll] = useState(false); // collapsed to 3 so one project does not fill the page
  const [target, setTarget] = useState<Record<number, 'env' | 'project'>>({}); // a rule's target, switchable
  const tagOf = (c: InboxMemory): string => (c.kind === 'rule' ? target[c.id] ?? (c.tag === 'project' ? 'project' : 'env') : c.tag);
  // accept sends the tag and text as shown (the server requires both)
  const decide = async (c: InboxMemory, action: 'accept' | 'reject') => {
    const id = c.id;
    setBusy(id);
    setErr(null);
    try {
      await dbMemoryDecide(id, action, action === 'accept' ? { tag: tagOf(c), text: c.text } : undefined);
      setGone((g) => ({ ...g, [id]: action }));
    } catch (e) {
      const m = categorize(e).message;
      if (/already_decided/.test(m)) setGone((g) => ({ ...g, [id]: action }));
      else setErr(m);
    } finally {
      setBusy(null);
    }
  };
  const left = item.candidates.filter((c) => !gone[c.id]);
  useEffect(() => {
    if (item.candidates.length && !left.length) void onChanged();
  }, [left.length, item.candidates.length, onChanged]);
  return (
    <div className="inbox-memory">
      <div className="muted tiny">{item.count} candidate{item.count === 1 ? '' : 's'} for the project memory{item.count > item.candidates.length ? `, newest ${item.candidates.length} shown (all in the project panel)` : ''}</div>
      {err && <div className="error-text small">{err}</div>}
      <ul>
        {(all ? left : left.slice(0, 3)).map((c) => (
          <li key={c.id}>
            {c.kind === 'rule' ? (
              <select className="tag mem-rule" value={tagOf(c)} disabled={busy !== null} onChange={(e) => setTarget((t) => ({ ...t, [c.id]: e.target.value as 'env' | 'project' }))}
                title="Where the rule goes: Reflexes = the environment profile (every project), or this project's memory.md">
                <option value="env">{'rule → profile' + (c.targetEnv ? ` (${c.targetEnv})` : '')}</option>
                <option value="project">{'rule → project'}</option>
              </select>
            ) : (
              <span className={`tag mem-${c.tag}`}>{c.tag}</span>
            )}{' '}
            {c.text}
            {c.kind === 'rule' && c.reason && <span className="muted tiny">{` · “${c.reason}”`}</span>}
            <span className="muted tiny">{' · '}</span>
            <button className="link tiny" onClick={() => navigate(`/e/${env}/p/${item.projectId}/c/${c.chatId}`)} title="Open the chat">
              {(c.chatTitle || c.chatId) + (c.kind === 'rule' && c.msg ? ` #${c.msg}` : '')}
            </button>{' '}
            <button className="btn tiny" disabled={busy !== null} onClick={() => void decide(c, 'accept')}
              title={tagOf(c) === 'env' ? 'Accept: add to the environment profile (Reflexes)' : 'Accept: write to memory.md'}>✓</button>{' '}
            <button className="btn tiny ghost" disabled={busy !== null} onClick={() => void decide(c, 'reject')} title="Reject">✕</button>
          </li>
        ))}
      </ul>
      {left.length > 3 && <button className="link" onClick={() => setAll(!all)}>{all ? 'Show fewer' : `Show ${left.length - 3} more`}</button>}
    </div>
  );
}

// 0.75.0 (schema 16): proposals of the nightly tool library pass. Accept & run sends the agent task in a new chat of the project
// the server names (AI-Workbench); the agent changes the library through toolbox.py. The proposal is marked accepted
// only when that chat is created (ChatView → dbToolStarted); until then it comes back here after a reload.
function ToolList({ item, env, onChanged }: { item: InboxItem; env: string; onChanged: () => Promise<void> }) {
  const navigate = useNavigate();
  const [gone, setGone] = useState<Record<number, true>>({});
  const [busy, setBusy] = useState<number | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const left = item.tools.filter((c) => !gone[c.id]);
  useEffect(() => {
    if (item.tools.length && !left.length) void onChanged();
  }, [left.length, item.tools.length, onChanged]);
  const decide = async (c: ToolCandidate, action: 'accept' | 'reject') => {
    const again = c.offeredUtc ? `

Accept & run was already pressed ${formatLocal(c.offeredUtc)} and its chat was not confirmed: check the project's chats first, the task may be running.` : '';
    if (action === 'accept' && !window.confirm(`Run an agent job for "${c.title}"? It opens a new chat and changes the tool library through toolbox.py.${again}`)) return;
    setBusy(c.id);
    setErr(null);
    try {
      const r = await dbToolDecide(c.id, action);
      setGone((g) => ({ ...g, [c.id]: true }));
      if (r) navigate(`/e/${env}/p/${r.projectId || item.projectId}`, { state: { toolTask: { text: r.task, id: c.id } } });
    } catch (e) {
      const m = categorize(e).message;
      if (/already_decided/.test(m)) setGone((g) => ({ ...g, [c.id]: true }));
      else setErr(m);
    } finally {
      setBusy(null);
    }
  };
  return (
    <div className="inbox-memory">
      <div className="muted tiny">{item.count} proposal{item.count === 1 ? '' : 's'} from the nightly pass over agent jobs</div>
      {err && <div className="error-text small">{err}</div>}
      <ul>
        {left.map((c) => (
          <li key={c.id}>
            <span className={`tag tool-${c.kind}`}>{TOOL_KIND_LABEL[c.kind]}</span> <code>{c.tool}</code> {c.title}{' '}
            {c.offeredUtc && <span className="muted tiny" title="Accept & run was pressed, but the chat with the task was not confirmed">{`(run offered ${formatLocal(c.offeredUtc)}) `}</span>}
            <button className="link tiny" onClick={() => setOpen(open === c.id ? null : c.id)}>{open === c.id ? 'less' : 'details'}</button>{' '}
            <button className="btn tiny" disabled={busy !== null} onClick={() => void decide(c, 'accept')} title="Accept and run an agent job in a new chat">Accept &amp; run</button>{' '}
            <button className="btn tiny ghost" disabled={busy !== null} onClick={() => void decide(c, 'reject')} title="Reject: not proposed again">✕</button>
            {open === c.id && (
              <div className="inbox-text small">
                <div>{c.text}</div>
                {c.evidence.length > 0 && (
                  <div className="muted tiny">
                    {'Jobs: '}
                    {c.evidence.map((e, i) => (
                      <span key={e.jobId}>
                        {i > 0 && ', '}
                        <button className="link tiny" onClick={() => navigate(`/e/${env}/p/${e.projectId}/c/${e.chatId}`)} title={e.jobId}>{e.chatId || e.jobId}</button>
                      </span>
                    ))}
                  </div>
                )}
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
