// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.89.0: family chat - a shared chat with chat.family, where the members of the workbench write to each other like in a
// messenger (bubbles, avatars, photos, emoji, reactions, replies) and the agent (@co, "AI Workbench") answers only when
// called by name (0.90.0: no more answers to problem reports on its own). Messages of people have no job
// (appendFamilyMessage); the server pushes them to the other members (family-lib.php). 0.90.0: the room lives in the
// right-panel tab "Family" (FamilyPanel.tsx), not in the project list.
import { useEffect, useRef, useState, type ClipboardEvent, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import { ALLOWLIST, MAX_ATTACHMENT_BYTES } from './config';
import { getBytes } from './drive';
import { Markdown } from './Markdown';
import { stripQuickReplies } from './quickReplies';
import { stripTitleMarker } from './chatTitle';
import { categorize } from './errors';
import { formatBytes, formatLocal } from './util';
import { familyName } from './workbench';
import type { AttachmentRef, ChatEvent } from './types';
import { memberColor, memberNames } from './members';

export const QUICK_REACTIONS = ['👍', '❤️', '😂', '😮', '😢', '🙏'];
const EMOJI = [
  '😀', '😃', '😄', '😁', '😆', '😅', '🤣', '😂', '🙂', '😉', '😊', '😇', '🥰', '😍', '😘', '😋', '😜', '🤪', '🤗', '🤔',
  '🤨', '😐', '😏', '🙄', '😬', '😌', '😴', '🤒', '🤯', '🥳', '😎', '🤓', '😕', '😟', '😮', '😲', '🥺', '😢', '😭', '😱',
  '😤', '😡', '🤬', '💩', '🤡', '👻', '😺', '🙈', '🙉', '🙊', '❤️', '🧡', '💛', '💚', '💙', '💜', '🖤', '💔', '💯', '✨',
  '👍', '👎', '👌', '✌️', '🤞', '🤝', '👏', '🙌', '🙏', '💪', '👋', '🤷', '🤦', '👀', '🎉', '🎂', '🎁', '☕', '🍕', '🍷',
  '🐶', '🐱', '🌸', '🌞', '🌧️', '❄️', '🔥', '⚡', '🏠', '🚗', '✈️', '🎓', '📚', '💻', '📱', '🎹', '⚽', '✅', '❌', '❓',
];

const handledPrefill = new Set<number>(); // prefills already put into a composer (it remounts on Family on/off)

export function familyEnv(email?: string): string | undefined {
  return email ? ALLOWLIST[email.toLowerCase()]?.env : undefined;
}

export function Avatar({ email, agent }: { email?: string; agent?: boolean }) {
  if (agent) return <span className="fam-avatar fam-agent" title="AI Workbench (@co)">co</span>;
  const env = familyEnv(email);
  return (
    <span className="fam-avatar" style={{ background: memberColor(env) || '#888' }} title={familyName(email)}>
      {familyName(email).slice(0, 1).toUpperCase()}
    </span>
  );
}

// "@co" anywhere as a word calls the agent; 0.90.1: also Cyrillic "@со" (and mixed letters - same keys on a ru layout)
export function wantsCo(text: string): boolean {
  return /(^|[^\p{L}\p{N}_@])@[cс][oо](?![\p{L}\p{N}_])/iu.test(text);
}

/** Hidden preamble of an agent turn in a family chat (sendTurn resume field: the agent sees it, the chat does not). */
export function familyBlock(called: boolean, author: string, recent: string, filesDir: string): string {
  return [
    '[Family chat]',
    `This is the family chat of ${memberNames()} in CO (AI Workbench).`
      + ' People write to each other here; you are @co (shown as "AI Workbench"), and speak only when called.',
    `Photos and files people sent are under ${filesDir} (one family-* folder per message).`,
    'The newest messages, with who wrote them:',
    recent,
    '',
    'Answer like a friend in a messenger, not like an assistant: the readers are millennials and younger. One to three short lines, '
      + 'casual and warm, a bit of humour is welcome; in Russian use "ты". In the language of the message, no headings, no bullet lists '
      + 'unless steps are really needed, no reports of what you checked or how you work, no versions, job ids, reviews or tests. '
      + 'Emoji are fine, do not overdo them.',
    called
      ? `${familyName(author)} called you with @co: do what they ask.`
      : 'Nobody called you by name: reply exactly [[silent]] and nothing else.',
    'Never send messages or mail outside CO on anyone\'s behalf.',
    '[End of family chat]',
    '',
  ].join('\n');
}

export function isSilent(ev: ChatEvent): boolean {
  return ev.type === 'assistant_message' && (ev.text || '').trim() === '[[silent]]';
}

// target eventId -> emoji -> authors (reaction events in order; remove takes one back)
export type Reactions = Map<string, Map<string, Set<string>>>;
export function reactionsOf(events: ChatEvent[]): Reactions {
  const out: Reactions = new Map();
  for (const e of events) {
    if (e.type !== 'reaction' || !e.target || !e.emoji || !e.author) continue;
    const per = out.get(e.target) || new Map<string, Set<string>>();
    const who = per.get(e.emoji) || new Set<string>();
    if (e.remove) who.delete(e.author);
    else who.add(e.author);
    if (who.size) per.set(e.emoji, who);
    else per.delete(e.emoji);
    out.set(e.target, per);
  }
  return out;
}

const IMG_RE = /\.(jpe?g|png|gif|webp|bmp|avif)$/i;
const isImage = (a: AttachmentRef) => IMG_RE.test(a.displayName) && !!a.fileId;
const blobs = new Map<string, Promise<string>>(); // fileId -> object URL (kept for the session)
function blobUrl(fileId: string, name: string): Promise<string> {
  let p = blobs.get(fileId);
  if (!p) {
    const type = /\.png$/i.test(name) ? 'image/png' : /\.gif$/i.test(name) ? 'image/gif' : /\.webp$/i.test(name) ? 'image/webp' : 'image/jpeg';
    p = getBytes(fileId).then((b) => URL.createObjectURL(new Blob([b], { type })));
    p.catch(() => blobs.delete(fileId));
    blobs.set(fileId, p);
  }
  return p;
}

function FamilyImage({ a, onOpen }: { a: AttachmentRef; onOpen: (url: string) => void }) {
  const [url, setUrl] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    blobUrl(a.fileId!, a.displayName).then((u) => live && setUrl(u)).catch((e) => live && setErr(categorize(e).message));
    return () => { live = false; };
  }, [a.fileId, a.displayName]);
  if (err) return <span className="muted tiny">📷 {a.displayName}: {err}</span>;
  if (!url) return <span className="fam-img loading" />;
  return <img className="fam-img" src={url} alt={a.displayName} loading="lazy" onClick={() => onOpen(url)} />;
}

