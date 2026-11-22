// Operations page: every tab renders from fixture API answers without throwing; restore/clear/realm calls carry the
// typed confirmations. Fake DOM (objects), no browser: layout and styling are NOT tested.
// Run: node tests/test_operations_ui.cjs
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let def,confirmAnswer=true,typedHook=null;const calls=[];
const node=(tag,props,...kids)=>Object.assign({tag,value:'',checked:false,style:{},children:kids.flat(Infinity).filter(x=>x!=null&&x!==false),append(...x){this.children.push(...x.flat(Infinity).filter(y=>y!=null));},setAttribute(k,v){this[k]=v;},addEventListener(){},querySelectorAll(){return [];}},props||{});
const GW={h:node,module:(n,d)=>def=d,clear:n=>{n.children=[];return n;},status(){},emit(){},writeDone(){},writeError:async e=>{throw e;},alert:async()=>true,
  confirm:async o=>{if(typedHook&&o.el)typedHook(o.el);return confirmAnswer;}};
const document={createElementNS:(ns,t)=>node(t),body:node('body')};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/operations.js'),'utf8'),{window:{GW},document,console,setInterval:()=>1,clearInterval(){},setTimeout:(f)=>f(),Blob:function(){},URL:{createObjectURL:()=>'blob:x',revokeObjectURL(){}}});
const svc={godswar251:{memory:1e8,cpu_pct:3.2,pids:20,running:true},postgres:{memory:2e8,cpu_pct:1,pids:9,running:true},caddy:null,gwsignup:null,editor:{memory:2e8,cpu_pct:null,pids:3,running:true}};
const fixtures={
 overview:{service:{ActiveState:'active',SubState:'running',ActiveEnterTimestamp:'Wed 2026-11-12 02:27:01 UTC',MainPID:'123',NRestarts:'0'},players_online:0,players_detail:{accounts:0,connections:0},control_available:true,writable:true,database:'godswar',jobs:[{job:'a',action:'backup',state:'succeeded',started_at:'2026-11-12T01:00:00Z'}],restore_rule:'rule'},
 check:{checks:[{name:'Login port 6999',ok:true,detail:'open'},{name:'Disk /',ok:false,detail:'95%'}],ok:false},
 monitor:{now:{cpu_pct:5,net_rx:100,net_tx:50,services:svc},history:[{cpu_pct:5,mem_pct:50,load1:.2,players:0,net_rx:100,services:svc},{cpu_pct:7,mem_pct:51,load1:.3,players:1,net_rx:300,services:svc}],interval:10,cores:4,memory:{total:4e9,used:2e9,used_pct:50,swap_total:0},load:[.2,.1,.1],uptime:90000,disks:[{mount:'/',total:1e11,free:5e10,used_pct:50}],players_online:1,game_uptime:3600,services:Object.keys(svc)},
 realms:{realms:[{id:1,name:'Tempest',identifier:'KAL3jcIzqGgKvOf1dbYZKC8cS',ip_address:'YOUR_SERVER_IP',game_port:7000,server_limit:250,display_order:1,enabled:true,recommended:true,references:[{table:'public.character_base',rows:40}],protected:'home',reachable:true},
   {id:3,name:'Test',identifier:'x'.repeat(25),ip_address:'10.0.0.1',game_port:7002,server_limit:9,display_order:3,enabled:false,recommended:false,references:[],protected:null,reachable:false}],next_id:4,new_identifier:'y'.repeat(25),writable:true,limits:{name:35,host:23,enabled:16},how:['a','b'],character_rule:'chars stay'},
 'changes/summary':{records:5,keep:1,keep_list:[{action:'x'}],oldest:'2026-11-01T00:00:00Z',newest:'2026-11-12T00:00:00Z',archives:[]},
 'changes/export':{exported_at:'2026-11-12T00:00:00Z',changes:[{id:1}]},
};
const ctx=route=>({route:[route],navigate(){},api:async(sub,body)=>{calls.push({sub,body});if(body){if(sub==='control'&&body.action==='backups')return {job:{job:'b',state:'succeeded',backups:[{file:'godswar251-2026-11-12-173000.sql.gz',folder:'nightly',bytes:5e7,time:'2026-11-12T17:30:00Z'}]}};return {job:{job:'c',state:'succeeded',output:'ok'},archived:4,kept:1,folder:'f'};}
  if(!(sub in fixtures))throw new Error('no fixture '+sub);return JSON.parse(JSON.stringify(fixtures[sub]));}});
