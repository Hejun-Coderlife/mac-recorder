// Studio 剪辑窗口（像 Screen Studio，照 Mac 版 StudioEditor.swift）：上面实时预览，中间一条播放栏，下面时间轴，右边样式面板。
// 推近段、修剪、样式都存在素材夹的 project.json（格式同 Mac 版），改了自动存；「导出」出成片到桌面。
// 预览用和导出同一套合成（StudioScene），只是按比例缩小（轨迹、像素密度一起缩，构图一样），CPU 算
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor : Form
    {
        // ---- 数据 ----
        public readonly string Dir, Raw;
        public readonly VideoInfo Info;
        public readonly Track Track;          // 原尺寸轨迹（推近段的自动规则、导出用）
        public double Duration => Info.Duration;
        public StudioProject Project;
        public string Selected;               // 选中的推近段 id
        public string SelectedCut;            // 选中的剪掉 / 快放段 id（和推近段只能选一个）
        public string SelectedVoice;          // 选中的讲解 id
        public string SelectedCaption;        // 选中的字幕 id

        // 选中一样东西（推近 / 剪辑 / 讲解 / 字幕只能选一个）；kind = null 全不选
        public void Select(string kind, string id)
        {
            Selected = kind == "seg" ? id : null; SelectedCut = kind == "cut" ? id : null;
            SelectedVoice = kind == "voice" ? id : null; SelectedCaption = kind == "cap" ? id : null; SelectedLayout = kind == "layout" ? id : null;
            SelectedRed = kind == "red" ? id : null; SelectedAnn = kind == "ann" ? id : null; SelectedMusic = kind == "music" ? id : null;
            SelectedPiece = null; SelectedBurst = null;   // 片段 / 打字声的选中由 SelectPiece / SelectBurst 再设
            Picking = false;
            Changed?.Invoke();
        }
        public bool Picking;                  // 正在「在预览上点一下设固定位置」
        public double Time;                   // 播放头（秒，素材时间）
        public bool Playing;
        public PresetStore Presets = PresetStore.Load();
        public List<double> Events;           // 点击、打字的时刻（时间轴上的小点）
        public readonly int Fps = 60;
        readonly List<string> undo = new List<string>(), redo = new List<string>();
        StudioProject dragBase;
        public bool IsDragging => dragBase != null;
        public event Action Changed;          // 工程、选中、播放头变了：界面重画
        public void Touch() => Changed?.Invoke();

        // ---- 预览 ----
        readonly int pw, ph;                  // 预览用的原画面尺寸（缩小后）
        readonly Track ptrack;
        StudioScene scene; FramePlan[] plan;
        readonly object sceneGate = new object();
        byte[] srcFrame; double srcTime = -1;  // 最近解码到的原画面
        readonly Scrubber scrub = new Scrubber();
        Bitmap shown;
        readonly StudioStyle st = new StudioStyle();

        // ---- 界面 ----
        readonly PreviewBox preview = new PreviewBox();
        public readonly TimelineView Timeline;
        readonly StylePanel style;
        readonly Panel inspector = new Panel();
        readonly Label title = new Label(), exportStatus = new Label(), timeLabel = new Label();
        readonly Button exportBtn = new Button(), openBtn = new Button(), showBtn = new Button(), playBtn = new Button();
        readonly Button zoomBtn = new Button(), autoBtn = new Button(), undoBtn = new Button(), redoBtn = new Button(), tlMinus = new Button(), tlPlus = new Button();
        readonly TrackBar tlScale = new TrackBar();
        readonly ProgressBar exportBar = new ProgressBar();
        readonly ToolTip tips = new ToolTip { InitialDelay = 400 };
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        string exported;
        bool exporting;
        public string OutPath;
        public ExportOptions ExportOpt = ExportOptions.Load(Settings.GetString("exportOptions", ""));
        CancellationTokenSource exportCancel;
        readonly Button optBtn = new Button(), cancelBtn = new Button(), cutBtn = new Button(), voiceBtn = new Button(), capBtn = new Button();

        // 打开一个素材夹（Studio 录完、或主窗口「打开 Studio 素材…」）
        public static StudioEditor Open(string dir, string outPath = null)
        {
            foreach (Form f in Application.OpenForms)
                if (f is StudioEditor e && string.Equals(e.Dir, dir, StringComparison.OrdinalIgnoreCase)) { e.Activate(); return e; }
            var ed = new StudioEditor(dir, outPath);
            ed.Show();
            return ed;
        }

        public StudioEditor(string dir, string outPath = null)
        {
            Dir = dir; Raw = Path.Combine(dir, "raw.mp4");
            OutPath = outPath ?? Path.Combine(Desktop, Path.GetFileName(dir.TrimEnd('\\', '/')) + ".mp4");
            Info = Studio.Probe(Raw);
            Track = Studio.LoadTrack(dir, Info);
            Project = StudioProject.Load(dir, Track, Duration, st.Zoom, Presets.DefaultLook);
            // 打开时按最新规则重排了推近段：原来的留进撤销栈（⌘Z 能拿回来）
            if (File.Exists(StudioProject.PathIn(dir)))
            {
                try
                {
                    var old = StudioProject.FromJson(File.ReadAllText(StudioProject.PathIn(dir)));
                    if (old.Segments.Count > 0 && !old.SameSegments(Project)) { old.TrimStart = Project.TrimStart; old.TrimEnd = Project.TrimEnd; old.Look = Project.Look.Clone(); undo.Add(old.ToJson()); }
                }
                catch { }
            }
            Events = StudioPlan.Events(Track, Duration).Select(e => e.t).ToList();
            // 预览尺寸：画布最长不超过 1280×800（够看清，CPU 合成也跟得上）
            var full = new StudioLayout(Track, Project.Look);
            double k = Math.Min(1, Math.Min(1280 / full.cw, 800 / full.ch));
            pw = Math.Max(2, (int)Math.Round(Info.Width * k / 2) * 2); ph = Math.Max(2, (int)Math.Round(Info.Height * k / 2) * 2);
            ptrack = Track.Scaled(pw, ph);
            Time = Project.TrimStart;

            Text = App.Name + " · Studio — " + Path.GetFileName(dir.TrimEnd('\\', '/'));
            Icon = App.Icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1280, 800); MinimumSize = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            BackColor = Theme.Window;

            Timeline = new TimelineView(this);
            style = new StylePanel(this);
            BuildLayout();
            Rebuild();
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
            Changed += SyncUi;
            SyncUi();
            audioTimer.Tick += (s, e) => { audioTimer.Stop(); StartAudioPrep(); };
            Shown += (s, e) => { RequestFrame(Time); StartAudioPrep(); };
        }

        // ---- 布局 ----
        void BuildLayout()
        {
            SuspendLayout();
            // 右边样式面板
            style.Dock = DockStyle.Right; style.Width = 300;
            var sep = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.Line };
            // 顶部：文件名 + 右上「导出」
            var top = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 8, 14, 8), BackColor = Theme.Window };
            title.Text = Path.GetFileName(Dir.TrimEnd('\\', '/')); title.AutoSize = false; title.Dock = DockStyle.Fill; title.TextAlign = ContentAlignment.MiddleLeft; title.ForeColor = Theme.Secondary;
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0), Margin = new Padding(0) };
            exportBar.Width = 160; exportBar.Height = 14; exportBar.Margin = new Padding(0, 11, 8, 0); exportBar.Visible = false;
            exportStatus.AutoSize = true; exportStatus.Margin = new Padding(0, 9, 8, 0); exportStatus.ForeColor = Theme.Secondary;
            Flat(openBtn, L("打开", "Open"), (s, e) => { if (exported != null) Shell(exported); }); openBtn.Visible = false;
            Flat(showBtn, L("在资源管理器中显示", "Show in Explorer"), (s, e) => { if (exported != null) try { Process.Start("explorer.exe", $"/select,\"{exported}\""); } catch { } }); showBtn.Visible = false;
            Primary(exportBtn, L("导出", "Export"), (s, e) => Export());
            Flat(cancelBtn, L("取消", "Cancel"), (s, e) => { exportCancel?.Cancel(); taskCancel?.Cancel(); }); cancelBtn.Visible = false;
            Flat(optBtn, "⚙ " + ExportOpt.Summary(L), (s, e) => ShowExportMenu());
            tips.SetToolTip(optBtn, L("导出设置：分辨率、帧率、画质、视频 / GIF、导出后复制到剪贴板", "Export settings: resolution, frame rate, quality, video / GIF, copy to clipboard"));
            right.Controls.AddRange(new Control[] { exportBar, exportStatus, cancelBtn, openBtn, showBtn, optBtn, exportBtn });
            top.Controls.Add(title); top.Controls.Add(right);
            var topLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };

            // 左边：预览 / 播放栏 / 属性条 / 时间轴
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
            var tlArea = new Panel { Dock = DockStyle.Bottom, Height = DefaultTimelineArea(HasCamera), Padding = new Padding(14, 8, 14, 10), BackColor = Theme.Window };
            inspector.Dock = DockStyle.Top; inspector.Height = 34; inspector.Visible = false;
            cutInspector.Dock = DockStyle.Top; cutInspector.Height = 34; cutInspector.Visible = false;
            voiceInspector.Dock = DockStyle.Top; voiceInspector.Height = 34; voiceInspector.Visible = false;
            capInspector.Dock = DockStyle.Top; capInspector.Height = 34; capInspector.Visible = false;
            BuildInspector(); BuildCutInspector(); BuildVoiceInspector(); BuildCaptionInspector(); BuildLayoutInspector();
            tlArea.Controls.Add(inspector); tlArea.Controls.Add(cutInspector); tlArea.Controls.Add(voiceInspector); tlArea.Controls.Add(capInspector); tlArea.Controls.Add(layInspector);
            BuildW4Inspectors(tlArea);
            BuildW5Inspectors(tlArea);
            var barLine2 = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(14, 7, 14, 7), BackColor = Theme.Window };
            BuildControlBar(bar);
            var barLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line };
            preview.Dock = DockStyle.Fill; preview.Margin = new Padding(14);
            var pv = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = Theme.Window };
            pv.Controls.Add(preview);
            preview.Pick = (x, y) => PickAt(x, y);
            InitW3(); InitW4();
            left.Controls.Add(pv); left.Controls.Add(barLine); left.Controls.Add(bar); left.Controls.Add(barLine2); left.Controls.Add(tlArea);
            InitW5(tlArea, pv, left);

            Controls.Add(left); Controls.Add(sep); Controls.Add(style); Controls.Add(topLine); Controls.Add(top);
            ResumeLayout();
        }

        void BuildControlBar(Panel bar)
        {
            var leftTools = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            Flat(zoomBtn, "⊕ " + L("推近", "Zoom"), (s, e) => AddSegment(Time));
            tips.SetToolTip(zoomBtn, L("在播放头处新加一段推近（镜头放大凑近看）；也可以在时间轴空白处双击", "Add a zoom-in at the playhead; you can also double-click empty space on the timeline"));
            Flat(autoBtn, "✦", (s, e) => Regenerate()); autoBtn.Width = 34;
            tips.SetToolTip(autoBtn, L("按录制时的点击和打字重新排推近段（会替换现在的段，可以撤销 Ctrl+Z）", "Rebuild zooms from your clicks and typing (replaces the current zooms; undo with Ctrl+Z)"));
            Flat(undoBtn, "↶", (s, e) => Undo()); tips.SetToolTip(undoBtn, L("撤销上一步（Ctrl+Z）", "Undo the last change (Ctrl+Z)"));
            Flat(redoBtn, "↷", (s, e) => Redo()); tips.SetToolTip(redoBtn, L("重做刚撤销的那步（Ctrl+Y）", "Redo what you just undid (Ctrl+Y)"));
            foreach (var b in new[] { undoBtn, redoBtn }) { b.Width = 34; b.Font = new Font(Font.FontFamily, Font.Size + 2); }
            Flat(cutBtn, "✂ " + L("分割", "Split"), (s, e) => Split(Time));
            tips.SetToolTip(cutBtn, L("在播放头处切一刀（S 或 Ctrl+B）：点选切出来的一段按 Delete 删掉，时间轴直接合拢；也能给那段改速度", "Split at the playhead (S or Ctrl+B): select a part and press Delete to remove it, the timeline closes up; you can also change its speed"));
            Flat(voiceBtn, "🎙 " + L("录讲解", "Voice-over"), (s, e) => ToggleVoice());
            tips.SetToolTip(voiceBtn, L("从播放头开始边放边录你的声音（麦克风）；再点一下、空格或 Esc 停。录好的在时间轴「讲解」轨", "Plays from the playhead while recording your microphone; click again, Space or Esc to stop. It goes on the Voice-over lane"));
            Flat(capBtn, "字 " + L("字幕", "Captions"), (s, e) => ShowCaptionMenu());
            tips.SetToolTip(capBtn, L("把说的话自动变成字幕（在本机识别，不上传）；也能导出 .srt", "Turn speech into captions (recognized on this PC, nothing uploaded); export .srt"));
            leftTools.Controls.AddRange(new Control[] { zoomBtn, cutBtn, voiceBtn, capBtn, annotateBtn });
            BuildW4Bar();

            var rightTools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            Flat(tlMinus, "−", (s, e) => SetTimelineScale(Timeline.Scale / 1.5, null)); tlMinus.Width = 30;
            tlScale.Minimum = 100; tlScale.Maximum = 800; tlScale.TickStyle = TickStyle.None; tlScale.Width = 100; tlScale.AutoSize = false; tlScale.Height = 30; tlScale.Margin = new Padding(0, 3, 0, 0);
            tlScale.Scroll += (s, e) => SetTimelineScale(tlScale.Value / 100.0, null);
            Flat(tlPlus, "+", (s, e) => SetTimelineScale(Timeline.Scale * 1.5, null)); tlPlus.Width = 30;
            var tlTip = L("时间轴缩放（只改时间轴的显示，不影响成片）：也可以在时间轴上按住 Ctrl 滚动滚轮、或触控板双指捏合；放大后滚轮或双指左右滑翻看",
                          "Timeline zoom (display only): hold Ctrl and scroll on the timeline, or pinch on a touchpad; when zoomed in, scroll or swipe sideways to move along");
            foreach (Control c in new Control[] { tlMinus, tlScale, tlPlus }) tips.SetToolTip(c, tlTip);
            rightTools.Controls.AddRange(new Control[] { suggestBtn, autoBtn, undoBtn, redoBtn, tlMinus, tlScale, tlPlus });

            // 正中：圆形播放键 + 时间（按整条居中）
            var center = new Panel { Width = 290, Height = 36, BackColor = Theme.Window };
            playBtn.FlatStyle = FlatStyle.Flat; playBtn.FlatAppearance.BorderSize = 0; playBtn.Size = new Size(36, 36); playBtn.Location = new Point(145 - 18, 0);
            playBtn.BackColor = Theme.Window; playBtn.Paint += PaintPlay; playBtn.Click += (s, e) => TogglePlay(); playBtn.TabStop = false;
            tips.SetToolTip(playBtn, L("播放 / 暂停（空格）", "Play / Pause (Space)"));
            timeLabel.AutoSize = false; timeLabel.Size = new Size(126, 36); timeLabel.Location = new Point(145 + 26, 0); timeLabel.TextAlign = ContentAlignment.MiddleLeft;
            timeLabel.Font = new Font("Consolas", Font.Size + 0.5f); timeLabel.ForeColor = Theme.Secondary;
            center.Controls.Add(playBtn); center.Controls.Add(timeLabel);
            bar.Controls.Add(center); bar.Controls.Add(leftTools); bar.Controls.Add(rightTools);
            bar.Resize += (s, e) => center.Location = new Point((bar.ClientSize.Width - center.Width) / 2, (bar.ClientSize.Height - center.Height) / 2);
        }

        void PaintPlay(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, playBtn.Width - 3, playBtn.Height - 3);
            g.Clear(Theme.Window);
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, r);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, u = r.Width / 36f;
            using (var w = new SolidBrush(Color.White))
            {
                if (Playing) { g.FillRectangle(w, cx - 6 * u, cy - 7 * u, 4 * u, 14 * u); g.FillRectangle(w, cx + 2 * u, cy - 7 * u, 4 * u, 14 * u); }
                else g.FillPolygon(w, new[] { new PointF(cx - 4.5f * u, cy - 8 * u), new PointF(cx - 4.5f * u, cy + 8 * u), new PointF(cx + 8.5f * u, cy) });
            }
        }

        // 选中推近段才出现：放大倍数、跟随方式、选位置、删除
        readonly Label insTitle = new Label(), insTime = new Label(), insZoomValue = new Label();
        readonly TrackBar insZoom = new TrackBar();
        readonly Button insFollow = new Button(), insFixed = new Button(), insPick = new Button(), insDelete = new Button();
        void BuildInspector()
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            insTitle.Text = L("推近", "Zoom"); insTitle.AutoSize = true; insTitle.Font = new Font(Font, FontStyle.Bold); insTitle.Margin = new Padding(0, 8, 8, 0);
            insTime.AutoSize = true; insTime.ForeColor = Theme.Secondary; insTime.Margin = new Padding(0, 8, 14, 0);
            var scaleL = new Label { Text = L("放大", "Scale"), AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            insZoom.Minimum = 12; insZoom.Maximum = 40; insZoom.TickStyle = TickStyle.None; insZoom.AutoSize = false; insZoom.Width = 150; insZoom.Height = 28; insZoom.Margin = new Padding(0, 3, 0, 0);
            insZoom.Scroll += (s, e) => { var id = Selected; DragBegin(); DragUpdate(p => { var sg = p.Segments.FirstOrDefault(x => x.Id == id); if (sg != null) sg.Zoom = insZoom.Value / 10.0; }); };
            insZoom.MouseUp += (s, e) => DragEnd();
            insZoom.KeyUp += (s, e) => DragEnd();
            insZoomValue.AutoSize = false; insZoomValue.Width = 40; insZoomValue.Margin = new Padding(2, 8, 10, 0);
            Flat(insFollow, L("跟着鼠标", "Follow cursor"), (s, e) => SetMode(false));
            Flat(insFixed, L("固定位置", "Fixed position"), (s, e) => SetMode(true));
            Flat(insPick, L("选位置", "Set Position"), (s, e) => { Picking = !Picking; Changed?.Invoke(); });
            tips.SetToolTip(insPick, L("点了以后在预览画面上点一下，设成这段推近盯住的位置（Esc 取消）", "Then click the preview to set where this zoom looks (Esc cancels)"));
            Flat(insDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected());
            flow.Controls.AddRange(new Control[] { insTitle, insTime, scaleL, insZoom, insZoomValue, insFollow, insFixed, insPick });
            AddZoomSpeed(flow);
            inspector.Controls.Add(flow);
            insDelete.Dock = DockStyle.Right;
            inspector.Controls.Add(insDelete);
        }

        // 选中剪掉 / 快放段才出现：剪掉 / 快放 2× 4× 8×、时长变化、删除
        readonly Panel cutInspector = new Panel();
        readonly Label cutTitle = new Label(), cutTime = new Label(), cutLen = new Label();
        readonly Button cutCut = new Button(), cut2 = new Button(), cut4 = new Button(), cut8 = new Button(), cutDelete = new Button();
        void BuildCutInspector()
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            cutTitle.AutoSize = true; cutTitle.Font = new Font(Font, FontStyle.Bold); cutTitle.Margin = new Padding(0, 8, 8, 0);
            cutTime.AutoSize = true; cutTime.ForeColor = Theme.Secondary; cutTime.Margin = new Padding(0, 8, 14, 0);
            Flat(cutCut, L("剪掉", "Cut"), (s, e) => SetCut(false, 0));
            Flat(cut2, L("快放 2×", "Speed 2×"), (s, e) => SetCut(true, 2));
            Flat(cut4, "4×", (s, e) => SetCut(true, 4));
            Flat(cut8, "8×", (s, e) => SetCut(true, 8));
            cutLen.AutoSize = true; cutLen.ForeColor = Theme.Secondary; cutLen.Margin = new Padding(8, 8, 0, 0);
            Flat(cutDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected());
            flow.Controls.AddRange(new Control[] { cutTitle, cutTime, cutCut, cut2, cut4, cut8, cutLen });
            cutInspector.Controls.Add(flow);
            cutDelete.Dock = DockStyle.Right;
            cutInspector.Controls.Add(cutDelete);
        }

        public void SetCut(bool speed, double rate)
        {
            var id = SelectedCut;
            Edit(p => { var c = p.Cuts.FirstOrDefault(x => x.Id == id); if (c != null) { c.Speed = speed; if (speed) c.Rate = rate; } });
            Changed?.Invoke();
        }

        void SetMode(bool fix) { var id = Selected; Edit(p => { var sg = p.Segments.FirstOrDefault(x => x.Id == id); if (sg != null) sg.Fixed = fix; }); if (!fix) Picking = false; Changed?.Invoke(); }

        void Flat(Button b, string text, EventHandler click)
        {
            b.Text = text; b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink; b.Padding = new Padding(6, 1, 6, 1); b.Margin = new Padding(0, 2, 6, 2);
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Theme.Line; b.BackColor = Theme.Control; b.ForeColor = Theme.Text; b.Click += click;
            b.MinimumSize = new Size(0, 30);
        }

        void Primary(Button b, string text, EventHandler click)
        {
            Flat(b, text, click);
            b.BackColor = Theme.Accent; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; b.Font = new Font(Font, FontStyle.Bold); b.Padding = new Padding(14, 1, 14, 1);
        }

        void SyncUi()
        {
            if (IsDisposed) return;
            undoBtn.Enabled = undo.Count > 0; redoBtn.Enabled = redo.Count > 0;
            // 时间按成片算（剪掉的不算、快放的按压短后的长度），同 Mac 版
            var map = new TimeMap(Project);
            double len = map.Out(Project.TrimEnd) - Project.TrimStart;
            timeLabel.Text = Clock(map.Out(Math.Max(Time, Project.TrimStart)) - Project.TrimStart) + " / " + Clock(len);
            var cut = Project.Cuts.FirstOrDefault(c => c.Id == SelectedCut);
            cutInspector.Visible = cut != null;
            if (cut != null)
            {
                cutTitle.Text = cut.Speed ? L("快放", "Speed-up") : L("剪掉", "Cut");
                cutTime.Text = Clock(cut.Start) + " – " + Clock(cut.End);
                foreach (var (b, on) in new[] { (cutCut, !cut.Speed), (cut2, cut.Speed && cut.Rate == 2), (cut4, cut.Speed && cut.Rate == 4), (cut8, cut.Speed && cut.Rate == 8) })
                { b.BackColor = on ? Theme.Accent : Theme.Control; b.ForeColor = on ? Color.White : Theme.Text; }
                double d = cut.End - cut.Start;
                cutLen.Text = cut.Speed ? L($"{d:0.0} 秒 → {d / cut.Rate:0.0} 秒", $"{d:0.0}s → {d / cut.Rate:0.0}s") : L($"成片少 {d:0.0} 秒", $"{d:0.0}s removed");
            }
            SyncVoiceCaption();
            SyncLayoutInspector();
            SyncW4();
            SyncW5();
            optBtn.Text = "⚙ " + ExportOpt.Summary(L);
            playBtn.Invalidate();
            tlMinus.Enabled = Timeline.Scale > 1.001; tlPlus.Enabled = Timeline.Scale < 7.999;
            int sv = (int)Math.Round(Timeline.Scale * 100); if (tlScale.Value != sv) tlScale.Value = Math.Min(800, Math.Max(100, sv));
            var seg = Project.Segments.FirstOrDefault(s => s.Id == Selected);
            inspector.Visible = seg != null && cut == null;
            if (seg != null)
            {
                insTime.Text = Clock(seg.Start) + " – " + Clock(seg.End);
                int zv = (int)Math.Round(seg.Zoom * 10); if (insZoom.Value != Math.Min(40, Math.Max(12, zv))) insZoom.Value = Math.Min(40, Math.Max(12, zv));
                insZoomValue.Text = seg.Zoom.ToString("0.0") + "×";
                insFollow.BackColor = !seg.Fixed ? Theme.Accent : Theme.Control; insFollow.ForeColor = !seg.Fixed ? Color.White : Theme.Text;
                insFixed.BackColor = seg.Fixed ? Theme.Accent : Theme.Control; insFixed.ForeColor = seg.Fixed ? Color.White : Theme.Text;
                insPick.Visible = seg.Fixed;
                insPick.Text = Picking ? L("点画面…", "Click the picture…") : L("选位置", "Set Position");
                SyncZoomSpeed(seg);
            }
            preview.Picking = Picking;
            preview.Hint = Tool != null ? ToolHint : Picking ? L("在画面上点一下，设成这段推近盯住的位置（Esc 取消）", "Click the picture to set where this zoom looks (Esc to cancel)") : voiceHint;
            preview.Invalidate();
            Timeline.Invalidate();
            style.Sync();
        }

        public static string Clock(double t) { t = Math.Max(0, t); return $"{(int)t / 60}:{(int)t % 60:00}.{(int)(t * 10) % 10}"; }

        // ---- 编辑（都能撤销） ----
        public void Edit(Action<StudioProject> change)
        {
            var p = Project.Clone();
            change(p);
            if (p.Same(Project)) return;
            if (!p.SameSegments(Project)) p.SegmentsEdited = true;
            undo.Add(Project.ToJson()); redo.Clear();
            SetProject(p);
        }

        // 拖动：开始时记下原样，拖的过程中实时改，松手才算一步撤销
        public void DragBegin() { if (dragBase == null) { dragBase = Project.Clone(); ScrubBegin(); } }
        public void DragUpdate(Action<StudioProject> change)
        {
            if (dragBase == null) return;
            var p = dragBase.Clone();
            change(p);
            if (!p.SameSegments(dragBase)) p.SegmentsEdited = true;
            SetProject(p);
        }
        public void DragEnd()
        {
            if (dragBase != null && !dragBase.Same(Project)) { undo.Add(dragBase.ToJson()); redo.Clear(); }
            dragBase = null;
            ScrubEnd();
            Changed?.Invoke();
        }

        public void Undo() { if (undo.Count == 0) return; redo.Add(Project.ToJson()); var p = StudioProject.FromJson(undo.Last()); undo.RemoveAt(undo.Count - 1); if (!p.SameSegments(Project)) p.SegmentsEdited = true; SetProject(p); }   // 撤回来的推近段算手动选的，以后不再自动重排
        public void Redo() { if (redo.Count == 0) return; undo.Add(Project.ToJson()); var p = StudioProject.FromJson(redo.Last()); redo.RemoveAt(redo.Count - 1); SetProject(p); }

        void SetProject(StudioProject p)
        {
            bool lookChanged = !p.Look.Same(Project.Look), segsChanged = !p.SameSegments(Project) || (p.Look.CursorLoop && (p.TrimStart != Project.TrimStart || p.TrimEnd != Project.TrimEnd));
            Project = p;
            if (lookChanged) Rebuild(); else if (segsChanged) Replan();
            saveTimer.Stop(); saveTimer.Start();
            if (AudioSig(p) != audioSig) { audioTimer.Stop(); audioTimer.Start(); }
            Recompose();
            Changed?.Invoke();
        }

        // 按自动规则重新排推近段（能撤销）；排完算「没手动改过」
        public void Regenerate()
        {
            var old = Project.Segments.Select(x => (x.Start, x.End)).ToList();
            var segs = StudioPlan.AutoSegments(Track, Duration, Project.Segments.FirstOrDefault()?.Zoom ?? 2.0);
            Edit(p => p.Segments = segs);
            Project.SegmentsEdited = false;
            Selected = null;
            // 告诉用户发生了什么（自动排的和现在一样时，点了看不出变化）
            bool same = old.Count == segs.Count && old.Zip(segs, (a, b) => Math.Abs(a.Start - b.Start) < 0.05 && Math.Abs(a.End - b.End) < 0.05).All(x => x);
            Toast(same ? L("已按点击和打字重新排推近：和现在的一样，没有变化", "Zooms regenerated from your clicks and typing: same as before")
                       : L($"已按点击和打字重新排了 {segs.Count} 段推近（原来 {old.Count} 段）· Ctrl+Z 撤销", $"Regenerated {segs.Count} zooms (was {old.Count}) · Ctrl+Z to undo"));
            saveTimer.Stop(); saveTimer.Start();
            Changed?.Invoke();
        }

        // 某一段能占的范围：不压到前后两段、不出修剪范围
        public (double lo, double hi) Room(string id, StudioProject p)
        {
            var segs = p.Segments.OrderBy(s => s.Start).ToList();
            int i = segs.FindIndex(s => s.Id == id);
            if (i < 0) return (p.TrimStart, p.TrimEnd);
            double lo = Math.Max(p.TrimStart, i > 0 ? segs[i - 1].End : 0);
            double hi = Math.Min(p.TrimEnd, i + 1 < segs.Count ? segs[i + 1].Start : Duration);
            return (lo, Math.Max(lo, hi));
        }

        public void AddSegment(double t)
        {
            const double len = 2.0;
            var segs = Project.Segments.OrderBy(s => s.Start).ToList();
            if (segs.Any(s => t >= s.Start && t < s.End)) { System.Media.SystemSounds.Beep.Play(); return; }
            double lo = Math.Max(Project.TrimStart, segs.LastOrDefault(s => s.End <= t)?.End ?? 0);
            double hi = Math.Min(Project.TrimEnd, segs.FirstOrDefault(s => s.Start > t)?.Start ?? Duration);
            if (hi - lo < 0.3) { System.Media.SystemSounds.Beep.Play(); return; }
            double s0 = Math.Max(lo, t - len / 2), e0 = Math.Min(hi, s0 + len);
            s0 = Math.Max(lo, e0 - len); e0 = Math.Min(hi, s0 + len);
            var m = new StudioPlan.Mouse(Track);
            double fx = 0.5, fy = 0.5;
            if (m.At(t, out var mx, out var my, out _)) { fx = mx / Track.Width; fy = 1 - my / Track.Height; }
            var seg = new ZoomSegment { Start = s0, End = e0, FixedX = fx, FixedY = fy };
            Edit(p => { p.Segments.Add(seg); p.Segments = p.Segments.OrderBy(x => x.Start).ToList(); });
            Select("seg", seg.Id);
        }

        // ---- 剪掉 / 快放 ----
        // 某一段剪辑能占的范围：不压到前后两段剪辑、不出修剪范围
        public (double lo, double hi) CutRoom(string id, StudioProject p)
        {
            var cuts = p.Cuts.OrderBy(c => c.Start).ToList();
            int i = cuts.FindIndex(c => c.Id == id);
            if (i < 0) return (p.TrimStart, p.TrimEnd);
            double lo = Math.Max(p.TrimStart, i > 0 ? cuts[i - 1].End : 0);
            double hi = Math.Min(p.TrimEnd, i + 1 < cuts.Count ? cuts[i + 1].Start : Duration);
            return (lo, Math.Max(lo, hi));
        }

        public void AddCut(double t)
        {
            const double len = 2.0;
            var cuts = Project.Cuts.OrderBy(c => c.Start).ToList();
            if (cuts.Any(c => t >= c.Start && t < c.End)) { System.Media.SystemSounds.Beep.Play(); return; }
            double lo = Math.Max(Project.TrimStart, cuts.LastOrDefault(c => c.End <= t)?.End ?? 0);
            double hi = Math.Min(Project.TrimEnd, cuts.FirstOrDefault(c => c.Start > t)?.Start ?? Duration);
            if (hi - lo < 0.3) { System.Media.SystemSounds.Beep.Play(); return; }
            double s0 = Math.Max(lo, t), e0 = Math.Min(hi, s0 + len);
            s0 = Math.Max(lo, e0 - len);
            var c0 = new CutSegment { Start = s0, End = e0 };
            Edit(p => { p.Cuts.Add(c0); p.Cuts = p.Cuts.OrderBy(x => x.Start).ToList(); });
            Select("cut", c0.Id);
        }

        public void DeleteSelected()
        {
            if (DeleteSelectedW5() || DeleteSelectedLayout() || DeleteSelectedW4()) return;
            if (SelectedVoice != null) { var vid = SelectedVoice; Edit(p => p.Voiceovers.RemoveAll(v => v.Id == vid)); Select(null, null); return; }
            if (SelectedCaption != null) { var kid = SelectedCaption; Edit(p => p.Captions.RemoveAll(c => c.Id == kid)); Select(null, null); return; }
            if (SelectedCut != null)
            {
                var cid = SelectedCut;
                Edit(p => p.Cuts.RemoveAll(c => c.Id == cid));
                SelectedCut = null;
                Changed?.Invoke();
                return;
            }
            var id = Selected;
            if (id == null) return;
            Edit(p => p.Segments.RemoveAll(s => s.Id == id));
            Selected = null; Picking = false;
            Changed?.Invoke();
        }

        // 在预览上点一下：设成选中那段的固定位置。x、y = 预览画面里的比例（左上为原点）
        void PickAt(double x, double y)
        {
            if (!Picking || Selected == null) return;
            StudioScene sc; FramePlan[] pl;
            lock (sceneGate) { sc = scene; pl = plan; }
            if (sc == null || pl == null) return;
            var f = pl[Math.Min(Math.Max((int)Math.Round(Time * Fps), 0), pl.Length - 1)];
            var pt = StudioPlan.SourcePoint(sc.Lay, x * sc.Lay.cw, y * sc.Lay.ch, f);
            var id = Selected;
            Edit(p => { var sg = p.Segments.FirstOrDefault(s => s.Id == id); if (sg != null) { sg.Fixed = true; sg.FixedX = pt.x; sg.FixedY = pt.y; } });
            Picking = false;
            Changed?.Invoke();
        }

        // ---- 样式 ----
        public void EditLook(Action<StudioLook> change) => Edit(p => change(p.Look));
        public void DragLook(Action<StudioLook> change) { DragBegin(); DragUpdate(p => change(p.Look)); }

        public void ApplyPreset(string name) { var pr = Presets.Find(name); if (pr != null) Edit(p => p.Look = pr.Look.Clone()); }

        // ---- 预览：重建场景（样式变了）、重算镜头（推近段变了）、重新合成当前帧 ----
        void Rebuild()
        {
            var sc = new StudioScene(Dir, ptrack, Project.Look.Clone(), st, CursorTracker.LoadCursor);
            var pl = sc.Plan(Project.Segments, Duration, Math.Max(1, (int)(Duration * Fps)), (Project.TrimStart, Project.TrimEnd));
            lock (sceneGate) { scene = sc; plan = pl; }
        }

        void Replan()
        {
            StudioScene sc; lock (sceneGate) sc = scene;
            var pl = sc.Plan(Project.Segments.Select(s => s.Clone()).ToList(), Duration, Math.Max(1, (int)(Duration * Fps)), (Project.TrimStart, Project.TrimEnd));
            lock (sceneGate) plan = pl;
        }

        // 用手上最近那张原画面重新合成（样式、推近改了，播放头没动）
        void Recompose()
        {
            if (Playing) return;
            byte[] src = srcFrame; double t = srcTime;
            if (src == null) return;
            ShowFrame(src, t);
        }

        readonly object composeGate = new object();   // 合成器的临时缓存不能两个线程同时用（拖动解码、播放、改样式重画）
        void ShowFrame(byte[] src, double t)
        {
            lock (composeGate) ShowFrameLocked(src, t);
        }

        void ShowFrameLocked(byte[] src, double t)
        {
            StudioScene sc; FramePlan[] pl;
            lock (sceneGate) { sc = scene; pl = plan; }
            if (sc == null || pl == null) return;
            int CW = (int)sc.Lay.cw, CH = (int)sc.Lay.ch;
            var dst = new byte[CW * CH * 4];
            int i = Math.Min(Math.Max((int)Math.Round(t * Fps), 0), pl.Length - 1);
            sc.Frame(sc.Cpu, MarkOverlays(src, t), i, pl, dst);
            try { ApplyCanvas(sc, dst, t); } catch { }
            var bmp = new Bitmap(CW, CH, PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, CW, CH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < CH; y++) Marshal.Copy(dst, y * CW * 4, bd.Scan0 + y * bd.Stride, CW * 4);
            bmp.UnlockBits(bd);
            DrawCaption(bmp, t);
            if (InvokeRequired) { try { BeginInvoke((Action)(() => SetShown(bmp))); } catch { bmp.Dispose(); } }
            else SetShown(bmp);
        }

        void SetShown(Bitmap b)
        {
            if (IsDisposed) { b.Dispose(); return; }
            var old = shown; shown = b; preview.Image = b; preview.Invalidate(); old?.Dispose();
            FirstFrameShown = true;
        }
        public bool FirstFrameShown;

        // 播放头跳到 t（拖动时排队：同时只解一帧，只追最新的目标）
        public void Seek(double t)
        {
            t = Math.Min(Math.Max(t, 0), Duration);
            Time = t;
            Changed?.Invoke();
            if (Playing) { StopPlayback(); }
            RequestFrame(t);
        }

        void RequestFrame(double t)
        {
            if (!(scrub.Request(t) is double go)) return;
            Task.Run(() => Decode(go));
        }

        void Decode(double t)
        {
            while (true)
            {
                byte[] src = null;
                try { src = Studio.DecodeFrame(Raw, Math.Min(t, Math.Max(0, Duration - 0.05)), pw, ph, Info.Matrix); } catch { }
                if (src != null) { srcFrame = src; srcTime = t; ShowFrame(src, t); }
                double? next = null;
                try { next = (double?)Invoke((Func<double?>)(() => scrub.SeekDone())); } catch { return; }
                if (!(next is double n)) return;
                t = n;
            }
        }

        public void ScrubBegin() { scrub.Begin(); if (Playing) StopPlayback(); }
        public void ScrubEnd() => scrub.End();

        // ---- 播放：剪掉的跳过、快放的快放（同 Mac 版）。画面一块一块地解（每块从它的起点开始，快放块按倍数取帧），按成片时钟推进；
        // 声音放「预览声音」：和导出同一套做法提前做好的整条 wav（剪掉 / 快放 / 音量 / 点击声都在里面），从成片时间开始放 ----
        Process player; Thread playThread; volatile bool stopPlay;
        string audioWav; string audioSig; int audioGen;
        readonly int playFps = 30;
        readonly System.Windows.Forms.Timer audioTimer = new System.Windows.Forms.Timer { Interval = 400 };

        static string AudioSig(StudioProject p) =>
            p.Audio.ToJson() + "|" + string.Join(";", p.Voiceovers.Select(v => $"{v.File},{v.Start:R},{v.TrimIn:R},{v.TrimOut:R},{v.Volume:R}")) + "|" + string.Join(";", p.Cuts.Select(c => $"{c.Start:R},{c.End:R},{c.Speed},{c.Rate:R}")) + "|" + p.TrimStart.ToString("R") + "," + p.TrimEnd.ToString("R") + "|" + p.Look.ClickSound + p.Look.ClickStyle + "|" + p.Music.ToJson();

        // 预览声音过期了（改了音量、剪辑、修剪、点击声）：后台重做一份，做好换上
        void StartAudioPrep()
        {
            var p = Project.Clone(); var sig = AudioSig(p); int gen = ++audioGen;
            audioSig = sig;
            var wav = Path.Combine(Path.GetTempPath(), "studio-preview-" + Guid.NewGuid().ToString("N") + ".wav");
            Task.Run(() =>
            {
                bool ok = false;
                try { ok = Studio.RenderAudio(Dir, Info, Track, p, wav); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!ok || gen != audioGen) { try { File.Delete(wav); } catch { } return; }
                        var old = audioWav; audioWav = wav; AudioReady++;
                        if (old != null && !Playing) try { File.Delete(old); } catch { }
                        else if (old != null) oldWavs.Add(old);
                    }));
                }
                catch { }
            });
        }
        readonly List<string> oldWavs = new List<string>();
        public int AudioReady;   // 自测用：预览声音做好过几次

        public void TogglePlay()
        {
            if (Playing) { StopPlayback(); Changed?.Invoke(); return; }
            if (Time >= Project.TrimEnd - 0.05 || Time < Project.TrimStart - 0.01) Time = Project.TrimStart;
            StartPlayback(Time);
        }

        void StartPlayback(double t0, bool audio = true)
        {
            StopPlayback(true);
            var map = new TimeMap(Project);
            // 播放头落在剪掉段里：从剪掉段的终点开始
            if (map.At(t0) is TimeMap.Piece pc && pc.Rate == 0) t0 = pc.End;
            Playing = true; stopPlay = false;
            Changed?.Invoke();
            if (audioWav != null && audio) Mci.Play(audioWav, map.Out(t0) - Project.TrimStart);
            var spans = map.Spans(t0, Project.TrimEnd);
            double o0 = map.Out(t0);
            playThread = new Thread(() => PlayLoop(spans, map, o0)) { IsBackground = true, Name = "studio-preview" };
            playThread.Start();
        }

        void PlayLoop(List<(double start, double end, double rate)> spans, TimeMap map, double o0)
        {
            int bytes = pw * ph * 4;
            var sw = Stopwatch.StartNew();
            double end = Project.TrimEnd, t = spans.Count > 0 ? spans[0].start : end;
            foreach (var sp in spans)
            {
                if (stopPlay) break;
                double fps = playFps * sp.rate;   // 快放块：按倍数多取帧，每帧照样按 1/30 秒放
                var proc = Studio.StartDecode(Raw, sp.start, pw, ph, (int)Math.Round(fps), Info.Matrix);
                player = proc;
                proc.ErrorDataReceived += (s, e) => { }; proc.BeginErrorReadLine();
                var stream = proc.StandardOutput.BaseStream;
                for (int k = 0; !stopPlay; k++)
                {
                    t = sp.start + k / fps;
                    if (t >= sp.end - 1e-6) break;
                    var buf = new byte[bytes]; int got = 0;
                    while (got < bytes) { int n = stream.Read(buf, got, bytes - got); if (n <= 0) break; got += n; }
                    if (got < bytes) break;
                    // 赶不上就跳过这张（按成片时钟走，不拖慢）
                    double due = map.Out(t) - o0, now = sw.Elapsed.TotalSeconds;
                    if (now > due + 0.1) continue;
                    if (due > now) Thread.Sleep((int)((due - now) * 1000));
                    srcFrame = buf; srcTime = t;
                    ShowFrame(buf, t);
                    double tt = t;
                    try { BeginInvoke((Action)(() => { if (!stopPlay) { Time = tt; Changed?.Invoke(); Timeline.FollowPlayhead(); } })); } catch { return; }
                }
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
            try { BeginInvoke((Action)(() => { if (!stopPlay) { StopPlayback(); if (Time >= end - 0.1) Time = end; Changed?.Invoke(); } })); } catch { }
        }

        void StopPlayback(bool restarting = false)
        {
            // 放完了 / 被停了：正在录讲解的话一起停（重新开始播放时不算）
            if (!restarting && mic != null) { BeginInvoke((Action)StopVoice); }
            stopPlay = true;
            Playing = false;
            Mci.Stop();
            try { if (player != null && !player.HasExited) player.Kill(); } catch { }
            player = null;
            foreach (var w in oldWavs) try { File.Delete(w); } catch { }
            oldWavs.Clear();
        }

        public void Step(int n)
        {
            if (Playing) StopPlayback();
            Seek((Math.Round(Time * Fps) + n) / Fps);
        }

        // ---- 时间轴缩放 ----
        public void SetTimelineScale(double s, double? anchorX)
        {
            var z = TimelineMath.Zoom(Timeline.Scale, Timeline.Offset, Math.Max(1, Timeline.ViewWidth), anchorX ?? Timeline.ViewWidth / 2.0, s);
            Timeline.Scale = z.scale; Timeline.Offset = z.offset;
            Changed?.Invoke();
        }

        // ---- 存盘、导出 ----
        void SaveNow()
        {
            if (TestMode) return;
            try { Project.Save(Dir); } catch { }
        }
        public static bool TestMode;   // 截图自测时不写工程文件

        public void Export()
        {
            if (exporting || capBusy || RecordingVoice) return;
            saveTimer.Stop(); SaveNow();
            exporting = true; exported = null;
            exportBtn.Enabled = false; optBtn.Enabled = false; exportBar.Visible = true; exportBar.Value = 0; cancelBtn.Visible = true;
            openBtn.Visible = showBtn.Visible = false;
            exportStatus.Text = ""; exportStatus.ForeColor = Theme.Secondary;
            var p = Project.Clone();
            var opt = ExportOpt.Clone();
            var outPath = Unique(opt.Gif ? Path.ChangeExtension(OutPath, ".gif") : OutPath);
            exportCancel = new CancellationTokenSource();
            var token = exportCancel.Token;
            Task.Run(() =>
            {
                string err = null; RenderStats stats = null; bool cancelled = false;
                try { stats = Studio.Render(Dir, outPath, new StudioStyle(), v => { try { BeginInvoke((Action)(() => { exportBar.Value = (int)Math.Min(100, v * 100); exportStatus.Text = (int)(v * 100) + "%"; })); } catch { } }, CursorTracker.LoadCursor, null, p, opt, token); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception e) { err = e.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        exporting = false; exportBtn.Enabled = true; optBtn.Enabled = true; exportBar.Visible = false; cancelBtn.Visible = false;
                        if (cancelled) { exportStatus.Text = L("已取消导出", "Export cancelled"); LastExportCancelled = true; return; }
                        if (err != null) { exportStatus.Text = L("导出失败：", "Export failed: ") + err; exportStatus.ForeColor = Color.FromArgb(200, 30, 30); return; }
                        exported = outPath;
                        string how = stats != null && (stats.Gpu || stats.Encoder != "libx264") ? L("显卡", "GPU") : "CPU";
                        exportStatus.Text = L($"已导出到桌面，用时 {stats?.TotalSeconds:0} 秒（{how}）", $"Exported to Desktop in {stats?.TotalSeconds:0} s ({how})");
                        if (opt.CopyToClipboard)
                        {
                            try { var files = new System.Collections.Specialized.StringCollection { outPath }; Clipboard.SetFileDropList(files); exportStatus.Text += L("，已复制到剪贴板", " · copied to clipboard"); }
                            catch { }
                        }
                        openBtn.Visible = showBtn.Visible = true;
                        Exported?.Invoke(outPath);
                    }));
                }
                catch { }
            });
        }
        public bool LastExportCancelled;
        public void ScrollStyleTo(string what) => style.ScrollTo(what);
        public void CancelExport() => exportCancel?.Cancel();

        // 导出设置菜单：分辨率 / 帧率 / 画质 / 格式 / 复制到剪贴板，选了马上记住
        void ShowExportMenu()
        {
            var m = new ContextMenuStrip();
            void Group(string title, IEnumerable<(string text, bool on, Action set)> items)
            {
                if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripLabel(title) { ForeColor = Theme.Secondary, Font = new Font(Font, FontStyle.Bold) });
                foreach (var (text, on, set) in items)
                {
                    var it = new ToolStripMenuItem(text) { Checked = on };
                    it.Click += (s, e) => { set(); SaveExportOpt(); };
                    m.Items.Add(it);
                }
            }
            var o = ExportOpt;
            Group(L("格式", "Format"), new[] { (L("视频（mp4）", "Video (mp4)"), !o.Gif, (Action)(() => o.Gif = false)), ("GIF", o.Gif, () => o.Gif = true) });
            Group(L("分辨率", "Resolution"), ExportOptions.Resolutions.Select(r => (r == "original" ? L("原始", "Original") : r == "4k" ? "4K" : r + "p" + (r == "1440" ? L("（推荐）", " (recommended)") : r == "1080" ? L("（发微信、抖音够用）", " (good for sharing)") : ""), o.Resolution == r, (Action)(() => o.Resolution = r))));
            Group(L("帧率", "Frame rate"), new[] { ("60", o.Fps == 60, (Action)(() => o.Fps = 60)), ("30", o.Fps == 30, () => o.Fps = 30) });
            Group(L("画质", "Quality"), new[] { (L("高", "High"), o.Quality == "high", (Action)(() => o.Quality = "high")), (L("标准", "Standard"), o.Quality == "standard", () => o.Quality = "standard"), (L("小文件", "Small file"), o.Quality == "small", () => o.Quality = "small") });
            m.Items.Add(new ToolStripSeparator());
            var clip = new ToolStripMenuItem(L("导出后复制到剪贴板", "Copy to clipboard after export")) { Checked = o.CopyToClipboard };
            clip.Click += (s, e) => { o.CopyToClipboard = !o.CopyToClipboard; SaveExportOpt(); };
            m.Items.Add(clip);
            if (o.Gif) m.Items.Add(new ToolStripLabel(L("GIF：宽 960、每秒 15 帧、没有声音", "GIF: 960 wide, 15 fps, no sound")) { ForeColor = Theme.Secondary });
            m.Show(optBtn, new Point(0, optBtn.Height));
        }
        void SaveExportOpt() { Settings.SetString("exportOptions", ExportOpt.Save()); Changed?.Invoke(); }

        public event Action<string> Exported;
        public bool Exporting => exporting;
        public string ExportStatusText => exportStatus.Text;

        static void Shell(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

        // ---- 键盘：空格播放、←→ 逐帧、Ctrl+Z / Ctrl+Y、Delete、Esc ----
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox) return base.ProcessCmdKey(ref msg, keyData);
            switch (keyData)
            {
                case Keys.Space: if (RecordingVoice || countdownLeft > 0) { ToggleVoice(); return true; } TogglePlay(); return true;
                case Keys.Left: Step(-1); return true;
                case Keys.Right: Step(1); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.S: case Keys.Control | Keys.B: Split(Time); return true;   // 分割
                case Keys.Delete: case Keys.Back: if (SelectedPiece != null || SelectedBurst != null || Selected != null || SelectedCut != null || SelectedVoice != null || SelectedCaption != null || SelectedLayout != null || SelectedRed != null || SelectedAnn != null || SelectedMusic != null) { DeleteSelected(); return true; } break;
                case Keys.Control | Keys.D: if (SelectedAnn != null || SelectedRed != null) { DuplicateOverlay(); return true; } break;
                case Keys.Escape:
                    if (RecordingVoice || countdownLeft > 0) { ToggleVoice(); return true; }
                    if (Picking) { Picking = false; Changed?.Invoke(); return true; }
                    if (Tool != null) { Tool = null; Changed?.Invoke(); return true; }
                    if (SelectedPiece != null || SelectedBurst != null || Selected != null || SelectedCut != null || SelectedVoice != null || SelectedCaption != null || SelectedLayout != null || SelectedRed != null || SelectedAnn != null || SelectedMusic != null) { Select(null, null); return true; } break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (RecordingVoice) StopVoice();
            taskCancel?.Cancel();
            if (exporting && e.CloseReason == CloseReason.UserClosing)
            {
                exportStatus.Text = L("正在导出，等它做完再关", "Still exporting, close it when it's done");
                e.Cancel = true; return;
            }
            StopPlayback();
            saveTimer.Stop(); SaveNow();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            shown?.Dispose();
            var wavs = new List<string>(oldWavs); if (audioWav != null) wavs.Add(audioWav);
            audioGen++;
            Task.Run(() => { Thread.Sleep(500); foreach (var w in wavs) try { File.Delete(w); } catch { } });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitle(Handle);
        }
    }

    // 预览画面：按比例放进框里；选固定位置时把点击换算成画面里的比例
    sealed class PreviewBox : Control
    {
        public Image Image;
        public bool Picking;
        public string Hint;
        public Action<double, double> Pick;
        public Func<double, double, bool> GrabCamera;     // 在摄像头小窗上按下：返回 true 开始拖
        public Action<double, double> MoveCamera;
        public Action ReleaseCamera;
        // 遮挡、标注：在画面上拖出来、选中后拖动 / 拉八个点、双击改字；Decor 画选中框和把手
        public Func<double, double, bool> OverlayDown;
        public Action<double, double> OverlayMove;
        public Action OverlayUp;
        public Func<double, double, bool> OverlayDouble;
        public Func<double, double, Cursor> OverlayCursor;
        public Action<Graphics, RectangleF> Decor;
        bool draggingCam, draggingOv, panning;
        RectangleF imgRect;
        public RectangleF ImageRect => imgRect;
        // 预览放大（像剪映）：1 = 适应窗口；Pan = 放大后挪了多少（像素，右 / 下为正）。只放大预览，不影响成片
        public float Zoom = 1; public PointF Pan;
        Point panFrom; PointF panBase;
        RectangleF FitRect() { float k = Math.Min((Width - 16f) / Image.Width, (Height - 16f) / Image.Height); float w = Image.Width * k, h = Image.Height * k; return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h); }
        void ClampPan()
        {
            if (Image == null) return;
            var f = FitRect(); float mx = f.Width * (Zoom - 1) / 2, my = f.Height * (Zoom - 1) / 2;
            Pan = new PointF(Math.Min(Math.Max(Pan.X, -mx), mx), Math.Min(Math.Max(Pan.Y, -my), my));
        }
        // 以 anchor（本控件坐标）为中心放大：那一点在放大前后不动
        public void SetZoom(float z, Point? anchor = null)
        {
            z = Math.Min(Math.Max(z, 1), 6);
            if (Image != null && anchor is Point a)
            {
                float cx = Width / 2f + Pan.X, cy = Height / 2f + Pan.Y;
                Pan = new PointF(Pan.X - (a.X - cx) * (z / Zoom - 1), Pan.Y - (a.Y - cy) * (z / Zoom - 1));
            }
            Zoom = z; if (Zoom <= 1.001) Pan = PointF.Empty;
            ClampPan(); Invalidate();
        }
        // 右下角的 − 100% + 适应
        RectangleF ZoomBar => new RectangleF(Width - 196, Height - 38, 186, 28);
        RectangleF ZMinus => new RectangleF(ZoomBar.X + 6, ZoomBar.Y + 3, 22, 22);
        RectangleF ZPlus => new RectangleF(ZoomBar.X + 86, ZoomBar.Y + 3, 22, 22);
        RectangleF ZFit => new RectangleF(ZoomBar.X + 114, ZoomBar.Y + 3, 66, 22);
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0) { SetZoom(Zoom * (float)Math.Pow(1.0015, e.Delta), e.Location); return; }   // 触控板双指捏合也是发这个
            if (Zoom > 1.001) { if ((ModifierKeys & Keys.Shift) != 0) Pan = new PointF(Pan.X + e.Delta / 2f, Pan.Y); else Pan = new PointF(Pan.X, Pan.Y + e.Delta / 2f); ClampPan(); Invalidate(); return; }
            base.OnMouseWheel(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020E && Zoom > 1.001) { int d = (short)((long)m.WParam >> 16 & 0xffff); Pan = new PointF(Pan.X - d / 2f, Pan.Y); ClampPan(); Invalidate(); m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        public PreviewBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Under;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8))
            using (var b = new SolidBrush(Theme.Under)) g.FillPath(b, path);
            if (Image != null)
            {
                ClampPan();
                var f = FitRect();
                float w = f.Width * Zoom, h = f.Height * Zoom;
                imgRect = new RectangleF((Width - w) / 2 + Pan.X, (Height - h) / 2 + Pan.Y, w, h);
                var clip = g.Clip;
                using (var cp = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8)) g.SetClip(cp);   // 放大后裁在框里
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(Image, imgRect);
                Decor?.Invoke(g, imgRect);
                g.Clip = clip;
                // 右下角放大控件：放大时常亮，平时淡一点
                var zb = ZoomBar;
                using (var path = Theme.Round(zb, zb.Height / 2))
                using (var b = new SolidBrush(Color.FromArgb(Zoom > 1.001 ? 230 : 150, Theme.Control))) g.FillPath(b, path);
                using (var f2 = new Font("Segoe UI", 8.5f))
                {
                    TextRenderer.DrawText(g, "−", f2, Rectangle.Round(ZMinus), Zoom > 1.001 ? Theme.Text : Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, (int)Math.Round(Zoom * 100) + "%", f2, Rectangle.Round(new RectangleF(ZMinus.Right, zb.Y, ZPlus.X - ZMinus.Right, zb.Height)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, "+", f2, Rectangle.Round(ZPlus), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, App.L("适应", "Fit"), f2, Rectangle.Round(ZFit), Zoom > 1.001 ? Theme.Accent : Theme.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            if (Hint != null)
            {
                var sz = g.MeasureString(Hint, Font);
                var r = new RectangleF((Width - sz.Width) / 2 - 12, 10, sz.Width + 24, sz.Height + 10);
                using (var path = Theme.Round(r, r.Height / 2))
                using (var b = new SolidBrush(Color.FromArgb(230, Theme.Control))) g.FillPath(b, path);
                using (var t = new SolidBrush(Theme.Text)) g.DrawString(Hint, Font, t, r.X + 12, r.Y + 5);
            }
        }

        public int MouseMoves;   // 自测诊断
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MouseMoves++;
            if (panning) { Pan = new PointF(panBase.X + e.X - panFrom.X, panBase.Y + e.Y - panFrom.Y); ClampPan(); Invalidate(); return; }
            if (draggingCam && imgRect.Width > 0) { MoveCamera?.Invoke((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height); return; }
            if (draggingOv && imgRect.Width > 0) { OverlayMove?.Invoke((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height); return; }
            var oc = !Picking && imgRect.Width > 0 && OverlayCursor != null ? OverlayCursor((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height) : null;
            Cursor = oc ?? (Picking && imgRect.Contains(e.Location) ? Cursors.Cross : Cursors.Default);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (panning) { panning = false; Capture = false; Cursor = Cursors.Default; return; }
            if (draggingCam) { draggingCam = false; Capture = false; ReleaseCamera?.Invoke(); }
            if (draggingOv) { draggingOv = false; Capture = false; OverlayUp?.Invoke(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!Picking && e.Button == MouseButtons.Left && imgRect.Width > 0 && imgRect.Contains(e.Location))
                OverlayDouble?.Invoke((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left && Image != null && ZoomBar.Contains(e.Location))
            {
                if (ZMinus.Contains(e.Location)) SetZoom(Zoom / 1.25f); else if (ZPlus.Contains(e.Location)) SetZoom(Zoom * 1.25f); else if (ZFit.Contains(e.Location)) SetZoom(1);
                return;
            }
            // 放大后：中键或空格按住拖 = 平移
            if (Zoom > 1.001 && e.Button == MouseButtons.Middle) { panning = true; panFrom = e.Location; panBase = Pan; Capture = true; Cursor = Cursors.SizeAll; return; }
            if (!Picking && e.Button == MouseButtons.Left && imgRect.Width > 0 && imgRect.Contains(e.Location) && e.Clicks == 1 &&
                OverlayDown != null && OverlayDown((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height)) { draggingOv = true; Capture = true; return; }
            if (!Picking && e.Button == MouseButtons.Left && imgRect.Width > 0 && imgRect.Contains(e.Location) &&
                GrabCamera != null && GrabCamera((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height)) { draggingCam = true; Capture = true; Cursor = Cursors.SizeAll; return; }
            if (!Picking || !imgRect.Contains(e.Location) || imgRect.Width <= 0) return;
            Pick?.Invoke((e.X - imgRect.X) / imgRect.Width, (e.Y - imgRect.Y) / imgRect.Height);
        }
    }

    // 预览声音：MCI 放 wav，可以从任意位置开始
    static class Mci
    {
        static bool open;
        public static void Play(string wav, double from)
        {
            try
            {
                Stop();
                if (mciSendString($"open \"{wav}\" type waveaudio alias studiopv", null, 0, IntPtr.Zero) != 0) return;
                open = true;
                mciSendString("set studiopv time format milliseconds", null, 0, IntPtr.Zero);
                mciSendString($"play studiopv from {(int)(from * 1000)}", null, 0, IntPtr.Zero);
            }
            catch { }
        }
        public static void Stop()
        {
            if (!open) return;
            try { mciSendString("stop studiopv", null, 0, IntPtr.Zero); mciSendString("close studiopv", null, 0, IntPtr.Zero); } catch { }
            open = false;
        }
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
    }

    // 剪辑窗口的配色（我们自己画的部分统一用这几种颜色）
    static class Theme
    {
        public static readonly bool Dark = IsDark();
        public static Color Window => Dark ? Color.FromArgb(32, 32, 34) : Color.FromArgb(246, 246, 247);
        public static Color Control => Dark ? Color.FromArgb(52, 52, 56) : Color.White;
        public static Color Under => Dark ? Color.FromArgb(22, 22, 24) : Color.FromArgb(228, 228, 231);
        public static Color Line => Dark ? Color.FromArgb(64, 64, 68) : Color.FromArgb(218, 218, 222);
        public static Color Text => Dark ? Color.FromArgb(235, 235, 238) : Color.FromArgb(28, 28, 30);
        public static Color Secondary => Dark ? Color.FromArgb(160, 160, 166) : Color.FromArgb(110, 110, 116);
        public static Color Accent => Color.FromArgb(0, 103, 192);
        public static Color Segment => Color.FromArgb(108, 92, 242);

        static bool IsDark()
        {
            // WinForms 自带的滑块、勾选框、下拉框不跟深色，混在一起难看：默认浅色（EDITOR_APPEARANCE=dark 可以看深色效果）
            return Environment.GetEnvironmentVariable("EDITOR_APPEARANCE") == "dark";
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Max(0.1f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 深色模式下标题栏也用深色（Windows 10 2004+ / 11）
        public static void DarkTitle(IntPtr h)
        {
            if (!Dark) return;
            try { int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4); } catch { }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }
}
