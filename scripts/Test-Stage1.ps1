# Local, packaged-app acceptance batch. Never locks or suspends the workstation.
param([int]$AppPid = 0)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts = Join-Path $repoRoot "ui-test-artifacts\stage1\$runId"
$fixture = Join-Path $repoRoot ".tools\stage1-media\$runId"
$folderA = Join-Path $fixture "Stage1-$runId-A"
$folderB = Join-Path $fixture "Stage1-$runId-B"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$oldWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldBuildServer = $env:DOTNET_CLI_USE_MSBUILD_SERVER
$env:WINAPP_UI_WORKFLOW_ID = "continuousplayer-stage1-$runId"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$positions = [Collections.Generic.List[object]]::new()
$registered = [Collections.Generic.List[string]]::new()
$hwnd = $null
$launchedHere = $false

function Invoke-Stage1WinApp([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    if ($null -eq $LASTEXITCODE -or $LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function UI([string[]]$Arguments, [string]$Window) {
    $selector = if ($Window) { @('-w',$Window) } else { @('-a',"$AppPid") }
    Invoke-Stage1WinApp (@('ui') + $Arguments + $selector)
}
function Wait([string]$Id) { if($Id -eq 'Resume'){ UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null; return }; UI @('wait-for',$Id,'-t','5000') | Out-Null }
function Gone([string]$Id) { if($Id -eq 'Resume'){ UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','5000') | Out-Null; return }; UI @('wait-for',$Id,'--gone','-t','5000') | Out-Null }
function Keys([string]$Value) { UI @('send-keys',$Value,'--via','send-input') | Out-Null }
function Elements($nodes) { foreach($node in $nodes){ if($null -ne $node){$node; Elements $node.children} } }
function Inspect([string]$Window) {
    $tree = UI @('inspect','--json','-d','12') -Window $Window | ConvertFrom-Json
    @(Elements @($tree.windows | ForEach-Object {$_.elements}))
}
function Capture([string]$Name) {
    $path = Join-Path $artifacts "$Name.png"
    UI @('screenshot','--output',$path,'--json') -Window $hwnd | Out-Null
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw 'Screenshot was not delivered.' }
    $screenshots.Add($path)
}
function Case([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null; $results.Add(@{name=$Name;status='PASS'}); Write-Host "PASS $Name" }
    catch { $results.Add(@{name=$Name;status='FAIL';detail="$_"}); throw }
}
function Pick-Folder([string]$Path) {
    UI @('click','フォルダーを追加') | Out-Null; Wait 'PC / ローカルフォルダー'
    UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker = $null
    for($attempt=0;$attempt -lt 40;$attempt++) {
        $windows = @(UI @('list-windows','--json') | ConvertFrom-Json)
        $picker = $windows | Where-Object {$_.className -eq '#32770' -and $_.ownerHwnd -eq [long]$hwnd} | Select-Object -First 1
        if($picker){break}; Start-Sleep -Milliseconds 100
    }
    if(!$picker){throw 'Native folder picker not found.'}
    $pickerHwnd = [string]$picker.hwnd
    UI @('set-value','1152',$Path) -Window $pickerHwnd | Out-Null
    $button = Inspect $pickerHwnd | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
    if(!$button){throw 'Native picker confirmation button not found.'}
    UI @('invoke',$button.selector) -Window $pickerHwnd | Out-Null
    Wait 'RegisterFolder'
}
function Register-Folder([string]$Path) {
    Pick-Folder $Path
    UI @('invoke','RegisterFolder') | Out-Null
    $name = Split-Path -Leaf $Path
    $registered.Add($name)
    Wait $name
}
function Info-Position([string]$Name) {
    Keys 'i'; Wait 'MediaInfoText'
    $value = UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    $text = [string]$value.text
    if($text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})'){throw "No playback position: $text"}
    $seconds = [TimeSpan]::Parse($Matches[1]).TotalSeconds
    $positions.Add(@{name=$Name;seconds=$seconds;file=($text -split "`n")[0]})
    return $seconds
}
function Close-Info { Keys 'i'; Gone 'MediaInfoText' }
function Near([double]$Expected,[double]$Actual,[double]$Tolerance=2) {
    if([math]::Abs($Actual-$Expected) -gt $Tolerance){throw "Position expected $Expected +/- $Tolerance, actual $Actual"}
}
function Notification([uint32]$Message,[uint64]$Value) {
    [Stage1Native]::Notify([IntPtr]::new([long]$hwnd),$AppPid,$Message,[UIntPtr]::new([uint64]$Value))
}

if(!('Stage1Native' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Stage1Native {
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h,uint m,UIntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h,uint m,UIntPtr w,IntPtr l);
    static void Check(IntPtr h,int pid){uint actual;GetWindowThreadProcessId(h,out actual);if(actual!=(uint)pid)throw new Exception("Window does not belong to test app.");}
    public static void Notify(IntPtr h,int pid,uint m,UIntPtr w){Check(h,pid);SendMessageW(h,m,w,IntPtr.Zero);}
    public static void Minimize(IntPtr h,int pid){Check(h,pid);ShowWindow(h,6);}
    public static void Restore(IntPtr h,int pid){Check(h,pid);ShowWindow(h,9);}
    public static void Close(IntPtr h,int pid){Check(h,pid);PostMessageW(h,0x10,UIntPtr.Zero,IntPtr.Zero);}
}
'@
}

try {
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $folderA -DurationSeconds 120 | Out-Null
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $folderB -DurationSeconds 120 | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $folderA 'Child') | Out-Null
    Copy-Item -LiteralPath (Join-Path $folderB 'Sample ED.wav') -Destination (Join-Path $folderA 'Child\Nested.wav')
    if($AppPid -eq 0) {
        $launch = Invoke-Stage1WinApp @('run',(Join-Path $repoRoot 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
        $AppPid = [int]$launch.ProcessId
        if(!$AppPid -or ($launch.ProcessScope -and $launch.ProcessScope -ne 'local')){throw 'No local app process returned.'}
        $launchedHere = $true
    }
    $main = $null
    for($attempt=0;$attempt -lt 30;$attempt++){
        $windows = @(UI @('list-windows','--json') | ConvertFrom-Json)
        $main = $windows | Where-Object {$_.processId -eq $AppPid -and $_.ownerHwnd -eq 0 -and $_.className -eq 'WinUIDesktopWin32WindowClass'} | Select-Object -First 1
        if($main){break}; Start-Sleep -Milliseconds 100
    }
    if(!$main){throw 'No app window found.'}
    $hwnd = [string]$main.hwnd
    Case 'Setup: registered silent fixture, playback and source focus' {
        Wait 'ChooseFolder'
        UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
        UI @('focus','ChooseFolder') | Out-Null
        UI @('invoke','ChooseFolder') | Out-Null
        Register-Folder $folderA
        UI @('click',(Split-Path -Leaf $folderA)) | Out-Null; Wait 'Sample OP.wav'
        UI @('click','Sample OP.wav') | Out-Null; Wait 'VideoPlayer'; Gone 'Resume'
        Start-Sleep -Milliseconds 600
        Keys 'down down down'
        $script:start = Info-Position 'original-playing'
        Near 30 $script:start 4
        Close-Info; Gone 'Resume'
    }
    Case 'S01: cancel browsing a different folder restores original position and playback' {
        Keys 'esc'; Wait 'BrowserList'; Wait 'Sample OP.wav'
        UI @('click','Child') | Out-Null; Wait 'Nested.wav'; Capture '01-browsing-child'
        UI @('invoke','BrowserCancel') | Out-Null; Gone 'BrowserList'; Gone 'Resume'
        $value = Info-Position 'after-browser-cancel'; Near $script:start $value 4
        Close-Info; Gone 'Resume'
    }
    Case 'S02: paused browsing cancellation preserves position and pause' {
        Keys 'space'; Wait 'Resume'
        $script:pausedAt = Info-Position 'original-paused'; Close-Info; Wait 'Resume'
        Keys 'esc'; Wait 'BrowserList'; UI @('click','Child') | Out-Null; Wait 'Nested.wav'
        UI @('invoke','BrowserCancel') | Out-Null; Wait 'Resume'
        $value = Info-Position 'after-paused-cancel'; Near $script:pausedAt $value 1
        Close-Info; Wait 'Resume'
    }
    Case 'S04: selecting the same file while paused starts at zero and plays' {
        Keys 'esc'; Wait 'BrowserList'; UI @('click','Sample OP.wav') | Out-Null
        Gone 'Resume'; $value = Info-Position 'same-file-reselected'
        if($value -gt 4){throw "Same-file selection did not reset: $value"}
        Close-Info; Gone 'Resume'
    }
    Case 'S05/S11: information owns I/Escape and blocks the next media key' {
        $null = Info-Position 'information-input'
        Keys 'vk=0xB0'; Capture '02-information'
        Keys 'esc'; Gone 'MediaInfoText'; Gone 'BrowserList'
        $null = Info-Position 'after-info-next-key'
        if($positions[$positions.Count-1].file -ne 'Sample OP.wav'){throw 'Media key reached playback behind information.'}
        Close-Info; Gone 'Resume'
    }
    Case 'S03: register another folder while browsing, then cancel to original session' {
        Keys 'esc'; Wait 'BrowserList'; UI @('invoke','BrowserBack') | Out-Null; Wait 'フォルダーを追加'
        Register-Folder $folderB
        UI @('invoke','BrowserCancel') | Out-Null; Gone 'Resume'
        $null = Info-Position 'after-registration'
        if($positions[$positions.Count-1].file -ne 'Sample OP.wav'){throw 'Registration changed the playback queue.'}
        Close-Info; Gone 'Resume'
    }
    Case 'S06: simulated session lock during browser prevents automatic resume' {
        Keys 'esc'; Wait 'BrowserList'
        Notification 0x2B1 7; Notification 0x2B1 8
        UI @('invoke','BrowserCancel') | Out-Null; Wait 'Resume'
        $script:lockedAt = Info-Position 'after-simulated-lock'; Close-Info; Wait 'Resume'
        Start-Sleep -Seconds 2
        $value = Info-Position 'still-paused-after-lock'; Near $script:lockedAt $value 1
        Close-Info; Wait 'Resume'
    }
    Case 'S06: simulated suspend during information prevents automatic resume' {
        UI @('invoke','Resume') | Out-Null; Gone 'Resume'
        $null = Info-Position 'before-simulated-suspend'
        Notification 0x218 4; Notification 0x218 7
        Close-Info; Wait 'Resume'; Capture '03-interrupted-paused'
    }
    Case 'S08: deactivation notification alone keeps playback running' {
        UI @('invoke','Resume') | Out-Null; Gone 'Resume'
        $before = Info-Position 'before-deactivation'; Close-Info; Gone 'Resume'
        Notification 0x6 0
        Start-Sleep -Seconds 3
        $after = Info-Position 'after-deactivation'
        if($after -lt $before+2){throw 'Playback stopped on deactivation.'}
        Close-Info; Gone 'Resume'
    }
    Case 'S09: real app minimization requires manual resume after restore' {
        [Stage1Native]::Minimize([IntPtr]::new([long]$hwnd),$AppPid)
        Start-Sleep -Milliseconds 300
        [Stage1Native]::Restore([IntPtr]::new([long]$hwnd),$AppPid)
        Wait 'Resume'
        $before = Info-Position 'after-minimize'; Close-Info; Wait 'Resume'
        Start-Sleep -Seconds 2
        $after = Info-Position 'still-paused-after-minimize'; Near $before $after 1
        Close-Info; Wait 'Resume'
    }
    Case 'S07: home disposes held session and test-only registrations can be removed' {
        Keys 'h'; Wait 'ChooseFolder'; UI @('invoke','ChooseFolder') | Out-Null; Wait 'BrowserList'
        foreach($name in @($registered.ToArray())) {
            $row = Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($name+'。')} | Select-Object -First 1
            if(!$row){throw "Missing test registration: $name"}
            UI @('invoke',$row.selector,'--action','select') | Out-Null
            Keys 'delete'; Wait 'PrimaryButton'
            UI @('invoke','PrimaryButton') | Out-Null; Gone $name
            $registered.Remove($name) | Out-Null
        }
        UI @('invoke','BrowserCancel') | Out-Null; Wait 'ChooseFolder'
    }
} catch {
    if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){
        $results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})
    }
    Write-Host "FAIL $_"
} finally {
    # Do not touch registrations belonging to the user. Failed cleanup is visible
    # in the report, with exact test names, rather than editing library.json.
    if($registered.Count -and $hwnd -and (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)){
        try {
            [Stage1Native]::Restore([IntPtr]::new([long]$hwnd),$AppPid)
            $nodes = Inspect
            if($nodes.automationId -contains 'CloseButton'){UI @('invoke','CloseButton') | Out-Null}
            $nodes = Inspect
            if($nodes.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}
            Keys 'h'; Wait 'ChooseFolder'; UI @('invoke','ChooseFolder') | Out-Null; Wait 'BrowserList'
            foreach($name in @($registered.ToArray())){
                $row = Inspect | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($name+'。')} | Select-Object -First 1
                if(!$row){throw "Cannot find test registration $name"}
                UI @('invoke',$row.selector,'--action','select') | Out-Null
                Keys 'delete'; Wait 'PrimaryButton'
                UI @('invoke','PrimaryButton') | Out-Null; Gone $name
                $registered.Remove($name) | Out-Null
            }
        } catch { Write-Host "Cleanup: $_" }
    }
    if($registered.Count){$results.Add(@{name='Test registration cleanup';status='FAIL';detail="Remove test registrations: $($registered -join ', ')"})}
    if($launchedHere -and $hwnd -and (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)){
        try {
            [Stage1Native]::Restore([IntPtr]::new([long]$hwnd),$AppPid)
            [Stage1Native]::Close([IntPtr]::new([long]$hwnd),$AppPid)
        } catch {$results.Add(@{name='Close test app';status='FAIL';detail="$_"})}
    }
    try { Invoke-Stage1WinApp @('ui','yield') | Out-Null }
    catch {$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID = $oldWorkflow
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = $oldBuildServer
}
$failed = @($results | Where-Object {$_.status -eq 'FAIL'}).Count
$report = [ordered]@{
    target='local'; appPid=$AppPid; passed=$results.Count-$failed; failed=$failed
    physicalLockSleepTested=$false; physicalOutputDisconnectTested=$false
    lockAndSleepMethod='Synthetic messages to the test app HWND only'
    results=@($results.ToArray()); positions=@($positions.ToArray()); screenshots=@($screenshots.ToArray())
    remainingTestRegistrations=@($registered.ToArray()); fixture=$fixture
}
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($report.passed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
