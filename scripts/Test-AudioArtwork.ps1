# Local UI acceptance with deterministic embedded FLAC and folder-cover fixtures.
param([string]$FlacPath=(Join-Path $PSScriptRoot '..\.tools\audio-stage3\ui-media\02 96k.flac'),[switch]$ExclusiveAudio)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Migration-TestRegistration.ps1')
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$fixture=Join-Path $repo ".tools\audio-artwork\$id"
$artifacts=Join-Path $repo "ui-test-artifacts\audio-artwork\$id"
New-Item -ItemType Directory -Force -Path $fixture,$artifacts | Out-Null
$priorWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
$env:WINAPP_UI_WORKFLOW_ID="audio-artwork-$id"
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$appPid=0;$hwnd=[IntPtr]::Zero
$results=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[string]]::new()
function Checked([string[]]$Arguments){$r=@(& winapp @Arguments);if($LASTEXITCODE -ne 0){throw "winapp $($Arguments -join ' ') failed: $($r -join "`n")"};$r}
function UI([string[]]$Arguments){Checked (@('ui')+$Arguments+@('-w',"$hwnd"))}
function Nodes($items){foreach($n in $items){if($null -ne $n){$n;Nodes $n.children}}}
function Inspect{$tree=UI @('inspect','--json','-d','40') | ConvertFrom-Json;@(Nodes @($tree.windows | ForEach-Object {$_.elements}))}
function Fresh([string]$Id){foreach($n in 1..40){$item=Inspect | Where-Object {$_.automationId -eq $Id -or $_.name -eq $Id} | Select-Object -First 1;if($item){return $item};Start-Sleep -Milliseconds 100};throw "Missing element: $Id"}
function Keys([string]$Value){UI @('send-keys',$Value,'--via','send-input') | Out-Null}
function Capture([string]$Name){$path=Join-Path $artifacts "$Name.png";UI @('screenshot','--output',$path) | Out-Null;if(!(Test-Path -LiteralPath $path)){throw 'Screenshot missing.'};$screenshots.Add($path)}
function Case([string]$Name,[scriptblock]$Action){try{& $Action | Out-Null;$results.Add(@{name=$Name;status='PASS'});Write-Host "PASS $Name"}catch{$results.Add(@{name=$Name;status='FAIL';detail="$_"});throw}}
function Artwork([bool]$Expected){
    foreach($n in 1..40){
        $image=Inspect | Where-Object {$_.automationId -eq 'AudioArtwork' -and !$_.isOffscreen -and $_.width -gt 0} | Select-Object -First 1
        if($Expected -and $image){return $image}
        if(!$Expected -and !$image){return}
        Start-Sleep -Milliseconds 150
    }
    throw "Artwork visibility differs: expected=$Expected"
}
function OpenTrack([string]$Folder,[string]$Name,[switch]$First){
    if(!$First){Keys 'o';$null=Fresh 'BrowserBack';UI @('invoke','BrowserBack') | Out-Null}
    $folderRow=Fresh $Folder;UI @('click',$folderRow.selector) | Out-Null
    $file=Fresh $Name;UI @('click',$file.selector) | Out-Null
    UI @('wait-for','PART_TitleText','--value',[IO.Path]::GetFileNameWithoutExtension($Name),'-t','5000') | Out-Null
    Keys 'space';UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null
}
function Big32([IO.Stream]$Stream,[uint32]$Value){foreach($shift in 24,16,8,0){$Stream.WriteByte([byte](($Value -shr $shift) -band 255))}}
function EmbedCover([string]$Source,[string]$Image,[string]$Destination){
    # RFC 9639 section 8.8: a front-cover PICTURE block, without changing audio frames.
    $flac=[IO.File]::ReadAllBytes($Source);$picture=[IO.File]::ReadAllBytes($Image)
    if([Text.Encoding]::ASCII.GetString($flac,0,4) -ne 'fLaC'){throw 'Fixture is not FLAC.'}
    $block=[IO.MemoryStream]::new()
    try{
        Big32 $block 3;$mime=[Text.Encoding]::ASCII.GetBytes('image/png');Big32 $block $mime.Length;$block.Write($mime,0,$mime.Length)
        Big32 $block 0;Big32 $block 300;Big32 $block 300;Big32 $block 32;Big32 $block 0;Big32 $block $picture.Length;$block.Write($picture,0,$picture.Length)
        $output=[IO.MemoryStream]::new()
        try{
            $output.Write($flac,0,4);$position=4
            do{
                $last=($flac[$position] -band 128) -ne 0
                $length=([int]$flac[$position+1] -shl 16) -bor ([int]$flac[$position+2] -shl 8) -bor $flac[$position+3]
                if(($flac[$position] -band 127) -ne 6){$output.WriteByte([byte]($flac[$position] -band 127));$output.Write($flac,$position+1,$length+3)}
                $position+=$length+4
            }while(!$last)
            $output.WriteByte(134);foreach($shift in 16,8,0){$output.WriteByte([byte](($block.Length -shr $shift) -band 255))}
            $block.Position=0;$block.CopyTo($output);$output.Write($flac,$position,$flac.Length-$position)
            [IO.File]::WriteAllBytes($Destination,$output.ToArray())
        }finally{$output.Dispose()}
    }finally{$block.Dispose()}
}
$cursor=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Cursor.ps1') -Raw
$native=[regex]::Match($cursor,"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value.Replace('NativeCursorTest','AudioArtworkNative')
$extra=@'
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr w,uint m,IntPtr p,IntPtr l);
    public static void Close(IntPtr w){PostMessageW(w,0x10,IntPtr.Zero,IntPtr.Zero);}
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr w);
    public static void Resize(IntPtr w,int width,int height){Guard(w);var old=SetCursorDpiContext(new IntPtr(-4));try{double scale=GetDpiForWindow(w)/96.0;if(!SetWindowPos(w,IntPtr.Zero,0,0,(int)(width*scale),(int)(height*scale),0x16))throw new Exception("Resize failed.");}finally{if(old!=IntPtr.Zero)SetCursorDpiContext(old);}}
'@
$native=$native.Insert($native.LastIndexOf('}'),$extra)
Add-Type $native
try{
    if(!(Test-Path -LiteralPath $FlacPath)){throw 'A FLAC fixture of at least 10 seconds is required.'}
    foreach($folder in 'Embedded','Folder','None','Broken','Video'){New-Item -ItemType Directory -Path (Join-Path $fixture $folder) | Out-Null}
    EmbedCover ([IO.Path]::GetFullPath($FlacPath)) (Join-Path $repo 'ContinuousPlayer\Assets\Square150x150Logo.scale-200.png') (Join-Path $fixture 'Embedded\Embedded.flac')
    Copy-Item -LiteralPath $FlacPath -Destination (Join-Path $fixture 'Folder\Folder.flac')
    Copy-Item -LiteralPath (Join-Path $repo 'ContinuousPlayer\Assets\Wide310x150Logo.scale-200.png') -Destination (Join-Path $fixture 'Folder\cover.png')
    Copy-Item -LiteralPath $FlacPath -Destination (Join-Path $fixture 'None\None.flac')
    Copy-Item -LiteralPath $FlacPath -Destination (Join-Path $fixture 'Broken\Broken.flac')
    [IO.File]::WriteAllText((Join-Path $fixture 'Broken\cover.png'),'invalid image')
    Copy-Item -LiteralPath (Join-Path $repo '.tools\cursor-media\Cursor test.mp4') -Destination (Join-Path $fixture 'Video\Video.mp4')
    $audioArgument=if($ExclusiveAudio){'--args=--exclusive-audio'}else{'--args=--standard-audio'}
    $launch=Checked @('run',(Join-Path $repo 'ContinuousPlayer'),'--arch','x64','--no-build','--no-restore','--detach',$audioArgument,'--json') | ConvertFrom-Json
    $launch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'launch.json') -Encoding utf8
    $appPid=[int]$launch.ProcessId
    foreach($n in 1..50){$hwnd=[AudioArtworkNative]::Main($appPid);if($hwnd -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}
    if(!$appPid -or $hwnd -eq [IntPtr]::Zero){throw 'No owned app window.'}
    UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','10000') | Out-Null
    UI @('invoke','ChooseFolder') | Out-Null;Keys 'end';UI @('click','フォルダーを追加') | Out-Null;UI @('click','PC / ローカルフォルダー') | Out-Null
    $picker=[IntPtr]::Zero
    foreach($n in 1..40){$picker=[AudioArtworkNative]::Picker($hwnd);if($picker -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}
    if($picker -eq [IntPtr]::Zero){throw 'No folder picker.'}
    [AudioArtworkNative]::SetForegroundWindow($picker) | Out-Null;Start-Sleep -Milliseconds 150
    [AudioArtworkNative]::SelectFolder($picker,$fixture)
    $register=Fresh 'RegisterFolder';UI @('invoke',$register.selector) | Out-Null
    $script:registeredFixtureName=Split-Path -Leaf $fixture;$script:testFixtureWasRegistered=$true
    $row=Fresh $script:registeredFixtureName;UI @('click',$row.selector) | Out-Null
    Case 'Embedded FLAC front cover is displayed' {
        OpenTrack 'Embedded' 'Embedded.flac' -First
        $null=Artwork $true;Capture '01-embedded-flac'
    }
    Case 'Album artwork fits above controls at minimum window size' {
        [AudioArtworkNative]::Resize($hwnd,440,380);Start-Sleep -Milliseconds 300
        $image=Artwork $true;$button=Fresh 'SeekForward';$video=Fresh 'VideoPlayer'
        if($image.y -lt $video.y -or $image.y+$image.height -gt $button.y-2 -or $image.x -lt $video.x -or $image.x+$image.width -gt $video.x+$video.width+1){throw 'Artwork overlaps playback controls or the viewport.'}
        Capture '02-minimum-artwork';[AudioArtworkNative]::Resize($hwnd,1024,624)
    }
    Case 'A folder cover is displayed for a track without embedded art' {
        OpenTrack 'Folder' 'Folder.flac';$null=Artwork $true;Capture '03-folder-cover'
    }
    Case 'Switching from album art to video clears the artwork' {
        Keys 'o';$null=Fresh 'BrowserBack';UI @('invoke','BrowserBack') | Out-Null
        $folderRow=Fresh 'Video';UI @('click',$folderRow.selector) | Out-Null
        $videoRow=Fresh 'Video.mp4';UI @('click',$videoRow.selector) | Out-Null
        Start-Sleep -Milliseconds 1600;[AudioArtworkNative]::KeyPress($hwnd,32)
        UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null
        Artwork $false;Capture '03-video-without-artwork'
    }
    Case 'A coverless track clears the previous artwork' {
        OpenTrack 'None' 'None.flac';Start-Sleep -Milliseconds 1000;Artwork $false;Capture '04-no-artwork'
    }
    Case 'Corrupt artwork keeps the fallback and playback keyboard controls' {
        OpenTrack 'Broken' 'Broken.flac';Start-Sleep -Milliseconds 1000;Artwork $false
        Keys 'space';UI @('wait-for','Resume','-p','Name','--value','一時停止','-t','5000') | Out-Null
        Keys 'space';UI @('wait-for','Resume','-p','Name','--value','再生','-t','5000') | Out-Null;Capture '05-broken-artwork'
    }
}catch{
    if(!$results.Count -or $results[$results.Count-1].status -ne 'FAIL'){$results.Add(@{name='Setup / execution';status='FAIL';detail="$_"})}
    Write-Host "FAIL $_"
    try{Capture 'failure';UI @('inspect','--json','-d','40') | Set-Content -LiteralPath (Join-Path $artifacts 'failure-tree.json') -Encoding utf8}catch{}
}finally{
    try{if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){Remove-TestFixtureRegistration}}catch{$results.Add(@{name='Fixture cleanup';status='FAIL';detail="$_"})}
    if($hwnd -eq [IntPtr]::Zero -and $appPid -gt 0){$hwnd=[AudioArtworkNative]::Main($appPid)}
    if($hwnd -ne [IntPtr]::Zero -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){[AudioArtworkNative]::Close($hwnd)}
    try{Checked @('ui','yield') | Out-Null}catch{$results.Add(@{name='Release workflow';status='FAIL';detail="$_"})}
    $env:WINAPP_UI_WORKFLOW_ID=$priorWorkflow;$env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
$failed=@($results | Where-Object status -eq 'FAIL').Count
@{appPid=$appPid;passed=$results.Count-$failed;failed=$failed;results=@($results.ToArray());screenshots=@($screenshots.ToArray());remainingTestRegistration=$script:registeredFixtureName} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host "Passed: $($results.Count-$failed); Failed: $failed; Report: $artifacts\results.json"
if($failed){exit 1}
