const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let component,resolveConfirm,calls=[];
const h=(tag,props,...kids)=>Object.assign({tag,value:'',children:kids.flat(Infinity).filter(x=>x!=null),append(...x){this.children.push(...x);},addEventListener(){}},props||{});
const GW={h,module:(n,p)=>component=p,clear:n=>{n.children=[];return n;},confirm:()=>new Promise(r=>resolveConfirm=r),status(){},writeDone(){},writeError:async()=>{}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/buffs.js'),'utf8'),{window:{GW},console});
function setup(removable=true){
 calls=[];const ctx={api:async(route,body)=>{calls.push({route,body});return route.startsWith('remove/')?{result:{removed:true}}:{supported:true,online:true,epoch:'b'.repeat(32),buffs:[]};}};
 const d={character:{id:7,name:'PortalAth'},writable:true,catalogue:{supported:true,maxDurationSeconds:4294967295,buffs:[{skillId:774,name:'Gaia',description:'dodge',rank:5,durationSeconds:10,effects:{dodge:3000}}]}};
 const snapshot={supported:true,online:true,epoch:'a'.repeat(32),buffs:[{kind:34,statusId:274,name:'Gaia',revision:'9223372036854775807',remainingSeconds:10,removable}]};
 const p=Object.assign({},component,{ctx,cid:7,data:d,online:{online:true},body:h('div'),activeSnapshot:snapshot,activeRequest:0});p.render();
 const nodes=[];function walk(x){if(!x||typeof x!=='object')return;nodes.push(x);(x.children||[]).forEach(walk);}walk(p.body);
 return {p,ctx,d,snapshot,button:nodes.find(n=>n.tag==='button'&&n.children.includes('Remove'))};
}
(async()=>{
 let s=setup(),pending=s.button.onclick();resolveConfirm(true);await pending;
 assert.equal(calls[0].route,'remove/7');assert.equal(calls[0].body.expected.revision,'9223372036854775807');assert.equal(calls[0].body.expected.epoch,'a'.repeat(32));assert.equal(calls[1].route,'active/7');
 for(const change of [s=>s.p.cid=8,s=>s.p.ctx={},s=>s.p.data={},s=>s.p.activeSnapshot={},s=>s.p.unmount()]){
  s=setup();pending=s.button.onclick();change(s);resolveConfirm(true);await pending;assert.equal(calls.length,0);
 }
 s=setup(false);assert.equal(s.button.disabled,true);
 s=setup();const completions=[];s.ctx.api=()=>new Promise(r=>completions.push(r));
 const first=s.p.refreshActive(),second=s.p.refreshActive();completions[1]({supported:true,online:true,epoch:'new',buffs:[]});await second;completions[0]({supported:true,online:true,epoch:'old',buffs:[]});await first;assert.equal(s.p.activeSnapshot.epoch,'new');
 console.log('PASS active-buff UI: exact revision/target, stale target/context/data/snapshot/unmount guards, protected effects, and out-of-order refresh. Rendering not tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
