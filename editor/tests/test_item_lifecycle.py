"""E2 reviewed item metadata and editor-only display policy."""
import csv
from collections import Counter
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, _item_lifecycle as lifecycle


class ItemLifecycleTests(unittest.TestCase):
    def test_vendored_audit_matches_handoff_and_exact_status_counts(self):
        original = Path(__file__).resolve().parents[2] / 'docs/handoff/dead-items-2026-11-05.csv'
        self.assertEqual(original.read_bytes(), (lifecycle.DATA / 'dead-items.csv').read_bytes())
        rows = lifecycle.reviewed()
        self.assertEqual(len(rows), 745)
        self.assertEqual(Counter(r['status'] for r in rows.values()),
                         {'defunct': 143, 'no-system': 581, 'remove': 21})
        self.assertEqual(set(lifecycle.translations()), {k for k, r in rows.items() if r['status'] == 'remove'})

    def test_index_preserves_every_item_id_and_adds_named_metadata(self):
        cat = common.CatalogService()
        ix = cat.index('item')
        rows = {str(r[0]): dict(zip(ix['fields'], r)) for r in ix['rows']}
        client = cat.file('items-index')
        id_col = client['fields'].index('id')
        self.assertTrue({str(r[id_col]) for r in client['rows']}.issubset(rows))
        self.assertEqual(Counter(r['dead_status'] for r in rows.values() if r['dead_status']),
                         {'defunct': 143, 'no-system': 581, 'remove': 21})
        self.assertTrue(all(len(r) == len(ix['fields']) for r in ix['rows']))
        self.assertEqual(ix['fields'][-1], 'search_text')
        for key in lifecycle.translations():
            row = rows[key]
            self.assertTrue(row['generated_translation'])
            self.assertIn('GENERATED', row['sub'])
            self.assertNotIn('cn', row['flags'].split())
            self.assertTrue(row['name'].isascii() or key in ('4477', '4478'))

    def test_translated_entry_keeps_original_and_does_not_modify_client(self):
        cat = common.CatalogService()
        for key, translated in lifecycle.translations().items():
            with self.subTest(item=key):
                before = json.dumps(cat.file('items')[key], sort_keys=True)
                e = cat.entry('item', key)
                self.assertEqual(e['name'], translated['name'])
                self.assertEqual(e['desc'], translated['desc'])
                self.assertEqual(e['original_name'], cat.file('items')[key]['name'])
                self.assertEqual(e['original_desc'], cat.file('items')[key]['desc'])
                self.assertEqual(before, json.dumps(cat.file('items')[key], sort_keys=True))
                self.assertEqual(e['dead_status'], 'remove')
                self.assertTrue(e['generated_translation'])

    def test_translations_and_original_descriptions_are_searchable(self):
        cat = common.CatalogService()
        for key, query in [('3903', 'edifier housing'), ('3911', 'usb'), ('4477', 'facebook'), ('4480', '5,000')]:
            with self.subTest(item=key):
                self.assertTrue(cat.matches('item', key, query))
        self.assertTrue(cat.matches('item', 3903, cat.file('items')['3903']['name']))
        e = cat.entry('item', 1001)
        self.assertFalse(e['generated_translation'])
        self.assertEqual(e['name'], cat.file('items')['1001']['name'])


if __name__ == '__main__':
    unittest.main()
