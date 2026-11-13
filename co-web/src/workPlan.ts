// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.76.0: work plans of long jobs (ideas of planning-with-files and Continuous-Claude). A multi-step job keeps a
// checklist in the chat scratch (.plan/plan.md) and ticks each step when it is done; when the job breaks off (Stop, a
// killed process, timeout, a Bridge restart), the next message of the chat carries that plan, so a retry continues from
// the first unticked step instead of starting over. Short jobs keep no plan: the rule is one line in context level 3.
import type { ChatEvent } from './types';
import { drivePath } from './members.ts';

export const PLAN_DIR = '.plan';
export const PLAN_FILE = 'plan.md';
// Plans longer than this are cut in the resume block (the agent still has the whole file).
const PLAN_MAX_CHARS = 8000;
// Drive for desktop uploads with a delay; the file may also be written a little before the user message is stored.
const CLOCK_SLACK_MS = 120_000;

// Absolute path of the plan for agents on a Drive host (the same Drive mount as workbenchNote).
export function planPath(projectId: string, chatId: string): string {
  return drivePath(`AI-Workbench/projects/${projectId}/chats/${chatId}/scratch/${PLAN_DIR}/${PLAN_FILE}`);
}

// The level-3 line; deliberately short because it goes into every new session.
export function planHeuristic(path: string): string {
  return (
    `- Work plan: for a task of 4+ steps or likely over 15 minutes, first write a checklist to ${path} (line 1 "Status: active", ` +
    'the goal, then "- [ ] step" lines), tick each step "- [x] step - result" right after it is done, and set line 1 to ' +
    '"Status: done" when the task is finished. A broken-off job is resumed from this file. Short tasks: no plan.'
  );
}

export interface BrokenRun {
  // createdUtc of the first user message of the trailing run of jobs that did not complete
  sinceUtc: string;
  // the last of them
  jobId: string;
  sequence: number;
  status: string;
  error: string;
}

const BROKEN = new Set(['failed', 'cancelled', 'timed_out']);

// The trailing jobs of this chat that ended without completing (a retry that failed again keeps the first start, so
// a plan written by the first attempt still counts). null when the last answer of the chat completed.
export function brokenRun(events: ChatEvent[], chatId: string, isSide: (e: ChatEvent) => boolean = () => false): BrokenRun | null {
  // side jobs of modules (own agent session) are not turns of this chat
  const own = events.filter((e) => (!e.chatId || e.chatId === chatId) && !isSide(e));
  const answers = own.filter((e) => e.type === 'assistant_message' && e.jobId);
  let last: ChatEvent | null = null;
  let first: ChatEvent | null = null;
  for (let i = answers.length - 1; i >= 0; i--) {
    const a = answers[i];
    if (!BROKEN.has(a.status || '')) break;
    last ??= a;
    first = a;
  }
  if (!last || !first) return null;
  const u = own.find((e) => e.type === 'user_message' && e.jobId === first!.jobId);
  return {
    sinceUtc: u?.createdUtc || first.createdUtc,
    jobId: last.jobId!,
    sequence: last.sequence,
    status: last.status || 'failed',
    error: (last.error || '').replace(/\s+/g, ' ').trim().slice(0, 200),
  };
}

// True when the plan file was written by the broken run and is not finished.
export function planApplies(text: string, modifiedUtc: string | undefined, run: BrokenRun): boolean {
  if (!text.trim()) return false;
  if (/^\s*Status:\s*done\b/im.test(text.split(/\r?\n/, 3).join('\n'))) return false;
  if (!/^\s*-\s*\[\s\]/m.test(text)) return false; // nothing left to do
  const m = Date.parse(modifiedUtc || '');
  const s = Date.parse(run.sinceUtc);
  return !Number.isFinite(m) || !Number.isFinite(s) || m >= s - CLOCK_SLACK_MS;
}

// Hidden preamble of the next message after a broken run (not shown in the chat).
export function resumeBlock(text: string, run: BrokenRun, path: string): string {
  const plan = text.length > PLAN_MAX_CHARS ? `${text.slice(0, PLAN_MAX_CHARS)}\n[… cut; read the whole file …]` : text;
  const why = run.status === 'cancelled' ? 'was stopped' : `ended as ${run.status}${run.error ? ` (${run.error})` : ''}`;
  return [
    `[Resume: the previous job of this chat (message #${run.sequence}, ${run.jobId}) ${why} before its work plan was finished. ` +
      `The plan is ${path}; its state:]`,
    plan.trim(),
    '[End of plan. If this message asks to continue, retry or repeat that task, first check that the last ticked step ' +
      'really took effect, then continue from the first unticked step without redoing ticked ones, and keep ticking the ' +
      'same file. If the message is about something else, answer it and mention the unfinished plan in one line.]',
    '',
    '',
  ].join('\n');
}

// Preamble when the plan of a broken run could not be read from Drive or is not there yet (Codex review): the agent
// checks the file itself.
export function resumeUnreadBlock(run: BrokenRun, path: string): string {
  const why = run.status === 'cancelled' ? 'was stopped' : `ended as ${run.status}`;
  return (
    `[Resume: the previous job of this chat (message #${run.sequence}, ${run.jobId}) ${why}. CO Web could not read a work ` +
    `plan of it; if ${path} exists and is not "Status: done", read it first and continue from the first unticked step ` +
    'when this message asks to continue or retry.]\n\n'
  );
}
