// SPDX-License-Identifier: AGPL-3.0-or-later
// diagnose.ts — turns raw agent/Bridge failures into actionable diagnoses (0.53.0).
import { corporate, corporateAgent, corporateBlocked, corporateErrorRe } from './corporate.ts';

export type DiagAction = 'continue_other' | 'retry' | 'retry_new';

export interface Diagnosis {
  kind: string;          // stable id, e.g. 'codex_limit'
  title: string;         // short headline
  todo: string;          // what the user should do now (imperative)
  actions: DiagAction[]; // buttons to offer, most useful first
  detail: string;        // raw error text (+ failed reply text) for Details
}

export interface DiagnoseInput {
  error?: string | null;
  text?: string | null;
  agent?: string;
  status?: string;
  corporateBlocked?: string | null;
  engieBlocked?: string | null; // legacy wire name of corporateBlocked
  jobId?: string;
}

type Body = Pick<Diagnosis, 'title' | 'todo' | 'actions'>;

interface Ctx {
  error: string;
  text: string;
  /** error + reply text; used by rules whose message may land in the reply text. */
  all: string;
  agent: string;
  isCodex: boolean;
  isClaude: boolean;
  otherName: string;
  jobId: string;
  corporateBlocked: string;
}

interface Rule {
  kind: string;
  /** Restrict the rule to one agent family. */
  agent?: 'codex' | 'claude';
  /** Where a RegExp test searches: 'error' (default) or 'all' (error + reply text). */
  scope?: 'error' | 'all';
  test: RegExp | ((c: Ctx) => boolean);
  build: (c: Ctx, m: RegExpMatchArray | null) => Body;
}

// ---------------------------------------------------------------------------
// Reset time extraction
// ---------------------------------------------------------------------------

// Capture lazily up to a sentence/segment terminator or end of string.
const END = String.raw`(?=[.;·|](?:\s|$)|\n|\s*$)`;
const RESET_PATTERNS: RegExp[] = [
  new RegExp(String.raw`try again (?:at|after)\s+([^\n;·|]{1,48}?)` + END, 'i'),
  new RegExp(String.raw`resets?\s+(?:at\s+|on\s+)?([^\n;·|]{1,48}?)` + END, 'i'),
];

/** Extracts a reset/retry time such as "7:33 PM", "8am (UTC)", "3:20pm (Europe/Brussels)". */
export function resetTime(s: string): string | null {
  if (!s) return null;
  for (const re of RESET_PATTERNS) {
    const m = s.match(re);
    if (!m || !m[1]) continue;
    const t = m[1].trim().replace(/[.,;:\s]+$/, '');
    if (/\d/.test(t)) return t;
  }
  return null;
}

const later = (t: string | null): string => (t ? `after ${t}` : 'later');

// ---------------------------------------------------------------------------
// Fallback helpers
// ---------------------------------------------------------------------------

const GENERIC_PARTS: RegExp[] = [
  /^timeout$/i,
  /^exitCode=-?\d+$/i,
  /^output not drained$/i,
  /^structured final event missing$/i,
  /^structured result failed$/i,
  /^agent error events:\s*\d+$/i,
  /^Agent process exited with code -?\d+\.?$/i,
  /^Claude result subtype:\s*\S+$/i,
];

const isGeneric = (part: string): boolean => GENERIC_PARTS.some((re) => re.test(part));

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max - 1).trimEnd() + '…';
}

/** First non-generic line of the error (Bridge joins parts with '; ', agent messages with '\n'). */
function firstAgentLine(error: string): string | null {
  for (const line of error.split(/\r?\n/)) {
    const kept = line
      .split(/;\s+/)
      .map((p) => p.trim())
      .filter((p) => p.length > 0 && !isGeneric(p));
    const joined = kept.join('; ').replace(/^(?:error|fatal)\s*:\s*/i, '').trim();
    if (joined.length >= 3) return truncate(joined, 160);
  }
  return null;
}

// ---------------------------------------------------------------------------
// Rules (evaluated in order, first match wins)
// ---------------------------------------------------------------------------

