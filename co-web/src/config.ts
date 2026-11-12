// SPDX-License-Identifier: AGPL-3.0-or-later
import { corporate, pausedAgents } from './corporate.ts';
// Static configuration of the CO web client.
// Nothing here is secret: the OAuth client ID is public by design.

export const APP_VERSION = '0.101.2';

// Freedom F1: google.webClientId of install.json, put in by vite.config.ts (empty outside a vite build).
declare const __CO_CLIENT_ID__: string | undefined;
export const CLIENT_ID: string = typeof __CO_CLIENT_ID__ === 'string' ? __CO_CLIENT_ID__ : '';

// Full Drive scope: drive.file cannot see files written by Google Drive for Desktop
// (verified with the probe on 2026-09-25).
// pubsub: fast transport to Bridge v0.9 (falls back to Drive polling when not permitted).
export const SCOPES = 'openid email https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/pubsub';

export const WORKBENCH_FOLDER_NAME = 'AI-Workbench';
export const HOST_ID = 'home-main';

// Executors the user can switch between. Chats stay on Drive either way; only the job
// transport folder differs. Events without hostId were written for home-main.
export type HostId = 'home-main' | 'site';
export const HOSTS: { id: HostId; label: string; title: string }[] = [
  { id: 'home-main', label: 'Home', title: 'Home computer (BridgeAgent)' },
  { id: 'site', label: 'Site', title: 'Server agent on the CO site (works when the computer is off)' },
];
export const HOST_STORAGE_KEY = 'co.host';
// S2 (0.98.0): executor preference 'auto' | 'home-main' | 'site' (routing.ts HostPref); the old co.host choice is not
// carried over - everyone starts on auto, the manual choice is in Host and devices.
export const HOST_PREF_KEY = 'co.hostPref';
// Host name of this CO site (agents are told where the server agent and the database live).
export const siteHost = (): string => (typeof location !== 'undefined' && location.host) || 'the CO site';
export const DEFAULT_BRANCH = 'BR-root';

// Environment id of a member of this workbench ([a-z0-9-]); Freedom F1: the list comes from the server (members.ts).
export type EnvironmentId = string;

// Filled in place by members.ts from the table members (db.php op members) and its localStorage copy.
export const ENVIRONMENTS: { id: EnvironmentId; displayName: string }[] = [];

// Google identity -> own environment. The owner may switch to any environment;
// other members are locked to their own. Real access control is the Drive ACL and the server.
export const ALLOWLIST: Record<string, { env: EnvironmentId; owner: boolean }> = {};

// 'claude' | 'codex' and the agents of the corporate connector (corporate.ts, from its manifest: e.g. its LLM gateway and
// its dispatcher).
export type AgentId = 'claude' | 'codex' | (string & {});

export const AGENTS: { id: AgentId; displayName: string; capability: string | null }[] = [
  { id: 'claude', displayName: 'Claude', capability: null },
  { id: 'codex', displayName: 'GPT', capability: null },
  // Corporate agents. 0.66.0: a paused connector (owner's decision 27.10) keeps them only so old messages show their
  // agent's name; never offered in the composer, the new-project dialog or the task hint (PICKABLE_AGENTS).
  ...corporate().agents.map((a) => ({ id: a.id, displayName: a.title, capability: null })),
];

// 0.66.0: agents a user can choose.
export const RETIRED_AGENTS: ReadonlySet<string> = pausedAgents();
export const PICKABLE_AGENTS = AGENTS.filter((a) => !RETIRED_AGENTS.has(a.id));

// Polling policy (TZ section 17). Never faster than 1 s.
export const POLL_RUNNING_EVENTS_MS = 1500;
export const POLL_RUNNING_RESULT_MS = 3000;
export const POLL_HOST_MS = 30000;
export const POLL_LISTS_MS = 20000;
export const POLL_ACTIVITY_MS = 60000;
export const POLL_VERSION_MS = 5 * 60000;

export const MAX_ATTACHMENT_BYTES = 100 * 1024 * 1024;
// 0.67.0: a text paste this long gets the offer "Attach as a notebook (.md)" under the message box (canvas M4)
export const LONG_PASTE_CHARS = 3000;
export const DEFAULT_TIMEOUT_MINUTES = 120; // 0.49.0: was 30 (web-device SPEC §12)

// Budget for the context package sent when a new provider session has to be
// created inside an existing chat (provider switch or missing session).
export const CONTEXT_PACKAGE_MAX_CHARS = 120000;
