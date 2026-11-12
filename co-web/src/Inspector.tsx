// SPDX-License-Identifier: AGPL-3.0-or-later
import { ModelsSection } from './Models';
import { useNavigate } from 'react-router-dom';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChatEvent, ContextFolder } from './types';
import { dbDossierGet, dbDossierPut, dbGetProfile, dbMemoryCandidates, dbPutProfile, type EnvProfile, type MemoryCandidate } from './dbapi';
import { DOSSIER_SECTION_LABEL, type DossierRow } from './secretaryHints';
import { FilesTab } from './FilesPanel';
import { setChatSlot, setFamilySlot, setFilesSlot, setShownTab, useChatPanel, type InspectorTab } from './chatPanel';
import { MemoryReviewDialog } from './MemoryReview';
import { categorize } from './errors';
import { FOLDER_MIME, children, getBytes, type DriveFile } from './drive';
import { ContextFoldersSection } from './ContextFolders';
import { SchedulesSection } from './Schedules';
import { AccountConnectorsSection } from './AccountConnectors';
import { FolderPicker } from './FolderPicker';
import { FileMenu, canvasItem, googleDocsUrl, hasGoogleDocs, studyItem } from './FileMenu';
import { LimitsDetail } from './Limits';
import { NotificationsSection } from './Notifications';
import { OpsSection } from './Ops';
import { HealthSection } from './Health';
import DesktopDownloadSection from './DesktopDownload';
import { isMacDesktop, openMacOffer } from './MacOffer';
import { DriveDesktopGuide, WebDeviceSection } from './WebDevice';
import { insideDesktop } from './webdevStore';
import { SharingSection } from './Sharing';
import { fmtMs, fmtTok } from './JobTimeline';
import { dbAutoSwitch, dbQueueOn, dbReadsPrimary, dbUsage, setDbQueueOn, setDbReadsPrimary, type UsageRow } from './dbapi';
import { resetAutoSwitchGlobal } from './Permissions';
import { chatPermissions, type Chat, type LimitsFile, type OwnerInfo, type Project } from './types';
import { formatBytes, formatLocal } from './util';
import type { HostPref } from './routing';
import {
  chatScratchPath, copyToFolder, listProjectFiles, loadChatEvents, readProjectInstructions, resolveDrivePath,
  saveProjectContextFile, readProjectContextFile, saveProjectInstructions, saveProjectMeta, uploadProjectFile, type HostDirs, type HostHealth, type Root,
} from './workbench';
import { memberNames } from './members';
import { corporate } from './corporate';

type Tab = InspectorTab;

const TAB_LABEL: Record<Tab, string> = { family: '👪 Family', chat: 'Chat', session: 'Session', project: 'Project', workspace: 'Workspace', artifacts: 'Files', host: 'Host' };

export default function Inspector({
  project, chat, root, owner, health, authOk, host, onProjectChanged, limits, chats, env, onClose, hostPref, onHostPref,
}: {
  onClose?: () => void;
  // S2 (0.98.0): executor preference, chosen by hand only here
  hostPref?: HostPref;
  onHostPref?: (p: HostPref) => void;
  chats?: Chat[] | null;
  env?: string;
  limits: LimitsFile | null;
  project: Project | null; chat: Chat | null; root: Root | null; owner: OwnerInfo | null; health: HostHealth; authOk: boolean;
  host: HostDirs | null; onProjectChanged: (p: Project) => void;
}) {
  // 0.86.0: the Chat tab holds the chat's own controls (ChatView renders them into the slot); default when a chat is open
  const [tab, setTab] = useState<Tab>(() => (localStorage.getItem('co.inspectorTab') as Tab | null) || 'chat');
  const { want, familyUnread } = useChatPanel();
  useEffect(() => { if (want) setTab(want.tab); }, [want]);
  useEffect(() => { localStorage.setItem('co.inspectorTab', tab); setShownTab(tab); }, [tab]);
  return (
    <div className={`inspector${tab === 'family' ? ' family-on' : ''}`}>
      <div className="tabs" role="tablist">
        {(['family', 'chat', 'session', 'project', 'workspace', 'artifacts', 'host'] as Tab[]).map((t) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}
            title={t === 'family' ? `Family chat of ${memberNames()}: messages, photos, emoji; @co calls AI Workbench` : undefined}>
            {TAB_LABEL[t]}
            {t === 'family' && familyUnread > 0 && <span className="fam-badge" aria-label={`${familyUnread} new`}>{familyUnread > 99 ? '99+' : familyUnread}</span>}
          </button>
        ))}
        {/* 0.90.3: the panel is a drawer on narrow screens - close it here, not only through the scrim */}
        {onClose && <button type="button" className="tabs-close" aria-label="Close panel" title="Close panel" onClick={onClose}>✕</button>}
      </div>
      {/* 0.90.0: the family room (FamilyPanel renders it here through a portal) */}
      <div className="family-slot" ref={setFamilySlot} hidden={tab !== 'family'} />
      {tab === 'workspace' &&
        (root && authOk && project ? (
          <WorkspaceTab
            key={(project.folderId || '') + (chat?.folderId || '')}
            root={root}
            workspacePath={owner?.projects?.[project.projectId]?.workspaceDriveRelativePath || null}
            scratchPath={chat ? chatScratchPath(chat) : null}
          />
        ) : (
          <p className="muted pad">No project selected.</p>
        ))}
      {tab === 'project' && (project && authOk ? <ProjectTab key={project.folderId} project={project} owner={owner} host={host} chat={chat ?? null} onProjectChanged={onProjectChanged} /> : <p className="muted pad">No project selected.</p>)}
      <div className="pad chat-slot" ref={setChatSlot} hidden={tab !== 'chat' || !chat} />
      {tab === 'chat' && !chat && <p className="muted pad">Open a chat to see its settings.</p>}
      <div className="pad files-slot" ref={setFilesSlot} hidden={tab !== 'artifacts' || !chat} />
      {tab === 'artifacts' && (chat && root && authOk && !chat.draft
        ? <FilesTab key={chat.folderId} chat={chat} root={root} canWrite={chat.shareRole !== 'viewer' && project?.shareRole !== 'viewer'} />
        : <p className="muted pad">Open a chat to see its files.</p>)}
      {tab === 'host' && <HostTab owner={owner} health={health} limits={limits} host={host} hostPref={hostPref} onHostPref={onHostPref} />}
      {tab === 'session' &&
        (chat && !chat.draft && authOk ? (
          <SessionTab key={chat.chatId} chat={chat} chats={chats ?? null} env={env || ''} contextFolders={project?.contextFolders} />
        ) : (
          <p className="muted pad">Open a chat to see its session.</p>
        ))}
    </div>
  );
}

