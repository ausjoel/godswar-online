// Player health page: renders from a fixture overview; red players first with the reason and what to do; the Login
// check lists blocked characters with a Fix button that asks first and sends exactly the finding's fix; filters
// (problems only, day, client version, name); "Check every character now" repaints from the check call; a database
// without login records says so and still shows the Login check. Also the shared login-check banner and the alert
// GW.writeDone raises after a character write. Fake DOM (objects), no browser: layout and styling are NOT tested.
// Run: node tests/test_player_health_ui.cjs
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('node:assert/strict');
let def,confirmAnswer=true;const confirms=[],alerts=[],done=[];
const node=(tag,props,...kids)=>Object.assign({tag,value:'',checked:false,hidden:false,style:{},children:kids.flat(Infinity).filter(x=>x!=null&&x!==false),append(...x){this.children.push(...x.flat(Infinity).filter(y=>y!=null&&y!==false));},setAttribute(k,v){this[k]=v;},addEventListener(){}},props||{});
const GW={h:(t,p,...k)=>{const n=node(t,p,...k);if(p&&p.checked===null)n.checked=false;if(p&&p.hidden===true)n.hidden=true;return n;},module:(n,d)=>def=d,clear:n=>{n.children=[];return n;},status(){},emit(){},
  helpText:o=>[o.title,o.text].concat((o.facts||[]).map(f=>f.join(': ')),o.list||[],o.note||[]).join('\n'),
  writeDone:(r,l)=>{done.push({r,l});},writeError:async e=>{throw e;},alert:async(m,t)=>{alerts.push({m,t});return true;},confirm:async o=>{confirms.push(o);return confirmAnswer;}};
vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/modules/player_health.js'),'utf8'),{window:{GW},console,encodeURIComponent});
const jadeFix={kind:'remove_item',row:478,location:1,slot:17,item:10109,label:'Remove this Seal Jade (it has no pet inside)'};
const jade=(sev)=>({severity:sev,code:'seal_jade_no_pet',title:'A packed Seal Jade has no pet inside it',detail:'It was added by hand, so it is an empty shell.',where:'Bag page 1, slot 18',source:'database',fix:jadeFix});
const checkOf=(sev)=>({results:[{cid:10,account_id:44,character:'Tester',username:'tester',can_enter:sev!=='blocks',checked_by_server:true,findings:[jade(sev)],light:sev==='blocks'?'red':'amber'},
  {cid:14,account_id:45,character:'Other',username:'other',can_enter:true,checked_by_server:true,findings:[],light:'green'}],server:true,why:'',blocked:sev==='blocks'?1:0,hidden:sev==='blocks'?0:1,checked:2});
const overview=(over)=>Object.assign({records_ready:true,records_note:'',days:7,now:'2026-11-13 10:00',
  stats:{today:'2026-11-13',logins_today:12,failed_today:3,failed_percent:25,unknown_name_attempts:2,days:[],
    top_reasons:[{code:'enter_fault',title:'Server error while entering the world',count:3,accounts:1,level:'bad'},{code:'login_bad_credentials',title:'Wrong password',count:2,accounts:2,level:'warn'}]},
  what_changed:{text:'Failed logins rose from 0% to 60% in the day after the server restart (2026-11-12 20:44). Most common: Server error while entering the world.'},
  players:[
    {account_id:44,username:'tester',characters:[{id:10,name:'Tester',level:200,online:false}],light:'red',headline:'A packed Seal Jade has no pet inside it',fix:'Remove this Seal Jade (it has no pet inside)',last:'2026-11-13 09:40',last_day:'2026-11-13',build:4102004,build_text:'2.51.002 (4102004)',build_behind:true,problems:3,logins:3,days:['2026-11-13'],
      timeline:[{at:'2026-11-13 09:40',level:'bad',title:'Server error while entering the world',detail:'A packed Seal Jade in the bag has no pet inside it',fix:'Run the Login check below: it names the item, pet or row to fix.',reached:'character screen',duration:'2 seconds',network:'203.0.113.0',build:'2.51.002 (4102004)',character_id:10}]},
    {account_id:9,username:'kid',characters:[{id:21,name:'Kiddo',level:30,online:true}],light:'amber',headline:'Wrong password',fix:'Ask the player to type it again (check Caps Lock), or set a new password on the Accounts page.',last:'2026-11-12 18:00',last_day:'2026-11-12',build:4102005,build_text:'4102005',build_behind:false,problems:1,logins:4,days:['2026-11-11','2026-11-12'],timeline:[{at:'2026-11-12 18:00',level:'warn',title:'Wrong password',detail:'',fix:'',reached:'password check',duration:'2 seconds',network:'198.51.100.0',build:'4102005',character_id:null}]},
    {account_id:45,username:'other',characters:[{id:14,name:'Other',level:200,online:false}],light:'green',headline:'Played and left normally',fix:'',last:'2026-11-13 08:00',last_day:'2026-11-13',build:4102004,build_text:'2.51.002 (4102004)',build_behind:true,problems:0,logins:5,days:['2026-11-13'],timeline:[]},
    {account_id:3,username:'old',characters:[{id:4,name:'Sleepy',level:9,online:false}],light:'grey',headline:'No logins in the last 7 days',fix:'',last:null,last_day:null,build:null,build_text:'',build_behind:false,problems:0,logins:0,days:[],timeline:[]}],
  counts:{red:1,amber:1,green:1,grey:1},builds:[{build:4102005,text:'4102005',accounts:1,newest:true},{build:4102004,text:'2.51.002 (4102004)',accounts:2,newest:false}],
  build_note:'Every client release so far sends the same version number.',check:checkOf('blocks'),writable:true},over||{});
