# Local packaged-app presentation acceptance. Uses a temporary owned registration.
param([string]$VideoPath = (Join-Path $PSScriptRoot '..\.tools\cursor-media\Cursor test.mp4'))
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage3\$id"
$fixture=Join-Path $repo ".tools\stage3-media\$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="continuousplayer-stage3-$id"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=$null
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
$observations=[Collections.Generic.List[object]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Name){UI @('wait-for',$Name,'-t','5000') | Out-Null}
function Keys([string]$Text){UI @('send-keys',$Text,'--via','send-input') | Out-Null}
function State([string]$Label){UI @('wait-for','Resume','-p','Name','--value',$Label,'-t','5000') | Out-Null}
function Enabled([string]$Name,[bool]$Value){UI @('wait-for',$Name,'-p','IsEnabled','--value',"$Value",'-t','5000') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect{$t=UI @('inspect','--json','-d','12') | ConvertFrom-Json;@(Nodes @($t.windows | ForEach-Object {$_.elements}))}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});try{Capture 'failure'}catch{};throw}}
function Capture([string]$Name){$p=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$p,'--json') -Window ([string]$hwnd) | Out-Null;if(!(Test-Path -LiteralPath $p) -or !(Get-Item -LiteralPath $p).Length){throw 'Screenshot missing.'};$screenshots.Add($p)}
function Info([string]$Name){Keys 'i';Wait 'MediaInfoText';$v=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json;if($v.text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})'){throw 'Position missing.'};$r=@{name=$Name;file=([string]$v.text -split "`n")[0];seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds;text=$v.text};$observations.Add($r);$r}
function CloseInfo{Keys 'i';UI @('wait-for','MediaInfoText','--gone','-t','5000') | Out-Null;UI @('wait-for','PlaybackFolder','-p','IsEnabled','--value','True','-t','5000') | Out-Null}
function Near([double]$Expected,[double]$Actual,[double]$Tolerance=1){if([math]::Abs($Expected-$Actual) -gt $Tolerance){throw "Position $Actual, expected $Expected +/- $Tolerance"}}
function PickFolder {
    UI @('invoke','ChooseFolder') | Out-Null;UI @('click','フォルダーを追加') | Out-Null
    Wait 'PC / ローカルフォルダー';UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker=$null
    for($n=0;$n -lt 40;$n++){$picker=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770'} | Select-Object -First 1;if($picker){break};Start-Sleep -Milliseconds 100}
    if(!$picker){throw 'No folder picker.'};$pickerHwnd=[string]$picker.hwnd
    UI @('set-value','1152',$fixture) -Window $pickerHwnd | Out-Null
    $t=UI @('inspect','--json','-d','12') -Window $pickerHwnd | ConvertFrom-Json
    $confirm=@(Nodes @($t.windows | ForEach-Object {$_.elements})) | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
    if(!$confirm){throw 'Picker confirm unavailable.'};UI @('invoke',$confirm.selector) -Window $pickerHwnd | Out-Null
    Register-TestFixture $fixture;Wait 'Sample OP.wav'
}
function CheckLayout([string]$Name){
    $items=Inspect;$ids=@('PlayerHome','PlaybackFolder','PlaybackInfo','PreviousFile','SeekBackward','Resume','SeekForward','NextFile')
    $buttons=@($items | Where-Object {$_.automationId -in $ids});if($buttons.Count -ne 8){throw 'Eight main commands were not exposed.'}
    $title=$items | Where-Object {$_.automationId -in @('PART_TitleText','PlaybackTitle') -and !$_.isOffscreen} | Select-Object -First 1
    $bottom=$buttons | Where-Object {$_.automationId -eq 'Resume'}
    foreach($b in $buttons){$minimum=if($b.automationId -in @('PlayerHome','PlaybackFolder','PlaybackInfo')){27}else{47};if($b.width -lt $minimum -or $b.height -lt $minimum -or $b.x -lt 0 -or $b.y -lt 0){throw "Clipped target $($b.automationId)"}}
    $top=@($buttons | Where-Object {$_.automationId -in @('PlayerHome','PlaybackFolder','PlaybackInfo')} | Sort-Object x)
    if(($top.automationId -join ',') -ne 'PlayerHome,PlaybackFolder,PlaybackInfo'){throw 'Top command order differs.'}
    $lower=@($buttons | Where-Object {$_.automationId -in @('PreviousFile','SeekBackward','Resume','SeekForward','NextFile')} | Sort-Object x)
    if(($lower.automationId -join ',') -ne 'PreviousFile,SeekBackward,Resume,SeekForward,NextFile'){throw 'Bottom order differs.'}
    if($title.y -ge $bottom.y){throw 'Top and bottom overlap.'}
    $observations.Add(@{name=$Name;buttons=$buttons;title=$title});Capture $Name
}

# Reuse the guarded, bitmap-aware OS cursor probe from the existing cursor test.
# Read its literal C# declaration without executing its test flow.
$cursorScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Cursor.ps1') -Raw
$native=[regex]::Match($cursorScript,"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(!$native){throw 'Cursor probe declaration missing.'}
$native=$native.Replace('NativeCursorTest','Stage3Native')
$extra=@'
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr w);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr w,uint m,IntPtr p,IntPtr l);
    public static void Resize(IntPtr w,int width,int height){Guard(w);var old=SetThreadDpiAwarenessContext(new IntPtr(-4));try{double scale=GetDpiForWindow(w)/96.0;if(!SetWindowPos(w,IntPtr.Zero,40,40,(int)(width*scale),(int)(height*scale),0x14))throw new Exception("Resize failed");}finally{SetThreadDpiAwarenessContext(old);}}
    public static void Close(IntPtr w){PostMessageW(w,0x10,IntPtr.Zero,IntPtr.Zero);}
