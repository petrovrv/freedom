<?php
function co_push_write_atomic($p,$v){file_put_contents($p,$v);}
$handlers=require __DIR__.'/../server/ops.php';
$d=sys_get_temp_dir().'/home-test-'.bin2hex(random_bytes(5));mkdir($d);
$c=['stateDir'=>$d,'owner'=>true,'myEnv'=>'owner','config'=>[]];$checks=0;
function check($v){global $checks;if(!$v)throw new Exception('test failed #'.($checks+1));$checks++;}
try{
 $member=$c;$member['owner']=false;$member['myEnv']='member';
 check(home_state($member)['error']==='home_forbidden');
 $allowed=$member;$allowed['config']=['members'=>'member'];
 check(isset(home_state($allowed)['entities']));
 $report=['online'=>true,'updated'=>time(),'results'=>[],'entities'=>[
 ['id'=>'switch.lamp','state'=>'off','actions'=>['turn_on','turn_off'],'attrs'=>[]],
 ['id'=>'climate.room','state'=>'heat','actions'=>['set_temperature'],'attrs'=>['min_temp'=>7,'max_temp'=>30,'target_temp_step'=>0.5]]]];
 home_exchange($c,$report);
 $cmd=['entity'=>'switch.lamp','action'=>'turn_on','requestKey'=>'test-key-000000001'];
 check(home_control($member,$cmd)['error']==='home_forbidden');
 $a=home_control($c,$cmd);$b=home_control($c,$cmd);check($a['id']===$b['id']);
 check(count(home_exchange($c,$report)['commands'])===1);
 check(count(home_exchange($c,$report)['commands'])===0); // Another worker/restart cannot claim twice.
 check(home_control($c,['entity'=>'switch.unknown','action'=>'turn_on','requestKey'=>'test-key-000000002'])['error']==='action_not_allowed');
 check(home_control($c,['entity'=>'switch.lamp','action'=>'toggle','requestKey'=>'test-key-000000003'])['error']==='action_not_allowed');
 foreach([6,31,20.3,'20',true] as $value){
  $r=home_control($c,['entity'=>'climate.room','action'=>'set_temperature','value'=>$value,'requestKey'=>'test-key-000000004']);
  check(isset($r['error']));
 }
 $report['results']=[$a['id']=>['status'=>'accepted','note'=>'ok']];
 check(count(home_exchange($c,$report)['commands'])===0);
 check(home_state($c)['results'][$a['id']]['status']==='accepted');
 $report['updated']=time()-30;home_exchange($c,$report);
 check(home_control($c,['entity'=>'switch.lamp','action'=>'turn_off','requestKey'=>'test-key-000000005'])['error']==='home_offline');
 echo "Dashboard PHP checks: $checks passed\n";
}finally{foreach(glob($d.'/*') as $f)unlink($f);rmdir($d);}
