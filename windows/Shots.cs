using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    // 截图（仿 Mac ⌘⇧4）：Ctrl+Alt+S → 先把所有屏幕定格成一张图，每块屏幕盖一层变暗的遮罩，拖框松手就截好；Esc / 右键取消。
    // 截好的图放进剪贴板，右下角浮出缩略图（Thumb.cs）：拖进别的软件就是发过去；没动它就自动存到桌面
    static class Shots
    {
        // 拖拽要真实文件：截好先写到这里，没拖走才搬到桌面；拖走的留着给对方慢慢读，下次打开程序时删掉一天前的
        public static string TempDir
        {
            get { var d = Path.Combine(Path.GetTempPath(), "录屏截图"); Directory.CreateDirectory(d); return d; }
        }

        public static void CleanTemp()
        {
            try
            {
                foreach (var f in Directory.GetFiles(TempDir))
                    if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(1)) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        // 自测截屏要能拍到遮罩和缩略图：SHOT_VISIBLE=1 时不把它们排除在截屏 / 录屏之外
        public static bool Hidden => Environment.GetEnvironmentVariable("SHOT_VISIBLE") != "1";

        // 整个虚拟屏幕（所有显示器拼起来，物理像素）定格成一张图
        public static Bitmap Freeze(out Rectangle virt)
        {
            virt = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(virt.Width, virt.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(virt.Location, Point.Empty, virt.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static bool Active => Snipper.Open.Count > 0;

        // 开始截图：定格 → 每块屏幕一层遮罩；done 收到截好的图（取消就不调）
        public static void Start(Action<Bitmap> done)
        {
            if (Active) return;
            var frozen = Freeze(out var virt);
            var all = new List<Snipper>();
            Action<Bitmap> finish = bmp =>
            {
                foreach (var s in all.ToList()) s.Close();
                frozen.Dispose();
                if (bmp != null) done(bmp);
            };
            foreach (var scr in Screen.AllScreens)
            {
                var b = scr.Bounds;
                var part = frozen.Clone(new Rectangle(b.X - virt.X, b.Y - virt.Y, b.Width, b.Height), frozen.PixelFormat);
                all.Add(new Snipper(scr, part, finish));
            }
            foreach (var s in all) s.Show();
            // 鼠标所在那块屏幕拿键盘焦点（Esc 取消）
            var here = all.FirstOrDefault(s => s.Bounds.Contains(Cursor.Position)) ?? all[0];
            here.Activate();
            SetForegroundWindow(here.Handle);
        }

        // 截好的图：放进剪贴板（位图 + PNG 两种格式，微信 / QQ / Office 都能粘），存成临时 png，右下角出缩略图
        public static Thumb Deliver(Bitmap bmp)
        {
            var at = DateTime.Now;
            var tmp = Path.Combine(TempDir, $"{L("截图", "Screenshot")} {at:yyyy-MM-dd HH.mm.ss}.png");
            tmp = Unique(tmp);
            bmp.Save(tmp, ImageFormat.Png);
            ToClipboard(bmp, tmp);
            System.Media.SystemSounds.Asterisk.Play();
            return Thumb.ShowImage(bmp, tmp);
        }

        public static void ToClipboard(Bitmap bmp, string pngFile)
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, bmp);
            data.SetData("PNG", false, new MemoryStream(File.ReadAllBytes(pngFile)));
            for (int i = 0; i < 5; i++)   // 剪贴板偶尔被别的程序占着，重试几次
            {
                try { Clipboard.SetDataObject(data, true, 3, 50); return; }
                catch (ExternalException) { System.Threading.Thread.Sleep(80); }
            }
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    }

    // 一块屏幕上的遮罩：显示定格的画面，拖框选区，松手就完成
    sealed class Snipper : Form
    {
        public static readonly List<Snipper> Open = new List<Snipper>();
        readonly Bitmap shot;
        readonly Action<Bitmap> finish;
        Rectangle sel;
        Point from;
        bool dragging, done;

        public Snipper(Screen screen, Bitmap shot, Action<Bitmap> finish)
        {
            this.shot = shot; this.finish = finish;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;   // 程序按物理像素工作（PerMonitorV2），屏幕坐标和截到的像素一一对应
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            Font = SystemFonts.MessageBoxFont;
            Open.Add(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Shots.Hidden) SetWindowDisplayAffinity(Handle, 0x11);   // 正在录屏时遮罩不进画面
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Open.Remove(this);
            shot.Dispose();
            base.OnFormClosed(e);
        }

        void Cancel() { if (!done) { done = true; finish(null); } }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left) return;
            dragging = true; from = e.Location; sel = new Rectangle(from, Size.Empty);
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            var p = new Point(Math.Max(0, Math.Min(ClientSize.Width, e.X)), Math.Max(0, Math.Min(ClientSize.Height, e.Y)));
            sel = Rectangle.FromLTRB(Math.Min(from.X, p.X), Math.Min(from.Y, p.Y), Math.Max(from.X, p.X), Math.Max(from.Y, p.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false; Capture = false;
            if (sel.Width < 4 || sel.Height < 4) { sel = Rectangle.Empty; Invalidate(); return; }   // 只是点了一下：接着等拖框
            done = true;
            var crop = shot.Clone(sel, PixelFormat.Format32bppArgb);
            finish(crop);
        }

        int Scale(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(shot, 0, 0);
            using (var dim = new Region(ClientRectangle))
            using (var shade = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                if (sel.Width > 0 && sel.Height > 0) dim.Exclude(sel);
                g.FillRegion(shade, dim);
            }
            if (sel.Width <= 0 || sel.Height <= 0) return;
            using (var pen = new Pen(Color.White, Math.Max(1, Scale(1))))
                g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            // 尺寸数字（像素），放在框右下角外面，放不下就放里面
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            string size = $"{sel.Width} × {sel.Height}";
            using (var f = new Font(Font.FontFamily, Font.SizeInPoints, FontStyle.Regular))
            {
                var ts = g.MeasureString(size, f);
                float w = ts.Width + Scale(10), h = ts.Height + Scale(4);
                float x = sel.Right - w, y = sel.Bottom + Scale(6);
                if (y + h > ClientSize.Height) y = sel.Bottom - h - Scale(6);
                if (x < 0) x = sel.Left;
                using (var path = Rounded(new RectangleF(x, y, w, h), Scale(4)))
                using (var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(bg, path);
                }
                g.DrawString(size, f, Brushes.White, x + Scale(5), y + Scale(2));
            }
        }

        internal static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    }
}
