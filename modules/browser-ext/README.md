# Module browser-ext (AI Workbench browser extension)

Chrome/Edge extension (Manifest V3). The side panel holds CO (`https://co.example.org/?ext=1`) in a frame. The agent
in a CO chat gets `browser_*` tools that act in the user's own tabs: mail drafts, forms, and multi-page applications
with Next steps. The user's own sign-ins are used and no separate browser profile is needed.

Since Freedom F10 this is a module (SPEC §3.6, §3.7 row "Расширение браузера"; was `browser-ext/` in the repository
root). It is a **client of `device.tools`**: the panel registers itself as a web device of the core (`webdev_*`), the
core's rules (risk classes, confirmations, read-guard, restricted jobs) apply to its tools as to any device.

| Part | Here |
|---|---|
| `extension/` | the unpacked extension (manifest version = module version) |
| `module.json` | `provides.client` (kind, id, what it embeds), `provides.deviceTools` (the `browser_*` tools with a risk class) |
| `package.py` | zip for another PC or a store upload (`dist/ai-workbench-extension-<version>.zip`, with `site.js`); `--check` compares the tools of `panel.js` with `module.json` |
| `tests/` | `python modules/browser-ext/tests/run.py tools` (local fake site) or `live` |

`extension/site.js` (the CO site of this install) and `dist/` are `generated`: neither signed nor uploaded. Nothing of
the module goes to the server (`localOnly`); installing it registers the module so it shows in Host → Modules.

## Install (developer mode)

1. `python install/aiwb_install.py ext-config modules/browser-ext/extension/site.js` (the CO site of this install).
2. Open `chrome://extensions` (Edge: `edge://extensions`) and turn on **Developer mode**.
3. Click **Load unpacked** and pick `<repo>/modules/browser-ext/extension` (or unzip the package of `package.py`).
4. Open the panel with the toolbar button or **Alt+Shift+A**. Sign in to CO: sign-in opens in a tab, and the panel
   picks it up within a few seconds.

An extension loaded from the old folder `browser-ext/` (before F10) must be removed and loaded again from the new one.

The manifest `key` fixes the extension id at `eobijkacekfneefkngaejdcdaehfijaf`. CO Web allows only this origin to
frame it (CSP `frame-ancestors` in `co-web/public/.htaccess`, module pages in `module.php`) and talks only to it
(`co-web/src/extLink.ts`). Store builds must keep the same id; otherwise, update these places (a later ext/ point:
allowed client origins from `provides.client` of enabled modules).

## How it works

```
agent (Bridge home-main) --MCP--> Bridge --webdev_call_put--> database <--webdev_claim-- CO Web in the panel frame
                                                                                      | postMessage (co-ext-call)
                                                                                      v
                                                     panel.js: rules, confirmations -> chrome.scripting / debugger -> tab
```

- CO Web (`co-web/src/extDevice.ts`) registers the panel as its own **web device** (schema 11, `webdev_*` ops). Its tool
  catalog comes from `TOOLS` in `panel.js`. The server and Bridge are unchanged: Bridge treats it like any `w-` device.
- In the composer the device is shown as `🧭 Browser extension on … (this panel)` and is on by default inside the panel.
  The extension's device works only while the panel is open.
- `page.js` is injected on demand into every frame of the run's tab, in the extension's isolated world. It builds the
  snapshot with refs `[n12]` / `[f3:n12]` and performs the actions. Cross-origin frames (CAPTCHA widgets) get their
  own sections.
- `browser_upload path=…` reads the file from Google Drive in CO (CO has the token) and passes the bytes to the panel.
- Never send a tool with an empty `properties` object. The server's PHP JSON round trip turns `{}` into `[]`, and Claude Code then
  silently drops every tool of the device.

## Tools

`browser_status`, `browser_use_tab`, `browser_snapshot`, `browser_navigate`, `browser_open`, `browser_back`, `browser_click`
(`trusted=true` = real mouse input through `chrome.debugger`), `browser_type`, `browser_select`, `browser_press`,
`browser_scroll`, `browser_upload`, `browser_wait`, `browser_ask_user`.

A run works in the tab the user was looking at when the first page tool was called. It switches tabs with
`browser_open` or `browser_use_tab`. An orange frame marks the tab the agent works in, and the frame has a Pause
button.

## Rules (owner, chat CHAT-20261029223004-WDSH, 29.10.2026)

| Action | What happens |
|---|---|
| Send / publish / reply / post | Runs only if the user allows that one action in the panel; `send_denied` otherwise |
| Purchase, delete, final submit (Submit/Apply/Confirm), file upload | Confirmed in the panel for each action |
| `<select>` change | Confirmed; "Allow for this run" offered |
| Next / Continue steps, cookie banners, other clicks | Run without asking (option: *Ask before clicks with an unclear effect*) |
| CAPTCHA | The agent clicks it itself (`trusted=true`); for a picture puzzle it calls `browser_ask_user` |
| Password, MFA / one-time code, payment fields | The agent never types there; it calls `browser_ask_user` |
| Pause agent (panel or orange bar) | Every page tool fails with `paused_by_user` until Resume |

Confirmations are drawn in the panel, outside both CO and the web page, so neither can fake or skip them. Each one
waits up to 100 s; Bridge waits 120 + 15 s for a web device call. Page text is returned as untrusted, so the job
becomes tainted the same way it does with other device tools.

The agent does not work on the own CO site, browser pages (`chrome://`, `edge://`), or extension stores.

## Tests

```
python tests\run.py tools   # 23 checks on a local fake site (cookie banner, 2-step form, upload, submit, mail send, CAPTCHA frame, pause)
python tests\run.py live    # CO build in the panel + real Claude job through the queue; deletes the chat, revokes the test device
```
