// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const $=id=>document.getElementById(id), cards=new Map(), pending=new Map(), notes=new Map();
let data={entities:[],online:false,updated:0},kind='',busy=false,timer,signature='',localReceipt=0,lastRefresh=0;
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
$('search').oninput=render;$('room').onchange=render;$('refresh').onclick=refresh;
$('kinds').onclick=e=>{const b=e.target.closest('button');if(!b)return;kind=b.dataset.kind;
 for(const x of $('kinds').children)x.classList.toggle('selected',x===b);render();};
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
window.addEventListener('focus',()=>{if(!document.hidden)refresh();});
coExt.ready.then(()=>{coExt.title('Дом');refresh();setInterval(render,1000);});
})();
