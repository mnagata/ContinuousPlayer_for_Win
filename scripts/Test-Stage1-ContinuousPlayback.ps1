# Focused regression for the per-file MediaPlayer replacement introduced in stage 1.
# Its unique fixture registration is removed on exit; user files are unchanged.
param([ValidateRange(0,45)][int]$HoldFinalInfoSeconds=0)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage1-continuous\$id"
$media=Join-Path $repo ".tools\stage1-continuous\$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$previous=$env:WINAPP_UI_WORKFLOW_ID
$previousTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="stage1-continuous-$id"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0
$screenshot=$null
$replayScreenshot=$null
$errorText=$null
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Selector){if($Selector -eq 'Resume'){UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null;return};UI @('wait-for',$Selector,'-t','5000') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
try{
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $media | Out-Null
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId
    if(!$appPid){throw 'No local test process.'}
    Wait 'ChooseFolder';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.ownerHwnd -eq 0 -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
    $mainHwnd=[string]$main.hwnd;if(!$mainHwnd){throw 'No main window.'}
    UI @('focus','ChooseFolder') | Out-Null
    UI @('invoke','ChooseFolder') | Out-Null;UI @('click','フォルダーを追加') | Out-Null
    Wait 'PC / ローカルフォルダー';UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker=$null
    for($i=0;$i -lt 30;$i++){$picker=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770'} | Select-Object -First 1;if($picker){break};Start-Sleep -Milliseconds 100}
    if(!$picker){throw 'No folder picker.'}
    $pickerHwnd=[string]$picker.hwnd
    UI @('set-value','1152',$media) -Window $pickerHwnd | Out-Null
    $tree=UI @('inspect','--json','-d','12') -Window $pickerHwnd | ConvertFrom-Json
    $button=@(Nodes @($tree.windows | ForEach-Object {$_.elements})) | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
    if(!$button){throw 'No picker confirm button.'}
    UI @('invoke',$button.selector) -Window $pickerHwnd | Out-Null
    Register-TestFixture $media
    Wait 'Sample OP.wav';UI @('click','Sample OP.wav') | Out-Null;Wait 'VideoPlayer'
    Start-Sleep -Milliseconds 800
    UI @('focus','TouchSurface') | Out-Null
    UI @('send-keys','down down down down','--via','send-input') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $beforeEnd=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if($beforeEnd.text -notmatch '再生位置: 00:00:4[0-3]'){throw "Seek did not reach the natural-end fixture position: $($beforeEnd.text)"}
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    # The 45-second source is now near 40 seconds. Let the engine end naturally.
    Start-Sleep -Seconds 7
    UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','3000') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $info=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if(([string]$info.text -split "`n")[0] -ne 'Sample ED.wav'){throw 'Natural progression selected the wrong file.'}
    $screenshot=Join-Path $artifacts 'natural-next.png'
    Start-Sleep -Seconds 1
    UI @('screenshot','--capture-screen','--output',$screenshot,'--json') -Window $mainHwnd | Out-Null
    if(!(Test-Path -LiteralPath $screenshot)){throw 'Screenshot missing.'}
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    UI @('focus','TouchSurface') | Out-Null
    UI @('send-keys','right right','--via','send-input') | Out-Null
    UI @('wait-for','PART_TitleText','--value','Sample ED2','-t','5000') | Out-Null
    UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    UI @('send-keys','down down down down','--via','send-input') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $finalBeforeEnd=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    $finalBeforeEnd | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'final-before-end.json') -Encoding utf8
    if($finalBeforeEnd.text -notmatch '再生位置: 00:00:4[0-3]'){throw "Final seek did not reach the natural-end position: $($finalBeforeEnd.text)"}
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    Start-Sleep -Seconds 7
    Wait 'Resume'
    # PlaybackStatus belongs to the fullscreen overlay; windowed playback uses the title bar.
    UI @('send-keys','f11','--via','send-input') | Out-Null
    UI @('wait-for','PlaybackStatus','--value','最後まで再生しました','-t','5000') | Out-Null
    UI @('invoke','Resume') | Out-Null
    UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','3000') | Out-Null
    UI @('wait-for','SeekForward','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    Start-Sleep -Milliseconds 800
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $replay=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if($replay.text -notmatch '再生位置: 00:00:0[0-4]'){throw 'Explicit replay did not restart at zero.'}
    $replayScreenshot=Join-Path $artifacts 'final-replay.png'
    Start-Sleep -Seconds 1
    UI @('screenshot','--capture-screen','--output',$replayScreenshot,'--json') -Window $mainHwnd | Out-Null
    if($HoldFinalInfoSeconds){Write-Host "HOLD final information for capture comparison: PID=$appPid; ReportDirectory=$artifacts";Start-Sleep -Seconds $HoldFinalInfoSeconds}
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    UI @('send-keys','f11','--via','send-input') | Out-Null
    UI @('send-keys','h','--via','send-input') | Out-Null;Wait 'ChooseFolder'
    'PASS Natural media ending advances and plays the next file.'
    'PASS Explicit replay of the final file starts at zero.'
}catch{
    $errorText="$_";Write-Host "FAIL $errorText"
    try{
        UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
        UI @('get-value','MediaInfoText','--json') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-info.json') -Encoding utf8
        $failureScreenshot=Join-Path $artifacts 'failure.png'
        UI @('screenshot','--capture-screen','--output',$failureScreenshot,'--json') -Window $mainHwnd | Out-Null
    }catch{}
}
finally{
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){Remove-TestFixtureRegistration}}catch{if(!$errorText){$errorText="Fixture cleanup: $_"}else{$errorText+="; Fixture cleanup: $_"}}
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
        $tree=UI @('inspect','--json','-d','12') | ConvertFrom-Json
        $items=@(Nodes @($tree.windows | ForEach-Object {$_.elements}))
        if($items.automationId -contains 'MediaInfoText'){UI @('send-keys','i','--via','send-input') | Out-Null;UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null}
        if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}
        UI @('send-keys','h','--via','send-input') | Out-Null;Wait 'ChooseFolder'
        (Get-Process -Id $appPid).CloseMainWindow() | Out-Null
    }}catch{if(!$errorText){$errorText="Cleanup: $_"}}
    try{Checked @('ui','yield') | Out-Null}catch{if(!$errorText){$errorText="Release: $_"}}
    $env:WINAPP_UI_WORKFLOW_ID=$previous
    $env:DOTNET_CLI_TELEMETRY_OPTOUT=$previousTelemetry
}
@{passed=(!$errorText);error=$errorText;target='local';appPid=$appPid;screenshots=@($screenshot,$replayScreenshot | Where-Object {$_});fixture=$media} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Report: $artifacts\results.json"
if($errorText){exit 1}
