// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.91.0: family presence in the header (geo-lib.php; geo-presence on the home PC reads OwnTracks). One chip per member:
// red ↑ - left home recently and still on the way, green ↓ - heading home, 🏠 - at home, 🐕 - a known routine such as
// a dog walk (geo-presence activities: no arrows and no heading-home push for it), otherwise the place name
// (a remembered place, a small zone or the district/town). A "?" on a chip is a frequent place the agent suggests to
// remember; the member (and the owner) can name it or decline in the chip's popover. Nothing here shows coordinates.
// 0.93.0: a place with an icon in the geo-presence config (zones/places "icon", e.g. 🎓 for Sterre) shows the
// icon instead of its name; the name stays in the tooltip and the popover.
// 0.97.0: the icon of a known place shows in every state (a stale chip keeps 🏠 dimmed instead of the word "Дом"); a known
// place without an icon asks for one ("?", kind icon). Privacy: the own chip has a 🔒/👁 pill with the modes where I am /
// only heading home / a saved place / a place on the map / off, plus manual "I'm home" and "Heading home". The others
// see a normal-looking chip; the home PC filters before the server gets anything.
import { useCallback, useEffect, useState } from 'react';
import { dbGeoAnswer, dbGeoPrivacy, dbGeoState, type GeoMember, type GeoMode, type GeoPrivacy, type GeoState } from './dbapi';
import { useInterval } from './hooks';
import GeoMap from './GeoMap';

const POLL_MS = 60000;
const PC_STALE_S = 20 * 60;
const DEFAULT_CENTER: [number, number] = [50.85, 4.35];

const MODES: { id: GeoMode; label: string; hint: string }[] = [
  { id: 'full', label: 'Where I am', hint: 'place, arrows and icons as they are' },
  { id: 'home', label: 'Only heading home', hint: 'others see 🏠, ↓ heading home, otherwise "out"' },
  { id: 'saved', label: 'A saved place', hint: 'others see the place you choose while you are out' },
  { id: 'map', label: 'A place on the map', hint: 'others see the area of the point you pick' },
  { id: 'off', label: 'Off', hint: 'only "at home" and "heading home", automatic or by the buttons below' },
];

const UNTIL: { id: string; label: string; at: () => number | undefined }[] = [
  { id: 'ever', label: 'until I change it', at: () => undefined },
  { id: '4h', label: 'for 4 hours', at: () => Math.floor(Date.now() / 1000) + 4 * 3600 },
  { id: 'day', label: 'until tonight', at: () => { const d = new Date(); d.setHours(23, 59, 0, 0); return Math.floor(d.getTime() / 1000); } },
];

function hm(ts?: number): string {
  if (!ts) return '';
  const d = new Date(ts * 1000);
  const t = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return new Date().toDateString() === d.toDateString() ? t : `${d.toLocaleDateString([], { day: '2-digit', month: '2-digit' })} ${t}`;
}

function km(m: GeoMember): string {
  return m.km === undefined ? '' : `${m.km.toFixed(m.km < 10 ? 1 : 0)} km from home`;
}

function describe(m: GeoMember): string {
  const place = m.place || 'unknown place';
  switch (m.state) {
    case 'home': return `At home${m.since ? ` since ${hm(m.since)}` : ''}`;
    case 'left': return `Left home at ${hm(m.since)}, on the way · now near ${place}`;
    case 'coming': return `Heading home${m.etaMin ? `, ~${m.etaMin} min` : ''}${m.place ? ` · now near ${m.place}` : ''}`;
    case 'walk': return `${m.activity || 'Walk'} since ${hm(m.since)} · now near ${place}`;
    case 'there': return `At ${place}${m.since ? ` since ${hm(m.since)}` : ''}`;
    case 'moving': return `On the move near ${place}`;
    case 'stale': return `Last seen ${m.place ? `at ${m.place} ` : ''}at ${hm(m.updated)}`;
    case 'away': return 'Out';
    default: return 'No location yet (OwnTracks is not connected on the phone)';
  }
}

