"""Register coding API resources and refresh their non-secret balance snapshot."""
import argparse
import math
import importlib.util
import json
import os
from pathlib import Path
import sys


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(obj)
    return obj


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--tools', type=Path, required=True)
    p.add_argument('--env', required=True)
    p.add_argument('--funded-usd', type=float, help='Verified total funded account credit in USD; omit if unknown')
    p.add_argument('--apply', action='store_true')
    args = p.parse_args()
    if os.environ.get('AIWB_JOB_ROLE') == 'member':
        raise RuntimeError('own_device_first')
    if args.funded_usd is not None and (not math.isfinite(args.funded_usd) or args.funded_usd <= 0):
        raise ValueError('funded USD must be positive')
    client = load('coding', args.tools / 'aiwb-modelbench/coding.py').DeepSeekClient()
    models = client.models().get('data', [])
    balance = client.balance()
    usd = next((b for b in balance.get('balance_infos', []) if b['currency'] == 'USD'), None)
    if usd is None:
        raise RuntimeError('No USD balance in API response')
    ledger = Path(os.environ.get('LOCALAPPDATA') or Path.home()) / 'AIWorkbench/api-subtasks/ledger'
    tokens = {'inputTokens': 0, 'outputTokens': 0}
    for path in ledger.glob('*.json'):
        usage = json.loads(path.read_text(encoding='utf-8')).get('usage', {})
        tokens['inputTokens'] += usage.get('prompt_tokens', 0)
        tokens['outputTokens'] += usage.get('completion_tokens', 0)
    usage = {'currency': 'USD', 'remaining': float(usd['total_balance']), 'funded': args.funded_usd,
             'available': balance.get('is_available') is True, **tokens}
    if args.funded_usd is None:
        del usage['funded']
    rows = []
    for model in models:
        if model['id'] not in ('deepseek-flash', 'deepseek-v4-pro'):
            continue
        rows.append({'op': 'model_register', 'id': args.env + '/' + model['id'], 'env': args.env,
            'kind': 'api', 'title': 'DeepSeek ' + model['id'].removeprefix('deepseek-'), 'agent': 'tool',
            'host': '', 'models': [{'id': model['id'], 'label': model['id']}], 'defaultModel': model['id'],
            'slaveAllowed': False, 'confidential': False,
            'execution': {'tool': 'aiwb-modelbench', 'taskType': 'code'}, 'usage': usage,
            'card': {'summary': 'Paid API coding helper on the owning device.', 'strengths': 'Implementation, tests, code review',
                'limits': 'Coding only. Sanitized prompts only. No private mail, secrets, devices or cross-environment access.',
                'dataStaysIn': 'DeepSeek API', 'notes': 'aiwb-subtask spawn --resource ' + args.env + '/' + model['id'] + ' --task-type code'}})
    if args.apply:
        subtask = load('aiwb_subtask', args.tools / 'aiwb-subtask/aiwb-subtask.py')
        for row in rows:
            result = subtask.db_api(row)
            if result.get('ok') is not True:
                raise RuntimeError('Resource registration failed: ' + str(result.get('error')))
    print(json.dumps({'applied': args.apply, 'resources': rows}, indent=2))


if __name__ == '__main__':
    main()
