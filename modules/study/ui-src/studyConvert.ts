// SPDX-License-Identifier: AGPL-3.0-or-later
// Module study (was co-web/src/studyConvert.ts of CO Web 0.80.0): the viewer's PDF of a Drive file. Everything is shown
// as PDF: a pdf as it is; Office and text files converted by Google Drive (ext/1 file.pdf), images built into a PDF
// here. The result is kept next to the original as "<name>.study.pdf" with the Drive id and md5 of the original in its
// app properties (studyOf, studySource), so it is rebuilt only when the original changes. The core replaces an
// existing sibling only when its first app property (studyOf) matches, so a file of the same name that is not ours is
// never lost. All Drive access goes through the core (coExt.req), the page has no network of its own.
import { fileGet, filePdf, filePutSibling, fileSibling, type Sibling } from './coExt';
import { jpegPdf, studyKind, studyPdfName } from './studyDoc';

export interface StudyPdf {
  bytes: Uint8Array;
  name: string; // the original's name
  fileId: string;
  note?: string; // e.g. the copy could not be saved
}

const MAX_SIDE = 3000; // px of an image page

async function imagePdf(bytes: ArrayBuffer, mime: string): Promise<Uint8Array> {
  const bmp = await createImageBitmap(new Blob([bytes], { type: mime || 'image/*' }));
  const k = Math.min(1, MAX_SIDE / Math.max(bmp.width, bmp.height));
  const w = Math.max(1, Math.round(bmp.width * k));
  const h = Math.max(1, Math.round(bmp.height * k));
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const ctx = c.getContext('2d')!;
  ctx.fillStyle = '#fff'; // transparent PNG -> white page
  ctx.fillRect(0, 0, w, h);
  ctx.drawImage(bmp, 0, 0, w, h);
  const jpg = await new Promise<Blob>((ok, no) => c.toBlob((b) => (b ? ok(b) : no(new Error('Could not encode the image.'))), 'image/jpeg', 0.9));
  return jpegPdf([{ bytes: new Uint8Array(await jpg.arrayBuffer()), width: w, height: h }]);
}

export async function loadStudyPdf(path: string, status: (s: string) => void): Promise<StudyPdf> {
  const base = path.split('/').pop() || '';
  if (!studyKind(base)) throw new Error('This file type does not open in the study viewer.');
  status('Finding the file…');
  // file.get is the only way to learn the Drive id and md5 of the original (the cache key); it brings the bytes too
  const meta = await fileGet(path).catch((e: Error) => {
    throw new Error(/not.?found|404/i.test(e.message) ? 'File not found on Drive.' : e.message);
  });
  if (!meta) throw new Error('File not found on Drive.');
  const kind = studyKind(meta.name || base);
  if (!kind) throw new Error('This file type does not open in the study viewer.');
  if (kind === 'pdf') return { bytes: new Uint8Array(meta.bytes), name: meta.name, fileId: meta.id };

  const copyName = studyPdfName(meta.name);
  const md5 = meta.md5 || meta.modifiedTime || '';
  const found: Sibling | null = await fileSibling(path, copyName, false).catch(() => null);
  // only a copy this viewer made for this very file (studyOf = its Drive id) is reused or replaced (review 03.11)
  const copy = found && found.appProperties?.studyOf === meta.id ? found : null;
  if (copy && md5 && copy.appProperties?.studySource === md5) {
    status('Loading…');
    const cached = await fileSibling(path, copyName, true).catch(() => null);
    // the copy may have changed in between: use it only when it is still this file's current copy
    if (cached?.bytes && cached.appProperties?.studyOf === meta.id && cached.appProperties?.studySource === md5) {
      return { bytes: new Uint8Array(cached.bytes), name: meta.name, fileId: meta.id };
    }
  }
  let pdf: Uint8Array;
  const notes: string[] = [];
  if (kind === 'image') {
    status('Building a PDF from the image…');
    pdf = await imagePdf(meta.bytes, meta.mimeType);
  } else {
    status('Converting to PDF (Google Drive)…');
    const conv = await filePdf(path);
    pdf = new Uint8Array(conv.bytes);
    if (conv.note) notes.push(conv.note); // e.g. a temporary Google copy stayed in My Drive
  }
  const done = (n?: string) => {
    if (n) notes.push(n);
    return { bytes: pdf, name: meta.name, fileId: meta.id, note: notes.join(' ') || undefined };
  };
  if (found && !copy) return done(`The PDF copy was not saved: "${copyName}" already exists and is not the viewer's copy.`);
  try {
    status('Saving the PDF copy…');
    await filePutSibling(path, copyName, pdf.slice().buffer, 'application/pdf', { studyOf: meta.id, studySource: md5 } /* studyOf first: the core matches the first key */);
    return done();
  } catch (e) {
    return done(`The PDF copy was not saved: ${(e as Error).message}`);
  }
}
