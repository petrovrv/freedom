// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (SPEC §3.6, ext/1 chat.mode): modes a module adds to chats. CO Web knows no mode by name: each comes from
// the manifest of an enabled module (db.php modules_list -> modes). A chat follows its own switch (chat[key]); without
// one a new chat takes the project's default (project[key + 'Default']). Every switch is kept in chat[key + 'Log'] and
// shown as a line in the chat, so the agent and the people of the chat see when the mode was turned on or off and by
// whom. While a mode is on, every job of the chat gets its rules; once switched off, a short note. Pure functions.

export interface ModeViewer { panel: string; param: string; titlePrefix: string }
export interface ModeDecl {
  key: string; label: string; short: string; icon: string; rules: string; offNote: string;
  hintOn: string; hintOff: string; projectLabel: string; newProjectHint: string; inherit: boolean;
  viewer?: ModeViewer;
}
export interface ModeSwitch { on: boolean; by: string; utc: string }

type Rec = Record<string, unknown>;
const rec = (o: object): Rec => o as unknown as Rec;

/** Field names of a mode in chat and project documents. */
export const modeFields = (key: string) => ({ on: key, log: key + 'Log', def: key + 'Default', doc: key + 'Doc' });

// The project default applies only to a new chat (draft) and is written into the chat when it is created, so switching
// the project later changes new chats only.
export function modeOn<C extends { draft?: boolean }, P extends object>(chat: C, project: P, key: string): boolean {
  const v = rec(chat)[key];
  if (typeof v === 'boolean') return v;
  return chat.draft ? rec(project)[modeFields(key).def] === true : false;
}

export function modeLog<C extends object>(chat: C, key: string): ModeSwitch[] {
  const l = rec(chat)[modeFields(key).log];
  return Array.isArray(l) ? (l as ModeSwitch[]).filter((s) => s && typeof s.utc === 'string') : [];
}

export const modeDoc = (chat: object, key: string): string | undefined => {
  const d = rec(chat)[modeFields(key).doc];
  return typeof d === 'string' && d ? d : undefined;
};

export const projectModeDefault = (project: object, key: string): boolean => rec(project)[modeFields(key).def] === true;

/** Text for one turn: the rules of every mode that is on; a short note for a mode once switched off in this chat. */
export function modesText<C extends { draft?: boolean }, P extends object>(chat: C, project: P, modes: ModeDecl[]): string {
  return modes
    .map((m) => (modeOn(chat, project, m.key) ? m.rules : modeLog(chat, m.key).length > 0 ? m.offNote : ''))
    .filter((t) => t.trim())
    .join('\n\n');
}

const LOG_MAX = 50;

/** Patch for updateChatMeta: the new state and one more line in the log (newest LOG_MAX kept). */
export function modePatch<C extends { draft?: boolean }, P extends object>(chat: C, project: P, key: string, by: string): Rec {
  const on = !modeOn(chat, project, key);
  const entry: ModeSwitch = { on, by: by || 'someone', utc: new Date().toISOString() };
  return { [key]: on, [modeFields(key).log]: [...modeLog(chat, key), entry].slice(-LOG_MAX) };
}

/** What a new chat made from `chat` (branch, side question) keeps: the modes that are on and inherit. */
export function inheritedModes<C extends { draft?: boolean }, P extends object>(chat: C, project: P, modes: ModeDecl[]): Rec {
  const out: Rec = {};
  for (const m of modes) if (m.inherit && modeOn(chat, project, m.key)) out[m.key] = true;
  return out;
}

/** Who switched: the display name of the sender's own environment (anna@example.org -> Anna), else the e-mail's first name. */
export function switchWho(by: string, known: Record<string, string> = {}): string {
  const k = known[(by || '').toLowerCase()];
  if (k) return k;
  const name = (by || '').split('@')[0].split(/[._-]/)[0];
  return name ? name[0].toUpperCase() + name.slice(1) : 'someone';
}

export interface ModeLine extends ModeSwitch { mode: ModeDecl }

/** Switch lines of every mode, oldest first. */
export function modeLines<C extends object>(chat: C, modes: ModeDecl[]): ModeLine[] {
  return modes.flatMap((m) => modeLog(chat, m.key).map((s) => ({ ...s, mode: m }))).sort((a, b) => (a.utc < b.utc ? -1 : a.utc > b.utc ? 1 : 0));
}

/** Lines placed after the event with createdUtc <= their time (the timeline shows them between messages). */
export function linesBetween<T extends ModeSwitch>(lines: T[], afterUtc: string | null, untilUtc: string | null): T[] {
  return lines.filter((s) => (afterUtc === null || s.utc > afterUtc) && (untilUtc === null || s.utc <= untilUtc));
}


/** The documentless viewer of a persistent module chat, including direct chat URLs. */
export function moduleChatViewer(modules: import('./extBridge').ModuleInfo[], chat: object | null) {
  const id = chat ? rec(chat).moduleChat : null;
  const mod = modules.find((m) => m.enabled && m.id === id && m.moduleChat);
  if (!mod?.moduleChat) return null;
  const mode = mod.modes?.find((m) => m.key === mod.moduleChat!.mode);
  const ui = mod.ui.find((u) => u.slot === 'ui.panel' && u.id === mod.moduleChat!.panel);
  return mode && ui ? { mode, doc: '', mod, ui, moduleChat: true } : null;
}


/** A module route is safe to enter only after its persisted chat is readable. Never substitute a draft. */
export async function loadModuleChat<P extends { projectId: string }, C extends { projectId: string; chatId: string }>(
  projectId: string, chatId: string, projects: P[], readChats: (project: P) => Promise<C[]>,
): Promise<{ project: P; chats: C[] }> {
  const project = projects.find((p) => p.projectId === projectId);
  if (!project) throw new Error('The module project could not be loaded. Try opening the app again.');
  const chats = await readChats(project);
  if (!chats.some((c) => c.projectId === projectId && c.chatId === chatId)) {
    throw new Error('The module chat could not be loaded. Try opening the app again.');
  }
  return { project, chats };
}
