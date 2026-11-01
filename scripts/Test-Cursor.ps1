param(
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$AppPid,
    [string]$VideoPath = (Join-Path $PSScriptRoot '..\.tools\cursor-media\Cursor test.mp4'),
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\ui-test-artifacts\cursor')
)
$ErrorActionPreference = 'Stop'
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
if (@(Get-ChildItem -LiteralPath (Split-Path -Parent $VideoPath)).Count -ne 1) {
    throw 'Use a dedicated test folder containing only the test video (at least 60 seconds long).'
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
# Use guarded Win32 input and inspect the OS cursor directly. UIA traversal of the
# playing MediaPlayerElement can crash this environment's UI Automation provider.
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
public static class NativeCursorTest {
    public delegate bool EnumProc(IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct Info { public int size, flags; public IntPtr cursor; public int x,y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { public int isIcon,x,y; public IntPtr mask,color; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int type,width,height,stride; public ushort planes,bpp; public IntPtr bits; }
    [DllImport("gdi32.dll")] static extern int GetObjectW(IntPtr obj,int count,out BitmapInfo bitmap);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll")] static extern int GetBitmapBits(IntPtr bitmap,int count,byte[] bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Union u; }
    [StructLayout(LayoutKind.Explicit)] struct Union { [FieldOffset(0)] public Key key; [FieldOffset(0)] public Mouse mouse; }
    [StructLayout(LayoutKind.Sequential)] struct Key { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int dx,dy; public uint data,flags,time; public UIntPtr extra; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr w, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref Info i);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr w, out Rect r);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint {public int x,y;}
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out ScreenPoint p);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr w, uint cmd);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr w);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr w, uint m, IntPtr p, string s);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr w);
    static string Class(IntPtr w) { var s=new StringBuilder(256); GetClassName(w,s,s.Capacity); return s.ToString(); }
    public static IntPtr Main(int pid) { IntPtr result=IntPtr.Zero; EnumWindows((w,l)=> { GetWindowThreadProcessId(w,out uint p); if(p==pid && Class(w)=="WinUIDesktopWin32WindowClass" && GetWindow(w,4)==IntPtr.Zero) result=w; return true; },IntPtr.Zero); return result; }
    public static IntPtr Picker(IntPtr owner) { IntPtr result=IntPtr.Zero; EnumWindows((w,l)=> { if(Class(w)=="#32770" && GetWindow(w,4)==owner && IsWindowVisible(w)) result=w; return true; },IntPtr.Zero); return result; }
    public static void Guard(IntPtr w) { if (GetForegroundWindow()!=w) throw new Exception("Target lost foreground"); }
    public static void KeyPress(IntPtr w, ushort vk) { Guard(w); var down=new[]{new Input { type=1,u=new Union{key=new Key{vk=vk}} }}; var up=new[]{new Input { type=1,u=new Union{key=new Key{vk=vk,flags=2}} }}; if(SendInput(1,down,Marshal.SizeOf<Input>())!=1) throw new Exception("Key down failed"); Thread.Sleep(80); if(SendInput(1,up,Marshal.SizeOf<Input>())!=1) throw new Exception("Key up failed"); }
    public static void Move(IntPtr w,int dx=0) { Guard(w); GetWindowRect(w,out var r); if(!SetCursorPos((r.left+r.right)/2+dx,(r.top+r.bottom)/2)) throw new Exception("Move failed"); var input=new[]{new Input{type=0,u=new Union{mouse=new Mouse{dx=20,flags=1}}}}; if(SendInput(1,input,Marshal.SizeOf<Input>())!=1) throw new Exception("Mouse input failed"); Thread.Sleep(100); }
    public static string Position() { if(!GetPhysicalCursorPos(out var p)) throw new Exception("Read position failed"); return $"{p.x},{p.y}"; }
    public static void ClickLeft(IntPtr w) { Guard(w); GetWindowRect(w,out var r); SetCursorPos(r.left+(r.right-r.left)/4,(r.top+r.bottom)/2); var a=new[]{new Input{type=0,u=new Union{mouse=new Mouse{flags=2}}},new Input{type=0,u=new Union{mouse=new Mouse{flags=4}}}}; if(SendInput(2,a,Marshal.SizeOf<Input>())!=2) throw new Exception("Click failed"); }
    public static bool Visible(IntPtr w) {
        Guard(w); var i=new Info{size=Marshal.SizeOf<Info>()}; if(!GetCursorInfo(ref i)) throw new Exception("Read failed");
        if((i.flags&1)==0) return false;
        if(!GetIconInfo(i.cursor,out var icon)) throw new Exception("Cursor image read failed");
        try { if(GetObjectW(icon.mask,Marshal.SizeOf<BitmapInfo>(),out var bm)==0) throw new Exception("Cursor bitmap read failed"); var bits=new byte[bm.stride*bm.height]; int count=GetBitmapBits(icon.mask,bits.Length,bits); if(icon.color==IntPtr.Zero && count==bits.Length) { bool empty=true; for(int j=0;j<bits.Length/2;j++) if(bits[j]!=255 || bits[j+bits.Length/2]!=0) empty=false; if(empty) return false; } return true; }
        finally { if(icon.mask!=IntPtr.Zero) DeleteObject(icon.mask); if(icon.color!=IntPtr.Zero) DeleteObject(icon.color); }
    }
    public static void SelectFolder(IntPtr picker,string path) { Guard(picker); IntPtr edit=IntPtr.Zero,button=IntPtr.Zero; EnumChildWindows(picker,(w,l)=> { if(GetDlgCtrlID(w)==1152 && Class(w)=="Edit") edit=w; if(GetDlgCtrlID(w)==1 && Class(w)=="Button" && IsWindowVisible(w)) button=w; return true; },IntPtr.Zero); if(edit==IntPtr.Zero || button==IntPtr.Zero) throw new Exception("Picker controls not found"); SendMessage(edit,0xC,IntPtr.Zero,path); SendMessage(button,0xF5,IntPtr.Zero,null); }
}
'@
if ((Get-Process -Id $AppPid).ProcessName -ne 'ContinuousPlayer') { throw 'Unexpected target app.' }
$w=[NativeCursorTest]::Main($AppPid)
if ($w -eq [IntPtr]::Zero) { throw 'Main window not found.' }
[NativeCursorTest]::SetForegroundWindow($w) | Out-Null
Start-Sleep -Milliseconds 700
function Key([int]$vk) { [NativeCursorTest]::KeyPress($w,$vk); Start-Sleep -Milliseconds 500 }
function Check([string]$name,[bool]$expected) {
    $actual=[NativeCursorTest]::Visible($w)
    $position=[NativeCursorTest]::Position()
    $positionUnchanged=$expected -or $position -eq $script:lastVisiblePosition
    $status=if($actual -eq $expected -and $positionUnchanged) { 'PASS' } else { 'FAIL' }
    $results.Add(@{ name=$name; status=$status; expectedVisible=$expected; actualVisible=$actual; position=$position; positionUnchanged=$positionUnchanged })
    if($expected) { $script:lastVisiblePosition=$position }
    Write-Host "$status $name"
    if($status -eq 'FAIL') { throw "$name failed (visible=$actual, positionUnchanged=$positionUnchanged)" }
}
$results=[Collections.Generic.List[object]]::new()
try {
Key 72; Key 13; Key 35; Key 13; Key 36; Key 13
$picker=[IntPtr]::Zero
for($i=0;$i -lt 40 -and $picker -eq [IntPtr]::Zero;$i++) { $picker=[NativeCursorTest]::Picker($w); if($picker -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 } }
if($picker -eq [IntPtr]::Zero) { throw 'Folder picker not found.' }
[NativeCursorTest]::SetForegroundWindow($picker) | Out-Null
Start-Sleep -Milliseconds 300
[NativeCursorTest]::SelectFolder($picker,(Split-Path -Parent $VideoPath))
Start-Sleep -Milliseconds 800
Key 13
Start-Sleep -Milliseconds 2000
Start-Sleep -Milliseconds 300
[NativeCursorTest]::Move($w)
Start-Sleep -Milliseconds 200
Check 'Move shows' $true
Start-Sleep -Milliseconds 4000
Check 'Idle hides' $false
[NativeCursorTest]::Move($w,50)
Start-Sleep -Milliseconds 200
Check 'Movement restores' $true
Start-Sleep -Milliseconds 4000
Check 'Second idle hides' $false
Key 32
Check 'Pause restores' $true
Start-Sleep -Milliseconds 3500
Check 'Pause keeps visible' $true
Key 32
Start-Sleep -Milliseconds 4000
Check 'Resume hides' $false
Key 73
Check 'Info restores' $true
Key 27
Start-Sleep -Milliseconds 4000
Check 'Close info hides' $false
Key 72
Check 'Home restores' $true
} catch {
    $results.Add(@{ name='Cursor test execution'; status='FAIL'; detail="$_" })
    Write-Host "FAIL $_"
} finally {
    $failed=@($results | Where-Object { $_.status -eq 'FAIL' }).Count
    @{ target='local'; appPid=$AppPid; results=@($results.ToArray()); failed=$failed } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
}
if($failed) { exit 1 }
