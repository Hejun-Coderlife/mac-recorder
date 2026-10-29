// 录制时的 W3 部分（照 Mac 版 Camera.swift / Keys.swift）：
//   · 摄像头：用随包的 ffmpeg（DirectShow）开摄像头，屏幕上浮一个能拖的圆形小窗；Studio 模式同时写 camera.mp4（小窗不录进画面），
//     普通录制小窗直接录进画面（录单个窗口时录不到小窗，见 README）；camera.mp4 用分段 MP4，万一被强制结束也读得出来
//   · 键帽：GDI+ 画真字（核心的 KeyLayer.Renderer 指过来）
//   · 勿扰：录制期间临时关掉系统通知横幅（HKCU PushNotifications\ToastEnabled = 0），录完 / 下次打开时换回原值
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    static class CameraDevices
    {
        // DirectShow 里的视频设备名（ffmpeg -list_devices）；没有返回空
        public static List<string> List()
        {
            var names = new List<string>();
            if (!File.Exists(Studio.FfmpegPath)) return names;
            try
            {
                var psi = new ProcessStartInfo(Studio.FfmpegPath, "-hide_banner -list_devices true -f dshow -i dummy")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = System.Text.Encoding.UTF8 };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEnd(); p.WaitForExit(8000);
                    foreach (Match m in Regex.Matches(err, "\"([^\"]+)\"\\s*\\(video\\)")) if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
                }
            }
            catch { }
            return names;
        }
    }

    // 开着的摄像头：一路小预览（给浮窗，240×240、每秒 20 帧），Studio 模式另写 camera.mp4
    sealed class CameraCapture : IDisposable
    {
        public const int PreviewSize = 240;
        Process proc; Thread reader;
        volatile bool stopping;
        public double FirstFrameAt;            // 第一帧到的时刻（Clock 时钟），和屏幕第一帧对齐用
        public string File;                    // 写的 camera.mp4（只预览时是 null）
        public event Action<byte[]> Frame;     // 预览帧：BGRA 240×240
        public string Error;
        public bool Running => proc != null && !proc.HasExited;

        // device：DirectShow 设备名；file = null 只预览。自测：CAMERA_INJECT=视频文件 → 用文件代替摄像头（runner 没有摄像头）
        public static CameraCapture Start(string device, string file)
        {
            var c = new CameraCapture { File = file };
            string inject = Environment.GetEnvironmentVariable("CAMERA_INJECT");
            string input = inject != null ? $"-re -stream_loop -1 -i \"{inject}\"" : $"-f dshow -rtbufsize 256M -i video=\"{device}\"";
            string rec = file != null ? $"-map 0:v -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p -an -movflags +frag_keyframe+empty_moov+default_base_moof \"{file}\" " : "";
            string pv = $"-map 0:v -vf \"fps=20,scale={PreviewSize}:{PreviewSize}:force_original_aspect_ratio=increase,crop={PreviewSize}:{PreviewSize},format=bgra\" -f rawvideo -pix_fmt bgra pipe:1";
            var psi = new ProcessStartInfo(Studio.FfmpegPath, $"-hide_banner -v error -y {input} {rec}{pv}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            c.proc = Process.Start(psi);
            var errs = new System.Text.StringBuilder();
            c.proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errs) { errs.AppendLine(e.Data); c.Error = errs.ToString(); } };
            c.proc.BeginErrorReadLine();
            c.reader = new Thread(c.Read) { IsBackground = true, Name = "camera-preview" };
            c.reader.Start();
            return c;
        }

        void Read()
        {
            try
            {
                var s = proc.StandardOutput.BaseStream;
                int n = PreviewSize * PreviewSize * 4;
                // 一直读到 ffmpeg 退出：停的时候也要把管道里的帧读掉，不然 ffmpeg 写管道堵住、收不到 q，camera.mp4 收不了尾
                while (true)
                {
                    var buf = new byte[n]; int got = 0;
                    while (got < n) { int k = s.Read(buf, got, n - got); if (k <= 0) break; got += k; }
                    if (got < n) break;
                    if (stopping) continue;
                    if (FirstFrameAt == 0) FirstFrameAt = Clock.Now;
                    Frame?.Invoke(buf);
                }
            }
            catch { }
        }

        // 停：给 ffmpeg 发 q（camera.mp4 正常收尾），等它退出
        public void Stop()
        {
            if (proc == null) return;
            stopping = true;
            try { proc.StandardInput.Write("q"); proc.StandardInput.Flush(); } catch { }
            if (!proc.WaitForExit(8000)) try { proc.Kill(); } catch { }
            reader?.Join(3000);
            proc.Dispose(); proc = null;
        }

        public void Dispose() => Stop();
    }

    // 屏幕上的摄像头小窗：无边框、置顶、能拖；右键换形状和大小；位置大小记在设置里
    sealed class CameraBubble : Form
    {
        Bitmap frame;
        readonly object gate = new object();
        bool round = Settings.GetString("camRound", "1") == "1";
        int sizeIdx = Math.Max(0, Math.Min(2, int.TryParse(Settings.GetString("camSize", "1"), out var si) ? si : 1));
        Point downAt; bool dragging;
        bool excluded;

        public CameraBubble()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black; DoubleBuffered = true;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int side = Side;
            int x = int.TryParse(Settings.GetString("camX", ""), out var sx) ? sx : wa.Right - side - 40, y = int.TryParse(Settings.GetString("camY", ""), out var sy) ? sy : wa.Bottom - side - 40;
            var screen = Screen.FromPoint(new Point(x + side / 2, y + side / 2)).WorkingArea;
            Bounds = new Rectangle(Math.Max(screen.Left, Math.Min(x, screen.Right - side)), Math.Max(screen.Top, Math.Min(y, screen.Bottom - side)), side, side);
            var menu = new ContextMenuStrip();
            menu.Items.Add(L("圆形", "Circle"), null, (s, e) => { round = true; Settings.SetString("camRound", "1"); Reshape(); });
            menu.Items.Add(L("圆角方形", "Rounded square"), null, (s, e) => { round = false; Settings.SetString("camRound", "0"); Reshape(); });
            menu.Items.Add(new ToolStripSeparator());
            foreach (var (t, i) in new[] { (L("小", "Small"), 0), (L("中", "Medium"), 1), (L("大", "Large"), 2) })
                menu.Items.Add(t, null, (s, e) => { sizeIdx = i; Settings.SetString("camSize", i.ToString()); var c = new Point(Left + Width / 2, Top + Height / 2); Size = new Size(Side, Side); Location = new Point(c.X - Side / 2, c.Y - Side / 2); Reshape(); });
            ContextMenuStrip = menu;
            Reshape();
        }

        int Side => (int)Math.Round(new[] { 150, 210, 290 }[sizeIdx] * DeviceDpi / 96.0);
        public bool Round => round;

        void Reshape()
        {
            using (var path = new GraphicsPath())
            {
                if (round) path.AddEllipse(0, 0, Width, Height);
                else { int r = (int)(Width * 0.2); path.AddArc(0, 0, r * 2, r * 2, 180, 90); path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90); path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90); path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90); path.CloseFigure(); }
                Region = new Region(path);
            }
            Invalidate();
        }

        // Studio 模式：小窗不录进画面（摄像头另外录，剪辑时再叠）；普通录制：录进画面
        public void ExcludeFromCapture(bool on)
        {
            excluded = on;
            if (IsHandleCreated) SetWindowDisplayAffinity(Handle, on ? 0x11u : 0u);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (excluded) SetWindowDisplayAffinity(Handle, 0x11); }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW

        public void Push(byte[] bgra)
        {
            var bmp = new Bitmap(CameraCapture.PreviewSize, CameraCapture.PreviewSize, PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length); bmp.UnlockBits(bd);
            Bitmap old; lock (gate) { old = frame; frame = bmp; }
            old?.Dispose();
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            lock (gate)
            {
                if (frame == null) { g.Clear(Color.FromArgb(40, 40, 44)); TextRenderer.DrawText(g, L("摄像头…", "Camera…"), Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
                else
                {
                    // 像照镜子：左右翻过来
                    g.TranslateTransform(Width, 0); g.ScaleTransform(-1, 1);
                    g.DrawImage(frame, new Rectangle(0, 0, Width, Height));
                    g.ResetTransform();
                }
            }
            using (var pen = new Pen(Color.White, Math.Max(2, Width / 60f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (round) g.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { downAt = e.Location; dragging = true; } }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) Location = new Point(Left + e.X - downAt.X, Top + e.Y - downAt.Y);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Settings.SetString("camX", Left.ToString()); Settings.SetString("camY", Top.ToString()); }
        }

        // 录完：小窗在录制范围里的位置大小（比例，同 Mac 版 CameraTrack）
        public CameraTrack TrackIn(Rectangle area, double offset)
        {
            double cx = (Left + Width / 2.0 - area.X) / Math.Max(1, area.Width), cy = (Top + Height / 2.0 - area.Y) / Math.Max(1, area.Height);
            return new CameraTrack { Offset = offset, X = Math.Min(1, Math.Max(0, cx)), Y = Math.Min(1, Math.Max(0, cy)), Size = Width / (double)Math.Max(1, Math.Min(area.Width, area.Height)), Round = round };
        }

        protected override void Dispose(bool disposing) { if (disposing) lock (gate) { frame?.Dispose(); frame = null; } base.Dispose(disposing); }
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    }

    // 键帽（GDI+ 画真字）：一块圆角底板托住几个键，键之间一个小「+」，同 Mac 版的样子
    static class KeyCapsGdi
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        public static Sprite Render(string[] keys, bool dark, double px)
        {
            string key = string.Join(" ", keys) + "|" + dark + "|" + (int)px;
            lock (cache) if (cache.TryGetValue(key, out var c)) return c;
            float h = (float)px, gap = h * 0.42f, pad = h * 0.28f, sh = h * 0.3f;
            using (var font = new Font("Segoe UI Semibold", h * 0.42f, GraphicsUnit.Pixel))
            using (var plus = new Font("Segoe UI", h * 0.36f, GraphicsUnit.Pixel))
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                var widths = keys.Select(k => Math.Max(h, pg.MeasureString(k, font, PointF.Empty, StringFormat.GenericTypographic).Width + h * 0.5f)).ToArray();
                int W = (int)Math.Ceiling(widths.Sum() + gap * (keys.Length - 1) + pad * 2 + sh * 2), H = (int)Math.Ceiling(h + pad * 2 + sh * 2);
                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        var plate = new RectangleF(sh, sh, W - sh * 2, H - sh * 2);
                        // 阴影：往下挪一点、几圈半透明
                        for (int i = 4; i >= 1; i--)
                            using (var sp = Theme.Round(new RectangleF(plate.X - i, plate.Y - i + h * 0.06f, plate.Width + i * 2, plate.Height + i * 2), h * 0.42f + i))
                            using (var sb = new SolidBrush(Color.FromArgb(14, 0, 0, 0))) g.FillPath(sb, sp);
                        using (var pp = Theme.Round(plate, h * 0.42f))
                        using (var pb = new SolidBrush(dark ? Color.FromArgb(209, 26, 26, 28) : Color.FromArgb(235, 250, 250, 250))) g.FillPath(pb, pp);
                        var fg = dark ? Color.White : Color.FromArgb(26, 26, 26);
                        float x = plate.X + pad;
                        for (int i = 0; i < keys.Length; i++)
                        {
                            var r = new RectangleF(x, plate.Y + pad, widths[i], h);
                            using (var kp = Theme.Round(r, h * 0.22f))
                            {
                                using (var kb = new SolidBrush(dark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0))) g.FillPath(kb, kp);
                                using (var kl = new Pen(dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0), 1)) g.DrawPath(kl, kp);
                            }
                            using (var tb = new SolidBrush(fg))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString(keys[i], font, tb, r, sf);
                            x += widths[i];
                            if (i < keys.Length - 1)
                                using (var tb = new SolidBrush(Color.FromArgb(160, fg)))
                                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                    g.DrawString("+", plus, tb, new RectangleF(x, plate.Y + pad, gap, h), sf);
                            x += gap;
                        }
                    }
                    var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var bytes = new byte[W * H * 4];
                    for (int y = 0; y < H; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, bytes, y * W * 4, W * 4);
                    bmp.UnlockBits(bd);
                    var s = Sprites.FromBgra(bytes, W, H, 1, 0, 0);
                    lock (cache) { if (cache.Count > 200) cache.Clear(); cache[key] = s; }
                    return s;
                }
            }
        }
    }

    // 录制时勿扰：Windows 没有给普通程序开关「专注 / 勿扰」的公开接口；这里临时关掉通知横幅（Toast），录完换回原值。
    // 原值先记进设置，程序中途崩了下次打开也会换回来
    static class Dnd
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", Value = "ToastEnabled", Flag = "dndRestore";
        public static string KeyPath = Key;   // 自测可以换成别的键

        // 录制前：记下原值（"absent" = 本来没有这个值），设成 0
        public static bool Begin()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    var v = k.GetValue(Value);
                    if (Settings.GetString(Flag, "") == "") Settings.SetString(Flag, v == null ? "absent" : Convert.ToString(v));
                    k.SetValue(Value, 0, RegistryValueKind.DWord);
                }
                Broadcast();
                return true;
            }
            catch { return false; }
        }

        // 录完 / 下次打开：是我们改的才换回来
        public static void Restore()
        {
            var orig = Settings.GetString(Flag, "");
            if (orig == "") return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (orig == "absent") k.DeleteValue(Value, false);
                    else k.SetValue(Value, int.TryParse(orig, out var n) ? n : 1, RegistryValueKind.DWord);
                }
                Broadcast();
            }
            catch { }
            Settings.SetString(Flag, "");
        }

        public static object Current { get { try { using (var k = Registry.CurrentUser.OpenSubKey(KeyPath)) return k?.GetValue(Value); } catch { return null; } } }

        static void Broadcast()
        {
            try { SendMessageTimeout((IntPtr)0xffff, 0x001A, IntPtr.Zero, "PushNotifications", 2, 500, out _); } catch { }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    }
}
