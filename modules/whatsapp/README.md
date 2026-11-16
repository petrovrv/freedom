# Module whatsapp (the workbench's own WhatsApp account)

A second WhatsApp number for the workbench itself, not the owner's personal one. Household members write to it as to
their own CO chat; other saved contacts and groups get an isolated assistant; the owner can ask any agent to write to
someone first.

| Who | What happens |
|---|---|
| household member (`config --family NUMBER=ENV`), direct | a normal job in their own chat "💬 WhatsApp" of their environment (made on first use), their dossier and permissions |
| other saved contact, direct | an isolated job in the owner's chat "💬 WhatsApp: contacts"; without the owner in the conversation it talks only on the owner's instruction (`note`, `send --purpose`) or about the owner's affairs, otherwise asks for permission and the owner gets a 🔔 on WhatsApp |
| group | silent until called (mention, reply to it, a word of `config --names`); sees the group's last 15 messages; same rules when the owner is not in the group |
| anyone else | logged only |

Outside the family: 20 turns an hour and 80 a day per conversation; every isolated turn carries the anti-injection rules.

| Layer | Here |
|---|---|
| service (home PC) | `tools/whatsapp-agent` — `whatsapp-agent.py run --forever --forward co` under Bridge: [whatsapp-web.js](https://github.com/pedroslopez/whatsapp-web.js) in Edge (own profile, window off the screen), local API on 127.0.0.1 with a token in keyring; incoming direct messages from saved contacts go to `forward`, everyone else is logged and ignored |
| server | `server/ops.php` — host ops `wa_ask` (finds or makes the chat by title in the environment's home project, puts `💬 name: text` and a job, one per conversation at a time, session resumed within 6 h) and `wa_answer` |
| voice notes | a voice note (or audio file) from a household member or saved contact is downloaded, sent to the home voice hub of module voice (`whatsapp-agent.py transcribe`: hub address from `voice-point.json`, the point key in keyring `aiwb/voice-hub`; speech to text by speaches on the family server) and deleted; the agent gets the transcript. Without a hub it stays `[ptt]`. Voice of non-contacts is never downloaded |
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
    # optional: CO pushes of these environments go to their numbers (--family NUMBER=ENV) on WhatsApp
    python <tools>/whatsapp-agent/whatsapp-agent.py config --pushes "<env>"

Pushes (0.3.0): the server hands every push to the module's `notify` hook (ext/1 `provides.notify`); copies are kept
only for envs the home service asked for in the last 30 min (`state/modules/whatsapp/pushes.json`, at most 300, 24 h)
and the service fetches them with host op `wa_pushes` every 20 s.
