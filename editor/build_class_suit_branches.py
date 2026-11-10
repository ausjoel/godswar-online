"""Freeze the explicit server conversion catalogue for editor-only deployments."""
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'server/src/Godswar.Server/State/ClassSuitConversionCatalog.cs'


def generate():
    source = SOURCE.read_text()
    table = source.split('private static List<ClassSuitConversionBranch> CreateBranches()',1)[1].split('return branches;',1)[0]
    rows = []
    for key, common, first, cost, profession in re.findall(r'AddFamily\(branches, "([^"]+)", (\d+), (\d+), (\d+), profession: (\d)\);', table):
        rows.append(dict(key=f'{key}-p{profession}',profession=int(profession),common=[int(common)],tiers=list(range(int(first),int(first)+4))))
    for key, common, first, cost in re.findall(r'AddProfessionFamily\(branches, "([^"]+)", \[([\d, ]+)\], (\d+), (\d+)\);', table):
        for p in range(4):
            start = int(first)+p*10
            rows.append(dict(key=f'{key}-p{p}',profession=p,common=[int(x) for x in common.split(',')],tiers=list(range(start,start+4))))
    # Explicit exceptional ring layout from CreateBranches; verify source before freezing.
    assert '[targetBase, targetBase + 1, targetBase + 2, targetBase + 6]' in table
    assert '[targetBase + 3, targetBase + 4, targetBase + 5, targetBase + 7]' in table
    for p in range(4):
        b=3230+p*10
        for key, common, offsets in [('hp',3209,[0,1,2,6]),('mp',3210,[3,4,5,7])]:
            rows.append(dict(key=f'ring-{key}-p{p}',profession=p,common=[common],tiers=[b+n for n in offsets]))
    native = ROOT / 'server/src/Godswar.Server/State/ClassSuitItemCatalog.Generated.cs'
    ids={int(x) for x in re.findall(r'\b(\d+)u\b', native.read_text())}
    generated=[i for r in rows for i in r['tiers']]
    assert len(rows)==62 and len(set(generated))==248 and set(generated)==ids
    return {'source':'server/src/Godswar.Server/State/ClassSuitConversionCatalog.cs',
            'source_sha256':hashlib.sha256(SOURCE.read_bytes()).hexdigest(),'branches':rows}


if __name__ == '__main__':
    (ROOT/'editor/data/class-suit-branches.json').write_text(json.dumps(generate(),indent=2)+'\n')
