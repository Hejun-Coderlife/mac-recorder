// 剪辑窗口：录讲解、讲解 / 字幕的属性条、生成字幕（whisper，本机）、导出 .srt、预览上画字幕。
// 照 Mac 版 StudioEditor.swift 的讲解、字幕部分；数据和规则在 StudioVoice.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor
    {
        // ---- 讲解的波形（时间轴上画）：每 20 毫秒一个峰值，后台算好记住 ----
        readonly Dictionary<string, float[]> peaks = new Dictionary<string, float[]>();
        readonly HashSet<string> peaking = new HashSet<string>();
        public float[] Peaks(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (peaks.TryGetValue(file, out var p)) return p;
            if (peaking.Add(file))
                Task.Run(() =>
                {
                    float[] r = null;
                    try
                    {
                        var pcm = Studio.DecodePcm(Path.Combine(Dir, file), 8000);
                        if (pcm != null)
                        {
                            r = new float[pcm.Length / 160 + 1];
                            for (int i = 0; i < pcm.Length; i++) { float v = Math.Abs(pcm[i]); if (v > r[i / 160]) r[i / 160] = v; }
                        }
                    }
                    catch { }
                    try { BeginInvoke((Action)(() => { peaks[file] = r; Timeline.Invalidate(); })); } catch { }
                });
            return null;
        }

        // ---- 录讲解 ----
        MicRecorder mic;
        string recWav; double recT0, recLead;
        int countdownLeft;
        readonly System.Windows.Forms.Timer recTimer = new System.Windows.Forms.Timer { Interval = 100 };
        readonly System.Windows.Forms.Timer cdTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        public bool RecordingVoice => mic != null;
        string voiceHint;
        public double? RecordStart => mic != null ? recT0 : (double?)null;
        public string LastVoiceError;   // 自测用
        public int VoiceAdded;          // 自测用：录好放进工程几次

        public void ToggleVoice()
        {
            if (mic != null) { StopVoice(); return; }
            if (countdownLeft > 0) { cdTimer.Stop(); countdownLeft = 0; Changed?.Invoke(); return; }
            if (exporting || capBusy) return;
            StopPlayback();
            var map = new TimeMap(Project);
            double t0 = Time;
            if (t0 >= Project.TrimEnd - 0.3 || t0 < Project.TrimStart) t0 = Project.TrimStart;
            if (map.At(t0) is TimeMap.Piece pc && pc.Rate == 0) t0 = pc.End;
            Time = t0;
            // 倒数 3、2、1（预览上显示），再开始
            if (Settings.Get("voiceCountdown", true) && Environment.GetEnvironmentVariable("VOICE_NOCOUNTDOWN") == null)
            {
                if (cdTimer.Tag == null) { cdTimer.Tag = 1; cdTimer.Tick += (s, e) => { countdownLeft--; if (countdownLeft <= 0) { cdTimer.Stop(); BeginVoice(Time); } Changed?.Invoke(); }; }
                countdownLeft = 3; cdTimer.Start();
                Changed?.Invoke();
                return;
            }
            BeginVoice(t0);
        }

        void BeginVoice(double t0)
        {
            countdownLeft = 0;
            var vdir = Path.Combine(Dir, "voice");
            Directory.CreateDirectory(vdir);
            recWav = Path.Combine(vdir, "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".wav");
            try { mic = new MicRecorder(recWav); }
            catch (Exception e) { LastVoiceError = e.Message; exportStatus.ForeColor = Color.FromArgb(200, 30, 30); exportStatus.Text = e.Message; mic = null; Changed?.Invoke(); return; }
            recT0 = t0;
            StartPlayback(t0, !Settings.Get("voiceMute", false));
            recLead = mic.Seconds;   // 开始放预览那一刻已经录了多久：这一截不要
            if (recTimer.Tag == null) { recTimer.Tag = 1; recTimer.Tick += (s, e) => { if (mic != null) { SyncVoiceCaption(); Timeline.Invalidate(); } }; }
            recTimer.Start();
            Changed?.Invoke();
        }

        public void StopVoice()
        {
            var m = mic; if (m == null) return;
            mic = null; recTimer.Stop();
            double dur = 0;
            try { dur = m.Stop(); } catch (Exception e) { LastVoiceError = e.Message; }
            StopPlayback();
            Changed?.Invoke();
            double t0 = recT0, lead = Math.Min(recLead, dur);
            string wav = recWav;
            var m4a = Path.ChangeExtension(wav, ".m4a");
            Task.Run(() =>
            {
                bool ok = dur - lead >= 0.3 && Studio.RunQuiet($"-v error -y -i \"{wav}\" -c:a aac -b:a 128k \"{m4a}\"", 120000) && File.Exists(m4a);
                try { File.Delete(wav); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!ok)
                        {
                            exportStatus.ForeColor = Theme.Secondary;
                            exportStatus.Text = dur - lead < 0.3 ? L("讲解太短，没留下", "Voice-over too short, discarded") : L("讲解保存失败", "Couldn't save the voice-over");
                            return;
                        }
                        var clip = new VoiceClip { File = "voice/" + Path.GetFileName(m4a), Start = t0, TrimIn = lead, TrimOut = dur, FileLength = dur, Volume = 1 };
                        Edit(p => p.Voiceovers = VoiceClip.Place(clip, p.Voiceovers, new TimeMap(p)));
                        Select("voice", clip.Id);
                        VoiceAdded++;
                        exportStatus.ForeColor = Theme.Secondary;
                        exportStatus.Text = L($"录好一段讲解（{dur - lead:0.0} 秒）", $"Voice-over added ({dur - lead:0.0}s)");
                    }));
                }
                catch { }
            });
        }

        // ---- 讲解的属性条：音量、删除 ----
        readonly Panel voiceInspector = new Panel();
        readonly Label voiceTitle = new Label(), voiceTime = new Label(), voiceVolValue = new Label();
        readonly TrackBar voiceVol = new TrackBar();
        readonly Button voiceDelete = new Button();
        void BuildVoiceInspector()
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            voiceTitle.Text = L("讲解", "Voice-over"); voiceTitle.AutoSize = true; voiceTitle.Font = new Font(Font, FontStyle.Bold); voiceTitle.Margin = new Padding(0, 8, 8, 0);
            voiceTime.AutoSize = true; voiceTime.ForeColor = Theme.Secondary; voiceTime.Margin = new Padding(0, 8, 14, 0);
            var volL = new Label { Text = L("音量", "Volume"), AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            voiceVol.Minimum = 0; voiceVol.Maximum = 200; voiceVol.TickStyle = TickStyle.None; voiceVol.AutoSize = false; voiceVol.Width = 150; voiceVol.Height = 28; voiceVol.Margin = new Padding(0, 3, 0, 0);
            voiceVol.Scroll += (s, e) => { var id = SelectedVoice; DragBegin(); DragUpdate(p => { var v = p.Voiceovers.FirstOrDefault(x => x.Id == id); if (v != null) v.Volume = voiceVol.Value / 100.0; }); };
            voiceVol.MouseUp += (s, e) => DragEnd(); voiceVol.KeyUp += (s, e) => DragEnd();
            voiceVolValue.AutoSize = false; voiceVolValue.Width = 46; voiceVolValue.Margin = new Padding(2, 8, 10, 0);
            Flat(voiceDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected());
            flow.Controls.AddRange(new Control[] { voiceTitle, voiceTime, volL, voiceVol, voiceVolValue });
            voiceInspector.Controls.Add(flow);
            voiceDelete.Dock = DockStyle.Right;
            voiceInspector.Controls.Add(voiceDelete);
        }

        // ---- 字幕的属性条：改字、在播放头处拆开、和下一句合并、删除 ----
        readonly Panel capInspector = new Panel();
        readonly Label capTitle = new Label(), capTime = new Label();
        readonly TextBox capText = new TextBox();
        readonly Button capSplit = new Button(), capMerge = new Button(), capDelete = new Button();
        string capEditing;
        void BuildCaptionInspector()
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            capTitle.Text = L("字幕", "Caption"); capTitle.AutoSize = true; capTitle.Font = new Font(Font, FontStyle.Bold); capTitle.Margin = new Padding(0, 8, 8, 0);
            capTime.AutoSize = true; capTime.ForeColor = Theme.Secondary; capTime.Margin = new Padding(0, 8, 10, 0);
            capText.Width = 260; capText.Margin = new Padding(0, 4, 8, 0);
            capText.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { CommitCaptionText(); e.SuppressKeyPress = true; Timeline.Focus(); } else if (e.KeyCode == Keys.Escape) { capEditing = null; SyncVoiceCaption(); Timeline.Focus(); e.SuppressKeyPress = true; } };
            capText.Leave += (s, e) => CommitCaptionText();
            capText.Enter += (s, e) => capEditing = SelectedCaption;
            Flat(capSplit, L("在播放头处拆开", "Split at Playhead"), (s, e) => SplitCaption());
            Flat(capMerge, L("和下一句合并", "Merge with Next"), (s, e) => MergeCaption());
            Flat(capDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected());
            flow.Controls.AddRange(new Control[] { capTitle, capTime, capText, capSplit, capMerge });
            capInspector.Controls.Add(flow);
            capDelete.Dock = DockStyle.Right;
            capInspector.Controls.Add(capDelete);
        }

        void CommitCaptionText()
        {
            var id = capEditing; capEditing = null;
            if (id == null) return;
            var text = capText.Text.Replace("\r", "").Replace("\n", " ").Trim();
            Edit(p => { var c = p.Captions.FirstOrDefault(x => x.Id == id); if (c != null && c.Text != text) c.Text = text; });
        }

        // 双击字幕块：在属性条里改字
        public void EditCaptionText()
        {
            SyncVoiceCaption();
            if (!capInspector.Visible) return;
            capEditing = SelectedCaption;
            capText.Focus(); capText.SelectAll();
        }

        // 字幕轨空白处双击：在这里加一句（2 秒），马上改字
        public void AddCaption(double t)
        {
            var c = new Caption { Start = Math.Max(0, Math.Min(t, Duration - 0.3)), Text = L("新字幕", "New caption") };
            c.End = Math.Min(Duration, c.Start + 2);
            var next = Project.Captions.Where(x => x.Start > c.Start).OrderBy(x => x.Start).FirstOrDefault();
            if (next != null) c.End = Math.Max(c.Start + 0.3, Math.Min(c.End, next.Start));
            Edit(p => { p.Captions.Add(c); p.Captions = p.Captions.OrderBy(x => x.Start).ToList(); });
            Select("cap", c.Id);
            EditCaptionText();
        }

        // 在播放头处拆开：字按时间比例分到两句
        void SplitCaption()
        {
            var id = SelectedCaption; double t = Time;
            var c = Project.Captions.FirstOrDefault(x => x.Id == id);
            if (c == null || t <= c.Start + 0.2 || t >= c.End - 0.2) { System.Media.SystemSounds.Beep.Play(); return; }
            var units = WhisperParse.Graphemes(c.Text);
            int k = Math.Max(1, Math.Min(units.Count - 1, (int)Math.Round(units.Count * (t - c.Start) / (c.End - c.Start))));
            string a = string.Concat(units.Take(k)), b = string.Concat(units.Skip(k));
            var n = new Caption { Start = t, End = c.End, Text = b };
            Edit(p => { var x = p.Captions.First(y => y.Id == id); x.End = t; x.Text = a; p.Captions.Add(n); p.Captions = p.Captions.OrderBy(y => y.Start).ToList(); });
            Select("cap", n.Id);
        }

        void MergeCaption()
        {
            var id = SelectedCaption;
            var list = Project.Captions.OrderBy(x => x.Start).ToList();
            int i = list.FindIndex(x => x.Id == id);
            if (i < 0 || i + 1 >= list.Count) { System.Media.SystemSounds.Beep.Play(); return; }
            var nid = list[i + 1].Id;
            Edit(p => { var a = p.Captions.First(x => x.Id == id); var b = p.Captions.First(x => x.Id == nid); a.End = Math.Max(a.End, b.End); a.Text += b.Text; p.Captions.Remove(b); });
        }

        void SyncVoiceCaption()
        {
            if (IsDisposed) return;
            var v = Project.Voiceovers.FirstOrDefault(x => x.Id == SelectedVoice);
            voiceInspector.Visible = v != null;
            if (v != null)
            {
                var (os, oe) = v.OutRange(new TimeMap(Project));
                voiceTime.Text = Clock(v.Start) + L($"  · 长 {v.Length:0.0} 秒", $"  · {v.Length:0.0}s");
                int vv = (int)Math.Round(Math.Min(2, Math.Max(0, v.Volume)) * 100); if (!IsDragging && voiceVol.Value != vv) voiceVol.Value = vv;
                voiceVolValue.Text = Math.Round(v.Volume * 100) + "%";
            }
            var c = Project.Captions.FirstOrDefault(x => x.Id == SelectedCaption);
            capInspector.Visible = c != null;
            if (c != null)
            {
                capTime.Text = Clock(c.Start) + " – " + Clock(c.End);
                if (capEditing != c.Id && !capText.Focused && capText.Text != c.Text) capText.Text = c.Text;
            }
            // 录讲解按钮：录的时候变红，显示时长和音量
            if (mic != null)
            {
                int bars = (int)Math.Round(Math.Min(1, mic.Level * 2.5) * 5);
                voiceBtn.Text = "■ " + L("停止 ", "Stop ") + Clock(mic.Seconds - recLead) + " " + new string('▮', bars) + new string('▯', 5 - bars);
                voiceBtn.BackColor = Color.FromArgb(220, 50, 50); voiceBtn.ForeColor = Color.White;
                voiceHint = L("● 正在录讲解 · 空格 / Esc 停止", "● Recording voice-over · Space / Esc to stop");
            }
            else if (countdownLeft > 0)
            {
                voiceBtn.Text = L("取消", "Cancel"); voiceBtn.BackColor = Theme.Control; voiceBtn.ForeColor = Theme.Text;
                voiceHint = L($"{countdownLeft} 秒后开始录讲解（Esc 取消）", $"Recording starts in {countdownLeft}… (Esc to cancel)");
            }
            else
            {
                voiceBtn.Text = "🎙 " + L("录讲解", "Voice-over"); voiceBtn.BackColor = Theme.Control; voiceBtn.ForeColor = Theme.Text;
                voiceHint = null;
            }
            preview.Hint = Picking ? preview.Hint : voiceHint; preview.Invalidate();
            bool busy = exporting || capBusy || mic != null || countdownLeft > 0;
            capBtn.Enabled = !busy; exportBtn.Enabled = !busy;
        }

        // ---- 字幕：播放栏「字幕」菜单 ----
        CancellationTokenSource taskCancel;
        bool capBusy;
        public bool CaptionBusy => capBusy;
        public string CaptionStatus => exportStatus.Text;

        void ShowCaptionMenu()
        {
            var m = new ContextMenuStrip();
            bool hasRec = Info.AudioChannels.Count > 0, hasVoice = Project.Voiceovers.Count > 0;
            void Item(string text, bool enabled, Action act) { var it = new ToolStripMenuItem(text) { Enabled = enabled }; it.Click += (s, e) => act(); m.Items.Add(it); }
            m.Items.Add(new ToolStripLabel(L("把说的话变成字幕（本机识别普通话）", "Turn speech into captions (Mandarin, on this PC)")) { ForeColor = Theme.Secondary });
            Item(L("从录到的声音生成", "From the recorded sound"), hasRec, () => GenerateCaptions("recorded"));
            Item(L("从讲解生成", "From the voice-overs"), hasVoice, () => GenerateCaptions("voice"));
            Item(L("两样都识别", "From both"), hasRec && hasVoice, () => GenerateCaptions("both"));
            m.Items.Add(new ToolStripSeparator());
            Item(L("在播放头处加一句", "Add a caption at the playhead"), true, () => AddCaption(Time));
            Item(L("导出 .srt 字幕文件…", "Export .srt subtitles…"), Project.Captions.Count > 0, ExportSrt);
            Item(L("清空字幕", "Clear all captions"), Project.Captions.Count > 0, () => { Edit(p => p.Captions.Clear()); Select(null, null); });
            if (Project.Captions.Count > 0) m.Items.Add(new ToolStripLabel(L("重新生成会替换现在的字幕（可以撤销 Ctrl+Z）", "Generating again replaces the current captions (undo with Ctrl+Z)")) { ForeColor = Theme.Secondary });
            m.Show(capBtn, new Point(0, capBtn.Height));
        }

        // 第一次用先下载识别模型（约 148 MB），再识别、断句，替换现在的字幕（能撤销）
        public void GenerateCaptions(string source)
        {
            if (capBusy || exporting || mic != null) return;
            capBusy = true; taskCancel = new CancellationTokenSource();
            var token = taskCancel.Token;
            exportBar.Visible = true; exportBar.Value = 0; cancelBtn.Visible = true; openBtn.Visible = showBtn.Visible = false;
            exportStatus.ForeColor = Theme.Secondary; exportStatus.Text = L("准备识别…", "Preparing…");
            Changed?.Invoke();
            var p = Project.Clone();
            var lay = new StudioLayout(Track, p.Look);
            void Status(string text, double? f)
            {
                try { BeginInvoke((Action)(() => { exportStatus.Text = text + (f is double v ? $" {Math.Round(v * 100)}%" : ""); exportBar.Value = (int)Math.Min(100, Math.Max(0, (f ?? 0) * 100)); })); } catch { }
            }
            Task.Run(() =>
            {
                List<Caption> caps = null; string err = null; bool cancelled = false;
                try
                {
                    if (!Whisper.ModelReady) Status(L("第一次用：下载识别模型（约 148 MB）", "First use: downloading the speech model (~148 MB)"), 0);
                    Whisper.EnsureModel(f => Status(L("第一次用：下载识别模型（约 148 MB）", "First use: downloading the speech model (~148 MB)"), f), token);
                    caps = CaptionMaker.Make(Dir, Info, p, source, lay.cw, lay.ch, (wav, prog) => Whisper.Transcribe(wav, prog, token), Status, token);
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception e) { err = e.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        capBusy = false; exportBar.Visible = false; cancelBtn.Visible = false;
                        if (cancelled) exportStatus.Text = L("已取消生成字幕", "Captioning cancelled");
                        else if (err != null) { exportStatus.ForeColor = Color.FromArgb(200, 30, 30); exportStatus.Text = L("生成字幕失败：", "Captioning failed: ") + err; }
                        else if (caps == null || caps.Count == 0) exportStatus.Text = L("没识别出说的话", "No speech was recognized");
                        else
                        {
                            Edit(pp => pp.Captions = caps);
                            Select(null, null);
                            exportStatus.Text = L($"生成了 {caps.Count} 句字幕", $"{caps.Count} captions added");
                        }
                        CaptionsDone++;
                        Changed?.Invoke();
                    }));
                }
                catch { }
            });
        }
        public int CaptionsDone;   // 自测用

        void ExportSrt()
        {
            using (var dlg = new SaveFileDialog { Filter = L("字幕文件", "Subtitles") + " (*.srt)|*.srt", FileName = Path.GetFileNameWithoutExtension(OutPath) + ".srt", InitialDirectory = Path.GetDirectoryName(OutPath) })
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try { File.WriteAllText(dlg.FileName, Srt.Text(Project), new System.Text.UTF8Encoding(true)); exportStatus.Text = L("已导出字幕文件", "Subtitles exported"); }
                    catch (Exception e) { exportStatus.Text = e.Message; }
                }
        }

        // ---- 预览上画字幕（成片里是 ffmpeg 的 ass 滤镜烧进去的；这里用 GDI+ 照同样的位置、字号、描边、底色条画） ----
        static readonly string CaptionFont = new InstalledFontCollection().Families.Any(f => f.Name == "Microsoft YaHei") ? "Microsoft YaHei" : SystemFonts.MessageBoxFont.FontFamily.Name;
        void DrawCaption(Bitmap bmp, double t)
        {
            var p = Project;
            if (p.Captions.Count == 0 || !p.CaptionStyle.Show) return;
            var text = new CaptionTrack(p.Captions, true, new TimeMap(p), p.TrimStart, p.TrimEnd).AtSource(t);
            if (string.IsNullOrEmpty(text)) return;
            var st = p.CaptionStyle;
            int cw = bmp.Width, ch = bmp.Height;
            float px = (float)st.FontPx(cw, ch), edge = (float)st.Edge(cw, ch);
            bool box = st.Box != "none";
            using (var g = Graphics.FromImage(bmp))
            using (var fam = new FontFamily(CaptionFont))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var style = st.Weight == "regular" ? FontStyle.Regular : FontStyle.Bold;
                var fmt = StringFormat.GenericTypographic;
                // 太长就缩小字号放进 90% 宽
                float padK = box ? 0.55f : 0.25f;
                SizeF sz;
                using (var f0 = new Font(fam, px, style, GraphicsUnit.Pixel)) sz = g.MeasureString(text, f0, int.MaxValue, fmt);
                if (sz.Width + 2 * padK * px > cw * 0.9f) { px *= cw * 0.9f / (sz.Width + 2 * padK * px); }
                using (var f = new Font(fam, px, style, GraphicsUnit.Pixel))
                {
                    sz = g.MeasureString(text, f, int.MaxValue, fmt);
                    float padX = px * padK, padY = px * (box ? 0.28f : 0.22f);
                    float w = sz.Width + padX * 2, h = sz.Height + padY * 2;
                    float x = (cw - w) / 2, y = st.Position == "top" ? edge - h * 0.2f : ch - edge - h * 0.8f;
                    if (box)
                        using (var path = Theme.Round(new RectangleF(x, y, w, h), px * 0.28f))
                        using (var b = new SolidBrush(st.Box == "dark" ? Color.FromArgb(153, 0, 0, 0) : Color.FromArgb(224, 255, 255, 255))) g.FillPath(b, path);
                    using (var path = new GraphicsPath())
                    {
                        path.AddString(text, fam, (int)style, px, new PointF(x + padX, y + padY), fmt);
                        if (st.Shadow && !box)
                            using (var sh = (GraphicsPath)path.Clone())
                            using (var sb = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                            { var mx = new Matrix(); mx.Translate(0, px * 0.05f); sh.Transform(mx); g.FillPath(sb, sh); }
                        if (st.Outline && !box)
                            using (var pen = new Pen(Color.FromArgb(217, 0, 0, 0), Math.Max(1, px * 0.12f)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                        var c = st.Color;
                        using (var fill = new SolidBrush(Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)))) g.FillPath(fill, path);
                    }
                }
            }
        }
    }
}
