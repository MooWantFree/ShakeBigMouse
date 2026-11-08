param([Parameter(Mandatory)][int]$ProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class CursorBenchmark
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Monitor { public int Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Icon { public bool IsIcon; public int X,Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] struct Bitmap { public int Type, Width, Height, Stride; public ushort Planes, Bits; public IntPtr Data; }
    [DllImport("user32")] static extern bool GetPhysicalCursorPos(out Point p);
    [DllImport("user32")] static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref Monitor info);
    [DllImport("user32")] static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [DllImport("user32")] static extern int GetSystemMetrics(int metric);
    [DllImport("user32")] static extern IntPtr LoadCursor(IntPtr module, int role);
    [DllImport("user32")] static extern bool GetIconInfo(IntPtr cursor, out Icon icon);
    [DllImport("gdi32")] static extern int GetObject(IntPtr handle, int size, out Bitmap bitmap);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32")] static extern uint GetGuiResources(IntPtr process, uint kind);

    [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern IntPtr WindowFromPoint(Point pt);
    [DllImport("user32")] static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] static extern IntPtr DispatchMessage(ref Msg msg);
    [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr hwnd; public uint msg; public UIntPtr wp; public IntPtr lp; public uint time; public Point pt; public uint extra; }
    static void Wait(int ms)
    {
        var timer = Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds < ms)
        {
            Msg msg;
            while(PeekMessage(out msg,IntPtr.Zero,0,0,1)) DispatchMessage(ref msg);
            Thread.Sleep(Math.Min(8, Math.Max(1,ms-(int)timer.ElapsedMilliseconds)));
        }
    }

    static int Width()
    {
        Icon i;
        if (!GetIconInfo(LoadCursor(IntPtr.Zero, 32512), out i)) throw new Exception("Missing shared arrow");
        try { Bitmap b; GetObject(i.Color != IntPtr.Zero ? i.Color : i.Mask, Marshal.SizeOf(typeof(Bitmap)), out b); return b.Width; }
        finally { DeleteObject(i.Mask); DeleteObject(i.Color); }
    }

    public static void Run(int id)
    {
        using (var p = Process.GetProcessById(id))
        {
            var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Point saved;
            if (!GetPhysicalCursorPos(out saved)) throw new Exception("Missing pointer");
            var previous = GetForegroundWindow();
            IntPtr window = IntPtr.Zero;
            try
            {
                var m = new Monitor { Size = Marshal.SizeOf(typeof(Monitor)) };
                if (!GetMonitorInfo(MonitorFromPoint(saved, 2), ref m)) throw new Exception("Missing monitor");
                int x = (m.Work.Left + m.Work.Right)/2, y = (m.Work.Top + m.Work.Bottom)/2;
                int dx = Math.Min(700, (m.Work.Right - m.Work.Left)/4);
                int vx=GetSystemMetrics(76), vy=GetSystemMetrics(77), vw=GetSystemMetrics(78), vh=GetSystemMetrics(79);
                Action<int> move = pos => mouse_event(0xc001, (int)((pos-vx)*65535L/(vw-1)), (int)((y-vy)*65535L/(vh-1)), 0, UIntPtr.Zero);
                window = CreateWindowEx(0x88,"#32770","Cursor performance measurement",0x10C00000,
                    x-dx-60,y-100,2*dx+120,200,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                if(window==IntPtr.Zero) throw new Exception("Missing benchmark window");
                SetForegroundWindow(window);
                move(x); Wait(1000);
                if(GetForegroundWindow()!=window && WindowFromPoint(new Point {X=x,Y=y})==window)
                {
                    mouse_event(6,0,0,0,UIntPtr.Zero);
                    Wait(200);
                }
                Console.WriteLine("Benchmark foreground={0}, pointer on benchmark={1}",GetForegroundWindow()==window,WindowFromPoint(new Point {X=x,Y=y})==window);
                int baseline = Width();
                for (int phase = -2; phase <= 5; phase++)
                {
                    int peak = baseline;
                    p.Refresh();
                    var start = p.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    if (phase == -2 || phase == 5) Wait(12000);
                    else if (phase == -1)
                    {
                        for (int i=0; i<500; i++) { move(x + (int)(100*Math.Sin(i/90.0))); Wait(8); }
                    }
                    else
                    {
                        for (int i=0; i<80; i++)
                        {
                            int step = i%8;
                            move(x - dx + dx*(step<=4 ? step : 8-step)/2);
                            Wait(8);
                            if (i%2 == 0) peak = Math.Max(peak, Width());
                        }
                        Wait(1200);
                        if (peak <= baseline || Width() != baseline) throw new Exception("Shake did not reach peak and return: " + peak + "/" + Width());
                    }
                    p.Refresh();
                    Console.WriteLine("{0},{1:F0},{2:F2},{3:F1},{4:F1},{5},{6},{7}", phase,clock.Elapsed.TotalMilliseconds,
                        (p.TotalProcessorTime-start).TotalMilliseconds,p.WorkingSet64/1048576.0,p.PrivateMemorySize64/1048576.0,
                        GetGuiResources(p.Handle,0), GetGuiResources(p.Handle,1),peak);
                }
            }
            finally { if(window!=IntPtr.Zero) DestroyWindow(window); SetForegroundWindow(previous); SetPhysicalCursorPos(saved.X,saved.Y); SetThreadDpiAwarenessContext(old); }
        }
    }
}
"@
'phase(-2 idle,-1 motion,0 warmup,1-4 shake,5 settled idle),wallMs,cpuMs,workingMiB,privateMiB,GDI,USER,peakPx'
[CursorBenchmark]::Run($ProcessId)
