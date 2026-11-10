"""Editor-only reviewed item status and generated promotion translations.

The vendored audit is an exact copy of docs/handoff/dead-items-2026-11-05.csv.
Keep IDs, source client catalogues and server templates unchanged.
"""
import csv
from functools import lru_cache
import json
from pathlib import Path

DATA = Path(__file__).resolve().parents[1] / 'data'
STATUS_LABELS = {'defunct': 'Defunct', 'no-system': 'No system', 'remove': 'Removed promotion'}


@lru_cache(maxsize=1)
def reviewed():
    with (DATA / 'dead-items.csv').open(encoding='utf-8', newline='') as source:
        rows = list(csv.DictReader(source))
    result = {}
    for row in rows:
        if row['status'] not in STATUS_LABELS or row['id'] in result:
            raise ValueError('invalid or duplicate reviewed item status')
        result[row['id']] = row
    return result


@lru_cache(maxsize=1)
def translations():
    return json.loads((DATA / 'promo-translations.json').read_text(encoding='utf-8'))['items']


def metadata(item_id):
    row = reviewed().get(str(item_id), {})
    return {'dead_status': row.get('status', ''), 'dead_group': row.get('group_title', ''),
            'generated_translation': str(item_id) in translations()}


def display(entry, item_id):
    out = dict(entry)
    out.update(metadata(item_id))
    translation = translations().get(str(item_id))
    if translation:
        out['original_name'] = entry.get('name', '')
        out['original_desc'] = entry.get('desc', '')
        out['name'] = translation['name']
        out['desc'] = translation['desc']
        out['name_cn'] = False
    return out
