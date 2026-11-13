// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6, point `import`): checks of what an import module hands the core. The module (its ui.page in a
// sandboxed frame, or a tool at home through the database API) reads the export of an outside service; the core writes
// chats, messages and attachment texts. Pure functions; used by importWriter.ts and tested in importPoint.selftest.ts.

export interface ImportMessage { role: 'user' | 'assistant'; text: string; createdUtc?: string; attachments?: { name: string; text?: string }[] }
export interface ImportConversation {
  source: string; id: string; title: string; createdUtc: string; updatedUtc: string; model?: string; messages: ImportMessage[];
}
export type ImportTarget =
  | { kind: 'new'; projectId: string; title: string; description?: string; instructions?: string; docs?: { name: string; content: string }[] }
  | { kind: 'existing'; projectId: string };
export interface ImportWrite { conversation: ImportConversation; target: ImportTarget }

const SOURCE = /^[a-z0-9][a-z0-9-]{0,31}$/;
const PROJECT_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/;
const MAX_MESSAGES = 20000;

class Bad extends Error {}

const s = (v: unknown, name: string, max: number, optional = false): string => {
  if ((v === undefined || v === null) && optional) return '';
  if (typeof v !== 'string' || v.length > max) throw new Bad(`bad ${name}`);
  return v;
};
const iso = (v: unknown, name: string): string => {
  const x = s(v, name, 40, true);
  if (x && isNaN(new Date(x).getTime())) throw new Bad(`bad ${name}`);
  return x;
};

/** An outside service a module imports from (manifest provides.import): how the core names it on imported chats. */
export interface ImportSource { source: string; label: string; speaker: string }
const sources = new Map<string, ImportSource>();

/** Called with the enabled modules (ModuleSlot.tsx useModules); a source of no enabled module shows its raw name. */
export function setImportSources(list: { source: string; label?: string; speaker?: string }[]): void {
  sources.clear();
  for (const x of list) {
    if (!x || typeof x.source !== 'string' || !SOURCE.test(x.source)) continue;
    const label = typeof x.label === 'string' && x.label.trim() ? x.label.trim().slice(0, 40) : x.source;
    sources.set(x.source, { source: x.source, label, speaker: typeof x.speaker === 'string' && x.speaker.trim() ? x.speaker.trim().slice(0, 60) : label });
  }
}
/** Tag text of an imported chat or message ("imported · <label>"). */
export const importLabel = (source: string): string => sources.get(source)?.label ?? source;
/** Speaker of an imported assistant message. */
export const importSpeaker = (source: string): string => sources.get(source)?.speaker ?? source;

/** Key of a conversation in the import index (`imports/index.json`), shared with the import tools at home. */
export const indexKey = (source: string, id: string) => `${source}:${id}`;

/** The arguments of core.import.write, checked; throws an Error with a short reason. */
export function parseImportWrite(a: Record<string, unknown>): ImportWrite {
  const c = a.conversation as Record<string, unknown> | undefined;
  if (!c || typeof c !== 'object' || Array.isArray(c)) throw new Bad('bad conversation');
  const source = s(c.source, 'source', 32);
  if (!SOURCE.test(source)) throw new Bad('bad source');
  const id = s(c.id, 'id', 200);
  if (!id.trim()) throw new Bad('bad id');
  if (!Array.isArray(c.messages) || c.messages.length > MAX_MESSAGES) throw new Bad('bad messages');
  const messages: ImportMessage[] = c.messages.map((m: unknown, i: number) => {
    const x = m as Record<string, unknown>;
    if (!x || typeof x !== 'object' || (x.role !== 'user' && x.role !== 'assistant')) throw new Bad(`bad message ${i}`);
    const out: ImportMessage = { role: x.role, text: s(x.text, `text of message ${i}`, 16_000_000) };
    const cu = iso(x.createdUtc, `time of message ${i}`);
    if (cu) out.createdUtc = cu;
    if (x.attachments !== undefined) {
      if (!Array.isArray(x.attachments) || x.attachments.length > 100) throw new Bad(`bad attachments of message ${i}`);
      out.attachments = x.attachments.map((at: unknown) => {
        const y = at as Record<string, unknown>;
        const name = s(y?.name, 'attachment name', 300) || 'attachment';
        const text = s(y?.text, 'attachment text', 2_000_000, true);
        return text ? { name, text } : { name };
      });
    }
    return out;
  });
  const conversation: ImportConversation = {
    source, id, title: (typeof c.title === 'string' ? c.title.replace(/\s+/g, ' ').trim().slice(0, 300) : '') || 'Untitled', createdUtc: iso(c.createdUtc, 'createdUtc'),
    updatedUtc: iso(c.updatedUtc, 'updatedUtc'), messages,
  };
  const model = s(c.model, 'model', 100, true);
  if (model) conversation.model = model;

  const t = a.target as Record<string, unknown> | undefined;
  if (!t || typeof t !== 'object') throw new Bad('bad target');
  const projectId = s(t.projectId, 'projectId', 40);
  if (!PROJECT_ID.test(projectId)) throw new Bad('bad projectId');
  let target: ImportTarget;
  if (t.kind === 'existing') target = { kind: 'existing', projectId };
  else if (t.kind === 'new') {
    const docs = t.docs === undefined ? [] : t.docs;
    if (!Array.isArray(docs) || docs.length > 500) throw new Bad('bad docs');
    target = {
      kind: 'new', projectId, title: s(t.title, 'project title', 200).trim() || projectId,
      description: s(t.description, 'description', 2000, true), instructions: s(t.instructions, 'instructions', 500_000, true),
      docs: docs.map((d: unknown) => {
        const y = d as Record<string, unknown>;
        return { name: s(y?.name, 'doc name', 300) || 'document', content: s(y?.content, 'doc content', 2_000_000) };
      }),
    };
  } else throw new Bad('bad target kind');
  return { conversation, target };
}
