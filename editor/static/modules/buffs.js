(function () {
  "use strict";
  const GW=window.GW,h=GW.h;
  const labels={hit:"Hit",criticalAppend:"Critical",physicalDamageReduction:"Physical damage reduction",magicDamageReduction:"Magic damage reduction",physicalDefense:"Physical defence",magicDefense:"Magic defence",dodge:"Dodge",criticalResistance:"Critical resistance",physicalAttack:"Physical attack",magicAttack:"Magic attack",physicalDamageBonusBasisPoints:"Physical damage dealt",magicDamageBonusBasisPoints:"Magic damage dealt",hpPerSecond:"HP restored per second",mpPerSecond:"MP restored per second"};
  const sources={"class-skill":"Class skill buff","item":"Item buff (same effect as using the item)","status-data":"From the game's status data","status-extra":"From the game's status data (some effects icon only, see below)","hostile":"Hostile effect (debuff, control or damage over time) - for testing a player"};
  const page={
    async mount(el,ctx){
      this.el=el;this.ctx=ctx;this.cid=ctx.cid;this.data=null;this.online=null;this.activeSnapshot=null;this.activeRequest=0;
      if(this.cid==null){el.append(h("p",null,"Pick a character in the top bar to apply buffs. The buff library below works without one."));this.libraryBox=h("section",{class:"gw-buff-library"});el.append(this.libraryBox);this.renderLibrary(true);return;}
      this.banner=GW.onlineBanner(this.cid,{onState:state=>{const was=this.online&&this.online.online;this.online=state;if(this.data&&was!==!!(state&&state.online))this.render();}});el.append(this.banner);
      this.body=h("div",{class:"gw-buff-body"});el.append(this.body);await this.reload();
    },
    unmount(){this.ctx=null;},
    async reload(){
      const ctx=this.ctx,cid=this.cid;
      try{const d=await ctx.api("sheet/"+cid);if(this.ctx!==ctx||this.cid!==cid)return;this.data=d;this.online=d.online;this.render();await this.refreshActive();}
      catch(e){if(this.ctx===ctx)GW.clear(this.body).append(h("p",{class:"tt-bad"},e.message));}
    },
    render(){
      const d=this.data,C=d.catalogue;GW.clear(this.body);
      this.body.append(h("h2",null,"Buffs for "+d.character.name),h("p",{class:"gw-dim"},"Apply any status effect of the game using its normal effects and stacking rules: class skill buffs, item buffs, other game-data effects and hostile effects (debuffs, controls, damage over time) for testing. Each one says what the server really applies. They expire naturally or end when the player logs out."));
      this.libraryBox=h("section",{class:"gw-buff-library"});
      if(!C.supported){this.body.append(h("p",null,C.reason),this.libraryBox);this.renderLibrary(this.libOpen);return;}
      const search=h("input",{type:"search",placeholder:"Search buffs, effects or descriptions","aria-label":"Search buffs"});
      const select=h("select",{size:"10","aria-label":"Buff"}),details=h("div",{class:"gw-buff-details"});
      const duration=h("input",{type:"number",min:"1",max:String(C.maxDurationSeconds),step:"1","aria-label":"Duration in seconds"});
      const button=h("button",{class:"gw-btnx",type:"button",disabled:!d.writable||!(this.online||{}).online,onclick:async()=>{
        const row=C.buffs.find(x=>x.statusId===Number(select.value));if(!row)return;
        const seconds=Number(duration.value);
        if(!Number.isInteger(seconds)||seconds<1||seconds>C.maxDurationSeconds){GW.status("Enter a valid duration in seconds.","err");return;}
        const ctx=this.ctx,cid=this.cid,body={status_id:row.statusId,duration_seconds:seconds};
        const ok=await GW.confirm({title:"Apply buff?",message:row.name+" → "+d.character.name+" for "+seconds+" seconds. A stronger buff of the same kind takes priority."});
        if(!ok||this.ctx!==ctx||this.cid!==cid||this.data!==d)return;
        try{const r=await ctx.api("apply/"+cid,body);GW.writeDone(r,(r.result||{}).applied?row.name+" applied":"Buff not applied");if(this.ctx===ctx&&this.cid===cid)await this.refreshActive();}
        catch(e){await GW.writeError(e);}
      }},"Apply buff");
      let previous=null;
      const update=()=>{
        const row=C.buffs.find(x=>x.statusId===Number(select.value));GW.clear(details);button.disabled=!row||!d.writable||!(this.online||{}).online;
        if(!row)return;
        if(previous!==row.statusId){duration.value=String(row.durationSeconds);previous=row.statusId;}
        details.append(h("h3",null,row.name),h("p",null,row.description),h("p",{class:"gw-dim"},"Default duration: "+row.durationSeconds+" seconds"+(row.rank?" · rank "+row.rank:"")+" · "+(sources[row.source]||row.source||"")));
        const effects=h("dl",{class:"gw-buff-effects"});
        for(const [key,label] of Object.entries(labels)){
          const v=(row.effects||{})[key];if(!v)continue;
          effects.append(h("dt",null,label),h("dd",null,key.endsWith("Reduction")?Math.round(v*10000)/100+"%":key.endsWith("BasisPoints")?"+"+Math.round(v)/100+"%":"+"+v));
        }
        details.append(effects);
        if((row.not_applied||[]).length)details.append(h("p",{class:"tt-bad"},"Not applied by the server: "+row.not_applied.join("; ")+". The description above is the client's text."));
        if(row.server_note)details.append(h("p",{class:row.hostile?"tt-bad":"gw-dim"},"What the server does: "+row.server_note));
      };
      const draw=()=>{
        const old=select.value;GW.clear(select);const words=search.value.toLowerCase().trim().split(/\s+/).filter(Boolean);
        const rows=C.buffs.filter(row=>words.every(word=>(row.name+" "+row.statusId+" "+row.description+" "+row.skill_description+" "+row.status_name+" "+(row.effect_text||"")+" "+(sources[row.source]||"")).toLowerCase().includes(word)));
        for(const row of rows)select.append(h("option",{value:String(row.statusId)},row.name));
        select.value=rows.some(row=>String(row.statusId)===old)?old:rows.length?String(rows[0].statusId):"";update();
      };
      search.oninput=draw;select.onchange=update;draw();
      this.body.append(h("div",{class:"gw-buff-layout"},h("div",null,search,select),h("div",null,details,h("label",null,"Duration (seconds) ",duration),button)),
        !(this.online||{}).online?h("p",{class:"gw-dim"},"Log this character into the game to apply a session buff."):h("p",{class:"gw-dim"},"Applied effects appear immediately in the game. EXP events are managed under World & content."));
      this.pick=statusId=>{search.value="";draw();select.value=String(statusId);update();select.scrollIntoView({block:"center"});};
      this.activeBox=h("section",{class:"gw-buff-active"});this.body.append(this.activeBox,this.libraryBox);this.renderActive();this.renderLibrary(this.libOpen);

    },
    renderLibrary(startOpen){
      const box=GW.clear(this.libraryBox),self=this;
      const open=h("details",{class:"gw-buff-lib"},h("summary",null,"Buff library: every effect in the game data (icon grid)"));
      box.append(open);let loaded=false;
      open.addEventListener("toggle",async()=>{
        self.libOpen=open.open;      // the page re-renders on every online-state poll: keep the library open
        if(!open.open||loaded)return;loaded=true;
        let L;try{L=await self.ctx.api("library");}catch(e){open.append(h("p",{class:"tt-bad"},e.message));loaded=false;return;}
        const search=h("input",{type:"search","aria-label":"Search the buff library",placeholder:"Search by name, effect, skill or item"});
        const cat=h("select",{"aria-label":"Library category"},h("option",{value:""},"All categories ("+L.count+")"),L.categories.map(c=>h("option",{value:c},c+" ("+L.entries.filter(e=>e.category===c).length+")")));
        const only=h("input",{type:"checkbox","aria-label":"Only effects the server can apply"});
        const list=h("div",{class:"gw-buff-lib-list"});
        let view=self.libView||"grid";
        const viewSel=h("select",{"aria-label":"Library view",onchange:e=>{view=self.libView=e.target.value;draw();}},h("option",{value:"grid"},"Icon grid"),h("option",{value:"list",selected:view==="list"||null},"List"));
        // hover text for a grid tile: proper name, what it does, how long, who casts it, whether the server can apply it
        const tipText=e=>[e.label+"  (#"+e.id+")",e.category+(e.beneficial?"":" · harmful"),
          e.effects.map(x=>x.label).join("; "),e.note||"",(e.duration_s!==null&&e.duration_s!==undefined?"Lasts "+dur(e.duration_s):"")+(e.interval_s?", every "+e.interval_s+" s":""),
          e.skills.length?"Cast by: "+e.skills.filter(k=>!/[\u3000-\u9fff]/.test(k.name)).slice(0,4).map(k=>k.name).join(", "):"",
          e.icon_source==="skill"?"(icon: the casting skill's; the effect has none of its own)":e.icon_source==="generic"?"(icon: generic EXP boost; server-only effect)":"",
          e.apply_status_id!==null?"The running server can apply it: click to choose it above.":"Listed only: the running server cannot apply it yet."].filter(Boolean).join(" · ");
        const dur=s=>s===null||s===undefined?"":s<0?"until removed":s>=3600?Math.round(s/360)/10+" h":s>=60?Math.round(s/6)/10+" min":s+" s";
        const draw=()=>{
          GW.clear(list);const words=search.value.toLowerCase().trim().split(/\s+/).filter(Boolean);let n=0;
          if(view==="grid"){
            list.className="gw-buff-lib-grid";
            let lastCat=null,grid=null;
            const rows=L.entries.filter(e=>(!cat.value||e.category===cat.value)&&(!only.checked||e.apply_status_id!==null)&&
              words.every(w=>(e.label+" "+e.name+" "+e.id+" "+e.note+" "+e.category+" "+e.effects.map(x=>x.label).join(" ")+" "+e.skills.map(k=>k.name).join(" ")).toLowerCase().includes(w)));
            rows.sort((a,b)=>a.category.localeCompare(b.category)||a.label.localeCompare(b.label)||a.id-b.id);
            for(const e of rows){
              if(e.category!==lastCat){lastCat=e.category;list.append(h("h4",{class:"gw-buff-cat"},lastCat+" ("+rows.filter(x=>x.category===lastCat).length+")"));grid=h("div",{class:"gw-buff-tiles"});list.append(grid);}
              const can=e.apply_status_id!==null&&self.pick;
              grid.append(h("button",{class:"gw-buff-tile"+(e.beneficial?"":" bad")+(e.apply_status_id!==null?" can":""),type:"button","data-help":tipText(e),"aria-label":e.label,
                onclick:()=>{if(can)self.pick(e.apply_status_id);else GW.status(e.label+": "+(e.apply_status_id!==null?"pick a character to apply it":"listed only (the running server cannot apply it)"),"ok");}},
                h("span",{class:"gw-sprite gw-cell gw-cellx"},GW.icon(e.icon,{name:e.label})),h("span",{class:"gw-buff-tname"},e.label)));
              n++;
            }
            if(!n)list.append(h("p",{class:"gw-dim"},"Nothing matches."));
            return;
          }
          list.className="gw-buff-lib-list";
          for(const e of L.entries){
            if(cat.value&&e.category!==cat.value)continue;if(only.checked&&e.apply_status_id===null)continue;
            const hay=(e.name+" "+e.id+" "+e.note+" "+e.category+" "+e.effects.map(x=>x.label).join(" ")+" "+e.skills.map(k=>k.name).join(" ")).toLowerCase();
            if(!words.every(w=>hay.includes(w)))continue;if(++n>150)continue;
            const can=e.apply_status_id!==null;
            list.append(h("div",{class:"gw-buff-lib-row"},
              h("div",null,h("b",null,e.label||e.name)," ",h("span",{class:"gw-dim"},"#"+e.id+" · "+e.category+(e.beneficial?"":" · harmful")+(e.duration_s!==null?" · "+dur(e.duration_s):"")+(e.interval_s?" · every "+e.interval_s+" s":"")+(e.in_2008_server?" · also in the 2008 server":"")),
                h("div",null,e.effects.map(x=>x.label).join("; ")),e.note?h("div",{class:"gw-dim"},e.note):null,
                e.skills.length?h("div",{class:"gw-dim"},"Cast by: "+e.skills.slice(0,4).map(k=>k.name+(k.item_count?" ("+k.item_count+" item"+(k.item_count===1?"":"s")+")":"")).join(", ")+(e.skills.length>4?" and "+(e.skills.length-4)+" more":"")):h("div",{class:"gw-dim"},"No skill in the client data casts this directly (items, quests and the server can still grant it).")),
              can?h("button",{class:"gw-btnx",type:"button",onclick:()=>self.pick(e.apply_status_id)},"Choose above"):h("span",{class:"gw-dim",style:"white-space:nowrap;align-self:flex-start","data-help":"The running game server only applies the effects in its own catalogue. This one is listed so it is documented; adding it needs a server update."},"Listed only")));
          }
          if(!n)list.append(h("p",{class:"gw-dim"},"Nothing matches."));else if(n>150)list.append(h("p",{class:"gw-dim"},"Showing the first 150 of "+n+". Narrow the search."));
        };
        search.oninput=draw;cat.onchange=draw;only.onchange=draw;
        open.append(h("p",{class:"gw-dim"},L.count+" effects (the client's Status.ini plus statuses only the server defines); "+L.applicable+" can be applied to a player by the running server, the rest are listed with their labels but not applicable until the server supports them."),
          h("div",{class:"gw-buff-lib-bar"},search,cat,viewSel,h("label",null,only," applicable only")),list);draw();
      });
      if(startOpen)open.open=true;
    },
    async refreshActive(){
      const ctx=this.ctx,cid=this.cid,request=++this.activeRequest;if(!ctx)return;
      try{
        const snapshot=await ctx.api("active/"+cid);
        if(this.ctx!==ctx||this.cid!==cid||request!==this.activeRequest)return;
        this.activeSnapshot=snapshot;this.renderActive();
      }catch(e){if(this.ctx===ctx&&this.cid===cid&&request===this.activeRequest){this.activeSnapshot={supported:false,reason:e.message};this.renderActive();}}
    },
    renderActive(){
      if(!this.activeBox)return;const box=GW.clear(this.activeBox),snapshot=this.activeSnapshot,d=this.data;
      box.append(h("h3",null,"Active runtime effects"),h("button",{class:"gw-btnx",type:"button",onclick:()=>this.refreshActive()},"Refresh active effects"));
      if(!snapshot){box.append(h("p",{class:"gw-dim"},"Refresh to inspect the current effects."));return;}
      if(!snapshot.supported){box.append(h("p",{class:"gw-dim"},snapshot.reason||"Active-effect controls need the compatible server update."));return;}
      if(!snapshot.online){box.append(h("p",{class:"gw-dim"},"This character is not in the game."));return;}
      box.append(h("p",{class:"gw-dim"},"Remaining times are measured at refresh. Class buffs can be removed here; source-managed effects use their own controls."));
      if(!(snapshot.buffs||[]).length)box.append(h("p",{class:"gw-dim"},"No active runtime effects."));
      for(const row of snapshot.buffs||[]){
        const remove=h("button",{class:"gw-btnx",type:"button",disabled:!row.removable||!d.writable||!(this.online||{}).online,onclick:async()=>{
          const ctx=this.ctx,cid=this.cid;
          const body={expected:{epoch:snapshot.epoch,kind:row.kind,statusId:row.statusId,revision:row.revision}};
          const ok=await GW.confirm({title:"Remove effect?",message:"Remove "+row.name+" from "+d.character.name+"? If the effect has changed, this request will be refused."});
          if(!ok||this.ctx!==ctx||this.cid!==cid||this.data!==d||this.activeSnapshot!==snapshot)return;
          try{const r=await ctx.api("remove/"+cid,body);GW.writeDone(r,"Effect removed");}
          catch(e){await GW.writeError(e);}
          if(this.ctx===ctx&&this.cid===cid)await this.refreshActive();
        }},"Remove");
        box.append(h("div",{style:"display:flex;gap:1rem;align-items:center;margin:.7rem 0"},h("span",null,row.name+" · "+(row.permanent?"persistent":row.remainingSeconds+" seconds at refresh")),remove));
      }
    }
  };
  GW.module("buffs",page);
})();
