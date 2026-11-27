// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.128.0 (F15.2 part 4, SPEC §3.6b п. 1): what used to be the Files and Workspace tabs of the right panel is the
// content of the folders now. A chat folder holds the chat's files (version streams, FilesPanel.tsx), its scratch on
// Drive and the context folder chips; a project folder holds the project workspace. Shown under the items of the
// Explorer (and under the tree when the tree view is chosen); each part folds and loads only when unfolded.
// 0.129.0 (SPEC §3.6b, «Папка чата»): the chat folder has parts instead of one "Files of …": Subchats · Context ·
// Correspondence · Events · Watchers · Documents · Products (chatFolder.ts). Documents = files the person added,
// Products = files the agent made (with the scratch folder); versions stay in each file's ⋯ menu; the context folder
// chips moved to Context.
import { useEffect, useState, type ReactNode } from 'react';
import { FilesTab, useChatStreams } from './FilesPanel';
import { setCtxListSlot, setFilesSlot } from './chatPanel';
import { contextInputOf } from './ChatContext';
import {
  agreements, chatPartContributions, contextCount, dueState, localDate, onChatPartsChanged, partCounts, splitStreams, visibleParts,
  type Agreement, type ChatPartId,
} from './chatFolder';
import { visible } from './chatFiles';
import { dbProtocol, type ProtocolEntry } from './dbapi';
import { placeParent } from './chatTree';
import { isAgentTalk } from './explorerTree';
import { DriveTree } from './Properties';
import { chatScratchPath, type Root } from './workbench';
import type { Chat, OwnerInfo, Project } from './types';

export function Fold({ id, label, hint, children, defOpen = false }: { id: string; label: string; hint?: string; children: () => ReactNode; defOpen?: boolean }) {
  const key = `co.fold.${id}`;
  const [open, setOpen] = useState(() => {
    try {
      const v = localStorage.getItem(key);
      return v === null ? defOpen : v === '1';
    } catch {
      return defOpen;
    }
  });
  const toggle = () => {
    try {
      localStorage.setItem(key, open ? '0' : '1');
    } catch {
      /* storage unavailable */
    }
    setOpen(!open);
  };
  return (
    <div className="fold">
      <button type="button" className="fold-head" aria-expanded={open} title={hint} onClick={toggle}>
        <span aria-hidden="true">{open ? '▾' : '▸'}</span> {label}
      </button>
      {open && <div className="fold-body">{children()}</div>}
    </div>
  );
}

/** 0.129.0: the folder of the open chat - its parts (chatFolder.ts): Subchats · Context · Correspondence · Events ·
 * Watchers · Documents · Products; Documents and Products always, the others when they hold something. */
export function ChatFolder({ chat, project, chats, root, authOk, showSubchats, onOpenChat }: {
  chat: Chat; project: Project; chats: Chat[] | null; root: Root | null; authOk: boolean;
  /** the tree view lists the chats inside here; the Explorer shows them as its own items */
  showSubchats: boolean;
  onOpenChat: (projectId: string, chatId: string) => void;
}) {
  const ok = !!root && authOk && !chat.draft;
  const data = useChatStreams(chat, ok);
  const [selectedPart, setSelectedPart] = useState<ChatPartId | null>(null);
  const [proto, setProto] = useState<ProtocolEntry[] | null>(null);
  const [, setTick] = useState(0);
  useEffect(() => onChatPartsChanged(() => setTick((n) => n + 1)), []);
  useEffect(() => {
    if (!ok) return;
    let live = true;
    setProto(null);
    dbProtocol(chat.chatId).then((p) => { if (live) setProto(p?.entries ?? []); }, () => { if (live) setProto([]); });
    return () => { live = false; };
  }, [ok, chat.chatId, chat.updatedUtc]);
  if (!ok || !root) return null;
  const canWrite = chat.shareRole !== 'viewer' && project.shareRole !== 'viewer';
  const shared = !!(project.shareRole || chat.shareRole);
  const inside = (chats || []).filter((c) => placeParent(c) === chat.chatId && !c.archived && c.chatId !== chat.chatId);
  // 0.129.1: the person's own chats apart from the talks an agent started (explorerTree.ts isAgentTalk, 0.128.2)
  const own = inside.filter((c) => !isAgentTalk(c));
  const talks = inside.filter(isAgentTalk);
  // removed and expired files stay in their list behind "Show removed…", they do not count
  const split = data.streams ? splitStreams(data.streams) : null;
  const shownCount = (list: typeof data.streams) => (list ? list.filter((x) => visible(x)).length : undefined);
  const ag = agreements(proto);
  const ref = { projectId: project.projectId, chatId: chat.chatId };
  const active = proto ? proto.filter((e) => e.status === 'active').length : null;
  const counts = partCounts({
    subchats: showSubchats ? inside.length : 0,
    context: contextCount(contextInputOf(chat, project, chats, active, shared)),
    events: ag.open.length + ag.done.length,
    documents: shownCount(split?.documents ?? null),
    products: shownCount(split?.products ?? null),
  }, ref);
  const more = (part: ChatPartId) => chatPartContributions(part).map((c) => <div key={c.key} className="cf-more">{c.render(ref) as ReactNode}</div>);
  const body = (part: ChatPartId): ReactNode => {
    switch (part) {
      case 'subchats': {
        const item = (c: Chat, icon: string) => (
          <li key={c.chatId}><button type="button" className="link" onClick={() => onOpenChat(project.projectId, c.chatId)}>{icon} {c.title || c.chatId}</button></li>
        );
        return (
          <>
            {!!own.length && <ul className="cf-list">{own.map((c) => item(c, '💬'))}</ul>}
            {!!talks.length && (
              <details>
                <summary className="muted small">🤖 Agent talks ({talks.length})</summary>
                <ul className="cf-list">{talks.map((c) => item(c, '🤖'))}</ul>
              </details>
            )}
          </>
        );
      }
      case 'context':
        return (
          <>
            <div ref={setCtxListSlot} />
            <div className="files-slot" ref={setFilesSlot} />
            {more(part)}
          </>
        );
      case 'events':
        return (<><Agreements open={ag.open} done={ag.done} />{more(part)}</>);
      case 'documents':
        return (
          <>
            <FilesTab chat={chat} root={root} canWrite={canWrite} data={data} only={split?.documents ?? null}
              emptyText="No documents yet. Files you attach to a message appear here." />
            {more(part)}
          </>
        );
      case 'products':
        return (
          <>
            <FilesTab chat={chat} root={root} canWrite={canWrite} data={data} only={split?.products ?? null}
              emptyText="No products yet. Files the agent writes appear here." />
            <ChatScratch chat={chat} root={root} />
            {more(part)}
          </>
        );
      default:
        return more(part);
    }
  };
  return (
    <div className="chat-folder">
      <div className="cf-head small muted" title={chat.title || chat.chatId}>💬 Folder of «{chat.title || chat.chatId}»</div>
      <div className="cf-tiles" aria-label="Chat folder parts">
        {visibleParts(counts).map((p) => (
          <button type="button" key={p.id} className={`cf-tile${selectedPart === p.id ? ' selected' : ''}`}
            title={p.hint} aria-expanded={selectedPart === p.id} onClick={() => setSelectedPart(selectedPart === p.id ? null : p.id)}>
            <span className="cf-icon" aria-hidden="true">{p.icon}</span>
            <span>{p.label}{counts[p.id] && p.id !== 'context' ? ` (${counts[p.id]})` : ''}</span>
          </button>
        ))}
      </div>
      {selectedPart && visibleParts(counts).some((p) => p.id === selectedPart) && <div className="cf-content">{body(selectedPart)}</div>}
    </div>
  );
}

