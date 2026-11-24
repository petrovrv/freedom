// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.86.0: the chat's own controls (permission chips, modes, protocol … delete) are rendered by ChatView with a portal
// into a slot of the side panel; the context folder chips into the files slot.
// 0.128.0 (F15.2 part 4, SPEC §3.6b): the right panel with tabs is gone. One side panel shows what was asked for:
// the Properties of a chat folder or of a project folder, System (Settings → System) or the family room (app 👪).
// On a computer it slides in from the right and can be pinned (then it is a column of the layout); on a phone it is
// a sheet from the bottom. Files of a chat and the workspace of a project are the contents of their folders in the
// Explorer (FolderContents.tsx), not a panel.
import { useSyncExternalStore } from 'react';

export type PanelKind = 'chat' | 'project' | 'system' | 'family';
export interface PanelTarget {
  kind: PanelKind;
  /** chat and project Properties: whose (default: the open one) */
  projectId?: string;
  chatId?: string;
}

type State = {
  chatSlot: HTMLElement | null;
  filesSlot: HTMLElement | null;
  familySlot: HTMLElement | null;
  /** the shown panel, null = closed */
  panel: PanelTarget | null;
  /** changes with every open request (the shell opens the panel and closes the projects drawer on a phone) */
  nonce: number;
  pinned: boolean;
  familyUnread: number;
};

const PIN_KEY = 'co.props.pin';
function readPin(): boolean {
  try {
    return localStorage.getItem(PIN_KEY) === '1';
  } catch {
    return false;
  }
}

let state: State = { chatSlot: null, filesSlot: null, familySlot: null, panel: null, nonce: 0, pinned: readPin(), familyUnread: 0 };
const subs = new Set<() => void>();

function set(patch: Partial<State>) {
  state = { ...state, ...patch };
  subs.forEach((f) => f());
}

function subscribe(f: () => void) {
  subs.add(f);
  return () => { subs.delete(f); };
}

export function useChatPanel(): State {
  return useSyncExternalStore(subscribe, () => state);
}

export function setChatSlot(el: HTMLElement | null) {
  if (state.chatSlot !== el) set({ chatSlot: el });
}

export function setFilesSlot(el: HTMLElement | null) {
  if (state.filesSlot !== el) set({ filesSlot: el });
}

export function setFamilySlot(el: HTMLElement | null) {
  if (state.familySlot !== el) set({ familySlot: el });
}

export function setFamilyUnread(n: number) {
  if (state.familyUnread !== n) set({ familyUnread: n });
}

/** Opens the side panel: Properties of a chat or project (the open one by default), System or the family room. */
export function openPanel(kind: PanelKind, target: { projectId?: string; chatId?: string } = {}) {
  set({ panel: { kind, ...target }, nonce: state.nonce + 1 });
}

/** The same panel again closes it (a second Alt+Enter, ⓘ or tile), another one replaces it. */
export function togglePanel(kind: PanelKind, target: { projectId?: string; chatId?: string } = {}) {
  const p = state.panel;
  if (p && p.kind === kind && (p.projectId || '') === (target.projectId || '') && (p.chatId || '') === (target.chatId || '')) closePanel();
  else openPanel(kind, target);
}

export function closePanel() {
  if (state.panel) set({ panel: null });
}

export function setPanelPinned(pinned: boolean) {
  try {
    localStorage.setItem(PIN_KEY, pinned ? '1' : '0');
  } catch {
    /* storage unavailable */
  }
  set({ pinned });
}
