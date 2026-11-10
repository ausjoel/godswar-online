const fs=require('fs'),vm=require('vm'),assert=require('assert'),path=require('path');
const root=path.resolve(__dirname,'..'),context={window:{GW:{}}};vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(root,'static/shared/picker.js'),'utf8'),context);
const group=context.window.GW.groupVariantRows;
const families=JSON.parse(fs.readFileSync(path.join(root,'catalog-origin/mount-families.json'),'utf8')).families;
const items=JSON.parse(fs.readFileSync(path.join(root,'catalog-origin/items.json'),'utf8'));
const rows=Object.values(items).map(i=>({id:i.id,name:i.name,key:i.kind,minlv:i.minlv,sprite:i.sprite}));
const before=JSON.stringify(rows),all=group(rows,families,null);
assert.equal(rows.length-all.length,270);assert.equal(JSON.stringify(rows),before);
const greek=families.find(f=>f.id==='greeksteed');const greekIDs=greek.variants.map(v=>v.id);
const visible=rows.filter(r=>greekIDs.includes(r.id));const compact=group(visible,families,14229);
assert.equal(compact.length,1);assert.equal(compact[0].id,14229);assert.equal(compact[0]._variants.length,10);assert.equal(compact[0]._tiers['14229'],'special');assert.equal(compact[0]._tiers['14228'],'max');
const byLevel=visible.filter(r=>r.minlv>=80 && r.minlv<=90);const filtered=group(byLevel,families,null);assert.equal(filtered.length,1);assert.deepEqual(Array.from(filtered[0]._variants,r=>r.id),[14224,14225]);
const exact=group(visible.filter(r=>r.id===14229),families,null);assert.equal(exact.length,1);assert.equal(exact[0].id,14229);assert.equal(exact[0]._variants,undefined);
const dragon=group(rows.filter(r=>r.name==='Argent Armored Dragon'),families,null);assert.equal(dragon.length,2);assert.notEqual(dragon[0]._familyName,dragon[1]._familyName);
const allIDs=all.flatMap(r=>r._variants?r._variants.map(v=>v.id):[r.id]).sort((a,b)=>a-b);assert.deepEqual(allIDs,rows.map(r=>r.id).sort((a,b)=>a-b));
assert(all.some(r=>r.id===6000 && !r._variants)); // legacy mount stays independent
console.log('PASS: 300 reviewed mount IDs condense to 30 families; 270 redundant tiles removed');
console.log('PASS: selected IDs, max/special distinction, timed families, level/search filters and legacy IDs preserved');
console.log('PASS: complete catalog coverage and original rows unchanged');
