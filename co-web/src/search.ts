// SPDX-License-Identifier: AGPL-3.0-or-later
// Chat index, project activity and AI search over chats and projects (CO Web 0.12.0).
// The index comes from a few Drive metadata queries over the whole tree, not one listing per project.
// AI search: Claude Haiku on the CO site (/agent/search.php) turns the request into search terms
// (with translations and synonyms), Drive full-text search finds the chats whose messages contain them,
// and a second Haiku call ranks the chats and projects by meaning.

import { getToken } from './auth';
import { FOLDER_MIME, getJson, list, q, resolveFile } from './drive';
import type { Chat, Project } from './types';
import { loadChatEvents } from './workbench';
import { mapLimit } from './util';
import { dbSearchChats, dbSearchDocs } from './dbapi';

export interface ChatRef {
  chatFolderId: string;
  chatJsonId: string;
  projectFolderId: string;
  modifiedUtc: string;
}

export interface SearchHit {
  projectId: string;
  projectTitle: string;
  chatId?: string;
  title: string;
  why: string;
  activityUtc?: string;
  // 0.27.0: a project document (path relative to AI-Workbench/projects)
  docPath?: string;
}

// chat folder -> project folder; folder ids are stable, so the map only grows.
const chatToProject = new Map<string, string>();
// events folder -> chat folder
const eventsToChat = new Map<string, string>();
// chat folders outside the current project list (other environments); asked again when the list changes.
const missed = new Set<string>();
let missedFor = '';

async function mapChatFolders(projects: Project[], wanted: string[]) {
  const projectFolders = new Set(projects.map((p) => p.folderId));
  const chatsDirs = (await list(`name='chats' and mimeType=${q(FOLDER_MIME)} and trashed=false`, 'id,parents'))
    .filter((f) => f.parents?.some((x) => projectFolders.has(x)));
  for (let i = 0; i < chatsDirs.length; i += 30) {
    const batch = chatsDirs.slice(i, i + 30);
    const parentOf = new Map(batch.map((d) => [d.id, d.parents!.find((x) => projectFolders.has(x))!]));
    const kids = await list(
      `mimeType=${q(FOLDER_MIME)} and trashed=false and (${batch.map((d) => `${q(d.id)} in parents`).join(' or ')})`, 'id,parents');
    for (const k of kids) {
      const pf = k.parents?.map((x) => parentOf.get(x)).find(Boolean);
      if (pf) chatToProject.set(k.id, pf);
    }
  }
  for (const id of wanted) if (!chatToProject.has(id)) missed.add(id);
}

// Every chat.json of the given projects with its last modification (CO Web on send, Bridge on scheduled runs).
export async function chatRefs(projects: Project[]): Promise<ChatRef[]> {
  const key = projects.map((p) => p.folderId).sort().join(',');
  if (key !== missedFor) {
    missed.clear();
    missedFor = key;
  }
  const files = await list(`name='chat.json' and trashed=false`, 'id,parents,modifiedTime');
  const parentOf = (f: { parents?: string[] }) => f.parents?.[0] || '';
  const unknown = files.map(parentOf).filter((id) => id && !chatToProject.has(id) && !missed.has(id));
  if (unknown.length) await mapChatFolders(projects, unknown);
  const projectFolders = new Set(projects.map((p) => p.folderId));
  return files
    .map((f) => ({ chatFolderId: parentOf(f), chatJsonId: f.id, projectFolderId: chatToProject.get(parentOf(f)) || '', modifiedUtc: f.modifiedTime || '' }))
    .filter((r) => projectFolders.has(r.projectFolderId));
}

// Project folder -> newest chat activity.
export function projectActivity(refs: ChatRef[]): Record<string, string> {
  const out: Record<string, string> = {};
  for (const r of refs) if ((out[r.projectFolderId] || '') < r.modifiedUtc) out[r.projectFolderId] = r.modifiedUtc;
  return out;
}

export const projectFreshness = (p: Project, activity: Record<string, string>) =>
  [p.updatedUtc || '', activity[p.folderId] || ''].sort().pop() || '';

// ---------- Titles (cached by chat.json id + modification time) ----------

const TITLE_CACHE_KEY = 'co.chatTitles.v1';
type TitleEntry = { m: string; id: string; t: string };
let titleCache: Record<string, TitleEntry> | null = null;

function titles(): Record<string, TitleEntry> {
  if (!titleCache) {
    try {
      titleCache = JSON.parse(localStorage.getItem(TITLE_CACHE_KEY) || '{}');
    } catch {
      titleCache = {};
    }
  }
  return titleCache!;
}

