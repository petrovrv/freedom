// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.97.0: a small slippy map for the "place on the map" privacy mode of the presence chip. Tiles are OpenStreetMap
// through agent/geotile.php (CSP allows images only from 'self'); drag to move, +/- to zoom, a click picks the point.
import { useRef, useState } from 'react';

const T = 256;
const W = 276;
const H = 200;

function toPx(lat: number, lon: number, z: number): [number, number] {
  const n = T * 2 ** z;
  const s = Math.sin((lat * Math.PI) / 180);
  return [((lon + 180) / 360) * n, (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * n];
}

function toLatLon(x: number, y: number, z: number): [number, number] {
  const n = T * 2 ** z;
  const lon = (x / n) * 360 - 180;
  const lat = (Math.atan(Math.sinh(Math.PI * (1 - (2 * y) / n))) * 180) / Math.PI;
  return [lat, lon];
}

export default function GeoMap({ center, pick, onPick, token }: {
  center: [number, number];
  token?: string;
  pick?: [number, number];
  onPick: (lat: number, lon: number) => void;
}) {
  const [z, setZ] = useState(12);
  const [c, setC] = useState<[number, number]>(pick ?? center);
  const drag = useRef<{ x: number; y: number; cx: number; cy: number; moved: boolean } | null>(null);
  const box = useRef<HTMLDivElement>(null);

  const [cx, cy] = toPx(c[0], c[1], z);
  const left = cx - W / 2;
  const top = cy - H / 2;
  const tiles: { k: string; x: number; y: number; tx: number; ty: number }[] = [];
  const max = 2 ** z;
  for (let ty = Math.floor(top / T); ty <= Math.floor((top + H) / T); ty++) {
    for (let tx = Math.floor(left / T); tx <= Math.floor((left + W) / T); tx++) {
      if (ty < 0 || ty >= max) continue;
      const wx = ((tx % max) + max) % max;
      tiles.push({ k: `${z}-${tx}-${ty}`, x: tx * T - left, y: ty * T - top, tx: wx, ty });
    }
  }
  const zoom = (d: number) => setZ((v) => Math.min(17, Math.max(3, v + d)));
  const pin = pick ? toPx(pick[0], pick[1], z) : null;

  return (
    <div
      ref={box}
      className="geo-map"
      style={{ width: W, height: H }}
      onPointerDown={(e) => {
        drag.current = { x: e.clientX, y: e.clientY, cx, cy, moved: false };
        e.currentTarget.setPointerCapture?.(e.pointerId);
      }}
      onPointerMove={(e) => {
        const d = drag.current;
        if (!d) return;
        const dx = e.clientX - d.x;
        const dy = e.clientY - d.y;
        if (Math.abs(dx) + Math.abs(dy) > 4) d.moved = true;
        if (d.moved) setC(toLatLon(d.cx - dx, d.cy - dy, z));
      }}
      onPointerUp={(e) => {
        const d = drag.current;
        drag.current = null;
        if (!d || d.moved || !box.current) return;
        const r = box.current.getBoundingClientRect();
        const [lat, lon] = toLatLon(left + e.clientX - r.left, top + e.clientY - r.top, z);
        onPick(Math.round(lat * 1e5) / 1e5, Math.round((((lon + 540) % 360) - 180) * 1e5) / 1e5);
      }}
    >
      {tiles.map((t) => (
        <img key={t.k} src={`/agent/geotile.php?z=${z}&x=${t.tx}&y=${t.ty}${token ? `&t=${token}` : ''}`} alt="" draggable={false} style={{ left: t.x, top: t.y }} />
      ))}
      {pin && <span className="geo-map-pin" style={{ left: pin[0] - left, top: pin[1] - top }}>📍</span>}
      <div className="geo-map-zoom" onPointerDown={(e) => e.stopPropagation()}>
        <button type="button" onClick={() => zoom(1)}>+</button>
        <button type="button" onClick={() => zoom(-1)}>−</button>
      </div>
      <span className="geo-map-attr">© OpenStreetMap</span>
    </div>
  );
}
