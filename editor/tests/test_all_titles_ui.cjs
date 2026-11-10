const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
class El{
 constructor(tag,a={}){this.tag=tag;this.children=[];Object.assign(this,a);}
 append(...kids){for(const k of kids.flat(Infinity))if(k!=null&&k!==false)this.children.push(k);}
}
const all=e=>e instanceof El?[e,...e.children.flatMap(all)]:[];
const h=(tag,a,...kids)=>{const e=new El(tag,a||{});e.append(...kids);return e;};
let mod,listener;
const GW={h,clear:e=>{e.children=[];return e;},module:(_,m)=>mod=m,onlineBanner:()=>h('div'),on:(_,f)=>{listener=f;return()=>listener=null;},status(){},writeDone(){},writeError:async()=>{},confirm:async()=>true};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/all_titles.js'),'utf8'),{window:{GW}});
const view={character:{id:4,name:'Copy'},add:[1001],owned:[1000],blocked:[{id:5152}],titles:[{id:1000,name:'Old title',status:'owned'},{id:1001,name:'New title',status:'grant'},{id:5152,name:'Native-only title',status:'blocked',reason:'Unreviewed'}],final_count:2,limit:128,selected:1000,writable:true,capacity_ok:true,changed:true,token:'reviewed'};
(async()=>{
 const root=h('div'),calls=[];
 let data=view;
 const api=async(route,body)=>{calls.push({route,body});if(route.startsWith('apply')){data={...view,add:[],owned:[1000,1001],changed:false,token:'after'};return {added:1};}return data;};
 await mod.mount(root,{cid:4,api});
 const button=s=>all(root).find(e=>e.tag==='button'&&e.children.includes(s));
 assert.equal(button('Give all titles').disabled,true);assert.equal(calls.length,0);
 await button('Preview all titles').onclick();assert.equal(button('Give all titles').disabled,false);
 assert.ok(all(root).some(e=>e.children.includes('Unreviewed')));
 GW.confirm=async()=>false;await button('Give all titles').onclick();assert.equal(calls.filter(c=>c.route.startsWith('apply')).length,0);
 assert.equal(button('Give all titles').disabled,false);
 GW.confirm=async()=>true;await button('Give all titles').onclick();
 assert.equal(calls.find(c=>c.route==='apply/4').body.token,'reviewed');
 assert.equal(button('Give all titles').disabled,true);
 const n=calls.length;await listener();assert.equal(calls[n].route,'preview/4');
 data={...view,capacity_ok:false};await button('Preview all titles').onclick();assert.equal(button('Give all titles').disabled,true);
 data={...view,writable:false};await button('Preview all titles').onclick();assert.equal(button('Give all titles').disabled,true);
 mod.unmount();assert.equal(listener,null);
 let finish;const stale=h('div');await mod.mount(stale,{cid:4,api:()=>new Promise(r=>finish=r)});
 const pending=all(stale).find(e=>e.tag==='button'&&e.children.includes('Preview all titles')).onclick();mod.unmount();finish(view);await pending;
 assert.equal(all(stale).find(e=>e.tag==='button'&&e.children.includes('Give all titles')).disabled,true);
 console.log('PASS title preview gate, blocked reasons, cancel preservation, reviewed token, idempotent read-back, Undo refresh, limit/read-only guard and stale unmount.');
})().catch(e=>{console.error(e);process.exitCode=1;});