function ProjectTab({
  project, owner, host, chat, onProjectChanged,
}: { project: Project; owner: OwnerInfo | null; host: HostDirs | null; chat: Chat | null; onProjectChanged: (p: Project) => void }) {
  const [instr, setInstr] = useState<string | null>(null);
  const [fileId, setFileId] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [files, setFiles] = useState<DriveFile[] | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [memRev, setMemRev] = useState(0);
  const [memDirty, setMemDirty] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    void (async () => {
      try {
        const r = await readProjectInstructions(project);
        setInstr(r.text);
        setFileId(r.fileId);
        setFiles(await listProjectFiles(project));
      } catch (e) {
        setMsg(categorize(e).message);
      }
    })();
  }, [project]);

  async function download(f: DriveFile) {
    const bytes = await getBytes(f.id);
    const url = URL.createObjectURL(new Blob([bytes]));
    const a = document.createElement('a');
    a.href = url;
    a.download = f.name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }

  // 0.45.0: read-only view for projects shared with the user as a viewer
  if (project.shareRole === 'viewer') {
    return (
      <div className="pad">
        <h4>{project.title}</h4>
        <p className="muted small"><code>{project.projectId}</code> - shared with you as a viewer (read only)</p>
        {msg && <p className="error-text small">{msg}</p>}
        <h5>Sharing</h5>
        <SharingSection
          kind="project"
          id={project.projectId}
          project={project}
          transferable={false}
          onProjectChanged={onProjectChanged}
          onError={setMsg}
        />
        <h5>Instructions</h5>
        {instr === null
          ? <div className="muted small">Loading...</div>
          : <div className="small pre">{instr || '(none)'}</div>}
      </div>
    );
  }

  return (
    <div className="pad">
      <h4>{project.title}</h4>
      <p className="muted small">
        <code>{project.projectId}</code> · {project.environmentId}
        {project.legacy ? ' · legacy (no project.json)' : ''}
      </p>
      {project.description && <p className="small">{project.description}</p>}
      <p>
        {/* 0.25.0: archived projects leave the project list (shown under "Archived projects") */}
        <button
          className="btn tiny ghost"
          onClick={async () => {
            try {
              onProjectChanged(await saveProjectMeta(project, { archived: !project.archived }));
            } catch (e) {
              setMsg(categorize(e).message);
            }
          }}
        >
          {project.archived ? 'Restore project from archive' : 'Archive project'}
        </button>
      </p>

      <p className="small">
        {/* 0.28.0: project type and corporate flag (flag only, no effect yet) */}
        <label>
          Type{' '}
          <select
            value={project.kind ?? 'work'}
            onChange={async (e) => {
              try {
                onProjectChanged(await saveProjectMeta(project, { kind: e.target.value as 'work' | 'service' | 'sandbox' }));
              } catch (err) {
                setMsg(categorize(err).message);
              }
            }}
          >
            <option value="work">work</option>
            <option value="service">service</option>
            <option value="sandbox">sandbox</option>
          </select>
        </label>{' '}
        <label title="Marks the project as holding employer data. For now only a flag.">
          <input
            type="checkbox"
            checked={project.corporate === true}
            onChange={async (e) => {
              try {
                onProjectChanged(await saveProjectMeta(project, { corporate: e.target.checked }));
              } catch (err) {
                setMsg(categorize(err).message);
              }
            }}
          />{' '}
          Corporate ({corporate().title} data)
        </label>
      </p>

      {/* 0.45.0: sharing */}
      <h5>Sharing</h5>
      <SharingSection
        kind="project"
        id={project.projectId}
        project={project}
        transferable={!project.legacy && !owner?.projects?.[project.projectId]}
        onProjectChanged={onProjectChanged}
        onError={setMsg}
      />
      {chat && !chat.draft && !!chat.chatId && (
        <>
          <h5>This chat</h5>
          <SharingSection
            kind="chat"
            id={chat.chatId}
            project={project}
            transferable={false}
            onProjectChanged={onProjectChanged}
            onError={setMsg}
          />
        </>
      )}

      <h5>Instructions</h5>
      <p className="muted tiny">Sent at the start of every new agent session in this project.</p>
      {instr === null ? (
        <div className="muted small">Loading…</div>
      ) : (
        <>
          <textarea
            className="instr"
            rows={8}
            value={instr}
            onChange={(e) => {
              setInstr(e.target.value);
              setDirty(true);
            }}
            aria-label="Project instructions"
          />
          <button
            className="btn small"
            disabled={!dirty || saving}
            onClick={async () => {
              setSaving(true);
              try {
                setFileId(await saveProjectInstructions(project, instr, fileId));
                setDirty(false);
              } catch (e) {
                setMsg(categorize(e).message);
              } finally {
                setSaving(false);
              }
            }}
          >
            {saving ? 'Saving…' : 'Save instructions'}
          </button>
        </>
      )}

      {/* 0.45.0: not for projects shared with me */}
      {!project.shareRole && (
        <>
          <h5>Environment profile ({project.environmentId})</h5>
          <p className="muted tiny">
            Preferences, style, contacts of this environment; shared by all its projects and sent to every new agent session.
          </p>
          <EnvProfileEditor env={project.environmentId} onError={setMsg} />
          <h5>Dossier ({project.environmentId})</h5>
          <p className="muted tiny">
            What agents know about you: facts about you, current affairs and people around you. The protocol secretary adds and
            updates rows from your chats on its own; fix or delete wrong ones here. New agent sessions get it in every project;
            shared projects and chats get only the people.
          </p>
          <DossierEditor env={project.environmentId} onError={setMsg} />
        </>
      )}

      <h5>Memory</h5>
      <p className="muted tiny">
        Facts and decisions of this project (context/memory.md). New agent sessions get it together with short summaries
        of the 5 newest chats; agents add entries with the aiwb-memory tool. A line <code>{'<!-- memory: index -->'}</code> in
        the file sends Claude and GPT on the home PC an index (long entries cut) that they expand with aiwb-memory get/search;
        the site agent always gets the whole file. Every night a consolidation pass proposes merges and removals of outdated
        entries below (Revisions); nothing changes until you accept.
      </p>
      <MemoryCandidatesSection project={project} locked={memDirty} onAccepted={() => setMemRev((n) => n + 1)} onError={setMsg} />
      <ContextFileEditor key={memRev} project={project} name="memory.md" label="Save memory" onError={setMsg} onDirty={setMemDirty} />

      <h5>Context folders</h5>
      {host && <ContextFoldersSection project={project} owner={owner} host={host} onChanged={onProjectChanged} />}

      <h5>Schedules</h5>
      <SchedulesSection project={project} />

      <h5>Project files</h5>
      <button className="btn small" onClick={() => input.current?.click()}>Upload</button>
      <input
        ref={input}
        type="file"
        multiple
        hidden
        onChange={async (e) => {
          const list = Array.from(e.target.files || []);
          for (const f of list) {
            setMsg(`Uploading ${f.name}…`);
            try {
              await uploadProjectFile(project, f);
            } catch (x) {
              setMsg(categorize(x).message);
            }
          }
          setMsg(null);
          setFiles(await listProjectFiles(project));
        }}
      />
      <p className="muted tiny">Stored in Drive under the project. v0.1 does not send them to agents automatically; attach them to a message.</p>
      {files === null ? (
        <div className="muted small">Loading…</div>
      ) : files.length === 0 ? (
        <div className="muted small">No files.</div>
      ) : (
        <ul className="file-list">
          {files.map((f) => (
            <li key={f.id}>
              <button className="link" onClick={() => void download(f)}>{f.name}</button>
              <span className="muted tiny"> {formatBytes(Number(f.size || 0))}</span>
            </li>
          ))}
        </ul>
      )}
      {msg && <p className="small muted">{msg}</p>}
    </div>
  );
}

