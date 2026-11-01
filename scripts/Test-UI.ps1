param([int]$AppPid = 0, [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\ui-test-artifacts\suite'))
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = 'continuousplayer-suite-' + [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()

function Invoke-WinAppChecked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output
}
function UI([string[]]$Arguments, [string]$Window) {
    $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
    Invoke-WinAppChecked (@('ui') + $Arguments + $selector)
}
function Elements($nodes) {
    foreach ($node in $nodes) { if ($null -ne $node) { $node; Elements $node.children } }
}
function Inspect([string]$Window) {
    $tree = UI @('inspect','--json','-d','12') -Window $Window | ConvertFrom-Json
    @(Elements @($tree.windows | ForEach-Object { $_.elements }))
}
function Wait([string]$Selector) { UI @('wait-for', $Selector, '-t', '5000') | Out-Null }
function Capture([string]$Name) {
    $path = Join-Path $ArtifactDirectory ($Name + '.png')
    UI @('screenshot','--output',$path,'--json') -Window $script:mainHwnd | Out-Null
    if (!(Test-Path -LiteralPath $path)) { throw "Screenshot missing: $path" }
    $screenshots.Add($path)
}
function Case([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null; $results.Add(@{ name=$Name; status='PASS' }); Write-Host "PASS $Name" }
    catch { $results.Add(@{ name=$Name; status='FAIL'; detail="$_" }); throw }
}
function Picker {
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        $windows = @(UI @('list-windows','--json') | ConvertFrom-Json)
        $picker = $windows | Where-Object { $_.className -eq '#32770' -and $_.ownerHwnd -eq [long]$script:mainHwnd } | Select-Object -First 1
        if ($picker) { return [string]$picker.hwnd }
        Start-Sleep -Milliseconds 100
    }
    throw 'Folder picker did not appear.'
}
function PickerButton([string]$Window, [string]$Id) {
    $button = Inspect $Window | Where-Object { $_.type -eq 'Button' -and $_.automationId -eq $Id } | Select-Object -First 1
    if (!$button) { throw "Picker button missing: $Id" }
    UI @('invoke',$button.selector) -Window $Window
}

try {
    if ($AppPid -eq 0) {
        $launch = Invoke-WinAppChecked @('run',(Join-Path $PSScriptRoot '..\ContinuousPlayer'),'--detach','--json') | ConvertFrom-Json
        $AppPid = [int]$launch.ProcessId
        if (!$AppPid) { throw 'Launch did not return an app PID.' }
    }
    $windows = @(UI @('list-windows','--json') | ConvertFrom-Json)
    $main = $windows | Where-Object { $_.processId -eq $AppPid -and $_.ownerHwnd -eq 0 -and $_.className -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1
    if (!$main) { throw 'Main app window not found.' }
    $script:mainHwnd = [string]$main.hwnd
    $initial = Inspect
    if ($initial.automationId -contains 'CloseButton') { UI @('invoke','CloseButton') | Out-Null }
    $initial = Inspect
    if ($initial.automationId -contains 'PlayerHome') { UI @('invoke','PlayerHome') | Out-Null }
    elseif ($initial.automationId -contains 'BrowserCancel') { UI @('invoke','BrowserCancel') | Out-Null }
    Case 'Home controls and screenshot' { Wait 'ChooseFolder'; Wait 'ExitHome'; UI @('focus','ChooseFolder'); Capture '01-home' }
    Case 'Folder browser opens without launching a picker' {
        UI @('invoke','ChooseFolder'); Wait 'BrowserList'
        UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','False','-t','2000')
        Capture '02-library'
    }
    Case 'Add source and cancel native picker' {
        UI @('click','フォルダーを追加'); Wait 'PC / ローカルフォルダー'; Capture '03-sources'
        UI @('click','PC / ローカルフォルダー'); $pickerWindow = Picker; PickerButton $pickerWindow '2'
        UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','3000')
        Wait 'PC / ローカルフォルダー'
    }
    $mediaPath = & (Join-Path $PSScriptRoot 'New-TestMedia.ps1')
    Case 'Native picker opens local media and excludes AppleDouble' {
        UI @('click','PC / ローカルフォルダー'); $pickerWindow = Picker
        UI @('set-value','1152', $mediaPath) -Window $pickerWindow; PickerButton $pickerWindow '1'
        Wait 'RegisterFolder'; Wait 'Sample OP.wav'
        $items = @(Inspect | Where-Object { $_.type -eq 'ListItem' })
        $names = @($items | ForEach-Object { ($_.name -split '。')[0] })
        if (($names -join '|') -ne 'Sample OP.wav|Sample ED.wav|Sample OP2.wav|Sample ED2.wav') { throw "Unexpected media order: $($names -join '|')" }
        Capture '04-files'
    }
    Case 'Register local folder and reopen from library' {
        UI @('invoke','RegisterFolder'); Wait 'ui-media'; Capture '05-registered'
        UI @('click','ui-media'); Wait 'Sample OP.wav'
    }
    Case 'Keyboard Enter starts playback and Space pauses' {
        UI @('send-keys','enter','--target','Sample OP.wav','--via','send-input'); Wait 'VideoPlayer'
        Start-Sleep -Milliseconds 800
        UI @('send-keys','space','--via','send-input'); Wait 'Resume'; Capture '06-paused'
    }
    Case 'Toolbar arrows move focus; surface arrows select files while paused' {
        UI @('send-keys','right','--via','send-input')
        UI @('wait-for','PlaybackInfo','-p','HasKeyboardFocus','--value','True','-t','3000')
        UI @('focus','TouchSurface')
        UI @('send-keys','right','--via','send-input'); Wait 'Sample ED.wav'; Wait 'Resume'
    }
    Case 'Touch swipes move next and previous while paused' {
        UI @('touch','VideoPlayer','-g','swipe','--direction','left','--distance','240'); Wait 'Sample OP2.wav'
        UI @('touch','VideoPlayer','-g','swipe','--direction','right','--distance','240'); Wait 'Sample ED.wav'
    }
    Case 'Center touch resumes and pauses' {
        UI @('touch','VideoPlayer','-g','tap'); UI @('wait-for','Resume','--gone','-t','3000')
        Start-Sleep -Milliseconds 1000 # Deliberate separate taps, outside the double-tap window.
        UI @('touch','VideoPlayer','-g','tap'); Wait 'Resume'
    }
    Case 'Right-zone taps seek and advance at the end' {
        $player = Inspect | Where-Object { $_.automationId -eq 'VideoPlayer' } | Select-Object -First 1
        $point = '{0},{1}' -f [int]($player.x + $player.width * .85), [int]($player.y + $player.height * .5)
        for ($i=0; $i -lt 5; $i++) { UI @('touch','--at',$point,'-g','tap'); Start-Sleep -Milliseconds 1000 }
        Wait 'Sample OP2.wav'; Wait 'Resume'
    }
    Case 'Keyboard toolbar navigation and media information' {
        UI @('focus','Resume'); UI @('send-keys','tab enter','--via','send-input'); Wait 'CloseButton'
        $text = (Inspect | Where-Object { $_.type -eq 'Text' } | ForEach-Object { $_.name }) -join "`n"
        if ($text -notmatch '8,?000 Hz' -or $text -notmatch 'Bit-Perfect') { throw 'Audio metadata missing.' }
        Capture '07-info'; UI @('invoke','CloseButton')
    }
    Case 'Escape returns to source folder and restores current file focus' {
        UI @('click','Resume'); UI @('send-keys','esc','--via','send-input'); Wait 'BrowserList'; Wait 'Sample OP2.wav'
        UI @('wait-for','Sample OP2.wav','-p','HasKeyboardFocus','--value','True','-t','3000')
        Capture '08-returned'
    }
    Case 'Offline-safe unregister confirmation does not delete media' {
        UI @('invoke','BrowserBack'); Wait 'RemoveFolder'
        $savedItem = Inspect | Where-Object { $_.type -eq 'ListItem' -and $_.name.StartsWith('ui-media。') } | Select-Object -First 1
        if (!$savedItem) { throw 'Saved test folder was not found.' }
        UI @('invoke',$savedItem.selector,'--action','select'); UI @('invoke','RemoveFolder'); Wait 'PrimaryButton'
        UI @('invoke','PrimaryButton'); UI @('wait-for','ui-media','--gone','-t','3000')
        if (!(Test-Path -LiteralPath (Join-Path $mediaPath 'Sample OP.wav'))) { throw 'Media was removed.' }
        UI @('invoke','BrowserCancel'); Wait 'VideoPlayer'
        UI @('send-keys','h','--via','send-input'); Wait 'ChooseFolder'; Capture '09-final-home'
    }
} catch {
    if (!$results.Count -or $results[$results.Count-1].status -ne 'FAIL') { $results.Add(@{ name='Suite execution'; status='FAIL'; detail="$_" }) }
    Write-Host "FAIL $_"
} finally {
    try { Invoke-WinAppChecked @('ui','yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
}
$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{ target='local'; appPid=$AppPid; passed=$results.Count-$failed; failed=$failed; results=@($results.ToArray()); screenshots=@($screenshots.ToArray()) }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
Write-Host "Passed: $($report.passed); Failed: $failed; Report: $ArtifactDirectory\results.json"
if ($failed) { exit 1 }
