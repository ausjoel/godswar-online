// Colour-tag helpers of the NPC greeting editor (static/modules/npc_dialogue.js, GW.colourTags). Pure string checks; no DOM.
// Run: node tests/test_npc_colour_tags_ui.cjs
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
const GW={h:()=>({}),module(){}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/npc_dialogue.js'),'utf8'),{window:{GW},console});
const CT=GW.colourTags,J=x=>JSON.parse(JSON.stringify(x));
assert.equal(CT.tag('#39d8b8'),'|cff39D8B8');assert.equal(CT.tag('FF39d8b8'),'|cff39D8B8');assert.equal(CT.tag('|cffF14187'),'|cffF14187');
assert.throws(()=>CT.tag('red'));assert.throws(()=>CT.tag('#12345'));
const s="Before |cffebed99Cronus|cffffffff was Uranus|r.";
assert.deepEqual(J(CT.runs(s)),[{text:'Before ',color:null},{text:'Cronus',color:'#EBED99'},{text:' was Uranus',color:'#FFFFFF'},{text:'.',color:null}]);
assert.deepEqual(J([...CT.used(s)]),[['FFEBED99',1],['FFFFFFFF',1]]);
assert.equal(CT.strip(s),'Before Cronus was Uranus.');
let r=CT.wrap('Hi weapon now',3,9,'#F14187');
assert.equal(r.value,'Hi |cffF14187weapon|cffffffff now');assert.equal(r.value.slice(r.start,r.end),'weapon');
r=CT.wrap('Hi weapon',3,9,'#F14187','|r');assert.equal(r.value,'Hi |cffF14187weapon|r');
r=CT.wrap('Hi',2,2,'#00ff00');assert.equal(r.value,'Hi|cff00FF00');assert.equal(r.start,12);
r=CT.unwrap('Hi |cffF14187weapon|cffffffff now',0,33);assert.equal(r.value,'Hi weapon now');
assert.deepEqual(J(CT.runs('')),[]);assert.deepEqual(J(CT.runs('plain')),[{text:'plain',color:null}]);
console.log('PASS colour tags: tag normalising, run split with |c and |r, used-colour count, strip, wrap/insert/unwrap selection. Rendering in a browser not tested.');
