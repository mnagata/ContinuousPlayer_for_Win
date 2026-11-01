# Focused regression for the per-file MediaPlayer replacement introduced in stage 1.
# No registrations or user files are changed.
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
function Wait([string]$Selector){UI @('wait-for',$Selector,'-t','5000') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
try{
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $media | Out-Null
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId
    if(!$appPid){throw 'No local test process.'}
    Wait 'ChooseFolder';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
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
    Wait 'Sample OP.wav';UI @('click','Sample OP.wav') | Out-Null;Wait 'VideoPlayer'
    Start-Sleep -Milliseconds 800
    UI @('send-keys','down down down down','--via','send-input') | Out-Null
    # The 45-second source is now near 40 seconds. Let the engine end naturally.
    Start-Sleep -Seconds 7
    Wait 'Sample ED.wav'
    UI @('wait-for','Resume','--gone','-t','3000') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $info=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if(([string]$info.text -split "`n")[0] -ne 'Sample ED.wav'){throw 'Natural progression selected the wrong file.'}
    $screenshot=Join-Path $artifacts 'natural-next.png'
    UI @('screenshot','--output',$screenshot,'--json') | Out-Null
    if(!(Test-Path -LiteralPath $screenshot)){throw 'Screenshot missing.'}
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    UI @('send-keys','right right','--via','send-input') | Out-Null;Wait 'Sample ED2.wav'
    Start-Sleep -Milliseconds 500
    UI @('send-keys','down down down down','--via','send-input') | Out-Null
    Start-Sleep -Seconds 7
    Wait 'Resume';UI @('invoke','Resume') | Out-Null
    UI @('wait-for','Resume','--gone','-t','3000') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null;Wait 'MediaInfoText'
    $replay=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if($replay.text -notmatch '再生位置: 00:00:0[0-4]'){throw 'Explicit replay did not restart at zero.'}
    $replayScreenshot=Join-Path $artifacts 'final-replay.png'
    UI @('screenshot','--output',$replayScreenshot,'--json') | Out-Null
    UI @('send-keys','i','--via','send-input') | Out-Null
    UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    UI @('send-keys','h','--via','send-input') | Out-Null;Wait 'ChooseFolder'
    'PASS Natural media ending advances and plays the next file.'
    'PASS Explicit replay of the final file starts at zero.'
}catch{$errorText="$_";Write-Host "FAIL $errorText"}
finally{
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
        $tree=UI @('inspect','--json','-d','12') | ConvertFrom-Json
        $items=@(Nodes @($tree.windows | ForEach-Object {$_.elements}))
        if($items.automationId -contains 'MediaInfoText'){UI @('send-keys','i','--via','send-input') | Out-Null;UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null}
        if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}
        UI @('send-keys','h','--via','send-input') | Out-Null;Wait 'ChooseFolder'
        UI @('invoke','ExitHome') | Out-Null
    }}catch{if(!$errorText){$errorText="Cleanup: $_"}}
    try{Checked @('ui','yield') | Out-Null}catch{if(!$errorText){$errorText="Release: $_"}}
    $env:WINAPP_UI_WORKFLOW_ID=$previous
    $env:DOTNET_CLI_TELEMETRY_OPTOUT=$previousTelemetry
}
@{passed=(!$errorText);error=$errorText;target='local';appPid=$appPid;screenshots=@($screenshot,$replayScreenshot | Where-Object {$_});fixture=$media} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Report: $artifacts\results.json"
if($errorText){exit 1}
