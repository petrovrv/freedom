import json
import queue
import subprocess
import threading
import time


def catalog(codex, cwd, timeout=30):
    """Read the authenticated CLI catalog without starting an agent turn."""
    proc = subprocess.Popen([codex, 'app-server'], cwd=str(cwd), stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            text=True, encoding='utf-8', errors='replace')
    messages = queue.Queue()
    def read():
        for line in proc.stdout:
            messages.put(line)
        messages.put(None)
    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    deadline = time.monotonic() + timeout
    def send(value):
        proc.stdin.write(json.dumps(value) + '\n')
        proc.stdin.flush()
    def request(number, method, params):
        send(dict(id=number, method=method, params=params))
        while True:
            try:
                line = messages.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty:
                raise RuntimeError('Codex model catalog timed out') from None
            if line is None:
                raise RuntimeError('Codex model catalog closed unexpectedly')
            value = json.loads(line)
            if value.get('id') != number:
                continue
            if 'error' in value:
                raise RuntimeError('Codex model catalog request failed')
            return value['result']
    try:
        request(1, 'initialize', {'clientInfo': {'name': 'aiwb_devtree', 'version': '0.7.0'}})
        send({'method': 'initialized', 'params': {}})
        rows, cursor, seen, number = [], None, set(), 2
        while True:
            params = {'limit': 100, 'includeHidden': False}
            if cursor:
                params['cursor'] = cursor
            page = request(number, 'model/list', params)
            rows.extend(page.get('data', []))
            cursor = page.get('nextCursor')
            if not cursor:
                return rows
            if cursor in seen:
                raise RuntimeError('Codex model catalog repeated a cursor')
            seen.add(cursor)
            number += 1
    finally:
        try:
            proc.stdin.close()
        except OSError:
            pass
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.terminate()  # Only this metadata child, never another job.
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        reader.join(timeout=1)
        proc.stdout.close()


def choose(rows, preferred=None, excluded=()):
    available = [r for r in rows if r.get('model') and not r.get('hidden') and r['model'] not in excluded]
    if not available:
        raise RuntimeError('No available Codex review model in catalog')
    selected = next((r for r in available if r['model'] == preferred), None)
    selected = selected or next((r for r in available if r.get('isDefault')), available[0])
    efforts = [e.get('reasoningEffort') for e in selected.get('supportedReasoningEfforts', [])]
    effort = 'medium' if 'medium' in efforts else selected.get('defaultReasoningEffort')
    if effort not in efforts:
        effort = next((e for e in efforts if e), None)
    return selected['model'], effort


def run_review(codex, cwd, preferred, command, prompt, timeout, report):
    excluded = set()
    for attempt in range(2):
        model, effort = choose(catalog(codex, cwd), preferred, excluded)
        report(f'review: selected Codex model {model}' + (f' (requested {preferred})' if preferred and model != preferred else ''))
        proc = subprocess.run(command(model, effort), cwd=str(cwd), input=prompt, capture_output=True,
                              text=True, encoding='utf-8', errors='replace', timeout=timeout)
        if proc.returncode == 0 or 'model_not_found' not in ((proc.stderr or '') + (proc.stdout or '')).lower():
            return proc
        if attempt == 0:
            excluded.add(model)
            report('review: model_not_found; refreshing catalog and retrying once')
    return proc
