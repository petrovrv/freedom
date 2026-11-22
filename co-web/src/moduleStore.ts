// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10: the latest module list (db.php modules_list) for the screens that weave module declarations into their
// own parts (chat modes, file menu items, message views, side-job rows, context). ModuleSlot.useModules fills it.
import { useSyncExternalStore } from 'react';
import { pruneModuleBadges, type MessageView, type ModuleInfo, type SideJob } from './extBridge';
import type { ModeDecl } from './moduleModes';

let list: ModuleInfo[] = [];
const subs = new Set<() => void>();

export function setModuleList(next: ModuleInfo[]): void {
  list = next;
  // a badge belongs to an enabled module: switching it off or removing it drops the badge, so it cannot come back
  pruneModuleBadges(new Set(next.filter((m) => m.enabled).map((m) => m.id)));
  subs.forEach((f) => f());
}

export const moduleList = (): ModuleInfo[] => list;

function subscribe(f: () => void): () => void {
  subs.add(f);
  return () => subs.delete(f);
}

export function useModuleList(): ModuleInfo[] {
  return useSyncExternalStore(subscribe, moduleList, moduleList);
}

const on = (l: ModuleInfo[]) => l.filter((m) => m.enabled);
export const modesOf = (l: ModuleInfo[]): ModeDecl[] => on(l).flatMap((m) => m.modes || []);
export const viewsOf = (l: ModuleInfo[]): MessageView[] => on(l).flatMap((m) => m.messageViews || []);
export const contextOf = (l: ModuleInfo[]): string => on(l).flatMap((m) => m.context || []).filter((t) => t.trim()).join('\n\n');
/** Side jobs of every module the server listed (rows of a switched-off module stay collapsed too). */
export const sideJobsOf = (l: ModuleInfo[]): SideJob[] => l.flatMap((m) => m.sideJobs || []);
