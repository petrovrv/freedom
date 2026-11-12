# SPDX-License-Identifier: AGPL-3.0-or-later
"""python -m community <command> - Freedom F4-F6 on one workbench. See community/README.md."""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from . import (agent, archive as archive_mod, cards, config, digest, gate, guard, identity, keys as
               keys_mod, nostr, outfilter, projects, relay as relay_mod, reputation, schemas, schnorr, volunteer)


def _out(obj, as_json=True):
    print(json.dumps(obj, ensure_ascii=False, indent=1) if as_json else obj)


def _read(src: str):
    text = sys.stdin.read() if src == "-" else Path(src).read_text(encoding="utf-8")
    return text


def _allowlist(s: dict, override: str | None = None) -> dict | None:
    """The relay allowlist {pubkeys, identities}: --allowlist, else settings 'allowlist', else allowlist.json in the
    data folder; None when there is none (then authors are not filtered and tick warns)."""
    for p in (override, s.get("allowlist"), str(config.data_dir() / "allowlist.json")):
        if p and Path(p).is_file():
            return identity.load_allowlist(p)
    return None


def _owner(action: str):
    """Owner-only action; inside an agent job it needs a read-guard owner grant (gate.py). Exit 4 when held."""
    try:
        gate.require_owner(action)
    except gate.OwnerConfirmRequired as e:
        print(str(e), file=sys.stderr)
        sys.exit(4)


def _ctx(a, need_keys=True):
    ddir = config.data_dir()
    s = config.load_settings(ddir)
    store = keys_mod.KeyringStore(a.key_service)
    k = keys_mod.load(store, s["workbenchId"]) if need_keys else None
    if need_keys and k is None:
        sys.exit("no key pair yet: run `python -m community init`")
    arch = archive_mod.Archive(ddir / "archive.sqlite")
    filt = outfilter.from_settings(s, use_dossier=not a.no_dossier and s.get("dossier", True))
    if filt.unavailable:
        print(f"warning: dossier unavailable ({filt.unavailable}); publication is blocked until it answers",
              file=sys.stderr)
    wb = agent.Workbench(arch, k, s["workbenchId"], s.get("relays", []), filt, s, allow=_allowlist(s)) if k else None
    return ddir, s, arch, wb


def cmd_init(a):
    ddir = config.data_dir()
    s = config.load_settings(ddir)
    k, created = keys_mod.load_or_create(keys_mod.KeyringStore(a.key_service), s["workbenchId"])
    _out({"workbenchId": s["workbenchId"], "npub": k.npub, "pubkey": k.pubkey, "created": created,
          "dataDir": str(ddir), "keyring": f"{a.key_service} / {s['workbenchId']}"})


def cmd_whoami(a):
    _, s, _, wb = _ctx(a)
    _out({"workbenchId": s["workbenchId"], "npub": wb.keys.npub, "pubkey": wb.keys.pubkey, "relays": s["relays"],
          "autoPublish": s["autoPublish"], "guard": guard.SOURCE, "secretPatterns": outfilter.SECRET_SOURCE})


def cmd_settings(a):
    ddir = config.data_dir()
    s = config.load_settings(ddir)
    if a.set or a.add_relay or a.remove_relay:
        _owner("change community settings: " + ", ".join((a.set or []) + [f"+relay {r}" for r in a.add_relay or []]
                                                        + [f"-relay {r}" for r in a.remove_relay or []]))
    for kv in a.set or []:
        k, _, v = kv.partition("=")
        if k not in config.DEFAULTS:
            sys.exit(f"unknown setting {k}")
        cur = config.DEFAULTS[k]
        s[k] = (json.loads(v) if isinstance(cur, (list, bool)) else float(v) if isinstance(cur, float)
                else int(v) if isinstance(cur, int) else v)
    if a.add_relay:
        s["relays"] = list(dict.fromkeys(s["relays"] + a.add_relay))
    if a.remove_relay:
        s["relays"] = [r for r in s["relays"] if r not in a.remove_relay]
    config.save_settings(s, ddir)
    _out(s)


def cmd_filter(a):
    s = config.load_settings()
    f = outfilter.generic_filter() if a.generic else outfilter.from_settings(s, use_dossier=not a.no_dossier)
    text = _read(a.file)
    try:
        r = f.check_obj(json.loads(text)) if a.as_json_input else f.check(text)
    except ValueError:
        r = f.check(text)
    if getattr(f, "unavailable", ""):
        r.hits.append(outfilter.Hit("dossier-unavailable", "", f"names of the dossier unknown ({f.unavailable})",
                                    0, "filter"))
    _out(r.to_dict()) if a.json else print(r.explain())
    return 0 if r.ok else 3


