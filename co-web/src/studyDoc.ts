// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.80.0: study viewer, stage V1 (SPEC.md of chat CHAT-20261103081329-PX01, part V). Pure helpers: which files
// open in the viewer and how, the question tag of a message ("[doc: name, p. 7, area x y w h]") that puts a marker on
// the page, and a minimal PDF writer for photos and scans (one JPEG per page).

// How a file becomes the viewer's PDF: as it is, converted by Google Drive (Office, text), or built from an image.
export type StudyKind = 'pdf' | 'drive' | 'image';

const DRIVE_TYPES: Record<string, string> = {
  doc: 'application/vnd.google-apps.document',
  docx: 'application/vnd.google-apps.document',
  odt: 'application/vnd.google-apps.document',
  rtf: 'application/vnd.google-apps.document',
  txt: 'application/vnd.google-apps.document',
  md: 'application/vnd.google-apps.document',
  markdown: 'application/vnd.google-apps.document',
  ppt: 'application/vnd.google-apps.presentation',
  pptx: 'application/vnd.google-apps.presentation',
  odp: 'application/vnd.google-apps.presentation',
  xls: 'application/vnd.google-apps.spreadsheet',
  xlsx: 'application/vnd.google-apps.spreadsheet',
  ods: 'application/vnd.google-apps.spreadsheet',
};

const IMAGES = ['jpg', 'jpeg', 'png', 'webp', 'gif', 'bmp'];

function ext(name: string): string {
  const m = /\.([^./]+)$/.exec(name.toLowerCase());
  return m ? m[1] : '';
}

export function studyKind(name: string): StudyKind | null {
  const e = ext(name);
  if (/\.study\.pdf$/i.test(name)) return null; // the viewer's own copy: open its original instead
  if (e === 'pdf') return 'pdf';
  if (DRIVE_TYPES[e]) return 'drive';
  if (IMAGES.includes(e)) return 'image';
  return null;
}

// Google editor type a Drive copy converts the file to (then exported as PDF).
export function driveTarget(name: string): string | null {
  return DRIVE_TYPES[ext(name)] || null;
}

// The viewer's PDF copy lives next to the original: "Lecture 3.pptx" -> "Lecture 3.pptx.study.pdf" (the extension
// stays, so Lecture.docx and Lecture.pptx get separate copies).
export function studyPdfName(name: string): string {
  return name + '.study.pdf';
}

// ---------- question tags ----------

export type AskShape = 'area' | 'text' | 'page';

export interface DocTag {
  doc: string;
  page: number; // 1-based
  shape: AskShape;
  // area on the page as fractions of its width/height (0..1), absent for a whole page
  rect?: { x: number; y: number; w: number; h: number };
  wtf: boolean;
}

const f3 = (n: number) => (Math.round(Math.min(1, Math.max(0, n)) * 1000) / 1000).toFixed(3);

export function docTag(t: DocTag): string {
  const r = t.rect ? ` ${f3(t.rect.x)} ${f3(t.rect.y)} ${f3(t.rect.w)} ${f3(t.rect.h)}` : '';
  return `[doc: ${t.doc.replace(/[\],]/g, ' ')}, p. ${t.page}, ${t.shape}${r}]${t.wtf ? ' WTF' : ''}`;
}

const TAG_RE = /^\s*\[doc: ([^\],]+), p\. (\d+), (area|text|page)((?: [01]\.\d{3}){4})?\]( WTF)?/;

export function parseDocTag(text: string | undefined): DocTag | null {
  const m = TAG_RE.exec(text || '');
  if (!m) return null;
  const nums = m[4] ? m[4].trim().split(' ').map(Number) : null;
  return {
    doc: m[1].trim(),
    page: Number(m[2]),
    shape: m[3] as AskShape,
    rect: nums ? { x: nums[0], y: nums[1], w: nums[2], h: nums[3] } : undefined,
    wtf: !!m[5],
  };
}

export const WTF_TEXT =
  'I do not understand this at all. Explain from scratch what is written here: every symbol and notation, what each line means, and why it is there (what it is for in the course). Start from what I would need to know first.';

