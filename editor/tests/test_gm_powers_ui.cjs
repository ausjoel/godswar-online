// GM powers page: renders from a fixture overview; switches collect into ONE save call with the version; the master
// switch and switching powers off ask first; grant / revoke send the flag the page showed; an old game build shows the
// inventory read-only. Fake DOM (objects), no browser: layout and styling are NOT tested.
// Run: node tests/test_gm_powers_ui.cjs
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let def,confirmAnswer=true;const confirms=[];
const node=(tag,props,...kids)=>Object.assign({tag,value:'',checked:false,style:{},children:kids.flat(Infinity).filter(x=>x!=null&&x!==false),append(...x){this.children.push(...x.flat(Infinity).filter(y=>y!=null));},setAttribute(k,v){this[k]=v;},addEventListener(){}},props||{});
const GW={h:(t,p,...k)=>{const n=node(t,p,...k);if(p&&p.checked===null)n.checked=false;return n;},module:(n,d)=>def=d,clear:n=>{n.children=[];return n;},status(){},emit(){},writeDone(){},writeError:async e=>{throw e;},alert:async()=>true,
  confirm:async o=>{confirms.push(o);return confirmAnswer;}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/gm_powers.js'),'utf8'),{window:{GW},console,encodeURIComponent});
const inv=JSON.parse(fs.readFileSync(path.join(__dirname,'../data/gm_powers_inventory.json'),'utf8')).powers;
const groups=[['master','Master switch'],['moderation','Moderation'],['travel','Teleport and instances'],['self',"The GM's own powers"],['items','Items'],['progression','Levels and progress'],['tools','Tools'],['bypass','Rules a GM skips']].map(([key,title])=>({key,title}));
const overview=(over)=>Object.assign({ready:true,why_not_ready:null,powers:inv.map(p=>Object.assign({},p,{allowed:p.key!=='cmd.dupe',server_allowed:p.key!=='cmd.dupe',updated_at:p.key==='cmd.dupe'?'2026-11-12T10:00:00Z':null,updated_by:p.key==='cmd.dupe'?'gweditor':null})),
  master:true,groups,from_server:true,commands_enabled:true,authority:'character_flag',pending:[],version:'a'.repeat(32),environment:{},
  gms:[{id:7,name:'Odin3',level:120,gm:1,account_id:3,username:'gmhost',account_status:0,realm:1,online:true,works:true,why_not:null},{id:9,name:'Old',level:5,gm:1,account_id:4,username:'gone',account_status:1,realm:1,online:false,works:false,why_not:'the account is blocked (status 1)'}],
  allowlist:[{id:3,username:'gmhost',status:0}],who_rule:'A GM is a character with the GM flag',not_here:[{what:'Each gate',where:'Gates page',link:'#/gates'},{what:'mall price',where:'drop-in',link:null}],writable:true,applies_text:'applies within 5 seconds'},over||{});
const all=n=>{const out=[];(function w(x){if(!x||typeof x!=='object')return;out.push(x);(x.children||[]).forEach(w);})(n);return out;};
const strings=n=>{const out=[];(function w(x){if(typeof x==='string'||typeof x==='number'){out.push(String(x));return;}if(!x||typeof x!=='object')return;if(x.textContent)out.push(x.textContent);(x.children||[]).forEach(w);})(n);return out.join(' ');};
const J=x=>JSON.parse(JSON.stringify(x));
const tick=()=>new Promise(r=>setImmediate(r));
async function mount(ov,extra){const el=node('div');const calls=[];
  await def.mount(el,{route:[],navigate(){},api:async(sub,body)=>{calls.push({sub,body});if(sub==='overview')return J(ov);if(extra&&extra(sub,body)!==undefined)return extra(sub,body);return {change:{action:'ok'}};}});await tick();
  return {el,calls,box:label=>all(el).find(x=>x.tag==='input'&&x.type==='checkbox'&&x['aria-label']===label),btn:rx=>all(el).find(x=>x.tag==='button'&&rx.test(strings(x)))};}
(async()=>{
 // 1. the picture
 let p=await mount(overview());let s=strings(p.el);
 for(const g of groups)assert.match(s,new RegExp(g.title.replace(/[']/g,'.')),'group '+g.title);
 assert.equal(all(p.el).filter(x=>x.tag==='input'&&x.type==='checkbox').length,inv.length,'one switch per power, master included');
 assert.match(s,/38 powers of 39 allowed/);assert.match(s,/Odin3/);assert.match(s,/no: the account is blocked/);assert.match(s,/NOT used while the character flag decides/);
 assert.equal(p.box('/dupe <slot> [@name]').checked,false,'a switched-off power shows off');assert.match(s,/off since 2026-11-12 10:00/);
 assert.equal(p.btn(/^Save switches$/).disabled,true,'nothing to save yet');
 // 2. switch one off and one back on -> one confirmed save with the version
 const give=p.box('/give <itemId> [qty] [bound] [@name]');give.checked=false;give.onchange();
 const dupe=p.box('/dupe <slot> [@name]');dupe.checked=true;dupe.onchange();
 assert.match(strings(p.el),/2 changes not saved/);assert.equal(p.btn(/^Save switches$/).disabled,false);
 confirmAnswer=false;confirms.length=0;p.calls.length=0;await p.btn(/^Save switches$/).onclick();assert.equal(p.calls.filter(c=>c.sub==='save').length,0,'declined: nothing sent');
 assert.match(confirms[0].message,/cmd\.give/);assert.match(confirms[0].message,/Switched back on: cmd\.dupe/);
 confirmAnswer=true;await p.btn(/^Save switches$/).onclick();await tick();
 assert.deepEqual(J(p.calls.find(c=>c.sub==='save').body),{changes:{'cmd.give':false,'cmd.dupe':true},version:'a'.repeat(32)});
 // toggling back to the saved state is not a change
 p=await mount(overview());const kick=p.box('/kick <name>');kick.checked=false;kick.onchange();kick.checked=true;kick.onchange();assert.equal(p.btn(/^Save switches$/).disabled,true);
 // 3. group "All off" and the master switch
 const itemsPanel=all(p.el).find(x=>x.class&&/gmp-panel/.test(x.class)&&/^Items /.test(strings(x)));
 await all(itemsPanel).find(x=>x.tag==='button'&&strings(x)==='All off').onclick();
 const nItems=inv.filter(x=>x.group==='items').length;assert.match(strings(itemsPanel),new RegExp('0 of '+nItems+' on'));
 confirms.length=0;p.calls.length=0;await p.btn(/^Save switches$/).onclick();await tick();
 assert.equal(Object.keys(p.calls.find(c=>c.sub==='save').body.changes).length,nItems-1,'only the powers that were on are sent (cmd.dupe was off already)');
 p=await mount(overview());const m=p.box('GM powers (master switch)');m.checked=false;m.onchange();
 assert.match(strings(p.el),/allowed, but the master switch is off/,'rows show that the master switch overrides them');
 confirms.length=0;confirmAnswer=true;await p.btn(/^Save switches$/).onclick();await tick();
 assert.match(confirms[0].title,/every GM power off/);assert.equal(confirms[0].danger,true);
 assert.deepEqual(J(p.calls.find(c=>c.sub==='save').body.changes),{master:false});
 // 4. revoke and grant
 p=await mount(overview(),(sub)=>sub.startsWith('find?')?{characters:[{id:21,name:'Freya',level:60,gm:0,account_id:8,username:'freya5',account_status:0,online:false},{id:7,name:'Odin3',level:120,gm:1,account_id:3,username:'gmhost',account_status:0,online:true}]}:undefined);
 assert.equal(all(p.el).filter(x=>x.tag==='button'&&strings(x)==='Revoke').length,2);
 confirms.length=0;await all(p.el).filter(x=>x.tag==='button'&&strings(x)==='Revoke')[0].onclick();await tick();
 assert.match(confirms[0].message,/last working GM/,'revoking the last working GM says so');
 assert.deepEqual(J(p.calls.find(c=>c.sub==='grant').body),{character_id:7,gm:false,expected:1});
 all(p.el).find(x=>x.tag==='input'&&x.type==='text').value='Li';await p.btn(/^Find$/).onclick();await tick();
 assert.equal(p.calls.find(c=>c.sub.startsWith('find?')).sub,'find?q=Li');
 const mk=all(p.el).filter(x=>x.tag==='button'&&strings(x)==='Make GM');assert.equal(mk.length,1,'a character that is a GM already offers no button');
 p.calls.length=0;await mk[0].onclick();await tick();assert.deepEqual(J(p.calls.find(c=>c.sub==='grant').body),{character_id:21,gm:true,expected:0});
 // 5. log
 p=await mount(overview(),(sub)=>sub==='log'?{changes:[{at:'2026-11-12T10:00:00Z',power_key:'cmd.dupe',operation:'INSERT',old_allowed:null,new_allowed:false,changed_by:'gweditor'}],refused:[{at:'2026-11-12T10:05:00Z',actor_character:'Odin3',command:'/dupe',detail:'refused: cmd.dupe is switched off'}],notes:[]}:undefined);
 await p.btn(/Show switch changes/).onclick();await tick();s=strings(p.el);assert.match(s,/switched OFF/);assert.match(s,/refused: cmd\.dupe is switched off/);
 // 6. an older game build: inventory shown, nothing can be switched; a read-only editor likewise
 p=await mount(overview({ready:false,from_server:false,why_not_ready:'The running game build has no GM power switches yet'}));
 assert.match(strings(p.el),/no GM power switches yet/);assert.ok(all(p.el).filter(x=>x.tag==='input'&&x.type==='checkbox').every(x=>x.disabled===true),'every switch is disabled');
 assert.match(strings(p.el),/\/announce <text>/,'the inventory is still listed');
 p=await mount(overview({writable:false}));assert.ok(all(p.el).filter(x=>x.tag==='button'&&/Revoke|All off|All on/.test(strings(x))).every(x=>x.disabled===true));
 // 7. start-up switch off / account-list mode are said plainly
 p=await mount(overview({commands_enabled:false,authority:'account_allowlist',who_rule:'A GM is any character of an account on the allow list'}));
 s=strings(p.el);assert.match(s,/OFF at start-up/);assert.match(s,/flag set, but the account list decides/);
 console.log('PASS GM powers UI: one switch per power in 7 groups + master; changes collect into one confirmed save with the version; master off and group All off; grant / revoke send the shown flag; log; old build and read-only are read-only. Browser rendering not tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
