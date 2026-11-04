// 剪辑窗口：文字转配音（照 Mac 版 Speak.swift 的 speakCaptions / previewVoice / SpeakBox）。
// 「字幕」菜单里「文字转配音…」打开设置小窗：选声音来源 → 选声音 →（云端）填 key → 语速 → 试听 / 全部念；
// 字幕属性条上「念成配音」只念选中那句。念好的放到讲解轨上、从那句字幕开始，同一句以前念过的那段换掉；一次放进去，一步撤销
using System;
using System.Collections.Generic;
using System.Drawing;
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
        public double? Speaking;          // 正在念（0–1）
        public int SpeakDone;             // 自测用：念完几次
        public string LastSpeakError;     // 自测用
        bool previewing;
        System.Media.SoundPlayer previewPlayer;
        System.Speech.Synthesis.SpeechSynthesizer previewSynth;

        // 把这些字幕念成配音（不给 = 全部有字的）；进度在下面状态栏
        public void SpeakCaptions(IEnumerable<string> ids = null)
        {
            if (Speaking != null || capBusy || exporting || mic != null) return;
            var want = ids?.ToList();
            var caps = Project.Captions.Where(c => (want == null || want.Contains(c.Id)) && !string.IsNullOrWhiteSpace(c.Text)).Select(c => (c.Id, c.Start, c.Text)).ToList();
            if (caps.Count == 0) { System.Media.SystemSounds.Beep.Play(); return; }
            Speaking = 0; taskCancel = new CancellationTokenSource();
            var token = taskCancel.Token;
            exportBar.Visible = true; exportBar.Value = 0; cancelBtn.Visible = true; openBtn.Visible = showBtn.Visible = false;
            exportStatus.ForeColor = Theme.Secondary; exportStatus.Text = L("正在念…", "Speaking…");
            Changed?.Invoke();
            string dir = Dir;
            void Status(string text, double? f)
            {
                try { BeginInvoke((Action)(() => { exportStatus.Text = text + (f is double v ? $" {Math.Round(v * 100)}%" : ""); exportBar.Value = (int)Math.Min(100, Math.Max(0, (f ?? 0) * 100)); })); } catch { }
            }
            Task.Run(() =>
            {
                var made = new List<(string capId, VoiceClip clip)>(); string err = null; bool cancelled = false;
                for (int k = 0; k < caps.Count; k++)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    Speaking = k / (double)caps.Count;
                    Status(L($"正在念第 {k + 1} / {caps.Count} 句", $"Speaking {k + 1} of {caps.Count}"), Speaking);
                    try
                    {
                        var (name, len) = Tts.SynthLine(dir, caps[k].Text, caps[k].Id, Status, token);
                        if (len <= 0.05) continue;
                        made.Add((caps[k].Id, new VoiceClip { File = name, Start = caps[k].Start, TrimIn = 0, TrimOut = len, FileLength = len, Volume = 1 }));
                    }
                    catch (OperationCanceledException) { cancelled = true; break; }
                    catch (Exception e) { err = e.Message; break; }
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        Speaking = null; exportBar.Visible = false; cancelBtn.Visible = false;
                        LastSpeakError = err;
                        if (made.Count > 0)
                        {
                            // 同一句以前念过的那段换掉（文件名前缀 voice/tts-字幕id前8位）
                            var keys = new HashSet<string>(made.Select(m => Prefix(m.clip.File)));
                            Edit(p =>
                            {
                                p.Voiceovers.RemoveAll(v => keys.Contains(Prefix(v.File)));
                                foreach (var m in made) p.Voiceovers = VoiceClip.Place(m.clip, p.Voiceovers, new TimeMap(p));
                            });
                        }
                        if (err != null) { exportStatus.ForeColor = Color.FromArgb(200, 30, 30); exportStatus.Text = L("念不出来：", "Couldn't speak: ") + err; }
                        else if (cancelled) exportStatus.Text = made.Count > 0 ? L($"已停下，念好的 {made.Count} 句放在讲解轨上", $"Stopped; {made.Count} spoken captions are on the voice lane") : L("已取消念配音", "Speaking cancelled");
                        else exportStatus.Text = L($"念好了 {made.Count} 句，放在讲解轨上 · Ctrl+Z 撤销", $"Spoke {made.Count} captions onto the voice-over lane · Ctrl+Z to undo");
                        SpeakDone++;
                        Changed?.Invoke();
                    }));
                }
                catch { }
            });
        }
        // voice/tts-<字幕id前8位>-xxxx.wav → voice/tts-<字幕id前8位>
        static string Prefix(string file) { const string p = "voice/tts-"; return file != null && file.StartsWith(p) && file.Length >= p.Length + 8 ? file.Substring(0, p.Length + 8) : file; }

        // 试听：用现在选的声音念第一句字幕（没有就念一句示例）
        public void PreviewVoice(Action<string> error = null)
        {
            var text = Project.Captions.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text))?.Text ?? L("大家好，这是配音试听。", "Hello, this is a voice preview.");
            try { previewSynth?.SpeakAsyncCancelAll(); previewPlayer?.Stop(); } catch { }
            var e = Tts.Engine;
            if (e == "system")
            {
                try
                {
                    previewSynth = previewSynth ?? new System.Speech.Synthesis.SpeechSynthesizer();
                    var v = SystemTts.Pick(text, Tts.Voice("system"));
                    if (v != null) previewSynth.SelectVoice(v);
                    previewSynth.Rate = SystemTts.SapiRate(Tts.Rate);
                    previewSynth.SpeakAsync(text);
                }
                catch (Exception ex) { error?.Invoke(ex.Message); }
                return;
            }
            if (previewing) return;
            previewing = true; Changed?.Invoke();
            var tmp = Path.Combine(Path.GetTempPath(), "tts-preview-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".wav");
            Task.Run(() =>
            {
                string err = null, file = tmp;
                try
                {
                    if (e == "local") { LocalTts.EnsureModel(null, CancellationToken.None); LocalTts.Synth(text, Tts.Voice(e), Tts.Rate, tmp, CancellationToken.None); }
                    else file = CloudTts.Synth(e, text, Tts.Voice(e), Tts.Rate, tmp);
                    // SoundPlayer 只放 wav：别的格式先转一下
                    if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        var w = Path.ChangeExtension(file, ".conv.wav");
                        if (Studio.RunQuiet($"-v error -y -i \"{file}\" \"{w}\"", 60000)) file = w;
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        previewing = false; Changed?.Invoke();
                        if (err != null) { error?.Invoke(err); exportStatus.ForeColor = Color.FromArgb(200, 30, 30); exportStatus.Text = err; return; }
                        try { previewPlayer = new System.Media.SoundPlayer(file); previewPlayer.Play(); } catch (Exception ex) { error?.Invoke(ex.Message); }
                    }));
                }
                catch { }
            });
        }

        // ---- 「文字转配音」小窗（同 Mac 版 SpeakBox）----
        Form speakBox;
        public Form SpeakBox => speakBox;
        public void ShowSpeakBox()
        {
            if (speakBox != null && !speakBox.IsDisposed) { speakBox.Activate(); return; }
            var f = new Form
            {
                Text = L("文字转配音", "Text to Speech"), FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, Owner = this, Font = Font, BackColor = Theme.Window, AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(380, 300),
            };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 12, 14, 12), AutoScroll = true };
            int W = 350;
            Label Lbl(string t, bool sub = false) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = sub ? Theme.Secondary : Theme.Text, Margin = new Padding(0, 2, 0, 2), Font = sub ? new Font(Font.FontFamily, Font.Size - 1) : Font };
            ComboBox Box() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W, Margin = new Padding(0, 0, 0, 6) };

            var engine = Box(); foreach (var e in Tts.Engines) engine.Items.Add(Tts.Name(e));
            var voice = Box();
            var key = new TextBox { Width = W, UseSystemPasswordChar = true, Margin = new Padding(0, 0, 0, 4) };
            var region = new TextBox { Width = W, Margin = new Padding(0, 0, 0, 4) };
            var keyL = Lbl(""); var regionL = Lbl("");
            var help = new LinkLabel { AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 4) };
            var note = Lbl("", true);
            var rateRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 6) };
            var rate = new TrackBar { Minimum = 60, Maximum = 140, TickStyle = TickStyle.None, AutoSize = false, Width = 220, Height = 28 };
            var rateV = new Label { AutoSize = false, Width = 50, Height = 24, ForeColor = Theme.Secondary, TextAlign = ContentAlignment.MiddleLeft };
            rateRow.Controls.Add(new Label { Text = L("语速", "Speed"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }); rateRow.Controls.Add(rate); rateRow.Controls.Add(rateV);
            var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var all = new Button(); Primary(all, L("全部念成配音", "Speak All"), (s, e) => SpeakCaptions());
            var pre = new Button(); Flat(pre, L("试听", "Preview"), (s, e) => PreviewVoice(err => note.Text = err));
            btnRow.Controls.Add(all); btnRow.Controls.Add(pre);

            bool loading = false;
            void Load()
            {
                loading = true;
                var e = Tts.Engine;
                engine.SelectedIndex = Array.IndexOf(Tts.Engines, e);
                voice.Items.Clear();
                var vs = Tts.Voices(e);
                if (e == "system") voice.Items.Add(L("自动（按字幕语言挑）", "Auto (by the caption's language)"));
                foreach (var v in vs) voice.Items.Add(v.name);
                var cur = Tts.Voice(e); int i = vs.FindIndex(v => v.id == cur);
                voice.SelectedIndex = e == "system" ? i + 1 : Math.Max(0, i);
                bool cloud = Tts.Cloud(e);
                key.Visible = keyL.Visible = help.Visible = cloud;
                region.Visible = regionL.Visible = e == "azure" || e == "volc";
                keyL.Text = e == "volc" ? "Access Token" : "API key";
                regionL.Text = e == "volc" ? "AppID" : L("区域（比如 eastasia、eastus）", "Region (e.g. eastasia, eastus)");
                key.Text = cloud ? TtsKeys.Get(e) : ""; region.Text = region.Visible ? TtsKeys.Get(e, "region") : "";
                var h = Tts.KeyHelp(e); help.Text = h != null ? h.Value.text + " ↗" : ""; help.Tag = h?.url;
                note.Text = cloud ? L("key 只存在这台电脑的凭据管理器里；念的时候把字幕文字发给这家服务，按字数收费（一般几分钱一段）。", "The key stays in this PC's Credential Manager; caption text is sent to this service when speaking, billed per character.")
                          : e == "local" ? (LocalTts.Ready ? L("在这台电脑上用 AI 念，不上传、不收费。", "AI voice on this PC — nothing uploaded, free.") : L("第一次用会下载约 360MB 的声音模型（只下一次），之后不联网也能用。", "First use downloads a ~360MB voice model once; works offline afterwards."))
                          : (vs.Count == 0 ? L("这台电脑没有装系统声音：在「设置 → 时间和语言 → 语音」里添加。", "No Windows voices installed: add one in Settings → Time & Language → Speech.") : L("Windows 自带的声音，不用下载。", "Built-in Windows voices, nothing to download."));
                rate.Value = (int)Math.Round(Math.Min(1.4, Math.Max(0.6, Tts.Rate)) * 100); rateV.Text = $"{Tts.Rate:0.0}×";
                loading = false;
            }
            engine.SelectionChangeCommitted += (s, e) => { Tts.Engine = Tts.Engines[engine.SelectedIndex]; Load(); };
            voice.SelectionChangeCommitted += (s, e) =>
            {
                var en = Tts.Engine; var vs = Tts.Voices(en); int i = voice.SelectedIndex - (en == "system" ? 1 : 0);
                Tts.SetVoice(en, i >= 0 && i < vs.Count ? vs[i].id : "");
            };
            key.TextChanged += (s, e) => { if (!loading) TtsKeys.Set(Tts.Engine, "key", key.Text.Trim()); };
            region.TextChanged += (s, e) => { if (!loading) TtsKeys.Set(Tts.Engine, "region", region.Text.Trim()); };
            help.LinkClicked += (s, e) => { if (help.Tag is string u) try { System.Diagnostics.Process.Start(u); } catch { } };
            rate.Scroll += (s, e) => { Tts.Rate = rate.Value / 100.0; rateV.Text = $"{Tts.Rate:0.0}×"; };

            flow.Controls.Add(Lbl(L("声音来源", "Engine"))); flow.Controls.Add(engine);
            flow.Controls.Add(Lbl(L("声音", "Voice"))); flow.Controls.Add(voice);
            flow.Controls.Add(keyL); flow.Controls.Add(key); flow.Controls.Add(regionL); flow.Controls.Add(region); flow.Controls.Add(help);
            flow.Controls.Add(note); flow.Controls.Add(rateRow); flow.Controls.Add(btnRow);
            f.Controls.Add(flow);
            Load();
            // 按内容定高度；放在「字幕」按钮上方
            f.Load += (s, e) =>
            {
                int h = flow.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height + c.Margin.Vertical) + flow.Padding.Vertical + 8;
                f.ClientSize = new Size(f.ClientSize.Width, Math.Min(560, h));
                var p = capBtn.PointToScreen(new Point(0, 0));
                f.Location = new Point(Math.Max(0, p.X), Math.Max(0, p.Y - f.Height - 6));
            };
            void Sync() { if (!f.IsDisposed) { all.Enabled = Project.Captions.Count > 0 && Speaking == null; pre.Enabled = !previewing; pre.Text = previewing ? L("生成中…", "Working…") : L("试听", "Preview"); } }
            Action onChange = Sync; Changed += onChange;
            f.FormClosed += (s, e) => { Changed -= onChange; speakBox = null; };
            Sync();
            speakBox = f;
            f.Show(this);
        }
    }
}
