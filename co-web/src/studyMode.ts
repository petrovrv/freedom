// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.78.0: study mode - the agent leads a learner to the answer instead of giving it (SPEC.md of chat
// CHAT-20261103081329-PX01, part B). A chat follows its own switch (chat.study); without one it takes the project's
// default (project.studyDefault). Every switch is kept in chat.studyLog and shown as a line in the chat, so the agent
// and the people of the chat see when the mode was turned on or off and by whom. A learner's own tool, not parental control.
import type { Chat, Project, StudySwitch } from './types';

export const STUDY_RULES = [
  '[Study mode is ON in this chat: the person is learning. Act as a patient tutor, not as an answer machine.]',
  '- First find out the context: course, what they already know, where exactly they are stuck, what they tried. One or two short questions, not a questionnaire. Use files and documents of the chat for the course context.',
  '- Hint ladder for study tasks (problems, derivations, formulas, algorithms, code exercises): guiding question -> hint (which law or idea applies) -> a worked SIMILAR example with other numbers -> the solution.',
  '- Give the full solution only after three hints AND the person\'s own attempt (their work, even a wrong one). After the solution ask which step was unclear.',
  '- Check understanding instead of asking "clear?": ask them to explain it in their own words or to solve a small variant, then correct gently.',
  '- When they send their own solution (text or a photo), point to the FIRST mistake and ask a question about it; do not fix it for them.',
  '- Language: the language of their message; for a question about a document, the language of that document. Keep course terms in the course language.',
  '- Not study content (deadlines, emails to a professor, formatting, finding a file, using AI Workbench) gets a normal direct answer.',
  '- Calm tone, no judging; every question is welcome. Write formulas in LaTeX ($...$ inline, $$...$$ display); the chat renders them.',
].join('\n');

export const STUDY_OFF_NOTE = '[Study mode was turned off in this chat: answer normally and do not mention the mode.]';

// The project default applies only to a new chat (draft) and is written into the chat when it is created, so switching
// the project later changes new chats only (review 03.11).
export function studyOn(chat: Pick<Chat, 'study' | 'draft'>, project: Pick<Project, 'studyDefault'>): boolean {
  return chat.study ?? (chat.draft ? project.studyDefault ?? false : false);
}

// Level-3 text for one turn: the rules while on; a short note once the mode has been switched off in this chat.
export function studyHeuristic(chat: Pick<Chat, 'study' | 'studyLog' | 'draft'>, project: Pick<Project, 'studyDefault'>): string {
  if (studyOn(chat, project)) return STUDY_RULES;
  return (chat.studyLog || []).length > 0 ? STUDY_OFF_NOTE : '';
}

const LOG_MAX = 50;

// Patch for updateChatMeta: the new state and one more line in the log (newest LOG_MAX kept).
export function studyPatch(chat: Pick<Chat, 'study' | 'studyLog' | 'draft'>, project: Pick<Project, 'studyDefault'>, by: string): Pick<Chat, 'study' | 'studyLog'> {
  const on = !studyOn(chat, project);
  const entry: StudySwitch = { on, by: by || 'someone', utc: new Date().toISOString() };
  return { study: on, studyLog: [...(chat.studyLog || []), entry].slice(-LOG_MAX) };
}

// Who switched: the display name of the sender's own environment (anna@example.org -> Anna), else the e-mail's first name.
export function studyWho(by: string, known: Record<string, string> = {}): string {
  const k = known[(by || '').toLowerCase()];
  if (k) return k;
  const name = (by || '').split('@')[0].split(/[._-]/)[0];
  return name ? name[0].toUpperCase() + name.slice(1) : 'someone';
}

// Log lines placed after the event with createdUtc <= their time (the timeline shows them between messages).
export function studyLinesBetween(log: StudySwitch[] | undefined, afterUtc: string | null, untilUtc: string | null): StudySwitch[] {
  return (log || []).filter((s) => (afterUtc === null || s.utc > afterUtc) && (untilUtc === null || s.utc <= untilUtc));
}
