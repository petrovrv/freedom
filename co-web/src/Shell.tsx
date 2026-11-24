// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode, type SyntheticEvent } from 'react';
import { archiveScope, buildChatTree, readTreeOpen, trashDue, trashScope, TRASH_DAYS, treeTitle, writeTreeOpen } from './chatTree';
import { SORT_MODES, compareBy, readSortMode, ts as tsOf, writeSortMode, type SortMode } from './sidebarSort';
import ViewerSplit from './ViewerSplit';
import { Resizer, useColumnWidth } from './Resizer';
import { ChatStatusIcon, ProjectRing, chatStatus, isUnread, useSeenVersion } from './status';
import { dbActivity, dbGatewayWaiting, type ChatActivity } from './dbapi';
import { InboxView, useInbox } from './Inbox';
import { configureFast, fastConfigFromOwner } from './pubsub';
import { Navigate, useLocation, useNavigate, useNavigationType, useParams } from 'react-router-dom';
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
import { allProjectChats, latestChats, moveChatGroup, purgeTrashedChat, restoreChat, saveProjectMeta, trashChat, updateChatMeta } from './workbench';
import { projectModeDefault, type ModeDecl } from './moduleModes';
import { modesOf } from './moduleStore';
import { importCalls } from './importWriter';
import { LimitsBar } from './Limits';
import SidePanel from './Properties';
import { ChatFolder, ProjectWorkspace } from './FolderContents';
import FamilyPanel from './FamilyPanel';
import Launcher from './Launcher';
import Explorer from './Explorer';
import { folderChatId, inAgents, isAgentTalk, readExplorerOn, subtreeOf, writeExplorerOn, type ExplorerPath, type MoveDest } from './explorerTree';
import { placeParent } from './chatTree';
import AppHost, { RecentApps, useAppEvents } from './AppHost';
import { trayWantsRefresh } from './appEvents';
import { appKey, exitTarget, findApp, findPage, liveLimit, loadRecent, pruneApps, RECENT_MAX, saveRecent, touchApp, type AppRef } from './appStack';
import { shellOpen, type ShellApp } from './extBridge';
import { ModuleFrame, ModuleSlots, modulePages, useModules, type PanelLink } from './ModuleSlot';
import type { Chat, ChatEvent, LimitsFile, Mode, OwnerInfo, Project } from './types';

import { SAFE_ID, formatLocal, mapLimit } from './util';
import { autoHost, HOME, ownBridgeWaiting, SITE, type HostPref } from './routing';
import { applyExecutors, autoOrder, HOST_ID_RE, loadCachedExecutors } from './executors';
import { dbExecutors } from './dbapi';
import { HostLight } from './HostLight';
import { aiSearch, chatRefs, openDocument, projectActivity, projectFreshness, type SearchHit } from './search';
import {
  createProject, dbOnlyHost, discoverRoot, hostHealth, listChats, listProjects, loadHost, readLimits, readOwner,
  unregisteredBridgeProjects, type HostDirs, type Root,
} from './workbench';

import { useBridgeWatch } from './bridgeWatch';
import { ShareDialog } from './ShareDialog';
import MacOffer from './MacOffer';
import { closePanel, openPanel, togglePanel, useChatPanel } from './chatPanel';
import { dropShare, latestShare, type SharedItem } from './share';
import { gateway, projectConfidential } from './gateway';

type Signed = Exclude<AuthState, { status: 'signed_out' }>;

