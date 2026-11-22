// SPDX-License-Identifier: AGPL-3.0-or-later
// Tray of the WhatsApp agent (ext/1 shell): a small 💬 chip while the WhatsApp service of the home PC runs - green
// while it polls (it does so every 20 s while its WhatsApp is connected and pushes are on), red when it stopped.
// Nothing for a workbench where the service never ran or, for a member, whose pushes are not on WhatsApp.
(() => {
'use strict';
const OP='wa_status',PERIOD=60000;
const ready=()=>true;
function view(s){
 if(!s||s.error||typeof s.seen!=='number'||s.seen>7*86400)return null;
 const mine=s.pushes?' · your CO pushes go to WhatsApp':'';
 return s.seen<=90?{ok:true,short:'connected',title:'WhatsApp agent connected'+mine}
  :{ok:false,short:'offline',title:'WhatsApp agent silent since '+ago(s.seen)+' (home PC off or WhatsApp disconnected)'};
}
const chip=document.getElementById('chip'),label=document.getElementById('label');
let last='',wide=false,state=null;
function fit(){
 const key=chip.hidden?'0':Math.ceil(chip.getBoundingClientRect().width)+2+'';
 if(key!==last){last=key;coExt.size(Number(key),chip.hidden?0:32,false);}
}
function ago(s){return s<90?'just now':s<5400?Math.round(s/60)+' min ago':s<172800?Math.round(s/3600)+' h ago':Math.round(s/86400)+' days ago';}
function render(){
 const v=state&&view(state);
 chip.hidden=!v;
 if(v){
  chip.className=v.ok?'ok':'bad';
  chip.title=v.title+' (click for details)';
  chip.setAttribute('aria-label',v.title);
  label.textContent=wide?v.short:'';
 }
 fit();
}
async function poll(){
 try{state=await coExt.call(OP,{},{timeoutMs:15000});}catch{state=null;}
 render();
}
coExt.ready.then(ctx=>{
 coExt.size(0,0,false);
 if(!ready(ctx))return;
 chip.onclick=()=>{wide=!wide;render();};
 poll();setInterval(poll,PERIOD);
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)poll();});
});
})();