async function chatTitles(refs: ChatRef[]): Promise<Map<string, { chatId: string; title: string }>> {
  const cache = titles();
  await mapLimit(refs.filter((r) => cache[r.chatJsonId]?.m !== r.modifiedUtc), 8, async (r) => {
    try {
      const c = await getJson<Chat>(r.chatJsonId);
      cache[r.chatJsonId] = { m: r.modifiedUtc, id: c.chatId, t: c.title };
    } catch {
      /* unreadable chat.json: the chat is left out of the search */
    }
  });
  try {
    localStorage.setItem(TITLE_CACHE_KEY, JSON.stringify(cache));
  } catch {
    /* storage full: the cache lives for this page only */
  }
  const out = new Map<string, { chatId: string; title: string }>();
  for (const r of refs) if (cache[r.chatJsonId]) out.set(r.chatFolderId, { chatId: cache[r.chatJsonId].id, title: cache[r.chatJsonId].t });
  return out;
}

// ---------- Full-text hits in chat messages ----------

async function messageHits(terms: string[]): Promise<Map<string, Set<string>>> {
  const perTerm = await mapLimit(terms, 4, async (t) =>
    ({ t, files: await list(`fullText contains ${q(t)} and name contains 'message' and trashed=false`, 'id,parents', undefined, 1000) }));
  const unknown = new Set<string>();
  for (const { files } of perTerm) for (const f of files) if (f.parents?.[0] && !eventsToChat.has(f.parents[0])) unknown.add(f.parents[0]);
  if (unknown.size) {
    for (const d of await list(`name='events' and mimeType=${q(FOLDER_MIME)} and trashed=false`, 'id,parents')) {
      if (d.parents?.[0]) eventsToChat.set(d.id, d.parents[0]);
    }
  }
  const hits = new Map<string, Set<string>>(); // chat folder -> matched terms
  for (const { t, files } of perTerm) {
    for (const f of files) {
      const chat = eventsToChat.get(f.parents?.[0] || '');
      if (!chat) continue;
      if (!hits.has(chat)) hits.set(chat, new Set());
      hits.get(chat)!.add(t);
    }
  }
  return hits;
}

// ---------- Server calls ----------

