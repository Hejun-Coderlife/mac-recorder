// 时间轴的 W5 部分（照 Mac 版）：剪辑轨改成剪映那样一段段的片段（删掉的地方合拢、留小剪刀记号可恢复）、
// 「打字声」音效轨（一阵打字一块、每下按键一根竖线）、右键菜单、左边轨道名要的各条轨位置
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
        bool Removed(double t) => ed.Project.Cuts.Any(c => !c.Speed && t >= c.Start && t < c.End);

        // ---- 剪辑轨：片段 + 删掉处的小剪刀 ----
        void PaintClips(Graphics g, StudioProject p)
        {
            using (var lane = new SolidBrush(Color.FromArgb(Theme.Dark ? 30 : 18, Theme.Secondary))) g.FillRectangle(lane, 1, CutY, ClientSize.Width - 2, CutH);
            using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                foreach (var pc in Pieces.List(p, ed.Duration))
                {
                    float x0 = X(pc.Start) + 1, x1 = Math.Max(x0 + 3, X(pc.End) - 1);
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    bool sel = ed.SelectedPiece is double sp && Math.Abs(sp - pc.Start) < 1e-6;
                    bool fast = Math.Abs(pc.Rate - 1) > 1e-6;
                    var col = fast ? Color.FromArgb(52, 133, 245) : Color.FromArgb(107, 112, 128);
                    var r = new RectangleF(x0, CutY, x1 - x0, CutH);
                    using (var path = Theme.Round(r, 5))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(sel ? 242 : 140, col))) g.FillPath(b, path);
                        using (var pen = new Pen(sel ? Color.White : Color.FromArgb(46, Color.White), sel ? 2 : 1)) g.DrawPath(pen, path);
                    }
                    if (fast && r.Width > 30)
                        TextRenderer.DrawText(g, "▶▶ " + pc.Rate.ToString("0.#") + "×", f, Rectangle.Round(r), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
            foreach (var c in Pieces.Removed(p))
            {
                if (c.End <= p.TrimStart || c.Start >= p.TrimEnd) continue;
                var rc = SeamRect(c);
                bool sel = c.Id == ed.SelectedCut;
                using (var b = new SolidBrush(Color.FromArgb(235, 69, 69))) g.FillEllipse(b, rc);
                if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawEllipse(pen, rc);
                using (var f = new Font("Segoe UI Symbol", 7f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "✂", f, Rectangle.Round(rc), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        RectangleF SeamRect(CutSegment c) { float x = X(c.Start); return new RectangleF(x - 8, CutY + (CutH - 16) / 2f, 16, 16); }

        Hit HitClips(Point pt, out string id, out double pieceStart)
        {
            id = null; pieceStart = 0;
            if (pt.Y < CutY || pt.Y > CutY + CutH) return Hit.None;
            // 建议加速块上的 ✓ ✕ 让给 W4 那边处理
            foreach (var sg in ed.Suggestions) { float a = X(sg.Start), b = Math.Max(a + 40, X(sg.End)); if (pt.X >= a && pt.X <= b) return Hit.None; }
            var p = ed.Project;
            foreach (var c in Pieces.Removed(p))
                if (c.End > p.TrimStart && c.Start < p.TrimEnd && SeamRect(c).Contains(pt)) { id = c.Id; return Hit.Seam; }
            foreach (var pc in Pieces.List(p, ed.Duration))
                if (pt.X >= X(pc.Start) && pt.X <= X(pc.End)) { pieceStart = pc.Start; return Hit.Piece; }
            return Hit.None;
        }

        // ---- 打字声轨：一阵打字一块，里面的竖线是每一下按键；去掉的画灰、被单独静音的那几下画淡 ----
        void PaintBursts(Graphics g, StudioProject p)
        {
            using (var lane = new SolidBrush(Color.FromArgb(Theme.Dark ? 30 : 18, Theme.Secondary))) g.FillRectangle(lane, 1, KeyY, ClientSize.Width - 2, KeyH);
            var bursts = Pieces.Bursts(ed.Track, p);
            if (bursts.Count == 0)
                using (var lf = new Font("Segoe UI", 7f)) using (var lb = new SolidBrush(Color.FromArgb(150, Theme.Secondary)))
                    g.DrawString(ed.Track.Keys.Count == 0 ? L("打字声（录制时没有打字）", "Typing sound (no typing recorded)") : L("打字声", "Typing sound"), lf, lb, 6, KeyY + 3);
            var tint = Color.FromArgb(140, 107, 242);
            using (var f = new Font("Segoe UI", 7f, FontStyle.Bold))
                foreach (var b in bursts)
                {
                    float x0 = X(b.Start), x1 = Math.Max(x0 + 8, X(b.End));
                    if (x1 < -2 || x0 > ClientSize.Width + 2) continue;
                    bool sel = ed.SelectedBurst is double sb && Math.Abs(sb - b.Start) < 1e-6;
                    bool on = Pieces.BurstOn(p, b);
                    var r = new RectangleF(x0, KeyY, x1 - x0, KeyH);
                    using (var path = Theme.Round(r, 5))
                    {
                        using (var br = new SolidBrush(on ? Color.FromArgb(sel ? 242 : 190, tint) : Color.FromArgb(sel ? 90 : 46, Theme.Secondary))) g.FillPath(br, path);
                        if (sel) using (var pen = new Pen(Color.White, 2)) g.DrawPath(pen, path);
                        var clip = g.Clip; g.SetClip(path);
                        foreach (var k in b.Keys)
                        {
                            float kx = X(k);
                            using (var kb = new SolidBrush(Color.FromArgb(Pieces.KeySounds(p, k) ? 230 : 64, Color.White))) g.FillRectangle(kb, kx - 0.6f, KeyY + KeyH * 0.25f, 1.2f, KeyH * 0.5f);
                        }
                        g.Clip = clip;
                    }
                    double rate = Pieces.BurstRate(p, b);
                    if (Math.Abs(rate - 1) > 1e-6 && r.Width > 28)
                        TextRenderer.DrawText(g, rate.ToString("0.#") + "×", f, Rectangle.Round(new RectangleF(r.Right - 30, r.Y, 28, r.Height)), Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    if (!on && r.Width > 18)
                        TextRenderer.DrawText(g, "🔇", f, Rectangle.Round(new RectangleF(r.X + 2, r.Y, 16, r.Height)), Theme.Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
        }
        double? HitBurst(Point pt)
        {
            foreach (var b in Pieces.Bursts(ed.Track, ed.Project))
            {
                float x0 = X(b.Start), x1 = Math.Max(x0 + 8, X(b.End));
                if (pt.X >= x0 - 2 && pt.X <= x1 + 2) return b.Start;
            }
            return null;
        }

        // 自测用：片段 / 打字声块中间在哪（时间轴自己的坐标）
        public PointF ClipAt(double t) => new PointF(X(t), CutY + CutH / 2f);
        public PointF BurstAt(double t) => new PointF(X(t), KeyY + KeyH / 2f);

        // ---- 各条轨的位置（左边轨道名照这个画） ----
        public List<(int y, int h, string name)> Lanes()
        {
            var l = new List<(int, int, string)>
            {
                (CutY, CutH, "✂ " + L("片段", "Clips")), (SegY, SegH, "🔍 " + L("推近", "Zoom")), (VoiceY, VoiceH, "🎙 " + L("讲解", "Voice")),
                (CapY, CapH, "💬 " + L("字幕", "Captions")), (KeyY, KeyH, "⌨ " + L("打字声", "Typing")),
            };
            if (ed.HasCamera) l.Add((LayY, LayH, "👤 " + L("布局", "Camera")));
            l.Add((RedY, RedH, "▦ " + L("遮挡", "Redact"))); l.Add((AnnY, AnnH, "✎ " + L("标注", "Notes"))); l.Add((MusY, MusH, "♪ " + L("音乐", "Music")));
            return l;
        }

        // ---- 右键菜单（同 Mac 版）：点到什么就是什么的菜单，空白处是常用操作 ----
        void ContextMenuAt(Point pt)
        {
            var h = HitTest(pt, out var id);
            double t = T(pt.X);
            var m = new ContextMenuStrip();
            void Item(ToolStripItemCollection to, string text, Action a, bool enabled = true) { var it = new ToolStripMenuItem(text) { Enabled = enabled }; it.Click += (s, e) => a(); to.Add(it); }
            void Speeds(ToolStripItemCollection to, double[] rates, double cur, Action<double> set)
            {
                var sub = new ToolStripMenuItem(L("速度", "Speed"));
                foreach (var r in rates) { var it = new ToolStripMenuItem(r.ToString("0.#") + "×") { Checked = Math.Abs(r - cur) < 1e-6 }; double rr = r; it.Click += (s, e) => set(rr); sub.DropDownItems.Add(it); }
                to.Add(sub);
            }
            var p = ed.Project;
            switch (h)
            {
                case Hit.Piece:
                {
                    ed.SelectPiece(hitPiece);
                    var pc = ed.CurrentPiece;
                    if (pc is Piece pp)
                    {
                        Item(m.Items, L("在播放头分割（S）", "Split at Playhead (S)"), () => ed.Split(ed.Time));
                        Speeds(m.Items, new[] { 1.0, 1.5, 2, 4, 8 }, pp.Rate, r => ed.SetPieceSpeed(pp, r));
                        m.Items.Add(new ToolStripSeparator());
                        Item(m.Items, L("删除这段", "Delete Part"), () => ed.DeletePiece(pp));
                    }
                    break;
                }
                case Hit.Seam:
                    ed.Select("cut", id);
                    Item(m.Items, L("恢复这段", "Restore"), () => ed.RestoreCut(id));
                    break;
                case Hit.Burst:
                {
                    ed.SelectBurst(hitPiece);
                    if (ed.CurrentBurst is KeyBurst b)
                    {
                        bool on = Pieces.BurstOn(p, b);
                        Item(m.Items, on ? L("去掉这阵打字声", "Remove Typing Sound") : L("恢复这阵打字声", "Restore Typing Sound"), () => ed.SetBurstSound(b, !on));
                        Speeds(m.Items, new[] { 1.0, 1.5, 2, 3, 4 }, Pieces.BurstRate(p, b), r => ed.SetBurstRate(b, r));
                    }
                    break;
                }
                case Hit.SegBody: case Hit.SegStart: case Hit.SegEnd:
                {
                    ed.Select("seg", id);
                    var sg = p.Segments.FirstOrDefault(x => x.Id == id); if (sg == null) break;
                    var sub = new ToolStripMenuItem(L("放大", "Zoom"));
                    foreach (var z in new[] { 1.5, 2.0, 2.5, 3.0 }) { var it = new ToolStripMenuItem(z.ToString("0.0") + "×") { Checked = Math.Abs(sg.Zoom - z) < 1e-6 }; double zz = z; it.Click += (s, e) => ed.Edit(q => { var x = q.Segments.FirstOrDefault(y => y.Id == id); if (x != null) x.Zoom = zz; }); sub.DropDownItems.Add(it); }
                    m.Items.Add(sub);
                    Item(m.Items, sg.Fixed ? L("改成跟着鼠标", "Follow Cursor") : L("改成固定位置", "Fixed Position"), () => ed.Edit(q => { var x = q.Segments.FirstOrDefault(y => y.Id == id); if (x != null) x.Fixed = !x.Fixed; }));
                    m.Items.Add(new ToolStripSeparator());
                    Item(m.Items, L("删除这段推近", "Delete Zoom"), () => ed.DeleteSelected());
                    break;
                }
                case Hit.CapBody: case Hit.CapStart: case Hit.CapEnd:
                    ed.Select("cap", id);
                    Item(m.Items, L("改字", "Edit Text"), () => ed.EditCaptionText());
                    m.Items.Add(new ToolStripSeparator());
                    Item(m.Items, L("删除", "Delete"), () => ed.DeleteSelected());
                    break;
                case Hit.VoiceBody: case Hit.VoiceStart: case Hit.VoiceEnd:
                    ed.Select("voice", id);
                    Item(m.Items, L("删除这段讲解", "Delete Voiceover"), () => ed.DeleteSelected());
                    break;
                case Hit.LayBody: case Hit.LayStart: case Hit.LayEnd:
                    ed.Select("layout", id);
                    Item(m.Items, L("删除这段布局", "Delete Layout Part"), () => ed.DeleteSelected());
                    break;
                case Hit.MusBody: case Hit.MusStart: case Hit.MusEnd:
                    ed.Select("music", id);
                    Item(m.Items, L("删除这段音乐", "Delete Music"), () => ed.DeleteSelected());
                    break;
                case Hit.RedBody: case Hit.RedStart: case Hit.RedEnd: case Hit.AnnBody: case Hit.AnnStart: case Hit.AnnEnd:
                {
                    bool red = h == Hit.RedBody || h == Hit.RedStart || h == Hit.RedEnd;
                    ed.Select(red ? "red" : "ann", id);
                    if (!red && p.Annotations.FirstOrDefault(a => a.Id == id)?.Kind == "text") Item(m.Items, L("改字", "Edit Text"), () => ed.EditAnnotationText());
                    Item(m.Items, L("复制一份", "Duplicate"), () => ed.DuplicateOverlay());
                    Item(m.Items, L("移到播放头", "Move to Playhead"), () => ed.MoveOverlay(id, ed.Time));
                    m.Items.Add(new ToolStripSeparator());
                    Item(m.Items, L("删除", "Delete"), () => ed.DeleteSelected());
                    break;
                }
                default:
                    Item(m.Items, L("在播放头分割（S）", "Split at Playhead (S)"), () => ed.Split(ed.Time));
                    Item(m.Items, L("在播放头加推近", "Add Zoom at Playhead"), () => ed.AddSegment(ed.Time));
                    Item(m.Items, L("在播放头加字幕", "Add Caption at Playhead"), () => ed.AddCaption(ed.Time));
                    m.Items.Add(new ToolStripSeparator());
                    Item(m.Items, L("建议加速", "Suggest Speed-ups"), () => ed.SuggestSpeed(), !ed.Suggesting);
                    Item(m.Items, L("重新自动生成推近", "Regenerate Zooms"), () => ed.Regenerate());
                    break;
            }
            if (m.Items.Count == 0) { m.Dispose(); return; }
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, pt);
        }
    }

    // 时间轴左边的轨道名（像剪映左边那列）：跟着时间轴各条轨的位置画
    sealed class TrackHeaders : Control
    {
        readonly TimelineView tl;
        public TrackHeaders(TimelineView tl)
        {
            this.tl = tl;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Window);
            using (var f = new Font("Segoe UI", 7.5f))
                foreach (var (y, h, name) in tl.Lanes())
                    TextRenderer.DrawText(g, name, f, new Rectangle(0, y, Width - 4, h), Theme.Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}
