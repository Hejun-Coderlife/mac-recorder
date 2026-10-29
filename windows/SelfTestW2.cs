// W2 的自测（CI 上跑）：
//   W2TEST=工作文件夹 [W2SPEECH=中文语音 wav] [W2WHISPER=1] → 讲解 / 字幕规则、真导出（讲解时刻、压低、音量、降噪、字幕烧进画面）；
//       W2WHISPER=1 时真的下载识别模型（没有的话）、用 whisper-cli 识别，再跑一遍「录到的声音 / 讲解 → 字幕」的整条流程；结果写 %TEMP%\w2_test.txt
//   VOICEUITEST=素材夹 VOICE_INJECT=wav [W2WHISPER=1] [VOICEUI_SHOTS=文件夹] → 打开剪辑窗口，点「录讲解」录 4 秒（声音从文件来，不开真麦克风），
//       停下后检查讲解放进工程、位置和长短；W2WHISPER=1 时再点「字幕 → 从讲解生成」；中途截几张图；结果写 %TEMP%\voiceui_test.txt
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class SelfTestW2
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Add(string res) { sb.Append(res); ok &= res.TrimEnd().EndsWith(" OK"); File.WriteAllText(Tmp("w2_test.txt"), sb.ToString()); }
            try
            {
                string work = Env("W2TEST"), speech = Env("W2SPEECH");
                Add(StudioTestsW2.Voice());
                Add(StudioTestsW2.Captions());
                Add(StudioTestsW2.Export(work, speech));
                if (Env("W2WHISPER") == "1" && speech != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool had = Whisper.ModelReady;
                    Whisper.EnsureModel(f => { }, CancellationToken.None);
                    sb.AppendLine($"model: {(had ? "already there" : "downloaded from " + Whisper.LastSource)} in {sw.Elapsed.TotalSeconds:0.0}s, cli={Whisper.CliPath}, size={new FileInfo(Whisper.ModelPath).Length}");
                    Add(StudioTestsW2.Whisper(speech, wav => Whisper.Transcribe(wav, f => { }, CancellationToken.None)));
                    Add(StudioTestsW2.Maker(Path.Combine(work, "maker"), speech, (wav, prog) => Whisper.Transcribe(wav, prog, CancellationToken.None)));
                }
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            File.WriteAllText(Tmp("w2_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        static void Pump(double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
        static bool PumpUntil(Func<bool> c, double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { if (c()) return true; Application.DoEvents(); Thread.Sleep(10); } return c(); }

        public static int VoiceUi()
        {
            var log = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { log.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("voiceui_test.txt"), log.ToString()); }
            StudioEditor ed = null;
            string shots = Env("VOICEUI_SHOTS");
            void Shot(string name)
            {
                if (shots == null || ed == null) return;
                Directory.CreateDirectory(shots);
                using (var bmp = new Bitmap(ed.Width, ed.Height)) { ed.DrawToBitmap(bmp, new Rectangle(0, 0, ed.Width, ed.Height)); bmp.Save(Path.Combine(shots, name + ".png"), ImageFormat.Png); }
            }
            try
            {
                var dir = Env("VOICEUITEST");
                try { File.Delete(StudioProject.PathIn(dir)); } catch { }
                ed = new StudioEditor(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                ed.ClientSize = new Size(1280, 800);
                ed.Show(); ed.Activate();
                PumpUntil(() => ed.FirstFrameShown, 30);
                Check("editor opened", ed.FirstFrameShown);
                ed.Seek(1.0); Pump(0.5);
                // 点「录讲解」：VOICE_NOCOUNTDOWN 时马上开始；声音从 VOICE_INJECT 来
                ed.ToggleVoice();
                Check("recording started", PumpUntil(() => ed.RecordingVoice, 5), ed.LastVoiceError ?? "");
                Pump(2.0);
                Check("preview is playing while recording", ed.Playing && ed.Time > 1.5, $"time {ed.Time:0.00}");
                Shot("voice-recording");
                Pump(2.0);
                ed.ToggleVoice();
                Check("voice-over added to the project", PumpUntil(() => ed.VoiceAdded == 1, 30), ed.CaptionStatus);
                var v = ed.Project.Voiceovers.FirstOrDefault();
                Check("one clip, file saved as m4a", ed.Project.Voiceovers.Count == 1 && v != null && v.File.EndsWith(".m4a") && File.Exists(Path.Combine(dir, v.File)), v?.File ?? "");
                Check("clip anchored at the playhead (1.0 s)", v != null && Math.Abs(v.Start - 1.0) < 0.05, $"{v?.Start:0.000}");
                Check("clip length ≈ 4 s", v != null && Math.Abs(v.Length - 4.0) < 0.6, $"{v?.Length:0.00}s (trimIn {v?.TrimIn:0.000})");
                Check("wav removed after converting", !Directory.GetFiles(Path.Combine(dir, "voice"), "*.wav").Any());
                Check("clip selected (inspector shown)", ed.SelectedVoice == v?.Id);
                PumpUntil(() => ed.Peaks(v.File) != null, 10); Pump(0.5);
                Shot("voice-added");
                // 再录一段盖住后半截：旧的剪短
                ed.Seek(3.0); Pump(0.3);
                ed.ToggleVoice(); PumpUntil(() => ed.RecordingVoice, 5); Pump(1.5); ed.ToggleVoice();
                PumpUntil(() => ed.VoiceAdded == 2, 30);
                var vs = ed.Project.Voiceovers.OrderBy(x => x.Start).ToList();
                // 第一段 1–5 秒，第二段从 3 秒录 1.5 秒：第一段剪成 1–3 和 4.5–5 两截，新的在中间
                Check("second take over the first: old one split around the new one", vs.Count == 3 && Math.Abs(vs[0].Length - 2.0) < 0.15 && Math.Abs(vs[1].Start - 3.0) < 0.05 && vs[2].TrimIn > vs[0].TrimIn + 3,
                      string.Join(" ", vs.Select(x => $"{x.Start:0.00}+{x.Length:0.00}")));
                ed.Undo(); Pump(0.3);
                Check("undo brings the first take back whole", ed.Project.Voiceovers.Count == 1 && Math.Abs(ed.Project.Voiceovers[0].Length - v.Length) < 0.01);
                // Esc 停录音
                ed.Seek(5.0); ed.ToggleVoice(); PumpUntil(() => ed.RecordingVoice, 5); Pump(0.8);
                SendKeys.SendWait("{ESC}");
                Check("Esc stops recording", PumpUntil(() => !ed.RecordingVoice, 5));
                PumpUntil(() => ed.VoiceAdded == 3, 30); Pump(0.3);
                // 字幕：从讲解生成（真识别）
                if (Env("W2WHISPER") == "1")
                {
                    ed.Undo(); Pump(0.3);   // 去掉 Esc 那段（太短的话本来就没留下）
                    ed.GenerateCaptions("voice");
                    Pump(1.0);
                    Shot("captions-progress");
                    Check("captions generated", PumpUntil(() => ed.CaptionsDone > 0, 900), ed.CaptionStatus);
                    var caps = ed.Project.Captions;
                    var text = string.Concat(caps.Select(c => c.Text));
                    Check("captions have the spoken words", caps.Count > 0 && text.Contains("字幕"), text);
                    var first = caps.FirstOrDefault();
                    // 讲解从 1.0 秒开始，录音从开头就是那段语音（W2SPEECH 里 1.12 秒处开始说话）
                    Check("first caption lines up with the voice-over", first != null && Math.Abs(first.Start - (1.0 + 1.12 - (v?.TrimIn ?? 0))) < 0.5, $"{first?.Start:0.00}");
                    ed.Seek(first != null ? (first.Start + first.End) / 2 : 2); Pump(1.5);
                    ed.Select("cap", first?.Id);
                    ed.ScrollStyleTo(App.L("字幕", "Captions")); Pump(0.8);
                    Shot("captions");
                    // 改字：属性条里改，回车
                    ed.EditCaptionText(); Pump(0.2);
                    SendKeys.SendWait("改过的字幕{ENTER}"); Pump(0.5);
                    Check("edit caption text in the inspector", ed.Project.Captions.Any(c => c.Text == "改过的字幕"), string.Join("|", ed.Project.Captions.Select(c => c.Text)));
                    ed.Undo(); Pump(0.3);
                    // 导出成片（带讲解、字幕），抽一帧
                    var outF = Path.Combine(Path.GetTempPath(), "voiceui-export.mp4");
                    ed.OutPath = outF; ed.ExportOpt = new ExportOptions { Resolution = "720", Fps = 30, Quality = "standard" };
                    ed.Export();
                    PumpUntil(() => !ed.Exporting, 300);
                    Check("export with voice-over + captions", File.Exists(outF) || Directory.GetFiles(Path.GetDirectoryName(outF), "voiceui-export*.mp4").Any(), ed.ExportStatusText);
                }
                ed.Close();
                Check("project saved with voiceovers", File.Exists(StudioProject.PathIn(dir)) && StudioProject.FromJson(File.ReadAllText(StudioProject.PathIn(dir))).Voiceovers.Count >= 1);
            }
            catch (Exception e) { Check("exception", false, e.ToString()); try { ed?.Close(); } catch { } }
            File.WriteAllText(Tmp("voiceui_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + log);
            return ok ? 0 : 1;
        }
    }
}
