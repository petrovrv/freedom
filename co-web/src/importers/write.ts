// SPDX-License-Identifier: AGPL-3.0-or-later
// Writes selected conversations and projects from an export into the AI-Workbench tree.
// Each imported chat becomes chat.json + one sealed event segment; the export itself
// never leaves the browser except for the selected content written to Drive.

import { DEFAULT_BRANCH, type EnvironmentId } from '../config';
import { createFile, ensurePath, findChild, getJson, updateContent } from '../drive';
import type { AttachmentRef, ChatEvent, Project } from '../types';
import { mapLimit, newChatId, newEventId, nowUtc, safeFileName, sha256Hex, uniqueNames } from '../util';
import { createProject, type Root } from '../workbench';
import type { ImportBundle, ImportedConversation, ImportedProject, ImportSource } from './parse';

export interface ImportIndexEntry {
  projectId: string;
  chatId: string;
  importedUtc: string;
}

export interface ImportIndex {
  fileId: string | null;
  entries: Record<string, ImportIndexEntry>; // "<source>:<conversation id>"
}

export const indexKey = (source: ImportSource, id: string) => `${source}:${id}`;

export async function readImportIndex(root: Root): Promise<ImportIndex> {
  const dir = await ensurePath(root.id, ['imports']);
  const f = await findChild(dir, 'index.json');
  if (!f) return { fileId: null, entries: {} };
  try {
    const j = await getJson<{ entries: Record<string, ImportIndexEntry> }>(f.id);
    return { fileId: f.id, entries: j.entries || {} };
  } catch {
    return { fileId: f.id, entries: {} };
  }
}

async function saveImportIndex(root: Root, index: ImportIndex): Promise<void> {
  const body = JSON.stringify({ schemaVersion: 1, entries: index.entries }, null, 1);
  if (index.fileId) {
    await updateContent(index.fileId, body, 'application/json');
  } else {
    const dir = await ensurePath(root.id, ['imports']);
    index.fileId = (await createFile(dir, 'index.json', body, 'application/json')).id;
  }
}

const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'g', ґ: 'g', д: 'd', е: 'e', ё: 'e', є: 'ie', ж: 'zh', з: 'z', и: 'i', і: 'i', ї: 'i',
  й: 'i', к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts',
  ч: 'ch', ш: 'sh', щ: 'shch', ъ: '', ы: 'y', ь: '', э: 'e', ю: 'iu', я: 'ia',
};

// Bridge-safe project id from a display name: ^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$
export function projectIdFrom(name: string, prefix: string): string {
  const latin = name
    .toLowerCase()
    .split('')
    .map((c) => TRANSLIT[c] ?? c)
    .join('')
    .normalize('NFKD')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  const id = `${prefix}-${latin}`.slice(0, 40).replace(/-+$/g, '');
  return /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/.test(id) ? id : `${prefix}-${Date.now().toString(36)}`;
}

export type ProjectTarget =
  | { kind: 'new'; projectId: string; title: string; source?: ImportedProject }
  | { kind: 'existing'; project: Project };

export interface ImportPlan {
  root: Root;
  env: EnvironmentId;
  bundle: ImportBundle;
  conversations: ImportedConversation[];
  targetFor: (c: ImportedConversation) => ProjectTarget;
  includeProjectContent: boolean;
  index: ImportIndex;
  onProgress: (done: number, total: number, label: string) => void;
}

export interface ImportResult {
  imported: number;
  skipped: number;
  failed: { title: string; error: string }[];
  projects: string[];
}

const sourceLabel = (s: ImportSource) => (s === 'claude' ? 'claude.ai' : 'ChatGPT');

