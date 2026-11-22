// W4-B: static/modules/login_awards.js renders the overview and sends the expected bodies (fake DOM; layout not tested).
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let component,modals=[],calls=[];
const h=(tag,props,...kids)=>Object.assign({tag,value:'',checked:false,children:kids.flat(Infinity).filter(x=>x!=null),classList:{add(){}},append(...x){this.children.push(...x.flat(Infinity).filter(y=>y!=null));},addEventListener(ev,fn){this['on'+ev]=fn;}},props||{});
const GW={h,module:(n,p)=>component=p,clear:n=>{n.children=[];return n;},confirm:async()=>true,status(){},writeDone(){},writeError:async e=>{throw e;},
  cached:async()=>({entries:[{apply_status_id:1000,label:'Double EXP',beneficial:true}]}),
  api:async()=>[],idField:(type,value,o)=>Object.assign(h('span'),{value,pick:o.onChange}),
  modal:(content,o)=>{const m={el:content,close(){m.closed=true;}};modals.push(m);return m;}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/login_awards.js'),'utf8'),{window:{GW},console});
const overview={ready:true,version:'v1',now:'2026-11-12 13:00',time_zone:'Australia/Brisbane (AEST, UTC+10)',titles:[{id:1000,name:'Valiant Spirit'}],
  chest:{waiting:2,characters:1},queued:{waiting:1},broadcast:{available:true},limits:{items:16,stack:99,buffs:8,silver:1e9,gold:1e6,binding_gold:1e7},
  awards:[{id:4,name:'Launch',starts:'2026-11-13 00:00',ends:'2026-11-20 00:00',per_scope:'account',delivery:'prize_chest',items:[{item_id:4102,quantity:5,quality:1,name:'Key'}],
    buffs:[{status_id:1000}],silver:0,gold:0,binding_gold:300,title_id:1000,min_level:1,enabled:true,open:true,claims:3,claims_chest:3}]};
const walk=(x,out=[])=>{if(x&&typeof x==='object'){out.push(x);(x.children||[]).forEach(c=>walk(c,out));}return out;};
const text=x=>walk(x).filter(n=>typeof n==='string'||typeof n==='number').join(' ')+walk(x).flatMap(n=>(n.children||[]).filter(c=>typeof c==='string')).join(' ');
(async()=>{
  const el=h('div');const ctx={api:async(route,body)=>{calls.push({route,body});return route==='overview'?overview:{ok:true,matched:2,applied:2,notApplied:0,busy:0,queuedOffline:5,sent_to:1};}};
  await component.mount.call(component,el,ctx);
  const all=walk(el),t=text(el);
  assert.ok(t.includes('Launch')&&t.includes('2026-11-13 00:00 to 2026-11-20 00:00')&&t.includes('once per account')&&t.includes('Prize Chest'),'award row');
  const buttons=all.filter(n=>n.tag==='button');
  const byText=s=>buttons.find(b=>(b.children||[]).some(c=>typeof c==='string'&&c.startsWith(s)));
  // Disable sends the page version.
  await byText('Disable').onclick();
  assert.equal(JSON.stringify(calls.find(c=>c.route==='award/4/enabled').body),JSON.stringify({enabled:false,version:'v1'}));
  // Server-wide buff with filters.
  const selects=all.filter(n=>n.tag==='select');const buffSel=selects.find(s=>s['aria-label']==='Buff');buffSel.value='1000';buffSel.selectedIndex=1;buffSel.options=buffSel.children;
  const camp=selects.find(s=>s['aria-label']==='Faction');camp.value='1';
  const queue=all.find(n=>n['aria-label']==='Also queue for offline characters');queue.checked=true;
  await byText('Apply to everyone online').onclick();
  const b=calls.find(c=>c.route==='buffs/broadcast').body;
  assert.equal(b.status_id,'1000');assert.equal(b.camp,'1');assert.equal(b.queue_offline,true);
  // New award form saves Brisbane times as typed.
  byText('New award').onclick();
  const form=modals.at(-1).el,fall=walk(form);
  fall.find(n=>n['aria-label']==='Name').value='Weekend';
  fall.find(n=>n['aria-label']==='Start (Brisbane)').value='2026-11-14T09:00';
  fall.find(n=>n['aria-label']==='End (Brisbane)').value='2026-11-16T09:00';
  await fall.find(n=>n.tag==='button'&&n.children.includes('Save award')).onclick();
  const save=calls.find(c=>c.route==='award/save').body;
  assert.equal(save.award.starts,'2026-11-14 09:00');assert.equal(save.award.ends,'2026-11-16 09:00');assert.equal(save.version,'v1');assert.equal(save.id,null);
  console.log('PASS login awards UI: overview row, versioned disable, broadcast body with filters, new award Brisbane times. Layout not tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
