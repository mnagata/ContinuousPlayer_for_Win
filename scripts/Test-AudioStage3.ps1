# Local hardware acceptance for the phase 3 production engine and packaged UI.
param([Parameter(Mandatory)][int]$AppPid,
    [string]$MediaDirectory='.tools/audio-stage3/ui-media',
    [string]$TracePath='.tools/audio-stage3/app.jsonl')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts/audio-stage3/$id"
$sourceMedia=[IO.Path]::GetFullPath((Join-Path $repo $MediaDirectory))
$media=Join-Path $repo ".tools/audio-stage3/ui/$id"
$trace=[IO.Path]::GetFullPath((Join-Path $repo $TracePath))
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID="audio-stage3-$id"
$hwnd=$null
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$selector=if($Window){@('-w',$Window)}elseif($hwnd -and $Arguments[0] -ne 'list-windows'){@('-w',$hwnd)}else{@('-a',"$AppPid")};Checked (@('ui')+$Arguments+$selector)}
function Wait([string]$Selector){UI @('wait-for',$Selector,'-t','5000') | Out-Null}
function Keys([string]$Text){UI @('send-keys',$Text,'--via','send-input') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Traces{if(Test-Path -LiteralPath $trace){@(Get-Content -LiteralPath $trace | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})}else{@()}}
function Until([scriptblock]$Condition,[int]$Seconds=10){$deadline=[DateTime]::UtcNow.AddSeconds($Seconds);while([DateTime]::UtcNow -lt $deadline){if(& $Condition){return};Start-Sleep -Milliseconds 100};throw 'Timed out waiting for playback evidence.'}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});throw}}
function Capture([string]$Name){$p=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$p,'--json') | Out-Null;if(!(Test-Path -LiteralPath $p)){throw 'Screenshot missing.'};$screenshots.Add($p)}
function Info{Keys 'i';Wait 'MediaInfoText';(UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json).text}
function CloseInfo{Keys 'i';UI @('wait-for','MediaInfoText','--gone','-t','5000') | Out-Null}
function Paused{UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null}
function Playing{UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','5000') | Out-Null}
try{
    if((Test-Path -LiteralPath $trace) -and (Get-Item -LiteralPath $trace).Length -gt 0){throw 'Choose a fresh audio log path; existing traces cannot prove this run.'}
    New-Item -ItemType Directory -Force -Path $media | Out-Null
    Get-ChildItem -LiteralPath $sourceMedia -File | Where-Object Extension -in '.wav','.flac','.m4a' | Copy-Item -Destination $media
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $AppPid -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
    $hwnd=[string]$main.hwnd;if(!$hwnd){throw 'No local packaged app window.'}
    Case 'Register owned local fixture' {
        Wait 'ChooseFolder';UI @('invoke','ChooseFolder') | Out-Null;UI @('click','フォルダーを追加') | Out-Null
        Wait 'PC / ローカルフォルダー';UI @('click','PC / ローカルフォルダー') | Out-Null
        $script:picker=$null
        Until {$script:picker=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770'} | Select-Object -First 1;!!$script:picker}
        $pickerHwnd=[string]$picker.hwnd
        UI @('set-value','1152',$media) -Window $pickerHwnd | Out-Null
        $tree=UI @('inspect','--json','-d','12') -Window $pickerHwnd | ConvertFrom-Json
        $button=@(Nodes @($tree.windows | ForEach-Object {$_.elements})) | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
        if(!$button){throw 'No picker confirm button.'};UI @('invoke',$button.selector) -Window $pickerHwnd | Out-Null
        Register-TestFixture $media
    }
    Case 'Natural WAV / FLAC / WAV / AAC sequence uses 44.1 / 96 / 192 / 44.1 kHz and drains' {
        Wait '01 44.1k.wav';UI @('click','01 44.1k.wav') | Out-Null
        Until {@(Traces | Where-Object Event -eq playing).Count -ge 1}
        Write-Host 'DAC DISPLAY SEQUENCE STARTED: 44.1 -> 96 -> 192 -> 44.1 kHz (16 seconds each)'
        Until {@(Traces | Where-Object Event -eq ended).Count -ge 4} 85
        $ended=@(Traces | Where-Object Event -eq ended)
        if(($ended.Output.Format.SampleRate -join ',') -ne '44100,96000,192000,44100'){throw 'Unexpected applied rate sequence.'}
        if(@($ended | Where-Object {$_.PlayedFrames -lt $_.ContentFrames -or $_.ContentFrames -le 0}).Count){throw 'Natural ending preceded PCM drain.'}
        if(@(Traces | Where-Object Event -eq failed).Count){throw 'Engine reported a failure.'}
        Paused
        UI @('wait-for','PART_TitleText','--value','04 44.1k','-t','5000') | Out-Null
        Capture '01-final-ended'
    }
    Case 'Explicit replay of final AAC starts at zero' {
        $count=@(Traces | Where-Object Event -eq playing).Count
        UI @('invoke','Resume') | Out-Null
        Until {@(Traces | Where-Object Event -eq playing).Count -gt $count}
        $last=Traces | Where-Object Event -eq playing | Select-Object -Last 1
        if([TimeSpan]::Parse($last.Position).TotalSeconds -gt 1){throw 'Replay did not begin at zero.'}
        Playing
    }
    Case 'Information releases exclusive output and cancellation resumes' {
        $text=Info
        if($text -notmatch 'WASAPI / Media Foundation' -or $text -notmatch '44,100 Hz' -or $text -notmatch 'デコードPCM'){throw "Missing actual audio information: $text"}
        $text | Set-Content -LiteralPath (Join-Path $artifacts 'information.txt') -Encoding utf8
        Until {(Traces | Select-Object -Last 1).Event -eq 'released'}
        Capture '02-information'
        $count=@(Traces | Where-Object Event -eq playing).Count
        CloseInfo
        Until {@(Traces | Where-Object Event -eq playing).Count -gt $count}
        Playing
    }
    Case 'Paused seek stays paused and does not acquire WASAPI' {
        UI @('focus','TouchSurface') | Out-Null;Keys 'space';Paused
        Until {(Traces | Select-Object -Last 1).Event -eq 'released'}
        $count=@(Traces | Where-Object Event -eq initialized).Count
        Keys 'down'
        Until {@(Traces | Where-Object Event -eq 'seek-completed').Count -gt 0}
        Paused
        $seek=Traces | Where-Object Event -eq 'seek-completed' | Select-Object -Last 1
        if([TimeSpan]::Parse($seek.Position).TotalSeconds -lt 10){throw 'Paused seek did not move position.'}
        if(@(Traces | Where-Object Event -eq initialized).Count -ne $count){throw 'Paused seek opened an endpoint.'}
    }
    Case 'Next/previous while paused and rapid changes do not start stale tracks' {
        UI @('focus','TouchSurface') | Out-Null
        $playing=@(Traces | Where-Object Event -eq playing).Count
        Keys 'left left right left'
        UI @('wait-for','PART_TitleText','--value','02 96k','-t','5000') | Out-Null
        UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','7000') | Out-Null
        Paused;Start-Sleep -Milliseconds 500
        if(@(Traces | Where-Object Event -eq playing).Count -ne $playing){throw 'A paused or stale track started.'}
        $text=Info;if(([string]$text -split "`n")[0] -ne '02 96k.flac'){throw 'Rapid selection ended on the wrong file.'};CloseInfo;Paused
    }
    Case 'List cancellation preserves pause and file' {
        Keys 'esc';Wait 'BrowserList';UI @('invoke','BrowserCancel') | Out-Null;Paused
        $text=Info;if(([string]$text -split "`n")[0] -ne '02 96k.flac'){throw 'List cancellation changed the file.'};CloseInfo;Paused
        Capture '03-paused-restored'
    }
}catch{
    if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})}
    Write-Host "FAIL $_"
    try{Capture 'failure';UI @('inspect','--json','-d','12') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-tree.json') -Encoding utf8}catch{}
}finally{
    try{if(Get-Process -Id $AppPid -ErrorAction SilentlyContinue){Remove-TestFixtureRegistration;(Get-Process -Id $AppPid).CloseMainWindow() | Out-Null}}catch{$results.Add(@{name='Owned fixture cleanup';status='FAIL';detail="$_"})}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow
}
$failed=@($results | Where-Object status -eq FAIL).Count
@{target='local';appPid=$AppPid;passed=$results.Count-$failed;failed=$failed;results=$results.ToArray();screenshots=$screenshots.ToArray();trace=$trace} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Report: $artifacts/results.json"
if($failed){exit 1}
