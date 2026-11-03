# Local packaged UI acceptance using loopback HTTP without SSDP and an owned registration.
param([switch]$ContinueAfterFocusFailure)
$ErrorActionPreference='Stop';$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage5\$id";$fixture=Join-Path $repo ".tools\stage5-media\$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID;$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="continuousplayer-stage5-$id";$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=$null;$serverA=$null;$serverB=$null;$owned=$false
$results=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[string]]::new();$observations=[Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments 2>&1 | ForEach-Object {"$_"});if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments){$scope=if($hwnd -and $Arguments[0] -ne 'list-windows'){@('-w',$hwnd)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$scope)}
function Wait([string]$Name,[int]$Timeout=10000){UI @('wait-for',$Name,'-t',"$Timeout") | Out-Null}
function Gone([string]$Name){UI @('wait-for',$Name,'--gone','-t','5000') | Out-Null}
function Keys([string]$Value){UI @('send-keys',$Value,'--via','send-input') | Out-Null}
function Ready{UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','15000') | Out-Null}
function Title([string]$Text){UI @('wait-for','BrowserTitle','--value',$Text,'-t','15000') | Out-Null;Ready}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect{
    for($attempt=0;$attempt -lt 3;$attempt++){
        try{$t=UI @('inspect','--json','-d','14') | ConvertFrom-Json;return @(Nodes @($t.windows | ForEach-Object {$_.elements}))}
        catch{if($attempt -eq 2 -or "$_" -notmatch 'stale_element'){throw};Start-Sleep -Milliseconds 200}
    }
}
function Row([string]$Name){$r=@(Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($Name+'。')});if($r.Count -ne 1){throw "Expected one row for $Name, got $($r.Count)"};$r[0]}
function OpenRow([string]$Name){$r=Row $Name;UI @('scroll-into-view',$r.selector) | Out-Null;UI @('click',$r.selector) | Out-Null;Ready}
function Capture([string]$Name){Start-Sleep -Seconds 1;$path=Join-Path $artifacts "$Name.png";UI @('screenshot','--capture-screen','--output',$path,'--json') | Out-Null;if(!(Test-Path -LiteralPath $path) -or !(Get-Item -LiteralPath $path).Length){throw 'Screenshot missing.'};$screenshots.Add($path)}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});try{Capture 'failure'}catch{};throw}}
function Launch{
    $script:hwnd=$null;$r=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $script:appPid=[int]$r.ProcessId;if(!$appPid){throw 'No local process.'}
    UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','15000') | Out-Null
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.ownerHwnd -eq 0 -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
    $script:hwnd=[string]$main.hwnd;if(!$hwnd){throw 'No main window.'};UI @('focus','ChooseFolder') | Out-Null
}
function CloseApp{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){$p=Get-Process -Id $appPid;$p.CloseMainWindow() | Out-Null;if(!$p.WaitForExit(5000)){throw 'Test app did not close.'}};$script:hwnd=$null}
function DialogIdle{UI @('wait-for','DlnaAddress','-p','IsEnabled','--value','True','-t','12000') | Out-Null}
function Address([string]$Value){DialogIdle;UI @('set-value','DlnaAddress',$Value) | Out-Null}
function Connect{UI @('invoke','PrimaryButton') | Out-Null}
function CloseInfo{Keys 'i';Gone 'MediaInfoText';UI @('wait-for','PlaybackFolder','-p','IsEnabled','--value','True','-t','5000') | Out-Null}
function Info([string]$Name){Keys 'i';Wait 'MediaInfoText';$v=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json;if($v.text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})'){throw 'No position.'};$o=@{name=$Name;file=([string]$v.text -split "`n")[0];seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds};$observations.Add($o);$o}
function FailurePosition([string]$Name){
    $watch=[Diagnostics.Stopwatch]::StartNew();$found=$false;$nextLog=20
    while($watch.Elapsed.TotalSeconds -lt 180){
        if(@(Inspect).automationId -contains 'PlaybackFailureText'){$found=$true;break}
        if($watch.Elapsed.TotalSeconds -ge $nextLog){Write-Host "Waiting for playback failure: $Name ($([int]$watch.Elapsed.TotalSeconds)s)";$nextLog+=20}
        Start-Sleep -Seconds 1
    }
    if(!$found){throw 'No playback failure was shown within 180 seconds after the stream was reset.'}
    $v=UI @('get-value','PlaybackFailureText','--json') | ConvertFrom-Json;if($v.text -notmatch '最後に確認した位置: (\d{2}:\d{2}:\d{2})'){throw 'No confirmed failure position.'}
    $o=@{name=$Name;file=([string]$v.text -split "`n")[0];seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds;failureDelaySeconds=[math]::Round($watch.Elapsed.TotalSeconds,1)};$observations.Add($o);$o
}
function PlaybackReady{UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','30000') | Out-Null}
function State([string]$Name){UI @('wait-for','Resume','-p','Name','--value',$Name,'-t','8000') | Out-Null}
function GoLibrary{
    $items=Inspect
    if($items.automationId -contains 'DlnaAddress' -or $items.automationId -contains 'PlaybackFailureText'){UI @('invoke','CloseButton') | Out-Null;Gone 'CloseButton'}
    if($items.automationId -contains 'MediaInfoText'){Keys 'i';Gone 'MediaInfoText'}
    foreach($n in 1..2){$items=Inspect;if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}}
    $items=Inspect
    if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null;Gone 'BrowserList';$items=Inspect}
    if($items | Where-Object {$_.automationId -eq 'PlayerHome' -and $_.isEnabled}){UI @('invoke','PlayerHome') | Out-Null}
    Wait 'ChooseFolder';UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー'
}
try{
Add-Type -Path (Join-Path $PSScriptRoot 'Stage5-TestServer.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Stage5Native {
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")]static extern IntPtr SendMessageW(IntPtr h,uint m,UIntPtr w,IntPtr l);
 public static void Power(IntPtr h,int pid,bool active){GetWindowThreadProcessId(h,out uint p);if(p!=pid)throw new Exception("Unexpected target");SendMessageW(h,0x218,new UIntPtr(active?4u:18u),IntPtr.Zero);}
}
'@
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $fixture -DurationSeconds 120 | Out-Null
    $serverA=[Stage5TestServer]::new($id,$fixture);$serverB=[Stage5TestServer]::new($id,$fixture)
    Launch
    Case 'S33: address entry is reachable during discovery and typing survives old discovery completion'{
        UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー';UI @('click','フォルダーを追加') | Out-Null;Title 'フォルダーを追加'
        UI @('click','NAS / DLNA') | Out-Null;Wait 'ManualDlnaConnect';UI @('invoke','ManualDlnaConnect') | Out-Null;Wait 'DlnaAddress'
        Address '';UI @('focus','DlnaAddress') | Out-Null;Keys 'i';Keys 'o';Keys 'h';Start-Sleep -Seconds 5
        UI @('wait-for','DlnaAddress','--value','ioh','-t','3000') | Out-Null;Capture '01-entry-during-discovery'
        Address 'file:///invalid';Connect;Wait 'DlnaConnectError';Wait 'DlnaAddress'
        UI @('invoke','CloseButton') | Out-Null;Gone 'DlnaAddress';Title 'NAS / DLNA'
    }
    Case 'S33: cancel during a slow direct connection cannot navigate after its late response'{
        UI @('invoke','ManualDlnaConnect') | Out-Null;Wait 'DlnaAddress';Address $serverA.SlowDescriptionUri.AbsoluteUri;Connect;Wait 'DlnaConnecting'
        UI @('invoke','CloseButton') | Out-Null;Gone 'DlnaAddress';Title 'NAS / DLNA';Start-Sleep -Seconds 3
        Gone 'RegisterFolder';Title 'NAS / DLNA'
    }
    Case 'S33 S34: direct endpoint errors preserve input; host and explicit port connect without SSDP'{
        UI @('invoke','ManualDlnaConnect') | Out-Null;Wait 'DlnaAddress';$denied=[Uri]::new($serverA.DescriptionUri,'/denied').AbsoluteUri
        Address $denied;Connect;Wait 'DlnaConnectError';UI @('wait-for','DlnaAddress','--value',$denied,'-t','3000') | Out-Null;Capture '02-connect-error'
        Address $serverA.DescriptionUri.Authority;Connect;Gone 'DlnaAddress';Title $serverA.Name
        OpenRow $serverA.RootName;Title $serverA.RootName;$script:owned=$true
        UI @('invoke','RegisterFolder') | Out-Null;Title '保存済みフォルダー';$null=Row $serverA.RootName;Gone 'VideoPlayer'
    }
    Case 'S34: unavailable saved endpoint remains registered and can be reassigned only to the same server'{
        $serverA.DeviceAvailable=$false;OpenRow $serverA.RootName;Wait 'BrowserError';Wait 'DlnaReconnect';$null=Row $serverA.RootName
        UI @('invoke','DlnaReconnect') | Out-Null;Wait 'DlnaAddress'
        Address ([Uri]::new($serverB.DescriptionUri,'/not-device').AbsoluteUri);Connect;Wait 'DlnaConnectError'
        $serverB.DifferentIdentity=$true;Address $serverB.DescriptionUri.AbsoluteUri;Connect;Wait 'DlnaConnectError'
        DialogIdle;$serverB.DifferentIdentity=$false;Connect;Gone 'DlnaAddress';Title $serverB.RootName;Gone 'RegisterFolder';Capture '03-reassigned-root'
        GoLibrary
    }
    Case 'S34: app restart reconnects a saved folder through its new description URL without discovery'{
        CloseApp;Launch;UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー';$before=$serverB.DescriptionRequests
        OpenRow $serverB.RootName;Title $serverB.RootName
        if($serverB.DescriptionRequests -le $before){throw 'Saved description was not fetched.'};$observations.Add(@{name='restart-direct';descriptionRequests=$serverB.DescriptionRequests})
    }
    Case 'S35: network failure holds the last confirmed position and closing the error stays failed'{
        UI @('click','Remote A.wav') | Out-Null;PlaybackReady;State '一時停止'
        foreach($n in 1..3){UI @('invoke','SeekForward') | Out-Null;Start-Sleep -Seconds 1}
        $before=Info 'before-network-cut';if($before.seconds -lt 29){throw 'Could not seek the stream.'};CloseInfo
        $serverB.CutNetwork();$script:firstFailure=FailurePosition 'first-network-failure'
        if($firstFailure.file -ne 'Remote A.wav' -or $firstFailure.seconds -lt 29){throw 'Confirmed position was lost.'};Wait 'ErrorNextFile'
        try{UI @('wait-for','PrimaryButton','-p','HasKeyboardFocus','--value','True','-t','5000') | Out-Null}
        catch{
            if(!$ContinueAfterFocusFailure){throw}
            $results.Add(@{name='S35: retry dialog initial focus';status='FAIL';detail="$_"})
            Write-Host "FAIL Retry dialog initial focus (continuing independent retry checks): $_"
        }
        Capture '04-playback-failure'
        UI @('invoke','CloseButton') | Out-Null;Gone 'PlaybackFailureText';State '再試行';Start-Sleep -Seconds 1
        $v=Info 'failed-position';if($v.file -ne $firstFailure.file -or [math]::Abs($v.seconds-$firstFailure.seconds) -gt 1){throw 'Close changed the failed media.'};CloseInfo
    }
    Case 'S35: retry refreshes the media URL and seeks before resuming the same file'{
        $serverB.Recover();$before=$serverB.BrowseRequests;UI @('invoke','Resume') | Out-Null;PlaybackReady;State '一時停止'
        $v=Info 'restored-after-retry';if($v.file -ne 'Remote A.wav' -or [math]::Abs($v.seconds-$firstFailure.seconds) -gt 4){throw 'Retry did not restore the same file position.'}
        if($serverB.BrowseRequests -le $before){throw 'Retry reused a stale media URL.'};Capture '05-restored';CloseInfo
    }
    Case 'S35: interruption during retry preparation retains position and requires manual resume'{
        $serverB.CutNetwork();$saved=FailurePosition 'before-interrupted-retry';$serverB.Recover();$serverB.MediaOpenDelayMilliseconds=2000
        $opens=$serverB.MediaRequests;Connect;Gone 'PlaybackFailureText';$watch=[Diagnostics.Stopwatch]::StartNew()
        while($serverB.MediaRequests -le $opens -and $watch.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 100}
        if($serverB.MediaRequests -le $opens){throw 'Retry preparation did not begin.'}
        [Stage5Native]::Power([IntPtr]::new([long]$hwnd),$appPid,$true);[Stage5Native]::Power([IntPtr]::new([long]$hwnd),$appPid,$false)
        PlaybackReady;State '再生';$v=Info 'after-interrupted-retry';if([math]::Abs($v.seconds-$saved.seconds) -gt 2){throw 'Interrupted retry lost position.'};Capture '06-interrupted-retry';CloseInfo
        $serverB.MediaOpenDelayMilliseconds=0;UI @('invoke','Resume') | Out-Null;State '一時停止'
    }
    Case 'S36: explicit Next starts the new file at zero; failure never falls back and final Next is absent'{
        $serverB.CutNetwork();$null=FailurePosition 'before-explicit-next';$serverB.Recover();$serverB.FailSecond=$true
        UI @('invoke','ErrorNextFile') | Out-Null;$v=FailurePosition 'new-b-failed';if($v.file -ne 'Remote B.wav' -or $v.seconds -ne 0){throw 'New-file failure inherited another file position.'};Gone 'ErrorNextFile';Capture '07-new-file-error'
        UI @('invoke','SecondaryButton') | Out-Null;Gone 'PlaybackFailureText';Title $serverB.RootName
        $r=Row 'Remote B.wav';UI @('wait-for',$r.selector,'-p','HasKeyboardFocus','--value','True','-t','5000') | Out-Null
        UI @('invoke','BrowserCancel') | Out-Null;State '再試行'
    }
}catch{if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})};Write-Host "FAIL $_"}
finally{
    if($owned -and $appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
        try{GoLibrary;$r=Row $serverA.RootName;UI @('invoke',$r.selector,'--action','select') | Out-Null;Keys 'delete';Wait 'PrimaryButton';Connect;Ready;Gone $serverA.RootName;$owned=$false}
        catch{$results.Add(@{name='Owned registration cleanup';status='FAIL';detail="$_"})}
    }
    try{CloseApp}catch{$results.Add(@{name='Close test app';status='FAIL';detail="$_"})}
    if($serverA){$serverA.Dispose()};if($serverB){$serverB.Dispose()}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray());observations=@($observations.ToArray());fixture=$fixture;remainingTestRegistration=$owned;ssdpFixture=$false;physicalLockSleepTested=$false;physicalNasTested=$false} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
