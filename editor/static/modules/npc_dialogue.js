(function () {
 "use strict";
 const GW=window.GW,h=GW.h;
 // ---- colour tags: |cAARRGGBB starts a colour, the next |c (the game's habit: |cffffffff back to white) or |r ends it.
 const CT={
  CODE:/\|c([0-9A-Fa-f]{8})|\|r/g,
  WHITE:"|cffffffff",
  tag(rgb){const v=String(rgb||"").replace(/^#/,"").replace(/^\|c(?:ff)?/i,"");if(!/^[0-9A-Fa-f]{6}$/.test(v)&&!/^[0-9A-Fa-f]{8}$/.test(v))throw new Error("not a colour: "+rgb);return "|cff"+v.slice(-6).toUpperCase();},
  // split into [{text, color}] runs; color = "#RRGGBB" or null (default)
  runs(text){const out=[];let color=null,last=0;const s=String(text||"");CT.CODE.lastIndex=0;let m;
   while((m=CT.CODE.exec(s))){if(m.index>last)out.push({text:s.slice(last,m.index),color});color=m[1]?"#"+m[1].slice(2).toUpperCase():null;last=CT.CODE.lastIndex;}
   if(last<s.length)out.push({text:s.slice(last),color});return out;},
  used(text){const n=new Map();String(text||"").replace(/\|c([0-9A-Fa-f]{8})/g,(_,c)=>{c=c.toUpperCase();n.set(c,(n.get(c)||0)+1);return "";});return n;},
  strip(text){return String(text||"").replace(/\|c[0-9A-Fa-f]{8}|\|r/g,"");},
  // wrap [start,end) of value in a colour; with no selection insert the tag at the cursor. Returns {value, start, end} (new selection)
  wrap(value,start,end,rgb,close){const t=CT.tag(rgb),c=close===undefined?CT.WHITE:close;
   if(start===end)return {value:value.slice(0,start)+t+value.slice(end),start:start+t.length,end:start+t.length};
   const mid=value.slice(start,end);const v=value.slice(0,start)+t+mid+c+value.slice(end);return {value:v,start:start+t.length,end:start+t.length+mid.length};},
  unwrap(value,start,end){const mid=CT.strip(value.slice(start,end));return {value:value.slice(0,start)+mid+value.slice(end),start,end:start+mid.length};},
 };
 GW.colourTags=CT;
 GW.module("npc_dialogue",{async mount(el,ctx){
  let data,selected,preview=null,busy=false,needle="",draft=new Map(),menus=null,labels=new Map(),labelsDirty=false;
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
   const counter=h("small",{class:"gw-dim"}),preview=h("div",{class:"npc-preview","aria-label":"Greeting as the game shows it"}),usedBox=h("div",{class:"npc-used"});
   const toolbar=colourBar(description,()=>change());
   const gameBox=h("div",{class:"npc-game-wrap"});
   function change(){
    drawGame(gameBox,row,name.value,description.value);
    const next={npc_key:row.npc_key,display_name:name.value,description:description.value};
    if(next.display_name===row.display_name && next.description===row.description)draft.delete(row.npc_key);else draft.set(row.npc_key,next);
    counter.textContent=new TextEncoder().encode(next.description).length+" / 16384 text bytes";
    showPreview();invalidate();renderList();
   }
   name.addEventListener("input",change);description.addEventListener("input",change);
   function showPreview(){
    GW.clear(preview);for(const r of CT.runs(description.value)){const sp=h("span",null,r.text);if(r.color)sp.style.color=r.color;preview.append(sp);}
    GW.clear(usedBox);const used=CT.used(description.value);if(used.size)usedBox.append(h("span",{class:"gw-dim"},"Colours in this greeting: "));
    for(const [c,n] of used)usedBox.append(h("span",{class:"npc-swatch-sm",title:"|c"+c+" × "+n,style:"background:#"+c.slice(2)}),h("code",null,c.slice(2)+(n>1?" ×"+n:"")+" "));
   }
   form.append(h("h3",null,row.npc_key),h("p",{class:"gw-dim"},row.scene_key),h("label",null,"Greeting name",name),
    h("label",null,"Greeting text"),toolbar,description,h("div",{class:"npc-preview-h gw-dim"},"Preview (colours as the game draws them)"),preview,usedBox,counter,h("details",null,h("summary",null,"Currently published greeting"),h("pre",null,row.description)),gameBox);
   counter.textContent=new TextEncoder().encode(description.value).length+" / 16384 text bytes";
   showPreview();drawGame(gameBox,row,name.value,description.value);
  }
  // the colour toolbar: the game's own palette (counted in the client text), a custom colour, wrap / insert, remove
  let palette=null;
  function colourBar(area,changed){
   const bar=h("div",{class:"npc-colours"}),chips=h("div",{class:"npc-chips"}),more=h("button",{type:"button",class:"gw-btnx small"},"All colours"),
    custom=h("input",{type:"color",value:"#39d8b8","aria-label":"Custom colour"}),
    close=h("select",{"aria-label":"How a wrapped colour ends","data-help":"The game's own text goes back to white with |cffffffff after a coloured word. |r also ends a colour."},
     h("option",{value:CT.WHITE},"end: back to white"),h("option",{value:"|r"},"end: |r (rare in the game's text; not checked in game)"));
   let all=false;
   function apply(rgb){
    if(area.disabled)return;
    try{const r=CT.wrap(area.value,area.selectionStart,area.selectionEnd,rgb,close.value);area.value=r.value;area.focus();area.setSelectionRange(r.start,r.end);changed();}
    catch(e){GW.status(e.message,"err");}
   }
   function draw(){
    GW.clear(chips);const list=(palette&&palette.colours)||[];
    if(!list.length){chips.append(h("span",{class:"gw-dim"},palette&&palette.error?palette.error:"Loading the game's colours…"));return;}
    for(const c of (all?list:list.slice(0,14)))chips.append(h("button",{type:"button",class:"npc-chip",
      "data-help":"|c"+c.code+(c.name?" · "+c.name:"")+" · used "+c.count.toLocaleString("en-US")+" times in the client text"+(c.sample?" (e.g. \u201c"+c.sample+"\u201d)":"")+". Select text and click to colour it; with nothing selected the tag is inserted at the cursor.",
      onclick:()=>apply(c.rgb)},h("span",{class:"npc-swatch",style:"background:"+c.rgb}),h("small",null,c.count.toLocaleString("en-US"))));
    more.textContent=all?"Fewer colours":"All "+list.length+" colours";
   }
   more.onclick=()=>{all=!all;draw();};
   bar.append(h("span",{class:"gw-dim"},"Colour: "),chips,more,custom,
    h("button",{type:"button",class:"gw-btnx small",onclick:()=>apply(custom.value),"data-help":"Wrap the selection in the colour picked on the left."},"Use custom"),close,
    h("button",{type:"button",class:"gw-btnx small","data-help":"Remove every colour code inside the selection.",onclick:()=>{if(area.disabled)return;const r=CT.unwrap(area.value,area.selectionStart,area.selectionEnd);area.value=r.value;area.setSelectionRange(r.start,r.end);changed();}},"Remove colours"));
   if(palette)draw();else ctx.api("colours").then(p=>{palette=p;draw();},e=>{palette={colours:[],error:"Colour list unavailable: "+e.message};draw();});
   draw();return bar;
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
  async function load(){
   [data,menus]=await Promise.all([ctx.api("overview"),ctx.api("menus").catch(e=>({available:false,error:e.message}))]);
   labels=new Map(Object.entries((menus&&menus.draft&&menus.draft.labels)||{}));labelsDirty=false;
   const want=ctx.params&&ctx.params.get&&ctx.params.get("npc");
   if(!selected&&want&&data.texts.some(r=>r.npc_key===want))selected=want;
   if(!selected)selected=data.texts[0]?.npc_key;render();renderHistory();}
  // ---- B21 item 20: the whole dialogue as the game draws it. Greeting = server (above); option lines = client words.
  function runs(text,cls){const p=h("span",{class:cls||null});for(const r of CT.runs(text)){const sp=h("span",null,r.text);if(r.color)sp.style.color=r.color;p.append(sp);}return p;}
  function labelOf(el){return el&&el.key&&labels.has(el.key)?labels.get(el.key):(el?el.text:"");}
  function drawGame(box,row,name,greeting){
   GW.clear(box);
   const win=h("div",{class:"npc-game","aria-label":"The NPC window as the game draws it"},
    h("div",{class:"npc-game-title"},name||row.display_name),h("div",{class:"npc-game-text"},runs(greeting)));
   box.append(h("h3",null,"What players see"),win);
   const qs=(menus&&menus.quests&&menus.quests[row.npc_key])||[];
   if(qs.length){const ql=h("div",{class:"npc-game-quests","data-help":"Quests this NPC gives or takes back, from the client's quest list. In game a player sees only the ones that fit their level, faction and progress. Quest words are edited with the quests, not here."});
    for(const q of qs.slice(0,40))ql.append(h("div",{class:"npc-game-opt npc-game-quest"},h("span",null,q.title),h("small",{class:"gw-dim"},(q.role==="gives"?"gives":"hands in")+(q.min_level?" · Lv "+q.min_level+(q.max_level&&q.max_level!==q.min_level?"-"+q.max_level:""):""))));
    if(qs.length>40)ql.append(h("small",{class:"gw-dim"},"+"+(qs.length-40)+" more"));
    win.append(ql);}
   const routes=menus&&menus.available?(menus.routes[row.npc_key]||[]):[];
   if(!menus||!menus.available){box.append(h("p",{class:"gw-dim"},"Option lines unavailable: "+((menus&&menus.error)||"not loaded")));return;}
   if(!routes.length){
    box.append(h("p",{class:"gw-dim"},"This NPC has no function page on this server. Its window shows the greeting, its quests (in the window above) and, for a merchant, the shop button. The client program draws those buttons itself, so their words cannot be changed here (not listed: their exact words were not read from the game)."));return;}
   for(const r of routes){
    const pg=r.page,fw=h("div",{class:"npc-game npc-game-page","aria-label":"Function page "+r.dialog_index});
    fw.append(h("div",{class:"npc-game-title"},(name||row.display_name)+(routes.length>1?" · page "+(routes.indexOf(r)+1):"")));
    if(!pg.resolved)fw.append(h("p",{class:"tt-bad"},pg.note));
    for(const t of pg.texts)fw.append(h("div",{class:"npc-game-text"},runs(labelOf(t)),editBtn(t,"page text")));
    for(const o of pg.options){
     if(!o.label){fw.append(h("div",{class:"npc-game-opt npc-game-missing","data-help":"The client script draws no button for option "+o.sub+" on this page (it may only change the page text, or open a window)."},"(an option the client draws no words for)"));continue;}
     fw.append(h("div",{class:"npc-game-opt"},runs(labelOf(o.label)),o.added?h("span",{class:"gw-chip","data-help":"Not in the published menu: the game server adds this button after the menu's own buttons."},"added by the server"):null,editBtn(o.label,"option line")));
    }
    box.append(fw);
   }
   box.append(labelBar());
  }
  function editBtn(el,what){
   if(!el)return null;
   if(!el.key)return h("span",{class:"gw-chip","data-help":"Part of this text is filled in while the game runs, so it cannot be changed here."},"fixed");
   const info=(menus.strings||{})[el.key]||{},others=(info.npcs||[]).filter(k=>k!==selected);
   return h("button",{type:"button",class:"gw-btnx small"+(labels.has(el.key)?" on":""),
    "data-help":"Change this "+what+"."+(others.length?" The same words are also shown by "+others.length+" other NPC(s): "+others.slice(0,6).join(", ")+".":"")+" Reaches players with the next client update.",
    onclick:()=>editLabel(el,what)},labels.has(el.key)?"edited":"edit");
  }
  async function editLabel(el,what){
   const cur=labelOf(el),inp=h("input",{type:"text",value:cur,"aria-label":"New "+what,style:"width:100%;box-sizing:border-box"}),pv=h("div",{class:"npc-preview"});
   const show=()=>{GW.clear(pv).append(runs(inp.value));};inp.addEventListener("input",show);show();
   const box=h("div",null,h("p",{class:"gw-dim"},"Colour codes work as in greetings; the game's option lines are yellow (|cffFFFF00) and start with *. One line only."),inp,pv,
    h("details",null,h("summary",null,"Original"),h("pre",null,el.text),h("small",{class:"gw-dim"},"Text id "+el.key)));
   const ok=await GW.confirm({title:"Change "+what,el:box,ok:"Keep in draft",wide:true});
   if(!ok)return;
   const v=inp.value;if(!v.trim()){GW.status("The text cannot be empty.","err");return;}
   if(v===el.text)labels.delete(el.key);else labels.set(el.key,v);labelsDirty=true;renderForm();
  }
  function labelBar(){
   const n=labels.size;
   return h("div",{class:"npc-toolbar"},h("span",{class:"gw-chip"},"Option lines: takes effect with the next client update"),
    h("span",{class:"gw-dim"},n+" changed option text"+(n===1?"":"s")+(labelsDirty?" · not saved":"")),
    h("button",{class:"gw-btnx",disabled:!labelsDirty,onclick:saveLabels},"Save option-text draft"),
    h("button",{class:"gw-btnx",disabled:!n,onclick:exportLabels,"data-help":"Download the change as a LuaText.lua patch for the next client build (tools/apply_npc_menu_text.py applies it)."},"Download client patch"));
  }
  async function saveLabels(){
   try{const r=await ctx.api("menus/draft",{labels:Object.fromEntries(labels),version:menus.draft_version});
    menus.draft=r.draft;menus.draft_version=r.draft_version;labels=new Map(Object.entries(r.draft.labels||{}));labelsDirty=false;
    GW.status("Option-text draft saved ("+labels.size+" changed). Players see it after the next client update.","ok");renderForm();}
   catch(e){if(await GW.writeError(e)==="conflict")await load();}
  }
  async function exportLabels(){
   if(labelsDirty){GW.status("Save the draft first.","err");return;}
   try{const r=await ctx.api("menus/export");const a=h("a",{href:URL.createObjectURL(new Blob([JSON.stringify({labels:r.draft.labels,client:r.draft.client,saved_at:r.draft.saved_at,lua:r.lua},null,1)],{type:"application/json"})),download:"npc-menu-text-draft.json"});document.body.append(a);a.click();a.remove();}
   catch(e){GW.writeError(e);}
  }
  try{await load();}catch(e){status.textContent=e.message;}
 }});
})();