const HOST_PREFS: { id: HostPref; label: string; title: string }[] = [
  { id: 'auto', label: 'Automatic', title: 'Home while its Bridge takes jobs, otherwise Site; device tools and home-only agents go to Home' },
  { id: 'home-main', label: 'Home', title: 'Always the home computer (jobs wait in its queue while it is off)' },
  { id: 'site', label: 'Site', title: 'Always the server agent on the CO site' },
];

function HostTab({ owner, health, limits, host, hostPref, onHostPref }: {
  owner: OwnerInfo | null; health: HostHealth; limits: LimitsFile | null; host: HostDirs | null;
  hostPref?: HostPref; onHostPref?: (p: HostPref) => void;
}) {
  const desktop = (
    <>
      {onHostPref && (
        <>
          <h5>Executor of new jobs</h5>
          <div className="exec-pref" role="radiogroup" aria-label="Executor of new jobs">
            {HOST_PREFS.map((p) => (
              <button key={p.id} type="button" role="radio" aria-checked={hostPref === p.id} title={p.title}
                className={`btn tiny ${hostPref === p.id ? 'primary' : 'ghost'}`} onClick={() => onHostPref(p.id)}>{p.label}</button>
            ))}
          </div>
        </>
      )}
      <h5>CO Desktop</h5>
      <DesktopDownloadSection />
      {/* 0.84.0: CO Resident for macOS (agent/mac.php) */}
      {isMacDesktop() && (
        <p>
          <button className="btn small" onClick={openMacOffer}>On a Mac: install the CO helper…</button>
        </p>
      )}
      {/* 0.49.0: web device; CO Desktop is a device itself */}
      {!insideDesktop() && (
        <>
          <h5>This browser as a device</h5>
          <WebDeviceSection />
        </>
      )}
      <h5>Google Drive for desktop</h5>
      <DriveDesktopGuide />
    </>
  );
  if (!owner) return <div className="pad"><p className="muted">owner.json not found — the home agent has not announced itself.</p>{desktop}</div>;
  const caps = owner.capabilities || [];
  return (
    <div className="pad">
      {desktop}
      <dl className="kv">
        <dt>Status</dt>
        <dd><span className={`host-pill ${health}`}>{health}</span></dd>
        <dt>Host</dt>
        <dd>{owner.hostId}</dd>
        <dt>Machine</dt>
        <dd>{owner.machineName}</dd>
        <dt>Bridge</dt>
        <dd>v{owner.bridgeVersion}</dd>
        <dt>Updated</dt>
        <dd>{formatLocal(owner.updatedUtc)}</dd>
        <dt>Capabilities</dt>
        <dd>{caps.join(', ') || '—'}</dd>
        <dt>Resume</dt>
        <dd>{caps.includes('conversation-resume') ? 'Claude, Codex' : 'no'}</dd>
        <dt>Native fork</dt>
        <dd>{caps.includes('codex-fork') ? 'Codex (UI in v0.2)' : 'no'}</dd>
      </dl>
      <h5>Notifications</h5>
      <NotificationsSection />
      <h5>Agents</h5>
      <AutoSwitchToggle />
      <h5 id="models-section">Models</h5>
      <ModelsSection />
      <h5>Data</h5>
      <DataSourceToggle />
      <DbQueueToggle />
      <h5>Usage limits</h5>
      <LimitsDetail limits={limits} />
      <h5>Usage by project</h5>
      <UsageSection />
      <h5>Health</h5>
      <HealthSection />
      <h5>Jobs</h5>
      <OpsSection />
      <h5>Account connectors</h5>
      <AccountConnectorsSection host={host} />
    </div>
  );
}