export async function runImport(plan: ImportPlan): Promise<ImportResult> {
  const { root, env, bundle, index } = plan;
  const result: ImportResult = { imported: 0, skipped: 0, failed: [], projects: [] };
  const projectCache = new Map<string, Promise<Project>>();

  const ensureProject = (t: ProjectTarget): Promise<Project> => {
    if (t.kind === 'existing') return Promise.resolve(t.project);
    const hit = projectCache.get(t.projectId);
    if (hit) return hit;
    const p = (async () => {
      const projectsId = await ensurePath(root.id, ['projects']);
      const existing = await findChild(projectsId, t.projectId, true);
      if (existing) {
        // Re-import into a project created by an earlier run: reuse it as is.
        const pj = await findChild(existing.id, 'project.json');
        if (!pj) throw new Error(`Folder projects/${t.projectId} exists without project.json.`);
        const doc = await getJson<Omit<Project, 'folderId'>>(pj.id);
        if (doc.environmentId !== env) throw new Error(`Project ${t.projectId} belongs to environment ${doc.environmentId}.`);
        return { ...doc, folderId: existing.id };
      }
      const project = await createProject(root, {
        projectId: t.projectId, environmentId: env, title: t.title,
        description: t.source?.description || `Imported from ${sourceLabel(bundle.source)}`, defaultAgentId: 'claude',
      });
      result.projects.push(project.projectId);
      if (plan.includeProjectContent && t.source) {
        if (t.source.instructions?.trim()) {
          const ctx = await ensurePath(project.folderId, ['context']);
          await createFile(ctx, 'project.md', t.source.instructions, 'text/markdown');
        }
        if (t.source.docs.length) {
          const files = await ensurePath(project.folderId, ['files']);
          const names = uniqueNames(t.source.docs.map((d) => safeFileName(d.name)));
          for (let i = 0; i < t.source.docs.length; i++) {
            await createFile(files, names[i], t.source.docs[i].content, 'text/plain');
          }
        }
      }
      return project;
    })();
    projectCache.set(t.projectId, p);
    return p;
  };

  let done = 0;
  const total = plan.conversations.length;
  let sinceSave = 0;

  await mapLimit(plan.conversations, 3, async (conv) => {
    const key = indexKey(conv.source, conv.id);
    if (index.entries[key]) {
      result.skipped++;
      plan.onProgress(++done, total, conv.title);
      return;
    }
    try {
      const project = await ensureProject(plan.targetFor(conv));
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

      // Attachment texts that the export carries (claude.ai extracted_content).
      const attachmentRefs = new Map<number, AttachmentRef[]>();
      const withText = conv.messages.map((m, i) => ({ m, i })).filter(({ m }) => m.attachments?.some((a) => a.text));
      if (withText.length) {
        const dir = await ensurePath(folderId, ['branches', DEFAULT_BRANCH, 'attachments', 'imported']);
        for (const { m, i } of withText) {
          const refs: AttachmentRef[] = [];
          for (const a of m.attachments || []) {
            if (!a.text) {
              refs.push({ displayName: a.name, path: '', sizeBytes: 0, sha256: '' });
              continue;
            }
            const bytes = new TextEncoder().encode(a.text);
            const name = `${String(i).padStart(4, '0')}-${safeFileName(a.name)}${/\.(txt|md)$/i.test(a.name) ? '' : '.txt'}`;
            const f = await createFile(dir, name, bytes, 'text/plain');
            refs.push({ displayName: a.name, path: name, sizeBytes: bytes.length, sha256: await sha256Hex(bytes), fileId: f.id });
          }
          attachmentRefs.set(i, refs);
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
              author: sourceLabel(conv.source),
              attachments: attachmentRefs.get(i) || (m.attachments || []).map((a) => ({ displayName: a.name, path: '', sizeBytes: 0, sha256: '' })),
            }
          : { status: 'completed', model: conv.model || null }),
      }));
      const evDir = await ensurePath(folderId, ['events']);
      await createFile(
        evDir,
        '000000-imported.segment.json',
        JSON.stringify({ schemaVersion: 1, source: conv.source, originalId: conv.id, events }),
        'application/json',
      );

      index.entries[key] = { projectId: project.projectId, chatId, importedUtc };
      result.imported++;
      if (++sinceSave >= 10) {
        sinceSave = 0;
        await saveImportIndex(root, index);
      }
    } catch (e) {
      result.failed.push({ title: conv.title, error: (e as Error).message });
    }
    plan.onProgress(++done, total, conv.title);
  });

  await saveImportIndex(root, index);
  return result;
}
