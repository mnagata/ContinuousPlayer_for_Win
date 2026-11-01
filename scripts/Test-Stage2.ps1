# Local input acceptance batch. Uses valid silent media without registering folders.
param()
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage2\$id"
$fixture=Join-Path $repo ".tools\stage2-media\$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="continuousplayer-stage2-$id"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=$null
$results=[Collections.Generic.List[object]]::new()
$observations=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Wait([string]$Id){UI @('wait-for',$Id,'-t','5000') | Out-Null}
function Gone([string]$Id){UI @('wait-for',$Id,'--gone','-t','5000') | Out-Null}
function Keys([string]$Text){UI @('send-keys',$Text,'--via','send-input') | Out-Null}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect([string]$Window){$t=UI @('inspect','--json','-d','12') -Window $Window | ConvertFrom-Json;@(Nodes @($t.windows | ForEach-Object {$_.elements}))}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});throw}}
function Capture([string]$Name){$p=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$p,'--json') | Out-Null;if(!(Test-Path -LiteralPath $p)){throw 'Screenshot missing.'};$screenshots.Add($p)}
function Info([string]$Name){
    Keys 'i';Wait 'MediaInfoText';$v=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if($v.text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})'){throw 'No playback position in information.'}
    $v2=@{name=$Name;file=([string]$v.text -split "`n")[0];seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds}
    $observations.Add($v2);$v2
}
function CloseInfo{Keys 'i';Gone 'MediaInfoText'}
function Near([double]$Expected,[double]$Actual,[double]$Tolerance=1){if([math]::Abs($Actual-$Expected) -gt $Tolerance){throw "Position $Actual, expected $Expected +/- $Tolerance"}}
function VisibleCount{
    $all=Inspect;$list=$all | Where-Object {$_.automationId -eq 'BrowserList'} | Select-Object -First 1
    if(!$list){throw 'List bounds unavailable.'}
    $visible=@($all | Where-Object {$_.type -eq 'ListItem' -and $_.height -gt 0 -and $_.y -lt $list.y+$list.height -and $_.y+$_.height -gt $list.y})
    if(!$visible.Count){throw 'No visible list items.'};$visible.Count
}
function CheckPage([int]$Expected,[string]$Name){
    Keys 'enter';Wait 'VideoPlayer';$value=Info $Name
    $actual=[array]::IndexOf($ordered,[string]$value.file)
    $observations.Add(@{name="$Name-index";expected=$Expected;actual=$actual})
    # UIA can include/exclude a clipped edge item; exact clamping is checked in Core.
    if($actual -lt 0 -or [math]::Abs($actual-$Expected) -gt 1){throw "Page selected index $actual; expected $Expected +/- 1"}
    CloseInfo;Keys 'esc';Wait 'BrowserList';return $actual
}
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class Stage2Native {
 [StructLayout(LayoutKind.Explicit,Size=40)] struct Input { [FieldOffset(0)]public uint Type;[FieldOffset(8)]public Key Key;[FieldOffset(8)]public Mouse Mouse; }
 [StructLayout(LayoutKind.Sequential)] struct Key {public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Sequential)] struct Mouse {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Sequential)] struct Point {public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] struct Message {public IntPtr Hwnd;public uint Msg;public UIntPtr W;public IntPtr L;public uint Time;public Point Point;public uint Private;}
 static HashSet<ushort> owned=new HashSet<ushort>();static IntPtr probe;static Thread probeThread;
 [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,Input[] data,int size);
 [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
 [DllImport("user32.dll")]static extern bool PeekMessageW(out Message m,IntPtr h,uint min,uint max,uint remove);
 [DllImport("user32.dll")]static extern bool TranslateMessage(ref Message m);
 [DllImport("user32.dll")]static extern IntPtr DispatchMessageW(ref Message m);
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")]static extern bool PostMessageW(IntPtr h,uint m,UIntPtr w,IntPtr l);
 static void Send(params Input[] data){if(IntPtr.Size!=8 || SendInput((uint)data.Length,data,40)!=data.Length)throw new Exception("Synthetic input was not delivered.");}
 public static void Down(ushort key){owned.Add(key);Send(new Input{Type=1,Key=new Key{Vk=key}});}
 public static void Up(ushort key){Send(new Input{Type=1,Key=new Key{Vk=key,Flags=2}});owned.Remove(key);}
 public static void Click(int x,int y){SetCursorPos(x,y);Send(new Input{Mouse=new Mouse{Flags=2}},new Input{Mouse=new Mouse{Flags=4}});}
 public static void Release(){foreach(var key in new List<ushort>(owned))Up(key);}
 public static IntPtr OpenProbe(){var ready=new ManualResetEventSlim();probeThread=new Thread(()=>{probe=CreateWindowExW(0,"STATIC","Stage2 activation probe",0x10CF0000,100,100,300,160,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);CreateWindowExW(0,"BUTTON","Activate probe",0x50010000,20,20,220,60,probe,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);ready.Set();while(IsWindow(probe)){Message m;while(PeekMessageW(out m,IntPtr.Zero,0,0,1)){TranslateMessage(ref m);DispatchMessageW(ref m);}Thread.Sleep(5);}});probeThread.IsBackground=true;probeThread.SetApartmentState(ApartmentState.STA);probeThread.Start();if(!ready.Wait(5000)||probe==IntPtr.Zero)throw new Exception("No test focus window.");return probe;}
 public static void CloseProbe(){if(probe!=IntPtr.Zero)PostMessageW(probe,0x10,UIntPtr.Zero,IntPtr.Zero);if(probeThread!=null)probeThread.Join(1000);probe=IntPtr.Zero;}
 public static void Close(IntPtr h){PostMessageW(h,0x10,UIntPtr.Zero,IntPtr.Zero);}
}
'@
try{
    & (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -Directory $fixture -DurationSeconds 120 | Out-Null
    foreach($n in 0..39){Copy-Item -LiteralPath (Join-Path $fixture 'Sample OP.wav') -Destination (Join-Path $fixture ('Track {0:D2}.wav' -f $n))}
    $ordered=@('Sample OP.wav','Sample ED.wav','Sample OP2.wav','Sample ED2.wav')+@(0..39 | ForEach-Object {'Track {0:D2}.wav' -f $_})
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json') | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId;if(!$appPid){throw 'No local app process.'}
    $launch | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'launch.json') -Encoding utf8
    Wait 'ChooseFolder';UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    $main=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.processId -eq $appPid -and $_.ownerHwnd -eq 0} | Select-Object -First 1
    $hwnd=[IntPtr]::new([long]$main.hwnd)
    UI @('focus','ChooseFolder') | Out-Null
    UI @('invoke','ChooseFolder') | Out-Null;Wait 'BrowserList'
    UI @('click','フォルダーを追加') | Out-Null;Wait 'PC / ローカルフォルダー';UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker=$null
    for($i=0;$i -lt 30;$i++){$picker=@(UI @('list-windows','--json') | ConvertFrom-Json) | Where-Object {$_.className -eq '#32770'} | Select-Object -First 1;if($picker){break};Start-Sleep -Milliseconds 100}
    if(!$picker){throw 'No native folder picker.'}
    $pickerHwnd=[string]$picker.hwnd
    UI @('set-value','1152',$fixture) -Window $pickerHwnd | Out-Null
    $button=Inspect $pickerHwnd | Where-Object {$_.type -eq 'Button' -and $_.automationId -eq '1'} | Select-Object -First 1
    if(!$button){throw 'No native picker confirmation button.'};UI @('invoke',$button.selector) -Window $pickerHwnd | Out-Null;Wait 'Sample OP.wav'
    Case 'S19: horizontal pages use the visible list, PageUp returns, ends never wrap' {
        $visible=VisibleCount;Keys 'right';$index=CheckPage $visible 'right-page'
        $visible=VisibleCount;Keys 'vk=0x21';$null=CheckPage ([math]::Max(0,$index-$visible)) 'page-up'
        Keys 'vk=0x23';Keys 'right';$index=CheckPage 43 'last-page';if($index -ne 43){throw 'End wrapped.'}
        Keys 'vk=0x24';Keys 'left';$index=CheckPage 0 'first-page';if($index -ne 0){throw 'Start wrapped.'}
        Capture '01-browser-page'
        Keys 'enter';Wait 'VideoPlayer';Gone 'Resume';Keys 'space';Wait 'Resume'
    }
    Case 'S13: toolbar arrows move focus and Enter activates that button' {
        UI @('focus','Resume') | Out-Null;Keys 'right'
        UI @('wait-for','PlaybackInfo','-p','HasKeyboardFocus','--value','True','-t','3000') | Out-Null
        Capture '02-toolbar-focus';Keys 'enter';Wait 'MediaInfoText';CloseInfo;Wait 'Resume'
    }
    Case 'S13: surface Shift+arrows seek without changing files' {
        UI @('focus','TouchSurface') | Out-Null;Keys 'down down down';$before=Info 'before-shift';CloseInfo
        UI @('focus','TouchSurface') | Out-Null;Keys 'shift+right';$after=Info 'after-shift';Near ($before.seconds+10) $after.seconds
        if($after.file -ne $before.file){throw 'Shift+Right changed files.'};CloseInfo;Wait 'Resume'
    }
    $surface=Inspect | Where-Object {$_.automationId -eq 'TouchSurface'} | Select-Object -First 1
    $leftX=[int]($surface.x+$surface.width*.15);$rightX=[int]($surface.x+$surface.width*.85);$y=[int]($surface.y+$surface.height*.65)
    Case 'S14: left and right mouse clicks toggle without seeking; double click toggles once' {
        $before=Info 'before-mouse';CloseInfo
        [Stage2Native]::Click($leftX,$y);Gone 'Resume';Keys 'space';Wait 'Resume'
        $after=Info 'left-mouse';Near $before.seconds $after.seconds 3;CloseInfo
        [Stage2Native]::Click($rightX,$y);Gone 'Resume';Keys 'space';Wait 'Resume'
        $after=Info 'right-mouse';Near $before.seconds $after.seconds 5;CloseInfo
        UI @('click','TouchSurface','--double') | Out-Null;Gone 'Resume';Keys 'space';Wait 'Resume'
    }
    Case 'S14: a mouse drag cannot send files' {
        UI @('drag',"$leftX,$y","$($leftX+240),$y") | Out-Null;Wait 'Resume'
        $value=Info 'after-mouse-drag';if($value.file -ne 'Sample OP.wav'){throw 'Mouse drag changed files.'};CloseInfo
    }
    Case 'S17: direct-touch double taps seek ten seconds or toggle exactly once' {
        $before=Info 'before-touch-double';CloseInfo
        UI @('touch','--at',"$rightX,$y",'-g','double-tap') | Out-Null;Wait 'Resume'
        $after=Info 'after-touch-double';Near ($before.seconds+10) $after.seconds;CloseInfo
        UI @('touch','TouchSurface','-g','double-tap') | Out-Null;Gone 'Resume';Keys 'space';Wait 'Resume'
    }
    Case 'S16: slow 75 DIP swipes work, and pinch never sends a file or toggles' {
        $distance=[string][int][math]::Ceiling(75*[Stage2Native]::GetDpiForWindow($hwnd)/96)
        UI @('touch','TouchSurface','-g','swipe','--direction','left','--distance',$distance,'--duration-ms','2000') | Out-Null
        Wait 'Sample ED.wav';Wait 'Resume'
        UI @('touch','TouchSurface','-g','swipe','--direction','right','--distance',$distance,'--duration-ms','2000') | Out-Null
        Wait 'Sample OP.wav';Wait 'Resume'
        UI @('touch','TouchSurface','-g','pinch','--distance','180') | Out-Null;Wait 'Resume'
        $value=Info 'after-pinch';if($value.file -ne 'Sample OP.wav'){throw 'Pinch changed files.'};Near 0 $value.seconds;CloseInfo
    }
    Case 'S18: held media Next executes once, release enables a second deliberate press' {
        try{foreach($n in 1..4){[Stage2Native]::Down(0xB0);Start-Sleep -Milliseconds 160}}finally{[Stage2Native]::Up(0xB0)}
        $value=Info 'held-media-next';if($value.file -ne 'Sample ED.wav'){throw "Media hold selected $($value.file)"};CloseInfo
        [Stage2Native]::Down(0xB0);Start-Sleep -Milliseconds 160;[Stage2Native]::Up(0xB0)
        $value=Info 'second-media-press';if($value.file -ne 'Sample OP2.wav'){throw 'Second media press was lost.'};CloseInfo
    }
    Case 'S18: held ordinary Right executes once on the playback surface' {
        UI @('focus','TouchSurface') | Out-Null
        try{foreach($n in 1..4){[Stage2Native]::Down(0x27);Start-Sleep -Milliseconds 100}}finally{[Stage2Native]::Up(0x27)}
        $value=Info 'held-arrow';if($value.file -ne 'Sample ED2.wav'){throw "Arrow hold selected $($value.file)"};CloseInfo
    }
    Case 'S11: information blocks media and letter shortcuts from the player behind it' {
        $value=Info 'modal-before';[Stage2Native]::Down(0xB0);Start-Sleep -Milliseconds 100;[Stage2Native]::Up(0xB0)
        Keys 'h';Keys 'o';Capture '03-information';CloseInfo
        $after=Info 'modal-after';if($value.file -ne $after.file){throw 'Modal input reached playback.'};Near $value.seconds $after.seconds;CloseInfo
    }
    Case 'S15: inactive first click only activates; a second click toggles playback' {
        $probe=[Stage2Native]::OpenProbe()
        try{
            UI @('focus','Activate probe') -Window ([string]$probe.ToInt64()) | Out-Null
            if([Stage2Native]::GetForegroundWindow() -ne $probe){throw 'Probe did not receive foreground.'}
            [Stage2Native]::Click($leftX,$y);Start-Sleep -Milliseconds 150
            if([Stage2Native]::GetForegroundWindow() -ne $hwnd){throw 'First click did not activate app.'}
            Wait 'Resume'
            [Stage2Native]::Click($leftX,$y);Gone 'Resume';Keys 'space';Wait 'Resume'
        }finally{[Stage2Native]::CloseProbe()}
    }
}catch{if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})};Write-Host "FAIL $_"}
finally{
    [Stage2Native]::Release();[Stage2Native]::CloseProbe()
    if($hwnd -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){[Stage2Native]::Close($hwnd)}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());observations=@($observations.ToArray());screenshots=@($screenshots.ToArray());fixture=$fixture;registeredFolders=$false} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
