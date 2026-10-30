// 讲解、字幕、降噪：数据（project.json 里的 voiceovers / captions / captionStyle / audio.voice·duck·denoise，格式同 Mac 版）、
// 规则（新录的讲解覆盖重叠部分、讲解时压低原声音、一句句断字幕、.srt）和导出要用的小文件（讲解声音、压低曲线、ASS 字幕）。
// 照 Mac 版 Studio.swift（VoiceClip、VoiceMix）、Captions.swift（CaptionSplit、CaptionLayer、SRT）、Denoise.swift 移植。
// 只用纯 C#（不碰 WinForms），所以也能在 Mac 上用 .NET 8 跑（tools/StudioHarness）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    // ---- 剪辑时另外录的一段讲解（素材夹 voice/ 下的 m4a），同 Mac 版 VoiceClip ----
    // 锚点 Start 是素材时间：讲解「从素材这一刻开始」，之后按成片时间（真实时间）一直往后放 Length 秒——
    // 录的时候本来就是看着剪过的预览边看边说的。之后在讲解前面加 / 改剪掉、快放，讲解跟着它开头那一刻的画面一起挪；
    // 讲解中间那段画面再被剪掉 / 快放，讲解本身不剪不变速，照样连着放完
    sealed class VoiceClip
    {
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public string File = "";               // 相对素材夹：voice/xxx.m4a
        public double Start;                   // 素材时间
        public double TrimIn, TrimOut;         // 用录音的第几秒到第几秒
        public double FileLength;              // 录音文件总长（拖两头时别超出）
        public double Volume = 1;              // 这一段的音量 0–2
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        public double Length => Math.Max(0, TrimOut - TrimIn);

        public VoiceClip Clone() { var c = (VoiceClip)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        // 成片里从哪到哪（成片时间）
        public (double start, double end) OutRange(TimeMap m) { double s = m.Out(Start); return (s, s + Length); }
        // 时间轴上画到哪（素材时间）：讲解结尾那一刻对应素材的哪儿
        public double SrcEnd(TimeMap m) => m.Src(m.Out(Start) + Length);

        // 新录的一段放进来：和旧的重叠的部分以新的为准（旧段被剪短、剪成两段或整段去掉），按成片时间算
        public static List<VoiceClip> Place(VoiceClip n, List<VoiceClip> list, TimeMap m)
        {
            var (ns, ne) = n.OutRange(m);
            var outL = new List<VoiceClip>();
            foreach (var o in list)
            {
                var (os, oe) = o.OutRange(m);
                if (oe <= ns + 1e-6 || os >= ne - 1e-6) { outL.Add(o); continue; }   // 不重叠
                if (os < ns)                                                           // 前面露出来的一截
                {
                    var a = o.Clone(); a.TrimOut = o.TrimIn + (ns - os);
                    if (a.Length >= 0.05) outL.Add(a);
                }
                if (oe > ne)                                                           // 后面露出来的一截
                {
                    var b = o.Clone(); if (os < ns) b.Id = Guid.NewGuid().ToString().ToUpperInvariant();   // 被剪成两段时后一截换个新 id
                    b.TrimIn = o.TrimIn + (ne - os); b.Start = m.Src(ne);
                    if (b.Length >= 0.05) outL.Add(b);
                }
            }
            outL.Add(n);
            return outL.OrderBy(c => c.OutRange(m).start).ToList();
        }

        public void Write(JsonW w)
        {
            w.BeginItem();
            w.Str("file", File); w.Num("fileLength", FileLength); w.Str("id", Id); w.Num("start", Start);
            w.Num("trimIn", TrimIn); w.Num("trimOut", TrimOut); w.Num("volume", Volume);
            w.Extra(Extra);
            w.End();
        }
        public static VoiceClip From(Dictionary<string, object> x)
        {
            var r = new JsonR(x);
            return new VoiceClip
            {
                Id = r.Str("id", Guid.NewGuid().ToString().ToUpperInvariant()), File = r.Str("file", ""), Start = r.Num("start", 0),
                TrimIn = r.Num("trimIn", 0), TrimOut = r.Num("trimOut", 0), FileLength = r.Num("fileLength", 0), Volume = r.Num("volume", 1),
                Extra = JsonR.Unknown(x, new[] { "id", "file", "start", "trimIn", "trimOut", "fileLength", "volume" }),
            };
        }
    }

    // ---- 讲解时把原声音压低（同 Mac 版 VoiceMix）：讲解开始前 0.15 秒慢慢压下去，结束后 0.35 秒慢慢回来，压到 -12 dB ----
    static class Duck
    {
        public const double Attack = 0.15, Release = 0.35, Level = 0.25;

        // 成片时间 t（秒）原声音该乘多少；spans = 讲解占的范围（成片时间）
        public static double Gain(double t, IList<(double a, double b)> spans, bool on = true)
        {
            if (!on || spans == null) return 1;
            double w = 0;
            foreach (var (a, b) in spans)
            {
                if (t >= a && t < b) return Level;
                if (t < a) { double d = a - t; if (d < Attack) w = Math.Max(w, 1 - d / Attack); }
                else { double d = t - b; if (d < Release) w = Math.Max(w, 1 - d / Release); }
            }
            return 1 - (1 - Level) * w;
        }

        // 整条压低曲线（单声道 16 位 WAV，采样率低一点就够：导出时 ffmpeg 再升到 48k），t0 = 成片开头（修剪起点的成片时间）
        public static byte[] Envelope(IList<(double a, double b)> spans, double t0, double len, int sr = 8000)
        {
            int n = Math.Max(1, (int)Math.Ceiling(len * sr));
            var buf = new float[n];
            for (int i = 0; i < n; i++) buf[i] = (float)Gain(t0 + i / (double)sr, spans);
            return ClickSound.Wav(buf, sr);
        }
    }

    // ---- 降噪（同 Mac 版的强度：处理后的声音占几成）。Mac 用系统「声音隔离」，Windows 用 ffmpeg 自带的 afftdn（本机、不用模型文件） ----
    static class Denoise
    {
        public static readonly string[] Levels = { "off", "light", "medium", "strong" };
        public static double Wet(string l) => l == "light" ? 0.85 : l == "medium" ? 0.97 : l == "strong" ? 1 : 0;

        // 一条声音的降噪滤镜：原声 / 处理后按比例混（同 Mac）。in / out 是 ffmpeg 滤镜图里的标签名，tag 用来起不重名的中间标签；
        // nf = 噪声底（dB，按这段声音自己估：NoiseFloor）。afftdn 处理后晚 1200 个采样（48k 下 25 毫秒）：切掉补齐，和原声对齐
        public static string Filter(string level, string inLabel, string outLabel, string tag, double nf, string pre = "")
        {
            double w = Wet(level);
            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            string dn = $"afftdn=nr=36:nf={N(nf)},atrim=start_sample=1200,asetpts=PTS-STARTPTS,apad=pad_len=1200";
            string head = $"[{inLabel}]{pre}{(pre.Length > 0 ? "," : "")}aresample=48000";
            if (w >= 0.999) return $"{head},{dn}[{outLabel}];";
            return $"{head},asplit=2[{tag}a][{tag}b];[{tag}b]{dn}[{tag}c];[{tag}a][{tag}c]amix=inputs=2:weights='{N(1 - w)} {N(w)}':normalize=0:duration=first[{outLabel}];";
        }

        // 噪声底：每 50 毫秒的音量里最小的那一成（安静处的底噪）+ 12 dB，夹在 -50…-20 dB（afftdn 的 nf 要贴着真实底噪才降得动）
        public static double NoiseFloor(float[] pcm, int sr)
        {
            if (pcm == null || pcm.Length < sr / 10) return -35;
            int hop = sr / 20, n = pcm.Length / hop;
            var db = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double s = 0; for (int k = i * hop; k < (i + 1) * hop; k++) s += pcm[k] * (double)pcm[k];
                double r = Math.Sqrt(s / hop);
                if (r > 1e-6) db.Add(20 * Math.Log10(r));   // 全零（数字静音）不算
            }
            if (db.Count == 0) return -50;
            db.Sort();
            return Math.Min(-20, Math.Max(-50, db[db.Count / 10] + 12));
        }
    }

    // ---- 一句字幕（素材时间），同 Mac 版 Caption ----
    sealed class Caption
    {
        public string Id = Guid.NewGuid().ToString().ToUpperInvariant();
        public double Start, End;
        public string Text = "";
        public Dictionary<string, object> Extra = new Dictionary<string, object>();
        public Caption Clone() { var c = (Caption)MemberwiseClone(); c.Extra = new Dictionary<string, object>(Extra); return c; }
        public (double start, double end) OutRange(TimeMap m) => (m.Out(Start), m.Out(End));

        public void Write(JsonW w) { w.BeginItem(); w.Num("end", End); w.Str("id", Id); w.Num("start", Start); w.Str("text", Text); w.Extra(Extra); w.End(); }
        public static Caption From(Dictionary<string, object> x)
        {
            var r = new JsonR(x);
            return new Caption { Id = r.Str("id", Guid.NewGuid().ToString().ToUpperInvariant()), Start = r.Num("start", 0), End = r.Num("end", 0), Text = r.Str("text", ""), Extra = JsonR.Unknown(x, new[] { "id", "start", "end", "text" }) };
        }
    }

    // ---- 字幕样式（同 Mac 版 CaptionStyle）：粗细、字号（占画布短边）、颜色、描边、阴影、底色条、位置 ----
    sealed class CaptionStyle
    {
        public static readonly string[] Weights = { "regular", "medium", "semibold" };
        public static readonly string[] Boxes = { "none", "dark", "light", "glass" };   // glass：Mac 的玻璃底（W6 前先按深色画）
        public static readonly string[] Positions = { "bottom", "top" };
        public bool Show = true;
        public string Weight = "semibold";
        public double Size = 0.05;             // 字高约占画布短边几成（0.03–0.09）
        public RGB Color = RGB.White;
        public bool Outline = true;            // 黑色描边：浅色画面上也看得清
        public bool Shadow = true;
        public string Box = "none";
        public string Position = "bottom";
        public double Margin = 0.07;           // 离上 / 下边多远（占画布高）
        public Dictionary<string, object> Extra = new Dictionary<string, object>();

        public CaptionStyle Clone() => From(Json.Parse(ToJson()) as Dictionary<string, object>);
        public bool Same(CaptionStyle o) => o != null && ToJson() == o.ToJson();

        // 画布上的字号（像素）：竖屏（比 1 窄）时大一号，手机上看得清
        public double FontPx(double cw, double ch) { double b = Size * Math.Min(cw, ch); return cw < ch ? b * 1.25 : b; }
        // 离边多远（像素）：竖屏放底部时至少留出 14%，别被抖音等 App 底部的按钮和文案挡住
        public double Edge(double cw, double ch) => (cw < ch && Position == "bottom" ? Math.Max(Margin, 0.14) : Margin) * ch;
        // 一行最多几个字（中文按 1、英文数字按 0.55 算）：按画布宽和字号自适应，不超过 18
        public double MaxChars(double cw, double ch) => Math.Min(18, Math.Max(8, Math.Floor(cw * 0.86 / FontPx(cw, ch))));

        public string ToJson()
        {
            var w = new JsonW(); w.Begin();
            w.Str("box", Box); w.Rgb("color", Color); w.Num("margin", Margin); w.Bool("outline", Outline); w.Str("position", Position);
            w.Bool("shadow", Shadow); w.Bool("show", Show); w.Num("size", Size); w.Str("weight", Weight); w.Extra(Extra);
            w.End(); return w.ToString();
        }
        public static CaptionStyle From(Dictionary<string, object> o)
        {
            var z = new CaptionStyle(); if (o == null) return z;
            var j = new JsonR(o);
            z.Show = j.Bool("show", z.Show); z.Weight = j.Str("weight", z.Weight, Weights); z.Size = j.Num("size", z.Size); z.Color = j.Rgb("color", z.Color);
            z.Outline = j.Bool("outline", z.Outline); z.Shadow = j.Bool("shadow", z.Shadow); z.Box = j.Str("box", z.Box, Boxes);
            z.Position = j.Str("position", z.Position, Positions); z.Margin = j.Num("margin", z.Margin);
            z.Extra = JsonR.Unknown(o, new[] { "show", "weight", "size", "color", "outline", "shadow", "box", "position", "margin" });
            return z;
        }
    }

    // ---- 一帧上显示哪句字幕（成片时间；整句被剪掉的不出），同 Mac 版 CaptionLayer 的取字部分 ----
    sealed class CaptionTrack
    {
        public readonly List<(double start, double end, string text)> Items;
        readonly TimeMap map;
        public CaptionTrack(IEnumerable<Caption> caps, bool show, TimeMap map, double trimStart, double trimEnd)
        {
            this.map = map;
            double lo = map.Out(trimStart), hi = map.Out(trimEnd);
            Items = !show ? new List<(double, double, string)>() : caps.Select(c =>
            {
                var t = (c.Text ?? "").Trim();
                var (s, e) = c.OutRange(map);
                double a = Math.Max(s, lo), b = Math.Min(e, hi);
                return (a, b, t);
            }).Where(x => x.t.Length > 0 && x.b - x.a >= 0.05).OrderBy(x => x.a).ToList();
        }
        public bool IsEmpty => Items.Count == 0;
        // 成片时间 t 显示哪句：后开始的盖住先开始的
        public string AtOut(double t) { string r = null; foreach (var x in Items) if (t >= x.start && t < x.end) r = x.text; return r; }
        public string AtSource(double s) => map.InCut(s) ? null : AtOut(map.Out(s));
    }

    // ---- 识别出的一个个字 → 一句句字幕：按标点、停顿断句，太长的按最大停顿拆，一次只显示一行（同 Mac 版 CaptionSplit） ----
    static class CaptionSplit
    {
        public struct Token { public string Text; public double Start, End; public Token(string t, double s, double e) { Text = t; Start = s; End = e; } }
        static readonly HashSet<char> BreakPunct = new HashSet<char>("，。！？；、：,.!?;:…");
        static readonly HashSet<char> KeepPunct = new HashSet<char>("？！?!");

        // 一个字算几格：中文 1，英文数字 0.55
        public static double Width(string s) => s.Sum(c => c < 128 ? 0.55 : 1.0);
        static string Join(IEnumerable<Token> t) => string.Concat(t.Select(x => x.Text));

        public static List<Caption> Make(IEnumerable<Token> tokens, double maxChars, double pause = 0.6)
        {
            // 1) 按标点和停顿分成小句
            var clauses = new List<List<Token>>(); var cur = new List<Token>();
            void Flush() { if (cur.Count > 0) { clauses.Add(cur); cur = new List<Token>(); } }
            foreach (var t0 in tokens.OrderBy(t => t.Start))
            {
                var t = t0;
                var s = (t.Text ?? "").Trim();
                if (s.Length == 0) continue;
                var core = new string(s.Where(c => !BreakPunct.Contains(c)).ToArray());
                // 识别器给的头一个字有时把前面的静音也算进去：一个字最长按 0.45 秒（英文单词按长度放宽）
                if (core.Length > 0) t.Start = Math.Max(t.Start, t.End - 0.45 * Math.Max(1, Width(core)));
                if (cur.Count > 0 && t.Start - cur[cur.Count - 1].End > pause) Flush();
                if (core.Length > 0) cur.Add(new Token(core, t.Start, t.End));
                char last = s[s.Length - 1];
                if (BreakPunct.Contains(last))
                {
                    if (KeepPunct.Contains(last) && cur.Count > 0) { var x = cur[cur.Count - 1]; x.Text += last; cur[cur.Count - 1] = x; }
                    Flush();
                }
            }
            Flush();
            // 1.5) 识别结果常常没有标点：小句偏长时，在里面明显的停顿（≥ 0.25 秒）处再断开，两边都至少 4 个字
            List<List<Token>> ByPause(List<Token> c)
            {
                if (!(Width(Join(c)) > maxChars * 0.6) || c.Count <= 1) return new List<List<Token>> { c };
                int best = -1; double gap = 0.25;
                for (int k = 1; k < c.Count; k++)
                {
                    double g = c[k].Start - c[k - 1].End;
                    if (g < gap || Width(Join(c.Take(k))) < 4 || Width(Join(c.Skip(k))) < 4) continue;
                    best = k; gap = g;
                }
                if (best < 0) return new List<List<Token>> { c };
                var r = ByPause(c.Take(best).ToList()); r.AddRange(ByPause(c.Skip(best).ToList())); return r;
            }
            clauses = clauses.SelectMany(ByPause).ToList();
            // 2) 太长的小句拆开：在理想位置附近找停顿最大的地方断
            var lines = new List<List<Token>>();
            foreach (var c in clauses)
            {
                var rest = c;
                while (Width(Join(rest)) > maxChars && rest.Count > 1)
                {
                    double total = Width(Join(rest)), parts = Math.Ceiling(total / maxChars), target = total / parts;
                    double acc = 0; int ideal = 1;
                    for (int i = 0; i < rest.Count; i++) { acc += Width(rest[i].Text); if (acc >= target) { ideal = i + 1; break; } }
                    ideal = Math.Min(Math.Max(1, ideal), rest.Count - 1);
                    int best = ideal; double bestGap = -1;
                    for (int k = Math.Max(1, ideal - 3); k <= Math.Min(rest.Count - 1, ideal + 3); k++)
                    {
                        if (Width(Join(rest.Take(k))) > maxChars) continue;
                        double g = rest[k].Start - rest[k - 1].End;
                        if (g > bestGap + 0.02 || (Math.Abs(g - bestGap) <= 0.02 && Math.Abs(k - ideal) < Math.Abs(best - ideal))) { best = k; bestGap = g; }
                    }
                    lines.Add(rest.Take(best).ToList()); rest = rest.Skip(best).ToList();
                }
                lines.Add(rest);
            }
            // 3) 时间：首字开始到末字结束；末尾多留一点（不压到下一句），太短的拉到 0.7 秒
            var o = lines.Where(l => l.Count > 0).Select(l => (s: l[0].Start, e: l[l.Count - 1].End, t: Join(l))).ToList();
            for (int i = 0; i < o.Count; i++)
            {
                double next = i + 1 < o.Count ? o[i + 1].s : double.PositiveInfinity;
                double e = Math.Min(o[i].e + 0.35, next);
                if (e - o[i].s < 0.7) e = Math.Min(o[i].s + 0.7, next);
                o[i] = (o[i].s, Math.Max(e, o[i].e), o[i].t);
            }
            return o.Select(x => new Caption { Start = x.s, End = x.e, Text = x.t }).ToList();
        }
    }

    // ---- .srt（成片时间，从修剪起点算 0），同 Mac 版 ----
    static class Srt
    {
        public static string Stamp(double t)
        {
            long ms = (long)Math.Round(Math.Max(0, t) * 1000);
            return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";
        }
        public static string Text(StudioProject p)
        {
            var m = new TimeMap(p); double b = m.Out(p.TrimStart);
            var tr = new CaptionTrack(p.Captions, true, m, p.TrimStart, p.TrimEnd);
            var sb = new StringBuilder();
            for (int i = 0; i < tr.Items.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(i + 1).Append('\n').Append(Stamp(tr.Items[i].start - b)).Append(" --> ").Append(Stamp(tr.Items[i].end - b)).Append('\n').Append(tr.Items[i].text).Append('\n');
            }
            return sb.ToString();
        }
    }

    // ---- 导出时把字幕烧进画面：写一份 ASS 字幕交给 ffmpeg 的 ass 滤镜（画布像素坐标，一次一行，太长的缩小字号放进 90% 宽） ----
    static class CaptionAss
    {
        // Windows 上的中文字体：微软雅黑；没有就黑体；Mac 上（自测）用苹方
        public static string FontName(string fontsDir)
        {
            if (fontsDir != null && File.Exists(Path.Combine(fontsDir, "msyh.ttc"))) return "Microsoft YaHei";
            if (fontsDir != null && File.Exists(Path.Combine(fontsDir, "simhei.ttf"))) return "SimHei";
            if (Directory.Exists("/System/Library/Fonts")) return "PingFang SC";
            return "Arial";
        }

        static string Col(RGB c, int alpha = 0) =>   // ASS 颜色：&HAABBGGRR（A 0 = 不透明）
            $"&H{alpha:X2}{(int)Math.Round(c.B * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.R * 255):X2}";
        static string T(double s) { s = Math.Max(0, s); int cs = (int)Math.Round(s * 100); return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}"; }

        // t0 = 成片开头（修剪起点的成片时间）；cw / ch = 输出画布像素
        public static string Build(CaptionTrack tr, CaptionStyle st, double t0, int cw, int ch, string font)
        {
            double px = st.FontPx(cw, ch), edge = st.Edge(cw, ch);
            bool box = st.Box != "none";
            int bold = st.Weight == "regular" ? 0 : 1;
            // 底色条：BorderStyle 3 = 不透明框（用 OutlineColour 当框的颜色）；不要底色条时 BorderStyle 1 = 描边 + 阴影
            string outline = box ? (st.Box == "light" ? "&H1FFFFFFF" : "&H66000000") : "&H26000000";
            double ow = box ? px * 0.22 : st.Outline ? Math.Max(1, px * 0.06) : 0, sh = st.Shadow && !box ? Math.Max(1, px * 0.05) : 0;
            var sb = new StringBuilder();
            sb.Append("[Script Info]\nScriptType: v4.00+\nWrapStyle: 2\nScaledBorderAndShadow: yes\n");
            sb.Append($"PlayResX: {cw}\nPlayResY: {ch}\n\n");
            sb.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            int align = st.Position == "top" ? 8 : 2;
            sb.Append(string.Format(CultureInfo.InvariantCulture, "Style: Cap,{0},{1:0},{2},&H000000FF,{3},&H80000000,{4},0,0,0,100,100,0,0,{5},{6:0.#},{7:0.#},{8},{9},{9},{10:0},1\n\n",
                font, px, Col(st.Color), outline, bold, box ? 3 : 1, ow, sh, align, (int)(cw * 0.05), edge));
            sb.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
            double maxW = cw * 0.9;
            foreach (var (s, e, text) in tr.Items)
            {
                // 太长的字缩小放进一行（中文按 1 个字号宽、英文数字按 0.55 估）
                double w = CaptionSplit.Width(text) * px + 2 * (box ? 0.55 : 0.25) * px;
                string fs = w > maxW ? "{\\fs" + (px * maxW / w).ToString("0", CultureInfo.InvariantCulture) + "}" : "";
                string esc = text.Replace("\\", "＼").Replace("{", "｛").Replace("}", "｝").Replace("\n", " ");
                sb.Append($"Dialogue: 0,{T(s - t0)},{T(e - t0)},Cap,,0,0,0,,{fs}{esc}\n");
            }
            return sb.ToString();
        }

        // ass 滤镜的文件名写法：Windows 路径 C:\a\b → C\:/a/b（冒号要转义）
        public static string FilterPath(string path) => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");
    }

    // ---- whisper.cpp 识别结果 → 一个个字（音频时间）：whisper 给的是一小段一小段，段里的字按真正有声音的地方摊开（停顿处留空），
    // 段的起止也按声音收紧到说话的地方（whisper 段的开头常常把前面的静音也算进去） ----
    static class WhisperParse
    {
        // whisper-cli -oj 的结果：transcription[].offsets.from / to（毫秒）、text
        public static List<(double start, double end, string text)> Segments(string json)
        {
            var list = new List<(double, double, string)>();
            if (!(Json.Parse(json) is Dictionary<string, object> o) || !(o.TryGetValue("transcription", out var tv) && tv is List<object> tr)) return list;
            foreach (var x in tr.OfType<Dictionary<string, object>>())
            {
                var r = new JsonR(x);
                double a = 0, b = 0;
                if (x.TryGetValue("offsets", out var ov) && ov is Dictionary<string, object> off) { var j = new JsonR(off); a = j.Num("from", 0) / 1000; b = j.Num("to", 0) / 1000; }
                var text = r.Str("text", "").Trim();
                if (text.Length > 0 && b > a) list.Add((a, b, text));
            }
            return list;
        }

        // 每 10 毫秒一格：有没有声音（相对整段最响的 10% 以上、且不太小）
        public static bool[] Voiced(float[] pcm, int sr, double rel = 0.08)
        {
            int hop = sr / 100, n = pcm.Length / Math.Max(1, hop);
            var rms = new double[n]; double max = 0;
            for (int i = 0; i < n; i++)
            {
                double s = 0; for (int k = i * hop; k < (i + 1) * hop; k++) s += pcm[k] * (double)pcm[k];
                rms[i] = Math.Sqrt(s / hop); max = Math.Max(max, rms[i]);
            }
            double thr = Math.Max(0.004, max * rel);
            var v = new bool[n];
            for (int i = 0; i < n; i++) v[i] = rms[i] > thr;
            // 补上字中间的小空隙（< 120 毫秒），去掉孤立的小毛刺（< 40 毫秒）
            Close(v, 12, true); Close(v, 4, false);
            return v;
        }
        static void Close(bool[] v, int maxRun, bool fillGaps)
        {
            int i = 0;
            while (i < v.Length)
            {
                int j = i; while (j < v.Length && v[j] == v[i]) j++;
                bool inner = i > 0 && j < v.Length;
                if (v[i] == !fillGaps && j - i < maxRun && (inner || !fillGaps)) for (int k = i; k < j; k++) v[k] = fillGaps;
                i = j;
            }
        }

        // 段里的字摊到真正有声音的地方：先把段的起止收紧到有声音的格子；中间 ≥ 0.25 秒的无声处当停顿，字按各块有声的时长分
        public static List<CaptionSplit.Token> Tokens(List<(double start, double end, string text)> segs, bool[] voiced)
        {
            var outL = new List<CaptionSplit.Token>();
            foreach (var (a0, b0, text) in segs)
            {
                // 一个字一份，标点贴在前一个字后面（不占时间）
                var chars = new List<string>();
                foreach (var c in Graphemes(text))
                    if (IsPunct(c) && chars.Count > 0) chars[chars.Count - 1] += c; else if (!IsPunct(c)) chars.Add(c);
                if (chars.Count == 0) continue;
                int ia = Math.Max(0, (int)Math.Floor(a0 * 100)), ib = Math.Min(voiced.Length, (int)Math.Ceiling(b0 * 100));
                // 段的前后稍放宽一点找声音（whisper 段的边界常差一两百毫秒）
                int sa = Math.Max(0, ia - 20), sb = Math.Min(voiced.Length, ib + 20);
                var runs = new List<(int a, int b)>();
                for (int i = sa; i < sb;)
                {
                    if (!voiced[i]) { i++; continue; }
                    int j = i; while (j < sb && voiced[j]) j++;
                    runs.Add((i, j)); i = j;
                }
                // 只留和 whisper 段重叠的有声块
                runs = runs.Where(r => r.b > ia && r.a < ib).ToList();
                if (runs.Count == 0) { Spread(outL, chars, a0, b0); continue; }
                // 小于 0.25 秒的无声缝并起来
                var merged = new List<(int a, int b)> { runs[0] };
                foreach (var r in runs.Skip(1)) { var l = merged[merged.Count - 1]; if (r.a - l.b < 25) merged[merged.Count - 1] = (l.a, r.b); else merged.Add(r); }
                double total = merged.Sum(r => r.b - r.a);
                // 字按每块的长短分（至少 1 个字），保持顺序
                int left = chars.Count, used = 0;
                for (int k = 0; k < merged.Count; k++)
                {
                    int take = k == merged.Count - 1 ? left : Math.Max(1, Math.Min(left - (merged.Count - 1 - k), (int)Math.Round(chars.Count * (merged[k].b - merged[k].a) / total)));
                    // 按时长算出来的断点附近（±3 个字）有标点，就断在标点后面（停顿多半在标点处）
                    if (k < merged.Count - 1)
                        for (int d = 0; d <= 3; d++)
                        {
                            int c1 = take + d, c2 = take - d;
                            if (c1 >= 1 && c1 <= left - (merged.Count - 1 - k) && EndsPunct(chars[used + c1 - 1])) { take = c1; break; }
                            if (c2 >= 1 && EndsPunct(chars[used + c2 - 1])) { take = c2; break; }
                        }
                    if (take <= 0) continue;
                    Spread(outL, chars.Skip(used).Take(take).ToList(), merged[k].a / 100.0, merged[k].b / 100.0);
                    used += take; left -= take;
                    if (left <= 0) break;
                }
            }
            return outL;
        }

        static void Spread(List<CaptionSplit.Token> o, List<string> chars, double a, double b)
        {
            double step = (b - a) / Math.Max(1, chars.Count), t = a;
            foreach (var c in chars) { o.Add(new CaptionSplit.Token(c, t, t + step * 0.92)); t += step; }
        }
        static bool EndsPunct(string c) => c.Length > 0 && "，。！？；、：,.!?;:…".IndexOf(c[c.Length - 1]) >= 0;
        static bool IsPunct(string c) => c.Length == 1 && "，。！？；、：,.!?;:…".IndexOf(c[0]) >= 0;

        // 拆成一个个字：中文一个字一份，连着的英文 / 数字算一个词
        public static List<string> Graphemes(string s)
        {
            var o = new List<string>(); var word = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { if (word.Length > 0) { o.Add(word.ToString()); word.Clear(); } continue; }
                if (c < 128 && char.IsLetterOrDigit(c)) { word.Append(c); continue; }
                if (word.Length > 0) { o.Add(word.ToString()); word.Clear(); }
                o.Add(c.ToString());
            }
            if (word.Length > 0) o.Add(word.ToString());
            return o;
        }

        // whisper 有时给繁体：常见的几个字换成简体（只换最常见、不会错的）
        static readonly Dictionary<char, char> T2S = new Dictionary<char, char>
        {
            ['這'] = '这', ['個'] = '个', ['們'] = '们', ['來'] = '来', ['時'] = '时', ['會'] = '会', ['說'] = '说', ['話'] = '话', ['錄'] = '录', ['軟'] = '软',
            ['體'] = '体', ['後'] = '后', ['裡'] = '里', ['為'] = '为', ['麼'] = '么', ['樣'] = '样', ['開'] = '开', ['關'] = '关', ['點'] = '点', ['擊'] = '击',
            ['剪'] = '剪', ['輯'] = '辑', ['視'] = '视', ['頻'] = '频', ['幕'] = '幕', ['動'] = '动', ['變'] = '变', ['文'] = '文', ['覺'] = '觉', ['得'] = '得',
            ['怎'] = '怎', ['過'] = '过', ['還'] = '还', ['調'] = '调', ['整'] = '整', ['置'] = '置', ['窗'] = '窗', ['蘋'] = '苹', ['畫'] = '画', ['麵'] = '面',
            ['讓'] = '让', ['寫'] = '写', ['聲'] = '声', ['音'] = '音', ['見'] = '见', ['對'] = '对', ['號'] = '号', ['學'] = '学', ['實'] = '实', ['發'] = '发',
            ['機'] = '机', ['電'] = '电', ['腦'] = '脑', ['鍵'] = '键', ['區'] = '区', ['選'] = '选', ['擇'] = '择', ['條'] = '条', ['將'] = '将', ['從'] = '从',
        };
        public static string Simplify(string s) => new string(s.Select(c => T2S.TryGetValue(c, out var x) ? x : c).ToArray());
    }
}

