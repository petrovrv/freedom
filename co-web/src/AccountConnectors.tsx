// SPDX-License-Identifier: AGPL-3.0-or-later
import { Fragment, useEffect, useState } from 'react';
import { categorize } from './errors';
import { findChild, getJson, updateContent } from './drive';
import type { HostDirs } from './workbench';

// transport/hosts/<host>/cloud-connectors.json (Bridge 1.2.2; F12: of any Bridge executor with a Drive folder): boxes of the Claude account connectors (Gmail,
// Google Calendar). Bridge keeps it in line with its local copy (CO Desktop on the home computer) and turns unticked
// boxes and "Never" into --disallowedTools of every Claude and Dispatcher job.
const FILE_NAME = 'cloud-connectors.json';
const SITE_ID = 'site';
const LEVELS = ['Read', 'Create', 'Edit', 'Delete'] as const;
type Level = (typeof LEVELS)[number];

interface Connector {
  id: string;
  name: string;
  description?: string;
  rights: string;
  tools: Partial<Record<Level | 'Never', string[]>>;
}

interface ConnectorFile {
  schemaVersion: number;
  updatedUtc?: string;
  updatedBy?: string;
  connectors: Connector[];
}

// Display name: part after the last '__'.
function shortName(tool: string): string {
  const i = tool.lastIndexOf('__');
  return i >= 0 ? tool.slice(i + 2) : tool;
}

// "Read, Create" -> Set{'Read','Create'}; unknown parts are ignored.
function parseRights(rights: string | undefined): Set<Level> {
  const set = new Set<Level>();
  for (const part of (rights ?? '').split(',')) {
    const p = part.trim();
    if ((LEVELS as readonly string[]).includes(p)) set.add(p as Level);
  }
  return set;
}

// Always ordered Read, Create, Edit, Delete.
function formatRights(set: Set<Level>): string {
  return LEVELS.filter((l) => set.has(l)).join(', ');
}

export function AccountConnectorsSection({ host }: { host: HostDirs | null }) {
  const [file, setFile] = useState<{ id: string; doc: ConnectorFile } | null | undefined>(undefined);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const isHome = !!host && host.hostId !== SITE_ID;
  const dirId = host?.hostDirId;

  async function load() {
    if (!isHome || !dirId) return;
    try {
      const f = await findChild(dirId, FILE_NAME);
      setFile(f ? { id: f.id, doc: await getJson<ConnectorFile>(f.id) } : null);
    } catch (e) {
      setMsg(categorize(e).message);
    }
  }

  useEffect(() => {
    setFile(undefined);
    setMsg(null);
    void load();
  }, [dirId, isHome]); // eslint-disable-line react-hooks/exhaustive-deps

  async function toggle(c: Connector, level: Level, checked: boolean) {
    if (!file) return;
    setBusy(c.id);
    setMsg(null);
    try {
      // Reads the file again before writing: Bridge also writes it.
      const doc = await getJson<ConnectorFile>(file.id);
      const target = doc.connectors.find((x) => x.id === c.id);
      if (!target) throw new Error(`${c.name}: no longer listed in ${FILE_NAME}.`);
      // Only the clicked box changes; unticking Read keeps the others as they are.
      const set = parseRights(target.rights);
      if (checked) set.add(level);
      else set.delete(level);
      target.rights = formatRights(set);
      doc.updatedUtc = new Date().toISOString();
      doc.updatedBy = 'co-web';
      await updateContent(file.id, JSON.stringify(doc, null, 2), 'application/json');
      setFile({ id: file.id, doc });
      setMsg(`${target.name}: saved. Bridge applies it to the next job once Drive syncs (usually under a minute).`);
    } catch (e) {
      setMsg(categorize(e).message);
    } finally {
      setBusy(null);
    }
  }

  if (!host || !isHome) {
    return <p className="muted tiny">Gmail and Google Calendar of the Claude account work on the home computer (Home).</p>;
  }
  if (file === undefined) {
    return (
      <div>
        <div className="muted small">Loading…</div>
        {msg && <p className="tiny">{msg}</p>}
      </div>
    );
  }
  if (file === null) {
    return <p className="muted tiny">cloud-connectors.json not found: Bridge 1.2.2 or newer writes it at start.</p>;
  }

  const { doc } = file;
  return (
    <div>
      <p className="muted tiny">
        Tools of the Claude account connectors for Claude jobs. A ticked box is permission without
        confirmation. Sending mail, new events and replies to invitations are never offered.
      </p>
      <table className="acc-conn">
        <thead>
          <tr>
            <th>Name</th>
            {LEVELS.map((l) => <th key={l}>{l}</th>)}
          </tr>
        </thead>
        <tbody>
          {doc.connectors.map((c) => {
            const rights = parseRights(c.rights);
            const never = c.tools.Never ?? [];
            return (
              <Fragment key={c.id}>
                <tr>
                  <td title={c.description}>{c.name}</td>
                  {LEVELS.map((l) => {
                    const tools = c.tools[l] ?? [];
                    const empty = tools.length === 0;
                    // Empty box: grey and unchecked; Create/Edit/Delete are locked while Read is off.
                    const disabled = empty || busy !== null || (l !== 'Read' && !rights.has('Read'));
                    return (
                      <td key={l}>
                        <input
                          type="checkbox"
                          checked={!empty && rights.has(l)}
                          disabled={disabled}
                          title={empty ? 'No tools' : tools.map(shortName).join(', ')}
                          onChange={(e) => void toggle(c, l, e.target.checked)}
                        />
                      </td>
                    );
                  })}
                </tr>
                {never.length > 0 && (
                  <tr className="never">
                    <td colSpan={LEVELS.length + 1} className="muted tiny">
                      Never: {never.map(shortName).join(', ')}
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
      <div>
        {doc.updatedUtc && (
          <span className="muted tiny">
            Last change: {new Date(doc.updatedUtc).toLocaleString()} by {doc.updatedBy ?? '?'}{' '}
          </span>
        )}
        <button
          className="btn small"
          disabled={busy !== null}
          onClick={() => {
            setMsg(null);
            void load();
          }}
        >
          Refresh
        </button>
      </div>
      {msg && <p className="tiny">{msg}</p>}
    </div>
  );
}
