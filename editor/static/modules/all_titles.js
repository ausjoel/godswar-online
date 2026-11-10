(function(){
 'use strict';
 const GW=window.GW,h=GW.h;
 let active=null;
 GW.module('all_titles',{
  async mount(el,ctx){
   const session={};active=session;
   el.append(GW.onlineBanner(ctx.cid));
   const message=h('p',{class:'gw-dim'},'Grant missing supported titles without changing your shown title or earned ownership. The character must be logged out; ownership refreshes at next login.');
   const summary=h('p'),list=h('div',{class:'gw-all-title-list'}),status=h('p');
   const preview=h('button',{class:'gw-btnx',type:'button'},'Preview all titles');
   const apply=h('button',{class:'gw-btnx',type:'button',disabled:true},'Give all titles');
   let view=null,busy=false,loadId=0;
   function show(v){
    GW.clear(list);
    summary.textContent=v.add.length+' new grants · '+v.owned.length+' already owned · '+v.blocked.length+' blocked · '+v.final_count+'/'+v.limit+' final ownership. Shown title #'+v.selected+' is kept.';
    if(!v.capacity_ok)list.append(h('p',{class:'tt-bad'},'Ownership limit exceeded: the whole batch is refused.'));
    for(const row of v.titles){
     const entry=h('div',{class:'gw-all-title '+row.status},h('b',null,row.name+' #'+row.id),h('span',null,' — '+({grant:'will grant',owned:'already owned',blocked:'cannot grant'})[row.status]));
     if(row.reason)entry.append(h('p',{class:'gw-dim'},row.reason));
     list.append(entry);
    }
    apply.disabled=busy || !v.writable || !v.capacity_ok || !v.changed;
   }
   async function load(){
    const request=++loadId;view=null;apply.disabled=true;preview.disabled=true;
    try{const v=await ctx.api('preview/'+ctx.cid);if(active!==session || request!==loadId)return;view=v;show(v);status.textContent=v.changed?'Review the titles above, then Give all titles.':'All supported titles are already owned. No changes needed.';}
    catch(e){if(active===session && request===loadId)status.textContent=e.message;}
    finally{if(active===session && request===loadId)preview.disabled=busy;}
   }
   preview.onclick=()=>load();
   apply.onclick=async()=>{
    if(!view || busy || apply.disabled)return;
    const reviewed=view;busy=true;apply.disabled=true;preview.disabled=true;
    try{
     if(!await GW.confirm({title:'Give all supported titles?',message:'Grant '+reviewed.add.length+' missing titles to '+reviewed.character.name+'? '+reviewed.blocked.length+' blocked titles will remain unavailable. The shown title and earned ownership are kept. Applies at next login.',ok:'Grant titles'}))return;
     if(active!==session || view!==reviewed)return;
     const result=await ctx.api('apply/'+ctx.cid,{token:reviewed.token});
     if(active!==session)return;
     if(!result.unchanged)GW.writeDone(result,'Granted '+result.added+' titles');
     else GW.status('All supported titles are already owned.');
     await load();
    }catch(e){if(active===session){await GW.writeError(e);view=null;apply.disabled=true;status.textContent='Preview again before retrying.';}}
    finally{busy=false;if(active===session){preview.disabled=false;if(view)show(view);}}
   };
   el.append(message,h('div',{class:'gw-all-title-actions'},preview,apply),status,summary,list);
   const stop=GW.on('change',()=>load());session.stop=stop;
  },
  unmount(){if(active && active.stop)active.stop();active=null;}
 });
})();