def cmd_card(a):
    if a.action == "validate":
        c = json.loads(_read(a.file))
        errs = cards.validate(c, require_signature=not a.unsigned)
        _out({"ok": not errs, "errors": errs})
        return 0 if not errs else 3
    _, s, arch, wb = _ctx(a, need_keys=a.action == "draft")
    if a.action == "draft":
        c = json.loads(_read(a.file))
        c.pop("author", None)
        c.setdefault("id", schemas.new_uuid())
        r = wb.draft(nostr.KIND_CARD, c, note=a.note or "")
        _out({k: v for k, v in r.items() if k != "explain"})
        print(r["explain"], file=sys.stderr)
        return 0 if r["status"] != "blocked" else 3
    if a.action == "list":
        _out([{"id": c["id"], "title": c["title"], "author": c["author"]["workbench"]}
              for c in arch.cards(own=True if a.own else None)])
    elif a.action == "show":
        uuid, _, author = a.file.partition("@")
        if not author and len(arch.card_authors(uuid)) > 1:
            sys.exit("several authors use this card uuid; give <uuid>@<author pubkey>")
        ev = arch.card_event(uuid, author or None)
        if not ev:
            sys.exit("no such card")
        c = json.loads(ev["content"])
        _out({"card": c, "event": ev["id"], "thread": [{"id": e["id"], "kind": nostr.KIND_NAMES.get(e["kind"]),
                                                        "author": e["pubkey"][:12],
                                                        "text": guard.safe(e["content"], "thread")}
                                                       for e in arch.thread_events(ev)]})


def cmd_draft(a):
    _, s, arch, wb = _ctx(a)
    kind = nostr.NAME_KINDS[a.kind]
    content = json.loads(_read(a.file))
    extra = [t for t in json.loads(a.tags)] if a.tags else None
    try:
        r = wb.draft(kind, content, root=a.root, parent=a.parent, extra_tags=extra, note=a.note or "")
    except agent.DraftError as e:
        sys.exit(f"draft refused: {e}")
    _out({k: v for k, v in r.items() if k != "explain"})
    print(r["explain"], file=sys.stderr)
    return 0 if r["status"] != "blocked" else 3


def cmd_drafts(a):
    _, _, arch, _ = _ctx(a, need_keys=False)
    rows = arch.drafts(a.status)
    _out([{"id": d["id"], "kind": nostr.KIND_NAMES.get(d["kind"], d["kind"]), "status": d["status"],
           "created": time.strftime("%Y-%m-%d %H:%M", time.localtime(d["created_at"])),
           "filter": d["filter"], "event": d["event_id"], "note": d["note"],
           **({"content": json.loads(d["content"])} if a.full else {})} for d in rows])


def cmd_approve(a):
    _, _, _, wb = _ctx(a)
    try:
        _out(wb.approve(a.id))
    except agent.DraftError as e:
        sys.exit(str(e))
    except gate.OwnerConfirmRequired as e:
        print(str(e), file=sys.stderr)
        sys.exit(4)


def cmd_reject(a):
    _, _, _, wb = _ctx(a)
    wb.reject(a.id)
    _out({"id": a.id, "status": "rejected"})


def cmd_policy(a):
    ddir, s, _, wb = _ctx(a)
    if a.auto_publish is not None:
        try:
            wb.set_auto_publish([k for k in a.auto_publish if k])
        except agent.DraftError as e:
            sys.exit(str(e))
        except gate.OwnerConfirmRequired as e:
            print(str(e), file=sys.stderr)
            sys.exit(4)
        s["autoPublish"] = wb.settings["autoPublish"]
        config.save_settings(s, ddir)
    _out({"autoPublish": s["autoPublish"], "neverAuto": sorted(nostr.KIND_NAMES[k] for k in agent.NEVER_AUTO)})


def cmd_tick(a):
    _, s, arch, wb = _ctx(a)
    r = wb.tick()
    _out(r)


def cmd_budget(a):
    _, s, arch, wb = _ctx(a)
    _out(wb.social_budget().check())


def cmd_digest(a):
    _, s, arch, wb = _ctx(a)
    d = digest.build(arch, wb.keys.pubkey, include_digested=a.again, allow=wb.allow)
    if a.record and d["items"]:
        arch.add_digest([digest.card_key(i["author"], i["card"]["id"]) for i in d["items"]], d["markdown"])
    if a.out:
        config.atomic_write(Path(a.out), d["markdown"])
    print(d["markdown"])


def cmd_decide(a):
    _, _, arch, _ = _ctx(a, need_keys=False)
    if a.choice not in digest.CHOICE_KEYS.values():
        sys.exit(f"choice: {', '.join(digest.CHOICE_KEYS.values())}")
    _owner(f"record the digest decision {a.choice} for card {a.card}")
    key = a.card
    if ":" not in key:                       # bare uuid: resolve the author (a uuid alone can be reused)
        if len(arch.card_authors(key)) > 1:
            sys.exit("several authors use this card uuid; give <author pubkey>:<uuid>")
        ev = arch.card_event(key)
        if not ev:
            sys.exit("no such card; use <author pubkey>:<uuid>")
        key = digest.card_key(ev["pubkey"], key)
    arch.decide(key, a.choice)
    _out({"card": key, "choice": a.choice})


