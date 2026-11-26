// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.55.0: Files tab of the inspector - the chat's files as version streams (chatFiles.ts, docs/SPEC-chat-files.md).
// One row per file with its main (newest) version; ⋯ opens the versions (download, make main, branch, delete), Keep /
// Temporary and Hide. Dragging a file onto another merges the two streams after a 10 s undo window.
// 0.129.0: the folder of a chat shows two lists of one load (useChatStreams): Documents (added by the person) and
// Products (made by the agent), chatFolder.ts splitStreams; a merge stays inside one list.
import { useEffect, useRef, useState } from 'react';
import type { Chat } from './types';
import type { Root } from './workbench';
import { loadChatEvents } from './workbench';
import { dbFiles } from './dbapi';
import { categorize } from './errors';
import { formatBytes, formatLocal } from './util';
import { useOpenFile } from './FileViewer';
import {
  branchVersion, buildStreams, bytesOf, deleteVersion, describe, fetchVersion, headUtc, kindOf, listVersions, makeMain, mergeStreams, setHidden,
  isWorkingFile, setKind, TEMP_DAYS, visible, type ChatStream, type VersionItem,
} from './chatFiles';

function saveBytes(bytes: ArrayBuffer, name: string) {
  const url = URL.createObjectURL(new Blob([bytes]));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

const baseName = (s: ChatStream) => s.name.split('/').pop() || s.name;

export interface ChatStreamsData {
  streams: ChatStream[] | null;
  dbOk: boolean;
  err: string | null;
  reload: () => void;
}

/** The chat's files as version streams, loaded once for both lists of the chat folder; again when the chat changes. */
export function useChatStreams(chat: Chat, enabled = true): ChatStreamsData {
  const [streams, setStreams] = useState<ChatStream[] | null>(null);
  const [dbOk, setDbOk] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const seq = useRef(0);
  const reload = () => {
    const n = ++seq.current;
    Promise.all([loadChatEvents(chat), dbFiles(chat.chatId)])
      .then(([ev, rows]) => {
        if (n !== seq.current) return;
        setDbOk(rows !== null);
        setStreams(buildStreams(ev, rows, chat.projectId, chat.chatId));
        setErr(null);
      })
      .catch((e) => {
        if (n === seq.current) setErr(categorize(e).message);
      });
  };
  useEffect(() => {
    setStreams(null);
    setErr(null);
  }, [chat.chatId]);
  useEffect(() => {
    if (enabled) reload();
  }, [enabled, chat.chatId, chat.updatedUtc]); // eslint-disable-line react-hooks/exhaustive-deps
  return { streams, dbOk, err, reload };
}

export function FilesTab({ chat, root, canWrite, data, only, emptyText }: {
  chat: Chat; root: Root; canWrite: boolean; data: ChatStreamsData;
  /** the streams of this list (a part of data.streams); all when absent */
  only?: ChatStream[] | null;
  emptyText?: string;
}) {
  const { dbOk, reload } = data;
  const all = data.streams;
  const streams = only ?? all;
  const [err, setErr] = useState<string | null>(null);
  const [showHidden, setShowHidden] = useState(false);
  const [pending, setPending] = useState<{ from: ChatStream; into: ChatStream; timer: number } | null>(null);
  const [dragKey, setDragKey] = useState<string | null>(null);
  const [overKey, setOverKey] = useState<string | null>(null);
  const load = async () => reload();

  function proposeMerge(from: ChatStream, into: ChatStream) {
    if (pending) window.clearTimeout(pending.timer);
    const timer = window.setTimeout(() => {
      setPending(null);
      mergeStreams(root.id, chat.chatId, chat.folderId, from, into)
        .then(load)
        .catch((e) => setErr(categorize(e).message));
    }, 10000);
    setPending({ from, into, timer });
  }

  if (data.err && !all) return <p className="error-text pad">{data.err}</p>;
  if (!streams) return <p className="muted pad">Loading…</p>;
  const list = streams.filter((s) => visible(s, showHidden));
  // Service files fold under one line, by the same rule as under a message (isWorkingFile); a kind the user set wins.
  const isService = (s: ChatStream) => (s.row?.kindByUser ? s.row.kind === 'temp'
    : kindOf(s) === 'temp' || isWorkingFile(s.name, s.row?.head.size ?? s.versions[s.versions.length - 1]?.size ?? undefined));
  const docs = list.filter((s) => !isService(s));
  const service = list.filter(isService);
  const row = (s: ChatStream) => (
    <FileRowView
      key={s.key}
      s={s}
      root={root}
      chat={chat}
      canWrite={canWrite && dbOk}
      dragging={dragKey !== null}
      over={overKey === s.key && dragKey !== s.key}
      onDragStart={() => setDragKey(s.key)}
      onDragEnd={() => { setDragKey(null); setOverKey(null); }}
      onDragOver={() => setOverKey(s.key)}
      onDrop={() => {
        const from = streams.find((x) => x.key === dragKey);
        setDragKey(null);
        setOverKey(null);
        if (from && from.key !== s.key) proposeMerge(from, s);
      }}
      onChanged={() => void load()}
      onError={setErr}
    />
  );
  const hiddenCount = streams.filter((s) => !visible(s) && s.row?.state !== 'merged').length;
  return (
    <div className="pad files-tab">
      <div className="files-head">
        <span className="muted small">{list.length} file{list.length === 1 ? '' : 's'} · newest first · drag a file onto another to merge</span>
        <button className="link small" onClick={() => void load()}>Refresh</button>
      </div>
      {!dbOk && <p className="muted small">The database is not reachable: files from the messages only, without versions actions.</p>}
      {(err || data.err) && <p className="error-text small">{err || data.err}</p>}
      {pending && (
        <div className="files-pending small">
          Merging <b>{baseName(pending.from)}</b> into <b>{baseName(pending.into)}</b> in 10 s…{' '}
          <button className="link" onClick={() => { window.clearTimeout(pending.timer); setPending(null); }}>Undo</button>
        </div>
      )}
      {!list.length && <p className="muted small">{emptyText ?? 'No files in this chat yet. Files the agent writes and files you attach appear here.'}</p>}
      <ul className="files-list">{docs.map(row)}</ul>
      {!!service.length && (
        <details className="service-files">
          <summary className="muted small">Service files ({service.length})</summary>
          <ul className="files-list">{service.map(row)}</ul>
        </details>
      )}
      {hiddenCount > 0 && (
        <button className="link small" onClick={() => setShowHidden(!showHidden)}>
          {showHidden ? 'Hide removed files' : `Show removed and expired files (${hiddenCount})`}
        </button>
      )}
    </div>
  );
}

function FileRowView({
  s, root, chat, canWrite, dragging, over, onDragStart, onDragEnd, onDragOver, onDrop, onChanged, onError,
}: {
  s: ChatStream; root: Root; chat: Chat; canWrite: boolean; dragging: boolean; over: boolean;
  onDragStart: () => void; onDragEnd: () => void; onDragOver: () => void; onDrop: () => void; onChanged: () => void; onError: (m: string) => void;
}) {
  const [menu, setMenu] = useState(false);
  const [showVersions, setShowVersions] = useState(false);
  const [busy, setBusy] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const head = s.versions[s.versions.length - 1];
  const n = Math.max(s.row?.versions || 0, s.versions.length);
  const state = s.row?.state || 'live';
  const temp = kindOf(s) === 'temp';

  useEffect(() => {
    if (!menu) return;
    const close = (e: MouseEvent) => { if (!menuRef.current?.contains(e.target as Node)) setMenu(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [menu]);

  async function run(fn: () => Promise<void>) {
    setBusy(true);
    try {
      await fn();
    } catch (e) {
      onError(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  const headBytes = async (): Promise<ArrayBuffer> => {
    const v = head && !head.legacy
      ? await fetchVersion(root.id, { path: s.key, md5: s.row?.head.md5 || head.md5, sha256: s.row?.head.sha256 || head.sha256 })
      : head ? await fetchVersion(root.id, { path: head.path, legacy: true, fileId: head.fileId }) : null;
    if (!v) throw new Error('No version to download.');
    return v.bytes;
  };
  const download = () => run(async () => saveBytes(await headBytes(), baseName(s)));
  const [openFile, viewer] = useOpenFile();

  return (
    <li
      className={`file-row${over ? ' drop-over' : ''}${state !== 'live' ? ' file-off' : ''}`}
      draggable={canWrite && state === 'live'}
      onDragStart={(e) => { e.dataTransfer.effectAllowed = 'move'; onDragStart(); }}
      onDragEnd={onDragEnd}
      onDragOver={(e) => { if (dragging && canWrite && state === 'live') { e.preventDefault(); onDragOver(); } }}
      onDrop={(e) => { e.preventDefault(); onDrop(); }}
    >
      <div className="tree-row" title={`${s.name}
${describe(s)}`}>
        <button type="button" className="link name" disabled={busy || state === 'gone'} title="Click to open (download: ⋯)"
          onClick={() => openFile({ name: baseName(s), path: s.key, load: headBytes, fallback: () => void download() })}>
          {head?.origin === 'user' ? '↑' : '↓'} {n > 1 ? '🗂' : '📄'} {s.name}
        </button>
        {viewer}
        <span className="muted tiny">{n > 1 ? `${n} v · ` : ''}{formatBytes(s.row?.head.size ?? head?.size ?? null)}</span>
        <div className="file-menu" ref={menuRef}>
          <button className="link" aria-label={`Actions for ${s.name}`} onClick={() => setMenu(!menu)}>⋯</button>
          {menu && (
            <div className="menu-pop">
              <button disabled={busy} onClick={() => { setMenu(false); void download(); }}>Download</button>
              <button onClick={() => { setMenu(false); setShowVersions(!showVersions); }}>{showVersions ? 'Hide versions' : 'Versions…'}</button>
              {canWrite && s.row && state === 'live' && (
                <button disabled={busy} onClick={() => { setMenu(false); void run(async () => { await setKind(chat.chatId, s, temp ? 'doc' : 'temp'); onChanged(); }); }}>
                  {temp ? 'Keep (do not expire)' : `Service file (expires in ${TEMP_DAYS} days)`}
                </button>
              )}
              {canWrite && s.row && (state === 'live' || state === 'hidden') && (
                <button disabled={busy} onClick={() => { setMenu(false); void run(async () => { await setHidden(chat.chatId, s, state === 'live'); onChanged(); }); }}>
                  {state === 'live' ? 'Remove from the list' : 'Show in the list'}
                </button>
              )}
            </div>
          )}
        </div>
      </div>
      <div className="muted tiny file-sub">
        {formatLocal(headUtc(s))}
        {temp && ' · service'}
        {state === 'gone' && ' · expired'}
        {s.row?.extra?.parent && ` · branched from ${s.row.extra.parent.name}`}
        {s.row?.annotation && <span className="file-note"> · {s.row.annotation}</span>}
      </div>
      {showVersions && <Versions s={s} root={root} chat={chat} canWrite={canWrite} onChanged={onChanged} onError={onError} />}
    </li>
  );
}

const BY: Record<VersionItem['by'], string> = { agent: '↓ agent', user: '↑ you', drive: '✎ Drive' };

function Versions({ s, root, chat, canWrite, onChanged, onError }: {
  s: ChatStream; root: Root; chat: Chat; canWrite: boolean; onChanged: () => void; onError: (m: string) => void;
}) {
  const [items, setItems] = useState<VersionItem[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const load = () => listVersions(root.id, s).then((r) => setItems(r.items)).catch((e) => onError(categorize(e).message));
  useEffect(() => {
    void load();
  }, [s.key]); // eslint-disable-line react-hooks/exhaustive-deps

  async function run(fn: () => Promise<void>) {
    setBusy(true);
    setNote(null);
    try {
      await fn();
    } catch (e) {
      onError(categorize(e).message);
    } finally {
      setBusy(false);
    }
  }

  if (!items) return <div className="muted tiny versions">Loading versions…</div>;
  if (!items.length) return <div className="muted tiny versions">No versions in Drive (the file may still be uploading).</div>;
  return (
    <ul className="versions">
      {items.map((v, i) => (
        <li key={`${v.fileId || v.legacyPath}:${v.revisionId || i}`} className={v.head ? 'v-head' : ''}>
          <span className="tiny">{formatLocal(v.utc)}</span>
          <span className="tiny">{BY[v.by]}{v.seq != null ? ` · #${v.seq}` : ''}{v.restored ? ' · restored' : ''}{v.member ? ' · merged' : ''}{v.legacyPath ? ' · copy' : ''}</span>
          <span className="muted tiny">{formatBytes(v.size)}</span>
          {v.head && <span className="tag">main</span>}
          <button className="link tiny" disabled={busy} onClick={() => void run(async () => saveBytes(await bytesOf(root.id, v), baseName(s)))}>Download</button>
          {canWrite && !v.head && (
            <>
              <button className="link tiny" disabled={busy} title="Upload this version as the newest one (the history stays)"
                onClick={() => void run(async () => { await makeMain(root.id, chat.chatId, s, v); setNote('Made main.'); onChanged(); })}>Make main</button>
              <button className="link tiny" disabled={busy} title="A new independent file from this version"
                onClick={() => void run(async () => { const n = await branchVersion(root.id, chat.chatId, chat.folderId, s, v); setNote(`New file ${n}.`); onChanged(); })}>Branch</button>
              <button className="link tiny" disabled={busy}
                onClick={() => { if (window.confirm('Delete this version from Drive? It cannot be restored.')) void run(async () => { await deleteVersion(root.id, v); await load(); }); }}>Delete</button>
            </>
          )}
        </li>
      ))}
      {note && <li className="ok-text tiny">{note}</li>}
    </ul>
  );
}
