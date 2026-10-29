// 剪辑窗口的时间轴（照 Mac 版 TimelineStrip）：刻度、点击 / 打字小点、推近段、两端「[」「]」修剪把手、播放头。
// 拖动都按「按下那一刻的工程 + 拖了多远」算（固定坐标，不会越拖越歪、不抖）；
// Ctrl + 滚轮（触控板双指捏合 Windows 也是发这个）= 以鼠标处为锚点缩放；放大后滚轮 / 横向滑动 = 左右平移
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed class TimelineView : Control
    {
        readonly StudioEditor ed;
        public double Scale = 1, Offset;      // 放大倍数（1–8）、内容滚出左边的像素
        public int ViewWidth => Math.Max(1, ClientSize.Width);
        const int RulerH = 22, SegY = 38, SegH = 46, HandleW = 16, Bar = 4, EdgeW = 9;
        readonly ToolTip tip = new ToolTip { InitialDelay = 500 };
        string tipText;

        enum Hit { None, Ruler, TrimStart, TrimEnd, SegStart, SegEnd, SegBody, Empty }
        Hit drag = Hit.None;
        string dragSeg;
        int downX;
        bool moved;
        double baseStart, baseEnd, baseTrimS, baseTrimE;

        public TimelineView(StudioEditor ed)
        {
            this.ed = ed;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Window;
        }

        double D => Math.Max(ed.Duration, 0.01);
        double ContentW => ViewWidth * Scale;
        public float X(double t) => (float)(t / D * ContentW - Offset);
        double T(double x) => Math.Min(Math.Max((x + Offset) / ContentW * D, 0), D);

        // 播放时播放头跑出可见区就翻一页
        public void FollowPlayhead()
        {
            if (Scale <= 1.01) return;
            float x = X(ed.Time);
            if (x < 0 || x > ViewWidth - 20) { Offset = TimelineMath.ClampOffset(Offset + x - ViewWidth * 0.1, Scale, ViewWidth); Invalidate(); }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Offset = TimelineMath.ClampOffset(Offset, Scale, ViewWidth); }

        // ---- 画 ----
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = ed.Project;
            int h = ClientSize.Height;
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1.5f, h - 1.5f), 8))
            using (var bg = new SolidBrush(Theme.Control)) using (var ln = new Pen(Theme.Line)) { g.FillPath(bg, path); g.DrawPath(ln, path); }
            // 刻度
            double d = D, visible = d / Scale;
            double step = visible > 120 ? 10 : visible > 40 ? 5 : visible > 12 ? 2 : visible > 4 ? 1 : 0.5;
            using (var tick = new Pen(Color.FromArgb(140, Theme.Secondary)))
            using (var font = new Font("Segoe UI", 7.5f))
            using (var txt = new SolidBrush(Theme.Secondary))
                for (double t = 0; t <= d + 1e-6; t += step)
                {
                    float x = X(t);
                    if (x < -40 || x > ClientSize.Width + 40) continue;
                    g.DrawLine(tick, x, 0, x, 6);
                    string s = step < 1 ? $"{(int)t / 60}:{t % 60:00.0}" : $"{(int)t / 60}:{(int)t % 60:00}";
                    g.DrawString(s, font, txt, x + (t == 0 ? 8 : 1), 6);   // 0:00 别被左边的修剪把手挡住
                }
            // 点击、打字的时刻
            using (var dot = new SolidBrush(Color.FromArgb(140, Theme.Secondary)))
                foreach (var t in ed.Events) { float x = X(t); if (x >= -3 && x <= ClientSize.Width + 3) g.FillEllipse(dot, x - 2, RulerH + 7, 4, 4); }
            // 推近段
            foreach (var s in p.Segments)
            {
                float x0 = X(s.Start), x1 = Math.Max(x0 + 6, X(s.End));
                if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                bool sel = s.Id == ed.Selected;
                var r = new RectangleF(x0, SegY, x1 - x0, SegH);
                using (var path = Theme.Round(r, 7))
                {
                    using (var b = new SolidBrush(Color.FromArgb(sel ? 245 : 160, Theme.Segment))) g.FillPath(b, path);
                    if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                }
                // 两头的小把手
                using (var hb = new SolidBrush(Color.FromArgb(215, Color.White)))
                {
                    g.FillRectangle(hb, x0 + 3, SegY + SegH / 2 - 8, 3, 16);
                    g.FillRectangle(hb, x1 - 6, SegY + SegH / 2 - 8, 3, 16);
                }
                string label = (s.Fixed ? "◎ " : "↖ ") + s.Zoom.ToString("0.0") + "×";
                using (var f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(label, f);
                    if (sz.Width + 16 < r.Width)
                        using (var w = new SolidBrush(Color.White)) g.DrawString(label, f, w, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
                }
            }
            // 修剪范围外压暗
            using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            {
                float a = X(p.TrimStart), b = X(p.TrimEnd);
                if (a > 0) g.FillRectangle(dim, 0, 0, a, h);
                if (b < ClientSize.Width) g.FillRectangle(dim, b, 0, ClientSize.Width - b, h);
            }
            // 修剪把手「[」「]」：竖杠贴着修剪点，上下小横杠朝成片那一侧
            DrawBracket(g, X(p.TrimStart), true, h);
            DrawBracket(g, X(p.TrimEnd), false, h);
            // 播放头：细线 + 顶上小把手，压在最上面
            float px = X(ed.Time);
            var pc = Theme.Dark ? Color.White : Color.FromArgb(50, 50, 52);
            using (var shadow = new Pen(Color.FromArgb(Theme.Dark ? 170 : 80, 0, 0, 0), 3.5f)) g.DrawLine(shadow, px, 12, px, h - 1);
            using (var pen = new Pen(pc, 1.5f)) g.DrawLine(pen, px, 12, px, h - 1);
            using (var knob = Theme.Round(new RectangleF(px - 5.5f, 0, 11, 13), 3))
            using (var kb = new SolidBrush(pc)) g.FillPath(kb, knob);
        }

        static void DrawBracket(Graphics g, float x, bool isStart, int h)
        {
            using (var y = new SolidBrush(Color.FromArgb(255, 204, 0)))
            {
                float barX = isStart ? x - Bar : x;
                g.FillRectangle(y, barX, 0, Bar, h);
                float armX = isStart ? x - Bar : x - 9 + Bar;
                g.FillRectangle(y, isStart ? x - Bar : x - 9 + Bar, 0, 9, 3);
                g.FillRectangle(y, armX, h - 3, 9, 3);
            }
        }

        // ---- 点在哪 ----
        Hit HitTest(Point pt, out string seg)
        {
            seg = null;
            var p = ed.Project;
            float ts = X(p.TrimStart), te = X(p.TrimEnd);
            if (pt.X >= ts - HandleW + 2 && pt.X <= ts + 4) return Hit.TrimStart;
            if (pt.X >= te - 4 && pt.X <= te + HandleW - 2) return Hit.TrimEnd;
            if (pt.Y < RulerH) return Hit.Ruler;
            if (pt.Y >= SegY && pt.Y <= SegY + SegH)
                foreach (var s in p.Segments)
                {
                    float x0 = X(s.Start), x1 = Math.Max(x0 + 6, X(s.End));
                    if (pt.X < x0 - 2 || pt.X > x1 + 2) continue;
                    seg = s.Id;
                    if (x1 - x0 > EdgeW * 2.5f) { if (pt.X <= x0 + EdgeW) return Hit.SegStart; if (pt.X >= x1 - EdgeW) return Hit.SegEnd; }
                    return Hit.SegBody;
                }
            return Hit.Empty;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            drag = HitTest(e.Location, out dragSeg);
            downX = e.X; moved = false;
            var p = ed.Project;
            baseTrimS = p.TrimStart; baseTrimE = p.TrimEnd;
            var sg = p.Segments.FirstOrDefault(s => s.Id == dragSeg);
            if (sg != null) { baseStart = sg.Start; baseEnd = sg.End; }
            switch (drag)
            {
                case Hit.Ruler: ed.ScrubBegin(); ed.Seek(T(e.X)); break;
                case Hit.SegStart: case Hit.SegEnd: case Hit.SegBody: ed.Selected = dragSeg; ed.Picking = false; ed.Touch(); break;
                case Hit.TrimStart: case Hit.TrimEnd: break;   // 单击把手什么都不做（别把播放头叫过来）
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag == Hit.None)
            {
                var h = HitTest(e.Location, out _);
                Cursor = h == Hit.TrimStart || h == Hit.TrimEnd || h == Hit.SegStart || h == Hit.SegEnd ? Cursors.SizeWE : h == Hit.SegBody ? Cursors.SizeAll : Cursors.Default;
                string t = h == Hit.TrimStart ? L("拖动剪掉开头", "Drag to cut the start") : h == Hit.TrimEnd ? L("拖动剪掉结尾", "Drag to cut the end")
                    : L("点选推近段来调整，空白处双击新加，两端黄条修剪；Ctrl + 滚轮缩放时间轴", "Click a zoom to edit it, double-click empty space to add one, drag the yellow bars to trim; Ctrl + scroll zooms the timeline");
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
                return;
            }
            if (Math.Abs(e.X - downX) >= 2) moved = true;
            if (!moved) return;
            double dx = e.X - downX, dt = dx / ContentW * D;
            string id = dragSeg;
            switch (drag)
            {
                case Hit.Ruler: ed.Seek(T(e.X)); break;
                case Hit.TrimStart: case Hit.TrimEnd:
                    bool isStart = drag == Hit.TrimStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        double t = TimelineMath.Trim(isStart, baseTrimS, baseTrimE, dx, ContentW, D);
                        if (isStart) p.TrimStart = t; else p.TrimEnd = t;
                    });
                    ed.Seek(isStart ? ed.Project.TrimStart : ed.Project.TrimEnd);
                    break;
                case Hit.SegBody:
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var s = p.Segments.FirstOrDefault(x => x.Id == id); if (s == null) return;
                        var room = ed.Room(id, p); double len = baseEnd - baseStart;
                        double ns = Math.Min(Math.Max(baseStart + dt, room.lo), room.hi - len);
                        s.Start = ns; s.End = ns + len;
                    });
                    break;
                case Hit.SegStart: case Hit.SegEnd:
                    bool st = drag == Hit.SegStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var s = p.Segments.FirstOrDefault(x => x.Id == id); if (s == null) return;
                        var room = ed.Room(id, p);
                        if (st) s.Start = Math.Min(Math.Max(baseStart + dt, room.lo), baseEnd - 0.3);
                        else s.End = Math.Max(Math.Min(baseEnd + dt, room.hi), baseStart + 0.3);
                    });
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            var was = drag; drag = Hit.None;
            if (was == Hit.Ruler) { ed.ScrubEnd(); return; }
            if (ed.IsDragging) { ed.DragEnd(); return; }
            if (!moved && was == Hit.Empty) { ed.Selected = null; ed.Picking = false; ed.Seek(T(e.X)); }
            if (!moved && (was == Hit.SegBody || was == Hit.SegStart || was == Hit.SegEnd)) ed.Touch();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (HitTest(e.Location, out _) == Hit.Empty) ed.AddSegment(T(e.X));
        }

        // ---- 滚轮 / 触控板 ----
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (ed.IsDragging) return;
            if ((ModifierKeys & Keys.Control) != 0) { ed.SetTimelineScale(Scale * TimelineMath.WheelFactor(e.Delta / 120.0), e.X); return; }
            if (Scale > 1.01) { Offset = TimelineMath.ClampOffset(Offset - e.Delta / 120.0 * 60, Scale, ViewWidth); Invalidate(); }
        }

        protected override void WndProc(ref Message m)
        {
            // WM_MOUSEHWHEEL：触控板双指左右滑 / 横向滚轮 → 平移
            if (m.Msg == 0x020E && Scale > 1.01)
            {
                int delta = (short)((long)m.WParam >> 16 & 0xffff);
                Offset = TimelineMath.ClampOffset(Offset + delta / 120.0 * 60, Scale, ViewWidth);
                Invalidate();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
    }
}
