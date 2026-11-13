// SPDX-License-Identifier: AGPL-3.0-or-later
// Build of the study viewer: ui-src/main.tsx -> ui/viewer.js, ONE minified IIFE (a classic script: module scripts of a
// sandboxed frame without an origin would need CORS). Bundled: React, pdf.js and the pdf.js worker module (it runs on
// the main thread, see ui-src/main.tsx). The bundler is rolldown from ../../co-web/node_modules (Vite 8 builds with it;
// esbuild is no longer installed there), and so are react, react-dom and pdfjs-dist. The server has no build step:
// commit ui/viewer.js after a change of ui-src/.
//   node modules/study/build.mjs            build
//   node modules/study/build.mjs --check    build into memory and fail when ui/viewer.js differs (stale build)
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const nodeModules = path.resolve(here, '../../co-web/node_modules');
const require = createRequire(path.join(nodeModules, 'x.js'));
const { build } = await import(pathToFileURL(require.resolve('rolldown')).href);

const out = path.join(here, 'ui/viewer.js');
const banner = '/* SPDX-License-Identifier: AGPL-3.0-or-later. Module study, study viewer: built by modules/study/build.mjs from ui-src/ (React, pdf.js: Apache-2.0, MIT). Do not edit. */';

const res = await build({
  input: path.join(here, 'ui-src/main.tsx'),
  cwd: here,
  platform: 'browser',
  resolve: { modules: [nodeModules, 'node_modules'] },
  transform: {
    jsx: { runtime: 'automatic' },
    define: { 'process.env.NODE_ENV': '"production"', 'import.meta.url': '""' },
  },
  logLevel: 'warn',
  write: false,
  output: {
    format: 'iife',
    minify: true,
    codeSplitting: false,
    comments: { legal: false },
  },
});
const chunks = (res.output || []).filter((o) => o.type === 'chunk');
if (chunks.length !== 1) throw new Error(`expected one chunk, got ${chunks.length}`);
const code = `${banner}\n${chunks[0].code}`; // the minifier drops an output banner
if (/^\s*(import|export)\s/m.test(code.slice(0, 2000))) throw new Error('the bundle is not an IIFE');

if (process.argv.includes('--check')) {
  let old = '';
  try { old = readFileSync(out, 'utf8'); } catch { /* missing */ }
  if (old !== code) {
    console.error('ui/viewer.js is stale: run node modules/study/build.mjs');
    process.exit(1);
  }
  console.log('ui/viewer.js is up to date');
} else {
  writeFileSync(out, code);
  console.log(`ui/viewer.js: ${(code.length / 1024).toFixed(0)} KB`);
}
