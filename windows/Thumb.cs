using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    // 截完 / 录完右下角浮出的缩略图（仿 macOS）：约 5 秒后自己消失，鼠标停在上面就不消失；
    // 拖出去 = 把文件拖进微信、资源管理器、剪辑软件；单击 = 用默认程序打开；悬停时右上角有 ×。
    // 截图：拖走了或点了 × 就不保存，没动它到时间 → 存到桌面。视频：本来就在桌面，× 只是关掉缩略图
    sealed class Thumb : Form
    {
        public static readonly List<Thumb> Open = new List<Thumb>();
        public enum Kind { Image, Video }

        public readonly Kind What;
        public string FilePath { get; private set; }     // 截图：先是临时文件，存到桌面后换成桌面路径
        public string Outcome { get; private set; }      // 自测看结果：saved / dragged / dismissed / opened
        readonly Bitmap preview;
        readonly Bitmap dragBitmap;                      // 截图拖出去时顺带的位图（有的程序只认图片不认文件）
        readonly Timer life = new Timer(), anim = new Timer { Interval = 15 };
        bool hovering, closing;
        Point downAt; bool down;
        int targetX, targetY, startX; int animStep;
        const int AnimSteps = 14;

        static int Seconds
        {
            get
            {
                double.TryParse(Environment.GetEnvironmentVariable("THUMB_SECONDS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s);
                return (int)((s > 0 ? s : 5) * 1000);
            }
        }

        public static Thumb ShowImage(Bitmap shot, string tempPng) => Show_(new Thumb(Kind.Image, tempPng, shot, new Bitmap(shot)));

        // 视频：frame = 预览帧（VideoFrame 在后台线程抽好；null 就画个深色底），视频总是存在桌面
        public static Thumb ShowVideo(string path, Bitmap frame) => Show_(new Thumb(Kind.Video, path, frame, null));

        static Thumb Show_(Thumb t)
        {
            Open.Add(t);
            t.Layout_(true);
            t.Show();
            t.life.Start();
            return t;
        }

        Thumb(Kind kind, string path, Bitmap img, Bitmap drag)
        {
            What = kind; FilePath = path; dragBitmap = drag;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.White;
            Cursor = Cursors.Hand;

            var wa = Screen.PrimaryScreen.WorkingArea;
            float k = DpiOf(wa) / 96f;
            // 宽 220、高按比例（夹在 90～200 之间），太窄太高的图按高度来
            int maxW = (int)(220 * k), maxH = (int)(200 * k), minS = (int)(90 * k);
            int iw = img?.Width ?? 16, ih = img?.Height ?? 9;
            double ar = (double)iw / ih;
            int w = maxW, h = (int)(w / ar);
            if (h > maxH) { h = maxH; w = Math.Max(minS, (int)(h * ar)); }
            if (h < minS) h = minS;
            Size = new Size(w, h);
            preview = Fit(img, w, h);
            img?.Dispose();

            life.Interval = Seconds;
            life.Tick += (s, e) => TimeUp();
            anim.Tick += (s, e) => Step();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8;   // 不抢焦点、不进任务栏 / Alt+Tab、置顶
                cp.ClassStyle |= 0x20000;                 // 阴影
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // Windows 11：系统画圆角（Windows 10 上没有这个属性，就是直角）
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
            if (Shots.Hidden) SetWindowDisplayAffinity(Handle, 0x11);   // 不出现在之后的截图 / 录屏里
        }

        static int DpiOf(Rectangle area)
        {
            try
            {
                var mon = MonitorFromPoint(new POINT { X = area.X + area.Width / 2, Y = area.Y + area.Height / 2 }, 2);
                if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) return (int)dx;
            }
            catch { }
            return 96;
        }

        // 预览图缩到窗口大小（居中铺满、多的裁掉），视频再叠一个播放键
        Bitmap Fit(Bitmap img, int w, int h)
        {
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(What == Kind.Video ? Color.FromArgb(40, 40, 44) : Color.White);
                if (img != null)
                {
                    double s = Math.Max((double)w / img.Width, (double)h / img.Height);
                    int dw = (int)Math.Ceiling(img.Width * s), dh = (int)Math.Ceiling(img.Height * s);
                    g.DrawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
                }
                if (What == Kind.Video)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float r = Math.Min(w, h) * 0.17f, cx = w / 2f, cy = h / 2f;
                    using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
                    var tri = new[] { new PointF(cx - r * 0.35f, cy - r * 0.5f), new PointF(cx - r * 0.35f, cy + r * 0.5f), new PointF(cx + r * 0.55f, cy) };
                    g.FillPolygon(Brushes.White, tri);
                }
                using (var edge = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawRectangle(edge, 0, 0, w - 1, h - 1);
            }
            return b;
        }

        // 位置：主屏工作区右下角，留边距，多张往上叠；first = 从右边滑进来
        void Layout_(bool first)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(16 * DpiOf(wa) / 96f);
            int y = wa.Bottom - m;
            foreach (var t in Open) { if (t == this) break; y -= t.Height + m / 2; }
            targetX = wa.Right - m - Width; targetY = y - Height;
            if (first) { startX = wa.Right; Location = new Point(startX, targetY); animStep = 0; anim.Start(); }
            else if (!anim.Enabled) Location = new Point(targetX, targetY);
        }

        static void Reflow() { foreach (var t in Open.ToList()) if (!t.closing) t.Layout_(false); }

        void Step()
        {
            animStep++;
            double u = Math.Min(1, (double)animStep / AnimSteps), e = 1 - Math.Pow(1 - u, 3);
            if (!closing) Location = new Point((int)(startX + (targetX - startX) * e), targetY);
            else Location = new Point((int)(targetX + (Screen.PrimaryScreen.WorkingArea.Right - targetX) * e), Top);
            if (u >= 1) { anim.Stop(); if (closing) Close(); }
        }

        // 滑出去再关
        void Leave_()
        {
            if (closing) return;
            closing = true; life.Stop();
            Open.Remove(this); Reflow();
            targetX = Left; animStep = 0; anim.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Open.Remove(this);
            life.Dispose(); anim.Dispose(); preview.Dispose(); dragBitmap?.Dispose();
            base.OnFormClosed(e);
        }

        // ---- 结果 ----
        void TimeUp()
        {
            if (hovering || closing) return;
            if (What == Kind.Image) SaveToDesktop();
            Outcome = Outcome ?? "saved";
            Leave_();
        }

        // 截图搬到桌面（只搬一次）
        void SaveToDesktop()
        {
            if (What != Kind.Image || !FilePath.StartsWith(Shots.TempDir, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var dst = Unique(Path.Combine(Desktop, Path.GetFileName(FilePath)));
                File.Move(FilePath, dst);
                FilePath = dst;
            }
            catch { }
        }

        public void Dismiss()   // 点 ×：截图不要了，视频只是关掉缩略图
        {
            if (What == Kind.Image) try { File.Delete(FilePath); } catch { }
            Outcome = "dismissed";
            Leave_();
        }

        void OpenIt()
        {
            if (What == Kind.Image) SaveToDesktop();
            Outcome = "opened";
            try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); } catch { }
            Leave_();
        }

        void DragOut()
        {
            life.Stop();
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { FilePath });
            if (dragBitmap != null) data.SetData(DataFormats.Bitmap, true, dragBitmap);
            // 视频总要留在桌面：只许复制；截图在临时文件夹，拖去哪都行
            var eff = DoDragDrop(data, What == Kind.Video ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move);
            if (eff != DragDropEffects.None) { Outcome = "dragged"; Leave_(); }
            else if (!hovering) life.Start();   // 拖到一半放回来了：接着计时
        }

        // ---- 鼠标 ----
        public Rectangle CloseBox
        {
            get { int s = (int)(Width * 0.11f) + 10, m = 6; return new Rectangle(Width - s - m, m, s, s); }
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; life.Stop(); Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false; Invalidate();
            if (!closing) { life.Stop(); life.Start(); }   // 离开后重新计时
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; downAt = e.Location; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!hovering) { hovering = true; life.Stop(); Invalidate(); }
            if (down && !closing)
            {
                var d = SystemInformation.DragSize;
                if (Math.Abs(e.X - downAt.X) > d.Width || Math.Abs(e.Y - downAt.Y) > d.Height) { down = false; DragOut(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (down && e.Button == MouseButtons.Left && !closing)
            {
                down = false;
                if (CloseBox.Contains(e.Location)) Dismiss(); else OpenIt();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(preview, 0, 0);
            if (!hovering) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = CloseBox;
            using (var bg = new SolidBrush(Color.FromArgb(170, 30, 30, 30))) g.FillEllipse(bg, r);
            using (var pen = new Pen(Color.White, Math.Max(1.5f, r.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float p = r.Width * 0.32f;
                g.DrawLine(pen, r.Left + p, r.Top + p, r.Right - p, r.Bottom - p);
                g.DrawLine(pen, r.Right - p, r.Top + p, r.Left + p, r.Bottom - p);
            }
        }

        // 用 ffmpeg 抽视频中间一帧（480 宽），抽不到返回 null
        public static Bitmap VideoFrame(string path)
        {
            try { return VideoFrame_(path); } catch { return null; }
        }

        static Bitmap VideoFrame_(string path)
        {
            if (!File.Exists(Studio.FfmpegPath)) return null;
            double dur = 0;
            try { dur = Studio.Probe(path).Duration; } catch { }
            var png = Path.Combine(Shots.TempDir, "frame-" + Guid.NewGuid().ToString("N") + ".png");
            var psi = new ProcessStartInfo(Studio.FfmpegPath, $"-v error -y -ss {Math.Max(0, dur / 2):0.###} -i \"{path}\" -frames:v 1 -vf scale=480:-2 \"{png}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } } }
            if (!File.Exists(png)) return null;
            Bitmap b;
            using (var f = new Bitmap(png)) b = new Bitmap(f);
            try { File.Delete(png); } catch { }
            return b;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    }
}
