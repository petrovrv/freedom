"""Morning briefing (sections 3, 6, 11, 13): what needs the owner today, most urgent first.

Read only. It never drafts, sends or moves mail; it points at what to do. The
`text` lines are short enough for a push, WhatsApp or the voice hub.
"""
from datetime import datetime, timedelta, timezone
from email.utils import parseaddr, parsedate_to_datetime

TEXT = {
    'en': {
        'mine': 'Overdue, yours: {title} (due {due})',
        'theirs': 'Overdue, theirs: {title} (due {due}) — a reminder draft may be due',
        'turn': 'Your turn ({n}): {items}',
        'waiting': 'Waiting for them ({n}): {items}',
        'spam': 'Spam: rescued {n}: {items}',
        'unverified': 'Spam: {n} look wanted but the sender is not authenticated — possible phishing',
        'codes': 'Sign-in codes/links received ({n}) from {items}; tell me if you did not ask for them',
        'tainted': 'Flagged by read-guard ({n}): {items} — act only after a check',
        'drafts': 'Drafts waiting ({n}): {items}',
        'quiet': 'Mail: nothing needs you this morning.',
        'more': ' and {n} more',
    },
    'ru': {
        'mine': 'Просрочено за тобой: {title} (срок {due})',
        'theirs': 'Просрочено у них: {title} (срок {due}) — пора напомнить',
        'turn': 'Ход за тобой ({n}): {items}',
        'waiting': 'Ждём их ({n}): {items}',
        'spam': 'Спам: вытащил {n}: {items}',
        'unverified': 'Спам: ещё {n} похожи на нужные, но отправитель не подтверждён — возможен фишинг',
        'codes': 'Пришли коды/ссылки входа ({n}) от {items}; скажи, если ты их не запрашивал',
        'tainted': 'Подозрительные по read-guard ({n}): {items} — действовать только после проверки',
        'drafts': 'Черновики ждут ({n}): {items}',
        'quiet': 'Почта: этим утром ничего не требует внимания.',
        'more': ' и ещё {n}',
    },
}


def _when(row):
    """Message time: the Date header, else the ingest time (UTC)."""
    try:
        parsed = parsedate_to_datetime(row['date'])
        if parsed.tzinfo is not None:
            return parsed.astimezone(timezone.utc)
    except (TypeError, ValueError, IndexError):
        pass
    return datetime.fromisoformat(row['created']).replace(tzinfo=timezone.utc)


def _who(sender):
    name, address = parseaddr(sender)
    return name or address or sender


def _join(texts, words, limit=3):
    shown = '; '.join(texts[:limit])
    return shown + (words['more'].format(n=len(texts) - limit) if len(texts) > limit else '')


