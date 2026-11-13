// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6, point `import`): the core side of importing chats from outside services. An import module
// (its ui.page, permission import.write) parses the export itself and hands the core one conversation at a time
// (core.import.write); the core writes it into the AI-Workbench tree as chat.json + one sealed event segment, attachment
// texts as files, and keeps imports/index.json ("<source>:<id>"), which the import tools at home use too, so nothing is
// imported twice. The core knows no service: a module may write only the sources its manifest declares (provides.import),
// with their label from the manifest, and only into its person's own projects (not shared, not archived).
import type { CoreCalls } from './ModuleSlot';
import type { ModuleInfo } from './extBridge';
import { DEFAULT_BRANCH, type EnvironmentId } from './config';
import { createFile, ensurePath, findChild, getJson, updateContent } from './drive';
import { indexKey, parseImportWrite, type ImportTarget } from './importPoint';
import type { AttachmentRef, ChatEvent, Project } from './types';
import { newChatId, newEventId, nowUtc, safeFileName, sha256Hex, uniqueNames } from './util';
import { createProject, type Root } from './workbench';

interface IndexEntry { projectId: string; chatId: string; importedUtc: string }
interface ImportIndex { fileId: string | null; entries: Record<string, IndexEntry> }

async function readIndex(root: Root): Promise<ImportIndex> {
  const dir = await ensurePath(root.id, ['imports']);
  const f = await findChild(dir, 'index.json');
  if (!f) return { fileId: null, entries: {} };
  try {
    const j = await getJson<{ entries: Record<string, IndexEntry> }>(f.id);
    return { fileId: f.id, entries: j.entries || {} };
  } catch {
    return { fileId: f.id, entries: {} };
  }
}

// Saves of one index run one after the other (the first one creates the file); a save asked for while another waits
// joins the waiting one, so a long import uploads the index about once per finished upload, not once per chat.
interface SaveState { running: Promise<void> | null; queued: Promise<void> | null }
const saving = new WeakMap<ImportIndex, SaveState>();
function saveIndex(root: Root, index: ImportIndex): Promise<void> {
  let st = saving.get(index);
  if (!st) saving.set(index, (st = { running: null, queued: null }));
  const s = st;
  if (s.queued) return s.queued;
  const start = (): Promise<void> => {
    s.queued = null;
    const run = saveIndexNow(root, index);
    s.running = run;
    return run.finally(() => { if (s.running === run) s.running = null; });
  };
  if (!s.running) return start();
  s.queued = s.running.catch(() => undefined).then(start);
  return s.queued;
}

async function saveIndexNow(root: Root, index: ImportIndex): Promise<void> {
  // entries the tools at home added meanwhile (nightly sync) are kept: theirs first, ours on top
  if (index.fileId) {
    try {
      const j = await getJson<{ entries: Record<string, IndexEntry> }>(index.fileId);
      for (const [k, e] of Object.entries(j.entries || {})) if (!index.entries[k]) index.entries[k] = e;
    } catch { /* unreadable: ours is written */ }
  }
  const body = JSON.stringify({ schemaVersion: 1, entries: index.entries }, null, 1);
  if (index.fileId) {
    await updateContent(index.fileId, body, 'application/json');
  } else {
    const dir = await ensurePath(root.id, ['imports']);
    index.fileId = (await createFile(dir, 'index.json', body, 'application/json')).id;
  }
}

/**
 * The core calls of the import point for a page of environment `env`. The index and the projects made by this page are
 * kept between calls; core.import.finish saves the index and lets the shell refresh its project list (and open one).
 */
