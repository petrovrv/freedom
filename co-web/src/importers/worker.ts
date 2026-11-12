// SPDX-License-Identifier: AGPL-3.0-or-later
// Parses an export (zip or single JSON) off the UI thread.
// Only *.json entries are inflated; images and other binaries in the zip are skipped
// while streaming, so multi-GB ChatGPT exports do not have to fit in memory.
import { Unzip, UnzipInflate } from 'fflate';
import { detect } from './parse';

type Msg = { files: File[] };

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

self.onmessage = async (e: MessageEvent<Msg>) => {
  const post = (m: unknown) => (self as unknown as Worker).postMessage(m);
  try {
    const files: Record<string, unknown> = {};
    for (const file of e.data.files) {
      if (/\.zip$/i.test(file.name) || file.type === 'application/zip') {
        Object.assign(files, await readZipJson(file, (p) => post({ type: 'progress', value: p })));
      } else {
        files[file.name] = JSON.parse(await file.text());
      }
    }
    post({ type: 'done', bundle: detect(files) });
  } catch (err) {
    post({ type: 'error', message: (err as Error).message });
  }
};
