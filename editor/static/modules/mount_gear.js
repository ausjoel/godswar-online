(function(){
  'use strict';const GW=window.GW,h=GW.h;
  const LOC={0:'Worn',1:'Bag',3:'Warehouse'};
  GW.module('mount_gear',{
    async mount(el,ctx){
      let alive=true,serial=0,D=null,current=null,view=null,busy=false,off=null;
      this.cleanup=()=>{alive=false;serial++;if(off)off();if(this.adderOff)this.adderOff();};
      el.append(GW.onlineBanner(ctx.cid));
      // W23 (the admin 24 Nov): the mount gear adder - mount level + all five pieces at once, best stats or four copied stats
      const adder=h('section',{class:'mg-adder'});el.append(adder);
      this.adderOff=drawAdder(adder,ctx,()=>{if(alive&&!busy)load();});
      el.append(h('h3',null,'One piece: stats & morph'),h('p',{class:'gw-dim'},'Choose up to five ordinary Epic G10 stats, or change a mount piece’s template while retaining its existing stats, quality, grade, binding and sockets. Copy creates a new item in the first empty bag cell. The character must be logged out.'));
      const source=h('select',{'aria-label':'Source mount gear'}),mode=h('select',{'aria-label':'Mount gear operation'},
        h('option',{value:'edit'},'Edit / morph this item'),h('option',{value:'copy'},'Copy into bag'));
      const target=h('select',{'aria-label':'Target mount gear'}),epic=h('input',{type:'checkbox','aria-label':'Set Epic G10 stats'});
      const checks=h('div',{class:'mg-stats'}),status=h('p',{role:'status'}),result=h('div');
      const previewButton=h('button',{class:'gw-btnx',type:'button',onclick:()=>preview()},'Preview');
      const applyButton=h('button',{class:'gw-btnx',type:'button',disabled:true,onclick:()=>apply()},'Apply');
      const controls=[source,mode,target,epic,previewButton];
      function lock(on){busy=on;controls.forEach(c=>c.disabled=on);for(const c of checks.querySelectorAll('input'))c.disabled=on;applyButton.disabled=on||!view||!view.changed||!view.writable;}
      function invalidate(){serial++;view=null;applyButton.disabled=true;GW.clear(result);status.textContent='Preview the current choices before applying.';}
      function keys(){return Array.from(checks.querySelectorAll('input:checked')).map(c=>c.value);}
      function options(){return {location:current.location,slot:current.slot,mode:mode.value,target:Number(target.value),epic_g10:epic.checked,stats:keys()};}
      function targets(){
        const previous=target.value;GW.clear(target);
        const eligible=D.targets.filter(t=>!current||mode.value==='copy'||current.location!==0||t.kind===current.kind);
        for(const t of eligible)target.append(h('option',{value:String(t.id)},t.name+' · '+t.kind+' · Lv'+t.min_level));
        target.value=eligible.some(t=>String(t.id)===previous)?previous:String(current.row.prop_id);
      }
      function select(){
        current=D.items.find(i=>String(i.row.id)===source.value);invalidate();
        if(!current){status.textContent='This character has no mount gear in equipment, bags or warehouse.';previewButton.disabled=true;return;}
        target.value=String(current.row.prop_id);targets();epic.checked=false;
        const selected=new Set([1,2,3,4,5].map(n=>current.row['attribute'+n]).filter(a=>a!=null).map(a=>Math.floor(Number(a)/10)*10));
        GW.clear(checks);
        for(const stat of D.stats){const input=h('input',{type:'checkbox',value:stat.key,checked:selected.has(Math.floor(stat.id/10)*10)});
          input.addEventListener('change',()=>{if(keys().length>5){input.checked=false;GW.status('Choose at most five stats.','err');}invalidate();});
          checks.append(h('label',null,input,stat.name+' +'+stat.display));}
        status.textContent='Keeping current stats unless Set Epic G10 stats is selected. Percentages use the game’s one-decimal display.';
      }
      source.addEventListener('change',()=>{select();drawSlots();});mode.addEventListener('change',()=>{invalidate();if(current)targets();});target.addEventListener('change',invalidate);epic.addEventListener('change',invalidate);
      // B21 item 26: the mount slots and every mount-gear piece as in-game cells (hover = the game tooltip, click = choose it)
      const slots=h('div',{class:'mg-slots'}),cells=h('div',{class:'mg-beforeafter'});
      const loadGearUI=()=>GW.gearUI?Promise.resolve():new Promise((res,rej)=>{const s=document.createElement('script');s.src='/static/modules/_gear_ui.js';s.onload=res;s.onerror=()=>rej(new Error('cannot load the item slots'));document.body.append(s);});
      const asRow=i=>Object.assign({prop_id:i.row.prop_id,sprite:i.sprite,name:i.name,slot_index:i.slot},i.row);
      async function drawSlots(){
        GW.clear(slots);
        try{await loadGearUI();}catch(e){slots.append(h('p',{class:'tt-bad'},e.message));return;}
        let worn={};
        try{const st=await GW.gearUI.state(ctx.cid);for(const [s,r] of Object.entries(st.worn))if(Number(s)>=15)worn[s]=r;}catch(e){/* the mount-gear rows below still show */}
        for(const i of D.items)if(i.location===0)worn[i.slot]=asRow(i);
        const choose=(loc,s)=>{const i=D.items.find(x=>x.location===loc&&x.slot===s);if(!i)return;source.value=String(i.row.id);select();drawSlots();};
        const mark=loc=>(s,row)=>row&&current&&current.location===loc&&current.slot===s?'gw-gearui-on':'';
        const bag=D.items.filter(i=>i.location===1).map(asRow),wh=D.items.filter(i=>i.location===3).map(asRow);
        slots.append(h('div',{class:'gw-gearui-row'},GW.gearUI.doll({tab:'mount',title:'Mount slots',rows:worn,mark:mark(0),onClick:s=>choose(0,s)}),
          h('div',null,h('div',{class:'gw-gearui-dolltitle'},'Mount gear in the bag'),GW.gearUI.bag({rows:bag,mark:mark(1),onClick:s=>choose(1,s)})),
          wh.length?h('div',null,h('div',{class:'gw-gearui-dolltitle'},'Mount gear in the warehouse'),GW.gearUI.bag({rows:wh,label:'Box',pages:9,mark:mark(3),onClick:s=>choose(3,s)})):null));
      }
      el.append(slots);
      el.append(h('div',{class:'mg-controls'},h('label',null,'Source ',source),h('label',null,'Operation ',mode),h('label',null,'Target ',target),h('label',null,epic,' Set Epic G10 stats (replace ordinary stats)')),
        checks,h('div',{class:'mg-controls'},previewButton,applyButton),status,result);
      async function load(){
        const ticket=++serial;const old=current&&String(current.row.id);lock(true);
        try{const data=await ctx.api('gear/'+ctx.cid);if(!alive||ticket!==serial)return;D=data;GW.clear(source);
          for(const i of D.items)source.append(h('option',{value:String(i.row.id)},LOC[i.location]+' '+(i.slot+1)+': '+i.name+' · Q'+i.row.item_quality+'/G'+i.row.item_grade));
          if(old&&D.items.some(i=>String(i.row.id)===old))source.value=old;select();drawSlots();
        }catch(e){if(alive)status.textContent=e.message;}
        finally{if(alive)lock(false);if(D&&!D.items.length)previewButton.disabled=true;}
      }
      async function preview(){
        if(busy||!current)return;const ticket=++serial;view=null;lock(true);
        try{const p=await ctx.api('preview/'+ctx.cid,options());if(!alive||ticket!==serial)return;view=p;GW.clear(result);
          status.textContent=(p.changed?'Ready to apply.':'No changes.')+' Applies at next login.';
          if(GW.gearUI){const src=D.items.find(i=>String(i.row.id)===source.value)||{};
            result.append(h('div',{class:'mg-beforeafter'},h('span',null,'Now '),GW.gearUI.cell(Object.assign({sprite:src.sprite,name:p.source_name},p.before)),
              h('span',null,' → '),GW.gearUI.cell(Object.assign({sprite:(GW.catalog.loadedRow('item',p.after.prop_id)||{}).sprite,name:p.name},p.after),{mark:'gw-gearui-changed'}),
              h('span',{class:'gw-dim'},' (hover for the game tooltip)')));}
          result.append(h('h3',null,p.source_name+' → '+p.name),h('p',null,'Destination: '+LOC[p.destination.location]+' '+(p.destination.slot+1)+' · Q'+p.before.item_quality+'/G'+p.before.item_grade+' → Q'+p.after.item_quality+'/G'+p.after.item_grade),
            h('ul',null,p.attributes.map(s=>h('li',null,s.name+' +'+s.display))));
          if(!p.options.epic_g10)result.append(h('p',null,'All existing appended stat IDs and levels are retained. The target template’s own base bonuses may change.'));
          result.append(h('p',null,'Binding, item EXP, class/element fields and sockets are retained.'));
          if(p.warnings.length)result.append(h('p',{class:'tt-bad'},p.warnings.join('; ')));
        }catch(e){if(alive&&ticket===serial)status.textContent=e.message;}
        finally{if(alive&&ticket===serial)lock(false);}
      }
      async function apply(){
        if(busy||!view||!view.writable||!view.changed)return;const p=view;let refresh=false;lock(true);
        try{if(!await GW.confirm({title:'Apply mount gear change?',message:p.source_name+' → '+p.name+'. '+(p.options.mode==='copy'?'A new item is created in the bag.':'The current item changes in place.')+' The exact preview is saved in one transaction with backup and Undo.',ok:'Apply'}))return;
          if(!alive)return;refresh=true;const r=await ctx.api('apply/'+ctx.cid,Object.assign({},p.options,{token:p.token}));if(alive){GW.writeDone(r,'Mount gear saved');view=null;}
        }catch(e){if(alive){view=null;await GW.writeError(e);}}
        finally{if(alive){lock(false);if(refresh)await load();}}
      }
      off=GW.on('change',()=>{if(alive&&!busy)load();});await load();
    },unmount(){if(this.cleanup)this.cleanup();}
  });
  // ------------------------------------------------------------------ W23 mount gear adder
  function drawAdder(box,ctx,reloadPieces){
    let alive=true,S=null,view=null,busy=false;
    const fam=h('select',{'aria-label':'Mount family'}),mlv=h('select',{'aria-label':'Mount level'}),glv=h('select',{'aria-label':'Mount gear level'});
    const q=h('input',{type:'number',min:1,max:20,step:1,class:'mg-num','aria-label':'Quality'}),g=h('input',{type:'number',min:1,max:25,step:1,class:'mg-num','aria-label':'Grade'});
    const modeAuto=h('input',{type:'radio',name:'mg-adder-mode',value:'auto',checked:true}),modeCopy=h('input',{type:'radio',name:'mg-adder-mode',value:'copy'});
    const picks=h('div',{class:'mg-stats mg-adder-picks'}),out=h('div'),status=h('p',{role:'status',class:'gw-dim'});
    const prevB=h('button',{class:'gw-btnx',type:'button',onclick:()=>preview()},'Preview');
    const applyB=h('button',{class:'gw-btnx',type:'button',disabled:true,onclick:()=>apply()},'Apply to all five');
    const ctl=[fam,mlv,glv,q,g,modeAuto,modeCopy,prevB];
    const lock=on=>{busy=on;ctl.forEach(c=>c.disabled=on);for(const c of picks.querySelectorAll('input'))c.disabled=on||!modeCopy.checked;applyB.disabled=on||!view||!view.changed||!view.writable;};
    const reset=()=>{view=null;applyB.disabled=true;GW.clear(out);status.textContent='Preview first: nothing is written until you apply.';};
    const keys=()=>Array.from(picks.querySelectorAll('input:checked')).map(c=>c.value);
    for(const c of [fam,mlv,glv,q,g])c.addEventListener('change',reset);
    for(const c of [modeAuto,modeCopy])c.addEventListener('change',()=>{reset();lock(false);});
    box.append(h('h3',{'data-help':'Sets the mount and all five mount-gear pieces (Coronet, Armor, Soul, Saddle, Tassels) in one step.\n• Levels go 40, 50 ... 120 (Boorish to Faithcreed); each gear grade needs 10 more levels, like the Mount Feeder.\n• Character level >= mount level >= gear level, as the game requires.\n• The gear comes out bound. Applies at the next login; the character must be logged out.'},'Mount gear adder'),
      h('div',{class:'mg-controls'},h('label',{'data-help':'The mount family in the Mount slot. Empty slot or a special mount: choose one to put there.'},'Mount ',fam),
        h('label',null,'Mount level ',mlv),h('label',{'data-help':'Gear level 40-120. Cannot be above the mount level.'},'Gear level ',glv),
        h('label',{'data-help':'Quality 1-20 (colour). 20 is the highest your client shows.'},'Quality ',q),h('label',{'data-help':'Grade 1-25: the size of the four stats.'},'Grade ',g)),
      h('div',{class:'mg-controls'},h('label',{'data-help':'For each piece, the highest tier that piece allows of the four stats that suit the class (physical or magic).'},modeAuto,' Auto best stats'),
        h('label',{'data-help':'Pick up to four stats; every piece gets them, each at the highest tier that piece allows.'},modeCopy,' Same four stats on all five pieces')),
      picks,h('div',{class:'mg-controls'},prevB,applyB),status,out);
    function fill(sel,vals,label,cur){GW.clear(sel);for(const v of vals)sel.append(h('option',{value:String(v)},label(v)));if(cur!=null&&vals.map(String).includes(String(cur)))sel.value=String(cur);}
    async function load(){
      try{S=await ctx.api('adder/'+ctx.cid);}catch(e){if(alive)status.textContent=e.message;return;}
      if(!alive)return;
      GW.clear(fam);fam.append(h('option',{value:''},S.mount&&S.mount.family?'(keep this family)':'choose a family'));
      for(const f of S.families)fam.append(h('option',{value:f.id},f.name));
      if(S.mount&&S.mount.family)fam.value=S.mount.family;
      const lv=S.levels.filter(v=>v<=S.character.level);
      fill(mlv,lv.length?lv:[S.levels[0]],v=>'Lv'+v+(v===120?' (top)':''),Math.min(120,Math.max(...(lv.length?lv:[40]))));
      fill(glv,S.levels,v=>'Lv'+v+(v===40?' Boorish':v===120?' Faithcreed':''),mlv.value);
      q.value=S.quality.default;g.value=S.grade.default;
      GW.clear(picks);
      for(const st of S.stats){const i=h('input',{type:'checkbox',value:st.key,checked:S.best.includes(st.key)});
        i.addEventListener('change',()=>{if(keys().length>S.max_stats){i.checked=false;GW.status('Choose at most '+S.max_stats+' stats.','err');}reset();});
        picks.append(h('label',null,i,st.name));}
      status.textContent=(S.mount?'Mount now: '+S.mount.name+' (Lv'+S.mount.level+'). ':'No mount equipped. ')+S.character.name+' is a level '+S.character.level+' '+S.character.class+'; auto best uses '+S.best.length+' stats that suit the class.';
      lock(false);
    }
    function opts(){return {family:fam.value||null,mount_level:Number(mlv.value),gear_level:Number(glv.value),quality:Number(q.value),grade:Number(g.value),mode:modeCopy.checked?'copy':'auto',stats:modeCopy.checked?keys():undefined};}
    async function preview(){
      if(busy)return;lock(true);view=null;GW.clear(out);
      try{const v=await ctx.api('adder/preview/'+ctx.cid,opts());if(!alive)return;view=v;
        const rows=v.pieces.map(p=>h('tr',null,h('td',null,p.what),h('td',null,p.before?(GW.catalog.loadedRow('item',p.before.prop_id)||{}).name||('#'+p.before.prop_id):'(empty)'),
          h('td',null,p.name+' · Lv'+p.level+(p.slot===20?'':' · Q'+p.after.item_quality+'/G'+p.after.item_grade+(p.after.bound?' · bound':''))),
          h('td',null,p.stats.map(x=>x.name+' +'+x.display).join(', ')||'-')));
        out.append(h('table',{class:'mg-adder-tbl'},h('tr',null,h('th',null,'Slot'),h('th',null,'Now'),h('th',null,'After'),h('th',null,'Stats')),rows));
        if(v.warnings.length)out.append(h('ul',{class:'tt-bad'},v.warnings.map(w=>h('li',null,w))));
        status.textContent=(v.changed?'Ready to apply.':'Nothing to change.')+' Applies at the next login.';
      }catch(e){if(alive)status.textContent=e.message;}
      finally{if(alive)lock(false);}
    }
    async function apply(){
      if(busy||!view)return;const v=view;
      if(!await GW.confirm({title:'Set the mount and all five pieces?',message:v.family+' Lv'+v.options.mount_level+', gear Lv'+v.options.gear_level+' Q'+v.options.quality+'/G'+v.options.grade+'. One write with a backup; Undo is in the change log.',ok:'Apply'}))return;
      lock(true);
      try{const r=await ctx.api('adder/apply/'+ctx.cid,Object.assign({},v.options,{token:v.token}));if(alive){GW.writeDone(r,'Mount and mount gear set');reset();await load();reloadPieces();}}
      catch(e){if(alive){reset();await GW.writeError(e);}}
      finally{if(alive)lock(false);}
    }
    load();
    return ()=>{alive=false;};
  }
})();
