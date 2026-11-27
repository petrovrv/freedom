// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createChildChat, lastSequence, readSnapshot, type ChatParentKind } from './lineage';
import type { MouseEvent as ReactMouseEvent, ReactElement } from 'react';
import { useDraft } from './drafts';
import JobTimeline from './JobTimeline';
import { markSeen } from './status';
import { HOME, SITE, hostStateText, label as hostLabel, routeHost } from './routing';
import { autoOrder } from './executors';
import { importLabel } from './importPoint';
import { ModuleHints, useModuleList } from './ModuleSlot';
import type { ModuleHint } from './extBridge';
import { applyBridgeEvents, emptyActivity, type ActivityItem, type ActivityState } from './activity';
import { AGENTS, ALLOWLIST, DEFAULT_BRANCH, PICKABLE_AGENTS, RETIRED_AGENTS, ENVIRONMENTS, HOST_ID, MAX_ATTACHMENT_BYTES, LONG_PASTE_CHARS, POLL_LISTS_MS, POLL_RUNNING_EVENTS_MS, type AgentId, type EnvironmentId } from './config';
import { extractTitle, heuristicTitle, stripTitleMarker } from './chatTitle';
import { categorize } from './errors';
import { diagnose } from './diagnose';
import { remoteDevice, useDeviceToggle, useInterval, useLocalDevice } from './hooks';
import { WebDevicePicker, useWebDevicePick } from './WebDevicePicker';
import { createPortal } from 'react-dom';
import { BrowserSettingsPanel } from './BrowserSettings';
import { openPanel, useChatPanel } from './chatPanel';
import { ChatContext } from './ChatContext';
import { buildFolderBlock, folderKey, folderLevels, levelsToSend, memoryDigest, projectCtxOf, type FolderLevel, type LevelContent } from './folderContext';
import { FolderCtxSection } from './FolderCtx';
import type { ChatAction } from './ChatActions';
import { FolderPicker } from './FolderPicker';
import { FileMenu, googleDocsUrl, hasGoogleDocs, moduleFileItems, viewerHref } from './FileMenu';
import { useOpenFile, type FileAskState } from './FileViewer';
import { COMPOSER_SENT, MessageCard, ModuleSlots, useLoadedModules, type ComposerInput } from './ModuleSlot';
import { cardsCheckKey, cardsLive, extractCards } from './messageCards';
import type { ModuleInfo } from './extBridge';
import type { ChatRef } from './chatFolder';
import { Markdown } from './Markdown';
import { extractQuickReplies, formatAnswers, stripQuickReplies, type AskItem } from './quickReplies';
import { decodeFastResult, publishJobReadyDb, watchJob, type FastMessage } from './pubsub';
import { defaultSelection } from './defaults';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { shareText, takeShare } from './share';
import { chatPermissions, type ArtifactRecord, type AttachmentRef, type Chat, type ChatEvent, type EnvTemplate, type JobPhase, type LimitsFile, type Mode, type OwnerInfo, type Project, type Usage } from './types';
import { PermissionsBar } from './Permissions';
import { RowResizer } from './Resizer';
import { pingSiteAgent } from './siteAgent';
import { formatBytes, formatLocal, safeFileName, sha256Hex } from './util';
import { buildStreams, fetchVersion, isWorkingFile, matchUpload, uploadTarget, type ChatStream } from './chatFiles';
import { UndoPanel } from './UndoPanel';
import { canHide, canRewrite, hiddenOf, restorable, type DeletedBatch } from './msgEdit';
import {
  appendEvent, copyToFolder, DbUnavailableError, fetchArtifact, finalizeTurn, locateArtifact, loadChatEvents, newCursor, readBridgeEvents, readJob, envProfileBlock, projectMemoryBlock, readProjectContextFile, readProjectInstructions, speaker,
  appendFamilyMessage, appendReaction, familyTranscript, createChat, eventHost, requestCancel, sendTurn, updateChatMeta, wantsTitle, type EventCursor, type HostDirs, type HostHealth, type Root,
} from './workbench';
import { dbDossierContext, dbFollowupAdd, dbFollowupCancel, dbFollowupDone, dbFollowupList, dbFollowupTake, dbGetTemplates, dbProtocol, dbPutTemplates, dbQueueOn, dbRelated, dbSharesGet, dbToolStarted, dbMsgDelete, dbMsgRestore, dbMsgRewrite, dbMemoryDecide, dbDossierPut, type Followup, type MsgHideResult } from './dbapi';
import { hintsBlock, storedHints, strongHits, wantsHints, type Related } from './secretaryHints';
import { buildLevels, CONTEXT_MODE_HINT, CONTEXT_MODE_LABEL, type ContextMode } from './contextLevels';
import { sideJobOf, sideReplies, viewMessage, type SideJob } from './extBridge';
import QuoteAsk from './QuoteAsk';
import { quoteDisplay } from './chatQuote';
import { inheritedModes, linesBetween, modeDoc, modeLines, modeOn, modePatch, modesText, switchWho, type ModeDecl, type ModeLine } from './moduleModes';
import { modulePartsNote } from './chatFolder';
import { contextOf, moduleList, modesOf, sideJobsOf, useModuleList as useModuleDecls, viewsOf } from './moduleStore';
import { FamilyComposer, FamilyMessage, Lightbox, familyBlock, followsTopic, isSilent, reactionsOf, wantsCo } from './FamilyChat';

// names for the lines of mode switches (Google identity -> its environment's name)
// Freedom F1: built on use - the member list arrives from the server after this module is loaded
const switchNames = (): Record<string, string> => Object.fromEntries(
  Object.entries(ALLOWLIST).map(([mail, a]) => [mail, ENVIRONMENTS.find((e) => e.id === a.env)?.displayName || a.env]),
);
import { brokenRun, PLAN_DIR, PLAN_FILE, planApplies, planHeuristic, planPath, resumeBlock, resumeUnreadBlock } from './workPlan';
import { getText, resolveFile } from './drive';
import { ProtocolCell, ProtocolHeader, protocolPaneOn, setProtocolPaneOn, useProtocol } from './Protocol';
import { outboxAdd, outboxList, outboxPut, outboxRemove, outboxSubscribe, outboxTake, watchOnline, type OutboxItem } from './outbox';
import { gateway, gatewayBlocked, gatewayTools, gatewayVersion } from './gateway';
import { ModelPicker } from './ModelPicker';
import { assistantName } from './members';

interface Props {
  // 0.90.0: the family room (FamilyPanel, app 👪): no head, no panel slots, no navigation state
  docked?: boolean;
  // F15.2 part 4: the chats of the project, for the folders above this chat (folderContext.ts)
  projectChats?: Chat[] | null;
  // 0.130.0: the project of the Explorer folder shown (undefined: tree view); another project than this chat's is flagged
  folderProjectId?: string;
  root: Root;
  host: HostDirs;
  // All executors by hostId: a job is observed on the host that received it.
  hosts: Record<string, HostDirs>;
  // 0.29.0: owner.json of every executor (routing and failover)
  owners: Record<string, OwnerInfo | null>;
  // S8: the executor came from the 'auto' preference (the server may hand an untaken job to the other one)
  hostAuto?: boolean;
  holdOwn?: boolean; // 0.110.0: CO Desktop waits for the Bridge of its own computer (routing.ownBridgeWaiting)
  env: EnvironmentId;
  project: Project;
  chat: Chat;
  authOk: boolean;
  author: string;
  capabilities: string[];
  hostHealth: HostHealth;
  owner: OwnerInfo | null;
  limits: LimitsFile | null;
  onChatUpdated: () => void;
  onChatAction: (a: ChatAction) => void;
  onProjectChanged?: (p: Project) => void;
  // 0.17.0: a draft chat was created by its first message; the parent switches to its URL.
  onChatCreated?: (c: Chat) => void;
  // 0.37.0: a template switches the executor (home / site)
  onHostChange?: (hostId: string) => void;
  // F10 (ext/1 chat.mode viewer): a module's viewer next to the chat - the mode and its document (a new chat gets it as
  // <key>Doc and the mode on), questions it sends, and the chat's events for the viewer
  viewer?: { mode: ModeDecl; doc: string };
  /** 0.135.0: send false - the text only goes into the message box, the person sends it */
  viewerAsk?: { text: string; files: File[]; nonce: number; send?: boolean } | null;
  onEvents?: (events: ChatEvent[] | null) => void;
  // whether the viewer may ask here (not read-only, the host takes attachments)
  onCanAsk?: (ok: boolean) => void;
}

export interface Selection {
  agent: AgentId;
  mode: Mode;
  model: string | null;
  effort: string | null;
  // S6 (0.100.0): executor picked with the model (ModelPicker) when it is not the chat's current one; routeHost may still
  // move the job (device tools, home-only agents, failover)
  host?: string | null;
}

interface Monitor {
  phase: JobPhase;
  activity: ActivityState;
  cursor: EventCursor;
  tick: number;
  finalizing?: boolean;
}

const TERMINAL: JobPhase[] = ['completed', 'failed', 'cancelled', 'rejected'];

const handledViewerAsks = new Set<number>(); // questions of a mode viewer already put into a composer

export const agentName = (a?: string) => AGENTS.find((x) => x.id === a)?.displayName || a || 'Agent';

