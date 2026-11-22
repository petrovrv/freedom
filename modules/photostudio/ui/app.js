// SPDX-License-Identifier: AGPL-3.0-or-later
// The PhotoStudio app (ext/1 shell, F14d device app): on a computer with CO Desktop the page starts the program
// through the module's device op ps_open (with ?path=<photo> from "Open in PhotoStudio"); elsewhere it says where the
// program runs. The page itself edits nothing.
(() => {
'use strict';
const $=(id)=>document.getElementById(id);
const open=$('open'),msg=$('msg'),file=$('file');
function say(t,err){msg.textContent=t;msg.className=err?'err':'';}
let path='';
async function start(){
 open.disabled=true;say(path?'Opening the photo in PhotoStudio…':'Starting PhotoStudio…');
 try{
  const r=await coExt.call('ps_open',path?{path}:{},{timeoutMs:60000});
  if(r&&r.ok)say(path?'Opened in PhotoStudio on this computer.':'PhotoStudio started on this computer.');
  else say((r&&(r.hint||r.error))||'PhotoStudio did not start.',true);
 }catch(e){say(String(e&&e.message||e),true);}
 open.disabled=false;
}
coExt.ready.then(async (ctx)=>{
 path=typeof ctx.params?.path==='string'?ctx.params.path:'';
 if(path){file.textContent=path.split('/').pop();file.hidden=false;}
 coExt.title(path?'PhotoStudio · '+path.split('/').pop():'PhotoStudio');
 let st=null;
 try{st=await coExt.call('ps_status',{},{timeoutMs:30000});}
 catch(e){
  const why=String(e&&e.message||e);
  if(/device_op_unavailable/.test(why))say('PhotoStudio runs on a computer: open this page in CO Desktop there. From here, ask an agent in a chat to use its tools.');
  else if(/module_off/.test(why))say('PhotoStudio is off on this computer: CO Desktop → Connectors → Modules.',true);
  else say(why,true);
  $('agent').open=true;
  return;
 }
 if(!st||!st.installed){say((st&&st.hint)||'PhotoStudio is not installed on this computer.',true);return;}
 open.hidden=false;open.onclick=start;
 if(path)start();
});
})();
