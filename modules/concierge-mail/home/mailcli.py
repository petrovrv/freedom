"""Local concierge mail archive. Never sends mail."""
import argparse
import json
from pathlib import Path
from mailstore import Store, sync_imap, sync_account
from workflow import Workflow, critic
from archive import checkpoint, restore_checkpoint, verify_checkpoint, export_maildir
import read_guard


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    sub = parser.add_subparsers(dest='command', required=True)
    imp = sub.add_parser('import-eml')
    imp.add_argument('file', type=Path)
    imp.add_argument('--account', required=True)
    sync = sub.add_parser('sync')
    sync.add_argument('--account', required=True)
    sync.add_argument('--folder', help='Single-folder diagnostic snapshot; default includes Inbox and Sent')
    sync.add_argument('--sent-folder', default='[Gmail]/Sent Mail')
    sync.add_argument('--limit', type=int, default=100)
    sub.add_parser('list')
    show = sub.add_parser('show')
    show.add_argument('id', type=int)
    search = sub.add_parser('search')
    search.add_argument('query')
    sub.add_parser('cases')
    assign = sub.add_parser('assign')
    assign.add_argument('id', type=int)
    assign.add_argument('title')
    state = sub.add_parser('state')
    state.add_argument('case_id', type=int)
    state.add_argument('state', choices=['inbox', 'waiting_them', 'your_turn', 'closed'])
    promise = sub.add_parser('promise-add')
    promise.add_argument('case_id', type=int)
    promise.add_argument('title')
    promise.add_argument('--owner', default='me')
    promise.add_argument('--due-at')
    promise.add_argument('--message-id', type=int)
    promises = sub.add_parser('promise-list')
    promises.add_argument('--case-id', type=int)
    promises.add_argument('--status', default='open', choices=['open', 'resolved', 'cancelled', 'all'])
    for name in ['promise-resolve', 'promise-cancel']:
        sub.add_parser(name).add_argument('id', type=int)
    draft = sub.add_parser('draft-save')
    draft.add_argument('thread_id', type=int)
    draft.add_argument('--body-file', required=True, type=Path)
    draft.add_argument('--subject', default='')
    draft.add_argument('--attachments-json', type=Path)
    draft.add_argument('--expected-version', required=True, type=int)
    draft.add_argument('--confirm-untrusted', action='store_true', help='owner said yes to drafting on a thread flagged by read-guard')
    for name in ['draft-show', 'draft-history', 'draft-undo']:
        command = sub.add_parser(name)
        command.add_argument('thread_id', type=int)
        if name == 'draft-undo':
            command.add_argument('--expected-version', required=True, type=int)
    check = sub.add_parser('critic')
    check.add_argument('--body-file', required=True, type=Path)
    check.add_argument('--subject', default='')
    sub.add_parser('briefing')
    sub.add_parser('audit')
    for name in ('checkpoint', 'restore', 'verify', 'export-maildir'):
        cmd = sub.add_parser(name)
        cmd.add_argument('path', type=Path)
        if name == 'export-maildir':
            cmd.add_argument('--account', required=True)
    args = parser.parse_args()
    if args.command in ('restore', 'verify'):
        result = restore_checkpoint(args.path, args.root) if args.command == 'restore' else verify_checkpoint(args.path)
        print(json.dumps(result))
        return
    store = Store(args.root)
    try:
        flow = Workflow(store)
        command = args.command
        if command == 'import-eml':
            result = store.ingest(args.account, args.file.read_bytes())
        elif command == 'sync':
            result = sync_imap(store, args.account, folder=args.folder, limit=args.limit) if args.folder else sync_account(store, args.account, sent_folder=args.sent_folder, limit=args.limit)
        elif command == 'checkpoint':
            result = checkpoint(store, args.path)
        elif command == 'export-maildir':
            result = export_maildir(store, args.path, args.account)
        elif command == 'list':
            result = store.list_messages()
        elif command == 'show':
            result = store.get_message(args.id)
        elif command == 'search':
            result = store.search(args.query)
        elif command == 'cases':
            result = store.list_cases()
        elif command == 'assign':
            result = store.assign_case(args.id, args.title)
        elif command == 'state':
            result = flow.transition_case(args.case_id, args.state)
        elif command == 'promise-add':
            result = flow.create_promise(args.case_id, args.title, owner=args.owner, due_at=args.due_at, message_id=args.message_id)
        elif command == 'promise-list':
            result = flow.list_promises(case_id=args.case_id, status=None if args.status == 'all' else args.status)
        elif command == 'promise-resolve':
            result = flow.resolve_promise(args.id)
        elif command == 'promise-cancel':
            result = flow.delete_promise(args.id)
        elif command == 'draft-save':
            attachments = json.loads(args.attachments_json.read_text(encoding='utf-8-sig')) if args.attachments_json else []
            result = flow.save_draft(args.thread_id, args.body_file.read_text(encoding='utf-8-sig'), subject=args.subject, attachments=attachments, expected_version=args.expected_version, confirm_untrusted=args.confirm_untrusted)
        elif command == 'draft-show':
            result = flow.get_draft(args.thread_id)
        elif command == 'draft-history':
            result = flow.draft_history(args.thread_id)
        elif command == 'draft-undo':
            result = flow.undo_draft(args.thread_id, expected_version=args.expected_version)
        elif command == 'critic':
            result = critic(args.body_file.read_text(encoding='utf-8-sig'), subject=args.subject)
        elif command == 'briefing':
            result = flow.briefing()
        else:
            result = store.audit()
        print(json.dumps(read_guard.guard_obj(result, source='concierge-mail', tool='mailcli'), ensure_ascii=False, indent=2))
    finally:
        store.close()


if __name__ == '__main__':
    main()
