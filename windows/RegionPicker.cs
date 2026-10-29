using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    // 框选区域（仿 Mac ⌘⇧5）：鼠标所在屏幕盖一层暗色遮罩，默认给一个框，拖八个点调大小、拖框内移动、框外重新拖；
    // 底部工具栏点「录制」直接开录，Esc 取消
    sealed class RegionPicker : Form
    {
        static RegionPicker current;
        readonly Screen screen;
        readonly Bitmap shot;
        readonly Action<Screen, Rectangle, bool> done;
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        Rectangle sel;
        int drag = -1;            // -1 无；0..7 八个点；8 移动；9 新拖一个框
        Point dragFrom;
        Rectangle selFrom;

        public static void Pick(Source initial, Action<Screen, Rectangle, bool> done)
        {
            current?.Close();
            var screen = Screen.FromPoint(Cursor.Position);
            Rectangle start;
            if (initial != null && initial.DeviceName == screen.DeviceName) start = initial.Area;
            else
            {
                var b = screen.Bounds;
                start = new Rectangle(b.Width / 5, b.Height / 5, b.Width * 3 / 5, b.Height * 3 / 5);
            }
            current = new RegionPicker(screen, start, done);
            current.Show();
            current.Activate();
        }

        RegionPicker(Screen screen, Rectangle start, Action<Screen, Rectangle, bool> done)
        {
            this.screen = screen; this.done = done; sel = start;
            var b = screen.Bounds;
            shot = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            Font = SystemFonts.MessageBoxFont;

            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.BackColor = Color.FromArgb(245, 245, 245);
            bar.Padding = new Padding(6);
            bar.Cursor = Cursors.Default;
            var cancel = new Button { Text = L("取消", "Cancel"), AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
            var rec = new Button
            {
                Text = "●  " + L("录制", "Record"), AutoSize = true, Padding = new Padding(14, 3, 14, 3),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(220, 50, 50), ForeColor = Color.White,
                Font = new Font(Font, FontStyle.Bold),
            };
            rec.FlatAppearance.BorderSize = 0;
            cancel.Click += (s, e) => Close();
            rec.Click += (s, e) => Finish(true);
            bar.Controls.Add(cancel); bar.Controls.Add(rec);
            Controls.Add(bar);
            PlaceBar();
        }

        void Finish(bool record)
        {
            var area = Rectangle.Intersect(sel, new Rectangle(Point.Empty, screen.Bounds.Size));
            if (area.Width < 16 || area.Height < 16) return;
            Hide();
            // 等遮罩真的从屏幕上消失再开录，第一帧不带遮罩
            var t = new Timer { Interval = 200 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Close(); done(screen, area, record); };
            t.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (current == this) current = null;
            shot.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Enter) Finish(true);
            base.OnKeyDown(e);
        }

        Rectangle[] Handles()
        {
            int s = Scale(10), h = s / 2;
            int l = sel.Left, t = sel.Top, r = sel.Right, b = sel.Bottom, cx = (l + r) / 2, cy = (t + b) / 2;
            return new[]
            {
                new Rectangle(l - h, t - h, s, s), new Rectangle(cx - h, t - h, s, s), new Rectangle(r - h, t - h, s, s),
                new Rectangle(r - h, cy - h, s, s), new Rectangle(r - h, b - h, s, s), new Rectangle(cx - h, b - h, s, s),
                new Rectangle(l - h, b - h, s, s), new Rectangle(l - h, cy - h, s, s),
            };
        }

        int Scale(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        int HitTest(Point p)
        {
            var hs = Handles();
            for (int i = 0; i < hs.Length; i++) { var r = hs[i]; r.Inflate(Scale(4), Scale(4)); if (r.Contains(p)) return i; }
            return sel.Contains(p) ? 8 : 9;
        }

        static readonly Cursor[] handleCursors =
        {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2 && sel.Contains(e.Location)) { Finish(true); return; }
            drag = HitTest(e.Location); dragFrom = e.Location; selFrom = sel;
            if (drag == 9) sel = new Rectangle(e.Location, Size.Empty);
            bar.Visible = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag < 0)
            {
                int h = HitTest(e.Location);
                Cursor = h < 8 ? handleCursors[h] : h == 8 ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            int dx = e.X - dragFrom.X, dy = e.Y - dragFrom.Y;
            var W = ClientSize.Width; var H = ClientSize.Height;
            if (drag == 8)
            {
                int x = Math.Max(0, Math.Min(W - selFrom.Width, selFrom.X + dx));
                int y = Math.Max(0, Math.Min(H - selFrom.Height, selFrom.Y + dy));
                sel = new Rectangle(x, y, selFrom.Width, selFrom.Height);
            }
            else if (drag == 9) sel = Norm(dragFrom, Clamp(e.Location));
            else
            {
                int l = selFrom.Left, t = selFrom.Top, r = selFrom.Right, b = selFrom.Bottom;
                var p = Clamp(e.Location);
                if (drag == 0 || drag == 6 || drag == 7) l = p.X;
                if (drag == 2 || drag == 3 || drag == 4) r = p.X;
                if (drag == 0 || drag == 1 || drag == 2) t = p.Y;
                if (drag == 4 || drag == 5 || drag == 6) b = p.Y;
                sel = Norm(new Point(l, t), new Point(r, b));
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drag < 0) return;
            drag = -1;
            if (sel.Width < 16 || sel.Height < 16) sel = selFrom.Width >= 16 ? selFrom : new Rectangle(sel.X, sel.Y, Math.Max(sel.Width, Scale(200)), Math.Max(sel.Height, Scale(120)));
            PlaceBar();
            Invalidate();
        }

        Point Clamp(Point p) => new Point(Math.Max(0, Math.Min(ClientSize.Width, p.X)), Math.Max(0, Math.Min(ClientSize.Height, p.Y)));
        static Rectangle Norm(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        // 工具栏放在框下方居中，放不下就放框里底部
        void PlaceBar()
        {
            var s = bar.PreferredSize;
            int x = Math.Max(Scale(8), Math.Min(ClientSize.Width - s.Width - Scale(8), sel.Left + (sel.Width - s.Width) / 2));
            int y = sel.Bottom + Scale(12);
            if (y + s.Height > ClientSize.Height - Scale(8)) y = Math.Max(Scale(8), sel.Bottom - s.Height - Scale(12));
            bar.Location = new Point(x, y);
            bar.Visible = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(shot, 0, 0);
            using (var dim = new Region(ClientRectangle))
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                dim.Exclude(sel);
                g.FillRegion(shade, dim);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, Math.Max(1, Scale(1))) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            using (var fill = new SolidBrush(Color.White))
            using (var edge = new Pen(Color.FromArgb(120, 0, 0, 0)))
                foreach (var h in Handles()) { g.FillEllipse(fill, h); g.DrawEllipse(edge, h); }
            // 尺寸标签
            string size = $"{sel.Width} × {sel.Height}";
            var ts = g.MeasureString(size, Font);
            var lbl = new RectangleF(sel.Left, sel.Top - ts.Height - Scale(8), ts.Width + Scale(10), ts.Height + Scale(4));
            if (lbl.Top < 0) lbl.Y = sel.Top + Scale(6);
            using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(bg, lbl);
            g.DrawString(size, Font, Brushes.White, lbl.X + Scale(5), lbl.Y + Scale(2));
        }
    }
}
