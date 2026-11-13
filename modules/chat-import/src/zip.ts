// SPDX-License-Identifier: AGPL-3.0-or-later
// Reads an export (zip or single JSON files) inside the module page (was co-web/src/importers/worker.ts). The page runs
// in a sandboxed frame with an opaque origin and script-src 'self', so there is no worker: the zip is streamed in chunks
// and only *.json entries are inflated; images and other binaries are skipped while streaming, so multi-GB ChatGPT
// exports do not have to fit in memory.
import { Unzip, UnzipInflate } from 'fflate';
import { detect, type ImportBundle } from './parse';

async function readZipJson(file: File, progress: (p: number) => void): Promise<Record<string, unknown>> {
  const out: Record<string, unknown> = {};
  const pending: Promise<void>[] = [];
  const unzip = new Unzip();
  unzip.register(UnzipInflate);
  unzip.onfile = (f) => {
    if (!/\.json$/i.test(f.name)) return; // not started = skipped
    pending.push(
      new Promise<void>((resolve, reject) => {
        const chunks: Uint8Array[] = [];
        f.ondata = (err, data, final) => {
          if (err) return reject(err);
          chunks.push(data);
          if (final) {
            const total = chunks.reduce((s, c) => s + c.length, 0);
            const buf = new Uint8Array(total);
            let o = 0;
            for (const c of chunks) {
              buf.set(c, o);
              o += c.length;
            }
            try {
              out[f.name] = JSON.parse(new TextDecoder('utf-8').decode(buf));
            } catch {
              /* non-JSON or damaged entry: ignored */
            }
            resolve();
          }
        };
        f.start();
      }),
    );
  };
  const reader = file.stream().getReader();
  let read = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      unzip.push(new Uint8Array(0), true);
      break;
    }
    read += value.length;
    progress(read / file.size);
    unzip.push(value);
  }
  await Promise.all(pending);
  return out;
}

/** The export in the chosen files as a bundle of conversations and projects. */
export async function readExport(files: File[], progress: (p: number) => void): Promise<ImportBundle> {
  const all: Record<string, unknown> = {};
  for (const file of files) {
    if (/\.zip$/i.test(file.name) || file.type === 'application/zip') Object.assign(all, await readZipJson(file, progress));
    else all[file.name] = JSON.parse(await file.text());
  }
  return detect(all);
}