function WorkspaceTab({ root, workspacePath, scratchPath }: { root: Root; workspacePath: string | null; scratchPath: string | null }) {
  return (
    <div className="pad">
      <h5>Project workspace</h5>
      {workspacePath ? (
        <DriveTree root={root} path={workspacePath} />
      ) : (
        <p className="muted small">The workspace is on the home computer, outside Google Drive. Changed files arrive as artifacts.</p>
      )}
      {scratchPath && (
        <>
          <h5>Chat scratch</h5>
          <DriveTree root={root} path={scratchPath} emptyText="Empty. Chat-mode agents work here." />
        </>
      )}
    </div>
  );
}

function DriveTree({ root, path, emptyText }: { root: Root; path: string; emptyText?: string }) {
  const [folderId, setFolderId] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    resolveDrivePath(root, path).then(setFolderId).catch(() => setFolderId(null));
  }, [root, path]);
  if (folderId === undefined) return <div className="muted small">Loading…</div>;
  if (folderId === null) return <div className="muted small">{emptyText || 'Folder not found in Drive.'}</div>;
  return <FolderNode id={folderId} path={path} emptyText={emptyText} />;
}

// path: the folder relative to AI-Workbench (0.63.0, for Open in canvas)
function FolderNode({ id, path, emptyText }: { id: string; path: string; emptyText?: string }) {
  const [items, setItems] = useState<DriveFile[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const load = () =>
    children(id)
      .then((list) =>
        setItems(
          list
            .filter((f) => !f.name.startsWith('.co-attachments'))
            .sort((a, b) => Number(b.mimeType === FOLDER_MIME) - Number(a.mimeType === FOLDER_MIME) || a.name.localeCompare(b.name)),
        ),
      )
      .catch((e) => setErr(categorize(e).message));
  useEffect(() => {
    void load();
  }, [id]); // eslint-disable-line react-hooks/exhaustive-deps
  if (err) return <div className="error-text small">{err}</div>;
  if (!items) return <div className="muted small">Loading…</div>;
  if (!items.length) return <div className="muted small">{emptyText || 'Empty folder.'}</div>;
  return (
    <ul className="tree">
      <li>
        <button className="link" onClick={() => void load()}>Refresh</button>
      </li>
      {items.map((f) => (f.mimeType === FOLDER_MIME ? <FolderItem key={f.id} f={f} path={`${path}/${f.name}`} /> : <FileItem key={f.id} f={f} path={`${path}/${f.name}`} />))}
    </ul>
  );
}

function FolderItem({ f, path }: { f: DriveFile; path: string }) {
  const [open, setOpen] = useState(false);
  return (
    <li>
      <div className="tree-row">
        <button className="folder" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? '▾' : '▸'} 📁 {f.name}
        </button>
      </div>
      {open && <FolderNode id={f.id} path={path} />}
    </li>
  );
}

function FileItem({ f, path }: { f: DriveFile; path: string }) {
  const goStudy = useNavigate();
  const [busy, setBusy] = useState(false);
  const [picking, setPicking] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  async function download() {
    setBusy(true);
    try {
      const url = URL.createObjectURL(new Blob([await getBytes(f.id)]));
      const a = document.createElement('a');
      a.href = url;
      a.download = f.name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } catch (e) {
      setMsg(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <li>
      <div className="tree-row">
        <span className="name" title={f.name}>📄 {f.name}</span>
        <span className="muted tiny">{formatBytes(Number(f.size || 0))}</span>
        <FileMenu
          name={f.name}
          busy={busy}
          items={[
            { label: 'Download', onClick: () => void download() },
            { label: 'Open in Drive', onClick: () => window.open(f.webViewLink || `https://drive.google.com/file/d/${f.id}/view`, '_blank', 'noopener') },
            hasGoogleDocs(f.name) && { label: 'Open in Google Docs', onClick: () => window.open(googleDocsUrl(f.id, f.name) || '', '_blank', 'noopener') },
            canvasItem(f.name, path),
            studyItem(f.name, path, (href) => goStudy(href)),
            { label: 'Save to Drive…', onClick: () => setPicking(true) },
          ]}
        />
      </div>
      {msg && <div className="small muted">{msg}</div>}
      {picking && (
        <FolderPicker
          onCancel={() => setPicking(false)}
          onPick={async (target) => {
            setPicking(false);
            setBusy(true);
            try {
              const c = await copyToFolder(f.id, target.id, f.name);
              setMsg(`Saved: ${target.path}/${c.name}`);
            } catch (e) {
              setMsg(categorize(e).message);
            } finally {
              setBusy(false);
            }
          }}
        />
      )}
    </li>
  );
}

// 0.18.0: chats and messages are read from the database; off = Drive listing + database content (the 0.15 path).
function DataSourceToggle() {
  const [on, setOn] = useState(dbReadsPrimary());
  return (
    <label className="small" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
      <input
        type="checkbox"
        checked={on}
        onChange={(e) => {
          setDbReadsPrimary(e.target.checked);
          setOn(e.target.checked);
        }}
      />
      Read chats and messages from the database (off: list Drive folders first). Applies to the next load.
    </label>
  );
}

// 0.23.0: finished jobs per project and agent from the database (tokens and Claude Code's API-equivalent cost).
// 0.74.0 (schema 15): from job_stats - API-equivalent for Codex too (server price table), tool calls, run time;
// a click on a project lists its chats.
function usd(v: number | null | undefined): string {
  return v == null ? '—' : v >= 10 ? String(Math.round(v)) : v.toFixed(2);
}

function UsageTable({ rows, byChat, onPick, picked }: { rows: UsageRow[]; byChat?: boolean; onPick?: (r: UsageRow) => void; picked?: string | null }) {
  const envs = new Set(rows.map((r) => r.env));
  const total = rows.reduce(
    (t, r) => ({ jobs: t.jobs + r.jobs, out: t.out + r.outputTokens, cost: t.cost + (r.apiUsd ?? r.costUsd ?? 0), calls: t.calls + (r.toolCalls || 0), ms: t.ms + (r.durationMs || 0) }),
    { jobs: 0, out: 0, cost: 0, calls: 0, ms: 0 },
  );
  const right = { textAlign: 'right' } as const;
  return (
    <table className="usage-table" style={{ width: '100%', borderCollapse: 'collapse', marginTop: 6 }}>
      <thead>
        <tr>
          <th style={{ textAlign: 'left' }}>{byChat ? 'Chat' : 'Project'}</th>
          <th style={{ textAlign: 'left' }}>Agent</th>
          <th style={right} title="finished jobs (failed)">Jobs</th>
          <th style={right} title="tool calls (failed)">Calls</th>
          <th style={right} title="run time of the jobs">Time</th>
          <th style={right} title="output tokens">Out</th>
          <th style={right} title="API-equivalent: the cost Claude Code reports; for Codex tokens x the server price table (meta.api_prices). Subscriptions are not billed per job.">≈API $</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => {
          const key = `${r.env}/${r.projectId}/${r.chatId || ''}/${r.agent}`;
          return (
            <tr key={key} style={!byChat && picked === r.projectId ? { background: 'var(--panel-2)' } : undefined}>
              <td>
                {byChat ? <span title={r.chatId}>{r.chatTitle || r.chatId}</span>
                  : onPick ? <button className="link" onClick={() => onPick(r)} title="Chats of this project">{r.title || r.projectId}</button>
                  : r.title || r.projectId}
                {!byChat && envs.size > 1 && r.env ? <span className="muted"> · {r.env}</span> : null}
              </td>
              <td>{r.agent}</td>
              <td style={right}>{r.jobs}{r.failed ? <span className="muted"> ({r.failed})</span> : null}</td>
              <td style={right}>{r.toolCalls == null ? '—' : fmtTok(r.toolCalls)}{r.toolErrors ? <span className="muted"> ({r.toolErrors})</span> : null}</td>
              <td style={right}>{r.durationMs == null ? '—' : fmtMs(r.durationMs)}</td>
              <td style={right}>{fmtTok(r.outputTokens)}</td>
              <td style={right}>{usd(r.apiUsd ?? r.costUsd)}</td>
            </tr>
          );
        })}
        <tr style={{ fontWeight: 600 }}>
          <td>Total</td>
          <td />
          <td style={right}>{total.jobs}</td>
          <td style={right}>{fmtTok(total.calls)}</td>
          <td style={right}>{fmtMs(total.ms)}</td>
          <td style={right}>{fmtTok(total.out)}</td>
          <td style={right}>{usd(total.cost)}</td>
        </tr>
      </tbody>
    </table>
  );
}

