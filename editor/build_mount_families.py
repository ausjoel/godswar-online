"""Extract explicit tier families from the reviewed server catalog; never infer families from shared icons."""
import hashlib,json,re
from pathlib import Path
HERE=Path(__file__).resolve().parent
DEFAULT_SOURCE=HERE.parents[2]/'GodsWar-2.51/Polish/reborn-src-v2/src/Godswar.Server/State/DeveloperMountCatalog.cs'
TIERS=['40','50','60','70','80','90','100','110','max','special']
def build(source,items):
 raw=Path(source).read_bytes();text=raw.decode()
 helper=text.split('private void AddTieredFamily(',1)[1].split('private void AddFamily(',1)[0]
 tiers=re.search(r'\[([^]]+)\]\);',helper)
 if not re.search(r'Enumerable\.Range\(0,\s*10\)',helper) or not tiers or re.findall(r'"([^"]+)"',tiers.group(1))!=TIERS:raise ValueError('Server tier layout changed; review extraction before regenerating')
 found=re.findall(r'AddTieredFamily\(\s*families,\s*assignedIds,\s*(\d+),\s*"([^"]+)",\s*"([^"]+)"',text)
 if not found:raise ValueError('No explicit server families found; source format needs review')
 families=[];excluded=[];seen=set()
 for base,alias,name in found:
  ids=list(range(int(base),int(base)+10))
  if any(str(i) not in items or items[str(i)].get('kind')!='mount' for i in ids):
   excluded.append(dict(alias=alias,reason='One or more IDs lack a client mount record',ids=ids));continue
  if any(i in seen for i in ids):raise ValueError('Server families overlap')
  seen.update(ids)
  families.append(dict(id=alias,name=name,variants=[dict(id=i,tier=tier) for i,tier in zip(ids,TIERS)]))
 return dict(schema=1,source='src/Godswar.Server/State/DeveloperMountCatalog.cs',source_sha256=hashlib.sha256(raw).hexdigest(),families=families,excluded=excluded)
if __name__=='__main__':
 import argparse
 p=argparse.ArgumentParser();p.add_argument('--source',type=Path,default=DEFAULT_SOURCE);a=p.parse_args()
 items=json.loads((HERE/'catalog-origin/items.json').read_text());result=build(a.source,items);(HERE/'catalog-origin/mount-families.json').write_text(json.dumps(result,indent=2));print('Reviewed mount families:',len(result['families']),'Grouped IDs:',sum(len(f['variants']) for f in result['families']),'Excluded families:',len(result['excluded']))