function FileChip({ a }: { a: AttachmentRef }) {
  const [busy, setBusy] = useState(false);
  async function download() {
    if (!a.fileId) return;
    setBusy(true);
    try {
      const url = URL.createObjectURL(new Blob([await getBytes(a.fileId)]));
      const link = document.createElement('a');
      link.href = url;
      link.download = a.displayName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } finally {
      setBusy(false);
    }
  }
  return (
    <button type="button" className="fam-file" disabled={busy || !a.fileId} onClick={() => void download()}>
      📎 {a.displayName} <span className="muted tiny">{formatBytes(a.sizeBytes)}</span>
    </button>
  );
}

// Text with clickable links (people's messages are plain text, not Markdown)
function Linked({ text }: { text: string }) {
  const parts = text.split(/(https?:\/\/[^\s<>"]+)/g);
  return (
    <div className="fam-text">
      {parts.map((p, i) => (i % 2 ? <a key={i} href={p} target="_blank" rel="noreferrer">{p}</a> : p.replace(/(^|\s)@co\b/gi, '$1@co')))}
    </div>
  );
}

function short(ev?: ChatEvent): string {
  if (!ev) return '…';
  const t = stripQuickReplies(stripTitleMarker(ev.text || '')).replace(/\s+/g, ' ').trim();
  return t ? (t.length > 90 ? t.slice(0, 89) + '…' : t) : ev.attachments?.length ? '📷 / 📎' : '…';
}

export function FamilyMessage({
  ev, me, reactions, replyEv, onReact, onReply, onOpenImage, footer,
}: {
  ev: ChatEvent;
  me: string;
  reactions?: Map<string, Set<string>>;
  replyEv?: ChatEvent;
  onReact?: (emoji: string, remove: boolean) => void;
  onReply?: () => void;
  onOpenImage: (url: string) => void;
  footer?: ReactNode;
}) {
  const agent = ev.type === 'assistant_message';
  const mine = !agent && (ev.author || '').toLowerCase() === me.toLowerCase();
  const [picker, setPicker] = useState(false);
  const [tap, setTap] = useState(false); // phones: the tools show after a tap on the message
  const imgs = (ev.attachments || []).filter(isImage);
  const files = (ev.attachments || []).filter((a) => !isImage(a));
  const text = agent ? stripQuickReplies(stripTitleMarker(ev.text || '')) : ev.text || '';
  const failed = agent && ev.status && ev.status !== 'completed';
  return (
    <div className={`fam-row${mine ? ' mine' : ''}${tap ? ' tap' : ''}`} id={`ev-${ev.eventId}`} data-seq={ev.sequence}
      onClick={(e) => { if (!(e.target as HTMLElement).closest('a, button, img')) setTap((x) => !x); }}>
      {!mine && <Avatar email={ev.author} agent={agent} />}
      <div className="fam-col">
        {!mine && <div className="fam-name">{agent ? 'AI Workbench' : familyName(ev.author)}</div>}
        <div className={`fam-bubble${agent ? ' agent' : ''}${failed ? ' failed' : ''}`}>
          {replyEv && (
            <button type="button" className="fam-quote" onClick={() => document.getElementById(`ev-${replyEv.eventId}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })}>
              <b>{replyEv.type === 'assistant_message' ? 'AI Workbench' : familyName(replyEv.author)}</b> {short(replyEv)}
            </button>
          )}
          {!!imgs.length && (
            <div className={`fam-imgs n${Math.min(imgs.length, 4)}`}>
              {imgs.map((a) => <FamilyImage key={a.fileId} a={a} onOpen={onOpenImage} />)}
            </div>
          )}
          {agent ? (text && <Markdown text={text} />) : text && <Linked text={text} />}
          {failed && <div className="error-box small">{ev.error || `The agent ended: ${ev.status}`}</div>}
          {!!files.length && <div className="fam-files">{files.map((a) => <FileChip key={a.fileId || a.path} a={a} />)}</div>}
          <span className="fam-time">{formatLocal(ev.createdUtc).replace(/^.*?(\d{1,2}:\d{2}).*$/, '$1')}</span>
        </div>
        {!!reactions?.size && (
          <div className="fam-reactions">
            {[...reactions.entries()].map(([emoji, who]) => {
              const minePick = [...who].some((w) => w.toLowerCase() === me.toLowerCase());
              return (
                <button key={emoji} type="button" className={`fam-react${minePick ? ' on' : ''}`} disabled={!onReact}
                  title={[...who].map(familyName).join(', ')} onClick={() => onReact?.(emoji, minePick)}>
                  {emoji}{who.size > 1 ? ` ${who.size}` : ''}
                </button>
              );
            })}
          </div>
        )}
        {footer}
      </div>
      {(onReact || onReply) && (
        <div className="fam-tools">
          {onReact && <button type="button" className="link" title="React" onClick={() => setPicker((x) => !x)}>☺</button>}
          {onReply && <button type="button" className="link" title="Reply" onClick={onReply}>↩</button>}
          {picker && onReact && (
            <div className="fam-pop quick" onMouseLeave={() => setPicker(false)}>
              {QUICK_REACTIONS.map((e) => (
                <button key={e} type="button" onClick={() => { setPicker(false); onReact(e, !!reactions?.get(e)?.has(me)); }}>{e}</button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function Lightbox({ url, onClose }: { url: string | null; onClose: () => void }) {
  useEffect(() => {
    if (!url) return;
    const k = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, [url, onClose]);
  if (!url) return null;
  return (
    <div className="fam-lightbox" onClick={onClose} role="dialog">
      <img src={url} alt="" />
    </div>
  );
}

// Phone photos are made smaller before upload (long side 2048 px, JPEG 85 %); other files go as they are.
async function shrink(file: File): Promise<File> {
  if (!/^image\/(jpeg|png|webp)$/.test(file.type) || file.size < 1_500_000) return file;
  try {
    const bmp = await createImageBitmap(file);
    const k = Math.min(1, 2048 / Math.max(bmp.width, bmp.height));
    const c = document.createElement('canvas');
    c.width = Math.round(bmp.width * k);
    c.height = Math.round(bmp.height * k);
    c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height);
    const blob = await new Promise<Blob | null>((r) => c.toBlob(r, 'image/jpeg', 0.85));
    if (!blob || blob.size >= file.size) return file;
    return new File([blob], file.name.replace(/\.(png|webp|jpe?g)$/i, '') + '.jpg', { type: 'image/jpeg' });
  } catch {
    return file;
  }
}

export function FamilyComposer({
  draftKey, disabled, reply, onCancelReply, onSend, initialText,
}: {
  draftKey: string;
  disabled: boolean;
  reply?: ChatEvent | null;
  onCancelReply: () => void;
  onSend: (text: string, files: File[], progress: (s: string) => void) => Promise<void>;
  initialText?: { text: string; files?: File[]; nonce: number } | null;
}) {
  const storeKey = 'co.familyDraft.' + draftKey;
  const [text, setText] = useState(() => localStorage.getItem(storeKey) || '');
  const [files, setFiles] = useState<File[]>([]);
  const [previews, setPreviews] = useState<string[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [emoji, setEmoji] = useState(false);
  const ta = useRef<HTMLTextAreaElement>(null);
  const fileIn = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (text) localStorage.setItem(storeKey, text);
    else localStorage.removeItem(storeKey);
  }, [text, storeKey]);
  useEffect(() => {
    if (!initialText || handledPrefill.has(initialText.nonce)) return;
    handledPrefill.add(initialText.nonce);
    setText((t) => (t ? t + '\n' : '') + initialText.text);
    if (initialText.files?.length) void add(initialText.files);
  }, [initialText?.nonce]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const urls = files.map((f) => (f.type.startsWith('image/') ? URL.createObjectURL(f) : ''));
    setPreviews(urls);
    return () => urls.forEach((u) => u && URL.revokeObjectURL(u));
  }, [files]);
  useEffect(() => {
    const el = ta.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }, [text]);
  useEffect(() => { if (reply) ta.current?.focus(); }, [reply]);

  async function add(list: File[] | FileList) {
    const next: File[] = [];
    for (const f of Array.from(list)) {
      const s = await shrink(f);
      if (s.size > MAX_ATTACHMENT_BYTES) { setErr(`${f.name} is larger than ${formatBytes(MAX_ATTACHMENT_BYTES)}`); continue; }
      next.push(s);
    }
    setFiles((prev) => [...prev, ...next].slice(0, 10));
  }

  async function send() {
    const t = text.trim();
    if ((!t && !files.length) || busy || disabled) return;
    setErr(null);
    setBusy('Sending…');
    try {
      await onSend(t, files, (s) => setBusy(s));
      localStorage.removeItem(storeKey); // now: a new chat remounts this composer before the effect would run
      setText('');
      setFiles([]);
      setEmoji(false);
    } catch (e) {
      setErr(categorize(e).message);
    } finally {
      setBusy(null);
    }
  }

  function onKey(e: KeyboardEvent<HTMLTextAreaElement>) {
    const touch = window.matchMedia('(pointer: coarse)').matches;
    if (e.key === 'Enter' && !e.shiftKey && !touch && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void send();
    }
  }
  function onPaste(e: ClipboardEvent<HTMLTextAreaElement>) {
    const list = Array.from(e.clipboardData.files || []);
    if (list.length) {
      e.preventDefault();
      void add(list);
    }
  }
  function onDrop(e: DragEvent<HTMLDivElement>) {
    if (!e.dataTransfer.files.length) return;
    e.preventDefault();
    void add(e.dataTransfer.files);
  }
  function insert(s: string) {
    const el = ta.current;
    const at = el ? el.selectionStart : text.length;
    const end = el ? el.selectionEnd : text.length;
    setText(text.slice(0, at) + s + text.slice(end));
    requestAnimationFrame(() => {
      if (!el) return;
      el.focus();
      el.selectionStart = el.selectionEnd = at + s.length;
    });
  }

  return (
    <div className="fam-composer" onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
      {reply && (
        <div className="fam-replybar">
          ↩ <b>{reply.type === 'assistant_message' ? 'AI Workbench' : familyName(reply.author)}</b> {short(reply)}
          <button type="button" className="link" onClick={onCancelReply} title="Cancel the reply">×</button>
        </div>
      )}
      {!!files.length && (
        <div className="fam-pending">
          {files.map((f, i) => (
            <span key={i} className="fam-pend">
              {previews[i] ? <img src={previews[i]} alt="" /> : <span>📎 {f.name}</span>}
              <button type="button" className="link" title="Remove" onClick={() => setFiles(files.filter((_, j) => j !== i))}>×</button>
            </span>
          ))}
        </div>
      )}
      {emoji && (
        <div className="fam-pop emoji">
          {EMOJI.map((e) => <button key={e} type="button" onClick={() => insert(e)}>{e}</button>)}
        </div>
      )}
      <div className="fam-inputrow">
        <button type="button" className="fam-btn" title="Emoji" disabled={disabled} onClick={() => setEmoji((x) => !x)}>😊</button>
        <button type="button" className="fam-btn" title="Photo or file" disabled={disabled || !!busy} onClick={() => fileIn.current?.click()}>📎</button>
        <input ref={fileIn} type="file" multiple hidden onChange={(e) => { if (e.target.files) void add(e.target.files); e.target.value = ''; }} />
        <textarea ref={ta} rows={1} value={text} disabled={disabled || !!busy} placeholder="Message… (@co calls AI Workbench)"
          onChange={(e) => setText(e.target.value)} onKeyDown={onKey} onPaste={onPaste} />
        <button type="button" className="fam-btn send" title="Send" disabled={disabled || !!busy || (!text.trim() && !files.length)} onClick={() => void send()}>➤</button>
      </div>
      {(busy || err) && <div className={`small ${err ? 'error-text' : 'muted'}`}>{err || busy}</div>}
    </div>
  );
}