function UsageSection() {
  const [days, setDays] = useState(30);
  const [rows, setRows] = useState<UsageRow[] | null | undefined>(undefined);
  const [pick, setPick] = useState<UsageRow | null>(null);
  const [chats, setChats] = useState<UsageRow[] | null | undefined>(undefined);
  useEffect(() => {
    let live = true;
    setRows(undefined);
    void dbUsage(days).then((r) => live && setRows(r));
    return () => {
      live = false;
    };
  }, [days]);
  useEffect(() => {
    let live = true;
    setChats(undefined);
    if (pick?.projectId) void dbUsage(days, { chatsOf: pick.projectId }).then((r) => live && setChats(r));
    return () => {
      live = false;
    };
  }, [days, pick?.projectId]);
  return (
    <div className="small">
      <label>
        Period{' '}
        <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
          <option value={1}>24 hours</option>
          <option value={7}>7 days</option>
          <option value={30}>30 days</option>
          <option value={90}>90 days</option>
          <option value={365}>365 days</option>
        </select>
      </label>
      {rows === undefined && <p className="muted">Loading…</p>}
      {rows === null && <p className="muted">Database not reachable.</p>}
      {rows && !rows.length && <p className="muted">No finished jobs in this period.</p>}
      {rows && rows.length > 0 && <UsageTable rows={rows} picked={pick?.projectId} onPick={(r) => setPick(pick?.projectId === r.projectId ? null : r)} />}
      {pick && (
        <>
          <p style={{ marginBottom: 0 }}><strong>{pick.title || pick.projectId}</strong> by chat <button className="link" onClick={() => setPick(null)}>close</button></p>
          {chats === undefined && <p className="muted">Loading…</p>}
          {chats === null && <p className="muted">Per-chat usage not available (database not reachable or not updated yet).</p>}
          {chats && <UsageTable rows={chats} byChat />}
        </>
      )}
      <p className="muted">≈API $ is what the jobs would cost on the API: Claude Code reports it for Claude jobs; Codex jobs are priced from their tokens by the server's price table. Subscriptions are not billed per job. Each answer's "Timeline and trace" shows one job.</p>
    </div>
  );
}

// 0.19.0: jobs without files go to the database queue when the host announces "db-queue" (Bridge 1.3.0).
// 0.71.0: global switch of the server's automatic Claude <-> Codex retry (autoswitch-lib.php); chats can opt out one by one
function AutoSwitchToggle() {
  const [st, setSt] = useState<{ enabled: boolean; canSet: boolean } | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    dbAutoSwitch().then(setSt, (e) => setErr(categorize(e).message));
  }, []);
  return (
    <>
      <label className="small" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
        <input
          type="checkbox"
          checked={!!st?.enabled}
          disabled={!st?.canSet}
          onChange={(e) => {
            setErr(null);
            dbAutoSwitch(e.target.checked).then((r) => { setSt(r); resetAutoSwitchGlobal(); }, (x) => setErr(categorize(x).message));
          }}
        />
        Auto-switch: when Claude or Codex stops on its usage limit, sign-in or model before doing anything, send the message once to the other agent (all chats; ⇄ chip turns it off per chat).
      </label>
      {st && !st.canSet && <div className="muted tiny">Only the owner changes this.</div>}
      {err && <div className="error-text tiny">{err}</div>}
    </>
  );
}

function DbQueueToggle() {
  const [on, setOn] = useState(dbQueueOn());
  return (
    <label className="small" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
      <input
        type="checkbox"
        checked={on}
        onChange={(e) => {
          setDbQueueOn(e.target.checked);
          setOn(e.target.checked);
        }}
      />
      Send jobs through the database queue (off: Drive inbox). Jobs with attached files always use Drive.
    </label>
  );
}

