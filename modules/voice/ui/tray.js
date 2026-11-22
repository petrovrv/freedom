// SPDX-License-Identifier: AGPL-3.0-or-later
// Tray of the voice assistant (ext/1 shell): a small 🎙 chip while the voice hub is configured - green when the hub
// answers, red when it does not. Nothing when this workbench has no voice hub. Click shows the state as text.
(() => {
'use strict';
const OP='voice_status',PERIOD=120000;
const ready=()=>true;
function view(s){
 if(!s||s.error||!s.configured)return null;
 return s.online?{ok:true,short:'online',title:'Voice hub online'+(s.version?' · '+s.version:'')}
  :{ok:false,short:'offline',title:'Voice hub does not answer'};
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
