# CO web v0.1

Static SPA for https://co.example.org — web interface of AI Workbench (TZ-CO-WEB v1.1, Bridge protocol v3).

## Build

    npm ci
    npm run build        # -> dist/ (includes .htaccess with security headers and SPA fallback)

## Test

    node e2e/mock-e2e.mjs      # full flow against an in-memory Drive mock + simulated Bridge
    node e2e/prod-smoke.mjs    # production URL: loads, GIS present, no CSP errors
    npm test                   # web device self-test: path rules, fs_edit, result envelopes (node, no deps)

## Deploy

Upload the contents of `dist/` to `~/domains/co.example.org/public_html/` (replace `assets/`).

## Layout

- `src/auth.ts` — GIS token model; token in memory only
- `src/drive.ts` — Drive REST v3 client
- `src/workbench.ts` — AI-Workbench tree, canonical events, job protocol v3
- `src/activity.ts` — Codex/Claude JSONL -> activity cards
- `src/Shell.tsx`, `src/ChatView.tsx`, `src/Inspector.tsx` — UI
- `src/webdev*.ts`, `src/WebDevice*.tsx` — this browser as a device (0.49.0, `lab/web-device/SPEC.md`)

## Changes

- 0.51.0 — 2026-10-19 — Memory candidates: the Project tab shows counts and a Review… button; the review window is a table (tag, text, source, per-row Accept/Reject) with All/Decisions/Facts tabs, checkboxes (header = all shown, Shift+click = range), Accept all / Reject all for the shown tab and Accept/Reject selected; bulk actions confirm first and run one by one with progress.
- 0.50.0 — 2026-10-19 — Sidebar: the chevron before a project collapses/expands its chat list (a click on the project name does so too when its new-chat draft is open; from a chat it still opens the draft); chats with child chats show a folder icon (closed/open), a click on the active parent chat toggles its child chats, opening a parent expands them.
- 0.49.0 — 2026-10-19 — Web device: Host tab "This browser as a device" (folders via File System Access API, handles in IndexedDB, Reconnect, rename, Revoke), worker loop over `db.php` `webdev_*` (one tab per browser, Web Lock), 9 fs tools with in-page confirmation (Allow / Allow for this run / Deny; delete and move always asked), composer picker of web devices (`w-` ids in `request.devices`), "Google Drive for desktop" guide; default job timeout 120 min.
