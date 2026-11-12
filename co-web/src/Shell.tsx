// SPDX-License-Identifier: AGPL-3.0-or-later
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode, type SyntheticEvent } from 'react';
import { archiveScope, buildChatTree, readTreeOpen, treeTitle, writeTreeOpen } from './chatTree';
import { SORT_MODES, compareBy, readSortMode, ts as tsOf, writeSortMode, type SortMode } from './sidebarSort';
import { Resizer, useColumnWidth } from './Resizer';
import { ChatStatusIcon, ProjectRing, chatStatus, isUnread, useSeenVersion } from './status';
import { dbActivity, type ChatActivity, type CorporateWaiting } from './dbapi';
import { InboxView, useInbox } from './Inbox';
import { configureFast, fastConfigFromOwner } from './pubsub';
import { Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { refresh, signIn, signOut, type AuthState } from './auth';
import ChatView from './ChatView';
import { WebDevConfirmDialog, useWebDeviceRunner } from './WebDevice';
import { useExtDeviceRunner } from './extDevice';
import { PICKABLE_AGENTS, ENVIRONMENTS, HOST_ID, HOSTS, HOST_PREF_KEY, POLL_ACTIVITY_MS, POLL_HOST_MS, POLL_VERSION_MS, POLL_LISTS_MS, type AgentId, type EnvironmentId, type HostId } from './config';
import { pingSiteAgent, SITE_PING_MS } from './siteAgent';
import { categorize } from './errors';
import { useInterval, useLocalDevice } from './hooks';
import { ChatActionDialog, ChatMenu, type ChatAction } from './ChatActions';
import { ProjectActionDialog, ProjectMenu, projectActionsFor, type ProjectAction } from './ProjectActions';

// 0.45.0: chat menu actions by access (move/delete need owner; viewers get no menu)
function chatActionsFor(p: { shareRole?: string }, c: { shareRole?: string }): ChatAction[] {
  if (!p.shareRole) return ['rename', 'title', 'move', 'archive', 'delete'];
  if (p.shareRole === 'editor' || c.shareRole === 'editor') return ['rename', 'title', 'archive'];
  return [];
}
import { saveProjectMeta, updateChatMeta } from './workbench';
import { studyOn, studyPatch } from './studyMode';
import ImportView from './ImportView';
import { LimitsBar } from './Limits';
import Inspector from './Inspector';
import FamilyPanel from './FamilyPanel';
import GeoStatus from './GeoStatus';
import type { Chat, ChatEvent, LimitsFile, Mode, OwnerInfo, Project } from './types';

// 0.80.0: the study viewer (pdf.js) loads only when a document is opened
const StudyViewer = lazy(() => import('./StudyViewer'));
import { SAFE_ID, formatLocal } from './util';
import { autoHost, type HostPref } from './routing';
import { HostLight } from './HostLight';
import { aiSearch, chatRefs, openDocument, projectActivity, projectFreshness, type SearchHit } from './search';
import {
  createProject, discoverRoot, hostHealth, listChats, listProjects, loadHost, readLimits, readOwner,
  unregisteredBridgeProjects, type HostDirs, type Root,
} from './workbench';

import { useBridgeWatch } from './bridgeWatch';
import { ShareDialog } from './ShareDialog';
import MacOffer from './MacOffer';
import { openInspectorTab, useChatPanel, type InspectorTab } from './chatPanel';
import { dropShare, latestShare, type SharedItem } from './share';
import { memberNames } from './members';
import { corporate } from './corporate';

type Signed = Exclude<AuthState, { status: 'signed_out' }>;

export default function Shell({ auth }: { auth: Signed }) {
  useBridgeWatch();
  useWebDeviceRunner(auth.email); // 0.49.0: this browser as a device
  useExtDeviceRunner(auth.email); // 0.62.0: CO in the browser extension panel: the panel as a browser_* device
  const params = useParams();
  const navigate = useNavigate();
  const pathname = useLocation().pathname;
  const importRoute = pathname.endsWith('/import');
  const inboxRoute = pathname.endsWith('/inbox'); // 0.71.0: "Waiting for you"
  const env = params.env as EnvironmentId;
  const projectId = params.projectId;
  const chatId = params.chatId;
  const authOk = auth.status === 'signed_in';

  const [root, setRoot] = useState<Root | null>(null);
  const [hosts, setHosts] = useState<Record<string, HostDirs> | null>(null);
  const [owners, setOwners] = useState<Record<string, OwnerInfo | null>>({});
  const [limitsByHost, setLimitsByHost] = useState<Record<string, LimitsFile | null>>({});
  // S2 (0.98.0): the executor is chosen automatically (routing.autoHost) unless set by hand in Host and devices.
  const [hostPref, setHostPrefState] = useState<HostPref>(() => {
    const v = localStorage.getItem(HOST_PREF_KEY);
    return v && HOSTS.some((h) => h.id === v) ? (v as HostPref) : 'auto';
  });
  const setHostPref = (p: HostPref) => {
    localStorage.setItem(HOST_PREF_KEY, p);
    setHostPrefState(p);
  };
  const setHostId = (id: HostId) => setHostPref(id as HostPref);
  // 12.11: auto = Home while its Bridge is online (any device), else the Site
  const localDevice = useLocalDevice();
  const hostId = (hostPref === 'auto' ? autoHost(owners, localDevice) : hostPref) as HostId;
  const host = hosts?.[hostId] || null;
  const owner = owners[hostId] ?? null;
  const limits = limitsByHost[hostId] ?? null;
  const [fatal, setFatal] = useState<string | null>(null);
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [chats, setChats] = useState<Chat[] | null>(null);
  // 0.40.0: rerender after a tree node is collapsed/expanded (state lives in localStorage)
  const [, setTreeOpenTick] = useState(0);
  // 0.50.0: the active project's chat list collapses on a click on the project; reset when the project changes
  const [projectCollapsed, setProjectCollapsed] = useState(false);
  useEffect(() => {
    setProjectCollapsed(false);
  }, [projectId]);
  const [filter, setFilter] = useState('');
  // 0.83.0: sort order of projects and chats (sidebarSort.ts)
  const [sortMode, setSortModeState] = useState<SortMode>(readSortMode);
  const setSortMode = (m: SortMode) => {
    writeSortMode(m);
    setSortModeState(m);
  };
  const [leftOpen, setLeftOpen] = useState(false);
  const [rightOpen, setRightOpen] = useState(false);
  // 0.86.0: ☰ menu (panels, account) and requests to show an Inspector tab (opens the drawer on narrow screens)
  const [menuOpen, setMenuOpen] = useState(false);
  const { want: wantTab, familyUnread } = useChatPanel();
  useEffect(() => { if (wantTab) { setRightOpen(true); setLeftOpen(false); } }, [wantTab]);
  // 0.90.3: an open drawer (narrow screens) holds one history entry, so the phone's Back closes it;
  // closing it otherwise (✕, scrim) drops that entry again
  const drawerOpen = leftOpen || rightOpen;
  useEffect(() => {
    if (!drawerOpen || !window.matchMedia('(max-width: 1100px)').matches) return;
    if (!window.history.state?.coDrawer) window.history.pushState({ ...window.history.state, coDrawer: true }, '');
    const onPop = () => { setLeftOpen(false); setRightOpen(false); };
    window.addEventListener('popstate', onPop);
    return () => {
      window.removeEventListener('popstate', onPop);
      if (window.history.state?.coDrawer) window.history.back();
    };
  }, [drawerOpen]);
  // 0.90.0: a family push opens /e/<env>?family=1 -> the Family tab of the right panel
  const loc = useLocation();
  useEffect(() => {
    if (!new URLSearchParams(loc.search).has('family')) return;
    openInspectorTab('family');
    navigate(loc.pathname, { replace: true });
  }, [loc.search, loc.pathname, navigate]);
  const [showNewProject, setShowNewProject] = useState(false);
  const [reconnectErr, setReconnectErr] = useState<string | null>(null);
  const [chatAction, setChatAction] = useState<{ action: ChatAction; chat: Chat } | null>(null);
  const [projectAction, setProjectAction] = useState<{ action: ProjectAction; project: Project } | null>(null);
  // 0.31.0: activity of the env for the status icons (running/queued chats, chats waiting for an answer)
  // 0.52.0: activity also carries chats stopped while the corporate model was unavailable
  const [jobActivity, setJobActivity] = useState<{ running: ChatActivity[]; asking: string[]; corporateWaiting: CorporateWaiting[] } | null>(null);
  useSeenVersion();
  useColumnWidth('--left-w', 'co.leftW', 270, 180, 560);
  useColumnWidth('--right-w', 'co.rightW', 300, 220, 640);
  // 0.25.0: archive (projects and chats are hidden from the lists, not deleted)
  const [showArchivedChats, setShowArchivedChats] = useState(false);
  const [showArchivedProjects, setShowArchivedProjects] = useState(false);
  // CO Web 0.14.0: text/links/files shared from another app (service worker share target)
  const [share, setShare] = useState<SharedItem | null>(null);
  const handledShares = useRef(new Set<string>());

  // Pick up pending shares on mount, focus and when the tab becomes visible
  useEffect(() => {
    const check = () => {
      latestShare()
        .then((s) => {
          if (s && !handledShares.current.has(s.id)) setShare((prev) => (prev?.id === s.id ? prev : s));
        })
        .catch(() => undefined);
    };
    const onVisibility = () => {
      if (document.visibilityState === 'visible') check();
    };
    check();
    window.addEventListener('focus', check);
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      window.removeEventListener('focus', check);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  const envValid = ENVIRONMENTS.some((e) => e.id === env) && (auth.owner || env === auth.homeEnv);

  // Bootstrap: locate AI-Workbench and host transport.
  useEffect(() => {
    if (!authOk || root) return;
    (async () => {
      try {
        const r = await discoverRoot();
        const loaded: Record<string, HostDirs> = {};
        for (const h of HOSTS) loaded[h.id] = await loadHost(r, h.id);
        setRoot(r);
        setHosts(loaded);
        await readHosts(loaded);
      } catch (e) {
        setFatal(categorize(e).message);
      }
    })();
  }, [authOk, root]);

  async function readHosts(hs: Record<string, HostDirs>) {
    for (const [id, h] of Object.entries(hs)) {
      try {
        const o = await readOwner(h);
        setOwners((prev) => ({ ...prev, [id]: o }));
        const l = await readLimits(h).catch(() => null);
        setLimitsByHost((prev) => ({ ...prev, [id]: l }));
      } catch {
        /* keep last known */
      }
    }
  }
  const refreshOwner = useCallback(async () => {
    if (!hosts || !authOk) return;
    await readHosts(hosts);
  }, [hosts, authOk]); // eslint-disable-line react-hooks/exhaustive-deps
  useInterval(refreshOwner, POLL_HOST_MS, !!hosts && authOk);

  // The site agent is started on demand by the web server; keep it alive while it is selected.
  const pingSite = useCallback(() => {
    if ((hostId === 'site' || hostPref === 'auto') && authOk) void pingSiteAgent();
  }, [hostId, hostPref, authOk]);
  useEffect(pingSite, [pingSite]);
  // S2: in auto mode the site agent is kept alive too, so that the light and the failover see it
  useInterval(pingSite, SITE_PING_MS, (hostId === 'site' || hostPref === 'auto') && authOk);

  const refreshProjects = useCallback(async () => {
    if (!root || !authOk || !envValid) return;
    try {
      setProjects(await listProjects(root, env, owner));
    } catch (e) {
      console.warn(e);
    }
  }, [root, authOk, env, envValid, owner?.projects ? JSON.stringify(owner.projects) : '']); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    setProjects(null);
    void refreshProjects();
  }, [refreshProjects]);
  useInterval(refreshProjects, POLL_LISTS_MS, !!root && authOk);
  const refreshJobActivity = useCallback(async () => {
    if (!authOk) return;
    const a = await dbActivity(env);
    if (a) setJobActivity(a);
  }, [authOk, env]);
  useEffect(() => {
    void refreshJobActivity();
  }, [refreshJobActivity]);
  useInterval(refreshJobActivity, 10000, authOk);
  // 0.71.0: what agents wait on (sidebar badge + page); opening a chat marks its items read
  const inbox = useInbox(env, authOk);
  const inboxMark = inbox.mark;
  const inboxItems = inbox.items;
  useEffect(() => {
    if (!chatId || !inboxItems) return;
    const ids = inboxItems.filter((x) => x.chatId === chatId && x.kind !== 'memory' && x.unread).map((x) => x.id);
    if (ids.length) void inboxMark(ids, 'read');
  }, [chatId, inboxItems, inboxMark]);
  const runningByChat = new Map((jobActivity?.running || []).map((r) => [r.chatId, r.state] as const));
  const askingSet = new Set(jobActivity?.asking || []);
  const workingByProject = new Map<string, number>();
  for (const r of jobActivity?.running || []) workingByProject.set(r.projectId, (workingByProject.get(r.projectId) || 0) + 1);
  const statusOf = (c: Chat) =>
    chatStatus({
      running: runningByChat.get(c.chatId),
      asking: askingSet.has(c.chatId) && c.chatId !== chatId,
      unread: c.chatId !== chatId && isUnread(c.chatId, c.updatedUtc),
      scheduled: c.title.startsWith('⏰'),
    });
  const shownTitle = (t: string) => t.replace(/^⏰\s*/u, '');
  // 0.83.0: "waiting first" — inbox items (questions, confirmations, failed jobs) and asking chats, then unread answers
  const inboxByChat = new Set<string>();
  const inboxByProject = new Map<string, number>();
  for (const x of inboxItems || []) {
    const main = x.kind !== 'memory' && x.kind !== 'tools';
    if (main && x.chatId) inboxByChat.add(x.chatId);
    if (x.projectId) inboxByProject.set(x.projectId, (inboxByProject.get(x.projectId) || 0) + (main ? 10 : 1));
  }
  const chatWaitRank = (c: Chat) =>
    inboxByChat.has(c.chatId) || askingSet.has(c.chatId) ? 2 : c.chatId !== chatId && isUnread(c.chatId, c.updatedUtc) ? 1 : 0;

  // Projects are listed by their newest chat activity (chat.json changes on every message).
  const [activity, setActivity] = useState<Record<string, string>>({});
  const projectKey = (projects || []).map((p) => p.folderId).join(',');
  const refreshActivity = useCallback(async () => {
    if (!projects?.length || !authOk) return;
    try {
      setActivity(projectActivity(await chatRefs(projects)));
    } catch (e) {
      console.warn('activity', e);
    }
  }, [projectKey, authOk]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => void refreshActivity(), [refreshActivity]);
  useInterval(refreshActivity, POLL_ACTIVITY_MS, !!projects?.length && authOk);

  // A new deployment changes the hashed main bundle in index.html. CO Desktop keeps one page open for days,
  // so the page checks and offers a reload instead of running an old version.
  const [updateReady, setUpdateReady] = useState(false);
  const checkVersion = useCallback(async () => {
    const mine = document.querySelector<HTMLScriptElement>('script[src*="/assets/main-"]')?.getAttribute('src');
    if (!mine) return;
    try {
      const html = await (await fetch('/', { cache: 'no-store' })).text();
      const live = /src="([^"]*\/assets\/main-[^"]+\.js)"/.exec(html)?.[1];
      if (live && live !== mine) setUpdateReady(true);
    } catch {
      /* offline: try again later */
    }
  }, []);
  useInterval(checkVersion, POLL_VERSION_MS, !updateReady);

  const [searchStep, setSearchStep] = useState<string | null>(null);
  const [searchHits, setSearchHits] = useState<SearchHit[] | null>(null);
  const [searchErr, setSearchErr] = useState<string | null>(null);
  async function runSearch() {
    const text = filter.trim();
    if (!text || !projects || searchStep) return;
    setSearchErr(null);
    setSearchHits(null);
    setSearchStep('Starting…');
    try {
      setSearchHits(await aiSearch(projects, text, setSearchStep));
    } catch (e) {
      setSearchErr(categorize(e).message);
    } finally {
      setSearchStep(null);
    }
  }
  function clearSearch() {
    setFilter('');
    setSearchHits(null);
    setSearchErr(null);
  }

  const project = useMemo(() => projects?.find((p) => p.projectId === projectId) || null, [projects, projectId]);

  const refreshChats = useCallback(async () => {
    if (!project || !authOk) return;
    try {
      setChats(await listChats(project, env)); // 0.48.0: preview-aware
    } catch (e) {
      console.warn(e);
    }
  }, [project, authOk, env]);
  useEffect(() => {
    setChats(null);
    void refreshChats();
  }, [project?.folderId]); // eslint-disable-line react-hooks/exhaustive-deps
  useInterval(refreshChats, POLL_LISTS_MS, !!project && authOk);

  const chat = useMemo(() => chats?.find((c) => c.chatId === chatId) || null, [chats, chatId]);

  // 0.80.0: study viewer - "?study=<path relative to AI-Workbench>" shows the document left of its chat. On a project
  // URL the chat of that document (chat.studyDoc) opens if there is one; otherwise a new chat is made by the first question.
  const search = useLocation().search;
  const studyPath = useMemo(() => new URLSearchParams(search).get('study'), [search]);
  const studyChat = useMemo(
    () => (studyPath && !chatId ? (chats || []).find((c) => c.studyDoc === studyPath && !c.archived) || null : null),
    [studyPath, chatId, chats],
  );
  const [studyEvents, setStudyEvents] = useState<ChatEvent[] | null>(null);
  const [studyAsk, setStudyAsk] = useState<{ text: string; files: File[]; nonce: number } | null>(null);
  const [studyCanAsk, setStudyCanAsk] = useState(false);
  useEffect(() => {
    setStudyEvents(null);
    setStudyAsk(null);
  }, [studyPath, chatId]);
  const studyWrap = (node: ReactNode) => {
    if (!studyPath || !root) return node;
    return (
      <div className="study-split">
        <Suspense fallback={<div className="study"><div className="empty"><p className="muted">Loading the viewer…</p></div></div>}>
          <StudyViewer
            key={studyPath} // 0.80.1: another document starts with a clean viewer (outline, hits, selection)
            root={root}
            path={studyPath}
            events={studyEvents}
            canAsk={!!authOk && studyCanAsk}
            onAsk={(text, files) => setStudyAsk({ text, files, nonce: Date.now() })}
            onOpenAnswer={(id) => {
              const el = document.getElementById(`ev-${id}`);
              if (!el) return;
              el.scrollIntoView({ behavior: 'smooth', block: 'start' });
              el.classList.add('flash');
              setTimeout(() => el.classList.remove('flash'), 1600);
            }}
            onClose={() => navigate(pathname, { replace: true })}
          />
        </Suspense>
        <div className="study-chat">{node}</div>
      </div>
    );
  };

  const capabilities = owner?.capabilities || [];
  // Pub/Sub fast transport exists only for the home Bridge.
  const homeOwner = owners[HOST_ID] ?? null;
  useEffect(() => configureFast(fastConfigFromOwner(homeOwner, env)), [homeOwner, env]);
  const health = hostHealth(owner);

  if (!envValid) return <Navigate to={`/e/${auth.homeEnv}`} replace />;

  const envName = ENVIRONMENTS.find((e) => e.id === env)?.displayName || env;
  const f = filter.trim().toLowerCase();
  const projectCmp = compareBy(sortMode);
  const projectSortKey = (p: Project) => ({
    id: p.projectId,
    title: p.title,
    created: tsOf(p.createdUtc),
    fresh: tsOf(projectFreshness(p, activity)),
    waiting: inboxByProject.get(p.projectId) || 0,
  });
  const visibleProjects = (projects || [])
    .filter((p) => !p.family) // 0.90.0: the family room lives in the right-panel tab
    .filter((p) => !f || p.title.toLowerCase().includes(f) || p.projectId.toLowerCase().includes(f))
    .map((p) => ({ p, k: projectSortKey(p) }))
    .sort((a, b) => projectCmp(a.k, b.k))
    .map((x) => x.p);
  const matchingChats = (chats || []).filter((c) => !f || c.title.toLowerCase().includes(f));
  const visibleChats = matchingChats.filter((c) => !c.archived);
  const archivedChats = matchingChats.filter((c) => c.archived);
  // 0.40.0: child chats under their parent (chatTree.ts)
  const treeRows = buildChatTree(visibleChats, {
    isWorking: (id) => runningByChat.has(id),
    selectedChatId: chatId,
    isOpen: readTreeOpen,
    sort: sortMode,
    waitRank: chatWaitRank,
  });
  // 0.70.0: the archive keeps the chat tree too (folders and subchats), collapsed by default
  const archivedRows = showArchivedChats
    ? buildChatTree(archivedChats, { isWorking: () => false, selectedChatId: chatId, isOpen: readTreeOpen, sort: sortMode === 'waiting' ? 'activity' : sortMode })
    : [];
  const toggleTree = (e: SyntheticEvent, id: string, open: boolean) => {
    e.stopPropagation();
    e.preventDefault();
    writeTreeOpen(id, !open);
    setTreeOpenTick((n) => n + 1);
  };
  const onToggleKey = (e: KeyboardEvent, id: string, open: boolean) => {
    if (e.key === 'Enter' || e.key === ' ') toggleTree(e, id, open);
  };
  const archivedProjects = visibleProjects.filter((p) => p.archived);

  async function pickProjectAction(action: ProjectAction, p: Project) {
    // 0.78.0: study mode for new chats of the project
    if (action === 'study') {
      try {
        const updated = await saveProjectMeta(p, { studyDefault: !p.studyDefault });
        setProjects((prev) => (prev || []).map((x) => (x.folderId === updated.folderId ? updated : x)));
      } catch (e) {
        alert(categorize(e).message);
      }
      return;
    }
    if (action !== 'archive') {
      setProjectAction({ action, project: p });
      return;
    }
    try {
      const updated = await saveProjectMeta(p, { archived: !p.archived });
      setProjects((prev) => (prev || []).map((x) => (x.folderId === updated.folderId ? updated : x)));
    } catch (e) {
      alert(categorize(e).message);
    }
  }

  async function pickChatAction(action: ChatAction, c: Chat) {
    // 0.78.0: study mode of one chat (the switch is logged in the chat)
    if (action === 'study') {
      const p = (projects || []).find((x) => x.projectId === c.projectId) || { studyDefault: false };
      try {
        const updated = await updateChatMeta(c, studyPatch(c, p, auth.email));
        setChats((prev) => (prev || []).map((x) => (x.folderId === updated.folderId ? updated : x)));
      } catch (e) {
        alert(categorize(e).message);
      }
      return;
    }
    if (action !== 'archive') {
      setChatAction({ action, chat: c });
      return;
    }
    // 0.70.0: archive takes the subchats along; restore also brings back archived parents, so the tree stays whole
    const scope = archiveScope(chats || [], c.chatId, !c.archived);
    const changed = new Map<string, Chat>();
    let failed = 0;
    // The chat itself goes first (archiveScope order); when it fails, its relatives are left alone
    for (const x of scope.length ? scope : [c]) {
      try {
        const updated = await updateChatMeta(x, { archived: !c.archived });
        changed.set(updated.folderId, updated);
      } catch (e) {
        if (x.chatId === c.chatId) {
          alert(categorize(e).message);
          return;
        }
        failed++;
      }
    }
    setChats((prev) => (prev || []).map((x) => changed.get(x.folderId) ?? x));
    if (failed) alert(`${failed} related chat(s) could not be changed.`);
  }

  // Projects unknown to the Bridge config run in Chat mode when the environment allows it.
  const projectModes: Mode[] =
    (project && owner?.projects?.[project.projectId]?.allowedModes) ||
    (owner?.chatProjectEnvironments?.[env] ? (capabilities.includes('chat-cowork') ? ['chat', 'cowork'] : ['chat']) : ['cowork']);
  const pendingBridge = projects ? unregisteredBridgeProjects(owner, env, projects) : [];

  const loadShareChats = async (pid: string) => {
    const p = projects?.find((x) => x.projectId === pid);
    if (!p) return [];
    const list = await listChats(p, env); // 0.48.0: preview-aware
    const ts = (c: Chat) => Date.parse(c.updatedUtc) || 0;
    return [...list].sort((a, b) => ts(b) - ts(a)).map((c) => ({ chatId: c.chatId, title: c.title }));
  };

  const openShare = async (pid: string, chatIdSel: string | null) => {
    if (!share) return;
    const p = projects?.find((x) => x.projectId === pid);
    if (!p) throw new Error('Project not found');
    handledShares.current.add(share.id);
    // 0.17.0: "New chat" opens the project's draft; the chat is created with the first message.
    navigate(chatIdSel ? `/e/${env}/p/${pid}/c/${chatIdSel}` : `/e/${env}/p/${pid}`, { state: { shareId: share.id } });
    setShare(null);
  };

  const cancelShare = () => {
    if (!share) return;
    handledShares.current.add(share.id);
    dropShare(share.id).catch(() => undefined);
    setShare(null);
  };

  return (
    <div className={`shell env-${env}`}>
      <WebDevConfirmDialog />
      <MacOffer />
      {share && projects && (
        <ShareDialog
          item={share}
          projects={[...projects]
            .sort((a, b) => projectFreshness(b, activity).localeCompare(projectFreshness(a, activity)))
            .map((p) => ({ projectId: p.projectId, title: p.title }))}
          loadChats={loadShareChats}
          onOpen={openShare}
          onCancel={cancelShare}
        />
      )}
      {auth.status === 'expired' && (
        <div className="banner" role="alert">
          <span>Google Drive session expired. Running jobs continue on the home agent.</span>
          <button
            className="btn small"
            onClick={async () => {
              setReconnectErr(null);
              try {
                if (!(await refresh())) await signIn();
              } catch (e) {
                setReconnectErr((e as Error).message);
              }
            }}
          >
            Reconnect Google Drive
          </button>
          {reconnectErr && <span className="error-text">{reconnectErr}</span>}
        </div>
      )}

      {updateReady && (
        <div className="banner" role="status">
          <span>A new version of CO is available.</span>
          <button className="btn small" onClick={() => window.location.reload()}>Reload</button>
        </div>
      )}

      <header className="topbar">
        <div className="app-menu">
          <button className="icon-btn" aria-label="Menu" data-email={auth.email} aria-expanded={menuOpen} title="Menu: panels, settings, account" onClick={() => setMenuOpen((v) => !v)}>☰</button>
          {menuOpen && (
            <>
              <div className="app-menu-scrim" onClick={() => setMenuOpen(false)} />
              <div className="app-menu-list" role="menu" onClick={() => setMenuOpen(false)}>
                <button role="menuitem" className="only-mobile" onClick={() => { setRightOpen(false); setLeftOpen((v) => !v); }}>📂 Projects and chats</button>
                {([
                  ['family', `👪 Family${familyUnread ? ` (${familyUnread} new)` : ''}`, `Family chat of ${memberNames()}`],
                  ['chat', '⚙ Chat settings', 'Permissions, device, study mode, protocol, rename, move, delete'],
                  ['artifacts', '📁 Files and folders', 'Files of this chat and the context folders of the project'],
                  ['project', '🗂 Project', 'Instructions, memory, schedules, sharing'],
                  ['session', '🧾 Session', 'What the agent of this chat gets and spends'],
                  ['host', '🖥 Host and devices', 'Bridge, agents, CO Desktop, browser devices, notifications'],
                ] as [InspectorTab, string, string][]).map(([t, label, hint]) => (
                  <button key={t} role="menuitem" title={hint} onClick={() => openInspectorTab(t)}>{label}</button>
                ))}
                <div className="app-menu-sep" />
                <div className="app-menu-who small muted">{envName} · {auth.email}</div>
                <button role="menuitem" onClick={signOut}>Sign out</button>
              </div>
            </>
          )}
        </div>
        <img className="brand-logo" src="/icon.svg" alt="" />
        <div className="brand-sm">CO</div>
        <div className="env-badge" title="Active environment">
          <span className="dot" />
          {envName}
        </div>
        <HostLight owners={owners} env={env} pref={hostPref} effective={hostId} onAdvanced={() => openInspectorTab('host')} />
        <div className="spacer" />
        <GeoStatus authOk={authOk} me={auth.homeEnv} />
        {familyUnread > 0 && (
          <button type="button" className="fam-top" title="New messages in the family chat" onClick={() => openInspectorTab('family')}>
            👪 <span className="fam-badge">{familyUnread > 99 ? '99+' : familyUnread}</span>
          </button>
        )}
        <LimitsBar limits={limits} />
      </header>

      {fatal ? (
        <div className="fatal">
          <h2>Cannot open AI-Workbench</h2>
          <p>{fatal}</p>
        </div>
      ) : (
        <div className="panes">
          <Resizer cssVar="--left-w" storageKey="co.leftW" side="left" def={270} min={180} max={560} />
          <Resizer cssVar="--right-w" storageKey="co.rightW" side="right" def={300} min={220} max={640} />
          <aside className={`left ${leftOpen ? 'open' : ''}`}>
            {auth.owner && (
              <div className="env-switch" role="tablist" aria-label="Environment">
                {ENVIRONMENTS.map((e) => (
                  <button
                    key={e.id}
                    role="tab"
                    aria-selected={e.id === env}
                    className={e.id === env ? 'active' : ''}
                    onClick={() => navigate(`/e/${e.id}`)}
                  >
                    {e.displayName}
                  </button>
                ))}
              </div>
            )}
            <div className="search-row">
              <input
                className="search"
                placeholder="Filter, or Enter for AI search"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') void runSearch();
                  if (e.key === 'Escape') clearSearch();
                }}
                aria-label="Filter projects and chats; Enter searches by meaning"
              />
              <button
                className="btn tiny"
                disabled={!filter.trim() || !projects || !!searchStep}
                onClick={() => void runSearch()}
                title="AI search: finds chats and projects by meaning, in any language, including message text"
              >
                AI
              </button>
            </div>
            {(searchStep || searchHits || searchErr) && (
              <div className="search-results">
                <div className="section-head">
                  <span>Search</span>
                  <button className="btn tiny" onClick={clearSearch} title="Close search (Esc)">×</button>
                </div>
                {searchStep && <div className="muted small pad">{searchStep}</div>}
                {searchErr && <div className="error-text small pad">{searchErr}</div>}
                {searchHits?.length === 0 && <div className="muted small pad">Nothing found.</div>}
                <ul className="nav-list">
                  {searchHits?.map((h) => (
                    <li key={`${h.projectId}/${h.chatId || ''}/${h.docPath || ''}`}>
                      <button
                        className={h.docPath ? '' : h.chatId ? (h.chatId === chatId ? 'active' : '') : h.projectId === projectId && !chatId ? 'active' : ''}
                        title={h.docPath ? `${h.docPath}\n\n${h.why}` : h.why}
                        onClick={() => {
                          if (h.docPath) {
                            openDocument(projects || [], h).catch((e) => setSearchErr(e instanceof Error ? e.message : String(e)));
                            return;
                          }
                          navigate(h.chatId ? `/e/${env}/p/${h.projectId}/c/${h.chatId}` : `/e/${env}/p/${h.projectId}`);
                          setLeftOpen(false);
                        }}
                      >
                        <span className="nav-title">{h.docPath ? `📄 ${h.title}` : h.chatId ? h.title : `📁 ${h.title}`}</span>
                        <span className="nav-date">{h.chatId || h.docPath ? `${h.projectTitle} · ` : ''}{h.activityUtc ? formatLocal(h.activityUtc) : ''}</span>
                        {h.why && <span className="search-why">{h.why}</span>}
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <button
              className={`inbox-link ${inboxRoute ? 'active' : ''}`}
              onClick={() => { navigate(`/e/${env}/inbox`); setLeftOpen(false); void inbox.refresh(); }}
              title="Questions, captcha and browser confirmations, failed jobs and memory candidates waiting for you"
            >
              <span>Waiting for you</span>
              {!!inbox.items?.length && <span className={`badge ${inbox.unread ? 'unread' : ''}`}>{inbox.unread || inbox.items.length}</span>}
            </button>

            <div className="section-head">
              <span>Projects</span>
              <span>
                <select
                  className="sort-select"
                  value={sortMode}
                  onChange={(e) => setSortMode(e.target.value as SortMode)}
                  aria-label="Sort projects and chats"
                  title={`Sort projects and chats: ${SORT_MODES.find((m) => m.id === sortMode)?.title}`}
                >
                  {SORT_MODES.map((m) => (
                    <option key={m.id} value={m.id} title={m.title}>{m.label}</option>
                  ))}
                </select>{' '}
                <button className="btn tiny" onClick={() => navigate(`/e/${env}/import`)} disabled={!root} title="Import chats from claude.ai or ChatGPT">Import…</button>{' '}
                <button className="btn tiny" onClick={() => setShowNewProject(true)} disabled={!root}>+ New</button>
              </span>
            </div>
            {projects === null && <div className="muted small pad">Loading…</div>}
            {projects?.length === 0 && <div className="muted small pad">No projects in {envName}.</div>}
            <ul className="nav-list">
              {visibleProjects.filter((p) => !p.archived || p.projectId === projectId).map((p) => (
                <li key={p.folderId}>
                  <div className="chat-li proj-row">
                  <button
                    className={p.projectId === projectId ? 'active' : ''}
                    onClick={() => {
                      // 0.50.0: on the active project's draft (no chat open) a row click toggles its chat list;
                      // otherwise it opens the project draft expanded, so a new chat can still be started from a chat
                      if (p.projectId === projectId && !chatId) {
                        setProjectCollapsed((v) => !v);
                      } else {
                        setProjectCollapsed(false);
                        navigate(`/e/${env}/p/${p.projectId}`);
                      }
                    }}
                  >
                    <span
                      className="proj-toggle"
                      role="button"
                      tabIndex={0}
                      aria-expanded={p.projectId === projectId && !projectCollapsed}
                      aria-label={p.projectId === projectId && !projectCollapsed ? 'Collapse chats' : 'Expand chats'}
                      onClick={(e) => {
                        // 0.50.0: the chevron always toggles the active project; a non-active project opens expanded
                        e.stopPropagation();
                        e.preventDefault();
                        if (p.projectId === projectId) {
                          setProjectCollapsed((v) => !v);
                        } else {
                          setProjectCollapsed(false);
                          navigate(`/e/${env}/p/${p.projectId}`);
                        }
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.stopPropagation();
                          e.preventDefault();
                          if (p.projectId === projectId) {
                            setProjectCollapsed((v) => !v);
                          } else {
                            setProjectCollapsed(false);
                            navigate(`/e/${env}/p/${p.projectId}`);
                          }
                        }
                      }}
                      onKeyUp={(e) => {
                        // Keep Space from activating the row button on keyup
                        if (e.key === ' ') {
                          e.stopPropagation();
                          e.preventDefault();
                        }
                      }}
                    >
                      {p.projectId === projectId && !projectCollapsed ? '\u25BE' : '\u25B8'}
                    </span>
                    <span className="nav-title">{p.title}</span>
                    <ProjectRing count={workingByProject.get(p.projectId) || 0} />
                    {p.legacy && <span className="tag">legacy</span>}
                    {p.archived && <span className="tag">archived</span>}
                    {p.kind && p.kind !== 'work' && <span className="tag">{p.kind}</span>}
                    {p.corporate && <span className="tag" title={`Corporate (${corporate().title} data)`}>corp</span>}
                    {/* 0.45.0: projects shared with me */}
                    {p.shareRole && (() => { const o = ENVIRONMENTS.find(e => e.id === p.environmentId)?.displayName ?? p.environmentId; return <span className="tag" title={`Shared by ${o}: ${p.shareRole === 'editor' ? 'can edit' : 'can view'}${p.shareVia === 'chat' ? ', selected chats only' : ''}`}>{'\u{1F465} ' + o}</span>; })()}
                  </button>
                  {/* 0.58.0: project menu; "as a chat with subchats" only without chat trees (known for the open project) */}
                  <ProjectMenu
                    archived={p.archived}
                    study={!!p.studyDefault}
                    actions={projectActionsFor(p, p.projectId === projectId && !!chats?.some((c) => c.parent))}
                    onPick={(action) => void pickProjectAction(action, p)}
                  />
                  </div>
                  {p.projectId === projectId && !projectCollapsed && (
                    <div className="chat-list">
                      {chats === null && <div className="muted small pad">Loading chats…</div>}
                      <ul>
                        {treeRows.map(({ chat: c, depth, hasChildren, open }) => (
                          <li
                            key={c.folderId}
                            className={'chat-li tree-d' + (depth > 0 ? ' tree-child' : '')}
                            style={{ paddingLeft: depth * 14, ['--tree-x' as string]: depth * 14 - 8 + 'px' }}
                          >
                            <button
                              className={c.chatId === chatId ? 'active' : ''}
                              title={c.summary || undefined}
                              onClick={() => {
                                // 0.50.0: a click on the active chat toggles its child chats; opening a parent expands them
                                if (c.chatId === chatId && hasChildren) {
                                  writeTreeOpen(c.chatId, !open);
                                  setTreeOpenTick((n) => n + 1);
                                  return;
                                }
                                if (hasChildren && !open) {
                                  writeTreeOpen(c.chatId, true);
                                  setTreeOpenTick((n) => n + 1);
                                }
                                navigate(`/e/${env}/p/${p.projectId}/c/${c.chatId}`);
                                setLeftOpen(false);
                              }}
                            >
                              {hasChildren ? (
                                <span
                                  role="button"
                                  tabIndex={0}
                                  className="tree-toggle"
                                  aria-expanded={open}
                                  aria-label={open ? 'Collapse child chats' : 'Expand child chats'}
                                  onClick={(e) => toggleTree(e, c.chatId, open)}
                                  onKeyDown={(e) => onToggleKey(e, c.chatId, open)}
                                  onKeyUp={(e) => {
                                    // Keep Space from activating the row button on keyup
                                    if (e.key === ' ') {
                                      e.stopPropagation();
                                      e.preventDefault();
                                    }
                                  }}
                                >
                                  {open ? '\u{1F4C2}' : '\u{1F4C1}'}
                                </span>
                              ) : (
                                <span className="tree-spacer" aria-hidden="true" />
                              )}
                              <ChatStatusIcon status={statusOf(c)} />
                              <span className="nav-title">{shownTitle(treeTitle(c.title || '', depth))}</span>
                              <span className="nav-date" title={sortMode === 'created' ? 'Created' : 'Last activity'}>{formatLocal(sortMode === 'created' ? c.createdUtc : c.updatedUtc)}</span>
                            </button>
                            <ChatMenu study={studyOn(c, p)} actions={chatActionsFor(p, c)} onPick={(action) => void pickChatAction(action, c)} />
                          </li>
                        ))}
                      </ul>
                      {archivedChats.length > 0 && (
                        <>
                          <button className="btn tiny ghost" onClick={() => setShowArchivedChats((v) => !v)} aria-expanded={showArchivedChats}>
                            {showArchivedChats ? '▾' : '▸'} Archived ({archivedChats.length})
                          </button>
                          {showArchivedChats && (
                            <ul className="archived-list">
                              {archivedRows.map(({ chat: c, depth, hasChildren, open }) => (
                                <li
                                  key={c.folderId}
                                  className={'chat-li tree-d' + (depth > 0 ? ' tree-child' : '')}
                                  style={{ paddingLeft: depth * 14, ['--tree-x' as string]: depth * 14 - 8 + 'px' }}
                                >
                                  <button
                                    className={c.chatId === chatId ? 'active' : ''}
                                    title={c.summary || undefined}
                                    onClick={() => {
                                      if (c.chatId === chatId && hasChildren) {
                                        writeTreeOpen(c.chatId, !open);
                                        setTreeOpenTick((n) => n + 1);
                                        return;
                                      }
                                      navigate(`/e/${env}/p/${p.projectId}/c/${c.chatId}`);
                                      setLeftOpen(false);
                                    }}
                                  >
                                    {hasChildren ? (
                                      <span
                                        role="button"
                                        tabIndex={0}
                                        className="tree-toggle"
                                        aria-expanded={open}
                                        aria-label={open ? 'Collapse child chats' : 'Expand child chats'}
                                        onClick={(e) => toggleTree(e, c.chatId, open)}
                                        onKeyDown={(e) => onToggleKey(e, c.chatId, open)}
                                        onKeyUp={(e) => {
                                          if (e.key === ' ') {
                                            e.stopPropagation();
                                            e.preventDefault();
                                          }
                                        }}
                                      >
                                        {open ? '\u{1F4C2}' : '\u{1F4C1}'}
                                      </span>
                                    ) : (
                                      <span className="tree-spacer" aria-hidden="true" />
                                    )}
                                    <span className="nav-title">{shownTitle(treeTitle(c.title || '', depth))}</span>
                                    <span className="nav-date">{formatLocal(c.updatedUtc)}</span>
                                  </button>
                                  <ChatMenu archived study={studyOn(c, p)} actions={chatActionsFor(p, c)} onPick={(action) => void pickChatAction(action, c)} />
                                </li>
                              ))}
                            </ul>
                          )}
                        </>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ul>
            {archivedProjects.length > 0 && (
              <div className="archived-projects">
                <button className="btn tiny ghost" onClick={() => setShowArchivedProjects((v) => !v)} aria-expanded={showArchivedProjects}>
                  {showArchivedProjects ? '▾' : '▸'} Archived projects ({archivedProjects.length})
                </button>
                {showArchivedProjects && (
                  <ul className="nav-list archived-list">
                    {archivedProjects.filter((p) => p.projectId !== projectId).map((p) => (
                      <li key={p.folderId} className="chat-li">
                        <button onClick={() => navigate(`/e/${env}/p/${p.projectId}`)}>
                          <span className="nav-title">{p.title}</span>
                        </button>
                        <ProjectMenu archived actions={projectActionsFor(p)} onPick={(action) => void pickProjectAction(action, p)} />
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
            {pendingBridge.length > 0 && (
              <div className="pending-bridge">
                <div className="section-head"><span>On home agent, not in Drive</span></div>
                {pendingBridge.map((id) => (
                  <button
                    key={id}
                    className="btn tiny wide"
                    title="Create project.json in AI-Workbench for this Bridge project"
                    onClick={async () => {
                      if (!root) return;
                      const p = await createProject(root, { projectId: id, environmentId: env, title: id, description: '', defaultAgentId: 'claude' });
                      setProjects((prev) => [p, ...(prev || [])]);
                      navigate(`/e/${env}/p/${p.projectId}`);
                    }}
                  >
                    + {id}
                  </button>
                ))}
              </div>
            )}
          </aside>

          <main className="center">
            {!root || !host ? (
              <div className="empty">Connecting to AI-Workbench…</div>
            ) : inboxRoute ? (
              <InboxView env={env} inbox={inbox} />
            ) : importRoute ? (
              <ImportView
                root={root}
                env={env}
                projects={projects || []}
                onFinished={async (pid) => {
                  await refreshProjects();
                  navigate(pid ? `/e/${env}/p/${pid}` : `/e/${env}`);
                }}
              />
            ) : studyChat ? (
              <Navigate to={`/e/${env}/p/${studyChat.projectId}/c/${studyChat.chatId}${search}`} replace />
            ) : chat && project ? studyWrap(
              <ChatView
                key={chat.folderId}
                studyDoc={studyPath || undefined}
                studyAsk={studyPath ? studyAsk : null}
                onEvents={studyPath ? setStudyEvents : undefined}
                onCanAsk={studyPath ? setStudyCanAsk : undefined}
                root={root}
                host={host}
                hosts={hosts!}
                owners={owners}
                hostAuto={hostPref === 'auto'}
                env={env}
                project={project}
                chat={chat}
                authOk={authOk}
                author={auth.email}
                capabilities={capabilities}
                hostHealth={health}
                owner={owner}
                limits={limits}
                onChatUpdated={refreshChats}
                onChatAction={(action) => setChatAction({ action, chat })}
                onChatCreated={(c) => {
                  // 0.33.0: branch / follow-up opened from this chat
                  setChats((prev) => [c, ...(prev || []).filter((x) => x.chatId !== c.chatId)]);
                  navigate(`/e/${env}/p/${c.projectId}/c/${c.chatId}`);
                }}
                onProjectChanged={(p) => setProjects((prev) => (prev || []).map((x) => (x.folderId === p.folderId ? p : x)))}
              />
            ) : project && (!studyPath || chats) ? studyWrap(
              // 0.17.0: selecting a project opens a new chat at once; it is saved with the first message.
              <ChatView
                key={`draft:${project.projectId}${studyPath ? ':study' : ''}`}
                studyDoc={studyPath || undefined}
                studyAsk={studyPath ? studyAsk : null}
                onEvents={studyPath ? setStudyEvents : undefined}
                onCanAsk={studyPath ? setStudyCanAsk : undefined}
                onHostChange={(id) => { if (hostPref !== 'auto' && HOSTS.some((h) => h.id === id)) setHostId(id as HostId); }}
                root={root}
                host={host}
                hosts={hosts!}
                owners={owners}
                hostAuto={hostPref === 'auto'}
                env={env}
                project={project}
                chat={{
                  chatId: '', projectId: project.projectId, title: 'New chat', rootBranchId: 'BR-root',
                  createdUtc: '', updatedUtc: '', folderId: '', defaultMode: projectModes[0], draft: true,
                  ...(studyPath ? { study: true } : {}),
                }}
                authOk={authOk}
                author={auth.email}
                capabilities={capabilities}
                hostHealth={health}
                owner={owner}
                limits={limits}
                onChatUpdated={refreshChats}
                onChatAction={() => undefined}
                onProjectChanged={(p) => setProjects((prev) => (prev || []).map((x) => (x.folderId === p.folderId ? p : x)))}
                onChatCreated={(c) => {
                  setChats((prev) => [c, ...(prev || []).filter((x) => x.chatId !== c.chatId)]);
                  setStudyAsk(null);
                  navigate(`/e/${env}/p/${project.projectId}/c/${c.chatId}${studyPath ? search : ''}`);
                }}
              />
            ) : (
              <div className="empty">
                <h2>{envName}</h2>
                <p className="muted">Select a project on the left or create a new one.</p>
              </div>
            )}
          </main>

          <FamilyPanel
            root={root}
            host={host}
            hosts={hosts}
            owners={owners}
            env={auth.homeEnv}
            author={auth.email}
            owner={owner}
            capabilities={capabilities}
            hostHealth={health}
            limits={limits}
            authOk={authOk}
          />
          <aside className={`right ${rightOpen ? 'open' : ''}`}>
            <Inspector
              chats={chats}
              env={env}
              limits={limits}
              project={project}
              chat={chat}
              root={root}
              owner={owner}
              health={health}
              authOk={authOk}
              host={host}
              hostPref={hostPref}
              onHostPref={setHostPref}
              onProjectChanged={(p) => setProjects((prev) => (prev || []).map((x) => (x.folderId === p.folderId ? p : x)))}
              onClose={() => setRightOpen(false)}
            />
          </aside>
          {(leftOpen || rightOpen) && <div className="scrim only-mobile drawer-scrim" onClick={() => { setLeftOpen(false); setRightOpen(false); }} />}
        </div>
      )}

      {chatAction && root && hosts && (
        <ChatActionDialog
          action={chatAction.action}
          chat={chatAction.chat}
          root={root}
          hosts={hosts}
          projects={projects || []}
          onClose={() => setChatAction(null)}
          onDone={({ chat: updated, deleted }) => {
            const orig = chatAction.chat;
            setChatAction(null);
            if (deleted) {
              setChats((prev) => (prev || []).filter((c) => c.folderId !== orig.folderId));
              if (orig.chatId === chatId) navigate(`/e/${env}/p/${orig.projectId}`);
            } else if (updated) {
              if (updated.projectId !== orig.projectId) {
                setChats((prev) => (prev || []).filter((c) => c.folderId !== orig.folderId));
                navigate(`/e/${env}/p/${updated.projectId}/c/${updated.chatId}`);
              } else {
                setChats((prev) => (prev || []).map((c) => (c.folderId === updated.folderId ? updated : c)));
              }
            }
          }}
        />
      )}

      {projectAction && root && hosts && (
        <ProjectActionDialog
          action={projectAction.action}
          project={projectAction.project}
          root={root}
          hosts={hosts}
          projects={projects || []}
          onClose={() => setProjectAction(null)}
          onDone={({ project: updated, deleted, movedTo, hub }) => {
            const orig = projectAction.project;
            setProjectAction(null);
            if (deleted) setProjects((prev) => (prev || []).filter((x) => x.folderId !== orig.folderId));
            else if (updated) setProjects((prev) => (prev || []).map((x) => (x.folderId === updated.folderId ? updated : x)));
            if (movedTo) {
              setProjectCollapsed(false);
              if (movedTo.projectId === projectId) void refreshChats();
              navigate(hub ? `/e/${env}/p/${movedTo.projectId}/c/${hub.chatId}` : `/e/${env}/p/${movedTo.projectId}`);
            } else if (deleted && orig.projectId === projectId) navigate(`/e/${env}`);
            void refreshProjects();
          }}
        />
      )}

      {showNewProject && root && (
        <NewProjectDialog
          env={env}
          onClose={() => setShowNewProject(false)}
          onCreate={async (input) => {
            const p = await createProject(root, { ...input, environmentId: env });
            setProjects((prev) => [p, ...(prev || [])]);
            setShowNewProject(false);
            navigate(`/e/${env}/p/${p.projectId}`);
          }}
        />
      )}
    </div>
  );
}

function NewProjectDialog({
  env, onClose, onCreate,
}: {
  env: EnvironmentId;
  onClose: () => void;
  onCreate: (i: { projectId: string; title: string; description: string; defaultAgentId: AgentId; studyDefault?: boolean }) => Promise<void>;
}) {
  const [projectId, setProjectId] = useState('');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [agent, setAgent] = useState<AgentId>('claude');
  const [study, setStudy] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const idOk = SAFE_ID.test(projectId);
  return (
    <div className="modal-back" onClick={onClose}>
      <form
        className="modal"
        onClick={(e) => e.stopPropagation()}
        onSubmit={async (e) => {
          e.preventDefault();
          if (!idOk || !title.trim()) return;
          setBusy(true);
          setErr(null);
          try {
            await onCreate({ projectId, title: title.trim(), description, defaultAgentId: agent, ...(study ? { studyDefault: true } : {}) });
          } catch (x) {
            setErr((x as Error).message);
            setBusy(false);
          }
        }}
      >
        <h3>New project in {env}</h3>
        <label>
          Project ID
          <input value={projectId} onChange={(e) => setProjectId(e.target.value)} placeholder="e.g. grad-programmes-2027" autoFocus />
        </label>
        <p className="hint">
          Must match a project key in BridgeAgent <code>config.json</code> with <code>environmentId: "{env}"</code>. Otherwise the home
          agent rejects jobs. Letters, digits, <code>-</code>, <code>_</code>, max 40.
        </p>
        {projectId && !idOk && <p className="error-text">Invalid project ID.</p>}
        <label>
          Title
          <input value={title} onChange={(e) => setTitle(e.target.value)} />
        </label>
        <label>
          Description
          <textarea rows={3} value={description} onChange={(e) => setDescription(e.target.value)} />
        </label>
        <label>
          Default agent
          <select value={agent} onChange={(e) => setAgent(e.target.value as AgentId)}>
            {PICKABLE_AGENTS.map((a) => <option key={a.id} value={a.id}>{a.displayName}</option>)}
          </select>
        </label>
        {/* 0.78.0: new chats of the project start in study mode (each chat can switch it) */}
        <label className="check">
          <input type="checkbox" checked={study} onChange={(e) => setStudy(e.target.checked)} />
          🎓 Study mode for new chats: the agent leads to the answer with hints instead of giving it right away
        </label>
        {err && <p className="error-text">{err}</p>}
        <div className="modal-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !idOk || !title.trim()}>{busy ? 'Creating…' : 'Create'}</button>
        </div>
      </form>
    </div>
  );
}
