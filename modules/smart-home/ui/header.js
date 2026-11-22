// SPDX-License-Identifier: AGPL-3.0-or-later
// Tray of the Home app (ext/1 shell): nothing while Zigbee pairing is idle; a small chip with the countdown while it
// runs and for a minute after its result. The button itself lives on the Home page (/x/smart-home/home).
(() => {
'use strict';
const chip=document.getElementById('chip'),label=document.getElementById('label');
let pair,last='';
function render(){
 const s=pair.state,show=pair.active()||pair.recent();
 chip.hidden=!show;chip.className=s.state;
 const t=s.state==='success'?'✓':s.state==='timeout'||s.state==='error'?'!':(pair.text().match(/\d+:\d\d/)||['…'])[0];
 label.textContent=t;
 chip.title='Zigbee: '+pair.text()+(s.device?' · '+s.device:'')+' · открыть Дом';
 chip.setAttribute('aria-label',chip.title);
 const key=show?Math.ceil(chip.getBoundingClientRect().width)+2+'':'0';
 if(key!==last){last=key;coExt.size(Number(key),show?32:0,false);}
}
coExt.ready.then(ctx=>{
 coExt.size(0,0,false);
 if(!ctx.owner)return;
 chip.onclick=()=>coExt.navigate('/x/smart-home/home','self');
 pair=window.zigbeePair(render,true);pair.refresh();
});
})();