function Arrow({ state }: { state: GeoMember['state'] }) {
  if (state === 'left') return <span className="geo-arrow up" aria-label="left home">↑</span>;
  if (state === 'coming') return <span className="geo-arrow down" aria-label="heading home">↓</span>;
  if (state === 'home') return <span className="geo-arrow" aria-label="at home">🏠</span>;
  if (state === 'walk') return <span className="geo-arrow" aria-label="walk">🐕</span>;
  return null;
}

function Privacy({ st, onDone }: { st: GeoState; onDone: () => Promise<void> }) {
  const pv: GeoPrivacy = st.privacy;
  const [mode, setMode] = useState<GeoMode>(pv.mode);
  const [place, setPlace] = useState(pv.place || st.places[0]?.name || '');
  const [pt, setPt] = useState<[number, number] | undefined>(pv.lat !== undefined && pv.lon !== undefined ? [pv.lat, pv.lon] : undefined);
  const [until, setUntil] = useState('ever');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  const run = async (p: Parameters<typeof dbGeoPrivacy>[0]) => {
    setBusy(true);
    setErr('');
    try {
      await dbGeoPrivacy(p);
      await onDone();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const ready = (mode !== 'saved' || !!place) && (mode !== 'map' || !!pt);
  const apply = () => void run({
    mode, until: UNTIL.find((u) => u.id === until)?.at(),
    ...(mode === 'saved' ? { place } : {}), ...(mode === 'map' && pt ? { lat: pt[0], lon: pt[1] } : {}),
  });

  return (
    <div className="geo-privacy">
      <div><b>Privacy</b> <span className="small muted">— only you see and change this</span></div>
      {MODES.map((x) => (
        <label key={x.id} className="geo-privacy-mode" title={x.hint}>
          <input type="radio" name="geo-mode" checked={mode === x.id} onChange={() => setMode(x.id)} />
          <span>{x.label}<span className="small muted"> · {x.hint}</span></span>
        </label>
      ))}
      {mode === 'saved' && (
        st.places.length
          ? <select value={place} onChange={(e) => setPlace(e.target.value)}>
            {st.places.map((p) => <option key={p.name} value={p.name}>{p.icon ? `${p.icon} ` : ''}{p.name}</option>)}
          </select>
          : <div className="small muted">No saved places yet.</div>
      )}
      {mode === 'map' && (
        <>
          <GeoMap center={st.center ?? DEFAULT_CENTER} token={st.tileToken} pick={pt} onPick={(la, lo) => setPt([la, lo])} />
          <div className="small muted">{pt ? 'Point picked; others see the name of its area.' : 'Click the map to pick a point.'}</div>
        </>
      )}
      {mode !== 'full' && (
        <select value={until} onChange={(e) => setUntil(e.target.value)} aria-label="how long">
          {UNTIL.map((u) => <option key={u.id} value={u.id}>{u.label}</option>)}
        </select>
      )}
      <div className="geo-sugg-btns">
        <button className="btn small" type="button" disabled={busy || !ready} onClick={apply}>Apply</button>
      </div>
      {pv.mode !== 'full' && (
        <div className="geo-sugg-btns">
          <button className="btn small ghost" type="button" disabled={busy} onClick={() => void run({ manual: 'home' })}>🏠 I'm home</button>
          <button className="btn small ghost" type="button" disabled={busy} onClick={() => void run({ manual: 'coming' })}>↓ Heading home</button>
          {pv.manual && <button className="btn small ghost" type="button" disabled={busy} onClick={() => void run({ manual: '' })}>Auto</button>}
        </div>
      )}
      {pv.until ? <div className="small muted">Mode ends {hm(pv.until)}</div> : null}
      {err && <div className="small" style={{ color: '#d93838' }}>{err}</div>}
    </div>
  );
}

export default function GeoStatus({ authOk, me }: { authOk: boolean; me?: string }) {
  const [st, setSt] = useState<GeoState | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [names, setNames] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const poll = useCallback(async () => {
    if (!authOk) return;
    try { setSt(await dbGeoState()); } catch { /* the header stays as it was */ }
  }, [authOk]);
  useEffect(() => { void poll(); }, [poll]);
  useInterval(() => void poll(), POLL_MS, authOk);

  if (!st || !st.members.length) return null;
  const pcStale = st.now - st.updated > PC_STALE_S;
  const hidden = st.privacy.mode !== 'full';

  const answer = async (id: string, name: string) => {
    setBusy(true);
    try {
      await dbGeoAnswer(id, name);
      setNames((n) => ({ ...n, [id]: '' }));
      await poll();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={`geo-bar${pcStale ? ' pc-stale' : ''}`} title={pcStale ? `Home PC has not updated locations since ${hm(st.updated)}` : undefined}>
      {st.members.map((m) => {
        const sugg = st.suggestions.filter((s) => s.user === m.user);
        const mine = m.user === me;
        const label = m.state === 'home' ? '' : m.icon || (m.state === 'away' ? 'out' : m.place);
        return (
          <div key={m.user} className="geo-wrap">
            <button
              type="button"
              className={`geo-chip ${m.state}`}
              title={`${m.name}: ${describe(m)}`}
              aria-expanded={open === m.user}
              onClick={() => setOpen((o) => (o === m.user ? null : m.user))}
            >
              <span className="geo-name">{m.name}</span>
              <span className="geo-initial">{m.name.slice(0, 1)}</span>
              <Arrow state={m.state} />
              {label && <span className={m.icon && m.state !== 'home' ? 'geo-arrow' : 'geo-place'} aria-label={m.icon ? m.place : undefined}>{label}</span>}
              {mine && hidden && <span className="geo-lock" aria-label="privacy on">🔒</span>}
              {sugg.length > 0 && <span className="geo-ask" aria-label="question">?</span>}
            </button>
            {open === m.user && (
              <>
                <div className="app-menu-scrim" onClick={() => setOpen(null)} />
                <div className="geo-pop" role="dialog" aria-label={m.name}>
                  <div><b>{m.name}</b></div>
                  <div>{describe(m)}</div>
                  <div className="small muted">
                    {[km(m), m.batt !== undefined ? `battery ${m.batt}%` : '', m.updated ? `updated ${hm(m.updated)}` : ''].filter(Boolean).join(' · ')}
                  </div>
                  {mine && hidden && <div className="small muted">This is what the others see now.</div>}
                  {sugg.map((s) => {
                    const icon = s.kind === 'icon';
                    return (
                      <form
                        key={s.id}
                        className="geo-sugg"
                        onSubmit={(e) => { e.preventDefault(); const n = (names[s.id] ?? '').trim(); if (n) void answer(s.id, n); }}
                      >
                        {icon
                          ? <div>{s.place || 'This place'} has no icon yet. Pick an emoji for it</div>
                          : <div>{m.name} often stays here{s.area ? ` (${s.area})` : ''}{s.reason ? `: ${s.reason}` : ''}. Remember the place as</div>}
                        <input
                          value={names[s.id] ?? ''}
                          placeholder={icon ? 'e.g. 🏋️ 📚 🛒' : "e.g. Gym, Hana's, Library"}
                          maxLength={icon ? 8 : 60}
                          onChange={(e) => setNames((n) => ({ ...n, [s.id]: e.target.value }))}
                        />
                        <div className="geo-sugg-btns">
                          <button className="btn small" type="submit" disabled={busy || !(names[s.id] ?? '').trim()}>{icon ? 'Set icon' : 'Remember'}</button>
                          <button className="btn small ghost" type="button" disabled={busy} onClick={() => void answer(s.id, '')}>Don't ask</button>
                        </div>
                      </form>
                    );
                  })}
                  {mine && <Privacy st={st} onDone={poll} />}
                </div>
              </>
            )}
          </div>
        );
      })}
    </div>
  );
}