export default function ChatView(props: Props) {
  const { root, host, hosts, project, authOk, capabilities } = props;
  const hostOf = useCallback((e: ChatEvent) => hosts[eventHost(e)] || host, [hosts, host]);
  // 0.20.0: with Bridge 1.3.0 ("db-queue") the server writes the answer of a home-main job into the chat.
  // Freedom F12: so does it for a job of any Bridge executor with the database queue (not the site agent)
  const serverFinalizes = useCallback((e: ChatEvent) => {
    const h = hostOf(e)?.hostId || HOST_ID;
    if (h === SITE) return false;
    const caps = h === host?.hostId ? capabilities : props.owners[h]?.capabilities;
    // owner.json of that host not loaded (yet): as before 0.104.0, so the client never finalizes a second copy
    if (!caps) return capabilities.includes('db-queue') && h === HOST_ID;
    return caps.includes('db-queue');
  }, [hostOf, host, capabilities, props.owners]);
  const [chat, setChat] = useState(props.chat);
  useEffect(() => setChat(props.chat), [props.chat.title, props.chat.projectId, props.chat.trashedUtc, props.chat.archived, props.chat.folderContext]); // eslint-disable-line react-hooks/exhaustive-deps -- F15.2: trash and archive state, the folder switch (another device) too
  const [events, setEvents] = useState<ChatEvent[] | null>(null);
  // 0.55.0: files of this chat (from its messages), to recognise an upload as a new version of one of them
  const composerStreams = useMemo<ChatStream[]>(() => buildStreams(events || [], null, project.projectId, chat.chatId), [events, project.projectId, chat.chatId]);
  const [loadErr, setLoadErr] = useState<string | null>(null);
  const [staleUtc, setStaleUtc] = useState<string | null>(null); // 0.106.0: messages on screen are a copy from then
  const lastLoadUtc = useRef<string | null>(null);
  const [monitors, setMonitors] = useState<Record<string, Monitor>>({});
  const monRef = useRef(monitors);
  const finalizingRef = useRef(new Set<string>());
  monRef.current = monitors;
  const eventsRef = useRef(events);
  eventsRef.current = events;
  const [prefill, setPrefill] = useState<Prefill | null>(null);
  const { onEvents, viewerAsk } = props;
  useEffect(() => onEvents?.(events), [events, onEvents]);
  useEffect(() => {
    // once per question: the chat view of a new chat mounts again after its first message
    if (!viewerAsk || handledViewerAsks.has(viewerAsk.nonce)) return;
    handledViewerAsks.add(viewerAsk.nonce);
    setPrefill({ text: viewerAsk.text, files: viewerAsk.files, send: viewerAsk.send !== false, nonce: viewerAsk.nonce });
  }, [viewerAsk]);
  // F10: what enabled modules add to chats (modes, message views, side-job rows, context)
  const mods = useModuleDecls();
  const modes = useMemo(() => modesOf(mods), [mods]);
  const sideJobs = useMemo(() => sideJobsOf(mods), [mods]);
  const isSide = useCallback((e: ChatEvent) => sideJobOf(sideJobs, e) !== null, [sideJobs]);
  const pendingTool = useRef<number | null>(null); // 0.75.0: id of the tool library proposal whose task this draft sends
  const bottomRef = useRef<HTMLDivElement>(null);
  const msgBox = useRef<HTMLDivElement>(null); // 0.82.0: Ask / WTF about selected text of an answer

  // CO Web 0.14.0: a share chosen in ShareDialog arrives as navigation state and prefills the composer once
  const location = useLocation();
  const navigate = useNavigate();
  const handledShareId = useRef<string | null>(null);
  useEffect(() => {
    const shareId = (location.state as { shareId?: string } | null)?.shareId;
    if (props.docked || !shareId || handledShareId.current === shareId) return;
    handledShareId.current = shareId;
    const target = location.pathname + location.search;
    takeShare(shareId)
      .then((s) => {
        if (s) setPrefill({ text: shareText(s), files: s.files, nonce: Date.now() });
      })
      .catch(() => undefined)
      .finally(() => navigate(target, { replace: true, state: null }));
  }, [location.state, location.pathname, location.search, navigate, props.docked]);
  // 0.132.0: "Ask in chat" of an open file (FileViewer.tsx askAboutFile, ext/1 file.ask) arrives as navigation state:
  // the file's path and the quote go into the composer once; the person writes the question and sends it
  const handledAsk = useRef(0);
  useEffect(() => {
    const ask = (location.state as Partial<FileAskState> | null)?.fileAsk;
    if (props.docked || !ask || handledAsk.current === ask.nonce) return;
    handledAsk.current = ask.nonce;
    setPrefill({ text: ask.text, nonce: ask.nonce });
    navigate(location.pathname + location.search, { replace: true, state: null });
  }, [location.state, location.pathname, location.search, navigate, props.docked]);

  // 0.109.0: one load at a time (a call during a load runs once more after it), and the list on screen is kept when
  // the database returns the same events, so polls do not re-render the whole chat while the user types.
  const loading = useRef<{ p: Promise<void> | null; again: boolean }>({ p: null, again: false });
  const shown = useRef<{ list: ChatEvent[] | null; sig: string }>({ list: null, sig: '' });
  const reloadOnce = useCallback(async () => {
    if (!authOk) return;
    if (chat.draft) {
      setEvents([]); // nothing on Drive yet
      return;
    }
    try {
      const ev = await loadChatEvents(chat);
      const sig = JSON.stringify(ev);
      if (eventsRef.current && eventsRef.current === shown.current.list && sig === shown.current.sig) {
        // same events as on screen: no new list
      } else {
        shown.current = { list: ev, sig };
        setEvents(ev);
      }
      setLoadErr(null);
      setStaleUtc(null);
      lastLoadUtc.current = new Date().toISOString();
    } catch (e) {
      // 0.106.0: database down — keep what is on screen, or show this browser's last copy, and say so (not a red error)
      if (e instanceof DbUnavailableError) {
        const copy = e.copy;
        if (!eventsRef.current?.length && copy) {
          setEvents(copy.events);
          setStaleUtc(copy.savedUtc);
        } else if (eventsRef.current?.length) {
          setStaleUtc((v) => v || lastLoadUtc.current || new Date().toISOString());
        }
        setLoadErr(e.message);
        return;
      }
      setLoadErr(categorize(e).message);
    }
  }, [chat, authOk]);
  const reload = useCallback((): Promise<void> => {
    const st = loading.current;
    if (st.p) {
      st.again = true;
      return st.p;
    }
    st.p = (async () => {
      do {
        st.again = false;
        await reloadOnce();
      } while (st.again);
    })().finally(() => {
      st.p = null;
    });
    return st.p;
  }, [reloadOnce]);

  useEffect(() => {
    void reload();
  }, [reload]);
  // 0.109.0: one poll of the chat (it was two or three, each with its own focus listener: doublets on Android).
  // Family chat every 4 s; while the database does not answer, every 10 s (0.106.0); else POLL_LISTS_MS.
  useInterval(reload, chat.family && !chat.draft ? 4000 : loadErr ? 10000 : POLL_LISTS_MS, authOk);
  useEffect(() => {
    const on = () => void reload();
    window.addEventListener('online', on);
    return () => window.removeEventListener('online', on);
  }, [reload]);

  // 0.32.0: the chat protocol (server secretary) as a mirror column next to the messages.
  // 0.90.2: never shown in the family chat (the secretary still keeps it)
  const [protoPane, setProtoOn] = useState(protocolPaneOn);
  const { data: proto } = useProtocol(chat.draft ? null : chat.chatId, protoPane && authOk, POLL_LISTS_MS);
  const protoOn = protoPane && !chat.family;

  const pending = useMemo(() => {
    if (!events) return [];
    const done = new Set(events.filter((e) => e.type === 'assistant_message').map((e) => e.jobId));
    return events.filter((e) => e.type === 'user_message' && e.jobId && !done.has(e.jobId));
  }, [events]);
  // side jobs of modules (own agent session) never hold back the chat's own messages
  const chatPending = useMemo(() => pending.filter((e) => !isSide(e)), [pending, isSide]);

  const cancelled = useMemo(() => new Set((events || []).filter((e) => e.type === 'cancel_requested').map((e) => e.jobId)), [events]);

  // Live monitoring of unfinished jobs (TZ section 17: ~1.5 s for the running chat).
  const pollJobs = useCallback(async () => {
    if (!authOk || !pending.length) return;
    for (const u of pending) {
      const jobId = u.jobId!;
      const m: Monitor = monRef.current[jobId] || { phase: 'queued', activity: emptyActivity(), cursor: newCursor(), tick: 0 };
      if (m.finalizing) continue;
      try {
        if (finalizingRef.current.has(jobId)) continue;
        const jobHost = hostOf(u);
        const bev = await readBridgeEvents(jobHost, jobId, m.cursor, u.queue === 'db');
        const activity = bev.length ? applyBridgeEvents(m.activity, bev) : m.activity;
        let phase = m.phase;
        if (bev.some((b) => b.type === 'claimed' || b.type === 'started')) phase = phase === 'queued' ? 'running' : phase;
        const tick = m.tick + 1;
        let terminal = false;
        if (tick % 2 === 1 || bev.some((b) => ['completed', 'failed', 'cancelled', 'timeout'].includes(b.type))) {
          const snap = await readJob(jobHost, jobId, u.queue === 'db');
          if (snap.phase !== 'preparing') phase = snap.phase;
          if (TERMINAL.includes(snap.phase) && !finalizingRef.current.has(jobId)) {
            finalizingRef.current.add(jobId);
            terminal = true;
            setMonitors((prev) => ({ ...prev, [jobId]: { ...m, activity, phase, tick, finalizing: true } }));
            const latest = eventsRef.current || [];
            await finalizeTurn(chat, latest, u, snap, serverFinalizes(u));
            await reload();
          }
        }
        if (!terminal) {
          if (cancelled.has(jobId) && !TERMINAL.includes(phase)) phase = 'cancel_requested';
          // Merge with whatever the fast path delivered meanwhile (sequence dedupe in applyBridgeEvents).
          setMonitors((prev) => {
            const cur = prev[jobId];
            const merged = cur && cur !== m ? (bev.length ? applyBridgeEvents(cur.activity, bev) : cur.activity) : activity;
            const ph = cur && cur.phase === 'running' && phase === 'queued' ? 'running' : phase;
            return { ...prev, [jobId]: { ...(cur || m), cursor: m.cursor, activity: merged, phase: ph, tick } };
          });
        }
      } catch (e) {
        console.warn('poll', jobId, e);
      }
    }
  }, [authOk, pending, hostOf, chat, reload, cancelled, serverFinalizes]);
  useInterval(pollJobs, POLL_RUNNING_EVENTS_MS, authOk && pending.length > 0);

  // Fast path (Bridge v0.9 via Pub/Sub): live events and the result, ahead of Drive.
  const onFast = useCallback(
    async (u: ChatEvent, msg: FastMessage) => {
      const jobId = u.jobId!;
      if (finalizingRef.current.has(jobId)) return;
      const blank: Monitor = { phase: 'queued', activity: emptyActivity(), cursor: newCursor(), tick: 0 };
      if (msg.type === 'job.claimed' || msg.type === 'job.events') {
        const evs = msg.type === 'job.events' ? msg.data?.events || [] : [];
        setMonitors((prev) => {
          const cur = prev[jobId] || blank;
          if (cur.finalizing) return prev;
          const phase = cur.phase === 'queued' || cur.phase === 'preparing' ? 'running' : cur.phase;
          return { ...prev, [jobId]: { ...cur, phase, activity: evs.length ? applyBridgeEvents(cur.activity, evs) : cur.activity } };
        });
        return;
      }
      if (msg.type === 'job.result') {
        const result = await decodeFastResult(msg.data, sha256Hex);
        if (!result || finalizingRef.current.has(jobId)) return;
        finalizingRef.current.add(jobId);
        const phase: JobPhase = result.status === 'completed' ? 'completed' : result.status === 'cancelled' ? 'cancelled' : 'failed';
        setMonitors((prev) => ({ ...prev, [jobId]: { ...(prev[jobId] || blank), phase, finalizing: true } }));
        try {
          await finalizeTurn(chat, eventsRef.current || [], u, { phase, result }, serverFinalizes(u));
        } catch (e) {
          finalizingRef.current.delete(jobId); // Drive polling finishes it
          console.warn('fast finalize', e);
        }
        await reload();
      }
    },
    [chat, reload, serverFinalizes],
  );
  const pendingKey = pending.map((p) => p.jobId).join(',');
  useEffect(() => {
    if (!authOk || !pending.length) return;
    const offs = pending.map((u) => watchJob(u.jobId!, (msg) => void onFast(u, msg)));
    return () => offs.forEach((off) => off());
  }, [pendingKey, authOk, onFast]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (pending.length) void pollJobs();
  }, [pending.length]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ block: 'end' });
  }, [events?.length, pending.length]);

  const [routeNote, setRouteNote] = useState<string | null>(null);
  const [sideNote, setSideNote] = useState<{ chatId: string; title: string } | null>(null);
  // 0.33.0: a child chat shows (and gives the agent) its parent's history up to the branch point
  const [inherited, setInherited] = useState<ChatEvent[]>([]);
  useEffect(() => {
    setInherited([]);
    if (chat.parent) void readSnapshot(chat.parent).then((s) => setInherited(s.events)).catch((e: unknown) => setBranchErr(categorize(e).message));
  }, [chat.parent?.chatId, chat.parent?.seq]); // eslint-disable-line react-hooks/exhaustive-deps
  const [branchErr, setBranchErr] = useState<string | null>(null);
  async function branchAt(seq: number, kind: ChatParentKind, draftText = '') {
    setBranchErr(null);
    // Only from a saved chat whose history is loaded: a branch point 0 would drop the parent's messages for good.
    if (chat.draft || !chat.chatId || events === null || loadErr || (kind === 'followup' && seq <= 0)) {
      setBranchErr('The chat history is not loaded yet; branch after it appears.');
      return;
    }
    try {
      let child = await createChildChat(project, chat, seq, kind, chat.defaultMode || 'cowork');
      const keep = inheritedModes(chat, project, modes); // modes that inherit stay on in branches
      if (Object.keys(keep).length) child = await updateChatMeta(child, keep);
      if (draftText) {
        try {
          localStorage.setItem('co.draft.' + child.chatId, JSON.stringify({ t: draftText, u: Date.now() }));
        } catch {
          /* draft is a convenience */
        }
      }
      props.onChatCreated?.(child);
    } catch (e) {
      setBranchErr(categorize(e).message);
    }
  }
  // 0.31.0: the open chat counts as read (chat list status icons)
  useEffect(() => {
    // Latest of the chat record, the newest loaded event and now: chat.updatedUtc is not refreshed by new events.
    if (chat.draft || !chat.chatId) return;
    const last = events?.length ? events[events.length - 1].createdUtc || '' : '';
    const seen = [chat.updatedUtc || '', last, new Date().toISOString()].sort().pop() as string;
    markSeen(chat.chatId, seen);
  }, [chat.chatId, chat.updatedUtc, chat.draft, events?.length]);
  const localDevice = useLocalDevice();
  // 0.42.0: without CO Desktop (a phone) the home PC's device from owner.json (Bridge 1.4.1)
  // 0.79.0: not with the site selected - the remote device forced every phone message to home (03.11, a job waited 17 min
  // behind a Bridge update while Site was chosen); a local CO Desktop device still routes home as before
  const device = useMemo(() => localDevice ?? (host.hostId === SITE ? null : remoteDevice(props.owners[HOME], props.env)),
    [localDevice, host.hostId, props.owners, props.env]);
  const [useDevice, setUseDevice] = useDeviceToggle();
  const webPick = useWebDevicePick(props.author); // 0.49.0: web devices (browsers with connected folders)
  // 0.45.0: shared project/chat -> no devices, context folders
  const [shared, setShared] = useState<boolean>(!!project.shareRole);
  // 0.138.0: env/project/chat this lookup came back empty for; cards wait for it. Another chat, env or project never
  // matches the key; a failed refresh clears it; role or draft changes are checked by cardsLive itself
  const [shareChecked, setShareChecked] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    const byProject = !!chat.draft || !chat.chatId;
    dbSharesGet(byProject ? 'project' : 'chat', byProject ? project.projectId : chat.chatId)
      .then((info) => {
        if (!alive) return;
        const any = !!project.shareRole || info.shares.length > 0 || info.projectShares.length > 0;
        setShared(any);
        setShareChecked(cardsCheckKey(info, project.shareRole, `${props.env}/${project.projectId}/${chat.chatId}`));
      })
      .catch(() => { if (alive) setShareChecked(null); /* sharing: keep the current value */ });
    return () => { alive = false; };
  }, [chat.chatId, chat.draft, project.projectId, project.shareRole, props.env]);
  // 0.45.0: viewers, and new chats in chat-shared projects, cannot send
  // chats shared one by one use the chat's own role
  const readOnly =
    project.shareVia === 'chat'
      ? !!chat.draft || chat.shareRole !== 'editor'
      : // 0.45.0: project viewers can write in chats shared to them as editor (not drafts)
        project.shareRole === 'viewer' && (!!chat.draft || chat.shareRole !== 'editor');
  // 0.134.0 (msgEdit.ts): delete a message / rewrite from here; the server checks the rights again (rule 4)
  const chatOwner = !project.shareRole && !chat.shareRole;
  const [msgDone, setMsgDone] = useState<{ res: MsgHideResult; what: string } | null>(null);
  const [msgErr, setMsgErr] = useState<string | null>(null);
  const [msgUndo, setMsgUndo] = useState<ChatEvent | null>(null);
  const rewriteOf = useRef<number | null>(null);
  const [rewriting, setRewriting] = useState<number | null>(null); // shown above the composer; ✕ drops the mark
  // hiddenOf() is refreshed by every load of the events
  const batches = useMemo(() => (events ? restorable(hiddenOf(chat.chatId), Date.now()) : []), [chat.chatId, events]);
  const hideMsg = async (e: ChatEvent, mode: 'pair' | 'answer' | 'rewrite') => {
    const what = mode === 'rewrite' ? `everything from #${e.sequence} on` : mode === 'pair' ? `message #${e.sequence} with its answer` : `answer #${e.sequence}`;
    const note = mode === 'rewrite'
      ? `Rewrite from #${e.sequence}: ${what} is forgotten (hidden, restorable for 30 days), running jobs of these messages stop, the protocol goes back to #${e.sequence - 1} and the agent starts a new session. The text goes back into the message box.`
      : `Delete ${what}? It is hidden (restorable for 30 days) and the agent starts a new session.`;
    if (!window.confirm(note)) return;
    setMsgErr(null);
    try {
      const res = mode === 'rewrite' ? await dbMsgRewrite(chat.chatId, e.sequence) : await dbMsgDelete(chat.chatId, e.sequence, mode);
      setMsgDone({ res, what });
      if (mode === 'rewrite') {
        rewriteOf.current = e.sequence;
        setRewriting(e.sequence);
        setPrefill({ text: e.text || '', agent: e.agent, nonce: Date.now() });
      }
      await reload();
    } catch (x) {
      setMsgErr(categorize(x).message);
    }
  };
  const restoreBatch = async (b: DeletedBatch) => {
    setMsgErr(null);
    try {
      await dbMsgRestore(chat.chatId, b.batch);
      if (msgDone?.res.batch === b.batch) setMsgDone(null);
      await reload();
    } catch (x) {
      setMsgErr(categorize(x).message);
    }
  };
  // 0.89.0: family chat (FamilyChat.tsx) - people write to each other, @co calls the agent
  const family = !!chat.family;
  // 0.138.0 ext/1 ui.messageCard: module cards in agent answers only in the person's own writable chat
  const cardModules = useModuleList();
  const cardsOn = cardsLive({ readOnly, shared, family, draft: !!chat.draft, child: !!chat.parent, shareRole: project.shareRole || chat.shareRole,
    checked: shareChecked, key: `${props.env}/${project.projectId}/${chat.chatId}` });
  const reactions = useMemo(() => reactionsOf(events || []), [events]);
  const evById = useMemo(() => new Map((events || []).map((e) => [e.eventId, e])), [events]);
  const [famReply, setFamReply] = useState<ChatEvent | null>(null);
  const [lightbox, setLightbox] = useState<string | null>(null);
  const viewerCanAsk = !readOnly && (capabilities.length === 0 || capabilities.includes('attachments'));
  const { onCanAsk } = props;
  useEffect(() => onCanAsk?.(viewerCanAsk), [viewerCanAsk, onCanAsk]);
  // 0.37.0: environment templates (new-chat screen only)
  const [templates, setTemplates] = useState<EnvTemplate[] | null>(null);
  const [tplErr, setTplErr] = useState<string | null>(null);
  const tplRev = useRef(''); // sha of the set last loaded or stored
  useEffect(() => {
    if (!chat.draft) return;
    let alive = true;
    dbGetTemplates(props.env)
      .then((s) => {
        if (!alive) return;
        tplRev.current = s.rev;
        setTemplates(s.list);
      })
      .catch((e) => { if (alive) setTplErr(categorize(e).message); });
    return () => { alive = false; };
  }, [chat.draft, props.env]);

  async function storeTemplates(list: EnvTemplate[]): Promise<void> {
    setTplErr(null);
    try {
      const r = await dbPutTemplates(props.env, list, tplRev.current);
      if (r.ok) {
        tplRev.current = r.rev;
        setTemplates(list);
      } else {
        tplRev.current = r.current.rev;
        setTemplates(r.current.list);
        setTplErr('Templates were changed on another computer and reloaded; repeat the change.');
      }
    } catch (e) {
      setTplErr(categorize(e).message);
    }
  }

  async function saveTemplate(sel: Selection): Promise<void> {
    if (templates === null) { setTplErr('Templates are not loaded yet; try again in a moment.'); return; }
    const raw = window.prompt('Template name', `${agentName(sel.agent)} ${sel.mode}`);
    if (raw === null) return;
    const name = raw.trim().slice(0, 40);
    if (!name) return;
    const rest = (templates || []).filter((x) => x.name !== name);
    if (rest.length >= 30) {
      setTplErr('At most 30 templates per environment.');
      return;
    }
    const t: EnvTemplate = {
      name, agent: sel.agent, mode: sel.mode, model: sel.model, effort: sel.effort, hostId: sel.host || host.hostId,
      permissions: chatPermissions(chat.permissions), useDevice,
    };
    const old = (templates || []).findIndex((x) => x.name === name);
    await storeTemplates(old >= 0 ? (templates || []).map((x, i) => (i === old ? t : x)) : [...rest, t]);
  }

  async function deleteTemplate(name: string): Promise<void> {
    if (templates === null) { setTplErr('Templates are not loaded yet; try again in a moment.'); return; }
    if (!window.confirm(`Delete template "${name}"?`)) return;
    await storeTemplates((templates || []).filter((x) => x.name !== name));
  }

  function applyTemplate(t: EnvTemplate): void {
    if (t.hostId && t.hostId !== host.hostId) props.onHostChange?.(t.hostId);
    if (t.permissions) setChat({ ...chat, permissions: t.permissions });
    if (typeof t.useDevice === 'boolean') setUseDevice(t.useDevice);
  }
  // 0.45.0: policy of the project's (owner's) environment
  const policy = props.owner?.chatProjectEnvironments?.[project.environmentId];
  const projectReg =
    props.owner?.projects?.[project.projectId] ||
    (policy
      ? {
          environmentId: project.environmentId, allowedAgents: policy.allowedAgents, autoExport: policy.autoExport,
          allowedModes: capabilities.includes('chat-cowork') ? (['chat', 'cowork'] as Mode[]) : (['chat'] as Mode[]),
        }
      : undefined);

  const chatState = chat;
  async function onSend(input: SendForm, progress: (s: string) => void) {
    // F15.2: no new work in a trashed chat (the 30-day clean-up would take it along); the text stays in the box
    if (chatState.trashedUtc) throw new Error('This chat is in the trash: restore it first (chat panel → Restore from trash).');
    // 0.17.0: a draft becomes a real chat (Drive folder + chat.json) only with its first message.
    let chat = chatState;
    if (chat.draft) {
      progress('Creating chat…');
      const vw = props.viewer;
      let created = await createChat(project, vw ? `${vw.mode.viewer?.titlePrefix || '📄'} ${vw.doc.split('/').pop()}` : 'New chat', input.sel.mode);
      // the chat of a viewer document starts with its mode on (Shell passes the draft with the mode on)
      const draftModes: Record<string, unknown> = {};
      for (const m of modes) {
        // the switch of the draft; without one the project's default applies
        const own = (chatState as unknown as Record<string, unknown>)[m.key];
        const on = modeOn(chatState, project, m.key);
        if (on || own !== undefined) {
          draftModes[m.key] = on;
          const log = (chatState as unknown as Record<string, unknown>)[m.key + 'Log'];
          if (log) draftModes[m.key + 'Log'] = log;
        }
      }
      if (chatState.permissions || chatState.autoSwitch === false || Object.keys(draftModes).length || vw || chatState.browser || chatState.family) {
        created = await updateChatMeta(created, {
          ...(chatState.browser ? { browser: chatState.browser } : {}),
          ...(chatState.family ? { family: true } : {}),
          ...(vw ? { [vw.mode.key + 'Doc']: vw.doc, titleAuto: false } : {}),
          ...(chatState.permissions ? { permissions: chatState.permissions } : {}),
          ...(chatState.autoSwitch === false ? { autoSwitch: false } : {}),
          ...draftModes,
        });
      }
      chat = created;
    }
    const current = chatState.draft ? [] : eventsRef.current || [];
    // 0.34.0: "/btw <question>" asks a side question in a child chat (lineage snapshot of this chat at its last
    // message); this chat stays as it is and shows a link to the side chat.
    const btw = chatState.draft ? null : /^\/btw\s+([\s\S]+)$/i.exec(input.text.trim());
    if (btw) {
      if (input.files.length) throw new Error('Side questions (/btw) take text only.');
      progress('Opening a side question…');
      let side = await createChildChat(project, chatState, lastSequence(current), 'side', input.sel.mode);
      const keep = inheritedModes(chatState, project, modes); // a side question keeps the modes that inherit
      if (chatState.permissions || Object.keys(keep).length) {
        side = await updateChatMeta(side, {
          ...(chatState.permissions ? { permissions: chatState.permissions } : {}),
          ...keep,
        });
      }
      try {
        await sendToChat(side, [], { ...input, text: btw[1].trim() }, progress);
      } catch (e) {
        // the side chat exists: link it so the question can be resent there instead of creating another one
        setSideNote({ chatId: side.chatId, title: btw[1].trim().slice(0, 80) + ' (not sent: open it and resend)' });
        props.onChatUpdated();
        throw e;
      }
      setSideNote({ chatId: side.chatId, title: btw[1].trim().slice(0, 80) });
      props.onChatUpdated();
      return;
    }
    // 0.60.0: while a job of this chat runs, the message is its follow-up, never a parallel job (29.10: three parallel
    // jobs of one chat shared a browser session and one agent killed the other two)
    const running = chatState.draft ? undefined : [...chatPending].reverse().find((u) => !cancelled.has(u.jobId!));
    if (running && !input.forceNew) {
      if (input.files.length) throw new Error('The agent is still working: send files after its answer, or stop it first.');
      const rHost = hostOf(running).hostId;
      const rCaps = (rHost === host.hostId ? capabilities : props.owners[rHost]?.capabilities) || [];
      const deliver = rHost !== 'site' && (running.agent === 'claude' || running.agent === 'dispatcher') && rCaps.includes('followups');
      progress(deliver ? 'Passing to the running agent…' : 'Queued until the running answer…');
      const r = await dbFollowupAdd(running.jobId!, input.text, deliver);
      if (r === 'no_job') {
        // not in the database yet: the device outbox sends it after the job (it waits while a job of the chat runs)
        outboxAdd({ chatId: chatState.chatId, text: input.text, sel: input.sel, returnFiles: input.returnFiles, forceNew: false });
        return;
      }
      if (r !== 'job_done') {
        if (deliver) void publishJobReadyDb(running.jobId!);
        await loadFollowups();
        return;
      }
    }
    const ev = await sendToChat(chat, current, input, progress);
    if (chatState.draft && pendingTool.current !== null) {
      // 0.75.0: the chat with the tool library task exists - the proposal becomes accepted (stays pending until then)
      const toolId = pendingTool.current;
      pendingTool.current = null;
      void dbToolStarted(toolId, chat.chatId).catch(() => undefined);
    }
    if (chatState.draft) {
      // the chat of a viewer document keeps its "<prefix> <file>" title
      const titled = props.viewer ? chat : await updateChatMeta(chat, { title: heuristicTitle(input.text), titleAuto: true });
      props.onChatCreated?.(titled);
      return;
    }
    setEvents([...current, ev]);
    if (wantsTitle(chat, current)) {
      setChat(await updateChatMeta(chat, { title: heuristicTitle(input.text), titleAuto: true }));
    } else {
      await updateChatMeta(chat, {});
    }
    props.onChatUpdated();
  }

  // F15.2 part 4: protocols of the folders above (and the newest messages of the full ones) as one block
  async function folderContents(levels: FolderLevel[]): Promise<string> {
    const items: LevelContent[] = await Promise.all(levels.map(async (level) => {
      const [p, evs] = await Promise.all([
        dbProtocol(level.chat.chatId).catch(() => null),
        level.mode === 'full' ? loadChatEvents(level.chat).catch(() => [] as ChatEvent[]) : Promise.resolve([] as ChatEvent[]),
      ]);
      const messages = evs
        .filter((e) => (e.type === 'user_message' && !e.followupOf) || e.type === 'assistant_message')
        .slice(-12)
        .map((e) => {
          const who = e.type === 'user_message' ? 'User' : speaker(e);
          const t = (e.type === 'assistant_message' ? stripTitleMarker(e.text || '') || e.error || '' : e.text || '').trim();
          return `#${e.sequence} ${who}: ${t.length > 1500 ? t.slice(0, 1500) + ' …' : t}`;
        });
      return { level, protocol: p?.entries ?? null, messages };
    }));
    return buildFolderBlock(items);
  }

  // One turn into `chat` (this chat, or a child such as a /btw side chat): context blocks, lineage snapshot, routing.
  async function sendToChat(chat: Chat, current: ChatEvent[], input: SendForm, progress: (s: string) => void): Promise<ChatEvent> {
    const instr = await readProjectInstructions(project);
    // 0.85.0: secretary hints (other chats and dossier rows matching the text) and the dossier for new sessions; both
    // are best-effort: a slow or failed call sends the message without them
    const cmodeEarly: ContextMode = chat.contextMode || 'full';
    const quick = <T,>(p: Promise<T>, fallback: T): Promise<T> =>
      Promise.race([p.catch(() => fallback), new Promise<T>((r) => setTimeout(() => r(fallback), 3000))]);
    const [envProfile, parentProto, related, dossier] = await Promise.all([
      envProfileBlock(props.env), // 0.45.0: the sender's own profile
      Promise.resolve(''),
      cmodeEarly === 'full' && wantsHints(input.text) && !chat.draft
        ? quick<Related | null>(dbRelated(props.env, project.projectId, chat.chatId, input.text), null)
        : Promise.resolve(null),
      cmodeEarly === 'full' && !chat.draft ? quick(dbDossierContext(project.projectId, chat.chatId, input.text), '') : Promise.resolve(''),
    ]);
    // The child's lineage snapshot (read again at send time; a failed read stops the send).
    const snap = chat.parent ? await readSnapshot(chat.parent) : null;
    const inheritedNow = snap ? snap.events : [];
    // 0.36.0: context levels and the live protocol. Child chats (inherited messages carry the parent's numbers) keep
    // the transcript; a failed protocol read also falls back to it.
    const cmode: ContextMode = chat.contextMode || 'full';
    const proto = chat.draft || chat.parent ? null : await dbProtocol(chat.chatId);
    // F15.2 part 4: the folders above the chat (full / summary / off) and the project folder's switch, only in full mode
    const pctx = cmode === 'full' ? projectCtxOf(project) : 'full';
    const above = cmode === 'full' && !chat.family && !chat.draft ? folderLevels(chat, props.projectChats || []) : [];
    const fKey = cmode === 'full' ? folderKey(above, pctx) : '';
    const toSend = levelsToSend(above);
    const folderBlock = toSend.length
      ? await Promise.race([folderContents(toSend).catch(() => ''), new Promise<string>((r) => setTimeout(() => r(''), 6000))])
      : '';
    // 0.29.0: device tools and the gateway agents only at home; failover when the selected executor is offline.
    // 0.45.0: no devices in shared chats. 0.66.0: gateway module disabled (paused) - a message still addressed to one of its
    // agents (offline outbox, old template) goes to the first agent the project allows instead of failing on every resend.
    if (RETIRED_AGENTS.has(input.sel.agent)) {
      const to = allowedAgents[0];
      if (!to) throw new Error(`This project allows only ${gateway().title} agents, which are paused. Allow Claude or GPT for it in the Bridge config.`);
      progress(`${agentName(input.sel.agent)} is paused; sent to ${agentName(to)}`);
      input = { ...input, sel: { ...input.sel, agent: to, model: null, effort: null } };
    }
    const wantsDevices = !!(!shared && device?.paired && useDevice);
    const pinned = input.sel.host && props.hosts[input.sel.host] ? input.sel.host : null;
    const route = routeHost({ selected: pinned || host.hostId, agent: input.sel.agent, wantsDevices, owners: props.owners, webDevices: !shared && webPick.selected.length > 0,
      order: autoOrder().filter((id) => props.hosts[id]), holdOwn: !!props.holdOwn && !pinned });
    const target = props.hosts[route.hostId] || host;
    const tOwner = target.hostId === host.hostId ? props.owner : props.owners[target.hostId] ?? props.owner;
    const tCaps = target.hostId === host.hostId ? capabilities : tOwner?.capabilities || capabilities;
    setRouteNote(route.note);
    if (route.note) progress(route.note);
    // 0.73.0: memory on demand - an index of memory.md for Claude/GPT on a home host with shell access (aiwb-memory)
    const memIndexOk = !shared && target.hostId !== 'site' && (input.sel.agent === 'claude' || input.sel.agent === 'codex')
      && chatPermissions(chat.permissions).shell !== false;
    const memory = pctx === 'off' ? ''
      : pctx === 'summary' ? await readProjectContextFile(project, 'memory.md').then((r) => memoryDigest(r.text), () => '')
      : await projectMemoryBlock(project, chat.chatId, memIndexOk).catch(() => '');
    const instrText = pctx === 'off' ? '' : instr.text;
    // 0.76.0: work plans of long jobs (workPlan.ts) where the agent can write the chat scratch on Drive: the rule in
    // level 3 and, after a broken-off job, its unfinished plan as a hidden preamble of this message
    const planOk = target.hostId !== 'site' && !project.legacy;
    const pPath = planPath(project.projectId, chat.chatId);
    const levels = buildLevels({ mode: cmode, envProfile, projectInstructions: instrText, projectMemory: memory, workPlan: planOk ? planHeuristic(pPath) : '', modes: modesText(chat, project, modes), moduleContext: contextOf(mods), dossier });
    // 0.89.0: family chats - who called and why goes as a hidden preamble (FamilyChat.tsx familyBlock)
    const planResume = planOk ? await readResume(chat, current, pPath) : '';
    // an @co turn started later for a message posted while the agent was busy (coFor) or any other path keeps "called"
    const famCalled = input.family?.called ?? wantsCo(input.text);
    const famFollow = !famCalled && !!input.family?.following;
    const resume = (chat.family ? familyBlock(famCalled, props.author,
      familyTranscript([...current, { type: 'user_message', family: true, author: props.author, text: input.text, jobId: 'now', attachments: [] } as unknown as ChatEvent]),
      `AI-Workbench/projects/${project.projectId}/chats/${chat.chatId}/branches/${DEFAULT_BRANCH}/attachments/`, famFollow) : '') + planResume;
    if (planResume) progress('Resuming the unfinished work plan…');
    // 0.131.0: what modules keep in the folder of this chat (mail threads put into it, …) - a hint for the agent; not in
    // shared or family chats (the module's content is the sender's own)
    const partsNote = !shared && !chat.family && !chat.draft
      ? modulePartsNote({ projectId: chat.projectId, chatId: chat.chatId }, new Map(mods.map((m) => [m.id, m.title])))
      : '';
    // S8: on the Site the named device (and the PC library) comes through the relay; the site agent keeps only these
    const desktopIds = (tCaps.includes('device-tools') || target.hostId === SITE) && wantsDevices && device ? [device.deviceId] : [];
    // 0.49.0: web devices (w- ids) of the sender; not in shared chats (0.45.0 rule); max 4 devices per run
    const webOk = tCaps.includes('device-tools') || tCaps.includes('web-devices');
    const webIds = !shared && webPick.selected.length ? (webOk ? webPick.selected : []) : [];
    if (!shared && webPick.selected.length && !webOk) progress(`${target.hostId} does not support browser devices; sent without them`);
    const allIds = [...new Set([...desktopIds, ...webIds])].slice(0, 4); // 0.63.1: Bridge rejects a repeated device id
    const devices = allIds.length ? allIds : undefined;
    // 0.66.0: a paused (disabled) gateway has no button: Bridge 1.3.5+ hides its tools (module manifest) from the agent.
    const gwDeny = gateway().paused ? gatewayTools() : [];
    const deviceToolsDeny = desktopIds.length && gwDeny.length && tCaps.includes('device-tools-deny') ? gwDeny : undefined;
    // 0.55.0: uploads become chat files (a new version of a matched file) when the executor supports it and the job works
    // in the chat scratch (chat mode, the site, or a project without a configured workspace).
    const chatFilesOk = tCaps.includes('chat-files') && (input.sel.mode === 'chat' || target.hostId === 'site' || !tOwner?.projects?.[project.projectId]);
    const streams = buildStreams(current, null, project.projectId, chat.chatId);
    const usedTargets = new Set<string>();
    const fileTargets = input.files.map((f, i) => {
      if (!chatFilesOk) return null;
      const name = safeFileName(f.name);
      const match = input.fileModes?.[i] === 'new' ? null : matchUpload(name, streams);
      let t = uploadTarget(name, match, streams, project.projectId, chat.chatId);
      if (!match) for (let n = 2; usedTargets.has(t.toLowerCase()); n++) t = uploadTarget(name.replace(/(\.[^.]+)?$/, ` (${n})$1`), null, streams, project.projectId, chat.chatId);
      usedTargets.add(t.toLowerCase());
      return t;
    });
    const ev = await sendTurn({
      root, host: target, env: project.environmentId, project, chat, events: [...(cmode === 'full' ? inheritedNow : []), ...current.filter((e) => !isSide(e))], agent: input.sel.agent, text: input.text,
      files: input.files, fileTargets, returnFiles: input.returnFiles, projectInstructions: [envProfile, instrText, memory, parentProto, snap?.protocol || '', folderBlock].filter((x) => x.trim()).join('\n\n'), author: props.author,
      levels,
      live: { protocol: proto, contextMode: cmode, title: chat.title, chatId: chat.chatId, parentProtocol: cmode === 'full' ? snap?.protocol || '' : '', folderBlock, folderKey: fKey },
      forceNewSession: input.forceNew || !capabilities.includes('conversation-resume'), onProgress: progress,
      mode: input.sel.mode, model: input.sel.model, effort: input.sel.effort, protocolMinor: tOwner?.protocolMinor || 0,
      autoExport: true,
      contextFolders: !shared && tCaps.includes('context-folders') ? project.contextFolders : undefined, // 0.45.0: none when shared
      devices,
      deviceToolsDeny,
      permissions: tCaps.includes('permissions') ? chatPermissions(chat.permissions) : undefined,
      dbQueue: tCaps.includes('db-queue') && dbQueueOn(),
      hostAuto: !!props.hostAuto && !pinned, // S8 (a pinned model resource keeps its host): the server may move it to the other executor when not taken in ~60 s
      resume: resume + partsNote,
      hints: hintsBlock(related, target.hostId !== 'site'),
      hintsResume: hintsBlock(strongHits(related), target.hostId !== 'site'),
      hintsStored: storedHints(related),
      hintsStoredResume: storedHints(strongHits(related)),
      extra: chat.family ? {
        family: true, ...(input.family?.replyTo ? { replyTo: input.family.replyTo } : {}), ...(input.family?.coFor ? { coFor: input.family.coFor } : {}),
      } : rewriteOf.current ? { rewriteOf: rewriteOf.current } : undefined,
    });
    rewriteOf.current = null;
    setRewriting(null);
    if (target.hostId === 'site') void pingSiteAgent();
    return ev;
  }

  // 0.22.0: offline outbox. Without network a text message of an existing chat waits on this device and is sent, one
  // at a time and only while no job of the chat runs, when the connection is back and the chat is open.
  const [online, setOnline] = useState(() => navigator.onLine);
  useEffect(() => watchOnline(setOnline), []);
  const [outbox, setOutbox] = useState<OutboxItem[]>(() => outboxList(chat.chatId));
  useEffect(() => {
    setOutbox(outboxList(chat.chatId));
    return outboxSubscribe(() => setOutbox(outboxList(chat.chatId)));
  }, [chat.chatId]);
  const [outboxErr, setOutboxErr] = useState<string | null>(null);
  const flushing = useRef(false);
  const lastFlushFail = useRef(0);

  // 0.89.0: a family message goes to the people only; @co also starts an agent turn (Claude, chat mode).
  // 0.90.0: only @co - the agent no longer answers on its own to messages that read like a problem report
  async function onFamilySend(text: string, files: File[], progress: (s: string) => void) {
    const called = wantsCo(text);
    const running = chatState.draft ? undefined : chatPending.find((u) => !cancelled.has(u.jobId!));
    // 0.109.0: after a call the agent follows the topic for a few messages (FamilyChat.tsx followsTopic); not while it works
    const following = !called && !running && !readOnly && !chatState.draft && followsTopic(eventsRef.current || []);
    const agentTurn = called || following;
    const replyTo = famReply?.eventId;
    // while a turn runs, a message is never a follow-up (it would vanish from the family view): it is posted to the
    // people at once and, when it calls @co, starts its own turn after the running one (coPending -> coFor effect)
    if (agentTurn && !running) {
      await onSend({ text, sel: { agent: 'claude', mode: 'chat', model: null, effort: null }, files, returnFiles: [], forceNew: false, family: { called, replyTo, following } }, progress);
      setFamReply(null);
      return;
    }
    let target = chatState;
    if (target.draft) {
      progress('Creating chat…');
      target = await updateChatMeta(await createChat(project, heuristicTitle(text || '📷'), 'chat'), { family: true, titleAuto: false });
    }
    // the sequence is taken from a fresh read after the upload (several people write at once)
    const fresh = async () => (target.draft || chatState.draft ? [] : loadChatEvents(target).catch(() => eventsRef.current || []));
    const ev = await appendFamilyMessage(target, fresh, {
      text, files, author: props.author, replyTo, onProgress: progress, ...(agentTurn && running ? { coPending: true } : {}),
    });
    setFamReply(null);
    if (chatState.draft) {
      props.onChatCreated?.(target);
      return;
    }
    if (agentTurn && running) progress(`${assistantName()} is busy: it answers right after the running turn`);
    setEvents((prev) => (prev && !prev.some((x) => x.eventId === ev.eventId) ? [...prev, ev] : prev));
    await updateChatMeta(target, {});
    props.onChatUpdated();
  }

  // 0.89.0: my @co messages posted while the agent was busy start their turn once nothing runs (one at a time)
  const coStarting = useRef(new Set<string>());
  useEffect(() => {
    if (!family || !authOk || readOnly || !events || chatPending.length) return;
    const done = new Set(events.filter((e) => e.coFor).map((e) => e.coFor!));
    const next = events.find((e) => e.coPending && e.author === props.author && !done.has(e.eventId) && !coStarting.current.has(e.eventId));
    if (!next) return;
    coStarting.current.add(next.eventId);
    onSend({
      text: next.text || '', sel: { agent: 'claude', mode: 'chat', model: null, effort: null }, files: [], returnFiles: [], forceNew: false,
      family: { called: wantsCo(next.text || ''), replyTo: next.replyTo, coFor: next.eventId },
    }, () => undefined).catch((e: unknown) => {
      coStarting.current.delete(next.eventId);
      setFamErr(categorize(e).message);
    });
  }, [family, authOk, readOnly, events, chatPending.length]); // eslint-disable-line react-hooks/exhaustive-deps

  const [famErr, setFamErr] = useState<string | null>(null);
  async function react(target: ChatEvent, emoji: string, remove: boolean) {
    const current = (await loadChatEvents(chat).catch(() => null)) || eventsRef.current || [];
    const ev = await appendReaction(chat, current, target.eventId, emoji, props.author, remove);
    setEvents((prev) => (prev ? [...prev.filter((x) => x.eventId !== ev.eventId), ev] : prev));
  }

  async function sendOrQueue(input: SendForm, progress: (s: string) => void) {
    // 0.106.0: messages on screen are a copy (database down): queue like offline, sent once the database answers
    if (navigator.onLine && !staleUtc) return onSend(input, progress);
    const why = navigator.onLine ? 'No connection to the CO database' : 'No network';
    if (chatState.draft) throw new Error(`${why}: a new chat needs a connection. The text stays here.`);
    if (input.files.length) throw new Error(`${why}: files are sent only online. The text stays here.`);
    outboxAdd({ chatId: chatState.chatId, text: input.text, sel: input.sel, returnFiles: input.returnFiles, forceNew: input.forceNew });
  }

  useEffect(() => {
    if (!online || !authOk || events === null || staleUtc || chatPending.length > 0 || !outbox.length || flushing.current || chat.draft || chat.trashedUtc) return;
    if (Date.now() - lastFlushFail.current < 20000) return; // after a failure: next try with the chat reload (20 s)
    const item = outbox[0];
    if (!outboxTake(item.id)) return;
    flushing.current = true;
    setOutboxErr(null);
    onSend({ text: item.text, sel: item.sel, files: [], returnFiles: item.returnFiles, forceNew: item.forceNew }, () => undefined)
      .catch((e) => {
        lastFlushFail.current = Date.now();
        outboxPut(item);
        setOutboxErr(categorize(e).message);
      })
      .finally(() => {
        flushing.current = false;
      });
  }, [online, authOk, events, staleUtc, chatPending.length, outbox]); // eslint-disable-line react-hooks/exhaustive-deps

  // 0.60.0: follow-ups of running jobs (server rows). Delivered ones become chat messages written by the server; late
  // ones (Codex, the site, or the turn already ended) are taken once and sent as one next message after the job.
  const [followups, setFollowups] = useState<Followup[]>([]);
  const fuIds = useRef('');
  const loadFollowups = useCallback(async () => {
    if (!authOk || chat.draft) return;
    const rows = await dbFollowupList(chat.chatId);
    if (!rows) return;
    const ids = rows.map((f) => f.id).join(',');
    const gone = fuIds.current !== '' && fuIds.current.split(',').some((id) => !rows.some((f) => String(f.id) === id));
    fuIds.current = ids;
    setFollowups(rows);
    if (gone) void reload(); // a delivered follow-up is now a message of the chat
  }, [authOk, chat.chatId, chat.draft, reload]);
  useEffect(() => {
    setFollowups([]);
    fuIds.current = '';
    void loadFollowups();
  }, [loadFollowups]);
  useInterval(loadFollowups, POLL_RUNNING_EVENTS_MS * 2, authOk && (pending.length > 0 || followups.length > 0));
  const fuFlushing = useRef(false);
  useEffect(() => {
    if (!authOk || readOnly || events === null || chatPending.length > 0 || fuFlushing.current) return;
    const late = followups.filter((f) => f.jobOver);
    if (!late.length) return;
    fuFlushing.current = true;
    void (async () => {
      try {
        // taken rows are "sending"; until followup_done the server gives them out again after 2 min (nothing is lost)
        const taken = await dbFollowupTake(chat.chatId, late.map((f) => f.id));
        const text = late.filter((f) => taken.includes(f.id)).map((f) => f.text).join('\n\n');
        if (!taken.length) return; // nothing eligible yet (an offered row waits 30 s): the next poll tries again
        setFollowups((prev) => prev.filter((f) => !taken.includes(f.id)));
        if (text) {
          await onSend({ text, sel: lastSelection, files: [], returnFiles: [], forceNew: false }, () => undefined);
          await dbFollowupDone(chat.chatId, taken);
        }
      } catch (e) {
        setOutboxErr(categorize(e).message);
      } finally {
        fuFlushing.current = false;
      }
    })();
  }, [authOk, events, chatPending.length, followups]); // eslint-disable-line react-hooks/exhaustive-deps

  async function cancelFollowup(id: number) {
    if (await dbFollowupCancel(chat.chatId, id)) setFollowups((prev) => prev.filter((f) => f.id !== id));
    else void loadFollowups();
  }

  // The agent named the chat in its first answer: replace the interim title once.
  const proposedTitle = useMemo(() => {
    if (!chat.titleAuto) return null;
    const first = (events || []).find((e) => e.type === 'assistant_message');
    return first ? first.autoTitle || extractTitle(first.text || '').title : null;
  }, [events, chat.titleAuto]);
  useEffect(() => {
    if (!proposedTitle || !chat.titleAuto) return;
    updateChatMeta(chat, { title: proposedTitle, titleAuto: false })
      .then((c) => {
        setChat(c);
        props.onChatUpdated();
      })
      .catch((e) => console.warn('auto title', e));
  }, [proposedTitle]); // eslint-disable-line react-hooks/exhaustive-deps

  async function onStop(jobId: string) {
    const current = eventsRef.current || [];
    const u = current.find((e) => e.jobId === jobId && e.type === 'user_message');
    await requestCancel(u ? hostOf(u) : host, jobId);
    if (!cancelled.has(jobId)) {
      const ev = await appendEvent(chat, current, { type: 'cancel_requested', jobId, branchId: u?.branchId });
      setEvents([...current, ev]);
    }
    setMonitors((prev) => (prev[jobId] ? { ...prev, [jobId]: { ...prev[jobId], phase: 'cancel_requested' } } : prev));
  }

  // Composer starts from the last selection used in this chat; a new chat starts from defaultSelection,
  // fixed once per chat so that a later limits refresh does not switch the agent under the user.
  // 0.66.0: agents of a paused gateway are dropped even where the environment still allows them
  const allowedAgents = ((projectReg?.allowedAgents as AgentId[] | undefined) || AGENTS.map((a) => a.id)).filter((a) => !RETIRED_AGENTS.has(a));
  const hasOwner = !!props.owner;
  const hasLimits = !!props.limits;
  const def = useMemo(
    () => (hasOwner
      ? defaultSelection(allowedAgents, props.limits, project.defaultAgentId || 'claude')
      : { agent: project.defaultAgentId || 'claude', model: null }),
    [hasOwner, hasLimits, props.env], // eslint-disable-line react-hooks/exhaustive-deps
  );
  const lastSelection = useMemo<Selection>(() => {
    const u = [...(events || [])].reverse().find((e) => e.type === 'user_message' && !isSide(e));
    return {
      agent: (u?.agent as AgentId) || def.agent,
      mode: u?.mode || chat.defaultMode || 'cowork',
      model: u ? u.model ?? null : def.model,
      effort: u?.effort ?? null,
    };
  }, [events, def.agent, def.model, chat.defaultMode, isSide]);

  const visible = (events || []).filter((e) => e.type === 'user_message' || e.type === 'assistant_message');
  // 0.57.0: quick replies only under the newest message when it is an answer
  const own = visible.filter((e) => !isSide(e));
  const lastVisible = own.length ? own[own.length - 1] : null;
  const quickFor = lastVisible?.type === 'assistant_message' && !readOnly ? lastVisible.eventId : null;
  // 0.71.0: a reply chosen in "Waiting for you" arrives as navigation state once the messages are loaded; it is sent
  // only while the answer it replies to is still the newest, otherwise it waits in the input box
  const inboxReply = (location.state as { inboxReply?: { text: string; eventId: string } } | null)?.inboxReply;
  const handledInbox = useRef<string | null>(null);
  useEffect(() => {
    if (props.docked || !inboxReply || events === null || handledInbox.current === inboxReply.eventId + inboxReply.text) return;
    handledInbox.current = inboxReply.eventId + inboxReply.text;
    setPrefill({ text: inboxReply.text, send: quickFor === inboxReply.eventId, nonce: Date.now() });
    navigate(location.pathname + location.search, { replace: true, state: null });
  }, [inboxReply, events, quickFor, navigate, location.pathname, location.search, props.docked]);
  // 0.74.0: which folded part of the chat head is open on a phone (CSS ignores it on wider screens)
  const [headOpen, setHeadOpen] = useState<'info' | null>(null);
  const panel = useChatPanel(); // 0.86.0: slots of the side panel (chat Properties) for the chat controls
  const headRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!headOpen) return;
    const close = (ev: PointerEvent) => { if (!headRef.current?.contains(ev.target as Node)) setHeadOpen(null); };
    document.addEventListener('pointerdown', close);
    return () => document.removeEventListener('pointerdown', close);
  }, [headOpen]);
  useEffect(() => setHeadOpen(null), [chat.chatId]);
  // 0.75.0 (schema 16): an accepted tool library proposal ("Waiting for you" → Accept & run) arrives as navigation state on a
  // new chat and is sent once through the normal path; when the composer is busy it stays in the input box
  const toolTask = (location.state as { toolTask?: { text: string; id: number } } | null)?.toolTask;
  const handledTool = useRef<number | null>(null);
  useEffect(() => {
    if (props.docked || !toolTask || !chat.draft || events === null || handledTool.current === toolTask.id) return;
    handledTool.current = toolTask.id;
    pendingTool.current = toolTask.id;
    setPrefill({ text: toolTask.text, send: true, nonce: Date.now() });
    navigate(location.pathname + location.search, { replace: true, state: null });
  }, [toolTask, chat.draft, events, navigate, location.pathname, location.search, props.docked]);
  const parentTitle = chat.parent?.title || chat.parent?.chatId || '';
  const byJob = new Map((events || []).filter((e) => e.type === 'assistant_message').map((e) => [e.jobId, e]));
  // 0.71.0: failed jobs the server already sent to the other agent
  const switchedTo = new Map((events || []).filter((e) => e.type === 'user_message' && e.autoSwitchFrom).map((e) => [e.autoSwitchFrom!, e.agent || '']));
  // 0.60.0: follow-up messages and rows are shown inside the turn of their job
  const turnJobs = new Set(visible.filter((e) => e.type === 'user_message' && e.jobId).map((e) => e.jobId!));
  const fuEvents = (jobId?: string) => (jobId ? visible.filter((f) => f.followupOf === jobId) : []);
  const fuRows = (jobId?: string) => (jobId ? followups.filter((f) => f.jobId === jobId) : []);
  const orphanRows = followups.filter((f) => !turnJobs.has(f.jobId));
  // mode switches as lines before the next message (the rest after the last one)
  const allLines = modeLines(chat, modes);
  const linesBefore = new Map<string, ModeLine[]>();
  let linesAfter: string | null = null;
  for (const e of visible) {
    if (e.type !== 'user_message' || e.followupOf) continue;
    linesBefore.set(e.eventId, linesBetween(allLines, linesAfter, e.createdUtc));
    linesAfter = e.createdUtc;
  }
  const linesTail = linesBetween(allLines, linesAfter, null);
  const modeLine = (s: ModeLine) => (
    <div key={`mode-${s.mode.key}-${s.utc}`} className="mode-line small muted">
      {s.mode.icon} {s.mode.label} {s.on ? 'on' : 'off'} — {switchWho(s.by, switchNames())}, {formatLocal(s.utc)}
    </div>
  );

  // 0.86.0: one status line under the title: device, folders, permissions, modes, context mode
  const headDevice = !shared && device && (capabilities.includes('device-tools') || (device.remote && props.owners[HOME]?.capabilities?.includes('device-tools')))
    ? { name: device.name, paired: device.paired, remote: device.remote } : null;
  const headStatus: { text: string; warn?: boolean }[] = [];
  if (family) headStatus.push({ text: '👪 Family' });
  else if (shared) headStatus.push({ text: '👥 Shared' });
  else if (headDevice) {
    if (!headDevice.paired) headStatus.push({ text: `💻 ${headDevice.name}: not connected`, warn: true });
    else if (useDevice) headStatus.push({ text: `💻 ${headDevice.name}` });
  }
  const nFolders = shared ? 0 : (project.contextFolders || []).length;
  if (nFolders) headStatus.push({ text: `📁 ${nFolders} folder${nFolders === 1 ? '' : 's'}` });
  if (capabilities.includes('permissions')) {
    const perm = chatPermissions(chat.permissions);
    headStatus.push({ text: perm.files === 'write' ? '✎ write' : '👁 read-only' });
    if (!perm.shell) headStatus.push({ text: 'no commands' });
    if (!perm.web) headStatus.push({ text: 'no web' });
  }
  for (const m of modes) if (modeOn(chat, project, m.key)) headStatus.push({ text: `${m.icon} ${m.short || m.label}`.trim() });
  if ((chat.contextMode || 'full') !== 'full') headStatus.push({ text: CONTEXT_MODE_LABEL[chat.contextMode || 'full'] });

  return (
    <div className={`chat${props.docked ? ' docked' : ''}`}>
      {/* 0.90.0: the family room in the right panel has no head (the tab names it) */}
      {!props.docked && <div ref={headRef} className={`chat-head${headOpen ? ' open-' + headOpen : ''}`}>
        <div className="head-main">
          <h2 className="chat-title">{chat.title}</h2>
          {/* 0.74.0: on a phone the info folds behind ⓘ; 0.128.0: settings and actions are in the chat's Properties */}
          <div className="head-info">
          <div className="muted small">
            {chat.draft ? <>{project.title} · new chat, saved with the first message</> : <>{project.title} · <code>{chat.chatId}</code></>}
          </div>
          {chat.parent && (
            <div className="lineage small">
              {chat.parent.kind === 'side' ? 'Side question from ' : chat.parent.kind === 'followup' ? 'Continues ' : chat.parent.kind === 'subtask' ? 'Subtask of ' : 'Branch of '}
              <a href={`/e/${props.env}/p/${project.projectId}/c/${chat.parent.chatId}`}>«{parentTitle}»</a> after message #{chat.parent.seq}
            </div>
          )}
          </div>
          <button type="button" className="head-status small" title="Properties of this chat: settings, actions, context and usage (Alt+Enter)"
            onClick={() => openPanel('chat', { projectId: project.projectId, chatId: chat.chatId })}>
            {headStatus.map((t, i) => <span key={i} className={t.warn ? 'warn' : ''}>{t.text}</span>)}
            <span className="head-status-gear">ⓘ Properties</span>
          </button>
          {branchErr && <div className="error-text small">{branchErr}</div>}
        </div>
        <div className="spacer" />
        <div className="head-toggles">
          <button type="button" className={`btn tiny ${headOpen === 'info' ? '' : 'ghost'}`} aria-expanded={headOpen === 'info'}
            title="Chat id and origin" onClick={() => setHeadOpen(headOpen === 'info' ? null : 'info')}>ⓘ</button>
        </div>
      </div>}
      {!props.docked && panel.chatSlot && createPortal(
        <div className="chat-panel">
          <h5>What agents may do</h5>
          <PermissionsBar
            part="chat"
            value={chat.permissions}
            supported={capabilities.includes('permissions')}
            onChange={(permissions) =>
              chat.draft ? setChat({ ...chat, permissions }) : void updateChatMeta(chat, { permissions }).then(setChat)}
            project={project}
            onProjectChanged={props.onProjectChanged}
            device={headDevice}
            shared={shared}
            useDevice={useDevice}
            onUseDevice={setUseDevice}
            autoSwitch={chat.autoSwitch}
            onAutoSwitch={readOnly ? undefined : (autoSwitch) =>
              chat.draft ? setChat({ ...chat, autoSwitch }) : void updateChatMeta(chat, { autoSwitch }).then(setChat)}
          />
          {!shared && webPick.devices.length > 0 && (
            <div className="perm-bar" aria-label="Browser devices for this chat">
              <WebDevicePicker devices={webPick.devices} selected={webPick.selected} onToggle={webPick.toggle} />
            </div>
          )}
          <h5>Mode</h5>
          {/* F10 (ext/1 chat.mode): modes of enabled modules - each a switch of this chat, logged as a line */}
          <div className="perm-bar">
            {modes.map((m) => {
              const on = modeOn(chat, project, m.key);
              return (
                <button key={m.key} type="button" className={`btn tiny ${on ? 'mode-on' : 'ghost'}`} disabled={readOnly}
                  aria-pressed={on}
                  title={(on ? m.hintOn : m.hintOff) || m.label}
                  onClick={() => {
                    const patch = modePatch(chat, project, m.key, props.author);
                    if (chat.draft) setChat({ ...chat, ...patch });
                    else void updateChatMeta(chat, patch).then(setChat).catch((e: unknown) => setBranchErr(categorize(e).message));
                  }}>
                  {`${m.icon} ${m.short || m.label}`.trim()}{on ? ' on' : ''}
                </button>
              );
            })}
            {/* the chat of a viewer document reopens it */}
            {modes.map((m) => {
              const doc = modeDoc(chat, m.key);
              const href = doc && m.viewer && !props.viewer ? viewerHref(m.viewer.param, doc) : null;
              return href && (
                <button key={'doc-' + m.key} type="button" className="btn tiny ghost" title={`Open ${doc} in the viewer (${m.label})`} onClick={() => navigate(href)}>
                  {m.viewer?.titlePrefix || '📄'} Document
                </button>
              );
            })}
            <select
              className="btn tiny ghost"
              value={chat.contextMode || 'full'}
              title={CONTEXT_MODE_HINT[chat.contextMode || 'full']}
              disabled={readOnly}
              onChange={(ev) => {
                const contextMode = ev.target.value as ContextMode;
                if (chat.draft) setChat({ ...chat, contextMode });
                else void updateChatMeta(chat, { contextMode }).then(setChat);
              }}
            >
              {(Object.keys(CONTEXT_MODE_LABEL) as ContextMode[]).map((m) => (
                <option key={m} value={m} title={CONTEXT_MODE_HINT[m]}>{CONTEXT_MODE_LABEL[m]}</option>
              ))}
            </select>
          </div>
          <h5>Agent browser</h5>
          <BrowserSettingsPanel env={props.env} value={chat.browser} readOnly={readOnly}
            onChange={(browser) => chat.draft ? setChat({ ...chat, browser }) : void updateChatMeta(chat, { browser }).then(setChat)} />
          {!chat.draft && (
            <>
              <h5>Chat</h5>
              <div className="chat-panel-actions">
                <button
                  className={`btn tiny ${protoOn ? '' : 'ghost'}`}
                  onClick={() => {
                    setProtocolPaneOn(!protoOn);
                    setProtoOn(!protoOn);
                  }}
                  title="Protocol kept by the secretary (the chat's model): decisions, constraints, facts, questions, artifacts"
                >
                  Protocol
                </button>
                <button
                  className="btn tiny ghost"
                  disabled={events === null || !!loadErr || lastSequence(events || []) === 0}
                  onClick={() => void branchAt(lastSequence(events || []), 'followup')}
                  title="Start a new chat that continues this one (history and protocol go with it); this chat stays as it is"
                >
                  ↪ New chat from here
                </button>
                <button className="btn tiny ghost" onClick={() => props.onChatAction('rename')}>Rename</button>
                <button className="btn tiny ghost" onClick={() => props.onChatAction('title')} title="Claude Haiku reads the chat and proposes a title">✨ Title</button>
                <button className="btn tiny ghost" onClick={() => props.onChatAction('move')}>Move…</button>
              </div>
              <div className="chat-panel-danger">
                {/* F15.2: Delete moves the chat to the trash; a trashed chat offers Restore and Delete forever */}
                {chat.trashedUtc ? (
                  <>
                    <button className="btn tiny ghost" onClick={() => props.onChatAction('restore')}>Restore from trash</button>
                    <button className="btn tiny ghost danger" onClick={() => props.onChatAction('purge')}>Delete forever</button>
                  </>
                ) : (
                  <button className="btn tiny ghost danger" onClick={() => props.onChatAction('delete')} title="Moves the chat with its subchats to the trash (restorable for 30 days)">Delete chat</button>
                )}
              </div>
            </>
          )}
        </div>,
        panel.chatSlot,
      )}
      {/* F15.2 part 4: the context switch of this chat as a folder ("Context and usage" of its Properties) */}
      {!props.docked && !chat.draft && panel.ctxSlot && createPortal(
        <FolderCtxSection chat={chat} chats={props.projectChats ?? null} project={project} readOnly={readOnly}
          onSet={async (folderContext) => {
            setChat(await updateChatMeta(chat, { folderContext }));
            props.onChatUpdated(); // the shell's list: the chats inside read their folders from it
          }} />,
        panel.ctxSlot,
      )}
      {/* 0.129.0: the Context part of the chat folder - what this chat's agent gets, with the chat's context mode */}
      {!props.docked && !chat.draft && panel.ctxListSlot && createPortal(
        <ChatContext chat={chat} project={project} chats={props.projectChats ?? null} shared={shared} readOnly={readOnly}
          onMode={async (contextMode) => {
            setChat(await updateChatMeta(chat, { contextMode }));
            props.onChatUpdated();
          }} />,
        panel.ctxListSlot,
      )}
      {!props.docked && panel.filesSlot && createPortal(
        <PermissionsBar
          part="folders"
          value={chat.permissions}
          supported={capabilities.includes('permissions')}
          onChange={() => undefined}
          project={project}
          onProjectChanged={props.onProjectChanged}
          device={null}
          shared={shared}
          useDevice={useDevice}
          onUseDevice={setUseDevice}
        />,
        panel.filesSlot,
      )}

      <Lightbox url={lightbox} onClose={() => setLightbox(null)} />
      <div ref={msgBox} className={`messages${protoOn ? ' mirror' : ''}${family ? ' family' : ''}`} aria-live="polite">
        {protoOn && !chat.draft && <ProtocolHeader data={proto} />}
        {!props.docked && <QuoteAsk box={msgBox} canAsk={authOk && events !== null && !readOnly} onAsk={(text) => setPrefill({ text, send: true, nonce: Date.now() })} />}
        {loadErr && staleUtc && (
          <div className="warn-box">
            {loadErr} Shown: the messages as of{' '}
            {new Date(staleUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
            {new Date(staleUtc).toDateString() !== new Date().toDateString() && ', ' + new Date(staleUtc).toLocaleDateString()}
            ; a message you send now goes out once the database answers.
          </div>
        )}
        {loadErr && !staleUtc && <div className="error-box">{loadErr}</div>}
        {msgErr && <div className="error-box">{msgErr}</div>}
        {rewriting !== null && (
          <div className="msg-traces small">
            ✎ The next message rewrites #{rewriting}.{' '}
            <button className="link" onClick={() => { rewriteOf.current = null; setRewriting(null); }}>Not a rewrite</button>
          </div>
        )}
        {msgDone && <MsgTracesPanel done={msgDone} env={project.environmentId} hidden={hiddenOf(chat.chatId)} onUndo={setMsgUndo} onClose={() => setMsgDone(null)} />}
        {msgUndo && <UndoPanel rootId={root.id} ev={msgUndo} events={[...(events || []), ...hiddenOf(chat.chatId)].sort((a, b) => a.sequence - b.sequence)}
          projectId={project.projectId} chatId={chat.chatId} onClose={() => setMsgUndo(null)} />}
        {!readOnly && batches.length > 0 && (
          <details className="msg-restore small">
            <summary className="muted">🗑 {batches.length} deleted in this chat (restorable for 30 days)</summary>
            <ul>
              {batches.map((b) => (
                <li key={b.batch}>
                  {b.mode === 'rewrite' ? `Forgotten from #${b.at} (${b.messages} messages)` : b.mode === 'pair' ? `#${b.at} with its answer` : `Answer #${b.at}`}
                  <span className="muted"> · {formatLocal(b.utc)}</span>{' '}
                  {(chatOwner || b.by === props.author.toLowerCase()) && <button className="link" onClick={() => void restoreBatch(b)}>Restore</button>}
                </li>
              ))}
            </ul>
          </details>
        )}
        {events === null && !loadErr && <div className="muted pad">Loading history from Drive…</div>}
        {inherited.length > 0 && (
          <div className="inherited">
            {inherited.map((e) => (
              <div key={'inh-' + e.eventId} className={`msg ${e.type === 'user_message' ? 'user' : 'assistant'} inherited-msg`}>
                <div className="msg-meta">
                  <span className="who">{e.type === 'user_message' ? 'You' : speaker(e)}</span>
                  <span className="tag">from «{parentTitle}» #{e.sequence}</span>
                </div>
                <div className="msg-body">
                  {e.type === 'user_message' ? <div className="pre">{e.text}</div> : <Markdown text={stripQuickReplies(stripTitleMarker(e.text || ''))} />}
                </div>
              </div>
            ))}
            <div className="branch-divider">⑂ branch starts here</div>
          </div>
        )}
        {events !== null && visible.length === 0 && (
          <div className="empty small-empty">
            <p className="muted">
              {chat.legacy ? 'This chat was created before CO and has no canonical history.'
                : family ? `👪 Family chat: write to each other, add photos and emoji. @co calls ${assistantName()}.`
                : 'No messages yet. Choose an agent and send the first message.'}
            </p>
          </div>
        )}
        {visible.map((e) => {
          const mirror = (node: ReactElement) =>
            protoOn ? (
              <div key={e.eventId} className="mirror-row">
                <div className="mirror-main">{node}</div>
                <div className="mirror-proto">
                  <ProtocolCell seq={e.sequence} data={proto} />
                </div>
              </div>
            ) : node;
          // 0.89.0: family chat - every message is a bubble; the agent's answer to @co too (silent ones are hidden)
          if (family) {
            if (e.type === 'assistant_message' && isSilent(e)) return null;
            const pend = e.type === 'user_message' && !!e.jobId && !byJob.has(e.jobId);
            if (e.type === 'user_message' && e.coFor) {
              return pend ? (
                <div key={e.eventId} className="fam-row">
                  <RunningTurn compact ev={e} monitor={monitors[e.jobId!]} canCancel={capabilities.includes('cancel')}
                    cancelRequested={cancelled.has(e.jobId!)} onStop={() => onStop(e.jobId!)} />
                </div>
              ) : null;
            }
            return mirror(
              <FamilyMessage key={e.eventId} ev={e} me={props.author} reactions={reactions.get(e.eventId)}
                replyEv={e.replyTo ? evById.get(e.replyTo) : undefined}
                onReact={readOnly ? undefined : (emoji, remove) => void react(e, emoji, remove).catch((x: unknown) => setFamErr(categorize(x).message))}
                onReply={readOnly ? undefined : () => setFamReply(e)}
                onOpenImage={setLightbox}
                footer={pend ? (
                  <RunningTurn compact ev={e} monitor={monitors[e.jobId!]} canCancel={capabilities.includes('cancel')}
                    cancelRequested={cancelled.has(e.jobId!)} onStop={() => onStop(e.jobId!)} />
                ) : undefined}
              />,
            );
          }
          if (e.type === 'user_message' && e.followupOf && turnJobs.has(e.followupOf)) return null;
          // a turn of a module's side job (question + answer) is one collapsed row
          const side = sideJobOf(sideJobs, e);
          if (side) {
            if (e.type !== 'user_message') return visible.some((u) => u.type === 'user_message' && u.jobId === e.jobId) ? null : mirror(<SideJobRow key={e.eventId} ev={e} side={side} me={props.author} />);
            return mirror(<SideJobRow key={e.eventId} ev={e} side={side} answer={byJob.get(e.jobId)} running={!byJob.has(e.jobId)} me={props.author} />);
          }
          if (e.type === 'user_message') {
            const done = byJob.get(e.jobId);
            const m = e.jobId ? monitors[e.jobId] : undefined;
            return mirror(
              <div key={e.eventId} className="turn" id={`ev-${e.eventId}`}>
                {(linesBefore.get(e.eventId) || []).map(modeLine)}
                <UserBubble root={root}
                  ev={e}
                  onEdit={() => setPrefill({ text: e.text || '', agent: e.agent, nonce: Date.now() })}
                  onBranch={() => void branchAt(previousMessageSeq(visible, e.sequence), 'branch', e.text || '')}
                  onDelete={!readOnly && !family && canHide(e, visible, props.author, chatOwner) ? () => void hideMsg(e, 'pair') : undefined}
                  onRewrite={!readOnly && !family && canRewrite(e, visible, props.author, chatOwner) ? () => void hideMsg(e, 'rewrite') : undefined}
                  owner={props.owner}
                  me={props.author}
                />
                {fuEvents(e.jobId).map((f) => (
                  <UserBubble key={f.eventId} root={root}
                    ev={f}
                    onEdit={() => setPrefill({ text: f.text || '', agent: f.agent, nonce: Date.now() })}
                    onBranch={() => void branchAt(previousMessageSeq(visible, f.sequence), 'branch', f.text || '')}
                    owner={props.owner}
                    me={props.author}
                  />
                ))}
                {fuRows(e.jobId).map((f) => <FollowupRow key={f.id} f={f} onCancel={readOnly ? undefined : () => void cancelFollowup(f.id)} />)}
                {!done && e.jobId && (
                  <RunningTurn
                    ev={e}
                    monitor={m}
                    canCancel={capabilities.includes('cancel')}
                    cancelRequested={cancelled.has(e.jobId)}
                    onStop={() => onStop(e.jobId!)}
                  />
                )}
              </div>
            );
          }
          return mirror(
            <AssistantBubble
              key={e.eventId}
              ev={e}
              owner={props.owner}
              root={root}
              host={hostOf(e)}
              onContinue={(agent) => setPrefill({ text: '', agent, nonce: Date.now() })}
              onCritique={(agent) =>
                setPrefill({
                  text: `Critically review the previous answer from ${agentName(e.agent)}. List concrete errors, gaps and risks, with fixes.`,
                  agent,
                  nonce: Date.now(),
                })
              }
              onBranch={() => void branchAt(e.sequence, 'branch')}
              onDelete={!readOnly && !family && canHide(e, visible, props.author, chatOwner) ? () => void hideMsg(e, 'answer') : undefined}
              switchedTo={e.jobId ? switchedTo.get(e.jobId) : undefined}
              undo={readOnly ? undefined : { events: events || [], projectId: project.projectId, chatId: chat.chatId }}
              cards={{ modules: cardModules, me: project.environmentId, chat: { projectId: project.projectId, chatId: chat.chatId }, live: cardsOn }}
              onQuick={quickFor === e.eventId ? (text) => setPrefill({ text, send: true, nonce: Date.now() }) : undefined}
              onRetry={(agent: AgentId, text?: string) => {
                const u = text === undefined
                  ? (events || []).find((x) => x.type === 'user_message' && x.jobId === e.jobId)
                  : undefined;
                setPrefill({ text: text ?? (u?.text || ''), agent, nonce: Date.now() });
              }}
              onRetryNew={() => {
                const u = (events || []).find((x) => x.type === 'user_message' && x.jobId === e.jobId);
                setPrefill({ text: u?.text || '', agent: e.agent, forceNew: true, nonce: Date.now() });
              }}
            />
          );
        })}
        {orphanRows.map((f) => <FollowupRow key={f.id} f={f} onCancel={readOnly ? undefined : () => void cancelFollowup(f.id)} />)}
        {linesTail.map(modeLine)}
        {outbox.map((o) => (
          <div key={o.id} className="turn">
            <div className="msg user">
              <div className="msg-meta">{online ? '🕓 Waits for the running answer, then goes as the next message' : '⏳ Waiting for network: sent automatically when the connection is back'}</div>
              <div className="msg-body pre">{o.text}</div>
              <div className="msg-actions">
                <button className="link" onClick={() => outboxRemove(o.id)}>Cancel</button>
              </div>
            </div>
          </div>
        ))}
        {outboxErr && outbox.length > 0 && <div className="error-box">Queued message not sent yet: {outboxErr}</div>}
        {family && famErr && <div className="error-box">{famErr} <button className="link" onClick={() => setFamErr(null)}>×</button></div>}
        <div ref={bottomRef} />
      </div>
      {!online && <div className="small muted">Offline: messages wait on this device and are sent when the connection is back.</div>}

      {routeNote && <div className="muted small route-note">{routeNote}</div>}
      {sideNote && (
        <div className="muted small route-note">
          Side question sent:{' '}
          <a href={`/e/${props.env}/p/${project.projectId}/c/${sideNote.chatId}`}>btw: {sideNote.title}</a>{' '}
          <button className="link" onClick={() => setSideNote(null)}>×</button>
        </div>
      )}
      {/* 0.45.0: read-only notice for shared projects */}
      {readOnly && (
        <div className="muted small route-note">
          {(project.shareVia === 'chat'
            ? !chat.draft
            : project.shareRole === 'viewer' && (!!chat.draft || chat.shareRole !== 'editor'))
            ? 'View only: shared with you as a viewer.'
            : 'Only the chats shared with you are open; new chats here are not allowed.'}
        </div>
      )}
      {!props.viewer && !readOnly && (() => {
        // 0.130.0: where the message goes; flagged when the Explorer shows another project's folder (C0J6 #14)
        const elsewhere = !!props.folderProjectId && props.folderProjectId !== project.projectId;
        return (
          <div className={`send-target${elsewhere ? ' elsewhere' : ''}`} title={elsewhere ? 'The Explorer shows another folder; the message goes to this chat' : 'The message goes to this chat'}>
            {elsewhere ? '⚠ ' : '→ '}{project.title || project.projectId} › {chat.draft ? 'new chat' : stripTitleMarker(chat.title || '') || chat.chatId}
          </div>
        );
      })()}
      {family ? (
        <FamilyComposer
          key={chat.folderId + ':family'}
          draftKey={chat.draft ? `new:${project.projectId}` : chat.chatId}
          disabled={!authOk || events === null || readOnly}
          reply={famReply}
          onCancelReply={() => setFamReply(null)}
          onSend={onFamilySend}
          initialText={prefill ? { text: prefill.text, files: prefill.files, nonce: prefill.nonce } : null}
        />
      ) : <Composer
        key={chat.folderId}
        draftKey={chat.draft ? `new:${project.projectId}` : chat.chatId}
        initial={lastSelection}
        owner={props.owner}
        owners={props.owners}
        hostId={host.hostId}
        env={props.env}
        allowedModes={projectReg?.allowedModes || ['cowork']}
        allowedAgents={allowedAgents}
        prefill={prefill}
        disabled={!authOk || events === null || readOnly}
        canAttach={capabilities.length === 0 || capabilities.includes('attachments')}
        matchFile={capabilities.includes('chat-files') ? (n) => matchUpload(safeFileName(n), composerStreams)?.name ?? null : undefined}
        hostHealth={props.hostHealth}
        events={events}
        onSend={sendOrQueue}
        onModeChange={(m) => {
          // 0.37.0: a draft has no chat.json yet; its mode goes with the first message (changing chat.defaultMode
          // here would reset the composer's agent and model through `initial`)
          if (chat.draft || chat.defaultMode === m) return;
          void updateChatMeta(chat, { defaultMode: m }).then(setChat).catch(() => undefined);
        }}
        templates={chat.draft ? {
          list: templates || [], onApply: applyTemplate, onSave: (s) => void saveTemplate(s), onDelete: (n) => void deleteTemplate(n), err: tplErr,
        } : undefined}
      />}
    </div>
  );
}

// ---------- Messages ----------

function copy(text: string) {
  void navigator.clipboard?.writeText(text);
}

function selectionTags(ev: ChatEvent, owner: OwnerInfo | null) {
  const model = ev.model ? modelLabel(owner, ev.agent, ev.model) : null;
  return (
    <>
      {ev.mode && <span className={`tag mode-${ev.mode}`}>{ev.mode === 'chat' ? 'Chat' : ev.agent === 'codex' ? 'Codex' : 'Cowork'}</span>}
      {model && <span className="tag">{model}</span>}
      {ev.effort && <span className="tag">{ev.effort}</span>}
    </>
  );
}

function modelLabel(owner: OwnerInfo | null, agent: string | undefined, id: string): string {
  const m = owner?.agents?.[agent || '']?.models?.find((x) => x.id === id);
  return m?.label || id;
}

// Codex counts cached tokens inside input_tokens; Claude reports cache reads and writes separately.
function formatUsage(u: Usage, agent?: string): string {
  const parts: string[] = [];
  const cached = u.cachedInputTokens || 0;
  const input = agent === 'codex' ? u.inputTokens || 0 : (u.inputTokens || 0) + cached + (u.cacheCreationInputTokens || 0);
  parts.push(`${input.toLocaleString()} in${cached ? ` (${cached.toLocaleString()} cached)` : ''} · ${(u.outputTokens || 0).toLocaleString()} out`);
  if (u.reasoningOutputTokens) parts.push(`${u.reasoningOutputTokens.toLocaleString()} reasoning`);
  if (u.costUsd != null) parts.push(`${u.costUsd.toFixed(3)} USD`);
  if (u.durationMs != null) parts.push(`${(u.durationMs / 1000).toFixed(1)} s`);
  return parts.join(' · ');
}

// F10 (ext/1 sideJobs): a job a module queued into the chat (e.g. comment threads of a document) and the agent's
// answer, collapsed to one row.
function SideJobRow({ ev, side, answer, running, me }: { ev: ChatEvent; side: { job: SideJob; path: string; file: string }; answer?: ChatEvent; running?: boolean; me: string }) {
  const q = ev.type === 'user_message' ? ev : null;
  const a = ev.type === 'assistant_message' ? ev : answer;
  const other = !!q?.author && q.author !== me;
  const who = other ? (ENVIRONMENTS.find((x) => x.id === ALLOWLIST[q!.author as string]?.env)?.displayName || (q!.author as string)) : 'You';
  const replies = a?.status === 'completed' ? sideReplies(side.job, a.text || '') : [];
  const state = running ? 'working…' : a?.status === 'completed' ? `${replies.length || 0} repl${replies.length === 1 ? 'y' : 'ies'}` : a?.status || '';
  return (
    <details className="msg side-row">
      <summary className="msg-meta">
        <span className="who">{side.job.label}: {q ? `${who} → ${agentName(ev.agent)}` : agentName(ev.agent)}</span>
        <span className="tag" title={side.path}>{side.file}</span>
        <span className="muted">{formatLocal(ev.createdUtc)}</span>
        {state && <span className={`tag${a?.status && a.status !== 'completed' ? ' danger' : ''}`}>{state}</span>}
      </summary>
      {q && <div className="msg-body pre small">{q.text}</div>}
      {a && (
        <div className="msg-body small">
          {a.status === 'completed'
            ? replies.map((r, i) => <div key={i} className="pre">↳ {r}</div>)
            : <div className="error-text">{a.error || a.status}</div>}
        </div>
      )}
    </details>
  );
}

function UserBubble({ ev, onEdit, onBranch, onDelete, onRewrite, owner, me, root }: {
  ev: ChatEvent; onEdit: () => void; onBranch: () => void; onDelete?: () => void; onRewrite?: () => void; owner: OwnerInfo | null; me: string; root: Root;
}) {
  // 0.45.0: the other participant's name in shared chats
  const other = !!ev.author && ev.author !== me;
  const authorEnv = other ? ALLOWLIST[ev.author as string]?.env : undefined;
  const who = other ? (ENVIRONMENTS.find((x) => x.id === authorEnv)?.displayName || (ev.author as string)) : 'You';
  return (
    <div className="msg user" data-seq={ev.sequence}>
      <div className="msg-meta">
        <span className="who">{ev.imported ? 'You' : `${who} → ${agentName(ev.agent)}`}</span>
        {ev.imported ? <span className="tag">imported · {importLabel(ev.imported.source)}</span> : selectionTags(ev, owner)}
        <span className="muted">{formatLocal(ev.createdUtc)}</span>
        {ev.conversation?.operation === 'resume' && <span className="tag">resume</span>}
        {ev.followupOf && <span className="tag" title="Sent while the agent was working; it read the message after its current step">follow-up</span>}
        {!!ev.rewriteOf && <span className="tag" title={`Rewritten: message #${ev.rewriteOf} and everything after it were forgotten`}>rewrites #{ev.rewriteOf}</span>}
        {ev.contextPack === 'protocol' && <span className="tag" title={ev.contextReason ? `new session: ${ev.contextReason}` : 'new session from the protocol'}>protocol</span>}
        {ev.contextMode && ev.contextMode !== 'full' && <span className="tag">{CONTEXT_MODE_LABEL[ev.contextMode]}</span>}
      </div>
      {/* F10 (ext/1 messageViews): a message a module page wrote shows as its label and text; Copy keeps the full text */}
      {(() => {
        // 0.82.0: a question about selected text of an answer shows as its label, the quote and the question
        const qd = quoteDisplay(ev.text);
        if (qd) return (
          <>
            <button type="button" className="tag msg-view-tag link" title={`Show answer #${qd.seq}`}
              onClick={() => document.querySelector(`.msg.assistant[data-seq="${qd.seq}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })}>
              {qd.label}{qd.wtf ? ' · WTF' : ''}
            </button>
            {qd.quote && <blockquote className="quote-frag"><Markdown text={qd.quote} /></blockquote>}
            <div className="msg-body pre">{qd.text}</div>
          </>
        );
        const sd = viewMessage(viewsOf(moduleList()), ev.text);
        return sd ? (
          <>
            <div className="tag msg-view-tag">{sd.label}</div>
            <div className="msg-body pre">{sd.text}</div>
          </>
        ) : <div className="msg-body pre">{ev.text}</div>;
      })()}
      {!!ev.attachments?.some(isAttachedImage) && <AttachmentImages list={ev.attachments.filter(isAttachedImage)} root={root} />}
      {!!ev.attachments?.some((a) => !isAttachedImage(a)) && (
        <div className="chips">
          {ev.attachments.filter((a) => !isAttachedImage(a)).map((a) => <AttachmentChip key={a.path} a={a} root={root} />)}
        </div>
      )}
      {!!ev.returnFiles?.length && <div className="muted small">Return files: {ev.returnFiles.join(', ')}</div>}
      {!!ev.hints?.length && (
        // 0.85.0: secretary hints the agent got with this message
        <details className="hints small">
          <summary className="muted" title="Entries of other chats that matched this message; the agent got them as hints">🔎 {ev.hints.length} related</summary>
          <ul>
            {ev.hints.map((h) => (
              <li key={`${h.chatId}-${h.msg}`}>
                <Link to={`/e/${h.env}/p/${encodeURIComponent(h.projectId)}/c/${encodeURIComponent(h.chatId)}`}>{h.projectId} · {h.chatId.slice(-4)}</Link>
                <span className="muted"> #{h.msg}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
      <div className="msg-actions">
        <button className="link" onClick={() => copy(ev.text || '')}>Copy</button>
        <button className="link" onClick={onEdit}>Edit and resubmit</button>
        <button className="link" onClick={onBranch} title="New chat that branches before this message, with this text ready to edit">⑂ Branch from here</button>
        {(onDelete || onRewrite) && (
          <details className="msg-more">
            <summary className="link" title="More">⋯</summary>
            <div className="msg-more-menu">
              {onRewrite && <button className="link" onClick={onRewrite} title="Edit this message in place: it and everything after it are forgotten, the agent starts a new session">✎ Rewrite from here</button>}
              {onDelete && <button className="link" onClick={onDelete} title="Hide this message and its answer (restorable for 30 days)">🗑 Delete with its answer</button>}
            </div>
          </details>
        )}
      </div>
    </div>
  );
}

// 0.134.0: after a delete or rewrite - what was stopped, and what the forgotten messages left behind (memory candidates,
// dossier rows, files of their jobs): shown to the person to remove, never removed silently
function MsgTracesPanel({ done, env, hidden, onUndo, onClose }: {
  done: { res: MsgHideResult; what: string }; env: string; hidden: ChatEvent[]; onUndo: (ev: ChatEvent) => void; onClose: () => void;
}) {
  const { res } = done;
  const [gone, setGone] = useState<Set<string>>(new Set());
  const [err, setErr] = useState<string | null>(null);
  const act = async (key: string, fn: () => Promise<unknown>) => {
    setErr(null);
    try {
      await fn();
      setGone((g) => new Set(g).add(key));
    } catch (x) {
      setErr(categorize(x).message);
    }
  };
  const files = hidden.filter((e) => e.type === 'assistant_message' && e.jobId && res.answers.includes(e.jobId) && (!!e.artifacts?.length || !!e.exportSkipped?.length));
  const mem = res.traces.memory.filter((m) => !gone.has('m' + m.id));
  const dos = res.traces.dossier.filter((d) => !gone.has('d' + d.id));
  return (
    <div className="msg-traces small">
      <div>
        Hidden: {done.what}{res.stopped.length ? ` · stopped ${res.stopped.length} running job(s)` : ''}{res.protocolRemoved ? ` · ${res.protocolRemoved} protocol entr${res.protocolRemoved === 1 ? 'y' : 'ies'} taken out` : ''}.{' '}
        <button className="link" onClick={onClose}>Close</button>
      </div>
      {(mem.length > 0 || dos.length > 0 || files.length > 0) && <div className="muted">Left by the forgotten messages — remove what should be forgotten too:</div>}
      <ul>
        {mem.map((m) => (
          <li key={'m' + m.id}>
            memory #{m.msg} ({m.tag}, {m.status}): {m.text}{' '}
            {m.status === 'pending'
              ? <button className="link" onClick={() => void act('m' + m.id, () => dbMemoryDecide(m.id, 'reject'))}>Reject</button>
              : <span className="muted">already in the project memory: remove it there</span>}
          </li>
        ))}
        {dos.map((d) => (
          <li key={'d' + d.id}>
            dossier #{d.msg} ({d.section}{d.subject ? ` · ${d.subject}` : ''}): {d.text}{' '}
            <button className="link" onClick={() => void act('d' + d.id, () => dbDossierPut(d.env || env, 'delete', { id: d.id }))}>Delete</button>
          </li>
        ))}
        {files.map((e) => (
          <li key={e.eventId}>
            files of answer #{e.sequence}{' '}
            <button className="link" onClick={() => onUndo(e)}>↶ Undo changes of this job</button>
          </li>
        ))}
      </ul>
      {err && <div className="error-box">{err}</div>}
    </div>
  );
}

// 0.60.0: a follow-up on its way to the running agent, or waiting for its answer
function FollowupRow({ f, onCancel }: { f: Followup; onCancel?: () => void }) {
  const waiting = f.state === 'late';
  return (
    <div className="msg user">
      <div className="msg-meta">
        {waiting
          ? '🕓 Waits for the running answer, then goes as the next message'
          : '⏳ Passing to the running agent (it reads it after its current step)'}
      </div>
      <div className="msg-body pre">{f.text}</div>
      {onCancel && f.state !== 'offered' && (
        <div className="msg-actions">
          <button className="link" onClick={onCancel}>Cancel</button>
        </div>
      )}
    </div>
  );
}

// 0.76.0: the unfinished work plan of the chat's broken-off job (workPlan.ts), or ''.
async function readResume(chat: Chat, events: ChatEvent[], path: string): Promise<string> {
  const jobs = sideJobsOf(moduleList());
  const run = brokenRun(events, chat.chatId, (e) => sideJobOf(jobs, e) !== null);
  if (!run) return '';
  try {
    const f = await resolveFile(chat.folderId, ['scratch', PLAN_DIR], PLAN_FILE);
    // not on Drive (yet): the agent may have written it moments ago and Drive for desktop has not uploaded it
    if (!f) return resumeUnreadBlock(run, path);
    const text = await getText(f.id);
    return planApplies(text, f.modifiedTime, run) ? resumeBlock(text, run, path) : '';
  } catch {
    // a Drive error must not drop the plan silently: the agent is pointed to the file instead
    return resumeUnreadBlock(run, path);
  }
}

const STOP_CONFIRM_MS = 5000;

const PHASE_LABEL: Record<string, string> = {
  preparing: 'Preparing', queued: 'Queued', claimed: 'Claimed', running: 'Running',
  cancel_requested: 'Cancel requested', completed: 'Completed', failed: 'Failed', cancelled: 'Cancelled', rejected: 'Rejected',
};

// 0.90.2: compact (family chat) - only "AI Workbench is typing…" and Stop: no job id, steps, thinking or draft
function RunningTurn({
  ev, monitor, canCancel, cancelRequested, onStop, compact,
}: { ev: ChatEvent; monitor?: Monitor; canCancel: boolean; cancelRequested: boolean; onStop: () => Promise<void>; compact?: boolean }) {
  const phase = cancelRequested && monitor && !TERMINAL.includes(monitor.phase) ? 'cancel_requested' : monitor?.phase || 'queued';
  const act = monitor?.activity;
  const [stopping, setStopping] = useState(false);
  // 0.76.0: the card appears under the Send button; a tap in its first seconds is confirmed (01.11 a job was stopped 2 s
  // after it was sent and did nothing)
  const shownAt = useRef(Date.now());
  return (
    <div className="msg assistant running">
      <div className="msg-meta">
        {compact ? (
          <span className="muted small"><span className="spinner small" aria-hidden /> {assistantName()} is typing…</span>
        ) : <>
          <span className="who">{agentName(ev.agent)}</span>
          <span className={`phase phase-${phase}`}>
            <span className="spinner" aria-hidden /> {phase === 'queued' ? `Queued — waiting for ${hostLabel(eventHost(ev))}` : PHASE_LABEL[phase] || phase}
          </span>
          <span className="muted small">{ev.jobId}</span>
        </>}
        <div className="spacer" />
        {canCancel && (
          <button
            className="btn small danger"
            disabled={stopping || phase === 'cancel_requested'}
            onClick={async () => {
              if (Date.now() - shownAt.current < STOP_CONFIRM_MS && !window.confirm('Stop this job? It was sent a moment ago.')) return;
              setStopping(true);
              try {
                await onStop();
              } finally {
                setStopping(false);
              }
            }}
          >
            Stop
          </button>
        )}
      </div>
      {!compact && act && act.items.length > 0 && <ActivityList items={act.items} live />}
      {!compact && act && act.draft.length > 0 && (
        <div className="msg-body draft">
          <Markdown text={stripQuickReplies(act.draft.join('\n\n'))} />
        </div>
      )}
    </div>
  );
}

function ActivityList({ items, live }: { items: ActivityItem[]; live?: boolean }) {
  const shown = live ? items.slice(-12) : items;
  return (
    <ul className="activity">
      {live && items.length > shown.length && <li className="muted small">… {items.length - shown.length} earlier steps</li>}
      {shown.map((i) => (
        <li key={i.key} className={`act act-${i.kind} ${i.status || ''}${i.depth ? ' act-sub' : ''}`}>
          <details>
            <summary>
              <span className="act-icon" aria-hidden>{ICON[i.kind]}</span>
              <span className="act-label">{i.label}</span>
              {i.status === 'running' && <span className="spinner small" aria-label="running" />}
              {i.status === 'error' && <span className="tag bad">error</span>}
            </summary>
            {i.detail && <pre className="act-detail">{i.detail.length > 6000 ? i.detail.slice(0, 6000) + '\n…' : i.detail}</pre>}
            <pre className="act-raw">{i.raw.join('\n').slice(0, 8000)}</pre>
          </details>
        </li>
      ))}
    </ul>
  );
}

const ICON: Record<string, string> = {
  bridge: '◆', command: '›_', read: '📄', write: '✎', search: '⌕', tool: '⚙', reasoning: '…', error: '!', other: '·',
};

function AssistantBubble({
  ev, owner, root, host, onContinue, onCritique, onRetry, onRetryNew, onBranch, onDelete, onQuick, switchedTo, undo, cards,
}: {
  switchedTo?: string; // 0.71.0: agent the server retried this failed job on
  ev: ChatEvent; owner: OwnerInfo | null; root: Root; host: HostDirs;
  onContinue: (a: AgentId) => void; onCritique: (a: AgentId) => void; onRetry: (a: AgentId, text?: string) => void; onRetryNew: () => void; onBranch: () => void; onDelete?: () => void;
  onQuick?: (text: string) => void; // 0.57.0: set only for the newest answer
  undo?: { events: ChatEvent[]; projectId: string; chatId: string }; // 0.72.0: unset when the chat is read-only
  cards?: { modules: ModuleInfo[]; me: string; chat: ChatRef; live: boolean }; // 0.138.0 ui.messageCard
}) {
  const [undoOpen, setUndoOpen] = useState(false);
  const canUndo = !!undo && !!ev.jobId && !ev.imported && (!!ev.artifacts?.length || !!ev.exportSkipped?.length);
  const quick = useMemo(() => extractQuickReplies(stripTitleMarker(ev.text || '')), [ev.text]);
  // 0.138.0: [[card …]] lines of a finished answer (not of an imported one) become module cards
  const hasCards = !!cards;
  const shown = useMemo(() => hasCards && ev.status === 'completed' && !ev.imported ? extractCards(quick.text) : { text: quick.text, cards: [] },
    [hasCards, ev.status, ev.imported, quick.text]);
  const [trace, setTrace] = useState<ActivityState | null>(null);
  const [traceBusy, setTraceBusy] = useState(false);
  const [traceOpen, setTraceOpen] = useState(false); // 0.74.0: the job timeline loads on first open
  // No box for runs the user stopped, nor for dispatcher replies that already explain the gateway block.
  // Bridge 1.16.0: a run paused for the Bridge update is "cancelled" too, but says that it continues by itself.
  const blocked = gatewayBlocked(ev);
  const explained = blocked === 'agent_reported' && !!ev.text;
  const stoppedByUser = ev.status === 'cancelled' && !/Paused for the Bridge update/i.test(ev.error || '');
  const diag = stoppedByUser || explained ? null : diagnose({ error: ev.error, text: ev.text, agent: ev.agent, status: ev.status, gatewayBlocked: blocked, jobId: ev.jobId });
  const other: AgentId = ev.agent === 'claude' ? 'codex' : 'claude';
  const diagOther: AgentId = ev.agent === 'codex' ? 'claude' : 'codex'; // must match diagnose().otherName

  async function loadTrace() {
    if (trace || traceBusy || !ev.jobId) return;
    setTraceBusy(true);
    try {
      const bev = await readBridgeEvents(host, ev.jobId, newCursor());
      setTrace(applyBridgeEvents(emptyActivity(), bev));
    } catch {
      setTrace(emptyActivity());
    } finally {
      setTraceBusy(false);
    }
  }

  return (
    <div className={`msg assistant status-${ev.status}`} data-seq={ev.sequence}>
      <div className="msg-meta">
        <span className="who">{speaker(ev)}</span>
        {ev.imported ? <span className="tag">imported{ev.model ? ` · ${ev.model}` : ''}</span> : ev.modelReported ? <span className="tag" title="Model reported by the agent">{ev.modelReported}</span> : selectionTags(ev, owner)}
        {ev.status !== 'completed' && <span className={`phase phase-${ev.status}`}>{PHASE_LABEL[ev.status || ''] || ev.status}</span>}
        <span className="muted">{formatLocal(ev.completedUtc || ev.createdUtc)}</span>
      </div>
      {ev.text ? (
        <div className="msg-body">
          <Markdown text={shown.text} />
          {cards && shown.cards.map((c) => (
            <MessageCard key={`${c.module}/${c.card}/${c.id}`} modules={cards.modules} card={c} me={cards.me} chat={cards.chat} eventId={ev.eventId} live={cards.live} />
          ))}
          {quick.asks.length > 0 && (onQuick
            ? <AskForm asks={quick.asks} onSend={onQuick} />
            : <ol className="ask-form done">{quick.asks.map((a, i) => <li key={i}>{a.question}</li>)}</ol>)}
          {quick.options.length > 0 && (
            <div className="quick-options">
              {quick.options.map((o) => (
                <button key={o} type="button" className="btn small" disabled={!onQuick} onClick={() => onQuick?.(o)} title={onQuick ? 'Send this as your reply' : undefined}>{o}</button>
              ))}
            </div>
          )}
        </div>
      ) : (
        !diag && <div className="msg-body muted">(no final response)</div>
      )}
      {diag && (
        <div className="error-box">
          <strong>{diag.title}</strong>
          <div>{diag.todo}</div>
          {diag.actions.length > 0 && (
            <div>
              {diag.actions.map((a) => {
                switch (a) {
                  case 'continue_other':
                    // 0.71.0: already retried there by the server
                    if (switchedTo) return (<span key={a} className="muted small">↪ Sent automatically to {agentName(switchedTo)} (see below).</span>);
                    return (<button key={a} className="btn small" onClick={() => onRetry(diagOther)}>{'Send to ' + agentName(diagOther)}</button>);
                  case 'retry':
                    // 0.66.0: gateway paused - no Continue / Send again for its runs, only "Send to ..."
                    return RETIRED_AGENTS.has(ev.agent || '') ? null
                      : diag.kind === 'timeout'
                      ? (<button key={a} className="btn small" onClick={() => onRetry(ev.agent as AgentId, 'continue')}>Continue</button>)
                      : (<button key={a} className="btn small" onClick={() => onRetry(ev.agent as AgentId)}>Send again</button>);
                  case 'retry_new':
                    return (<button key={a} className="btn small" onClick={onRetryNew}>Retry in a new session</button>);
                  default:
                    return null;
                }
              })}
            </div>
          )}
          <details>
            <summary>Details</summary>
            <pre>{diag.detail}</pre>
          </details>
        </div>
      )}
      {!!ev.artifacts?.length && <MessageArtifacts list={ev.artifacts} root={root} />}
      {!!ev.missingArtifacts?.length && <div className="muted small">Not produced: {ev.missingArtifacts.join(', ')}</div>}
      {!!ev.exportSkipped?.length && (
        <details className="muted small">
          <summary>{ev.exportSkipped.length} changed file(s) not exported</summary>
          <ul>{ev.exportSkipped.map((x) => <li key={x.path}>{x.path}: {x.reason}</li>)}</ul>
        </details>
      )}
      {ev.usage && <div className="usage muted tiny">{formatUsage(ev.usage, ev.agent)}</div>}
      {!ev.imported && <details className="trace" onToggle={(e) => { const o = (e.currentTarget as HTMLDetailsElement).open; if (o) { setTraceOpen(true); void loadTrace(); } }}>
        <summary className="muted small">Timeline and trace</summary>
        {traceOpen && ev.jobId && <JobTimeline jobId={ev.jobId} />}
        {traceBusy && <div className="muted small">Loading…</div>}
        {trace && (trace.items.length ? <ActivityList items={trace.items} /> : <div className="muted small">No events recorded.</div>)}
      </details>}
      <div className="msg-actions">
        <button className="link" onClick={() => copy(stripQuickReplies(stripTitleMarker(ev.text || '')))}>Copy</button>
        <button className="link" onClick={() => onContinue(ev.agent as AgentId)}>Continue with {agentName(ev.agent)}</button>
        <button className="link" onClick={() => onContinue(other)}>Continue with {agentName(other)}</button>
        <button className="link" onClick={() => onCritique(other)}>Ask {agentName(other)} to critique</button>
        <button className="link" onClick={onBranch} title="New chat that continues from this answer; this chat stays as it is">⑂ Branch</button>
        {canUndo && <button className="link" onClick={() => setUndoOpen((o) => !o)} title="Preview the files this answer changed in the chat folder and restore their earlier versions">↶ Undo changes of this job</button>}
        {onDelete && <button className="link" onClick={onDelete} title="Hide this answer only (restorable for 30 days); the agent starts a new session">🗑 Delete answer</button>}
      </div>
      {canUndo && undoOpen && <UndoPanel rootId={root.id} ev={ev} events={undo!.events} projectId={undo!.projectId} chatId={undo!.chatId} onClose={() => setUndoOpen(false)} />}
    </div>
  );
}

// 0.57.0: short questions of an answer with an input each; Enter goes to the next field, on the last one sends.
function AskForm({ asks, onSend }: { asks: AskItem[]; onSend: (text: string) => void }) {
  const [answers, setAnswers] = useState<string[]>(() => asks.map(() => ''));
  const refs = useRef<(HTMLInputElement | null)[]>([]);
  const msg = formatAnswers(asks, answers);
  const set = (i: number, v: string) => setAnswers((a) => a.map((x, j) => (j === i ? v : x)));
  return (
    <form className="ask-form" onSubmit={(e) => { e.preventDefault(); if (msg) onSend(msg); }}>
      <ol>
        {asks.map((a, i) => (
          <li key={i}>
            <label>
              <span className="ask-q">{a.question}</span>
              <input
                ref={(el) => { refs.current[i] = el; }}
                value={answers[i] || ''}
                onChange={(e) => set(i, e.target.value)}
                onKeyDown={(e) => {
                  if (e.key !== 'Enter' || e.shiftKey || e.nativeEvent.isComposing) return;
                  if (i < asks.length - 1) {
                    e.preventDefault();
                    refs.current[i + 1]?.focus();
                  }
                }}
              />
            </label>
            {a.suggestions.length > 0 && (
              <span className="ask-sugg">
                {a.suggestions.map((s) => (
                  <button key={s} type="button" className={`chip${answers[i] === s ? ' on' : ''}`} onClick={() => set(i, s)}>{s}</button>
                ))}
              </span>
            )}
          </li>
        ))}
      </ol>
      <button type="submit" className="btn small" disabled={!msg} title="Send the filled answers as one message">Send answers</button>
    </form>
  );
}

// The version of an uploaded file this message sent: the chat file's revision by md5, or the request copy.
function attachmentVersion(a: AttachmentRef, root: Root) {
  return a.streamPath
    ? fetchVersion(root.id, { path: a.streamPath, md5: a.md5, sha256: a.sha256 }).catch(async (e) => {
        if (!a.fileId) throw e;
        return fetchVersion(root.id, { path: '', legacy: true, fileId: a.fileId });
      })
    : fetchVersion(root.id, { path: '', legacy: true, fileId: a.fileId });
}

// 0.95.0: photos of a message show as numbered thumbnails (the number is the order the agent got them in, "photo 4"),
// a click opens them full size with ← → between the photos of that message.
const ATTACHED_IMG_RE = /\.(jpe?g|png|gif|webp|bmp|avif)$/i;
const isAttachedImage = (a: AttachmentRef) => ATTACHED_IMG_RE.test(a.displayName) && !!(a.fileId || a.streamPath);
const IMAGE_TYPES: Record<string, string> = { png: 'image/png', gif: 'image/gif', webp: 'image/webp', bmp: 'image/bmp', avif: 'image/avif' };
const imageType = (name: string) => IMAGE_TYPES[(name.split('.').pop() || '').toLowerCase()] || 'image/jpeg';
interface LoadedImage { full: string; thumb: string; note: string | null }
const loadedImages = new Map<string, Promise<LoadedImage>>(); // attachment version -> object URLs (kept for the session)
let imageSlots = 3; // phone photos are 3-6 MB: fetch a few at a time
const imageQueue: (() => void)[] = [];
async function imageSlot<T>(run: () => Promise<T>): Promise<T> {
  if (imageSlots > 0) imageSlots--;
  else await new Promise<void>((r) => imageQueue.push(r));
  try {
    return await run();
  } finally {
    const next = imageQueue.shift();
    if (next) next();
    else imageSlots++;
  }
}
async function makeThumb(blob: Blob): Promise<string> {
  try {
    const bmp = await createImageBitmap(blob);
    const k = Math.min(1, 320 / Math.max(bmp.width, bmp.height));
    const c = document.createElement('canvas');
    c.width = Math.max(1, Math.round(bmp.width * k));
    c.height = Math.max(1, Math.round(bmp.height * k));
    c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height);
    bmp.close();
    const t = await new Promise<Blob | null>((r) => c.toBlob(r, 'image/jpeg', 0.82));
    if (t) return URL.createObjectURL(t);
  } catch {
    // the browser cannot decode it here: the full image stands in
  }
  return '';
}
function loadImage(a: AttachmentRef, root: Root): Promise<LoadedImage> {
  const key = `${a.streamPath || ''}|${a.md5 || a.sha256}|${a.fileId || ''}`;
  let p = loadedImages.get(key);
  if (!p) {
    p = imageSlot(async () => {
      const v = await attachmentVersion(a, root);
      const blob = new Blob([v.bytes], { type: imageType(a.displayName) });
      const full = URL.createObjectURL(blob);
      return { full, thumb: (await makeThumb(blob)) || full, note: v.source === 'expired' ? 'version expired, current file' : null };
    });
    p.catch(() => loadedImages.delete(key));
    loadedImages.set(key, p);
  }
  return p;
}

function AttachmentImages({ list, root }: { list: AttachmentRef[]; root: Root }) {
  const [open, setOpen] = useState<number | null>(null);
  return (
    <div className="att-imgs">
      {list.map((a, i) => <AttachmentThumb key={a.path} a={a} n={i + 1} root={root} onOpen={() => setOpen(i)} />)}
      {open !== null && <ImageViewer key={open} list={list} at={open} root={root} onMove={setOpen} onClose={() => setOpen(null)} />}
    </div>
  );
}

function AttachmentThumb({ a, n, root, onOpen }: { a: AttachmentRef; n: number; root: Root; onOpen: () => void }) {
  const box = useRef<HTMLButtonElement>(null);
  const [img, setImg] = useState<LoadedImage | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    let live = true;
    const ob = new IntersectionObserver((es) => {
      if (!es.some((e) => e.isIntersecting)) return;
      ob.disconnect();
      loadImage(a, root).then((v) => live && setImg(v)).catch((e) => live && setErr(categorize(e).message));
    }, { rootMargin: '400px' });
    ob.observe(el);
    return () => { live = false; ob.disconnect(); };
  }, [a.path, a.md5, a.fileId, root.id]); // eslint-disable-line react-hooks/exhaustive-deps
  const tip = [`${n}. ${a.displayName} · ${formatBytes(a.sizeBytes)}`, a.target && a.target !== a.displayName ? `→ ${a.target}` : '', err || img?.note || ''];
  return (
    <button type="button" ref={box} className="att-img" onClick={onOpen} title={tip.filter(Boolean).join('\n')}>
      {img ? <img src={img.thumb} alt={a.displayName} /> : <span className="att-img-ph">{err ? '⚠' : '…'}</span>}
      <span className="att-img-n">{n}</span>
    </button>
  );
}

function ImageViewer({ list, at, root, onMove, onClose }: { list: AttachmentRef[]; at: number; root: Root; onMove: (i: number) => void; onClose: () => void }) {
  const a = list[at];
  const [img, setImg] = useState<LoadedImage | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const go = useCallback((d: number) => onMove((at + d + list.length) % list.length), [at, list.length, onMove]);
  useEffect(() => {
    let live = true;
    loadImage(a, root).then((v) => live && setImg(v)).catch((e) => live && setErr(categorize(e).message));
    return () => { live = false; };
  }, [a.path, a.md5, a.fileId, root.id]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const k = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      else if (e.key === 'ArrowLeft') go(-1);
      else if (e.key === 'ArrowRight') go(1);
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [go, onClose]);
  const stop = (e: ReactMouseEvent) => e.stopPropagation();
  return createPortal(
    <div className="fam-lightbox att-viewer" onClick={onClose} role="dialog">
      {img ? <img src={img.full} alt={a.displayName} /> : <span className="att-viewer-msg">{err || 'Loading…'}</span>}
      {list.length > 1 && <button type="button" className="att-nav prev" title="Previous (←)" onClick={(e) => { stop(e); go(-1); }}>‹</button>}
      {list.length > 1 && <button type="button" className="att-nav next" title="Next (→)" onClick={(e) => { stop(e); go(1); }}>›</button>}
      <div className="att-caption" onClick={stop}>
        <b>{at + 1}</b>{list.length > 1 && ` / ${list.length}`} · {a.displayName} · {formatBytes(a.sizeBytes)}
        {img && <> · <a href={img.full} download={a.displayName}>Download</a></>}
        {img?.note && <span> · {img.note}</span>}
      </div>
    </div>,
    document.body,
  );
}

// 0.55.0: an uploaded file of a message, click = download that version (the chat file's revision by md5, or the copy).
function AttachmentChip({ a, root }: { a: AttachmentRef; root: Root }) {
  const [openFile, viewer] = useOpenFile();
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function download() {
    setBusy(true);
    setMsg(null);
    try {
      const v = await attachmentVersion(a, root);
      if (v.source === 'expired') setMsg('version expired, current file');
      const url = URL.createObjectURL(new Blob([v.bytes]));
      const link = document.createElement('a');
      link.href = url;
      link.download = a.displayName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (e) {
      setMsg(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <span className="chip" title={a.target ? `Chat file ${a.target} (click to open the current file; ⬇ downloads this version)` : 'Click to open; ⬇ downloads it'}>
      <button type="button" className="link" disabled={busy || (!a.fileId && !a.streamPath)}
        onClick={() => openFile({ name: a.displayName, path: a.streamPath || null, load: async () => (await attachmentVersion(a, root)).bytes, fallback: () => void download() })}>📎 {a.displayName}</button>
      {viewer}
      <button type="button" className="link tiny" disabled={busy || (!a.fileId && !a.streamPath)} title="Download this version" aria-label={`Download ${a.displayName}`} onClick={() => void download()}>⬇</button>
      {' '}· {formatBytes(a.sizeBytes)}
      {a.target && a.target !== a.displayName && <span className="muted tiny"> → {a.target}</span>}
      {msg && <span className="muted tiny"> · {msg}</span>}
    </span>
  );
}

// 0.56.0: working files of the agent (isWorkingFile) fold into one line under the message; the result files stay as buttons.
function MessageArtifacts({ list, root }: { list: ArtifactRecord[]; root: Root }) {
  const main = list.filter((a) => !isWorkingFile(a.sourceRelativePath || a.driveRelativePath, a.sizeBytes));
  const work = list.filter((a) => !main.includes(a));
  const names = work.map((a) => (a.sourceRelativePath || a.driveRelativePath).split('/').pop() || '');
  const preview = names.slice(0, 3).join(', ') + (names.length > 3 ? ', …' : '');
  return (
    <div className="artifacts">
      {main.map((a) => <ArtifactRow key={a.driveRelativePath} a={a} root={root} />)}
      {!!work.length && (
        <details className="working-files">
          <summary className="muted small" title={names.join('\n')}>
            Service files ({work.length}) <span className="tiny">· {preview}</span>
          </summary>
          <div className="artifacts">
            {work.map((a) => <ArtifactRow key={a.driveRelativePath} a={a} root={root} />)}
          </div>
        </details>
      )}
    </div>
  );
}

export function ArtifactRow({ a, root }: { a: ArtifactRecord; root: Root }) {
  const goModule = useNavigate();
  const [openFile, viewer] = useOpenFile();
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [saved, setSaved] = useState<{ name: string; link?: string } | null>(null);
  const [picking, setPicking] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const name = a.sourceRelativePath.split('/').pop() || a.sourceRelativePath;
  const type = guessType(name);
  const previewable = /^(text|image)\/|application\/(pdf|json)/.test(type);

  async function run(fn: () => Promise<void>) {
    setBusy(true);
    setErr(null);
    try {
      await fn();
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  // 0.55.0: a reference (Bridge 1.6.0) is the live chat file; this message's version is the Drive revision with its md5.
  // A legacy copy (artifacts/<job>/) that is gone falls back to the live file when it still has the same content.
  async function verifiedBytes(): Promise<ArrayBuffer> {
    setNote(null);
    let bytes: ArrayBuffer;
    let checked = true;
    if (a.ref) {
      const v = await fetchVersion(root.id, { path: a.driveRelativePath, md5: a.md5, sha256: a.sha256 });
      if (v.source === 'expired') {
        setNote('This version expired (Drive keeps old versions 30 days): this is the current file.');
        return v.bytes;
      }
      if (v.source === 'revision') setNote('An earlier version: the file changed after this message.');
      bytes = v.bytes;
    } else {
      try {
        bytes = (await fetchArtifact(root, a.driveRelativePath)).bytes;
      } catch (e) {
        const m = /^(projects\/[^/]+\/chats\/[^/]+)\/(?:branches\/[^/]+\/)?artifacts\/[^/]+\/(.+)$/.exec(a.driveRelativePath);
        if (!m) throw e;
        const v = await fetchVersion(root.id, { path: `${m[1]}/scratch/${m[2]}`, md5: a.md5, sha256: a.sha256 });
        if (v.source === 'expired') setNote('This version expired (old copies are kept 30 days): this is the current file.');
        checked = v.source !== 'expired';
        bytes = v.bytes;
      }
    }
    if (checked && (await sha256Hex(bytes)) !== a.sha256.toLowerCase())
      throw new Error('SHA-256 mismatch: Drive copy differs from the recorded artifact.');
    return bytes;
  }

  const download = () =>
    run(async () => {
      const url = URL.createObjectURL(new Blob([await verifiedBytes()], { type }));
      const link = document.createElement('a');
      link.href = url;
      link.download = name;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    });

  const preview = () =>
    run(async () => {
      const url = URL.createObjectURL(new Blob([await verifiedBytes()], { type }));
      window.open(url, '_blank', 'noopener');
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    });

  // Open the tab synchronously (user gesture), then point it at the Drive viewer or the Google editor.
  const openAt = (url: (f: { id: string; webViewLink?: string }) => string) => {
    const w = window.open('about:blank', '_blank');
    void run(async () => {
      try {
        const f = await locateArtifact(root, a.driveRelativePath);
        if (w) w.location.href = url(f);
      } catch (e) {
        w?.close();
        throw e;
      }
    });
  };
  const openInDrive = () => openAt((f) => f.webViewLink || `https://drive.google.com/file/d/${f.id}/view`);
  const openInDocs = () => openAt((f) => googleDocsUrl(f.id, name) || `https://drive.google.com/file/d/${f.id}/view`);

  const saveTo = (folder: { id: string; path: string }) =>
    run(async () => {
      const f = await locateArtifact(root, a.driveRelativePath);
      const copy = await copyToFolder(f.id, folder.id, name);
      setSaved({ name: `${folder.path}/${copy.name}`, link: copy.webViewLink });
    });

  return (
    <div className="artifact">
      <button type="button" className="link art-name" disabled={busy} title={`${a.driveRelativePath}\nClick to open (download: ⋯)`}
        onClick={() => openFile({ name, path: a.driveRelativePath, load: verifiedBytes, fallback: () => void download() })}>📦 {name}</button>
      {viewer}
      <span className="muted small">{formatBytes(a.sizeBytes)}</span>
      <FileMenu
        name={name}
        busy={busy}
        items={[
          { label: 'Download', onClick: () => void download() },
          previewable && { label: 'Preview', onClick: () => void preview() },
          { label: 'Open in Drive', onClick: openInDrive },
          hasGoogleDocs(name) && { label: 'Open in Google Docs', onClick: openInDocs },
          ...moduleFileItems(moduleList(), name, a.driveRelativePath, (href) => goModule(href)),
          { label: 'Save to Drive…', onClick: () => setPicking(true) },
        ]}
      />
      {saved && (
        <span className="small ok-text">
          Saved: {saved.link ? <a href={saved.link} target="_blank" rel="noopener noreferrer">{saved.name}</a> : saved.name}
        </span>
      )}
      {err && <span className="error-text small">{err}</span>}
      {note && <span className="muted small">{note}</span>}
      {picking && (
        <FolderPicker
          onCancel={() => setPicking(false)}
          onPick={(f) => {
            setPicking(false);
            void saveTo(f);
          }}
        />
      )}
    </div>
  );
}

function guessType(name: string): string {
  const ext = name.toLowerCase().split('.').pop() || '';
  const map: Record<string, string> = {
    txt: 'text/plain;charset=utf-8', md: 'text/plain;charset=utf-8', json: 'application/json', csv: 'text/plain;charset=utf-8',
    log: 'text/plain;charset=utf-8', pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg',
    gif: 'image/gif', svg: 'text/plain;charset=utf-8', html: 'text/plain;charset=utf-8', py: 'text/plain;charset=utf-8',
    ts: 'text/plain;charset=utf-8', js: 'text/plain;charset=utf-8', cs: 'text/plain;charset=utf-8', xml: 'text/plain;charset=utf-8',
  };
  return map[ext] || 'application/octet-stream';
}

// ---------- Composer ----------

interface Prefill {
  text: string;
  agent?: AgentId;
  forceNew?: boolean;
  files?: File[]; // CO Web 0.14.0: shared from another app
  send?: boolean; // 0.57.0: quick reply - send the text at once with the current selection, the draft stays
  nonce: number;
}

interface SendForm {
  text: string;
  sel: Selection;
  files: File[];
  // 0.55.0: per file - 'version' continues the matched chat file, 'new' makes a separate file
  fileModes?: ('version' | 'new')[];
  returnFiles: string[];
  forceNew: boolean;
  family?: { called: boolean; replyTo?: string; coFor?: string; following?: boolean }; // 0.89.0: an agent turn of a family chat; 0.109.0 following - no @co, the agent follows its topic
}

function Composer({
  draftKey, initial, owner, owners, hostId, env, allowedModes, allowedAgents, prefill, disabled, canAttach, matchFile, hostHealth, events, onSend, onModeChange, templates,
}: {
  draftKey: string;
  initial: Selection;
  owner: OwnerInfo | null;
  owners: Record<string, OwnerInfo | null>;
  hostId: string;
  env: string;
  allowedModes: Mode[];
  allowedAgents: AgentId[];
  prefill: Prefill | null;
  disabled: boolean;
  canAttach: boolean;
  // 0.55.0: name of the chat file an upload would continue (null = a new file); absent = the host has no chat files
  matchFile?: (name: string) => string | null;
  hostHealth: HostHealth;
  // the chat's messages, for composer pages with chat.read (voice mode waits for the answer)
  events?: ChatEvent[] | null;
  onSend: (i: SendForm, progress: (s: string) => void) => Promise<void>;
  onModeChange: (m: Mode) => void;
  // 0.37.0: environment templates (new-chat screen only)
  templates?: { list: EnvTemplate[]; onApply: (t: EnvTemplate) => void; onSave: (sel: Selection) => void; onDelete: (name: string) => void; err: string | null };
}) {
  const [text, setText, clearDraft] = useDraft(draftKey); // 0.31.0: survives reloads and app upgrades
  const [sel, setSel] = useState<Selection>(initial);
  const [files, setFiles] = useState<File[]>([]);
  // 0.67.0: a long text pasted into the box can become a .md attachment
  // 0.69.0: where the long paste landed in the message; the same text pasted twice is cut out at that place
  const [longPaste, setLongPaste] = useState<{ text: string; at: number } | null>(null);
  const [separate, setSeparate] = useState<boolean[]>([]);
  const [ret, setRet] = useState('');
  const [forceNew, setForceNew] = useState(false);
  const [showOptions, setShowOptions] = useState(false);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [drag, setDrag] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const ta = useRef<HTMLTextAreaElement>(null);
  // Freedom F10: input actions of modules (ui.composer slot, ext/1 composer.input), e.g. voice input. A session keeps
  // the box text of its first input and puts the session's text (and an interim tail) after it; done ends it.
  const composerModules = useLoadedModules();
  const inputBase = useRef<string | null>(null);
  const composerInput = useMemo<ComposerInput>(() => ({
    set(t, interim, done) {
      setText((prev) => {
        const base = inputBase.current ?? (prev && !/\s$/.test(prev) ? prev + ' ' : prev);
        inputBase.current = done ? null : base;
        return base + t + (interim ? (t ? ' ' : '') + interim : '');
      });
    },
    focus() {
      const el = ta.current;
      if (el) {
        el.focus();
        el.setSelectionRange(el.value.length, el.value.length);
      }
    },
    // ext/1 composer.send (voice mode): the phrase goes out like a quick reply; a busy composer refuses it
    send: (t: string) => voiceSend.current(t),
    events: events || undefined,
  }), [setText, events]);

  // S6: the catalog of the executor the model was picked on (ModelPicker), else of the chat's current one
  const catalog = (sel.host ? owners[sel.host] ?? owner : owner)?.agents?.[sel.agent];
  const models = catalog?.models || [];
  const currentModel = models.find((m) => m.id === sel.model);
  const efforts = currentModel?.efforts?.length ? currentModel.efforts : catalog?.efforts || [];
  const agentModes = (catalog?.modes as Mode[] | undefined) || ['cowork'];
  const modes = allowedModes.filter((m) => agentModes.includes(m));
  // 0.18.1: only agents the selected host announces (the server agent has no gateway agents: "Unknown agent profile").
  // S6: an agent of any executor (routeHost sends the job where it lives), e.g. GPT at home while the chat is on Site
  // F10: PICKABLE_AGENTS is refilled when the gateway description arrives or changes (gatewayVersion)
  const gwVersion = gatewayVersion();
  const projAgents = useMemo(() => PICKABLE_AGENTS.filter((a) => allowedAgents.includes(a.id)), [allowedAgents.join(','), gwVersion]); // eslint-disable-line react-hooks/exhaustive-deps
  const agents = projAgents.filter((a) => !owner?.agents || !!owner.agents[a.id] || Object.values(owners).some((o) => !!o?.agents?.[a.id]));
  // F10: a hint of a module (ui.hint, e.g. which agent suits the task); a suggestion only, the user switches the agent.
  // The first pick of the hint that this host offers is shown.
  const modules = useModuleList();
  const [moduleHint, setModuleHint] = useState<ModuleHint | null>(null);
  const hint = useMemo(() => {
    if (!moduleHint) return null;
    const i = moduleHint.picks.findIndex((p) => agents.some((a) => a.id === p.agent) && (p.model === null || !!owner?.agents?.[p.agent]?.models?.some((m) => m.id === p.model)));
    return i < 0 ? null : { text: moduleHint.text, title: moduleHint.title, pick: moduleHint.picks[i], isFallback: i > 0 };
  }, [moduleHint, agents, owner]);
  const hintShown = !!hint && !!text.trim() && !(sel.agent === hint.pick.agent && (sel.model || null) === hint.pick.model);

  // S6: the pinned executor is not in the chat's events; keep it when the selection follows the last sent message
  useEffect(() => setSel((s) => ({ ...initial, host: initial.host ?? s.host ?? null })), [initial.agent, initial.mode, initial.model, initial.effort]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!prefill) return;
    if (prefill.send) {
      void sendQuick(prefill.text, canAttach ? prefill.files : undefined);
      return;
    }
    if (prefill.text) setText(prefill.text);
    if (prefill.files?.length && canAttach) addFiles(prefill.files);
    if (prefill.agent) setSel((s) => ({ ...s, agent: prefill.agent!, model: null, effort: null, host: null }));
    setForceNew(!!prefill.forceNew);
    if (prefill.forceNew) setShowOptions(true);
    ta.current?.focus();
  }, [prefill?.nonce]); // eslint-disable-line react-hooks/exhaustive-deps

  // Keep the selection valid when the catalog or agent changes.
  useEffect(() => {
    setSel((s) => {
      let next = s;
      if (agents.length && !agents.some((a) => a.id === s.agent)) next = { ...next, agent: agents[0].id, model: null, effort: null, host: null };
      // a pin stays while auto flips the executor; it goes only when the pinned host no longer has the agent
      if (next.host && owners[next.host]?.agents && !owners[next.host]!.agents![next.agent]) next = { ...next, host: null };
      // the agent is not on the current executor (old chat, retry, hint): pin the executor that has it, as the picker shows
      if (!next.host && owners[hostId]?.agents && !owners[hostId]!.agents![next.agent]) {
        const other = Object.keys(owners).find((h) => !!owners[h]?.agents?.[next.agent]);
        if (other) next = { ...next, host: other };
      }
      if (!modes.includes(s.mode) && modes.length) next = { ...next, mode: modes[0] };
      if (s.model && models.length && !models.some((m) => m.id === s.model)) next = { ...next, model: null };
      if (s.effort && efforts.length && !efforts.includes(s.effort)) next = { ...next, effort: null };
      return next;
    });
  }, [owner, owners, hostId, sel.agent, sel.model, sel.host]); // eslint-disable-line react-hooks/exhaustive-deps

  function addFiles(list: FileList | File[]) {
    const arr = Array.from(list);
    const tooBig = arr.filter((f) => f.size > MAX_ATTACHMENT_BYTES);
    if (tooBig.length) setErr(`Over ${formatBytes(MAX_ATTACHMENT_BYTES)} limit: ${tooBig.map((f) => f.name).join(', ')}`);
    const ok = arr.filter((f) => f.size <= MAX_ATTACHMENT_BYTES);
    setFiles((prev) => [...prev, ...ok]);
    setSeparate((prev) => [...prev, ...ok.map(() => false)]);
  }

  // 0.57.0: a quick reply goes out on its own; files, options and the typed draft stay for the next message.
  // While the composer is busy (or disabled) or the send fails, the reply is added to the input box after the draft.
  const keepInBox = (t: string) => setText(text.trim() ? `${text.trimEnd()}\n\n${t}` : t);
  // 0.80.0: a viewer question brings its files (e.g. a PNG of a page area, the page text); they stay attached if it is not sent
  async function sendQuick(t: string, withFiles?: File[]) {
    if (!t.trim()) return;
    if (busy || disabled) {
      keepInBox(t);
      if (withFiles?.length) addFiles(withFiles);
      ta.current?.focus();
      return;
    }
    setBusy(true);
    setErr(null);
    try {
      await onSend({ text: t.trim(), sel, files: withFiles || [], returnFiles: [], forceNew: false }, setProgress);
    } catch (e) {
      const c = categorize(e);
      keepInBox(t);
      if (withFiles?.length) addFiles(withFiles);
      setErr(`${c.message}${c.detail ? ` (${c.detail})` : ''}`);
    } finally {
      setBusy(false);
      setProgress('');
    }
  }

  // composer.send of a module page: the latest state (a ref, since composerInput is memoized)
  const voiceSend = useRef<(t: string) => Promise<void>>(async () => undefined);
  const voiceBusy = useRef(false); // set at once: two sends in one tick must not both pass the busy check
  voiceSend.current = async (t: string) => {
    if (busy || disabled || voiceBusy.current) throw new Error('The chat is busy; try again when the answer is in.');
    voiceBusy.current = true;
    setBusy(true);
    setErr(null);
    try {
      await onSend({ text: t.trim(), sel, files: [], returnFiles: [], forceNew: false }, setProgress);
    } finally {
      voiceBusy.current = false;
      setBusy(false);
      setProgress('');
    }
  };

  async function submit() {
    if (!text.trim() || busy || disabled) return;
    inputBase.current = null;
    window.dispatchEvent(new Event(COMPOSER_SENT)); // input actions of modules stop listening
    setBusy(true);
    setErr(null);
    try {
      const returnFiles = ret.split(/[,\n]/).map((s) => s.trim()).filter(Boolean);
      const fileModes = files.map((_, i) => (separate[i] ? 'new' : 'version') as 'version' | 'new');
      await onSend({ text: text.trim(), sel, files, fileModes, returnFiles, forceNew }, setProgress);
      clearDraft();
      setFiles([]);
      setSeparate([]);
      setRet('');
      setForceNew(false);
    } catch (e) {
      const c = categorize(e);
      setErr(`${c.message}${c.detail ? ` (${c.detail})` : ''}`);
    } finally {
      setBusy(false);
      setProgress('');
    }
  }

  const defaultEffort = currentModel?.defaultEffort || (sel.model ? null : catalog?.defaultEffort) || null;

  return (
    <div
      className={`composer ${drag ? 'drag' : ''}`}
      onDragOver={(e) => {
        if (!canAttach) return;
        e.preventDefault();
        setDrag(true);
      }}
      onDragLeave={() => setDrag(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDrag(false);
        if (canAttach && e.dataTransfer.files.length) addFiles(e.dataTransfer.files);
      }}
    >
      <RowResizer cssVar="--composer-h" storageKey="co.composerH" def={84} min={48} minRest={120} />
      {hostHealth === 'updating' ? (
        <div className="warn small">The selected agent is {hostStateText(owner, hostHealth)} to finish. New jobs go to the other agent if it is online, otherwise wait in the queue.</div>
      ) : hostHealth === 'paused' ? (
        <div className="warn small">The selected agent is paused (Bridge tray menu). New jobs go to the other agent if it is online, otherwise wait until it is resumed.</div>
      ) : hostHealth !== 'online' && hostHealth !== 'unknown' && (
        <div className="warn small">The selected agent is {hostHealth}. Jobs will wait in the queue until it is back.</div>
      )}
      {templates && (
        <div className="templates-row small">
          <span className="muted">Templates:</span>
          {templates.list.map((t) => (
            <span key={t.name} className="template">
              <button
                type="button"
                className="btn tiny ghost"
                disabled={disabled}
                title={`${agentName(t.agent)} / ${t.mode} / ${t.model || 'default model'}${t.effort ? ` / ${t.effort}` : ''} / ${t.hostId ? hostLabel(t.hostId).toLowerCase() : 'home'}`}
                onClick={() => {
                  setSel({ agent: t.agent as AgentId, mode: t.mode, model: t.model, effort: t.effort, host: t.hostId && t.hostId !== hostId ? t.hostId : null });
                  templates.onApply(t);
                }}
              >
                {t.name}
              </button>
              <button type="button" className="link tiny" disabled={disabled} title={`Delete template "${t.name}"`} aria-label={`Delete template ${t.name}`} onClick={() => templates.onDelete(t.name)}>
                ×
              </button>
            </span>
          ))}
          <button type="button" className="btn tiny ghost" disabled={disabled} title="Save agent, model, mode, executor, permissions and device as a template" onClick={() => templates.onSave(sel)}>
            + Save as template
          </button>
          {templates.err && <span className="error-text tiny">{templates.err}</span>}
        </div>
      )}
      <div className="composer-row top">
        {modes.length > 1 ? (
          <div className="seg" role="radiogroup" aria-label="Mode">
            {modes.map((m) => (
              <button
                key={m}
                role="radio"
                aria-checked={sel.mode === m}
                className={sel.mode === m ? 'active' : ''}
                title={m === 'chat'
                  ? 'Chat: light agent (fewer tools and tokens) in this chat\'s own folder'
                  : sel.agent === 'codex'
                    ? 'Codex: full Codex agent with plugins and your Codex settings, in the project workspace (or this chat\'s folder)'
                    : 'Cowork: full Claude Code agent in the project workspace (or this chat\'s folder)'}
                onClick={() => {
                  setSel({ ...sel, mode: m });
                  onModeChange(m);
                }}
              >
                {m === 'chat' ? 'Chat' : sel.agent === 'codex' ? 'Codex' : 'Cowork'}
              </button>
            ))}
          </div>
        ) : (
          <span className="tag">{modes[0] === 'chat' ? 'Chat' : 'Cowork'}</span>
        )}
        {/* S6 (0.100.0): one model list (model resources) instead of the agent and model selects */}
        <ModelPicker sel={sel} env={env} hostId={hostId} owners={owners} agents={agents} disabled={disabled}
          onPick={(p) => setSel({ ...sel, agent: p.agent as AgentId, model: p.model, host: p.host, effort: p.agent === sel.agent && p.model === sel.model ? sel.effort : null })} />
        {efforts.length > 0 && (
          <select value={sel.effort || ''} onChange={(e) => setSel({ ...sel, effort: e.target.value || null })} aria-label="Effort">
            <option value="">{defaultEffort ? `Effort: default (${defaultEffort})` : 'Effort: default'}</option>
            {efforts.map((x) => <option key={x} value={x}>Effort: {x}</option>)}
          </select>
        )}
      </div>
      <ModuleHints modules={modules} text={text} me={env} owner={false} onHint={setModuleHint} />
      {hintShown && hint && (
        <div className="muted small task-hint" title={`${hint.title}${hint.isFallback ? '; the best pick is not available here' : ''}`}>
          {hint.text}{' '}
          <button type="button" className="link" disabled={disabled || busy}
            onClick={() => setSel({ ...sel, agent: hint.pick.agent as AgentId, model: hint.pick.model, effort: null, host: null })}>
            {hint.pick.label}
          </button>
        </div>
      )}
      {!!files.length && (
        <div className="chips">
          {files.map((f, i) => {
            const match = matchFile ? matchFile(f.name) : null;
            return (
              <span key={i} className="chip" title={match ? `The chat already has ${match}: this upload becomes its new version (older versions stay in the history)` : undefined}>
                📎 {f.name} · {formatBytes(f.size)}
                {match && !separate[i] && <span className="muted tiny"> → new version of {match}</span>}
                {match && (
                  <button type="button" className="link tiny" onClick={() => setSeparate(separate.map((x, j) => (j === i ? !x : x)))}>
                    {separate[i] ? 'as new version' : 'separate file'}
                  </button>
                )}
                <button className="chip-x" aria-label={`Remove ${f.name}`} onClick={() => { setFiles(files.filter((_, j) => j !== i)); setSeparate(separate.filter((_, j) => j !== i)); }}>×</button>
              </span>
            );
          })}
        </div>
      )}
      {longPaste && text.includes(longPaste.text) && (
        <div className="muted small long-paste">
          Long text pasted ({longPaste.text.length.toLocaleString()} characters).{' '}
          <button type="button" className="link" disabled={busy} title="Moves the pasted text into a Markdown file attached to this message"
            onClick={() => {
              const name = `pasted-${new Date().toISOString().slice(0, 16).replace(/[-:]/g, '').replace('T', '-')}.md`;
              const file = new File([longPaste.text], name, { type: 'text/markdown' });
              if (file.size > MAX_ATTACHMENT_BYTES) {
                setErr(`Over ${formatBytes(MAX_ATTACHMENT_BYTES)} limit: the text stays in the message`);
                setLongPaste(null);
                return;
              }
              const pasted = longPaste.text;
              // the place it was pasted at, unless the message was edited in front of it; then the last copy
              const at = text.startsWith(pasted, longPaste.at) ? longPaste.at : text.lastIndexOf(pasted);
              const rest = (text.slice(0, at) + text.slice(at + pasted.length)).trim();
              addFiles([file]);
              setText(rest || `Pasted text: ${name}`);
              setLongPaste(null);
            }}>
            Attach as a notebook (.md)
          </button>{' '}
          <button type="button" className="chip-x" aria-label="Keep the text in the message" onClick={() => setLongPaste(null)}>×</button>
        </div>
      )}
      <textarea
        ref={ta}
        value={text}
        rows={3}
        placeholder={`Message ${agentName(sel.agent)}… (Ctrl+Enter to send; /btw for a side question)`}
        onChange={(e) => setText(e.target.value)}
        onPaste={(e) => {
          if (canAttach && e.clipboardData.files.length) {
            e.preventDefault();
            addFiles(e.clipboardData.files);
            return;
          }
          // the text box keeps line ends as \n, so the pasted text is compared in that form
          const t = e.clipboardData.getData('text/plain').replace(/\r\n?/g, '\n');
          if (canAttach && t.length >= LONG_PASTE_CHARS) setLongPaste({ text: t, at: e.currentTarget.selectionStart });
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            void submit();
          }
        }}
        disabled={busy}
        aria-label="Message"
      />
      <div className="composer-row">
        <button className="btn ghost small" onClick={() => fileInput.current?.click()} disabled={!canAttach || busy} title="Attach files">📎 Attach</button>
        <input ref={fileInput} type="file" multiple hidden onChange={(e) => e.target.files && addFiles(e.target.files)} />
        <ModuleSlots slot="ui.composer" modules={composerModules} owner={false} input={composerInput} />
        <button className="btn ghost small" onClick={() => setShowOptions((v) => !v)} aria-expanded={showOptions}>Options</button>
        <div className="spacer" />
        {progress && <span className="muted small">{progress}</span>}
        <button className="btn primary" onClick={() => void submit()} disabled={busy || disabled || !text.trim()}>
          {busy ? 'Sending…' : 'Send'}
        </button>
      </div>
      {showOptions && (
        <div className="composer-row options">
          <input
            className="ret"
            value={ret}
            onChange={(e) => setRet(e.target.value)}
            placeholder="Extra return files (workspace paths, comma-separated)"
            aria-label="Files to return as artifacts"
          />
          <label className="small check">
            <input type="checkbox" checked={forceNew} onChange={(e) => setForceNew(e.target.checked)} /> New session
          </label>
        </div>
      )}
      {err && <div className="error-text small">{err}</div>}
    </div>
  );
}

// 0.33.0: branch point before a user message = the previous user/assistant message (events in between may be
// cancellations or errors, which are not messages).
function previousMessageSeq(messages: ChatEvent[], seq: number): number {
  let best = 0;
  for (const m of messages) if ((m.type === 'user_message' || m.type === 'assistant_message') && m.sequence < seq && m.sequence > best) best = m.sequence;
  return best;
}
