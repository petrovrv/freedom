// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.90.0: the family chat as the first tab of the right panel ("👪 Family") instead of a chat in the project list.
// The room is a chat with chat.family in the hidden project with project.family (family-lib.php co_family_state finds
// both for the signed-in environment); the owner's CO Web creates the room on the first open. The tab shows the number
// of new messages of the others (read mark per environment on the server) and renders the room with ChatView docked.
import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import ChatView from './ChatView';
import { dbFamilyRead, dbFamilyState } from './dbapi';
import { categorize } from './errors';
import { useInterval } from './hooks';
import { setFamilyUnread, useChatPanel } from './chatPanel';
import { createChat, updateChatMeta, type HostDirs, type HostHealth, type Root } from './workbench';
import type { EnvironmentId } from './config';
import type { Chat, ChatEvent, LimitsFile, OwnerInfo, Project } from './types';
import { ownerName } from './members';

const POLL_MS = 20000;

export default function FamilyPanel(props: {
  root: Root | null;
  host: HostDirs | null;
  hosts: Record<string, HostDirs> | null;
  owners: Record<string, OwnerInfo | null>;
  env: EnvironmentId;
  author: string;
  owner: OwnerInfo | null;
  capabilities: string[];
  hostHealth: HostHealth;
  limits: LimitsFile | null;
  authOk: boolean;
}) {
  const { familySlot, tab } = useChatPanel();
  const [project, setProject] = useState<Project | null>(null);
  const [chat, setChat] = useState<Chat | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const creating = useRef(false);
  const shown = tab === 'family' && !!familySlot;

  const poll = useCallback(async () => {
    if (!props.authOk) return;
    try {
      const s = await dbFamilyState();
      setProject(s.project ? (s.project as Project) : null);
      // keep the same object while nothing changed, so the docked ChatView is not reset
      setChat((prev) => (s.chat && prev && prev.chatId === s.chat.chatId && prev.folderId === s.chat.folderId ? prev : (s.chat as Chat | null)));
      setFamilyUnread(shown && document.visibilityState === 'visible' ? 0 : s.unread);
      setErr(null);
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setLoaded(true);
    }
  }, [props.authOk, shown]);
  useEffect(() => { void poll(); }, [poll]);
  useInterval(() => void poll(), POLL_MS, props.authOk); // also on focus / becoming visible (0.109.0)

  // The owner opens the tab before the room exists: the room is created once (Drive folder + database row)
  useEffect(() => {
    if (!shown || !loaded || chat || !project || project.shareRole || creating.current || !props.authOk) return;
    creating.current = true;
    (async () => {
      const c = await createChat(project, 'Семья', 'chat');
      setChat(await updateChatMeta(c, { family: true, titleAuto: false }));
    })().catch((e: unknown) => {
      creating.current = false;
      setErr(categorize(e).message);
    });
  }, [shown, loaded, chat, project, props.authOk]);

  // Everything shown in the open tab counts as read
  const lastRead = useRef(0);
  const seen = useRef<ChatEvent[] | null>(null);
  const onEvents = useCallback((events: ChatEvent[] | null) => {
    seen.current = events;
    if (!chat || !events?.length || document.visibilityState !== 'visible') return;
    const seq = Math.max(...events.map((e) => e.sequence || 0));
    if (seq <= lastRead.current) return;
    lastRead.current = seq;
    setFamilyUnread(0);
    void dbFamilyRead(chat.chatId, seq).catch(() => { lastRead.current = 0; });
  }, [chat]);
  // 0.109.0: ChatView no longer hands over an unchanged list, so a mark missed in a hidden tab or a failed one is
  // retried on the panel's own poll and when the tab becomes visible
  const markAgain = useCallback(() => { if (seen.current) onEvents(seen.current); }, [onEvents]);
  useInterval(markAgain, POLL_MS, props.authOk && !!chat);

  if (!familySlot) return null;
  let body;
  if (!shown) body = null;
  else if (!props.authOk) body = <p className="muted pad">Sign in to see the family chat.</p>;
  else if (err && !chat) body = <div className="error-box pad">{err}</div>;
  else if (!loaded || (!chat && project && !project.shareRole)) body = <p className="muted pad">Opening the family chat…</p>;
  else if (!chat || !project) body = <p className="muted pad">The family chat is not open yet: {ownerName()} starts it from their CO.</p>;
  else if (!props.root || !props.host || !props.hosts) body = <p className="muted pad">Connecting…</p>;
  else body = (
    <ChatView
      key={chat.folderId}
      docked
      root={props.root}
      host={props.host}
      hosts={props.hosts}
      owners={props.owners}
      env={props.env}
      project={project}
      chat={chat}
      authOk={props.authOk}
      author={props.author}
      capabilities={props.capabilities}
      hostHealth={props.hostHealth}
      owner={props.owner}
      limits={props.limits}
      onEvents={onEvents}
      onChatUpdated={() => undefined}
      onChatAction={() => undefined}
      onChatCreated={() => undefined}
    />
  );
  return createPortal(body, familySlot);
}
