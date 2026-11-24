const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
class El{
 constructor(tag,a={}){this.tag=tag;this.children=[];this.events={};this.value='';this.checked=false;Object.assign(this,a);}
 append(...kids){for(const k of kids.flat(Infinity))if(k!=null&&k!==false){this.children.push(k);if(this.tag==='select'&&!this.value)this.value=k.value;}}
 addEventListener(k,f){(this.events[k]||=[]).push(f);}fire(k){for(const f of this.events[k]||[])f({target:this});}
 querySelectorAll(s){return all(this).filter(e=>e.tag==='input'&&(s!=='input:checked'||e.checked));}
}
const all=e=>e instanceof El?[e,...e.children.flatMap(all)]:[];
const h=(tag,a,...kids)=>{const e=new El(tag,a||{});e.append(...kids);return e;};
let mod,listener,warnings=0;
const GW={h,clear:e=>{e.children=[];return e;},module:(_,m)=>mod=m,onlineBanner:()=>h('div'),on:(_,f)=>{listener=f;return()=>listener=null;},status:()=>warnings++,writeDone(){},writeError:async()=>{},confirm:async()=>true};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/mount_gear.js'),'utf8'),{window:{GW}});
const rows=[['patk',347],['matk',357],['pdmg',367],['mdmg',377],['hp',307],['mp',317]];
const D={items:[{location:1,slot:1,name:'Coronet',kind:'mounthead',row:{id:44,prop_id:14508,item_quality:8,item_grade:10,attribute1:347,attribute2:377,attribute3:307}}],
 targets:[{id:14508,name:'Coronet',kind:'mounthead',min_level:120},{id:14608,name:'Armor',kind:'mountarmor',min_level:120}],stats:rows.map(([key,id])=>({key,id,name:key,display:'99'})),writable:true};
// W23: the mount gear adder above the one-piece tools
const A={character:{id:4,name:'T',level:120,class:'Mage',profession:3},mount:null,families:[{id:'greeksteed',name:'Greek Steed'}],gear:{},
 slots:[],levels:[40,50,60,70,80,90,100,110,120],stats:rows.map(([key,id])=>({key,name:key,kind:Math.floor(id/10)})),best:['matk','mdmg'],max_stats:4,
 quality:{max:20,default:20},grade:{max:25,default:25},writable:true};
const adderApi=async(route,body)=>{if(route==='adder/4')return A;if(route.startsWith('adder/preview'))return {family:'Greek Steed',options:body,pieces:[{slot:20,what:'Mount',before:null,after:{item_quality:20,item_grade:1,bound:1},name:'Greek Steed',level:120,stats:[]}],warnings:[],changed:true,writable:true,token:'adder-token'};
 return {ok:true};};
const inAdder=root=>{const sec=all(root).find(e=>e.class==='mg-adder');return new Set(sec?all(sec):[]);};
(async()=>{
 const root=h('div'),calls=[];
 const api=async(route,body)=>{calls.push({route,body});if(route.startsWith('gear'))return D;if(route.startsWith('adder/'))return adderApi(route,body);if(route.startsWith('apply'))return {ok:true};
 return {source_name:'Coronet',name:'Armor',before:{item_quality:8,item_grade:10},after:{item_quality:8,item_grade:10},destination:{location:1,slot:2},options:body,selected:[],attributes:[{name:'Physical attack',display:'99'}],warnings:[],changed:true,writable:true,token:'reviewed'};};
 await mod.mount(root,{cid:4,api});
 const adderEls=inAdder(root),own=e=>!adderEls.has(e);
 const byLabel=s=>all(root).find(e=>own(e)&&e['aria-label']===s),button=s=>all(root).find(e=>own(e)&&e.tag==='button'&&e.children.includes(s));
 assert.equal(button('Apply').disabled,true);
 const mode=byLabel('Mount gear operation'),target=byLabel('Target mount gear');
 mode.value='copy';mode.fire('change');target.value='14608';target.fire('change');
 await button('Preview').onclick();assert.equal(button('Apply').disabled,false);
 assert.equal(calls.at(-1).body.mode,'copy');assert.equal(calls.at(-1).body.target,14608);
 byLabel('Set Epic G10 stats').checked=true;byLabel('Set Epic G10 stats').fire('change');assert.equal(button('Apply').disabled,true);
 const statInputs=all(root).filter(e=>e.tag==='input'&&e.value&&e.value!==''&&!adderEls.has(e));
 for(const e of statInputs){e.checked=true;e.fire('change');}
 assert.equal(statInputs.filter(e=>e.checked).length,5);assert.equal(warnings,1);
 await button('Preview').onclick();GW.confirm=async()=>false;await button('Apply').onclick();assert.equal(calls.filter(c=>c.route.startsWith('apply')).length,0);
 await button('Preview').onclick();GW.confirm=async()=>true;await button('Apply').onclick();
 const write=calls.find(c=>c.route==='apply/4');assert.equal(write.body.token,'reviewed');assert.equal(write.body.mode,'copy');assert.equal(write.body.epic_g10,true); // cancelling confirmation preserves the selected choices
 assert.equal(button('Apply').disabled,true);
 const count=calls.length;listener();await Promise.resolve();await Promise.resolve();assert.equal(calls[count].route,'gear/4');
 // the adder: auto best stats by default, apply sends the reviewed token
 const abutton=s=>[...adderEls].find(e=>e.tag==='button'&&e.children.includes(s));
 const fam=[...adderEls].find(e=>e['aria-label']==='Mount family');fam.value='greeksteed';
 await abutton('Preview').onclick();assert.equal(calls.at(-1).route,'adder/preview/4');assert.equal(calls.at(-1).body.mode,'auto');assert.equal(calls.at(-1).body.family,'greeksteed');
 await abutton('Apply to all five').onclick();const aw=calls.find(c=>c.route==='adder/apply/4');assert.equal(aw.body.token,'adder-token');assert.equal(aw.body.mount_level,120);
 mod.unmount();assert.equal(listener,null);
 let finish;const stale=h('div');const pending=mod.mount(stale,{cid:4,api:()=>new Promise(r=>finish=r)});mod.unmount();finish(D);await pending;
 assert.equal(all(stale).find(e=>e.tag==='button'&&e.children.includes('Apply')).disabled,true);
 console.log('PASS mount gear UI: adder preview/apply (family, auto stats, token), preview gate, copy target/options, five-stat limit, cancellation, reviewed token, reload/Undo refresh and stale unmount handling.');
})().catch(e=>{console.error(e);process.exitCode=1;});