// 0.38.0: project memory candidates proposed by the chat protocol secretary (schema 9). Accept makes the server append
// the (edited) line to context/memory.md on Drive and mark the candidate accepted. Disabled while the memory editor
// below has unsaved edits (its save would overwrite the new line). 0.51.0: the panel shows counts only; the
// candidates are reviewed in a table window (MemoryReview.tsx) with bulk accept/reject.
function MemoryCandidatesSection({ project, locked, onAccepted, onError }: { project: Project; locked: boolean; onAccepted: () => void; onError: (m: string) => void }) {
  const [loading, setLoading] = useState(true);
  const [candidates, setCandidates] = useState<MemoryCandidate[]>([]);
  const [counts, setCounts] = useState({ accepted: 0, rejected: 0 });
  // 0.51.0: review modal (MemoryReview.tsx); acceptedAny tracks accepts made while it is open
  const [open, setOpen] = useState(false);
  const [acceptedAny, setAcceptedAny] = useState(false);

  useEffect(() => {
    if (project.legacy) return;
    let cancelled = false;
    setLoading(true);
    setOpen(false);
    dbMemoryCandidates(project.projectId)
      .then((res) => {
        if (cancelled) return;
        setCandidates(res.candidates);
        setCounts({ accepted: res.accepted, rejected: res.rejected });
      })
      .catch((err) => {
        if (!cancelled) onError(categorize(err).message);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [project.projectId, project.legacy]); // eslint-disable-line react-hooks/exhaustive-deps

  // Called per id by the dialog, so the list and counts stay accurate even if a batch stops
  function handleDecided(ids: number[], action: 'accept' | 'reject' | 'gone') {
    setCandidates((prev) => prev.filter((c) => ids.indexOf(c.id) < 0));
    if (action === 'gone') return;
    setCounts((prev) =>
      action === 'accept'
        ? { accepted: prev.accepted + ids.length, rejected: prev.rejected }
        : { accepted: prev.accepted, rejected: prev.rejected + ids.length },
    );
    if (action === 'accept') setAcceptedAny(true);
  }

  // Reload the memory editor once on close if anything was accepted (memory.md changed)
  function handleClose() {
    setOpen(false);
    if (acceptedAny) {
      setAcceptedAny(false);
      onAccepted();
    }
  }

  if (project.legacy) return null;
  if (loading) return <div className="muted small">{'Loading…'}</div>;

  const dialog = open ? (
    <MemoryReviewDialog candidates={candidates} locked={locked} onClose={handleClose} onDecided={handleDecided} onError={onError} />
  ) : null;

  if (candidates.length === 0) {
    const suffix = counts.accepted || counts.rejected ? ' (accepted ' + counts.accepted + ', rejected ' + counts.rejected + ')' : '';
    return (
      <>
        <div className="muted tiny">No memory candidates{suffix}</div>
        {dialog}
      </>
    );
  }

  const adds = candidates.filter((c) => c.kind === 'add');
  const decisions = adds.filter((c) => c.tag === 'decision').length;
  const facts = adds.length - decisions;
  const rules = candidates.filter((c) => c.kind === 'rule').length;
  const revisions = candidates.length - adds.length - rules;
  const word = (n: number, one: string) => n + ' ' + one + (n === 1 ? '' : 's');
  const summary = word(candidates.length, 'candidate') + ': ' + word(decisions, 'decision') + ', ' + word(facts, 'fact')
    + (revisions ? ', ' + word(revisions, 'revision') : '') + (rules ? ', ' + word(rules, 'correction') : '');

  return (
    <>
      <div className="memcand-summary">
        <div className="tiny">
          {summary}
          {locked && <span className="muted">{' Save the memory edits below first.'}</span>}
        </div>
        <button className="btn small" onClick={() => setOpen(true)}>
          {'Review…'}
        </button>
      </div>
      {dialog}
    </>
  );
}

// 0.26.0: editor of one file in the project's context folder (memory.md)
function ContextFileEditor({ project, name, label, onError, onDirty }: { project: Project; name: string; label: string; onError: (m: string) => void; onDirty?: (d: boolean) => void }) {
  const [text, setText] = useState<string | null>(null);
  const [fileId, setFileId] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [base, setBase] = useState('');
  const [reload, setReload] = useState(0);
  useEffect(() => onDirty?.(dirty), [dirty]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    readProjectContextFile(project, name)
      .then((r) => {
        setText(r.text);
        setBase(r.text);
        setFileId(r.fileId);
        setDirty(false);
      })
      .catch((e) => onError(categorize(e).message));
  }, [project, name, reload]); // eslint-disable-line react-hooks/exhaustive-deps
  if (text === null) return <div className="muted small">Loading…</div>;
  return (
    <>
      <textarea
        className="instr"
        rows={8}
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          setDirty(true);
        }}
        aria-label={name}
      />
      <button
        className="btn small"
        disabled={!dirty || saving}
        onClick={async () => {
          setSaving(true);
          try {
            // 0.38.0: the file is also written by the server (accepted memory candidates) and by agents (aiwb-memory);
            // never overwrite a version this editor has not seen.
            const now = await readProjectContextFile(project, name);
            if (now.text !== base) {
              onError(`${name} was changed elsewhere since it was opened. Copy your edits, press Reload, and apply them again.`);
              return;
            }
            setFileId(await saveProjectContextFile(project, name, text, now.fileId || fileId));
            setBase(text);
            setDirty(false);
          } catch (e) {
            onError(categorize(e).message);
          } finally {
            setSaving(false);
          }
        }}
      >
        {saving ? 'Saving…' : label}
      </button>{' '}
      <button className="btn small" disabled={saving} onClick={() => setReload((n) => n + 1)}>
        Reload
      </button>
    </>
  );
}

