<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
declare(strict_types=1);
function home_allowed(array $c): bool {
    $allowed = array_filter(array_map('trim', explode(',', $c['config']['members'] ?? '')));
    return $c['owner'] || ($c['myEnv'] !== '' && in_array($c['myEnv'], $allowed, true));
}
function home_locked(array $c, callable $fn): array {
    $path=$c['stateDir'].'/dashboard.json';
    $lock=fopen($c['stateDir'].'/dashboard.lock','c');
    if (!$lock || !flock($lock,LOCK_EX)) throw new RuntimeException('home_lock');
    try {
        $s=json_decode((string)@file_get_contents($path),true) ?: [];
        $result=$fn($s);
        co_push_write_atomic($path,json_encode($s,JSON_UNESCAPED_UNICODE|JSON_THROW_ON_ERROR));
        return $result;
    } finally {flock($lock,LOCK_UN);fclose($lock);}
}
function home_online(array $s): bool {
    return ($s['online']??false) && time()-($s['seen']??0)<25 && time()-($s['updated']??0)<25;
}
function home_state(array $c): array {
    if (!home_allowed($c)) return ['error'=>'home_forbidden'];
    return home_locked($c,static function (&$s) {
        $s['interest']=time()+35;
        foreach (($s['commands']??[]) as $id=>$cmd) {
            if ($cmd['expires']<=time() && !isset($s['results'][$id]))
                $s['results'][$id]=['id'=>$id,'status'=>'error','note'=>($cmd['claimed']??false)?'Результат неизвестен; проверьте состояние':'Время ожидания истекло'];
        }
        return ['online'=>home_online($s),'updated'=>$s['updated']??0,
            'entities'=>$s['entities']??[],'results'=>$s['results']??[], 'serverNow'=>time()];
    });
}
function home_control(array $c,array $in): array {
    if (!home_allowed($c)) return ['error'=>'home_forbidden'];
    $eid=$in['entity']??'';$action=$in['action']??'';$key=$in['requestKey']??'';
    if (!is_string($eid) || !preg_match('/^(switch|light|climate)\.[a-z0-9_]+$/D',$eid)
        || !is_string($key) || !preg_match('/^[a-zA-Z0-9-]{16,80}$/D',$key))
        return ['error'=>'bad_command'];
    return home_locked($c,static function (&$s) use($c,$in,$eid,$action,$key) {
        $id=substr(hash('sha256',$c['myEnv'].':'.$key),0,32);
        if (isset($s['commands'][$id]) || isset($s['results'][$id])) return ['id'=>$id];
        if (!home_online($s)) return ['error'=>'home_offline'];
        $row=null;
        foreach ($s['entities']??[] as $r) if ($r['id']===$eid) {$row=$r;break;}
        if (!$row || !in_array($action,$row['actions']??[],true)) return ['error'=>'action_not_allowed'];
        if (in_array($row['state'],['unknown','unavailable'],true)) return ['error'=>'device_unavailable'];
        $value=null;
        if ($action==='set_temperature') {
            $value=$in['value']??null;$a=$row['attrs'];
            if ((!is_int($value)&&!is_float($value)) || !is_finite((float)$value)
                || $value<($a['min_temp']??5) || $value>($a['max_temp']??30))
                return ['error'=>'temperature_range'];
            $step=(float)($a['target_temp_step']??0.5);
            if ($step<=0 || abs(($value-$a['min_temp'])/$step-round(($value-$a['min_temp'])/$step))>0.001)
                return ['error'=>'temperature_step'];
        } elseif (!in_array($action,['turn_on','turn_off'],true)) return ['error'=>'action_not_allowed'];
        $s['commands']=array_filter($s['commands']??[],static fn($x)=>$x['expires']>time());
        if (count($s['commands'])>=30) return ['error'=>'queue_busy'];
        $s['commands'][$id]=['id'=>$id,'entity'=>$eid,'action'=>$action,'value'=>$value,'expires'=>time()+20];
        $s['interest']=time()+35;
        return ['id'=>$id];
    });
}
function home_exchange(array $c,array $in): array {
    if (!is_array($in['entities']??null) || count($in['entities'])>500
        || !is_array($in['results']??null)) return ['error'=>'bad_report'];
    return home_locked($c,static function (&$s) use($in) {
        $offset=time()-(float)($in['hostNow']??time());
        $s['entities']=$in['entities'];$s['updated']=(int)(($in['updated']??0)+$offset);
        $s['online']=($in['online']??false)===true;$s['seen']=time();
        foreach (array_slice($in['results'], -200, null, true) as $id=>$r)
            if (isset($s['commands'][$id]) && is_array($r)) {
                $s['results'][$id]=['id'=>$id,'status'=>($r['status']??'')==='accepted'?'accepted':'error',
                    'note'=>mb_substr((string)($r['note']??''),0,180)];
                unset($s['commands'][$id]);
            }
        $s['results']=array_slice($s['results']??[],-200,null,true);
        $commands=[];
        foreach ($s['commands']??[] as $id=>$cmd) {
            if ($cmd['expires']>time()) {
                if (!($cmd['claimed']??false)) {
                    $s['commands'][$id]['claimed']=true;
                    $cmd['expires']-=$offset;$commands[]=$cmd;
                }
            }
            else {
                $s['results'][$id]=['id'=>$id,'status'=>'error','note'=>($cmd['claimed']??false)?'Результат неизвестен; проверьте состояние':'Время ожидания истекло'];
                unset($s['commands'][$id]);
            }
        }
        return ['commands'=>$commands,'pollSeconds'=>($s['interest']??0)>time()?2:8];
    });
}