export default function Shell({ auth }: { auth: Signed }) {
  useBridgeWatch();
  useWebDeviceRunner(auth.email); // 0.49.0: this browser as a device
  useExtDeviceRunner(auth.email); // 0.62.0: CO in the browser extension panel: the panel as a browser_* device
  const params = useParams();
  const navigate = useNavigate();
  const pathname = useLocation().pathname;
  // F10: a page (ui.page) of a module: /e/:env/m/:moduleId/:pageId
  const modulePageRoute = params.moduleId && params.pageId ? { moduleId: params.moduleId, pageId: params.pageId } : null;
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
    // F12: any executor id (the server's list may come after the first render)
    return v && (HOSTS.some((h) => h.id === v) || HOST_ID_RE.test(v)) ? (v as HostPref) : 'auto';
  });
  const setHostPref = (p: HostPref) => {
    localStorage.setItem(HOST_PREF_KEY, p);
    setHostPrefState(p);
  };
  const setHostId = (id: HostId) => setHostPref(id as HostPref);
  // 12.11: auto = Home while its Bridge is online (any device), else the Site
  const localDevice = useLocalDevice();
  // F12 (SPEC §3.1a): executors from the server (db.php op executors), in the order auto tries them for this environment
  const [execOrder, setExecOrder] = useState<string[]>(() => {
    loadCachedExecutors();
    return autoOrder();
  });
  const autoIds = execOrder.filter((id) => !hosts || hosts[id]);
  // a hand-picked executor that is gone, switched off or not serving this environment falls back to auto
  const prefOk = hostPref === 'auto' || HOSTS.some((h) => h.id === hostPref);
  const hostId = (!prefOk || hostPref === 'auto' ? autoHost(owners, localDevice, autoIds) : hostPref) as HostId;
  // 0.110.0: CO Desktop on the Bridge PC waits for its own Bridge while it is briefly out: no handover, no failover
  const waitOwn = (!prefOk || hostPref === 'auto') && ownBridgeWaiting(owners, localDevice, autoIds) === hostId;
  const host = hosts?.[hostId] || null;
  const owner = owners[hostId] ?? null;
  const limits = limitsByHost[hostId] ?? null;
  const [fatal, setFatal] = useState<string | null>(null);
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [chats, setChats] = useState<Chat[] | null>(null);
  // F15.1: the project whose chats `chats` holds (null while the open project's list loads)
  const [chatsPid, setChatsPid] = useState<string | null>(null);
  // F15.1: Explorer view of the left panel (0.128.0: the default; ☰ → Tree view) and the chats of the other projects it shows
  const [explorerOn, setExplorerOnState] = useState(readExplorerOn);
  const setExplorerOn = (on: boolean) => {
    writeExplorerOn(on);
    setExplorerOnState(on);
  };
  const [exChats, setExChats] = useState<Record<string, { at: number; list: Chat[] }>>({});
  const exLoading = useRef(new Set<string>());
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
  // 0.86.0: ☰ menu (navigation, account). 0.128.0: the side panel (Properties of a folder, System, the family room;
  // chatPanel.ts) instead of the right panel with tabs
  const [menuOpen, setMenuOpen] = useState(false);
  const { panel, nonce: panelNonce, pinned: panelPinned, familyUnread } = useChatPanel();
  // 0.90.3: an open drawer (narrow screens) holds one history entry, so the phone's Back closes it;
  // closing it otherwise (✕, scrim) drops that entry again
  const drawerOpen = leftOpen || !!panel;
  useEffect(() => {
    if (!drawerOpen || !window.matchMedia('(max-width: 1100px)').matches) return;
    if (!window.history.state?.coDrawer) window.history.pushState({ ...window.history.state, coDrawer: true }, '');
    const onPop = () => {
      setLeftOpen(false);
      // unsaved edits kept: the panel stays, and so does the entry Back closes it with
      if (!closePanel()) window.history.pushState({ ...window.history.state, coDrawer: true }, '');
    };
    window.addEventListener('popstate', onPop);
    return () => {
      window.removeEventListener('popstate', onPop);
      if (window.history.state?.coDrawer) window.history.back();
    };
    // 0.128.0: a new page while a drawer stays open (Properties of another chat) gets its own entry, so Back closes the
    // drawer on that page
  }, [drawerOpen, pathname]);
  // 0.90.0: a family push opens /e/<env>?family=1 -> the family room (app 👪)
  const loc = useLocation();
  useEffect(() => {
    if (!new URLSearchParams(loc.search).has('family')) return;
    openPanel('family');
    navigate(loc.pathname, { replace: true });
  }, [loc.search, loc.pathname, navigate]);
  const [showNewProject, setShowNewProject] = useState(false);
  const [reconnectErr, setReconnectErr] = useState<string | null>(null);
  const [chatAction, setChatAction] = useState<{ action: ChatAction; chat: Chat } | null>(null);
  const [projectAction, setProjectAction] = useState<{ action: ProjectAction; project: Project } | null>(null);
  // 0.31.0: activity of the env for the status icons (running/queued chats, chats waiting for an answer)
  // 0.52.0: chats stopped while the gateway model was unavailable (F10: the module's op ops.waiting, dbGatewayWaiting)
  const [jobActivity, setJobActivity] = useState<{ running: ChatActivity[]; asking: string[]; gatewayWaiting: string[] } | null>(null);
  // F10: modules (modules_list); their header pages render in ModuleSlots
  const modules = useModules(authOk);

  // F14e.1 (SPEC §3.6a п. 9): apps stay alive while hidden (the last 3 on a phone, 5 on a computer), recent ones are
  // icons in the header and the app bar; Back, Esc and "← Chats" leave an app for the page it was opened from
  const navType = useNavigationType();
  const appNow: AppRef | null = useMemo(() => (modulePageRoute ? { env, ...modulePageRoute } : null),
    [env, modulePageRoute?.moduleId, modulePageRoute?.pageId]); // eslint-disable-line react-hooks/exhaustive-deps
  const [liveApps, setLiveApps] = useState<AppRef[]>([]);
  const [recentApps, setRecentApps] = useState<AppRef[]>(loadRecent);
  // F14b-2: refresh of app pages and tray chips after an action, also with no app open (a tray chip of a module with actions)
  const trayListens = useMemo(() => trayWantsRefresh(modules), [modules]);
  useAppEvents(authOk && (liveApps.length > 0 || trayListens));
  const lastShellPath = useRef<string | null>(null);
  const openedFromShell = useRef(false);
  const wasApp = useRef(false);
  const { search: locSearch } = useLocation();
  useEffect(() => {
    if (!appNow) {
      lastShellPath.current = pathname + locSearch;
      wasApp.current = false;
      return;
    }
    // a new push from a page of the shell: Back returns there; app to app (replace) keeps where the first came from
    if (!wasApp.current) openedFromShell.current = navType === 'PUSH' && lastShellPath.current !== null;
    wasApp.current = true;
    setLiveApps((l) => touchApp(l, appNow, liveLimit(window.matchMedia('(max-width: 760px)').matches)));
  }, [appNow, pathname, locSearch, navType]);
  // recent icons are apps of the launcher only (a page of the side panel has its link there); waits for the module list
  const appNowIsApp = !!appNow && !!findApp(modules, appNow);
  useEffect(() => {
    if (appNow && appNowIsApp) setRecentApps((l) => { const n = touchApp(l, appNow, RECENT_MAX); saveRecent(n); return n; });
  }, [appNow, appNowIsApp]);
  // a page whose module was switched off or removed is unloaded (it stays out of the recent icons by itself); pages of
  // another environment too: the core calls of the shell act in the current one
  // the page shown now comes back when its module is switched on again
  useEffect(() => {
    if (!modules.length) return;
    setLiveApps((l) => {
      const n = pruneApps(l, (a) => a.env === env && !!findPage(modules, a));
      if (!appNow || !findPage(modules, appNow) || n.some((x) => appKey(x) === appKey(appNow))) return n;
      return touchApp(n, appNow, liveLimit(window.matchMedia('(max-width: 760px)').matches));
    });
  }, [modules, env, appNow]);
  const exitApp = useCallback(() => {
    const t = exitTarget(openedFromShell.current, lastShellPath.current, env);
    // the fallback replaces the app's entry: Back must not open the app again
    if (t === -1) navigate(-1); else navigate(t, { replace: true });
  }, [env, navigate]);
  const openApp = useCallback((a: ShellApp) => {
    const desktop = !!(window as unknown as { coDesktop?: unknown }).coDesktop;
    const o = shellOpen(a, env, desktop);
    if (o.target === 'blank') window.open(o.href, '_blank', 'noopener');
    else if (!appNow || appKey(appNow) !== appKey({ env, moduleId: a.m.id, pageId: a.ui.id })) navigate(o.href, { replace: !!appNow });
  }, [env, navigate, appNow]);
  useEffect(() => {
    if (!appNow) return;
    const f = (e: globalThis.KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented || e.isComposing) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
      // a menu, popover or dialog of the shell takes Esc first
      if (document.querySelector('.app-menu-scrim, .modal, .launcher-pop')) return;
      exitApp();
    };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [appNow, exitApp]);
  useSeenVersion();
  useColumnWidth('--left-w', 'co.leftW', 270, 180, 560);
  useColumnWidth('--right-w', 'co.rightW', 340, 260, 720);
  // 0.25.0: archive (projects and chats are hidden from the lists, not deleted)
  const [showArchivedChats, setShowArchivedChats] = useState(false);
  const [showTrash, setShowTrash] = useState(false);
  const purging = useRef(new Set<string>()); // F15.2: chats being deleted by the 30-day trash clean-up
  const chatsNow = useRef<Chat[] | null>(null); // F15.2: the newest chat list for the clean-up's re-check
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
        // F12: other executors take their jobs from the database only; no Drive folders are made for them
        for (const h of HOSTS) loaded[h.id] = h.id === HOME || h.id === SITE ? await loadHost(r, h.id) : dbOnlyHost(h.id);
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
        setOwners((prev) => ({ ...prev, [id]: newerOwner(prev[id] ?? null, o) }));
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

  // F12: the executor list and the owner.json copies the Bridges send to the database (a Linux server has no Drive)
  const hostsRef = useRef<Record<string, HostDirs> | null>(null);
  hostsRef.current = hosts;
  const refreshExecutors = useCallback(async () => {
    if (!authOk || !env) return;
    const list = await dbExecutors(env);
    if (!list) return;
    applyExecutors(list);
    setExecOrder((prev) => {
      const next = autoOrder();
      return prev.join(',') === next.join(',') ? prev : next;
    });
    setOwners((prev) => {
      let next = prev;
      for (const e of list) {
        if (!e.owner) continue;
        const o = newerOwner(prev[e.id] ?? null, e.owner);
        if (o !== prev[e.id]) next = { ...next, [e.id]: o };
      }
      return next;
    });
    const fresh = list.filter((e) => !hostsRef.current?.[e.id]);
    if (fresh.length && hostsRef.current) {
      setHosts((prev) => {
        const next = { ...(prev || {}) };
        for (const e of fresh) if (!next[e.id]) next[e.id] = dbOnlyHost(e.id);
        return next;
      });
    }
  }, [authOk, env]);
  useEffect(() => { void refreshExecutors(); }, [refreshExecutors]);
  useInterval(refreshExecutors, POLL_HOST_MS, authOk);

  // The site agent is started on demand by the web server; keep it alive while it is selected.
  const pingSite = useCallback(() => {
    if ((hostId === 'site' || hostPref === 'auto') && authOk) void pingSiteAgent();
  }, [hostId, hostPref, authOk]);
  useEffect(pingSite, [pingSite]);
  // S2: in auto mode the site agent is kept alive too, so that the light and the failover see it
  useInterval(pingSite, SITE_PING_MS, (hostId === 'site' || hostPref === 'auto') && authOk);

  // F10: generic core calls a module page may use (each refused unless its manifest declares the point's permission)
  const projectsRef = useRef<Project[] | null>(null);
  projectsRef.current = projects;
  // navigate changes with every address; the sessions of the page frames must not
  const navigateRef = useRef(navigate);
  navigateRef.current = navigate;
  // a factory: every page frame gets its own session of core calls for its life (a hidden app keeps its import session)
  const pageCalls = useCallback(() => (root ? importCalls({
    root, env, projects: () => projectsRef.current,
    onFinished: async (open) => {
      await refreshProjectsRef.current();
      if (open !== undefined) navigateRef.current(open ? `/e/${env}/p/${open}` : `/e/${env}`);
    },
  }) : undefined), [root, env]); // eslint-disable-line react-hooks/exhaustive-deps
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
  const refreshProjectsRef = useRef(refreshProjects);
  refreshProjectsRef.current = refreshProjects;
  const refreshJobActivity = useCallback(async () => {
    if (!authOk) return;
    const [a, w] = await Promise.all([dbActivity(env), dbGatewayWaiting(env)]);
    if (a) setJobActivity({ ...a, gatewayWaiting: w.map((x) => x.chatId) });
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
  const gatewaySet = new Set(jobActivity?.gatewayWaiting || []);
  const workingByProject = new Map<string, number>();
  for (const r of jobActivity?.running || []) workingByProject.set(r.projectId, (workingByProject.get(r.projectId) || 0) + 1);
  const statusOf = (c: Chat) =>
    chatStatus({
      running: runningByChat.get(c.chatId),
      gateway: gatewaySet.has(c.chatId),
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

  useEffect(() => {
    chatsNow.current = chats;
  }, [chats]);
  // F15.2: trashed chats of an own project are deleted for good 30 days after deletion, when the project is opened
  useEffect(() => {
    if (!chats || !root || !hosts || !project || project.shareRole || chatsPid !== project.projectId) return;
    const due = trashDue(chats, Date.now()).filter((c) => !purging.current.has(c.chatId));
    if (!due.length) return;
    for (const c of due) purging.current.add(c.chatId);
    void (async () => {
      const done = new Set<string>();
      for (const c of due) {
        try {
          // re-checked right before: restored here since the list was read; a restore elsewhere is caught by the server
          const here = (chatsNow.current || []).find((x) => x.folderId === c.folderId);
          if (!here || !trashDue([here], Date.now()).length) continue;
          if (await purgeTrashedChat(root, here)) done.add(c.folderId);
        } catch (e) {
          console.warn('trash clean-up', c.chatId, e);
        }
      }
      for (const c of due) purging.current.delete(c.chatId);
      if (done.size) setChats((prev) => (prev || []).filter((x) => !done.has(x.folderId)));
    })();
  }, [chats, chatsPid, root, hosts, project]); // eslint-disable-line react-hooks/exhaustive-deps

  const refreshChats = useCallback(async () => {
    if (!project || !authOk) return;
    try {
      setChats(await listChats(project, env)); // 0.48.0: preview-aware
      setChatsPid(project.projectId);
    } catch (e) {
      console.warn(e);
    }
  }, [project, authOk, env]);
  useEffect(() => {
    setChats(null);
    setChatsPid(null);
    void refreshChats();
  }, [project?.folderId]); // eslint-disable-line react-hooks/exhaustive-deps
  useInterval(refreshChats, POLL_LISTS_MS, !!project && authOk);

  const chat = useMemo(() => chats?.find((c) => c.chatId === chatId) || null, [chats, chatId]);

  // 0.128.0: the Properties of a chat show the open chat and follow it - another chat is opened first (its settings
  // live in its ChatView), then the request drops its ids; any open request closes the projects drawer of a phone
  useEffect(() => {
    if (!panel) return;
    if (window.matchMedia('(max-width: 1100px)').matches) setLeftOpen(false);
    if (panel.kind === 'chat' && panel.chatId) {
      // the drawer's own history entry (a phone) is replaced, so Back does not lead to the chat left behind
      if (panel.projectId && panel.chatId !== chatId) navigate(`/e/${env}/p/${panel.projectId}/c/${panel.chatId}`, { replace: !!window.history.state?.coDrawer });
      openPanel('chat');
    }
  }, [panelNonce]); // eslint-disable-line react-hooks/exhaustive-deps
  // Alt+Enter: the Properties of the open chat, else of the open project, else System (the Explorer handles its items)
  useEffect(() => {
    const f = (e: globalThis.KeyboardEvent) => {
      if (!e.altKey || e.key !== 'Enter' || e.defaultPrevented || e.isComposing || e.ctrlKey || e.metaKey) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
      e.preventDefault();
      if (projectId && chatId) togglePanel('chat');
      else if (projectId) togglePanel('project');
      else togglePanel('system');
    };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [projectId, chatId]);

  // F15.1: the Explorer browses any project; chats of the open project come from `chats`, the others load on demand.
  // A generation per environment: answers and queued loads of the previous environment are dropped.
  const exGen = useRef(0);
  useEffect(() => {
    exGen.current++;
    setExChats({});
    exLoading.current.clear();
  }, [env]);
  const exLoad = useCallback(async (pid: string, gen: number) => {
    const p = projects?.find((x) => x.projectId === pid);
    if (gen !== exGen.current || !p || !authOk || exLoading.current.has(pid)) return;
    exLoading.current.add(pid);
    try {
      const list = await listChats(p, env);
      if (gen === exGen.current) setExChats((m) => ({ ...m, [pid]: { at: Date.now(), list } }));
    } catch (e) {
      console.warn('explorer chats', e);
    } finally {
      if (gen === exGen.current) exLoading.current.delete(pid);
    }
  }, [projects, authOk, env]);
  // fresh for half the list poll, so the Explorer's poll of the shown project reloads it every time
  const exFresh = (pid: string) => Date.now() - (exChats[pid]?.at ?? 0) < POLL_LISTS_MS / 2;
  const exLoadOne = (pid: string) => {
    if (pid !== projectId && !exFresh(pid)) void exLoad(pid, exGen.current);
  };
  const exLoadAll = () => {
    const gen = exGen.current;
    const todo = (projects || []).filter((p) => !p.family && !p.archived && p.projectId !== projectId && !exFresh(p.projectId));
    void mapLimit(todo, 4, (p) => exLoad(p.projectId, gen));
  };
  // the open project: only its own list (`chats`), never an older copy, so the Explorer finds a new chat's folder
  const exChatsOf = useCallback(
    (pid: string): Chat[] | null => (pid === projectId ? (chatsPid === pid ? chats : null) : exChats[pid]?.list ?? null),
    [projectId, chatsPid, chats, exChats],
  );

  // F15.2: a move in the Explorer - inside a project only the chat's place changes; to another project the chat goes
  // with the chats under it. Resolves to the undo, which puts everything back where it was.
  const exReload = (pids: string[]) => {
    for (const pid of new Set(pids)) {
      if (pid === projectId) void refreshChats();
      else void exLoad(pid, exGen.current);
    }
  };
  const exMove = async (c: Chat, d: MoveDest): Promise<() => Promise<void>> => {
    const from = c.projectId;
    const prev = c.folder;
    const src = projects?.find((p) => p.projectId === from);
    const dst = projects?.find((p) => p.projectId === d.projectId);
    if (!src || !dst) throw new Error('The project is not loaded yet.');
    if (d.projectId === from) {
      await updateChatMeta(c, { folder: d.folder });
      exReload([from]);
      return async () => {
        const [now] = await latestChats(src, [c]);
        await updateChatMeta(now, { folder: prev });
        exReload([from]);
      };
    }
    if (!root) throw new Error('The workbench is not loaded yet.');
    const follow = (group: Chat[], to: string) => {
      if (chatId && group.some((x) => x.chatId === chatId)) navigate(`/e/${env}/p/${to}/c/${chatId}`);
    };
    // the whole project (the list shown is capped), so no chat under it is left behind
    const group = await moveChatGroup(root, subtreeOf(c, await allProjectChats(src)), src, dst, d.folder);
    exReload([from, d.projectId]);
    follow(group, d.projectId);
    return async () => {
      const now = await latestChats(dst, group);
      const back = await moveChatGroup(root, now.map((x, i) => (i === 0 ? { ...x, folder: d.folder } : x)), dst, src, prev);
      exReload([from, d.projectId]);
      follow(back, from);
    };
  };

  // F10 (ext/1 chat.mode viewer): "?<param>=<path relative to AI-Workbench>" of a mode with a viewer shows the module's
  // viewer (ui.panel) left of the chat of that document. On a project URL the chat of the document (chat[<key>Doc])
  // opens if there is one; otherwise a new chat with the mode on is made by the first question.
  const search = useLocation().search;
  const modes = useMemo(() => modesOf(modules), [modules]);
  const viewer = useMemo(() => {
    const q = new URLSearchParams(search);
    for (const m of modes) {
      const doc = m.viewer ? q.get(m.viewer.param) : null;
      const mod = doc ? modules.find((x) => x.enabled && (x.modes || []).some((y) => y.key === m.key)) : null;
      const ui = mod?.ui.find((u) => u.slot === 'ui.panel' && u.id === m.viewer!.panel);
      if (doc && mod && ui) return { mode: m, doc, mod, ui };
    }
    return null;
  }, [search, modes, modules]);
  const viewerPath = viewer?.doc ?? null;
  const viewerChat = useMemo(
    () => (viewer && !chatId ? (chats || []).find((c) => (c as unknown as Record<string, unknown>)[viewer.mode.key + 'Doc'] === viewer.doc && !c.archived) || null : null),
    [viewer, chatId, chats],
  );
  const [viewerEvents, setViewerEvents] = useState<ChatEvent[] | null>(null);
  const [viewerAsk, setViewerAsk] = useState<{ text: string; files: File[]; nonce: number } | null>(null);
  const [viewerCanAsk, setViewerCanAsk] = useState(false);
  useEffect(() => {
    setViewerEvents(null);
    setViewerAsk(null);
  }, [viewerPath, chatId]);
  const viewerWrap = (node: ReactNode) => {
    if (!viewer || !root) return node;
    const link: PanelLink = {
      chat: { id: chat?.chatId || null, title: chat?.title || '' },
      events: viewerEvents,
      canAsk: !!authOk && viewerCanAsk,
      ask: (text, files) => setViewerAsk({ text, files, nonce: Date.now() }),
      reveal: (id) => {
        const el = document.getElementById(`ev-${id}`);
        if (!el) return;
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        el.classList.add('flash');
        setTimeout(() => el.classList.remove('flash'), 1600);
      },
      close: () => navigate(pathname, { replace: true }),
    };
    return (
      <ViewerSplit
        pane={
          // another document starts with a clean viewer
          <ModuleFrame key={`${viewer.mod.id}/${viewer.ui.id}/${viewer.mod.version}/${viewer.doc}`} m={viewer.mod} ui={viewer.ui} me={auth.homeEnv} owner={!!auth.owner}
            mode="fill" extra={{ uiId: viewer.ui.id, params: { [viewer.mode.viewer!.param]: viewer.doc }, chat: link.chat }} rootId={root.id} panel={link} />
        }
        chat={node}
      />
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
    .filter((p) => !p.family) // 0.90.0: the family room lives in its own place (0.128.0: the app 👪)
    .filter((p) => !f || p.title.toLowerCase().includes(f) || p.projectId.toLowerCase().includes(f))
    .map((p) => ({ p, k: projectSortKey(p) }))
    .sort((a, b) => projectCmp(a.k, b.k))
    .map((x) => x.p);
  const matchingChats = (chats || []).filter((c) => !f || c.title.toLowerCase().includes(f));
  const visibleChats = matchingChats.filter((c) => !c.archived);
  // F15.2: a trashed chat is archived too (hidden wherever archived ones are) and listed only under Trash
  const archivedChats = matchingChats.filter((c) => c.archived && !c.trashedUtc);
  const trashedChats = matchingChats.filter((c) => !!c.trashedUtc);
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
  // F15.1: the Explorer view sorts projects like the tree (newest chat activity, waiting items)
  const exProjectFresh = (p: Project) => projectFreshness(p, activity);
  const exProjectWait = (p: Project) => inboxByProject.get(p.projectId) || 0;

  // F15.2: the archived chats and the trash of the open project - under its chats in the tree, in its folder in the Explorer
  const archiveAndTrash = (p: Project) => (
    <>
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
                  <ChatMenu archived actions={chatActionsFor(p, c)} onPick={(action) => void pickChatAction(action, c)} onProperties={() => openPanel('chat', { projectId: p.projectId, chatId: c.chatId })} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {trashedChats.length > 0 && (
        <>
          <button className="btn tiny ghost" onClick={() => setShowTrash((v) => !v)} aria-expanded={showTrash}>
            {showTrash ? '▾' : '▸'} 🗑 Trash ({trashedChats.length})
          </button>
          {showTrash && (
            <ul className="archived-list">
              <li className="hint small">Deleted for good {TRASH_DAYS} days after deletion.</li>
              {trashedChats.map((c) => (
                <li key={c.folderId} className="chat-li">
                  <button
                    className={c.chatId === chatId ? 'active' : ''}
                    title={c.summary || undefined}
                    onClick={() => {
                      navigate(`/e/${env}/p/${p.projectId}/c/${c.chatId}`);
                      setLeftOpen(false);
                    }}
                  >
                    <span className="nav-title">{shownTitle(c.title || '')}</span>
                    <span className="nav-date" title="Moved to trash">{formatLocal(c.trashedUtc || '')}</span>
                  </button>
                  <ChatMenu actions={chatActionsFor(p, c).includes('delete') ? ['restore', 'purge'] : []} onPick={(action) => void pickChatAction(action, c)} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </>
  );

  const exOpenChat = (pid: string, cid: string) => { navigate(`/e/${env}/p/${pid}/c/${cid}`); setLeftOpen(false); };
  // 0.128.0: content of a folder in the Explorer besides its chats: the files of the open chat (in its own folder and
  // in the folder it sits in), the workspace of a project, the archive and trash of the open project.
  // 0.129.0: the folder of the open chat with its parts (FolderContents.tsx ChatFolder) instead of its files
  const exFolderExtra = (path: ExplorerPath) => {
    const p = path.projectId ? projects?.find((x) => x.projectId === path.projectId) : undefined;
    if (!p) return null;
    // 0.128.2: in a chat's "Agent talks" folder the files of an open agent talk, in the chat folder those of the others
    const here = folderChatId(path);
    const talks = inAgents(path);
    let filesHere = false;
    if (chat && p.projectId === projectId) {
      const up = placeParent(chat);
      const shownIn = up && chats?.some((c) => c.chatId === up && !c.archived) ? up : undefined;
      filesHere = (here === chat.chatId && !talks) || (here === shownIn && (!shownIn || isAgentTalk(chat) === talks));
    }
    return (
      <>
        {filesHere && chat && <ChatFolder chat={chat} project={p} chats={chats} root={root} authOk={authOk} showSubchats={false} onOpenChat={exOpenChat} />}
        {!here && <ProjectWorkspace project={p} owner={owner} root={root} authOk={authOk} />}
        {!here && p.projectId === projectId && <div className="ex-archive">{archiveAndTrash(p)}</div>}
      </>
    );
  };

  async function pickProjectAction(action: ProjectAction, p: Project) {
    // F10: a chat mode of a module for new chats of the project (<key>Default)
    if (action.startsWith('mode:')) {
      const key = action.slice(5);
      try {
        const updated = await saveProjectMeta(p, { [key + 'Default']: !projectModeDefault(p, key) } as Partial<Project>);
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

  // F15.2: Delete moves the chat with its subchats to the trash; Restore brings them back with trashed parents
  async function trashOrRestore(c: Chat, trash: boolean) {
    if (!hosts || !!c.trashedUtc === trash) return; // already there
    const scope = trashScope(chats || [], c.chatId, trash);
    if (scope.some((x) => purging.current.has(x.chatId))) {
      alert('The trash is being cleaned up (30 days passed); try again in a moment.');
      return;
    }
    const changed = new Map<string, Chat>();
    let failed = 0;
    for (const x of scope.length ? scope : [c]) {
      try {
        const updated = trash ? await trashChat(x, hosts) : await restoreChat(x);
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
    if (trash && chatId && [...changed.values()].some((x) => x.chatId === chatId)) navigate(`/e/${env}/p/${c.projectId}`);
    if (failed) alert(`${failed} related chat(s) could not be changed.`);
  }

  async function pickChatAction(action: ChatAction, c: Chat) {
    if (action === 'delete' || action === 'restore') return trashOrRestore(c, action === 'delete');
    if (c.trashedUtc && action !== 'purge' && action !== 'rename' && action !== 'title') {
      alert('This chat is in the trash; restore it first.');
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
    // a document viewer next to the chat takes the whole width: the side panes become drawers (☰ menu)
    <div className={`shell env-${env}${viewer ? ' viewer-on' : ''}`}>
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
          <button className="icon-btn" aria-label="Menu" data-email={auth.email} aria-expanded={menuOpen} title="Menu: projects, the view of the panel, account (apps, family and settings are in ▦)" onClick={() => setMenuOpen((v) => !v)}>☰</button>
          {menuOpen && (
            <>
              <div className="app-menu-scrim" onClick={() => setMenuOpen(false)} />
              <div className="app-menu-list" role="menu" onClick={() => setMenuOpen(false)}>
                <button role="menuitem" className="only-mobile" onClick={() => { closePanel(); setLeftOpen((v) => !v); }}>📂 Projects and chats</button>
                {/* 0.128.0: the Explorer is the default view of the panel; the tree is a switch, remembered in this browser */}
                <button role="menuitemcheckbox" aria-checked={!explorerOn} title="Projects and chats as a tree instead of the Explorer (folders with a path bar, Properties, Ctrl+K)" onClick={() => setExplorerOn(!explorerOn)}>
                  🌳 Tree view{explorerOn ? '' : ' ✓'}
                </button>
                <button role="menuitem" title="Properties of the open chat or project (Alt+Enter)" disabled={!projectId}
                  onClick={() => openPanel(chatId ? 'chat' : 'project')}>ⓘ Properties</button>
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
        <HostLight owners={owners} ids={execOrder} env={env} pref={hostPref} effective={hostId} onAdvanced={() => openPanel('system')} />
        <div className="spacer" />
        {/* ext/1 shell: the tray (small ui.header status pages of enabled modules, SPEC §3.6) and the launcher of apps */}
        <div className="module-tray">
          <ModuleSlots slot="ui.header" modules={modules} me={auth.homeEnv} owner={!!auth.owner} />
        </div>
        <RecentApps modules={modules} recent={recentApps.filter((r) => r.env === env).slice(0, 5)} current={appNow} onOpen={openApp} className="hide-mobile" />
        <Launcher modules={modules} env={env} current={modulePageRoute} familyUnread={familyUnread} />
        {familyUnread > 0 && (
          <button type="button" className="fam-top" title="New messages in the family chat" onClick={() => openPanel('family')}>
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
        <div className={`panes${panel ? ' panel-on' : ''}${panel && panelPinned ? ' panel-pinned' : ''}`}>
          <Resizer cssVar="--left-w" storageKey="co.leftW" side="left" def={270} min={180} max={560} />
          {panel && <Resizer cssVar="--right-w" storageKey="co.rightW" side="right" def={340} min={260} max={720} />}
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
                {/* F10: pages of enabled modules (e.g. an import of chats from another service) */}
                {modulePages(modules).filter(({ ui }) => !ui.app).map(({ m, ui }) => (
                  <span key={`${m.id}/${ui.id}`}>
                    <button className="btn tiny" onClick={() => navigate(`/e/${env}/m/${m.id}/${ui.id}`)} disabled={!root} title={ui.title || m.title}>{ui.label || ui.title || m.title}</button>{' '}
                  </span>
                ))}
                <button className="btn tiny" onClick={() => setShowNewProject(true)} disabled={!root}>+ New</button>
              </span>
            </div>
            {projects === null && <div className="muted small pad">Loading…</div>}
            {projects?.length === 0 && <div className="muted small pad">No projects in {envName}.</div>}
            {/* F15.1: the Explorer view (☰ → Explorer view); the tree below stays the default */}
            {explorerOn && projects && (
              <Explorer
                rootLabel={envName}
                projects={projects}
                chatsOf={exChatsOf}
                loadChats={exLoadOne}
                loadAll={exLoadAll}
                routeProjectId={projectId}
                routeChatId={chatId}
                sort={sortMode}
                filter={filter}
                statusOf={statusOf}
                workingOf={(pid) => workingByProject.get(pid) || 0}
                projectFresh={exProjectFresh}
                projectWait={exProjectWait}
                chatWait={chatWaitRank}
                onOpenChat={exOpenChat}
                onOpenProject={(pid) => { navigate(`/e/${env}/p/${pid}`); setLeftOpen(false); }}
                onMove={exMove}
                onProperties={(t) => (t.projectId ? openPanel(t.chatId ? 'chat' : 'project', t) : openPanel('system'))}
                folderExtra={exFolderExtra}
              />
            )}
            {!explorerOn && (<>
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
                    {projectConfidential(p) && <span className="tag" title={`Confidential (${gateway().employerTitle} data)`}>conf</span>}
                    {/* 0.45.0: projects shared with me */}
                    {p.shareRole && (() => { const o = ENVIRONMENTS.find(e => e.id === p.environmentId)?.displayName ?? p.environmentId; return <span className="tag" title={`Shared by ${o}: ${p.shareRole === 'editor' ? 'can edit' : 'can view'}${p.shareVia === 'chat' ? ', selected chats only' : ''}`}>{'\u{1F465} ' + o}</span>; })()}
                  </button>
                  {/* 0.58.0: project menu; "as a chat with subchats" only without chat trees (known for the open project) */}
                  <ProjectMenu
                    archived={p.archived}
                    modes={modes.filter((m) => m.projectLabel).map((m) => ({ key: m.key, label: `${m.icon} ${m.projectLabel}`.trim(), on: projectModeDefault(p, m.key) }))}
                    actions={projectActionsFor(p, p.projectId === projectId && !!chats?.some((c) => c.parent), modes.filter((m) => m.projectLabel).map((m) => m.key))}
                    onPick={(action) => void pickProjectAction(action, p)}
                    onProperties={() => openPanel('project', { projectId: p.projectId })}
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
                            <ChatMenu actions={chatActionsFor(p, c)} onPick={(action) => void pickChatAction(action, c)} onProperties={() => openPanel('chat', { projectId: p.projectId, chatId: c.chatId })} />
                          </li>
                        ))}
                      </ul>
                      {archiveAndTrash(p)}
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
                        <ProjectMenu archived modes={modes.filter((m) => m.projectLabel).map((m) => ({ key: m.key, label: `${m.icon} ${m.projectLabel}`.trim(), on: projectModeDefault(p, m.key) }))} actions={projectActionsFor(p, false, modes.filter((m) => m.projectLabel).map((m) => m.key))} onPick={(action) => void pickProjectAction(action, p)} />
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
            {/* 0.128.0: the files of the open chat and the project workspace (in the Explorer: inside their folders); 0.129.0: the chat folder */}
            {project && (
              <div className="tree-extra">
                {chat && <ChatFolder chat={chat} project={project} chats={chats} root={root} authOk={authOk} showSubchats onOpenChat={exOpenChat} />}
                <ProjectWorkspace project={project} owner={owner} root={root} authOk={authOk} />
              </div>
            )}
            </>)}
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
            ) : modulePageRoute ? null : viewerChat ? (
              <Navigate to={`/e/${env}/p/${viewerChat.projectId}/c/${viewerChat.chatId}${search}`} replace />
            ) : chat && project ? viewerWrap(
              <ChatView
                key={chat.folderId}
                viewer={viewer ? { mode: viewer.mode, doc: viewer.doc } : undefined}
                viewerAsk={viewer ? viewerAsk : null}
                onEvents={viewer ? setViewerEvents : undefined}
                onCanAsk={viewer ? setViewerCanAsk : undefined}
                root={root}
                host={host}
                hosts={hosts!}
                owners={owners}
                hostAuto={hostPref === 'auto' && !waitOwn}
                holdOwn={waitOwn}
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
                projectChats={chats}
                onChatAction={(action) => void pickChatAction(action, chat)}
                onChatCreated={(c) => {
                  // 0.33.0: branch / follow-up opened from this chat
                  setChats((prev) => [c, ...(prev || []).filter((x) => x.chatId !== c.chatId)]);
                  navigate(`/e/${env}/p/${c.projectId}/c/${c.chatId}`);
                }}
                onProjectChanged={(p) => setProjects((prev) => (prev || []).map((x) => (x.folderId === p.folderId ? p : x)))}
              />
            ) : project && (!viewer || chats) ? viewerWrap(
              // 0.17.0: selecting a project opens a new chat at once; it is saved with the first message.
              <ChatView
                key={`draft:${project.projectId}${viewer ? ':' + viewer.mode.key : ''}`}
                viewer={viewer ? { mode: viewer.mode, doc: viewer.doc } : undefined}
                viewerAsk={viewer ? viewerAsk : null}
                onEvents={viewer ? setViewerEvents : undefined}
                onCanAsk={viewer ? setViewerCanAsk : undefined}
                onHostChange={(id) => { if (hostPref !== 'auto' && HOSTS.some((h) => h.id === id)) setHostId(id as HostId); }}
                root={root}
                host={host}
                hosts={hosts!}
                owners={owners}
                hostAuto={hostPref === 'auto' && !waitOwn}
                holdOwn={waitOwn}
                env={env}
                project={project}
                chat={{
                  chatId: '', projectId: project.projectId, title: 'New chat', rootBranchId: 'BR-root',
                  createdUtc: '', updatedUtc: '', folderId: '', defaultMode: projectModes[0], draft: true,
                  ...(viewer ? { [viewer.mode.key]: true } : {}),
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
                  setViewerAsk(null);
                  navigate(`/e/${env}/p/${project.projectId}/c/${c.chatId}${viewer ? search : ''}`);
                }}
              />
            ) : (
              <div className="empty">
                <h2>{envName}</h2>
                <p className="muted">Select a project on the left or create a new one.</p>
              </div>
            )}
            {/* mounted on every page of the shell, so that hidden apps keep their state (also while the inbox is open) */}
            {root && (
              <AppHost modules={modules} env={env} live={liveApps} current={appNow} recent={recentApps} me={auth.homeEnv} owner={!!auth.owner}
                makeCoreCalls={pageCalls} onExit={exitApp} onOpen={openApp} />
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
          {panel && (
            <aside className={`right side ${panelPinned ? 'pinned' : 'overlay'} kind-${panel.kind}`}>
              <SidePanel
                projects={projects}
                chats={chats}
                env={env}
                limits={limits}
                project={project}
                chat={chat}
                owner={owner}
                health={health}
                authOk={authOk}
                host={host}
                hostPref={hostPref}
                onHostPref={setHostPref}
                onProjectChanged={(p) => setProjects((prev) => (prev || []).map((x) => (x.folderId === p.folderId ? p : x)))}
              />
            </aside>
          )}
          {leftOpen && <div className="scrim only-mobile drawer-scrim" onClick={() => setLeftOpen(false)} />}
          {panel && <div className={`scrim side-scrim${panelPinned ? ' pinned' : ''}`} onClick={closePanel} />}
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
          modes={modes}
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
  env, onClose, onCreate, modes,
}: {
  env: EnvironmentId;
  onClose: () => void;
  onCreate: (i: { projectId: string; title: string; description: string; defaultAgentId: AgentId } & Record<string, unknown>) => Promise<void>;
  modes: ModeDecl[];
}) {
  const [projectId, setProjectId] = useState('');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [agent, setAgent] = useState<AgentId>('claude');
  const [modeDefaults, setModeDefaults] = useState<Record<string, boolean>>({});
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
            const defs = Object.fromEntries(Object.entries(modeDefaults).filter(([, on]) => on).map(([k]) => [k + 'Default', true]));
            await onCreate({ projectId, title: title.trim(), description, defaultAgentId: agent, ...defs });
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
        {/* F10: new chats of the project start with a mode of a module on (each chat can switch it) */}
        {modes.filter((m) => m.projectLabel).map((m) => (
          <label key={m.key} className="check">
            <input type="checkbox" checked={!!modeDefaults[m.key]} onChange={(e) => setModeDefaults((d) => ({ ...d, [m.key]: e.target.checked }))} />
            {`${m.icon} ${m.newProjectHint || m.projectLabel}`.trim()}
          </label>
        ))}
        {err && <p className="error-text">{err}</p>}
        <div className="modal-actions">
          <button type="button" className="btn ghost" onClick={onClose}>Cancel</button>
          <button type="submit" className="btn primary" disabled={busy || !idOk || !title.trim()}>{busy ? 'Creating…' : 'Create'}</button>
        </div>
      </form>
    </div>
  );
}

// F12: of two copies of a host's owner.json (Drive, database) the one written last
function newerOwner(a: OwnerInfo | null, b: OwnerInfo | null): OwnerInfo | null {
  if (!a) return b;
  if (!b) return a;
  const ta = Date.parse(a.updatedUtc || '') || 0;
  const tb = Date.parse(b.updatedUtc || '') || 0;
  return tb >= ta ? b : a;
}
