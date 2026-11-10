// Headless event tests, not visual/browser validation.
const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict'),path=require('path');
let component,resolveConfirm,calls=[];
function h(tag,props,...kids){return Object.assign({tag,children:kids.flat(Infinity).filter(x=>x!=null),value:'',append(...x){this.children.push(...x);}},props||{});}
const GW={h,fmt:String,module:(name,obj)=>component=obj,appliesChip:()=>null,clear:el=>{el.children=[];return el;},confirm:()=>new Promise(r=>resolveConfirm=r),writeDone(){},writeError:async e=>{throw e;},status(){}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/skills.js'),'utf8'),{window:{GW},console});
function setup(){
 calls=[];const ctx={api:async (route,body)=>{calls.push({route,body});return {};}};
 const d={writable:true,character:{name:'First'},titles:{admin:[],grant_support:{available:true},grant_catalogue:[{title_id:1000,display_name:'First title',description:'healing mana'}]}};
 const p=Object.assign({},component,{ctx,cid:7,data:d,online:null,reload:async()=>{}});
 const view=p.titleGrantControls(d),nodes=[];function walk(x){if(!x||typeof x!=='object')return;nodes.push(x);(x.children||[]).forEach(walk);}walk(view);
 const checkbox=nodes.find(x=>x.type==='checkbox');checkbox.checked=true;checkbox.onchange();
 return {p,ctx,d,button:nodes.find(x=>x.tag==='button'),search:nodes.find(x=>x.type==='search'),list:nodes.find(x=>x.children&&x.children[0]&&x.children[0].tag==='label')};
}
(async()=>{
 let s=setup(),pending=s.button.onclick();resolveConfirm(true);await pending;
 assert.equal(calls.length,1);assert.equal(calls[0].route,'title-grants/7');assert.equal(JSON.stringify(calls[0].body.add),'[1000]');
 for(const change of [s=>s.p.cid=8,s=>s.p.ctx={},s=>s.p.data={},s=>s.p.unmount()]){
  s=setup();pending=s.button.onclick();change(s);resolveConfirm(true);await pending;assert.equal(calls.length,0);
 }
 s=setup();pending=s.button.onclick();resolveConfirm(false);await pending;assert.equal(calls.length,0);
 s=setup();s.search.value='mana';s.search.oninput();assert.equal(s.list.children.length,1);s.search.value='absent';s.search.oninput();assert.equal(s.list.children.length,0);
 console.log('PASS title UI: captured target, changed character/context/data, unmount and cancel guards; description-search events. No browser rendering tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
