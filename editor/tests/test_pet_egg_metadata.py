import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import reborn
from modules.common import CatalogService
class EggMetadataTests(unittest.TestCase):
 def setUp(self):
  self.cat={'species':{'1':{'egg_item_id':10150,'display_name':'Rock Elf'}},'profiles':[{'species':1,'aptitude':x} for x in [1,2,3,4,5,7,8,9,10,12,14]],'items':{'10150':{'name':'Rock Elf Egg','kind':'consume item','overlap':1}}}
 def test_native_quality_is_aptitude_not_equipment_colour(self):
  egg=reborn.pet_egg_info(self.cat,10150)
  self.assertIn(10,egg['aptitudes']);self.assertNotIn(6,egg['aptitudes'])
  labels=CatalogService().file('labels')['pet_aptitudes']
  self.assertEqual(labels['10']['name'],'Brave');self.assertEqual(labels['10']['color'],'GREEN_TEXTCOLOR')
 def test_new_egg_is_hatchable_and_tradable(self):
  rb=reborn.Reborn.__new__(reborn.Reborn);rb.cat=lambda:self.cat;rb.client=lambda:{'items':{}}
  meta=rb.item_meta(10150)
  self.assertEqual(meta['defaults'],{'item_quality':1,'item_grade':1,'bound':0})
  self.assertEqual(meta['pet_egg']['species'],1)
 def test_ordinary_item_has_no_pet_aptitude_mapping(self):self.assertIsNone(reborn.pet_egg_info(self.cat,4000))
if __name__=='__main__':unittest.main(verbosity=2)
