# Local packaged-app regression for playback focus and window-level shortcuts.
param(
    [string]$VideoPath = (Join-Path $PSScriptRoot '..\.tools\cursor-media\Cursor test.mp4'),
    [switch]$LayoutOnly,
    [switch]$MaximizeOnly,
    [switch]$OriginalMedia,
    [ValidateRange(50,10000)][int]$StartupDelayMilliseconds = 1600,
    [string]$RegisteredRoot,
    [ValidateRange(0,200)][int]$CaptionClickMilliseconds = 80,
    [switch]$LongTitle
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\windowed-keyboard\$id"
$fixture=Join-Path $repo ".tools\windowed-keyboard\$id"
New-Item -ItemType Directory -Force -Path $artifacts,$fixture | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="windowed-keyboard-$id"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=[IntPtr]::Zero
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments,[string]$Window){$target=if($Window){@('-w',$Window)}elseif($hwnd -ne [IntPtr]::Zero){@('-w',"$hwnd")}else{@('-a',"$appPid")};Checked (@('ui')+$Arguments+$target)}
function Nodes($items){foreach($item in $items){if($null -ne $item){$item;Nodes $item.children}}}
function Inspect{$t=UI @('inspect','--json','-d','40') | ConvertFrom-Json;@(Nodes @($t.windows | ForEach-Object {$_.elements}))}
function Fresh([string]$Id){
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do{$item=Inspect | Where-Object {$_.automationId -eq $Id -or $_.name -eq $Id} | Select-Object -First 1;if($item){return $item};Start-Sleep -Milliseconds 100}while($clock.Elapsed.TotalSeconds -lt 10)
    throw "Element missing after fresh inspections: $Id"
}
function Keys([string]$Value){UI @('send-keys',$Value,'--via','send-input') | Out-Null}
function Focused([string]$Id){UI @('wait-for',$Id,'-p','HasKeyboardFocus','--value','True','-t','3000') | Out-Null}
function ClickBrowserPath([string]$Path){
    foreach($page in 1..30){
        $row=Inspect | Where-Object {$_.automationId -eq ('BrowserItem:'+$Path) -and !$_.isOffscreen} | Select-Object -First 1
        if($row){UI @('click',$row.selector) | Out-Null;return}
        Keys 'pagedown'
    }
    throw "Registered browser path not found: $Path"
}
function Paused{UI @('wait-for','Resume','-p','Name','--value','再生','-t','3000') | Out-Null}
function Title([string]$Expected){UI @('wait-for','PART_TitleText','--value',$Expected,'-t','5000') | Out-Null}
function Capture([string]$Name){$path=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$path) | Out-Null;if(!(Test-Path -LiteralPath $path) -or !(Get-Item -LiteralPath $path).Length){throw 'Screenshot missing.'};$screenshots.Add($path)}
function CaptureChrome([string]$Name,[bool]$Visible){
    # The default fixture has dark video frames. Check the white title and icons directly,
    # rather than using the OS cursor, whose native resource can lag behind XAML visibility.
    Capture $Name
    AssertChromeImage $screenshots[$screenshots.Count-1] $Visible
    AssertChromeImage $screenshots[$screenshots.Count-1] $true $script:titleBarRegions
}
function AssertChromeImage([string]$Path,[bool]$Visible,[object[]]$Regions=$script:chromeRegions){
    $frame=[WindowedKeyboardNative]::CaptureBounds($hwnd)
    $bitmap=[System.Drawing.Bitmap]::new($Path)
    try{
        foreach($region in $Regions){
            $bright=0
            $left=[math]::Max(0,[int]($region.x-$frame.left));$top=[math]::Max(0,[int]($region.y-$frame.top))
            $right=[math]::Min($bitmap.Width,$left+[math]::Min(200,[int]$region.width));$bottom=[math]::Min($bitmap.Height,$top+[int]$region.height)
            for($y=$top;$y -lt $bottom;$y++){for($x=$left;$x -lt $right;$x++){$p=$bitmap.GetPixel($x,$y);if($p.R -gt 210 -and $p.G -gt 210 -and $p.B -gt 210){$bright++}}}
            if(($Visible -and $bright -lt 20) -or (!$Visible -and $bright -gt 10)){throw "Chrome visibility differs in $Path for $($region.automationId): expected=$Visible, brightPixels=$bright"}
        }
    }finally{$bitmap.Dispose()}
}
function CheckTitleBar {
    $items=Inspect
    $bar=$items | Where-Object {$_.automationId -eq 'AppTitleBar'} | Select-Object -First 1
    $player=$items | Where-Object {$_.automationId -eq 'VideoPlayer'} | Select-Object -First 1
    $caption=$items | Where-Object {$_.automationId -eq 'Minimize'} | Select-Object -First 1
    $scale=[WindowedKeyboardNative]::Scale($hwnd)
    $commands=@($items | Where-Object {$_.automationId -in @('PlayerHome','PlaybackFolder','PlaybackInfo','AudioOutputSettings','WindowFullscreen')})
    if(!$bar -or !$player -or !$caption -or $commands.Count -ne 5){throw 'Title-bar commands unavailable.'}
    if([math]::Abs($bar.height-32*$scale) -gt 1 -or [math]::Abs($bar.height-$caption.height) -gt 1 -or
        [math]::Abs($bar.y-$caption.y) -gt 1){throw 'Title bar does not match the standard caption height.'}
    foreach($button in $commands){
        if([math]::Abs($button.width-32*$scale) -gt 1 -or [math]::Abs($button.height-28*$scale) -gt 1 -or
            [math]::Abs(($button.y+$button.height/2)-($caption.y+$caption.height/2)) -gt 1.5 -or
            $button.x -lt $bar.x -or $button.y -lt $bar.y -or
            $button.x+$button.width -gt $caption.x-8*$scale+1 -or
            $button.x+$button.width -gt $bar.x+$bar.width+1 -or $button.y+$button.height -gt $player.y+1){throw "Command not inside title bar: $($button.automationId)"}
    }
    $title=$items | Where-Object {$_.automationId -eq 'PART_TitleText'} | Select-Object -First 1
    if(!$title -or $title.y -ge $player.y){throw 'Playback title remained over the video.'}
    $firstCommand=$commands | Sort-Object x | Select-Object -First 1
    if($title.x+$title.width -gt $firstCommand.x-1){throw 'Playback title overlaps title-bar commands.'}
    $subtitle=$items | Where-Object {$_.automationId -eq 'PART_SubtitleText' -and !$_.isOffscreen} | Select-Object -First 1
    if($subtitle -and $subtitle.x+$subtitle.width -gt $firstCommand.x-1){throw 'Playback status overlaps title-bar commands.'}
    if($title.height -lt 20*$scale -or $title.y -lt $bar.y -or $title.y+$title.height -gt $bar.y+$bar.height+1){throw 'Larger title text is missing or clipped vertically.'}
}
function CheckInitialVideoViewport {
    $items=Inspect
    $bar=$items | Where-Object {$_.automationId -eq 'AppTitleBar'} | Select-Object -First 1
    $player=$items | Where-Object {$_.automationId -eq 'VideoPlayer'} | Select-Object -First 1
    if(!$bar -or !$player -or $player.height -le 0){throw 'Video viewport unavailable.'}
    if($player.y -lt $bar.y+$bar.height-1){throw 'Video viewport overlaps the title bar.'}
    if([math]::Abs($player.width/$player.height-16.0/9.0) -gt 0.005){throw "Video viewport is not 16:9: $($player.width) x $($player.height)"}
}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});try{Capture 'failure';UI @('inspect','--json','-d','40') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-tree.json') -Encoding utf8}catch{};throw}}
function Information{
    Keys 'i';$null=Fresh 'MediaInfoText'
    $v=UI @('get-value','MediaInfoText','--json') | ConvertFrom-Json
    if($v.text -notmatch '再生位置: (\d{2}:\d{2}:\d{2})'){throw 'No playback position.'}
    $seconds=[TimeSpan]::Parse($Matches[1]).TotalSeconds
    Keys 'i';UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
    return $seconds
}
$cursorScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Cursor.ps1') -Raw
$native=[regex]::Match($cursorScript,"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value.Replace('NativeCursorTest','WindowedKeyboardNative')
if(!$native){throw 'Native input helper missing.'}
$extra=@'
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr w,uint m,IntPtr p,IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr w,uint attribute,out Rect r,int size);
    public static Rect CaptureBounds(IntPtr w){if(DwmGetWindowAttribute(w,9,out var r,Marshal.SizeOf<Rect>())!=0)throw new Exception("No capture bounds.");return r;}
    public static void Close(IntPtr w){PostMessageW(w,0x10,IntPtr.Zero,IntPtr.Zero);}
    public static void ClickPoint(IntPtr w,int x,int y){Guard(w);var old=SetCursorDpiContext(new IntPtr(-4));try{if(!GetWindowRect(w,out var r)||x<r.left||x>=r.right||y<r.top||y>=r.bottom)throw new Exception("Click outside target window.");if(!SetCursorPos(x,y))throw new Exception("Move to click failed.");var inputs=new[]{new Input{type=0,u=new Union{mouse=new Mouse{flags=2}}},new Input{type=0,u=new Union{mouse=new Mouse{flags=4}}}};if(SendInput(2,inputs,Marshal.SizeOf<Input>())!=2)throw new Exception("Click failed.");Thread.Sleep(100);}finally{if(old!=IntPtr.Zero)SetCursorDpiContext(old);}}
    public static void MoveOutside(IntPtr w){Guard(w);var old=SetCursorDpiContext(new IntPtr(-4));try{if(!GetWindowRect(w,out var r)||!SetCursorPos(r.left>20?r.left-16:r.right+16,(r.top+r.bottom)/2))throw new Exception("Move outside failed.");if(!GetPhysicalCursorPos(out var p)||(p.x>=r.left&&p.x<r.right&&p.y>=r.top&&p.y<r.bottom))throw new Exception("Pointer remained inside window.");}finally{if(old!=IntPtr.Zero)SetCursorDpiContext(old);}}
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr w);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr w);
    public static void DoubleClickCaption(IntPtr w,int timing=80){Guard(w);var old=SetCursorDpiContext(new IntPtr(-4));try{if(!GetWindowRect(w,out var r))throw new Exception("No window bounds.");double scale=GetDpiForWindow(w)/96.0;int x=r.left+(int)(80*scale),y=r.top+(int)(16*scale);if(!SetCursorPos(x,y))throw new Exception("Move to caption failed.");var down=new[]{new Input{type=0,u=new Union{mouse=new Mouse{flags=2}}}};var up=new[]{new Input{type=0,u=new Union{mouse=new Mouse{flags=4}}}};for(int i=0;i<2;i++){if(SendInput(1,down,Marshal.SizeOf<Input>())!=1)throw new Exception("Caption down failed.");Thread.Sleep(timing);if(SendInput(1,up,Marshal.SizeOf<Input>())!=1)throw new Exception("Caption up failed.");Thread.Sleep(timing);}}finally{if(old!=IntPtr.Zero)SetCursorDpiContext(old);}}
    public static double Scale(IntPtr w){return GetDpiForWindow(w)/96.0;}
    public static void Resize(IntPtr w,int width,int height){Guard(w);var old=SetCursorDpiContext(new IntPtr(-4));try{double scale=GetDpiForWindow(w)/96.0;if(!SetWindowPos(w,IntPtr.Zero,0,0,(int)(width*scale),(int)(height*scale),0x16))throw new Exception("Resize failed.");}finally{if(old!=IntPtr.Zero)SetCursorDpiContext(old);}}
