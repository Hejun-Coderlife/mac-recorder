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
        const int RulerH = 22, CutY = 34, CutH = 20, SegY = 58, SegH = 38, VoiceY = 100, VoiceH = 22, CapY = 126, CapH = 22, HandleW = 16, Bar = 4, EdgeW = 9;
        readonly ToolTip tip = new ToolTip { InitialDelay = 500 };
        string tipText;

        enum Hit { None, Ruler, TrimStart, TrimEnd, SegStart, SegEnd, SegBody, CutStart, CutEnd, CutBody, VoiceStart, VoiceEnd, VoiceBody, CapStart, CapEnd, CapBody, Empty }
        Hit drag = Hit.None;
        string dragSeg;
        int downX;
        bool moved, justAdded;
        double baseStart, baseEnd, baseTrimS, baseTrimE, baseIn, baseOut;

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
                    g.DrawString(s, font, txt, x + (t == 0 ? 10 : 1), 6);   // 0:00 别被左边的修剪把手挡住
                }
            // 点击、打字的时刻
            using (var dot = new SolidBrush(Color.FromArgb(140, Theme.Secondary)))
                foreach (var t in ed.Events) { float x = X(t); if (x >= -3 && x <= ClientSize.Width + 3) g.FillEllipse(dot, x - 2, RulerH + 7, 4, 4); }
            // 剪辑轨：剪掉 = 红色斜纹，快放 = 蓝色斜纹 + 倍数
            using (var lane = new SolidBrush(Color.FromArgb(Theme.Dark ? 30 : 18, Theme.Secondary))) g.FillRectangle(lane, 1, CutY, ClientSize.Width - 2, CutH);
            foreach (var c in p.Cuts)
            {
                float x0 = X(c.Start), x1 = Math.Max(x0 + 6, X(c.End));
                if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                bool sel = c.Id == ed.SelectedCut;
                var col = c.Speed ? Color.FromArgb(40, 120, 230) : Color.FromArgb(225, 60, 60);
                var r = new RectangleF(x0, CutY, x1 - x0, CutH);
                using (var path = Theme.Round(r, 5))
                {
                    using (var b = new SolidBrush(Color.FromArgb(sel ? 235 : 170, col))) g.FillPath(b, path);
                    using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(90, Color.White), Color.Transparent)) g.FillPath(hb, path);
                    if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                }
                using (var hb = new SolidBrush(Color.FromArgb(225, Color.White)))
                {
                    g.FillRectangle(hb, x0 + 3, CutY + CutH / 2 - 5, 2, 10);
                    g.FillRectangle(hb, x1 - 5, CutY + CutH / 2 - 5, 2, 10);
                }
                string label = c.Speed ? "▶▶ " + c.Rate.ToString("0") + "×" : "✂";
                using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(label, f);
                    if (sz.Width + 14 < r.Width)
                        using (var w = new SolidBrush(Color.White)) g.DrawString(label, f, w, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
                }
            }
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
            // 讲解轨：绿色块 + 波形（块从讲解开头那一刻画到讲解结尾那一刻对应的素材时间）
            var map = new TimeMap(p);
            using (var lane = new SolidBrush(Color.FromArgb(Theme.Dark ? 30 : 18, Theme.Secondary))) { g.FillRectangle(lane, 1, VoiceY, ClientSize.Width - 2, VoiceH); g.FillRectangle(lane, 1, CapY, ClientSize.Width - 2, CapH); }
            using (var lf = new Font("Segoe UI", 7f)) using (var lb = new SolidBrush(Color.FromArgb(150, Theme.Secondary)))
            {
                if (p.Voiceovers.Count == 0) g.DrawString(L("讲解（播放栏「录讲解」）", "Voice-over (Record in the play bar)"), lf, lb, 6, VoiceY + 4);
                if (p.Captions.Count == 0) g.DrawString(L("字幕（播放栏「字幕」，或双击这里加一句）", "Captions (Captions in the play bar, or double-click here)"), lf, lb, 6, CapY + 4);
            }
            foreach (var v in p.Voiceovers)
            {
                float x0 = X(v.Start), x1 = Math.Max(x0 + 6, X(v.SrcEnd(map)));
                if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                bool sel = v.Id == ed.SelectedVoice;
                var r = new RectangleF(x0, VoiceY, x1 - x0, VoiceH);
                var col = Color.FromArgb(52, 168, 83);
                using (var path = Theme.Round(r, 5))
                {
                    using (var b = new SolidBrush(Color.FromArgb(sel ? 235 : 175, col))) g.FillPath(b, path);
                    var peaks = ed.Peaks(v.File);
                    if (peaks != null && peaks.Length > 1)
                    {
                        var clip = g.Clip; g.SetClip(path);
                        using (var wp = new Pen(Color.FromArgb(200, Color.White), 1))
                            for (float x = Math.Max(x0 + 2, 0); x < Math.Min(x1 - 2, ClientSize.Width); x += 2)
                            {
                                double ft = v.TrimIn + (x - x0) / Math.Max(1, x1 - x0) * v.Length;   // 讲解里的第几秒（按块的长短摊开）
                                int i = (int)(ft * 50); if (i < 0 || i >= peaks.Length) continue;
                                float a = Math.Min(1, peaks[i] * 1.6f) * (VoiceH / 2f - 2);
                                g.DrawLine(wp, x, VoiceY + VoiceH / 2f - a, x, VoiceY + VoiceH / 2f + a);
                            }
                        g.Clip = clip;
                    }
                    if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                }
                using (var hb = new SolidBrush(Color.FromArgb(225, Color.White))) { g.FillRectangle(hb, x0 + 3, VoiceY + VoiceH / 2 - 5, 2, 10); g.FillRectangle(hb, x1 - 5, VoiceY + VoiceH / 2 - 5, 2, 10); }
            }
            // 正在录的讲解：红色块跟着播放头长出来
            if (ed.RecordingVoice && ed.RecordStart is double rs0)
                using (var rb = new SolidBrush(Color.FromArgb(200, 230, 50, 50))) { float x0 = X(rs0), x1 = Math.Max(x0 + 4, X(ed.Time)); g.FillRectangle(rb, x0, VoiceY, x1 - x0, VoiceH); }
            // 字幕轨：一句一块，块里写字
            using (var cf = new Font("Microsoft YaHei UI", 7.5f))
                foreach (var c in p.Captions)
                {
                    float x0 = X(c.Start), x1 = Math.Max(x0 + 6, X(c.End));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    bool sel = c.Id == ed.SelectedCaption;
                    var r = new RectangleF(x0, CapY, x1 - x0, CapH);
                    using (var path = Theme.Round(r, 4))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(sel ? 240 : 190, 230, 140, 30))) g.FillPath(b, path);
                        if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                    }
                    if (r.Width > 18)
                        TextRenderer.DrawText(g, c.Text, cf, Rectangle.Round(new RectangleF(r.X + 4, r.Y, r.Width - 8, r.Height)), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
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

        // 竖杠画在留下的那一侧（开头的在右、结尾的在左），修剪点在最边上时也看得见；上下小横杠朝成片那一侧伸出去
        static void DrawBracket(Graphics g, float x, bool isStart, int h)
        {
            using (var y = new SolidBrush(Color.FromArgb(255, 204, 0)))
            {
                float barX = isStart ? x : x - Bar;
                float armX = isStart ? x : x - 9;
                g.FillRectangle(y, barX, 0, Bar, h);
                g.FillRectangle(y, armX, 0, 9, 3);
                g.FillRectangle(y, armX, h - 3, 9, 3);
            }
        }

        // ---- 点在哪 ----
        Hit HitTest(Point pt, out string seg)
        {
            seg = null;
            var p = ed.Project;
            float ts = X(p.TrimStart), te = X(p.TrimEnd);
            if (pt.X >= ts - 4 && pt.X <= ts + HandleW - 2) return Hit.TrimStart;
            if (pt.X >= te - HandleW + 2 && pt.X <= te + 4) return Hit.TrimEnd;
            if (pt.Y < RulerH) return Hit.Ruler;
            if (pt.Y >= CutY && pt.Y <= CutY + CutH)
                foreach (var c in p.Cuts)
                {
                    float x0 = X(c.Start), x1 = Math.Max(x0 + 6, X(c.End));
                    if (pt.X < x0 - 2 || pt.X > x1 + 2) continue;
                    seg = c.Id;
                    if (x1 - x0 > EdgeW * 2.5f) { if (pt.X <= x0 + EdgeW) return Hit.CutStart; if (pt.X >= x1 - EdgeW) return Hit.CutEnd; }
                    return Hit.CutBody;
                }
            if (pt.Y >= VoiceY && pt.Y <= VoiceY + VoiceH)
            {
                var map = new TimeMap(p);
                foreach (var v in p.Voiceovers)
                {
                    float x0 = X(v.Start), x1 = Math.Max(x0 + 6, X(v.SrcEnd(map)));
                    if (pt.X < x0 - 2 || pt.X > x1 + 2) continue;
                    seg = v.Id;
                    if (x1 - x0 > EdgeW * 2.5f) { if (pt.X <= x0 + EdgeW) return Hit.VoiceStart; if (pt.X >= x1 - EdgeW) return Hit.VoiceEnd; }
                    return Hit.VoiceBody;
                }
            }
            if (pt.Y >= CapY && pt.Y <= CapY + CapH)
                foreach (var c in p.Captions)
                {
                    float x0 = X(c.Start), x1 = Math.Max(x0 + 6, X(c.End));
                    if (pt.X < x0 - 2 || pt.X > x1 + 2) continue;
                    seg = c.Id;
                    if (x1 - x0 > EdgeW * 2.5f) { if (pt.X <= x0 + EdgeW) return Hit.CapStart; if (pt.X >= x1 - EdgeW) return Hit.CapEnd; }
                    return Hit.CapBody;
                }
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
            var cg = p.Cuts.FirstOrDefault(c => c.Id == dragSeg);
            if (cg != null && (drag == Hit.CutStart || drag == Hit.CutEnd || drag == Hit.CutBody)) { baseStart = cg.Start; baseEnd = cg.End; }
            var vg = p.Voiceovers.FirstOrDefault(v => v.Id == dragSeg);
            if (vg != null && (drag == Hit.VoiceStart || drag == Hit.VoiceEnd || drag == Hit.VoiceBody)) { baseStart = vg.Start; baseIn = vg.TrimIn; baseOut = vg.TrimOut; }
            var kg = p.Captions.FirstOrDefault(c => c.Id == dragSeg);
            if (kg != null && (drag == Hit.CapStart || drag == Hit.CapEnd || drag == Hit.CapBody)) { baseStart = kg.Start; baseEnd = kg.End; }
            switch (drag)
            {
                case Hit.Ruler: ed.ScrubBegin(); ed.Seek(T(e.X)); break;
                case Hit.SegStart: case Hit.SegEnd: case Hit.SegBody: ed.Select("seg", dragSeg); break;
                case Hit.CutStart: case Hit.CutEnd: case Hit.CutBody: ed.Select("cut", dragSeg); break;
                case Hit.VoiceStart: case Hit.VoiceEnd: case Hit.VoiceBody: ed.Select("voice", dragSeg); break;
                case Hit.CapStart: case Hit.CapEnd: case Hit.CapBody: ed.Select("cap", dragSeg); break;
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
                Cursor = h == Hit.TrimStart || h == Hit.TrimEnd || h == Hit.SegStart || h == Hit.SegEnd || h == Hit.CutStart || h == Hit.CutEnd || h == Hit.VoiceStart || h == Hit.VoiceEnd || h == Hit.CapStart || h == Hit.CapEnd ? Cursors.SizeWE
                       : h == Hit.SegBody || h == Hit.CutBody || h == Hit.VoiceBody || h == Hit.CapBody ? Cursors.SizeAll : Cursors.Default;
                string t = h == Hit.TrimStart ? L("拖动剪掉开头", "Drag to cut the start") : h == Hit.TrimEnd ? L("拖动剪掉结尾", "Drag to cut the end")
                    : h == Hit.CutStart || h == Hit.CutEnd || h == Hit.CutBody ? L("拖两头改范围、拖中间挪；选中后可改成快放", "Drag the ends or the middle; select it to switch to speed-up")
                    : h == Hit.VoiceStart || h == Hit.VoiceEnd || h == Hit.VoiceBody ? L("讲解：拖中间挪位置，拖两头剪掉开头 / 结尾；选中后调音量、删除", "Voice-over: drag to move, drag the ends to trim; select it for volume / delete")
                    : h == Hit.CapStart || h == Hit.CapEnd || h == Hit.CapBody ? L("字幕：双击改字，拖两头改时间、拖中间挪", "Caption: double-click to edit, drag the ends or the middle")
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
                case Hit.CutBody:
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Cuts.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        var room = ed.CutRoom(id, p); double len = baseEnd - baseStart;
                        double ns = Math.Min(Math.Max(baseStart + dt, room.lo), room.hi - len);
                        c.Start = ns; c.End = ns + len;
                    });
                    break;
                case Hit.CutStart: case Hit.CutEnd:
                    bool cs = drag == Hit.CutStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Cuts.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        var room = ed.CutRoom(id, p);
                        if (cs) c.Start = Math.Min(Math.Max(baseStart + dt, room.lo), baseEnd - 0.2);
                        else c.End = Math.Max(Math.Min(baseEnd + dt, room.hi), baseStart + 0.2);
                    });
                    break;
                case Hit.VoiceBody:
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var v = p.Voiceovers.FirstOrDefault(x => x.Id == id); if (v == null) return;
                        v.Start = Math.Min(Math.Max(baseStart + dt, p.TrimStart), Math.Max(p.TrimStart, p.TrimEnd - 0.1));
                    });
                    break;
                case Hit.VoiceStart: case Hit.VoiceEnd:
                    bool vs = drag == Hit.VoiceStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var v = p.Voiceovers.FirstOrDefault(x => x.Id == id); if (v == null) return;
                        var m = new TimeMap(p);
                        if (vs)
                        {
                            // 拖开头：剪掉讲解的开头（或放回来），讲解后面的部分在成片里的位置不动
                            double ni = Math.Min(Math.Max(baseIn + dt, 0), baseOut - 0.2);
                            v.TrimIn = ni; v.Start = m.Src(Math.Max(m.Out(p.TrimStart), m.Out(baseStart) + (ni - baseIn)));
                        }
                        else v.TrimOut = Math.Max(Math.Min(baseOut + dt, v.FileLength > 0 ? v.FileLength : baseOut + dt), v.TrimIn + 0.2);
                    });
                    break;
                case Hit.CapBody:
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Captions.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        double len = baseEnd - baseStart, ns = Math.Min(Math.Max(baseStart + dt, 0), Math.Max(0, ed.Duration - len));
                        c.Start = ns; c.End = ns + len;
                    });
                    break;
                case Hit.CapStart: case Hit.CapEnd:
                    bool ks = drag == Hit.CapStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Captions.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        if (ks) c.Start = Math.Min(Math.Max(baseStart + dt, 0), baseEnd - 0.3);
                        else c.End = Math.Max(Math.Min(baseEnd + dt, ed.Duration), baseStart + 0.3);
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
            // 双击加了一段：WinForms 在双击之后还会来一次 MouseUp，别把刚加的那段又取消选中
            if (justAdded) { justAdded = false; return; }
            if (!moved && was == Hit.Empty) { ed.Select(null, null); ed.Seek(T(e.X)); }
            if (!moved && was != Hit.Empty && was != Hit.None && was != Hit.TrimStart && was != Hit.TrimEnd && was != Hit.Ruler) ed.Touch();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var h = HitTest(e.Location, out var id);
            if (h == Hit.CapBody || h == Hit.CapStart || h == Hit.CapEnd) { ed.Select("cap", id); ed.EditCaptionText(); justAdded = true; return; }
            if (h == Hit.Empty)
            {
                if (e.Y >= CutY && e.Y <= CutY + CutH) ed.AddCut(T(e.X));          // 剪辑轨上双击 = 加一段剪掉
                else if (e.Y >= CapY && e.Y <= CapY + CapH) ed.AddCaption(T(e.X)); // 字幕轨上双击 = 加一句
                else if (e.Y >= VoiceY && e.Y <= VoiceY + VoiceH) return;           // 讲解只能录
                else ed.AddSegment(T(e.X));
                justAdded = true;
            }
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