const all=n=>{const out=[];(function w(x){if(!x||typeof x!=='object')return;out.push(x);(x.children||[]).forEach(w);})(n);return out;};
const strings=n=>{const out=[];(function w(x){if(typeof x==='string'||typeof x==='number'){out.push(String(x));return;}if(!x||typeof x!=='object')return;if(x.textContent)out.push(x.textContent);(x.children||[]).forEach(w);})(n);return out.join(' ');};
const J=x=>JSON.parse(JSON.stringify(x));
const tick=()=>new Promise(r=>setImmediate(r));
async function mount(ov,extra){const el=node('div');const calls=[];
  await def.mount(el,{route:[],navigate(){},api:async(sub,body)=>{calls.push({sub,body});if(extra){const x=extra(sub,body);if(x!==undefined)return x;}if(sub.startsWith('overview'))return J(typeof ov==='function'?ov():ov);return {};}});await tick();
  const players=()=>all(el).filter(x=>x.class&&/^ph-player ph-player-/.test(x.class));
  return {el,calls,players,btn:rx=>all(el).find(x=>x.tag==='button'&&rx.test(strings(x))),input:label=>all(el).find(x=>(x.tag==='input'||x.tag==='select')&&x['aria-label']===label)};}
(async()=>{
 // 1. the picture: cards, what changed, top problems, red first
 let p=await mount(overview());let s=strings(p.el);
 assert.match(s,/Cannot log in 1 fix these first/);assert.match(s,/Players with problems 2 1 red, 1 yellow, 1 healthy/);assert.match(s,/Logins today 12/);assert.match(s,/Failed today 3 \(25%\)/);
 assert.match(s,/What changed:\s+Failed logins rose from 0% to 60% in the day after the server restart/);
 assert.match(s,/Server error while entering the world 3 1/);assert.match(s,/2 logins used a user name nobody has/);
 assert.deepEqual(p.players().map(x=>x.class),['ph-player ph-player-red','ph-player ph-player-amber','ph-player ph-player-green','ph-player ph-player-grey']);
 const first=p.players()[0];s=strings(first);
 assert.match(s,/Tester \(level 200\)/);assert.match(s,/A packed Seal Jade has no pet inside it/);assert.match(s,/What to do: Remove this Seal Jade/);assert.match(s,/older than the newest seen/);
 assert.match(strings(p.players()[1]),/online now/);
 // the timeline is there, hidden until asked for, and says what happened in plain words
 const tl=all(first).find(x=>x.class==='ph-timeline');assert.equal(tl.hidden,true);
 const show=all(first).find(x=>x.tag==='button'&&/Show logins/.test(strings(x)));show.onclick();assert.equal(tl.hidden,false);assert.equal(show['aria-expanded'],'false'.replace('false','true'));
 assert.match(strings(tl),/Server error while entering the world/);assert.match(strings(tl),/A packed Seal Jade in the bag has no pet inside it/);assert.match(strings(tl),/203\.0\.113\.0/);
 assert.equal(all(p.players()[3]).find(x=>x.tag==='button').disabled,true,'no logins: nothing to show');
 // every status dot has a text label (not colour alone)
 assert.ok(all(p.el).filter(x=>x.class&&/^ph-dot /.test(x.class)).every(x=>x['aria-label']&&x.role==='img'));
 // 2. login check: the blocked character, its finding and a Fix button
 const box=all(p.el).find(x=>x.class==='ph-check ph-check-red');s=strings(box);
 assert.match(s,/Tester/);assert.match(s,/cannot log in/);assert.match(s,/Bag page 1, slot 18/);
 assert.match(strings(p.el),/2 characters checked: 1 cannot log in, 0 with something hidden, 1 fine/);
 const fixBtn=p.btn(/^Remove this Seal Jade \(it has no pet inside\)$/);
 confirmAnswer=false;confirms.length=0;p.calls.length=0;await fixBtn.onclick();assert.equal(p.calls.filter(c=>c.sub==='fix').length,0,'declined: nothing sent');
 assert.match(confirms[0].message,/Tester: A packed Seal Jade has no pet inside it \(Bag page 1, slot 18\)/);assert.match(confirms[0].message,/Undo puts it back/);assert.equal(confirms[0].danger,true);
 // accepted: the fix goes out as shown, the page reloads
 let fixed=false;
 p=await mount(()=>fixed?overview({check:Object.assign(checkOf('hidden'),{results:[checkOf('hidden').results[1]],blocked:0,hidden:0,checked:2})}):overview(),(sub,body)=>{if(sub==='fix'){fixed=true;return {mode:'database',change:{id:'rb-1'},removed:{name:'Seal Jade(Packed)',where:'Bag page 1, slot 18'},check:{light:'green',findings:[]}};}});
 confirmAnswer=true;done.length=0;await p.btn(/^Remove this Seal Jade/).onclick();await tick();
 assert.deepEqual(J(p.calls.find(c=>c.sub==='fix').body),{character_id:10,fix:jadeFix});
 assert.equal(done[0].l,'Removed Seal Jade(Packed) from Tester');
 assert.match(strings(p.el),/Nothing would stop any character logging in/,'reloaded after the fix');
 // 3. filters
 p=await mount(overview());
 const prob=p.input('Problems only');prob.checked=true;prob.onchange();assert.equal(p.players().length,2);assert.match(strings(p.el),/2 of 4 players/);
 prob.checked=false;prob.onchange();
 const day=p.input('Day');day.value='2026-11-12';day.onchange();assert.deepEqual(p.players().map(x=>/Kiddo/.test(strings(x))),[true]);day.value='';day.onchange();
 const ver=p.input('Client version');ver.value='4102004';ver.onchange();assert.equal(p.players().length,2);ver.value='';ver.onchange();
 const find=p.input('Find a player');find.value='oth';find.oninput();assert.equal(p.players().length,1);find.value='zzz';find.oninput();assert.match(strings(p.el),/Nobody matches/);
 const period=p.input('Period');period.value='30';p.calls.length=0;await period.onchange();assert.equal(p.calls[0].sub,'overview?days=30');
 // 4. "Check every character now" repaints from the check call
 p=await mount(overview(),(sub)=>sub==='check'?checkOf('hidden'):undefined);
 await p.btn(/^Check every character now$/).onclick();await tick();
 assert.match(strings(p.el),/2 characters checked: 0 cannot log in, 1 with something hidden, 1 fine/);assert.match(strings(p.el),/logs in, with something hidden/);
 // 5. before the server update: no login records, the Login check still works; a read-only editor cannot fix
 p=await mount(overview({records_ready:false,records_note:'Login records start with the next game server update. The Login check works now.',check:Object.assign(checkOf('blocks'),{server:false,why:'The running game build cannot do the full dry run yet.'})}));
 s=strings(p.el);assert.match(s,/Login records start with the next game server update/);assert.match(s,/cannot do the full dry run yet/);assert.match(s,/Logins today -/);assert.doesNotMatch(s,/Most common problems/);
 assert.ok(p.btn(/^Remove this Seal Jade/),'the fix is offered without login records');
 p=await mount(overview({writable:false}));assert.equal(p.btn(/^Remove this Seal Jade/).disabled,true);
 // 6. shared pieces: the banner on character pages and the alert after a write
 const handlers={};const G2={h:GW.h,clear:GW.clear,status(){},emit(){},on:(e,f)=>{handlers[e]=f;return()=>{delete handlers[e];};},alert:async(m,t)=>{alerts.push({m,t});},api:async()=>({character:'Tester',light:'red',findings:[jade('blocks')]}),text:x=>x,confirm:async()=>true};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../static/shared/fields.js'),'utf8'),{window:{GW:G2},console,document:{createElement:()=>node('x')},setInterval(){},clearInterval(){},setTimeout(){},location:{hash:''}});
 const banner=G2.loginCheckBanner(10);await tick();await tick();
 assert.equal(banner.hidden,false);assert.match(strings(banner),/Tester cannot log in right now/);assert.match(strings(banner),/A packed Seal Jade has no pet inside it \(Bag page 1, slot 18\)/);
 assert.equal(all(banner).find(x=>x.tag==='a').href,'#/player_health');
 G2.api=async()=>({character:'Tester',light:'green',findings:[]});await banner.refresh();assert.equal(banner.hidden,true,'nothing to say when the character can log in');
 alerts.length=0;G2.writeDone({change:{action:'x'},login_check:{character:'Tester',findings:[jade('blocks')],sentences:['This would stop Tester logging in: A packed Seal Jade has no pet inside it (Bag page 1, slot 18).']}},'saved');
 assert.equal(alerts.length,1);assert.match(alerts[0].m,/This would stop Tester logging in/);assert.equal(alerts[0].t,'This change would stop the login');
 alerts.length=0;G2.writeDone({change:{action:'x'}},'saved');assert.equal(alerts.length,0,'an ordinary write says nothing extra');
 console.log('PASS Player health UI: cards, what changed, top problems; players red first with reason, what to do and a labelled status dot; timeline on demand; Login check with a confirmed Fix that sends the shown finding and reloads; filters (problems, day, version, name, period); re-check; no-records and read-only states; character-page banner and the alert after a write. Browser rendering not tested here.');
})().catch(e=>{console.error(e);process.exitCode=1;});
