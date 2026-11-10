const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let component,resolveConfirm,calls=[];
const h=(tag,props,...kids)=>Object.assign({tag,value:'',children:kids.flat(Infinity).filter(x=>x!=null),append(...x){this.children.push(...x);},addEventListener(){}},props||{});
const GW={h,module:(n,p)=>component=p,clear:n=>{n.children=[];return n;},confirm:()=>new Promise(r=>resolveConfirm=r),status(){},writeDone(){},writeError:async e=>{throw e;}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/buffs.js'),'utf8'),{window:{GW},console});
function setup(online=true){
 calls=[];const ctx={api:async (route,body)=>{calls.push({route,body});return {result:{applied:true}};}};
 const d={character:{id:7,name:'PortalAth'},writable:true,catalogue:{supported:true,maxDurationSeconds:4294967295,buffs:[{skillId:774,name:'Gaia Care 5',description:'avoid painful damage',skill_description:'',status_name:'Hermes',rank:5,durationSeconds:10,effects:{dodge:3000,criticalResistance:1000}}]}};
 const p=Object.assign({},component,{ctx,cid:7,data:d,online:{online},body:h('div'),activeRequest:0});p.render();
 const nodes=[];function walk(x){if(!x||typeof x!=='object')return;nodes.push(x);(x.children||[]).forEach(walk);}walk(p.body);
 return {p,ctx,d,button:nodes.find(x=>x.tag==='button'),duration:nodes.find(x=>x.type==='number'),search:nodes.find(x=>x.type==='search'),select:nodes.find(x=>x.tag==='select')};
}
(async()=>{
 let s=setup(),pending=s.button.onclick();resolveConfirm(true);await pending;assert.equal(calls.length,2);assert.equal(calls[0].route,'apply/7');assert.equal(calls[1].route,'active/7');assert.equal(JSON.stringify(calls[0].body),' {"skill_id":774,"duration_seconds":10}'.trim());
 for(const change of [s=>s.p.cid=8,s=>s.p.ctx={},s=>s.p.data={},s=>s.p.unmount()]){
  s=setup();pending=s.button.onclick();change(s);resolveConfirm(true);await pending;assert.equal(calls.length,0);
 }
 s=setup();pending=s.button.onclick();resolveConfirm(false);await pending;assert.equal(calls.length,0);
 s=setup();s.duration.value='1.5';await s.button.onclick();assert.equal(calls.length,0);
 s=setup();s.duration.value='4294967295';pending=s.button.onclick();resolveConfirm(true);await pending;assert.equal(calls[0].body.duration_seconds,4294967295);
 s=setup(false);assert.equal(s.button.disabled,true);
 s=setup();s.search.value='painful';s.search.oninput();assert.equal(s.select.children.length,1);s.search.value='missing';s.search.oninput();assert.equal(s.select.children.length,0);assert.equal(s.button.disabled,true);
 console.log('PASS buff UI event checks: exact target/payload, stale target/context/data/unmount/cancel guards, duration validation, offline state and description search. Rendering not tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
