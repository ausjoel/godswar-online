(function () {
 "use strict";
 const GW=window.GW,h=GW.h;
 GW.module("npc_dialogue",{async mount(el,ctx){
  let data,selected,preview=null,busy=false,needle="",draft=new Map();
  const list=h("div",{class:"npc-list"}),form=h("div",{class:"gw-frame-simplewindow npc-form"}),
   review=h("div",{class:"npc-review"}),status=h("p",{class:"gw-dim",role:"status"});
  const search=h("input",{type:"search",placeholder:"Find an NPC by name, key, scene or greeting",oninput:e=>{needle=e.target.value;renderList();}});
  const previewButton=h("button",{class:"gw-btnx",disabled:true,onclick:check},"Preview changes"),
   publishButton=h("button",{class:"gw-btnx",disabled:true,onclick:publish},"Publish greetings"),
   discard=h("button",{class:"gw-btnx",disabled:true,onclick:async()=>{
    if(draft.size && !await GW.confirm({title:"Discard greeting draft?",message:"Your unpublished changes will be discarded.",ok:"Discard"}))return;
    draft.clear();preview=null;render();
   }},"Discard draft");
  el.append(h("h2",null,"NPC greetings"),h("p",null,"Draft greeting names and text. Publishing keeps every NPC, route and menu, and preserves earlier versions."),
   h("p",{class:"gw-chip"},"Takes effect after the next game restart"),status,
   h("div",{class:"npc-toolbar"},search,previewButton,publishButton,discard),
   h("div",{class:"npc-layout"},list,form),review);
  let historyBox=h("details",null,h("summary",null,"Publication history"));el.append(historyBox);
  function edited(row){return draft.get(row.npc_key)||row;}
  function invalidate(){preview=null;GW.clear(review);buttons();}
  function buttons(){
   previewButton.disabled=busy||!data?.writable||!draft.size;
   publishButton.disabled=busy||!data?.writable||!preview;
   discard.disabled=busy||!draft.size;
   for(const input of form.querySelectorAll("input,textarea"))input.disabled=busy||!data?.writable;
   for(const button of historyBox.querySelectorAll("button"))button.disabled=busy||!data?.writable;
   status.textContent=data?((data.blocked?data.blocked+" ":"")+data.texts.length+" NPCs · "+draft.size+" drafted changes · published "+(data.publication?.revision?.slice(0,12)||"none")):"Loading greetings…";
  }
  function renderList(){
   GW.clear(list);let rows=data.texts.filter(r=>[r.npc_key,r.scene_key,edited(r).display_name,edited(r).description].join(" ").toLowerCase().includes(needle.toLowerCase()));
   for(const row of rows)list.append(h("button",{type:"button",class:"npc-row"+(selected===row.npc_key?" selected":""),onclick:()=>{selected=row.npc_key;renderList();renderForm();}},
    h("b",null,edited(row).display_name+(draft.has(row.npc_key)?" *":"")),h("small",null,row.npc_key+" · "+row.scene_key)));
   if(!rows.length)list.append(h("p",null,"No matching NPCs."));
  }
  function renderForm(){
   GW.clear(form);const row=data.texts.find(r=>r.npc_key===selected);if(!row){form.append(h("p",null,"Choose an NPC to edit its greeting."));return;}
   const value=edited(row),name=h("input",{type:"text",value:value.display_name,"aria-label":"Greeting name",disabled:busy||!data.writable}),
    description=h("textarea",{rows:12,"aria-label":"Greeting text",disabled:busy||!data.writable});description.value=value.description;
   const counter=h("small",{class:"gw-dim"});
   function change(){
    const next={npc_key:row.npc_key,display_name:name.value,description:description.value};
    if(next.display_name===row.display_name && next.description===row.description)draft.delete(row.npc_key);else draft.set(row.npc_key,next);
    counter.textContent=new TextEncoder().encode(next.description).length+" / 16384 text bytes";
    invalidate();renderList();
   }
   name.addEventListener("input",change);description.addEventListener("input",change);
   form.append(h("h3",null,row.npc_key),h("p",{class:"gw-dim"},row.scene_key),h("label",null,"Greeting name",name),
    h("label",null,"Greeting text",description),counter,h("details",null,h("summary",null,"Currently published greeting"),h("pre",null,row.description)));
   counter.textContent=new TextEncoder().encode(description.value).length+" / 16384 text bytes";
  }
  function renderHistory(){
   GW.clear(historyBox);historyBox.append(h("summary",null,"Publication history"));
   for(const r of data.history){const active=r.revision===data.publication?.revision;
    historyBox.append(h("div",{class:"npc-history"},h("code",null,r.revision.slice(0,12))," · "+r.created_at+" · "+r.source,
     active?h("span",{class:"gw-chip"},"Published"):h("button",{class:"gw-btnx small",onclick:async()=>{
      if(draft.size && !await GW.confirm({title:"Replace current draft?",message:"Load this historical greeting set into your draft? Nothing is published yet.",ok:"Load draft"}))return;
      try{const old=await ctx.api("revision/"+r.revision);draft.clear();
       const current=new Map(data.texts.map(x=>[x.npc_key,x]));
       for(const row of old.texts){const now=current.get(row.npc_key);if(row.display_name!==now.display_name||row.description!==now.description)draft.set(row.npc_key,{npc_key:row.npc_key,display_name:row.display_name,description:row.description});}
       preview=null;render();GW.status("Historical greetings loaded into draft. Preview before publishing.","ok");
      }catch(e){GW.writeError(e);}
     }},"Load into draft")));
   }
  }
  function render(){renderList();renderForm();GW.clear(review);buttons();}
  function body(){return {expected:data.publication,changes:[...draft.values()]};}
  async function check(){busy=true;buttons();try{
   preview=await ctx.api("preview",body());GW.clear(review);
   review.append(h("h3",null,preview.changes.length+" greeting changes"),h("p",null,preview.reuse?"This draft restores an existing immutable revision.":"A complete new immutable revision will be published."));
   for(const c of preview.changes)review.append(h("details",{open:preview.changes.length===1},h("summary",null,c.npc_key+" · "+c.scene_key),
    h("div",{class:"npc-diff"},h("div",null,h("b",null,"Published"),h("p",null,c.before.display_name),h("pre",null,c.before.description)),
     h("div",null,h("b",null,"Draft"),h("p",null,c.after.display_name),h("pre",null,c.after.description)))));
   }catch(e){preview=null;GW.writeError(e);}finally{busy=false;buttons();}}
  async function publish(){
   if(!preview)return;
   if(!await GW.confirm({title:"Publish NPC greetings?",message:preview.changes.length+" greetings will change after the next game restart. Earlier revisions remain in history.",ok:"Publish"}))return;
   busy=true;buttons();try{const r=await ctx.api("publish",{...body(),token:preview.token});GW.writeDone(r,"NPC greetings published; takes effect after the next game restart");draft.clear();preview=null;await load();}
   catch(e){invalidate();GW.writeError(e);}finally{busy=false;buttons();}
  }
  async function load(){data=await ctx.api("overview");if(!selected)selected=data.texts[0]?.npc_key;render();renderHistory();}
  try{await load();}catch(e){status.textContent=e.message;}
 }});
})();
