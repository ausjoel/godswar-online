import sys, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules.common import CatalogService

class DescriptionSearchTests(unittest.TestCase):
    def test_real_effects_and_base_stats_are_searchable(self):
        c = CatalogService()
        self.assertIn("mana", c.search_text("item", 2500).lower())
        self.assertNotIn("mana", c.entry("item", 2500)["name"].lower())
        self.assertIn("mana", c.search_text("petskill", 500).lower())
        self.assertIn("physical damage", c.search_text("skill", 0).lower())

    def test_index_contract_and_ids_survive_for_every_catalogue(self):
        c = CatalogService()
        for typ in c.TYPES:
            ix = c.index(typ)
            self.assertEqual(ix["fields"][-1], "search_text", typ)
            self.assertTrue(all(len(row) == len(ix["fields"]) for row in ix["rows"]), typ)
            self.assertEqual(len({r[0] for r in ix["rows"]}), ix["count"], typ)
            self.assertIs(ix, c.index(typ))
        rows = {r[0]: r for r in c.index("item")["rows"]}
        self.assertTrue({14228, 14229}.issubset(rows))

if __name__ == "__main__": unittest.main(verbosity=2)
