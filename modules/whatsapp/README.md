# Module whatsapp (the workbench's own WhatsApp account)

A second WhatsApp number for the workbench itself, not the owner's personal one. People saved in that account's
contacts write to it and the agent answers; the owner can ask any agent to write to someone first.

| Layer | Here |
|---|---|
| service (home PC) | `tools/whatsapp-agent` — `whatsapp-agent.py run --forever --forward co` under Bridge: [whatsapp-web.js](https://github.com/pedroslopez/whatsapp-web.js) in Edge (own profile, window off the screen), local API on 127.0.0.1 with a token in keyring; incoming direct messages from saved contacts go to `forward`, everyone else is logged and ignored |
| server | `server/ops.php` — host ops `wa_ask` (the message goes into the chosen chat as `💬 name: text` and a job of its own on home-main, one per contact at a time, a contact's session resumed within 6 h) and `wa_answer` |
| isolation | the job carries `request.whatsapp`; Bridge 1.21+ then gives it `permissions.files = isolated` (Claude `--safe-mode`, `--tools WebSearch,WebFetch`, file tools denied, no shell, devices, context folders, MCP, CLAUDE.md or module context), whatever the request says; an older Bridge refuses `isolated`. The prompt has nothing of the owner's: no dossier, other chats or protocol |
| context | `context/whatsapp.md` — the owner's agents may send through the account only when the owner asks (`whatsapp-agent send`) |
| data | none on the server; at home `%LOCALAPPDATA%/AIWorkbench/wa-agent/` (linked-device session, inbox, events, `config.json` with the chat) |

Unofficial: WhatsApp's terms do not allow automated personal accounts and it may ban the number. Use a number you can
lose, do not send bulk messages.

Install:

    python install/freedom_modules.py install modules/whatsapp --enable
    python <tools>/whatsapp-agent/whatsapp-agent.py setup
    python <tools>/whatsapp-agent/whatsapp-agent.py start && python <tools>/whatsapp-agent/whatsapp-agent.py qr-window
    # scan the QR with the agent's phone (Linked devices), then choose the chat:
    python <tools>/whatsapp-agent/whatsapp-agent.py config --chat CHAT-... --assistant "the assistant of ..."
