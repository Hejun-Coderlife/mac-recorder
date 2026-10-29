// 开录倒计时（屏幕中间大号数字，自己的窗口不进录像）和录制时隐藏桌面图标（录完 / 崩溃后下次打开都会恢复）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class CountdownForm : Form
    {
        readonly Timer tick = new Timer { Interval = 16 };
        readonly DateTime started = DateTime.Now;
        readonly int seconds;
        readonly Action done, cancelled;
        bool finished;
        public static CountdownForm Current;

        // seconds 秒后调 done；Esc / 再按快捷键（Cancel）取消调 cancelled
        public static void Run(int seconds, Screen screen, Action done, Action cancelled)
        {
            Current?.Cancel();
            var f = new CountdownForm(seconds, screen, done, cancelled);
            Current = f;
            f.Show();
            f.Activate();
        }

        CountdownForm(int seconds, Screen screen, Action done, Action cancelled)
        {
            this.seconds = seconds; this.done = done; this.cancelled = cancelled;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            var b = (screen ?? Screen.PrimaryScreen).Bounds;
            int sz = (int)(Math.Min(b.Width, b.Height) * 0.28);
            Bounds = new Rectangle(b.X + (b.Width - sz) / 2, b.Y + (b.Height - sz) / 2, sz, sz);
            BackColor = Color.FromArgb(1, 1, 1); TransparencyKey = BackColor;
            DoubleBuffered = true; KeyPreview = true;
            tick.Tick += (s, e) =>
            {
                double el = (DateTime.Now - started).TotalSeconds;
                if (el >= seconds) { Finish(true); return; }
                Invalidate();
            };
            tick.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowDisplayAffinity(Handle, 0x11);   // 倒计时窗口不出现在录出来的画面里
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            base.OnKeyDown(e);
        }

        public void Cancel() => Finish(false);

        void Finish(bool ok)
        {
            if (finished) return;
            finished = true;
            tick.Stop();
            if (Current == this) Current = null;
            Close();
            if (ok) done?.Invoke(); else cancelled?.Invoke();
        }

        // 一个圆形深色底 + 白色大数字；每一秒开头淡入放大、结尾淡出
        protected override void OnPaint(PaintEventArgs e)
        {
            double el = (DateTime.Now - started).TotalSeconds;
            int n = Math.Max(1, seconds - (int)Math.Floor(el));
            double u = el - Math.Floor(el);
            double a = u < 0.15 ? u / 0.15 : u > 0.8 ? Math.Max(0, (1 - u) / 0.2) : 1;
            float k = (float)(0.9 + 0.1 * Math.Min(1, u / 0.15));
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float w = ClientSize.Width * k, x = (ClientSize.Width - w) / 2;
            using (var bg = new SolidBrush(Color.FromArgb((int)(170 * a), 20, 20, 24))) g.FillEllipse(bg, x, x, w, w);
            using (var font = new Font("Segoe UI", ClientSize.Height * 0.42f * k, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fg = new SolidBrush(Color.FromArgb((int)(255 * a), 255, 255, 255)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(n.ToString(), font, fg, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), sf);
            using (var small = new Font("Segoe UI", Math.Max(10, ClientSize.Height * 0.05f), GraphicsUnit.Pixel))
            using (var fg2 = new SolidBrush(Color.FromArgb((int)(200 * a), 255, 255, 255)))
            using (var sf2 = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(L("Esc 取消", "Esc to cancel"), small, fg2, new RectangleF(0, ClientSize.Height * 0.74f, ClientSize.Width, ClientSize.Height * 0.2f), sf2);
        }

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    }

    // 录制时隐藏桌面图标：切换资源管理器的「显示桌面图标」（和右键桌面 → 查看 → 显示桌面图标一样），
    // 记一笔在设置里，录完换回来；程序崩了没换回来的，下次打开时先换回来
    static class DesktopIcons
    {
        const string Flag = "desktopIconsHidden";

        static IntPtr DefView()
        {
            IntPtr found = IntPtr.Zero;
            var progman = FindWindow("Progman", null);
            var dv = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
            // 换过壁纸幻灯片等之后，图标层在某个 WorkerW 下面
            EnumWindows((h, l) =>
            {
                var d = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) { found = d; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static IntPtr ListView(IntPtr defView) => defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);

        public static bool Available => ListView(DefView()) != IntPtr.Zero;
        public static bool Visible { get { var lv = ListView(DefView()); return lv != IntPtr.Zero && IsWindowVisible(lv); } }

        static void Toggle()
        {
            var dv = DefView();
            if (dv != IntPtr.Zero) SendMessage(dv, 0x0111, (IntPtr)0x7402, IntPtr.Zero);   // WM_COMMAND「显示桌面图标」
        }

        static bool WaitFor(bool visible)
        {
            for (int i = 0; i < 40; i++) { if (Visible == visible) return true; System.Threading.Thread.Sleep(25); }
            return Visible == visible;
        }

        // 录制前：图标正显示着才藏（本来就藏着的不动，也不记）
        public static bool Hide()
        {
            if (!Available || !Visible) return false;
            Settings.Set(Flag, true);
            Toggle();
            return WaitFor(false);
        }

        // 录完 / 出错 / 下次打开：是我们藏的才换回来
        public static void Restore()
        {
            if (!Settings.Get(Flag, false)) return;
            if (Available && !Visible) { Toggle(); WaitFor(true); }
            Settings.Set(Flag, false);
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
