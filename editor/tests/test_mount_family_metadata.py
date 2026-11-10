import sys,json,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT));sys.path.insert(0,str(ROOT.parent))
from modules.common import CatalogService,INDEX_FIELDS
from build_mount_families import build,DEFAULT_SOURCE
class MetadataTests(unittest.TestCase):
 def test_registry_matches_reviewed_source(self):
  items=json.loads((ROOT/'catalog-origin/items.json').read_text());actual=build(DEFAULT_SOURCE,items);saved=json.loads((ROOT/'catalog-origin/mount-families.json').read_text());self.assertEqual(actual,saved);self.assertEqual(len(actual['families']),31)
  self.assertEqual(actual['excluded'],[])
 def test_ids_are_unique_complete_and_clientside(self):
  d=json.loads((ROOT/'catalog-origin/mount-families.json').read_text());ids=[v['id'] for f in d['families'] for v in f['variants']];self.assertEqual(len(ids),310);self.assertEqual(len(set(ids)),310)
 def test_index_keeps_all_ids_and_adds_family_metadata(self):
  ix=CatalogService().index('item');self.assertEqual(len(ix['families']),31);self.assertEqual(ix['count'],len(ix['rows']));ids={r[0] for r in ix['rows']};self.assertTrue({14220,14229,6000,16200}.issubset(ids))
 def test_blank_sprite_fallback_reaches_index(self):
  ix=CatalogService().index('item');sprite=INDEX_FIELDS.index('sprite');rows={r[0]:r for r in ix['rows']}
  for id in [4417,4425,4430]:self.assertIsNone(rows[id][sprite])
if __name__=='__main__':unittest.main(verbosity=2)
