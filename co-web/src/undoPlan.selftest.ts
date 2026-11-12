// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.72.0: self-check of the undo planner (run: npm test, or node src/undoPlan.selftest.ts).
import { planUndoFile, undoSummary, type UndoInput } from './undoPlan.ts';

declare const process: { exitCode?: number };

const failures: string[] = [];
let count = 0;
function eq(name: string, got: unknown, want: unknown) {
  count++;
  if (JSON.stringify(got) !== JSON.stringify(want)) failures.push(`${name}: got ${JSON.stringify(got)}, want ${JSON.stringify(want)}`);
}

const START = '2026-10-31T12:00:00.000Z';
const t = (min: number) => new Date(Date.parse(START) + min * 60_000).toISOString();
const input = (o: Partial<UndoInput>): UndoInput => ({
  name: 'a.md', key: 'projects/p/chats/c/scratch/a.md', md5: 'B', jobStartUtc: START, jobEndUtc: t(5), priorMd5: null, userMd5: null,
  live: { id: 'F', md5Checksum: 'B', createdTime: t(-60) }, revs: [], ...o,
});

// changed file -> the revision before the job
let r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(1), md5Checksum: 'X' }, { id: 'r3', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('changed.action', r.action, 'restore');
eq('changed.rev', r.revisionId, 'r1');
eq('changed.on', r.defaultOn, false); // r2 was saved during the job (agent or user): not ticked

// created file -> trash
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'B', createdTime: t(1) }, revs: [{ id: 'r1', modifiedTime: t(1), md5Checksum: 'X' }, { id: 'r2', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('created.action', r.action, 'trash');
eq('created.on', r.defaultOn, false); // Codex review: the creator is unknown, the user ticks it

// created, then changed by a later job -> trash, not ticked
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'C', createdTime: t(1) }, revs: [{ id: 'r1', modifiedTime: t(2), md5Checksum: 'B' }, { id: 'r2', modifiedTime: t(30), md5Checksum: 'C' }] }));
eq('createdLater.action', r.action, 'trash');
eq('createdLater.on', r.defaultOn, false);
eq('createdLater.flag', r.changedLater, true);

// changed later -> restore, warned, not ticked
r = planUndoFile(input({ priorMd5: 'A', live: { id: 'F', md5Checksum: 'C' }, revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(2), md5Checksum: 'B' }, { id: 'r3', modifiedTime: t(40), md5Checksum: 'C' }] }));
eq('later.action', r.action, 'restore');
eq('later.rev', r.revisionId, 'r1');
eq('later.on', r.defaultOn, false);

// previous job's version uploaded late (after this job's message) is still "before", found by its md5
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r0', modifiedTime: t(-90), md5Checksum: 'Z' }, { id: 'r1', modifiedTime: t(0.5), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(3), md5Checksum: 'B' }] }));
eq('late.rev', r.revisionId, 'r1');

// the user's upload with this job's message is the version before the job
r = planUndoFile(input({ priorMd5: 'A', userMd5: 'U', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(0.2), md5Checksum: 'U' }, { id: 'r3', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('user.rev', r.revisionId, 'r2');

// manual Drive edit after the earlier message (untracked) is newer than the recorded version
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(-10), md5Checksum: 'M' }, { id: 'r3', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('manual.rev', r.revisionId, 'r2');

// previous revision expired, file older than the job -> nothing
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r3', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('expired.action', r.action, 'none');

// not changed by the job (reported with old content)
r = planUndoFile(input({ priorMd5: 'B', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'B' }] }));
eq('same.action', r.action, 'none');

// already undone (live is the old content)
r = planUndoFile(input({ priorMd5: 'A', live: { id: 'F', md5Checksum: 'A' }, revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(2), md5Checksum: 'B' }, { id: 'r3', modifiedTime: t(9), md5Checksum: 'A' }] }));
eq('again.action', r.action, 'none');

