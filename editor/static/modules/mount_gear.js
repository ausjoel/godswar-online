(function(){
  'use strict';const GW=window.GW,h=GW.h;
  const LOC={0:'Worn',1:'Bag',3:'Warehouse'};
  GW.module('mount_gear',{
    async mount(el,ctx){
      let alive=true,serial=0,D=null,current=null,view=null,busy=false,off=null;
      this.cleanup=()=>{alive=false;serial++;if(off)off();};
      el.append(GW.onlineBanner(ctx.cid),h('p',{class:'gw-dim'},'Choose up to five ordinary Epic G10 stats, or change a mount piece’s template while retaining its existing stats, quality, grade, binding and sockets. Copy creates a new item in the first empty bag cell. The character must be logged out.'));
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
      source.addEventListener('change',select);mode.addEventListener('change',()=>{invalidate();if(current)targets();});target.addEventListener('change',invalidate);epic.addEventListener('change',invalidate);
      el.append(h('div',{class:'mg-controls'},h('label',null,'Source ',source),h('label',null,'Operation ',mode),h('label',null,'Target ',target),h('label',null,epic,' Set Epic G10 stats (replace ordinary stats)')),
        checks,h('div',{class:'mg-controls'},previewButton,applyButton),status,result);
      async function load(){
        const ticket=++serial;const old=current&&String(current.row.id);lock(true);
        try{const data=await ctx.api('gear/'+ctx.cid);if(!alive||ticket!==serial)return;D=data;GW.clear(source);
          for(const i of D.items)source.append(h('option',{value:String(i.row.id)},LOC[i.location]+' '+(i.slot+1)+': '+i.name+' · Q'+i.row.item_quality+'/G'+i.row.item_grade));
          if(old&&D.items.some(i=>String(i.row.id)===old))source.value=old;select();
        }catch(e){if(alive)status.textContent=e.message;}
        finally{if(alive)lock(false);if(D&&!D.items.length)previewButton.disabled=true;}
      }
      async function preview(){
        if(busy||!current)return;const ticket=++serial;view=null;lock(true);
        try{const p=await ctx.api('preview/'+ctx.cid,options());if(!alive||ticket!==serial)return;view=p;GW.clear(result);
          status.textContent=(p.changed?'Ready to apply.':'No changes.')+' Applies at next login.';
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
})();