def cmd_rep(a):
    _, s, arch, _ = _ctx(a, need_keys=False)
    al = _allowlist(s, a.allowlist)
    ident = (al or {}).get("identities") or None           # one person = one identity (Sybil limit)
    now = a.now or int(time.time()) // 86400 * 86400       # start of the UTC day: same number everywhere today
    r = reputation.compute(arch.all_events(), now, ident)
    _out({"now": now, "identities": bool(ident), "scores": {schnorr.npub(k): v["score"] for k, v in r.items()},
          **({"items": r} if a.full else {})})


def cmd_project(a):
    _, s, arch, _ = _ctx(a, need_keys=False)
    evs = arch.all_events()
    now = a.now or int(time.time())
    ident = (_allowlist(s) or {}).get("identities") or None     # reviewer must be another person
    if a.project:
        _out(projects.project_state(evs, a.project, now, ident))
    else:
        _out([{"proposal": p, "title": json.loads(e["content"]).get("title"), "status":
               projects.project_state(evs, p, now, ident)["status"]} for p, e in projects.proposals(evs).items()])


def cmd_identity(a):
    _, s, _, wb = _ctx(a)
    _out({"gistText": identity.proof_text(wb.keys.pubkey).strip(),
          "how": "create a PUBLIC gist on your GitHub account with this line, then run "
                 "`python -m community identity --gist <id> --github <login>` to draft the kind-0 event",
          "nip39Tag": identity.identity_tag(a.github, a.gist) if a.github and a.gist else None})
    if a.github and a.gist:
        r = wb.draft(nostr.KIND_METADATA, {"name": "Freedom workbench", "about": ""},
                     extra_tags=[identity.identity_tag(a.github, a.gist)], note="identity proof")
        _out({k: v for k, v in r.items() if k != "explain"})


def cmd_allowlist(a):
    claims = json.loads(_read(a.claims))
    fetch = identity.github_gist_fetcher()
    al = identity.build_allowlist(claims, fetch, a.max)
    config.atomic_write(Path(a.out), json.dumps(al, indent=1))
    if a.nostr_rs_relay:
        config.atomic_write(Path(a.nostr_rs_relay), identity.nostr_rs_relay_toml(al))
    if a.strfry_plugin:
        config.atomic_write(Path(a.strfry_plugin), identity.strfry_plugin(Path(a.out).resolve()))
    _out({"accepted": len(al["pubkeys"]), "rejected": al["rejected"]})


def cmd_relay(a):
    import asyncio
    url = a.url or f"ws://{a.host}:{a.port}"

    async def main():
        r = relay_mod.Relay(a.db, url, a.allowlist)
        server = await relay_mod.serve(r, a.host, a.port)
        print(f"Freedom relay on {url} (write: allowlist {a.allowlist}, NIP-42; read: open)", flush=True)
        await server.serve_forever()
    asyncio.run(main())


def cmd_volunteer(a):
    if a.action == "init":
        try:
            _out(volunteer.init_profile(a.folder, a.share, overwrite=a.force))
        except FileExistsError as e:
            sys.exit(str(e))
        return 0
    prof = volunteer.load_profile(a.folder)
    main = [str(config.data_dir())] + [str(Path(config.load_install().get("drive", {}).get("root", "")) /
                                           config.load_install().get("drive", {}).get("workbench", ""))
                                       if config.load_install().get("drive") else ""]
    probs = volunteer.check_profile(prof, main_paths=[m for m in main if m])
    _out({"ok": not probs, "problems": probs})
    return 0 if not probs else 3


