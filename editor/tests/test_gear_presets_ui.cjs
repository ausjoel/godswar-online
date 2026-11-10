const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
class El {
  constructor(tag,attrs={}){this.tag=tag;this.children=[];this.events={};this.style={};this.value='';this.checked=false;Object.assign(this,attrs);}
  append(...kids){for(const k of kids.flat(Infinity))if(k!=null&&k!==false){this.children.push(k);if(this.tag==='select'&&this.value==='')this.value=k.value;}}
  addEventListener(k,f){(this.events[k]||=[]).push(f);}
  fire(k){for(const f of this.events[k]||[])f({target:this});}
}
const h=(tag,attrs,...kids)=>{const e=new El(tag,attrs||{});e.append(...kids);return e;};
const all=e=>e instanceof El?[e,...e.children.flatMap(all)]:[];
let changeListener=null;
const GW={h,on:(_,f)=>{changeListener=f;return ()=>{if(changeListener===f)changeListener=null;};},fmt:String,clear:e=>{e.children=[];return e;},onlineBanner:()=>h('div'),writeDone(){},writeError:async()=>{},confirm:async()=>true,module:(name,m)=>{GW.moduleImpl=m;}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/gear_presets.js'),'utf8'),{window:{GW}});
function data(options={},writable=true){return {character:{name:'Copy',class:'Priest'},options,token:'reviewed',writable,skipped:[],items:[{
  slot_name:'Weapon',name:'Wand',changed:true,before:{prop_id:1,item_quality:8,item_grade:10},after:{prop_id:2,item_quality:20,item_grade:25},
  stats:[{name:'Magic attack',value:123,level:null,percent:false}],stones:[{name:'Fire Spirit',value:500,percent:true}],warnings:[]}]};}
(async()=>{
 const calls=[],root=h('div');
 const ctx={cid:4,api:async(route,body)=>{calls.push({route,body});return route.startsWith('preview')?data(body):{ok:true};}};
 await GW.moduleImpl.mount(root,ctx);
 const button=name=>all(root).find(e=>e.tag==='button'&&e.children.includes(name));
 const apply=button('Apply preset'),preview=button('Preview all worn gear');
 assert.equal(apply.disabled,false);
 assert.equal(calls[0].body.preset,'standard');
 const preset=all(root).find(e=>e['aria-label']==='Gear preset');
 preset.value='boundless';preset.fire('change');assert.equal(apply.disabled,true);
 await preview.onclick();assert.equal(apply.disabled,false);
 await apply.onclick();
 const write=calls.find(c=>c.route==='apply/4');assert.ok(write);assert.equal(write.body.token,'reviewed');assert.equal(write.body.preset,'boundless');
 assert.equal(calls.at(-1).route,'preview/4');
 const previousCalls=calls.length;changeListener();await Promise.resolve();await Promise.resolve();
 assert.equal(calls.length,previousCalls+1);assert.equal(calls.at(-1).route,'preview/4');
 GW.confirm=async()=>false;
 const n=calls.filter(c=>c.route.startsWith('apply')).length;await apply.onclick();
 assert.equal(calls.filter(c=>c.route.startsWith('apply')).length,n);
 GW.moduleImpl.unmount();
 // Read-only plans cannot apply from the UI.
 const ro=h('div');await GW.moduleImpl.mount(ro,{cid:4,api:async(_,o)=>data(o,false)});
 assert.equal(all(ro).find(e=>e.children.includes('Apply preset')).disabled,true);GW.moduleImpl.unmount();
 // A completed request after unmount must not render or enable an old control.
 let finish;const stale=h('div');
 const mounting=GW.moduleImpl.mount(stale,{cid:4,api:()=>new Promise(r=>{finish=r;})});
 GW.moduleImpl.unmount();finish(data());await mounting;
 assert.equal(all(stale).find(e=>e.children.includes('Apply preset')).disabled,true);
 console.log('PASS gear preset UI: preview required, option invalidation, exact reviewed token/options, cancellation, read-only, Undo refresh and stale unmount handling.');
})().catch(e=>{console.error(e);process.exitCode=1;});
