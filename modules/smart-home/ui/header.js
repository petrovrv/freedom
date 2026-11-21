// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const b=document.getElementById('pair'), label=document.getElementById('label');
let state={state:'idle'}, busy=false, timer, offset=0, owner=false;
const active=()=>['starting','searching','interviewing','stopping'].includes(state.state);
function render(){
 const names={idle:'Сопряжение Zigbee',starting:'Запускаем…',stopping:'Останавливаем…',
 success:'Подключено ✓',timeout:'Не найдено · повторить',stopped:'Сопряжение Zigbee',
 error:'Ошибка · повторить',offline:'Zigbee недоступен'};
 const remaining=Math.max(0,Math.ceil((state.until||0)-(Date.now()/1000+offset)));
 label.textContent=state.state==='searching'||state.state==='interviewing'
 ? (state.state==='interviewing'?'Подключаем ':'Поиск ')+Math.floor(remaining/60)+':'+String(remaining%60).padStart(2,'0')
 : names[state.state]||names.idle;
 b.className=state.state;
 b.disabled=busy||!owner||state.state==='offline'||state.state==='stopping';
 b.title=[state.device,state.note,active()?'Нажмите, чтобы остановить':'Сопряжение Zigbee · 5 минут'].filter(Boolean).join(' · ');
 b.setAttribute('aria-label',label.textContent+' — '+b.title);
 coExt.size(Math.ceil(b.getBoundingClientRect().width)+2,32,false);
}
async function refresh(op='home_pair_state'){
 if(busy)return;
 clearTimeout(timer);busy=true;render();
 try{
  const r=await coExt.call(op,{},{timeoutMs:12000});
  if(r.error)throw Error(r.error);
  state=r; offset=(r.serverNow||Date.now()/1000)-Date.now()/1000;
 }catch(e){state={state:'offline',note:'Нет связи с Zigbee. Повторная проверка через несколько секунд.'};}
 finally{busy=false;render();timer=setTimeout(()=>refresh(),active()?2000:state.state==='offline'?5000:30000);}
}
b.onclick=()=>refresh(active()?'home_pair_stop':'home_pair_start');
coExt.ready.then(ctx=>{
 owner=!!ctx.owner;
 if(!owner){b.hidden=true;coExt.size(0,0,false);return;}
 refresh();setInterval(()=>{if(active())render();},1000);
});
})();