'@
$native=$native.Insert($native.LastIndexOf('}'),$extra)
Add-Type $native
try {
    if(!(Test-Path -LiteralPath $VideoPath -PathType Leaf)){throw 'A valid video of at least 60 seconds is required.'}
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $fixture -DurationSeconds 120 | Out-Null
    Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $fixture 'zz.Movie.version.1.mp4')
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId;if(!$appPid){throw 'No local process.'}
    Wait 'ChooseFolder';Enabled 'ChooseFolder' $true;UI @('focus','ChooseFolder') | Out-Null
    $hwnd=[Stage3Native]::Main($appPid);if(!$hwnd){throw 'No main window.'}
    Case 'S25: common home label, OS close only, fullscreen auxiliary available' {
        UI @('wait-for','ChooseFolder','-p','Name','--value','動画フォルダーを選ぶ','-t','3000') | Out-Null
        UI @('wait-for','ExitHome','--gone','-t','2000') | Out-Null;Wait 'WindowFullscreen';Capture '01-home'
    }
    PickFolder;UI @('click','Sample OP.wav') | Out-Null;Wait 'Resume';State '一時停止'
    Case 'S20 S23: windowed playback stays windowed and audio controls stay visible' {
        Wait 'WindowFullscreen';Start-Sleep -Seconds 4;State '一時停止';CheckLayout '02-audio-playing'
    }
    Case 'S21 S24: five transport buttons seek and move while preserving paused intent' {
        UI @('invoke','Resume') | Out-Null;State '再生';Enabled 'PreviousFile' $false;Enabled 'NextFile' $true
        Enabled 'SeekBackward' $true;Enabled 'SeekForward' $true
        UI @('invoke','SeekBackward') | Out-Null;$v=Info 'clamp-zero';Near 0 $v.seconds;CloseInfo
        UI @('invoke','SeekForward') | Out-Null;$v=Info 'button-forward';Near 10 $v.seconds;CloseInfo
        UI @('invoke','SeekBackward') | Out-Null;$v=Info 'button-back';Near 0 $v.seconds;CloseInfo
        UI @('invoke','NextFile') | Out-Null;State '再生';$v=Info 'button-next';if($v.file -ne 'Sample ED.wav'){throw 'Next failed.'};CloseInfo
        UI @('invoke','PreviousFile') | Out-Null;State '再生';$v=Info 'button-previous';if($v.file -ne 'Sample OP.wav'){throw 'Previous failed.'};CloseInfo
    }
    Case 'S21: keyboard focus owns arrows and Enter invokes the selected command' {
        UI @('focus','PlaybackFolder') | Out-Null;Keys 'right';UI @('wait-for','PlaybackInfo','-p','HasKeyboardFocus','--value','True','-t','3000') | Out-Null
        Keys 'enter';Wait 'MediaInfoText';Capture '03-information';CloseInfo
    }
    Case 'S25: information groups file, formats and output; raw name is retained' {
        $v=Info 'information-order'
        if($v.text.IndexOf('ファイル') -ge $v.text.IndexOf('映像・音声形式') -or $v.text.IndexOf('映像・音声形式') -ge $v.text.IndexOf('音声出力')){throw 'Information order differs.'}
        if($v.text -notmatch '名前: Sample OP.wav' -or $v.text -notmatch '未対応'){throw 'Name/capability missing.'};CloseInfo
        $title=UI @('get-value','PART_TitleText','--json') | ConvertFrom-Json;if($title.text -ne 'Sample OP'){throw 'Extension not removed.'}
    }
    Case 'S20: fullscreen buttons and F11 keep file, position, intent and fullscreen home' {
        UI @('invoke','SeekForward') | Out-Null;$before=Info 'before-fullscreen';CloseInfo
        UI @('invoke','WindowFullscreen') | Out-Null;Wait 'PlaybackFullscreen';$after=Info 'after-fullscreen';Near $before.seconds $after.seconds;CloseInfo;State '再生'
        UI @('invoke','PlaybackFullscreen') | Out-Null;Wait 'WindowFullscreen';State '再生'
        Keys 'f11';Wait 'PlaybackFullscreen';UI @('invoke','PlayerHome') | Out-Null;Wait 'ChooseFolder';Wait 'HomeFullscreen';Capture '04-fullscreen-home'
        UI @('invoke','HomeFullscreen') | Out-Null;Wait 'WindowFullscreen';PickFolder;UI @('click','Sample OP.wav') | Out-Null;State '一時停止';UI @('invoke','Resume') | Out-Null;State '再生'
    }
    Case 'S26: minimum, portrait and short landscape preserve eight accessible targets' {
        [Stage3Native]::Resize($hwnd,440,380);Start-Sleep -Milliseconds 400;CheckLayout '05-minimum'
        [Stage3Native]::Resize($hwnd,500,850);Start-Sleep -Milliseconds 400;CheckLayout '06-portrait'
        [Stage3Native]::Resize($hwnd,1100,400);Start-Sleep -Milliseconds 400;CheckLayout '07-landscape'
        [Stage3Native]::Resize($hwnd,1000,740)
        Start-Sleep -Milliseconds 400
    }
    Case 'S21: touch can invoke the screen seek, file list and information buttons' {
        $before=Info 'before-touch-button';CloseInfo
        UI @('touch','SeekForward','-g','tap') | Out-Null;$after=Info 'after-touch-button';Near ($before.seconds+10) $after.seconds;CloseInfo
        UI @('touch','PlaybackFolder','-g','tap') | Out-Null;Wait 'BrowserList';UI @('invoke','BrowserCancel') | Out-Null;State '再生'
        UI @('touch','PlaybackInfo','-g','tap') | Out-Null;Wait 'MediaInfoText';CloseInfo
    }
    # Avoid walking the MediaPlayerElement UIA provider during video playback.
    Keys 'esc';Wait 'BrowserList';UI @('click','zz.Movie.version.1.mp4') | Out-Null
    Start-Sleep -Seconds 1;[Stage3Native]::KeyPress($hwnd,32);State '再生'
    Case 'S24 S25: final next is disabled and dotted video title loses only its extension' {
        Enabled 'NextFile' $false;Enabled 'PreviousFile' $true
        $v=UI @('get-value','PART_TitleText','--json') | ConvertFrom-Json;if($v.text -ne 'zz.Movie.version.1'){throw 'Dotted title changed.'};Capture '08-video-paused'
        $script:playerBefore=Inspect | Where-Object {$_.automationId -eq 'VideoPlayer'} | Select-Object -First 1
        if(!$playerBefore){throw 'Video bounds unavailable.'}
    }
    Case 'S22: video mouse inactivity hides cursor and chrome, movement restores both' {
        [Stage3Native]::KeyPress($hwnd,32);[Stage3Native]::Move($hwnd);Start-Sleep -Milliseconds 200
        $cursorWait=[Diagnostics.Stopwatch]::StartNew()
        while(![Stage3Native]::Visible($hwnd) -and $cursorWait.Elapsed.TotalSeconds -lt 2){Start-Sleep -Milliseconds 100}
        $observations.Add(@{name='cursor-after-resume-move';state=[Stage3Native]::CursorState();waitSeconds=$cursorWait.Elapsed.TotalSeconds})
        if(![Stage3Native]::Visible($hwnd)){throw "Movement did not restore cursor: $([Stage3Native]::CursorState())"};Capture '09-video-active'
        $before=[Stage3Native]::Position();Start-Sleep -Seconds 4
        if([Stage3Native]::Visible($hwnd)){throw 'Idle cursor remained visible.'}
        if([Stage3Native]::Position() -ne $before){throw 'Cursor moved while hiding.'};Capture '10-video-idle'
        [Stage3Native]::Move($hwnd,50);Start-Sleep -Milliseconds 200
        if(![Stage3Native]::Visible($hwnd)){throw 'Cursor did not restore.'};Capture '11-video-restored'
        [Stage3Native]::KeyPress($hwnd,32);State '再生'
        $after=Inspect | Where-Object {$_.automationId -eq 'VideoPlayer'} | Select-Object -First 1
        if(!$after){throw 'Video bounds unavailable after toggling.'}
        foreach($p in @('x','y','width','height')){if($after.$p -ne $playerBefore.$p){throw "Video layout moved: $p"}}
    }
    Case 'S22: keyboard-focused controls keep the commands and cursor' {
        UI @('focus','PlaybackFolder') | Out-Null;Keys 'enter';Wait 'BrowserList';UI @('invoke','BrowserCancel') | Out-Null
        UI @('focus','PlaybackFolder') | Out-Null
        # Keep the paused intent here, then explicitly resume via SMTC-independent surface input.
        UI @('focus','TouchSurface') | Out-Null;[Stage3Native]::KeyPress($hwnd,32)
        UI @('focus','PlayerHome') | Out-Null;Keys 'right'
        UI @('wait-for','PlaybackFolder','-p','HasKeyboardFocus','--value','True','-t','3000') | Out-Null
        Start-Sleep -Seconds 4;Wait 'PlaybackFolder';Capture '12-focused-playing'
    }
    Case 'Video overlays hide after three idle seconds over a button and restore on movement' {
        UI @('focus','TouchSurface') | Out-Null;UI @('hover','SeekForward') | Out-Null
        Start-Sleep -Milliseconds 700
        if(![Stage3Native]::Visible($hwnd)){throw 'Hover hid before the idle delay.'};Capture '13-hover-active'
        $before=[Stage3Native]::Position();Start-Sleep -Milliseconds 2800
        if([Stage3Native]::Visible($hwnd)){throw 'Stationary hover prevented auto-hide.'}
        if([Stage3Native]::Position() -ne $before){throw 'Pointer moved during hover idle.'};Capture '14-hover-idle'
        [Stage3Native]::Move($hwnd);Start-Sleep -Milliseconds 200
        if(![Stage3Native]::Visible($hwnd)){throw 'Movement did not restore after hover idle.'};Capture '15-hover-restored'
        [Stage3Native]::KeyPress($hwnd,32)
        State '再生'
    }
    Case 'Mouse resume hides the video overlays immediately' {
        UI @('click','Resume') | Out-Null
        Start-Sleep -Milliseconds 150
        if([Stage3Native]::Visible($hwnd)){throw 'Resume did not hide controls immediately.'};Capture '16-click-resumed'
        $before=[Stage3Native]::Position();Start-Sleep -Milliseconds 3200
        if([Stage3Native]::Visible($hwnd)){throw 'Mouse focus prevented auto-hide.'}
        if([Stage3Native]::Position() -ne $before){throw 'Pointer moved during mouse-focus idle.'};Capture '17-click-idle'
        [Stage3Native]::KeyPress($hwnd,32);State '再生'
    }
} catch {if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"});try{Capture 'setup-failure'}catch{}};Write-Host "FAIL $_"}
finally {
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){Remove-TestFixtureRegistration}}catch{$results.Add(@{name='Fixture registration cleanup';status='FAIL';detail="$_"})}
    if($hwnd -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){[Stage3Native]::Close($hwnd)}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray());observations=@($observations.ToArray());fixture=$fixture;registeredFolders=$script:testFixtureWasRegistered;remainingTestRegistration=$script:registeredFixtureName} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
