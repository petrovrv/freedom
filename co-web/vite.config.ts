// SPDX-License-Identifier: AGPL-3.0-or-later
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
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
