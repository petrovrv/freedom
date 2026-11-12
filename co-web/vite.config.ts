// SPDX-License-Identifier: AGPL-3.0-or-later
import react from '@vitejs/plugin-react'
import { existsSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join, resolve } from 'node:path'
import { defineConfig } from 'vite'

// Freedom F1: names of this install come from install.json (install/aiwb_install.py); only the public OAuth client id
// of the lab page is needed at build time. No file: an empty id (the lab page cannot ask for Pub/Sub).
function installJson(): { google?: { webClientId?: string }; connectors?: { corporate?: boolean } } {
  // same lookup as aiwb_install.path(): AIWB_INSTALL, else %LOCALAPPDATA% on Windows, else ~/.config/aiwb
  const p =
    process.env.AIWB_INSTALL ||
    (process.platform === 'win32' && process.env.LOCALAPPDATA
      ? join(process.env.LOCALAPPDATA, 'AIWorkbench', 'install.json')
      : join(homedir(), '.config', 'aiwb', 'install.json'))
  try {
    return existsSync(p) ? JSON.parse(readFileSync(p, 'utf8').replace(/^﻿/, '')) : {}
  } catch {
    return {}
  }
}

// Freedom F1 5b: the corporate connector's manifest (names, agents, tools) for corporate.ts, wherever the module is in
// the tree (AIWB_CORPORATE_MANIFEST: another file). When install.json does not turn the connector on
// (connectors.corporate !== true) it is embedded as paused: its agents are not offered and its tools are denied, as
// before. No file: null, CO Web shows neutral texts.
function corporateManifest(): unknown {
  const p = process.env.AIWB_CORPORATE_MANIFEST || resolve(__dirname, '..', 'connectors', 'corporate', 'manifest.json')
  try {
    if (!existsSync(p)) return null
    const m = JSON.parse(readFileSync(p, 'utf8').replace(/^﻿/, ''))
    if (installJson().connectors?.corporate !== true && m && typeof m === 'object') m.paused = true
    return m
  } catch {
    return null
  }
}

// Two entries: the CO app and the latency lab page.
export default defineConfig({
  plugins: [react()],
  define: {
    __CO_CLIENT_ID__: JSON.stringify(installJson().google?.webClientId || ''),
    __CO_CORPORATE__: JSON.stringify(corporateManifest()),
  },
  build: {
    // 0.78.2: fonts (KaTeX) stay files - the site CSP has no font-src data:, so inlined small fonts were blocked
    assetsInlineLimit: (file) => (/\.(woff2?|ttf|otf)$/.test(file) ? false : undefined),
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
        lab: resolve(__dirname, 'lab.html'),
      },
      // 0.80.0: the pdf.js worker (an .mjs asset) is written as .js, which the host serves as JavaScript for sure
      output: {
        assetFileNames: (a) => (/\.mjs$/.test(a.names?.[0] || '') ? 'assets/[name]-[hash].js' : 'assets/[name]-[hash][extname]'),
      },
    },
  },
})