const all=n=>{const out=[];(function w(x){if(!x||typeof x!=='object')return;out.push(x);(x.children||[]).forEach(w);})(n);return out;};
const strings=n=>{const out=[];(function w(x){if(typeof x==='string'||typeof x==='number'){out.push(String(x));return;}if(!x||typeof x!=='object')return;(x.children||[]).forEach(w);})(n);return out.join(' ');};
const J=x=>JSON.parse(JSON.stringify(x));
const tick=()=>new Promise(r=>setImmediate(r));
(async()=>{
 for(const t of ['server','monitor','realms','changes']){const el=node('div');calls.length=0;await def.mount(el,ctx(t));await tick();await tick();
  const s=strings(el);assert.ok(!all(el).some(x=>x.class==='tt-bad'&&/no fixture|is not|undefined/.test(strings(x))),t+': no render error');
  assert.ok(s.length>50,t+' rendered');def.unmount();
  if(t==='monitor'){assert.match(s,/godswar251/);assert.match(s,/postgres/);assert.match(s,/no cgroup/);assert.match(s,/1d 1h 0m/);}
  if(t==='realms'){assert.match(s,/Tempest/);const removes=all(el).filter(x=>x.tag==='button'&&strings(x)==='Delete');assert.equal(removes.length,1,'realm 1 (protected) offers no Delete');
   assert.equal(all(el).filter(x=>x.tag==='button'&&strings(x)==='Close').length,0,'nothing to close: realm 1 never, realm 3 is disabled without a process');
   assert.equal(all(el).filter(x=>x.tag==='button'&&strings(x)==='Set up').length,1,'the disabled realm offers Set up');
   typedHook=box=>{all(box).find(x=>x.tag==='input').value='Test';};calls.length=0;await removes[0].onclick();await tick();
   assert.deepEqual(J(calls.filter(c=>c.body)),[{sub:'realm/close',body:{id:3,confirm:'Test',delete:true}},{sub:'realm/delete',body:{id:3,confirm:'Test'}}],'Delete = take the process down, then remove the row');typedHook=null;}
  if(t==='server'){const lb=all(el).find(x=>x.tag==='button'&&strings(x)==='List backups');calls.length=0;await lb.onclick();await tick();
   const rb=all(el).find(x=>x.tag==='button'&&strings(x)==='Restore…');assert.ok(rb,'restore button listed');
   typedHook=box=>{all(box).find(x=>x.tag==='input').value='wrong';};calls.length=0;await rb.onclick();assert.ok(!calls.some(c=>c.body&&c.body.action==='restore'),'wrong name: nothing sent');
   typedHook=box=>{all(box).find(x=>x.tag==='input').value='godswar251-2026-11-12-173000.sql.gz';};calls.length=0;await rb.onclick();
   assert.deepEqual(J(calls.find(c=>c.body&&c.body.action==='restore').body),{action:'restore',file:'godswar251-2026-11-12-173000.sql.gz',confirm:'godswar251-2026-11-12-173000.sql.gz'});typedHook=null;}
  if(t==='changes'){const cb=all(el).find(x=>x.tag==='button'&&/Clear change log/.test(strings(x)));
   typedHook=box=>{const i=all(box).filter(x=>x.tag==='input');i.find(x=>x.type==='text').value='CLEAR';i.find(x=>x.type==='checkbox').checked=false;};calls.length=0;await cb.onclick();
   assert.deepEqual(J(calls.find(c=>c.sub==='changes/clear').body),{confirm:'CLEAR',records_seen:5});typedHook=null;}
 }
 // ---- guided Add a server (wave 5 B)
 const setup={address:{address:'YOUR_SERVER_IP',source:"this machine's network interface",note:''},suggested_port:7002,port_range:[7001,7099],next_id:4,default_limit:250,
  reuse:[{id:2,name:'Dwargon',game_port:7001,ip_address:'YOUR_SERVER_IP',characters:0,suggested_port:7001}],handoff:{store:'postgres',shared:true,text:'shared'},processes:{},
  control_available:true,writable:true,what_happens:['row','process','ufw','test','start'],provider_rule:'PROVIDER',template_note:'Dwargon is not a template',character_rule:'chars stay'};
 async function guided(script){const el=node('div');const sent=[];let finishCalls=0;
  const c={route:['realms'],navigate(x){sent.push({nav:x});},api:async(sub,body)=>{sent.push({sub,body});
   if(sub==='realms')return J(fixtures.realms);if(sub==='realm/setup')return J(setup);
   if(sub==='realm/provision')return {job:{job:'j'.repeat(32),state:'running'},realm_id:body.id,port:body.port,name:body.name||'New',knock:{host:'YOUR_SERVER_IP',port:body.port,url:'http://YOUR_SERVER_IP:'+body.port+'/'}};
   if(sub.startsWith('realm/process/'))return script.process;
   if(sub.startsWith('job/'))return script.job;
   if(sub==='realm/provision/finish'){finishCalls++;if(script.finish409&&!body.acknowledge_unreachable){const e=new Error('No connection from outside reached port');e.status=409;throw e;}return {change:{action:'listed'}};}
   throw new Error('no fixture '+sub);}};
  await def.mount(el,c);await tick();
  await all(el).find(x=>x.tag==='button'&&strings(x)==='Add a server').onclick();await tick();
  const text=strings(el);assert.match(text,/23\.111\.15\.109/,'the detected address is shown, not typed');assert.match(text,/PROVIDER/);assert.match(text,/not a template/);
  const inputs=all(el).filter(x=>x.tag==='input');assert.ok(!inputs.some(i=>i.value==='YOUR_SERVER_IP'),'no input field for the address');
  assert.ok(inputs.some(i=>i.type==='number'&&i.value==='7001'),'port prefilled with the suggestion for Dwargon');
  if(script.pickNew){const sel=all(el).find(x=>x.tag==='select');sel.value='1';sel.onchange();const i2=all(el).filter(x=>x.tag==='input');i2.find(i=>i.type==='text').value='Olympus';
   assert.equal(i2.find(i=>i.type==='number'&&i.min===7001).value,'7002','a new server gets the next free port');}
  await all(el).find(x=>x.tag==='button'&&strings(x)==='Set up server').onclick();await tick();await tick();
  return {el,sent,finishCalls:()=>finishCalls};}
 const okJob={job:'j'.repeat(32),state:'succeeded',action:'realm_provision',realm_id:2,port:7001,realm:{state:'succeeded',steps:[{step:'started',ok:true,detail:'ready'}],outside:{result:'seen',from:'1.2.3.4'}}};
 let g=await guided({process:{state:'running',steps:[{step:'ufw',ok:true,detail:'open'}],outside:{result:'listening',seconds:30}},job:okJob});
 assert.deepEqual(J(g.sent.find(c=>c.sub==='realm/provision').body),{mode:'existing',id:2,name:'Dwargon',port:7001,server_limit:250},'one click sends the row choice, name, port and limit; no address');
 assert.deepEqual(J(g.sent.find(c=>c.sub==='realm/provision/finish').body),{id:2,job:'j'.repeat(32)},'a passed outside test lists the server without asking');def.unmount();
 g=await guided({pickNew:true,process:{state:'running',steps:[]},job:Object.assign({},okJob,{realm_id:4,port:7002})});
 assert.deepEqual(J(g.sent.find(c=>c.sub==='realm/provision').body),{mode:'new',id:4,name:'Olympus',port:7002,server_limit:250});def.unmount();
 g=await guided({process:{state:'running',steps:[]},job:Object.assign({},okJob,{realm:{state:'succeeded',steps:[],outside:{result:'not_seen'}}}),finish409:true});
 assert.equal(g.finishCalls(),1,'not seen from outside: the first finish is refused and nothing is listed');
 assert.ok(all(g.el).some(x=>/No connection from outside/.test(x.textContent||'')),'the refusal is shown');assert.ok(all(g.el).find(x=>x.tag==='button'&&strings(x)==='Test again'),'Test again offered');
 confirmAnswer=false;await all(g.el).find(x=>x.tag==='button'&&/list the server anyway/.test(strings(x))).onclick();await tick();assert.equal(g.finishCalls(),1,'declined: still not listed');
 confirmAnswer=true;await all(g.el).find(x=>x.tag==='button'&&/list the server anyway/.test(strings(x))).onclick();await tick();
 assert.deepEqual(J(g.sent.filter(c=>c.sub==='realm/provision/finish').pop().body),{id:2,job:'j'.repeat(32),acknowledge_unreachable:true},'listing anyway is an explicit, confirmed acknowledgement');def.unmount();
 g=await guided({process:{state:'failed',steps:[{step:'shared login handoff',ok:false,detail:'drop-in written'}]},job:{job:'j'.repeat(32),state:'failed',error:'The main game server must be restarted once',realm:{code:'needs_restart',steps:[{step:'shared login handoff',ok:false,detail:'drop-in written'}]}}});
 assert.equal(g.finishCalls(),0,'a failed set-up never lists the server');assert.ok(all(g.el).some(x=>/restarted once/.test(x.textContent||'')),'the reason is shown');assert.match(strings(g.el),/drop-in written/,'the steps are listed');
 const goBtn=all(g.el).find(x=>x.tag==='button'&&/Go to Game server/.test(strings(x)));assert.ok(goBtn,'needs_restart points at the restart control');goBtn.onclick();assert.deepEqual(J(g.sent.pop()),{nav:'server'});def.unmount();
 // the knock link appears while the script listens
 let seenLink=false;const el2=node('div');let n=0;
 await def.mount(el2,{route:['realms'],navigate(){},api:async(sub,body)=>{if(sub==='realms')return J(fixtures.realms);if(sub==='realm/setup')return J(setup);
  if(sub==='realm/provision')return {job:{job:'k'.repeat(32),state:'running'},realm_id:2,port:7001,name:'Dwargon',knock:{host:'YOUR_SERVER_IP',port:7001,url:'http://YOUR_SERVER_IP:7001/'}};
  if(sub.startsWith('realm/process/'))return {state:'running',steps:[],outside:{result:'listening',seconds:30}};
  if(sub.startsWith('job/')){seenLink=seenLink||all(el2).some(x=>x.tag==='a'&&x.href==='http://YOUR_SERVER_IP:7001/'&&x.target==='_blank');return ++n<2?{job:'k'.repeat(32),state:'running'}:okJob;}
  return {change:{}};}});await tick();
 await all(el2).find(x=>x.tag==='button'&&strings(x)==='Add a server').onclick();await tick();
 await all(el2).find(x=>x.tag==='button'&&strings(x)==='Set up server').onclick();await tick();await tick();
 assert.ok(seenLink,'"Test from this computer" link (new tab to the address and port) is offered while the server listens');def.unmount();
 // Close: a running, enabled extra realm
 const fx2=J(fixtures.realms);fx2.realms.push({id:2,name:'Dwargon',identifier:'z'.repeat(25),ip_address:'YOUR_SERVER_IP',game_port:7001,server_limit:250,display_order:2,enabled:true,recommended:false,references:[{table:'public.character_base',rows:3}],protected:'migrations',reachable:true,characters:3,process:{state:'succeeded',set_up:true,port:7001,outside:{result:'seen'}}});
 const el3=node('div');const sent3=[];await def.mount(el3,{route:['realms'],navigate(){},api:async(sub,body)=>{sent3.push({sub,body});if(sub==='realms')return J(fx2);if(sub.startsWith('job/'))return {job:'m',state:'succeeded'};return {job:{job:'m',state:'running'},row:{change:{action:'disabled'}}};}});await tick();
 const dw=all(el3).filter(x=>x.tag==='tr').find(x=>/Dwargon/.test(strings(x)));assert.match(strings(dw),/running on port 7001, seen from outside/);
 assert.ok(!all(dw).some(x=>x.tag==='button'&&strings(x)==='Delete'),'Dwargon (protected) offers Close, not Delete');
 let shown='';typedHook=box=>{shown=strings(box);all(box).find(x=>x.tag==='input').value='Dwargon';};
 await all(dw).find(x=>x.tag==='button'&&strings(x)==='Close').onclick();await tick();typedHook=null;
 assert.match(shown,/3 characters live on it/,'the dialog says how many characters and what happens to them');assert.match(shown,/chars stay|never deleted|Characters/);
 assert.deepEqual(J(sent3.filter(c=>c.body)),[{sub:'realm/close',body:{id:2,confirm:'Dwargon',delete:false}}],'Close never calls realm/delete');def.unmount();
 console.log('PASS operations UI (guided servers): address shown not typed, port prefilled; one click provisions; a passed outside test lists the server, a failed one needs Test again or a confirmed acknowledgement; needs_restart never lists; the knock link appears while listening; Close keeps the row and states the character rule.');
 console.log('PASS operations UI: four tabs render from fixtures; monitor lists services incl. missing cgroups; Delete takes the process down then removes the row (typed name sent); restore sends nothing on a wrong typed name and the exact file otherwise; clear sends CLEAR + the record count seen. Browser rendering not tested.');
})().catch(e=>{console.error(e);process.exitCode=1;});
