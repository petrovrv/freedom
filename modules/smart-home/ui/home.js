// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const $=id=>document.getElementById(id), cards=new Map(), pending=new Map(), notes=new Map();
let data={entities:[],online:false,updated:0},kind='',busy=false,timer,signature='',planSig='',localReceipt=0,lastRefresh=0;
let view=localStorage.getItem('smart-home.view')||'plan';const proom=new Map(),plights=new Map();
function el(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e;}
function usable(){return data.online && Date.now()/1000-localReceipt<25;}
function icon(r){return r.kind==='climate'?'♨':r.kind==='switch'?'⏻':({temperature:'🌡',humidity:'💧',battery:'▰',door:'◫',window:'◫',occupancy:'◉',motion:'◉',power:'ϟ',illuminance:'☀'})[r.attrs.device_class]||'◈';}
function value(r){
 if(r.state==='unavailable'||r.state==='unknown')return 'Нет данных';
 if(r.kind==='climate')return r.attrs.current_temperature===undefined?'—':r.attrs.current_temperature+' °C';
 if(r.kind==='switch')return r.state==='on'?'Включено':'Выключено';
 if(r.id.startsWith('binary_sensor.')){
  const labels={door:['Закрыто','Открыто'],window:['Закрыто','Открыто'],occupancy:['Никого нет','Есть присутствие'],motion:['Нет движения','Движение'],battery:['Заряд в норме','Низкий заряд']};
  return (labels[r.attrs.device_class]||['Нет','Да'])[r.state==='on'?1:0];
 }
 const n=Number(r.state),v=Number.isFinite(n)?Number(n.toFixed(1)):r.state;
 return v+(r.attrs.unit_of_measurement?' '+r.attrs.unit_of_measurement:'');
}
function filtered(){const q=$('search').value.toLocaleLowerCase(),room=$('room').value;return data.entities.filter(r=>
 (!kind||r.kind===kind)&&(!room||r.room===room)&&(!q||(r.name+' '+r.room).toLocaleLowerCase().includes(q)));}
