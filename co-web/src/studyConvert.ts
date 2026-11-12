// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Web 0.80.0: the study viewer's PDF of a Drive file (SPEC.md of chat CHAT-20261103081329-PX01, V1). Everything is
// shown as PDF: a pdf as it is; Office and text files converted by Google Drive itself (a temporary Google copy exported
// as PDF), images built into a PDF here. The result is kept next to the original as "<name>.study.pdf" with the md5 of
// the original in its app properties, so it is rebuilt only when the original changes. Nothing runs on the home PC,
// so the viewer works from any browser (a member's MacBook) even when that PC is off.
import { copyConverted, createFile, deleteFile, exportPdf, findWithProps, getBytes, replaceContent, setAppProperties } from './drive';
import { locate } from './chatFiles';
import { driveTarget, jpegPdf, studyKind, studyPdfName } from './studyDoc';

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

export async function loadStudyPdf(rootId: string, path: string, status: (s: string) => void): Promise<StudyPdf> {
  status('Finding the file…');
  const meta = await locate(rootId, path);
  if (!meta) throw new Error('File not found on Drive.');
  const kind = studyKind(meta.name);
  if (!kind) throw new Error('This file type does not open in the study viewer.');
  if (kind === 'pdf') {
    status('Loading…');
    return { bytes: new Uint8Array(await getBytes(meta.id)), name: meta.name, fileId: meta.id };
  }
  const parent = meta.parents?.[0];
  const copyName = studyPdfName(meta.name);
  const md5 = meta.md5Checksum || meta.modifiedTime || '';
  const found = parent ? await findWithProps(parent, copyName).catch(() => null) : null;
  // only a copy this viewer made for this very file (studyOf = its Drive id) is reused or replaced (review 03.11)
  const copy = found && found.appProperties?.studyOf === meta.id ? found : null;
  if (copy && md5 && copy.appProperties?.studySource === md5) {
    status('Loading…');
    return { bytes: new Uint8Array(await getBytes(copy.id)), name: meta.name, fileId: meta.id };
  }
  let pdf: Uint8Array;
  const notes: string[] = [];
  if (kind === 'image') {
    status('Building a PDF from the image…');
    pdf = await imagePdf(await getBytes(meta.id), meta.mimeType);
  } else {
    status('Converting to PDF (Google Drive)…');
    const target = driveTarget(meta.name)!;
    // the temporary Google copy goes to the user's own My Drive root, not into the chat folder
    const tmp = await copyConverted(meta.id, 'root', `~ ${meta.name} (CO study conversion)`, target);
    try {
      pdf = new Uint8Array(await exportPdf(tmp.id));
    } finally {
      await deleteFile(tmp.id).catch(() => notes.push(`The temporary copy "${tmp.name}" stayed in your My Drive; delete it there.`));
    }
  }
  const done = (n?: string) => {
    if (n) notes.push(n);
    return { bytes: pdf, name: meta.name, fileId: meta.id, note: notes.join(' ') || undefined };
  };
  if (!parent) return done('The PDF copy was not saved (no folder).');
  if (found && !copy) return done(`The PDF copy was not saved: "${copyName}" already exists and is not the viewer's copy.`);
  try {
    status('Saving the PDF copy…');
    const blob = new Blob([pdf as BlobPart], { type: 'application/pdf' });
    const id = copy ? (await replaceContent(copy.id, blob, 'application/pdf')).id : (await createFile(parent, copyName, blob, 'application/pdf')).id;
    await setAppProperties(id, { studyOf: meta.id, studySource: md5 });
    return done();
  } catch (e) {
    return done(`The PDF copy was not saved: ${(e as Error).message}`);
  }
}
