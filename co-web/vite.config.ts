// SPDX-License-Identifier: AGPL-3.0-or-later
import react from '@vitejs/plugin-react'
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { defineConfig, type Plugin } from 'vite'

// Freedom F10 (ext/1 provides.client): browser extensions whose panel frames CO Web come from the modules of this tree
// (modules/<id>/module.json provides.client {kind: "browser-extension", id}); the core names none of them.
function extensionOrigins(): string[] {
  const dir = resolve(__dirname, '../modules')
  if (!existsSync(dir)) return []
  const out = new Set<string>()
  for (const d of readdirSync(dir)) {
    const f = join(dir, d, 'module.json')
    if (!existsSync(f)) continue
    try {
      const c = JSON.parse(readFileSync(f, 'utf8'))?.provides?.client
      if (c?.kind === 'browser-extension' && /^[a-p]{32}$/.test(String(c.id || ''))) out.add(`chrome-extension://${c.id}`)
    } catch {
      // a broken manifest adds nothing (freedom_modules.py check reports it)
    }
  }
  return [...out].sort()
}

const EXT_ORIGINS = extensionOrigins()

// dist/.htaccess: the frame-ancestors of the site's CSP get the same origins (public/.htaccess holds the tokens)
function extensionFramers(): Plugin {
  let outDir = ''
  return {
    name: 'co-extension-framers',
    apply: 'build',
    configResolved(c) { outDir = resolve(c.root, c.build.outDir) },
    closeBundle() {
      const f = join(outDir, '.htaccess')
      if (!existsSync(f)) return
      const list = EXT_ORIGINS.join(' ')
      const text = readFileSync(f, 'utf8')
        .split('__CO_EXT_FRAMERS_OR_NONE__').join(list || "'none'")
        .split('__CO_EXT_FRAMERS__').join(list ? ' ' + list : '')
      if (/__CO_EXT_FRAMERS(_OR_NONE)?__/.test(text)) throw new Error('dist/.htaccess: extension tokens left')
      writeFileSync(f, text)
    },
  }
}

export default defineConfig({
  plugins: [react(), extensionFramers()],
  define: { __CO_EXT_ORIGINS__: JSON.stringify(EXT_ORIGINS) },
  build: {
    // 0.78.2: fonts (KaTeX) stay files - the site CSP has no font-src data:, so inlined small fonts were blocked
    assetsInlineLimit: (file) => (/\.(woff2?|ttf|otf)$/.test(file) ? false : undefined),
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
      },
      // 0.80.0: the pdf.js worker (an .mjs asset) is written as .js, which the host serves as JavaScript for sure
      output: {
        assetFileNames: (a) => (/\.mjs$/.test(a.names?.[0] || '') ? 'assets/[name]-[hash].js' : 'assets/[name]-[hash][extname]'),
      },
    },
  },
})