namespace ScreenRecorder
{
    // ---- 识别哪段声音：录到的声音（所有音轨混成一条）/ 后来录的讲解 / 两样都要；出来的字换到成片时间断句，再换回素材时间存（同 Mac 版 CaptionMaker） ----
    static class CaptionMaker
    {
        public static readonly string[] Sources = { "recorded", "voice", "both" };

        // 素材里录到的声音混成单声道 16k wav（按每条音轨自己的时间戳补静音：音轨比画面晚开始的，识别出来的时间才是素材时间）
        public static double RecordedAudio(string raw, VideoInfo info, string wav)
        {
            if (info.AudioChannels.Count == 0) return 0;
            var g = new StringBuilder();
            for (int i = 0; i < info.AudioChannels.Count; i++) g.Append($"[0:a:{i}]aresample=16000:async=1:first_pts=0,aformat=channel_layouts=mono[a{i}];");
            if (info.AudioChannels.Count == 1) g.Append("[a0]anull[out]");
            else { for (int i = 0; i < info.AudioChannels.Count; i++) g.Append($"[a{i}]"); g.Append($"amix=inputs={info.AudioChannels.Count}:duration=longest:normalize=0[out]"); }
            if (!Studio.RunQuiet($"-v error -y -i \"{raw}\" -vn -filter_complex \"{g}\" -map \"[out]\" -ac 1 -ar 16000 -c:a pcm_s16le \"{wav}\"", 600000) || !File.Exists(wav)) return 0;
            return new FileInfo(wav).Length / 32000.0;
        }

