// SPDX-License-Identifier: AGPL-3.0-or-later
// Builds the module page ui/import.js (one IIFE file: the parsers, the zip reader with fflate, the page) from src/ with
// the Vite of CO Web (co-web/node_modules; run `npm ci` there first). The built file is part of the signed module, so
// rebuild, then `python install/freedom_modules.py sign modules/chat-import` after a change of src/.
//   node modules/chat-import/build.mjs
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const coWeb = resolve(here, '..', '..', 'co-web');
const req = createRequire(resolve(coWeb, 'package.json'));
const { build } = await import(pathToFileURL(req.resolve('vite')).href);

await build({
  configFile: false,
  root: here,
  logLevel: 'warn',
  resolve: { alias: { fflate: resolve(dirname(req.resolve('fflate/package.json')), 'esm', 'browser.js') } },
  build: {
    outDir: resolve(here, 'ui'),
    emptyOutDir: false,
    minify: true,
    target: 'es2020',
    lib: { entry: resolve(here, 'src', 'page.ts'), formats: ['iife'], name: 'chatImport', fileName: () => 'import.js' },
  },
});
console.log('built ui/import.js');
