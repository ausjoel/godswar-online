(function(){
 "use strict";
 const GW=window.GW,h=GW.h;
 GW.module('favourites',{
  async mount(el,ctx){
   let source=null,selected=new Set(),favourites=[],pending=null,busy=false;
   const label=h('p',{class:'gw-dim'},'Loading…');
   const sourceSelect=h('select',{'aria-label':'Source character'});
   const name=h('input',{type:'text',maxlength:80,placeholder:'Favourite name','aria-label':'Favourite name'});
   const copies=h('input',{type:'number',min:1,max:96,value:1,'aria-label':'Copies of selection'});
   const location=h('select',{'aria-label':'Destination location'},h('option',{value:1},'Bags'),h('option',{value:3},'Warehouse'));
   const sourceRows=h('div'),shelf=h('div'),previewBox=h('div'),message=h('p',{role:'status'});
   const target=ctx.cid;
   el.append(h('p',{},'Save selected equipment, bag or warehouse items as a reusable set. Copying preserves item properties, but creates new item instances. Sources and destinations must be logged out for this atomic batch operation.'),label,
    h('section',{class:'gw-frame-normalwindow',style:'padding:16px;margin:12px 0'},h('h2',{},'1. Select source items'),sourceSelect,h('button',{class:'gw-btnx',onclick:loadSource},'Load source'),h('button',{class:'gw-btnx',onclick:()=>{if(!source)return;source.items.filter(r=>!r.sealed&&r.item_location!==2).slice(0,96).forEach(r=>selected.add(r.id));drawSource();}},'Select first 96 eligible'),h('button',{class:'gw-btnx',onclick:()=>{selected.clear();drawSource();}},'Clear selection'),sourceRows,name,h('button',{class:'gw-btnx',onclick:save},'Save selection as favourite')),
    h('section',{class:'gw-frame-normalwindow',style:'padding:16px;margin:12px 0'},h('h2',{},'2. Choose destination and preview'),h('p',{},'Destination is the character selected at the top of the editor. Copies repeats the complete selection, including each stack count. Existing items are never overwritten.'),h('label',{},'Copies ',copies),h('label',{},' Destination ',location),h('button',{class:'gw-btnx',onclick:()=>prepare(null)},'Preview copy of selection'),h('h3',{},'Saved favourites'),shelf),previewBox,message);
   async function action(fn){if(busy)return;busy=true;message.textContent='Working…';try{await fn();message.textContent='';}catch(e){message.textContent=e.message;GW.status(e.message,'err');}finally{busy=false;}}
   async function readSource(){source=await GW.api('/api/m/inventory/state/'+Number(sourceSelect.value));selected.clear();drawSource();}
   async function loadSource(){await action(readSource);}
   function selection(){if(!source)throw Error('Load a source character first');return {source:source.character.id,source_revision:source.character.inventory_revision,selection:source.items.filter(r=>selected.has(r.id)).map(r=>({id:r.id,location:r.item_location,slot:r.slot_index}))};}
   function drawSource(){
    sourceRows.replaceChildren();if(!source)return;
    sourceRows.append(h('p',{},source.character.name+' · '+source.items.length+' items · '+selected.size+' selected'+(source.online.online?' · ONLINE — log out before capturing or copying.':'')));
    const table=h('table',{style:'width:100%;text-align:left'},h('thead',{},h('tr',{},...['Select','Item','Location','Properties'].map(x=>h('th',{},x))))),body=h('tbody');table.append(body);
    for(const row of source.items){const rec=GW.catalog.loadedRow('item',row.prop_id)||{};const box=h('input',{type:'checkbox','aria-label':'Select '+(rec.name||row.prop_id),disabled:row.sealed||row.item_location===2,onchange:e=>{e.target.checked?selected.add(row.id):selected.delete(row.id);sourceRows.firstChild.textContent=source.character.name+' · '+source.items.length+' items · '+selected.size+' selected'+(source.online.online?' · ONLINE — log out before capturing or copying.':'');}});box.checked=selected.has(row.id);
     body.append(h('tr',{},h('td',{},box),h('td',{},GW.icon(rec.sprite,{name:rec.name}),rec.name||('Item #'+row.prop_id)),h('td',{},['Equipment','Bag','Server temp','Warehouse'][row.item_location]+' '+(row.slot_index+1)),h('td',{},'×'+row.stack+' · Q'+row.item_quality+' G'+row.item_grade+(row.sealed?' · sealed pet — excluded':''))));}
    sourceRows.append(table);
   }
   async function loadShelf(){const r=await ctx.api('list');favourites=r.favourites;shelf.replaceChildren();if(!favourites.length)shelf.append(h('p',{class:'gw-dim'},'No favourites saved yet.'));
    for(const f of favourites)shelf.append(h('div',{style:'margin:8px 0'},h('b',{},f.name),' · '+f.items.length+' rows ',h('button',{class:'gw-btnx',onclick:()=>prepare(f.id)},'Preview for selected player')));
   }
   async function save(){await action(async()=>{await ctx.api('capture',{...selection(),name:name.value});await loadShelf();GW.status('Favourite saved.','ok');});}
   async function prepare(id){await action(async()=>{
    pending=await ctx.api('preview',{...(id?{favourite:id}:selection()),target,copies:Number(copies.value),location:Number(location.value)});
    previewBox.replaceChildren();previewBox.append(h('h2',{},'3. Review batch for '+pending.character),h('p',{},pending.items.length+' new item rows. One transaction, one backup and one Undo entry. Preview expires after 15 minutes.'));
    const list=h('ul');for(const p of pending.items)list.append(h('li',{},p.name+' ×'+p.item.stack+' · Q'+p.item.item_quality+' G'+p.item.item_grade+' → '+(p.location===1?'bag':'warehouse')+' cell '+(p.slot+1)));previewBox.append(list);
    for(const warning of pending.warnings)previewBox.append(h('p',{class:'tt-bad'},warning));
    previewBox.append(h('button',{class:'gw-btnx',onclick:()=>action(async()=>{const r=await ctx.api('apply',{preview:pending.preview});if(r.already_applied)GW.status('This batch was already applied; no duplicate items added.','ok');else GW.writeDone(r,'Item batch copied');pending=null;previewBox.replaceChildren();await readSource();})},'Copy '+pending.items.length+' rows to '+pending.character));
   });}
   try{
    const [characters]=await Promise.all([GW.api('/api/m/characters'),GW.catalog.load('item')]);
    if(!characters.some(c=>c.id===target)){label.textContent='Choose a valid destination character at the top of the editor.';return;}
    for(const c of characters)sourceSelect.append(h('option',{value:c.id},c.name+' (#'+c.id+')'));sourceSelect.value=String(target);
    const c=characters.find(c=>c.id===target);label.textContent='Selected destination: '+(c?c.name:'#'+target);
    await loadShelf();await loadSource();
   }catch(e){message.textContent=e.message;}
  }
 });
})();