// 0.28.0: environment profile (database, kv env/<env>/profile)
function EnvProfileEditor({ env, onError }: { env: string; onError: (m: string) => void }) {
  const [saved, setSaved] = useState<EnvProfile | null>(null);
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    let cancelled = false;
    setSaved(null);
    dbGetProfile(env)
      .then((p) => {
        if (cancelled) return;
        setSaved(p);
        setText(p.text);
      })
      .catch((e) => {
        if (cancelled) return;
        onError(categorize(e).message);
        setSaved({ text: '', updatedUtc: null, updatedBy: null, rev: '' });
        setText('');
      });
    return () => {
      cancelled = true;
    };
  }, [env]); // eslint-disable-line react-hooks/exhaustive-deps
  if (!saved) return <div className="muted small">Loading…</div>;
  return (
    <>
      <textarea className="instr" rows={8} aria-label="Environment profile" value={text} onChange={(e) => setText(e.target.value)} />
      {saved.updatedUtc && (
        <div className="muted tiny">
          Last saved {new Date(saved.updatedUtc).toLocaleString()}
          {saved.updatedBy ? ` by ${saved.updatedBy}` : ''}
        </div>
      )}
      <button
        className="btn small"
        disabled={text === saved.text || saving}
        onClick={async () => {
          setSaving(true);
          try {
            await dbPutProfile(env, text, saved.rev);
            setSaved(await dbGetProfile(env).catch(() => ({ text, updatedUtc: new Date().toISOString(), updatedBy: null, rev: '?' })));
          } catch (e) {
            const m = categorize(e).message;
            if (/conflict/i.test(m) || /conflict/i.test(String((e as Error)?.message || e))) {
              // 0.75.0: changed elsewhere (e.g. an accepted correction rule): the draft stays in the box, the lines of the
              // newer saved version missing from it are listed; the next Save goes against the newer version
              const fresh = await dbGetProfile(env).catch(() => null);
              if (fresh) setSaved(fresh);
              const mine = new Set(text.split(/\r?\n/).map((l) => l.trim()));
              const missing = fresh ? fresh.text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !mine.has(l)) : [];
              onError('The profile changed since you opened it (another device or an accepted correction rule). Your text is kept, not saved yet.'
                + (missing.length ? ' Lines of the newer version missing from it: ' + missing.slice(0, 10).join(' | ') : '') + ' Merge and press Save again.');
            } else onError(m);
          } finally {
            setSaving(false);
          }
        }}
      >
        {saving ? 'Saving…' : 'Save profile'}
      </button>
    </>
  );
}

// 0.85.0 (schema 19): the dossier of an environment - list by section, search, edit, delete, add
function DossierEditor({ env, onError }: { env: string; onError: (m: string) => void }) {
  const [rows, setRows] = useState<DossierRow[] | null>(null);
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<{ id: number | null; section: DossierRow['section']; subject: string; text: string; rev?: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(() => {
    dbDossierGet(env).then(setRows).catch((e) => { onError(categorize(e).message); setRows([]); });
  }, [env]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (open) load(); }, [open, load]);
  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try { await fn(); setEditing(null); load(); } catch (e) { onError(categorize(e).message); } finally { setBusy(false); }
  };
  if (!open) return <button className="btn small" onClick={() => setOpen(true)}>Show dossier</button>;
  if (!rows) return <div className="muted small">Loading…</div>;
  const f = filter.trim().toLowerCase();
  const shown = f ? rows.filter((r) => `${r.subject} ${r.text}`.toLowerCase().includes(f)) : rows;
  const editor = editing && (
    <div className="dossier-row">
      <div className="dossier-text">
        <select value={editing.section} onChange={(e) => setEditing({ ...editing, section: e.target.value as DossierRow['section'] })}>
          {(Object.keys(DOSSIER_SECTION_LABEL) as DossierRow['section'][]).map((s) => <option key={s} value={s}>{DOSSIER_SECTION_LABEL[s]}</option>)}
        </select>
        <input placeholder={editing.section === 'people' ? 'Name' : editing.section === 'state' ? 'Topic' : 'Topic (optional)'} value={editing.subject}
          onChange={(e) => setEditing({ ...editing, subject: e.target.value })} />
        <textarea rows={2} value={editing.text} onChange={(e) => setEditing({ ...editing, text: e.target.value })} />
        <button className="btn small" disabled={busy || editing.text.trim().length < 3}
          onClick={() => act(() => dbDossierPut(env, editing.id === null ? 'add' : 'edit',
            { ...(editing.id === null ? {} : { id: editing.id, rev: editing.rev }), section: editing.section, subject: editing.subject, text: editing.text }))}>
          Save
        </button>{' '}
        <button className="link" onClick={() => setEditing(null)}>Cancel</button>
      </div>
    </div>
  );
  return (
    <>
      <div className="row">
        <input placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} />{' '}
        <button className="btn small" disabled={busy} onClick={() => setEditing({ id: null, section: 'me', subject: '', text: '' })}>+ Add</button>{' '}
        <span className="muted tiny">{rows.length} rows</span>
      </div>
      {editing?.id === null && editor}
      {(Object.keys(DOSSIER_SECTION_LABEL) as DossierRow['section'][]).map((sec) => {
        const list = shown.filter((r) => r.section === sec);
        if (!list.length) return null;
        return (
          <details key={sec} open={!!f || sec !== 'people'}>
            <summary><b>{DOSSIER_SECTION_LABEL[sec]}</b> <span className="muted tiny">{list.length}</span></summary>
            {list.map((r) => editing?.id === r.id ? <div key={r.id}>{editor}</div> : (
              <div key={r.id} className="dossier-row small">
                <div className="dossier-text">
                  {r.subject && <b>{r.subject}: </b>}{r.text}
                  <span className="muted tiny"> {r.date || ''} · d{r.id} · {r.author}{r.chatId ? ` · ${r.chatId}${r.msg ? ` #${r.msg}` : ''}` : ''}</span>
                </div>
                <button className="link tiny" disabled={busy} onClick={() => setEditing({ id: r.id, section: r.section, subject: r.subject, text: r.text, rev: r.updatedUtc })}>Edit</button>
                {r.section === 'state' && <button className="link tiny" disabled={busy} title="The affair is finished" onClick={() => act(() => dbDossierPut(env, 'close', { id: r.id }))}>Done</button>}
                <button className="link tiny" disabled={busy} onClick={() => act(() => dbDossierPut(env, 'delete', { id: r.id }))}>Delete</button>
              </div>
            ))}
          </details>
        );
      })}
    </>
  );
}

