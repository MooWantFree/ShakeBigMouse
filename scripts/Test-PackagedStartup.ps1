param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\ShakeBigMouse.exe'),
    [switch]$KeepRunning,
    [switch]$ShowCommand
)
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (Get-Process ShakeBigMouse,ShakeToFindCursor -ErrorAction SilentlyContinue) {
    throw 'Exit the running cursor utility before testing startup.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CursorStartupProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor,Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct IconInfo { public bool Icon; public int X,Y; public IntPtr Mask,Color; }
    [StructLayout(LayoutKind.Sequential)] public struct Bitmap { public int Type,Width,Height,WidthBytes; public ushort Planes,Bits; public IntPtr Data; }
    [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetPhysicalCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,int x,int y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr module,int id);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr cursor,out IconInfo info);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr bitmap,int size,out Bitmap info);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action,uint param,IntPtr value,uint flags);
    public static int Width() {
        IconInfo info;
        if(!GetIconInfo(LoadCursor(IntPtr.Zero,32512),out info)) throw new Exception("Missing arrow");
        try {
            Bitmap bitmap;
            if(GetObject(info.Color!=IntPtr.Zero?info.Color:info.Mask,Marshal.SizeOf<Bitmap>(),out bitmap)==0)
                throw new Exception("Missing arrow bitmap");
            return bitmap.Width;
        } finally { DeleteObject(info.Mask); DeleteObject(info.Color); }
    }
}
'@
$saved = [CursorStartupProbe+Point]::new()
if (![CursorStartupProbe]::GetPhysicalCursorPos([ref]$saved)) { throw 'Cannot save pointer position.' }
$oldContext = [CursorStartupProbe]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$appProcess = $null
$commandProcess = $null
$passed = $false
try {
    if ($ShowCommand) {
        $commandProcess = Start-Process -FilePath $ExePath -ArgumentList '--show' -PassThru -WindowStyle Hidden
        if (!$commandProcess.WaitForExit(5000) -or $commandProcess.ExitCode -ne 0) {
            throw '--show must exit without launching a tray app when no instance is running.'
        }
        $commandProcess.Dispose()
        $commandProcess = $null
    }
    $monitor = [CursorStartupProbe]::MonitorFromPoint($saved, 2)
    $monitorInfo = [CursorStartupProbe+MonitorInfo]::new()
    $monitorInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf($monitorInfo)
    if (![CursorStartupProbe]::GetMonitorInfo($monitor, [ref]$monitorInfo)) { throw 'Cannot read monitor.' }
    $centerX = [int](($monitorInfo.Work.Left + $monitorInfo.Work.Right) / 2)
    $centerY = [int](($monitorInfo.Work.Top + $monitorInfo.Work.Bottom) / 2)
    [void][CursorStartupProbe]::SetPhysicalCursorPos($centerX, $centerY)
    $baseline = [CursorStartupProbe]::Width()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $appProcess = Start-Process -FilePath $ExePath -PassThru -WindowStyle Hidden
    if (!$appProcess.WaitForInputIdle(10000)) { throw 'Application did not become responsive.' }
    $inputAt = $watch.ElapsedMilliseconds
    $firstAt = $null
    $peak = $baseline
    if ($ShowCommand) {
        $commandProcess = Start-Process -FilePath $ExePath -ArgumentList '--show' -PassThru -WindowStyle Hidden
    }
    $samples = if ($ShowCommand) { 375 } else { 64 }
    for ($i = 0; $i -lt $samples; $i++) {
        if (!$ShowCommand) {
            $direction = if ([int][Math]::Floor($i / 4) % 2 -eq 0) { 1 } else { -1 }
            [CursorStartupProbe]::mouse_event(1, 100 * $direction, 0, 0, [UIntPtr]::Zero)
        }
        Start-Sleep -Milliseconds 8
        $width = [CursorStartupProbe]::Width()
        if ($width -gt $baseline -and $null -eq $firstAt) { $firstAt = $watch.ElapsedMilliseconds }
        $peak = [Math]::Max($peak, $width)
    }
    if ($null -eq $firstAt) { throw 'Startup trigger did not enlarge the desktop cursor.' }
    if ($ShowCommand -and (!$commandProcess.WaitForExit(5000) -or $commandProcess.ExitCode -ne 0)) {
        throw '--show did not exit cleanly after notifying the primary instance.'
    }
    $deadline = $watch.ElapsedMilliseconds + $(if ($ShowCommand) { 45000 } else { 15000 })
    do {
        Start-Sleep -Milliseconds 25
        $width = [CursorStartupProbe]::Width()
        $peak = [Math]::Max($peak, $width)
    } while ($width -ne $baseline -and $watch.ElapsedMilliseconds -lt $deadline)
    if ($width -ne $baseline) { throw "Shrink ended at $width px instead of $baseline px." }
    if ($appProcess.HasExited) { throw 'Application exited during the test.' }
    $passed = $true
    [pscustomobject]@{
        Executable = $ExePath; PID = $appProcess.Id; Baseline = $baseline; Peak = $peak; Restored = $width
        Trigger = if ($ShowCommand) { '--show' } else { 'shake' }
        InputStartedAfterLaunchMs = $inputAt; FirstVisibleAfterLaunchMs = $firstAt
        FirstVisibleAfterInputMs = $firstAt - $inputAt
    } | ConvertTo-Json
}
finally {
    if ($commandProcess) {
        if (!$commandProcess.HasExited) { Stop-Process -Id $commandProcess.Id }
        $commandProcess.Dispose()
    }
    if ($appProcess -and (!$KeepRunning -or !$passed)) {
        if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
        [void][CursorStartupProbe]::SystemParametersInfo(0x57, 0, [IntPtr]::Zero, 0)
    }
    [void][CursorStartupProbe]::SetPhysicalCursorPos($saved.X, $saved.Y)
    [void][CursorStartupProbe]::SetThreadDpiAwarenessContext($oldContext)
}
