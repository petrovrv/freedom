// SPDX-License-Identifier: AGPL-3.0-or-later
// Wakes the server agent on the CO site. The web server starts it when it is not running;
// the agent itself takes jobs from transport/hosts/site on Drive like the home Bridge.

import { getToken } from './auth';

export const SITE_PING_MS = 60000;

export async function pingSiteAgent(): Promise<void> {
  const token = getToken();
  try {
    await fetch('/agent/ping.php', {
      method: 'POST',
      // The bearer token identifies the owner inside CO Desktop, which has no cookie session.
      headers: { 'X-CO-Auth': '1', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      credentials: 'same-origin',
      cache: 'no-store',
    });
  } catch {
    /* the host pill shows the agent state from owner.json */
  }
}
