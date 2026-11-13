// SPDX-License-Identifier: AGPL-3.0-or-later
// Test documents made on the fly (no binaries in the repo): a text PDF with an outline and a PNG.
import { crc32, deflateSync } from 'node:zlib';

// A PDF with one Helvetica text block per page and an outline entry per page.
export function textPdf(pages) {
  const parts = [];
  const offsets = [];
  let size = 0;
  const push = (s) => {
    const b = Buffer.from(s, 'latin1');
    parts.push(b);
    size += b.length;
  };
  const n = pages.length;
  // 1 catalog, 2 pages, 3 font, 4 outlines, then per page: page, content, outline item
  const total = 4 + n * 3;
  const obj = (id, body) => {
    offsets[id] = size;
    push(`${id} 0 obj\n${body}\nendobj\n`);
  };
  const pid = (i) => 5 + i * 3;
  push('%PDF-1.4\n%\xE2\xE3\xCF\xD3\n');
  obj(1, '<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /PageMode /UseOutlines >>');
  obj(2, `<< /Type /Pages /Kids [${pages.map((_, i) => `${pid(i)} 0 R`).join(' ')}] /Count ${n} >>`);
  obj(3, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
  obj(4, `<< /Type /Outlines /First ${pid(0) + 2} 0 R /Last ${pid(n - 1) + 2} 0 R /Count ${n} >>`);
  pages.forEach((lines, i) => {
    const p = pid(i);
    obj(p, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents ${p + 1} 0 R >>`);
    let s = 'BT /F1 22 Tf 60 770 Td 28 TL\n';
    for (const l of lines) s += `(${l.replace(/[()\\]/g, '\\$&')}) Tj T*\n`;
    s += 'ET\n0 0 0 RG 4 w 60 300 m 535 300 l S\n0.2 0.4 0.8 rg 60 120 200 120 re f\n';
    obj(p + 1, `<< /Length ${Buffer.byteLength(s, 'latin1')} >>\nstream\n${s}endstream`);
    const prev = i > 0 ? ` /Prev ${pid(i - 1) + 2} 0 R` : '';
    const next = i < n - 1 ? ` /Next ${pid(i + 1) + 2} 0 R` : '';
    obj(p + 2, `<< /Title (Chapter ${i + 1}) /Parent 4 0 R /Dest [${p} 0 R /Fit]${prev}${next} >>`);
  });
  const xref = size;
  let x = `xref\n0 ${total + 1}\n0000000000 65535 f \n`;
  for (let id = 1; id <= total; id++) x += `${String(offsets[id]).padStart(10, '0')} 00000 n \n`;
  push(x);
  push(`trailer\n<< /Size ${total + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  return Buffer.concat(parts);
}

// An RGB PNG: white with a dark square in the middle.
export function png(w, h) {
  const raw = Buffer.alloc((w * 3 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 3 + 1)] = 0;
    for (let x = 0; x < w; x++) {
      const dark = x > w / 4 && x < (3 * w) / 4 && y > h / 4 && y < (3 * h) / 4;
      const o = y * (w * 3 + 1) + 1 + x * 3;
      raw[o] = dark ? 20 : 255;
      raw[o + 1] = dark ? 40 : 255;
      raw[o + 2] = dark ? 90 : 255;
    }
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(td) >>> 0);
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;
  ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
