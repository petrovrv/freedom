#!/usr/bin/env python3
"""Read messages of an AI Workbench chat from the CO database by number or search (for agents working from the chat protocol);
find: which chats of the environment talked about something (messages + protocol + vectors + aliases, schema 20);
family / post: list family chats and post into one as AI Workbench (instructions for the family)."""

from __future__ import annotations

import argparse
import base64
import gzip
import json
import os
import sys
import time
import urllib.error
import urllib.request
from typing import Any

import keyring

KEYRING_SERVICE = "aiwb/co-push"
KEYRING_ACCOUNT = "default"
USER_AGENT = "aiwb-chat/0.3.1"
TIMEOUT = 60
BACKOFFS = (3, 6)  # sleeps between attempts -> 3 attempts total


class CliError(Exception):
    """Fatal error to be reported on stderr with exit code 1."""


def install_get(key: str, default: Any = None) -> Any:
    """One value of install.json: AIWB_INSTALL, else %LOCALAPPDATA%/AIWorkbench/install.json (Windows), else
    ~/.config/aiwb/install.json; the default when the file or the key is missing."""
    path = os.environ.get("AIWB_INSTALL") or (
        os.path.join(os.environ["LOCALAPPDATA"], "AIWorkbench", "install.json")
        if os.name == "nt" and os.environ.get("LOCALAPPDATA")
        else os.path.join(os.path.expanduser("~"), ".config", "aiwb", "install.json"))
    try:
        with open(path, encoding="utf-8-sig") as f:
            cur = json.load(f)
    except (OSError, ValueError):
        return default
    for part in key.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur


def endpoint() -> str:
    """db.php of the site: AIWB_DB_URL, else install.json site.url + /agent/db.php."""
    if os.environ.get("AIWB_DB_URL"):
        return os.environ["AIWB_DB_URL"]
    url = install_get("site.url")
    if not url:
        raise CliError("error: site unknown - set site.url in install.json or AIWB_DB_URL")
    return url.rstrip("/") + "/agent/db.php"


def owner_env() -> str:
    """Environment of the owner: AIWB_OWNER_ENV, else the member with role owner in install.json, else "owner"."""
    if os.environ.get("AIWB_OWNER_ENV"):
        return os.environ["AIWB_OWNER_ENV"]
    owner = next((m for m in install_get("members", []) or [] if m.get("role") == "owner"), {})
    return owner.get("env") or "owner"


SANDBOX_HINT = (
    "error: no network - the Codex sandbox blocks outgoing connections "
    "(CODEX_SANDBOX_NETWORK_DISABLED=1). Run the tool through the aiwb-tools MCP "
    "(tool_run aiwb-chat ...) or ask for an escalated (outside sandbox) run."
)


def sandbox_blocked(reason: object) -> bool:
    """True when the Codex sandbox, not the server, refused the connection."""
    if os.environ.get("CODEX_SANDBOX_NETWORK_DISABLED") != "1":
        return False
    return isinstance(reason, OSError)


def get_key() -> str:
    key = keyring.get_password(KEYRING_SERVICE, KEYRING_ACCOUNT)
    if not key:
        raise CliError(
            f"error: no key in keyring {KEYRING_SERVICE} (account {KEYRING_ACCOUNT})"
        )
    return key