// The message the viewer sends: the tag (marker on the page), the question, where the file is and what is attached.
export function askMessage(o: {
  tag: DocTag;
  question: string;
  path: string; // relative to AI-Workbench
  selected?: string; // the selected text, if any
  pagesNote: string; // e.g. "pages 6-8"
  hasImage: boolean;
}): string {
  const lines = [docTag(o.tag), '', o.tag.wtf ? WTF_TEXT : o.question.trim()];
  if (o.selected?.trim()) lines.push('', `Selected text (page ${o.tag.page}): «${o.selected.trim().slice(0, 1500)}»`);
  lines.push(
    '',
    `[Study viewer: the question is about page ${o.tag.page} of AI-Workbench/${o.path}.` +
      (o.hasImage ? ' The attached PNG is the selected part of the page (read formulas and figures from it);' : '') +
      ` the attached text file is the text of ${o.pagesNote} for context. Answer as a tutor, in the language of the document or of my question.]`,
  );
  return lines.join('\n');
}

// What a person sees of a viewer question in the chat: a short label instead of the tag, and the text without the
// technical note for the agent (the agent gets the full text).
export function askDisplay(text: string | undefined): { label: string; text: string } | null {
  const tag = parseDocTag(text);
  if (!tag) return null;
  const body = (text || '')
    .replace(TAG_RE, '')
    .replace(/\n*\[Study viewer: [\s\S]*\]\s*$/, '')
    .replace(WTF_TEXT, 'WTF?! Explain this from scratch.')
    .trim();
  return { label: `📖 ${tag.doc} · p. ${tag.page}${tag.shape === 'page' ? '' : ` · ${tag.shape}`}`, text: body };
}

// ---------- minimal PDF from JPEG images (one image per page, page size in points = image size at 96 dpi) ----------

export interface JpegPage {
  bytes: Uint8Array;
  width: number; // pixels
  height: number;
}

export function jpegPdf(pages: JpegPage[]): Uint8Array {
  const enc = new TextEncoder();
  const chunks: Uint8Array[] = [];
  const offsets: number[] = [];
  let size = 0;
  const push = (b: Uint8Array | string) => {
    const u = typeof b === 'string' ? enc.encode(b) : b;
    chunks.push(u);
    size += u.length;
  };
  const n = pages.length;
  // objects: 1 catalog, 2 pages, then per page: page, content, image
  const total = 2 + n * 3;
  const obj = (id: number, body: string | Uint8Array[]) => {
    offsets[id] = size;
    push(`${id} 0 obj\n`);
    if (typeof body === 'string') push(body);
    else body.forEach(push);
    push('\nendobj\n');
  };
  push('%PDF-1.4\n%\xE2\xE3\xCF\xD3\n');
  obj(1, '<< /Type /Catalog /Pages 2 0 R >>');
  const kids = pages.map((_, i) => `${3 + i * 3} 0 R`).join(' ');
  obj(2, `<< /Type /Pages /Kids [${kids}] /Count ${n} >>`);
  pages.forEach((p, i) => {
    const pid = 3 + i * 3;
    const w = Math.round(p.width * 0.75);
    const h = Math.round(p.height * 0.75);
    obj(pid, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${w} ${h}] /Resources << /XObject << /Im0 ${pid + 2} 0 R >> >> /Contents ${pid + 1} 0 R >>`);
    const content = `q ${w} 0 0 ${h} 0 0 cm /Im0 Do Q`;
    obj(pid + 1, `<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
    obj(pid + 2, [
      enc.encode(`<< /Type /XObject /Subtype /Image /Width ${p.width} /Height ${p.height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${p.bytes.length} >>\nstream\n`),
      p.bytes,
      enc.encode('\nendstream'),
    ]);
  });
  const xref = size;
  let x = `xref\n0 ${total + 1}\n0000000000 65535 f \n`;
  for (let id = 1; id <= total; id++) x += `${String(offsets[id]).padStart(10, '0')} 00000 n \n`;
  push(x);
  push(`trailer\n<< /Size ${total + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) {
    out.set(c, at);
    at += c.length;
  }
  return out;
}