// Codex review: a file written just before the job and reported unchanged is not the job's - never trashed
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'B', createdTime: t(-1) }, revs: [{ id: 'r1', modifiedTime: t(-1), md5Checksum: 'B' }] }));
eq('justBefore.action', r.action, 'none');
// created a second before the job, changed by it: no earlier version, not trashed
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'B', createdTime: t(-0.02) }, revs: [{ id: 'r1', modifiedTime: t(-0.02), md5Checksum: 'X' }, { id: 'r2', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('createdBefore.action', r.action, 'restore');
eq('createdBefore.rev', r.revisionId, 'r1');

// Codex review: the user's upload with this message, reported unchanged by the agent, is never overwritten
r = planUndoFile(input({ priorMd5: 'A', userMd5: 'B', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(0.5), md5Checksum: 'B' }] }));
eq('upload.action', r.action, 'none');
// Codex review: a later job recreated the file with the same content - not this answer's file, nothing trashed
r = planUndoFile(input({ live: { id: 'F2', md5Checksum: 'B', createdTime: t(120) }, revs: [{ id: 'r1', modifiedTime: t(120), md5Checksum: 'B' }] }));
eq('recreated.action', r.action, 'none');
// late Drive Desktop upload (8 min after the answer) still counts as the answer's
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'B', createdTime: t(13) }, revs: [{ id: 'r1', modifiedTime: t(13), md5Checksum: 'B' }] }));
eq('lateUpload.action', r.action, 'trash');

// Codex review: a later job changed the created file and ended with the same bytes - warned, not ticked
r = planUndoFile(input({ live: { id: 'F', md5Checksum: 'B', createdTime: t(1) }, revs: [{ id: 'r1', modifiedTime: t(2), md5Checksum: 'B' }, { id: 'r2', modifiedTime: t(60), md5Checksum: 'Q' }, { id: 'r3', modifiedTime: t(61), md5Checksum: 'B' }] }));
eq('sameBytesLater.action', r.action, 'trash');
eq('sameBytesLater.on', r.defaultOn, false);
// a later message reports the file (no new revision) - warned, not ticked
r = planUndoFile(input({ laterMessage: true, live: { id: 'F', md5Checksum: 'B', createdTime: t(1) }, revs: [{ id: 'r1', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('laterMsg.on', r.defaultOn, false);

// Codex review: a user edit 3 min after the answer, then the answer's bytes again (inside the late-upload window)
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(2), md5Checksum: 'B' }, { id: 'r3', modifiedTime: t(8), md5Checksum: 'E' }, { id: 'r4', modifiedTime: t(9), md5Checksum: 'B' }] }));
eq('editInWindow.on', r.defaultOn, false);
eq('editInWindow.head', r.liveRevisionId, 'r4');

// one revision of the answer right after the version before: ticked
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r3', modifiedTime: t(2), md5Checksum: 'B' }] }));
eq('clean.on', r.defaultOn, true);
// Codex review: a manual edit during the job before the agent's version - offered, not ticked
r = planUndoFile(input({ priorMd5: 'A', revs: [{ id: 'r1', modifiedTime: t(-60), md5Checksum: 'A' }, { id: 'r2', modifiedTime: t(1), md5Checksum: 'M' }, { id: 'r3', modifiedTime: t(3), md5Checksum: 'B' }] }));
eq('editDuring.rev', r.revisionId, 'r1');
eq('editDuring.on', r.defaultOn, false);

// file gone / version gone
eq('gone', planUndoFile(input({ live: null })).action, 'none');
eq('nohit', planUndoFile(input({ revs: [{ id: 'r1', modifiedTime: t(1), md5Checksum: 'Q' }] })).action, 'none');

eq('summary', undoSummary(1, 2), 'Undone: 1 file restored, 2 moved to trash');
eq('summary0', undoSummary(0, 0), 'Undone: 0 files restored, 0 moved to trash');

if (failures.length) {
  console.error(`undoPlan selftest: ${failures.length} of ${count} failed\n` + failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log(`undoPlan selftest: ${count} checks passed`);
}
