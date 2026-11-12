// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.72.0: "Undo file changes of this answer" - pure planning (no Drive calls; tested by undoPlan.selftest.ts).
// A chat file is one live Drive file; its versions are Drive revisions, linked to messages by md5 (SPEC-chat-files.md).
// For one file an answer reported, the plan says what undo does:
//   restore - the file existed before the job: its newest revision from before the job becomes the newest revision again;
//   trash   - the job created the file: it goes to the Drive trash (recoverable 30 days);
//   none    - nothing to do or impossible (reason says why).
// A file changed again after the answer (later job or the user) is planned but not ticked by default.

export interface UndoRev {
  id: string;
  modifiedTime: string;
  md5Checksum?: string;
}

export interface UndoLive {
  id: string;
  md5Checksum?: string;
  createdTime?: string;
  mimeType?: string;
}

export interface UndoInput {
  name: string;
  key: string; // path of the live file relative to AI-Workbench
  md5: string; // the answer's version of the file
  jobStartUtc: string; // the user message of the job (the job cannot have written before it)
  jobEndUtc: string; // the answer (its files reach Drive by then, or up to LATE_MS later through Drive Desktop)
  priorMd5: string | null; // newest version of this file recorded by an earlier message of the chat
  userMd5: string | null; // the user's upload into this file with the job's own message (a version from before the job)
  laterMessage?: boolean; // a later message of the chat reports or uploads this file
  live: UndoLive | null;
  revs: UndoRev[]; // Drive revisions, oldest first
}

export interface UndoItem {
  name: string;
  key: string;
  action: 'restore' | 'trash' | 'none';
  fileId: string | null;
  mimeType?: string;
  answerMd5: string;
  liveMd5: string | null; // the live file when planned; undo re-checks it before writing
  liveRevisionId: string | null; // its head revision when planned (same bytes written again = another revision)
  revisionId: string | null; // restore: the revision to bring back
  revisionUtc: string | null;
  changedLater: boolean;
  defaultOn: boolean;
  note: string;
}

// No clock allowance: a revision counts as the job's only when Drive dated it at or after the user message. A skewed
// clock then makes the file "none" (nothing undone), never trashes or rewinds a file that existed before the job.
// Drive Desktop may upload the job's files a while after the answer; later than this a revision belongs to someone else.
const LATE_MS = 10 * 60_000;
const ms = (s: string | undefined) => (s ? Date.parse(s) : NaN);

export function planUndoFile(inp: UndoInput): UndoItem {
  const base = {
    name: inp.name, key: inp.key, fileId: inp.live?.id ?? null, mimeType: inp.live?.mimeType, answerMd5: inp.md5,
    liveMd5: inp.live?.md5Checksum ?? null, liveRevisionId: inp.revs[inp.revs.length - 1]?.id ?? null, revisionId: null, revisionUtc: null, changedLater: false, defaultOn: false,
  };
  const none = (note: string): UndoItem => ({ ...base, action: 'none', note });
  if (!inp.md5) return none('No version recorded for this file.');
  if (!inp.live) return none('The file is no longer in the chat folder (deleted, moved or already undone).');
  const revs = inp.revs;
  const start = ms(inp.jobStartUtc);
  const end = ms(inp.jobEndUtc) + LATE_MS;
  if (!(start <= end)) return none('The time of this job is unknown.');
  // The user's own upload with this message is not a change of the answer, even when Drive dates it after the message.
  if (inp.userMd5 && inp.md5 === inp.userMd5) return none('Your upload with this message, not changed by the answer.');
  const inJob = (r: { modifiedTime: string }) => ms(r.modifiedTime) >= start && ms(r.modifiedTime) <= end;
  // The answer's revision: the first one with its md5 written during the job (a later job may write the same content).
  const hit = revs.findIndex((r) => r.md5Checksum === inp.md5 && inJob(r));
  if (hit < 0) {
    // The agent reported a file whose content was already there before the job.
    if (revs.some((r) => r.md5Checksum === inp.md5 && ms(r.modifiedTime) < start)) return none('Not changed by this answer.');
    return none('This answer’s version is no longer in the Drive history (Drive keeps 30 days).');
  }
  // Changed after the answer: other content now, any later revision with other content or dated after the answer (the
  // late-upload allowance covers only the answer's own revision; a later job or edit may end with the same bytes), or a
  // later message of the chat with this file. Such files are never ticked by default.
  const answered = ms(inp.jobEndUtc);
  const changedLater = inp.live.md5Checksum !== inp.md5 || !!inp.laterMessage
    || revs.slice(hit + 1).some((r) => r.md5Checksum !== inp.md5 || !(ms(r.modifiedTime) <= answered));
  // The version before the job: the newest revision before the answer's one that is either older than the job, the
  // version an earlier message recorded, or the user's upload with this job's message. The larger index wins: a manual
  // Drive edit after the earlier message is newer, a late Drive Desktop upload of the earlier job counts too.
  let before = -1;
  for (let i = 0; i < hit; i++) {
    const r = revs[i];
    const older = ms(r.modifiedTime) < start;
    const known = !!r.md5Checksum && (r.md5Checksum === inp.priorMd5 || r.md5Checksum === inp.userMd5);
    if (older || known) before = i;
  }
  if (before < 0) {
    const created = ms(inp.live.createdTime);
    // Created during this job: a file of the same name a later job made again is not this answer's to trash.
    const newFile = !inp.priorMd5 && !inp.userMd5 && created >= start && created <= end && ms(revs[0]?.modifiedTime) >= start;
    if (!newFile) return none('Its version from before this answer is no longer in the Drive history (Drive keeps 30 days).');
    // Drive cannot tell who created it (the agent and the user write as the same account): never ticked by default.
    return {
      ...base, action: 'trash', changedLater, defaultOn: false,
      note: changedLater ? 'New during this job, changed again later: moving it to the trash also loses the later changes.'
        : 'New during this job (by the agent, unless you added it yourself): tick to move it to the Drive trash (restorable for 30 days).',
    };
  }
  const b = revs[before];
  if (b.md5Checksum && b.md5Checksum === inp.md5) return none('Not changed by this answer.');
  if (b.md5Checksum && b.md5Checksum === inp.live.md5Checksum) return none('Already as before this answer.');
  // Other revisions between the version before and the answer's: saved during the job by the agent or by someone else
  // (Drive cannot tell). Restoring goes back past them, so the file is offered but not ticked.
  const between = hit - before > 1;
  return {
    ...base, action: 'restore', revisionId: b.id, revisionUtc: b.modifiedTime, changedLater, defaultOn: !changedLater && !between,
    note: changedLater ? 'Changed again after this answer: restoring loses those later changes (they stay in Versions).'
      : between ? 'Other versions were saved during this job (by the agent or by you): restoring goes back past them too (they stay in Versions).'
      : 'Changed by this answer: restored to the version before it (this answer’s version stays in Versions).',
  };
}

export function undoSummary(restored: number, trashed: number): string {
  const f = (n: number) => `${n} file${n === 1 ? '' : 's'}`;
  return `Undone: ${f(restored)} restored, ${trashed} moved to trash`;
}
