// Studio 效果的 Windows 部分：录制时记鼠标轨迹（每秒 120 次）、点击和按键时刻、指针图案；合成在 StudioCore.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenRecorder
{
    static class Clock
    {
        public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    // 录的时候：画面里不录鼠标，这里另记一份；所有坐标都是物理像素（程序按 per-monitor DPI 运行）
    sealed class CursorTracker
    {
        readonly Func<Rectangle?> areaOf;      // 录的范围（屏幕物理像素）；窗口会被拖动，每 0.1 秒重新问一次
        readonly double ppp;
        readonly object gate = new object();
        readonly List<double[]> samples = new List<double[]>();   // [秒, u, v（占录制范围的比例）, 图案, 左键]
        readonly List<double[]> clicks = new List<double[]>();    // [秒, u, v]
        readonly List<double> keys = new List<double>();
        readonly Dictionary<IntPtr, int> shapeOf = new Dictionary<IntPtr, int>();
        readonly List<CursorShape> shapes = new List<CursorShape>();
        readonly List<byte[]> pngs = new List<byte[]>();
        readonly IntPtr arrowHandle = LoadCursor(IntPtr.Zero, (IntPtr)32512);   // IDC_ARROW
        Rectangle area;
        Thread sampler, hooks;
        uint hookThreadId;
        volatile bool running;
        LowLevelProc mouseProc, keyProc;       // 留着引用，别被回收
        public readonly List<string> Seen = new List<string>();   // 自测时看系统报的指针状态

        public CursorTracker(Func<Rectangle?> area, double pixelsPerPoint) { areaOf = area; ppp = pixelsPerPoint; }

        public void Start()
        {
            running = true;
            area = areaOf() ?? Rectangle.Empty;
            sampler = new Thread(SampleLoop) { IsBackground = true, Name = "studio-cursor", Priority = ThreadPriority.AboveNormal };
            hooks = new Thread(HookLoop) { IsBackground = true, Name = "studio-hooks" };
            sampler.Start(); hooks.Start();
        }

        public void Stop()
        {
            running = false;
            sampler?.Join(1000);
            if (hookThreadId != 0) PostThreadMessage(hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT
            hooks?.Join(1000);
        }

        void SampleLoop()
        {
            timeBeginPeriod(1);
            try
            {
                double next = Clock.Now; int tick = 0;
                while (running)
                {
                    if (tick % 12 == 0) { var a = areaOf(); if (a.HasValue) lock (gate) area = a.Value; }
                    tick++;
                    Sample();
                    next += 1.0 / 120;
                    double wait = next - Clock.Now;
                    if (wait > 0.0015) Thread.Sleep((int)(wait * 1000));
                    else if (wait < -0.1) next = Clock.Now;   // 卡住了就别追
                }
            }
            finally { timeEndPeriod(1); }
        }

        void Sample()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci)) return;
            double now = Clock.Now;
            // 有指针图案就画（同 Mac 版）；系统因为没接鼠标 / 在用触摸而不画指针（CURSOR_SUPPRESSED）时画标准箭头，
            // 点到哪看得清；程序自己把指针藏起来（没有图案）才不画
            int shape = ci.hCursor != IntPtr.Zero ? ShapeIndex(ci.hCursor) : (ci.flags & 3) != 0 ? ShapeIndex(arrowHandle) : -1;
            if (Seen.Count < 8) { var key = $"flags={ci.flags} cursor={(ci.hCursor == IntPtr.Zero ? "none" : ci.hCursor == arrowHandle ? "arrow" : "other")}"; if (!Seen.Contains(key)) Seen.Add(key); }
            bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            lock (gate)
            {
                if (area.Width <= 0 || area.Height <= 0) return;
                samples.Add(new[] { now, (ci.ptScreenPos.X - area.X + 0.5) / area.Width, (ci.ptScreenPos.Y - area.Y + 0.5) / area.Height, shape, left ? 1 : 0 });
            }
        }

        // 每种指针（箭头、手、文字光标…）存一张图，编号记进轨迹
        int ShapeIndex(IntPtr h)
        {
            if (h == IntPtr.Zero) return -1;
            if (shapeOf.TryGetValue(h, out int i)) return i;
            i = shapes.Count;
            var cs = new CursorShape { File = $"cursor-{i}.png", Arrow = h == arrowHandle };
            byte[] png = null;
            try
            {
                if (GetIconInfo(h, out ICONINFO info))
                {
                    cs.HotX = info.xHotspot / ppp; cs.HotY = info.yHotspot / ppp;
                    if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                    if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                }
                using (var icon = Icon.FromHandle(h))
                using (var bmp = icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                    cs.W = bmp.Width / ppp; cs.H = bmp.Height / ppp;
                }
            }
            catch { cs.Arrow = true; }
            shapeOf[h] = i; shapes.Add(cs); pngs.Add(png);
            return i;
        }

        // 低级鼠标 / 键盘钩子：点击的准确时刻和位置；按键只记「按了」的时间，不记是哪个键
        void HookLoop()
        {
            hookThreadId = GetCurrentThreadId();
            mouseProc = (code, w, l) =>
            {
                if (code >= 0)
                {
                    int msg = w.ToInt32();
                    if (msg == 0x0201 || msg == 0x0204 || msg == 0x0207)   // 左 / 右 / 中键按下
                    {
                        var p = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                        double now = Clock.Now;
                        lock (gate) if (area.Width > 0) clicks.Add(new[] { now, (p.pt.X - area.X + 0.5) / area.Width, (p.pt.Y - area.Y + 0.5) / area.Height });
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            };
            keyProc = (code, w, l) =>
            {
                if (code >= 0 && (w.ToInt32() == 0x0100 || w.ToInt32() == 0x0104)) { double now = Clock.Now; lock (gate) keys.Add(now); }   // WM_KEYDOWN / WM_SYSKEYDOWN
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            };
            var mod = GetModuleHandle(null);
            var hm = SetWindowsHookEx(14, mouseProc, mod, 0);   // WH_MOUSE_LL
            var hk = SetWindowsHookEx(13, keyProc, mod, 0);     // WH_KEYBOARD_LL
            while (running && GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
            if (hm != IntPtr.Zero) UnhookWindowsHookEx(hm);
            if (hk != IntPtr.Zero) UnhookWindowsHookEx(hk);
        }

        // 写 track.json 和指针图案；start = 原始画面第一帧的时间（同一时钟），width/height = 原始画面像素
        public void Save(string dir, double start, int width, int height)
        {
            double R(double v, double k) => Math.Round(v * k) / k;
            var t = new Track { Width = width, Height = height, PixelsPerPoint = ppp };
            lock (gate)
            {
                t.Moves = samples.Select(s => new[] { R(s[0] - start, 1000), R(s[1] * width, 10), R(s[2] * height, 10), s[3], s[4] }).ToList();
                t.Clicks = clicks.Select(c => new[] { R(c[0] - start, 1000), R(c[1] * width, 10), R(c[2] * height, 10) }).ToList();
                t.Keys = keys.Select(k => R(k - start, 1000)).ToList();
            }
            t.Cursors = shapes;
            for (int i = 0; i < shapes.Count; i++) if (pngs[i] != null) File.WriteAllBytes(Path.Combine(dir, shapes[i].File), pngs[i]);
            File.WriteAllText(Path.Combine(dir, "track.json"), t.ToJson());
        }

        // 录制范围在屏幕上的位置（物理像素）和缩放倍数
        public static Func<Rectangle?> AreaOf(Source src)
        {
            switch (src.Kind)
            {
                case SourceKind.Window:
                    var h = src.Hwnd;
                    return () =>
                    {
                        if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return null;   // DWMWA_EXTENDED_FRAME_BOUNDS：不含阴影
                        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    };
                default:
                    var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == src.DeviceName) ?? System.Windows.Forms.Screen.PrimaryScreen;
                    var b = screen.Bounds;
                    var rect = src.Kind == SourceKind.Region ? new Rectangle(b.X + src.Area.X, b.Y + src.Area.Y, src.Area.Width, src.Area.Height) : b;
                    return () => rect;
            }
        }

        public static double ScaleOf(Source src)
        {
            try
            {
                if (src.Kind == SourceKind.Window) return GetDpiForWindow(src.Hwnd) / 96.0;
                var a = AreaOf(src)() ?? Rectangle.Empty;
                var mon = MonitorFromPoint(new POINT { X = a.X + a.Width / 2, Y = a.Y + a.Height / 2 }, 2);
                if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) return dx / 96.0;
            }
            catch { }
            return 1;
        }

        // 读录到的指针图（合成时用）
        public static Sprite LoadCursor(string file, CursorShape c, double ppp)
        {
            if (!File.Exists(file)) return null;
            using (var bmp = new Bitmap(file))
            {
                int w = bmp.Width, h = bmp.Height;
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
                bmp.UnlockBits(data);
                double sppp = c.W > 0 ? w / c.W : ppp;
                return Sprites.FromBgra(bytes, w, h, sppp, c.HotX * sppp, c.HotY * sppp);
            }
        }

        delegate IntPtr LowLevelProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    }

    // 一次 Studio 录制：原始画面 + 轨迹进「Studio 素材\<名字>\」，录完生成成片
    sealed class StudioProject
    {
        public string Dir, Raw, Out;
        public CursorTracker Tracker;

        public static string Folder
        {
            get { var d = Path.Combine(App.Folder, "Studio 素材"); Directory.CreateDirectory(d); return d; }
        }

        public static StudioProject Create(string name, string outPath, Source src)
        {
            var dir = Path.Combine(Folder, name);
            Directory.CreateDirectory(dir);
            return new StudioProject { Dir = dir, Raw = Path.Combine(dir, "raw.mp4"), Out = outPath, Tracker = new CursorTracker(CursorTracker.AreaOf(src), CursorTracker.ScaleOf(src)) };
        }

        // 录完：存轨迹（时间对齐到原始画面第一帧），生成成片
        public RenderStats Finish(double firstFrameAt, Action<double> progress, Action<string> log = null)
        {
            Tracker.Stop();
            var info = Studio.Probe(Raw);
            Tracker.Save(Dir, firstFrameAt, info.Width, info.Height);
            return Studio.Render(Dir, Out, new StudioStyle(), progress, CursorTracker.LoadCursor, log);
        }

        public static bool Available => File.Exists(Studio.FfmpegPath);
    }
}
