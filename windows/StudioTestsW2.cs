// W2 的自测：讲解（放置规则、锚点、压低、导出时刻和音量）、字幕（断句、剪辑后的时间、.srt、whisper 结果解析、烧进画面）、降噪。
// 纯 C#：Windows 程序里用环境变量 W2TEST 触发，Mac 上用 tools/StudioHarness 跑（导出要 ffmpeg）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    static class StudioTestsW2
    {
        sealed class Report
        {
            public readonly StringBuilder Text = new StringBuilder();
            public bool Ok = true;
            public void Check(string name, bool cond, string info = "") { Text.AppendLine((cond ? "PASS " : "FAIL ") + name + (info.Length > 0 ? "  " + info : "")); Ok &= cond; }
            public void Note(string s) => Text.AppendLine(s);
            public string Done(string what) { Text.AppendLine(what + (Ok ? " OK" : " FAIL")); return Text.ToString(); }
        }
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static bool Near(double a, double b, double e = 1e-6) => Math.Abs(a - b) < e;

        // ---- 讲解规则（同 Mac 版 VOICETEST 的规则部分） ----
        public static string Voice()
        {
            var r = new Report();
            var p = new StudioProject { TrimStart = 0, TrimEnd = 30 };
            var m = new TimeMap(p);
            VoiceClip V(double start, double len) => new VoiceClip { File = "voice/x.m4a", Start = start, TrimIn = 0, TrimOut = len, FileLength = len };
            string L(List<VoiceClip> l) => string.Join(" ", l.Select(c => F(c.OutRange(m).start) + "–" + F(c.OutRange(m).end)));
            // 新段落在旧段中间：旧段剪成两截
            var old = V(2, 10);
            var mid = VoiceClip.Place(V(5, 2), new List<VoiceClip> { old }, m);
            r.Check("new clip in the middle splits the old one", mid.Count == 3 && L(mid) == "2–5 5–7 7–12", L(mid));
            r.Check("split tail keeps the right part of the recording", mid.Any(c => Near(c.TrimIn, 5) && Near(c.Start, 7)));
            var head = VoiceClip.Place(V(1, 3), new List<VoiceClip> { old }, m);
            r.Check("covering the start trims the old one's head", L(head) == "1–4 4–12", L(head));
            var tail = VoiceClip.Place(V(10, 4), new List<VoiceClip> { old }, m);
            r.Check("covering the end trims the old one's tail", L(tail) == "2–10 10–14", L(tail));
            var all = VoiceClip.Place(V(1, 20), new List<VoiceClip> { old }, m);
            r.Check("covering it entirely removes it", all.Count == 1 && L(all) == "1–21", L(all));
            var apart = VoiceClip.Place(V(20, 2), new List<VoiceClip> { old }, m);
            r.Check("no overlap keeps both", apart.Count == 2, L(apart));
            // 锚点：讲解前面剪掉 2 秒 → 讲解整体前移 2 秒；讲解中间的画面被剪掉 → 讲解长度不变
            var pc = p.Clone(); pc.Cuts.Add(new CutSegment { Start = 0.5, End = 2.5 });
            var mc = new TimeMap(pc);
            var c1 = V(4, 3);
            r.Check("cut before the clip moves it 2 s earlier", Near(c1.OutRange(mc).start, 2) && Near(c1.OutRange(mc).end, 5), F(c1.OutRange(mc).start));
            var pc2 = p.Clone(); pc2.Cuts.Add(new CutSegment { Start = 5, End = 6 });
            var mc2 = new TimeMap(pc2);
            r.Check("cut inside the clip's picture doesn't shorten the clip", Near(c1.OutRange(mc2).end - c1.OutRange(mc2).start, 3));
            r.Check("timeline end of the clip skips the cut picture", Near(c1.SrcEnd(mc2), 8), F(c1.SrcEnd(mc2)));
            // 压低曲线
            var spans = new List<(double, double)> { (5, 8) };
            r.Check("duck: inside the voice -12 dB", Near(Duck.Gain(6, spans), 0.25));
            r.Check("duck: well before / after = 1", Near(Duck.Gain(4.5, spans), 1) && Near(Duck.Gain(8.5, spans), 1));
            r.Check("duck: ramps in 0.15 s before, out 0.35 s after", Near(Duck.Gain(5 - 0.075, spans), 1 - 0.75 * 0.5, 1e-3) && Near(Duck.Gain(8 + 0.175, spans), 1 - 0.75 * 0.5, 1e-3),
                F(Duck.Gain(4.925, spans)) + " " + F(Duck.Gain(8.175, spans)));
            r.Check("duck off = 1", Near(Duck.Gain(6, spans, false), 1));
            // project.json：存了再读一样；Mac 写的字段都认得
            var pj = p.Clone(); pj.Voiceovers.Add(V(3, 2)); pj.Audio.Voice = 0.7; pj.Audio.Duck = false; pj.Audio.Denoise = "medium"; pj.Audio.DenoiseSystem = true;
            pj.Captions.Add(new Caption { Start = 1, End = 2, Text = "你好" }); pj.CaptionStyle.Box = "dark"; pj.CaptionStyle.Position = "top";
            var back = StudioProject.FromJson(pj.ToJson());
            r.Check("round trip keeps voiceovers / audio / captions / style", back.ToJson() == pj.ToJson());
            var mac = StudioProject.FromJson("{\"segments\":[],\"trimStart\":0,\"trimEnd\":10,\"audio\":{\"system\":1,\"mic\":1,\"clicks\":1,\"muted\":false,\"voice\":0.5,\"duck\":false,\"denoise\":\"strong\",\"denoiseSystem\":false},"
                + "\"voiceovers\":[{\"id\":\"AB\",\"file\":\"voice/a.m4a\",\"start\":2,\"trimIn\":0.1,\"trimOut\":3,\"fileLength\":3.2,\"volume\":0.8}],"
                + "\"captions\":[{\"id\":\"C1\",\"start\":1,\"end\":2,\"text\":\"字幕\"}],\"captionStyle\":{\"show\":true,\"weight\":\"medium\",\"size\":0.06,\"color\":{\"r\":1,\"g\":0.9,\"b\":0.2},\"outline\":false,\"shadow\":true,\"box\":\"light\",\"position\":\"bottom\",\"margin\":0.1}}");
            r.Check("reads Mac voice fields", mac.Voiceovers.Count == 1 && Near(mac.Voiceovers[0].TrimIn, 0.1) && Near(mac.Voiceovers[0].Volume, 0.8) && Near(mac.Audio.Voice, 0.5) && !mac.Audio.Duck && mac.Audio.Denoise == "strong");
            r.Check("reads Mac captions + style", mac.Captions.Count == 1 && mac.Captions[0].Text == "字幕" && mac.CaptionStyle.Weight == "medium" && mac.CaptionStyle.Box == "light" && !mac.CaptionStyle.Outline && Near(mac.CaptionStyle.Color.G, 0.9));
            var old2 = StudioProject.FromJson("{\"segments\":[],\"trimStart\":0,\"trimEnd\":10,\"audio\":{\"system\":1,\"mic\":1,\"clicks\":1,\"muted\":false}}");
            r.Check("old project: voice on, duck on, denoise off, no captions", Near(old2.Audio.Voice, 1) && old2.Audio.Duck && old2.Audio.Denoise == "off" && old2.Captions.Count == 0 && old2.CaptionStyle.Show);
            return r.Done("VOICETEST");
        }

        // ---- 字幕规则（同 Mac 版 CAPTIONTEST 的规则部分）+ whisper 结果解析 ----
        public static string Captions()
        {
            var r = new Report();
            const string sent = "大家好，这是我自己做的录屏软件，录完以后可以在剪辑窗口里调整推近的位置，还可以加字幕自动把说的话变成文字。你觉得怎么样？";
            var toks = new List<CaptionSplit.Token>(); double t = 1;
            foreach (var ch in sent) { toks.Add(new CaptionSplit.Token(ch.ToString(), t, t + 0.18)); t += 0.2; }
            var caps = CaptionSplit.Make(toks, 16);
            r.Check("split by punctuation, long ones broken, each line ≤ 16", caps.Count >= 5 && caps.All(c => CaptionSplit.Width(c.Text) <= 16), string.Join(" | ", caps.Select(c => c.Text)));
            r.Check("commas / full stops removed, question mark kept", !caps.Any(c => c.Text.Contains("，") || c.Text.Contains("。")) && caps.Last().Text.EndsWith("？"));
            r.Check("no characters lost", string.Concat(caps.Select(c => c.Text)) == new string(sent.Where(c => "，。".IndexOf(c) < 0).ToArray()));
            r.Check("first starts at the first char, no overlaps", Near(caps[0].Start, 1) && caps.Zip(caps.Skip(1), (a, b) => a.End <= b.Start + 1e-9).All(x => x));
            var gap = new List<CaptionSplit.Token> { new CaptionSplit.Token("你", 0, 0.2), new CaptionSplit.Token("好", 0.2, 0.4), new CaptionSplit.Token("再", 1.5, 1.7), new CaptionSplit.Token("见", 1.7, 1.9) };
            r.Check("pause over 0.6 s splits", string.Join(",", CaptionSplit.Make(gap, 16).Select(c => c.Text)) == "你好,再见");
            gap[2] = new CaptionSplit.Token("再", 0.5, 0.7); gap[3] = new CaptionSplit.Token("见", 0.7, 0.9);
            r.Check("too short gets 0.7 s", CaptionSplit.Make(gap, 16)[0].End - CaptionSplit.Make(gap, 16)[0].Start >= 0.7 - 1e-9);
            var st = new CaptionStyle();
            r.Check("chars per line: landscape 18, portrait fewer", Near(st.MaxChars(1660, 1080), 18) && st.MaxChars(1080, 1920) < 16, F(st.MaxChars(1660, 1080)) + " " + F(st.MaxChars(1080, 1920)));
            var pc = new StudioProject { TrimStart = 0, TrimEnd = 30 };
            pc.Cuts.Add(new CutSegment { Start = 4, End = 6 });
            pc.Captions = new List<Caption> { new Caption { Start = 10, End = 12, Text = "后面" }, new Caption { Start = 4.2, End = 5.8, Text = "被剪掉" }, new Caption { Start = 1, End = 2, Text = "前面" } };
            var mc = new TimeMap(pc);
            var tr = new CaptionTrack(pc.Captions, true, mc, 0, 30);
            r.Check("captions after a cut move earlier", tr.AtOut(8.5) == "后面" && tr.AtOut(10.5) == null && tr.AtOut(1.5) == "前面");
            r.Check("caption entirely inside the cut is dropped", !tr.Items.Any(x => x.text == "被剪掉"));
            r.Check("preview looks up by source time", tr.AtSource(11) == "后面" && tr.AtSource(5) == null);
            var srt = Srt.Text(pc);
            r.Check(".srt: two lines, output time", srt.Contains("1\n00:00:01,000 --> 00:00:02,000\n前面") && srt.Contains("2\n00:00:08,000 --> 00:00:10,000\n后面") && !srt.Contains("被剪掉"), srt.Replace("\n", "⏎"));
            var ass = CaptionAss.Build(tr, st, 0, 1660, 1080, "Microsoft YaHei");
            r.Check("ASS: one dialogue per caption, no wrapping", ass.Split('\n').Count(l => l.StartsWith("Dialogue:")) == 2 && ass.Contains("WrapStyle: 2") && ass.Contains("PlayResX: 1660"));
            var longTr = new CaptionTrack(new[] { new Caption { Start = 0, End = 2, Text = string.Concat(Enumerable.Repeat("很长的一句字幕", 8)) } }, true, new TimeMap(new StudioProject { TrimEnd = 10 }), 0, 10);
            r.Check("ASS: too long text gets a smaller font", CaptionAss.Build(longTr, st, 0, 1660, 1080, "x").Contains("{\\fs"));
            // whisper 结果：段的起止收紧到真正有声音的地方，段里的字按有声的块分（中间的停顿留出来）
            const int sr = 16000;
            var pcm = new float[sr * 6];
            void Tone(double a, double b) { for (int i = (int)(a * sr); i < (int)(b * sr); i++) pcm[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / sr)); }
            Tone(1.2, 2.4); Tone(3.0, 4.2);   // 两句话，中间停 0.6 秒
            var json = "{\"transcription\":[{\"offsets\":{\"from\":0,\"to\":4400},\"text\":\" 大家好这是录屏，录完加字幕\"}]}";
            var segs = WhisperParse.Segments(json);
            var voiced = WhisperParse.Voiced(pcm, sr);
            var wt = WhisperParse.Tokens(segs, voiced);
            var wc = CaptionSplit.Make(wt, 16);
            r.Check("whisper: segment parsed", segs.Count == 1 && Near(segs[0].start, 0) && Near(segs[0].end, 4.4));
            r.Check("whisper: first char starts where the sound starts (not at 0)", wt.Count > 0 && Math.Abs(wt[0].Start - 1.2) < 0.06, F(wt.FirstOrDefault().Start));
            r.Check("whisper: chars split at the pause", wc.Count == 2 && wc[0].Text == "大家好这是录屏" && wc[1].Text == "录完加字幕" && Math.Abs(wc[1].Start - 3.0) < 0.06,
                string.Join(" | ", wc.Select(c => F(c.Start) + " " + c.Text)));
            r.Check("whisper: traditional → simplified", WhisperParse.Simplify("這是錄屏軟體") == "这是录屏软体");
            return r.Done("CAPTIONRULES");
        }

        // ---- 真导出：自己造一份素材（300Hz 原声 + 假轨迹），加讲解（4 声 2kHz）导出，按成片时间找讲解、量压低和音量；
        // 降噪（白噪 + 语音）；字幕烧进画面（开 / 关比画面）。speechWav = 一段中文语音（降噪测试用，可选） ----
        public static string Export(string work, string speechWav = null)
        {
            var r = new Report();
            Directory.CreateDirectory(work);
            string dir = Path.Combine(work, "src");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.Combine(dir, "voice"));
            // 素材：10 秒灰画面 + 300Hz 立体声 0.4；讲解录音：5 秒，1/2/3/4 秒处各一声 0.15 秒的 2kHz（0.5）
            Studio.RunQuiet($"-v error -y -f lavfi -i color=c=gray:s=640x360:r=30:d=10 -f lavfi -i \"aevalsrc=0.4*sin(2*PI*300*t)|0.4*sin(2*PI*300*t):s=48000:d=10\" -map 0:v -map 1:a -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 320k \"{Path.Combine(dir, "raw.mp4")}\"", 120000);
            Studio.RunQuiet($"-v error -y -f lavfi -i \"aevalsrc='0.5*sin(2*PI*2000*t)*(lt(mod(t,1),0.15))*gte(t,0.9)':s=48000:d=5\" -c:a aac -b:a 192k \"{Path.Combine(dir, "voice", "beeps.m4a")}\"", 60000);
            File.WriteAllText(Path.Combine(dir, "track.json"), "{\"width\":640,\"height\":360,\"pixelsPerPoint\":1,\"moves\":[[0,320,180,0,0]],\"clicks\":[],\"keys\":[],\"cursors\":[{\"file\":\"c.png\",\"hotX\":0,\"hotY\":0,\"w\":16,\"h\":16,\"arrow\":true}]}");
            r.Check("test footage made", File.Exists(Path.Combine(dir, "raw.mp4")) && File.Exists(Path.Combine(dir, "voice", "beeps.m4a")));
            var opt = new ExportOptions { Resolution = "720", Fps = 30, Quality = "standard" };
            StudioProject Base()
            {
                var p = new StudioProject { TrimStart = 0, TrimEnd = 10, SegmentsEdited = true };
                p.Look.Blur = "off"; p.Look.ClickSound = false;
                p.Voiceovers.Add(new VoiceClip { File = "voice/beeps.m4a", Start = 2, TrimIn = 0, TrimOut = 5, FileLength = 5, Volume = 1 });
                return p;
            }
            float[] Render(StudioProject p, string name)
            {
                var outF = Path.Combine(work, name + ".mp4");
                try { Studio.Render(dir, outF, new StudioStyle(), _ => { }, null, null, p, opt); }
                catch (Exception e) { r.Check("export " + name, false, e.Message); return null; }
                return Studio.DecodePcm(outF, 48000);
            }
            // 每 10 毫秒的某个频率的幅度（Goertzel）
            double[] Band(float[] pcm, double f)
            {
                int hop = 480, n = pcm.Length / hop; var o = new double[n];
                double w = 2 * Math.PI * f / 48000, c = 2 * Math.Cos(w);
                for (int i = 0; i < n; i++)
                {
                    double s1 = 0, s2 = 0;
                    for (int k = i * hop; k < (i + 1) * hop; k++) { double s0 = pcm[k] + c * s1 - s2; s2 = s1; s1 = s0; }
                    o[i] = Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) * 2 / hop;
                }
                return o;
            }
            List<double> Onsets(double[] b, double thr)
            {
                var l = new List<double>();
                for (int i = 1; i < b.Length; i++) if (b[i] > thr && b[i - 1] <= thr && (l.Count == 0 || i / 100.0 - l[l.Count - 1] > 0.5)) l.Add(i / 100.0);
                return l;
            }
            double Avg(double[] b, double a, double e) { int i0 = (int)(a * 100), i1 = Math.Min(b.Length, (int)(e * 100)); return i1 > i0 ? b.Skip(i0).Take(i1 - i0).Average() : 0; }

            // 1) 讲解出现的时刻：讲解从素材 2 秒开始，录音里 1/2/3/4 秒的声音 → 成片 3/4/5/6 秒
            var a1 = Render(Base(), "voice");
            if (a1 != null)
            {
                var hi = Band(a1, 2000); var on = Onsets(hi, 0.1);
                r.Check("voice beeps land at 3 / 4 / 5 / 6 s", on.Count == 4 && on.Zip(new[] { 3.0, 4, 5, 6 }, (x, y) => Math.Abs(x - y) < 0.03).All(x => x), string.Join(",", on.Select(F)));
                var lo = Band(a1, 300);
                double outside = Avg(lo, 0.5, 1.5), inside = Avg(lo, 3.3, 3.8);
                r.Check("original ducked to about -12 dB under the voice", outside > 0.3 && Math.Abs(inside / outside - 0.25) < 0.04, $"{F(inside)} / {F(outside)} = {F(inside / outside)}");
                double after = Avg(lo, 7.6, 8.5);
                r.Check("original back to full after the voice", Math.Abs(after / outside - 1) < 0.05, F(after / outside));
                // 2) 音量 50%
                var pv = Base(); pv.Voiceovers[0].Volume = 0.5;
                var a2 = Render(pv, "voice-half");
                if (a2 != null)
                {
                    double full = Avg(Band(a1, 2000), 3.02, 3.12), half = Avg(Band(a2, 2000), 3.02, 3.12);
                    r.Check("clip volume 50% halves the voice", Math.Abs(half / full - 0.5) < 0.06, F(half / full));
                }
                // 3) 压低关掉
                var pd = Base(); pd.Audio.Duck = false;
                var a3 = Render(pd, "voice-noduck");
                if (a3 != null) { var b3 = Band(a3, 300); r.Check("duck off keeps the original level", Math.Abs(Avg(b3, 3.3, 3.8) / Avg(b3, 0.5, 1.5) - 1) < 0.05); }
                // 4) 讲解前面剪掉 1 秒 → 讲解跟着前移 1 秒
                var pc = Base(); pc.Cuts.Add(new CutSegment { Start = 0.5, End = 1.5 });
                var a4 = Render(pc, "voice-cut");
                if (a4 != null) { var on4 = Onsets(Band(a4, 2000), 0.1); r.Check("cut before the voice moves it 1 s earlier", on4.Count == 4 && Math.Abs(on4[0] - 2) < 0.03, string.Join(",", on4.Select(F))); }
                // 5) 讲解总音量 0 → 没有讲解、也不压低
                var p0 = Base(); p0.Audio.Voice = 0;
                var a5 = Render(p0, "voice-off");
                if (a5 != null) r.Check("voice master 0 = no voice, no ducking", Onsets(Band(a5, 2000), 0.1).Count == 0 && Math.Abs(Avg(Band(a5, 300), 3.3, 3.8) / Avg(Band(a5, 300), 0.5, 1.5) - 1) < 0.05);
            }

            // 6) 降噪：白噪（0.05）+ 语音，前 2 秒只有噪声；强 vs 关
            if (speechWav != null && File.Exists(speechWav))
            {
                string nd = Path.Combine(work, "noisy"); if (Directory.Exists(nd)) Directory.Delete(nd, true); Directory.CreateDirectory(nd);
                File.Copy(Path.Combine(dir, "track.json"), Path.Combine(nd, "track.json"));
                Studio.RunQuiet($"-v error -y -f lavfi -i color=c=gray:s=640x360:r=30:d=12 -f lavfi -i \"anoisesrc=c=white:a=0.05:r=48000:d=12\" -i \"{speechWav}\" " +
                                $"-filter_complex \"[2:a]aresample=48000,adelay=2000|2000,apad[sp];[1:a][sp]amix=inputs=2:duration=first:normalize=0,aformat=channel_layouts=stereo[a]\" -map 0:v -map \"[a]\" -t 12 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 320k \"{Path.Combine(nd, "raw.mp4")}\"", 120000);
                float[] RenderN(string level)
                {
                    var p = new StudioProject { TrimStart = 0, TrimEnd = 12, SegmentsEdited = true }; p.Look.Blur = "off"; p.Look.ClickSound = false; p.Audio.Denoise = level;
                    var outF = Path.Combine(work, "denoise-" + level + ".mp4");
                    try { Studio.Render(nd, outF, new StudioStyle(), _ => { }, null, null, p, opt); } catch (Exception e) { r.Check("denoise export " + level, false, e.Message); return null; }
                    return Studio.DecodePcm(outF, 48000);
                }
                double Rms(float[] x, double a, double b) { int i0 = (int)(a * 48000), i1 = Math.Min(x.Length, (int)(b * 48000)); double s = 0; for (int i = i0; i < i1; i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, i1 - i0)); }
                var off = RenderN("off"); var strong = RenderN("strong"); var light = RenderN("light");
                if (off != null && strong != null && light != null)
                {
                    double db(double a, double b) => 20 * Math.Log10(Math.Max(1e-9, a) / Math.Max(1e-9, b));
                    double nOff = Rms(off, 1.0, 2.0), nStrong = Rms(strong, 1.0, 2.0), nLight = Rms(light, 1.0, 2.0);
                    double sOff = Rms(off, 3.5, 6.5), sStrong = Rms(strong, 3.5, 6.5);
                    r.Check("denoise strong: noise-only part ≥ 10 dB quieter", db(nOff, nStrong) >= 10, $"{F(db(nOff, nStrong))} dB");
                    r.Check("denoise light: less than strong but still quieter", db(nOff, nLight) > 6 && db(nOff, nLight) <= db(nOff, nStrong) + 0.5, $"{F(db(nOff, nLight))} dB");
                    r.Check("denoise keeps the speech (within 6 dB)", Math.Abs(db(sOff, sStrong)) < 6, $"{F(db(sOff, sStrong))} dB");
                    // 对齐：降噪后的声音和原来对得上（互相关的峰在 0 附近）
                    int best = 0; double bestC = double.MinValue;
                    for (int lag = -2400; lag <= 2400; lag += 24)
                    {
                        double c = 0; for (int i = 4 * 48000; i < 5 * 48000; i += 4) { int j = i + lag; if (j >= 0 && j < strong.Length) c += off[i] * strong[j]; }
                        if (c > bestC) { bestC = c; best = lag; }
                    }
                    r.Check("denoised audio stays in sync (< 10 ms)", Math.Abs(best) <= 480, $"lag {best} samples");
                }
            }
            else r.Note("SKIP denoise (no speech wav)");

            // 7) 字幕烧进画面：开 / 关各导一份，2.0 秒那一帧比：只有底部那一行不一样
            var pcap = Base(); pcap.Voiceovers.Clear(); pcap.Look.Background = "color"; pcap.Look.Color = new RGB(0.2, 0.3, 0.5); pcap.Look.Aspect = "16:9";
            pcap.Captions.Add(new Caption { Start = 1.5, End = 3.0, Text = "这是一行字幕 Test" });
            var poff = pcap.Clone(); poff.CaptionStyle.Show = false;
            string on1 = Path.Combine(work, "cap-on.mp4"), off1 = Path.Combine(work, "cap-off.mp4");
            try
            {
                Studio.Render(dir, on1, new StudioStyle(), _ => { }, null, null, pcap, opt);
                Studio.Render(dir, off1, new StudioStyle(), _ => { }, null, null, poff, opt);
                var fa = Gray(on1, 2.0); var fb = Gray(off1, 2.0); var fc = Gray(on1, 4.0); var fd = Gray(off1, 4.0);
                int w = 320, h = fa.Length / w;
                var rows = Enumerable.Range(0, h).Where(y => Enumerable.Range(0, w).Count(x => Math.Abs(fa[y * w + x] - fb[y * w + x]) > 40) > w / 100).ToList();
                r.Check("caption burned near the bottom", rows.Count > 0 && rows.First() > h * 0.75, rows.Count > 0 ? $"rows {rows.First()}–{rows.Last()} / {h}" : "no difference");
                r.Check("caption is one line", rows.Count > 0 && rows.Last() - rows.First() < h * 0.12, rows.Count > 0 ? $"{rows.Last() - rows.First()} px" : "");
                int diff4 = Enumerable.Range(0, fc.Length).Count(i => Math.Abs(fc[i] - fd[i]) > 40);
                r.Check("no caption → same picture", diff4 < fc.Length / 500, diff4.ToString());
            }
            catch (Exception e) { r.Check("caption export", false, e.Message); }
            return r.Done("W2EXPORT");
        }

        // 一帧缩到 320 宽的灰度
        static byte[] Gray(string file, double t)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "w2-" + Guid.NewGuid().ToString("N") + ".gray");
            Studio.RunQuiet(string.Format(CultureInfo.InvariantCulture, "-v error -y -ss {0:0.###} -i \"{1}\" -frames:v 1 -vf scale=320:-2,format=gray -f rawvideo \"{2}\"", t, file, tmp), 60000);
            var b = File.Exists(tmp) ? File.ReadAllBytes(tmp) : new byte[320];
            try { File.Delete(tmp); } catch { }
            return b;
        }

        // ---- 整条流程：素材的原声里 2 秒处有一段语音（另有一段剪掉在语音之前），讲解里也放同一段；
        // 「录到的声音」「讲解」各生成一次字幕，检查文字和存下来的素材时间 ----
        public static string Maker(string work, string speechWav, Func<string, Action<double>, string> transcribe)
        {
            var r = new Report();
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(Path.Combine(work, "voice"));
            Studio.RunQuiet($"-v error -y -f lavfi -i color=c=gray:s=640x360:r=30:d=16 -i \"{speechWav}\" -filter_complex \"[1:a]aresample=48000,adelay=2000|2000,apad,aformat=channel_layouts=stereo[a]\" -map 0:v -map \"[a]\" -t 16 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 192k \"{Path.Combine(work, "raw.mp4")}\"", 120000);
            Studio.RunQuiet($"-v error -y -i \"{speechWav}\" -c:a aac -b:a 128k \"{Path.Combine(work, "voice", "s.m4a")}\"", 60000);
            File.WriteAllText(Path.Combine(work, "track.json"), "{\"width\":640,\"height\":360,\"pixelsPerPoint\":1,\"moves\":[[0,320,180,0,0]],\"clicks\":[],\"keys\":[],\"cursors\":[]}");
            var pcm = Studio.DecodePcm(speechWav, 16000);
            double on0 = Array.FindIndex(pcm, v => Math.Abs(v) > 0.02) / 16000.0;
            var info = Studio.Probe(Path.Combine(work, "raw.mp4"));
            var p = new StudioProject { TrimStart = 0, TrimEnd = 16, SegmentsEdited = true };
            p.Cuts.Add(new CutSegment { Start = 0.3, End = 1.3 });   // 语音之前剪掉 1 秒：字幕存素材时间，不受影响
            var caps = CaptionMaker.Make(work, info, p, "recorded", 1660, 1080, transcribe, null, System.Threading.CancellationToken.None);
            string all = string.Concat(caps.Select(c => c.Text));
            foreach (var c in caps) r.Note($"  rec {F(c.Start)}–{F(c.End)} {c.Text}");
            r.Check("recorded sound → captions with the words", all.Contains("录屏") && all.Contains("字幕"), all);
            r.Check("recorded: first caption at the speech in source time (< 0.4 s)", caps.Count > 0 && Math.Abs(caps[0].Start - (2 + on0)) < 0.4, caps.Count > 0 ? $"{F(caps[0].Start)} vs {F(2 + on0)}" : "");
            var p2 = p.Clone(); p2.Voiceovers.Add(new VoiceClip { File = "voice/s.m4a", Start = 5, TrimIn = 0, TrimOut = pcm.Length / 16000.0, FileLength = pcm.Length / 16000.0 });
            var vc = CaptionMaker.Make(work, info, p2, "voice", 1660, 1080, transcribe, null, System.Threading.CancellationToken.None);
            foreach (var c in vc) r.Note($"  voice {F(c.Start)}–{F(c.End)} {c.Text}");
            var m = new TimeMap(p2);
            r.Check("voice-over → captions", string.Concat(vc.Select(c => c.Text)).Contains("字幕"));
            r.Check("voice: first caption at voice start + onset in output time (< 0.4 s)", vc.Count > 0 && Math.Abs(m.Out(vc[0].Start) - (m.Out(5) + on0)) < 0.4, vc.Count > 0 ? $"{F(m.Out(vc[0].Start))} vs {F(m.Out(5) + on0)}" : "");
            return r.Done("MAKERTEST");
        }

        // ---- 真识别：transcribe = 给一段 16k 单声道 wav，返回 whisper 的 json（Windows 上是 whisper-cli）；
        // 语音当讲解放在素材 2 秒处，识别出文字、时间对齐、拆行 ----
        public static string Whisper(string speechWav, Func<string, string> transcribe)
        {
            var r = new Report();
            var pcm = Studio.DecodePcm(speechWav, 16000);
            double on0 = Array.FindIndex(pcm, v => Math.Abs(v) > 0.02) / 16000.0, on1 = Array.FindLastIndex(pcm, v => Math.Abs(v) > 0.02) / 16000.0;
            r.Note($"speech {F(pcm.Length / 16000.0)} s, sound {F(on0)}–{F(on1)}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string json = transcribe(speechWav);
            r.Note($"transcribe {F(sw.Elapsed.TotalSeconds)} s");
            var segs = WhisperParse.Segments(json ?? "");
            r.Note("segments: " + string.Join(" | ", segs.Select(s => F(s.start) + "–" + F(s.end) + " " + s.text)));
            var toks = WhisperParse.Tokens(segs.Select(s => (s.start, s.end, WhisperParse.Simplify(s.text))).ToList(), WhisperParse.Voiced(pcm, 16000));
            // 讲解从素材 2 秒开始
            var caps = CaptionSplit.Make(toks.Select(t => new CaptionSplit.Token(t.Text, t.Start + 2, t.End + 2)), new CaptionStyle().MaxChars(1660, 1080));
            foreach (var c in caps) r.Note($"  {F(c.Start)}–{F(c.End)} {c.Text}");
            var all = string.Concat(caps.Select(c => c.Text));
            r.Check("recognized Chinese text", all.Contains("录屏") && all.Contains("字幕"), all);
            r.Check("one line each, ≤ 18 chars", caps.Count > 0 && caps.All(c => CaptionSplit.Width(c.Text) <= 18));
            r.Check("first caption starts at voice start + speech onset (< 0.4 s)", caps.Count > 0 && Math.Abs(caps[0].Start - (2 + on0)) < 0.4, caps.Count > 0 ? $"{F(caps[0].Start)} vs {F(2 + on0)}" : "");
            r.Check("last caption ends near the end of speech", caps.Count > 0 && caps.Last().End > 2 + on1 - 0.4 && caps.Last().End < 2 + on1 + 0.9, caps.Count > 0 ? $"{F(caps.Last().End)} vs {F(2 + on1)}" : "");
            r.Check("split at the 0.5 s pause (「软件」 and 「录完」 apart)", caps.Any(c => c.Text.EndsWith("软件") || c.Text.EndsWith("软体")) && caps.Any(c => c.Text.StartsWith("录完")), string.Join(" | ", caps.Select(c => c.Text)));
            return r.Done("WHISPERTEST");
        }
    }
}