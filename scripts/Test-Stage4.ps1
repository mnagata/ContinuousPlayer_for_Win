# Local packaged UI batch. Only registrations bearing this run's unique names are changed.
param([string]$VideoPath=(Join-Path $PSScriptRoot '..\.tools\cursor-media\Cursor test.mp4'))
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage4\$id"
$fixture=Join-Path $repo ".tools\stage4-media\$id"
$folderA=Join-Path $fixture "Stage4-$id-A"
$folderB=Join-Path $fixture "Stage4-$id-B"
$folderC=Join-Path $fixture "Stage4-$id-C"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID;$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="continuousplayer-stage4-$id";$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=$null;$server=$null
$results=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[string]]::new()
$owned=[Collections.Generic.List[string]]::new();$observations=[Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}elseif($hwnd -and $Arguments[0] -ne 'list-windows'){@('-w',$hwnd)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Name){UI @('wait-for',$Name,'-t','8000') | Out-Null}
function Gone([string]$Name){UI @('wait-for',$Name,'--gone','-t','5000') | Out-Null}
function Keys([string]$Text){UI @('send-keys',$Text,'--via','send-input') | Out-Null}
function Ready{UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','12000') | Out-Null}
function Title([string]$Value){UI @('wait-for','BrowserTitle','--value',$Value,'-t','12000') | Out-Null;Ready}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect([string]$Window){$t=UI @('inspect','--json','-d','12') -Window $Window | ConvertFrom-Json;@(Nodes @($t.windows | ForEach-Object {$_.elements}))}
function Row([string]$Name){$matches=@(Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($Name+'。')});if($matches.Count -ne 1){throw "Expected one row for $Name, got $($matches.Count)"};$matches[0]}
function OpenRow([string]$Name){$r=Row $Name;UI @('scroll-into-view',$r.selector) | Out-Null;UI @('click',$r.selector) | Out-Null;Ready}
function Focused([string]$Name){$r=Row $Name;UI @('wait-for',$r.selector,'-p','HasKeyboardFocus','--value','True','-t','5000') | Out-Null}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});try{Capture 'failure'}catch{};throw}}
function Capture([string]$Name){$p=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$p,'--json') -Window $hwnd | Out-Null;if(!(Test-Path -LiteralPath $p) -or !(Get-Item -LiteralPath $p).Length){throw 'Screenshot missing.'};$screenshots.Add($p)}
function Picker {
    for($n=0;$n -lt 40;$n++){$p=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770' -and $_.ownerHwnd -eq [long]$hwnd} | Select-Object -First 1;if($p){return [string]$p.hwnd};Start-Sleep -Milliseconds 100};throw 'No owned folder picker.'
}
function PickerButton([string]$Window,[string]$Id){$b=Inspect $Window | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq $Id} | Select-Object -First 1;if(!$b){throw "No picker button $Id"};UI @('invoke',$b.selector) -Window $Window | Out-Null}
function Pick([string]$Path){UI @('click','フォルダーを追加') | Out-Null;Title 'フォルダーを追加';UI @('click','PC / ローカルフォルダー') | Out-Null;$p=Picker;UI @('set-value','1152',$Path) -Window $p | Out-Null;PickerButton $p '1';Wait 'RegisterFolder';Ready}
function Register([string]$Path){Pick $Path;UI @('invoke','RegisterFolder') | Out-Null;Title '保存済みフォルダー';$name=Split-Path -Leaf $Path;if(!$owned.Contains($name)){$owned.Add($name)};Focused $name;Gone 'VideoPlayer'}
function GoLibrary{
    $items=Inspect
    if($items.automationId -contains 'MediaInfoText'){Keys 'i';Gone 'MediaInfoText'}
    if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null;$items=Inspect;if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}}
    $items=Inspect;if($items.automationId -contains 'PlayerHome'){UI @('invoke','PlayerHome') | Out-Null}
    Wait 'ChooseFolder';UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー'
}
function RemoveOwn([string]$Name,[string]$Method='delete'){
    if(!$owned.Contains($Name)){throw 'Refusing to remove an unowned registration.'}
    $r=Row $Name;UI @('scroll-into-view',$r.selector) | Out-Null
    if($Method -eq 'right'){UI @('click',$r.selector,'--right') | Out-Null}
    else{UI @('invoke',$r.selector,'--action','select') | Out-Null;UI @('focus',$r.selector) | Out-Null;Keys 'delete'}
    Wait 'PrimaryButton';UI @('invoke','PrimaryButton') | Out-Null;Ready;Gone $Name;$owned.Remove($Name) | Out-Null
}
Add-Type -Path (Join-Path $PSScriptRoot 'Stage4-TestServer.cs')
try{
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $folderA -DurationSeconds 120 | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $folderA 'Child/Deep'),$folderB,$folderC | Out-Null
    Copy-Item -LiteralPath (Join-Path $folderA 'Sample OP.wav') -Destination (Join-Path $folderA 'Child/Deep/Deep.wav')
    Copy-Item -LiteralPath (Join-Path $folderA 'Sample OP.wav') -Destination (Join-Path $folderA 'Episode.ver.1.wav')
    Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $folderA 'Episode.ver.1.mp4')
    Copy-Item -LiteralPath (Join-Path $folderA 'Sample OP.wav') -Destination (Join-Path $folderA ('長いタイトルの全文をツールチップと読み上げで確認するための作品名を繰り返した長い名前.ver.2.wav'))
    Copy-Item -LiteralPath (Join-Path $folderA 'Sample OP.wav') -Destination (Join-Path $folderB 'B.wav')
    $script:server=[Stage4TestServer]::new($id)
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId;if(!$appPid){throw 'No test process.'}
    Wait 'ChooseFolder';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','15000') | Out-Null;UI @('focus','ChooseFolder') | Out-Null
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.ownerHwnd -eq 0} | Select-Object -First 1;$hwnd=[string]$main.hwnd
    Case 'S28: source cancel, native picker cancel and top Back have distinct destinations'{
        UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー'
        $script:baselineEmpty=@(Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name -notlike 'フォルダーを追加*'}).Count -eq 0
        UI @('click','フォルダーを追加') | Out-Null;Title 'フォルダーを追加';Capture '01-sources'
        UI @('click','PC / ローカルフォルダー') | Out-Null;$p=Picker;PickerButton $p '2';Title 'フォルダーを追加'
        Keys 'esc';Title '保存済みフォルダー';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','False','-t','3000') | Out-Null
        UI @('invoke','BrowserBack') | Out-Null;Wait 'ChooseFolder';Gone 'BrowserList'
        UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー'
    }
    Case 'S29: register, re-register and multiple registrations select saved rows without playback'{
        Register $folderA;Register $folderA;Register $folderB;Register $folderC
        $r=Row (Split-Path -Leaf $folderA);if(!$r){throw 'Registration missing.'};Gone 'RemoveFolder';Capture '02-saved'
    }
    Case 'S30: matching titles gain format badges; raw names and periods remain available'{
        OpenRow (Split-Path -Leaf $folderA);Title (Split-Path -Leaf $folderA)
        $wav=Row 'Episode.ver.1.wav';$mp4=Row 'Episode.ver.1.mp4'
        if($wav.name -notmatch '形式 WAV' -or $mp4.name -notmatch '形式 MP4'){throw 'Collision formats missing.'}
        $text=@(Inspect | Where-Object {$_.type -eq 'Text'});if(@($text | Where-Object {$_.name -eq 'WAV'}).Count -lt 1 -or @($text | Where-Object {$_.name -eq 'MP4'}).Count -lt 1){throw 'Format badge not exposed.'}
        Capture '03-collisions'
    }
    Case 'S32: media right click and long press neither play nor open information'{
        $r=Row 'Sample OP.wav';UI @('click',$r.selector,'--right') | Out-Null;Gone 'VideoPlayer';Gone 'MediaInfoText'
        UI @('touch',$r.selector,'-g','long-press','--hold-ms','900') | Out-Null;Gone 'VideoPlayer';Gone 'MediaInfoText';Wait 'BrowserList'
    }
    Case 'S27 S01: deep playback source returns only as far as its registered root and restores paused media'{
        OpenRow 'Child';Title 'Child';OpenRow 'Deep';Title 'Deep';UI @('click','Deep.wav') | Out-Null
        UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','8000') | Out-Null
        UI @('focus','TouchSurface') | Out-Null;Keys 'space';UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null
        Keys 'o';Title 'Deep';UI @('invoke','BrowserBack') | Out-Null;Title 'Child';Focused 'Deep'
        UI @('invoke','BrowserBack') | Out-Null;Title (Split-Path -Leaf $folderA);Focused 'Child'
        UI @('invoke','BrowserBack') | Out-Null;Title '保存済みフォルダー';Focused (Split-Path -Leaf $folderA);Capture '04-root-return'
        Keys 'esc';Gone 'BrowserList';UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null
        Keys 'i';Wait 'MediaInfoText';$v=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json;if($v.text -notmatch '^Deep.wav'){throw 'Source changed.'};$observations.Add(@{name='restored-deep';text=$v.text});Keys 'i';Gone 'MediaInfoText';GoLibrary
    }
    Case 'S29: middle removal selects next; offline last removal selects previous and preserves files'{
        RemoveOwn (Split-Path -Leaf $folderB);Focused (Split-Path -Leaf $folderC)
        $moveTarget=[IO.Path]::GetFullPath($folderC+'-offline');$moveSource=[IO.Path]::GetFullPath($folderC)
        if(!$moveSource.StartsWith($fixture+[IO.Path]::DirectorySeparatorChar) -or !$moveTarget.StartsWith($fixture+[IO.Path]::DirectorySeparatorChar)){throw 'Move is outside owned fixture.'}
        Move-Item -LiteralPath $moveSource -Destination $moveTarget
        RemoveOwn (Split-Path -Leaf $folderC) 'right';Focused (Split-Path -Leaf $folderA);Capture '05-neighbor'
        if(!(Test-Path -LiteralPath (Join-Path $folderB 'B.wav'))){throw 'Media was removed.'}
    }
    Case 'S27 S30: DLNA deep registered root has the same boundaries and collision badges'{
        UI @('click','フォルダーを追加') | Out-Null;Title 'フォルダーを追加';UI @('click','NAS / DLNA') | Out-Null;Title 'NAS / DLNA'
        OpenRow $server.Name;Title $server.Name;OpenRow $server.RootName;Title $server.RootName
        UI @('invoke','RegisterFolder') | Out-Null;Title '保存済みフォルダー';$owned.Add($server.RootName);Focused $server.RootName
        OpenRow $server.RootName;Title $server.RootName
        if((Row 'NAS.part.1.wav').name -notmatch '形式 WAV' -or (Row 'NAS.part.1.flac').name -notmatch '形式 FLAC'){throw 'DLNA badges missing.'}
        Capture '06-dlna'
        OpenRow 'NAS Child';Title 'NAS Child';OpenRow 'NAS Deep';Title 'NAS Deep'
        UI @('invoke','BrowserBack') | Out-Null;Title 'NAS Child';Focused 'NAS Deep'
        UI @('invoke','BrowserBack') | Out-Null;Title $server.RootName;Focused 'NAS Child'
        UI @('invoke','BrowserBack') | Out-Null;Title '保存済みフォルダー';Focused $server.RootName
    }
    Case 'S31: delayed load retains title and rows; Back cancels and late response never reopens'{
        OpenRow $server.RootName;Title $server.RootName;$before=@(Inspect | Where-Object {$_.type -eq 'ListItem'} | ForEach-Object {$_.name})
        $server.DelayMilliseconds=6000;$r=Row 'NAS Child';UI @('click',$r.selector) | Out-Null;Wait 'BrowserLoading'
        UI @('wait-for','BrowserTitle','--value',$server.RootName,'-t','1000') | Out-Null
        UI @('wait-for','BrowserList','-p','IsEnabled','--value','False','-t','1000') | Out-Null
        UI @('wait-for','BrowserBack','-p','IsEnabled','--value','True','-t','1000') | Out-Null
        $after=@(Inspect | Where-Object {$_.type -eq 'ListItem'} | ForEach-Object {$_.name});if(($before -join '|') -ne ($after -join '|')){throw 'Rows changed before completion.'};Capture '07-delayed'
        UI @('invoke','BrowserBack') | Out-Null;Wait 'ChooseFolder';Gone 'BrowserList';Start-Sleep -Seconds 7;Gone 'BrowserList';Wait 'ChooseFolder'
        $server.DelayMilliseconds=0;UI @('invoke','ChooseFolder') | Out-Null;Title '保存済みフォルダー'
    }
    Case 'S29: final owned registrations remove offline without deleting media'{
        $server.Dispose();$script:server=$null
        RemoveOwn ('Stage4-'+$id+'-NAS-root') 'right';RemoveOwn (Split-Path -Leaf $folderA)
        if($baselineEmpty){UI @('wait-for','フォルダーを追加','-p','HasKeyboardFocus','--value','True','-t','5000') | Out-Null}
        if(!(Test-Path -LiteralPath (Join-Path $folderA 'Sample OP.wav'))){throw 'Registered media was deleted.'};Capture '08-cleaned'
    }
}catch{if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})};Write-Host "FAIL $_"}
finally{
    if($server){$server.Dispose()}
    if($owned.Count -and $appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
        try{GoLibrary;foreach($name in @($owned.ToArray())){RemoveOwn $name}}catch{$results.Add(@{name='Owned registration cleanup';status='FAIL';detail="$_"})}
    }
    if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){(Get-Process -Id $appPid).CloseMainWindow() | Out-Null}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray());observations=@($observations.ToArray());remainingTestRegistrations=@($owned.ToArray());fixture=$fixture;physicalNasTested=$false} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