const RULES: Rule[] = [
  {
    kind: 'codex_limit',
    agent: 'codex',
    scope: 'all',
    test: /usage limit|hit your usage limit|rate limit reached/i,
    build: (c) => ({
      title: 'ChatGPT usage limit reached',
      todo: `Continue with Claude now, or resend to GPT ${later(resetTime(c.all))}.`,
      actions: ['continue_other', 'retry'],
    }),
  },
  {
    kind: 'claude_limit',
    agent: 'claude',
    scope: 'all',
    test: /hit your (?:weekly |session |5-hour )?limit|usage limit reached|Claude AI usage limit/i,
    build: (c) => ({
      title: 'Claude usage limit reached',
      todo: `Continue with GPT now, or resend ${later(resetTime(c.all))}.`,
      actions: ['continue_other', 'retry'],
    }),
  },
  {
    kind: 'claude_auth',
    agent: 'claude',
    scope: 'all',
    test: /Invalid API key|Please run \/login|OAuth token (?:has )?expired|OAuth session expired|Failed to authenticate|authentication_error|Not logged in|\b401\b/i,
    build: () => ({
      title: 'Claude is signed out on the home PC',
      todo: 'On the home PC open a terminal, run claude and type /login, then resend. Meanwhile continue with GPT.',
      actions: ['continue_other', 'retry'],
    }),
  },
  {
    kind: 'codex_auth',
    agent: 'codex',
    scope: 'all',
    test: /\b401\b|Unauthorized|refresh token|codex login|not logged in|token_expired/i,
    build: () => ({
      title: 'Codex is signed out on the home PC',
      todo: 'On the home PC run codex login in a terminal, then resend. Meanwhile continue with Claude.',
      actions: ['continue_other', 'retry'],
    }),
  },
  {
    // 0.77.0: the model id is unknown to the provider or not available on this subscription
    kind: 'model_not_found',
    scope: 'all',
    test: /model_not_found|model is not supported|model (?:\S+ )?(?:does not exist|not found)|There's an issue with the selected model|not_found_error[^\n]*model/i,
    build: (c) => ({
      title: 'The selected model is not available on this account',
      todo: `Pick another model in the composer and resend, or continue with ${c.otherName}.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    // 0.77.0: Bridge supervisor closed a claimed job whose agent process was gone and that had no events
    kind: 'bridge_stuck_job',
    test: /bridge_stuck_job/i,
    build: () => ({
      title: 'The job hung on the home PC and was closed',
      todo: 'Resend the message (send "continue" to pick up from where it stopped).',
      actions: ['retry'],
    }),
  },
  {
    // Freedom F1 5b: was kind 'engie'; names and the connector's own messages come from its manifest
    kind: 'corporate',
    test: (c) => c.corporateBlocked.length > 0 || corporateErrorRe().test(c.error),
    build: () => {
      const m = corporate();
      const signin = m.signinTitle ? ` (${m.signinTitle})` : '';
      // 0.66.0: a paused connector - no button and no Resume; the message goes to another agent
      return {
        title: `${m.title} is not available`,
        todo: m.paused
          ? `${m.title} is paused: CO Web no longer offers its models. Send the message to another agent.`
          : `Sign in to ${m.portalTitle}${signin} on the device, or send the message to another agent.`,
        actions: ['continue_other'],
      };
    },
  },
  {
    kind: 'context_too_long',
    scope: 'all',
    test: /prompt is too long|context_length_exceeded|context window|maximum context/i,
    build: () => ({
      title: 'The conversation is too long for the model',
      todo: 'Retry in a new session (it gets the chat protocol instead of the full history).',
      actions: ['retry_new'],
    }),
  },
  {
    kind: 'session_missing',
    test: (c) =>
      /No conversation found/i.test(c.all) ||
      (/session/i.test(c.error) && /not found|no conversation|missing/i.test(c.error)),
    build: () => ({
      title: 'The agent session no longer exists',
      todo: 'Retry in a new session.',
      actions: ['retry_new'],
    }),
  },
  {
    kind: 'timeout',
    test: /Agent exceeded timeout of \d+ minutes|timed? ?out/i,
    build: (c) => {
      const n = c.error.match(/timeout of (\d+) minutes/i)?.[1];
      return {
        title: n ? `Stopped at the ${n}-minute job limit` : 'Stopped at the job time limit',
        todo: 'Send "continue" to resume from where it stopped, or split the task. The limit can be raised up to 240 min.',
        actions: ['retry'],
      };
    },
  },
  {
    // 0.77.0: the home PC had no network or DNS (30.10: claims and agent API calls failed with ENOTFOUND)
    kind: 'network',
    scope: 'all',
    test: /ENOTFOUND|EAI_AGAIN|getaddrinfo|Can't reach the API server|remote name could not be resolved/i,
    build: (c) => ({
      title: 'The home PC could not reach the model provider (network or DNS)',
      todo: `Check the internet connection of the home PC, then resend; or continue with ${c.otherName}.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    kind: 'overloaded',
    test: /overloaded|\b(?:529|503|502)\b|500 Internal|stream disconnected|Service Unavailable|Reconnecting|ECONNRESET|network error/i,
    build: (c) => ({
      title: 'The model provider is temporarily unavailable',
      todo: `Resend in a few minutes, or continue with ${c.otherName}.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    // Bridge 1.16.0: an update that waited too long paused the job; Bridge queued its continuation in this chat
    kind: 'bridge_update_pause',
    test: /Paused for the Bridge update/i,
    build: (c) => /continues as job/i.test(c.error)
      ? {
        title: 'Paused for the home PC agent update; continues by itself',
        todo: 'Nothing to do: the next message of this chat continues the job in the same session after the restart.',
        actions: [],
      }
      : {
        title: 'Stopped for the home PC agent update',
        todo: 'Resend the message (send "continue" to pick up from where it stopped).',
        actions: ['retry'],
      },
  },
  {
    kind: 'bridge_restart',
    test: /BridgeAgent restarted/i,
    build: () => ({
      title: 'The home PC agent restarted during the job (update or reboot)',
      todo: 'Just resend the message.',
      actions: ['retry'],
    }),
  },
  {
    kind: 'cancelled',
    test: /Cancellation requested/i,
    build: () => ({
      title: 'Stopped by you',
      todo: 'Resend if you still need the answer.',
      actions: ['retry'],
    }),
  },
  {
    kind: 'busy',
    test: /previous message in this chat is still running/i,
    build: () => ({
      title: 'The previous reply is still running',
      todo: 'Wait for it to finish or stop it, then resend.',
      actions: ['retry'],
    }),
  },
  {
    // site agent 1.2.1: Codex on the shared hosting hits the thread limit and cannot run any tool
    kind: 'site_codex_no_tools',
    test: /Site host cannot run Codex tools|code-mode host|creating threadpool failed|failed to spawn thread/i,
    build: (c) => ({
      title: 'GPT cannot run tools on the server',
      todo: `Resend with Home selected (the job waits there if Home is offline), or continue with ${c.otherName} now.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    kind: 'codex_crash',
    agent: 'codex',
    test: /panicked at|exitCode=128\b|non-unwinding panic/i,
    build: () => ({
      title: 'Codex CLI crashed',
      todo: 'Resend. If it repeats, retry in a new session or continue with Claude.',
      actions: ['retry', 'retry_new', 'continue_other'],
    }),
  },
  {
    // 0.77.0: thread/process limit of the host (EAGAIN) outside the Codex site case above
    kind: 'thread_limit',
    test: /\bEAGAIN\b|os error 11\b|Resource temporarily unavailable/i,
    build: (c) => ({
      title: 'The host ran out of processes or threads',
      todo: `Resend in a minute, or continue with ${c.otherName}.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    // 0.77.0: Windows native crash of the agent process (0xC0000409 stack buffer overrun, 0xC0000005 access violation)
    kind: 'native_crash',
    test: /exitCode=-1073740791\b|exitCode=-1073741819\b|0xC0000409|0xC0000005/i,
    build: () => ({
      title: 'The agent process crashed on the home PC',
      todo: 'Resend. If it repeats, retry in a new session.',
      actions: ['retry', 'retry_new'],
    }),
  },
  {
    // 0.77.0: exit code 128 of Claude (git or start-up failure; seen on the site host 02.11)
    kind: 'git_128',
    test: /exitCode=128\b/i,
    build: (c) => ({
      title: 'The agent exited with code 128 (git or start-up failure)',
      todo: `Resend; if it repeats, continue with ${c.otherName} or ask Claude to check the host log for ${c.jobId || 'this job'}.`,
      actions: ['retry', 'continue_other'],
    }),
  },
  {
    // 0.86.1: a bad files permission (schedule chat with "files": true, 08.11) is not a model problem
    kind: 'bad_permissions',
    test: /permissions\.files must/i,
    build: () => ({
      title: 'The chat has an invalid Files permission',
      todo: 'Open the Chat tab on the right (What agents may do), set Files to read-only or write, then resend. If the chat comes from a schedule, fix "files" in schedules.json to "read" or "write".',
      actions: ['retry'],
    }),
  },
  {
    kind: 'bad_request',
    test: /not in the catalog of agent|is not allowed for (?:agent|project)|Unknown agent profile|Unknown project profile|mode must be/i,
    build: () => ({
      title: 'The home PC does not accept these settings',
      todo: 'Pick another model or effort in the composer and resend. If the setting is new, the home Bridge may need an update.',
      actions: ['retry'],
    }),
  },
  {
    kind: 'folder_missing',
    test: /Workspace does not exist|Context folder not found|Unknown context root|excluded folder name/i,
    build: () => ({
      title: 'A connected folder is missing on the home PC',
      todo: 'Check the connected folders of the project (Project settings), then resend.',
      actions: ['retry'],
    }),
  },
  {
    kind: 'git_missing',
    test: /requires a Git work tree|git\.exe was not found/i,
    build: () => ({
      title: 'Git is not installed on the home PC',
      todo: 'Install Git for Windows on the home PC, then resend.',
      actions: ['retry'],
    }),
  },
  {
    kind: 'missing_artifacts',
    test: /missing artifacts:\s*([^;\n]*)/i,
    build: (_c, m) => {
      const names = (m?.[1] ?? '').trim().replace(/[.,\s]+$/, '');
      return {
        title: names
          ? `The agent finished without the expected file(s): ${truncate(names, 120)}`
          : 'The agent finished without the expected file(s)',
        todo: 'Ask the agent to create the missing file, or resend.',
        actions: ['retry'],
      };
    },
  },
  {
    kind: 'fallback',
    test: () => true,
    build: (c) => ({
      title: firstAgentLine(c.error) ?? 'The agent stopped unexpectedly',
      todo: `Resend. If it fails again, continue with ${c.otherName} or ask Claude to check the Bridge log for ${
        c.jobId ? `job ${c.jobId}` : 'this job'
      }.`,
      actions: ['retry', 'continue_other'],
    }),
  },
];

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

function buildDetail(c: Ctx, failed: boolean): string {
  const parts: string[] = [];
  if (c.error) parts.push(c.error);
  if (c.corporateBlocked) parts.push(`${corporate().title}: ${c.corporateBlocked}`);
  if (failed && c.text) parts.push(`Reply text:\n${c.text}`);
  if (c.jobId) parts.push(`Job: ${c.jobId}`);
  return parts.length ? parts.join('\n\n') : '(no error text)';
}

export function diagnose(input: DiagnoseInput): Diagnosis | null {
  const error = (input.error ?? '').trim();
  const text = (input.text ?? '').trim();
  const blocked = corporateBlocked(input);
  const failed = input.status === 'failed';
  if (!error && !failed && !blocked) return null;

  const agent = (input.agent ?? '').toLowerCase();
  const isCodex = agent === 'codex';
  const ctx: Ctx = {
    error,
    text,
    all: text ? `${error}\n${text}` : error,
    agent,
    isCodex,
    isClaude: agent === 'claude' || corporateAgent(agent)?.family === 'claude',
    otherName: isCodex ? 'Claude' : 'GPT',
    jobId: input.jobId ?? '',
    corporateBlocked: blocked,
  };

  for (const rule of RULES) {
    if (rule.agent === 'codex' && !ctx.isCodex) continue;
    if (rule.agent === 'claude' && !ctx.isClaude) continue;

    let m: RegExpMatchArray | null = null;
    if (rule.test instanceof RegExp) {
      m = (rule.scope === 'all' ? ctx.all : ctx.error).match(rule.test);
      if (!m) continue;
    } else if (!rule.test(ctx)) {
      continue;
    }

    return { kind: rule.kind, ...rule.build(ctx, m), detail: buildDetail(ctx, failed || !!error) };
  }
  return null; // unreachable: fallback always matches
}
