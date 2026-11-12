// SPDX-License-Identifier: AGPL-3.0-or-later
// Maps errors to the user-facing categories of TZ section 23.

import { DriveAuthError, DriveError } from './drive';

export function categorize(e: unknown): { category: string; message: string; detail?: string } {
  if (e instanceof DriveAuthError) return { category: 'auth', message: 'Google Drive session expired. Reconnect to continue.' };
  if (e instanceof DriveError) {
    if (e.status === 403) return { category: 'permission denied', message: 'Drive refused the operation.', detail: e.message };
    if (e.status === 404) return { category: 'Drive sync', message: 'Item not found in Drive yet.', detail: e.message };
    return { category: 'Drive sync', message: 'Drive request failed.', detail: e.message };
  }
  const msg = e instanceof Error ? e.message : String(e);
  if (/session/i.test(msg) && /not found|no conversation|missing/i.test(msg))
    return { category: 'session missing', message: 'The provider session no longer exists.', detail: msg };
  if (/timed? ?out/i.test(msg)) return { category: 'timeout', message: 'The job timed out.', detail: msg };
  if (/Unknown project profile|not allowed for project|environment does not match|Unknown agent profile/i.test(msg))
    return { category: 'invalid job', message: 'The home agent rejected the job.', detail: msg };
  return { category: 'error', message: msg.length > 200 ? msg.slice(0, 200) + '…' : msg, detail: msg.length > 200 ? msg : undefined };
}
