"""Compatibility API searches must find effect-only matches without broadening equipment filters."""
import copy
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reborn
from modules.common import CatalogService

class CompatibilityDescriptionSearchTests(unittest.TestCase):
    def setUp(self):
        self.catalog = CatalogService()
        self.rb = reborn.Reborn.__new__(reborn.Reborn)

    def test_native_description_and_stat_labels_without_name_match(self):
        name = self.catalog.entry('item', 2500)['name']
        self.assertNotIn('mana', name.lower())
        self.assertTrue(self.catalog.matches('item', 2500, '  MaNa  ', name))
        self.assertTrue(self.catalog.matches('item', 2500, '2500 mana', name))
        self.assertFalse(self.catalog.matches('item', 2500, 'mana unmatchedword', name))
        self.assertTrue(self.catalog.matches('petskill', 500, 'mana'))
        self.assertTrue(self.catalog.matches('skill', 0, 'physical damage'))

    def test_native_quest_details_objectives_and_completion_are_searchable(self):
        entry = self.catalog.entry('quest', 0)
        self.assertNotIn('mischievous', entry.get('title', '').lower())
        for word in ['mischievous', 'auto-track', 'monsters']:
            self.assertTrue(self.catalog.matches('quest', 0, word), word)
        self.assertEqual(self.rb.catalog_search('quest', [entry], '0 mischievous'), [entry])

    def test_numeric_id_prefix_and_server_only_description(self):
        self.assertTrue(self.catalog.matches('item', 2500, '25'))
        self.assertFalse(self.catalog.matches('item', 2500, '500'))
        self.assertTrue(self.catalog.matches('item', 9999999, 'restores vigor', 'Restores vigor over time'))
        self.assertFalse(self.catalog.matches('item', 9999999, 'mana', 'Restores vigor over time'))

    def test_compat_items_keep_kind_ring_slot_limit_and_metadata(self):
        rows = {
            2500: dict(id=2500, name='Obscure Charm', kind='head', slot=0, minlv=1, desc=''),
            900001: dict(id=900001, name='Quiet Ring', kind='ring', slot=9, minlv=2, desc='Restores mana'),
            900002: dict(id=900002, name='Mana Crystal', kind='consume item', slot=-1, minlv=3, desc='Mana')}
        server = {'items': {str(k): {} for k in rows}}
        self.rb.cat = lambda: server
        self.rb.item_meta = lambda k: rows[k]
        before = copy.deepcopy(rows)
        self.assertEqual([r['id'] for r in self.rb.item_search('mana', limit=10, kind='head')], [2500])
        self.assertEqual([r['id'] for r in self.rb.item_search('mana', limit=10, slot=8)], [900001])
        self.assertEqual([r['id'] for r in self.rb.item_search('mana', limit=10, slot=0)], [2500])
        self.assertEqual([r['id'] for r in self.rb.item_search('mana', limit=1)], [900002])
        self.assertEqual([r['id'] for r in self.rb.item_search('25')], [2500])
        self.assertEqual(rows, before)

    def test_compat_skills_and_pet_skills_use_native_effect_text(self):
        skills = [dict(id=0, name='Light Chop', extra='retained')]
        self.assertEqual(self.rb.catalog_search('skill', skills, 'physical damage'), skills)
        self.assertEqual(self.rb.catalog_search('skill', skills, 'impossibleword'), [])
        petskills = [dict(id=500, name='Hidden Skill', level=1)]
        self.assertEqual(self.rb.catalog_search('petskill', petskills, 'mana'), petskills)

    def test_server_names_titles_order_and_limits_survive(self):
        rows = [dict(id=990003, title='Mystery Goal', desc='Collect amber shards'),
                dict(id=990001, title='Another Goal', desc='Collect amber stones')]
        before = copy.deepcopy(rows)
        self.assertEqual([r['id'] for r in self.rb.catalog_search('quest', rows, 'collect amber', limit=1)], [990001])
        self.assertEqual([r['id'] for r in self.rb.catalog_search('quest', rows, 'mystery')], [990003])
        self.assertEqual([r['id'] for r in self.rb.catalog_search('quest', rows, '99000')], [990001, 990003])
        self.assertEqual(rows, before)

if __name__ == '__main__': unittest.main(verbosity=2)