'@
$native=$native.Insert($native.LastIndexOf('}'),$extra)
Add-Type $native
$recording=$null
$originalLink=$null
try{
    if(!(Test-Path -LiteralPath $VideoPath -PathType Leaf)){throw 'A test video of at least 60 seconds is required.'}
    if($LongTitle -and !$LayoutOnly){throw 'LongTitle requires LayoutOnly.'}
    if($RegisteredRoot){
        if(!$MaximizeOnly){throw 'RegisteredRoot requires MaximizeOnly.'}
        $startupFileName=Split-Path -Leaf $VideoPath
    }elseif($OriginalMedia){
        if(!$MaximizeOnly){throw 'OriginalMedia requires MaximizeOnly.'}
        $startupFileName=Split-Path -Leaf $VideoPath
        $originalLink=Join-Path $fixture 'Original'
        New-Item -ItemType Junction -Path $originalLink -Target (Split-Path -Parent $VideoPath) | Out-Null
    }else{
        $startupFileName=if($LongTitle){('長いタイトル表示確認_ABC123_'*5)+'OP.mp4'}elseif($MaximizeOnly){'Sample OP'+[IO.Path]::GetExtension($VideoPath)}else{'Sample OP.mp4'}
        Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $fixture $startupFileName)
        Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $fixture 'Sample ED.mp4')
    }
    $launchArguments=if($MaximizeOnly){@('run',(Join-Path $repo 'ContinuousPlayer'),'--detach','--json')}else{@('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach','--json')}
    $launch=Checked $launchArguments | ConvertFrom-Json
    $appPid=[int]$launch.ProcessId;if(!$appPid){throw 'No local process.'}
    UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    $hwnd=[WindowedKeyboardNative]::Main($appPid);if($hwnd -eq [IntPtr]::Zero){throw 'No main window.'}
    UI @('invoke','ChooseFolder') | Out-Null
    if($RegisteredRoot){
        $rootRow=Fresh ('BrowserItem:local:'+$RegisteredRoot);UI @('click',$rootRow.selector) | Out-Null
        $relative=[IO.Path]::GetRelativePath($RegisteredRoot,(Split-Path -Parent $VideoPath))
        if($relative.StartsWith('..') -or [IO.Path]::IsPathRooted($relative)){throw 'Video is outside registered root.'}
        $folderPath=$RegisteredRoot
        if($relative -ne '.') {foreach($part in $relative.Split([IO.Path]::DirectorySeparatorChar)){$folderPath=Join-Path $folderPath $part;ClickBrowserPath $folderPath}}
        ClickBrowserPath $VideoPath
    }else{
    Keys 'end';UI @('click','フォルダーを追加') | Out-Null;UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker=[IntPtr]::Zero
    for($i=0;$i -lt 40 -and $picker -eq [IntPtr]::Zero;$i++){$picker=[WindowedKeyboardNative]::Picker($hwnd);if($picker -eq [IntPtr]::Zero){Start-Sleep -Milliseconds 100}}
    if($picker -eq [IntPtr]::Zero){throw 'No native picker.'}
    [WindowedKeyboardNative]::SetForegroundWindow($picker) | Out-Null;Start-Sleep -Milliseconds 200
    [WindowedKeyboardNative]::SelectFolder($picker,$fixture)
    $register=Fresh 'RegisterFolder';UI @('invoke',$register.selector) | Out-Null
    $script:registeredFixtureName=Split-Path -Leaf $fixture;$script:testFixtureWasRegistered=$true
    $registration=Fresh $script:registeredFixtureName;UI @('click',$registration.selector) | Out-Null
    if($OriginalMedia){$original=Fresh 'Original';UI @('click',$original.selector) | Out-Null}
    $file=Fresh $startupFileName;UI @('click',$file.selector) | Out-Null
    }
    # Do not traverse the MediaPlayerElement UIA provider while video is playing.
    Start-Sleep -Milliseconds $StartupDelayMilliseconds
    if(!$LayoutOnly){
    Case 'Startup caption double-click maximizes and restores without stopping playback' {
        [WindowedKeyboardNative]::DoubleClickCaption($hwnd,$CaptionClickMilliseconds);Start-Sleep -Milliseconds 1800
        if(![WindowedKeyboardNative]::IsZoomed($hwnd)){throw 'Caption double-click did not maximize.'}
        Capture '00-maximized-playing'
        Start-Sleep -Milliseconds 1200;Capture '00-maximized-next-frame'
        $first=Information;Start-Sleep -Milliseconds 1800;$second=Information
        if($second-$first -lt 1){throw "Playback stopped after maximizing: $first -> $second seconds."}
        Write-Host "Maximized playback position: $first -> $second seconds."
        [WindowedKeyboardNative]::DoubleClickCaption($hwnd,$CaptionClickMilliseconds);Start-Sleep -Milliseconds 1000
        if([WindowedKeyboardNative]::IsZoomed($hwnd)){throw 'Caption double-click did not restore.'}
        $first=Information;Start-Sleep -Milliseconds 1800;$second=Information
        if($second-$first -lt 1){throw "Playback stopped after restoring: $first -> $second seconds."}
        Write-Host "Restored playback position: $first -> $second seconds."
    }
    }
    if(!$MaximizeOnly){
    [WindowedKeyboardNative]::KeyPress($hwnd,32)
    Case 'Windowed Space pauses without moving keyboard focus off the video' {
        Paused;Focused 'TouchSurface';$null=Fresh 'WindowFullscreen'
        $items=Inspect
        $script:chromeRegions=@($items | Where-Object {$_.automationId -in @('SeekBackward','SeekForward','Resume')})
        if($script:chromeRegions.Count -ne 3){throw 'Overlay bounds unavailable.'}
        $script:titleBarRegions=@($items | Where-Object {$_.automationId -in @('PART_TitleText','PlayerHome','PlaybackFolder','PlaybackInfo')})
        if($script:titleBarRegions.Count -ne 4){throw 'Title-bar bounds unavailable.'}
        CheckTitleBar
        CheckInitialVideoViewport
        CaptureChrome '01-paused-surface' $true
    }
    if(!$LayoutOnly){
    Case 'Clicking the title and empty title bar preserves playback shortcuts' {
        UI @('click','PART_TitleText') | Out-Null
        Keys 'right';Title 'Sample ED';Paused;Focused 'TouchSurface'
        $bar=Fresh 'AppTitleBar'
        $blankX=[int]($bar.x+$bar.width/2);$blankY=[int]($bar.y+$bar.height/2)
        [WindowedKeyboardNative]::ClickPoint($hwnd,$blankX,$blankY)
        Keys 'left';Title 'Sample OP';Paused;Focused 'TouchSurface'
        UI @('focus','WindowFullscreen') | Out-Null
        [WindowedKeyboardNative]::ClickPoint($hwnd,$blankX,$blankY)
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Start-Sleep -Milliseconds 200
        CaptureChrome '01-titlebar-click-resumed' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused;Focused 'TouchSurface'
    }
    Case 'Paused arrows select files and seek without manually refocusing the video' {
        Keys 'right';Title 'Sample ED';Paused;Focused 'TouchSurface'
        Keys 'left';Title 'Sample OP';Paused
        Keys 'down';$position=Information;if([math]::Abs($position-10) -gt 2){throw "Down did not seek ten seconds: $position"}
        Focused 'TouchSurface';Keys 'up';$position=Information;if($position -gt 2){throw "Up did not seek back: $position"}
        Capture '02-arrows-and-seek'
    }
    Case 'Information shortcut reaches the page from the window title bar' {
        UI @('focus','WindowFullscreen') | Out-Null;Focused 'WindowFullscreen'
        Keys 'i';$null=Fresh 'MediaInfoText';Capture '03-titlebar-information'
        Keys 'i';UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null;Focused 'TouchSurface'
    }
    Case 'F11 works from title-bar focus and returning to windowed playback restores surface focus' {
        UI @('focus','WindowFullscreen') | Out-Null;Keys 'f11';$null=Fresh 'PlaybackFullscreen'
        $items=Inspect;$player=$items | Where-Object {$_.automationId -eq 'VideoPlayer'} | Select-Object -First 1
        $info=$items | Where-Object {$_.automationId -eq 'PlaybackInfo'} | Select-Object -First 1
        if(!$info -or !$player -or $info.y -lt $player.y){throw 'Fullscreen controls did not return to the video.'};Capture '04-fullscreen-overlay'
        Keys 'f11';$null=Fresh 'WindowFullscreen';Focused 'TouchSurface';Paused;Capture '04-returned-window'
        CheckTitleBar
        CheckInitialVideoViewport
    }
    Case 'Explicit keyboard focus on playback buttons still owns arrows and Enter' {
        UI @('focus','PlaybackFolder') | Out-Null;Keys 'right';Focused 'PlaybackInfo'
        Keys 'enter';$null=Fresh 'MediaInfoText';Keys 'i'
        UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null;Focused 'TouchSurface'
    }
    Case 'Title-bar information and file buttons respond to physical clicks' {
        UI @('click','PlaybackInfo') | Out-Null;$null=Fresh 'MediaInfoText';Keys 'i'
        UI @('wait-for','MediaInfoText','--gone','-t','3000') | Out-Null
        UI @('click','PlaybackFolder') | Out-Null;$null=Fresh 'BrowserList';Capture '04-titlebar-file-list'
        UI @('invoke','BrowserCancel') | Out-Null;Paused
        UI @('click','WindowFullscreen') | Out-Null;$null=Fresh 'PlaybackFullscreen'
        UI @('click','PlaybackFullscreen') | Out-Null;$null=Fresh 'WindowFullscreen';Paused
    }
    Case 'Space resumes and pauses again while retaining surface keyboard controls' {
        [WindowedKeyboardNative]::Move($hwnd)
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Start-Sleep -Milliseconds 150
        CaptureChrome '05-keyboard-resumed-hidden' $false
        Start-Sleep -Milliseconds 500
        CaptureChrome '05-keyboard-still-hidden' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused;Focused 'TouchSurface';Capture '05-repeat-space'
    }
    Case 'Playing seek and next-file keys keep overlays hidden until mouse activity' {
        [WindowedKeyboardNative]::KeyPress($hwnd,32)
        [WindowedKeyboardNative]::KeyPress($hwnd,40);Start-Sleep -Milliseconds 150
        CaptureChrome '06-keyboard-seek-hidden' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,39);Start-Sleep -Milliseconds 1400
        CaptureChrome '07-keyboard-next-hidden' $false
        [WindowedKeyboardNative]::Move($hwnd,50);Start-Sleep -Milliseconds 200
        CaptureChrome '08-mouse-revealed' $true
        [WindowedKeyboardNative]::KeyPress($hwnd,40);Start-Sleep -Milliseconds 150
        CaptureChrome '09-keyboard-dismissed' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused;Title 'Sample ED';CaptureChrome '10-paused-visible' $true
    }
    Case 'Clicking resume also dismisses the pause overlay immediately' {
        UI @('click','Resume') | Out-Null;Start-Sleep -Milliseconds 150
        CaptureChrome '11-mouse-resumed-hidden' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused;Focused 'TouchSurface'
    }
    Case 'Windowed title and all three commands remain visible after idle outside the window' {
        [WindowedKeyboardNative]::KeyPress($hwnd,32)
        [WindowedKeyboardNative]::MoveOutside($hwnd)
        Start-Sleep -Milliseconds 3400
        CaptureChrome '11-idle-outside-titlebar' $false
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused
    }
    Case 'Left and Right file transitions never flash chrome in captured frames' {
        [WindowedKeyboardNative]::Move($hwnd)
        [WindowedKeyboardNative]::KeyPress($hwnd,32)
        Start-Sleep -Milliseconds 300
        $video=Join-Path $artifacts 'file-transitions.mp4'
        $recording=Start-Process -FilePath (Get-Command winapp).Source -ArgumentList @('ui','record','-w',"$hwnd",'--duration-sec','6','--fps','30','--max-edge','1280','--frames','--output',$video,'--json') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $artifacts 'recording.json') -RedirectStandardError (Join-Path $artifacts 'recording-error.log') -PassThru
        Start-Sleep -Milliseconds 1000
        foreach($n in 1..3){
            [WindowedKeyboardNative]::KeyPress($hwnd,37);Start-Sleep -Milliseconds 450
            [WindowedKeyboardNative]::KeyPress($hwnd,39);Start-Sleep -Milliseconds 450
        }
        if(!$recording.WaitForExit(15000)){throw 'Transition recording did not finish.'}
        if($recording.ExitCode -ne 0 -or !(Test-Path -LiteralPath $video)){throw 'Transition recording failed.'}
        $frameRoot=Join-Path $artifacts 'file-transitions.frames'
        $frames=@(Get-ChildItem -LiteralPath $frameRoot -Filter '*.jpg' -Recurse | Sort-Object FullName)
        $manifest=Get-Content -LiteralPath (Join-Path $frameRoot 'manifest.json') -Raw | ConvertFrom-Json
        # The recorder deduplicates identical images while retaining every sample in the index.
        if($manifest.status -ne 'complete' -or $manifest.timing.sampleCount -lt 150 -or !$frames.Count){throw 'Incomplete transition evidence.'}
        foreach($frame in $frames){AssertChromeImage $frame.FullName $false}
        Write-Host "Checked $($manifest.timing.sampleCount) samples ($($frames.Count) distinct images) across six file transitions."
        [WindowedKeyboardNative]::KeyPress($hwnd,32);Paused;Title 'Sample ED'
    }
    }
    Case 'Title-bar actions remain available at minimum window size' {
        [WindowedKeyboardNative]::Resize($hwnd,440,380);Start-Sleep -Milliseconds 400;CheckTitleBar;Capture '12-minimum-titlebar'
        [WindowedKeyboardNative]::Resize($hwnd,1024,576);Start-Sleep -Milliseconds 400
        CheckTitleBar
    }
    Case 'Title drag retains playback shortcuts and Home keeps the app title and commands visible' {
        $title=Fresh 'PART_TitleText';$before=[WindowedKeyboardNative]::CaptureBounds($hwnd)
        $x=[int]($title.x+40);$y=[int]($title.y+$title.height/2)
        UI @('drag',"$x,$y","$($x+60),$($y+40)") | Out-Null
        $after=[WindowedKeyboardNative]::CaptureBounds($hwnd)
        if([math]::Abs($after.left-$before.left) -lt 20){throw 'Playback title did not drag the window.'}
        if(!$LongTitle){
            Keys 'left';Title 'Sample OP';Paused;Focused 'TouchSurface'
            Keys 'right';Title 'Sample ED';Paused;Focused 'TouchSurface'
        }
        UI @('click','PlayerHome') | Out-Null;$null=Fresh 'ChooseFolder'
        $items=Inspect
        foreach($command in @('PlayerHome','PlaybackFolder','PlaybackInfo')){
            $button=$items | Where-Object {$_.automationId -eq $command -and !$_.isOffscreen} | Select-Object -First 1
            if(!$button){throw "Home title-bar command hidden: $command"}
            if($button.isEnabled){throw "Home title-bar command should be disabled without media: $command"}
        }
        Title 'ContinuousPlayer'
        Capture '13-home-titlebar'
    }
    }
}catch{if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"});try{Capture 'setup-failure'}catch{}};Write-Host "FAIL $_"}
finally{
    if($recording -and !$recording.HasExited){$recording.WaitForExit(15000) | Out-Null}
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){Remove-TestFixtureRegistration}}catch{$results.Add(@{name='Fixture cleanup';status='FAIL';detail="$_"})}
    if($hwnd -ne [IntPtr]::Zero -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){[WindowedKeyboardNative]::Close($hwnd)}
    if($originalLink -and (Test-Path -LiteralPath $originalLink)){
        $junction=Get-Item -LiteralPath $originalLink -Force
        if($junction.LinkType -ne 'Junction' -or !$junction.FullName.StartsWith($fixture+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected original-media link during cleanup.'}
        Remove-Item -LiteralPath $junction.FullName -Force
    }
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release UI workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object {$_.status -eq 'FAIL'}).Count
@{target='local';appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray());remainingTestRegistration=$script:registeredFixtureName} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
