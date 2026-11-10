/* Previewed offline batch edit; options invalidate the complete server plan. */
(function () {
  'use strict';
  const GW=window.GW,h=GW.h;
  const value=s=>s.value==null?'unknown':s.percent?(Number(s.value)*100).toLocaleString(undefined,{maximumFractionDigits:4})+'%':GW.fmt(s.value);
  GW.module('gear_presets',{
    async mount(el,ctx) {
      let alive=true,request=0,view=null,busy=false,unsubscribe=null;
      this.cleanup=()=>{alive=false;request++;if(unsubscribe)unsubscribe();};
      el.append(GW.onlineBanner(ctx.cid));
      const preset=h('select',{'aria-label':'Gear preset'},
        h('option',{value:'standard'},'Four stats · two Lv10 stones · Platinum 10'),
        h('option',{value:'boundless'},'Q20 Boundless / G25 · five stats · four Lv10 stones'));
      const tier=h('select',{'aria-label':'Class suit conversion'},h('option',{value:'0'},'Keep item types'),
        h('option',{value:'1'},'At least Class Suit I'),h('option',{value:'2'},'At least Class Suit II'),
        h('option',{value:'3'},'At least Class Suit III'),h('option',{value:'4'},'Class Suit IV'));
      const classStat=h('input',{type:'checkbox','aria-label':'Add class suit stat'});
      const previewButton=h('button',{class:'gw-btnx',type:'button',onclick:()=>preview()},'Preview all worn gear');
      const applyButton=h('button',{class:'gw-btnx',type:'button',disabled:true,onclick:()=>apply()},'Apply preset');
      const status=h('p',{role:'status'}),result=h('div');
      const controls=[preset,tier,classStat,previewButton];
      const options=()=>({preset:preset.value,class_tier:Number(tier.value),class_stat:classStat.checked});
      const lock=on=>{busy=on;controls.forEach(c=>c.disabled=on);applyButton.disabled=on||!view||!view.writable||!view.items.some(i=>i.changed);};
      function invalidate() {request++;view=null;applyButton.disabled=true;GW.clear(result);status.textContent='Options changed. Preview again before applying.';}
      [preset,tier,classStat].forEach(c=>c.addEventListener('change',invalidate));
      el.append(h('p',{class:'gw-dim'},'Updates occupied armor/weapon slots only. Fashion, mounts and mount gear stay in their separate systems. The character must be logged out. One transaction, backup and Undo; no crafting materials are consumed.'),
        h('p',{class:'gw-dim'},'GENERATED stat priorities favor the character’s attack type on offensive slots and defenses/HP on other slots. Platinum is level 10 in both presets. Heated stones use the same 5% damage and 8% ignore-defense values for physical/magical classes; Cooled values respect current server balance.'),
        h('div',{class:'gp-controls'},h('label',null,'Preset ',preset),h('label',null,'Class suit ',tier),
          h('label',null,classStat,' Add dedicated class-suit stat (requires III/IV)'),previewButton,applyButton),status,result);
      function draw(data) {
        GW.clear(result);
        status.textContent=data.character.name+' · '+data.character.class+' · '+data.items.filter(i=>i.changed).length+' of '+data.items.length+' worn pieces change. Applies at next login.';
        for(const row of data.items) {
          const after=row.after;
          result.append(h('section',{class:'gp-piece'},h('h3',null,row.slot_name+': '+row.name),
            h('p',null,'#'+row.before.prop_id+' → #'+after.prop_id+' · Q'+row.before.item_quality+'/G'+row.before.item_grade+' → Q'+after.item_quality+'/G'+after.item_grade+' · Platinum 10'),
            h('ul',null,row.stats.map(s=>h('li',null,s.name+' +'+value(s)+(s.level==null?' (grade '+after.item_grade+')':' (enhancement Lv'+s.level+' · grade '+after.item_grade+')')))),
            row.class_stat?h('p',null,'Class suit stat: '+row.class_stat.name+' +'+value(row.class_stat)+' (grade '+after.item_grade+')'):null,
            h('ul',null,row.stones.map(s=>h('li',null,s.name+' Lv10 · '+(s.percent?s.value/100+'%':GW.fmt(s.value))))),
            row.warnings.length?h('p',{class:'tt-bad'},row.warnings.join('; ')):null));
        }
        if(data.skipped.length)result.append(h('p',{class:'gw-dim'},data.skipped.length+' fashion/mount/mount-gear entries excluded from this preset.'));
      }
      async function preview() {
        if(busy)return;
        const ticket=++request;view=null;lock(true);status.textContent='Checking all worn gear…';GW.clear(result);
        try {
          const data=await ctx.api('preview/'+ctx.cid,options());
          if(!alive||ticket!==request)return;
          view=data;draw(data);
        }catch(e){if(alive&&ticket===request)status.textContent=e.message;}
        finally{if(alive&&ticket===request)lock(false);}
      }
      async function apply() {
        if(busy||!view||!view.writable)return;
        const selected=view;lock(true);
        try {
          if(!await GW.confirm({title:'Apply worn-gear preset?',message:selected.character.name+' gets the exact preview on '+selected.items.length+' worn pieces. Existing ordinary stats and sockets are replaced; no materials are consumed. Undo is in the Change log.',ok:'Apply preset'}))return;
          if(!alive)return;
          const response=await ctx.api('apply/'+ctx.cid,Object.assign({},selected.options,{token:selected.token}));
          if(!alive)return;
          GW.writeDone(response,'Worn gear preset applied');view=null;
        }catch(e){if(alive){view=null;await GW.writeError(e);}}
        finally{if(alive){lock(false);await preview();}}
      }
      unsubscribe=GW.on('change',()=>{if(alive&&!busy)preview();});
      await preview();
    },
    unmount(){if(this.cleanup)this.cleanup();}
  });
})();
