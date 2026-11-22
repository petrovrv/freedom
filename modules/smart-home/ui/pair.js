// SPDX-License-Identifier: AGPL-3.0-or-later
// Zigbee pairing state shared by the Home page (the button) and the tray (a small chip while pairing runs).
// tray: true - the header chip polls every 10 s while idle (it cannot hear the Home page start pairing) and every 5 s
// while it runs (the countdown is local); the Home page polls every 2 s while pairing runs, 30 s otherwise.
window.zigbeePair=(onChange,tray)=>{
'use strict';
let state={state:'idle'},busy=false,timer,offset=0,seenUtc=0;
const active=()=>['starting','searching','interviewing','stopping'].includes(state.state);
const names={idle:'Сопряжение Zigbee',starting:'Запускаем…',stopping:'Останавливаем…',success:'Подключено ✓',
 timeout:'Не найдено · повторить',stopped:'Сопряжение Zigbee',error:'Ошибка · повторить',offline:'Zigbee недоступен'};
const remaining=()=>Math.max(0,Math.ceil((state.until||0)-(Date.now()/1000+offset)));
const clock=()=>{const r=remaining();return Math.floor(r/60)+':'+String(r%60).padStart(2,'0');};
function text(){
 if(state.state==='searching'||state.state==='interviewing')return (state.state==='interviewing'?'Подключаем ':'Поиск ')+clock();
 return names[state.state]||names.idle;
}
// a finished result (green success, timeout, error) stays visible in the tray for a minute after it was first seen
function recent(){return ['success','timeout','error'].includes(state.state)&&Date.now()-seenUtc<60000;}
async function refresh(op='home_pair_state'){
 if(busy)return;clearTimeout(timer);busy=true;onChange();
 const before=state.state;
 try{const r=await coExt.call(op,{},{timeoutMs:12000});if(r.error)throw Error(r.error);
  state=r;offset=(r.serverNow||Date.now()/1000)-Date.now()/1000;}
 catch(e){state={state:'offline',note:'Нет связи с Zigbee. Повторная проверка через несколько секунд.'};}
 finally{
  if(state.state!==before)seenUtc=Date.now();
  busy=false;onChange();
  timer=setTimeout(()=>refresh(),document.hidden?60000:active()?(tray?5000:2000):state.state==='offline'?(tray?30000:5000):(tray?10000:30000));
 }
}
setInterval(()=>{if(active()||recent())onChange();},1000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
return {get state(){return state;},get busy(){return busy;},active,recent,text,refresh,
 toggle(){return refresh(active()?'home_pair_stop':'home_pair_start');}};
};