export function importCalls(o: {
  root: Root; env: EnvironmentId;
  /** projects of the shell; null while they load */
  projects: () => Project[] | null;
  /** refresh the project list; open = navigate (a project id, or '' for the environment) */
  onFinished: (open?: string) => void | Promise<void>;
}): CoreCalls {
  let indexP: Promise<ImportIndex> | null = null;
  const made = new Map<string, Promise<Project>>();
  const created: string[] = [];
  // one read shared by concurrent first calls
  const loadIndex = () => (indexP ??= readIndex(o.root).catch((e) => { indexP = null; throw e; }));
  // own projects only: a shared project (another person's) or an archived one is not a target
  const own = (list: Project[]) => list.filter((p) => !p.archived && !p.shareRole && p.environmentId === o.env);
  const projectsLoaded = async (): Promise<Project[]> => {
    for (let i = 0; i < 75; i++) {
      const l = o.projects();
      if (l) return l;
      await new Promise((r) => setTimeout(r, 200));
    }
    return o.projects() || [];
  };
  /** label of a source the calling module declares; refuses any other source */
  const sourceOf = (m: ModuleInfo, source: string): string => {
    const s = (m.imports || []).find((x) => x.source === source);
    if (!s) throw new Error(`source ${source} is not declared by module ${m.id}`);
    return (typeof s.label === 'string' && s.label.trim()) || source;
  };

  const ensureProject = (t: ImportTarget, sourceLabel: string): Promise<Project> => {
    if (t.kind === 'existing') {
      const p = own(o.projects() || []).find((x) => x.projectId === t.projectId);
      return p ? Promise.resolve(p) : Promise.reject(new Error(`Project ${t.projectId} not found (or not your own).`));
    }
    const hit = made.get(t.projectId);
    if (hit) return hit;
    const p = (async () => {
      const projectsId = await ensurePath(o.root.id, ['projects']);
      const existing = await findChild(projectsId, t.projectId, true);
      if (existing) {
        // a project made by an earlier import: reused as it is
        const pj = await findChild(existing.id, 'project.json');
        if (!pj) throw new Error(`Folder projects/${t.projectId} exists without project.json.`);
        const doc = await getJson<Omit<Project, 'folderId'>>(pj.id);
        if (doc.environmentId !== o.env) throw new Error(`Project ${t.projectId} belongs to environment ${doc.environmentId}.`);
        if (doc.archived) throw new Error(`Project ${t.projectId} is archived.`);
        return { ...doc, folderId: existing.id };
      }
      const project = await createProject(o.root, {
        projectId: t.projectId, environmentId: o.env, title: t.title,
        description: t.description || `Imported from ${sourceLabel}`, defaultAgentId: 'claude',
      });
      created.push(project.projectId);
      if (t.instructions?.trim()) {
        const ctx = await ensurePath(project.folderId, ['context']);
        await createFile(ctx, 'project.md', t.instructions, 'text/markdown');
      }
      if (t.docs?.length) {
        const files = await ensurePath(project.folderId, ['files']);
        const names = uniqueNames(t.docs.map((d) => safeFileName(d.name)));
        for (let i = 0; i < t.docs.length; i++) await createFile(files, names[i], t.docs[i].content, 'text/plain');
      }
      return project;
    })();
    made.set(t.projectId, p);
    p.catch(() => made.delete(t.projectId));
    return p;
  };

  return {
    'core.import.context': async () => {
      const [ix, list] = await Promise.all([loadIndex(), projectsLoaded()]);
      return {
        env: o.env,
        projects: own(list).map((p) => ({ projectId: p.projectId, title: p.title })),
        imported: Object.fromEntries(Object.entries(ix.entries).map(([k, e]) => [k, { projectId: e.projectId, chatId: e.chatId }])),
      };
    },

    'core.import.write': async (args, m) => {
      const { conversation: conv, target } = parseImportWrite(args);
      const sourceLabel = sourceOf(m, conv.source);
      const ix = await loadIndex();
      const key = indexKey(conv.source, conv.id);
      if (ix.entries[key]) return { skipped: true, ...ix.entries[key] };
      const project = await ensureProject(target, sourceLabel);
      const chatId = newChatId();
      const chatsId = await ensurePath(project.folderId, ['chats']);
      const folderId = await ensurePath(chatsId, [chatId]);
      const importedUtc = nowUtc();
      const chatDoc = {
        chatId, projectId: project.projectId, title: conv.title, rootBranchId: DEFAULT_BRANCH,
        createdUtc: conv.createdUtc || importedUtc, updatedUtc: conv.updatedUtc || importedUtc, defaultMode: 'chat',
        importedFrom: { source: conv.source, id: conv.id, importedUtc },
      };
      await createFile(folderId, 'chat.json', JSON.stringify(chatDoc, null, 2), 'application/json');

      // attachment texts the export carries
      const refs = new Map<number, AttachmentRef[]>();
      const withText = conv.messages.map((m, i) => ({ m, i })).filter(({ m }) => m.attachments?.some((a) => a.text));
      if (withText.length) {
        const dir = await ensurePath(folderId, ['branches', DEFAULT_BRANCH, 'attachments', 'imported']);
        for (const { m, i } of withText) {
          const list: AttachmentRef[] = [];
          for (const a of m.attachments || []) {
            if (!a.text) {
              list.push({ displayName: a.name, path: '', sizeBytes: 0, sha256: '' });
              continue;
            }
            const bytes = new TextEncoder().encode(a.text);
            const name = `${String(i).padStart(4, '0')}-${safeFileName(a.name)}${/\.(txt|md)$/i.test(a.name) ? '' : '.txt'}`;
            const f = await createFile(dir, name, bytes, 'text/plain');
            list.push({ displayName: a.name, path: name, sizeBytes: bytes.length, sha256: await sha256Hex(bytes), fileId: f.id });
          }
          refs.set(i, list);
        }
      }

      const events: ChatEvent[] = conv.messages.map((m, i) => ({
        schemaVersion: 1,
        eventId: newEventId(),
        sequence: i + 1,
        type: m.role === 'user' ? 'user_message' : 'assistant_message',
        createdUtc: m.createdUtc || conv.createdUtc || importedUtc,
        chatId,
        branchId: DEFAULT_BRANCH,
        text: m.text,
        imported: { source: conv.source, originalId: conv.id },
        ...(m.role === 'user'
          ? {
              author: sourceLabel,
              attachments: refs.get(i) || (m.attachments || []).map((a) => ({ displayName: a.name, path: '', sizeBytes: 0, sha256: '' })),
            }
          : { status: 'completed', model: conv.model || null }),
      }));
      const evDir = await ensurePath(folderId, ['events']);
      await createFile(evDir, '000000-imported.segment.json',
        JSON.stringify({ schemaVersion: 1, source: conv.source, originalId: conv.id, events }), 'application/json');

      ix.entries[key] = { projectId: project.projectId, chatId, importedUtc };
      // saved after every chat (joined saves): a page closed in the middle of an import leaves no chat outside the index;
      // the chat is written either way, a failed save is retried with the next one and by core.import.finish
      await saveIndex(o.root, ix).catch((e) => console.warn('import index not saved yet', e));
      return { skipped: false, projectId: project.projectId, chatId };
    },

    'core.import.finish': async (args) => {
      if (indexP) await saveIndex(o.root, await indexP);
      // open: a project id, or '' for the environment; without it the shell only refreshes its list
      const open = typeof args.open !== 'string' ? undefined : /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/.test(args.open) ? args.open : '';
      await o.onFinished(open);
      return { created: [...created] };
    },
  };
}
