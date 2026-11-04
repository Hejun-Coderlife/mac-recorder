// 文字转配音的自测（CI 上跑；不动鼠标键盘）：
//   TTSTEST=素材夹（有 raw.mp4 / track.json）→ 规则（文件名前缀、wav 时长、SAPI 语速换算、凭据管理器存取 key、每种来源都有声音可选）
//     + 系统声音念一句英文 + 本机 AI 声音（Kokoro，TTS_MODEL_ROOT 指模型文件夹；没有就先下载）念一句中文
//     + 剪辑窗口：两句字幕全部念成配音 → 讲解轨上两段、从字幕开始；只念第一句 → 换掉旧的那段（还是两段）；撤销回去
//     + 导出前检查（占位文字、空字幕；「去修改」不导出）+ HEVC 导出（hevc / hvc1、帧数一样）
//   TTS_ENGINE=system|local 选剪辑窗口那步用哪种（默认 local）；结果写 %TEMP%\tts_test.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenRecorder
{
    static class SelfTestTts
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k);
        static string Tmp(string f) => Path.Combine(Path.GetTempPath(), f);
        static void Pump(double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(10); } }
        static bool PumpUntil(Func<bool> c, double secs) { var until = DateTime.Now.AddSeconds(secs); while (DateTime.Now < until) { if (c()) return true; Application.DoEvents(); Thread.Sleep(10); } return c(); }

        // wav 里有声音的部分占多少（|样本| > 0.02）
        static double Voiced(string wav)
        {
            var pcm = Studio.DecodePcm(wav, 8000);
            return pcm == null || pcm.Length == 0 ? 0 : pcm.Count(v => Math.Abs(v) > 0.02) / (double)pcm.Length;
        }

        public static int Run()
        {
            var sb = new StringBuilder(); bool ok = true;
            void Check(string n, bool c, string info = "") { sb.AppendLine((c ? "PASS " : "FAIL ") + n + (info != "" ? "  " + info : "")); ok &= c; File.WriteAllText(Tmp("tts_test.txt"), sb.ToString()); }
            // 自测会改「用哪种声音」这些设置：跑完改回去
            string oldEngine = Settings.GetString("ttsEngine", null), oldRate = Settings.GetString("ttsRate", null);
            try
            {
                // ---- 规则 ----
                var tmp = Path.Combine(Path.GetTempPath(), "tts-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(tmp);
                // 1 秒 24k 16 位单声道 wav 的时长
                var w = Path.Combine(tmp, "one.wav");
                using (var bw = new BinaryWriter(File.Create(w)))
                {
                    int n = 24000 * 2;
                    bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + n); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
                    bw.Write(24000); bw.Write(48000); bw.Write((short)2); bw.Write((short)16); bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(n); bw.Write(new byte[n]);
                }
                Check("wav length from the header", Math.Abs(Tts.Length(w) - 1) < 1e-6, $"{Tts.Length(w)}");
                Check("SAPI rate: 1× = 0, 3× = 10, ⅓× = -10", SystemTts.SapiRate(1) == 0 && SystemTts.SapiRate(3) == 10 && SystemTts.SapiRate(1 / 3.0) == -10 && SystemTts.SapiRate(1.4) == 3, $"{SystemTts.SapiRate(1.4)}");
                Check("every engine except system has voices to pick", Tts.Engines.Where(e => e != "system").All(e => Tts.Voices(e).Count > 0), "");
                Check("on-device voices are the Mac ones (8 female, 8 male, 1 English)", LocalTts.Voices.Count == 17 && LocalTts.Voices[0].id == "3" && LocalTts.Voices[8].id == "58" && LocalTts.Voices[16].id == "0", "");
                TtsKeys.Set("openai", "key", "sk-test-键"); var got = TtsKeys.Get("openai");
                TtsKeys.Set("openai", "key", ""); var gone = TtsKeys.Get("openai");
                Check("key saved in Credential Manager, then removed", got == "sk-test-键" && gone == "", $"'{got}' '{gone}'");
                Tts.Rate = 9; Check("speed clamped to 0.5–1.6", Tts.Rate == 1.6, $"{Tts.Rate}"); Tts.Rate = 1;
                // ---- 豆包语音合成 2.0：分块 JSON → pcm → WAV（同 Mac 版 Volc.decode）----
                {
                    var p1 = new byte[480]; var p2 = new byte[960];
                    for (int i = 0; i < p1.Length; i++) p1[i] = (byte)(i * 7); for (int i = 0; i < p2.Length; i++) p2[i] = (byte)(i * 3 + 1);
                    // 第二块的 message 里带 { }、引号、反斜杠，块和块之间有的有换行、有的没有
                    var stream = "{\"code\":0,\"message\":\"\",\"data\":\"" + Convert.ToBase64String(p1) + "\"}\n" +
                                 "{\"code\":0,\"message\":\"a{b}c \\\"q\\\" \\\\ {\",\"data\":\"" + Convert.ToBase64String(p2) + "\"}" +
                                 "{\"code\":20000000,\"message\":\"ok\",\"data\":\"\"}\r\n";
                    var bytes = Encoding.UTF8.GetBytes(stream);
                    var objs = Volc.JsonObjects(bytes);
                    var wav = Volc.Decode(bytes);
                    var vf = Path.Combine(tmp, "volc.wav"); File.WriteAllBytes(vf, wav);
                    bool same = wav.Length == 44 + p1.Length + p2.Length && wav.Skip(44).Take(p1.Length).SequenceEqual(p1) && wav.Skip(44 + p1.Length).SequenceEqual(p2);
                    Check("Doubao 2.0: chunked JSON split (braces / quotes inside strings), pcm joined in order, 24 kHz 16-bit mono WAV header",
                          objs.Count == 3 && same && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 24) == 24000 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16
                          && Math.Abs(Tts.Length(vf) - (p1.Length + p2.Length) / 48000.0) < 1e-9, $"{objs.Count} chunks, {wav.Length} bytes");
                    string e45 = Throws(() => Volc.Decode(Encoding.UTF8.GetBytes("{\"code\":0,\"data\":\"" + Convert.ToBase64String(p1) + "\"}{\"code\":45000000,\"message\":\"speaker permission denied {x}\"}")));
                    Check("Doubao 2.0: 45000000 → error with code, message and the 'not enabled' hint", e45.Contains("45000000") && e45.Contains("speaker permission denied {x}") && (e45.Contains("没开通") || e45.Contains("isn't enabled")), e45);
                    string eEmpty = Throws(() => Volc.Decode(Encoding.UTF8.GetBytes("{\"code\":20000000,\"message\":\"ok\"}")));
                    Check("Doubao 2.0: no audio → error", eEmpty != "", eEmpty);
                    // 存的老声音（1.0 的 *_moon_bigtts、BV001_streaming）不在 2.0 列表里：用第一个
                    string oldV = Settings.GetString("ttsVoice.volc", null);
                    Tts.SetVoice("volc", "zh_female_shuangkuaisisi_moon_bigtts"); var v1 = Tts.Voice("volc");
                    Tts.SetVoice("volc", "BV001_streaming"); var v2 = Tts.Voice("volc");
                    Tts.SetVoice("volc", "zh_male_m191_uranus_bigtts"); var v3 = Tts.Voice("volc");
                    Tts.SetVoice("volc", oldV ?? "");
                    Check("Doubao 2.0: voices are *_uranus_bigtts; an old 1.0 voice falls back to the first", Tts.Voices("volc").All(x => x.id.EndsWith("_uranus_bigtts")) && Tts.Voices("volc").Count == 10
                          && v1 == "zh_female_vv_uranus_bigtts" && v2 == "zh_female_vv_uranus_bigtts" && v3 == "zh_male_m191_uranus_bigtts", $"{v1} {v2} {v3}");
                    // 假 key 真打一次接口：401 → 中文提示 AppID / Access Token 不对
                    string oldK = TtsKeys.Get("volc"), oldR = TtsKeys.Get("volc", "region");
                    TtsKeys.Set("volc", "key", "fake-token-" + Guid.NewGuid().ToString("N").Substring(0, 6)); TtsKeys.Set("volc", "region", "1234567890");
                    string e401 = Throws(() => CloudTts.Synth("volc", "你好", "zh_female_vv_uranus_bigtts", 1, Path.Combine(tmp, "v.wav")));
                    TtsKeys.Set("volc", "key", oldK); TtsKeys.Set("volc", "region", oldR);
                    Check("Doubao 2.0: real request with a fake key → 'wrong AppID or Access Token'", e401.Contains("AppID 或 Access Token 不对") || e401.Contains("wrong AppID or Access Token"), e401);
                }
                Check("cloud engine without a key says so", Throws(() => CloudTts.Synth("qwen", "hi", "Cherry", 1, Path.Combine(tmp, "x.wav"))).Contains("key"), "");

                // ---- 系统声音：念一句英文 ----
                var sys = SystemTts.Voices();
                if (sys.Count > 0)
                {
                    var sw = Path.Combine(tmp, "sys.wav");
                    var t0 = DateTime.Now;
                    SystemTts.Synth("Hello, this is a voice test.", "", 1, sw);
                    double len = Tts.Length(sw), vo = Voiced(sw);
                    Check("Windows voice speaks a line", len > 0.8 && len < 6 && vo > 0.2, $"{len:0.00}s voiced {vo:P0} ({(DateTime.Now - t0).TotalSeconds:0.0}s) voices: {string.Join(", ", sys.Select(v => v.id))}");
                }
                else sb.AppendLine("SKIP Windows voice: none installed");

                // ---- 本机 AI 声音：中文 ----
                var t1 = DateTime.Now;
                LocalTts.EnsureModel(null, CancellationToken.None);
                Check("voice model ready", LocalTts.Ready, $"{LocalTts.LastSource} in {(DateTime.Now - t1).TotalSeconds:0}s, helper {LocalTts.Helper}");
                var lw = Path.Combine(tmp, "local.wav");
                t1 = DateTime.Now;
                LocalTts.Synth("你好，欢迎使用录屏。今天是 2026 年 11 月 4 日。", "3", 1, lw, CancellationToken.None);
                double llen = Tts.Length(lw), lvo = Voiced(lw);
                Check("on-device AI voice speaks Chinese (digits and dates too)", llen > 2 && llen < 12 && lvo > 0.3, $"{llen:0.00}s voiced {lvo:P0} in {(DateTime.Now - t1).TotalSeconds:0.0}s");
                var fast = Path.Combine(tmp, "fast.wav");
                LocalTts.Synth("你好，欢迎使用录屏。今天是 2026 年 11 月 4 日。", "58", 1.5, fast, CancellationToken.None);
                Check("faster speed → shorter (male voice 58)", Tts.Length(fast) < llen * 0.85, $"{Tts.Length(fast):0.00}s vs {llen:0.00}s");
                var dash = Path.Combine(tmp, "dash.wav");
                LocalTts.Synth("-5 度", "3", 1, dash, CancellationToken.None);
                Check("text starting with - is not taken as an option", Tts.Length(dash) > 0.3, $"{Tts.Length(dash):0.00}s");

                // ---- HEVC 导出（同 Mac 版 codec = hevc）：编码是 hevc、标 hvc1，同画质比 H.264 小；选项存取 ----
                var o264 = new ExportOptions { Resolution = "720", Fps = 30 };
                var o265 = ExportOptions.Load(new ExportOptions { Resolution = "720", Fps = 30, Hevc = true }.Save());
                Check("HEVC option saved / loaded, 60% bitrate", o265.Hevc && !ExportOptions.Load(o264.Save()).Hevc && Math.Abs(o265.BitsPerPixel - o264.BitsPerPixel * 0.6) < 1e-9 && o265.MinRate == o264.MinRate * 6 / 10 && o265.Summary((a, b) => a).EndsWith("HEVC"), o265.Save());
                string src = Env("TTSTEST"), m264 = Path.Combine(tmp, "h264.mp4"), m265 = Path.Combine(tmp, "hevc.mp4");
                var st264 = Studio.Render(src, m264, new StudioStyle(), null, CursorTracker.LoadCursor, null, null, o264);
                var st265 = Studio.Render(src, m265, new StudioStyle(), null, CursorTracker.LoadCursor, null, null, o265);
                string Codec(string f)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(Studio.FfmpegPath, $"-hide_banner -i \"{f}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
                    using (var pr = System.Diagnostics.Process.Start(psi)) { var e = pr.StandardError.ReadToEnd(); pr.WaitForExit(); var mm = System.Text.RegularExpressions.Regex.Match(e, @"Video: (\w+)[^\r\n]*?\((\w{4}) /"); return mm.Success ? mm.Groups[1].Value + "/" + mm.Groups[2].Value : "?"; }
                }
                string c264 = Codec(m264), c265 = Codec(m265);
                long z264 = new FileInfo(m264).Length, z265 = new FileInfo(m265).Length;
                // 文件大小不比：测试画面很简单，两种编码都远用不满给的码率，大小看编码器自己发挥（码率六成在上面那条查）
                Check("HEVC export: hevc / hvc1, same frames", c264.StartsWith("h264") && c265 == "hevc/hvc1" && st265.Frames == st264.Frames,
                    $"{c264} {z264 / 1024} KB ({st264.Encoder}) vs {c265} {z265 / 1024} KB ({st265.Encoder}), {st265.Frames} frames in {st265.TotalSeconds:0.0}s");

                // ---- 剪辑窗口 ----
                Editor(Env("TTSTEST"), Check);
                try { Directory.Delete(tmp, true); } catch { }
            }
            catch (Exception e) { sb.AppendLine("FAIL exception " + e); ok = false; }
            finally
            {
                Settings.SetString("ttsEngine", oldEngine ?? "local"); if (oldRate != null) Settings.SetString("ttsRate", oldRate);
            }
            File.WriteAllText(Tmp("tts_test.txt"), (ok ? "OK" : "FAIL") + Environment.NewLine + sb);
            return ok ? 0 : 1;
        }

        static string Throws(Action a) { try { a(); return ""; } catch (Exception e) { return e.Message; } }

        static void Editor(string src, Action<string, bool, string> Check)
        {
            var dir = Path.Combine(Path.GetTempPath(), "tts-editor-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            try { File.Delete(StudioProject.PathIn(dir)); } catch { }
            Tts.Engine = Env("TTS_ENGINE") ?? "local";
            StudioEditor.TestMode = true;
            var ed = new StudioEditor(dir) { StartPosition = FormStartPosition.Manual, Location = new Point(10, 10) };
            ed.ClientSize = new Size(1200, 760);
            ed.Show();
            Check("editor: first preview frame", PumpUntil(() => ed.FirstFrameShown, 30), "");
            ed.Edit(p =>
            {
                p.Captions.Add(new Caption { Start = 0.5, End = 2.5, Text = "第一句，念成配音。" });
                p.Captions.Add(new Caption { Start = 4, End = 6, Text = "Second line in English." });
            });
            var c0 = ed.Project.Captions[0]; var c1 = ed.Project.Captions[1];
            string P(Caption c) => "voice/tts-" + c.Id.Substring(0, 8).ToLowerInvariant() + "-";

            int done = ed.SpeakDone;
            ed.SpeakCaptions();
            PumpUntil(() => ed.SpeakDone > done, 180); Pump(0.3);
            var vs = ed.Project.Voiceovers;
            Check($"Speak All ({Tts.Engine}): two clips on the voice lane, starting at their captions",
                vs.Count == 2 && vs.Any(v => v.File.StartsWith(P(c0)) && Math.Abs(v.Start - 0.5) < 1e-6) && vs.Any(v => v.File.StartsWith(P(c1)) && Math.Abs(v.Start - 4) < 1e-6)
                && vs.All(v => File.Exists(Path.Combine(dir, v.File.Replace('/', '\\'))) && v.Length > 0.5),
                ed.LastSpeakError ?? string.Join("; ", vs.Select(v => $"{v.File}@{v.Start:0.0} {v.Length:0.00}s")));
            var first = vs.FirstOrDefault(v => v.File.StartsWith(P(c0)))?.File;

            done = ed.SpeakDone;
            ed.SpeakCaptions(new[] { c0.Id });
            PumpUntil(() => ed.SpeakDone > done, 120); Pump(0.3);
            vs = ed.Project.Voiceovers;
            var again = vs.FirstOrDefault(v => v.File.StartsWith(P(c0)))?.File;
            Check("speak one caption again: replaces its old clip (new file, still two clips)", vs.Count == 2 && again != null && again != first, $"{vs.Count} {first} → {again}");
            ed.Undo(); Pump(0.3);
            Check("undo brings the old clip back", ed.Project.Voiceovers.Any(v => v.File == first) && ed.Project.Voiceovers.Count == 2, "");
            // 导出前检查（同 Mac 版）：占位文字、空字幕要被认出来；选「去修改」就不导出
            ed.Edit(p => { p.Annotations.Add(new Annotation { Start = 0, End = 2, Kind = "text", X = 0.2, Y = 0.2, Text = "hello" }); p.Captions.Add(new Caption { Start = 7, End = 8, Text = " " }); });
            var iss = ed.ExportIssues();
            Check("export check finds the placeholder note + empty caption", iss.Any(x => x.Contains("hello")) && iss.Any(x => x.Contains("1 句字幕是空的") || x.Contains("1 captions are empty")), string.Join(" | ", iss));
            List<string> asked = null;
            StudioEditor.TestMode = false; StudioEditor.AskExportAnyway = l => { asked = l; return false; };
            ed.ExportChecked(); Pump(0.3);
            Check("Go Back: asks first and doesn't export", asked != null && asked.Count == iss.Count && !ed.Exporting, $"{asked?.Count}");
            StudioEditor.AskExportAnyway = null; StudioEditor.TestMode = true;
            ed.Undo(); Pump(0.2);
            ed.ShowSpeakBox(); Pump(0.5);
            Check("Text to Speech box opens", ed.SpeakBox != null && ed.SpeakBox.Visible, "");
            if (Env("TTSSHOT") is string shot)
                using (var bmp = new Bitmap(ed.SpeakBox.Width, ed.SpeakBox.Height)) { ed.SpeakBox.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(shot); }
            ed.SpeakBox?.Close();
            ed.Close(); Pump(0.5);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