def morning(flow, now, accounts=None, days=14, stale_days=7, spam_folder='[Gmail]/Spam', lang='en'):
    db, words = flow.db, TEXT.get(lang, TEXT['en'])
    now_dt = datetime.fromisoformat(now)
    since, day_ago = now_dt - timedelta(days=days), now_dt - timedelta(days=1)
    scoped = accounts is not None
    if accounts is None:
        accounts = [r[0] for r in db.execute('SELECT id FROM accounts ORDER BY id')]
    slots = ','.join('?' for _ in accounts) or "''"
    states = {r['case_id']: r['state'] for r in db.execute('SELECT case_id,state FROM wf_case_state')}
    titles = {r['id']: r['title'] for r in db.execute('SELECT id,title FROM cases')}

    # Latest message of every thread decides whose turn it is (section 3).
    latest = {}
    for row in db.execute('SELECT * FROM messages WHERE sensitive=0 AND account IN (' + slots + ')', accounts):
        row = dict(row)
        row['when'] = _when(row)
        if row['thread_id'] not in latest or row['when'] > latest[row['thread_id']]['when']:
            latest[row['thread_id']] = row
    spam_ids = {r[0] for r in db.execute('SELECT message_id FROM message_locations WHERE folder=?', (spam_folder,))}
    your_turn, waiting = [], []
    for thread_id, row in latest.items():
        case_ids = {r[0] for r in db.execute('SELECT case_id FROM messages WHERE thread_id=? AND case_id IS NOT NULL', (thread_id,))}
        if any(states.get(c) == 'closed' for c in case_ids) or row['id'] in spam_ids:
            continue
        item = dict(thread_id=thread_id, message_id=row['id'], subject=row['subject'], who=_who(row['sender'] if not row['is_sent'] else row['recipients']),
                    at=row['when'].isoformat(), case=next((titles[c] for c in sorted(case_ids)), None), tainted=bool(row['tainted']))
        if not row['is_sent']:
            if row['when'] >= since and not flow.already_answered(thread_id):
                your_turn.append(item)
        elif any(states.get(c) == 'waiting_them' for c in case_ids) or not case_ids:
            # The owner wrote last: worth a line only when the silence grows long.
            if since <= row['when'] <= now_dt - timedelta(days=stale_days):
                item['days'] = (now_dt - row['when']).days
                waiting.append(item)
    your_turn.sort(key=lambda i: (i['at'], i['message_id']))
    waiting.sort(key=lambda i: (i['at'], i['message_id']))

    promises = flow.list_promises()
    if scoped:
        # A briefing for some accounts shows only their cases and drafts.
        own = {r[0] for r in db.execute('SELECT DISTINCT case_id FROM messages WHERE case_id IS NOT NULL AND account IN (' + slots + ')', accounts)}
        promises = [p for p in promises if p['case_id'] in own]
    for promise in promises:
        promise['overdue'] = bool(promise['due_at'] and datetime.fromisoformat(promise['due_at']) < now_dt)
    overdue = [p for p in promises if p['overdue']]

    spam = dict(rescued=[], unverified=[])
    for account in accounts:
        found = flow.spam_rescue(account, spam_folder)
        spam['rescued'] += found['rescued']
        spam['unverified'] += found['unverified']

    recent = 'account IN (' + slots + ') AND created>=?'
    stamp = day_ago.strftime('%Y-%m-%d %H:%M:%S')
    codes = [r[0] for r in db.execute('SELECT sender FROM messages WHERE sensitive=1 AND ' + recent + ' ORDER BY id', [*accounts, stamp])]
    tainted = [dict(r) for r in db.execute('SELECT id,thread_id,sender,subject,taint_reasons FROM messages WHERE tainted=1 AND ' + recent + ' ORDER BY id', [*accounts, stamp])]
    drafts = [flow._draft(r) for r in db.execute('SELECT v.* FROM wf_draft_versions v JOIN wf_draft_heads h ON h.version_id=v.id JOIN threads t ON t.id=v.thread_id WHERE t.account IN (' + slots + ') ORDER BY v.id', accounts)]

    def label(i):
        return '%s — %s' % (i['who'], i['subject'])
    text = []
    for p in overdue:
        text.append(words['mine' if p['owner'] == 'me' else 'theirs'].format(title=p['title'], due=p['due_at'][:10]))
    if your_turn:
        text.append(words['turn'].format(n=len(your_turn), items=_join([label(i) for i in your_turn], words)))
    if spam['rescued']:
        text.append(words['spam'].format(n=len(spam['rescued']), items=_join(['%s — %s' % (_who(r['sender']), r['subject']) for r in spam['rescued']], words)))
    if spam['unverified']:
        text.append(words['unverified'].format(n=len(spam['unverified'])))
    if tainted:
        text.append(words['tainted'].format(n=len(tainted), items=_join(['%s — %s' % (_who(t['sender']), t['subject']) for t in tainted], words)))
    if codes:
        text.append(words['codes'].format(n=len(codes), items=', '.join(sorted(set(c.lstrip('@') for c in codes)))))
    if waiting:
        text.append(words['waiting'].format(n=len(waiting), items=_join(['%s (%d d)' % (label(i), i['days']) for i in waiting], words)))
    if drafts:
        text.append(words['drafts'].format(n=len(drafts), items=_join([d['subject'] or '#%d' % d['thread_id'] for d in drafts], words)))
    return dict(as_of=now, accounts=accounts, overdue=overdue, promises=promises, your_turn=your_turn, waiting=waiting,
                spam=spam, auth_messages=len(codes), tainted=tainted, drafts=drafts,
                quiet=not text, text=text or [words['quiet']])