const DUE_CLASS = { overdue: 'bad', today: 'warn', soon: 'warn', later: '' } as const;

/** Events now: the agreements of the chat's protocol, open ones by due date; a click shows the message they came from. */
function Agreements({ open, done }: { open: Agreement[]; done: Agreement[] }) {
  const today = localDate();
  const jump = (msg: number) => document.querySelector(`.msg[data-seq="${msg}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  const row = (a: Agreement) => {
    const st = dueState(a.due, today);
    return (
      <li key={a.no} className={`cf-agree${a.status !== 'active' ? ' done' : ''}`}>
        <button type="button" className="link" title={`Agreement #${a.no} from message #${a.msg}: show the message`} onClick={() => jump(a.msg)}>
          🤝 {a.who && <b>{a.who}: </b>}{a.what}
        </button>
        {a.due && <span className={`tiny cf-due ${st ? DUE_CLASS[st] : ''}`}> · {st === 'overdue' ? 'overdue since' : st === 'today' ? 'today,' : 'by'} {a.due}</span>}
      </li>
    );
  };
  if (!open.length && !done.length) return <p className="muted small">No agreements yet. When someone in the chat promises to do something by a date, the protocol keeps it here.</p>;
  return (
    <div className="small">
      <ul className="cf-list">{open.map(row)}</ul>
      {!!done.length && (
        <details>
          <summary className="muted tiny">Done or called off ({done.length})</summary>
          <ul className="cf-list">{done.map(row)}</ul>
        </details>
      )}
      <div className="muted tiny">From the protocol of the chat. A reminder or an appointment is made only when you ask for it.</div>
    </div>
  );
}

function ChatScratch({ chat, root }: { chat: Chat; root: Root }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="small">
      <button type="button" className="link" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? '▾' : '▸'} Scratch folder on Drive</button>
      {open && <DriveTree root={root} path={chatScratchPath(chat)} emptyText="Empty. Chat-mode agents work here." />}
    </div>
  );
}

/** The workspace of a project (its Drive folder when the home computer keeps it on Drive). */
export function ProjectWorkspace({ project, owner, root, authOk }: { project: Project; owner: OwnerInfo | null; root: Root | null; authOk: boolean }) {
  // a project shared with me has no workspace of mine
  if (!root || !authOk || project.shareRole) return null;
  const path = owner?.projects?.[project.projectId]?.workspaceDriveRelativePath || null;
  return (
    <Fold id="workspace" label="🗂 Workspace" hint="The working folder of the project">
      {() => (path
        ? <DriveTree key={path} root={root} path={path} />
        : <p className="muted small">The workspace is on the home computer, outside Google Drive. Changed files arrive as artifacts.</p>)}
    </Fold>
  );
}