def call(op: str, params: dict[str, Any]) -> dict[str, Any]:
    key = get_key()
    payload = {"op": op, **params}
    raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    body = json.dumps(
        {"gz": base64.b64encode(gzip.compress(raw)).decode("ascii")}
    ).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "X-CO-Push-Key": key,
        "User-Agent": USER_AGENT,
    }

    attempts = len(BACKOFFS) + 1
    last_error = ""
    url = endpoint()
    for attempt in range(attempts):
        req = urllib.request.Request(url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                data = resp.read()
            try:
                return json.loads(data.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise CliError(f"error: invalid JSON response: {exc}") from exc
        except urllib.error.HTTPError as exc:
            try:
                text = exc.read().decode("utf-8", errors="replace")
            except Exception:
                text = ""
            if 400 <= exc.code < 500:
                raise CliError(f"error: HTTP {exc.code} {text[:300]}") from exc
            last_error = f"error: HTTP {exc.code} {text[:300]}"
        except urllib.error.URLError as exc:
            if sandbox_blocked(exc.reason):
                raise CliError(SANDBOX_HINT) from exc
            last_error = f"error: {exc.reason}"
        except TimeoutError as exc:
            last_error = f"error: timeout ({exc})"
        if attempt < attempts - 1:
            time.sleep(BACKOFFS[attempt])
    raise CliError(last_error or "error: request failed")


def role_label(msg: dict[str, Any], with_agent: bool) -> str:
    if msg.get("type") == "user_message":
        return "User"
    agent = msg.get("agent")
    if with_agent and agent:
        return f"Agent ({agent})"
    return "Agent"


def print_full(messages: list[dict[str, Any]]) -> None:
    if not messages:
        print("no such message")
        return
    for i, msg in enumerate(messages):
        if i:
            print("---")
        header = f"#{msg.get('seq')} {role_label(msg, True)} {msg.get('createdUtc', '')}"
        if msg.get("chars") is not None:
            header += f" ({msg['chars']} chars)"
        print(header)
        print(msg.get("text") or "")


def print_lines(messages: list[dict[str, Any]], empty: str) -> None:
    if not messages:
        print(empty)
        return
    for msg in messages:
        text = " ".join((msg.get("text") or "").splitlines())
        print(f"#{msg.get('seq')} {role_label(msg, False)} {msg.get('createdUtc', '')}: {text}")


def print_chats(result: dict[str, Any]) -> None:
    chats = result.get("chats") or []
    if result.get("aliases"):
        print("aliases: " + ", ".join(result["aliases"]))
    if not chats:
        print("no matches")
        return
    for c in chats:
        ranks = c.get("ranks") or {}
        how = ",".join(k for k in ("messages", "protocol", "vector") if ranks.get(k))
        print(f"{c.get('chatId')}  {c.get('title') or ''}  [{c.get('projectId')}, {(c.get('updatedUtc') or '')[:10]}, {how}]")
        for m in c.get("matches") or []:
            print("    " + " ".join(str(m).split()))


def build_parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument(
        "--json",
        action="store_true",
        default=argparse.SUPPRESS,
        help="print the raw JSON answer",
    )

    parser = argparse.ArgumentParser(
        prog="aiwb-chat.py", description=__doc__, parents=[common]
    )
    sub = parser.add_subparsers(dest="cmd", required=True, metavar="COMMAND")

    p_get = sub.add_parser("get", parents=[common], help="read a message by sequence number")
    p_get.add_argument("chat", metavar="CHAT", help="chat id")
    p_get.add_argument("seq", metavar="SEQ", type=int, help="message sequence number")
    p_get.add_argument(
        "--around", type=int, default=0, metavar="N",
        help="also return N messages before and after (default 0, max 5)",
    )

    p_search = sub.add_parser("search", parents=[common], help="search messages by words")
    p_search.add_argument("chat", metavar="CHAT", help="chat id")
    p_search.add_argument("words", metavar="WORDS", nargs="+", help="search words")
    p_search.add_argument(
        "--limit", type=int, default=8, metavar="N", help="max results (default 8, max 20)"
    )

    p_find = sub.add_parser("find", parents=[common], help="find chats of the environment about something")
    p_find.add_argument("words", metavar="WORDS", nargs="+", help="question or words, any language")
    p_find.add_argument("--chat", default=os.environ.get("AIWB_CHAT_ID", ""), metavar="CHAT",
                        help="the asking chat: its environment is searched and it is left out (default $AIWB_CHAT_ID)")
    p_find.add_argument("--env", default="", help="environment when there is no chat (default: the owner's, install.json)")
    p_find.add_argument("--self", dest="include_self", action="store_true", help="keep the asking chat in the results")
    p_find.add_argument("--limit", type=int, default=8, metavar="N", help="max chats (default 8, max 30)")

    p_fam = sub.add_parser("family", parents=[common], help="list family chats of the environment")
    p_fam.add_argument("--env", default="", help="environment (default: the owner's, install.json)")

    p_post = sub.add_parser("post", parents=[common], help="post a message into a family chat as AI Workbench")
    src = p_post.add_mutually_exclusive_group(required=True)
    src.add_argument("--text", help="message text (Markdown, several lines allowed)")
    src.add_argument("--file", help="UTF-8 file with the message text ('-' = stdin)")
    p_post.add_argument("--chat", default="", metavar="CHAT", help="family chat id (default: the only family chat of --env)")
    p_post.add_argument("--env", default="", help="environment whose family chat is meant (default: the owner's, install.json)")
    p_post.add_argument("--reply-to", default="", metavar="EVT", help="eventId of the message this one replies to")

    p_list = sub.add_parser("list", parents=[common], help="list the latest messages of a chat")
    p_list.add_argument("chat", metavar="CHAT", help="chat id")
    p_list.add_argument(
        "--limit", type=int, default=20, metavar="N", help="max results (default 20, max 50)"
    )

    return parser


def main(argv: list[str] | None = None) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    args = build_parser().parse_args(argv)
    as_json = getattr(args, "json", False)

    if args.cmd == "find":
        params = {"q": " ".join(args.words), "limit": args.limit, "includeSelf": args.include_self}
        if args.chat and not args.env:
            params["chatId"] = args.chat
        else:
            params["env"] = args.env or owner_env()
        try:
            result = call("find_chats", params)
        except CliError as exc:
            print(str(exc), file=sys.stderr)
            return 1
        if as_json:
            print(json.dumps(result, ensure_ascii=False, indent=1))
        else:
            print_chats(result)
        return 0

    if args.cmd in ("family", "post"):
        args.env = args.env or owner_env()
        try:
            if args.cmd == "family":
                result = call("family_chats", {"env": args.env})
            else:
                if args.file:
                    text = sys.stdin.read() if args.file == "-" else open(args.file, encoding="utf-8").read()
                else:
                    text = args.text
                params = {"text": text, "env": args.env, "fromChat": os.environ.get("AIWB_CHAT_ID", "")}
                if args.chat:
                    params["chatId"] = args.chat
                if args.reply_to:
                    params["replyTo"] = args.reply_to
                result = call("family_post", params)
        except (CliError, OSError) as exc:
            print(str(exc), file=sys.stderr)
            return 1
        if as_json:
            print(json.dumps(result, ensure_ascii=False, indent=1))
        elif args.cmd == "family":
            chats = result.get("chats") or []
            print("\n".join(f"{c['chatId']}  {c['title']}  [{c['projectId']}, owner {c['owner']}, {c['updatedUtc'][:10]}]" for c in chats) or "no family chats")
        else:
            print(f"posted #{result.get('sequence')} in {result.get('chatId')} ({result.get('eventId')}), push to {result.get('pushed')}")
        return 0

    if args.cmd == "get":
        params = {"chatId": args.chat, "seq": args.seq, "around": args.around}
    elif args.cmd == "search":
        params = {"chatId": args.chat, "q": " ".join(args.words), "limit": args.limit}
    else:
        params = {"chatId": args.chat, "limit": args.limit}

    try:
        result = call("chat_read", params)
    except CliError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    if as_json:
        print(json.dumps(result, ensure_ascii=False, indent=1))
        return 0

    messages = (result.get("messages") or []) if isinstance(result, dict) else []

    if args.cmd == "get":
        print_full(messages)
    else:
        print_lines(messages, "no matches")
    return 0


if __name__ == "__main__":
    sys.exit(main())
