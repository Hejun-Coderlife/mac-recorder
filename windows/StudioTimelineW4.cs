// 时间轴的 W4 部分（照 Mac 版）：「遮挡」「标注」「音乐」三条轨，剪辑轨上的虚线「建议加速」块（✓ 采纳、✕ 忽略）。
// 遮挡、标注按素材时间画；音乐按成片时间存（不跟着剪掉 / 快放变速），画的时候换回素材时间的位置
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class TimelineView
    {
        // 遮挡、标注叠在一起时各占一整行（像剪映 / Premiere），轨道跟着变高；时间轴整体高度 ContentHeight，放不下就上下滚
        const int Row = 18, RowGap = 2, MusH = 24;
        int RedY => (ed.HasCamera ? LayY + LayH : KeyY + KeyH) + 4;
        int RedRowsN => Math.Max(1, RedRows(ed.Project).Values.DefaultIfEmpty(-1).Max() + 1);
        int AnnRowsN => Math.Max(1, AnnRows(ed.Project).Values.DefaultIfEmpty(-1).Max() + 1);
        int RedH => RedRowsN * Row + (RedRowsN - 1) * RowGap;
        int AnnH => AnnRowsN * Row + (AnnRowsN - 1) * RowGap;
        int AnnY => RedY + RedH + 4;
        int MusY => AnnY + AnnH + 4;
        public int ContentHeight => MusY + MusH + 6;
        double baseMusStart, baseMusIn, baseMusOut;

        // 时间上叠在一起的排成几行（贪心：放进第一行放得下的，放不下就新开一行）
        static Dictionary<string, int> Pack(IEnumerable<(string id, double s, double e)> items)
        {
            var rows = new Dictionary<string, int>(); var ends = new List<double>();
            foreach (var (id, st, en) in items.OrderBy(x => x.s))
            {
                int r = ends.FindIndex(x => x <= st + 1e-6);
                if (r < 0) { ends.Add(en); r = ends.Count - 1; } else ends[r] = en;
                rows[id] = r;
            }
            return rows;
        }
        Dictionary<string, int> AnnRows(StudioProject p) => Pack(p.Annotations.Select(a => (a.Id, a.Start, a.End)));
        Dictionary<string, int> RedRows(StudioProject p) => Pack(p.Redactions.Select(r => (r.Id, r.Start, r.End)));
        (float y, float h) AnnRow(int r, int n) => (AnnY + r * (Row + RowGap), Row);
        (float y, float h) RedRow(int r) => (RedY + r * (Row + RowGap), Row);

        void PaintW4(Graphics g, StudioProject p)
        {
            var map = new TimeMap(p);
            using (var lane = new SolidBrush(Color.FromArgb(Theme.Dark ? 30 : 18, Theme.Secondary)))
            {
                g.FillRectangle(lane, 1, RedY, ClientSize.Width - 2, RedH);
                g.FillRectangle(lane, 1, AnnY, ClientSize.Width - 2, AnnH);
                g.FillRectangle(lane, 1, MusY, ClientSize.Width - 2, MusH);
            }
            using (var lf = new Font("Segoe UI", 7f)) using (var lb = new SolidBrush(Color.FromArgb(150, Theme.Secondary)))
            {
                if (p.Redactions.Count == 0) g.DrawString(L("遮挡（播放栏「标注」→ 遮挡，在画面上框出来）", "Redact (Annotate → Redact in the play bar, then drag on the picture)"), lf, lb, 6, RedY + 3);
                if (p.Annotations.Count == 0) g.DrawString(L("标注（播放栏「标注」→ 箭头 / 框 / 圈 / 高亮 / 文字）", "Annotations (Annotate in the play bar: arrow / box / circle / highlight / text)"), lf, lb, 6, AnnY + 8);
                if (p.Music.Clips.Count == 0) g.DrawString(L("背景音乐（右边「声音」→ 选择音乐…）", "Music (Sound → Choose Music… on the right)"), lf, lb, 6, MusY + 5);
            }
            // 遮挡：深灰紫块，写样式
            var rrows = RedRows(p);
            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                foreach (var r in p.Redactions)
                {
                    float x0 = X(r.Start), x1 = Math.Max(x0 + 6, X(r.End));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    bool sel = r.Id == ed.SelectedRed;
                    var (ry0, rh0) = RedRow(rrows.TryGetValue(r.Id, out var rri) ? rri : 0);
                    var rc = new RectangleF(x0, ry0, x1 - x0, rh0);
                    using (var path = Theme.Round(rc, 4))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(sel ? 240 : 190, 90, 90, 110))) g.FillPath(b, path);
                        using (var hb = new HatchBrush(HatchStyle.SmallCheckerBoard, Color.FromArgb(60, Color.White), Color.Transparent)) g.FillPath(hb, path);
                        if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                    }
                    Handles(g, x0, x1, (int)ry0, (int)rh0);
                    if (rc.Width > 40) TextRenderer.DrawText(g, Redaction.Name(r.Style, L), f, Rectangle.Round(new RectangleF(rc.X + 6, rc.Y, rc.Width - 12, rc.Height)), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
            // 标注：每个标注一块（颜色就是标注的颜色），叠在一起的分行
            var rows = AnnRows(p); int nRows = rows.Count == 0 ? 1 : rows.Values.Max() + 1;
            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                foreach (var a in p.Annotations)
                {
                    float x0 = X(a.Start), x1 = Math.Max(x0 + 6, X(a.End));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    var (ry, rh) = AnnRow(rows.TryGetValue(a.Id, out var rr) ? rr : 0, nRows);
                    bool sel = a.Id == ed.SelectedAnn;
                    var col = Color.FromArgb((int)(a.Color.R * 255), (int)(a.Color.G * 255), (int)(a.Color.B * 255));
                    var rc = new RectangleF(x0, ry, x1 - x0, rh);
                    using (var path = Theme.Round(rc, Math.Min(4, rh / 2)))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(sel ? 245 : 200, col))) g.FillPath(b, path);
                        using (var pen = new Pen(sel ? Color.White : Color.FromArgb(90, 0, 0, 0), sel ? 2 : 1)) g.DrawPath(pen, path);
                    }
                    if (rh >= 14) Handles(g, x0, x1, (int)ry, (int)rh);
                    string label = a.Kind == "text" && !string.IsNullOrEmpty(a.Text) ? a.Text : Annotation.Name(a.Kind, L);
                    double lum = 0.299 * a.Color.R + 0.587 * a.Color.G + 0.114 * a.Color.B;
                    if (rc.Width > 30 && rh >= 9) TextRenderer.DrawText(g, label, f, Rectangle.Round(new RectangleF(rc.X + 5, rc.Y, rc.Width - 10, rc.Height)), lum > 0.6 ? Color.FromArgb(30, 30, 30) : Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
            // 音乐：橙色块 + 波形；循环出来的那几遍画淡一点
            var film = map.Out(p.TrimEnd);
            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                foreach (var o in p.Music.Occurrences(Math.Max(film, p.Music.Clips.Select(c => c.Start + c.Length).DefaultIfEmpty(0).Max())))
                {
                    var c = o.Clip; bool first = Math.Abs(o.At - c.Start) < 1e-6;
                    float x0 = X(map.Src(o.At)), x1 = Math.Max(x0 + 6, X(map.Src(o.At + o.Len)));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    bool sel = c.Id == ed.SelectedMusic && first;
                    var rc = new RectangleF(x0, MusY, x1 - x0, MusH);
                    using (var path = Theme.Round(rc, 5))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(first ? (sel ? 240 : 200) : 90, 240, 150, 40))) g.FillPath(b, path);
                        var peaks = ed.Peaks(c.File);
                        if (peaks != null && peaks.Length > 1)
                        {
                            var clip = g.Clip; g.SetClip(path);
                            using (var wp = new Pen(Color.FromArgb(first ? 200 : 110, Color.White), 1))
                                for (float x = Math.Max(x0 + 2, 0); x < Math.Min(x1 - 2, ClientSize.Width); x += 2)
                                {
                                    double ft = o.From + (x - x0) / Math.Max(1, x1 - x0) * o.Len;
                                    int i = (int)(ft * 50); if (i < 0 || i >= peaks.Length) continue;
                                    float amp = Math.Min(1, peaks[i] * 1.6f) * (MusH / 2f - 2);
                                    g.DrawLine(wp, x, MusY + MusH / 2f - amp, x, MusY + MusH / 2f + amp);
                                }
                            g.Clip = clip;
                        }
                        if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                    }
                    if (first)
                    {
                        Handles(g, x0, x1, MusY, MusH);
                        if (rc.Width > 50) TextRenderer.DrawText(g, "♪ " + c.Name, f, Rectangle.Round(new RectangleF(rc.X + 8, rc.Y, rc.Width - 16, rc.Height)), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    }
                }
            // 建议加速：剪辑轨上的虚线块，右端 ✓ 采纳、✕ 忽略
            using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                foreach (var sg in ed.Suggestions)
                {
                    float x0 = X(sg.Start), x1 = Math.Max(x0 + 40, X(sg.End));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    var rc = new RectangleF(x0, CutY, x1 - x0, CutH);
                    using (var path = Theme.Round(rc, 5))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(70, 40, 120, 230))) g.FillPath(b, path);
                        using (var pen = new Pen(Color.FromArgb(220, 40, 120, 230), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
                    }
                    var (acc, ign) = SugButtons(rc);
                    using (var ab = new SolidBrush(Color.FromArgb(52, 168, 83))) g.FillEllipse(ab, acc);
                    using (var ib = new SolidBrush(Color.FromArgb(150, 150, 155))) g.FillEllipse(ib, ign);
                    TextRenderer.DrawText(g, "✓", f, Rectangle.Round(acc), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, "✕", f, Rectangle.Round(ign), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (rc.Width > 90) TextRenderer.DrawText(g, (sg.Typing ? L("打字", "Typing") : L("空档", "Idle")) + " " + sg.Rate.ToString("0") + "×", f, Rectangle.Round(new RectangleF(rc.X + 6, rc.Y, rc.Width - 44, rc.Height)), Color.FromArgb(20, 80, 170), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
        }

        // 自测用：建议块的 ✓ 在哪、音乐段中间在哪（时间轴自己的坐标）
        public PointF SugAcceptAt(string id)
        {
            var sg = ed.Suggestions.First(x => x.Id == id);
            float x0 = X(sg.Start), x1 = Math.Max(x0 + 40, X(sg.End));
            var r = SugButtons(new RectangleF(x0, CutY, x1 - x0, CutH)).acc;
            return new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
        }
        public PointF MusicAt(string id)
        {
            var c = ed.Project.Music.Clips.First(x => x.Id == id); var m = new TimeMap(ed.Project);
            float x0 = X(m.Src(c.Start)), x1 = X(m.Src(Math.Min(c.Start + c.Length, m.Out(ed.Project.TrimEnd))));
            return new PointF((x0 + x1) / 2, MusY + MusH / 2f);
        }

        static (RectangleF acc, RectangleF ign) SugButtons(RectangleF rc)
        {
            float d = rc.Height - 4;
            return (new RectangleF(rc.Right - 2 * d - 6, rc.Y + 2, d, d), new RectangleF(rc.Right - d - 3, rc.Y + 2, d, d));
        }

        static void Handles(Graphics g, float x0, float x1, int y, int h)
        {
            using (var hb = new SolidBrush(Color.FromArgb(225, Color.White))) { g.FillRectangle(hb, x0 + 3, y + h / 2 - 4, 2, 8); g.FillRectangle(hb, x1 - 5, y + h / 2 - 4, 2, 8); }
        }

        Hit HitW4(Point pt, out string id)
        {
            id = null;
            var p = ed.Project;
            // 建议块上的 ✓ ✕
            if (pt.Y >= CutY && pt.Y <= CutY + CutH)
                foreach (var sg in ed.Suggestions)
                {
                    float x0 = X(sg.Start), x1 = Math.Max(x0 + 40, X(sg.End));
                    var (acc, ign) = SugButtons(new RectangleF(x0, CutY, x1 - x0, CutH));
                    if (acc.Contains(pt)) { id = sg.Id; return Hit.SugAccept; }
                    if (ign.Contains(pt)) { id = sg.Id; return Hit.SugIgnore; }
                }
            Hit Edge(float x0, float x1, Hit s, Hit e, Hit b)
            {
                if (x1 - x0 > EdgeW * 2.5f) { if (pt.X <= x0 + EdgeW) return s; if (pt.X >= x1 - EdgeW) return e; }
                return b;
            }
            if (pt.Y >= RedY && pt.Y <= RedY + RedH)
            {
                var rrows = RedRows(p);
                foreach (var r in p.Redactions)
                {
                    float x0 = X(r.Start), x1 = Math.Max(x0 + 6, X(r.End));
                    var (ry, rh) = RedRow(rrows.TryGetValue(r.Id, out var rr0) ? rr0 : 0);
                    if (pt.X < x0 - 2 || pt.X > x1 + 2 || pt.Y < ry - 1 || pt.Y > ry + rh + 1) continue;
                    id = r.Id; return Edge(x0, x1, Hit.RedStart, Hit.RedEnd, Hit.RedBody);
                }
            }
            if (pt.Y >= AnnY && pt.Y <= AnnY + AnnH)
            {
                var rows = AnnRows(p); int nRows = rows.Count == 0 ? 1 : rows.Values.Max() + 1;
                foreach (var a in p.Annotations)
                {
                    float x0 = X(a.Start), x1 = Math.Max(x0 + 6, X(a.End));
                    var (ry, rh) = AnnRow(rows.TryGetValue(a.Id, out var rr) ? rr : 0, nRows);
                    if (pt.X < x0 - 2 || pt.X > x1 + 2 || pt.Y < ry - 1 || pt.Y > ry + rh + 1) continue;
                    id = a.Id; return Edge(x0, x1, Hit.AnnStart, Hit.AnnEnd, Hit.AnnBody);
                }
            }
            if (pt.Y >= MusY && pt.Y <= MusY + MusH)
            {
                var map = new TimeMap(p);
                foreach (var c in p.Music.Clips)
                {
                    float x0 = X(map.Src(c.Start)), x1 = Math.Max(x0 + 6, X(map.Src(c.Start + c.Length)));
                    if (pt.X < x0 - 2 || pt.X > x1 + 2) continue;
                    id = c.Id; return Edge(x0, x1, Hit.MusStart, Hit.MusEnd, Hit.MusBody);
                }
            }
            return Hit.None;
        }

        void DownW4(Hit h, string id)
        {
            var p = ed.Project;
            switch (h)
            {
                case Hit.RedStart: case Hit.RedEnd: case Hit.RedBody:
                {
                    var r = p.Redactions.FirstOrDefault(x => x.Id == id); if (r != null) { baseStart = r.Start; baseEnd = r.End; }
                    ed.Select("red", id); if (r != null && (ed.Time < r.Start || ed.Time >= r.End)) ed.Seek(r.Start + Math.Min(0.3, (r.End - r.Start) / 2));
                    break;
                }
                case Hit.AnnStart: case Hit.AnnEnd: case Hit.AnnBody:
                {
                    var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a != null) { baseStart = a.Start; baseEnd = a.End; }
                    ed.Select("ann", id); if (a != null && (ed.Time < a.Start || ed.Time >= a.End)) ed.Seek(a.Start + Math.Min(0.5, (a.End - a.Start) / 2));
                    break;
                }
                case Hit.MusStart: case Hit.MusEnd: case Hit.MusBody:
                {
                    var c = p.Music.Clips.FirstOrDefault(x => x.Id == id); if (c != null) { baseMusStart = c.Start; baseMusIn = c.TrimIn; baseMusOut = c.TrimOut; }
                    ed.Select("music", id);
                    break;
                }
            }
        }

        Cursor CursorW4(Hit h) =>
            h == Hit.RedStart || h == Hit.RedEnd || h == Hit.AnnStart || h == Hit.AnnEnd || h == Hit.MusStart || h == Hit.MusEnd ? Cursors.SizeWE
            : h == Hit.RedBody || h == Hit.AnnBody || h == Hit.MusBody ? Cursors.SizeAll
            : h == Hit.SugAccept || h == Hit.SugIgnore ? Cursors.Hand : null;

        string TipW4(Hit h)
        {
            switch (h)
            {
                case Hit.RedStart: case Hit.RedEnd: case Hit.RedBody: return L("遮挡：拖两头改什么时候出现 / 消失，拖中间挪；选中后在画面上拖它的位置和大小", "Redaction: drag the ends or the middle; select it to move / resize it on the picture");
                case Hit.AnnStart: case Hit.AnnEnd: case Hit.AnnBody: return L("标注：拖两头改出现多久，拖中间挪；双击文字改字；Ctrl+D 复制", "Annotation: drag the ends or the middle; double-click text to edit; Ctrl+D duplicates");
                case Hit.MusStart: case Hit.MusEnd: case Hit.MusBody: return L("背景音乐：拖动挪位置，拖两头剪掉不要的开头结尾", "Music: drag to move, drag the ends to trim");
                case Hit.SugAccept: return L("采纳：变成快放段（能撤销）", "Accept: turn it into a speed-up (undoable)");
                case Hit.SugIgnore: return L("忽略这条建议", "Ignore this suggestion");
            }
            return null;
        }

        void DragW4(Hit h, string id, Func<double, double> Dt)
        {
            double D0 = ed.Duration;
            switch (h)
            {
                case Hit.RedBody: case Hit.RedStart: case Hit.RedEnd:
                {
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var r = p.Redactions.FirstOrDefault(x => x.Id == id); if (r == null) return;
                        (r.Start, r.End) = Span(h == Hit.RedBody ? 0 : h == Hit.RedStart ? 1 : 2, Dt, D0);
                    });
                    break;
                }
                case Hit.AnnBody: case Hit.AnnStart: case Hit.AnnEnd:
                {
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a == null) return;
                        (a.Start, a.End) = Span(h == Hit.AnnBody ? 0 : h == Hit.AnnStart ? 1 : 2, Dt, D0);
                    });
                    break;
                }
                case Hit.MusBody:
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Music.Clips.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        var m = new TimeMap(p);
                        c.Start = Math.Max(0, m.Out(Math.Min(Math.Max(0, m.Src(baseMusStart) + Dt(m.Src(baseMusStart))), D0)));
                    });
                    break;
                case Hit.MusStart: case Hit.MusEnd:
                    bool isStart = h == Hit.MusStart;
                    ed.DragBegin();
                    ed.DragUpdate(p =>
                    {
                        var c = p.Music.Clips.FirstOrDefault(x => x.Id == id); if (c == null) return;
                        var m = new TimeMap(p); const double minLen = 0.5;
                        double len0 = baseMusOut - baseMusIn;
                        if (isStart)
                        {
                            double ns = m.Out(Math.Max(0, m.Src(baseMusStart) + Dt(m.Src(baseMusStart))));
                            double delta = Math.Min(Math.Max(ns - baseMusStart, -baseMusIn), len0 - minLen);
                            c.Start = baseMusStart + delta; c.TrimIn = baseMusIn + delta; c.TrimOut = baseMusOut;
                        }
                        else
                        {
                            double endOut = m.Out(Math.Max(0, m.Src(baseMusStart + len0) + Dt(m.Src(baseMusStart + len0))));
                            c.TrimOut = Math.Min(Math.Max(baseMusIn + minLen, baseMusIn + (endOut - baseMusStart)), c.FileLength > 0 ? c.FileLength : double.MaxValue);
                        }
                    });
                    break;
            }
        }

        // 遮挡 / 标注拖动：0 = 整体挪，1 = 拖开头，2 = 拖结尾（最短 0.3 秒，不出素材）
        (double s, double e) Span(int mode, Func<double, double> Dt, double d)
        {
            double len = baseEnd - baseStart;
            if (mode == 0) { double ns = Math.Min(Math.Max(baseStart + Dt(baseStart), 0), Math.Max(0, d - len)); return (ns, ns + len); }
            if (mode == 1) return (Math.Min(Math.Max(baseStart + Dt(baseStart), 0), baseEnd - 0.3), baseEnd);
            return (baseStart, Math.Max(Math.Min(baseEnd + Dt(baseEnd), d), baseStart + 0.3));
        }
    }
}