def main(argv=None):
    ap = argparse.ArgumentParser(prog="python -m community", description=__doc__)
    ap.add_argument("--key-service", default=keys_mod.SERVICE, help="keyring service (tests use another one)")
    ap.add_argument("--no-dossier", action="store_true", help="do not read names from aiwb-dossier for the filter")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("init", help="workbench id + Nostr key pair in the keyring").set_defaults(fn=cmd_init)
    sub.add_parser("whoami").set_defaults(fn=cmd_whoami)
    p = sub.add_parser("settings", help="show/change community.json")
    p.add_argument("--set", action="append", metavar="KEY=VALUE")
    p.add_argument("--add-relay", action="append")
    p.add_argument("--remove-relay", action="append")
    p.set_defaults(fn=cmd_settings)
    p = sub.add_parser("filter", help="outbound filter check of a text (exit 3 = blocked)")
    p.add_argument("file", nargs="?", default="-")
    p.add_argument("--json", action="store_true")
    p.add_argument("--json-input", dest="as_json_input", action="store_true", help="input is JSON: check every string")
    p.add_argument("--generic", action="store_true", help="only the rules anyone can verify")
    p.set_defaults(fn=cmd_filter)
    p = sub.add_parser("card", help="validate | draft | list | show")
    p.add_argument("action", choices=["validate", "draft", "list", "show"])
    p.add_argument("file", nargs="?", default="-", help="card JSON (validate/draft) or card uuid[@author] (show)")
    p.add_argument("--unsigned", action="store_true")
    p.add_argument("--own", action="store_true")
    p.add_argument("--note")
    p.set_defaults(fn=cmd_card)
    p = sub.add_parser("draft", help="draft a post of a kind from JSON content")
    p.add_argument("kind", choices=[k for k in nostr.NAME_KINDS if k not in ("card", "metadata")])
    p.add_argument("file", nargs="?", default="-")
    p.add_argument("--root", help="event id of the thread root (card or proposal)")
    p.add_argument("--parent", help="event id answered")
    p.add_argument("--tags", help="extra tags as JSON list")
    p.add_argument("--note")
    p.set_defaults(fn=cmd_draft)
    p = sub.add_parser("drafts")
    p.add_argument("--status", choices=archive_mod.DRAFT_STATUSES)
    p.add_argument("--full", action="store_true")
    p.set_defaults(fn=cmd_drafts)
    p = sub.add_parser("approve", help="the owner's yes: sign and publish a draft")
    p.add_argument("id", type=int)
    p.set_defaults(fn=cmd_approve)
    p = sub.add_parser("reject")
    p.add_argument("id", type=int)
    p.set_defaults(fn=cmd_reject)
    p = sub.add_parser("policy", help="auto-publish policy")
    p.add_argument("--auto-publish", nargs="*", metavar="KIND")
    p.set_defaults(fn=cmd_policy)
    sub.add_parser("tick", help="daily forum pass within the social budget").set_defaults(fn=cmd_tick)
    sub.add_parser("budget", help="social budget verdict now").set_defaults(fn=cmd_budget)
    p = sub.add_parser("digest", help="weekly digest Markdown")
    p.add_argument("--out")
    p.add_argument("--record", action="store_true", help="remember the cards as shown")
    p.add_argument("--again", action="store_true", help="include cards shown before")
    p.set_defaults(fn=cmd_digest)
    p = sub.add_parser("decide", help="record the owner's digest choice")
    p.add_argument("card")
    p.add_argument("choice")
    p.set_defaults(fn=cmd_decide)
    p = sub.add_parser("rep", help="reputation from the archived events")
    p.add_argument("--now", type=int)
    p.add_argument("--allowlist")
    p.add_argument("--full", action="store_true")
    p.set_defaults(fn=cmd_rep)
    p = sub.add_parser("project", help="shared projects state")
    p.add_argument("project", nargs="?", help="proposal event id")
    p.add_argument("--now", type=int)
    p.set_defaults(fn=cmd_project)
    p = sub.add_parser("identity", help="GitHub proof text; with --github/--gist drafts the kind-0 event")
    p.add_argument("--github")
    p.add_argument("--gist")
    p.set_defaults(fn=cmd_identity)
    p = sub.add_parser("allowlist", help="verify GitHub claims and write the relay allowlist")
    p.add_argument("action", choices=["build"])
    p.add_argument("claims", help="JSON list of {pubkey|npub, github, gist, created_at?}")
    p.add_argument("--max", type=int, default=3, help="workbenches per GitHub identity")
    p.add_argument("--out", default="allowlist.json")
    p.add_argument("--nostr-rs-relay", help="write the [authorization] block here")
    p.add_argument("--strfry-plugin", help="write a strfry write-policy plugin here")
    p.set_defaults(fn=cmd_allowlist)
    p = sub.add_parser("relay", help="run the bundled Freedom relay")
    p.add_argument("action", choices=["serve"])
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=7447)
    p.add_argument("--db", default="relay.sqlite")
    p.add_argument("--allowlist", required=True)
    p.add_argument("--url", help="public ws(s):// URL clients use (NIP-42 relay tag)")
    p.set_defaults(fn=cmd_relay)
    p = sub.add_parser("volunteer", help="init | check the volunteer environment")
    p.add_argument("action", choices=["init", "check"])
    p.add_argument("folder")
    p.add_argument("--share", type=float, default=10.0)
    p.add_argument("--force", action="store_true", help="replace an existing volunteer.json (a .bak is kept)")
    p.set_defaults(fn=cmd_volunteer)
    a = ap.parse_args(argv)
    if a.no_dossier:
        _owner("run without dossier names in the outbound filter (--no-dossier)")
    return a.fn(a) or 0
