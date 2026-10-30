// 剪辑窗口的 W5 部分（照 Mac 版）：剪映式剪辑（分割 S / Ctrl+B、选中片段 Delete 删掉、改速度、剪刀记号恢复）、
// 「打字声」轨（去掉 / 恢复一阵打字声、给一阵打字改速度）、一键加速菜单、提示条（toast）、
// 时间轴区域：左边轨道名 + 上下滚（叠在一起的遮挡 / 标注各占一行）、和预览之间的分隔条可拖（记住高度）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor
    {
        public double? SelectedPiece;          // 剪辑轨上选中的那段（起点）
        public double? SelectedBurst;          // 打字声轨上选中的那阵（起点）

        public List<Piece> PieceList => Pieces.List(Project, Duration);
        public Piece? CurrentPiece { get { if (!(SelectedPiece is double s)) return null; foreach (var p in PieceList) if (Math.Abs(p.Start - s) < 1e-6) return p; return null; } }
        public KeyBurst CurrentBurst => SelectedBurst is double s ? Pieces.Bursts(Track, Project).FirstOrDefault(b => Math.Abs(b.Start - s) < 1e-6) : null;

        public void SelectPiece(double start) { Select(null, null); SelectedPiece = start; Touch(); }
        public void SelectBurst(double start) { Select(null, null); SelectedBurst = start; Touch(); }

        // ---- 剪映式剪辑 ----
        public void Split(double t)
        {
            var probe = Project.Clone();
            if (!Pieces.Split(probe, t, Duration)) { System.Media.SystemSounds.Beep.Play(); return; }
            Edit(p => Pieces.Split(p, t, Duration));
            SelectPiece(t);
            Toast(L($"已在 {Clock(t)} 分割 · 点一段按 Delete 删掉", $"Split at {Clock(t)} · select a part and press Delete to remove it"));
        }
        public void DeletePiece(Piece pc)
        {
            Edit(p => Pieces.Delete(p, pc.Start, pc.End));
            Select(null, null);
            Toast(L($"删掉了 {pc.Len:0.0} 秒 · Ctrl+Z 撤销，或点剪辑轨上的 ✂ 恢复", $"Removed {pc.Len:0.0}s · Ctrl+Z to undo, or click the ✂ mark to restore"));
            if (Time >= pc.Start && Time < pc.End) Seek(Pieces.SrcTime(Project, Pieces.VisTime(Project, pc.Start), Duration));
        }
        public void SetPieceSpeed(Piece pc, double rate) { Edit(p => Pieces.SetSpeed(p, pc.Start, pc.End, rate)); SelectPiece(pc.Start); }
        public void RestoreCut(string id) { Edit(p => p.Cuts.RemoveAll(c => c.Id == id)); Select(null, null); }

        // ---- 打字声 ----
        public void SetBurstSound(KeyBurst b, bool on)
        {
            Edit(p => Pieces.SetBurstSound(p, b, on));
            SelectBurst(b.Start);
            Toast(on ? L("这阵打字声恢复了", "Typing sound restored here") : L("这阵打字声去掉了 · 再按 Delete 恢复", "Typing sound removed here · press Delete again to restore"));
        }
        public void SetBurstRate(KeyBurst b, double rate)
        {
            Edit(p => Pieces.SetBurstRate(p, b, rate));
            SelectBurst(b.Start);
            Toast(Math.Abs(rate - 1) < 1e-6 ? L("这阵打字恢复原速", "Back to normal speed") : L($"这阵打字 {rate:0.#}× 快放（画面和打字声一起）· Ctrl+Z 撤销", $"This typing plays {rate:0.#}× (picture and sound) · Ctrl+Z to undo"));
        }

        // Delete 键：片段 / 打字声 / 剪刀记号先处理（返回 true = 处理了）
        bool DeleteSelectedW5()
        {
            if (CurrentPiece is Piece pc) { DeletePiece(pc); return true; }
            if (CurrentBurst is KeyBurst b) { SetBurstSound(b, !Pieces.BurstOn(Project, b)); return true; }
            if (SelectedCut != null && Project.Cuts.FirstOrDefault(c => c.Id == SelectedCut) is CutSegment cs && !cs.Speed) { RestoreCut(cs.Id); return true; }
            return false;
        }

        // 整段挪到从 t 开始（长度不变）
        public void MoveOverlay(string id, double t)
        {
            Edit(p =>
            {
                foreach (var r in p.Redactions.Where(x => x.Id == id)) { double l = r.End - r.Start; r.Start = Math.Min(t, Duration - l); r.End = r.Start + l; }
                foreach (var a in p.Annotations.Where(x => x.Id == id)) { double l = a.End - a.Start; a.Start = Math.Min(t, Duration - l); a.End = a.Start + l; }
            });
        }

        // ---- 一键加速：打字的地方 / 很久没动的空档 全部加速（倍数自己选），能撤销 ----
        void ShowSpeedMenu()
        {
            var m = new ContextMenuStrip();
            var find = new ToolStripMenuItem(L("找可以加速的地方（逐个确认）", "Find Parts to Speed Up (review each)")); find.Click += (s, e) => SuggestSpeed(); m.Items.Add(find);
            m.Items.Add(new ToolStripSeparator());
            var typing = new ToolStripMenuItem(L("打字的地方全部加速", "Speed Up All Typing"));
            foreach (var r in new[] { 1.5, 2, 3, 4 }) { double rr = r; var it = new ToolStripMenuItem(r.ToString("0.#") + "×"); it.Click += (s, e) => AutoSpeed(true, rr); typing.DropDownItems.Add(it); }
            var idle = new ToolStripMenuItem(L("很久没动的空档全部加速", "Speed Up All Idle Parts"));
            foreach (var r in new[] { 2.0, 4, 8 }) { double rr = r; var it = new ToolStripMenuItem(r.ToString("0.#") + "×"); it.Click += (s, e) => AutoSpeed(false, rr); idle.DropDownItems.Add(it); }
            m.Items.Add(typing); m.Items.Add(idle);
            foreach (ToolStripItem it in m.Items) it.Enabled = !Suggesting;
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(suggestBtn, new Point(0, suggestBtn.Height));
        }
        public void AutoSpeed(bool typing, double rate)
        {
            if (Suggesting) return;
            Suggesting = true; Suggestions = new List<SpeedSuggestion>();
            double a = Project.TrimStart, b = Math.Max(Project.TrimStart, Project.TrimEnd);
            var cuts = Project.Cuts.Select(c => c.Clone()).ToList(); var tr = Track; var raw = Raw;
            Touch();
            Task.Run(() =>
            {
                List<SpeedSuggestion> found;
                try { found = SpeedSuggest.Find(tr, SpeedSuggest.FrameDiffs(raw, a, b), a, b, cuts).Where(g => g.Typing == typing).ToList(); } catch { found = new List<SpeedSuggestion>(); }
                try { BeginInvoke((Action)(() => { Suggesting = false; ApplyAutoSpeed(found, typing, rate); })); } catch { }
            });
        }
        public void ApplyAutoSpeed(List<SpeedSuggestion> found, bool typing, double rate)
        {
            var use = found.Where(g => !Project.Cuts.Any(c => c.Start < g.End && c.End > g.Start)).ToList();
            if (use.Count == 0)
            {
                Toast(found.Count == 0 ? (typing ? L("没找到连续打字的地方", "No long typing found") : L("没找到很久没动的空档", "No long idle stretches found"))
                                       : L("这些地方已经剪过 / 加速过了", "Those parts are already cut or sped up"));
                Touch(); return;
            }
            double before = new TimeMap(Project).Out(Project.TrimEnd);
            Edit(p =>
            {
                foreach (var g in use) p.Cuts.Add(new CutSegment { Start = Math.Max(g.Start, p.TrimStart), End = Math.Min(g.End, p.TrimEnd), Speed = true, Rate = rate });
                p.Cuts = p.Cuts.OrderBy(c => c.Start).ToList();
            });
            double saved = before - new TimeMap(Project).Out(Project.TrimEnd);
            Toast(L($"{use.Count} 处{(typing ? "打字" : "空档")}已加速 {rate:0.#}×，成片短了 {saved:0.0} 秒 · Ctrl+Z 撤销",
                    $"Sped up {use.Count} {(typing ? "typing" : "idle")} parts {rate:0.#}×, {saved:0.0}s shorter · Ctrl+Z to undo"));
        }

        // ---- 提示条：预览下方一句话，几秒后消失 ----
        readonly Label toast = new Label();
        readonly Timer toastTimer = new Timer { Interval = 3500 };
        public string LastToast;
        public void Toast(string text)
        {
            LastToast = text;
            toast.Text = text; toast.Visible = true; toast.BringToFront(); PlaceToast();
            toastTimer.Stop(); toastTimer.Start();
        }
        void PlaceToast()
        {
            if (toast.Parent == null) return;
            var sz = TextRenderer.MeasureText(toast.Text, toast.Font);
            toast.Size = new Size(Math.Min(toast.Parent.ClientSize.Width - 40, sz.Width + 28), sz.Height + 14);
            toast.Location = new Point((toast.Parent.ClientSize.Width - toast.Width) / 2, toast.Parent.ClientSize.Height - toast.Height - 28);
        }

        // ---- 时间轴区域：轨道名 + 时间轴放进能上下滚的框里；分隔条拖动改高度 ----
        Panel tlHost; TrackHeaders headers; Panel tlAreaW5;
        const int HeaderW = 66;
        public static int DefaultTimelineArea(bool camera) => 248 + 84 + (camera ? 26 : 0) + 22;
        void InitW5(Panel tlArea, Panel pv, Panel left)
        {
            tlAreaW5 = tlArea;
            // 提示条
            toast.AutoSize = false; toast.TextAlign = ContentAlignment.MiddleCenter; toast.Visible = false;
            toast.BackColor = Theme.Dark ? Color.FromArgb(70, 70, 76) : Color.FromArgb(40, 40, 44); toast.ForeColor = Color.White;
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); toast.Visible = false; };
            pv.Controls.Add(toast); pv.Resize += (s, e) => PlaceToast();
            // 能上下滚的时间轴：左边轨道名、右边时间轴，高度 = 时间轴内容的高度
            tlHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Window };
            headers = new TrackHeaders(Timeline) { Location = new Point(0, 0), Width = HeaderW };
            Timeline.Dock = DockStyle.None; Timeline.Location = new Point(HeaderW, 0);
            tlHost.Controls.Add(headers); tlHost.Controls.Add(Timeline);
            tlHost.Resize += (s, e) => SizeTimeline();
            Changed += SizeTimeline;
            tlArea.Controls.Add(tlHost); tlHost.BringToFront();
            // 分隔条：上下拖改时间轴区域的高度（记住），双击恢复
            var split = new Panel { Dock = DockStyle.Bottom, Height = 9, Cursor = Cursors.HSplit, BackColor = Theme.Window };
            split.Paint += (s, e) => { using (var b = new SolidBrush(Color.FromArgb(120, Theme.Secondary))) { var r = new Rectangle(split.Width / 2 - 18, 3, 36, 3); e.Graphics.FillRectangle(b, r); } };
            int downY = 0, baseH = 0; bool down = false;
            split.MouseDown += (s, e) => { if (e.Button != MouseButtons.Left) return; down = true; downY = Cursor.Position.Y; baseH = tlArea.Height; };
            split.MouseMove += (s, e) => { if (!down) return; int h = baseH - (Cursor.Position.Y - downY); tlArea.Height = Math.Min(Math.Max(h, 150), Math.Max(150, left.ClientSize.Height - 200)); };
            split.MouseUp += (s, e) => { if (!down) return; down = false; Settings.SetString("editorTimelineH", tlArea.Height.ToString()); };
            split.DoubleClick += (s, e) => { tlArea.Height = DefaultTimelineArea(HasCamera); Settings.SetString("editorTimelineH", ""); };
            tips.SetToolTip(split, L("上下拖动：调预览和时间轴的大小；双击恢复", "Drag up or down to resize the preview and timeline; double-click to reset"));
            left.Controls.Add(split); split.BringToFront(); pv.BringToFront();
            if (int.TryParse(Settings.GetString("editorTimelineH", ""), out var saved) && saved >= 150) tlArea.Height = saved;
        }
        void SizeTimeline()
        {
            if (tlHost == null || IsDisposed) return;
            int h = Timeline.ContentHeight;
            bool scroll = h > tlHost.ClientSize.Height;
            int w = Math.Max(100, tlHost.ClientSize.Width - HeaderW - (scroll && !tlHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            if (Timeline.Height != h || Timeline.Width != w) { Timeline.Size = new Size(w, h); headers.Height = h; }
            headers.Invalidate();
        }

        // ---- 属性条：选中片段 / 打字声时出现 ----
        readonly Panel pieceInspector = new Panel(), burstInspector = new Panel();
        readonly Label pieceTime = new Label(), pieceLen = new Label(), burstTime = new Label(), burstCount = new Label();
        readonly Button[] pieceRates = new Button[5], burstRates = new Button[5];
        readonly Button pieceDelete = new Button(), burstToggle = new Button(), cutRestore = new Button();
        static readonly double[] PieceRateList = { 1, 1.5, 2, 4, 8 }, BurstRateList = { 1, 1.5, 2, 3, 4 };
        void BuildW5Inspectors(Panel tlArea)
        {
            Label Bold(string t) => new Label { Text = t, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 8, 0) };
            foreach (var (panel, title, time, rates, list, extra, right, isPiece) in new[]
            {
                (pieceInspector, L("片段", "Clip"), pieceTime, pieceRates, PieceRateList, pieceLen, pieceDelete, true),
                (burstInspector, "⌨ " + L("打字声", "Typing sound"), burstTime, burstRates, BurstRateList, burstCount, burstToggle, false),
            })
            {
                panel.Dock = DockStyle.Top; panel.Height = 34; panel.Visible = false;
                var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
                time.AutoSize = true; time.ForeColor = Theme.Secondary; time.Margin = new Padding(0, 8, 14, 0);
                extra.AutoSize = true; extra.ForeColor = Theme.Secondary; extra.Margin = new Padding(8, 8, 0, 0);
                flow.Controls.Add(Bold(title)); flow.Controls.Add(time);
                flow.Controls.Add(new Label { Text = L("速度", "Speed"), AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
                for (int i = 0; i < rates.Length; i++)
                {
                    double r = list[i]; var b = new Button(); rates[i] = b;
                    Flat(b, r.ToString("0.#") + "×", (s, e) => { if (isPiece) { if (CurrentPiece is Piece pc) SetPieceSpeed(pc, r); } else if (CurrentBurst is KeyBurst kb) SetBurstRate(kb, r); });
                    flow.Controls.Add(b);
                }
                flow.Controls.Add(extra);
                panel.Controls.Add(flow);
                right.Dock = DockStyle.Right; panel.Controls.Add(right);
                tlArea.Controls.Add(panel);
            }
            Flat(pieceDelete, "🗑 " + L("删除这段", "Delete Part"), (s, e) => { if (CurrentPiece is Piece pc) DeletePiece(pc); });
            tips.SetToolTip(pieceDelete, L("删掉这段（Delete），时间轴直接合拢；Ctrl+Z 撤销", "Remove this part (Delete); the timeline closes up. Ctrl+Z to undo"));
            Flat(burstToggle, "", (s, e) => { if (CurrentBurst is KeyBurst kb) SetBurstSound(kb, !Pieces.BurstOn(Project, kb)); });
            // 删掉的段（小剪刀）选中时：剪辑属性条里只给「恢复」
            Flat(cutRestore, "↶ " + L("恢复这段", "Restore"), (s, e) => { if (SelectedCut != null) RestoreCut(SelectedCut); });
            cutRestore.Dock = DockStyle.Right; cutRestore.Visible = false; cutInspector.Controls.Add(cutRestore);
        }
        void SyncW5()
        {
            var pc = CurrentPiece;
            pieceInspector.Visible = pc != null;
            if (pc is Piece p)
            {
                pieceTime.Text = Clock(p.Start) + " – " + Clock(p.End);
                for (int i = 0; i < pieceRates.Length; i++) { bool on = Math.Abs(PieceRateList[i] - p.Rate) < 1e-6; pieceRates[i].BackColor = on ? Theme.Accent : Theme.Control; pieceRates[i].ForeColor = on ? Color.White : Theme.Text; }
                pieceLen.Text = Math.Abs(p.Rate - 1) < 1e-6 ? L($"{p.Len:0.0} 秒", $"{p.Len:0.0}s") : L($"{p.Len:0.0} 秒 → {p.Len / p.Rate:0.0} 秒", $"{p.Len:0.0}s → {p.Len / p.Rate:0.0}s");
            }
            var b = CurrentBurst;
            burstInspector.Visible = b != null;
            if (b != null)
            {
                bool on = Pieces.BurstOn(Project, b); double rate = Pieces.BurstRate(Project, b);
                burstTime.Text = Clock(Math.Max(0, b.Start)) + " – " + Clock(b.End);
                burstCount.Text = L($"{b.Keys.Count} 下", $"{b.Keys.Count} keys");
                for (int i = 0; i < burstRates.Length; i++) { bool sel = Math.Abs(BurstRateList[i] - rate) < 1e-6; burstRates[i].BackColor = sel ? Theme.Accent : Theme.Control; burstRates[i].ForeColor = sel ? Color.White : Theme.Text; }
                burstToggle.Text = on ? "🔇 " + L("去掉这阵打字声", "Remove") : "🔊 " + L("恢复", "Restore");
            }
            // 选中的是删掉的段（小剪刀）：只显示恢复
            var cut = Project.Cuts.FirstOrDefault(c => c.Id == SelectedCut);
            bool removed = cut != null && !cut.Speed;
            cutRestore.Visible = removed;
            foreach (var c in new Control[] { cutCut, cut2, cut4, cut8, cutDelete }) c.Visible = !removed;
            if (removed) { cutTitle.Text = "✂ " + L($"这里删掉了 {cut.End - cut.Start:0.0} 秒", $"{cut.End - cut.Start:0.0}s removed here"); cutLen.Text = ""; }
        }
    }
}
