# Packaged local UI/hardware checks; restores only this batch's audio-setting changes.
param([string]$DeviceId='{0.0.0.00000000}.{20f57f8f-d61c-4c73-892a-fe2bcdf376e9}',
    [ValidateSet('audio-stage4','audio-stage6-settings')][string]$ArtifactGroup='audio-stage4')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts/$ArtifactGroup/$id"
$media=Join-Path $repo ".tools/audio-stage4/audio-stage4-$id"
New-Item -ItemType Directory -Force -Path $artifacts,$media | Out-Null
$trace=Join-Path $artifacts 'audio.jsonl'
$settings=Join-Path $env:LOCALAPPDATA 'Packages/2587252A-FD49-4BA4-9C55-EC394DB02307_1z32rh13vfry6/LocalState/audio-settings.json'
$hadSettings=Test-Path -LiteralPath $settings
$originalSettings=if($hadSettings){[IO.File]::ReadAllBytes($settings)}else{$null}
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID="audio-stage4-$id"
$appPid=0;$hwnd=$null;$occupier=$null
$settingsRestored=$false
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
$performance=[Collections.Generic.List[object]]::new()
function Sample-App([string]$Label){
    $process=Get-Process -Id $appPid -ErrorAction Stop
    $performance.Add(@{label=$Label;timestamp=[DateTimeOffset]::UtcNow.ToString('o');processId=$appPid;
        lifetimeSeconds=([DateTime]::Now-$process.StartTime).TotalSeconds;cpuSeconds=$process.TotalProcessorTime.TotalSeconds;
        privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;handles=$process.HandleCount;threads=$process.Threads.Count})
}
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}elseif($hwnd -and $Arguments[0] -ne 'list-windows'){@('-w',$hwnd)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Selector){UI @('wait-for',$Selector,'-t','7000') | Out-Null}
function Keys([string]$Text){UI @('send-keys',$Text,'--via','send-input') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect([string]$Window){$tree=UI @('inspect','--json','-d','13') -Window $Window | ConvertFrom-Json;@(Nodes @($tree.windows | ForEach-Object elements))}
function Until([scriptblock]$Condition,[int]$Seconds=12){$deadline=[DateTime]::UtcNow.AddSeconds($Seconds);while([DateTime]::UtcNow -lt $deadline){if(& $Condition){return};Start-Sleep -Milliseconds 100};throw 'Timed out waiting for evidence.'}
function Traces{if(Test-Path -LiteralPath $trace){@(Get-Content -LiteralPath $trace | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})}else{@()}}
function CountTrace([string]$Event){@(Traces | Where-Object Event -eq $Event).Count}
function Output-Released{
    $events=@(Traces);$opened=$events | Where-Object Event -eq initialized | Select-Object -Last 1
    $released=$events | Where-Object Event -eq released | Select-Object -Last 1
    $null -ne $released -and (!$opened -or [DateTimeOffset]$released.Time -ge [DateTimeOffset]$opened.Time)
}
function Capture([string]$Name){$path=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$path,'--json') | Out-Null;if(!(Test-Path -LiteralPath $path)){throw 'Screenshot missing.'};$screenshots.Add($path)}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});throw}}
function Launch{
    $before=@(Get-Process ContinuousPlayer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--args',"--audio-log `"$trace`"",'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $launchedPid=[int]$launch.ProcessId;if(!$launchedPid -or $before -contains $launchedPid){throw 'No newly owned packaged process.'};$script:appPid=$launchedPid;$script:hwnd=$null
    UI @('wait-for','AudioOutputSettings','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    Sample-App 'launched'
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
    $script:hwnd=[string]$main.hwnd;if(!$hwnd){throw 'No main HWND.'}
    UI @('wait-for','AudioOutputSettings','-p','IsEnabled','--value','True','-t','10000') | Out-Null
}
function Close-App{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){(Get-Process -Id $appPid).CloseMainWindow() | Out-Null;Until {!(Get-Process -Id $appPid -ErrorAction SilentlyContinue)}};$script:hwnd=$null}
function Open-Settings{UI @('invoke','AudioOutputSettings') | Out-Null;Wait 'AudioMode';UI @('wait-for','PrimaryButton','-p','IsEnabled','--value','True','-t','7000') | Out-Null}
function Save-Settings{UI @('invoke','PrimaryButton') | Out-Null;UI @('wait-for','AudioMode','--gone','-t','7000') | Out-Null}
function Choose-Dac{
    UI @('invoke','AudioModeExclusive','--action','select') | Out-Null
    UI @('focus','AudioDevice') | Out-Null
    UI @('click','AudioDevice') | Out-Null
    $item=Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name -match 'FIIO BTR17'} | Select-Object -First 1
    if(!$item){throw 'BTR17 was not offered by the native device enumeration.'};UI @('click',$item.selector) | Out-Null
}
function Playing{UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','7000') | Out-Null;Sample-App 'playing'}
function Paused{UI @('wait-for','Resume','-p','Name','--value','再生','-t','7000') | Out-Null;Sample-App 'paused'}
function Info{Keys 'i';Wait 'MediaInfoText';(UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json).text}
function Close-Info{Keys 'i';UI @('wait-for','MediaInfoText','--gone','-t','7000') | Out-Null;UI @('wait-for','PlaybackInfo','-p','IsEnabled','--value','True','-t','7000') | Out-Null}
function Stereo([string]$Path,[int]$Channels,[int]$Seconds){
    $length=44100*$Channels*2*$Seconds
    $writer=[IO.BinaryWriter]::new([IO.File]::Create($Path))
    try{$writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([uint32](36+$length));$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([uint32]16);$writer.Write([uint16]1);$writer.Write([uint16]$Channels);$writer.Write([uint32]44100);$writer.Write([uint32](44100*$Channels*2));$writer.Write([uint16]($Channels*2));$writer.Write([uint16]16);$writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([uint32]$length);$writer.Write([byte[]]::new($length))}finally{$writer.Dispose()}
}
function Open-Fixture{
    UI @('invoke','ChooseFolder') | Out-Null;Wait (Split-Path -Leaf $media);UI @('scroll-into-view',(Split-Path -Leaf $media)) | Out-Null;UI @('click',(Split-Path -Leaf $media)) | Out-Null
    Wait '01 Stereo.wav';UI @('click','01 Stereo.wav') | Out-Null
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AudioStage4Native {
 [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr hwnd,uint message,UIntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
}
'@
try{
    Stereo (Join-Path $media '01 Stereo.wav') 2 120;Stereo (Join-Path $media '02 Mono.wav') 1 120
    Copy-Item -LiteralPath (Join-Path $repo '.tools/audio-stage4/samples/vorbis.ogg') -Destination (Join-Path $media '03 Unsupported.ogg')
    Launch
    Case 'Settings cancel preserves persisted data' {
        Open-Settings;Capture '01-settings-standard'
        UI @('invoke','AudioModeExclusive','--action','select') | Out-Null
        UI @('invoke','CloseButton') | Out-Null;UI @('wait-for','AudioMode','--gone','-t','5000') | Out-Null
        if((Test-Path -LiteralPath $settings) -ne $hadSettings){throw 'Cancel wrote settings.'}
        if($hadSettings -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($settings)) -ne [Convert]::ToBase64String($originalSettings)){throw 'Cancel changed settings bytes.'}
    }
    Case 'Mode and exact DAC ID save and survive ordinary restart' {
        Open-Settings;Choose-Dac;Capture '02-settings-exclusive';Save-Settings
        $saved=Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
        if(!$saved.Settings.Exclusive -or $saved.Settings.DeviceId -ne $DeviceId){throw 'Incorrect saved output identity.'}
        Close-App;Launch;Open-Settings
        $value=UI @('get-value','AudioDevice','--json') | ConvertFrom-Json
        if(($value | ConvertTo-Json -Depth 5) -notmatch 'FIIO BTR17'){throw 'Saved DAC was not selected after restart.'}
        Capture '03-settings-restarted';UI @('invoke','CloseButton') | Out-Null
    }
    Case 'Register owned fixture and play using saved exclusive settings' {
        UI @('invoke','ChooseFolder') | Out-Null;UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','8000') | Out-Null;UI @('scroll-into-view','BrowserItem:add') | Out-Null;UI @('click','BrowserItem:add') | Out-Null;Wait 'PC / ローカルフォルダー';UI @('click','PC / ローカルフォルダー') | Out-Null
        $script:picker=$null;Until {$script:picker=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770' -and $_.ownerHwnd -eq [long]$hwnd} | Select-Object -First 1;!!$script:picker}
        $pickerHwnd=[string]$picker.hwnd;UI @('set-value','1152',$media) -Window $pickerHwnd | Out-Null
        $confirm=Inspect $pickerHwnd | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
        if(!$confirm){throw 'No picker confirm.'};UI @('invoke',$confirm.selector) -Window $pickerHwnd | Out-Null
        Register-TestFixture $media
        UI @('click','01 Stereo.wav') | Out-Null;Until {(CountTrace playing) -gt 0};Playing
        $text=Info
        if($text -notmatch '実際の再生モード: WASAPI排他' -or $text -notmatch '直近の排他出力形式: 44,100 Hz' -or $text -notmatch [regex]::Escape($DeviceId)){throw "Incorrect source/output information: $text"}
        Capture '04-exclusive-information';Close-Info;Playing
    }
    Case 'Changing mode preserves position and stays paused; switching back also stays paused' {
        UI @('focus','TouchSurface') | Out-Null;Keys 'down';Start-Sleep -Milliseconds 300
        Open-Settings;UI @('invoke','AudioModeStandard','--action','select') | Out-Null;Save-Settings
        UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','7000') | Out-Null;Paused
        $text=Info;if($text -notmatch '実際の再生モード: 標準' -or $text -notmatch '再生位置: 00:00:1'){throw "Mode change lost position/pause: $text"};Close-Info;Paused
        Open-Settings;Choose-Dac;Save-Settings
        UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','7000') | Out-Null;Paused
        $text=Info;if($text -notmatch '実際の再生モード: WASAPI排他' -or $text -notmatch '再生位置: 00:00:1'){throw 'Exclusive mode change lost position.'};Close-Info;Paused
        UI @('invoke','Resume') | Out-Null;Playing
    }
    Case 'Suspend/resume notification during settings prevents automatic resume on cancel' {
        Open-Settings
        [AudioStage4Native]::SendMessageW([IntPtr][long]$hwnd,0x218,[UIntPtr]4,[IntPtr]::Zero) | Out-Null
        [AudioStage4Native]::SendMessageW([IntPtr][long]$hwnd,0x218,[UIntPtr]7,[IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 200
        UI @('invoke','CloseButton') | Out-Null;Paused
        $playing=CountTrace playing;Start-Sleep -Milliseconds 500;if((CountTrace playing) -ne $playing){throw 'Settings dismissal resumed interrupted playback.'}
        UI @('invoke','Resume') | Out-Null;Playing
    }
    Case 'Actual minimization releases exclusive output and restore waits for manual resume' {
        [AudioStage4Native]::ShowWindow([IntPtr][long]$hwnd,6) | Out-Null
        Until {Output-Released}
        [AudioStage4Native]::ShowWindow([IntPtr][long]$hwnd,9) | Out-Null;Paused
        UI @('invoke','Resume') | Out-Null;Playing
        UI @('focus','TouchSurface') | Out-Null;Keys 'space';Paused
        Until {Output-Released}
    }
    Case 'Real competing exclusive stream produces DEVICE_IN_USE guidance without fallback' {
        $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($arg in @((Join-Path $repo 'tools/ContinuousPlayer.AudioProbe/bin/Debug/net10.0-windows10.0.26100.0/ContinuousPlayer.AudioProbe.dll'),'engine','--input',(Join-Path $media '01 Stereo.wav'),'--device',$DeviceId,'--output',(Join-Path $artifacts 'occupier.json'))){$start.ArgumentList.Add($arg)}
        $script:occupier=[Diagnostics.Process]::Start($start)
        $ready=$false;$lines=[Collections.Generic.List[string]]::new()
        for($n=0;$n -lt 8;$n++){$line=$occupier.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult();$lines.Add($line);if($line -match ' playing:'){$ready=$true;break};if($null -eq $line){break}}
        $lines | Set-Content -LiteralPath (Join-Path $artifacts 'occupier.txt');if(!$ready){throw 'Competing renderer did not start.'}
        UI @('invoke','Resume') | Out-Null;Wait 'PlaybackFailureText'
        $text=(UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json).text
        if($text -notmatch '0x8889000A' -or $text -notmatch '他のアプリ'){throw "Wrong contention guidance: $text"}
        Wait 'ErrorPlayStandard';Capture '05-device-in-use'
        $occupier.Kill();$occupier.WaitForExit();$script:occupier=$null
        $played=CountTrace playing
        UI @('invoke','CloseButton') | Out-Null
        UI @('wait-for','PlaybackFailureText','--gone','-t','7000') | Out-Null
        UI @('wait-for','Resume','-p','IsEnabled','--value','True','-t','7000') | Out-Null
        UI @('invoke','Resume') | Out-Null;Playing;Until {(CountTrace playing) -gt $played}
    }
    Case 'Unsupported mono offers explicit standard playback with actual mode shown' {
        UI @('focus','TouchSurface') | Out-Null;Keys 'right';Wait 'PlaybackFailureText'
        $text=(UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json).text
        if($text -notmatch '同じレートとチャンネル数' -or $text -notmatch '1 ch'){throw 'Unsupported format guidance is missing.'}
        Capture '06-unsupported-mono';UI @('invoke','ErrorPlayStandard') | Out-Null;Playing
        $text=Info;if($text -notmatch '実際の再生モード: 標準' -or $text -notmatch 'この曲を標準再生する操作'){throw 'Explicit standard mode is not disclosed.'};Capture '07-explicit-standard';Close-Info
    }
    Case 'Missing decoder is disclosed without silently selecting standard output' {
        $initialized=CountTrace initialized;UI @('focus','TouchSurface') | Out-Null;Keys 'right';Wait 'PlaybackFailureText'
        $text=(UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json).text
        if($text -notmatch '0xC00D36C4' -or $text -notmatch 'デコーダー' -or (CountTrace initialized) -ne $initialized){throw 'Unavailable decoder did not fail explicitly.'}
        Wait 'ErrorPlayStandard';Capture '08-decoder-unavailable';UI @('invoke','CloseButton') | Out-Null
        UI @('wait-for','PlaybackFailureText','--gone','-t','7000') | Out-Null;UI @('wait-for','Resume','-p','IsEnabled','--value','True','-t','7000') | Out-Null;Keys 'h';Wait 'ChooseFolder'
    }
    Case 'Missing saved DAC remains selected and fails without output to another device' {
        Close-App
        @{Version=1;Settings=@{Exclusive=$true;DeviceId='{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}';DeviceName='未接続の検証DAC'}} | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
        Launch;Open-Settings
        $value=UI @('get-value','AudioDevice','--json') | ConvertFrom-Json
        if(($value | ConvertTo-Json -Depth 5) -notmatch '未接続'){throw 'Missing saved selection was replaced with default.'}
        Capture '08-missing-dac-selection';UI @('invoke','CloseButton') | Out-Null
        $initialized=CountTrace initialized;Open-Fixture;Wait 'PlaybackFailureText'
        $text=(UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json).text
        if($text -notmatch '別の機器へ自動出力しません' -or (CountTrace initialized) -ne $initialized){throw 'Missing DAC silently selected another output.'}
        Capture '09-missing-dac-error';UI @('invoke','ErrorPlayStandard') | Out-Null;Playing
        $text=Info;if($text -notmatch '実際の再生モード: 標準'){throw 'Explicit standard selection did not change the actual mode.'};Close-Info
    }
    Case 'Selecting an available DAC after failure prepares paused and requires manual resume' {
        Open-Settings;Choose-Dac;Save-Settings
        UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','8000') | Out-Null;Paused
        $playing=CountTrace playing;Start-Sleep -Milliseconds 300;if((CountTrace playing) -ne $playing){throw 'Changing output resumed without an explicit play action.'}
        UI @('invoke','Resume') | Out-Null;Playing;Capture '10-recovered-output';Keys 'h';Wait 'ChooseFolder'
    }
}catch{
    if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})}
    Write-Host "FAIL $_";try{Capture 'failure';UI @('inspect','--json','-d','14') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-tree.json') -Encoding utf8}catch{}
}finally{
    try{if($occupier -and !$occupier.HasExited){$occupier.Kill();$occupier.WaitForExit()}}catch{}
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){Remove-TestFixtureRegistration;Close-App}}catch{$results.Add(@{name='Owned fixture cleanup';status='FAIL';detail="$_"});try{Close-App}catch{}}
    try{
        if($hadSettings){[IO.File]::WriteAllBytes($settings,$originalSettings)}elseif(Test-Path -LiteralPath $settings){Remove-Item -LiteralPath $settings}
        $settingsRestored=(Test-Path -LiteralPath $settings) -eq $hadSettings
        if($hadSettings){$settingsRestored=$settingsRestored -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($settings)) -eq [Convert]::ToBase64String($originalSettings)}
        if(!$settingsRestored){throw 'Prior settings were not restored exactly.'}
    }catch{$results.Add(@{name='Restore prior audio settings';status='FAIL';detail="$_"})}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow
}
$failed=@($results | Where-Object status -eq FAIL).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray();trace=$trace;fixture=$media;priorSettingsRestored=$settingsRestored;performance=$performance.ToArray()} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Report: $artifacts/results.json"
if($failed){exit 1}
