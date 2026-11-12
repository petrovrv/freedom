// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.72.0: "Undo file changes of this answer" - Drive side. The plan (undoPlan.ts) is made from the answer's artifacts,
// the chat's earlier messages (the version each file had before) and the Drive revisions of the live files. Only files
// in this chat's scratch folder on Drive are touched; a folder is never trashed (a chat folder in the trash = a deleted chat).

import type { ChatEvent } from './types';
import { dbFiles, dbFilesOp } from './dbapi';
import { artifactKey, locate } from './chatFiles';
import { fileMeta, getRevisionBytes, listRevisions, replaceContent, trashFile } from './drive';
import { md5Hex } from './md5';
import { sha256Hex } from './util';
import { planUndoFile, type UndoItem } from './undoPlan';

export interface UndoJobPlan {
  items: UndoItem[]; // files in the chat folder
  outside: { name: string; note: string }[]; // reported changes undo cannot reach
}

const FOLDER = 'application/vnd.google-apps.folder';

export async function planUndoJob(rootId: string, ev: ChatEvent, events: ChatEvent[], projectId: string, chatId: string): Promise<UndoJobPlan> {
  const chatPrefix = `projects/${projectId}/chats/${chatId}/scratch/`;
  const user = events.find((x) => x.type === 'user_message' && x.jobId && x.jobId === ev.jobId && (!x.chatId || x.chatId === chatId));
  const startSeq = user?.sequence ?? ev.sequence;
  const jobStartUtc = user?.createdUtc || ev.createdUtc;
  const jobEndUtc = ev.completedUtc || ev.createdUtc;
  const items: UndoItem[] = [];
  const outside: UndoJobPlan['outside'] = [];
  const seen = new Set<string>();
  // Only files the server already registered (chat_files, synced every minute): the undo is recorded there, and a file
  // registered after a trash would come back to the list as live.
  const rows = ev.artifacts?.length ? await dbFiles(chatId) : [];
  if (rows === null) throw new Error('The chat file list is not reachable; try again in a minute.');
  const registered = new Set(rows.map((r) => r.path));
  for (const a of ev.artifacts || []) {
    const shown = (a.sourceRelativePath || a.driveRelativePath || '').replace(/\\/g, '/');
    const k = artifactKey(a, projectId, chatId);
    if (!k || !k.key.startsWith(chatPrefix)) {
      outside.push({ name: shown, note: 'Outside this chat’s folder on Drive.' });
      continue;
    }
    if (k.legacy || !a.ref) {
      outside.push({ name: shown, note: 'Saved as a copy (older agent): the chat file itself was not changed.' });
      continue;
    }
    if (seen.has(k.key)) continue;
    seen.add(k.key);
    // The version this file had before the job: the newest one recorded by an earlier message of this chat.
    let priorMd5: string | null = null;
    for (const e of events) {
      if (e.sequence >= startSeq || (e.chatId && e.chatId !== chatId)) continue;
      if (e.type === 'assistant_message') {
        for (const x of e.artifacts || []) if (x.md5 && artifactKey(x, projectId, chatId)?.key === k.key) priorMd5 = x.md5;
      } else if (e.type === 'user_message') {
        for (const x of e.attachments || []) if (x.md5 && x.streamPath === k.key) priorMd5 = x.md5;
      }
    }
    const userMd5 = (user?.attachments || []).find((x) => x.streamPath === k.key && x.md5)?.md5 || null;
    const laterMessage = events.some((e) => e.sequence > ev.sequence && (!e.chatId || e.chatId === chatId) && (
      (e.type === 'assistant_message' && (e.artifacts || []).some((x) => artifactKey(x, projectId, chatId)?.key === k.key))
      || (e.type === 'user_message' && (e.attachments || []).some((x) => x.streamPath === k.key))));
    // Drive errors stop the preview (shown with Retry): a failed call must not look like a missing file or history.
    const live = await locate(rootId, k.key);
    const revs = live && live.mimeType !== FOLDER ? await listRevisions(live.id) : [];
    if (!registered.has(k.key)) {
      items.push({ name: k.name, key: k.key, action: 'none', fileId: null, answerMd5: a.md5 || '', liveMd5: null, liveRevisionId: null,
        revisionId: null, revisionUtc: null, changedLater: false, defaultOn: false, note: 'Not in the chat file list yet; try again in a minute.' });
      continue;
    }
    items.push(planUndoFile({
      name: k.name, key: k.key, md5: a.md5 || '', jobStartUtc, jobEndUtc, priorMd5, userMd5, laterMessage,
      live: live && live.mimeType !== FOLDER ? { id: live.id, md5Checksum: live.md5Checksum, createdTime: live.createdTime, mimeType: live.mimeType } : null,
      revs: revs.map((r) => ({ id: r.id, modifiedTime: r.modifiedTime, md5Checksum: r.md5Checksum })),
    }));
  }
  for (const s of ev.exportSkipped || []) outside.push({ name: s.path, note: `Changed but not exported (${s.reason}).` });
  return { items, outside };
}

// Applies the ticked items one by one; a file that changed since the preview is skipped with a message.
export async function applyUndo(rootId: string, chatId: string, seq: number, items: UndoItem[]): Promise<{ restored: number; trashed: number; errors: string[] }> {
  let restored = 0;
  let trashed = 0;
  const errors: string[] = [];
  for (const it of items) {
    try {
      if (!it.fileId || it.action === 'none') continue;
      // Still the same file at the same place in the chat folder (not moved or replaced since the preview).
      const at = await locate(rootId, it.key);
      if (!at || at.id !== it.fileId) throw new Error('moved or replaced since the preview, not touched');
      const now = await fileMeta(it.fileId);
      if (now.mimeType === FOLDER) throw new Error('is a folder, not touched');
      if ((now.md5Checksum || null) !== it.liveMd5 || (now.headRevisionId || null) !== it.liveRevisionId)
        throw new Error('changed since the preview; open the preview again');
      if (it.action === 'restore') {
        if (!it.revisionId) continue;
        const bytes = await getRevisionBytes(it.fileId, it.revisionId);
        await replaceContent(it.fileId, bytes, now.mimeType || 'application/octet-stream');
        const head = { md5: md5Hex(bytes), sha256: await sha256Hex(bytes), size: bytes.byteLength, revisionId: it.revisionId };
        restored++;
        await dbFilesOp('files_undone', { chatId, path: it.key, action: 'restore', seq, head })
          .catch((e) => errors.push(`${it.name}: restored on Drive, but the chat file list was not updated (${e instanceof Error ? e.message : e})`));
      } else {
        await trashFile(it.fileId);
        trashed++;
        await dbFilesOp('files_undone', { chatId, path: it.key, action: 'trash', seq })
          .catch((e) => errors.push(`${it.name}: moved to trash, but the chat file list was not updated (${e instanceof Error ? e.message : e})`));
      }
    } catch (e) {
      errors.push(`${it.name}: ${e instanceof Error ? e.message : String(e)}`);
    }
  }
  return { restored, trashed, errors };
}