async function callAgent<T>(path: string, body: unknown): Promise<T> {
  const token = getToken();
  const r = await fetch(path, {
    method: 'POST',
    headers: { 'X-CO-Auth': '1', 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    credentials: 'same-origin',
    cache: 'no-store',
    body: JSON.stringify(body),
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(`AI: ${j.error || r.status}${j.detail ? ` (${j.detail})` : ''}`);
  return j as T;
}

const MAX_RANKED_CHATS = 300;

interface Candidate {
  project: Project;
  chatId: string;
  title: string;
  modifiedUtc: string;
  matched: string[];
}

// 0.16.0: chats and their full-text matches from the database (one call, ~100 ms).
async function dbCandidates(projects: Project[], terms: string[], query: string): Promise<Candidate[] | null> {
  const env = projects[0]?.environmentId;
  const clean = terms.map((t) => t.trim().slice(0, 60)).filter(Boolean).slice(0, 10);
  if (!env || !clean.length) return null;
  const rows = await dbSearchChats(env, clean, MAX_RANKED_CHATS, query);
  if (!rows) return null;
  const byId = new Map(projects.map((p) => [p.projectId, p]));
  return rows
    .filter((r) => byId.has(r.projectId))
    .map((r) => ({ project: byId.get(r.projectId)!, chatId: r.chatId, title: r.title, modifiedUtc: r.updatedUtc || '', matched: r.matches }));
}

// Drive path (before 0.16.0): chat index from metadata queries plus Drive full-text search.
async function driveCandidates(projects: Project[], terms: string[]): Promise<Candidate[]> {
  const refs = await chatRefs(projects);
  const [names, hits] = await Promise.all([chatTitles(refs), messageHits(terms.slice(0, 10))]);
  const byFolder = new Map(projects.map((p) => [p.folderId, p]));
  return refs
    .filter((r) => names.has(r.chatFolderId))
    .map((r) => ({
      project: byFolder.get(r.projectFolderId)!, chatId: names.get(r.chatFolderId)!.chatId, title: names.get(r.chatFolderId)!.title,
      modifiedUtc: r.modifiedUtc, matched: [...(hits.get(r.chatFolderId) || [])],
    }))
    .sort((a, b) => b.matched.length - a.matched.length || b.modifiedUtc.localeCompare(a.modifiedUtc))
    .slice(0, MAX_RANKED_CHATS);
}

export async function aiSearch(projects: Project[], query: string, onStep: (s: string) => void): Promise<SearchHit[]> {
  onStep('Understanding the request…');
  const { terms } = await callAgent<{ terms: string[] }>('/agent/search.php', { step: 'expand', query });
  onStep(`Searching chats and documents for: ${terms.join(', ')}`);
  const env = projects[0]?.environmentId;
  const docsP = env ? dbSearchDocs(env, [query, ...terms].join(' '), 8).catch(() => null) : Promise.resolve(null);
  const chats = (await dbCandidates(projects, terms, query)) || (await driveCandidates(projects, terms));
  const docs = (await docsP) || [];

  onStep(`Ranking ${chats.length} chats and ${projects.length} projects…`);
  const res = await callAgent<{ projects?: { id: string; why?: string }[]; results?: { id: string; why?: string }[] }>('/agent/search.php', {
    step: 'rank',
    query,
    projects: projects.map((p, i) => ({ id: `P${i}`, title: p.title, description: (p.description || '').slice(0, 200) })),
    chats: chats.map((c, i) => ({
      id: `C${i}`, project: c.project.title, title: c.title, date: c.modifiedUtc.slice(0, 10), matches: c.matched,
    })),
  });

  const out: SearchHit[] = [];
  for (const r of res.projects || []) {
    const p = projects[Number(r.id.slice(1))];
    if (r.id.startsWith('P') && p) out.push({ projectId: p.projectId, projectTitle: p.title, title: p.title, why: r.why || '', activityUtc: p.updatedUtc });
  }
  for (const r of res.results || []) {
    const c = chats[Number(r.id.slice(1))];
    if (r.id.startsWith('C') && c) {
      out.push({ projectId: c.project.projectId, projectTitle: c.project.title, chatId: c.chatId, title: c.title, why: r.why || '', activityUtc: c.modifiedUtc });
    }
  }
  // Documents by full-text score (not ranked by Haiku)
  for (const d of docs) {
    out.push({
      projectId: d.projectId, projectTitle: d.projectTitle, title: d.name, why: (d.snippets[0] || '').replace(/\s+/g, ' '),
      activityUtc: d.modified, docPath: d.path,
    });
  }
  return out;
}

// 0.27.0: Drive file of a document hit (path <projectId>/a/b/name under the project folder); opens it in a new tab.
export async function openDocument(projects: Project[], hit: SearchHit): Promise<void> {
  const win = window.open('about:blank', '_blank'); // before the await: popup blockers allow it only in the click
  try {
    const p = projects.find((x) => x.projectId === hit.projectId);
    const parts = (hit.docPath || '').split('/').slice(1);
    const name = parts.pop();
    const f = p && name ? await resolveFile(p.folderId, parts, name) : null;
    if (!f) throw new Error(`Not found on Drive: ${hit.docPath}`);
    if (win) win.location.href = `https://drive.google.com/file/d/${f.id}/view`;
  } catch (e) {
    win?.close();
    throw e;
  }
}

// ---------- Suggested chat title ----------

// Beginning and end of the conversation go to /agent/title.php (Claude Haiku); the server trims them further.
export async function suggestChatTitle(chat: Chat): Promise<string> {
  const msgs = (await loadChatEvents(chat))
    .filter((e) => (e.type === 'user_message' || e.type === 'assistant_message') && e.text?.trim())
    .sort((a, b) => a.sequence - b.sequence);
  if (!msgs.length) throw new Error('The chat has no messages yet.');
  const picked = msgs.length > 8 ? [...msgs.slice(0, 5), ...msgs.slice(-3)] : msgs;
  const r = await callAgent<{ title: string }>('/agent/title.php', {
    messages: picked.map((e) => ({ role: e.type === 'assistant_message' ? 'assistant' : 'user', text: (e.text || '').slice(0, 2000) })),
  });
  return r.title;
}
// 0.58.0: "Suggest title" for a project: the same server call, fed with the project description and its chat titles.
export async function suggestProjectTitle(p: Project, chats: Chat[]): Promise<string> {
  const titles = chats.filter((c) => !c.draft && c.title).map((c) => '- ' + c.title).slice(0, 60);
  if (!titles.length && !p.description) throw new Error('The project has no chats yet.');
  const text = [`Project "${p.title}" with these chats:`, ...titles, p.description ? `\nDescription: ${p.description}` : ''].join('\n');
  const r = await callAgent<{ title: string }>('/agent/title.php', { messages: [{ role: 'user', text: text.slice(0, 2000) }] });
  return r.title;
}