// 0.35.0: where the chat runs, its usage, permissions and context, changed files, lineage
const LINEAGE_LABEL: Record<string, string> = {
  branch: 'Branch of',
  followup: 'Continues',
  side: 'Side question from',
  subtask: 'Subtask of',
};

const CHILD_TAG: Record<string, string> = {
  branch: 'branch',
  followup: 'follow-up',
  side: 'side',
  subtask: 'subtask',
};

export function SessionTab({
  chat,
  chats,
  env,
  contextFolders,
}: {
  chat: Chat;
  chats: Chat[] | null;
  env: string;
  contextFolders?: ContextFolder[];
}) {
  const [events, setEvents] = useState<ChatEvent[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setEvents(null);
    setError(null);
    loadChatEvents(chat)
      .then((evs) => {
        if (!cancelled) setEvents(evs);
      })
      .catch((e) => {
        if (!cancelled) setError(categorize(e).message);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chat.chatId, chat.updatedUtc]); // refreshed after every turn

  if (error) return <div className="error-text small pad">{error}</div>;
  if (!events) return <div className="muted small pad">Loading…</div>;

  const assistants = events
    .filter((e) => e.type === 'assistant_message')
    .sort((a, b) => a.sequence - b.sequence);
  const last = assistants.length ? assistants[assistants.length - 1] : null;

  let inputTokens = 0;
  let outputTokens = 0;
  let cost = 0;
  let hasCost = false;
  for (const e of assistants) {
    inputTokens += (e.usage?.inputTokens ?? 0) + (e.usage?.cachedInputTokens ?? 0);
    outputTokens += e.usage?.outputTokens ?? 0;
    if (typeof e.usage?.costUsd === 'number') {
      cost += e.usage.costUsd;
      hasCost = true;
    }
  }

  const seen = new Set<string>();
  const files: { name: string; path: string }[] = [];
  for (let i = assistants.length - 1; i >= 0; i--) {
    const arts = assistants[i].artifacts ?? [];
    for (let j = arts.length - 1; j >= 0; j--) {
      const p = arts[j].driveRelativePath;
      if (!p || seen.has(p)) continue;
      seen.add(p);
      files.push({ name: p.split('/').filter(Boolean).pop() ?? p, path: p });
    }
  }

  const perms = chatPermissions(chat.permissions);
  const parent = chat.parent;
  const parentTitle = parent
    ? parent.title || chats?.find((c) => c.chatId === parent.chatId)?.title || parent.chatId
    : '';
  const children = (chats ?? []).filter((c) => c.parent?.chatId === chat.chatId);

  return (
    <div className="pad session-tab">
      <h5>Where it runs</h5>
      {last ? (
        <dl className="kv">
          <dt>Executor</dt>
          <dd>{last.hostId === 'site' ? `Site (${location.host})` : 'Home computer'}</dd>
          <dt>Agent</dt>
          <dd>{last.agent ?? '—'}</dd>
          <dt>Model</dt>
          <dd>{last.modelReported || last.model || 'default'}</dd>
          <dt>Mode</dt>
          <dd>{last.mode ?? '—'}</dd>
          <dt>Status</dt>
          <dd>{last.status ?? '—'}</dd>
          <dt>Session</dt>
          <dd>{last.sessionId ? `${last.sessionId.slice(0, 8)}…` : 'new'}</dd>
          <dt>Last answer</dt>
          <dd>{formatLocal(last.completedUtc || last.createdUtc)}</dd>
        </dl>
      ) : (
        <div className="muted small">No answers yet.</div>
      )}

      <h5>Usage in this chat</h5>
      <dl className="kv">
        <dt>Turns</dt>
        <dd>{assistants.length.toLocaleString()}</dd>
        <dt>Input tokens</dt>
        <dd>{inputTokens.toLocaleString()}</dd>
        <dt>Output tokens</dt>
        <dd>{outputTokens.toLocaleString()}</dd>
        <dt>Cost</dt>
        <dd>{hasCost ? `$${cost.toLocaleString(undefined, { maximumFractionDigits: 4 })}` : '—'}</dd>
      </dl>

      <h5>Permissions and context</h5>
      <dl className="kv">
        <dt>Files</dt>
        <dd>{perms?.files === 'read' ? 'read-only' : 'write'}</dd>
        <dt>Shell</dt>
        <dd>{perms?.shell === false ? 'off' : 'on'}</dd>
        <dt>Web</dt>
        <dd>{perms?.web === false ? 'off' : 'on'}</dd>
        <dt>Context</dt>
        <dd>
          {contextFolders && contextFolders.length ? (
            contextFolders
              .map((f) => `${f.label} (${f.access === 'write' ? 'rw' : 'ro'})`)
              .join(', ')
          ) : (
            <span className="muted">none</span>
          )}
        </dd>
      </dl>

      <h5>Exported files</h5>
      <div className="muted small">{files.length} files</div>
      {files.length > 0 && (
        <ul className="small">
          {files.slice(0, 15).map((f) => (
            <li key={f.path} title={f.path}>
              {f.name}
            </li>
          ))}
        </ul>
      )}

      <h5>Lineage</h5>
      {parent && (
        <div className="small">
          {LINEAGE_LABEL[parent.kind] ?? 'From'}{' '}
          <a href={`/e/${env}/p/${chat.projectId}/c/${parent.chatId}`}>{parentTitle}</a>
          {` after message #${parent.seq}`}
        </div>
      )}
      {children.length > 0 ? (
        <ul className="small">
          {children.map((c) => (
            <li key={c.chatId}>
              <a href={`/e/${env}/p/${c.projectId}/c/${c.chatId}`}>{c.title || c.chatId}</a>{' '}
              <span className="muted small">{CHILD_TAG[c.parent!.kind] ?? c.parent!.kind}</span>
            </li>
          ))}
        </ul>
      ) : (
        <div className="muted small">No branches, side questions or subtasks.</div>
      )}
    </div>
  );
}
