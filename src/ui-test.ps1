# Runs two separate instances against a test folder on the host and checks that they mirror each other.
# $HostKey is the host's SHA256:... fingerprint, $Key the fingerprint of the Pageant key to use.
param([Parameter(Mandatory)][string]$Out, [string]$Exe = "$env:LOCALAPPDATA\SshNotepadBuild\SshNotepad\bin\Debug\net10.0-windows\SshNotepad.exe",
      [Parameter(Mandatory)][string]$HostName, [Parameter(Mandatory)][string]$User,
      [Parameter(Mandatory)][string]$HostKey, [Parameter(Mandatory)][string]$Key)
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Text; using System.Drawing; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Ui {
  delegate bool Cb(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(Cb c, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, Cb c, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  // What a real mouse hit test gives: the strip first, then the form if it says "transparent".
  public static int Hit(IntPtr w, int x, int y) { R r; GetWindowRect(w,out r); IntPtr lp=(IntPtr)(((r.T+y)<<16)|((r.L+x)&0xFFFF)); int a=(int)SendMessage(Strip(w),0x84,IntPtr.Zero,lp); return a==-1 ? (int)SendMessage(w,0x84,IntPtr.Zero,lp) : a; }
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  struct R { public int L, T, Rt, B; }
  public static List<IntPtr> Windows(int pid) { var o = new List<IntPtr>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h, out p); if (p==pid && IsWindowVisible(h) && Title(h)!="") o.Add(h); return true; }, IntPtr.Zero); return o; }
  public static string Title(IntPtr h) { var s=new StringBuilder(400); GetWindowText(h,s,400); return s.ToString(); }
  public static IntPtr Edit(IntPtr w) { IntPtr r=IntPtr.Zero; EnumChildWindows(w,(h,l)=>{ var s=new StringBuilder(200); GetClassName(h,s,200); if (r==IntPtr.Zero && IsWindowVisible(h) && s.ToString().ToUpper().Contains("RICHEDIT")) r=h; return true; }, IntPtr.Zero); return r; }
  public static IntPtr Strip(IntPtr w) { IntPtr r=IntPtr.Zero; R wr; GetWindowRect(w,out wr); EnumChildWindows(w,(h,l)=>{ R c; GetWindowRect(h,out c); int ht=c.B-c.T; if (r==IntPtr.Zero && IsWindowVisible(h) && ht>=38 && ht<=62 && c.T-wr.T<60 && c.Rt-c.L>(wr.Rt-wr.L)-40) r=h; return true; }, IntPtr.Zero); return r; }
  public static void Set(IntPtr w, string t) { SendMessage(Edit(w), 0x0C, IntPtr.Zero, t); }
  public static string Get(IntPtr w) { var s=new StringBuilder(5000); SendMessage(Edit(w), 0x0D, (IntPtr)5000, s); return s.ToString(); }
  public static string Rect(IntPtr w) { R r; GetWindowRect(w,out r); return r.L+","+r.T+","+(r.Rt-r.L)+","+(r.B-r.T); }
  public static void Shot(IntPtr w, string f) { R r; GetWindowRect(w,out r); using(var b=new Bitmap(r.Rt-r.L,r.B-r.T)) { using(var g=Graphics.FromImage(b)) { var d=g.GetHdc(); PrintWindow(w,d,2); g.ReleaseHdc(d); } b.Save(f); } }
}
'@
$run = [guid]::NewGuid().ToString('N').Substring(0, 8)
$folder = "~/.sshnotepad-test/ui-$run"
$procs = @{}
function Start-Pc($name) {
    $env:SSHNOTEPAD_HOME = "$Out\$run-$name"; $env:SSHNOTEPAD_PC = $name
    New-Item -ItemType Directory -Force $env:SSHNOTEPAD_HOME | Out-Null
    $json = @{ Host = $HostName; Port = 22; User = $User; Fingerprint = $Key; RemoteFolder = $folder; HostKeyFingerprint = $HostKey } | ConvertTo-Json
    [IO.File]::WriteAllText("$env:SSHNOTEPAD_HOME\settings.json", $json)
    $script:procs[$name] = Start-Process $exe -PassThru
    foreach ($i in 1..40) { Start-Sleep -Milliseconds 500; $w = [Ui]::Windows($procs[$name].Id); if ($w.Count) { return } }
    throw "$name showed no window"
}
function Get-Win($name) { [Ui]::Windows($procs[$name].Id) | Select-Object -First 1 }
function Test-Until($label, [scriptblock]$cond) {
    foreach ($i in 1..30) { if (& $cond) { "PASS  $label"; return }; Start-Sleep -Milliseconds 500 }
    "FAIL  $label"
}
try {
    Start-Pc OFFICE
    Start-Sleep 3
    [Ui]::Set((Get-Win OFFICE), "Shopping`r`nmilk")
    Test-Until 'title follows first line' { [Ui]::Title((Get-Win OFFICE)) -eq 'Shopping - Notepad' }
    Start-Sleep 5
    Start-Pc HOME
    Test-Until 'HOME opens the same note' { [Ui]::Get((Get-Win HOME)) -match 'milk' }
    "INFO  OFFICE at $([Ui]::Rect((Get-Win OFFICE))), HOME at $([Ui]::Rect((Get-Win HOME)))"
    [Ui]::Set((Get-Win HOME), "Shopping`r`nmilk`r`neggs")
    Test-Until 'HOME edit appears live in OFFICE' { [Ui]::Get((Get-Win OFFICE)) -match 'eggs' }
    # A clash: both change the same note within the save delay.
    [Ui]::Set((Get-Win OFFICE), "Shopping`r`nbread"); [Ui]::Set((Get-Win HOME), "Shopping`r`ncheese")
    Test-Until 'conflict marked in both windows' { [Ui]::Title((Get-Win OFFICE)).StartsWith([string][char]0x26A0) -and [Ui]::Title((Get-Win HOME)).StartsWith([string][char]0x26A0) }
    Test-Until 'both show the same winner' { [Ui]::Get((Get-Win OFFICE)) -eq [Ui]::Get((Get-Win HOME)) }
    Start-Sleep 1
    [Ui]::Shot((Get-Win OFFICE), "$Out\office.png"); [Ui]::Shot((Get-Win HOME), "$Out\home.png")
    # Ctrl+N via the New menu's accelerator isn't sent to a background window, so click the + button.
    foreach ($x in 288, 518) { $s = [Ui]::Strip((Get-Win OFFICE)); foreach ($m in 0x201, 0x202) { [Ui]::PostMessage($s, $m, 0, [IntPtr]((22 -shl 16) -bor $x)) | Out-Null }; Start-Sleep 1; [Ui]::Set((Get-Win OFFICE), "Note at $x`r`nsecond line") }
    Start-Sleep 5
    [Ui]::Shot((Get-Win OFFICE), "$Out\tabs.png")
    $first = [Ui]::Strip((Get-Win OFFICE)); foreach ($m in 0x201, 0x202) { [Ui]::PostMessage($first, $m, 0, [IntPtr]((22 -shl 16) -bor 80)) | Out-Null }
    Test-Until 'clicking a tab switches to it' { [Ui]::Get((Get-Win OFFICE)) -match 'bread|cheese' }
    # Window buttons: HTMAXBUTTON = 9, as the mouse would send them.
    $w = Get-Win OFFICE; $before = [Ui]::Rect($w)
    foreach ($m in 0xA1, 0xA2) { [Ui]::PostMessage($w, $m, [IntPtr]9, 0) | Out-Null }
    Test-Until 'maximize button maximizes' { [Ui]::IsZoomed($w) }
    Start-Sleep 1; [Ui]::Shot($w, "$Out\max.png")
    foreach ($m in 0xA1, 0xA2) { [Ui]::PostMessage($w, $m, [IntPtr]9, 0) | Out-Null }
    Test-Until 'restore returns to the same bounds' { -not [Ui]::IsZoomed($w) -and [Ui]::Rect($w) -eq $before }
    "INFO  before $before, after $([Ui]::Rect($w))"
    "INFO  hit tests: $(foreach ($p in @(500, 2), @(735, 25), @(870, 15), @(825, 15), @(780, 15), @(20, 20), @(120, 25)) { [Ui]::Hit($w, $p[0], $p[1]) }) (expect 12 2 20 9 8 3 1)"
    # Reorder: drag the first tab over the second; then pull it out into a window, and drop it back.
    function Send-Drag($win, [int[][]]$points) {
        $strip = [Ui]::Strip($win); $l = { param($p) [IntPtr](($p[1] -shl 16) -bor ($p[0] -band 0xFFFF)) }
        [Ui]::PostMessage($strip, 0x201, [IntPtr]1, (& $l $points[0])) | Out-Null
        foreach ($p in $points) { [Ui]::PostMessage($strip, 0x200, [IntPtr]1, (& $l $p)) | Out-Null; Start-Sleep -Milliseconds 100 }
        [Ui]::PostMessage($strip, 0x202, 0, (& $l $points[-1])) | Out-Null
        Start-Sleep 1
    }
    Send-Drag $w @(@(80, 22), @(200, 22), @(330, 22))
    [Ui]::Shot($w, "$Out\reordered.png")
    foreach ($m in 0x201, 0x202) { [Ui]::PostMessage([Ui]::Strip($w), $m, 0, [IntPtr]((22 -shl 16) -bor 80)) | Out-Null }
    Test-Until 'dragging reorders tabs' { [Ui]::Get($w) -match 'Note at 288' }
    Send-Drag $w @(@(80, 22), @(120, 100), @(200, 300))
    Test-Until 'dragging a tab out makes a window' { [Ui]::Windows($procs.OFFICE.Id).Count -eq 2 }
    Test-Until 'the new window is mirrored' { [Ui]::Windows($procs.HOME.Id).Count -eq 2 }
    $new = [Ui]::Windows($procs.OFFICE.Id) | Where-Object { [Ui]::Title($_) -match 'Note at 288' } | Select-Object -First 1
    $a = [Ui]::Rect($new).Split(','); $b = [Ui]::Rect($w).Split(',')
    Send-Drag $new @(@(80, 22), @(80, 90), @(([int]$b[0] - [int]$a[0] + 300), ([int]$b[1] - [int]$a[1] + 20)))
    Test-Until 'dropping it on the first window merges it' { [Ui]::Windows($procs.OFFICE.Id).Count -eq 1 }
    Test-Until 'HOME follows' { [Ui]::Windows($procs.HOME.Id).Count -eq 1 }
    # Command line: a second start hands the file to the running instance.
    $file = "$Out\$run-local.txt"; [IO.File]::WriteAllText($file, "This is a speling misstake`r`nfine line")
    $env:SSHNOTEPAD_HOME = "$Out\$run-OFFICE"; $env:SSHNOTEPAD_PC = 'OFFICE'
    Start-Process $exe -ArgumentList "`"$file`"" -Wait
    Test-Until 'command-line file opens in the running app' { [Ui]::Title((Get-Win OFFICE)) -match 'local.txt' -and [Ui]::Get((Get-Win OFFICE)) -match 'speling' }
    Test-Until 'it is mirrored as a note' { [Ui]::Title((Get-Win HOME)) -match 'speling' }
    Start-Sleep 3; [Ui]::Shot((Get-Win OFFICE), "$Out\spell.png")
    [Ui]::Set((Get-Win OFFICE), "changed")
    Test-Until 'unsaved file is marked' { [Ui]::Title((Get-Win OFFICE)).StartsWith([string][char]0x2022) }
    $s = [Ui]::Strip((Get-Win OFFICE)); foreach ($x in 80, 250, 420, 600) { foreach ($m in 0x201, 0x202) { [Ui]::PostMessage($s, $m, 0, [IntPtr]((22 -shl 16) -bor $x)) | Out-Null }; Start-Sleep -Milliseconds 500; if ([Ui]::Get((Get-Win OFFICE)) -match 'bread|cheese') { break } }
    Start-Sleep 9
    "INFO  HOME title before OFFICE closes: $([Ui]::Title((Get-Win HOME)))"
    [Ui]::PostMessage((Get-Win OFFICE), 0x10, 0, 0) | Out-Null
    Test-Until 'closing the last window exits' { $procs.OFFICE.HasExited }
    Start-Pc OFFICE
    Test-Until 'reopening restores the note' { [Ui]::Get((Get-Win OFFICE)) -match 'bread|cheese' }
    "INFO  OFFICE reopened at $([Ui]::Rect((Get-Win OFFICE)))"
    "INFO  HOME title after: $([Ui]::Title((Get-Win HOME)))"
    Test-Until 'HOME kept its window' { -not $procs.HOME.HasExited -and [Ui]::Get((Get-Win HOME)) -match 'bread|cheese' }
}
finally {
    foreach ($p in $procs.Values) { if (-not $p.HasExited) { foreach ($w in [Ui]::Windows($p.Id)) { [Ui]::PostMessage($w, 0x10, 0, 0) | Out-Null }; if (-not $p.WaitForExit(10000)) { "WARN  had to stop $($p.Id)"; Stop-Process $p -Force } } }
    "INFO  remote folder $folder"
}
