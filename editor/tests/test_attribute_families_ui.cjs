const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),cp=require('node:child_process');
const path=require('node:path'), root=path.resolve(__dirname,'../..');
const attrs=JSON.parse(cp.execFileSync('python3',['-c',`import sys,json
sys.path.insert(0,'editor')
from modules import _attribute_families as A
x=json.load(open('editor/catalog-origin/appendattrs.json'))
for k,a in x.items():
 a.update(values=a['levels'],percent=bool(a['flag']),family_policy=A.policy(int(k)),dedicated=A.policy(int(k))['group']=='class')
print(json.dumps(x))`],{cwd:root,encoding:'utf8'}));
const GW={fmt:n=>String(n)},context={window:{GW},Map,Number};
vm.runInNewContext(fs.readFileSync(path.join(root,'editor/static/shared/attribute-families.js'),'utf8'),context);
const P=GW.attributeFamilies;
const choices=P.choices(attrs,new Set(),false);
assert.equal(choices.length,39); // 21 ordinary + 16 mount + 2 unmapped; eight dedicated class entries excluded
assert.equal(choices.filter(a=>a.family_policy.family==='9930').length,1);
assert.equal(choices.find(a=>a.family_policy.family==='9930').id,4);
assert.equal(choices.find(a=>a.family_policy.family==='9974').id,347);
assert.ok(!choices.some(a=>a.id===0 || a.id===1 || a.id===200 || a.id===220));
assert.equal(P.choices(attrs,new Set([0,1,40,200]),true).find(a=>a.family_policy.family==='9930').id,4);
assert.equal(P.format(attrs['4'],1),'+57');
assert.equal(P.format(attrs['4'],10),'+345');
assert.equal(P.format(attrs['4'],25),'+1636');
assert.equal(P.format(attrs['367'],10),'+1.84%');
assert.equal(P.format(attrs['377'],10),'+2.21%');
assert.equal(P.format(attrs['40'],10),'+31');
assert.equal(P.levelFor(attrs['0'],0,null),null);
assert.equal(P.levelFor(attrs['1'],1,2),2);
assert.equal(P.levelFor(attrs['4'],0,null),5);
assert.equal(P.levelFor(attrs['40'],0,1),null);
assert.equal(P.levelFor(attrs['40'],40,2),2);
assert.equal(P.levelFor(attrs['347'],340,1),null);
const unknown={900:{id:900,values:[11]},901:{id:901,values:[22]}};
assert.equal(P.choices(unknown,new Set(),false).length,2);
assert.equal(P.format(null,10),'?');
assert.equal(P.amount(attrs['4'],0),57);
assert.equal(P.amount(attrs['4'],99),1636);
console.log('PASS native maximum-family choices, item roll filtering, class separation, unknown identity, grade/percent values and exact old-level preservation.');
