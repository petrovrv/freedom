<?php
function co_push_write_atomic($p,$v){file_put_contents($p,$v);}
$handlers=eval(substr(base64_decode($argv[1]),5));
$d=sys_get_temp_dir().'/pair-test-'.bin2hex(random_bytes(5));mkdir($d);
$c=['stateDir'=>$d,'owner'=>true];$checks=0;
function check($v){global $checks;if(!$v)throw new Exception('test failed #'.($checks+1));$checks++;}
try{
 check(($handlers['user']['home_pair_start']($c,[])['error']??'')==='worker_offline');
 check(($handlers['user']['home_pair_start'](['stateDir'=>$d,'owner'=>false],[])['error']??'')==='owner_only');
 $r=['state'=>'idle','connected'=>true,'id'=>''];
 $handlers['host']['home_pair_exchange']($c,['report'=>$r]);
 check($handlers['user']['home_pair_start']($c,[])['state']==='starting');
 $a=$handlers['host']['home_pair_exchange']($c,['report'=>$r])['command'];
 $handlers['user']['home_pair_start']($c,[]);
 $b=$handlers['host']['home_pair_exchange']($c,['report'=>$r])['command'];
 check($a['id']===$b['id']);
 $r=['state'=>'searching','connected'=>true,'id'=>$a['id'],'until'=>time()+300];
 $handlers['host']['home_pair_exchange']($c,['report'=>$r]);
 check($handlers['user']['home_pair_state']($c,[])['state']==='searching');
 check($handlers['user']['home_pair_stop']($c,[])['state']==='stopping');
 echo "PHP checks: $checks passed\n";
}finally{foreach(glob($d.'/*') as $f)unlink($f);rmdir($d);}