        // 识别 → 成片时间的字。transcribe = 给 16k 单声道 wav，返回 whisper 的 json；stage(说明, 进度 0–1 或 null)
        public static List<CaptionSplit.Token> Tokens(string dir, VideoInfo info, StudioProject p, string source, Func<string, Action<double>, string> transcribe,
                                                      Action<string, double?> stage, System.Threading.CancellationToken cancel)
        {
            var m = new TimeMap(p);
            var jobs = new List<(string wav, bool temp, Func<double, double?> map)>();
            string tmpDir = Path.GetTempPath();
            if (source != "voice")
            {
                var wav = Path.Combine(tmpDir, "captions-" + Guid.NewGuid().ToString("N") + ".wav");
                stage?.Invoke(App2.L("准备声音…", "Preparing audio…"), null);
                if (RecordedAudio(Path.Combine(dir, "raw.mp4"), info, wav) > 0)
                    jobs.Add((wav, true, t => t < p.TrimStart || t > p.TrimEnd || m.InCut(t) ? (double?)null : m.Out(t)));
            }
            if (source != "recorded")
                foreach (var c in p.Voiceovers)
                {
                    var f = Path.Combine(dir, c.File);
                    if (!File.Exists(f)) continue;
                    var wav = Path.Combine(tmpDir, "captions-" + Guid.NewGuid().ToString("N") + ".wav");
                    if (!Studio.RunQuiet($"-v error -y -i \"{f}\" -vn -ac 1 -ar 16000 -c:a pcm_s16le \"{wav}\"", 120000)) continue;
                    double o0 = m.Out(c.Start), ti = c.TrimIn, to = c.TrimOut;
                    jobs.Add((wav, true, t => t < ti || t > to ? (double?)null : o0 + (t - ti)));
                }
            if (jobs.Count == 0) throw new Exception(App2.L("没有可以识别的声音", "There's no sound to transcribe"));
            var outL = new List<CaptionSplit.Token>();
            try
            {
                for (int k = 0; k < jobs.Count; k++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var j = jobs[k]; int kk = k;
                    var pcm = Studio.DecodePcm(j.wav, 16000) ?? new float[0];
                    var voiced = WhisperParse.Voiced(pcm, 16000);
                    // 开头有一大段没声音时，whisper 的 CPU 版常常听不出来、只吐出提示词里的几个字：
                    // 把开头的静音（留 0.3 秒）切掉再识别，时间加回去
                    int first = Array.IndexOf(voiced, true);
                    double lead = first > 0 ? Math.Max(0, first / 100.0 - 0.3) : 0;
                    if (lead > 0.5) { pcm = pcm.Skip((int)(lead * 16000)).ToArray(); voiced = voiced.Skip((int)Math.Round(lead * 100)).ToArray(); WriteWav(j.wav, pcm, 16000); }
                    else lead = 0;
                    var json = transcribe(j.wav, f => stage?.Invoke(App2.L("正在识别说的话…", "Transcribing…"), (kk + f) / jobs.Count));
                    cancel.ThrowIfCancellationRequested();
                    var segs = WhisperParse.Segments(json).Select(s => (s.start, s.end, WhisperParse.Simplify(s.text))).ToList();
                    foreach (var t in WhisperParse.Tokens(segs, voiced))
                    {
                        var s0 = j.map(t.Start + lead); var e0 = j.map(Math.Max(t.Start, t.End) + lead);
                        if (s0 is double s && e0 is double e) outL.Add(new CaptionSplit.Token(t.Text, s, Math.Max(s, e)));
                    }
                }
            }
            finally { foreach (var j in jobs) if (j.temp) try { File.Delete(j.wav); } catch { } }
            return outL.OrderBy(t => t.Start).ToList();
        }

        // 16 位单声道 wav
        static void WriteWav(string file, float[] pcm, int sr)
        {
            using (var w = new BinaryWriter(File.Create(file)))
            {
                int bytes = pcm.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                foreach (var v in pcm) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
            }
        }

        // 生成字幕（素材时间）：成片时间里断好句，再换回素材时间存
        public static List<Caption> Make(string dir, VideoInfo info, StudioProject p, string source, double cw, double ch, Func<string, Action<double>, string> transcribe,
                                         Action<string, double?> stage, System.Threading.CancellationToken cancel)
        {
            var toks = Tokens(dir, info, p, source, transcribe, stage, cancel);
            var m = new TimeMap(p);
            return CaptionSplit.Make(toks, p.CaptionStyle.MaxChars(cw, ch)).Select(c => { c.Start = m.Src(c.Start); c.End = m.Src(c.End); return c; }).ToList();
        }
    }

    // 纯 C# 文件里用的界面文字（Windows 程序里跟 App.L 一样；Mac 上的测试工具按中文）
    static class App2
    {
        public static Func<string, string, string> Lang;
        public static string L(string zh, string en) => Lang != null ? Lang(zh, en) : zh;
    }
}
