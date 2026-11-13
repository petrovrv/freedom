// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.86.0: the chat's own controls (permission chips, modes, protocol … delete) live in the Inspector "Chat" tab and the
// context folder chips in its "Files" tab. ChatView keeps their state and renders them into these slots with a portal;
// the header keeps the title, the project and one status line that opens the tab.
import { useSyncExternalStore } from 'react';

export type InspectorTab = 'family' | 'chat' | 'session' | 'project' | 'workspace' | 'artifacts' | 'host';

type State = {
  chatSlot: HTMLElement | null;
  filesSlot: HTMLElement | null;
  want: { tab: InspectorTab; nonce: number } | null; // a request to show a tab (and open the drawer on a phone)
  // 0.90.0: the Family tab - its slot (FamilyPanel renders the room there), the shown tab and the unread count
  familySlot: HTMLElement | null;
  tab: InspectorTab | null;
  familyUnread: number;
};

let state: State = { chatSlot: null, filesSlot: null, want: null, familySlot: null, tab: null, familyUnread: 0 };
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

export function setShownTab(tab: InspectorTab) {
  if (state.tab !== tab) set({ tab });
}

export function setFamilyUnread(n: number) {
  if (state.familyUnread !== n) set({ familyUnread: n });
}

export function openInspectorTab(tab: InspectorTab) {
  set({ want: { tab, nonce: Date.now() } });
}
