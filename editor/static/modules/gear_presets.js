/* Previewed offline batch edit; options invalidate the complete server plan.
   B21 items 25 + 26: every preset knob (quality, grade, stat count, stones and their level, holy-suit tier and level,
   class-suit tier and class stone, only some slots, fill empty slots), and the worn gear / bag drawn as in-game slots:
   "Now" and "After" paperdolls with the game tooltip on hover; click a slot in "After" to leave it out or put it back. */
(function () {
  'use strict';
  const GW=window.GW,h=GW.h;
  const value=s=>s.value==null?'unknown':s.percent?(Number(s.value)*100).toLocaleString(undefined,{maximumFractionDigits:4})+'%':GW.fmt(s.value);
  const loadGearUI=()=>GW.gearUI?Promise.resolve():new Promise((res,rej)=>{const s=document.createElement('script');s.src='/static/modules/_gear_ui.js';s.onload=res;s.onerror=()=>rej(new Error('cannot load the item slots'));document.body.append(s);});
  const SUITS=['Ordinary','Bronze','Silver','Gold','Platinum','Mithril','Orichalcum','Adamantium'];
  const STONES={0:[[200,'Blood: Physical Attack'],[201,'Courage: Hit Chance'],[210,'Energy: Physical Damage'],[211,'Rage: Crit Strike Chance']],
    2:[[220,'Holy: Spell Power'],[221,'Blessing: Healing Bonus'],[230,'Rune: Spell Damage'],[231,'Force: Crit Strike Chance']]};
  const opt=(v,t,sel)=>h('option',{value:String(v),selected:sel?true:null},t);
  const range=(lo,hi,f)=>Array.from({length:hi-lo+1},(_,i)=>f(lo+i));
  GW.module('gear_presets',{
    async mount(el,ctx) {
      let alive=true,request=0,view=null,busy=false,unsubscribe=null,timer=null,st=null,excluded=new Set();
      this.cleanup=()=>{alive=false;request++;clearTimeout(timer);if(unsubscribe)unsubscribe();};
      el.append(GW.onlineBanner(ctx.cid));
      const preset=h('select',{'aria-label':'Gear preset','data-help':'The starting point. Every box below can change it.'},
        opt('standard','Four stats · two Lv10 stones · Platinum 10'),
        opt('boundless','Q20 Boundless / G25 · five stats · four Lv10 stones'));
      const quality=h('select',{'aria-label':'Quality','data-help':'Item quality (colour). Keep = leave each item as it is. The client shows up to Q20.'},opt('','Keep'),range(1,20,q=>opt(q,'Q'+q)));
      const grade=h('select',{'aria-label':'Grade','data-help':'Grade (stars). Every ordinary and class stat amount follows the grade. The client shows up to G25.'},opt('','Keep'),range(1,25,g=>opt(g,'G'+g)));
      const gradeQuick=h('span',{class:'gp-quick'},[10,15,20,25].map(g=>h('button',{class:'gw-chipbtn',type:'button','data-help':'Set grade '+g,onclick:()=>{grade.value=String(g);changed();}},'G'+g)));
      const stats=h('select',{'aria-label':'Ordinary stats','data-help':'How many green (ordinary) stats each piece gets, from the legal families for its slot. The class stone is a separate, extra stat.'},range(0,5,n=>opt(n,n+' stats',n===4)));
      const stones=h('select',{'aria-label':'Holy stones','data-help':'How many holy-stone sockets are drilled and filled (the client shows 4).'},range(0,4,n=>opt(n,n+' stones',n===2)));
      const stoneLv=h('select',{'aria-label':'Holy stone level','data-help':'Level of every stone. A stone\'s range is its level x its level-1 range; lower levels scale the level-10 values down.'},range(1,10,n=>opt(n,'Lv'+n,n===10)));
      const suitTier=h('select',{'aria-label':'Holy suit tier','data-help':'Holy suit tier of every piece (Bronze ... Adamantium). Worn pieces add holy-suit points.'},SUITS.map((n,i)=>opt(i,n,i===4)));
      const suitLv=h('select',{'aria-label':'Holy suit level'},range(1,10,n=>opt(n,'Level '+n,n===10)));
      const tier=h('select',{'aria-label':'Class suit conversion','data-help':'Turn each piece into its class-suit version (never lowers an existing tier). Without "Also on other gear", class stones need Class Suit III or IV.'},opt(0,'Keep item types'),
        opt(1,'At least Class Suit I'),opt(2,'At least Class Suit II'),opt(3,'At least Class Suit III'),opt(4,'Class Suit IV'));
      const classStat=h('select',{'aria-label':'Class stone','data-help':'The class-suit stat stone from the points exchanger: one per item, a 5th stat next to the ordinary ones. Needs Class Suit III or IV, unless "Also on other gear" is ticked.'},opt('','No class stone'));
      const fill=h('input',{type:'checkbox','aria-label':'Fill empty slots'});
      const special=h('input',{type:'checkbox','aria-label':'Class stone also on other gear'});
      const previewButton=h('button',{class:'gw-btnx',type:'button',onclick:()=>preview()},'Preview');
      const applyButton=h('button',{class:'gw-btnx',type:'button',disabled:true,onclick:()=>apply()},'Apply preset');
      const status=h('p',{role:'status'}),visual=h('div',{class:'gp-visual'}),result=h('div');
      const inputs=[preset,quality,grade,stats,stones,stoneLv,suitTier,suitLv,tier,classStat,fill,special];
      const controls=inputs.concat([previewButton]);
      function onPreset(){const b=preset.value==='boundless';quality.value=b?'20':'';grade.value=b?'25':'';stats.value=b?'5':'4';stones.value=b?'4':'2';}
      preset.addEventListener('change',()=>{onPreset();changed();});
      function options(){
        const o={preset:preset.value,stats:Number(stats.value),stones:Number(stones.value),stone_level:Number(stoneLv.value),
          suit_tier:Number(suitTier.value),suit_level:Number(suitLv.value),class_tier:Number(tier.value),class_stat:classStat.value!=='',fill_empty:fill.checked,
          quality:quality.value===''?null:Number(quality.value),grade:grade.value===''?null:Number(grade.value)};
        if(classStat.value!=='')o.class_stat_id=Number(classStat.value);
        if(excluded.size)o.slots=range(0,11,s=>s).filter(s=>!excluded.has(s));
        if(special.checked)o.non_suit_special=true;
        return o;
      }
      const lock=on=>{busy=on;controls.forEach(c=>c.disabled=on);applyButton.disabled=on||!view||!view.writable||!view.items.some(i=>i.changed);};
      function changed(){request++;view=null;applyButton.disabled=true;status.textContent='Options changed: previewing ...';clearTimeout(timer);timer=setTimeout(preview,300);}
      inputs.filter(c=>c!==preset).forEach(c=>c.addEventListener('change',changed));
      suitTier.addEventListener('change',()=>{suitLv.disabled=suitTier.value==='0';});
      el.append(h('p',{class:'gw-dim'},'Updates armor and weapon slots 0-11 only. Fashion, mounts and mount gear stay in their own systems. The character must be logged out. One transaction, backup and Undo; no crafting materials are consumed.'),
        h('p',{class:'gw-dim'},'Stat priorities are our own choice (not from the original game): attack stats on offensive slots, defenses and HP on the others. Heated stones use the same 5% damage and 8% ignore-defense values at level 10 for physical and magical classes; Cooled values follow the current server balance.'),
        h('div',{class:'gp-controls'},h('label',null,'Preset ',preset),h('label',null,'Quality ',quality),h('label',null,'Grade ',grade),gradeQuick,h('label',null,'Stats ',stats)),
        h('div',{class:'gp-controls'},h('label',null,'Stones ',stones),h('label',null,'Stone level ',stoneLv),h('label',null,'Holy suit ',suitTier),suitLv),
        h('div',{class:'gp-controls'},h('label',null,'Class suit ',tier),h('label',null,'Class stone (5th stat) ',classStat),
          h('label',{'data-help':'Empty armor or weapon slots get the best item the class can wear at the character\'s level, then the same preset.'},fill,' Fill empty slots'),
          h('label',{'data-help':'Put the class stone on every chosen piece, also gear that is not a class suit, as players can reach in game. The preview says so if this database does not allow it yet.'},special,' Also on other gear')),
        h('div',{class:'gp-controls'},previewButton,applyButton),status,visual,result);
      function fillStones(profession){
        const group=profession>=2?2:0,cur=classStat.value;GW.clear(classStat).append(opt('','No class stone'));
        for(const [id,n] of STONES[group])classStat.append(opt(id,n+' (#'+id+')',String(id)===cur));
      }
      async function drawVisual(data){
        GW.clear(visual);
        try{await loadGearUI();st=await GW.gearUI.state(ctx.cid);}catch(e){visual.append(h('p',{class:'tt-bad'},e.message));return;}
        if(!alive)return;
        const after={},rowBy={};
        for(const r of data.items){rowBy[r.slot]=r;after[r.slot]=Object.assign({prop_id:r.after.prop_id,sprite:r.sprite,name:r.name},r.after);}
        for(const [s,r] of Object.entries(st.worn))if(Number(s)<12&&!after[s])after[s]=r;
        const UI=GW.gearUI;
        visual.append(h('div',{class:'gw-gearui-legend'},h('span',{class:'lg-changed'},'changes'),h('span',{class:'lg-new'},'new item'),h('span',{class:'lg-off'},'left out (click to put back)')),
          h('div',{class:'gw-gearui-row'},
            UI.doll({title:'Now',rows:st.worn,help:(s,row)=>row?null:'Empty slot.',onClick:(s,row)=>{if(row)location.hash='#/gearstats/'+ctx.cid+'?slot='+s;}}),
            UI.doll({title:'After',rows:after,mark:(s,row)=>excluded.has(s)?'gw-gearui-off':rowBy[s]&&rowBy[s].new?'gw-gearui-new':rowBy[s]&&rowBy[s].changed?'gw-gearui-changed':'',
              help:(s,row)=>excluded.has(s)?'Left out of this preset: click to include it again.':row?null:'Empty slot'+(fill.checked?' (nothing the class can wear at this level)':': tick "Fill empty slots" to add gear.'),
              onClick:s=>{if(excluded.has(s))excluded.delete(s);else if(rowBy[s]||st.worn[s])excluded.add(s);else return;changed();}}),
            h('div',null,h('div',{class:'gw-gearui-dolltitle'},'Kit bag (unchanged)'),UI.bag({rows:st.bag,onClick:()=>{location.hash='#/inventory/'+ctx.cid;}}))));
      }
      function draw(data) {
        GW.clear(result);
        fillStones(data.character.profession);
        status.textContent=data.character.name+' · '+data.character.class+' Lv'+data.character.level+' · '+data.items.filter(i=>i.changed).length+' of '+data.items.length+' pieces change'+(excluded.size?' ('+excluded.size+' left out)':'')+'. Applies at next login.';
        const suit=data.options.suit_code?SUITS[Math.floor(data.options.suit_code/100)]+' '+(data.options.suit_code%100):'Ordinary';
        for(const row of data.items) {
          const after=row.after,b=row.before||{};
          result.append(h('section',{class:'gp-piece'},h('h3',null,row.slot_name+': '+row.name+(row.new?' (new)':'')),
            h('p',null,(row.new?'empty':'#'+b.prop_id+' Q'+b.item_quality+'/G'+b.item_grade)+' → #'+after.prop_id+' Q'+after.item_quality+'/G'+after.item_grade+' · '+suit),
            h('ul',null,row.stats.map(s=>h('li',null,s.name+' +'+value(s)+(s.level==null?' (grade '+after.item_grade+')':' (enhancement Lv'+s.level+' · grade '+after.item_grade+')')))),
            row.class_stat?h('p',null,'Class stone: '+row.class_stat.name+' +'+value(row.class_stat)+' (grade '+after.item_grade+')'):null,
            h('ul',null,row.stones.map(s=>h('li',null,s.name+' Lv'+s.level+' · '+(s.percent?s.value/100+'%':GW.fmt(s.value))))),
            row.warnings.length?h('p',{class:'tt-bad'},row.warnings.join('; ')):null));
        }
        if(data.skipped.length)result.append(h('p',{class:'gw-dim'},data.skipped.length+' fashion/mount/mount-gear entries are not part of this preset.'));
        drawVisual(data);
      }
      async function preview() {
        if(busy)return;
        const ticket=++request;view=null;lock(true);status.textContent='Checking all worn gear…';GW.clear(result);
        try {
          const data=await ctx.api('preview/'+ctx.cid,options());
          if(!alive||ticket!==request)return;
          view=data;draw(data);
        }catch(e){if(alive&&ticket===request){status.textContent=e.message;GW.clear(visual);}}
        finally{if(alive&&ticket===request)lock(false);}
      }
      async function apply() {
        if(busy||!view||!view.writable)return;
        const selected=view;lock(true);
        try {
          if(!await GW.confirm({title:'Apply worn-gear preset?',message:selected.character.name+' gets the exact preview on '+selected.items.length+' pieces ('+selected.items.filter(i=>i.new).length+' new). Existing ordinary stats and sockets are replaced; no materials are consumed. Undo is in the Change log.',ok:'Apply preset'}))return;
          if(!alive)return;
          const response=await ctx.api('apply/'+ctx.cid,Object.assign({},selected.options,{token:selected.token}));
          if(!alive)return;
          GW.writeDone(response,'Worn gear preset applied');view=null;
        }catch(e){if(alive){view=null;await GW.writeError(e);}}
        finally{if(alive){lock(false);await preview();}}
      }
      // ---- W4A: quick-add loadout (a full class set for a chosen level, four stats on each piece)
      const CLS=['Warrior','Champion','Priest','Mage'];
      const loCls=h('select',{'aria-label':'Loadout class'},CLS.map((n,i)=>h('option',{value:String(i)},n)));
      const loLv=h('input',{type:'number',min:1,max:200,value:'100','aria-label':'Loadout level',style:'width:5em'});
      const loQ=h('input',{type:'number',min:1,max:20,value:'10','aria-label':'Loadout quality',style:'width:4em'});
      const loG=h('input',{type:'number',min:1,max:25,value:'10','aria-label':'Loadout grade',style:'width:4em'});
      const loStatus=h('p',{role:'status'}),loResult=h('div');
      let lo=null;
      const loOpts=()=>({profession:Number(loCls.value),level:Number(loLv.value),quality:Number(loQ.value),grade:Number(loG.value)});
      const loApply=h('button',{class:'gw-btnx',type:'button',disabled:true,onclick:async()=>{
        if(!lo)return;
        if(!await GW.confirm({title:'Equip this loadout?',message:lo.character.name+' gets '+lo.items.length+' new pieces ('+CLS[lo.options.profession]+' level '+lo.options.level+', Q'+lo.options.quality+' G'+lo.options.grade+', four stats each). '+lo.items.filter(i=>i.replaces!=null).length+' worn pieces move to the kit bag. Undo is in the Change log.',ok:'Equip'}))return;
        try{const r=await ctx.api('loadout/apply/'+ctx.cid,Object.assign({},lo.options,{token:lo.token}));GW.writeDone(r,'Loadout equipped');lo=null;loApply.disabled=true;await loPreview();}
        catch(e){await GW.writeError(e);}
      }},'Equip loadout');
      async function loPreview(){
        lo=null;loApply.disabled=true;GW.clear(loResult);loStatus.textContent='Choosing gear…';
        try{
          const v=await ctx.api('loadout/preview/'+ctx.cid,loOpts());if(!alive)return;
          lo=v;loApply.disabled=!v.writable||!v.items.length;
          loStatus.textContent=v.items.length+' pieces for a level '+v.options.level+' '+CLS[v.options.profession]+(v.missing.length?' · nothing found for: '+v.missing.join(', '):'')+'. Applies at next login.';
          if(v.warnings.length)loResult.append(h('p',{class:'tt-bad'},v.warnings.join(' · ')));
          try{
            await loadGearUI();await GW.catalog.load('item');
            const rows={};for(const r of v.items)rows[r.slot]=Object.assign({prop_id:r.item,sprite:r.sprite,name:r.name},r.after);
            loResult.append(GW.gearUI.doll({title:'Loadout',rows,mark:s=>rows[s]?'gw-gearui-new':''}));
          }catch(e){/* the table below still lists everything */}
          loResult.append(h('table',{class:'gp-loadout'},h('tr',null,h('th',null,'Slot'),h('th',null,'Item'),h('th',null,'Lv'),h('th',null,'Stats'),h('th',null,'Now worn')),
              v.items.map(r=>h('tr',null,h('td',null,r.slot_name),h('td',{'data-tip-type':'item','data-tip-id':String(r.item)},r.name+' #'+r.item),h('td',null,String(r.level)),
                h('td',null,r.stats.map(s=>s.name+' +'+value(s)).join(', ')),h('td',{class:'gw-dim'},r.replaces!=null?'#'+r.replaces+' → bag cell '+(r.to_bag_slot+1):'empty')))));
        }catch(e){if(alive)loStatus.textContent=e.message;}
      }
      [loCls,loLv,loQ,loG].forEach(c=>c.addEventListener('change',()=>{lo=null;loApply.disabled=true;GW.clear(loResult);loStatus.textContent='Options changed: preview again.';}));
      if(typeof GW.api==='function')GW.api('/api/m/character/sheet/'+ctx.cid).then(d=>{if(d&&d.base){loCls.value=String(d.base.profession);loLv.value=String(d.base.fighter_job_lv);}}).catch(()=>{});
      el.append(h('h2',null,'Quick add loadout'),
        h('p',{class:'gw-dim'},'Equips a full set for the chosen class and level: in each of the twelve armor/weapon slots the highest-level item that class can wear at that level, with four legal stats (the same choice as the four-stat preset) at the chosen quality and grade. Worn pieces move to the kit bag first. GM and novelty items are never chosen.'),
        h('div',{class:'gp-controls'},h('label',null,'Class ',loCls),h('label',null,'Level ',loLv),h('label',null,'Quality ',loQ),h('label',null,'Grade ',loG),
          h('button',{class:'gw-btnx',type:'button',onclick:()=>loPreview()},'Preview loadout'),loApply),loStatus,loResult);
      unsubscribe=GW.on('change',()=>{if(alive&&!busy)preview();});
      await preview();
    },
    unmount(){if(this.cleanup)this.cleanup();}
  });
})();