function makeCard(r){
 const card=el('article','card '+r.kind),top=el('div','card-top');
 top.append(el('span','icon',icon(r)),el('h3','',r.name));card.append(top);
 const val=el('div','value');card.append(val);
 let input,control;
 if(r.kind==='climate'){
  card.append(el('p','small','Температура в комнате'));
  const row=el('div','controls');input=el('input');input.type='number';input.setAttribute('aria-label','Температура: '+r.name);
  input.oninput=()=>input.dataset.dirty='1';
  control=el('button','','Задать');control.onclick=()=>send(r,'set_temperature',Number(input.value));
  row.append(input,el('span','small','°C'),control);card.append(row);
 }else if(r.actions.length){
  control=el('button');control.onclick=()=>{const current=data.entities.find(x=>x.id===r.id);send(current,current.state==='on'?'turn_off':'turn_on');};card.append(control);
 }
 if(r.note)card.append(el('p','small note',r.note));
 const result=el('div','result');result.setAttribute('role','status');card.append(result);
 cards.set(r.id,{card,val,input,control,result});return card;
}
function render(){
 const rows=filtered(),sig=rows.map(r=>r.id).join('|');
 if(sig!==signature){signature=sig;cards.clear();$('rooms').replaceChildren();
  const rooms=[...new Set(rows.map(r=>r.room))];
  for(const room of rooms){const section=el('section','room'),grid=el('div','grid');section.append(el('h2','',room),grid);
   for(const r of rows.filter(x=>x.room===room))grid.append(makeCard(r));$('rooms').append(section);}
 }
 $('empty').hidden=rows.length>0;
 for(const r of rows){
  const c=cards.get(r.id),unavailable=['unknown','unavailable'].includes(r.state);
  c.card.classList.toggle('unavailable',unavailable);c.val.textContent=value(r);c.val.classList.toggle('on',r.state==='on');
  if(c.control){c.control.disabled=!usable()||unavailable||pending.has(r.id)||!r.actions.length;
   if(r.kind!=='climate')c.control.textContent=pending.has(r.id)?'Выполняется…':r.state==='on'?'Выключить':'Включить';}
  if(c.input){const a=r.attrs;c.input.min=a.min_temp;c.input.max=a.max_temp;c.input.step=a.target_temp_step||0.5;
   c.input.disabled=!usable()||unavailable||pending.has(r.id);
   if(!c.input.dataset.dirty && document.activeElement!==c.input)c.input.value=a.temperature??'';
   c.control.textContent=pending.has(r.id)?'Выполняется…':'Задать';
  }
  const note=notes.get(r.id);c.result.textContent=note?.text||'';c.result.classList.toggle('error',!!note?.error);
 }
 const n=data.entities.length,rooms=new Set(data.entities.map(r=>r.room)).size;
 $('summary').textContent=rooms+' комнат и зон · '+n+' показаний и устройств';
 const connected=usable();$('connection').className=connected?'':'offline';
 renderPlan();
 $('connection').textContent=connected?'● На связи · обновлено '+new Date(data.updated*1000).toLocaleTimeString('ru-RU'):
 'Нет свежей связи с домом. Показаны последние данные; управление временно отключено.';
}
function updateRooms(){
 const options=[...new Set(data.entities.map(r=>r.room))],sel=$('room'),old=sel.value;
 if([...sel.options].slice(1).map(x=>x.value).join('|')!==options.join('|')){
  sel.replaceChildren(new Option('Все комнаты',''),...options.map(x=>new Option(x,x)));sel.value=options.includes(old)?old:'';
 }
}
async function refresh(){
 if(busy||Date.now()-lastRefresh<2000)return;lastRefresh=Date.now();busy=true;clearTimeout(timer);
 try{
  const r=await coExt.call('home_state',{},{timeoutMs:12000});if(r.error)throw Error(r.error);
  data=r;localReceipt=Date.now()/1000-Math.max(0,r.serverNow-r.updated);updateRooms();
  for(const [eid,p] of pending){
   const result=r.results?.[p.id];
   if(result){pending.delete(eid);notes.set(eid,{text:result.note,error:result.status!=='accepted'});
    if(result.status==='accepted'){const c=cards.get(eid);if(c?.input)delete c.input.dataset.dirty;}}
   else if(Date.now()-p.since>30000){pending.delete(eid);notes.set(eid,{text:'Ответ не получен. Проверьте состояние устройства.',error:true});}
  }
 }catch(e){data.online=false;
  if(String(e).includes('home_forbidden')){$('summary').textContent='Доступ к дому ещё не предоставлен';}}
 finally{busy=false;render();timer=setTimeout(refresh,document.hidden?15000:3000);}
}
async function send(r,action,v){
 if(!r||pending.has(r.id)||!usable())return;
 const c=cards.get(r.id);
 if(action==='set_temperature'&&(!c.input.value||!c.input.checkValidity())){c.input.reportValidity();return;}
 const requestKey=Array.from(crypto.getRandomValues(new Uint32Array(4)),n=>n.toString(16).padStart(8,'0')).join('-');pending.set(r.id,{id:'',since:Date.now()});notes.set(r.id,{text:'Отправляем…'});render();
 try{
  const result=await coExt.call('home_control',{entity:r.id,action,value:v,requestKey},{timeoutMs:12000});
  if(result.error)throw Error(result.error);
  pending.set(r.id,{id:result.id,since:Date.now()});notes.set(r.id,{text:'Ожидаем Home Assistant…'});
 }catch(e){pending.delete(r.id);notes.set(r.id,{text:'Не удалось подтвердить команду. Проверьте состояние.',error:true});}
 render();if(!busy)refresh();
}
// Floor plan (dashboard.json plan, sent by the home worker): rooms linked to entity rooms by name.
const SVG='http://www.w3.org/2000/svg';
function svg(tag,attrs){const e=document.createElementNS(SVG,tag);for(const k in attrs)e.setAttribute(k,attrs[k]);return e;}
function roomTemp(rows){
 const s=rows.find(r=>r.kind==='sensor'&&r.attrs.device_class==='temperature'&&!r.id.startsWith('binary_sensor.'))
  ||rows.find(r=>r.kind==='climate');
 if(!s)return null;
 const v=s.kind==='climate'?s.attrs.current_temperature:Number(s.state);
 return ['unknown','unavailable'].includes(s.state)||!Number.isFinite(Number(v))?NaN:Number(v);
}
function tempClass(t){return t===null?'':Number.isNaN(t)?'nodata':t<17?'cold':t<19?'cool':t<=22.5?'ok':t<=24?'warm':'hot';}
function openRoom(room){view='list';localStorage.setItem('smart-home.view',view);$('room').value=room;render();}
function buildPlan(){
 proom.clear();plights.clear();const box=el('div','floors');
 for(const f of data.plan.floors){
  const sec=el('section','floor'),fb=el('div','floor-box');fb.style.aspectRatio=f.w+' / '+f.h;
  if(f.name)sec.append(el('h2','',f.name));
  for(const r of f.rooms){
   const linked=!!r.room&&data.entities.some(x=>x.room===r.room),d=el('div','proom '+(linked?'linked':'blank'));
   Object.assign(d.style,{left:r.x/f.w*100+'%',top:r.y/f.h*100+'%',width:r.w/f.w*100+'%',height:r.h/f.h*100+'%'});
   if(r.name)d.append(el('div','pname',r.name));
   if(linked){
    const t=el('div','ptemp'),m=el('div','pmeta'),l=el('div','plights');d.append(t,m,l);
    d.title=r.room;d.tabIndex=0;d.setAttribute('role','button');d.setAttribute('aria-label','Открыть: '+r.room);
    d.onclick=e=>{if(!e.target.closest('button'))openRoom(r.room);};
    d.onkeydown=e=>{if(e.key==='Enter'&&e.target===d)openRoom(r.room);};
    for(const x of data.entities.filter(x=>x.room===r.room&&x.kind==='switch'&&x.actions.length)){
     const b=el('button','','💡');b.type='button';
     b.onclick=()=>{const cur=data.entities.find(y=>y.id===x.id);if(cur)send(cur,cur.state==='on'?'turn_off':'turn_on');};
     l.append(b);plights.set(x.id,b);
    }
    proom.set(d,{room:r.room,t,m});
   }
   fb.append(d);
  }
  const s=svg('svg',{viewBox:'0 0 '+f.w+' '+f.h,preserveAspectRatio:'none','aria-hidden':'true'});
  if(f.outline.length>2)s.append(svg('polygon',{points:f.outline.map(p=>p.join(',')).join(' '),fill:'none',stroke:'#26332b','stroke-width':4,'vector-effect':'non-scaling-stroke'}));
  const colors={door:'#2a9d3a',window:'#4f9fd6',wall:'#26332b'};
  for(const ln of f.lines)s.append(svg('line',{x1:ln.x1,y1:ln.y1,x2:ln.x2,y2:ln.y2,stroke:colors[ln.kind],'stroke-width':ln.kind==='wall'?2:5,'vector-effect':'non-scaling-stroke'}));
  fb.append(s);sec.append(fb);box.append(sec);
 }
 const legend=el('div','plegend');
 for(const [c,t] of [['#b9d6f2','< 17 °C'],['#d6e9f5','17–19'],['#cdebd3','19–22,5'],['#fbdcb4','22,5–24'],['#f5ae96','> 24'],['#e2e2e2','нет данных']]){
  const s=el('span','sw',t);s.style.setProperty('--c',c);legend.append(s);}
 legend.append(el('span','','💡 — включить или выключить свет; нажмите на комнату, чтобы открыть её устройства'));
 $('plan').replaceChildren(box,legend);
}
function renderPlan(){
 const has=!!data.plan?.floors?.length;$('views').hidden=!has;
 const showPlan=has&&view==='plan';$('plan').hidden=!showPlan;$('list').hidden=showPlan;
 for(const b of $('views').children)b.classList.toggle('selected',b.dataset.view===(showPlan?'plan':'list'));
 if(!showPlan)return;
 const sig=JSON.stringify(data.plan)+'|'+data.entities.map(r=>r.id+':'+r.room).join('|');
 if(sig!==planSig){planSig=sig;buildPlan();}
 for(const [d,p] of proom){
  const rows=data.entities.filter(r=>r.room===p.room),t=roomTemp(rows);
  d.className='proom linked '+tempClass(t);
  p.t.textContent=t===null?'':Number.isNaN(t)?'—':t.toFixed(1)+'°';
  const hum=rows.find(r=>r.attrs.device_class==='humidity'&&Number.isFinite(Number(r.state)));
  const cl=rows.find(r=>r.kind==='climate'&&r.attrs.temperature!==undefined&&r.attrs.temperature!==null);
  const bin=r=>r.id.startsWith('binary_sensor.')&&r.state==='on';
  const who=rows.some(r=>bin(r)&&['occupancy','motion','presence'].includes(r.attrs.device_class));
  const win=rows.some(r=>bin(r)&&['window','door'].includes(r.attrs.device_class));
  p.m.textContent=[hum?Math.round(Number(hum.state))+' %':'',cl?'→ '+cl.attrs.temperature+'°':'',who?'◉ кто-то есть':'',win?'окно открыто':''].filter(Boolean).join(' · ');
 }
 for(const [id,b] of plights){
  const r=data.entities.find(x=>x.id===id);if(!r)continue;const bad=['unknown','unavailable'].includes(r.state);
  b.classList.toggle('on',r.state==='on');b.disabled=!usable()||bad||pending.has(id);
  b.title=r.name+': '+(pending.has(id)?'выполняется…':bad?'нет данных':r.state==='on'?'включено':'выключено');b.setAttribute('aria-label',b.title);
 }
}
$('views').onclick=e=>{const b=e.target.closest('button');if(!b)return;view=b.dataset.view;localStorage.setItem('smart-home.view',view);render();};
$('search').oninput=render;$('room').onchange=render;$('refresh').onclick=refresh;
$('kinds').onclick=e=>{const b=e.target.closest('button');if(!b)return;kind=b.dataset.kind;
 for(const x of $('kinds').children)x.classList.toggle('selected',x===b);render();};
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
window.addEventListener('focus',()=>{if(!document.hidden)refresh();});
// ext/1 F14b: an agent ran an action of this app (core event refresh)
coExt.on('refresh',()=>{lastRefresh=0;refresh();});
function renderPair(){
 const b=$('pair'),s=pair.state;
 $('pair-label').textContent=pair.text();b.className='secondary pair '+s.state;
 b.disabled=pair.busy||s.state==='offline'||s.state==='stopping';
 b.title=pair.active()?'Нажмите, чтобы остановить':'Открыть сопряжение новых Zigbee-устройств на 5 минут';
 const note=[s.device,s.note].filter(Boolean).join(' · ');$('pair-note').textContent=note;$('pair-note').hidden=!note;
}
let pair;
coExt.ready.then(ctx=>{coExt.title('Дом');refresh();setInterval(render,1000);
 if(ctx.owner){$('pair').hidden=false;pair=window.zigbeePair(renderPair);$('pair').onclick=()=>pair.toggle();pair.refresh();}
});
})();
