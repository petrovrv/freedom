// SPDX-License-Identifier: AGPL-3.0-or-later
// Freedom F10 (ext/1 core services, modules/EXT1.md): files a module page may read and write through CO Web. The page
// never gets the person's Google token: CO Web does the Drive calls itself, only for paths inside projects/ of
// AI-Workbench and only for modules whose manifest declares files.read / files.write (checked in extBridge.reqRefusal).
import { locate } from './chatFiles';
import { copyConverted, createFile, deleteFile, exportPdf, findWithProps, getBytes, replaceContent, setAppProperties } from './drive';
import { filePathOk, siblingOk } from './extBridge';

const bad = (what: string) => new Error(`invalid ${what}`);

async function meta(rootId: string, path: unknown) {
  if (!filePathOk(path)) throw bad('path');
  const m = await locate(rootId, path);
  if (!m) throw new Error('File not found on Drive.');
  return m;
}

/** Answers a file.* request of a module page; the result may hold ArrayBuffers (returned with their transfer list). */
export async function fileService(rootId: string, name: string, args: Record<string, unknown>): Promise<{ data: unknown; transfer: ArrayBuffer[] }> {
  if (name === 'file.get') {
    const m = await meta(rootId, args.path);
    const bytes = await getBytes(m.id);
    return {
      data: { name: m.name, mimeType: m.mimeType, id: m.id, md5: m.md5Checksum || '', modifiedTime: m.modifiedTime || '', size: bytes.byteLength, bytes },
      transfer: [bytes],
    };
  }
  if (name === 'file.pdf') {
    // Google Drive converts it: a temporary Google copy in the person's own My Drive root, exported as PDF, deleted
    const m = await meta(rootId, args.path);
    const target = GOOGLE_TYPES[ext(m.name)];
    if (!target) throw new Error('Google Drive cannot convert this file type to PDF.');
    const tmp = await copyConverted(m.id, 'root', `~ ${m.name} (CO conversion)`, target);
    let bytes: ArrayBuffer;
    let note = '';
    try {
      bytes = await exportPdf(tmp.id);
    } finally {
      await deleteFile(tmp.id).catch(() => { note = `The temporary copy "${tmp.name}" stayed in your My Drive; delete it there.`; });
    }
    return { data: { bytes, note }, transfer: [bytes] };
  }
  if (name === 'file.sibling') {
    const m = await meta(rootId, args.path);
    if (!siblingOk(args.path as string, args.name)) throw bad('name');
    const parent = m.parents?.[0];
    const f = parent ? await findWithProps(parent, args.name) : null;
    if (!f) return { data: null, transfer: [] };
    const bytes = args.withBytes === false ? null : await getBytes(f.id);
    return { data: { id: f.id, name: f.name, appProperties: f.appProperties || {}, bytes }, transfer: bytes ? [bytes] : [] };
  }
  if (name === 'file.putSibling') {
    const m = await meta(rootId, args.path);
    if (!siblingOk(args.path as string, args.name)) throw bad('name');
    const bytes = args.bytes;
    if (!(bytes instanceof ArrayBuffer)) throw bad('bytes');
    const mime = typeof args.mimeType === 'string' && /^[a-z]+\/[A-Za-z0-9.+-]+$/.test(args.mimeType) ? args.mimeType : 'application/octet-stream';
    const props = propsOk(args.appProperties);
    const parent = m.parents?.[0];
    if (!parent) throw new Error('The file has no folder.');
    const found = await findWithProps(parent, args.name);
    // replaces only a file this module made for this very original (appProperties written by the module)
    if (found && !sameOwner(found.appProperties, props)) throw new Error(`"${args.name}" already exists and was not made for this file.`);
    const blob = new Blob([bytes], { type: mime });
    const id = found ? (await replaceContent(found.id, blob, mime)).id : (await createFile(parent, args.name, blob, mime)).id;
    if (Object.keys(props).length) await setAppProperties(id, props);
    return { data: { id }, transfer: [] };
  }
  throw new Error('unknown_request');
}

// a replaced copy must carry the same first property (the module's link to the original, e.g. {xOf: <id>})
function sameOwner(have: Record<string, string> | undefined, want: Record<string, string>): boolean {
  const k = Object.keys(want)[0];
  return !!k && !!have && have[k] === want[k];
}

function propsOk(v: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (!v || typeof v !== 'object' || Array.isArray(v)) return out;
  for (const [k, x] of Object.entries(v)) {
    if (/^[A-Za-z][A-Za-z0-9_]{0,40}$/.test(k) && typeof x === 'string' && x.length <= 120) out[k] = x;
    if (Object.keys(out).length >= 8) break;
  }
  return out;
}

const ext = (name: string) => /\.([^./]+)$/.exec(name.toLowerCase())?.[1] || '';

// What Google Drive converts to its own editors (and then exports as PDF).
const GOOGLE_TYPES: Record<string, string> = {
  doc: 'application/vnd.google-apps.document', docx: 'application/vnd.google-apps.document', odt: 'application/vnd.google-apps.document',
  rtf: 'application/vnd.google-apps.document', txt: 'application/vnd.google-apps.document', md: 'application/vnd.google-apps.document',
  markdown: 'application/vnd.google-apps.document',
  ppt: 'application/vnd.google-apps.presentation', pptx: 'application/vnd.google-apps.presentation', odp: 'application/vnd.google-apps.presentation',
  xls: 'application/vnd.google-apps.spreadsheet', xlsx: 'application/vnd.google-apps.spreadsheet', ods: 'application/vnd.google-apps.spreadsheet',
};
