# Local packaged UI acceptance, using a controlled DLNA/Range server and the BTR17.
param([string]$DeviceId='{0.0.0.00000000}.{20f57f8f-d61c-4c73-892a-fe2bcdf376e9}',
    [ValidateSet('audio-stage5','audio-stage6')][string]$ArtifactGroup='audio-stage5')
$ErrorActionPreference='Stop';$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts/$ArtifactGroup/$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$trace=Join-Path $artifacts 'audio.jsonl'
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID;$env:WINAPP_UI_WORKFLOW_ID="audio-stage5-$id"
$appPid=0;$hwnd=$null;$owned=$false;$server=$null
$beforePids=@(Get-Process ContinuousPlayer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$results=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[string]]::new();$observations=[Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments){$target=if($hwnd -and $Arguments[0] -ne 'list-windows'){@('-w',$hwnd)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Id){UI @('wait-for',$Id,'-t','10000') | Out-Null}
function Gone([string]$Id){UI @('wait-for',$Id,'--gone','-t','10000') | Out-Null}
function Keys([string]$Value){UI @('send-keys',$Value,'--via','send-input') | Out-Null}
function Until([scriptblock]$Condition,[int]$Seconds=20){$end=[DateTime]::UtcNow.AddSeconds($Seconds);while([DateTime]::UtcNow -lt $end){if(& $Condition){return};Start-Sleep -Milliseconds 100};throw 'Timed out waiting for evidence.'}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect{$tree=UI @('inspect','--json','-d','13') | ConvertFrom-Json;@(Nodes @($tree.windows | ForEach-Object elements))}
function Traces{if(Test-Path -LiteralPath $trace){@(Get-Content -LiteralPath $trace | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})}else{@()}}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});throw}}
function Capture([string]$Name){$path=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$path,'--json') | Out-Null;if(!(Test-Path -LiteralPath $path)){throw 'Screenshot missing.'};$screenshots.Add($path)}
function Ready{UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','15000') | Out-Null}
function PlaybackReady{UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','30000') | Out-Null}
function State([string]$Label){UI @('wait-for','Resume','-p','Name','--value',$Label,'-t','10000') | Out-Null}
function Info{Keys 'i';Wait 'MediaInfoText';(UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json).text}
function CloseInfo{Keys 'i';Gone 'MediaInfoText';UI @('wait-for','PlaybackInfo','-p','IsEnabled','--value','True','-t','10000') | Out-Null}
function OpenRoot{UI @('invoke','ChooseFolder') | Out-Null;Ready;UI @('scroll-into-view',$server.RootName) | Out-Null;UI @('click',$server.RootName) | Out-Null;Ready}
function Failure{
    UI @('wait-for','PlaybackFailureText','-t','25000') | Out-Null
    $text=(UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json).text
    if($text -notmatch '最後に確認した位置: (\d{2}:\d{2}:\d{2})'){throw 'Missing confirmed failure position.'}
    @{text=$text;seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds}
}
function GoHome{
    $nodes=Inspect
    if($nodes.automationId -contains 'PlaybackFailureText'){UI @('invoke','CloseButton') | Out-Null;Gone 'PlaybackFailureText';UI @('wait-for','Resume','-p','IsEnabled','--value','True','-t','10000') | Out-Null}
    if($nodes.automationId -contains 'MediaInfoText'){CloseInfo}
    if($nodes.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null;Gone 'BrowserList'}
    Keys 'h';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
}
Add-Type -Path (Join-Path $PSScriptRoot 'AudioHttp-TestServer.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AudioStage5Native {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h,uint message,UIntPtr w,IntPtr l);
 public static void Power(IntPtr hwnd,int pid,bool suspend){GetWindowThreadProcessId(hwnd,out uint actual);if(actual!=pid)throw new Exception("Unexpected target");SendMessageW(hwnd,0x218,new UIntPtr(suspend?4u:18u),IntPtr.Zero);}
}
'@
try{
    $server=[AudioHttpTestServer]::new()
    foreach($p in @(@('a','01 44.1k.wav','audio/wav'),@('b','02 96k.flac','audio/flac'),@('c','03 192k.wav','audio/wav'),@('d','04 44.1k.m4a','audio/mp4'))){$server.Add($p[0],$p[1],[IO.File]::ReadAllBytes((Join-Path $repo ".tools/audio-stage3/ui-media/$($p[1])")),$p[2],$p[0] -eq 'b')}
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--args',"--exclusive-audio --audio-device `"$DeviceId`" --audio-log `"$trace`"",'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId;if(!$appPid -or $beforePids -contains $appPid){throw 'No newly owned test process.'}
    UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','15000') | Out-Null
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
    $hwnd=[string]$main.hwnd;if(!$hwnd){throw 'No main HWND.'}
    Case 'Register controlled DLNA endpoint and show four audio resources' {
        UI @('invoke','ChooseFolder') | Out-Null;Ready;UI @('scroll-into-view','BrowserItem:add') | Out-Null;UI @('click','BrowserItem:add') | Out-Null
        Wait 'NAS / DLNA';UI @('click','NAS / DLNA') | Out-Null;Wait 'ManualDlnaConnect';UI @('invoke','ManualDlnaConnect') | Out-Null
        Wait 'DlnaAddress';UI @('set-value','DlnaAddress',$server.DescriptionUri.AbsoluteUri) | Out-Null
        UI @('invoke','PrimaryButton') | Out-Null;Gone 'DlnaAddress';Ready
        UI @('click',$server.RootName) | Out-Null;Ready;Wait 'RegisterFolder';UI @('invoke','RegisterFolder') | Out-Null;Ready;$owned=$true
        UI @('click',$server.RootName) | Out-Null;Ready;Wait '01 44.1k.wav';Capture '01-remote-audio-list'
    }
    Case 'Natural HTTP playlist applies 44.1 / 96 / 192 / 44.1 kHz and drains all tracks' {
        UI @('click','01 44.1k.wav') | Out-Null
        Until {@(Traces | Where-Object Event -eq ended).Count -ge 4} 90
        $ended=@(Traces | Where-Object Event -eq ended)
        if(($ended.Output.Format.SampleRate -join ',') -ne '44100,96000,192000,44100'){throw 'Wrong actual rate sequence.'}
        if(@($ended | Where-Object {$_.PlayedFrames -lt $_.ContentFrames -or $_.ContentFrames -le 0}).Count){throw 'End preceded output drain.'}
        if(@(Traces | Where-Object Event -eq failed).Count){throw 'HTTP playlist failed.'}
        State '再生';Capture '02-remote-final-end'
    }
    Case 'Converted resource information uses received 96 kHz PCM instead of original 192 kHz tag' {
        UI @('focus','TouchSurface') | Out-Null;Keys 'left left';PlaybackReady;State '再生'
        $text=Info
        if($text -notmatch 'サーバー変換あり' -or $text -notmatch 'デコードPCM: 96,000 Hz' -or $text -notmatch '実際の再生モード: WASAPI排他' -or $text -match '192,000 Hz'){throw "Wrong received resource metadata: $text"}
        $text | Set-Content -LiteralPath (Join-Path $artifacts 'converted-info.txt') -Encoding utf8
        Capture '03-converted-information';CloseInfo;GoHome
    }
    Case 'HTTP disconnect shows a network failure at a confirmed position without advancing' {
        $wave=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($wave)
        $length=44100*4*120
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([uint32](36+$length));$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([uint32]16);$writer.Write([uint16]1);$writer.Write([uint16]2);$writer.Write([uint32]44100);$writer.Write([uint32]176400);$writer.Write([uint16]4);$writer.Write([uint16]16);$writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([uint32]$length);$writer.Write([byte[]]::new($length));$writer.Flush()
        $server.Add('z','05 Long.wav',$wave.ToArray(),'audio/wav',$false);$writer.Dispose();$wave.Dispose()
        OpenRoot;UI @('click','05 Long.wav') | Out-Null;PlaybackReady;State '一時停止'
        UI @('focus','TouchSurface') | Out-Null;Keys 'down'
        Until {@(Traces | Where-Object {$_.Event -eq 'seek-completed' -and [TimeSpan]::Parse($_.Position).TotalSeconds -ge 10 -and $_.File -match '/audio/z'}).Count -gt 0}
        $server.Disconnect();$script:saved=Failure
        if($saved.seconds -lt 10 -or $saved.text -notmatch 'HTTP音声' -or $saved.text -notmatch 'URL'){throw "Wrong network failure: $($saved.text)"}
        $observations.Add(@{name='disconnect';seconds=$saved.seconds});Capture '04-http-failure'
    }
    Case 'Retry reacquires the expired URL and restores position before playback' {
        $server.Recover();$browses=$server.BrowseRequests
        UI @('invoke','PrimaryButton') | Out-Null;Gone 'PlaybackFailureText';PlaybackReady;State '一時停止'
        Until {@(Traces | Where-Object {$_.Event -eq 'playing' -and $_.File -match '/audio/z\?v=1'}).Count -gt 0}
        $played=Traces | Where-Object {$_.Event -eq 'playing' -and $_.File -match '/audio/z\?v=1'} | Select-Object -Last 1
        if($server.BrowseRequests -le $browses -or [math]::Abs([TimeSpan]::Parse($played.Position).TotalSeconds-$saved.seconds) -gt 2){throw 'URL/position restoration was not verified.'}
        $observations.Add(@{name='retry';position=$played.Position;urlGeneration=1});Capture '05-restored-http-playback'
    }
    Case 'Suspend during delayed retry preserves position and waits for manual resume' {
        $server.Disconnect();$before=Failure;$server.Recover();$server.HeaderDelayMilliseconds=1200;$requests=$server.MediaRequests
        UI @('invoke','PrimaryButton') | Out-Null;Gone 'PlaybackFailureText'
        Until {$server.MediaRequests -gt $requests}
        [AudioStage5Native]::Power([IntPtr][long]$hwnd,$appPid,$true);[AudioStage5Native]::Power([IntPtr][long]$hwnd,$appPid,$false)
        PlaybackReady;State '再生';$server.HeaderDelayMilliseconds=0
        $text=Info;if($text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})' -or [math]::Abs([TimeSpan]::Parse($Matches[1]).TotalSeconds-$before.seconds) -gt 2){throw 'Interrupted HTTP retry lost its position.'}
        Capture '06-interrupted-http-retry';CloseInfo;State '再生'
        $count=@(Traces | Where-Object Event -eq playing).Count;Start-Sleep -Milliseconds 300
        if(@(Traces | Where-Object Event -eq playing).Count -ne $count){throw 'Closing information resumed interrupted playback.'}
        UI @('invoke','Resume') | Out-Null;State '一時停止';GoHome
    }
    Case 'Home during delayed HTTP inspection cancels stale work without late playback' {
        $server.HeaderDelayMilliseconds=2000;OpenRoot;$requests=$server.MediaRequests;$initialized=@(Traces | Where-Object Event -eq initialized).Count
        UI @('click','01 44.1k.wav') | Out-Null;Until {$server.MediaRequests -gt $requests}
        UI @('invoke','PlayerHome') | Out-Null;UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
        Start-Sleep -Seconds 3
        if(@(Traces | Where-Object Event -eq initialized).Count -ne $initialized){throw 'Canceled inspection initialized a stale stream.'}
        $server.HeaderDelayMilliseconds=0;Capture '07-canceled-http-home'
    }
}catch{
    if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})}
    Write-Host "FAIL $_";try{Capture 'failure';UI @('inspect','--json','-d','14') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-tree.json') -Encoding utf8}catch{}
}finally{
    if($appPid -gt 0 -and $beforePids -notcontains $appPid -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
        try{
            if($owned){GoHome;UI @('invoke','ChooseFolder') | Out-Null;Ready;$row=Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($server.RootName+'。')} | Select-Object -First 1
                if(!$row){throw 'Owned registration not found.'};UI @('invoke',$row.selector,'--action','select') | Out-Null;Keys 'delete';Wait 'PrimaryButton';UI @('invoke','PrimaryButton') | Out-Null;Gone $server.RootName;$owned=$false}
            (Get-Process -Id $appPid).CloseMainWindow() | Out-Null
        }catch{$results.Add(@{name='Owned registration / process cleanup';status='FAIL';detail="$_"})}
    }
    if($server){$server.Dispose()}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow
}
$failed=@($results | Where-Object status -eq FAIL).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray();observations=$observations.ToArray();trace=$trace;remainingRegistration=$owned;physicalSleepTested=$false} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Report: $artifacts/results.json"
if($failed){exit 1}
